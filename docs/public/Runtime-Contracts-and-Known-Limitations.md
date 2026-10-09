# EntJoy 运行时契约与已知限制

本文定义 EntJoy v1.0 的线程、生命周期、依赖和所有权边界。未满足契约的行为不属于框架保证的支持范围；调用方应在自己的封装层保证这些前置条件。

> NativeTranspiler（源生成器 / 原生编译任务）侧的边界、诊断与回归防线另见
> [`NativeTranspiler-Boundaries-and-Diagnostics.md`](./NativeTranspiler-Boundaries-and-Diagnostics.md)。

## 调度器生命周期

- 在提交任何 Job 之前调用 `JobScheduler.Initialize()`（统一入口：native 优先，不可用时自动回退 managed）；所有 World、JobHandle 和相关资源释放后，再调用 `JobScheduler.Shutdown()`。
- `ManagedJobScheduler` 具有相同的初始化/关闭顺序要求。
- `Initialize`、`Shutdown`、调度、`Complete` 和资源销毁不得并发交错。关闭期间不得提交新 Job，也不得在另一个线程继续调用调度器 API。
- `Shutdown` 必须由初始化线程（通常是主线程）调用。worker 线程或其他线程调用关闭会被拒绝。
- 调度器关闭后不得继续使用旧句柄、旧 World 或旧的原生视图；重新初始化后，旧句柄仍然视为失效句柄。

## Job 依赖和 Complete

- 依赖图必须是无环 DAG。循环依赖会使依赖回调永远无法满足。
- 不得在 Job 执行体内同步 `Complete` 自己、自己的祖先、自己的后继，或包含当前 Job 的组合句柄。
- Job 抛出的异常只在对应句柄 `Complete` 时传播；调用方必须完成需要观察异常的句柄。
- Job 执行期间不得直接进行 ECS 结构变更。请使用 `DeferredCommandBuffer`，并在主线程 Playback。

## 系统依赖传播（SystemState.Dependency）

- 系统内 `job.Schedule(query)` 未显式传 `dependsOn` 时，自动继承执行上下文的累积依赖，并在调度后回写。
- `SystemRunner` 按系统的 `[Read]`/`[Write]` 声明合并冲突依赖：读等写、写等写；**读读不互相等待**。依赖按组件类型传播，两张表：`lastWrite[X]`（写系统写回，读写系统都入站合并）与 `lastRead[X]`（读系统写回，多读系统 **merge**；**只有写系统**入站合并；写系统完成后清空）⇒ `[Read(X)]` 的 Job 仍在飞时后续 `[Write(X)]` 必须等它，而读读仍并行。无冲突系统不串行；开关 `ENTJOY_SYSTEM_READ_WRITE_ORDER=0` 可回到"只传播写依赖"的旧行为。
- `ISystemWithState.OnUpdate(ref SystemState)` 可显式读写 `state.Dependency`、调用 `state.CompleteDependency()` 同步等待本系统所有 Job。
- `World.DefaultWorld` 为 `[ThreadStatic]`（每线程独立）；Job worker 线程由调度器绑定所属 World，`SystemAPI.SendEvent` 写入正确 World。

## ECS 线程模型

- `World`、`EntityManager`、`Archetype` 的结构性 API（创建/销毁实体、增删组件、Playback、换帧、Dispose）是主线程 API。
- 主线程调用结构性 API 前，框架会等待该 World 的活动 Job；调用方不得绕过该同步路径直接修改 Chunk 或组件存储。
- `World.Dispose()`、`EntityManager.Dispose()`、`Archetype.Dispose()` 不得与查询、调度、事件 drain 或其他销毁操作并发执行。
- `EntityManager.GetAllArchetypes()` 返回当前 Archetype 的快照数组；后续结构变更不会更新该快照。

## EventStream

- `SendEvent`、`NextFrame`、`ReadBuffer` 和 `Dispose` 内部已串行化，可安全地并发调用；但应用层仍应在换帧后再消费上一帧数据。
- 调用 `NextFrame` 前必须确保本帧事件生产已结束；读取只允许发生在换帧完成后。
- `EventStream.Dispose()` 后不得再发送、读取或 drain 事件。
- `World.SendEvent` 返回 bool，满容量（默认 1024）返回 false；累计丢弃数通过 `EventStream<T>.OverflowCount` 观测（丢弃是显式、可观测的，不是静默行为）。
- `[RunWhen(typeof(T))]` 系统在「上一帧有 T 事件」时运行（一帧事件延迟）；事件计数由 `World.SendEvent` 在帧末自动统计，无需手动 `EventCounter.Increment`。

## 原生内存和容器

- `NativeArray`、`NativeList`、`UnsafeList` 和 `DeferredCommandBuffer` 的长度、索引、容量和字节数必须是非负且不发生整数溢出；复制区间必须完全落在源/目标范围内。
- 拥有内存的容器必须由其拥有者准确调用一次 `Dispose()`；视图不会延长底层内存生命周期。
- `PersistentAllocator.Free` 只应接收 `PersistentAllocator.Alloc` 返回的 payload 指针。其他 allocator、CRT 或第三方 DLL 的指针必须使用其对应的释放函数。
- `NativeArray.FromExternalPtr` 创建的视图不拥有外部内存；调用方必须保证该指针在所有读写和 Job 完成前保持有效。

## 并行读写冲突检测

