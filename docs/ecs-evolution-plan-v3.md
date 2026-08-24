# EntJoy ECS 进化方案修订版 v3

> 版本：v3  
> 日期：2026-08-13  
> 最后更新：2026-08-24  
> 前置文档：`docs/ecs-evolution-plan-v2.md`  
> 定位：本文档不是 v2 的增量补丁，而是基于当前源码核对后的决策完整修订版。  
> 实施时以本文档为准；v2 继续保留，仅作为设计背景和早期思路。

---

## 进度追踪（2026-08-24 更新）

> 本节为实施进度的实时快照，每次 Phase 完成后更新。

| Phase | 内容 | 状态 | 完成时间 | 关键提交 |
|-------|------|------|----------|----------|
| **Phase 1** | 基础设施优化 | ✅ **已完成** | 2026-08-22 | `6391038` |
| **Phase 2** | Archetype Edges + Chunk lazy zero | ✅ **已完成** | 2026-08-24 | `9a96b96` |
| **Phase 3** | Selective Wait + Batch CreateEntities | ✅ **基础完成** | 2026-08-24 | `1452573` |
| **Phase 4** | System/Processor 框架 + Query 分层 | 🔲 未开始 | — | — |
| **Phase 5** | 易用性基础设施 | 🔲 未开始 | — | — |
| **Phase 6** | 实体关系 | 🔲 未开始 | — | — |
| **Phase 7** | Shared Component 落地 + Subsystem Query | 🔲 未开始 | — | — |
| **Phase 8** | Source Generator 扩展 | 🔲 未开始 | — | — |
| **Phase 9** | Managed 类型与原生投影（独立轨道） | 🔲 未开始 | — | — |

**当前里程碑**：里程碑 A（高性能核心）—— Phase 1 ✅ → Phase 2 ✅ → Phase 3 ✅ → Phase 4 🔲

**附注**：
- Phase 1-3 已完成基础实现，性能数据已收集。
- Phase 4（System 框架）是下一个关键目标，为 ECS 提供完整的 System 生命周期。

---

## 0. 文档说明

本文档基于以下当前实现状态完成：

- `src/EntJoy/Archetype/Archetype.cs`
- `src/EntJoy/Chunk/Chunk.cs`
- `src/EntJoy/Entity/EntityManager.cs`
- `src/EntJoy/JobSystem/NativeJobScheduler.cs`
- `src/EntJoy/Query/QueryBuilder.cs`
- `src/EntJoy/World/World.cs`
- `src/EntJoy/System/ISystem.cs`
- `src/EntJoy.SourceGenerator/`

本文档的目标是：

1. 保留 v2 中成立的设计方向。
2. 纠正 v2 中会被当前代码结构破坏或无法直接实现的结论。
3. 为每个 Phase 给出可执行的变更边界、验收条件和风险控制。
4. 明确哪些优化属于高收益优先项，哪些属于需要前置协议的大型改造。

本轮只修改文档，不修改源码。

---

## 1. 结论摘要

### 1.1 仍然成立的设计方向

- 双轨制：`SystemBase + CodeGen` 易用路径与 `IJobChunk + NativeTranspiler` 深度路径。
- 底层统一：per-archetype 存储、同一套 JobSystem、同一个 TempAllocator。
- Per-archetype SoA 是核心存储模型。
- `IJobChunk`、NativeTranspiler、ISPC/C++ 是当前项目已有的高性能差异化能力。

### 1.2 必须纠正或补强的方向

- `ChunkPool` 不能简单使用普通 `Marshal.AllocHGlobal` 替代 slab，否则会破坏 64B 对齐和 chunk 地址稳定假设。
- `Shared Component` 不能实现为“每个 Archetype 一个可变 `Dictionary<int, object>`”。
- `Selective Wait` 必须修改调度契约，否则无法可靠地只等待受影响 Archetype。
- `QueryBuilder` 的零分配优化不能直接用静态数组覆盖追加语义。
- `Managed Component / NativeProjection` 必须先引入组件生命周期协议，不能先直接把 `NativeString`、`NativeDictionary` 塞进 SoA。

