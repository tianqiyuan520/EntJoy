using System;
using EntJoy.ECS;
using NativeTranspiler;

namespace EntJoySample.ECS
{
    // ======================== 关系组件定义（四种形态） ========================

    /// <summary>父子层级（默认：出边单值 + 入边多源 = 多对一）。</summary>
    public struct SceneChildOf : IRelationComponent { public RelationSlot Target; }

    /// <summary>索敌（默认：多敌人指向同一目标 = 多对一）。</summary>
    public struct Targets : IRelationComponent { public RelationSlot Target; }

    /// <summary>技能（[MultiRelation] 托管模式：角色多技能 + 技能多角色 = M:N，主线程）。</summary>
    [MultiRelation]
    public struct KnowsSkill : IRelationComponent { public RelationSlot Target; }

    /// <summary>技能（[MultiRelation(MaxSlots=4)] 定长多槽列：M:N，Job 可读——NativeTranspiler 演示用）。
    /// 手写 4 槽字段（不经源生成器注入），隔离验证 NativeTranspiler 收集路径。</summary>
    [MultiRelation(MaxSlots = 4)]
    public partial struct HotSkillSlot : IRelationComponent
    {
        public RelationSlot Target;
        public RelationSlot Slot1;
        public RelationSlot Slot2;
        public RelationSlot Slot3;
    }

    /// <summary>背包持有（[MultiRelation] + [ExclusiveTarget]：角色多物品，物品唯一持有者 = 一对多）。
    /// 注意：多值关系存储只有 RelationSlot（8B），关系数据（如堆叠数）应放 target 实体组件上。</summary>
    [MultiRelation]
    [ExclusiveTarget]
    public struct Carries : IRelationComponent { public RelationSlot Target; }

    /// <summary>物品堆叠数（放物品实体组件上——ECS 数据归属原则，多值关系的额外数据不进关系存储）。</summary>
    public struct InventoryStack : IComponentData { public int Count; }

    /// <summary>技能 CD 累计（预留：定长多槽列 Job 演示用组件；当前示例仅演示主线程 API）。</summary>
    public struct SkillCdSum : IComponentData { public long Value; }

    /// <summary>
    /// 关系全场景示例：演示四种关系形态在真实游戏场景中的使用。
    ///
    /// 1. 父子层级（多对一）：GetAncestors / GetDescendants 遍历
    /// 2. 索敌（多对一）：多敌人指向同一目标 + WithRelationship 查询过滤
    /// 3. 技能 M:N：托管模式（主线程）+ 定长多槽列模式（Job 可读）
    /// 4. 背包（一对多 + 关系数据）：角色多物品、物品唯一持有者、堆叠数
    /// </summary>
    public static class RelationScenarioDemo
    {
        private static World _world = null!;

        public static void Run()
        {
            Console.WriteLine("=== Relation Scenario Demo ===\n");

            _world = new World("RelationScenario");
            var em = _world.EntityManager;

            TestHierarchy(em);
            TestTargeting(em);
            TestSkills(em);
            TestFixedSlotSkills(em);
            TestInventory(em);

            _world.Dispose();
            Console.WriteLine("\n=== Relation Scenario Demo Complete ===\n");
        }