EntJoy 在读写点按「Job 执行上下文」登记持有者（写者或读者），用于在运行时捕获主线程与活动 Job 之间、以及多 Job 对同一容器的并发访问冲突。语义如下：

- **主线程访问拦截（完整双向）**：只要一个容器的句柄正被某个活动 Job 引用（无论读或写），主线程对它的任何访问（读或写）都抛 `InvalidOperationException`，提示"Complete() 后再访问"。Job 内写入若抛出，异常由对应句柄 `Complete()` 归集后以 `AggregateException` 重抛。
- **Job 间冲突（写-写）**：不同 Job（不同执行上下文）在未形成依赖的情况下交叉写同一容器，冲突在冲突那次写入抛出。
- **豁免（合法并行，不检测）**：同一 Job 的并行分块 tile 共享同一执行上下文，放行；ECS chunk 任务的组件列使用共享句柄（`ExemptWriteTracking`），对该句柄的持有跟踪整体豁免。
- **检测范围**：完整双向——主线程 vs Job 的读/写任意组合均拦截；Job 之间的「读-读」「读-写」不冲突（读共享合法）。Job 间冲突检测仅覆盖「写-写」。
- **拦截窗口从"某 worker 首次访问该容器"开始**（声明是**惰性登记**的）：
  写声明在 job 内首次写时登记（`AtomicSafetyHandle.TryAcquireWriteContext`），读登记在 job 内首次读时登记（`RegisterRead`），二者都在 job 结束（`Complete()`）时按执行上下文释放。
  ⇒ `Schedule()` 返回后、任何 worker 尚未触碰该容器之前的**短暂窗口内**，主线程访问不会被拦（此时确实没有任何 job 持有该容器）。
  这是"登记开销只在真正访问时付"的代价，**不是缺陷**；需要严格顺序时请先 `Complete()` 再访问，或按 job 依赖串行化。
  （该惰性语义曾使 `ParallelReadWriteContainmentTests` 的"40 次重试赌 worker 先写"在 CI 上出现假失败，测试已改为确定性握手，见 `docs/public/NativeArray-Index-Safety-Overhead-and-Fixes.md` §十。）
- **异常文本**（测试与工具依赖，勿改动）：
  - Job 间写冲突（写入点，经 `Complete()` 以 `AggregateException` 重抛）：`"NativeContainer already being written by another parallel job (ctx={ctx} vs {existing}); schedule it after that job with a dependency, or use separate containers."`
  - 主线程读 × Job 写：`"NativeContainer is being written by an active job; Complete() before accessing it from the main thread."`
  - 主线程读 × Job 读：`"NativeContainer is being read by an active job; Complete() before accessing it from the main thread."`
  - 主线程写 × Job 写：`"NativeContainer is being written by an active job; Complete() before writing from the main thread."`
  - 主线程写 × Job 读：`"NativeContainer is being read by an active job; Complete() before writing from the main thread."`

### 开关与开销

- 由 `ENTJOY_SAFETY` 或 `ENTJOY_SAFETY_BOUNDS` 宏启用（委托原生实现时二者之一生效），因此 **Release 默认开启**，作为防 Use-After-Free 兜底的一部分。注意 Debug 与 Release 走的是**同一段**追踪代码，Release 并不免除该开销。
- 实测每次索引的检查开销（`tools/SafetyLockOverheadBench`，Native 后端，1,048,576 实体 × 20 次、5 轮取中位）：
  - 主线程访问：约 2.7ns/次（`ctx==0`，只做状态/版本/持有者判定，不登记）。
  - **Job 内、同线程反复访问同一容器：约 0.8ns/次**（命中 thread-static 快路径，见下）。
  - Job 内经 `NativeArray` 索引器跨容器访问（如每元素 2 读 2 写）：残余检查约 1.7ns/访问；同一 job 改用 `AsSpan()` 后残余为 0，实测整任务 **2.1~2.2x** 加速。
- 可通过 `SafetyChecksEnabled=false` 或编译期全关安全宏（`-p:DefineConstants=`）彻底关闭，关闭后不再检测冲突。
- 依赖调度之下冲突不会误报：前一 Job 结束即释放其对容器的主持有声明，后继 Job 正常接续。冲突检测只在运行时出现真正交错的访问时才触发，并非调度期确定性检测。

### 登记机制与四条必须保持的顺序/唯一性契约

读写持有登记的状态由 `SafetyHandleManager` 维护，其中有四处是**正确性前提**，改动前请先读此处与代码注释：

1. **写声明按 Job 完成点释放，不得按 tile 释放。** 同一 Job 的所有 tile 共享一个执行上下文（ctx），逐 tile 释放会先把该 ctx 的 `_ctxWrites` 条目移除，而仍在运行的 tile 随后登记时会把 index 写进一份被丢弃的 list，释放循环再也扫不到它 → `_writerCtx[index]` 永久残留该 ctx，`Complete()` 后主线程访问被**永久误拦**。`ReleaseWritesForContext` 的实现是 `TryRemove(ctx)` + 清空该 list（`AtomicSafetyHandle.cs`）；ctx 为每次调度唯一，故条目必须移除，否则字典随 Job 数无界增长。
2. **读者计数登记的校验必须在 `set.Add` 之后。** 若「先校验条目现役、再 Add」，两步之间条目可能被并发释放移除，`+1` 会落在已丢弃的集合上而永不配对 → `_readerCount` 永久为正，同样导致主线程被永久误拦。

