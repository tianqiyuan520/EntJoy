# EntJoy 运行时契约与已知限制

本文定义 EntJoy v1.0 的线程、生命周期、依赖和所有权边界。未满足契约的行为不属于框架保证的支持范围；调用方应在自己的封装层保证这些前置条件。

## 调度器生命周期

- 在提交任何 Job 之前调用 `NativeJobScheduler.Initialize()`；所有 World、JobHandle 和相关资源释放后，再调用 `NativeJobScheduler.Shutdown()`。
- `ManagedJobScheduler` 具有相同的初始化/关闭顺序要求。
- `Initialize`、`Shutdown`、调度、`Complete` 和资源销毁不得并发交错。关闭期间不得提交新 Job，也不得在另一个线程继续调用调度器 API。
- `Shutdown` 必须由初始化线程（通常是主线程）调用。worker 线程或其他线程调用关闭会被拒绝。
- 调度器关闭后不得继续使用旧句柄、旧 World 或旧的原生视图；重新初始化后，旧句柄仍然视为失效句柄。

## Job 依赖和 Complete

- 依赖图必须是无环 DAG。循环依赖会使依赖回调永远无法满足。
- 不得在 Job 执行体内同步 `Complete` 自己、自己的祖先、自己的后继，或包含当前 Job 的组合句柄。
- Job 抛出的异常只在对应句柄 `Complete` 时传播；调用方必须完成需要观察异常的句柄。
- Job 执行期间不得直接进行 ECS 结构变更。请使用 `DeferredCommandBuffer`，并在主线程 Playback。

## ECS 线程模型

- `World`、`EntityManager`、`Archetype` 的结构性 API（创建/销毁实体、增删组件、Playback、换帧、Dispose）是主线程 API。
- 主线程调用结构性 API 前，框架会等待该 World 的活动 Job；调用方不得绕过该同步路径直接修改 Chunk 或组件存储。
- `World.Dispose()`、`EntityManager.Dispose()`、`Archetype.Dispose()` 不得与查询、调度、事件 drain 或其他销毁操作并发执行。
- `EntityManager.GetAllArchetypes()` 返回当前 Archetype 的快照数组；后续结构变更不会更新该快照。

## EventStream

- `SendEvent`、`NextFrame`、`ReadBuffer` 和 `Dispose` 内部已串行化，可安全地并发调用；但应用层仍应在换帧后再消费上一帧数据。
- 调用 `NextFrame` 前必须确保本帧事件生产已结束；读取只允许发生在换帧完成后。
- `EventStream.Dispose()` 后不得再发送、读取或 drain 事件。

## 原生内存和容器

- `NativeArray`、`NativeList`、`UnsafeList` 和 `DeferredCommandBuffer` 的长度、索引、容量和字节数必须是非负且不发生整数溢出；复制区间必须完全落在源/目标范围内。
- 拥有内存的容器必须由其拥有者准确调用一次 `Dispose()`；视图不会延长底层内存生命周期。
- `PersistentAllocator.Free` 只应接收 `PersistentAllocator.Alloc` 返回的 payload 指针。其他 allocator、CRT 或第三方 DLL 的指针必须使用其对应的释放函数。
- `NativeArray.FromExternalPtr` 创建的视图不拥有外部内存；调用方必须保证该指针在所有读写和 Job 完成前保持有效。

## SharedBlob 和调试 pin

- `SharedBlob<T>` 是带显式引用计数的值类型。复制其值不会自动增加引用计数；需要共享副本时必须调用 `Clone()`，每个成功的 `Clone()` 对应一次 `Dispose()`。
- `MemoryAddress.GetAddress`/`GetArrayAddress` 仅用于调试，会固定对象。调试流程结束必须调用 `MemoryAddress.ReleaseAll()`；不得将返回地址用于对象生命周期之外。

## 已知设计限制（不是实现性 bug）

以下行为需要调用方遵守契约，框架不会把它们转换为可恢复错误：

- 依赖图中的环、在 Job 内同步等待自身相关句柄会造成逻辑死锁；调度器不尝试推断或破坏依赖关系。
- ECS 结构性 API 仍是主线程模型；并发调用属于未定义的应用层行为，即使底层容器本身具备部分线程安全能力。
- `SharedBlob<T>` 的值复制不增加引用计数，跨系统共享必须使用 `Clone()`。
- 调试 pin 地址只在对象保持存活且未释放 pin 时有效；不得缓存到业务生命周期。

CI 全绿证明已覆盖路径通过；发布前仍应在目标平台运行 sanitizer、压力和长稳测试。