### 1.3 推荐实施顺序

```text
Phase 1 -> Phase 3 -> Phase 4 -> Phase 2 -> Phase 5 -> Phase 6 -> Phase 7 -> Phase 8
                                                                             \
                                                                              Phase 9
```

Phase 9 独立作为后续轨道，先完成组件 copy/move/destroy hooks，再引入非 blittable 组件。

---

## 2. 现状基线

以下是本轮源码核对结果，不是 v2 的推测。

| 项目 | 当前实现事实 | 影响 |
|------|--------------|------|
| TempAllocator | 全局 `ConcurrentDictionary<IntPtr, int>`、全局锁、逐次 `Marshal.FreeHGlobal` | 热路径锁竞争、帧末 O(n) 释放、GC/系统分配开销 |
| Archetype.AddEntity | 每次 `_chunkList.IndexOf(targetChunk)` | 每实体新增为 O(n) |
| Archetype.GetChunks | 每次 `new List<Chunk>(_chunkList)` | 查询与调度路径持续分配 |
| QueryBuilder.WithAll | 每次 `ToList()` + `ToArray()` | 查询构建分配 |
| EntityManager job tracking | `_activeJobs` 为全局列表，结构操作和 `Set` 等待所有 active jobs | 不必要串行化 |
| NativeJobScheduler.TrackEntityJob | 只登记 `NativeJobHandle`，未记录 matching archetypes | 无法实现选择性等待 |
| QueryEnumerable | `MoveNext()` 仍是 `NotImplementedException` | 易用查询路径不可用 |
| World | 尚无 `AddSystem` / `Update` | System 框架缺失 |
| ComponentTypeManager | 非 blittable 类型直接抛错 | Phase 9 需要重设计 |

关键文件位置：

- [TempAllocator.cs](E:/GODOT/Project/EntJoy/src/EntJoy/Collections/TempAllocator.cs)
- [Archetype.cs](E:/GODOT/Project/EntJoy/src/EntJoy/Archetype/Archetype.cs)
- [EntityManager.cs](E:/GODOT/Project/EntJoy/src/EntJoy/Entity/EntityManager.cs)
- [NativeJobScheduler.cs](E:/GODOT/Project/EntJoy/src/EntJoy/JobSystem/NativeJobScheduler.cs)
- [QueryBuilder.cs](E:/GODOT/Project/EntJoy/src/EntJoy/Query/QueryBuilder.cs)
- [QueryEnumerable.cs](E:/GODOT/Project/EntJoy/src/EntJoy/Query/QueryEnumerable.cs)
- [ComponentTypeManager.cs](E:/GODOT/Project/EntJoy/src/EntJoy/Component/ComponentTypeManager.cs)
- [World.cs](E:/GODOT/Project/EntJoy/src/EntJoy/World/World.cs)

---

## 3. 修订后的总体架构

### 3.1 双轨制

```text
易用路径
  SystemBase + SourceGenerator
  EntityQuery / Group / Index
  World Events / One-Frame Components
  Auto-Defer / ECB

深度路径
  IJobChunk / Processor
  ArchetypeChunk + Span<T>
  NativeTranspiler / ISPC / C++
  零事件生成、零 reactive 开销

共享底层
  Archetype + SoA
  Chunk / ChunkPool
  NativeJobScheduler
  TempAllocator
  ComponentTypeManager
```

### 3.2 底层不变量

实施所有 Phase 时必须维护以下不变量：

1. **Archetype 是组件集合的身份边界**，组件集合必须是排序后的 canonical set。
2. **Chunk 内组件数组按 64B 对齐**，且 chunk 基址本身也需要满足 SIMD 对齐。
3. **Entity 位置查找为 O(1)**：`EntityId -> Archetype + ChunkIndex + Slot`。
4. **结构变更前必须等待访问受影响 Archetype 的所有 Job**。
5. **Job 执行期间不能直接进行结构性变更**，只能 defer 到安全的 playback 阶段。
6. **SoA 组件默认只支持 blittable 数据**；指针型或原生容器组件必须显式定义 copy/move/destroy 语义。
7. **易用 API 的分配必须可控**：热路径不使用运行时反射、装箱或动态 delegate。