3. **「已完成」的发布必须晚于声明释放。** 完成计数（`ManagedCompletion.Remaining`）归零发生在释放之前；若完成判定只看计数，主线程会在「计数已归零、声明未释放」的窗口内 `Complete()` 返回并立即访问容器 → 被误拦。Managed 侧用 `_declReleased` 门把发布推到释放之后（`Signal` 中：释放写/读声明 → 置位 → `_done.Set()`）。
4. **并行冲突检测的 ctx 必须每次调度唯一。** 不得用池化对象的哈希充当 ctx（box 复用 ⇒ 相邻两次 Job 同 ctx ⇒ 幂等写登记与 `_readMark` 快路径跨 job 串味）。Managed 侧由 `ManagedJobScheduler.NextCtx()` 分配，tile 从所属 Job 的 completion 取，槽位复用时 `Reset` 清 `HostCtx`。

前两条先在 Native 后端实测复现（`tools/SafetyLockOverheadBench/repro`，N=262144 / batch=16384，曾于第 14 次尝试触发）、修复后 0 触发的回归项；第 3、4 条属 Managed 回退后端的同一根因族（2026-09-16 修复，见 `NativeArray-Index-Safety-Overhead-and-Fixes.md` §七）。四条都由测试 `MultiTileWriteJob_AfterComplete_MainThreadNotBlocked`、`RepeatedParallelReadJobs_NoReaderCountLeak` 与探针 `tools/SafetyInterceptProbe` 持续守护。

另有两条性能相关的实现约束：`RegisterRead` 的 thread-static 快路径以 `(读声明代际, ctx, 容器 index, 句柄代际)` 为键，命中即返回；句柄代际参与比较是防 ABA 的前提（index 释放后被复用时代际必递增，缓存自动失效）；**读声明代际（`_readMarkEpoch`）参与比较**是防「ctx 复用」的前提 —— native ctx 来自 `ContextPool`（指针可被复用），只靠 `(ctx,index,version)` 命中的旧缓存会让新 Job 跳过登记，使读者计数偏少、主线程拦截失效。

**第 5 条契约（2026-09-26 修）**：**读者声明必须与写声明同在 Job 完成点释放，不得按 tile 释放。** 所有 tile 共享一个 ctx，任一 tile 结束就 `ReleaseReadsForContext(ctx)` 会把整个 ctx 的读者计数清零，而兄弟 tile 仍在执行；主线程随后的访问因此不再被拦截（Native 后端曾按 tile 释放，Managed 后端本就是完成点释放 —— 两条后端行为不一致）。回归用例：`ReadClaimReleaseTests.SiblingTileStillRunning_MainThreadAccess_MustBeIntercepted`（双后端都跑）。

### ⚠ 跨 Job 共享的容器必须用 `GetUnsafePtr()` 访问（**不能用索引器**）

**契约**：当**多个并列的 Job** 需要访问同一个容器（哪怕各自处理的索引区间互不相交、逻辑上完全无冲突），job 体内必须
通过 **`GetUnsafePtr()` 裸指针**读写；用 `NativeArray` 索引器（`arr[i]`）会被写跟踪当成"另一个 Job 正在写该容器"而抛错：

```
System.AggregateException: One or more scheduled C# jobs failed.
  ---> System.InvalidOperationException: NativeContainer already being written by another parallel job
       (ctx=… vs …); schedule it after that job with a dependency, or use separate containers.
```

为什么：写跟踪的粒度是**容器**而不是索引区间，且以"Job 执行上下文（ctx）"为持有者标识 —— 并列的两个 Job ctx 不同，
第二个 Job 写同一容器即判定冲突（见上文「Job 间冲突（写-写）」）。这是**设计使然**（框架无法证明两段区间不相交），
不是 bug；`CreateView` 出来的共享视图句柄（`ExemptWriteTracking`）不受此限，但那是给"外部内存视图"用的。

**实测症状（CPU 百万同屏工程，2026-09-13）**：把 `YSort` 的直方图阶段新增的 `Keys[i] = key`（索引器）从
`histPtr[key]++`（裸指针）风格改写成索引器后，47 个直方图 Job 并列写同一 `Keys` 数组
⇒ 上述异常直接冒泡到主线程 ⇒ **仿真中止**（外部可见现象是"单位不再生成/存活数恒 0、统计窗口稀疏"），
而同一段代码用 `keysPtr[i] = key`（裸指针）则完全正常。该工程的原实现一直用裸指针，所以此前从未暴露。

**并列 Job 共享容器的三种正确写法**：
1. `var p = (int*)arr.GetUnsafePtr();` ⇒ `p[i] = …`（最常用；ECS chunk 组件列也是这么用的）；
2. 让容器走 `NativeArray<T>.CreateView(...)` 的共享句柄（仅适用于外部内存视图）；
3. 拆成"每个 Job 独占一个容器"（各自一份私有 scratch），事后由主线程归约。

**排查建议**：看到 `AggregateException … already being written by another parallel job` 时，先找"哪个容器被两个并列 Job 同时写"，
而不是先怀疑依赖缺失 —— 索引区间不相交也会触发，这是最常见的成因。

## ECS 原生内核（`NativeTranspile`）访问契约

### 逐组件 enable 位图（P1-6 / P1-7）

