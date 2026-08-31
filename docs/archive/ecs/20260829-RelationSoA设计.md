# Relation SoA 设计（S23 + S24 + Q1/Q2，2026-08-29）

> **范围**：Phase 6 实体关系 —— S23 Relation SoA 编码 + 关系查询过滤 + S24 级联删除 + target index + Q1 查询进阶 + Q2 IJobEntity 步长修复。
> **状态**：S23 ✅ + S24 ✅ + Q1 ✅ + Q2 ✅（2026-08-29）。
> **决策**：路径 A（单实例 SoA 列）+ 预留 B（多实例）扩展点。见 §6。
> **前置**：`ecs-evolution-plan-v3.md` §6、`Phase优先级分析与实施路线.md` §8.3。

---

## 一、目标与约束

### 1.1 目标

- 实体间关系（父子、链接、状态）作为 **SoA 列**存储于 Chunk，**不拆 Archetype**（对齐 Bevy 0.15 / Flecs 非碎片化路线）。
- target 编码带 **version/epoch**，防止实体 ID 回收后关系指向错误的新实体。
- 提供 `AddRelationship / RemoveRelationship / GetRelationship / HasRelationship / WithRelationship<T>(target)` 查询过滤。
- 零新存储机制：复用现有组件列（ComponentOffsets / ComponentSizes / GetComponentArrayPointer）。

### 1.2 约束（来自 §8.3 三条关键约束）

| # | 约束 | 落地 |
|---|------|------|
| 1 | 关系列的 target 必须含 version/epoch 防 ID 回收 | RelationSlot.TargetVersion |
| 2 | 级联删除需要 target index（targetEntity → relation list），不能扫描所有关系列 | S24（本设计预留接口，不实现） |
| 3 | 关系查询不能 O(N) 遍历所有实体，需按 Archetype 分组 + 索引加速 | WithRelationship 过滤在 chunk 收集期做，QueryKey 指纹化（复用查询缓存共享） |

---

## 二、存储模型（路径 A：单实例 SoA 列）

### 2.1 核心洞察

**同类型组件在 Archetype 签名中是集合语义**——一个实体不可能拥有两个相同类型的组件。因此：

> **单实例关系 = 普通组件列**，关系类型 T 就是组件类型，实体上 `ChildOf` 列槽位就是该实体的关系 target。

```
Chunk 布局（与普通组件完全一致）：
[Entity array] [Position column] [ChildOf column (RelationSlot)]
               ↑ 连续            ↑ 连续，每槽 8B
```

- 关系类型：`public struct ChildOf : IRelationComponent {}`（空 struct，标记关系语义）
- 列值类型：`RelationSlot`（8B blittable）
- 关系类型注册：与组件一致（`ComponentTypeManager.GetComponentType(typeof(ChildOf))`）
- 实体上无关系时：槽位为 `RelationSlot.Default`（TargetId=-1）——**不是结构变更**，只是列值默认

### 2.2 RelationSlot 编码（8B）

```csharp
/// <summary>关系槽位值：指向 target 实体（含版本防 ID 回收）。8B blittable。</summary>
public unsafe struct RelationSlot
{
    public int TargetId;       // 目标实体 ID（-1 = 无关系）
    public int TargetVersion;  // 目标实体版本（回收校验：Id 匹配且 Version 匹配才有效）

    public static readonly RelationSlot Default = new() { TargetId = -1, TargetVersion = -1 };

    public readonly bool IsValid => TargetId >= 0;
    public readonly bool Matches(Entity target) => TargetId == target.Id && TargetVersion == target.Version;
    public static RelationSlot From(Entity target) => new() { TargetId = target.Id, TargetVersion = target.Version };
    public readonly Entity ToEntity() => new() { Id = TargetId, Version = TargetVersion };
}
```

- **Version 防回收**：实体销毁后 Id 可能被复用（version+1）。关系列存 target 创建时的 version，查询/读取时校验 `TargetVersion == 现 version`；不匹配即视为失效（关系指向"已死"实体）。

### 2.3 为什么不是 §6.1 的 12B 三字段

§6.1 建议 `relationTypeId + targetEntityId + targetVersion` 针对的是**单列多关系类型**场景（Bevy 早期）。本设计采用**每关系类型一列**（关系类型 = 组件类型），所以：

- `relationTypeId` 冗余——关系类型就是列本身（`ChildOf` 列只存 ChildOf 关系）
- 列宽 8B vs 12B：8B 对齐友好（cache line 利用率 +33%）

§6.1 的 12B 方案保留为 B 扩展点的候选（多实例时 relationTypeId 变冗余，仍需 per-slot 索引，见 §6）。

