# EntJoy JobSystem 稳定性与性能优化方案

## 1. 目的与范围

本文记录对 EntJoy JobSystem 的 C++ 调度器、C# 桥接层、句柄所有权及 ABI 的审查结果，并给出面向生产环境的改造方案。

目标：

- 语义尽量对齐 Unity JobSystem；
- 消除死锁、永久等待、UAF、double release 和 data race；
- 保留 Chase-Lev deque、token batching、PushMany、defer notify；
- 不在每 tile 热路径引入 mutex、shared_ptr、分配或依赖图遍历；
- Debug/Development 构建提供 Safety 检查，Release 构建保持低开销。

本文同时记录审查结论、已落地修复和后续计划。历史问题段落保留原始
复现背景；每个问题以“状态”标注当前是否已修复。完整的命令输出和阶段
结果见 [`tools/JobSystemBugTests/FinalReport/README.md`](../tools/JobSystemBugTests/FinalReport/README.md)。

## 1.1 当前交付状态

本 JobSystem 修复已合并为单个 commit `03d134d "Jobsystem修复"`（自 `1e86d78`
之后）。以下用「阶段」编号追溯逻辑变更，不再引用已合并的 commit 哈希：

| 阶段 | 交付物 | 状态 |
|---|---|---|
| 0 | 基线、审查文档 | 已记录 |
| 1 | C# Bridge 依赖与句柄修复 | 已提交 |
| 1b | Managed Complete 异常语义 | 已提交 |
| 2 | Batch pending/Shutdown 退役修复 | 已提交 |
| 3 | Scheduler 初始化/销毁串行化 | 已提交 |
| 4 | ABI 校验与 fallback | 已提交 |
| 5 | 阶段回归报告 | 已提交 |
| 6 | C++ 异步异常与 cleanup 终态 | 已提交；Stage6 专项 9/9 |
| 7a | defer depth 下溢/异常终态 | 已提交；Stage7 专项 |
| 7b | C# Complete 等待期 retain | 已提交；Stage8 回归 |
| 7c | BatchStorage 退役单线程化 | 已提交；Stage9 回归 |
| 7d | 隐式批 toggle/pending 竞态 | 已提交；Stage10 回归 |
| 7e | 旧 ABI fixture + 性能基准 | 已提交；Stage11 + PerfBench |

阶段 7 关闭了 Stage6 复查列出的 4 项正确性风险（defer depth、C# Complete
retain、BatchStorage 退役竞态、隐式批 toggle 竞态），每项先失败测试再修复。
scheduler 生命周期 guard 评估为契约化（见 §3.2 与 §11）。ASAN/TSAN 仍缺
工具链证据，Safety Layer、Cancel API 等功能扩展明确不在范围内。

## 2. 当前结构

    C# JobScheduler / NativeJobScheduler
            ↓ P/Invoke function pointers
    NativeJobCore（上下文、委托、ABI、异常、句柄桥接）
            ↓
    C++ Exports.cpp
            ↓
    JobSystem::Scheduler / HandleState / BatchState
            ↓
    ChaseLevScheduler
            ├── per-worker SparseTileDeque
            ├── MPMC Injector
            ├── RangeTaskPool
            └── BatchStorage / token batching

主要生命周期：

    Schedule
      → 创建 HandleState / BatchStorage
      → 建立依赖
      → Submit 或放入 pending
      → worker/token 执行 tile
      → completed
      → backendRetired
      → storage/state 回收

审查时识别出的核心风险（部分已经修复）：

1. HandleState 的引用计数不完全等价于 BatchStorage 的独立存活保证（待后续
   intrusive refcount 完整化）；
2. C# NativeJobHandle 的复制语义与 native refcount 语义不一致（本轮已修复
   Complete 消费问题）；
3. scheduler 全局指针的检查与使用需要继续扩大到所有公开 API（初始化/销毁
   已串行化，完整 guard 仍待补齐）。

## 3. 已确认问题

