# 方案 B：仿 Unity 的本地分区、执行 Tile 与半量窃取 JobSystem 架构

## 1. 文档目的

本文给出 EntJoy ECS/JobSystem 的下一代并行调度架构。目标不是继续微调当前全局游标，而是参考 Unity Job System 和 DOTS ECS 的核心思想，对 Chunk 存储粒度、任务切分、worker 本地队列、工作窃取、`Complete()` 协作和诊断体系进行系统性重构。

本文是架构设计，不是逐行实施计划。实现时应保留旧路径作为对照，分阶段切换，避免一次性重写后无法判断性能变化来自哪里。

## 1.1 执行摘要

正确的高收益方向不是继续替换线程 API、增加永久自旋或给现有全局 `nextRange` 套一层本地队列，而是改变工作的所有权模型：

```text
旧模型：所有 worker 每处理一个 range 都竞争同一个全局原子游标

新模型：发布时分配连续本地工作
        → owner 顺序执行
        → 只有负载失衡时才从 victim 尾部窃取一半剩余工作
        → Complete() 通过同一协议协作
```

完整方案由六项组合构成：

1. 只保留一个真正执行 Job 的原生 WorkerPool。
2. `workerTarget` 精确限制参与者和唤醒数量。
3. 每个 worker 获得连续 `LocalPartition`，正常路径不争抢全局游标。
4. 空闲 worker 从 victim 尾部窃取一半剩余 Tile，而不是窃取一个 Job 指针后继续竞争全局游标。
5. ECS 物理 Chunk 使用字节预算，调度 Tile 与物理 Chunk 分层。
6. 保留完整 Trace、生命周期保护和新旧路径 A/B，任何阶段不允许以关闭诊断换取结果。

预计收益优先级：

| 优先级 | 改动 | 主要收益 |
|---|---|---|
| P0 | 恢复正确性、真实统计、`workerTarget` | 结果可信，避免过量参与和生命周期错误 |
| P1 | 单一 WorkerPool + 连续本地分区 | 消除逐 Range 全局原子争用，改善缓存局部性 |
| P1 | 半量窃取 | 降低负载不均衡造成的 p95 长尾 |
| P1 | 自适应 spin/park + 精确唤醒 | 降低 Heavy 后温度和 CPU 活跃污染 |
| P2 | 16~32KiB 物理 Chunk + 64~128KiB Tile | 缩小 DRAM 工作集，降低单 Range 长尾 |
| P3 | CPU topology 软提示 | 在主架构稳定后做平台级微调 |

## 1.2 当前 Claude 改写的处置原则

当前工作树里的 Claude 改写不能作为新架构基线，原因包括：

- `WorkerPool.cpp/.h` 只加入了 Visual Studio filter，没有加入 `NativeDll.vcxproj` 的编译列表，也没有接管 `JobSystem.cpp`；实际 DLL 仍使用 Taskflow。
- 实际执行仍由所有 Taskflow drain task 竞争同一个 `BatchState::nextRange`。
- `workerCap` 没有传入发布阶段，除 inline 判断外基本失效，日志里的 `WorkerCap=8` 不代表实际只使用 8 个 worker。
- 多项调试统计被直接写成零，Trace 显示 `batch=0/events=0/INCOMPLETE`，无法证明唤醒、窃取或参与者行为。
- `Complete()` 读取裸 `assistContext` 后，Taskflow 完成回调可能并发删除 `BatchState`，存在 use-after-free 风险。
- `PrewakeWorkers`、`KeepWorkersWarm`、`SetFrameLowLatencyMode`、`FlushScheduledJobs` 和调度模式变为空实现或被忽略。
- Native 测试已经因公共接口被删除而不能编译；未编译的 WorkerPool 也没有测试覆盖。

因此实施本方案前必须先恢复到最近一个具备以下能力的可靠基线，或者逐项恢复这些能力后再开始架构迁移：

```text
精确 workerTarget/ticket/permit
真实 Trace 和批次 ID
Complete assist reader 生命周期保护
可用的调度模式和公开 API
Native 测试可编译并通过
```

禁止在当前未接线的 WorkerPool 上继续叠加 affinity、自旋或队列算法；这会同时保留 Taskflow 和自定义线程池，形成双执行器和线程超额订阅风险。

## 2. 当前结论与问题边界