---

## 三、API 面（World / EntityManager 扩展）

```csharp
// ─── 关系类型标记接口 ───
public interface IRelationComponent { }   // 空接口，仅标记"此组件类型用于关系"

// ─── 关系操作（EntityManager）───
public void AddRelationship<TRel>(Entity entity, Entity target) where TRel : struct, IRelationComponent;
public void RemoveRelationship<TRel>(Entity entity) where TRel : struct, IRelationComponent;
public Entity GetRelationship<TRel>(Entity entity) where TRel : struct, IRelationComponent;   // 无关系 → default
public bool HasRelationship<TRel>(Entity entity) where TRel : struct, IRelationComponent;

// ─── 查询过滤（QueryBuilder / World.Query）───
// 只匹配"持有 TRel 关系且 target == specified"的实体
world.Query<Position>()
     .WithRelationship<ChildOf>(parentEntity);

// ─── World 便捷入口 ───
world.AddRelationship<ChildOf>(child, parent);
world.GetRelationship<ChildOf>(child);   // → parent
```

### 3.1 Add/Remove 语义

| 操作 | 行为 |
|------|------|
| `AddRelationship<TRel>(entity, target)` | 实体无 TRel 组件 → 结构变更（AddComponentRaw，走 Archetype Edges 快路径）；已有 → 原地 SetRaw 更新 RelationSlot（零结构变更） |
| `RemoveRelationship<TRel>(entity)` | 有 TRel 组件 → 移除组件（RemoveComponentRaw，走 edge 快路径）；无 → no-op |
| `GetRelationship<TRel>(entity)` | 无组件 → default(Entity)；有 → 读 RelationSlot，Version 校验，失效 → default |
| `HasRelationship<TRel>(entity)` | 无组件 → false；有组件但槽位 TargetId=-1 → false；TargetId≥0 且 version 匹配 → true |

### 3.2 与现有机制的融合

- **结构变更**：Add/Remove 完全复用 `AddComponentRaw`/`RemoveComponentRaw`（已含 Job 等待、锁、Archetype Edges、Observer 派发）。
- **SetRaw 更新**：已有关系时只写 8B，走 `SetRaw`（含 Change Tracking 标记）。
- **NativeTranspiler**：RelationSlot 是纯 blittable struct，IJobChunk 可直接读（与普通组件同路径，零翻译器改动）。

---

## 四、查询过滤：WithRelationship<T>(target)

### 4.1 QueryBuilder 扩展

```csharp
public partial struct QueryBuilder
{
    public ComponentType RelationshipFilterType;   // 关系组件类型（default = 无过滤）
    public RelationSlot RelationshipFilterTarget;  // 目标 RelationSlot（存 target.Id + target.Version）
    public bool HasRelationshipFilter;
}

public QueryBuilder WithRelationship<T>(Entity target) where T : struct, IRelationComponent
{
    RelationshipFilterType = ComponentTypeManager.GetComponentType(typeof(T));
    RelationshipFilterTarget = RelationSlot.From(target);
    HasRelationshipFilter = true;
    return this;
}
```

### 4.2 匹配语义

- **Archetype 匹配**（`Archetype.IsMatch`）：`HasRelationshipFilter` 时要求 `Has(RelationshipFilterType)`（拥有该关系组件列）。**零碎片化：不参与 Archetype 签名拆解，只是过滤条件。**
- **Chunk 收集过滤**（`EntityQuery.RefreshIncremental` / `QueryEnumerable.MoveNext`）：逐槽校验 `RelationSlot.Matches(target)`——Id 相等 + Version 相等才命中。
- **QueryKey 指纹**：`RelationshipFilterType.Id + target.Id + target.Version` 参与 QueryKey 计算，使同 target 查询共享缓存实例（复用 S20 查询缓存共享机制）。

### 4.3 性能特征

| 操作 | 成本 |
|------|------|
| Archetype 匹配（拥有 TRel 列） | O(1) 字典查（componentTypeRecorder） |
| 逐槽 RelationSlot 校验 | 8B 比较 ×2（Id + Version），SIMD 不适用（比较后跳过），但连续内存遍历 cache 友好 |
| 查询缓存 | QueryKey 指纹化，同 (TRel, target) 复用实例，零重复全量重扫 |

**性能目标**（S23 验收基准）：
- 10 万实体 `AddRelationship`：首个实体结构变更（创建带 TRel 列的 Archetype）+ 其余 SetRaw 写 8B，目标 < 5ms
- `GetRelationship`：O(1)（直接索引 + 列读）
- `WithRelationship` 查询：只扫匹配 Archetype 的 chunk，逐槽 8B 比较，10 万实体目标 < 2ms

