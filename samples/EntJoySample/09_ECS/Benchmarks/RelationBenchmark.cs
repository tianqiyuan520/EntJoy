using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using System;
using System.Diagnostics;

namespace EntJoySample.ECS
{
    /// <summary>
    /// 关系基准：Relation SoA 列性能基线。
    /// 测量：10 万实体 AddRelationship / GetRelationship / HasRelationship / WithRelationship 查询。
    /// </summary>
    public static unsafe class RelationBenchmark
    {
        public static void Run()
        {
            Console.WriteLine("=== Relation Benchmark ===\n");

            const int entityCount = 100000;

            using var world = new World("RelationBenchmark");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            // 构造：10 万子实体 + 100 个父实体
            var parents = new Entity[100];
            for (int i = 0; i < parents.Length; i++)
                parents[i] = em.NewEntity(typeof(Position));

            Console.WriteLine($"Creating {entityCount} children (with ChildOf column)...");
            var children = new Entity[entityCount];
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < entityCount; i++)
                children[i] = em.NewEntity(typeof(Position), typeof(ChildOf));  // 预建关系列，AddRelationship 走 SetRaw 8B
            sw.Stop();
            Console.WriteLine($"  CreateEntities: {sw.Elapsed.TotalMilliseconds:F2} ms\n");

            // ===== 1. AddRelationship（10 万次，纯 SetRaw 覆盖）=====
            const int warmup = 2000;
            // 预热
            for (int i = 0; i < warmup; i++)
                em.AddRelationship<ChildOf>(children[i], parents[i % parents.Length]);

            sw.Restart();
            for (int i = 0; i < entityCount; i++)
                em.AddRelationship<ChildOf>(children[i], parents[i % parents.Length]);
            sw.Stop();
            double addMs = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine($"AddRelationship x{entityCount} (SetRaw 8B): {addMs,8:F2} ms total, {addMs / entityCount * 1000:F3} us/op\n");

            // ===== 2. GetRelationship（10 万次）=====
            sw.Restart();
            for (int i = 0; i < entityCount; i++)
            {
                var t = em.GetRelationship<ChildOf>(children[i]);
                if (t.Id < 0) throw new InvalidOperationException("GetRelationship returned invalid");
            }
            sw.Stop();
            double getMs = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine($"GetRelationship x{entityCount}: {getMs,8:F2} ms total, {getMs / entityCount * 1000:F3} us/op");

            // ===== 3. HasRelationship（10 万次）=====
            sw.Restart();
            for (int i = 0; i < entityCount; i++)
            {
                if (!em.HasRelationship<ChildOf>(children[i])) throw new InvalidOperationException("HasRelationship false");
            }
            sw.Stop();
            double hasMs = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine($"HasRelationship x{entityCount}: {hasMs,8:F2} ms total, {hasMs / entityCount * 1000:F3} us/op\n");

            // ===== 4. WithRelationship 查询（按父实体过滤）=====
            // 每父 ~1000 子实体
            sw.Restart();
            long total = 0;
            for (int rep = 0; rep < 10; rep++)
            {
                var parent = parents[rep];
                int count = 0;
                foreach (var r in world.Query<Position>().WithRelationship<ChildOf>(parent))
                    count++;
                total += count;
            }
            sw.Stop();
            double queryMs = sw.Elapsed.TotalMilliseconds / 10;
            Console.WriteLine($"WithRelationship query (10 parents x ~1000 children): {queryMs,8:F2} ms/query, total matched {total}");
            Console.WriteLine($"  ({entityCount / parents.Length} children per parent expected)\n");

            // ===== 5. 级联删除：DestroyEntityCascade（索引 O(1)）=====
            Console.WriteLine("--- 5. Cascade destroy ---");
            // 重建 100 个父实体，各带 ~1000 子
            var parents2 = new Entity[100];
            for (int i = 0; i < parents2.Length; i++)
                parents2[i] = em.NewEntity(typeof(Position));
            var children2 = new Entity[entityCount];
            for (int i = 0; i < entityCount; i++)
            {
                children2[i] = em.NewEntity(typeof(Position), typeof(ChildOf));
                em.AddRelationship<ChildOf>(children2[i], parents2[i % parents2.Length]);
            }

