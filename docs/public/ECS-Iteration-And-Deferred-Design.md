# 遍历与延迟结构变更设计

## 遍历 API 分层

两层入口。两者看到的是同一批原型与 Chunk，差别只在推进粒度：

| 层 | 入口 | 逐实体开销 | 可用形态 |
| --- | --- | --- | --- |
| 按 Chunk | `world.Query<A,B>().Chunks()` | 无（循环体自己写） | 单/双组件选择器 |
| 实体级 | `foreach (var r in world.Query<A,B>())` | 逐实体游标 + 结果构造 | 单/双组件手写，其余由生成器发射 |

`Chunks()` 逐块给出 `QueryChunk`，`Span<T>()` 直接返回该块的组件列，封装开销可忽略。`T` 不必是查询条件之一：查 `A,B` 也能取 `Span<C>`。循环体自己写，JIT 能把 `Span` 上的整数运算向量化。

`Chunks()` 不带泛型参数，任何组件组合共用一个类型；`QuerySelection<T0>` 与 `QuerySelection<T0,T1>` 已提供，三/四组件形态用 `foreach`。

两种形态共用同一条「按段推进」的遍历：游标给出连续可用槽位段，段内按列基址指针索引。块级过滤（shared / changed）整块要么全要要么全不要，逐实体过滤（关系目标、启用位）给出一段连续可用槽位。10 万实体两组件：块级自写循环 28.5 µs，实体级 `foreach` 89.3（对 Arch `InlineQuery` 47.9 是 1.87 倍）。实体级慢在逐实体的游标推进与结果结构体，挡住的自动向量化，不是因为多走了别的路径。

## 变更过滤

`WithChanged<T>()` 挂在选择器上，可与实体遍历组合：`world.Query<A,B>().WithChanged<A>()`。它是**块级**过滤，与 DOTS 的 `WithChangeFilter` 同义——整块里任意实体被改过，整块都进来；要精确到实体得再判 `EntityManager.HasChanged<T>(entity)`。

10 万实体两组件，同一世界内配对（`--ab-pair query-changed query-changed-base 3` / `query-changed-none query-clean-base 3`，取轮内最小值）：

| 情形 | 耗时 | 相对同世界不过滤 |
| --- | --- | --- |
| 所有块都脏（全部命中） | 78.9–79.8 µs | 1.02 倍 |
| 所有块都干净（全部不命中） | 1.1 µs | 0.013 倍 |

块脏时判定开销可忽略；块干净时整个遍历只走一遍块头，这是每帧「没有变更就跳过」的常见情形。

## 其它过滤的成本

`sharedWorld`（10 万实体，A+B+Material+Flag，全部同值、全部启用）内配对，都是 `foreach`（`--ab-pair query-shared query-shared-base 3` / `query-enabled query-shared-base 3`，取轮内最小值）：

| 过滤 | 耗时 | 相对同世界不过滤 |
| --- | --- | --- |
| 无（基线） | 78.7–85.4 µs | 1.00 |
| shared 值匹配全部 | 89.5–92.5 µs | 1.11–1.15 倍 |
| 启用位全置位 | 119.9–121.8 µs | 1.42–1.49 倍 |
| 关系目标匹配全部 | 210–217 µs | 2.58–2.67 倍 |

两档差额分别约 10 µs、40 µs 与 130 µs，都按块推进路径收取。按 98 个块摊算，shared 与启用位每块 90 ns 与 400 ns，高于一次值比较或一次位图定位的开销，具体去向尚未定位。

关系过滤的 2.6 倍是逐实体比对一个关系槽的固有成本：块内列基址、槽宽与每槽数已经提到块边界（`PrepareRelationColumn`），连续命中段整段返回。改之前每个实体都要再进一次 `Advance`，实测 1415 µs / 14.4–15.7 倍；两步改动后 210–217 µs / 2.58–2.67 倍。启用位与关系同时在场时同样整段返回（1200 µs / 14.2–14.6 倍 → 256–275 µs / 2.96–3.15 倍）。

## 实体级遍历的固定成本

`Comp{i}` 直接返回 `ref`，不做逐访问校验：