### 3.3 组件生命周期协议

Phase 9 之前必须建立组件生命周期协议，至少包含：

| 操作 | 当前能力 | 需要补齐 |
|------|----------|----------|
| 创建 | `Chunk.AddEntity` 清零组件槽 | 对原生容器调用 allocator/init |
| 复制 | `Unsafe.CopyBlock` | 对 pointer 容器执行 deep copy 或引用计数 |
| 移动/swap-pop | `Unsafe.CopyBlock` | 对 pointer 容器执行 move/copy |
| 销毁 | 当前无显式 hook | 释放原生容器或 managed 引用 |

没有该协议，`NativeString`、`NativeDictionary` 等不能作为可复制组件放入 SoA。

---

## 4. 分阶段修订方案

---

## Phase 1：基础设施优化 ✅ 已完成（2026-08-22）

### 目标

消除当前最确定的 O(n) 和不必要分配。

### 变更点

#### 1.1 TempAllocator：Per-Thread Stack ✅ 已完成（1.1a 方案）

> 2026-08-22 实施：完整 per-thread 栈暂缓（收益/风险比低），改为 **1.1a 去全局锁**——
> `_active` ConcurrentDictionary + `_resetLock` → per-thread `ThreadEntry`（gate 锁，owner 无争用）；
> Alloc/Free 快路径无全局锁；Reset 逐线程收集。EntJoySample 编译通过 + IJobChunkMoveCompareTest 回归。

**目标形态：**

- 为每个主线程和原生 worker 线程维护独立内存栈。
- 分配使用当前线程的原子游标，避免全局锁。
- 帧末对所有线程栈执行 O(threadCount) 级 Reset。
- 小分配走栈；超过固定阈值的大块分配回退到独立 native 分配，并在独立 free 链表中跟踪。

**必须解决：**

- 如何把原生 worker thread 映射到 C# 侧 slot。
- 固定栈容量选择：建议默认 1 MiB，但需要可配置，并提供溢出回退。
- 跨线程所有权：当前 `TempAllocator.Reset` 会在释放前等待 active jobs；新实现必须保证 Reset 只发生在无 Job 使用 Temp 内存之后。

**验收：**

- 分配路径无全局锁。
- 常规帧末释放从 O(n) 降至 O(threadCount)。
- 超过栈容量的大块分配不越界。

#### 1.2 ChunkPool：保留对齐的池化 ✅ 已完成

> 2026-08-22 实现：`ChunkMemoryPool.cs`（64KB 块池化器），Archetype AllocateFromSlab/Dispose
> 改用池。EntJoySample 编译通过 + IJobChunkMoveCompareTest 回归。

**目标形态：**

- 池化空闲 chunk，但每个 chunk 仍需保证 64B 对齐。
- Pool key 必须使用 canonical archetype identity，不能只用 `archetypeTypeHash`。
- 结构性变更前确保访问对应 archetype 的 Job 已完成。

**实现原则：**

```text
Rent(archetype identity, chunkSize)
  -> 池中取空闲 chunk
  -> 无空闲时按对齐策略过度分配，并返回对齐后的指针

Return(chunk, identity)
  -> 将 chunk 放回该 identity 对应池

Trim()
  -> 场景切换或显式调用时释放所有空闲 chunk
```

**禁止：**

- 直接返回未对齐的 `Marshal.AllocHGlobal` 指针作为 chunk 基址。
- 用只含 `(size, typeHash)` 的 key，因为哈希碰撞可能导致复用错误布局 chunk。
- 在 Job 仍可能访问 chunk 时回池。

**验收：**

- chunk 基址 64B 对齐。
- 同布局 archetype 之间可复用。
- 不同布局 archetype 之间不会错误复用。

#### 1.3 AddEntity 去 O(n) ✅ 已完成

> 2026-08-22 实现：`Archetype.AddEntity` `_chunkList.IndexOf(targetChunk)` → `Count-1`
> （targetChunk 恒为末块或新建末块不变量）。