            sw.Restart();
            for (int i = 0; i < parents2.Length; i++)
                em.DestroyEntityCascade(parents2[i]);  // 每父级联销毁 ~1000 子
            sw.Stop();
            double cascadeMs = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine($"DestroyEntityCascade x{parents2.Length} parents (each ~{entityCount / parents2.Length} children): {cascadeMs,8:F2} ms total");
            Console.WriteLine($"  ({cascadeMs / parents2.Length * 1000:F1} us/parent, 包含 {entityCount / parents2.Length} 个子实体销毁)\n");

            // ===== 6. 反向查询：GetRelationsOf（索引 O(1)）=====
            Console.WriteLine("--- 6. GetRelationsOf (reverse index O(1)) ---");
            // 重建 1 父 + 10000 子
            var hub = em.NewEntity(typeof(Position));
            var hubChildren = new Entity[10000];
            for (int i = 0; i < hubChildren.Length; i++)
            {
                hubChildren[i] = em.NewEntity(typeof(Position), typeof(ChildOf));
                em.AddRelationship<ChildOf>(hubChildren[i], hub);
            }

            sw.Restart();
            for (int rep = 0; rep < 100; rep++)
            {
                var sources = em.GetRelationsOf<ChildOf>(hub);
                if (sources.Length != hubChildren.Length) throw new InvalidOperationException("GetRelationsOf count mismatch");
            }
            sw.Stop();
            double getRelMs = sw.Elapsed.TotalMilliseconds / 100;
            Console.WriteLine($"GetRelationsOf x{hubChildren.Length} (100 iters): {getRelMs,8:F4} ms/iter ({getRelMs * 1000:F2} us, O(1) 索引查表)\n");

            // ===== 7. 关系遍历基准 =====
            Console.WriteLine("--- 7. Traversal ---");
            // 深链：chain of 10000（每实体一个父，测 GetAncestors 单链爬升）
            var chainRoot = em.NewEntity(typeof(Position));
            var chainNode = chainRoot;
            for (int i = 0; i < 10000; i++)
            {
                var next = em.NewEntity(typeof(Position), typeof(ChildOf));
                em.AddRelationship<ChildOf>(next, chainNode);
                chainNode = next;
            }

            sw.Restart();
            var ancestors = em.GetAncestors<ChildOf>(chainNode);
            sw.Stop();
            Console.WriteLine($"GetAncestors depth=10000: {sw.Elapsed.TotalMilliseconds,8:F3} ms ({ancestors.Length} nodes)");

            // 宽树：hub + 10000 children，测 GetDescendants BFS
            var wideRoot = em.NewEntity(typeof(Position));
            for (int i = 0; i < 10000; i++)
            {
                var c = em.NewEntity(typeof(Position), typeof(ChildOf));
                em.AddRelationship<ChildOf>(c, wideRoot);
            }

            sw.Restart();
            var desc = em.GetDescendants<ChildOf>(wideRoot);
            sw.Stop();
            Console.WriteLine($"GetDescendants breadth=10000: {sw.Elapsed.TotalMilliseconds,8:F3} ms ({desc.Length} nodes)");

            sw.Restart();
            for (int rep = 0; rep < 1000; rep++)
            {
                _ = em.GetSiblings<ChildOf>(hubChildren[rep % hubChildren.Length]);
            }
            sw.Stop();
            Console.WriteLine($"GetSiblings x1000 (hub 10000 children): {sw.Elapsed.TotalMilliseconds,8:F3} ms ({sw.Elapsed.TotalMilliseconds:F3} us/op)");

            Console.WriteLine("=== End Relation Benchmark ===\n");
        }

