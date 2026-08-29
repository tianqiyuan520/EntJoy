# Relation Multi：多实例关系设计 v2（1:1 per-chunk 聚类 + 1:N 反向索引，2026-08-29）

> **⚠️ 已废弃（2026-08-29 决策）**：关系扩展（1:N / M:N）**不实施**，保持现有 1:1 关系系统（RelationSlot 列 + RelationIndex 反向 + 遍历 API + 级联）。
> **废弃原因**（多轮评估结论）：
> - v1（外置 RelationTable）：查询逐个定位实体（散点）、Job 不可读
> - v2（复用 S16 Shared values 区聚类）：**关系 target 是高基数**（实体即 target，1 万父 × 3 子 → chunk 利用率 0.4%），
>   与 Shared Component 的低基数聚类前提本质不同 → chunk 稀疏爆炸，方向性错误
> - Unity Buffer 式：query 过滤性能软肋（数据在 chunk 外、实体间不连续）
> - 正确认知：1:1 的"按 target 查询"高性能解 = 反向索引 O(1) 候选集（`RelationIndex` 已有，5ns/op），无需存储重构
> **本文档保留作为决策记录**；如需扩展请先解决"高基数 target 的存储/查询"本质问题，勿直接复用聚类机制。
> **范围**：EntJoy 关系系统扩展为"1:1 + 1:N + M:N 全支持"，核心是 **1:1 关系复用 S16 Shared Component 的 per-chunk 聚类机制**（高性能 chunk 级过滤），
> 1:N 走反向索引（查询数学最优），M:N = 双向 1:N。
> **状态**：设计定稿 v2（2026-08-29，替代 v1 纯表方案），待实施。
> **前置**：`20260829-RelationSoA设计.md`（S23/S24/Q1/Q2）、`20260829-其他框架关系实现调研.md`、`20260826-SharedComponent-perChunk设计.md`（S16/S16b，本方案核心复用）。

---

## 一、决策与依据

### 1.1 决策（v2，替代 v1）

| 关系形态 | 存储 | 查询 | Job |
|---------|------|------|-----|
| **1:1（默认）** | **chunk Shared values 区存 RelationSlot 单值（per-chunk 聚类，复用 S16）**——同 target 实体同 chunk | **chunk 级过滤**（同 `MatchesSharedFilter`），连续遍历 | ✅ S16b 已验证（C++/ISPC 单值指针） |
| **1:N（`[MultiRelation]`）** | 正向：实体关系列表（EntityManager 托管）；反向：`RelationIndex`（已有） | 反向索引 O(1) 连续集合（数学最优） | ❌ 主线程（同 managed shared 纪律） |
| **M:N** | = 双向 1:N（正向列表 + 反向索引） | 同 1:N | ❌ |

### 1.2 为什么 v2 抛弃 v1（纯 RelationTable）与 Unity Buffer 式

| 方案 | 问题 | 实测/依据 |
|------|------|----------|
| v1 纯表（外置 Dictionary） | 查询=逐个定位实体（散点），Job 不可读 | 缓存差、能力丢失 |
| Unity Buffer 式（header + 外部区） | **query 过滤性能软肋**：数据在 chunk 外、实体间不连续，`query 哪些实体有 (Rel,T)` 需 O(N) 随机访问 | Unity 生产现状 |
| Flecs 拆表聚类 | 按 target 查询最优，但**拆 archetype 碎片化**，全量遍历跨表 | Flecs 自提供 `DontFragment` 抑制 |

**v2 的核心：聚类发生在 chunk 级而非表级**——复用 S16 已验证机制，获得"表即答案"的查询性能，同时零拆表。

### 1.3 主流对照（为什么 1:1 聚类是正确方向）

| 框架 | 按 target 查询 | 碎片化 | 对照 |
|------|---------------|--------|------|
| Flecs（pair 进签名） | 表即答案 O(1) | ✅ 有 | v2 在 chunk 级达到同等查询语义，无碎片 |
| Bevy / Friflo / Unity | 无内建高性能过滤（遍历集合/索引） | 无 | v2 优于三者 |
| EntJoy v2 | **chunk 级过滤 O(目标 chunk)** | **无** | — |

---

## 二、存储设计

### 2.1 1:1：chunk Shared values 区存 RelationSlot（复用 S16）

```
Chunk 内存块（布局不变，复用 Shared values 区）：
[Entity array][组件列×N][变更位掩码][Shared values 区（含 RelationSlot 单值）]
                                     └─ ChildOf 类型 → 内联 RelationSlot（8B blittable）
                                        （对齐 S16：blittable shared 内联存值）

核心不变式：同一 chunk 的所有实体共享相同的 1:1 关系 target。
```

