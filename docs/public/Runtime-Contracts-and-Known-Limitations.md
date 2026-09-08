# EntJoy 运行时契约与已知限制

本文定义 EntJoy v1.0 的线程、生命周期、依赖和所有权边界。未满足契约的行为不属于框架保证的支持范围；调用方应在自己的封装层保证这些前置条件。

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

- 由 `ENTJOY_SAFETY` 或 `ENTJOY_SAFETY_BOUNDS` 宏启用（委托原生实现时二者之一生效），因此 **Release 默认开启**，作为防 Use-After-Free 兜底的一部分。
- 单次容器写访问的开销约为 1–2ns（热路径的追踪登记）；读访问额外承担一次原子计数登记与查询。可通过 `SafetyChecksEnabled=false` 或编译期全关安全宏（`-p:DefineConstants=`）彻底关闭，关闭后不再检测冲突。
- 依赖调度之下冲突不会误报：前一 Job 结束即释放其对容器的主持有声明，后继 Job 正常接续。冲突检测只在运行时出现真正交错的访问时才触发，并非调度期确定性检测。

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
- IJobEntity 的 DOTS 式 `Entity` 参数（`Execute(ref T0 c0, Entity e)`）的 `Execute` 方法体必须是**块体 `{ }`**，不支持表达式体 `=>`（ISPC 生成器只识别块体）。`e.Id` 即全局实体序号（对齐镜像 SoA 槽位），三后端（C# / C++ / ISPC）均支持，且仅当 Execute 声明了 `Entity` 参数时才传递实体数组（无该参数零开销）。

CI 全绿证明已覆盖路径通过；发布前仍应在目标平台运行 sanitizer、压力和长稳测试。