将：

```csharp
chunkIndex = _chunkList.IndexOf(targetChunk);
```

改为：

```csharp
chunkIndex = _chunkList.Count - 1;
```

前提是 `targetChunk` 只能是最后一个 chunk 或新建后位于末尾的 chunk。当前逻辑满足该前提。

**验收：**

- 10000 次 AddEntity 不再随 chunk 数线性增长。

#### 1.4 GetChunks 零分配 ✅ 已完成

> 2026-08-22 实现：`Archetype.ChunkSpan`（CollectionsMarshal.AsSpan 零拷贝），
> NativeEcsScheduler 7 处 + EntityQuery.Refresh + sample 迁移。

不直接返回可变内部列表。

建议拆分：

- 内部热路径访问 `Archetype.ChunkList`。
- 对外只读查询使用 `IReadOnlyList<Chunk>`。
- 调度器需要稳定快照时，继续使用数组或保留快照，不能依赖可变 `List` 在调度期间不变化。

**验收：**

- 查询调度路径不再每次 `new List<Chunk>`。
- 外部 API 不会绕过 `Archetype` 修改内部列表。

#### 1.5 QueryBuilder 零分配 ✅ 已完成

> 2026-08-22 实现：`WithAll<T>/<T,T2>/WithEnabled<T>` 单组件直接引用 `ComponentTypes<T>.Share`（0 分配）；
> 链式追加用 `Merge`（1 次 Array 分配，替代 List+ToArray 2 次）。

`WithAll<T>()` 不能简单写为：

```csharp
All = ComponentTypes<T>.Share;
```

因为 `WithAll<A>().WithAll<B>()` 会覆盖前面的条件。

应使用：

- Pooled builder storage。
- 固定组合重载的静态缓存。
- 或内部 stackalloc 构建临时列表，最终写入稳定数组。

同时检查 `WithEnabled<T>()`，它当前也有 `ToList()` + `ToArray()`。

**验收：**

- 单组件查询和链式查询均无分配。
- 链式条件不丢失。

### 依赖

无外部前置条件。

### 风险

- ChunkPool 是最容易破坏对齐和局部性的部分，应单独验证。
- 外部暴露 `ChunkList` 会增加并发修改风险，需要明确 API 边界。

#### 1.6 Chunk struct 化（元数据连续化）🔲 推荐下一步（Step 0）

> 2026-08-24 分析：Phase 1 主体已完成，Chunk struct 化是遗留项，推荐在 Phase 3 之前完成。
> 详见 `Phase优先级分析与实施路线.md` §三。

**目标形态：**

- `Chunk` 从 `sealed unsafe class : IDisposable` 改为 `struct`（blittable，只含 `IntPtr`/`int` 等值类型字段）。
- `Archetype._chunkList` 从 `List<Chunk>` 改为 `NativeList<Chunk>`（连续内存遍历）。
- 64KB 数据块保持独立（不合并），Chunk struct 只是"指向数据块的元数据句柄"。
- `Chunk.Dispose` 语义由 `ChunkMemoryPool.Return` 管理（struct 无 Dispose）。

**收益：**

- 300×40B=12KB 连续元数据进 L1 缓存，遍历零指针跳转。
- 消除 300 个 class 对象头（~16B/对象 = 4.8KB）+ GC 根扫描。
- `ArchetypeChunk`（对外句柄）已经是 struct，用户 API 零影响。
- 为 Phase 3（ECB staging 遍历）和 Phase 4（QueryEnumerator 遍历）提供连续内存基础设施。

**关键约束：**

- Chunk 不能包含任何托管引用（必须 blittable）。
- 修改 `EntityCount` 等字段时注意值拷贝（通过索引 + `ref` 访问）。
- `NativeList<Chunk>` 的 Capacity 扩容时指针失效——调度期间不扩容（快照语义）。

**验收：**

