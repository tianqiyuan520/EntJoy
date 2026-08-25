# EntJoy ECS 进化方案修订版 v3

> 版本：v3  
> 日期：2026-08-13  
> 最后更新：2026-08  
> 前置文档：`docs/ecs-evolution-plan-v2.md`  
> 定位：本文档不是 v2 的增量补丁，而是基于当前源码核对后的决策完整修订版。  
> 实施时以本文档为准；v2 继续保留，仅作为设计背景和早期思路。

---

## 进度追踪（2026-08 最新）

> 本节为实施进度的实时快照，每次 Phase 完成后更新。

| Phase | 内容 | 状态 | 完成时间 | 关键提交 |
|-------|------|------|----------|----------|
| **Phase 1** | 基础设施优化 | ✅ **已完成** | 2026-08-22 | `6391038` |
| **Phase 2** | Archetype Edges + Chunk lazy zero | ✅ **已完成** | 2026-08-24 | `9a96b96` |
| **Phase 3** | Selective Wait + Batch CreateEntities | ✅ **基础完成** | 2026-08-24 | `1452573` |
| **Phase 4** | System 调度与开发体验增强 | 🔲 **进行中** | — | — |
| **Phase 5** | 易用性（关系型状态机 + 事件总线） | 🔲 未开始 | — | — |
| **Phase 6** | 实体关系 | 🔲 未开始 | — | — |
| **Phase 7** | Shared Component | 🔲 未开始 | — | — |
| **Phase 8** | Source Generator + Zero Boilerplate | 🔲 未开始 | — | — |
| **Phase 9** | 托管类型 + AOT 兼容 | 🔲 未开始 | — | — |
| **工具链** | Godot 桥接 + 热重载 + 分析器 | 🔲 未开始 | — | — |

**当前里程碑**：里程碑 A（高性能核心）—— Phase 1 ✅ → Phase 2 ✅ → Phase 3 ✅ → Phase 4 进行中

**附注**：
- Phase 1-3 已完成基础实现，性能数据已收集。
- Phase 4 已完成大部分（2026-08）：Schedule Graph、OrderBefore/OrderAfter、Entity Builder、Change Tracking、RunWhen 已实现。命名空间从 `EntJoy` 重构为 `EntJoy.ECS`。
- Phase 9 新增 AOT 兼容修复和托管类型支持。
- 工具链新增热重载支持（仅限 Native System）。

---

## 0. 文档说明

本文档基于以下当前实现状态完成：

- `src/EntJoy.ECS/Archetype/Archetype.cs`
- `src/EntJoy.ECS/Chunk/Chunk.cs`
- `src/EntJoy.ECS/Entity/EntityManager.cs`
- `src/EntJoy.ECS/JobSystem/NativeEcsScheduler.cs`
- `src/EntJoy.ECS/Query/QueryBuilder.cs`
- `src/EntJoy.ECS/World/World.cs`
- `src/EntJoy.ECS/System/ISystem.cs`
- `src/EntJoy.ECS.SourceGenerator/`

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

- [TempAllocator.cs](E:/GODOT/Project/EntJoy/src/EntJoy.Collections/TempAllocator.cs)
- [Archetype.cs](E:/GODOT/Project/EntJoy/src/EntJoy.ECS/Archetype/Archetype.cs)
- [EntityManager.cs](E:/GODOT/Project/EntJoy/src/EntJoy.ECS/Entity/EntityManager.cs)
- [NativeEcsScheduler.cs](E:/GODOT/Project/EntJoy/src/EntJoy.ECS/JobSystem/NativeEcsScheduler.cs)
- [QueryBuilder.cs](E:/GODOT/Project/EntJoy/src/EntJoy.ECS/Query/QueryBuilder.cs)
- [QueryEnumerable.cs](E:/GODOT/Project/EntJoy/src/EntJoy.ECS/Query/QueryEnumerable.cs)
- [ComponentTypeManager.cs](E:/GODOT/Project/EntJoy/src/EntJoy.ECS/Component/ComponentTypeManager.cs)
- [World.cs](E:/GODOT/Project/EntJoy/src/EntJoy.ECS/World/World.cs)

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

## Phase 4：System 调度与开发体验增强（重设计 2026-08）

### 目标

补齐 System **自动并行化**、**声明式开发**、**变更追踪**三大能力。砍掉与 IJobEntity 重复的 Processor/SystemBase 包装。

