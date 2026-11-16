# ECS 关系示例

关系表达“谁指向谁”。例如：苹果指向背包，表示苹果放在这个背包里。

默认运行五个小场景，每步都有结果校验。`--advanced` 另外运行 C++/ISPC Job、ECB、复制、Prefab 和快照验证。

```powershell
dotnet build samples/EntJoySample/EntJoySample.csproj -c Release -p:ENTJOY_STARTUP=EntJoySample.Relationship.Program -o artifacts/relationship-sample
dotnet artifacts/relationship-sample/EntJoySample.dll
dotnet artifacts/relationship-sample/EntJoySample.dll --advanced
```

进阶模式需要已构建的 NativeDll 和 NativeTranspiled；默认模式支持托管调度器。

## 1. 物品放进背包

每件物品只属于一个背包。多个物品可以放进同一个背包。

```csharp
public struct StoredIn : IRelationComponent
{
    public RelationSlot Target;
    public int Slot; // 物品在背包中的格子号，由业务维护
}

em.AddRelationship<StoredIn>(apple, bag);             // 放进去
em.TryGetRelationship<StoredIn>(apple, out var owner); // 苹果在哪个背包？
var items = em.GetRelationsOf<StoredIn>(bag);          // 背包里有哪些物品？
em.AddRelationship<StoredIn>(apple, otherBag);         // 换背包，保留已有格子号
em.RemoveRelationship<StoredIn>(apple);               // 取出来，物品还在
```

普通关系只需实现 `IRelationComponent`；也可以显式添加 `[RelationshipComponent]`。首字段必须是 `RelationSlot Target`，其后可以保存单目标关系的业务数据。

重复放入同一个目标不产生变更。删除背包默认只解除关系，物品继续存在。背包格子是否冲突、容量是否足够、物品如何堆叠，由业务代码决定。

## 2. 手上只能拿一件装备

```csharp
[RelationshipComponent(ExclusiveTarget = true)]
public struct EquippedIn : IRelationComponent { public RelationSlot Target; }

em.AddRelationship<EquippedIn>(sword, hand);
em.AddRelationship<EquippedIn>(axe, hand); // 斧头接管，剑自动解除关系
```

`ExclusiveTarget` 表示同一种关系中，一个目标只能被一个来源持有。剑和斧头实体都继续存在。

## 3. 父子节点

```csharp
[RelationshipComponent(Hierarchy = true, DeleteWithTarget = true)]
public struct OwnedChildOf : IRelationComponent { public RelationSlot Target; }

em.AddRelationship<OwnedChildOf>(child, root);
em.AddRelationship<OwnedChildOf>(leaf, child);
var ancestors = em.GetAncestors<OwnedChildOf>(leaf); // child、root
em.DestroyEntity(root); // 一起删除 child、leaf
```

`Hierarchy` 保证一个子节点至多一个父节点，并拒绝直接或间接成环。`DeleteWithTarget` 单独控制删除联动；只声明 `Hierarchy = true` 时，删除父节点会解除关系，子节点继续存在。

## 4. 一个角色会多种技能

```csharp
[RelationshipComponent(Multiple = true)]
public struct KnowsSkill : IRelationComponent { public RelationSlot Target; }

em.AddRelationship<KnowsSkill>(mage, fire);
em.AddRelationship<KnowsSkill>(mage, ice);
em.AddRelationship<KnowsSkill>(knight, fire);
var skills = em.GetRelationships<KnowsSkill>(mage); // 火球、冰冻
var users = em.GetRelationsOf<KnowsSkill>(fire);    // 法师、骑士
em.RemoveRelationship<KnowsSkill>(mage, ice);      // 只忘掉冰冻
em.ClearRelationships<KnowsSkill>(mage);           // 忘掉所有技能
```

列表在主线程管理，目标保持插入顺序，相同目标只保存一次。Job 可以按目标筛选实体，读取目标列表时先用 `CopyRelationships` 复制到自己持有的 `NativeList<Entity>`，并保留到 Job 完成。

需要 Job 直接读取固定数量的目标时，声明容量：

```csharp
[RelationshipComponent(MaxTargets = 4)]
public partial struct FourSkills : IRelationComponent { public RelationSlot Target; }
```

生成器补齐其余三个连续槽位；`MaxTargets` 自动启用多目标，容量满后继续添加会被拒绝，已有关系保留。固定容量至少为 2；0 表示不指定固定容量。多目标关系只保存目标，独立等级、冷却等数据使用下一节的记录实体。

## 5. 每个角色的技能等级不同

一条“角色学会技能”的记录也是一个实体，普通组件保存等级和冷却，两个关系分别指向角色和技能：

```text
技能记录 ── SkillUser ───────→ 法师
         └─ SkillDefinition → 火球
         SkillProgress：等级 3，冷却 5
```

`EdgeDataScenario.cs` 的两个端点关系都声明 `DeleteWithTarget = true`，删除角色或技能时删除相关记录，另一端继续存在。创建失败会回收本次记录实体；多次业务调用各自提交。相同角色与技能之间是否允许重复记录，由业务决定。

## 统一声明规则

| 配置 | 用途 |
|---|---|
| 默认 | 每个来源至多一个目标，目标可被多个来源指向 |
| `Hierarchy = true` | 单父级并防环，适用于单目标关系 |
| `Multiple = true` | 一个来源保存多个目标，使用主线程列表 |
| `MaxTargets = N` | 自动启用多目标，使用 N 个 Job 可读的固定槽位 |
| `ExclusiveTarget = true` | 目标至多由一个来源持有，新来源接管时解除旧来源 |
| `DeleteWithTarget = true` | 删除目标时一并删除来源；多目标时任一目标删除都会触发 |

所有配置都写在同一个 `[RelationshipComponent(...)]` 中。层级与多目标组合、非法容量、漏写关系接口都会在类型注册时拒绝。

## 进阶使用边界

关系通过 EntityManager 或 ECB 修改，组件列通过只读 API 访问。空槽使用 `RelationSlot.Default`。`HasComponent` 只检查列存在，`HasRelationship` 或 `TryGetRelationship` 检查有效连接；只读视图也需要在重绑、结构变更、恢复之后重新获取。

ECB 回放按命令提交。失败会报告位置，已提交前缀保留。并行记录按 sortKey 排序，相同 key 的跨 writer 冲突不承诺胜者。

成组复制时，组内目标改指副本，组外普通目标保持不变；组外已经占用的独占目标会使复制被拒绝。需要独立目标时，将目标一起放进复制集合。Prefab 实例去除 Prefab 标记。

快照格式为 `EJSNAP03`，要求类型、模块及布局匹配；恢复会校验端点、独占和层级约束，成功后统一切换存储，失败保留当前状态。Observer 在完整提交后通知，快照恢复不逐边重放通知。

完整运行时边界见 [Runtime Contracts](../../../docs/public/Runtime-Contracts-and-Known-Limitations.md)。
