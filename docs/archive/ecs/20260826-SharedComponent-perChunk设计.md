# 20260826：Shared Component per-chunk 设计记录

> **范围**：EntJoy.ECS Shared Component 完整设计 —— 从 v3 的"值不同则拆 Archetype"改为 **per-chunk 存储**（对齐 Unity DOTS），
> 双类型策略（blittable 内联 / managed 扁平数组索引），值只增不减，
> 以及 NativeTranspiler 对 blittable SharedComponent 的支持方案。
> **关联文档**：`ecs-evolution-plan-v3.md`（§2.2）、`Phase优先级分析与实施路线.md`（S16 / S16b / S25 / 决策 6.4）

---

## 一、决策背景

### 1.1 为什么弃"拆 Archetype"

v3 原方案（2026-08 之前）：将 Shared Component 作为 Archetype 签名的一部分，值不同则拆成不同 Archetype。

| 问题 | 说明 |
|------|------|
| **Archetype 爆炸** | 共享值种类多时（材质、阵营、网格名…）Archetype 数量随值增长 |
| **高频改值 = 结构迁移** | SetSharedComponent 换 Archetype = 全量结构变更（swap-pop + 等待 Job） |
| **与查询过滤不匹配** | 共享值天然是"按 chunk 过滤"的场景，拆 Archetype 语义过重 |

### 1.2 Unity DOTS 的 per-chunk 模型（参考）

