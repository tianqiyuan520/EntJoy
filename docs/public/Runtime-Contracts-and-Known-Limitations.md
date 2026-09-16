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
- `SystemRunner` 按系统的 `[Read]`/`[Write]` 声明合并冲突依赖：读等写、写等写；读读不互相等待。依赖按组件类型传播（per-component 最后写入者），无冲突系统不串行。
- `ISystemWithState.OnUpdate(ref SystemState)` 可显式读写 `state.Dependency`、调用 `state.CompleteDependency()` 同步等待本系统所有 Job。
- `World.DefaultWorld` 为 `[ThreadStatic]`（每线程独立）；Job worker 线程由调度器绑定所属 World，`EventBus.SendEvent` 写入正确 World。

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

### 登记机制与两条必须保持的顺序契约

读写持有登记的状态由 `SafetyHandleManager` 维护，其中有两处顺序是**正确性前提**，改动前请先读此处与代码注释：

1. **写声明按 Job 完成点释放，不得按 tile 释放。** 同一 Job 的所有 tile 共享一个执行上下文（ctx），逐 tile 释放会先把该 ctx 的 `_ctxWrites` 条目移除，而仍在运行的 tile 随后登记时会把 index 写进一份被丢弃的 list，释放循环再也扫不到它 → `_writerCtx[index]` 永久残留该 ctx，`Complete()` 后主线程访问被**永久误拦**。同理 `ReleaseWritesForContext` 只清空条目、不删除条目。
2. **读者计数登记的校验必须在 `set.Add` 之后。** 若「先校验条目现役、再 Add」，两步之间条目可能被并发释放移除，`+1` 会落在已丢弃的集合上而永不配对 → `_readerCount` 永久为正，同样导致主线程被永久误拦。

两条都是先在 Native 后端实测复现（`tools/SafetyLockOverheadBench/repro`，N=262144 / batch=16384，曾于第 14 次尝试触发）、修复后 0 触发的回归项，并由测试 `MultiTileWriteJob_AfterComplete_MainThreadNotBlocked`、`RepeatedParallelReadJobs_NoReaderCountLeak` 持续守护。

另有两条性能相关的实现约束：`RegisterRead` 的 thread-static 快路径以 `(ctx, 容器 index, 句柄代际)` 为键，命中即返回；句柄代际参与比较是防 ABA 的前提（index 释放后被复用时代际必递增，缓存自动失效）。

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
- **Managed 回退后端的完成协议与安全声明释放尚未形成同一同步点（已知缺陷）**：`ManagedJobHandle.IsCompleted` 直接等于 `ManagedCompletion.Remaining == 0`，而释放读/写声明发生在该计数归零之后，主线程可能在声明释放前就观察到「已完成」→ `Complete()` 后的合法访问被误拦。另一个已定位因素是托管路径的执行上下文取自 `RuntimeHelpers.GetHashCode(box)`，而 box 由 `ParallelCache<T>` 池化复用，相邻两次 Job 可能拿到同一 ctx，使上一次 Job 的按-ctx 释放清掉下一次 Job 的登记。此外，隐藏 `NativeDll.dll` 后在托管路径上运行 `tools/SafetyLockOverheadBench/repro` 会稳定**卡死在 `Complete()`**（提交态代码即复现）。**该缺陷不影响 Native 后端**（`JobScheduler` 默认走 Native，本仓库测试集即运行在 Native 上：`IsNative=True`）；若需要在 NativeDll 加载失败的回退路径上也保证同一强度，需按「完成阶段状态机」重设计 `ManagedCompletion`（先放行声明、再发布完成态）并给每次 Job 调用分配唯一 ctx，两者需同批实施。详见 `NativeArray-Index-Safety-Overhead-and-Fixes.md`。
- IJobEntity 的 DOTS 式 `Entity` 参数（`Execute(ref T0 c0, Entity e)`）的 `Execute` 方法体必须是**块体 `{ }`**，不支持表达式体 `=>`（ISPC 生成器只识别块体）。`e.Id` 即全局实体序号（对齐镜像 SoA 槽位），三后端（C# / C++ / ISPC）均支持，且仅当 Execute 声明了 `Entity` 参数时才传递实体数组（无该参数零开销）。
- **并行创建未提供**：`ParallelWriter` 只支持自包含命令（销毁 + 写单个组件）。并行 `CreateEntitiesRange`
  （DOTS 的 placeholder 机制）未实现；创建/结构变更仍限主线程。
- **ISPC 后端未接逐组件 enable 位图 API**：`ArchetypeChunk.GetEnableBitMapPtr<T>()` 目前只有 C++ 后端转译
  （ISPC job 使用它会得到 ISPC/C++ 编译错误，不会静默错值）。
- **`IJobChunk` + `AutoSIMD` 的收益未证明**：双组件整结构体回写的正确性已逐实体验证（不一致=0），
  但没有证据表明该路径比标量 C++ 更快（历史结论是 `AutoSIMD` 在 `IJobParallelFor` 上慢 ~10%）。

CI 全绿证明已覆盖路径通过；发布前仍应在目标平台运行 sanitizer、压力和长稳测试。