### 3.1 C++ Batch 和 Shutdown

#### ForceFinalizeBatch 可能让句柄永久等待（已修复，需 sanitizer 复核）

位置：JobSystem_Tiles.cpp 第 829 行。

历史实现只设置 backendRetired=true 并清理上下文，没有保证 completed=true。可能出现：

    completed      = false
    backendRetired = true

此时 JobHandle::Complete() 仍等待 completed，形成永久等待。

当前实现已在强制退役路径发布 completed、backendRetired、cleanup 和引用释放，
并保持默认 Drain。仍需在 ASAN/TSAN CI 中复核极端残余 token 时序。

必须明确 Shutdown 语义：

- Drain：继续执行残余任务后正常完成；
- Cancel：不执行未认领任务，但标记 cancelled、完成句柄并执行 cleanup。

#### FlushPendingSubmits 存在 UAF（已修复）

位置：JobSystem_Tiles.cpp 第 600～617 行。

历史实现是在 SubmitBatch(b) 返回后再次访问 b->handle。worker 可能在两行之间
完成最后 token、释放并重建 BatchStorage，导致 b 或 b->handle 失效。当前实现
在提交前保存 state，并用 pending 引用完成 Release，不再在提交后解引用 BatchState。

应在提交前保存并单独持有 HandleState，不要在可能退役的 BatchState 上重新取指针。

#### 双条件退役存在并发 UAF（部分缓解，待完整所有权重构）

位置：JobSystem_Tiles.cpp 第 723～811 行。

最后 tile 完成者和最后 token/task 完成者都可能进入 TryFinalizeChaseLevBatch。一个线程成功设置 finalized 后销毁 BatchState，另一个线程仍可能访问 batch->handle、tilesRemaining、pendingTasks 或 finalized。

当前已有 finalized CAS、pendingTasks 双条件和残余批次去重，能避免已发现的
双重 cleanup 路径；但 BatchStorage 尚无独立 intrusive refcount，不能把所有
跨线程读者的存活保证形式化，因此仍是生产签字前的重点审计项。

#### scheduler 全局指针与 Shutdown 并发不安全（部分修复）

位置：

- JobSystem_Scheduler.cpp 第 269～308 行；
- JobSystem_Tiles.cpp 第 543～573 行；
- JobSystem_State.cpp 第 271～277 行。

历史实现存在上述窗口。当前 Initialize/Shutdown 已由生命周期锁串行化，重复
Shutdown 可安全返回；Schedule、Complete、Assist、Flush 等所有公开入口尚未
统一采用 scheduler guard，仍需在下一阶段补齐并用 TSAN 验证。

### 3.2 C++ 异常

#### ScheduleFor 异常会跳过完成与释放（基础路径已修复）

位置：JobSystem_Scheduler.cpp 第 347～368 行。

历史路径存在异步异常收尾不完整风险。阶段 6 已为原生 ScheduleFor、
batch/fast-path cleanup、多 tile 异常、并发 Complete 和 shutdown 异常增加
Stage6 专项测试；当前 Release 运行 9/9 通过。仍需在 sanitizer 和生命周期
竞态修复后复核极端提交失败/退役时序，不能把 9/9 作为全量生产签字。

#### cleanup 穿过 noexcept 边界（基础路径已修复，极端回滚待复核）

位置：JobSystem_Tiles.cpp 的 Chunk/General cleanup 适配器。

历史版本的 CleanupChunkContext 和 CleanupGeneralContext 将可能抛出的
originalCleanup 放在 noexcept 边界内。当前适配器已改为捕获并转移异常，
Stage6 的 batch/fast-path cleanup 失败测试通过；ForceFinalize、分配失败和
生命周期竞态仍需单独验证。

#### exception_ptr 存在并发读写（基础路径已加冷路径保护）

历史版本的 HandleState::batchExceptionPtr 是普通字段。当前实现以
exceptionMutex 保护记录/摘取，Stage6 并发 Complete 测试通过；仍需 TSAN
验证异常与最终退役、回滚路径的交错。