- Chunk 遍历性能提升 10-20%（连续内存 vs 指针跳转）。
- GC 根数量减少（300 个 class 对象 → 1 个 NativeList 数组对象）。
- IJobChunkMoveCompareTest 性能不退化（C#/C++/ISPC 全路径）。
- `ArchetypeChunk` 用户 API 无变化。

---

## Phase 2：Archetype Edges 与存储增强

### 目标

减少 `AddComponent` / `RemoveComponent` 反复创建 Archetype 和查找的开销。

### 2.1 Archetype Edges

**目标形态：**

每个 Archetype 缓存：

```text
add edge:    typeId -> targetArchetype
remove edge: typeId -> targetArchetype
```

但 target 必须基于排序后的 canonical component set 计算，不能依赖当前临时数组顺序。

`AddComponent` / `RemoveComponent` 先走 edge，未命中时才调用 `GetOrCreateArchetype`，并写回 cache。

**验收：**

- 同一 Archetype 的重复 Add/Remove 不重复排序和查找。
- 相同组件集合最终指向同一 Archetype。

### 2.2 Shared Components

**修订结论：**

Shared Component 不能实现为“同一 Archetype 的实体共享一个可变 `Dictionary<int, object>`”。

正确语义二选一：

1. 将 Shared Component 作为 Archetype 的键组成部分，值不同则拆成不同 Archetype。
2. 将 Shared Component 作为 per-entity 数据，但存储在独立的 sparse/compact 结构中。

推荐先采用选项 1，因为它与现有 Archetype/SoA 模型最兼容，语义也最清晰。

**验收：**

- 两个实体拥有不同 shared value 时，不再处于同一 Archetype。
- 查询 Shared Component 时不会误读其他实体的值。

### 2.3 Chunk lazy zero

保留 `AddEntity` 对目标 slot 清零。

移除 chunk 构造时的整体 `Unsafe.InitBlock`，因为从 pool 复用的 chunk 可能包含旧数据。

**要求：**

- 新 slot 的组件数据、entity slot、enableable bitmap 都必须显式初始化。
- 复制组件时必须覆盖目标 slot。

**验收：**

- 分配新 chunk 时不再整体清零。
- 从池中复用的 chunk 无脏数据泄漏。

### 风险

- Shared Component 若继续保留 v2 的模糊写法，会产生隐蔽错误。
- Edges 若 key 只使用 typeId 而未绑定当前 Archetype，可能返回错误目标。

---

## Phase 3：选择性等待 + Auto-Defer + ECB

### 目标

避免所有结构性操作等待无关 Job，并提供安全的延迟结构变更。

### 3.1 Per-Archetype Job Tracking

修改 `NativeJobScheduler.TrackEntityJob` 契约：

```text
TrackEntityJob(entityManager, handle, matchingArchetypes)
```

调度器在构建 chunk 列表时已经遍历过 Archetype，因此应将 matching archetypes 与 handle 一起登记。

**要求：**

- 所有 entity job 调度路径都要传入匹配 Archetype。
- `Raw` job 不操作 Entity 结构，不需要进入 EntityManager 的 archetype tracking。

**验收：**

- 每个 Entity Job 至少登记到其访问的 Archetype。
- 没有遗漏的未登记 entity job。

### 3.2 Selective Wait

定义 `CompleteArchetypeJobs(affectedArchetypes)`：

| 操作 | 受影响 Archetype |
|------|------------------|
| NewEntity | target archetype |
| DestroyEntity | source archetype |
| AddComponent | source + target archetype |
| RemoveComponent | source + target archetype |
| Set / SetComponentEnabled | current archetype |

**实现要点：**

- 从 per-archetype job 表取出 handles。
- 先 prune 已完成 handles。
- 再 `Complete()` 剩余 handles。
- 保留 `CompleteActiveJobs()` 用于 World Dispose 和 TempAllocator Reset。

**验收：**

- `Set<A>` 不再等待只访问 `B` 的 Job。
- Add/Remove 不会漏等 source 或 target Archetype 的 Job。

### 3.3 DeferredCommandBuffer 与 Auto-Defer

**底层要求：**