        /// <summary>场景 1：父子层级（多对一）——根 → 子 → 孙。</summary>
        private static void TestHierarchy(EntityManager em)
        {
            Console.WriteLine("--- 1. Hierarchy (1:N via default relation) ---");

            var root = em.NewEntity(typeof(Position));
            var childA = em.NewEntity(typeof(Position));
            var childB = em.NewEntity(typeof(Position));
            var grandchild = em.NewEntity(typeof(Position));

            em.AddRelationship<SceneChildOf>(childA, root);
            em.AddRelationship<SceneChildOf>(childB, root);
            em.AddRelationship<SceneChildOf>(grandchild, childA);

            var descendants = em.GetDescendants<SceneChildOf>(root);
            var ancestors = em.GetAncestors<SceneChildOf>(grandchild);
            var siblings = em.GetSiblings<SceneChildOf>(childB);

            Console.WriteLine($"  root descendants: {descendants.Length} (expect 3)");
            Console.WriteLine($"  grandchild ancestors: {ancestors.Length} (expect 2: childA, root)");
            Console.WriteLine($"  childB siblings: {siblings.Length} (expect 1: childA)");

            // 按父查询：谁是 root 的直接子
            int directChildren = 0;
            foreach (var _ in _world.Query<Position>().WithRelationship<SceneChildOf>(root))
                directChildren++;
            Console.WriteLine($"  WithRelationship(root) matched: {directChildren} (expect 2)");

            Check(directChildren == 2, "hierarchy query");
            Check(descendants.Length == 3 && ancestors.Length == 2 && siblings.Length == 1, "hierarchy traversal");
        }

        /// <summary>场景 2：索敌（多对一）——3 个敌人指向同一目标，按目标过滤。</summary>
        private static void TestTargeting(EntityManager em)
        {
            Console.WriteLine("\n--- 2. Targeting (many-to-one) ---");

            var player = em.NewEntity(typeof(Position));
            var e1 = em.NewEntity(typeof(Position));
            var e2 = em.NewEntity(typeof(Position));
            var e3 = em.NewEntity(typeof(Position));

            em.AddRelationship<Targets>(e1, player);
            em.AddRelationship<Targets>(e2, player);
            em.AddRelationship<Targets>(e3, player);

            // 反向：谁在打玩家
            var attackers = em.GetRelationsOf<Targets>(player);
            Console.WriteLine($"  attackers of player: {attackers.Length} (expect 3)");

            // 查询过滤：所有正在攻击 player 的实体（含其组件）
            int matched = 0;
            foreach (var r in _world.Query<Position>().WithRelationship<Targets>(player))
                matched++;
            Console.WriteLine($"  WithRelationship(player) matched: {matched} (expect 3)");

            Check(attackers.Length == 3 && matched == 3, "targeting");
        }

        /// <summary>场景 3：技能（M:N）——两角色共享技能，技能被多角色掌握。</summary>
        private static void TestSkills(EntityManager em)
        {
            Console.WriteLine("\n--- 3. Skills (M:N via [MultiRelation]) ---");

            var mage = em.NewEntity(typeof(Position));
            var knight = em.NewEntity(typeof(Position));
            var fireball = em.NewEntity(typeof(Position));
            var shield = em.NewEntity(typeof(Position));
            var heal = em.NewEntity(typeof(Position));

            em.AddRelationship<KnowsSkill>(mage, fireball);
            em.AddRelationship<KnowsSkill>(mage, shield);
            em.AddRelationship<KnowsSkill>(knight, fireball);   // 共享火球
            em.AddRelationship<KnowsSkill>(knight, heal);

            var mageSkills = em.GetRelationships<KnowsSkill>(mage);
            var fireballUsers = em.GetRelationsOf<KnowsSkill>(fireball);

            Console.WriteLine($"  mage skills: {mageSkills.Length} (expect 2: fireball, shield)");
            Console.WriteLine($"  fireball users: {fireballUsers.Length} (expect 2: mage, knight)");

            Check(mageSkills.Length == 2 && fireballUsers.Length == 2, "M:N skills");
        }