---

## 五、S24：级联删除 + target index（✅ 已完成 2026-08-29）

### 5.1 设计决策（用户确认）

| 决策点 | 选择 |
|--------|------|
| API 形态 | **显式 `DestroyEntityCascade(entity)`**；现有 `DestroyEntity` 保持不级联（不破坏旧语义） |
| 递归深度 | **递归**（销毁整棵子树：子实体的子实体也销毁） |
| 关系类型范围 | **全部关系类型**（ChildOf + InState 等所有指向该实体的关系都级联） |
| 索引维护 | **主动维护**（Add/Remove/覆盖时同步），级联删除 O(1) 查索引 |

### 5.2 实现

```csharp
// RelationIndex（src/EntJoy.ECS/Relation/RelationIndex.cs）
// target.Id → (relTypeId → HashSet<Entity> sources)
//   Add / RemoveRelTypeId：O(1)（HashSet 增删）
//   TryGetSources(targetId)：O(1) 查表，返回按类型分组的 sources

// EntityManager.DestroyEntityCascade(entity)
//   1. CompleteActiveJobs（级联跨多 Archetype）
//   2. lock(_structuralLock)
//   3. CollectCascade(entity, ...)：DFS 收集整棵子树
//      - visited HashSet 防环（环状关系 a→b→c→a 不死循环）
//      - StillPointsTo 槽位校验：source 仍指向 target 才入队（防索引滞后误伤）
//   4. 逐实体 DestroyEntityCore（含 Observer Destroyed + 索引清理）
```

### 5.3 索引一致性

| 时机 | 处理 |
|------|------|
| AddRelationship 覆盖 | 先 `RemoveRelTypeId`（旧 target 条目）再 `Add`（新 target） |
| RemoveRelationship | `RemoveRelTypeId`（旧槽位值） |
| 标准 DestroyEntity | `CleanupSourceRelations` 遍历实体关系列清索引（hook 进 DestroyEntityCore） |
| DestroyEntityCascade | 每实体销毁时同上清理 |

### 5.4 性能

| 操作 | 成本 |
|------|------|
| AddRelationship（含索引维护） | 0.71 us/op（HashSet 增删 + 覆盖检查） |
| DestroyEntityCascade | 100 父 × 1000 子 = 66ms（0.66ms/父，含 1000 子销毁） |

> 注：AddRelationship 从 S23 的 0.36us 升到 0.71us（+0.35us）——这是 S24 主动索引的固有维护成本（覆盖检查 + HashSet 增删）。可接受；若未来需要极致 Add 速度，可改惰性索引（级联时重建，级联 O(N)），在 §5.5 权衡。

### 5.5 惰性 vs 主动索引（权衡记录）

| 方案 | Add 开销 | 级联删除 | 适用 |
|------|---------|---------|------|
| 主动（当前） | +0.35us/op | O(1) 查表 | 级联频繁/树深场景 |
| 惰性 | 零 | 级联时重建 O(关系数) | 级联罕见、Add 极热场景 |

当前选择主动维护，因为文档 §8.3 约束"级联删除必须走索引，不扫描所有关系列"，且主动索引让 `WithRelationship` 之外的"target → sources"反向查询也 O(1)（为后续关系查询/状态机铺路）。

---

## 六、扩展点预留（路径 B：多实例）

**当前不做，仅记录接口边界，避免 Phase 5（关系型状态机：状态历史/并行状态）返工。**

| 能力 | B 方案形态 | 与 A 的兼容 |
|------|-----------|------------|
| 一实体多个同类型关系（不同 target） | 关系行存储（per-entity relation list，不在组件列），或 `(RelationSlot + 索引)` 多槽列 | A 的 RelationSlot 保留；B 需要新存储面（RelationTable），API 面 `AddRelationship` 增加重载（多实例语义） |
| 状态历史 | B 天然支持（多次 Add 追加） | A 的 `AddRelationship` 是"覆盖"语义；B 是"追加"语义——**API 重载区分** |
| 并行状态 | 多个关系类型列（A 已天然支持：MovementState / AnimationState 各自一列） | ✅ 无需 B |

**决策**：S23 只实现 A（单实例覆盖语义）。若 Phase 5 需要多实例，新增 `RelationTable` 存储 + `AddRelationship<TRel>(entity, target, append: true)` 重载，A 的组件列路径不变。

---

## 七、验收方法（S23 完成判据）