异常字段只在异常冷路径加锁，不进入 tile 热路径。

#### Stage6 后仍未关闭的正确性风险（阶段 7 已关闭 4 项）

以下问题在阶段 6 时仍待关闭，阶段 7 已逐项修复（每项标注本质 = 根因）：

1. ~~BatchStorage 无完整 refcount~~ → 阶段 7c：**本质是「最后 tile 完成者」与
   「最后 token 完成者」并发访问同一裸 BatchState 指针，销毁者与读者竞争**。
   改为退役单线程化（pendingTasks 归零线程唯一触发），消除双完成者 data race；
2. ~~隐式批 toggle/pending 竞态~~ → 阶段 7d：**本质是「读开关 → 入队」与
   「置 false → flush」两个 check-then-act 非原子**。SubmitOrPending 锁内复核
   开关，SetEnabled(false) 锁内置 false 再 flush；
3. ~~defer depth 异常/下溢~~ → 阶段 7a：**本质是深度计数器无下界保护，且
   唤醒判断用 `== 0`（下溢为负后永不成立，永久抑制唤醒）**。SubmitBatch 唤醒
   条件改为 `<=0`，ScheduleBatch defer 窗口改 RAII 保证配对；
4. ~~C# Complete 与别名 Release 竞态~~ → 阶段 7b：**本质是「读 handle」与
   「native Complete」之间的 TOCTOU 窗口未持使用期引用**。Complete 等待窗口
   持有 native retain；
5. Schedule、Complete、IsCompleted、Assist、Flush 等公开 API 与
   Shutdown/reset 并发访问裸指针 —— **本质是生命周期对象与使用无同步，但属
   调用方误用**。已评估：完整 guard 会在 Complete 等热路径引入锁（违背 §6
   性能原则），故改为文档化契约（Shutdown 仅主线程、且需在所有 job 完成后
   调用），保留现有 `g_shuttingDown` gate 与 Initialize/Shutdown 串行化。

### 3.3 C# 句柄、依赖和 ABI

#### Native 句柄副本会被 Complete 互相破坏（已修复）

位置：NativeJobHandle.cs 第 45～73 行。

NativeJobHandle 是 struct，但内部共享 NativeJobHandleBox。某个副本调用 Detach 会把共享 Box 的 handle 清零，导致其它副本认为句柄无效并立即返回。

Unity 风格应为：

    Complete = 等待，不消费句柄
    Release/Dispose = 释放句柄引用

#### Managed fallback 的 For/Batch 丢失依赖（已修复）

位置：JobScheduler.cs 第 77～99 行。

Managed fallback 下 ScheduleFor 和 ScheduleBatch 创建包装 job 后没有把 dependsOn 传入 ManagedJobScheduler，后续 job 可能提前执行。

#### 混合 Native/Managed 依赖被静默丢弃（已修复入口校验，需扩展覆盖）

位置：JobHandle.cs 第 37～58 行。

只要依赖数组中存在 Managed handle，就走 Managed 合并路径，Native handle 被排除。Native backend 调度时也会忽略 Managed dependency。

推荐固定 backend，混合依赖直接拒绝。

#### C# 初始化失败时可能遗留 native worker（已修复回滚路径）

位置：JobScheduler.cs 第 26～44 行。

Native 初始化成功后，如果统计布局校验、回调注册或配置阶段失败，C# 直接切换 Managed backend，却没有关闭已经启动的 C++ scheduler，可能形成 Native + Managed 双 worker 系统。

#### Shutdown 标志没有为二次初始化复位（已修复）

位置：NativeJobCore.cs 第 101～103 行和第 737～745 行。

_shutdownRequested 只在第一次 SafeShutdown 中置 1，重新初始化时没有清零。二次 Initialize → Shutdown 可能无法关闭新 scheduler。