### 背景：旧方案的问题

旧 Phase 4 计划了 Processor（Unreal Mass）、SystemBase（Unity DOTS）和 SystemGraph，但经审视发现：

- **Processor** 本质是 IJobEntity 换名（ConfigureQueries + 自动 Schedule），没有新增能力
- **SystemBase** 只是加了生命周期钩子，ISystem 已有
- **SystemGraph** 如果只做注册+排序，用户手动管理也够用

真正缺失的是：**自动并行化**（Bevy Schedule）、**声明式开发**（消灭样板代码）、**变更追踪**（高性能刚需）。

### 4.1 复用已有 API

不要另起一套重复系统。已有：

- `ISystem`：生命周期钩子（OnCreate/OnUpdate/OnDestroy）。
- `SystemAPI`：当前只提供基础 Query 入口。
- `QueryEnumerable<T0,T1>`：已实现（S6）。
- `ChunkEnumerable<T0,T1>`：chunk 遍历已有基础。
- `EntJoy.SourceGenerator`：已有多个生成器。
- `DeferredCommandBuffer`：帧末 Playback（Phase 3）。

### 4.2 Schedule Graph — 自动并行化（参考 Bevy Schedule）

**核心价值**：用户不再手动 Schedule/Complete，框架自动分析冲突、并行执行。

```csharp
// 注册 System + 声明读写
world.AddSystem<MovementSystem>(
    reads: typeof(Velocity),
    writes: typeof(Position)
);
world.AddSystem<RenderingSystem>(
    reads: typeof(Position), typeof(Sprite)
);

// 世界更新时：自动分析冲突 → 构建执行计划 → 并行执行
world.Update();
// 内部：
//   Batch 1: [MovementSystem ‖ RenderingSystem]  ← 无冲突，并行
//   SyncPoint
//   Batch 2: [CollisionSystem]                    ← 需要 Position Read 完成
```

**冲突分析规则**：两个 System 冲突条件 = 一方 Writes ∩ 另一方 (Reads ∪ Writes) ≠ ∅。

**文件：** `ScheduleGraph.cs`、`SystemDescriptor.cs`

### 4.3 Entity Builder — 实体构造器（消灭样板代码）

```csharp
// 现在：CreateEntities + typeof 数组
world.CreateEntities(100, typeof(Position), typeof(Velocity), typeof(Health));

// 如果能这样：
var entity = world.Spawn()
    .With(new Position { X = 1, Y = 2 })
    .With(new Velocity { X = 0, Y = 1 })
    .With(new Health { Value = 100 })
    .Build();

// 批量创建：
var horde = world.Spawn()
    .With(new Position())
    .With(new Velocity())
    .Repeat(1000)  // 创建1000个相同组件的实体
    .Build();
```

**文件：** `EntityBuilder.cs`

### 4.4 Declarative Components — 声明式组件（消灭样板代码）

```csharp
// 现在要写：
struct Health : IComponentData { public int Value; }
// 还要手动注册 ComponentType

// 如果能这样：
[ECSComponent]
partial struct Health { public int Value; }
// 编译时自动生成：
//   1. ComponentType 注册
//   2. 序列化支持
//   3. 调试显示
```

**文件：** SourceGenerator 新增 `ComponentGenerator.cs`

### 4.5 变更追踪 — ChangedThisFrame（高性能刚需）

```csharp
// 只查询"这帧变化过的"实体
foreach (var (e, pos) in world.Query<Position>().ChangedThisFrame()) {
    // 只有 Position 在这帧被修改过的实体
    MarkDirty(e);
}

// 实现原理：每个 chunk 维护 version bitmask
// SetValue 时置位 → 查询时只遍历置位的 chunk
```

**文件：** `Chunk.cs` 新增 version bitmask、`QueryEnumerable.cs` 新增 ChangedThisFrame 过滤

### 4.6 空闲跳过 — RunWhen（简单有效）

```csharp
// 系统声明：我只在有 DamageEvent 时才跑
[RunWhen(typeof(DamageEvent))]
class DamageSystem : ISystem { }

// 没有 DamageEvent → 整个系统跳过，零开销
// 有 DamageEvent → 正常执行
```

**文件：** `World.cs` 新增条件检查逻辑

### 文件变更总结

**新建：** `ScheduleGraph.cs`、`SystemDescriptor.cs`、`EntityBuilder.cs`