- 使用稳定的命令 staging 区。
- 命令记录必须是 append-only。
- Job 执行期间不直接修改 Archetype，只写入 staging。
- Playback 在主线程完成，并在 playback 前只等待该命令集合涉及的 Archetype。

**API 分层：**

```text
显式 ECB:
  CreateCommandBuffer -> Create/Destroy/Add/Remove -> Playback

Auto-Defer:
  检测 Job 执行上下文 -> 自动写线程 local staging -> 帧末统一 Playback
```

**验收：**

- Job 内结构变更不会抛出 `Structural changes are not allowed`。
- Playback 保持命令提交顺序。
- 不在 Job 中直接修改 Entity 结构。

### 3.4 Batch Structural Changes

提供：

```csharp
World.CreateEntities(count, types)
```

内部使用：

- 一次 Archetype 查找/创建。
- 一次 chunk 批量分配。
- 一次批量 slot 初始化。
- 一次返回 `Entity[]`。

不逐实体调用 `NewEntity`，也不逐实体 `CompleteActiveJobs`。

**验收：**

- 10000 实体批量创建远快于 10000 次单实体创建。
- Entity 版本、位置索引和结构版本正确。

### 风险

- Selective Wait 最危险的是遗漏调度路径。
- ECB 需要区分“可安全 playback”的时机，否则会与 Job 执行竞争。

---

## Phase 4：System/Processor 框架与 Query 分层

### 目标

补齐 System 调度、Query 迭代和易用/深度双 API。

### 4.1 复用已有 API

不要另起一套重复系统。已有：

- `ISystem`：生命周期钩子。
- `SystemAPI`：当前只提供基础 Query 入口。
- `QueryEnumerable<T0,T1>`：当前枚举未实现。
- `ChunkEnumerable<T0,T1>`：chunk 遍历已有基础。
- `EntJoy.SourceGenerator`：已有多个生成器。

### 4.2 QueryEnumerator

补齐 `QueryEnumerable.MoveNext` 和 `Current`。

设计为 `ref struct`，按 Archetype 和 Chunk 遍历，最终返回实体级 component 引用。

**验收：**

- `foreach (var (pos, vel) in state.Query<Position, Velocity>())` 可运行。
- 遍历过程无装箱和明显分配。

### 4.3 System 框架

新增：

- `SystemBase`：易用路径基类。
- `Processor`：深度路径基类。
- `SystemGraph`：注册、排序、Phase 执行。
- `Phase` / `SystemState`：阶段和上下文。

`World` 增加 `AddSystem` / `Update`，并在 Phase 间提供 sync point。

### 4.4 Lambda 易用路径

`Entities.ForEach((ref Position pos, in Velocity vel) => ...)` 不直接使用普通 C# lambda 作为长期运行方案。

应由 source generator 将易用路径展开为 struct callback 或直接的 chunk 循环。

**目标：**

- 不产生闭包/delegate 分配。
- AOT/Godot .NET 下仍可生成。

### 风险

- 普通 lambda 路径容易被错误地用于热路径。
- Phase 内并行调度需要读写冲突分析，不能只按注册顺序执行就宣称并行。

---

## Phase 5：易用性基础设施

### 5.1 World Events

- 使用 typed event channel。
- 默认事件为 struct，避免 `object` 装箱。
- 订阅只允许启动期或安全边界注册。

### 5.2 One-Frame Components

- 不要每个实体每帧逐个 `AddComponent` / `RemoveComponent`。
- 在帧末批量清理。
- 使用临时 Archetype 或 staging，避免结构变更风暴。

### 5.3 Entity Index / Group

- 维护 delta 更新。
- 组件值变化时更新索引，不在查询时全量扫描 predicate。
- 对 Group 的写入走批量或 ECB。

### 5.4 Context

- 支持多个隔离 World。
- World 命名仅用于诊断，不参与热路径。

### 5.5 DI

- 允许启动期反射扫描和注入。
- 运行期不得依赖反射查找 EntityManager、System 或组件。

---

## Phase 6：实体关系

### 6.1 Relation 编码

