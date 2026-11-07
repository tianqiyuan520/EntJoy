# Relationship 场景

独立入口为 `EntJoySample.Relationship.Program`。在仓库根目录运行：

```powershell
dotnet build samples/EntJoySample/EntJoySample.csproj -c Release --no-incremental -p:ENTJOY_STARTUP=EntJoySample.Relationship.Program -o artifacts/relationship-sample
dotnet artifacts/relationship-sample/EntJoySample.dll
```

需要 .NET 8、CMake、Visual Studio C++ 工具链及 PATH 中的 ISPC。构建使用样例工程已有的原生转译链，将运行文件放入独立目录。本入口包含 C++/ISPC 演示，使用原生调度，需要输出目录中的 `NativeDll.dll` 和 `NativeTranspiled.dll`；整套样例程序集在启动时加载原生绑定。所有场景核对成功后输出 `PASS: all Relationship scenarios.`，结果不符时返回非零退出码。

## 基数与建模

“单目标”约束的是每个 source 在同一种关系下的目标数量，不等于 1:1。

| 业务关系 | source → target | 当前写法 |
|---|---|---|
| 背包有多件物品 | 多件物品各自 → 同一个背包，N:1；反向就是 1:N | 普通关系 `StoredIn`，反查背包物品 |
| 装备槽至多放一件装备 | 装备 → 槽位，1:1 | `ExclusiveRelation`；新装备接管时解除旧装备 |
| 从背包直接保存多个物品 | 背包 → 多件物品，1:N，物品持有者唯一 | `MultiRelation` + `ExclusiveTarget`，示例中的 `Carries` |
| 多个角色共享多个技能 | 角色 → 多个技能，M:N | `MultiRelation`；同一技能允许被多个角色指向 |

背包优先采用“物品指向背包”的单目标建模：物品的唯一归属由单值保证，查背包内容复用框架反向索引，转移物品直接重绑目标。

```csharp
em.AddRelationship<StoredIn>(apple, bag);
em.AddRelationship<StoredIn>(coin, bag);
Entity[] contents = em.GetRelationsOf<StoredIn>(bag);
em.AddRelationship<StoredIn>(apple, otherBag);
```

堆叠数保存在物品组件 `RelationshipItem.Count`；`StoredIn.Slot` 展示单目标关系的附带数据，重绑目标时保留。容量、槽位唯一性和堆叠合并属于背包业务规则，这些字段本身不提供对应约束。

## 演示内容

| 入口 | 核对行为 |
|---|---|
| [SingleTargetScenarios.cs](SingleTargetScenarios.cs) / `ShowBackpack` | 精确反查、同目标重复 Add、转移保留数据、显式解除、Added/Set/Removed 顺序；普通目标删除保留物品 |
| `ShowEquipment` | 1:1 装备接管及旧装备解除 |
| `ShowHierarchy` | 祖先/后代遍历、成环拒绝、声明式级联删除与外部实体保留 |
| `ShowJobsAndCopies` | Job 只执行当前匹配实体、ECB 回放前后区别、组内 Target 重映射、Prefab 外部目标保留、快照恢复与继续解除 |
| [MultiTargetScenarios.cs](MultiTargetScenarios.cs) | 两种 M:N 分别运行 C++/ISPC 精确筛选、正向目标快照、主线程/并行 ECB、组复制、Prefab、恢复和删除 |
| [EdgeDataScenario.cs](EdgeDataScenario.cs) | 每条角色—技能边独立的等级、冷却；原生 Job 更新、两个端点重映射及端点删除时的边清理 |
| [RelationScenarioDemo](../09_ECS/Demos/RelationScenarioDemo.cs) | 父子层级、索敌、托管 M:N、定长多槽 M:N、直接出边式背包；定长槽读取核对 C++/ISPC 的 IJobChunk/IJobEntity 四条路径 |

每个场景拥有独立 World，多目标基础展示复用已有 `RelationScenarioDemo`。实体集合按完整 ID/Version 核对，反向集合不依赖枚举顺序。

## 纯连接的 M:N

```csharp
[MultiRelation]
public struct KnowsSkill : IRelationComponent { public RelationSlot Target; }

em.AddRelationship<KnowsSkill>(mage, fire);
em.AddRelationship<KnowsSkill>(mage, ice);
em.AddRelationship<KnowsSkill>(knight, fire);
Entity[] mageSkills = em.GetRelationships<KnowsSkill>(mage);
Entity[] fireUsers = em.GetRelationsOf<KnowsSkill>(fire);
em.RemoveRelationship<KnowsSkill>(mage, ice);
em.ClearRelationships<KnowsSkill>(knight);
```