**修改：** `World.cs`（新增 `AddSystem` / `Update`）、`Chunk.cs`（新增 version bitmask）、`QueryEnumerable.cs`（新增 ChangedThisFrame）、SourceGenerator 新增 `ComponentGenerator.cs`

### 风险

- Schedule Graph 的冲突分析必须覆盖所有组件类型，遗漏会导致竞态。
- 变更追踪的 version bitmask 增加每 chunk 16 字节开销（64 bits × 2 bytes）。
- 声明式组件需要 SourceGenerator 支持，AOT/Godot .NET 下需验证。

---

## Phase 5：易用性基础设施

### 5.1 World Events

- 使用 typed event channel。
- 默认事件为 struct，避免 `object` 装箱。
- 订阅只允许启动期或安全边界注册。

### 5.2 One-Frame Components

> **已移入 Phase 4 (S9-new)**：One-Frame Component 是 System 调度的自然配套，与 Observer 一起作为 Phase 4 的能力增强。见 Phase 4.4。

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

## Phase 9：Managed 类型与 NativeTranspiler 突破

### 9.1 核心问题

用户想要：
1. 使用 string/List<T> 等托管类型（保留 C# 习惯）
2. 不学 NativeCollection（降低学习成本）
3. 非托管部分保持 C++ 性能

当前限制：
- NativeTranspiler 完全禁止 managed 类型参与 C++ 执行
- Job struct 中 managed 字段 → void* 8B 零填充，Execute 不能访问

### 9.2 GCHandle.Pinned 方案分析

```
单个 Pin+Unpin：~200ns
100K 实体 × 200ns = 20ms ❌ 不可接受

堆碎片化风险：
  同时 Pin 100K 个对象 → 堆严重碎片化
  GC 无法压缩 pinned 区域 → 长期内存泄漏
```

**结论**：仅适用于单个对象调试，不适用于批量执行。

### 9.3 托管回调方案分析

```
单次回调（CLR 过渡）：~120ns
100K 实体 × 120ns = 12ms ❌ 太慢

批量回调（一次过渡，处理所有）：~120ns
100K 实体 × 0.12ns = 0.012ms ✅ 可接受
```

**推荐：批量回调 + 延迟执行**

### 9.4 分层执行 vs JobSystem

```
问题：JobSystem 调度 Job 到 Worker Thread
  拆分 Job 为 C++ 和 C# 两部分：
    C++ 部分：Worker Thread 执行
    C# 部分：在哪执行？
      - 主线程：阻塞主线程 ❌
      - Worker Thread：managed 代码在 worker 上（可行但复杂）

结论：不要拆分 Job，让 Job 自己处理 managed 和 unmanaged 部分
```

### 9.5 推荐方案：混合 Job + 指针数组

```csharp
// 用户写：
[NativeTranspile]
struct PlayerJob : IJobEntity {
    public float dt;
    public NativeArray<float> health;   // unmanaged → C++ SIMD
    public NativeArray<float> speed;    // unmanaged → C++ SIMD
    [ManagedField] public string Name;  // managed → 指针数组
}

// C# 侧：GCHandlePool + 批量 Pin
var pool = new GCHandlePool(maxPinned: 1024);
var handles = new GCHandle[count];
var pointers = new IntPtr[count];

// 分批 Pin
for (int i = 0; i < count; i += batchSize) {
    int end = Math.Min(i + batchSize, count);
    for (int j = i; j < end; j++) {
        handles[j] = pool.Pin(Encoding.UTF8.GetBytes(names[j]));
        pointers[j] = handles[j].AddrOfPinnedObject();
    }
    // C++ 处理本批次
    NativeBindings.ExecuteBatch(jobPtr, i, end - i, pointers, i);
}

// 分批 Unpin
foreach (var h in handles) pool.Unpin(h);
```

```cpp
// C++ 生成：
extern "C" void ExecuteBatch(
    JobData* data, 
    int startIndex, 
    int count,
    const IntPtr* namePointers,   // 指针数组
    int pointerOffset
) {
    for (int i = 0; i < count; i++) {
        // unmanaged：直接 SIMD
        data->health[startIndex + i] += data->speed[startIndex + i] * dt;
        
        // managed：直接指针访问（零拷贝）
        const char* name = (const char*)namePointers[pointerOffset + i];
        if (name != nullptr && memcmp(name, "Boss", 4) == 0) {
            data->health[startIndex + i] -= 100;
        }
    }
}
```