| # | 验收项 | 方法 |
|---|--------|------|
| 1 | 不拆 Archetype | 断言：target=A 与 target=B 的关系实体同属一个 Archetype；`Archetype.ComponentCount` 不含 target 维度 |
| 2 | 列连续性 | 单测：同一 chunk 内 `GetComponentArrayPointer(TRelIdx)` 连续，`slot * 8B` 索引 O(1) |
| 3 | Version 防 ID 回收 | 单测：target 销毁后（Id 复用、Version+1），`GetRelationship` 返回 default；`WithRelationship` 不命中 |
| 4 | 零结构变更覆盖更新 | 单测：已有关系再 `AddRelationship` 不触发结构变更（`StructuralVersion` 不变） |
| 5 | 查询过滤正确性 | 单测：A/B 两个 target 的实体混合，`WithRelationship<ChildOf>(A)` 只返回 A 的子实体 |
| 6 | 性能基线 | 基准：10 万实体 AddRelationship / GetRelationship / WithRelationship 查询耗时记录 |
| 7 | 回归 | `EntJoy.ECS.Tests` 全量通过 + `EntJoySample` 编译通过 |

---

## 八、实施清单

| 步骤 | 内容 | 文件 | 状态 |
|------|------|------|------|
| 1 | `IRelationComponent` 标记接口 + `RelationSlot` 结构（8B） | `src/EntJoy.ECS/Relation/RelationSlot.cs` | ✅ |
| 2 | QueryBuilder `WithRelationship<T>` + 过滤字段 | `src/EntJoy.ECS/Query/QueryBuilder.cs` | ✅ |
| 3 | Archetype.IsMatch 支持 RelationshipFilter | `src/EntJoy.ECS/Archetype/Archetype.cs` | ✅ |
| 4 | QueryKey 指纹加入关系过滤（TRel.Id + target.Id + target.Version） | `src/EntJoy.ECS/Query/EntityQuery.cs` | ✅ |
| 5 | QueryEnumerator per-slot 关系过滤（含 target 存活校验） | `src/EntJoy.ECS/Query/QueryEnumerable.cs` | ✅ |
| 6 | EntityManager Add/Remove/Get/HasRelationship | `src/EntJoy.ECS/Entity/EntityManager.Relation.cs` | ✅ |
| 7 | World 便捷入口 | `src/EntJoy.ECS/World/World.Relation.cs` | ✅ |
| 8 | ComponentTypeManager 关系类型 Size 强制 8B + IsRelation 标记 | `src/EntJoy.ECS/Component/ComponentTypeManager.cs` + `ComponentType.cs` | ✅ |
| 9 | 单元测试（S23：11 项） | `tests/EntJoy.ECS.Tests/RelationTests.cs` | ✅ 11/11 |
| 10 | 性能基准（S23） | `samples/EntJoySample/09_ECS/Benchmarks/RelationBenchmark.cs` | ✅ |
| 11 | **S24** `RelationIndex`（target→sources，HashSet O(1)） | `src/EntJoy.ECS/Relation/RelationIndex.cs` | ✅ |
| 12 | **S24** `DestroyEntityCascade`（DFS 递归 + 防环 + 槽位校验） | `src/EntJoy.ECS/Entity/EntityManager.Relation.cs` | ✅ |
| 13 | **S24** 索引 hook：Add/Remove/覆盖/标准 Destroy 清理 | `EntityManager.Relation.cs` + `EntityManager.cs`（DestroyEntityCore） | ✅ |
| 14 | **S24** 级联测试（7 项：基础/递归/防环/索引清理/旧语义/World） | `tests/EntJoy.ECS.Tests/CascadeTests.cs` | ✅ 7/7 |

---

## 九、验收结果（2026-08-29）

| # | 验收项 | 结果 |
|---|--------|------|
| 1 | 不拆 Archetype | ✅ `Relation_DoesNotSplitArchetype_ByTarget`：target 不同实体同 Archetype |
| 2 | 列连续性 | ✅ `RelationSlot` 8B 列走标准 `GetComponentArrayPointer`，槽位索引 O(1) |
| 3 | Version 防 ID 回收 | ✅ `Relationship_TargetDestroyed_BecomesInvalid` / `Relationship_TargetIdRecycled_VersionMismatch_Invalid` |
| 4 | 零结构变更覆盖更新 | ✅ `AddRelationship_AlreadyHas_NoStructuralChange`：StructuralVersion 不变 |
| 5 | 查询过滤正确性 | ✅ `WithRelationship_Query_ReturnsOnlyMatchingTarget`（10/10 精确）+ `WithRelationship_Query_AfterTargetDestroy_Empty` |
| 6 | 性能基线（Debug，10 万实体） | ✅ Add 0.71us/op（含索引维护）、Get 0.16us/op、Has 0.16us/op、WithRelationship 查询 ~1.06ms/10000 匹配 |
| 7 | 回归 | ✅ EntJoy.ECS.Tests 全量 77/77（70 原有 + 7 级联）+ EntJoySample 编译通过 |
| 8 | **S24** 级联删除 | ✅ `DestroyEntityCascade_DestroysDirectChildren` / `_Recursive` / `_CyclicRelation_Terminates` / `_CleansIndexEntries` / `_DoesNotCascade_ByDefault` / World 入口 |
| 9 | **S24** 非级联旧语义保持 | ✅ `DestroyEntity_DoesNotCascade_ByDefault`：普通销毁子实体保留，关系自动失效 |
| 10 | **S24** 级联性能 | ✅ 100 父 × 1000 子 = 66ms（0.66ms/父） |

