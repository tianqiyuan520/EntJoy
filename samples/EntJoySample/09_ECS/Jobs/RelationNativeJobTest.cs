using EntJoy.ECS;
using NativeTranspiler;
using System;

namespace EntJoySample.ECS
{
    /// <summary>
    /// NativeTranspiler 关系访问验证：验证 [NativeTranspile] IJobChunk/IJobEntity 能否访问关系列
    /// （ChildOf 含 RelationSlot Target 字段，8B）。
    ///
    /// 关键链路：
    /// 1. CollectUserStructTypes 递归收集 ChildOf → RelationSlot → 生成两者 C++ 头文件
    /// 2. GetComponentDataNativeArray&lt;ChildOf&gt;() 翻译为 C++ 数组指针（元素 = ChildOf 8B）
    /// 3. .Target.TargetId 翻译为 C++ 字段访问（EntJoy::ECS::RelationSlot 需有 C++ 定义）
    /// 4. C++ 编译通过 + static_assert(8B) 通过 → 运行结果与 C# 一致
    ///
    /// 结果回传：job 把 TargetId 累加写入 SumComponent 组件列（NativeTranspile job 字段是值拷贝，
    /// 不能通过字段回传；写组件列最直接）。
    /// </summary>
    public static class RelationNativeJobTest
    {
        /// <summary>累加目标（job 写入，C# 读取核对）。</summary>
        public struct SumComponent : IComponentData
        {
            public long Value;
        }

        [NativeTranspile(Target = BackendTarget.Cpp)]
        public struct RelNativeChunkJob : IJobChunk
        {
            public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
            {
                EntJoy.Collections.NativeArray<ChildOf> rels = chunk.GetComponentDataNativeArray<ChildOf>();
                EntJoy.Collections.NativeArray<SumComponent> sums = chunk.GetComponentDataNativeArray<SumComponent>();
                for (int i = 0; i < rels.Length; i++)
                {
                    var r = rels[i];
                    var s = sums[i];
                    s.Value += r.Target.TargetId;
                    sums[i] = s;
                }
            }
        }

        [NativeTranspile(Target = BackendTarget.Cpp)]
        public struct RelNativeEntityJob : IJobEntity
        {
            public void Execute(ref SumComponent sum, in ChildOf childOf)
            {
                sum.Value += childOf.Target.TargetId;
            }
        }

        [NativeTranspile(Target = BackendTarget.Ispc)]
        public struct RelIspcChunkJob : IJobChunk
        {
            public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
            {
                EntJoy.Collections.NativeArray<ChildOf> rels = chunk.GetComponentDataNativeArray<ChildOf>();
                EntJoy.Collections.NativeArray<SumComponent> sums = chunk.GetComponentDataNativeArray<SumComponent>();
                for (int i = 0; i < rels.Length; i++)
                {
                    var r = rels[i];
                    var s = sums[i];
                    s.Value += r.Target.TargetId;
                    sums[i] = s;
                }
            }
        }

        [NativeTranspile(Target = BackendTarget.Ispc)]
        public struct RelIspcEntityJob : IJobEntity
        {
            public void Execute(ref SumComponent sum, in ChildOf childOf)
            {
                sum.Value += childOf.Target.TargetId;
            }
        }

        public static void Run()
        {
            Console.WriteLine("=== NativeTranspiler Relation Access Test ===\n");

            using var world = new World("RelNativeTest");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            // 4 个父实体
            var parents = new Entity[4];
            for (int i = 0; i < parents.Length; i++)
                parents[i] = em.NewEntity(typeof(Position));

            // 8 个孩子，关系指向 parents[0..3] 循环
            var children = new Entity[8];
            for (int i = 0; i < children.Length; i++)
            {
                children[i] = em.NewEntity(typeof(Position), typeof(ChildOf), typeof(SumComponent));
                em.AddRelationship<ChildOf>(children[i], parents[i % parents.Length]);
                em.Set(children[i], new SumComponent { Value = 0 });
            }

            long expected = 0;
            for (int i = 0; i < children.Length; i++)
                expected += parents[i % parents.Length].Id;

            var query = new QueryBuilder().WithAll<Position, ChildOf, SumComponent>();

            // ===== 1. NativeTranspile IJobChunk =====
            new RelNativeChunkJob().Run(query);
            long chunkSum = SumAll(em, children);
            Console.WriteLine($"Native IJobChunk sum of ChildOf.Target.TargetId: {chunkSum} (expected {expected})");
            if (chunkSum != expected)
                throw new InvalidOperationException($"Native IJobChunk relation access FAILED: got {chunkSum}, expected {expected}");

            // 清零重跑 IJobEntity
            foreach (var c in children) em.Set(c, new SumComponent { Value = 0 });

            // ===== 2. NativeTranspile IJobEntity =====
            new RelNativeEntityJob().Run(query);
            long entitySum = SumAll(em, children);
            Console.WriteLine($"Native IJobEntity sum of ChildOf.Target.TargetId: {entitySum} (expected {expected})");
            if (entitySum != expected)
                throw new InvalidOperationException($"Native IJobEntity relation access FAILED: got {entitySum}, expected {expected}");

            // ===== 3. ISPC IJobChunk =====
            foreach (var c in children) em.Set(c, new SumComponent { Value = 0 });
            new RelIspcChunkJob().Run(query);
            long ispcChunkSum = SumAll(em, children);
            Console.WriteLine($"ISPC IJobChunk sum of ChildOf.Target.TargetId: {ispcChunkSum} (expected {expected})");
            if (ispcChunkSum != expected)
                throw new InvalidOperationException($"ISPC IJobChunk relation access FAILED: got {ispcChunkSum}, expected {expected}");

            // ===== 4. ISPC IJobEntity =====
            foreach (var c in children) em.Set(c, new SumComponent { Value = 0 });
            new RelIspcEntityJob().Run(query);
            long ispcEntitySum = SumAll(em, children);
            Console.WriteLine($"ISPC IJobEntity sum of ChildOf.Target.TargetId: {ispcEntitySum} (expected {expected})");
            if (ispcEntitySum != expected)
                throw new InvalidOperationException($"ISPC IJobEntity relation access FAILED: got {ispcEntitySum}, expected {expected}");

            Console.WriteLine("  OK: NativeTranspiler relation access correct (C++/ISPC compiled, layout matched)\n");
        }

        private static long SumAll(EntityManager em, Entity[] children)
        {
            long s = 0;
            foreach (var c in children)
                s += em.GetComponent<SumComponent>(c).Value;
            return s;
        }
    }
}