- 原生侧取位图：`ArchetypeChunk.GetEnableBitMapPtr<T>()`。托管实现直接返回 chunk 内的真实位图指针；
  原生内核被转译为 `reinterpret_cast<unsigned long long*>(__chunkData->requiredEnableBitMaps[requiredIdx])`
  （entity-batch 适配器为 `__batchData->enableBitMaps[requiredIdx]`）。
- **布局**：每实体 1 bit、64 实体/字；位 i 对应 chunk 内第 i 个实体。组件不是 enableable 时返回 `nullptr`（不抛异常）。
- **required 序号对齐**：位图数组与 `requiredComponentArrays` **同序**（`CollectChunkNativeArrayTypes` 会同时收集
  `GetComponentDataNativeArray<T>()` 与 `GetEnableBitMapPtr<T>()` 的类型）⇒ 同一 job 里两者对同一组件取到的序号一致。
  若某类型不在 required 列表却调用了位图 API，**生成期直接抛错**，不会静默取错列。
- **写位图的并发纪律**：位图是 chunk 内存的一部分。按实体逐位 `w = bits[word]; bits[word] = w | bit;` 时，
  多个 lane 命中同一字会互相覆盖（丢更新）。正确做法二选一：**按 64 位字整体写 + 字值是该字索引的纯函数**
  （两个 lane 命中同一字也写入同一个值 ⇒ 幂等），或保证**同一字只被一个 lane 写**。
- 托管侧 `IsComponentEnabled<T>` / `WithEnabled<T>()` 读的是同一份位图 ⇒ 原生写后托管侧立即可见。

### ECB 并行记录（`ParallelWriter`）

- **单线程形态**（`CreateParallelWriter(index)`）：一个 writer 同一时刻只能被一个线程使用，违反抛异常；
  staging 可扩容；只支持自包含命令（`DestroyEntity` / `SetComponent<T>`）。
- **跨 tile 共享形态**（`CreateSharedParallelWriter(index, stagingCapacityBytes, destroyCapacity)`）：
  追加走 `Interlocked.CompareExchange` 原子占位（无需线程独占）；**不扩容**（并发下无法安全搬移整块 staging）
  ⇒ 两个容量必须一次给足。CAS 占位保证两个不变式：`Offset ≤ Capacity`、`DestroyCount ≤ DestroyCapacity`；
  占位失败发生在写字节之前 ⇒ 失败只抛异常，既不越界写、回放也不会越界读。
- **命令顺序**：同一 writer 内跨 lane 的顺序不确定（与 DOTS 的 `EntityCommandBuffer.ParallelWriter` 同语义）；
  回放按 staging 顺序，并把**连续销毁槽**合并回一次批量销毁（遇非销毁命令先落地，保证命令序不被重排）。
- 记录期不得做结构变更；回放由主线程调用 `PlaybackParallel`。

### `DeferredCommandBuffer` 的值回放路径

- `SetComponentRange` 与 `AddComponentRaw(Entity entity, int typeId, byte* value, int elemSize)` 都按**原始字节**落列
  （无反射、无装箱、无 `Type` 解析）。字节数必须与组件实际大小一致（记录侧取 `Unsafe.SizeOf<T>()`），
  否则会按错误的 stride 写列。
- 未知 `typeId` 会在 `ComponentTypeManager.GetTypeByComponentType` 处抛 `KeyNotFoundException`（响亮失败）。

### `Interlocked.CompareExchange` 的参数序（2026-09-13 修）

- **契约**：C++ 后端生成的 `Interlocked.CompareExchange(ref loc, value, comparand)` 与 C# 语义一致
  （命中 `comparand` 时写入 `value`，返回旧值）。宏形参名是 `(ptr, oldVal, newVal)`
  ⇒ 生成器必须**先 comparand（期望旧值）再 value（新值）**。
- **曾经的 bug**：`Ast/StatementTranslator.cs` 按 C# 参数顺序直传，等价于写出
  `_InterlockedCompareExchange(ptr, comparand, value)` —— 命中时**写入 comparand**、把期望值当新值，
  语义完全相反。ISPC 后端一直是正确次序（`IspcStatementTranslator` 有显式换序与注释），只有 C++ 后端错。
- **症状**：转译内核里用 CAS 做「比较并更新」的代码静默失效。实测症状是
  `while (cur > mx) { var seen = Interlocked.CompareExchange(ref mx, cur, mx); ... }` 的自旋最大值计数器恒为 0
  （值写不进去，且返回值恰好等于比较值 ⇒ 循环立刻 break，不报错、不崩溃）。
- **修复判据**：生成产物里应为 `INTERLOCKED_COMPARE_EXCHANGE32(&x, <comparand>, <value>)`；
  运行期用「CAS 自旋最大值」这类探针应能看到非零结果（修复前恒 0）。

## SharedBlob 和调试 pin

- `SharedBlob<T>` 是带显式引用计数的值类型。复制其值不会自动增加引用计数；需要共享副本时必须调用 `Clone()`，每个成功的 `Clone()` 对应一次 `Dispose()`。
- `MemoryAddress.GetAddress`/`GetArrayAddress` 仅用于调试，会固定对象。调试流程结束必须调用 `MemoryAddress.ReleaseAll()`；不得将返回地址用于对象生命周期之外。

## 已知设计限制（不是实现性 bug）

以下行为需要调用方遵守契约，框架不会把它们转换为可恢复错误：