### 9.1 性能基线数据（2026-08-29，Debug 构建）

| 操作 | 10 万次总耗时 | 单次 |
|------|--------------|------|
| AddRelationship（SetRaw 8B 覆盖 + 索引维护） | 71.40 ms | 0.714 us/op |
| GetRelationship | 16.31 ms | 0.163 us/op |
| HasRelationship | 16.20 ms | 0.162 us/op |
| WithRelationship 查询（10 父 × 1000 子） | 1.06 ms/query | 10000 匹配精确命中 |
| DestroyEntityCascade（100 父 × 1000 子） | 66.20 ms | 0.66 ms/父（含 1000 子销毁） |

> 注意：Get/Has 均含 target 存活 + version 校验（`GetEntityInfoRef` 间接层）。~~Release 构建预计再降 2-4x~~（**已修正，见下**）。
> Add 从 S23 的 0.362us 升至 0.714us = S24 主动索引维护成本（覆盖检查 + HashSet 增删），见 §5.5 权衡。

### 9.1b Release 实测对比与预测修正（2026-08-29 后，EntJoySample Release 构建）

| 操作 | Debug | Release | 提升 |
|------|-------|---------|------|
| CreateEntities 100000（带 ChildOf 列） | 52.55 ms | 44.81 ms | 1.17x |
| AddRelationship x100000 | 0.649 us/op | 0.601 us/op | 1.08x |
| GetRelationship x100000 | 0.126 us/op | 0.121 us/op | 1.04x |
| HasRelationship x100000 | 0.130 us/op | 0.115 us/op | 1.13x |
| WithRelationship query（10 父 × 1000 子） | 0.85 ms/query | 0.88 ms/query | ~1.0x |
| DestroyEntityCascade（100 父 × 1000 子） | 51.54 ms | 47.04 ms | 1.10x |
| GetRelationsOf x10000/iter | 50.2 us/iter | 50.6 us/iter | ~1.0x |
| GetAncestors（深链 10000） | 4.653 ms | 3.541 ms | 1.31x |
| GetDescendants（宽 10000） | 2.247 ms | 2.047 ms | 1.10x |
| GetSiblings（hub 10000 子，1000 次） | 111.1 us/op | 81.5 us/op | 1.36x |

**结论（修正 §9.1 预测）**：Release 相比 Debug 仅提升 **1.04~1.36x**，远低于此前预估的 2-4x。
**瓶颈定位**：提升最大的恰是分配最重的两项（GetSiblings 1.36x / GetAncestors 1.31x，均输出大数组 + List/HashSet 容器）；
提升最小的纯容器/结构变更主导项（GetRelationsOf / WithRelationship 查询 ~1.0x，AddRelationship 1.08x）。
→ **瓶颈是托管分配与 Dictionary/HashSet 容器开销，而非 JIT 差异**。优化应优先消除分配（见 §十三 P1）。

### 9.2 实现中发现并修复的问题

| 问题 | 根因 | 修复 |
|------|------|------|
| 关系列内存越界 | `ChildOf` 空 struct 注册为 1B 列，但写入 8B `RelationSlot` | ComponentTypeManager 对 IRelationComponent 强制 Size=8B |
| 查询在 target 销毁后仍命中 | 过滤是纯值匹配，销毁未复用时值仍相等 | 枚举器首次进入时校验过滤目标存活（一次），失效则整个查询空 |
| AddRelationship 8 倍退化（0.36→2.82us） | 索引用 `List<Entity>`，同 target 大量 sources 时 RemoveAt 遍历 O(n) | 改 `HashSet<Entity>`，增删 O(1)；2.82→0.71us |

---

## 十、Q2：IJobEntity/IJobChunk 关系访问（步长一致性修复）

### 10.1 问题

