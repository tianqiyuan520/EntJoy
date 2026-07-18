# Taskflow 尾延迟优化与 A/B 后端设计

## 目标

在保留 Taskflow 默认执行器和现有 C#/Native ABI 的前提下，继续压低 ECS Chunk/Entity 短任务的 P95，并把剩余尾延迟拆分为可诊断的调度阶段。只有受控 A/B 数据证明自研执行器有稳定收益后，才允许考虑替换 Taskflow。

本阶段实现四项工作：

1. 减少 EntJoy Partition 层的无效窃取扫描。
2. 复用批次、Tile 和 Partition 存储。
3. 增加 Taskflow 提交、Worker 启动、Tile 完成和 Complete 返回边界诊断。
4. 建立互斥的执行后端边界，以 Taskflow 为默认，并提供原生 WorkerPool 实验后端用于 A/B。

## 已知约束

- Taskflow Executor 必须长期复用，不能按批次创建线程池。
- 默认后端保持 Taskflow；未显式启用实验后端时不得创建第二套 Worker 线程。
- 同一个 `BatchState` 只能被一个执行后端消费，两套后端不得同时参与。
- `workerCap` 继续限制批次参与者数量，而不是宣称 Taskflow 精确唤醒了指定数量的唯一线程。
- `Complete()` 仍可协助目标批次，且 Assist 生命周期规则保持不变。
- C++、ISPC 的 IJobChunk/IJobEntity 继续共用同一 Tile/Partition 协议。
- 性能验收关注诊断一致性和 P50/P95 分布；Codex 机器上的绝对耗时不作为验收门槛。

## 方案选择

### 采用：Taskflow 粗任务 + EntJoy 有界 Tile 调度

Taskflow 每批仍接收不超过 `workerCap` 个 Partition drain task。EntJoy 保留连续本地 Partition，使多数 Tile 维持顺序访问；只有本地工作耗尽后才进入有界窃取。

没有采用“每个 Tile 一个 Taskflow 节点”，因为 31 个左右的细节点会提高 topology 构建、入队和完成记账开销，也会丢失当前连续 Partition 的数据局部性。没有立即切换原生 WorkerPool，因为现有数据只证明 EntJoy 二层调度仍可优化，尚未证明 Taskflow 是瓶颈。

## 1. 有界最重 Victim 窃取

### Worker 路径

Worker 完成本地 Partition 后，只执行有限轮窃取。每轮读取各 Partition 的 `front/back` 快照，选出剩余 Tile 最多的 victim，并只对该 victim 调用一次 half-steal：

1. 没有 victim 剩余至少两个 Tile时立即结束。
2. half-steal 成功后执行所获连续区间，再重新选择最重 victim。
3. CAS 失败或 victim 已被其他线程抽干时，允许一次重新选择；连续失败后退出，不扫描每个 victim 并反复调用 `TryStealHalf`。

`stealAttempts` 只统计实际 CAS/half-steal 调用，不统计只读 victim 选择；新增 `victimScans`、`stealEmptyExits` 用于区分选择成本和失败成本。

### Complete Assist 路径

Assist 与 Worker 共用最重 victim 选择函数。剩余一个 Tile 时使用单 Tile claim；剩余两个及以上时 half-steal。所有 claim 仍通过 Partition 的原子 `front/back` 完成，保证 Tile 不重不漏。

## 2. 批次存储复用

引入内部 `BatchStorage` 所有权对象，持有：

- 一个 `BatchState`；
- 可增长的 `ExecutionTile` 连续缓冲区；
- 可增长且满足缓存行对齐的 `LocalPartition` 连续缓冲区。

存储池采用进程级、有上限的空闲链表：

- Acquire 选择容量足够的对象；没有合适对象时创建。
- Release 重置所有原子状态、回调、上下文、计数和诊断字段后归还。
- 空闲对象数量超过上限时直接释放。
- `Shutdown()` 在 Executor 停止且批次全部结束后清空池。
- 池只复用调度元数据，不拥有用户 Job context；用户 context 仍由现有 cleanup 回调管理。

`BatchState` 不再分别 `delete[] tiles`、`delete[] partitions`。Taskflow 完成回调与 Assist reader 都退出后，才把整个 `BatchStorage` 归还池，保持现有生命周期安全性。

新增 `batchStorageCreated`、`batchStorageReused`、`batchStorageReturned`、`batchStorageDropped` 统计，以验证复用确实发生且生命周期闭合。

