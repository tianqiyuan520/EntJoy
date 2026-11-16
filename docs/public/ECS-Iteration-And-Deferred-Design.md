# 遍历与延迟结构变更设计

## 遍历 API 分层

三层入口，按开销从低到高：

| 层 | 入口 | 10 万实体 2 组件 |
| --- | --- | --- |
| 按 Chunk | `world.Query<A,B>().Chunks()` | 与直接遍历 `ChunkSpan` 相同 |
| 块循环回调 | `world.Query<A,B>().ForEach(ref action)` | 约 48–55 µs |
| 实体级 | `foreach (var r in world.Query<A,B>())` | 约 123 µs |

`Chunks()` 逐块给出 `QueryChunk`，`Span<T>()` 直接返回该块的组件列，封装开销可忽略。`ForEach` 接受 `struct` 加 `IQueryForeach<...>` 实现，循环体在 chunk 内按指针推进。

**实体级 `foreach` 与 `ForEach` 的差距是契约成本，不是实现缺陷。** `Comp{i}` 属性每次访问都调用 `ComponentTypeManager.CheckWritableAccess<T>()`，内部一次 `Volatile.Read`，用于拦截跨线程并发写——`QueryResult_CrossThreadCopyScope_PreservesCallingThreadWriteGuard` 要求这个检查逐访问生效，移到块边界会破坏语义。10 万实体两次访问即 20 万次内存屏障，约 60 µs。`ForEach` 走指针不经过属性，因此没有这笔开销。

## 测量陷阱

`tools/EcsComparison` 的 `--traversal-matrix` 用同一个 `Action` 轮着调十几种写法，JIT 对不同委托目标的内联策略不一致，**同一份 `ChunkSpan` 代码在该框架内报 26.65 µs、在独立进程报 53–56 µs**。它历史上产出过「回调有 26 µs 开销」「`Span` 边界检查阻止向量化」「`NoInlining` 有影响」等结论，经逐行相同的对照臂验证，全部是假象。

判断遍历性能时，必须**一次进程只测一种写法**。跨写法对比不能在同一进程内完成。

## 延迟结构变更

`world.BeginDeferred()` 开启作用域，作用域内的结构变更进命令队列，离开作用域时统一回放：

```csharp
using (world.BeginDeferred())
{
    foreach (var r in world.Query<Position, Velocity>())
        em.DestroyEntity(...);
}
```

嵌套作用域只有最外层回放（`EntityManager.ExitDeferred`）。

覆盖范围：

| 操作 | 作用域内行为 |
| --- | --- |
| `DestroyEntity` | 进队列 |
| `AddComponent<T>` | 进队列 |
| `RemoveComponent<T>` | 进队列 |
| `NewEntity` | 抛 `InvalidOperationException` 并指向替代路径 |

`NewEntity` 不支持的原因：延迟创建需要占位实体 ID 与回放期的 ID 重映射（Flecs 的 deferred entity ID），`DeferredCommandBuffer` 目前的命令格式直接写入真实 `Entity` 值，没有重映射层。

## 为什么延迟必须显式

曾尝试隐式方案：`QueryEnumerator` 实现 `Dispose`，在 `foreach` 结束时把遍历深度归零并回放队列。该方案在 `world.Query<A,B>().GetEnumerator().MoveNext()` 这类不带 `foreach` 的写法上泄漏——ref struct 的 `Dispose` 只有 `foreach` 会调用，深度永久停在 1，之后所有结构变更被无限延迟。

C# 的 ref struct 没有确定性析构，任何依赖枚举器生命周期的隐式机制都不可靠。

## 与其他框架对照

| 框架 | 遍历期改结构 | 机制 |
| --- | --- | --- |
| Flecs | 排队，遍历后应用 | `ecs_defer_begin/end`、stage |
| Unity DOTS | `CompleteAllJobs` + 数组失效；推荐 ECB | `EntityCommandBufferSystem` 在固定时机回放 |
| EnTT | 未定义行为 | 文档约定，修改被迭代的 pool 使迭代器失效 |
| Arch | 未定义行为 | `InlineQuery` 零检查 |
| EntJoy | 立即执行并让枚举器失效；显式作用域内排队 | `BeginDeferred` |

## ECB 与 Job

`DeferredCommandBuffer.Playback` 与 `PlaybackParallel` 的第一行都调用 `entityManager.CompleteActiveJobs()`，先等所有活跃 Job 结束再应用命令。这是保守策略（等全部而非仅等生产者），对单线程主循环没有实际差别。

`EntityManager` 对 Job 内改结构有独立拦截：`EnsureNoJobExecuting` 在 `NativeJobScheduler.IsExecutingJob` 时抛出，`EnsureStructuralChangeAllowed` 拦截组件拷贝钩子、`Dispose` 与 Observer 回调内的结构变更，异常消息指向 `DeferredCommandBuffer`。

## 结构变更路径

`Chunk` 是 80 字节的结构体，从 `List<Chunk>` 索引器取值会整块复制。这条路径上有七处曾经这样取，全部改为 `ref` / `ref readonly`：

`EntityManager.UpdateEntityLocation`、`Archetype.CopyComponentsTo`、`Archetype.Set<T>`、`Archetype.SetStorageRaw`、`Archetype.NotifyComponentChanged`、`EntityManager.WriteColumnBytes`、移除路径的 `MarkEntityChanged`。

实测（`tools/EcsComparison` 的 `--struct 100000 25`，10 万实体，取各轮最小值）：

| 操作 | 修复前 | 修复后 |
| --- | --- | --- |
| 创建 | 24.13 ns | 24.12 ns |
| 销毁 | 24.07 ns | 24.29 ns |
| 添加组件 | 70.37 ns | 69.18 ns |
| 移除组件 | 83.51 ns | 78.31 ns |

移除组件收益明确（−6%），添加组件在轮次抖动内。

经实测排除的非瓶颈：`EntityLocateB` 的镜像写、`InvalidateStructuralQueries`、`HasComponent` 的组件查找、`EnterStructural` 的锁（`threadSafe: false` 时整句跳过）、`JobBookkeepingIdle` 的五个静态检查。`MoveComponentValue` 已是一次 `CopyBlock` 加条件清零。

添加组件距 Arch 仍有约 10 ns 差距，迁移路径各环节均已优化，进一步缩小需要重构 chunk 内的列布局。