S23 关系类型定义为**空 struct**（`struct ChildOf : IRelationComponent {}`），列宽靠 ComponentTypeManager 强制 8B（存 RelationSlot）。但：

- `IJobEntity` 生成器把 `Execute(ref Position, in ChildOf)` 参数转成 `__chunk.GetComponentDataSpan<ChildOf>()`
- `GetComponentDataSpan<T>()` 用 `Unsafe.SizeOf<T>` = **空 struct 的 1B** 作步长
- 列实际步长 8B → **读写错位**，Job 内访问关系列读到垃圾/越界

S23 测试全过是因为框架内部用 `RelationSlot` 直读写列（不走 Span API），掩盖了此 bug。

### 10.2 修复

**关系类型必须含 `RelationSlot Target` 字段**（`Unsafe.SizeOf<TRel>` == 8B == 列宽，天然一致）：

```csharp
// 修复前（空 struct + 强制 8B hack）
public struct ChildOf : IRelationComponent { }

// 修复后（实存 8B，步长天然正确）
public struct ChildOf : IRelationComponent { public RelationSlot Target; }
```

ComponentTypeManager 保留 IsRelation 强制 8B（防空 struct 误用兜底），更新注释。

### 10.3 验证

六条路径均实测通过（8 孩子循环指向 4 父，expected=12）：

| 路径 | 机制 | 实测 |
|------|------|------|
| **IJobEntity（托管）** | adapter 生成 `GetComponentDataSpan<TRel>()` | sum=12==expected ✅ |
| **IJobChunk（托管）** | `chunk.GetComponentDataSpan<ChildOf>()` | sum=12==expected ✅ |
| **NativeTranspiler C++ IJobChunk** | `[NativeTranspile(Cpp)]` → C++ 编译 | sum=12==expected ✅ |
| **NativeTranspiler C++ IJobEntity** | `[NativeTranspile(Cpp)]` | sum=12==expected ✅ |
| **NativeTranspiler ISPC IJobChunk** | `[NativeTranspile(Ispc)]` → ISPC 编译 | sum=12==expected ✅ |
| **NativeTranspiler ISPC IJobEntity** | `[NativeTranspile(Ispc)]` | sum=12==expected ✅ |

### 10.4 NativeTranspiler 修复汇总（3 个通用 bug，实测暴露）

| Bug | 症状 | 修复 |
|-----|------|------|
| **嵌套字段类型 include 缺失（C++）** | `ChildOf.h` 引用 `EntJoy::ECS::RelationSlot` 无定义 → C++ 编译失败 | `GenerateCppStructDefinition` 新增 `CollectNestedFieldIncludes`（递归收集字段类型头文件） |
| **嵌套字段类型 include 缺失（ISPC）** | ISPC include 只处理一层，深层嵌套缺定义 | `GenerateIspcStructDefinition` 的嵌套收集改为递归 |
| **ISPC 类型映射缺 int64 等** | `long Value` → `Int64`（.NET 类型名，ISPC 无效）→ ISPC 编译失败 | `MapCSharpTypeToIspc` 补 Int64/UInt64/Byte/SByte/Int16/UInt16/Char → int64/unsigned int64/int8/… |

**意义**：这 3 个是 NativeTranspiler 的通用缺陷（任何"组件引用嵌套 struct"或"组件含 long 字段"都会触发），非关系特有。修复后**所有用户组件（含嵌套字段/long 字段）在 C++/ISPC 双后端下均正确编译**。

---

## 十一、Q1：关系查询进阶

### 11.1 反向查询 GetRelationsOf（利用 S24 索引 O(1)）

```csharp
// EntityManager / World 新增：
public Entity[] GetRelationsOf<TRel>(Entity target);   // 所有 --TRel--> target 的 sources
public Entity[] GetRelationsOfAll(Entity target);       // 跨所有关系类型

// 实现：直接查 RelationIndex（target.Id → byType → HashSet），O(1)，不扫 chunk
// 防御：IsAlive 过滤（source 已销毁/句柄过期跳过）
```

**性能**：10000 个 sources 查询 = 58us（含数组分配 + 存活校验），O(1) 索引查表。

### 11.2 双组件链式 WithRelationship

新增 `QuerySelection<T0,T1>`（`world.Query<T0,T1>()` 返回它），支持链式：

```csharp
// 关系仅过滤，不占组件位（遍历结果 = Comp0/T1 组件）
foreach (var r in world.Query<Position, Velocity>().WithRelationship<ChildOf>(parent))
{
    ref var pos = ref r.Comp0;
}

// 可继续链式
world.Query<Position, Velocity>()
      .WithRelationship<ChildOf>(parent)
      .WithEnabled<ActiveComponent>()
```