关系列使用 SoA，但编码必须包含 target 版本或 epoch，防止实体 ID 回收后关系指向错误的新实体。

不能只使用：

```text
(RelationTypeID << 32) | TargetEntityID
```

建议拆分或扩展为：

```text
relationTypeId + targetEntityId + targetVersion/epoch
```

### 6.2 级联删除

建立 relation target index，例如：

```text
targetEntity -> relation list
```

级联删除走索引，而不是扫描所有关系列。

### 6.3 Prefab / IsA

标记为可选能力，不进入主里程碑。实现前先确定组件解析顺序和 inherited archetype 语义。

---

## Phase 7：Shared Component 与 Subsystem Query

### 7.1 Shared Component

- 明确采用值不同则拆 Archetype 的模型。
- 改变 shared value 时执行结构移动。
- 如果后续需要 per-entity 共享值但不拆 Archetype，再单独设计 compact store。

### 7.2 Subsystem Query

- 只用于系统依赖注入和生命周期管理。
- 不把 subsystem 放进组件 SoA。
- 不在热路径进行反射式查找。

---

## Phase 8：Source Generator

### 8.1 定位

扩展现有 `EntJoy.SourceGenerator`，而不是重新创建生成器项目。

已有：

- `EntitySystemGenerator`
- `QueryBuilderSourceGenerator`
- `SystemArgGenerator`
- `IJobEntitySourceGenerator`

### 8.2 生成目标

- Component 存取生成。
- System 注册生成。
- Reactive System 生成。
- 易用查询路径展开为 struct loop。

### 8.3 AOT 约束

- 不依赖运行时反射。
- 不依赖动态代码生成。
- 输出必须通过 AOT/IL2CPP/Godot .NET 检查。

---

## Phase 9：Managed 类型与原生投影

### 9.1 前置协议

Phase 9 实施前必须先完成：

1. Component copy hook。
2. Component move hook。
3. Component destroy hook。
4. Native container ownership 规则。
5. Swap-pop 对 pointer 组件的安全处理。

### 9.2 Managed 字段拆分

不能通过简单修改 `ComponentTypeManager` 就让非 blittable struct 直接进入现有 SoA。

建议：

- unmanaged 字段进入 chunk SoA。
- managed 字段进入 ManagedComponentStore。
- Source generator 自动生成拆分布局和访问器。
- 用户看到的组件 struct 保持统一外观，但底层存储分拆。

### 9.3 NativeProjection

`string` / `List<T>` / `Dictionary<K,V>` 不直接作为 SoA 数据。

投影规则：

| C# 类型 | 投影 | 放 SoA | C++ 可读 |
|---------|------|--------|----------|
| unmanaged scalar | 原生值 | 是 | 是 |
| `NativeString` | 原生字符串视图 | 是 | 是 |
| `NativeList<T>` | 原生列表 | 是 | 是 |
| `NativeDictionary<K,V>` | 原生哈希表 | 是 | 是 |
| `string` / `List<T>` | ManagedStore | 否 | 否 |
| `object` | 不支持 | 否 | 否 |

### 9.4 风险

这是所有 Phase 中对现有组件模型破坏最大的部分。

不能先做 API，再补生命周期；否则会先破坏 swap-pop、复制和销毁安全。

---

## 5. 困难与解决方案

| 困难 | 影响 | 修订后的解决方案 |
|------|------|------------------|
| TempAllocator 无法直接按 managed thread 映射 native worker | 线程栈设计失败或出现跨线程释放 | 建立 worker slot，主线程独立栈，大块回退独立 native 分配 |
| ChunkPool 破坏 64B 对齐 | SIMD 代码崩溃或性能回退 | Rent 时过度分配并手动对齐，验证 chunk 基址 |
| ChunkPool key 只含 type hash | 哈希碰撞导致错误布局复用 | 使用 canonical archetype identity |
| Selective Wait 遗漏调度路径 | 结构变更与 Job 并发访问 | 修改 `TrackEntityJob` 契约，覆盖所有 entity job 调度路径 |
| ECB 在 Job 中写入 staging 的线程安全 | 命令丢失或竞态 | 每个执行上下文独立 staging，主线程统一 playback |
| QueryBuilder 静态数组优化覆盖追加语义 | 链式查询条件丢失 | 使用 pooled builder 或固定组合静态缓存 |
| Lambda 易用 API 产生 delegate/闭包 | 热路径 GC 分配 | Source Generator 展开为 struct loop |
| Relation 只存 target ID | 实体回收后指向错误实体 | 增加 target version/epoch |
| 级联删除扫描所有关系列 | O(total relations) | 建立 target index |
| NativeString/NativeDictionary 被 swap-pop 浅复制 | double free / 泄漏 | 先定义 copy/move/destroy hooks |