- 依赖图中的环、在 Job 内同步等待自身相关句柄会造成逻辑死锁；调度器不尝试推断或破坏依赖关系。
- ECS 结构性 API 仍是主线程模型；并发调用属于未定义的应用层行为，即使底层容器本身具备部分线程安全能力。
- `SharedBlob<T>` 的值复制不增加引用计数，跨系统共享必须使用 `Clone()`。
- 调试 pin 地址只在对象保持存活且未释放 pin 时有效；不得缓存到业务生命周期。
- Job 之间的冲突检测只覆盖「写-写」：不同 Job 对同一容器的并发读（读读共享）或读-写组合不作为 Job 间冲突检测目标，并行读写同一容器时的确定性需由上层保证。主线程与 Job 的任意读/写组合则由上述「主线程访问拦截」覆盖。
- **Temp 容器在帧末回收后被手动 `Dispose()` 的残留风险**（2026-09-26 记录，独立验收指出尚未覆盖）：`TempAllocator.Reset()` 会对未手动释放的 Temp 容器 `MarkReleased(index)` 并把索引归还空闲队列；而 `MarkReleased` **保留** 原 index（以便旧句柄仍报"已释放"），所以此后若该 index 被新容器复用，调用方再对**陈旧容器**调用 `Dispose()`，`Release` 会看到 `StateActive` 而再次把 index 入队（重复项），并且按地址释放 payload 时可能释放已被重分配给他人的块。**规避**：`Allocator.Temp` / `TempJob` 的容器按设计由帧末统一回收——**不要**手动 `Dispose()`；确需提前释放请在同一帧内、`Reset()` 之前完成。
- **2026-09-27 审计收尾修复（均有可复现 RED → 回归）**：
  - `EntityQuery.RefreshIncremental` 原来只比 **Archetype 数量**判断能否复用匹配集合；`World.Restore()` 会整体重建且数量常相同 ⇒ 查询继续引用**已释放** Archetype、**静默返回 0 个实体**（实测 `Expected: 3, Actual: 0`）。现在比 `EntityManager.ArchetypeSetVersion`（新建/清空/Restore 递增）。回归：`QueryRestoreAndMemoryReportTests`。
  - `MemoryReport.TotalEntityCount` 原来填的是 `entityCount`（**已发放 id 计数**，Destroy 不递减）⇒ 销毁后仍显示历史峰值（实测 `Expected: 3, Actual: 10`）。现在填**存活数**（各 Archetype 计数求和）。`EntityManager.EntityCount` 保留原语义并在 XML 文档中写明"不是存活数"（兼容按 id 遍历的既有用法）。
  - `ScheduleGraph` 的执行顺序原来依赖**注册顺序**（对称读写冲突的自动边按 i<j 定方向；层内 `List.Sort` 不稳定）⇒ 同一组系统换个注册顺序就产生不同的提交顺序。现在双向冲突按类型全名定方向、层内按 `Order` + 类型全名排序。回归：`ScheduleDeterminismTests`（两种注册顺序拍平后必须逐位相同）。
  - `SystemRunner.ComputeIncomingDependency` 对**单个**依赖不再构造组合句柄（`JobHandle` 无 `Dispose`，组合句柄只能等终结器）⇒ 消除每帧每冲突系统的无谓句柄抖动。
  - `TempAllocator.Reset()` 的锁序：旧实现**先取 `_resetLock` 再等待活跃 job**，而跨线程 `TempAllocator.Free` 要拿同一把锁 ⇒ 「Reset 等 job、job 等锁」永久死锁。现在先完成任务再取锁。回归：`TempAllocatorLockOrderTests`（受控 hook 精确复现交错，旧锁序下有界等待超时失败）。
  - `SparseTileDeque` 容量校验：`RoundUpPow2(0xFFFFFFFF)` 溢出成 0 ⇒ `capacity_=0`、`mask_=2^32-1` ⇒ `new Slot[0]` 后用 mask 索引**越界读写**；且 ctor 标 `noexcept` 时 `new[]` 失败直接 `std::terminate`。现在显式拒绝过大/溢出容量。回归：`SparseTileDequeTests` 的 `PASS SparseTileDequeCapacityValidation`。
  - `ChaseLevScheduler::ApplyAffinity` 的 `KAFFINITY(1) << (1+i)` 在 `1+i ≥ 64` 时是 UB 且掩码为 0（静默不绑核）：现在跳过超范围核心。**本机 worker 数 ≤ 8，无法构造该分支的运行时用例**（属防御性修复，未取得 RED）。
  - **ImGui 调试面板的停止路径**（2026-09-27）：面板线程在 `Launch()` 里 `std::thread::detach()`，而 `JobDebuggerGUI::Shutdown()` 是空实现、`Scheduler::Shutdown()` 也不调用它 ⇒ 面板线程**比 JobSystem 活得更久**并继续读状态，且一次性 `g_guiLaunched` 永不复位（关停后再也无法打开面板）。现在：GUI 主循环每帧检查 `g_guiStopRequested`；`Shutdown()` 置位后**有界等待**（≤3s）`g_guiRunning==false`；退出路径复位 latch；`Scheduler::Shutdown()` 在拆 worker/状态**之前**调用它。**注意**：原先怀疑的"关停期 UAF"**不成立**——读取器都不 deref 调度器（`CurrentWorkerCount()` 读全局原子、`GetWorkerSnapshots` 读全局数组、名字表受锁保护），本次修的是生命周期与可重启性。无 ImGui 构建（CMake/CI）走空实现分支；ImGui 分支已用 `cl /DENTJOY_IMGUI_ENABLED=1` 单独编译验证（exit 0），**运行时未验证**（无头环境无法创建 D3D11 窗口）。
  - **帧末回收后陈旧 Temp 容器的 `Dispose`**（B18 残留，2026-09-27 修复）：`TempAllocator.Reset()` 会对帧末未手动释放的 Temp 容器 `MarkReleased` 并把内存归还池子；此后若调用方仍对那个**陈旧容器**调用 `Dispose()`：① index 已被新容器复用 → 旧实现会把**新容器的 index 再次入队**（同一 index 可能被发给两个容器，安全跟踪失效）；② index 未复用 → 仍按地址 `UnsafeUtility.Free`（那块内存可能已重分配给别人 ⇒ 释放别人的块）。现在释放路径要求"句柄仍活着"（`SafetyHandleManager.IsLive` = 状态 `Active` **且** version 与句柄一致），陈旧 Temp/TempJob 容器的 `Dispose` 降级为**幂等空操作**，活着的容器仍正常释放。回归：`TempStaleDisposeTests`（索引复用 RED 锚点 + "活容器必须真释放"反向守卫）。**规避仍成立**：Temp/TempJob 容器按契约由帧末统一回收，不要手动 `Dispose()`。
  - **codegen marker 缺口核对**（2026-09-27）：`__ENTJOY_UNSUPPORTED` 标记由 7 个核心翻译器的 `default` 分支与 SIMD/ISPC/嵌套 `return` 路径发射；新增 `NT15_UnsupportedMarkerCoverageTests` 表驱动探针（`unchecked` 块、`goto`/label、`throw`、`try/catch`、`lock`、`using` 语句、`switch` 语句、`checked` 块、`stackalloc`、lambda/委托、非数组 `foreach`），断言每种构造**必须"有 marker 或 有生成器诊断"**，11/11 通过（`GeneratorDriverRunResult.Diagnostics` 只含生成器诊断、不含 C# 编译错误，故不会假绿）。这不是穷举（构造清单会随后续发现扩充），但把"新增分支漏写标记"变成了红灯。**同日补齐 ISPC 侧最后一处漏标记**：`IspcStatementTranslator` 的 `SendEvent(非对象创建实参)`（即 `SendEvent(已有变量)` 而非 `new T { ... }`）原本只写一行普通注释 ⇒ ISPC 后端**静默丢掉该次事件写入**且构建照过；现改为 `{UnsupportedMarkers.Stmt}ISPC_SendEventNonObjectCreationArg`，构建期扫描会让它失败。全仓所有 `SendEvent(...)` 调用点都是 `new T {…}` 形态（无此形状）⇒ **零爆炸半径**，不会打断任何 shipped 样例。ISPC 侧其余静默面已逐条核过（见 `NativeTranspiler-Boundaries-and-Diagnostics.md`）。