回滚前的可靠基线已经解决了一部分问题；以下结论用于确定新架构的起点，不代表当前 Claude 工作树仍然保留了这些能力：

- `workerCap = 8` 时，只发布 8 个 ticket，并精确释放 8 个唤醒 permit。
- 实测 `workersPeak = 8`、`notified = 800`、`parkWakes ≈ 800`，不再唤醒整个 worker 池。
- `Complete()` 能持续处理目标批次未领取的 range。
- Sleep 测试的 C++ 中位数已接近目标：`p50 ≈ 0.89~0.92ms`。
- 主要差距已经从平均吞吐转为尾延迟：`p95 ≈ 2.1~2.3ms`。
- 单个 range 执行期间大多没有迁核，因此硬线程亲和不是当前第一优先级。

当前架构的核心问题位于：

```text
所有 worker
    │
    ├── nextRange.fetch_add(1)
    ├── nextRange.fetch_add(1)
    ├── nextRange.fetch_add(1)
    └── nextRange.fetch_add(1)
             │
             ▼
       同一个共享游标
             │
             ▼
      大粒度物理 Chunk/range
```

它带来四类问题：

1. 所有参与者反复修改同一个原子缓存行。
2. worker 没有稳定的本地数据区间，处理顺序由全局竞争决定。
3. 一个 range 的数据量过大，任意一次抢占或 DRAM 长延迟都会直接形成尾部气泡。
4. `Complete()` 与 worker 使用同一个全局游标，不能表达“本地优先、尾部才窃取”的策略。

## 3. Unity 提供的参考模型

Unity 的 ParallelFor 调度不是让所有线程永久竞争一个全局元素游标。官方文档描述的模型是：

- 工作先被切成多个 batch，并分配给各 native job/worker。
- worker 优先完成自己的 batch。
- worker 提前完成后，从其他 worker 的剩余 batch 中窃取工作。
- 每次只窃取对方剩余 batch 的一半，以维持缓存局部性。

参考：