同一 source/target 的重复 Add 保持原状态；读取同时检查 ID 和版本。一个角色有多个技能，一个技能被多个角色共享，形成 M:N。加上 `ExclusiveTarget` 后，一个目标只允许一个持有者，形成直接出边式 1:N。

需要固定的小容量并让 Job 直接读槽时，使用定长列：

```csharp
[MultiRelation(MaxSlots = 4)]
public partial struct HotSkillSlot : IRelationComponent { public RelationSlot Target; }
```

源生成器补充 Slot1 至 Slot3。四槽满后第五条边在修改前被拒绝，解除一条边后复用空槽。`ClearRelationships` 清空边并保留列；`RemoveComponentRaw(source, typeof(HotSkillSlot))` 同时清边并移除列。列表类型的 RemoveComponent 同样清除正反状态。

## 每条边独立的业务数据

技能实体保存共享定义，角色—技能关联的等级、冷却保存在边实体的普通组件上：

```csharp
[OnTargetDeleted(Cascade = true)]
public struct SkillUser : IRelationComponent { public RelationSlot Target; }
[OnTargetDeleted(Cascade = true)]
public struct SkillDefinition : IRelationComponent { public RelationSlot Target; }
public struct SkillProgress : IComponentData { public int Level; public int Cooldown; }

var edge = em.NewEntity(typeof(SkillProgress));
em.AddRelationship<SkillUser>(edge, mage);
em.AddRelationship<SkillDefinition>(edge, fire);
em.SetComponent(edge, new SkillProgress { Level = 3, Cooldown = 5 });
```

`GetRelationsOf<SkillUser>(mage)` 返回边实体；从每条边读取 SkillDefinition 得到技能，读取 SkillProgress 得到独立进度。两个角色可以指向同一技能，各自保留不同等级、冷却。两个端点都声明 Cascade，删除角色或技能时销毁关联边，另一个端点继续存活。

边实体允许相同端点之间存在多条关联。若业务要求同一角色的同一技能只有一条进度记录，创建前反查已有边并核对 SkillDefinition，由业务决定更新还是拒绝。创建包含多次 ECS 调用，各次写入分别提交；样例 CreateEdge 在失败时回收新实体，这个业务组合不构成整批事务。

## 查询与 Native Job

```csharp
var query = new QueryBuilder().WithAll<RelationshipItem>()
    .WithRelationship<KnowsSkill>(fire);
int users = world.GetOrCreateEntityQuery(query).CalculateEntityCount();
new VisitSkillUsersCpp().Schedule(query, world).Complete();
```

列表和定长列都按完整目标身份筛选，同一 source 只执行一次；可以组合 WithEnabled。改边后重新收集匹配结果。调度时生成关系位图，C++/ISPC 使用原生位图，内核不访问托管 Dictionary。

列表的 Execute 参数只列业务组件，原始列表不作为关系列传入。`world.Query<RelationshipItem>().WithRelationship<KnowsSkill>(fire)` 可以枚举业务组件；列表没有第二组件列，读取该结果的 ReadOnlyComp1 会拒绝。定长列可通过 `in HotSkillSlot` 或 Chunk 的只读 getter 读取；可写关系 ref/Span/NativeArray 被拒绝。

带数据的边按端点筛选，Execute 处理 SkillProgress：

```csharp
var userSkills = new QueryBuilder().WithAll<SkillProgress>()
    .WithRelationship<SkillUser>(mage);
new TickSkillCooldownCpp().Schedule(userSkills, world).Complete();
```

这样只更新 mage 的技能进度，其他角色的同一技能不受影响。IJobEntity 自动应用掩码；手写 IJobChunk 按 ChunkEnabledMask 遍历匹配实体。

## 正反向结果交给 Job

```csharp
using var targets = new NativeList<Entity>(4);
em.CopyRelationships<KnowsSkill>(mage, targets);
using var ids = new NativeArray<int>(targets.Length);
new ReadTargetsCpp
{
    Targets = targets.AsArray().AsReadOnly(),
    Ids = ids
}.Schedule(ids.Length, 1).Complete();
```

CopyRelationships 复制正向目标，CopyRelationsOf 复制反向来源，列表归调用方所有，可转只读 NativeArray 交给 Managed/C++/ISPC Job；Complete 返回后再 Dispose。正反向 Span 重载返回所需总长度，缓冲不足时写入可容纳的前缀，调用方扩容后重试。

复制结果是当时的身份列表，后续改图不会修改这份结果。需要当前图时重新复制。若 Job 还要读目标组件，调用方同时登记对应组件和生命周期依赖；复制 Entity 数组不自动授权任意组件访问。

## ECB 与并行记录