`QuerySelection<T0>`（单组件）保留 S23 形态（关系作为 Comp1 暴露，查询顺便读关系）。

### 11.3 验收

| 项 | 结果 |
|----|------|
| GetRelationsOf 按类型精确 | ✅ 7/7 测试（按类型/跨类型/Remove 后空/级联后空/World 入口） |
| 双组件链式过滤 | ✅ 5/5 精确 + sumX 校验 |
| 链式 WithEnabled | ✅ 组合过滤正确 |
| 全量回归 | ✅ 84/84（77 + 7） |
| Q2 步长修复 | ✅ IJobEntity 实测 sum=4==expected |

---

## 十二、与其他框架对比 + 后续计划（2026-08-29）

> 完整调研见 `docs/20260829-其他框架关系实现调研.md`（含 Bevy 官方源码 / Flecs flecs.c 源码，存于 `docs/research/`）。

### 12.1 三框架 vs EntJoy 关键差异

| 维度 | Bevy 0.16 | Flecs | Unity | **EntJoy** |
|------|-----------|-------|-------|-----------|
| 存储 | 组件对（`ChildOf`+`Children`） | pair 64 位 id 进 table 签名 | Buffer 组件 | 组件列（`RelationSlot` 8B 值） |
| 拆 archetype | ❌ | ⚠️ 默认拆（DontFragment/Union 抑制） | ❌ | ❌ |
| 反向索引 | 组件 hooks 双向同步 | id_record 表缓存 + 通配符链表 | 无 | 主动 `RelationIndex`（O(1)） |
| 级联删除 | `linked_spawn` + hook | 声明式 OnDeleteTarget | DestroyEntity 自动 | `DestroyEntityCascade` 递归 |
| 多同类型关系 | ❌ 出边限 1 | ✅ 天然 | ✅ | ❌ 路径 A（B 预留） |
| 遍历 API | `iter_ancestors`/`iter_descendants`/`iter_siblings` | `(Rel,*)`/`(*,Target)` 通配符 + up/down | 手动 | 🔲 未实现 |

### 12.2 EntJoy 设计定位确认

1. **不拆 archetype**（对齐 Bevy，优于 Flecs 默认）——S23 从一开始就正确
2. **主动 RelationIndex**（vs Bevy hooks）：O(1) 反向查询，但需显式维护（Add/Remove/覆盖/销毁 4 路径已全覆盖）
3. **多实例路径 B**：Flecs 证明"多实例 + 不拆 archetype"可共存（EcsUnion switch 列）——路径 B 有参考实现

### 12.3 S23c 关系遍历 API（✅ 已完成 2026-08-29）

借鉴 Bevy `iter_ancestors`/`iter_descendants`/`iter_siblings`，利用 RelationIndex O(1)：

```csharp
// EntityManager / World（已实现）
public Entity[] GetAncestors<TRel>(Entity entity);     // 沿 TRel 链向上（最近祖先在前，根在后）
public Entity[] GetDescendants<TRel>(Entity entity);   // 沿 TRel 链向下 BFS（直接子在前，不含自身）
public Entity[] GetSiblings<TRel>(Entity entity);      // 同 target 的兄弟（不含自身，无关系→空）
```

**实现要点**：
- **防环**：visited 集合**含起始实体**（环闭合时立即终止；不含则环多绕一圈）
- **GetAncestors**：单链爬升（单实例语义：每实体每关系类型最多 1 target）
- **GetDescendants**：BFS 逐层查 RelationIndex（O(1)/层）
- **关系类型参数化**：TRel 任意 IRelationComponent

**实现中发现的关键陷阱**：`default(Entity)` 的 `Id=0` 与真实实体 0 冲突——`GetRelationship` 无关系时返回 `default`，其 `Id=0` 恰好匹配第一个创建的实体（Id=0），导致遍历误把"无关系"当成"指向实体 0"。修复：新增 `TryGetRelationshipTarget<TRel>(entity, out target)`（内部用 `RelationSlot.IsValid` 判定），遍历 API 全部改用它。**教训：`GetRelationship` 的 default 返回值不可用于遍历判定**。

**验收结果**：

| 项 | 结果 |
|----|------|
| 祖先链（root→child→grandchild 3 层树） | ✅ 12/12 测试（链/单层/无关系/环/BFS/叶子/兄弟/World 入口/销毁后防御） |
| 环终止（a→b→c→a / a↔b） | ✅ 祖先 [b,c] / 后代 [b]，不绕环 |
| 销毁后防御（级联销毁后遍历） | ✅ 死实体跳过 |
| 全量回归 | ✅ 96/97（唯一 FAIL = 已知 S25 缺口判据 SharedQueryGapTest） |