### 9.6 托管字段偏移量分析

**NativeTranspiler 能否编译时知道偏移？**

| 类型 | 编译时可知？ | 原因 | 解决方案 |
|------|------------|------|---------|
| `string` | ✅ 可以 | 布局固定 | 硬编码偏移 |
| `List<T>` | ✅ 可以 | 内部结构固定 | 提取 _items 数组 |
| `float[]`, `int[]` | ✅ 可以 | 数组布局固定 | 硬编码偏移 |
| `NativeArray<T>` | ✅ 已有 | 已支持 | 已实现 |
| **自定义 class** | ❌ 不行 | CLR 可能重排字段 | 要求 `[StructLayout(Sequential)]` |

### 9.7 性能分析

**GCHandle 遍历开销**：

```
遍历指针数组：~1ns/元素（数组访问）
100K 实体：~0.1ms（可忽略）

C++ 处理：
  unmanaged 字段：SIMD 加速，~0.3ms/100K
  managed 字段：指针访问，~0.1ms/100K
  → C++ 依然快
```

**批次固定 + 解除耗时**：

| 批次大小 | Pin 耗时 | Unpin 耗时 | 总耗时 |
|---------|---------|-----------|--------|
| 100K | 10ms | 5ms | 15ms |
| 1K | 0.1ms | 0.05ms | 0.15ms |
| 100 | 0.01ms | 0.005ms | 0.015ms |

**关键**：分层 Pin 后，只有被访问的字段才 Pin，数量大幅减少。

### 9.8 GCHandlePool 解决碎片化

**碎片化原因**：同时 Pin 大量对象 → GC 无法压缩 → 堆碎片

**GCHandlePool 解决方案**：

```csharp
class GCHandlePool {
    private readonly int _maxPinned;      // 最大并发 Pin 数（如 1024）
    private int _currentPinned;           // 当前已 Pin 数量
    
    public GCHandle Pin(object obj) {
        if (_currentPinned >= _maxPinned) {
            // 达到上限，等待 GC 压缩
            GC.Collect(0, GCCollectionMode.Forced, false);
            _currentPinned = 0;
        }
        var handle = GCHandle.Alloc(obj, GCHandleType.Pinned);
        _currentPinned++;
        return handle;
    }
    
    public void Unpin(GCHandle handle) {
        handle.Free();
        _currentPinned--;
    }
}
```

**分批处理策略**：

```
总数据：100K 实体
批次大小：1024

批次 1：Pin 1024 → 处理 → Unpin → GC 压缩
批次 2：Pin 1024 → 处理 → Unpin → GC 压缩
...
批次 99：Pin 剩余 512 → 处理 → Unpin

总耗时：~15ms（与不分批相同）
但关键是：堆稳定，无碎片化
```

### 9.9 EnTT hashed_string 模式

```csharp
// EnTT：字符串在编译期转为 uint32_t
constexpr auto name = entt::hashed_string{"Player"};
// 比较：整数比较，O(1)

// EntJoy 可以这样用：
[Interned("Player")]
struct Player : IComponentData {
    [Interned("Name")] public string Name;  // 编译时转为 uint
    public int Health;
}

// NativeTranspiler 生成 C++：
struct Player {
    uint32_t nameId;    // 编译期哈希
    int32_t health;
};
```

### 9.7 实施路径

| 阶段 | 内容 | 工时 | 价值 |
|------|------|------|------|
| **Phase 9.1** | GCHandle + 延迟反序列化（批量回调） | 1-2 周 | ⭐⭐⭐⭐⭐ |
| **Phase 9.2** | 字符串驻留（[Interned] → uint32_t） | 3-5 天 | ⭐⭐⭐⭐⭐ |
| **Phase 9.3** | 混合内核（NativeTranspiler 自动生成 accessor） | 1-2 周 | ⭐⭐⭐⭐ |
| **Phase 9.4** | 列压缩（字典 + 索引） | 1 周 | ⭐⭐⭐ |

### 9.8 风险