#### 旧版 NativeDll 会在 ModuleInitializer 阶段崩溃（已修复 ABI/fallback）

位置：NativeJobCore.cs 第 306～340 行。

新导出使用 NativeLibrary.GetExport 强制加载。若 DLL 缺少 JobSystem_ScheduleBatch 等新符号，程序在模块初始化阶段抛 EntryPointNotFoundException，无法进入 Managed fallback。

## 4. Unity-compatible 目标契约

### JobHandle

- 值语义；复制不改变等待语义；
- Complete 幂等；
- Complete 不清空、不释放句柄；
- Release/Dispose 负责释放引用；
- 句柄只能依赖同一 backend；
- Debug 构建检测无效句柄、重复提交；
- 运行时**不做依赖环检测**（开销大，对齐 Unity）：依赖图必须无环，否则
  Complete 永不返回，由调用方保证无环。

### Schedule、Flush、Complete

    Schedule：
        构造 job、建立依赖、提交或加入 pending

    Flush：
        批量发布、一次唤醒 worker

    Complete：
        自动 Flush，然后等待 completed

这对应 Unity 的 ScheduleBatchedJobs 和 JobHandle.Complete 语义，同时保留 defer notify 优化。

### Shutdown

- 只允许初始化线程/主线程调用；
- 先停止接受新任务；
- 选择 Drain 或 Cancel；
- 所有 job 必须进入 completed/cancelled 终态；
- worker join 完成后才能回收 scheduler；
- 支持重复 Shutdown 和重新 Initialize。

## 5. 生产级实现方案与落地状态

### 5.1 Scheduler 生命周期闸门（串行化已落地，guard 契约化）

生命周期锁只用于 API 边界，不进入 worker 热循环。

当前版本已用生命周期锁串行化 Initialize/Shutdown，并在 shutdown gate 关闭后
拒绝新任务。Schedule、Complete、Assist 等入口的**统一 guard 不引入**：并发
Shutdown 属误用场景，完整 guard 会在 Complete 等热路径引入锁（违背 §6 性能
原则）。改为文档化契约：

- Shutdown 仅主线程调用（`g_mainThreadId` 已校验并拒绝 worker 线程）；
- Shutdown 必须等所有 Schedule/Complete/Assist 返回后调用，不与它们并发；
- 保留 `g_shuttingDown` gate 拒绝 Shutdown 后的新 Schedule；
- `SubmitBatch` 的 `IsRunning()` 检查兜底「已 Stop 未 reset」的提交。

如果采用 shared_ptr，只能在 API 边界复制，不能放入每 tile 路径。更低开销的实现是 scheduler intrusive refcount 或短生命周期 guard。

### 5.2 BatchStorage intrusive refcount（下一项优先级最高）

不为每个 tile 创建 shared_ptr。在 BatchStorage 内增加原子引用计数：

    struct BatchStorage
    {
        std::atomic<uint32_t> refs{1};
        BatchState batch;
        ExecutionTile* tileBuffer{};
        uint32_t tileCapacity{};
    };

引用来源：

    用户句柄、pending 队列、每个 token、Complete 等待者

最后一个引用释放后，storage 才能回收到池中。引用在 token 生命周期边界增加/减少，不在每个 tile 增加/减少。

### 5.3 唯一 finalizer 状态机（已有 finalized CAS，需与 refcount 合并）

建议状态：

    enum class BatchPhase : uint8_t
    {
        Running,
        LogicalCompleted,
        Finalizing,
        Finalized
    };

只有一个线程可以通过 CAS 进入 Finalizing，执行 cleanup、异常转移、状态通知和最终释放；其它线程发现已进入 Finalizing/Finalized 后直接返回。

### 5.4 统一异步执行包装器（现有路径部分覆盖）

所有 C++ 异步执行路径统一使用：

    try
    {
        execute();
    }
    catch (...)
    {
        RecordException(state, std::current_exception());
    }

    try
    {
        cleanup();
    }
    catch (...)
    {
        RecordException(state, std::current_exception());
    }

    CompleteState(state);
    ReleaseState(state);