- **2026-09-27 复核后驳回的两条审计疑点（有证据，不修）**：① **defer-wake"漏唤醒"**——`ENTJOY_DEFER_WAKE=1` 时 `SubmitBatch` 只置 `g_pendingDeferredWake`，但 `JobHandle::Complete()`（`JobSystem_State.cpp`）与批量提交导出（`Exports.cpp` 末尾 + 异常路径）都会 `FlushDeferredWake()`/`WakePending()`，且 `DeferWakeEnabled()` 把 env 缓存进 `static const bool` ⇒ 框架内调用点不存在漏唤醒窗口。② **JobProfiler seqlock**——写侧 `seq` 奇/偶 + release 配对、读侧 `s1` 奇偶判断 + 二次读比对，逻辑正确；`ReadAll` 在并发 Push 下的少量丢失/重复已在源码注释中显式声明为诊断设施的可接受行为。
- **系统间"读→写"已串行（2026-09-27 修复，口径 3：默认安全 + 可回退）**：原先只对 `slot.WriteComponents` 调 `SetLastWriter`，`[Read(X)]` 系统结束后不给后续 `[Write(X)]` 留依赖 ⇒ 前一系统的 Job 仍在飞时，后一系统可与它并发访问 X（撕裂/陈旧读）——这是当时唯一**静默**的竞态。现在 per-type 表拆两份：
  - `lastWrite[X]`：写系统写回；读系统与写系统入站合并（原行为）。
  - `lastRead[X]`：读系统写回（多读系统时 **merge**，不是覆盖——写系统要等的是全部读 Job）；**只有写系统**入站合并；写系统完成后清空（这次写已经等过所有读）。
  - 读系统**不**合并 `lastRead` ⇒ **读读仍然并行**（反向守卫用例断言两个读系统的 Job 时间窗必须重叠）。
  - 回退开关：`ENTJOY_SYSTEM_READ_WRITE_ORDER=0`（或 `SystemRunner.ReadWriteOrderingEnabled = false`）恢复旧行为。
  - 代价实测（交错 A/B，8 对；本机 4 worker）：**读多写少**图（3 组件 × 3 读 + 1 写，读系统排在写系统之前 = 真正会命中的形状）ON 中位 1.643 ms / p95 1.807 ms，OFF 中位 1.644 ms / p95 1.773 ms，成对中位比值 OFF/ON = 0.9996 ~ 1.00；把单系统 Job 加重到 ~1.9 ms（8192 实体 × 512 轮，22.5 ms/帧）后成对中位比值仍是 0.9996。**结论：本机/workload 上测不出代价** —— 帧末尾的全量等待（`TempAllocator.Reset → CompleteActiveJobs`）与"每帧系统数 × 每系统 Job 时长"决定了关键路径，读→写串行边落在这条路径之外。⚠ 未测场景：并行度**未饱和**（读 Job 比 worker 少）且读 Job 很长时，写 Job 原本能挤上空闲 worker，此时串行化代价会显现（理论上限 ≈ 写 Job 时长占帧时间的比例）。
  回归：`SystemReadWriteOrderTests`（`WriterSystem_WaitsForReaderSystemJob` 红→绿主用例 + 读读并行反向守卫 + 开关回退 + 读表生命周期）。