- [Unity Job System Overview：work stealing](https://docs.unity3d.com/cn/2022.3/Manual/JobSystemOverview.html)
- [Unity ParallelFor jobs：steal half of remaining batches](https://docs.unity3d.com/es/2018.3/Manual/JobSystemParallelForJobs.html)
- [Unity IJobFor ScheduleParallel：inner-loop batch 是窃取粒度](https://docs.unity3d.com/ru/2020.2/ScriptReference/Unity.Jobs.IJobForExtensions.ScheduleParallel.html)

DOTS ECS 还使用固定大小的 Archetype Chunk。Entities 1.0 文档说明每个 Chunk 为 16KiB，内部按组件类型保存并行数组。这使单个 Chunk 成为天然的局部数据块，而不是数百 KiB 的长任务。

- [Unity Entities：Archetype Chunk 为 16KiB](https://docs.unity.cn/Packages/com.unity.entities%401.0/manual/concepts-archetypes.html)
- [Unity Entities：IJobChunk 按匹配 Chunk 调用 Execute](https://docs.unity.cn/Packages/com.unity.entities%401.0/manual/iterating-data-ijobchunk.html)

EntJoy 不需要复制 Unity 的私有实现，但应采用相同的调度原则：

```text
先分区 → 本地顺序执行 → 负载失衡时半量窃取 → 最后同步
```

## 4. 设计目标

### 4.1 性能目标

以 100 万实体、Heavy 后执行、每帧间隔 16ms、8 个 worker、相同 Release 构建和固定测试顺序为目标环境：

```text
C++ IJobChunk p50 <= 0.90ms
C++ IJobChunk p95 <= 1.20ms
C++ IJobChunk max <= 1.50ms
```

硬件和系统调度会造成波动，因此这些绝对值是目标，不是单次运行的合并依据。阶段性改动必须同时满足：

- 同一可执行文件中运行旧/新路径，且两者使用相同数据、Warmup、Measure 和 Sleep 顺序。
- 至少进行 5 轮独立进程测试，报告每轮 p50/p95/max 和跨轮中位数。
- 新路径 p50 不劣于旧路径 5% 以上。
- 新路径跨轮 p95 至少改善 15%，或者在达到 `<= 1.20ms` 后保持稳定。
- 正确性、Trace 完整性和 Worker 参与约束全部通过；不能用关闭统计得到的耗时作为验收结果。

### 4.2 调度目标

- 正常执行至少 90% 的 Tile 来自 worker 本地分区。
- 只有进入尾部负载不均衡阶段才发生 steal。
- 每次 steal 获取受害者剩余工作的约一半，而不是一个 Tile。
- 普通发布仍只唤醒 `workerTarget` 个 worker。
- 一个逻辑执行单元只能完成一次，异常、Shutdown、依赖竞争时也不能重复。
- `Complete()` 可以协作，但不能破坏 worker 的本地性。

### 4.3 非目标

- 第一阶段不做硬 affinity。
- 第一阶段不使用无边界自旋掩盖调度延迟。
- 不把每个 Tile 都放入全局 MPMC 队列。
- 不要求一次性删除旧调度路径。

## 5. 三层粒度模型

新架构必须明确区分三个概念。

### 5.1 物理 ECS Chunk

物理 Chunk 是 ECS 的存储和结构变化单位：

- 相同 Archetype 的实体存放在同一类 Chunk 中。
- 组件采用 SoA 并行数组。
- Entity ID 与组件数组使用相同索引。
- `IJobChunk.Execute()` 对一个物理 Chunk 只调用一次。

长期建议把物理 Chunk 从“固定超大实体容量”改成“固定字节预算”。推荐先测试：

```text
16KiB：最接近 Unity，Chunk 多，局部性强
32KiB：折中方案，降低元数据和 Query 枚举数量
64KiB：迁移风险较低，但尾延迟改善可能较弱
```

不能简单地把一个物理 Chunk 拆成多个 `IJobChunk.Execute()` 调用，因为用户 Job 可能包含每 Chunk 一次的逻辑或副作用。物理 Chunk 缩小必须在 ECS 存储层完成，而不是仅在 JobSystem 中伪造多个 Chunk。

### 5.2 执行 Tile

Tile 是调度器的负载平衡单位，不一定等于物理 Chunk。

建议规则：

- `IJobChunk`：Tile 包含一个或多个连续物理 Chunk，但每个物理 Chunk 的 `Execute()` 仍只调用一次。
- `IJobEntity`：允许在一个物理 Chunk 内按实体子区间切 Tile，因为其语义是逐实体执行。
- Tile 目标数据量建议从 64~128KiB 开始测试。
- Tile 描述符只保存索引/偏移，不复制组件数据。

示例：

```cpp
struct ExecutionTile
{
    uint32_t firstChunk;
    uint16_t chunkCount;
    uint16_t flags;

    // 仅 IJobEntity 子区间使用。
    uint32_t firstEntityInChunk;
    uint32_t entityCount;
};
```

### 5.3 Worker 本地分区

本地分区是一段连续 Tile 索引区间。发布时把 Tile 按连续区间分给 `workerTarget` 个 partition。

```cpp
struct alignas(64) LocalPartition
{
    std::atomic<uint32_t> front;
    std::atomic<uint32_t> back;

    uint32_t initialFront;
    uint32_t initialBack;
    uint32_t ownerSlot;
};
```

`front` 由 owner 正向领取，`back` 由 thief 通过 CAS 从尾部缩减。每个 partition 单独占用缓存行，避免多个 worker 的热游标互相伪共享。

## 6. 总体架构

### 6.1 唯一执行器原则

迁移完成后的运行时只能存在一套执行 Job 的 worker 线程。Taskflow 可以暂时保留在旧路径中做 A/B，但新旧路径不能在同一次调度中同时参与同一个批次；新路径验收后应删除 Taskflow 执行器，而不是长期维护两个线程池。

建议模块边界：

```text
Scheduler
  负责依赖、Batch 生命周期和发布策略
        │
        ▼
NativeWorkerPool
  负责线程、ticket 队列、精确唤醒和 park
        │
        ▼
ChunkBatchState
  负责 Tile、LocalPartition、half-steal、完成计数
        │
        ▼
JobAdapter
  负责 IJobChunk / IJobEntity / ISPC 的回调适配
```

边界约束：

- WorkerPool 不理解 ECS 组件，只执行 Batch 提供的 Tile。
- Batch 不创建线程，也不直接操作平台 semaphore。
- Scheduler 不逐 Tile 调度，只发布参与 ticket。
- JobAdapter 不拥有 Batch 生命周期。
- 所有公开调度入口最终进入同一个 Batch/WorkerPool 协议。

```text
EntityQuery
    │
    ▼
连续 ChunkJobData 视图
    │
    ▼
TileBuilder
    │
    ├── Tile 0..29   → Partition 0
    ├── Tile 30..60  → Partition 1
    ├── Tile 61..90  → Partition 2
    ├── ...
    └── Tile N..M    → Partition 7
                         │
                         ▼
                   8 个 queue ticket
                         │
          ┌──────────────┼──────────────┐
          ▼              ▼              ▼
      Worker A       Worker B       Worker H
      本地顺序       本地顺序       本地顺序
          │              │              │
          └──── 本地耗尽后半量窃取 ─────┘
                         │
                         ▼
                 completedTiles == tileCount
                         │
                         ▼
                       Finalize
```

全局队列只保存“参与这个批次的资格”，不保存数百个 Tile：

```cpp
struct ChunkQueueTicket
{
    ChunkBatchState* batch;
    uint16_t partitionIndex;
};
```

ticket 表示一次参与权，不表示一个 Tile。一个 worker 领取 ticket 后先拥有一个本地 partition；它可以在协作预算内处理多个本地 Tile，随后才尝试 steal 或让出执行权。`workerTarget=8` 时，不论 Tile 是 31 个还是 3000 个，初始参与 ticket 都不得超过 8 个。

因此 245 个 Tile、8 个 worker 仍然只需要：

```text
8 个 queue ticket
8 个 wake permit
8 个 LocalPartition
```

## 7. 发布阶段

### 7.1 构建 Tile

`TileBuilder` 输入：

- 匹配 Query 的连续 `ChunkJobData`。
- 每个 Chunk 的实体数量和组件字节跨度。
- Job 类型：IJobChunk 或 IJobEntity。
- 目标 Tile 字节数。

输出：

- 连续 `ExecutionTile[]`。
- `tileCount`。
- 实际覆盖的实体/Chunk 总数。

TileBuilder 必须保证：

- 不遗漏、不重复。
- IJobChunk 不拆分用户可观察的 Chunk Execute 语义。
- IJobEntity 子区间严格覆盖 `[0, entityCount)`。
- enabled mask/filter 的边界不被错误合并。

### 7.2 初始分区

使用连续、近似均匀的静态分区：

```cpp
for (int slot = 0; slot < workerTarget; ++slot)
{
    begin = tileCount * slot / workerTarget;
    end   = tileCount * (slot + 1) / workerTarget;
    partitions[slot] = [begin, end);
}
```

不能使用 round-robin：

```text
错误：worker 0 = 0, 8, 16, 24 ...
正确：worker 0 = 0, 1, 2, 3 ...
```

连续分区能让 worker 顺序访问 Query 生成的 Chunk 列表，减少缓存和 TLB 抖动。

### 7.3 发布 ticket 与唤醒

每个非空 partition 发布一个 ticket：

```cpp
ticketCount = min(workerTarget, nonEmptyPartitionCount);
workerWakeSemaphore.release(ticketCount);
```

保持当前已经验证过的精确唤醒，不恢复 `notify_all`。

## 8. Worker 本地执行

worker 从全局 ticket 队列取得一个 partition 后，优先顺序处理自己的 Tile。

```cpp
bool TryTakeLocal(LocalPartition& partition, uint32_t& tileIndex)
{
    uint32_t front = partition.front.load(relaxed);
    while (true)
    {
        uint32_t back = partition.back.load(acquire);
        if (front >= back) return false;
        if (partition.front.compare_exchange_weak(
                front, front + 1, acq_rel, relaxed))
        {
            tileIndex = front;
            return true;
        }
    }
}
```

本地领取仍使用原子操作，因为 `Complete()` 或 thief 可能并发访问该 partition；但竞争只发生在尾部阶段，不再是所有线程每个 range 都争同一原子变量。

执行循环：

```cpp
while (TryTakeLocal(partition, tile))
{
    ExecuteTile(batch, tile);
}

while (TryStealHalf(batch, workerSlot, stolenSpan))
{
    ExecuteSpan(batch, stolenSpan);
}
```

## 9. 半量窃取

### 9.1 为什么不是一次偷一个 Tile

一次偷一个 Tile 会导致：

- 频繁扫描 victim。
- 频繁 CAS 同一 victim 的游标。
- thief 处理一个 Tile 后马上再次进入调度器。
- 数据局部性退化成当前全局游标模型。

一次偷走剩余工作的一半，则能用一次 CAS 获得一个连续区间，后续在 thief 本地顺序执行。

### 9.2 Victim 选择

第一版不需要随机复杂算法。可采用轮转扫描：

```cpp
victim = (workerSlot + stealRound + 1) % workerTarget;
```

扫描时读取每个 partition 的近似剩余量：

```cpp
remaining = max(0, back - front);
```

优先选择剩余量最大的 victim。该值允许近似，不要求全局一致快照。

### 9.3 Steal 算法

```cpp
bool TryStealHalf(LocalPartition& victim, TileSpan& stolen)
{
    uint32_t back = victim.back.load(acquire);
    while (true)
    {
        uint32_t front = victim.front.load(acquire);
        uint32_t remaining = back > front ? back - front : 0;
        if (remaining <= 1) return false;

        uint32_t stealCount = remaining / 2;
        uint32_t newBack = back - stealCount;
        if (victim.back.compare_exchange_weak(
                back, newBack, acq_rel, acquire))
        {
            stolen = { newBack, back };
            return true;
        }
    }
}
```

owner 从头部向后执行，thief 从尾部取连续区间。双方通常访问不同区域，减少组件数据相互干扰。

### 9.4 窃取停止条件

出现以下任一条件时停止：

- 所有 partition 剩余量都不超过 1。
- `completedTiles == tileCount`。
- batch 已因异常进入 cancelled 状态。
- Scheduler 正在 Shutdown。
- 当前 worker 达到公平性预算，且全局队列存在其他批次。

## 10. Complete 协作模型

`Complete()` 不再访问全局 `nextRange`，而是作为特殊 thief 参与目标批次。

### 10.1 AssistPolicy

```cpp
enum class CompleteAssistPolicy : uint8_t
{
    DrainTarget,
    OneTile,
    WorkerOnly
};
```

- `DrainTarget`：持续从剩余量最大的 partition 半量窃取，直到无未领取 Tile。
- `OneTile`：最多领取一个 Tile，然后等待 worker。
- `WorkerOnly`：不执行 Tile，只等待完成。

默认保持 `DrainTarget`，以兼容当前已经确认的语义。Sleep/ISPC 场景通过配置进行 A/B 测试，不在架构迁移时同时改变默认策略。

### 10.2 主线程协作规则

主线程不能直接成为某个 worker partition 的 owner，否则会与刚被唤醒的 worker 竞争头部。它只能：

1. 等待初始 worker ticket 完成领取。
2. 找到剩余量最大的 partition。
3. 从该 partition 尾部窃取一个连续 span。
4. 顺序执行这个 span。

这使 `Complete()` 的帮助行为与 worker steal 使用同一正确性模型。

## 11. 多批次公平性

当前 worker ticket 会一直执行直到整个 batch 的全局 range 耗尽。新架构应避免一个大型 batch 长时间独占线程池。

每个 participant 使用协作预算：

```text
本地最多执行 N 个 Tile，或
连续执行最多 T 微秒
```

预算到达后：

- 如果全局队列没有其他 batch，继续当前 batch，避免无意义 requeue。
- 如果存在其他 batch，把剩余本地 span 重新发布为轻量 continuation ticket。
- continuation 只释放一个 permit，不唤醒全池。

第一版建议使用 Tile 数预算而非时间预算，因为读取时钟本身会进入热路径。只有出现明显的跨 batch 饥饿后，再增加低频时间检查。

## 12. ChunkBatchState 重构

目标状态结构示意：

```cpp
struct ChunkBatchState
{
    // 不可变执行描述
    ExecutionTile* tiles;
    uint32_t tileCount;
    uint16_t workerTarget;
    CompleteAssistPolicy assistPolicy;

    // 本地分区
    LocalPartition* partitions;
    uint16_t partitionCount;

    // 完成与生命周期
    std::atomic<uint32_t> completedTiles;
    std::atomic<uint32_t> activeParticipants;
    std::atomic<uint32_t> queueTokens;
    std::atomic<uint32_t> assistReaders;
    std::atomic<bool> cleanupDone;
    std::atomic<bool> cancelled;

    // 诊断
    uint64_t diagnosticId;
};
```

应删除或退出热路径的字段：

```text
nextRange
把所有 worker 都视为 steal 的旧统计语义
每个 ticket 循环争抢全局 range 的执行模型
```

## 13. Exact-once 与内存模型

### 13.1 Tile 唯一所有权

Tile 只可能通过以下两种方式被取得：

- owner 成功 CAS 增加 `front`。
- thief 成功 CAS 减少 `back`，取得 `[newBack, oldBack)`。

两种操作不会生成重叠区间。失败的 CAS 必须重新读取边界，不能继续使用旧 span。

### 13.2 完成计数

每个成功执行的 Tile 在回调返回后执行：

```cpp
if (completedTiles.fetch_add(1, acq_rel) + 1 == tileCount)
    FinalizeChunkBatch(batch);
```

`FinalizeChunkBatch` 继续使用 `cleanupDone.exchange(true)` 保证 cleanup 和 Handle 完成只发生一次。

### 13.3 异常

任何 Tile 回调异常时：

1. 原子设置 `cancelled = true`。
2. 后续 owner/thief 停止领取新 Tile。
3. 已进入回调的 Tile 允许退出。
4. cleanup 只运行一次。
5. Handle 完成并把异常交还托管层。

异常路径不能通过伪造 `completedTiles = tileCount` 释放仍在执行的状态对象，生命周期仍由 `activeParticipants`、queue token 和 assist reader 共同保护。

### 13.4 Batch 生命周期和 Assist 安全

`Complete()` 不得读取一个可能被完成回调立即删除的裸 `BatchState*`。进入 assist 前必须先取得 reader 引用，退出后释放；回收必须同时满足：

```text
finalized == true
activeParticipants == 0
queueTokens == 0
assistReaders == 0
```

推荐协议：

```cpp
bool TryAcquireAssistReader(HandleState* handle, ChunkBatchState*& batch)
{
    for (;;)
    {
        batch = handle->assistBatch.load(std::memory_order_acquire);
        if (!batch) return false;

        batch->assistReaders.fetch_add(1, std::memory_order_acq_rel);
        if (handle->assistBatch.load(std::memory_order_acquire) == batch)
            return true;

        ReleaseAssistReader(batch);
    }
}
```

Finalize 顺序必须明确：

1. 原子设置 `finalized`，保证只有一个最终完成者。
2. 从 Handle 撤销新的 assist 入口。
3. 等待或延迟回收，直到已有 `assistReaders` 退出。
4. 执行一次 cleanup。
5. 发布 Handle completed。
6. 在 participant、token、reader 全部归零后回收 Batch。

Handle completed 与 Batch 可回收不是同一个条件，不能因为 `Complete()` 已经可以返回就立即 `delete batch`。

### 13.5 Shutdown 和依赖发布

- dependency 完成和 Shutdown 并发时，Batch 只能在“成功发布”与“取消并清理”之间二选一。
- 每个排队 ticket 必须持有 Batch 引用；ticket 被领取、取消或队列清空时必须对称释放。
- Shutdown 先停止接受新 Batch，再唤醒所有已 park worker，最后等待 active participant 退出。
- Shutdown 不得清空队列后直接释放仍被 worker、continuation 或 assist 引用的 Batch。
- 所有 cleanup、Handle 完成和异常传播必须保持 exact-once。

## 14. ECS 存储层调整

如果只改 JobSystem 本地分区，而继续使用超大物理 Chunk，能够减少原子竞争，但无法完全消除单个大 range 的尾延迟。因此完整 B 方案包括 ECS Chunk 字节预算化。

### 14.1 Chunk 容量计算

```text
chunkCapacity = floor(
    (chunkByteBudget - headerBytes - alignmentPadding)
    / bytesPerEntity)
```

其中 `bytesPerEntity` 包含：

- Entity ID/版本。
- 所有非共享、非外置组件。
- enable mask 的摊销。
- 每个组件数组的对齐损耗。

每个组件数组起始地址至少 64 字节对齐，便于 SIMD 和避免跨缓存行起始。

### 14.2 推荐迁移值

不要直接硬编码最终值。引入内部配置：

```cpp
constexpr size_t kDefaultChunkBytes = 32 * 1024;
```

依次测试 64KiB、32KiB、16KiB。选择依据不是单帧最小值，而是：

```text
p50、p95、max
Query 枚举成本
Chunk 元数据内存
结构变化成本
缓存缺失与 DRAM 带宽
```

### 14.3 API 语义

- `ArchetypeChunk.Count/Capacity` 根据新容量变化，但语义不变。
- `IJobChunk.Execute()` 仍是每个物理 Chunk 一次。
- `IJobEntity` 的生成代码不依赖固定 Chunk 容量。
- Query 结果顺序在一次调度内稳定，跨帧不承诺固定地址。

## 15. 诊断指标

新架构不能继续把所有 worker range 领取都统计为 steal。建议新增：

```text
localTiles
localSpans
stealAttempts
stealSuccesses
stolenSpans
stolenTiles
largestStealTiles
victimScans
assistStolenTiles
partitionImbalanceAtPublish
partitionImbalanceAtFinish
tileExecMin/Avg/P95/Max
```

每条 Tile trace 至少包含：

```text
batchId
tileIndex
physicalChunkBegin/count
entityBegin/count
workerIndex
partitionIndex
source = Local / Stolen / Assist
victimPartition
startCore/endCore
startNs/endNs
```

关键判定：

```text
localTiles / totalTiles >= 90%
stealSuccesses << totalTiles
stolenTiles 足以消除尾部不均衡
慢 Tile 是否来自 Assist、某个 victim 或固定物理 Chunk
```

## 16. Worker 等待、唤醒与温度控制

Worker 空闲时不能永久执行 `_mm_pause()` 轮询。永久自旋会维持 package 活跃和温度，尤其不适合“Heavy 后间隔 16ms 再执行 Sleep Job”的测试模型。

建议使用三阶段等待：

```text
Hot：短时间 _mm_pause，吸收紧邻发布
  ↓ 超出热窗口
Warm：少量 yield 或检查全局发布 epoch
  ↓ 仍无工作
Cold：semaphore/atomic_wait park
```

初始建议值只作为可配置实验参数：

```text
Hot spin：20~100μs
Warm yield：0~2 次
Cold：无限等待 permit，Shutdown 时统一唤醒
```

正确性不依赖这些时间参数；参数只影响唤醒延迟和功耗。发布规则必须满足：

- 只为新增的参与权释放 permit，不使用 `notify_all` 处理普通 Batch。
- `workerTarget=8` 时最多发布 8 个初始 ticket/permit。
- 已经处于 Hot/Warm 状态并成功领取 ticket 的 worker 计入参与者，避免重复释放 permit。
- permit、ticket、实际领取者三者分别统计，不能混为一个指标。
- Worker 从 park 返回后记录 wake latency，便于区分调度等待与 Tile 执行长尾。

推荐状态统计：

```text
hotClaimHits
parkEntries
parkWakes
permitsReleased
ticketsPublished
ticketsClaimed
spuriousWakes
wakeLatencyP50/P95/Max
activeWorkersPeak
```

只有在真实统计证明 wake latency 是主要瓶颈时，才增加 Hot spin；不能通过永久自旋把唤醒问题隐藏起来。

## 17. CPU 拓扑策略

本地分区和半量窃取稳定后，才测试 CPU 软亲和：

- Windows 使用 `GetSystemCpuSetInformation` 获取 CPU Set 和效率类别。
- 使用 `SetThreadIdealProcessorEx` 设置 ideal processor 提示。
- 给主线程保留一个逻辑处理器候选。
- 不在默认路径使用硬 affinity mask。

官方 API 说明：

- [GetSystemCpuSetInformation](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemcpusetinformation)
- [SetThreadIdealProcessorEx](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setthreadidealprocessorex)

该阶段是平台调优，不是 B 架构正确性的前提。

## 18. 兼容迁移方案

### 阶段 0：固定基线

- 从可编译、测试通过、具备精确唤醒和真实 Trace 的版本建立基线；当前 Claude 未接线的 WorkerPool 不属于基线。
- 保留可靠版本的全局 `nextRange` 路径作为 A/B 对照，不继续扩大其职责。
- 固定 Sleep 位于 Heavy 后。
- 固定 workerCap、Warmup、Measure、Sleep 时长。
- 保存 p50/p95/max 和完整调试统计。
- 验证日志中 batch ID 非零、Trace 完整，`ticketsPublished/permitsReleased/workersPeak` 与 workerTarget 一致。
- 修复 Native 测试编译，并为 Assist 生命周期、workerTarget 和公开 API 行为建立回归测试。

### 阶段 1：双路径 BatchState

- 增加 `LocalPartition` 和 Tile 描述。
- 使用内部环境变量或编译开关选择旧/新路径。
- ECS 存储和公开 API 暂不变化。

### 阶段 2：本地分区，不启用 steal

- 验证连续分区 exact-once。
- 对比全局游标与静态本地分区。
- 确认 `localTiles == totalTiles`。

### 阶段 3：启用半量 steal

- 增加 owner/thief 并发测试。
- 验证不重叠、不遗漏。
- 观察 steal 是否只发生在尾部。

### 阶段 4：迁移 Complete Assist

- `Complete()` 改为特殊 thief。
- 保持默认 `DrainTarget`。
- 单独测试 `OneTile/WorkerOnly`，不与调度器重构混为同一个性能变量。

### 阶段 5：物理 Chunk 字节预算化

- 先 64KiB，再 32KiB，最后 16KiB。
- 修正 Query、结构变化、序列化和 Chunk 池。
- IJobChunk 保持每物理 Chunk 一次 Execute。

### 阶段 6：删除旧路径

只有在以下条件全部满足后删除：

- 正确性测试全部通过。
- Shutdown/异常/依赖压力测试通过。
- p50 不退化。
- p95 明显优于旧路径。
- 至少连续多轮测试无偶发重复、遗漏和死锁。

## 19. 测试设计

### 19.1 正确性

- 每个 Tile/实体/Chunk exact-once。
- owner 与多个 thief 并发时区间不重叠。
- 奇数 Tile 数半量窃取无遗漏。
- `tileCount < workerTarget`。
- 空 Query、单 Chunk、单实体。
- enabled mask/filter 跨 Tile 边界。
- IJobChunk 每个物理 Chunk 只调用一次。
- IJobEntity 子区间覆盖完整实体集合。

### 19.2 生命周期

- `Complete()` 与 worker 同时取得最后 Tile。
- Handle copy 只 cleanup 一次。
- Dependency 完成与 publish 并发。
- Shutdown 时存在本地 span、stolen span、assist span。
- 回调异常时不再发布新工作，状态不提前回收。

### 19.3 调度压力

- 1、2、8、15 个 worker。
- 1 到数千个 Tile。
- 多个 batch 同时发布。
- 一个 Heavy batch 与多个轻量 batch 并存。
- 高频 Schedule/Complete。

### 19.4 性能

每次只改变一个变量：

```text
旧全局游标 vs 本地分区
无 steal vs 半量 steal
64KiB vs 32KiB vs 16KiB Chunk
DrainTarget vs OneTile vs WorkerOnly
soft ideal processor 开/关
```

结果必须报告 p50、p95、max，不能只报告 avg。

## 20. 风险与应对

### 风险 1：小 Chunk 增加元数据和 Query 成本

应对：物理 Chunk 与 Tile 分离；多个小 Chunk 合成一个 64~128KiB Tile，本地顺序执行。

### 风险 2：过多 steal 退化成全局竞争

应对：只偷剩余一半；剩余量不超过 1 时不偷；记录 steal 成功率和 victim 扫描数。

### 风险 3：IJobChunk 语义被实体子 Tile 破坏

应对：IJobChunk 不在调度层拆分物理 Chunk；通过缩小物理 Chunk 解决粒度问题。

### 风险 4：Complete 与 worker 重叠领取

应对：Complete 使用相同的 `back` CAS 半量窃取协议，不增加第二套领取机制。

### 风险 5：一次大改难以定位回归

应对：旧路径与新路径并存到验收完成；每个阶段都有独立开关和回归测试。

## 21. 最终建议

B 方案应按以下核心组合实现：

```text
固定字节预算的物理 ECS Chunk
        +
64~128KiB 调度 Tile
        +
每 worker 连续本地分区
        +
从 victim 尾部窃取剩余一半
        +
Complete 作为特殊 thief
        +
精确 ticket/permit 唤醒
```

其中最重要的不是把 `nextRange` 换成另一种原子变量，而是改变工作所有权：

```text
旧：所有线程共同竞争一份全局工作
新：每个线程先拥有稳定的本地工作，只在尾部重新平衡
```

这才是当前 EntJoy 与 Unity 风格 Job System 之间最关键的架构差异，也是降低 p95 尾延迟、保持缓存局部性和继续接近稳定 0.9ms 的主要方向。