- GCHandle.Pinned 批量使用会导致堆碎片化
- 托管回调增加 CLR 过渡开销（批量可缓解）
- 混合 Job 增加 NativeTranspiler 复杂度
- 分层执行与 JobSystem 调度冲突（需要仔细设计）

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
Phase 1: 基础设施优化 ✅
Phase 2: Archetype Edges ✅
Phase 3: Selective Wait + ECB + Batch Operations ✅
Phase 4: System 调度与能力增强（重设计 2026-08）
  ├─ QueryEnumerator ✅
  ├─ Schedule Graph（自动并行）
  ├─ Observer（变化触发）
  ├─ One-Frame Component（帧事件）
  └─ Lambda 易用路径
```

该里程碑完成后，应能用 `IJobChunk` / `World.Update()` 跑通基础 ECS 流程，系统自动并行，组件变化可触发回调。

### 里程碑 B：存储与易用性

```text
Phase 2 遗留: Shared Component 语义
Phase 5: Events / Group / Context / DI
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
Phase 9.1: GCHandle + 延迟反序列化（批量回调）
Phase 9.2: 字符串驻留（[Interned] → uint32_t）
Phase 9.3: 混合内核（NativeTranspiler 自动生成 accessor）
Phase 9.4: 列压缩（字典 + 索引）
```

该轨道独立进行，不阻塞 A/B/C。

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

---

## 11. AOT 兼容性问题（2026-08 审计）

> 本节记录当前项目的 AOT 不兼容反射用法，需要修复。

### 11.1 问题概述

当前项目有 **3 处严重的 AOT 不兼容反射用法**，会导致 iOS/主机/Godot AOT 编译失败。

### 11.2 问题详情

#### 问题 1：NativeJobCore.cs (line 748-753)

```csharp
// 问题代码：
var create = typeof(NativeJobCore)
    .GetMethod(nameof(CreateParallelForBatchCallback), BindingFlags.Static | BindingFlags.NonPublic)
    .MakeGenericMethod(typeof(T))  // ❌ AOT 不兼容：动态泛型实例化
    .Invoke(null, null);           // ❌ AOT 不兼容：动态调用

// 解决方案：用 SourceGenerator 生成
[NativeTranspile]
struct MyJob : IJobParallelForBatch { ... }