Unity Entities 事实行为（官方文档）：
- 同一 Archetype + 同一共享值 → 同一 Chunk（[Entities 0.50 Shared components](https://docs.unity3d.com/Packages/com.unity.entities@0.50/manual/shared_component_data.html)）
- Chunk 上存共享值**索引**（`ArchetypeChunk.GetSharedComponentIndex`），值本体在数据管理器仓库（数组 + hash map）（[API](https://docs.unity.cn/Packages/com.unity.entities@1.1/api/Unity.Entities.ArchetypeChunk.GetSharedComponentIndex.html)）
- 托管 shared 支持（`SetSharedComponentManaged`），仅主线程/数据层访问，job 不能读（[API](https://docs.unity3d.com/Packages/com.unity.entities@1.2/api/Unity.Entities.EntityManager.SetSharedComponentManaged.html)）

**EntJoy 结论**：采用同模型，并切分双类型（blittable 交给 NativeTranspiler，managed 只做分组）。

---

## 二、存储设计

### 2.1 总览

```
EntityManager（managed shared 区）
  ├── _managedSharedValues: object[]       // 值本体（追加式数组）
  ├── _managedSharedRefCounts: int[]       // 引用计数（= 引用该值的 chunk 数）
  ├── _managedSharedFreeList: int[]        // 空闲槽位栈（复用已销毁 index）
  ├── _managedSharedCount: int             // 活跃值数
  │
  ├── 哈希桶表（值 → index 查找，自动扩容）：
  │   ├── _sharedHashBuckets: int[]        // 桶头（存 index，-1 = 空）
  │   ├── _sharedHashNext: int[]           // 链式下一项（-1 = 链尾）
  │   └── _sharedBucketMask: int           // 容量-1（容量保持 2 的幂）

Chunk 内存块 Shared values 区（chunk 尾部）：
  [blittable 值内联][blittable 值内联]...[managed: int index][int index]...
   blittable 直接存值                    managed 只存 index → _managedSharedValues[index]
```

**核心不变式**：同一 Chunk 的所有实体共享相同的 SharedComponent 值组合。

### 2.2 为什么 managed 用"数组 + 哈希桶 + 索引引用"

| 需求 | 方案 |
|------|------|
| managed 值无法进非托管内存块 | chunk 只存 int 索引（blittable），对象引用留在托管堆 |
| 值去重（同值实体共享同一分组键） | 哈希桶：`GetHashCode & mask` → 桶；`EqualityComparer<T>` 链式比较 |
| 自动扩容 | load factor > 0.75 → 桶容量 ×2 → rehash（活跃 index 不变，只重建桶表） |
| 自动销毁 | refCount 归零 → 槽位清空（失去强引用 → GC）→ freelist 复用 index |
| 生命周期 | World Dispose 清空数组；孤儿值 GC 回收 |

### 2.3 managed 生命周期（refcount 自动销毁）

```
FindOrAddManagedValue<T>(value) → index：
  1. 桶 = hash & mask；链式遍历比较 EqualityComparer<T>
  2. 命中 → 返回 index（调用方 refCount++）
  3. 未命中 → freelist 弹槽位 或 追加；写值，refCount = 1；插入桶链头；检查扩容

ReleaseManagedValue(index)：
  refCount--
  if refCount == 0：
    _managedSharedValues[idx] = null      // 逻辑移除 → 失去强引用 → GC 物理回收
    从哈希桶链摘除该 index
    _managedSharedFreeList 压入 idx
    _managedSharedCount--

触发时机（均在结构变更同步点，单线程安全）：
  ├─ 新建 chunk（带新值）       → FindOrAdd + AddRef
  ├─ chunk 销毁 / 空 chunk 回收 → ReleaseRef
  ├─ SetSharedComponent 就地改值（单实体 chunk）→ 旧 Release + 新 AddRef
  └─ 实体移动到已有 chunk       → chunk 引用不变，无操作
```

### 2.4 chunk 内存块布局（ChunkMetadata 扩展）

```
新增字段：
  SharedValuesOffset      // shared 区起点（chunk 尾部，缓存行对齐）
  SharedValueOffsets[]    // 每 shared 类型在区内偏移
  SharedValueSizes[]      // blittable = 类型大小；managed = sizeof(int)
  SharedValueIsManaged[]

Shared values 区大小 = Σ(blittable 大小) + Σ(4B per managed)
chunk 创建时写入值（blittable 内联 / managed index），显式初始化防脏数据
```

---

## 三、ECS API（对齐 DOTS 用法）

```csharp
public interface ISharedComponentData { }   // blittable struct 或 class 均可

// EntityManager
T GetSharedComponent<T>(Entity entity);
   // blittable: 读 chunk 内存区内联值
   // managed:   读 chunk 槽位 index → _managedSharedValues[index] → (T)
void SetSharedComponent<T>(Entity entity, T value);
   // 1. 所在 chunk 单实体 → 就地改值（blittable 覆写 / managed 换 index + refcount 调整）
   // 2. 多实体且值不同 → Archetype 内找目标值 chunk（blittable 按值 / managed 按 index）
   //    无则新建（写入新值/index）；swap-pop 移动实体
   // 3. 值相同 → 无操作
Entity CreateEntity(ComponentType[] types, params (Type, object)[] sharedValues); // 带初始共享值

// QueryBuilder
QueryBuilder WithShared<T>(T filterValue);   // chunk 级过滤（对齐 WithSharedComponentFilter）

// EntityBuilder
EntityBuilder WithShared<T>(T value);        // Spawn().WithShared(...)
```

**移动语义**：SetSharedComponent 只在同一 Archetype 内移动 chunk（无 Archetype 变更），走既有 swap-pop + `CompleteArchetypeJobs`。

---

## 四、NativeTranspiler 支持方案（仅 blittable）

### 4.1 结论：两个入口，一套 ABI；managed 完全不处理

| 入口 | blittable shared | managed shared |
|------|-----------------|----------------|
| **IJobChunk** | Execute 内 `chunk.GetSharedComponent<T>()` → C++ 单值指针（新增翻译） | validator NT0xx 编译期报错 |
| **IJobEntity** | job 字段捕获（`public Material Mat;`），外层 C# 从 chunk 读出填入 | validator 拦截（同现有 NT002 托管字段） |
| **查询过滤** | C# 调度前按 chunk 过滤，无 ABI 改动 | 同左（C# 比较） |

### 4.2 ABI 扩展（blittable only）

**C# 侧（EntJoy.ECS）**：
- `ChunkJobData`（`src/EntJoy.ECS/JobSystem/NativeChunkJobs.cs`）新增：
  ```csharp
  public void** sharedValuePtrs;   // 每个 ISharedComponentData（blittable）单值指针 [sharedCount]
  public int sharedValueCount;
  ```
- `ChunkData`（轻量结构）同步新增
- `ChunkJobCollector.BuildNativePayload / BuildEntityBatchPayload`：填充 `sharedValuePtrs`（指向 chunk Shared values 区各 blittable 偏移，**跳过 managed 槽位**）

**C++ 侧（NativeDll）**：
- `ChunkJobData.h` / `ChunkData.h` 结构体同步加 `void** sharedValuePtrs; int sharedValueCount;`
- ClangCL 重编译

### 4.3 生成器翻译

- `CppJobGenerator.CollectChunkNativeArrayTypes`：收集 IJobChunk Execute 内 `chunk.GetSharedComponent<T>()` 的 T（过滤 `!IsManaged`）→ required shared types 集合
- `CppChunkStatementTranslator`：`chunk.GetSharedComponent<T>()` → `*reinterpret_cast<const T*>(__chunkData->sharedValuePtrs[{idx}])`（单值，无数组索引）
- `BindingsGenerator`：生成 `s_RequiredSharedTypeIds` 静态数组（类似现有 `s_RequiredComponentTypeIds`）
- ISPC 路径：`uniform T x = *({cppType}*)sharedValuePtrs[i];`（chunk 级均匀值，非 varying）
- **Validator**：`GetSharedComponent<T>` 的 T 是托管类型 → NT0xx（"managed shared component cannot be accessed in [NativeTranspile] job，请仅在主线程/C# 侧读取"）

### 4.4 IJobEntity（生成器零改动，复用现有 job 字段通道）

已验证（源码核对）：
- `BindingsGenerator.AppendFieldDllImportParams`：非 NativeArray 字段 → `{cppType} {field.Name}_ptr`（单值指针）
- `CppJobGenerator.AppendLocalVariableDeclarations`：`const {cppType}& {field} = *{field.Name}_ptr;`

**因此 blittable shared 走 job 字段天然可用**（C# 侧 Schedule 前从 chunk 读出填入字段）；托管字段被既有 NT002 拒绝，与"NativeTranspiler 不处理托管 shared"一致。

---

## 五、阶段拆分与工时

| 阶段 | 内容 | 工时 | 状态 |
|------|------|------|------|
| **阶段一（S16）** | Shared Component 核心存储：接口/类型判定、ChunkMetadata 布局、EntityManager 哈希桶 + refcount、Archetype 定位、Get/Set/create、WithShared、EntityBuilder、单测 | 3-5 天 | ✅ 已完成（2026-08-27） |
| **阶段二（S16b）** | NativeTranspiler blittable 支持：ABI、Collector 填充、IJobChunk 翻译、BindingsGenerator、ISPC、Validator、NativeDll 重编译、冒烟测试 | 3-5 天 | ✅ 已完成（2026-08-27） |
| **阶段三（S25，后续）** | 落地扩展：按共享值排序/分组 chunk、`WithShared` 进阶、与 Change Tracking / Job Tracking 联动 | 1 周 | ✅ 收口（b ✅ 2026-08-29、c ✅ 验证、a ⏸ 已评估暂不实现，见 §5.1） |

### 5.1 S25 缺口实证与修复记录（2026-08-29 源码核实 + 测试）

| 子项 | 内容 | 实证 / 修复 |
|------|------|------|
| **S25b** | `WithShared` 托管查询面过滤 | ✅ 已修复（2026-08-29）。缺陷清单（4 个查询面 + 1 个**基础设施布局 bug** + 2 个基础修复）：① `EntityQuery.Refresh`/`RefreshIncremental` 收集 chunk 时忽略 WithShared → 统一走 `EntityManager.MatchesSharedFilter`（单一真值来源，Job 面 `ChunkJobCollector` 同源）；② `QueryKey` 指纹缺 SharedFilterValue → `Material(2)`/`Material(3)` 误共享实例 → 指纹补 value + hash；③ `WithChanged` 同类缺陷：`Refresh` 不过滤变更位 → 新增 `MatchesChangedFilter` 并入三路径；④ 流式/查询路径对"不含共享列的 archetype"字典 miss 抛异常 → `Archetype.IsMatch` 补 SharedFilterType 列校验（与 S23 关系列同策略）；⑤ **chunk stride 布局 bug（最严重）**：`ComputeChunkStride` 只算 Entity + 组件数组（15360B），漏了变更位掩码 + 共享值区（布局尾 15552B）→ **下一 chunk 的 Entity 数组起点压在本 chunk 的位掩码/共享值区上**：写入新 chunk 的实体 Id 直接污染上一 chunk 的变更位掩码（WithChanged 假阳性，幽灵位=新实体 Id），共享值写入损坏下一 chunk 的 Entity 数组（实体引用错乱）→ 修复：stride 直接取 `ChunkMetadata.Create` 的 TotalSize 对齐（单一布局真值来源）。基础修复：新 chunk 变更位掩码未清零（slab 池复用残留）→ Chunk 构造时 InitBlock；`EnsureUpToDate` 仅按结构版本刷新，帧级变更位不触发 → 带 WithChanged 的查询每次访问重评。测试：`SharedQueryTests` 8/8（含新增 `WithShared_IgnoresArchetypesWithoutSharedColumn`），全量 104/104 |
| **S25a** | 按共享值排序/分组 chunk | ⏸ 已评估，暂不实现（2026-08-29 决策）。**现状已具备**：同值实体必同 chunk（`NewEntity`/`SetSharedComponent` 均"找/建目标值 chunk"）+ 空 chunk swap-pop 回收；唯一缺口是 ChunkList 中同值 chunk **物理相邻**。**关键约束**：跨值"排序"不可行——shared 值是 boxed 任意类型（managed class 无比较键、blittable 无 IComparable 契约），无全序，只能"同值分组"；物理换位须同步更新 `EntityInfo.ChunkIndex`（存于每个实体，插入中间为 O(N)），最小成本方案是"空新 chunk 与同值组尾 chunk 交换列表位置"（O(cap)），且 `Remove` 的 swap-pop 会打乱分组、维持不变量需额外逻辑。**收益存疑**：cap=768 大 chunk 下同值分散需 768+ 实体才出现。若日后需要，采用最小方案"新建目标值 chunk 时归入同值组尾" |
| **S25c** | 与 Change Tracking 联动 | ✅ 验证为既有能力（2026-08-29）：`SetSharedComponent` 就地改值路径（103/126 行）与移动路径（115/134 行）均已调用 `MarkEntityChanged`；Job 安全由入口 `CompleteActiveJobs()` 保证。补测试锁定语义：创建不标记变更（AddEntity 不打标）→ 帧末 `ClearAllChangedBitMasks` 归零 → 就地改值/移动后 WithChanged 查询可见 |

### 5.2 S25a 决策复核 + per-value 缓存（2026-08-29 后）

**决策维持**：S25a（按共享值排序/分组 chunk）仍暂不实现。复核补充两点：

1. **"同值实体必同 chunk"表述不准确（实证）**：仅 `NewEntity`/`SetSharedComponent` **多实体移动**路径找/建目标值 chunk；`SetSharedComponent` **单实体 chunk 就地改值**路径（`EntityManager_SharedComponent.cs` 就地改值分支）直接覆写不合并 → **同值可多 chunk 并存**。对 S25a 评估无影响（唯一缺口仍是物理相邻），但文档原表述需修正。
2. **per-value 最近使用缓存（方案 B）落地**：`FindChunkWithManagedValue`/`FindChunkWithBlittableBoxed` 原为 O(chunks) 全量扫描（SetSharedComponent 移动 / NewEntity 带 shared 高频路径），现改为缓存优先 + lazy 验证（验证 chunk 仍存在、未满、值匹配，失败回退扫描）。key = `(Archetype, compIdx, value)`（managed 值 = 全局 index box；blittable = boxed 值）。删除路径零维护（swap-pop / 就地改值后缓存自然失效）。`FindExistingChunkForShared` 单 shared 列复用缓存路径，多列保持扫描。Dispose 清空。新增 4 测试（缓存命中一致 / chunk 回收后失效 / 就地改值后失效 / managed 路径），全量 108/108。

---

## 六、风险与缓解

| 风险 | 概率 | 缓解 |
|------|------|------|
| refcount 与 chunk 生命周期不同步 | 中 | 所有增删改集中到结构变更同步点；单测覆盖 |
| 哈希桶 rehash 后 index 失效 | 低 | rehash 只重建桶表（index 不变），chunk 槽位不受影响 |
| 桶链删除错乱（refCount 归零摘除） | 中 | 链式删除单测（前驱/链尾/单节点） |
| 用户类型未覆写 GetHashCode | 中 | 文档要求 `IEquatable<T>` + `GetHashCode`；缺省 `EqualityComparer<T>.Default` 兜底 |
| freelist 复用错乱 | 低 | 单测断言 index 复用正确；refCount 归零即清空槽位 |
| chunk 复用脏数据 | 中 | chunk 创建显式写 shared 值；AddEntity 不动 Shared 区 |
| SetSharedComponent 移动正确性（swap-pop + 位掩码） | 中 | 复用结构变更流程 + 单测覆盖多实体 chunk |
| managed 出现在 native job | 低 | validator NT0xx 编译期拦截 |
| ISPC 遗漏 uniform 翻译 | 低 | required shared types 集合 + 冒烟测试 |

---

## 七、涉及文件清单（计划）

**文档**：`ecs-evolution-plan-v3.md`、`Phase优先级分析与实施路线.md`、`项目现状总览.md`（已更新）

**阶段一代码**：
- `src/EntJoy.ECS/Component/ISharedComponentData.cs`（新增）
- `src/EntJoy.ECS/Component/ComponentType.cs`（IsShared）
- `src/EntJoy.ECS/Component/ComponentTypeManager.cs`（shared/托管注册）
- `src/EntJoy.ECS/Archetype/ChunkMetadata.cs`（Shared values 区）
- `src/EntJoy.ECS/Chunk/Chunk.cs`（读写 API）
- `src/EntJoy.ECS/Archetype/Archetype.cs`（FindChunkBySharedValue）
- `src/EntJoy.ECS/Entity/EntityManager.cs`（Get/Set/Create + 哈希桶区）
- `src/EntJoy.ECS/Query/QueryBuilder.cs`（WithShared）
- `src/EntJoy.ECS/Entity/EntityBuilder.cs`（WithShared）
- `tests/EntJoy.ECS.Tests/SharedComponentTests.cs`（新增）

**阶段二代码**：
- `src/EntJoy.ECS/JobSystem/NativeChunkJobs.cs`、`ChunkJobCollector.cs`
- `src/NativeTranspiler/Analyzer/Cpp/CppJobGenerator.cs`、`CppChunkStatementTranslator.cs`
- `src/NativeTranspiler/Analyzer/Common/BindingsGenerator.cs`
- `src/NativeTranspiler/Analyzer/Ispc/IspcGenerator.cs`
- `src/NativeTranspiler/Analyzer/Common/NativeTranspileValidator.cs`
- `src/NativeDll/ChunkJobData.h`、`ChunkData.h`