- 关系列仍拒绝可写 `ref`：直接写会绕过关系索引。这是按类型定死的判据（泛型静态标记），不随运行期状态变化。托管多目标关系没有列，`Comp{i}` 据此抛出并指向关系读取 API。
- 遍历期间发生的结构变更不检查。这一点与 Arch、DOTS 一致：DOTS 在 Release 里没有逐访问 SafetyHandle，结构变更必须走 `EntityCommandBuffer`；Flecs 用 defer 把变更排到迭代结束。EntJoy 对应 `DeferredCommandBuffer` 与 `world.BeginDeferred()`。
- 借用校验保留在**块边界**：`MoveNext` 的快路径只做 `_slotOffset++` 与符号判断，跨 Chunk 时校验一次。单、双、三、四组件四条枚举器都按这一条实现；生成的三/四组件枚举器曾逐 `MoveNext` 与逐 `Current` 各校验一次，10 万实体三组件比现在慢 31%（107.7 → 73.7 µs）。
- 逐条消融 10 万实体两组件遍历（`--ab-pair query arch-query 3`）：去掉逐访问校验后 89.3–92.4 µs，此前带校验是 93.5–97.6。校验只占约 5%，差额主要在「`foreach` 取 `Current` 结果结构体、再经属性取 `ref`」这个形态本身。

## 稀疏随机访问

按实体句柄读写组件。10 万实体逐次一个操作（`--ab-pair`，取轮内最小值）。

读：

| 入口 | 耗时 | 相对 Arch `World.Get` |
| --- | --- | --- |
| `GetComponentLookup<T>()[entity]` | 394–402 µs | 1.12–1.15 |
| `EntityManager.GetComponent<T>(entity)` | 599–633 µs | 1.67–1.70 |
| Arch `World.Get<T>(entity)` | 350–378 µs | 1.00 |

写：

| 入口 | 耗时 | 相对 Arch `World.Set` |
| --- | --- | --- |
| `EntityManager.Set<T>(entity, value)` | 1616–1648 µs | 1.45–1.53 |
| `EntityManager.SetComponent<T>(entity, value)` | 3404–3455 µs | 3.12–3.16 |
| Arch `World.Set<T>(entity, value)` | 1092–1128 µs | 1.00 |

两条写入入口语义重叠，耗时差一倍：`Set<T>` 走类型化直写，`SetComponent<T>` 经定位表但每次按 `Type` 查一次类型表、并销毁旧值。两者应收敛到同一实现。

启用位翻转：`SetComponentEnabled<T>` 逐实体 930–938 µs（10 万次，每次 9.3 ns）。原先是 3167–3364 µs——完成作业的路径无条件进 `_activeJobLock`，没有 Job 时也照取一次锁；列下标还要先按 `typeof(T)` 查类型表、再查一次原型字典。改成先看记账标记、列下标走按组件 id 索引的数组后落到 9.3 ns。Add / Remove / Destroy 早就用了这个空闲短路，写入与启用位一直漏着。

句柄缓存上次原型与块基址，稳态只剩一次实体表加载加一次块比较；直取每次都从实体表重新解析定位，并多付处置、版本与关系列校验。热循环用句柄，一次性读取用直取。

写走类型化直写。关系列、世界里有观察者、原型含可析构列或 shared 列这三种情况退回对象路径——那条路每写一次要装箱并分配一个 GCHandle 钉住值。

关系列标记放在 `ChunkMetadata.RelationColumns` 里：这条标记按列定死，逐次访问只读一次数组，不再回查 `Archetype.Types`（省掉五次依赖加载，实测直取 −7%）。

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

作用域外改结构立即生效，并推进结构版本，已有查询借用随之失效：实体枚举器在跨 Chunk 的下一次 `MoveNext` 时抛 `InvalidOperationException`；同一 Chunk 内的 `Current` 与结果组件属性不做校验。Chunk 视图在获取列视图时抛。已取出的 `ref`、`Span`、裸指针不做版本校验，调用方负责使用期间存储有效。

## ECB 与 Job

`DeferredCommandBuffer.Playback` 与 `PlaybackParallel` 先调用 `entityManager.CompleteActiveJobs()`，等所有活跃 Job 结束再应用命令。这是保守策略（等全部而非只等生产者），对单线程主循环没有实际差别。

`EnsureNoJobExecuting` 在 `NativeJobScheduler.IsExecutingJob` 时抛错；`EnsureStructuralChangeAllowed` 拦截组件拷贝钩子、组件 `Dispose` 与 Observer 回调内的结构变更，异常消息指向 `DeferredCommandBuffer`。

## 结构变更路径

`Chunk` 是 24 字节的值类型（`ChunkMetadata` 是 class，结构体里只有引用）。热路径取块一律经 `CollectionsMarshal.AsSpan(_chunkList)[i]` 拿 `ref`，避免每次 24 字节复制；即使拿到副本，读写也作用在同一块非托管内存上，只有 `_entityCount`、`_highWater` 不跨副本递增。

迁移路径的列序、搬运方式与不变量见 [结构变更性能与不变量](Structural-Change-Performance-Handoff.md)。