正常路径不增加锁；异常记录使用 CAS + mutex，仅在异常发生时执行。

### 5.5 C# handle 的低开销修正

如果保持现有 struct API，最小修改是：

- Complete 不再 Detach；
- Complete 只读取共享 IntPtr 并调用 native wait；
- 显式 Release 或 Box finalizer 才释放 native 引用。

这样复制句柄不会改变其它副本的行为，也不需要额外 Task、ManualResetEvent 或 per-copy 分配。

长期版本可以把 public JobHandle 改为 class，但这是 API breaking change，不作为第一阶段要求。

### 5.6 Backend 固定与依赖校验

初始化时选择 backend，之后固定。JobHandle 增加 backend tag：

    enum JobBackend : byte
    {
        None,
        Native,
        Managed
    };

跨 backend 依赖直接抛异常。CombineDependencies 也必须拒绝混合 backend。

### 5.7 ABI 版本与初始化回滚（已落地）

已增加：

    JOB_API uint32_t JobSystem_GetAbiVersion();

C# 加载 DLL 后先检查 ABI version，再加载新功能入口；缺失的
`JobSystem_ScheduleBatch` 作为可选导出探测。初始化过程失败时先尝试
SafeShutdown，再切换 Managed fallback。

## 6. 性能保护原则

以下内容不能放入每 tile 执行路径：

- std::mutex；
- std::shared_ptr；
- std::function 临时构造；
- new/delete；
- 依赖图遍历；
- 日志和字符串格式化；
- 异常对象创建。

允许的额外操作：

| 路径 | 允许的额外操作 |
|---|---|
| Schedule | 一次生命周期检查 |
| Submit | 一次 scheduler guard |
| 每 token | 一次 BatchStorage refcount 增减 |
| 每 tile | 不新增原子操作 |
| Complete 快路径 | 一次 completed acquire |
| 异常路径 | mutex、异常记录、分配均可接受 |
| Shutdown | 可使用完整锁和全量清理 |

当前 Chase-Lev deque、MPMC injector、PushMany、token claim、defer notify 应全部保留。

## 7. 实施顺序与当前下一步

### 已完成阶段（0～7）

1. 建立基线并保存到 `tools/JobSystemBugTests/Baseline`；
2. 修复 C# For/Batch 依赖、句柄副本和 Managed Complete 异常语义；
3. 修复 pending flush、Batch 强制退役和 Drain 状态协议；
4. 串行化初始化/销毁并验证重复重启；
5. 增加 ABI 校验、可选导出探测和安全 fallback；
6. 执行现有测试、阶段测试和 C# Native/Managed 压力测试，生成阶段回归报告；
7. 修复 C++ 异步 callback/cleanup 的终态与异常传播，并以 Stage6 专项测试
   验证（9/9）；
8. 关闭 Stage6 复查列出的 4 项正确性风险（defer depth、C# Complete retain、
   BatchStorage 退役、隐式批 toggle），各配 Stage7～Stage10 专项回归，
   先失败测试再修复、单独提交。

对应提交和结果以 `FinalReport/README.md` 为准。

### 下一阶段 A：收尾剩余项（扩展功能之前）

按以下顺序执行，每一项都必须“先失败测试、再修复、再跑性能/压力、
单独提交”：

1. **scheduler 生命周期契约**：把「Shutdown 仅主线程、且需在所有 job 完成
   后调用，不与 Schedule/Complete 并发」写入契约；保留现有 `g_shuttingDown`
   gate 与 Initialize/Shutdown 串行化，不在热路径引入 guard 锁（见 §5.1）。
2. **旧 DLL fixture**：建立可提交的旧 ABI fixture，覆盖缺失核心导出、缺失
   可选导出、版本不匹配、部分初始化失败和 fallback 后 worker 清理。

### 下一阶段 B：验证门槛

只有 A 全部完成后，才进入：