**复用清单（全部已验证，零新存储机制）**：

| S16 机制 | 用途 |
|---------|------|
| `ChunkMetadata.SharedValuesOffset/SharedValueOffsets`（`ChunkMetadata.cs:138-172`） | 关系单值区布局 |
| `Chunk.GetSharedValue<T>` / `GetSharedValuePointer`（`Chunk.cs`） | 读写 chunk 单值 |
| `MatchesSharedFilter`（`EntityManager_SharedComponent.cs`） | **chunk 级过滤**（关系版 `MatchesRelationFilter` 同构） |
| `SetSharedComponent` 移动语义（单实体就地改 / 多实体跨 chunk 移动） | **改 target = 跨 chunk 移动**（对齐 `SetRelationship`） |
| per-value 最近使用缓存（`_lastChunkPerSharedValue`） | 找目标值 chunk O(1) 期望 |
| S16b `sharedValuePtrs` ABI（`NativeChunkJobs.cs` + `ChunkJobData.h`） | Job 读关系单值（C++/ISPC） |

**与 S16 的差异**：值类型是 `RelationSlot`（含 TargetVersion 防 ID 回收），相等判定 = target 的 Id+Version 双匹配（`RelationSlot.Matches`）。

### 2.2 1:N：正向实体列表 + 反向 RelationIndex

```csharp
/// <summary>多实例关系（[MultiRelation] 类型）正向存储：relTypeId → sourceId → targets。</summary>
internal sealed class RelationListStore
{
    // 正向：追加式 List<RelationSlot>（连续遍历）；Add 幂等去重
    private readonly Dictionary<int, Dictionary<int, List<RelationSlot>>> _forward;
    // 反向：复用 EntityManager._relationIndex（target → (relType → sources)，已有）
}
```

- 正向：主线程托管（简单、无预算、无 chunk 内区）
- 反向：`RelationIndex`（`RelationIndex.cs:16`，target→HashSet）——**查询侧 O(1) 数学最优**
- 1:N 类型不占 chunk 列、不进 Shared values 区（避免混淆 1:1 聚类）

### 2.3 类型判定与路由

```csharp
[AttributeUsage(AttributeTargets.Struct)]
public sealed class MultiRelationAttribute : Attribute { }

public struct ChildOf : IRelationComponent { public RelationSlot Target; }  // 1:1 → chunk 聚类
[MultiRelation]
public struct Skill  : IRelationComponent { public RelationSlot Target; }   // 1:N → 列表+索引
```

`AddRelationship` 单点路由：`[MultiRelation]` → 列表；否则 → chunk 单值聚类路径。

---

## 三、API 设计

### 3.1 现有 API（1:1，语义升级但签名不变）

```csharp
public void AddRelationship<TRel>(Entity entity, Entity target);   // 找/建 target 值 chunk → 移动实体（复用 SetSharedComponent 语义）
public void RemoveRelationship<TRel>(Entity entity);               // 移除关系（实体移回无值 chunk 或销毁列）
public Entity GetRelationship<TRel>(Entity entity);                // 读 chunk 单值
public bool HasRelationship<TRel>(Entity entity);
```

**语义变化**：改 target 从"原地 SetRaw 8B"变为"跨 chunk 移动实体"（S16 移动语义，per-value 缓存兜底）——这是 1:1 聚类换取查询性能的代价。

### 3.2 新增 API（1:N）

```csharp
public void AddRelationship<TRel>(Entity entity, Entity target);        // [MultiRelation] 类型 → 列表追加（幂等）
public void RemoveRelationship<TRel>(Entity entity, Entity target);     // 移除指定 target
public bool HasRelationship<TRel>(Entity entity, Entity target);        // 是否指向该 target
public Entity[] GetRelationships<TRel>(Entity entity);                  // 全部 targets
public int GetRelationshipCount<TRel>(Entity entity);
public void ClearRelationships<TRel>(Entity entity);                    // 实体销毁时自动调用
```

### 3.3 World 便捷入口

`World.Relation.cs` 同步转发（与现有模式一致）。

---

## 四、查询设计（高性能核心）

### 4.1 WithRelationship<TRel>(target) 路由

| 类型 | 过滤方式 | 复杂度 |
|------|---------|--------|
| 1:1 | **chunk 级过滤**：遍历 chunk 比较 Shared values 区单值（`MatchesRelationFilter`，同 `MatchesSharedFilter` 模式）——只遍历含该关系类型的 archetype chunk | O(chunks)，连续内存 |
| 1:N | 反向索引：`_relationIndex.TryGetSources(target.Id)` 拿候选实体集合 → 与 chunk 扫描结果取交集 | O(1) + 定位 |