## 3. Taskflow 边界诊断

每个批次使用单调时钟记录以下时间点：

- `publishedAt`：Assist 指针发布且准备提交 Taskflow。
- `firstWorkerAt`：第一个 Partition drain task 进入。
- `lastWorkerAt`：该批次最后一个首次参与的 slot 进入。
- `lastTileAt`：`completedTiles` 达到 `tileCount`。
- `topologyDoneAt`：Taskflow completion callback 进入。
- `completeWakeAt`：等待中的 `Complete()` 观察到完成。
- `completeReturnAt`：`Complete()` 即将返回。

聚合输出：

- `submitToFirstWorkerEwmaNs`
- `workerStartSpreadEwmaNs`
- `lastTileToTopologyDoneEwmaNs`
- `completeWakeToReturnEwmaNs`

时间点必须只记录一次；没有发生对应阶段时保持“不适用”语义，不能用伪造的零耗时代表测量结果。统计复位后这些字段清零，并由现有导出结构尾部追加，保持旧字段布局不变。

## 4. 互斥 A/B 执行后端

新增内部枚举：

```cpp
enum class ExecutionBackend : uint8_t
{
    Taskflow,
    NativeWorkerPoolExperimental
};
```

后端在 `Scheduler::Initialize` 时读取一次 `ENTJOY_JOB_BACKEND`：

- 未设置、空值或 `taskflow`：使用 Taskflow。
- `native`：使用实验 WorkerPool。
- 其他值：回退 Taskflow，并增加一次无效配置诊断。

运行期间不允许动态切换。`SubmitBatch` 只负责公共发布与 Assist 注册，然后分派到一个后端：

```text
SubmitBatch
  -> Publish assist/diagnostics
  -> TaskflowBackend::Submit
     或 NativeWorkerPoolBackend::Submit
  -> common workers-finished/finalize path
```

原生实验后端使用 `Initialize` 时创建、`Shutdown` 时销毁的持久 Worker；它消费与 Taskflow 完全相同的 Partition slot，并调用同一个 `WorkerPartitionLoop`。每批只发布 `partitionCount` 个 slot token，WorkerPool 不重新实现 Tile claim、half-steal 或 Assist。这样 A/B 只比较执行器的发布、唤醒和完成开销，不混入不同的 ECS 调度算法。

新增统计 `taskflowBatches`、`nativeBatches` 和 `invalidBackendSelections`。任何批次必须满足 `taskflowBatches + nativeBatches == published partition batches`。

## 错误处理与生命周期

- 存储分配失败沿用 C++ 异常边界，不把半初始化 Batch 发布给 Worker。
- Shutdown 顺序为：拒绝新调度、完成/停止选中的执行器、确认没有在途 Batch、清空 BatchStorage 池。
- 原生后端回调公共 `MarkWorkersFinished`，不能复制或绕过 Assist reader 生命周期判断。
- Job callback 的异常策略不在本阶段改变。

## 测试与验收

原生测试必须覆盖：

1. 1、2、7、8、31、32、100 个 Tile 在竞争下每个恰好执行一次。
2. 窃取成功、空 victim、CAS 竞争和单 Tile Assist fallback。
3. 新窃取策略的实际 half-steal 尝试显著少于旧的全 victim 反复扫描基线。
4. 连续批次复用 BatchStorage，统计满足 created/reused/returned 守恒。
5. Taskflow 四个边界时间顺序单调，统计 reset 有效。
6. 默认只初始化 Taskflow；`native` 只初始化原生 WorkerPool；同一批后端计数互斥。
7. 两个后端都通过统一 Tile 记账、依赖、Complete Assist、Shutdown 压力测试。
8. 现有 NativeDll.Tests、NativeDll 构建和 Sample 构建通过。

手工性能验证在相同 workerCap、Tile 数、Warmup 和 Measure 下分别运行 `taskflow` 与 `native`。先比较调试字段和结果正确性，再由用户机器比较 P50/P95；在实验后端没有稳定优于 Taskflow 之前，默认值不得改变。

## 非目标

- 本阶段不修改 ECS Chunk 容量、组件布局或 ISPC 内核。
- 不设置线程亲和性和优先级。
- 不承诺绝对 0.9 ms；目标是减少已识别的无效调度工作并提供可归因的 A/B 数据。
- 不删除 Taskflow。