- Linux/Clang ASAN+UBSAN 和 TSAN CI；
- 10,000 次初始化/销毁及长时间随机依赖压力；
- Schedule/Complete/tile claim 的 P50/P95/P99 与阶段 0 基线对比；
- 对新增 guard/refcount 的热路径反汇编和分配检查。

### 下一阶段 C：功能扩展（暂缓）

Safety Layer、Cancel/Shutdown API、依赖图可视化、worker affinity 调优和
JobCostCache 扩展，必须等 A/B 的正确性与性能门槛全部通过后另开计划和
独立提交，不能混入 Bug 修复。

## 8. 必须增加的回归测试

### 生命周期

- Schedule 与 Shutdown 并发；
- Complete 与 Shutdown 并发；
- 重复 Shutdown；
- Initialize → Shutdown → Initialize → Shutdown；
- 初始化中途失败；
- 旧版 NativeDll 缺少导出函数。

### 所有权

- pending batch 被 worker 立即完成；
- 最后 tile 与最后 token 同时退役；
- Stop 时 Injector 中有残余 token；
- Stop 时 worker deque 中有残余任务；
- 多个 C# handle 副本同时 Complete；
- finalizer 与显式 Release 并发。

### 依赖

- For、Batch、Chunk、Entity 的依赖顺序；
- 多级依赖链；
- 重复依赖；
- 依赖环/自等待：运行时不做检测（对齐 Unity），由调用方保证无环；
- Managed/Native 混合依赖拒绝。

### 异常

- callback 抛异常；
- cleanup 抛异常；
- 多 tile 同时抛异常；
- 多线程同时 Complete；
- Shutdown 时已有异常。

## 9. 性能验收标准（目标；已采集部分 PerfBench）

以下是生产门槛和目标值：

    普通 job 吞吐回退             < 1%
    tile claim 吞吐回退           < 0.5%
    平均调度延迟增加              < 100ns
    ASAN                          0 个 UAF/double free
    TSAN                          0 个 data race
    10000 次 Initialize/Shutdown  无 worker 泄漏

阶段 7 用 `tools/JobSystemPerfBench` 采集了同环境阶段 0 vs 阶段 7 对比
（各 3 次中位数，Workers=15）：

    Schedule+Complete 吞吐        基线 ~1.78 → 阶段 7 ~1.77 M ops/s（<1%，噪声）
    Schedule 延迟 P50/P95         500 / 600 ns → 500 / 600 ns（无差异）
    ParallelFor 吞吐              基线 ~119 → 阶段 7 ~118 M elem/s（噪声范围）

尚未归档的指标（仍需 CI 固定频率 + 多轮中位数）：

- Submit 到首 worker 的时间；
- tile claim throughput；
- Complete latency；
- worker park/wake latency；
- 每 job 的分配次数；
- 10000 次 Initialize/Shutdown 的 worker 泄漏检查。

## 10. 本轮验证结果与限制

阶段测试源代码：

- `tools/JobSystemBugTests/Stage1_CSharpBridge`
- `tools/JobSystemBugTests/Stage2_BatchLifetime`
- `tools/JobSystemBugTests/Stage3_SchedulerLifecycle`
- `tools/JobSystemBugTests/Stage4_AbiCompatibility`
- `tools/JobSystemBugTests/Stage6_CppExceptionSafety`
- `tools/JobSystemBugTests/Stage7_DeferDepth`
- `tools/JobSystemBugTests/Stage8_CompleteRetain`
- `tools/JobSystemBugTests/Stage9_BatchStorageRace`
- `tools/JobSystemBugTests/Stage10_ImplicitBatchToggle`

已运行：