```csharp
using var commands = new DeferredCommandBuffer();
commands.AddRelationship<KnowsSkill>(mage, fire);
commands.RemoveRelationship<KnowsSkill>(mage, ice);
commands.Playback(em);
commands.EnsureParallelWriters(2);
commands.CreateParallelWriter(0).ClearRelationships<KnowsSkill>(10, mage);
commands.CreateParallelWriter(1).AddRelationship<KnowsSkill>(20, mage, fire);
commands.PlaybackParallel(em);
```

记录阶段不改图，回放时检查身份和容量。AddRelationship 对单目标建立/重绑，对多目标追加；SetRelationshipTarget 专用于单目标重绑。带 target 的 RemoveRelationship 删除一条多目标边；ClearRelationships 清空该类型的全部多目标边。无 target 的 RemoveRelationship 对单目标移除列，对多目标清空边。

ParallelWriter 由托管 Job 记录，sortKey 决定冲突命令顺序。同 writer、同 key 保持记录顺序；相同 key 的跨 writer 冲突不承诺胜者。共享 writer 使用预留容量。C++/ISPC 内核的 ECB 方法不做自动转译。

每条关系命令完整提交，ECB 整份批次不是事务。命令失败时保留已提交前缀、消费本批记录，CommandPlaybackException 给出失败下标、writer 与未执行数量；不会自动重放前缀。Observer 新记录的命令留到下一次回放。

## 复制、Prefab 与恢复

```csharp
var copies = em.CloneEntities(new[] { mage, fire });
var instances = em.SpawnFrom(prefab, 2);
var snapshot = world.TakeSnapshot();
em.ClearRelationships<KnowsSkill>(mage);
world.Restore(snapshot);
```

列表和定长槽的每个内部目标映射到相应副本，外部普通目标保留。外部已占用的 ExclusiveRelation/ExclusiveTarget 在复制前拒绝，原持有者不被接管；把目标包含在复制集合中即可使用新目标。Prefab 实例去除标记，各自建立索引。

CloneEntities 只复制输入集合，不沿图扩张。带业务数据的边需要显式包含边实体，如 `CloneEntities(new[] { mage, fire, mageFire })`；两个端点随关系 Target 一起映射。其他业务组件中的普通 Entity 字段按值复制。

快照格式 EJSNAP03 保存组件值和托管目标列表；恢复先在临时状态检查端点、重复目标、独占和无环约束，再重建索引并切换。准备失败保留当前 World，成功后旧借用失效，Restore 不逐边重放 Observer。

## 事件、删除和失败

| 操作 | 事件与结果 |
|---|---|
| 多目标添加新边 | 每条边一次 Added |
| 重复添加同一边 | 无事件，无关系版本变化 |
| 移除边 / ClearRelationships | 每条有效边一次 Removed |
| source 销毁 | 每条有效出边一次 Destroyed，不重复发送 Removed |
| 普通 target 销毁 | 存活 source 解除对应边并发送 Removed |
| 声明 Cascade 的 target 销毁 | source 加入同一销毁闭包，迭代去重、防环 |
| 独占目标转移 | 旧 source Removed、新 source Added；回调看到完整提交后的图 |

多目标 Observer 按边提供 TRel 值：Target 标识本次边的目标，定长类型的其他槽为规范空值。单目标事件携带完整业务值。事件值只在回调期间有效；回调中同步重入 ECS 变更会被拒绝，需要继续变更时记录 ECB。

端点、容量或准备阶段失败保持原图，空准备存储可以作为缓存保留。用户回调异常不回滚已提交图。阶段故障注入不构成进程终止或任意系统内存耗尽的回滚保证。

## 支持边界

- `[MultiRelation]` 使用托管列表，没有声明固定槽位上限，供主线程增删和查询；不能在 Job 中直接读取列表。
- `[MultiRelation(MaxSlots = N)]` 使用 N 个连续 `RelationSlot` 的关系列，可由 Job 只读访问；槽满时拒绝继续添加。示例核对四槽满后第五条边被拒绝，原有边及反向索引保留。
- 列表和定长列均支持精确筛选、ECB、组复制、Prefab 与快照；原始列表不直接借给 worker。
- 纯目标集合只保存目标身份，独立等级、冷却等采用边实体；背包容量、槽位唯一性和堆叠规则属于业务。
- 关系不自动提供 Transform 传播或跨 World 身份检查。Entity 的 ID/Version 属于调用 World 的身份域。
- 关系类型为 sequential unmanaged struct，不叠加 Shared、Enableable 或资源 hook。快照对共享、Enabled 与拥有资源的普通组件沿用明确边界。
- 只读 Span/ref 与原始地址仍是借用，结构变化、恢复或 World 释放后重新获取。反向集合不保证顺序，遍历 k 个结果为 O(k)。批量复制和删除有准备成本，需要计入批次预算。

完整线程、所有权和生命周期边界见[运行时契约](../../../docs/public/Runtime-Contracts-and-Known-Limitations.md)。