- **job 路径的 `WithRelationship` 不做逐槽位匹配**：关系过滤在 chunk 收集阶段只能看到「该 archetype 是否含该关系列」，无法按 `RelationSlot` 的 target/version 逐实体筛选 ⇒ 以 `job.Run/Schedule(query)` 运行的关系过滤查询会处理同 archetype 内**所有**实体。需要逐槽位精确匹配时请用托管查询/`QuerySelection` 路径（它们会做槽位校验）。
- **`RefreshIncremental` 的「archetype 数量相等」捷径在 `World.Restore` 后可能陈旧**：`EntityQuery` 用 `archetypeCount == 上次扫描数` 判定可复用匹配集合，而 `ClearWorldInternal` 会把 `archetypeCount` 归零 ⇒ Restore 后若数量恰好与缓存时相同，缓存的查询可能仍引用已 Dispose 的 Archetype，从而静默返回 0 个实体。**规避**：Restore 后重新构造查询（或 `GetOrCreateEntityQuery` 取新实例）。
- **`JobHandle.CombineDependencies` 组合句柄已确定性回收（2026-09-27 修复）**：三条回收路径——① 单个依赖不再组合（R11）；② `SystemRunner` 自己组合出的入站句柄"用后即释"（未 adopted 进写表时）；③ `EntityManager` 在"剪枝已完成 Job""覆盖依赖表旧值""全量等待后清空依赖表"三处，对**已完成**句柄显式 `Release`。未完成的句柄**绝不**提前释放（所有 `JobHandle` 拷贝共享同一个 box，提前 detach 会让 `_activeJobs` 记账项与用户手里那份拷贝的 `Complete` 双双退化成空操作 ⇒ 等待/结构变更屏障静默消失）。原生侧新增 `JobSystem_GetLiveHandleCount`（`CreateState - RecycleState`）作为断言口径：1000 帧 + 无 GC 区域下 `EntJoy.ECS.Tests` 的 `TwoDependencyGraph_NativeHandlesDoNotGrowAcrossFrames` 实测增长 **0**（关掉上述任一路径分别增长 2997 / 2996）。
- **`ScheduleGraph` 的对称读写冲突，边方向取决于注册顺序**：当两个系统互相冲突（A 写 X 读 Y、B 写 Y 读 X）时，单一冲突边的方向按注册先后决定，因此提交顺序随注册顺序变化；层内顺序用 `Order` 排序但排序不稳定（同 Order 时）。需要确定顺序时请显式使用 `[OrderBefore]/[OrderAfter]`。
- **Managed 回退后端的完成协议（2026-09-16 已修复，此处保留记录）**：此前的缺陷是「完成态发布」与「安全声明释放」不在同一同步点 —— `IsCompleted` 只看 `Remaining == 0`，而读写声明在计数归零之后才释放，主线程可能在声明释放前观察到「已完成」→ `Complete()` 后的合法访问被误拦；另一个成因是托管路径的 ctx 取自 `RuntimeHelpers.GetHashCode(box)`，而 box 被 `ParallelCache<T>` 池化复用，相邻两次 Job 可能拿到同一 ctx。现实现为：`ManagedCompletion.Signal()` 按「释放写声明 → 释放读声明 → 置 `_declReleased` → `_done.Set()` → 派发回调」的顺序发布，`IsCompleted` 要求 `Remaining == 0 && _declReleased == 1`（`EntJoy.Jobs/Managed/ManagedJobHandle.cs`），且每次调度由 `ManagedJobScheduler.NextCtx()` 分配唯一 ctx。回归证据：`tools/SafetyLockOverheadBench/repro`（隐藏 `NativeDll.dll` 强制 Managed）**触发=0、残留状态为空、正常退出**；守卫用例见 `MultiTileWriteJob_AfterComplete_MainThreadNotBlocked`、`RepeatedParallelReadJobs_NoReaderCountLeak`。
- **两条后端都必须被测试覆盖，且不得隐式切换**：`EntJoy.ECS.Tests` 的运行后端取决于进程能否找到 `NativeDll.dll`（`AppContext.BaseDirectory` → 入口目录 → 程序集目录 → CWD 上溯）。历史上因此出现过「同一套测试本地跑 Native、CI 跑 Managed」的静默差异。现在测试用环境变量 `ENTJOY_TEST_BACKEND=native|managed` 显式指定，并由 `BackendSelectionTests` 断言实际后端与请求一致；CI 对两条后端分别跑一遍。
- IJobEntity 的 DOTS 式 `Entity` 参数（`Execute(ref T0 c0, Entity e)`）的 `Execute` 方法体必须是**块体 `{ }`**，不支持表达式体 `=>`（ISPC 生成器只识别块体）。`e.Id` 即全局实体序号（对齐镜像 SoA 槽位），三后端（C# / C++ / ISPC）均支持，且仅当 Execute 声明了 `Entity` 参数时才传递实体数组（无该参数零开销）。
- **并行创建未提供**：`ParallelWriter` 只支持自包含命令（销毁 + 写单个组件）。并行 `CreateEntitiesRange`
  （DOTS 的 placeholder 机制）未实现；创建/结构变更仍限主线程。