**性能基线**（Debug，2026-08-29）：

| 操作 | 规模 | 耗时 |
|------|------|------|
| GetAncestors（深链） | 深度 10000 | 4.46 ms |
| GetDescendants（BFS） | 宽度 10000 | 2.21 ms |
| GetSiblings | 1000 次（hub 10000 子） | 116.5 us/op（结果集 10000 元素场景；兄弟数量小的真实场景开销小得多） |

---

## 十三、优化排序与框架完善方向（2026-08-29 后评估）

> 依据：EntJoySample Release 实测（§9.1b）+ 框架调研（`20260829-其他框架关系实现调研.md`）。

### 13.1 优化排序（可行性 × 收益）

| 优先级 | 优化项 | 收益（实测依据） | 可行性/风险 | 工作量 |
|--------|--------|------------------|-------------|--------|
| P0 | 先跑 Release 基准 | 现状量化（已做，见 §9.1b） | 零风险 | ✅ 已完成 |
| **P1** | **遍历 API 分配消除**：`GetAncestors/Descendants/Siblings/RelationsOf/RelationsOfAll` 每次 `new List/HashSet/数组`，复用 `Util/TempBuffer.cs` + 输出到调用方 span 重载 | 高：GetSiblings 81.5us（Release）中 9999 元素数组分配占大头；GetAncestors 3.54ms 中容器分配显著 | 高（纯 C# 改造，不动存储，语义不变） | ✅ **已完成**（2026-08-29 后）：实例级复用容器 + BFS 双缓冲；Release 实测 GetSiblings 81.5→63.0us/op（-23%），GetAncestors/Descendants 分配占比小、改善被噪声淹没 |
| P2 | RelationIndex 存储：`HashSet<Entity>`（struct 哈希+桶）换 packed long（Id<<32\|Version）或 per-target 小列表 | 中：Add 0.60us 中索引维护 ~0.35us | 高（RelationIndex 独立类，6 处调用点） | 0.5-1 天 |
| P3 | 级联删除批量：`DestroyEntityCascade` 逐实体 `DestroyEntityInternal`（各含 swap-pop+索引+Observer），按 archetype 分组一次批量移除 | 中：470us/父（Release），2-4x | 中（需保持 Observer 逐实体语义） | 1-2 天 |
| P4 | 批量 AddRelationship：首次加列 = 结构变更；复用 Phase 3 batch 模式 | 场景性：仅"首次批量建关系"受益 | 中 | 1-2 天 |

**不做**：Get/Has 微优化（0.115-0.121us 已含版本校验链 + AggressiveInlining，Release 已确认瓶颈不在计算）。

### 13.2 框架完善方向（参考 Bevy 0.16 / Flecs / Unity）

| 优先级 | 完善项 | 参考框架 | 可行性 | 说明 |
|--------|--------|---------|--------|------|
| 高 | **声明式级联策略**：`[OnTargetDeleted(Delete\|Keep)]` 特性或注册 API，`DestroyEntity` 自动级联（现在必须显式调 `DestroyEntityCascade`） | Flecs `(ChildOf, OnDeleteTarget, EcsDelete)` | 中（hook 进 `DestroyEntityCore`，不破坏旧语义） | 防漏调、API 更安全 |
| 高（零成本） | **关系数据能力文档化 + 测试**：`TRel` struct 可带任意字段（`struct Owns : IRelationComponent { public RelationSlot Target; public int Count; }`），Q2 修复后已天然支持但无测试锁定 | Flecs pair 关联组件 / Bevy Relationship 组件 | 极高 | 补测试即落地 |
| 中 | **小工具补全**：`GetRoot<TRel>`（链顶）、`(Rel,*)` 语义（`WithAll<TRel>` 可表达，补 World 入口检查） | Bevy `root_ancestor` / Flecs 通配符 | 高 | 各半天 |
| 低（大工程） | **路径 B 多实例（outgoing 1:N）**：Flecs EcsUnion switch 列 / Unity buffer 有参考；需 RelationTable 新存储面（§6 已预留） | Flecs `EcsUnion` / Unity Buffer | 低 | 关系存储重构，按需再做 |
| 视需求 | **克隆联动**：克隆父递归克隆子 | Bevy `linked_cloning` | 中 | EntJoy 无克隆 API，需先有克隆 |

**已无差距**：不拆 archetype（对齐 Bevy，优于 Flecs 默认）、反向索引 O(1)（`GetRelationsOf` ~5ns）、遍历 API（`GetAncestors/Descendants/Siblings`）、级联防环。