- C++ JobSystemTests.exe：fresh Release exit 0，全部测试通过（约 19.50s）；
- C++ ChaseLevIntegrationTests.exe：fresh Release exit 0，5 项通过（约 265ms）；
- C++ AssistLifetimeTests.exe：fresh Release exit 0（约 238ms）；
- C++ ImplicitBatchTests.exe：fresh Release exit 0，全部测试通过（约 234ms）；
- C++ JobSystemStressTest.exe：fresh Release 20 项通过，3,868,400 jobs，约 26.46s；
- EntJoy.Jobs.csproj：编译成功，有 6 个已有的 ManagedJobScheduler 未赋值字段警告；
- Stage1～Stage4 专门测试：全部通过；
- Stage6 C++ 异常/cleanup 专项：fresh Release 9/9 通过（约 218ms）；
- Stage7 defer depth 下溢：修复前 Complete 永久阻塞，修复后通过；
- Stage8 C# Complete retain：并发 Complete/Release 回归 2/2；
- Stage9 BatchStorage 退役：storage 恰好回收一次 + 主线程 assist
  并发（约 19 万批）无崩溃；
- Stage10 隐式批 toggle：并发调度 + 开关切换，flush 后 pending
  必空（stranded-rounds 0）；
- Stage11 旧 ABI fixture：缺 ABI export / 版本不匹配均安全 fallback
  且 job 正常执行（2/2）；
- JobSystemPerfBench：阶段 0 vs 阶段 7 同环境对比，Schedule 吞吐 <1%、延迟
  P50/P95 无差异、ParallelFor 噪声范围（详见 §9）；
- 上述 Stress 与阶段 0 的 22.48s/22.69s 记录不是同一次构建环境，不能据此
  宣称性能门槛通过；
- C# Managed/Native Stress：使用同版本 ABI DLL 后全部通过，约 67.51s；
- ASAN/TSAN：本 Windows 主机未安装可用工具链，尚未形成 sanitizer 证据。

C# Native stress 在加载输出目录中的旧版 NativeDll.dll 时曾失败：

    EntryPointNotFoundException:
    Unable to find an entry point named 'JobSystem_ScheduleBatch' in DLL.

该失败促成了 ABI 校验和优雅 fallback 修复；替换为同版本 DLL 后，Native
stress 已通过。旧 DLL fixture 仍需纳入 CI，避免只依赖手工替换输出文件。

## 11. 下一步决策

当前不应直接开始 Safety Layer 或 Cancel API。阶段 7 已关闭 Stage6 复查的
4 项正确性风险（defer depth、C# Complete retain、BatchStorage 退役、隐式批
toggle），scheduler 生命周期契约已文档化（§5.1）。剩余工作为：

1. 旧 DLL ABI fixture 已建立（Stage11），剩余是纳入 CI 并补「缺失核心导出」
   「初始化中途失败」两个分支；
2. 在 Linux/Clang CI 补跑 ASAN+UBSAN、TSAN，验证 defer/toggle/退役竞态修复
   无残留 data race；
3. 性能完整签字：CI 固定频率 + 多轮中位数 + 分位数归档（PerfBench 已建，
   tile-claim / tasks/sec 独立分位数仍缺）。

性能门槛齐全前，本实现应标记为“候选生产版本”，而不是最终生产签字版本。

## 12. 总结

生产级方案的核心不是在 worker 热路径上增加锁，而是建立清晰的不变量：

    API 边界可以加锁；
    任务热路径保持无锁；
    BatchStorage 必须有独立生命周期；
    退役必须只有一个 finalizer；
    Complete 不能破坏句柄副本；
    Native/Managed backend 不允许静默混用；
    完成、取消、异常都必须进入明确终态。

已完成的阶段保留了当前 Chase-Lev、token、defer notify 性能结构。阶段 7
关闭了 defer depth 下溢、C# Complete 等待期 retain、BatchStorage 退役单线程化、
隐式批 toggle 四项正确性风险，每项先失败测试再修复。所有修复已 squash 为
单个 commit `03d134d "Jobsystem修复"`。剩余工作是旧 ABI fixture 纳入 CI、
sanitizer 证据与性能完整签字，依据这些决定是否进入生产签字和后续扩展。