- **ISPC 后端未接逐组件 enable 位图 API**：`ArchetypeChunk.GetEnableBitMapPtr<T>()` 目前只有 C++ 后端转译
  （ISPC job 使用它会得到 ISPC/C++ 编译错误，不会静默错值）。
- **`IJobChunk` + `AutoSIMD` 的收益未证明**：双组件整结构体回写的正确性已逐实体验证（不一致=0），
  但没有证据表明该路径比标量 C++ 更快（历史结论是 `AutoSIMD` 在 `IJobParallelFor` 上慢 ~10%）。

## 通过 NuGet 包消费时的运行时契约（2026-09-17）

包的组成（`EntJoy.ECS` / `EntJoy.Jobs` / `EntJoy.Collections` / `EntJoy.Mathematics`，lockstep 版本，**仅 win-x64**）：

- `EntJoy.Jobs` 携带运行时 `runtimes/win-x64/native/NativeDll.dll`、原生链接套件（`build/native/`：`NativeDll.lib` + 头 + `tasksys.cpp`）、
  `NativeTranspiler` 分析器与 MSBuild 任务（`tools/`）、以及 `buildTransitive/` 接线。`EntJoy.ECS` 依赖它，因此**只写一条 `PackageReference EntJoy.ECS` 即可**。
- 包模式下 `NativeDll.dll` 由 props 复制到 `$(OutDir)` / `$(PublishDir)`；消费者**不重编 NativeDll**（也不编 imgui）。
  用 `[NativeTranspile]` 时，本地只编出 `NativeTranspiled.dll` 并链接包内 `NativeDll.lib`。
- **发布渠道与还原凭据**：包发布到 **nuget.org**（可匿名还原）与 **GitHub Packages**（`nuget.pkg.github.com/tianqiyuan520`）。
  后者对**公开包也不支持匿名还原**（实测 `401 (Unauthorized)` / `NU1301`），消费方必须配 classic PAT（`read:packages`，
  fine-grained 不支持），并建议加 `packageSourceMapping`——否则该源的鉴权偶发失败会连累整个 restore（连它上面根本没有的
  公共包也失败）。发布由 `v*` tag 触发，见 [NativeTranspiler：边界、诊断与回归防线](NativeTranspiler-Boundaries-and-Diagnostics.md) §7。

必须遵守的契约：

- **两个原生 DLL 必须同目录**：`NativeTranspiled.dll` 与 `NativeDll.dll` 由运行时从同一目录解析
  （查找顺序见加载器实现）。缺任一者：前者静默降级为"仅托管路径"，后者直接 `NativeDll.dll is not loaded`。
- **头文件/布局必须与预编译 DLL 同版本**：包内头与 `NativeDll.lib`、`NativeDll.dll` 来自同一次打包 ⇒ 消费者**不得**
  混用不同版本的包（例如把 `EntJoy.Jobs` 1.0.0 的头与该包 1.0.1 的 DLL 拼在一起）；升级请整体升级四个包（lockstep）。
- **ABI 校验覆盖到「版本号 + 统计结构布局」，未覆盖「任意 ABI 结构的布局哈希」**：`LoadNativeDll` 会要求 `JobSystem_GetAbiVersion` 导出存在且等于 `ExpectedAbiVersion`，不满足则 `NativeLibrary.Free` + 回退 Managed（`EntJoy.Jobs/Native/NativeJobCore.cs`，回归用例 `tools/JobSystemBugTests/Stage11_AbiFixture` 的 `stub_no_abi` / `stub_wrong_version`）；初始化后还会由 `ValidateStatsLayout()` 校验统计结构的偏移/大小。残留缺口是：**改动任何跨 ABI 的 job 结构/适配器布局时必须手动递增 `ExpectedAbiVersion`**，没有基于头文件/布局哈希的自动门禁 ⇒ 忘记递增时错配仍是静默错值。
- **写 native job 需要本机 C++ 工具链**：CMake + MSVC（或 VS 自带 ClangCL；缺 ClangCL 自动回退 MSVC）；
  `Target = Ispc` 还要求 `ispc` 在 `PATH`。**纯 C#（不写 `[NativeTranspile]`）不需要任何工具链**。
- **独立消费 `EntJoy.Jobs` 时**：只有数组类 job（`IJob`/`IJobFor`/`IJobParallelFor`/`IJobParallelForBatch`）可转译；
  `IJobChunk`/`IJobEntity`/`SendEvent` 的类型定义在 `EntJoy.ECS` 内，无该引用时无法表达。
  生成器会在"生成物与是否引用 ECS"不一致时报 **NT029/NT030**（见边界文档 §4）。

CI 全绿证明已覆盖路径通过；发布前仍应在目标平台运行 sanitizer、压力和长稳测试。