// 生成：
public static class MyJob_BatchRunner {
    public static void Callback(IntPtr context, int start, int count) { ... }
}
```

#### 问题 2：NativeJobScheduler.cs (line 922-925)

```csharp
// 问题代码：
if (typeof(IJobParallelForBatch).IsAssignableFrom(typeof(T)))
    return _batchRunnerCache.GetOrAdd(typeof(T), t =>
        var f = typeof(BatchRunner<>)
            .MakeGenericType(t)  // ❌ AOT 不兼容：动态泛型构造
            .GetField("Runner"); // ❌ AOT 不兼容：反射获取字段

// 解决方案：用 SourceGenerator 生成具体类型
// 生成：
public static class BatchRunner_MyJob {
    public static BatchRunner Runner = new BatchRunner_MyJob();
}
```

#### 问题 3：EntityManager.cs (line 871-896)

```csharp
// 问题代码：
return typeof(EntityManager)
    .GetMethod(nameof(AddComponent))!
    .MakeGenericMethod(componentType)  // ❌ AOT 不兼容：动态泛型实例化
    .Invoke(this, new object[] { entity, boxedValue });  // ❌ AOT 不兼容：动态调用

// 解决方案：用 SourceGenerator 生成 switch 分发
switch (componentType.Id) {
    case 0: return AddComponent<Position>(entity, (Position)boxedValue);
    case 1: return AddComponent<Velocity>(entity, (Velocity)boxedValue);
    // ... 编译时生成所有 case
}
```

### 11.3 修复优先级

| 问题 | 严重性 | 修复方案 | 预估工时 |
|------|--------|---------|---------|
| `MakeGenericMethod` (EntityManager) | ⭐⭐⭐⭐⭐ | SourceGenerator 生成 switch 分发 | 3-5 天 |
| `MakeGenericType` (NativeJobScheduler) | ⭐⭐⭐⭐⭐ | SourceGenerator 生成具体类型 | 3-5 天 |
| `MakeGenericMethod` (NativeJobCore) | ⭐⭐⭐⭐⭐ | SourceGenerator 生成回调 | 3-5 天 |

### 11.4 AOT 兼容的反射用法（无问题）

| 用法 | 示例 | 说明 |
|------|------|------|
| `typeof(T).Name` | 多处 | 只是获取类型名字符串 |
| `typeof(T).IsAssignableFrom()` | ComponentTypeManager | 类型检查，不生成代码 |
| `typeof(T)` 作为参数 | 多处 | 编译时确定 |

### 11.5 影响范围

```
受影响平台：
  - iOS（AOT 编译）
  - 主机平台（PS5/Xbox/Switch）
  - Godot .NET AOT 模式

不受影响平台：
  - Windows/Linux/macOS（JIT 编译）
  - Android（部分 AOT）
```

### 11.6 修复建议

```
短期（Phase 4 之前）：
  1. 识别所有 AOT 不兼容代码
  2. 标记为 [UnsupportedOSPlatform] 或添加 AOT 兼容分支

中期（Phase 4-5）：
  1. 用 SourceGenerator 替换动态反射
  2. 为 EntityManager 生成 switch 分发
  3. 为 NativeJobScheduler 生成具体类型

长期（Phase 9）：
  1. 确保所有新代码 AOT 兼容
  2. 移除所有动态反射
```

---

## 12. 关系型状态机设计（2026-08 脑暴）

> 本节分析如何用 ECS 关系实现高性能状态机，避免 Add/Remove Component 的结构变更开销。

### 12.1 传统状态机的问题

```
传统做法：
  Entity 获得 WalkingState 组件 → AddComponent（结构变更）
  Entity 移除 IdleState 组件 → RemoveComponent（结构变更）

每次结构变更：
  1. Archetype 迁移（移动数据到新 chunk）~10μs
  2. Selective Wait（等待运行中的 Job）~100μs
  3. 新旧 Archetype 查找 ~1μs
  4. Chunk 分配/回收 ~1μs

100K 实体状态转换：
  100K × 112μs = 11.2s ❌ 完全不可接受
```

### 12.2 关系型状态机设计

**核心思想**：状态是独立的实体，实体与状态的关系 = 当前状态

```csharp
// 1. 定义状态实体
var idleState = world.Spawn()
    .With(new StateData { Name = "Idle", Duration = 0 })
    .With(new AnimationClip { Name = "idle_anim" })
    .Build();

var walkingState = world.Spawn()
    .With(new StateData { Name = "Walking", Speed = 2.0f })
    .With(new AnimationClip { Name = "walk_anim" })
    .Build();

// 2. 实体关联到状态
world.AddRelationship(playerEntity, idleState, new InState());

// 3. 状态转换（零结构变更）
world.RemoveRelationship(playerEntity, idleState);
world.AddRelationship(playerEntity, walkingState, new InState());

// 4. 查询：获取所有在 Idle 状态的实体
foreach (var (e, stateData) in world.Query<StateData>()
    .WithRelationship<InState>(idleState)) {
    // e 是所有在 Idle 状态的实体
}
```

### 12.3 关系型状态机的优势

| 优势 | 说明 |
|------|------|
| **零结构变更** | 状态转换只是修改关系，不触发 Archetype 迁移 |
| **状态可携带数据** | 状态是实体，可以有自己的 Component |
| **共享状态数据** | 多个实体可以共享同一个状态实例 |
| **状态历史** | 通过关系链记录状态转换历史 |
| **状态继承** | 状态可以继承自父状态 |
| **并行状态** | 实体可以同时处于多个状态（Movement + Animation） |
| **状态图** | 可以定义状态转换规则 |

### 12.4 性能分析

```
传统状态机（Add/Remove Component）：
  状态转换：~112μs（结构变更）
  100K 实体：~11.2s ❌

关系型状态机（修改关系）：
  状态转换：~100ns（修改关系）
  100K 实体：~10ms ✅

性能提升：1000x
```

### 12.5 实施建议

```
Phase 6（关系）完成后的扩展：
  1. 实现 InState 关系类型
  2. 提供 StateMachine 辅助类
  3. 支持状态历史、继承、并行状态
  4. 与 Phase 5（Events）集成，支持状态事件

性能目标：
  状态转换：< 100ns/实体
  100K 实体状态转换：< 10ms
```

---

## 13. 现代化 ECS 开发与设计（2026-08 探索）

### 13.1 现代化 ECS 设计原则

```
传统 ECS：
  - 命令式编程（告诉计算机怎么做）
  - 手动管理状态
  - 手动优化性能

现代化 ECS：
  - 声明式编程（告诉计算机想要什么）
  - 自动管理状态
  - 自动优化性能
```

### 13.2 声明式编程

```csharp
// 传统：命令式
public class MovementSystem : ISystem {
    public void OnUpdate() {
        foreach var (e, pos, vel) in world.Query<Position, Velocity>()) {
            pos.X += vel.X * dt;
            pos.Y += vel.Y * dt;
        }
    }
}