        /// <summary>场景 3b：定长多槽列技能（[MultiRelation(MaxSlots=4)]）——主线程 API 演示。</summary>
        private static void TestFixedSlotSkills(EntityManager em)
        {
            Console.WriteLine("\n--- 3b. Fixed-slot Skills (M:N via [MultiRelation(MaxSlots=4)]) ---");

            var mage = em.NewEntity(typeof(Position), typeof(HotSkillSlot));
            var knight = em.NewEntity(typeof(Position), typeof(HotSkillSlot));
            var fireball = em.NewEntity(typeof(Position));
            var shield = em.NewEntity(typeof(Position));
            var heal = em.NewEntity(typeof(Position));
            var blink = em.NewEntity(typeof(Position));

            em.AddRelationship<HotSkillSlot>(mage, fireball);
            em.AddRelationship<HotSkillSlot>(mage, shield);
            em.AddRelationship<HotSkillSlot>(mage, heal);    // 槽 0,1,2
            em.AddRelationship<HotSkillSlot>(knight, fireball);  // 共享火球
            em.AddRelationship<HotSkillSlot>(knight, blink);

            var mageSkills = em.GetRelationships<HotSkillSlot>(mage);
            var fireballUsers = em.GetRelationsOf<HotSkillSlot>(fireball);
            Console.WriteLine($"  mage skills: {mageSkills.Length} (expect 3)");
            Console.WriteLine($"  fireball users: {fireballUsers.Length} (expect 2: mage, knight)");
            Check(mageSkills.Length == 3 && fireballUsers.Length == 2, "fixed-slot M:N");

            // 定长列宽验证：HotSkillSlot = 4 槽 × 8B = 32B（Job 步长一致性前提）
            var ct = ComponentTypeManager.GetComponentType(typeof(HotSkillSlot));
            Console.WriteLine($"  HotSkillSlot column width: {ct.Size} bytes (expect 32 = 4 slots x 8B), MaxSlots={ct.MultiRelationMaxSlots}");
            Check(ct.Size == 32 && ct.MultiRelationMaxSlots == 4, "fixed-slot column layout");

            // ===== NativeTranspiler 四路径：IJobChunk/IJobEntity × C++/ISPC 读定长多槽列 =====
            // mage 槽 {fireball, shield, heal, empty} → 期望 id 和 = fb+sh+heal
            // knight 槽 {fireball, blink, empty, empty} → 期望 id 和 = fb+blink
            long expectedMage = fireball.Id + shield.Id + heal.Id;
            long expectedKnight = fireball.Id + blink.Id;
            long totalExpected = expectedMage + expectedKnight;

            em.AddComponent(mage, new SkillCdSum { Value = 0 });
            em.AddComponent(knight, new SkillCdSum { Value = 0 });
            var query = new QueryBuilder().WithAll<Position, HotSkillSlot, SkillCdSum>();

            // 1. IJobChunk C++
            new FixedSlotRelNativeChunkJob().Run(query);
            long chunkCpp = ReadCdSum(em, mage) + ReadCdSum(em, knight);
            Console.WriteLine($"  IJobChunk C++   slot sum: {chunkCpp} (expected {totalExpected})");
            Check(chunkCpp == totalExpected, "fixed-slot IJobChunk C++");

            // 2. IJobEntity C++
            em.Set(mage, new SkillCdSum { Value = 0 });
            em.Set(knight, new SkillCdSum { Value = 0 });
            new FixedSlotRelNativeEntityJob().Run(query);
            long entityCpp = ReadCdSum(em, mage) + ReadCdSum(em, knight);
            Console.WriteLine($"  IJobEntity C++  slot sum: {entityCpp} (expected {totalExpected})");
            Check(entityCpp == totalExpected, "fixed-slot IJobEntity C++");

            // 3. IJobChunk ISPC
            em.Set(mage, new SkillCdSum { Value = 0 });
            em.Set(knight, new SkillCdSum { Value = 0 });
            new FixedSlotRelIspcChunkJob().Run(query);
            long chunkIspc = ReadCdSum(em, mage) + ReadCdSum(em, knight);
            Console.WriteLine($"  IJobChunk ISPC  slot sum: {chunkIspc} (expected {totalExpected})");
            Check(chunkIspc == totalExpected, "fixed-slot IJobChunk ISPC");

            // 4. IJobEntity ISPC
            em.Set(mage, new SkillCdSum { Value = 0 });
            em.Set(knight, new SkillCdSum { Value = 0 });
            new FixedSlotRelIspcEntityJob().Run(query);
            long entityIspc = ReadCdSum(em, mage) + ReadCdSum(em, knight);
            Console.WriteLine($"  IJobEntity ISPC slot sum: {entityIspc} (expected {totalExpected})");
            Check(entityIspc == totalExpected, "fixed-slot IJobEntity ISPC");

            // 槽满溢出演示：第 5 个技能应抛异常
            var fifth = em.NewEntity(typeof(Position));
            try
            {
                em.AddRelationship<HotSkillSlot>(mage, fifth);
                Check(false, "column full should throw");
            }
            catch (InvalidOperationException)
            {
                Console.WriteLine("  column full: AddRelationship threw (expect)");
            }
        }