        /// <summary>
        /// IJobEntity 直接访问关系列验证（步长一致性）。
        /// Execute(ref Position, in ChildOf) → adapter 生成 GetComponentDataSpan&lt;ChildOf&gt;()，
        /// 若 ChildOf 是空 struct（1B）则步长错位，Target 读错；含 RelationSlot 字段（8B）则正确。
        /// </summary>
        public static void VerifyIJobEntityRelationAccess()
        {
            Console.WriteLine("=== IJobEntity Relation Access ===\n");

            using var world = new World("RelJobVerify");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            // 父实体（每个子实体关系指向特定父，Id 各不相同）
            var parents = new Entity[3];
            for (int i = 0; i < parents.Length; i++)
                parents[i] = em.NewEntity(typeof(Position));

            // 5 个孩子，各自关系指向不同父
            var children = new Entity[5];
            for (int i = 0; i < children.Length; i++)
            {
                children[i] = em.NewEntity(typeof(Position), typeof(ChildOf));
                em.AddRelationship<ChildOf>(children[i], parents[i % parents.Length]);
            }

            // IJobEntity：读每个实体的 ChildOf.Target.TargetId 累加
            var sum = 0L;
            var sumJob = new RelSumJob { SumPtr = &sum };
            sumJob.Run(new QueryBuilder().WithAll<Position, ChildOf>());

            // 预期：5 个孩子的 target 分别为 parents[0],1,2,0,1 → id 和
            long expected = 0;
            for (int i = 0; i < children.Length; i++)
                expected += parents[i % parents.Length].Id;

            Console.WriteLine($"Sum of ChildOf.Target.TargetId: {sum} (expected {expected})");
            if (sum != expected)
                throw new InvalidOperationException($"IJobEntity relation access FAILED: got {sum}, expected {expected} (stride mismatch!)");
            Console.WriteLine("  OK: IJobEntity relation access stride correct\n");
        }

        /// <summary>
        /// IJobChunk 直接访问关系列验证（步长一致性）。
        /// Execute(ArchetypeChunk) → chunk.GetComponentDataSpan&lt;ChildOf&gt;()，
        /// 逐槽读 ChildOf.Target.TargetId 累加；步长错位则 sum 错。
        /// </summary>
        public static void VerifyIJobChunkRelationAccess()
        {
            Console.WriteLine("=== IJobChunk Relation Access (verify) ===\n");

            using var world = new World("RelChunkJobVerify");
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
                children[i] = em.NewEntity(typeof(Position), typeof(ChildOf));
                em.AddRelationship<ChildOf>(children[i], parents[i % parents.Length]);
            }

            // IJobChunk：累加所有 ChildOf.Target.TargetId
            var sum = 0L;
            var chunkJob = new RelSumChunkJob { SumPtr = &sum };
            chunkJob.Run(new QueryBuilder().WithAll<Position, ChildOf>());

            // 预期：8 个孩子 target = parents[0],1,2,3,0,1,2,3 → id 和
            long expected = 0;
            for (int i = 0; i < children.Length; i++)
                expected += parents[i % parents.Length].Id;

            Console.WriteLine($"IJobChunk sum of ChildOf.Target.TargetId: {sum} (expected {expected})");
            if (sum != expected)
                throw new InvalidOperationException($"IJobChunk relation access FAILED: got {sum}, expected {expected} (stride mismatch!)");
            Console.WriteLine("  OK: IJobChunk relation access stride correct\n");
        }
    }

    /// <summary>IJobEntity：读关系列（步长验证）。</summary>
    public unsafe struct RelSumJob : IJobEntity
    {
        public long* SumPtr;

        public void Execute(ref Position position, in ChildOf childOf)
        {
            *SumPtr += childOf.Target.TargetId;
        }
    }

    /// <summary>IJobChunk：读关系列（步长验证）。</summary>
    public unsafe struct RelSumChunkJob : IJobChunk
    {
        public long* SumPtr;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            var childOfs = chunk.GetComponentDataSpan<ChildOf>();
            for (int i = 0; i < chunk.Count; i++)
            {
                *SumPtr += childOfs[i].Target.TargetId;
            }
        }
    }
}