**实现要点**：
- `EntityQuery.Refresh`/`RefreshIncremental`：关系过滤并入现有三路径（`EntityQuery.cs:191` 同 `MatchesSharedFilter` 位置）——新增 `MatchesRelationFilter`（chunk 单值比较）
- `QueryEnumerable`（枚举器）：1:1 逐 chunk 单值校验（`QueryEnumerable.cs:52` 同构）；1:N 进入时查反向索引拿候选集（一次）
- `QueryKey` 指纹：`TRel.Id + target.Id + target.Version` 已覆盖（`EntityQuery.cs:41-43`），1:1/1:N 共用

### 4.2 WithAll<TRel>

- 1:1：拥有关系类型（Shared values 区有槽）即匹配
- 1:N：反向索引含该 relType 的实体 ∪ 列实体

### 4.3 Job / NativeTranspiler

- **1:1**：IJobChunk `chunk.GetSharedComponent<ChildOf>()` → C++ 单值指针（S16b ABI 已就绪）；IJobEntity job 字段捕获（S16b 已验证，`BindingsGenerator` 单值指针通道）
- **1:N**：托管侧，`[NativeTranspile]` Job 访问 → validator 编译期拦截（同 managed shared：NT0xx 报错，提示主线程读取）

---

## 五、级联扩展

| 时机 | 1:1（chunk 聚类） | 1:N（列表+索引） |
|------|-------------------|------------------|
| AddRelationship | 找/建 target 值 chunk + 移动（S16 移动路径） | `RelationListStore.Add` + `RelationIndex.Add` |
| RemoveRelationship | 移动回无值 chunk / 移除 | `RelationListStore.Remove` + `RelationIndex.RemoveRelTypeId` |
| DestroyEntityInternal | chunk 单值无需清理（chunk 级） | 清列表 + 反向索引（扩展 `CleanupSourceRelations`，`EntityManager.Relation.cs:425-435` 模式） |
| DestroyEntityCascade | `CollectCascade` 走反向索引（target→sources，1:1 关系值在 chunk，由 RelationIndex 反查） | 同左 + 列表侧 |

**1:1 级联注意**：target 销毁后，chunk 单值中的 version 不匹配 → `Matches` 校验失败 → 关系自动失效（与现有 `RelationSlot.Matches` 同语义）；级联销毁走 `RelationIndex`（1:1 关系的 sources 也在反向索引中维护）。

---

## 六、性能分析

| 访问模式 | 1:1（chunk 聚类） | 1:N（列表+索引） |
|---------|-------------------|------------------|
| `WithRelationship(target)` | chunk 级过滤，连续单值比较（对标 Flecs 表即答案，零碎片） | 反向索引 O(1) 连续集合（数学最优） |
| `GetRelationship` / Has | chunk 单值读 O(1) | 列表查找 O(k) |
| 按 source 遍历 targets | 单值（1:1 本质） | 列表连续 O(k) |
| 改 target | 跨 chunk 移动（S16 已验证 + per-value 缓存） | 列表增删 O(k) |
| 全量遍历（Job） | 不拆表连续 + chunk 单值可读 | 不可用（主线程） |

**目标基线**：`WithRelationship` 查询 ≤ 现有 `WithShared` 过滤量级（chunk 级，0.85ms/万匹配为逐槽基线，聚类后降至 O(目标 chunk)）。

---

## 七、迁移路径（现有 1:1 列 → chunk 单值）

| 步骤 | 内容 | 影响 |
|------|------|------|
| 1 | `ChildOf` 等 1:1 关系类型改为 shared 式注册（进 Shared values 区，不再占组件列） | `ComponentTypeManager` 注册路径 + `ChunkMetadata` 布局 |
| 2 | 关系 API 读改为 `GetSharedValue<RelationSlot>`；写改为 S16 移动语义 | `EntityManager.Relation.cs` |
| 3 | **Q2 六路径改造**：`IJobEntity Execute(ref ChildOf)` 组件列参数 → job 字段捕获（S16b 已验证通道）；IJobChunk 改用 `GetSharedComponent<ChildOf>` | NativeTranspiler 生成器 + 六路径测试 |
| 4 | 级联/索引/查询过滤适配（§四/五） | `EntityManager.Relation.cs` + 查询路径 |
| 5 | 现有 RelationTests / CascadeTests 迁移断言（值相等、chunk 聚类语义） | 测试 |