        private static long ReadCdSum(EntityManager em, Entity e)
        {
            var info = em.GetEntityInfoRef(e.Id);
            var chunk = info.Archetype!.ChunkList[info.ChunkIndex];
            ref var s = ref chunk.GetComponent<SkillCdSum>(info.SlotInChunk, info.Archetype.GetComponentTypeIndex(typeof(SkillCdSum)));
            return s.Value;
        }

        /// <summary>场景 4：背包（一对多 + 关系数据）——角色多物品、物品唯一持有者、堆叠数。</summary>
        private static void TestInventory(EntityManager em)
        {
            Console.WriteLine("\n--- 4. Inventory (1:N outbound + exclusive target + data) ---");

            var hero = em.NewEntity(typeof(Position));
            var villager = em.NewEntity(typeof(Position));
            var sword = em.NewEntity(typeof(Position));
            var potion = em.NewEntity(typeof(Position));
            var bow = em.NewEntity(typeof(Position));

            // 英雄拾取三件物品
            em.AddRelationship<Carries>(hero, sword);
            em.AddRelationship<Carries>(hero, potion);
            em.AddRelationship<Carries>(hero, bow);

            var heroItems = em.GetRelationships<Carries>(hero);
            Console.WriteLine($"  hero items: {heroItems.Length} (expect 3)");

            // 关系数据（堆叠数）放物品实体组件上：ECS 数据归属原则
            em.AddComponent(sword, new InventoryStack { Count = 1 });
            var stackInfo = em.GetEntityInfoRef(sword.Id);
            var stackChunk = stackInfo.Archetype.ChunkList[stackInfo.ChunkIndex];
            ref var stack = ref stackChunk.GetComponent<InventoryStack>(
                stackInfo.SlotInChunk, stackInfo.Archetype.GetComponentTypeIndex(typeof(InventoryStack)));
            Console.WriteLine($"  sword stack count: {stack.Count} (expect 1)");

            // 村民尝试拾起 sword → 物品唯一持有者约束：英雄被解绑
            em.AddRelationship<Carries>(villager, sword);

            var villagerItems = em.GetRelationships<Carries>(villager);
            var heroAfter = em.GetRelationships<Carries>(hero);
            var swordOwner = em.GetRelationsOf<Carries>(sword);

            Console.WriteLine($"  villager items: {villagerItems.Length} (expect 1: sword)");
            Console.WriteLine($"  hero items after transfer: {heroAfter.Length} (expect 2)");
            Console.WriteLine($"  sword owner count: {swordOwner.Length} (expect 1)");

            Check(villagerItems.Length == 1 && heroAfter.Length == 2 && swordOwner.Length == 1, "exclusive target inventory");
        }

        private static void Check(bool condition, string name)
        {
            if (!condition)
                throw new InvalidOperationException($"[RelationScenarioDemo] FAILED: {name}");
            Console.WriteLine($"  OK: {name}");
        }
    }