// 现代化：声明式
[UpdateEveryFrame]
[Query<Position, Velocity>]
public void Move(ref Position pos, in Velocity vel) {
    pos.X += vel.X * dt;
    pos.Y += vel.Y * dt;
}

// 框架自动生成：
// 1. System 类
// 2. 查询过滤器
// 3. 调度逻辑
// 4. 性能优化
```

### 13.3 响应式编程

```csharp
// 传统：轮询
public class HealthSystem : ISystem {
    public void OnUpdate() {
        foreach var (e, health) in world.Query<Health>()) {
            if (health.Value <= 0) {
                world.DestroyEntity(e);
            }
        }
    }
}

// 现代化：响应式
world.Query<Health>()
    .Where(h => h.Value <= 0)
    .OnMatch((entity, health) => {
        world.DestroyEntity(entity);
    });
```

### 13.4 EntJoy 现代化 ECS 路线图

```
Phase 1-3（已完成）：
  ✅ 基础设施优化
  ✅ Archetype Edges
  ✅ Selective Wait

Phase 4（设计完成）：
  🔲 Schedule Graph（自动并行）
  🔲 Entity Builder（实体构造器）
  🔲 Declarative Components（声明式组件）
  🔲 变更追踪
  🔲 空闲跳过

Phase 5-6（设计完成）：
  🔲 关系型状态机
  🔲 事件总线
  🔲 World Events

Phase 7-8（设计完成）：
  🔲 SourceGenerator 扩展
  🔲 ECS 代码生成器
  🔲 ECS 性能优化器

Phase 9（设计完成）：
  🔲 托管类型支持
  🔲 GCHandlePool
  🔲 NativeTranspiler 突破

工具链（设计完成）：
  🔲 Godot 场景导入
  🔲 内存分析器
  🔲 性能分析器
  🔲 数据导航工具
```

---

## 14. EntJoy 定位与聚焦（2026-08 修正）

### 14.1 EntJoy 核心定位

```
EntJoy = 高性能无头 ECS 框架
  - 不是游戏引擎
  - 不是可视化工具
  - 是提供给游戏引擎和 .NET 生态的高性能 ECS 核心

核心价值：
  1. 高性能 ECS 核心（Archetype、Chunk、Query）
  2. NativeTranspiler（C# → C++/ISPC 自动编译）
  3. ChunkPool（内存管理、指针稳定性）
  4. JobSystem（多线程调度）
```

### 14.2 聚焦的创新方向

| 功能 | 创新度 | 实现难度 | 价值 | 符合定位 |
|------|--------|---------|------|---------|
| **Schedule Graph** | ⭐⭐⭐⭐ | 中 | ⭐⭐⭐⭐⭐ | ✅ |
| **Entity Builder** | ⭐⭐⭐ | 低 | ⭐⭐⭐⭐ | ✅ |
| **变更追踪** | ⭐⭐⭐⭐ | 中 | ⭐⭐⭐⭐⭐ | ✅ |
| **Auto-SIMD 增强** | ⭐⭐⭐⭐⭐ | 中 | ⭐⭐⭐⭐⭐ | ✅ |
| **托管类型支持** | ⭐⭐⭐⭐⭐ | 高 | ⭐⭐⭐⭐⭐ | ✅ |
| **Godot 场景桥接** | ⭐⭐⭐ | 低 | ⭐⭐⭐⭐ | ✅ |
| **AOT 兼容** | ⭐⭐⭐ | 中 | ⭐⭐⭐⭐⭐ | ✅ |

### 14.3 不需要的（偏离定位）

| 功能 | 原因 |
|------|------|
| ❌ ECS DSL | 太重，用户可以直接用 C# |
| ❌ 可视化编辑器 | 游戏引擎自己有 |
| ❌ 多人协作 | 不是框架的职责 |
| ❌ AI 代码生成 | 太超前，不实用 |
| ❌ 关卡编辑器 | 游戏引擎自己有 |

### 14.4 核心原则

```
EntJoy 的核心价值：
  1. 高性能（C++/ISPC 编译）
  2. 易用（C# API）
  3. 兼容（.NET 生态）
  4. 无头（不绑定特定引擎）
