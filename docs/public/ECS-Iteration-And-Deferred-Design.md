# 遍历与延迟结构变更设计

## 遍历 API 分层

三层入口，按开销从低到高。三者看到的是同一批原型与 Chunk，差别只在推进粒度：

| 层 | 入口 | 逐实体开销 | 可用形态 |
| --- | --- | --- | --- |
| 按 Chunk | `world.Query<A,B>().Chunks()` | 无（循环体自己写） | 单/双组件选择器 |
| 块循环回调 | `world.Query<A,B>().ForEach(ref action)` | 无（指针推进） | 单/双组件手写，其余由生成器发射 |
| 实体级 | `foreach (var r in world.Query<A,B>())` | 逐实体游标 + 属性检查 | 单/双组件手写，其余由生成器发射 |

`Chunks()` 逐块给出 `QueryChunk`，`Span<T>()` 直接返回该块的组件列，封装开销可忽略。`T` 不必是查询条件之一：查 `A,B` 也能取 `Span<C>`。

`ForEach` 接受 `struct` 加 `IQueryForeach<...>` 实现。动作类型是 struct 时 JIT 能内联整个循环体，因此循环内没有逐实体游标状态。

`Chunks()` 不带泛型参数，任何组件组合共用一个类型；`QuerySelection<T0>` 与 `QuerySelection<T0,T1>` 已提供，三/四组件形态用 `ForEach` 或 `foreach`。

## 实体级遍历的固定成本

`Comp{i}` 每次访问都先 `Validate()` 再 `ComponentTypeManager.CheckWritableAccess<T>()`，后者读两个静态位（关系列标记、拷贝作用域计数）并做一次 `Volatile.Read`：

- 关系列在托管侧是只读的，可写 `ref` 必须拒绝；`QueryResult_CrossThreadCopyScope_PreservesCallingThreadWriteGuard` 要求这个检查逐访问生效，移到块边界会破坏语义。
- 把那个 `Volatile.Read` 短路掉，10 万实体两组件遍历只快约 5%（实测 129.9 μs → 119.4 μs）。剩余差距是逐实体的 `MoveNext`、`Current` 构造与两次属性访问本身——属语法形态的成本，换检查策略消不掉。
- 要 arch 那种零检查写入，用 `Chunks()` 或 `ForEach`：两条路走块表快路径，裸指针索引，不经过属性。

## 测量陷阱

`--traversal-matrix` 在同一个进程里轮着调十几种写法，JIT 对不同委托目标的内联策略不一致：同一份 `ChunkSpan` 代码在框架内报 26.65 μs、在独立进程报 53–56 μs。**判断遍历性能时一次进程只测一种写法**；跨写法对比不能在同一进程内完成。`--body-bisect`、`--job-overhead` 同样一次进程测多个入口，只能同入口纵向比较。

## 延迟结构变更

```csharp
using (world.BeginDeferred())
{
    foreach (var r in world.Query<Position, Velocity>())
        world.EntityManager.DestroyEntity(...);
}
```

作用域内的结构变更进命令队列，离开作用域时统一回放；嵌套时只有最外层回放（`EntityManager.ExitDeferred` 按深度递减）。

| 操作 | 作用域内行为 |
| --- | --- |
| `DestroyEntity` | 进队列 |
| `AddComponent<T>` | 进队列 |
| `RemoveComponent<T>` | 进队列 |
| `NewEntity` | 抛 `InvalidOperationException`，指向 `DeferredCommandBuffer.CreateEntity` |

延迟创建需要占位实体 ID 与回放期的 ID 重映射（Flecs 的 deferred entity ID）。`DeferredCommandBuffer` 的命令格式直接写入真实 `Entity` 值，没有重映射层，因此创建走独立入口：用 `CreateEntity` 记录，回放后从 `CreatedEntities` 取实际句柄。

## 为什么延迟必须显式

C# 的 `ref struct` 没有确定性析构。任何依赖枚举器生命周期的隐式机制都不可靠：只有 `foreach` 会调用 `Dispose`，`world.Query<A,B>().GetEnumerator().MoveNext()` 这类写法不会，遍历深度会永久停在 1，之后所有结构变更被无限延迟。

## 与其他框架对照

| 框架 | 遍历期改结构 | 机制 |
| --- | --- | --- |
| Flecs | 排队，遍历后应用 | `ecs_defer_begin/end`、stage |
| Unity DOTS | `CompleteAllJobs` + 数组失效；推荐 ECB | `EntityCommandBufferSystem` 在固定时机回放 |
| EnTT | 未定义行为 | 文档约定，修改被迭代的 pool 使迭代器失效 |
| Arch | 未定义行为 | `InlineQuery` 零检查 |
| EntJoy | 立即执行并让枚举器失效；显式作用域内排队 | `BeginDeferred` |

## 即时改结构与借用失效

作用域外改结构立即生效，并推进结构版本，已有查询借用随之失效：实体枚举器在下一次 `MoveNext`、读取 `Current` 或访问结果属性时抛 `InvalidOperationException`，Chunk 视图在获取列视图时抛。已取出的 `ref`、`Span`、裸指针不做版本校验，调用方负责使用期间存储有效。

## ECB 与 Job

`DeferredCommandBuffer.Playback` 与 `PlaybackParallel` 先调用 `entityManager.CompleteActiveJobs()`，等所有活跃 Job 结束再应用命令。这是保守策略（等全部而非只等生产者），对单线程主循环没有实际差别。

`EnsureNoJobExecuting` 在 `NativeJobScheduler.IsExecutingJob` 时抛错；`EnsureStructuralChangeAllowed` 拦截组件拷贝钩子、组件 `Dispose` 与 Observer 回调内的结构变更，异常消息指向 `DeferredCommandBuffer`。

## 结构变更路径

`Chunk` 是 24 字节的值类型（`ChunkMetadata` 是 class，结构体里只有引用）。热路径取块一律经 `CollectionsMarshal.AsSpan(_chunkList)[i]` 拿 `ref`，避免每次 24 字节复制；即使拿到副本，读写也作用在同一块非托管内存上，只有 `_entityCount`、`_highWater` 不跨副本递增。

迁移路径的列序、搬运方式与不变量见 [结构变更性能与不变量](Structural-Change-Performance-Handoff.md)。