    // ======================== NativeTranspiler 定长多槽列 Job（四路径） ========================
    // HotSkillSlot 列宽 32B（4 槽 × 8B），Job 按列宽步进直接读取——多值关系进 native 的关键验证。
    // 空槽 = RelationSlot.Default（TargetId = -1），Job 必须跳过（否则 -1 污染累加和）。
    // 每个 Job 把实体所有有效槽的 TargetId 累加写入 SkillCdSum 列，C# 侧核对。

    /// <summary>IJobChunk（C++ 后端）。</summary>
    [NativeTranspile(Target = BackendTarget.Cpp)]
    public struct FixedSlotRelNativeChunkJob : IJobChunk
    {
        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            EntJoy.Collections.NativeArray<HotSkillSlot> skills = chunk.GetComponentDataNativeArray<HotSkillSlot>();
            EntJoy.Collections.NativeArray<SkillCdSum> sums = chunk.GetComponentDataNativeArray<SkillCdSum>();
            for (int i = 0; i < skills.Length; i++)
            {
                var s = skills[i];
                var sum = sums[i];
                if (s.Target.TargetId >= 0) sum.Value += s.Target.TargetId;
                if (s.Slot1.TargetId >= 0) sum.Value += s.Slot1.TargetId;
                if (s.Slot2.TargetId >= 0) sum.Value += s.Slot2.TargetId;
                if (s.Slot3.TargetId >= 0) sum.Value += s.Slot3.TargetId;
                sums[i] = sum;
            }
        }
    }

    /// <summary>IJobEntity（C++ 后端）。</summary>
    [NativeTranspile(Target = BackendTarget.Cpp)]
    public struct FixedSlotRelNativeEntityJob : IJobEntity
    {
        public void Execute(ref SkillCdSum sum, in HotSkillSlot skills)
        {
            if (skills.Target.TargetId >= 0) sum.Value += skills.Target.TargetId;
            if (skills.Slot1.TargetId >= 0) sum.Value += skills.Slot1.TargetId;
            if (skills.Slot2.TargetId >= 0) sum.Value += skills.Slot2.TargetId;
            if (skills.Slot3.TargetId >= 0) sum.Value += skills.Slot3.TargetId;
        }
    }

    /// <summary>IJobChunk（ISPC 后端）。</summary>
    [NativeTranspile(Target = BackendTarget.Ispc)]
    public struct FixedSlotRelIspcChunkJob : IJobChunk
    {
        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            EntJoy.Collections.NativeArray<HotSkillSlot> skills = chunk.GetComponentDataNativeArray<HotSkillSlot>();
            EntJoy.Collections.NativeArray<SkillCdSum> sums = chunk.GetComponentDataNativeArray<SkillCdSum>();
            for (int i = 0; i < skills.Length; i++)
            {
                var s = skills[i];
                var sum = sums[i];
                if (s.Target.TargetId >= 0) sum.Value += s.Target.TargetId;
                if (s.Slot1.TargetId >= 0) sum.Value += s.Slot1.TargetId;
                if (s.Slot2.TargetId >= 0) sum.Value += s.Slot2.TargetId;
                if (s.Slot3.TargetId >= 0) sum.Value += s.Slot3.TargetId;
                sums[i] = sum;
            }
        }
    }

    /// <summary>IJobEntity（ISPC 后端）。</summary>
    [NativeTranspile(Target = BackendTarget.Ispc)]
    public struct FixedSlotRelIspcEntityJob : IJobEntity
    {
        public void Execute(ref SkillCdSum sum, in HotSkillSlot skills)
        {
            if (skills.Target.TargetId >= 0) sum.Value += skills.Target.TargetId;
            if (skills.Slot1.TargetId >= 0) sum.Value += skills.Slot1.TargetId;
            if (skills.Slot2.TargetId >= 0) sum.Value += skills.Slot2.TargetId;
            if (skills.Slot3.TargetId >= 0) sum.Value += skills.Slot3.TargetId;
        }
    }
}