**兼容策略（可选）**：迁移期间保留"列模式"作为内部过渡（`[LegacyColumn]`），验证后删除——推荐直接迁移（机制已验证，避免双路径）。

---

## 八、工作量与风险

| 项 | 工作量 | 风险 |
|----|--------|------|
| 1:1 关系共享式注册 + 布局 | 0.5-1 天 | 低（S16 布局已验证） |
| 关系 API 改造（读写/移动语义） | 1-1.5 天 | 中（SetSharedComponent 移动路径适配 RelationSlot + version 校验） |
| 1:N 列表存储 + 新 API | 1 天 | 低（独立托管结构） |
| 查询接入（MatchesRelationFilter / 反向索引交集 / QueryKey） | 1 天 | 中（1:1 与 1:N 路由） |
| **Q2 六路径改造**（Job 访问方式变更） | 1-2 天 | **高**（IJobEntity 参数语义变化，需重建验证） |
| 级联 + 测试迁移 + 新增 1:N 测试 | 1-1.5 天 | 中 |
| 性能基准 | 0.5 天 | — |

**总工时**：约 **6-8 天**（主要成本在 Q2 六路径改造，其余复用 S16 已验证机制）。

**关键风险**：① IJobEntity 关系参数语义变更（`Execute(ref ChildOf)` → 字段捕获）——S16b 已验证通道但需适配；② 1:1 改 target 变结构变更（移动）——per-value 缓存兜底 + S16 已验证。

---

## 九、验收方法

| # | 验收项 | 方法 |
|---|--------|------|
| 1 | 1:1 聚类 | 同 target 实体同 chunk（断言 ChunkIndex 相等）；不同 target 不同 chunk |
| 2 | 1:1 查询高性能 | `WithRelationship(target)` chunk 级过滤正确性 + 基准（目标 ≤ WithShared 量级） |
| 3 | 1:1 改 target | 移动语义正确（跨 chunk），per-value 缓存命中路径 |
| 4 | 1:N 增删查 | 追加/幂等/`GetRelationships` 顺序数量/`HasRelationship(entity,target)` |
| 5 | 1:N 查询 | `WithRelationship(target)` 反向索引候选正确；与 1:1 混合查询 |
| 6 | 级联 | `DestroyEntityCascade(target)` 销毁 1:1/1:N sources；普通 Destroy 清理索引 |
| 7 | 防 ID 回收 | target 销毁后（version+1）1:1 chunk 单值与 1:N 列表均失效 |
| 8 | Job 六路径 | 1:1 C++/ISPC 读关系单值（S16b ABI）回归；1:N validator 拦截 |
| 9 | 回归 | EntJoy.ECS.Tests 全量 + EntJoySample 编译 |
| 10 | 基准 | Release：1:1 WithRelationship / 1:N WithRelationship / 改 target / GetRelationships |

---

## 十、实施清单

| 步骤 | 内容 | 文件 | 状态 |
|------|------|------|------|
| 1 | 1:1 关系共享式注册（`IsSharedRelation` 判定 + ChunkMetadata 布局） | `ComponentTypeManager.cs` + `ChunkMetadata.cs` | 🔲 |
| 2 | 关系 API 改造：`GetRelationship` 读单值 / `AddRelationship` 移动语义（复用 `SetSharedComponent` 路径 + version 校验） | `EntityManager.Relation.cs` + `EntityManager_SharedComponent.cs` | 🔲 |
| 3 | `MatchesRelationFilter`（chunk 单值比较）+ 查询三路径接入 + QueryKey | `EntityQuery.cs` / `QueryEnumerable.cs` / `QueryBuilder.cs` | 🔲 |
| 4 | 1:N `RelationListStore` + `[MultiRelation]` 特性 + 新 API | `RelationListStore.cs` + `EntityManager.Relation.cs` | 🔲 |
| 5 | Q2 六路径改造（IJobEntity 字段捕获 / IJobChunk GetSharedComponent）+ validator 拦截 1:N | `NativeTranspiler` 生成器 + `NativeTranspileValidator.cs` | 🔲 |
| 6 | 级联扩展（1:1 反查 + 1:N 清理） | `EntityManager.Relation.cs` | 🔲 |
| 7 | 测试迁移（RelationTests/CascadeTests）+ 新增 1:N/聚类测试 | `tests/EntJoy.ECS.Tests/` | 🔲 |
| 8 | 性能基准 + 文档收尾 | `samples/EntJoySample/09_ECS/Benchmarks/` + 本文档 §九 | 🔲 |