---

## 6. 实施顺序与里程碑

### 里程碑 A：高性能核心

```text
Phase 1: 基础设施优化
Phase 3: Selective Wait + ECB + Batch Operations
Phase 4: Query/System 最小可用
```

该里程碑完成后，应能用 `IJobChunk` / `SystemBase` 跑通基础 ECS 流程，并显著降低结构变更和查询分配。

### 里程碑 B：存储与易用性

```text
Phase 2: Edges + Shared Component 语义
Phase 5: Events / One-Frame / Group / Context / DI
Phase 6: Relations
Phase 7: Shared Component / Subsystem Query
```

该里程碑关注正确性和开发效率。

### 里程碑 C：生成器与扩展

```text
Phase 8: Source Generator 扩展
```

### 里程碑 D：Managed 类型轨道

```text
组件 copy/move/destroy hooks
ManagedComponentStore
Phase 9: Managed 类型与原生投影
```

该轨道单独进行，不阻塞 A/B/C。

---

## 7. 测试与基准

### 7.1 基准

保留 v2 的 benchmark 方向，并增加以下验证：

| 场景 | 验证目标 |
|------|----------|
| 10000 次 AddEntity | 从 O(n) 到 O(1)，无额外 chunk 扫描 |
| ChunkPool Rent/Return | 复用正确、64B 对齐、内存峰值 |
| Add/Remove Component | edge 命中、新老 Archetype 索引正确 |
| Selective Wait | 只等待受影响 Archetype，wait 次数下降 |
| Query/System 热路径 | 使用 allocation counter 或 profiler 确认零分配 |
| Relation target 回收 | 实体 ID 回收后旧关系不指向新实体 |
| Source Generator | Debug/Release/AOT 输出一致 |

### 7.2 正确性

每个 Phase 必须有可运行测试：

- TempAllocator：并发分配、Reset、溢出分配。
- ChunkPool：同布局复用、不同布局隔离、64B 对齐。
- Add/Remove：moved entity 索引、compacted chunk 索引。
- Selective Wait：查询 A 不阻塞查询 B 的结构操作。
- ECB：创建、销毁、Add/Remove、Playback 顺序。
- Relation：级联删除、版本回收。
- Native container：复制、移动、销毁、swap-pop。

---

## 8. 假设与待实测项

本文档基于当前源码静态核对，不包含本次基准测试结果。

以下结论在实施前仍建议先用 microbenchmark 确认：

- Per-thread TempAllocator 对当前 C++ worker 线程模型的精确收益。
- ChunkPool 相比现有 slab 的缓存局部性变化。
- Selective Wait 在不同 query 数量和 Archetype 数量下的实际收益。
- Source Generator 展开后的 allocation 情况。
- Relationship target index 的内存开销是否可接受。

---

## 9. 文件变更建议

已新增：

```text
docs/ecs-evolution-plan-v3.md              ← 本文档（v3 进化方案）
docs/项目现状总览.md                        ← 综合状态文档（记录各模块完成状态、性能基线、已知问题）
docs/Phase优先级分析与实施路线.md            ← 全部待办项优先级排序、依赖关系图、工时估算（2026-08-24 新增）
```

后续源码实施时再按 Phase 拆分 PR。

---

## 10. 与 v2 的关系

- v2 保留为设计背景。
- v3 是实施基准。
- 当 v2 与 v3 冲突时，以 v3 为准。