```

---

## 15. 实用创新功能（2026-08 聚焦）

### 15.1 核心痛点（必须解决）

| 痛点 | 解决方案 | 可行性 | 实用性 |
|------|---------|--------|--------|
| Component 定义太啰嗦 | 零接口 Component | ✅ 高 | ⭐⭐⭐⭐⭐ |
| 查询太啰嗦 | 查询缓存 | ✅ 高 | ⭐⭐⭐⭐⭐ |
| 手动 Schedule | 自动调度 | ✅ 高 | ⭐⭐⭐⭐⭐ |
| 手动 SIMD | 自动 SIMD | ✅ 高 | ⭐⭐⭐⭐⭐ |
| 手动并发控制 | 自动并发 | ✅ 高 | ⭐⭐⭐⭐⭐ |
| 每帧遍历所有实体 | 变更追踪 | ✅ 高 | ⭐⭐⭐⭐⭐ |

### 15.2 开发痛点（应该解决）

| 痛点 | 解决方案 | 可行性 | 实用性 |
|------|---------|--------|--------|
| 实体创建太啰嗦 | Entity Builder | ✅ 高 | ⭐⭐⭐⭐ |
| Component 访问不安全 | 安全访问 | ✅ 高 | ⭐⭐⭐⭐ |
| 批量操作太慢 | 批量操作 | ✅ 高 | ⭐⭐⭐⭐ |
| 调试困难 | 增强错误 | ✅ 高 | ⭐⭐⭐⭐ |

### 15.3 集成痛点（应该解决）

| 痛点 | 解决方案 | 可行性 | 实用性 |
|------|---------|--------|--------|
| AOT 不兼容 | 完全 AOT 兼容 | ✅ 高 | ⭐⭐⭐⭐⭐ |
| 后端切换困难 | 运行时切换 | ✅ 高 | ⭐⭐⭐⭐ |
| 迁移困难 | 迁移工具 | ✅ 中 | ⭐⭐⭐⭐⭐ |

### 15.4 不实用的（放弃）

| 功能 | 原因 |
|------|------|
| ❌ IDE 功能 | 这是 IDE 的职责 |
| ❌ 交互式文档 | 不是框架核心 |
| ❌ 代码模板 | 编辑器功能 |
| ❌ 重构工具 | 编辑器功能 |
| ❌ 自动文档 | Nice to have，不是必须 |

---

## 16. 突破 Blittable 瓶颈：最终架构（2026-08 深入）

### 16.1 各框架处理托管类型的对比

| 阵营 | 框架 | 方式 | 代价 |
|------|------|------|------|
| **纯托管 C#** | Arch、Morpeh、Friflo class | string/List/class 直接进组件 | 无 C++ 加速 |
| **Rust** | Bevy | 任意类型进 archetype 列 | 零 GC，C# 做不到 |
| **混合** | Unity DOTS | managed 只主线程，Job 不碰 | 用户手写 FixedString/NativeList |

**验证来源**：
- Unity: [Managed components in parallel jobs](https://discussions.unity.com/t/managed-components-in-parallel-jobs/903590/3) — 并行 Job 不碰托管
- Friflo: [component types](https://github.com/friflo/Friflo.Engine.ECS) — struct/class 分开存
- 对比表: [CSharpECSComparison](https://github.com/Chillu1/CSharpECSComparison#1)

### 16.2 分层字段路由

| 层 | 字段类型 | Job 内操作 | 方式 |
|----|---------|-----------|------|
| L0 | Blittable（float/int/Vector3） | ✅ 1.7ns | SoA / 直接指针 |
| L1 | string | ✅ int 比较 | Interned ID |
| L2 | List<blittable> 元素 | ✅ 直接读写 | Pin backing array（有界） |
| L3 | Dictionary/嵌套 class 图 | ❌ 主线程 C# | 回调法 / 同步点批量 |

### 16.3 最终架构

```
Job 内（并行 C++，纯指针）：
  Blittable + interned int → SIMD
  List backing array → 直接指针（有界 Pin）

Complete()（同步点）← Unity DOTS 行业验证的标准模式

主线程 C#（托管访问的物理上限）：
  Dictionary / 嵌套 class / string 修改 → 纯 C# 速度
```

- 当 v2 与 v3 冲突时，以 v3 为准。
