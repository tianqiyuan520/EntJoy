using System;
using System.Collections.Generic;
using System.Linq;
using EntJoy.Collections;
using EntJoy.ECS;

namespace EntJoySample.ECS
{
    /// <summary>
    /// Chunk 碎片整理验证：制造碎片（移除 80%，产生多个瘦 Chunk）→ CompactChunks →
    /// 验证 Chunk 数下降、实体总数不变、查询结果完全一致、Disposable 搬移走 move 无泄漏。
    /// </summary>
    public static class ChunkDefragDemo
    {
        public struct DefragBuffer : IComponentData, IDisposable
        {
            public NativeArray<int> Data;
            public void Dispose() => Data.Dispose();
        }

        public static void Run()
        {
            Console.WriteLine("=== Chunk 碎片整理 Demo ===\n");
            Scenario1_FragmentAndCompact();
            Scenario2_DisposableHookBalance();
            Console.WriteLine("\n=== Chunk 碎片整理 Demo Complete ===\n");
        }

        /// <summary>场景 1：制造碎片 + 整理 + 验证 Chunk 数/实体数/查询快照。</summary>
        private static void Scenario1_FragmentAndCompact()
        {
            Console.WriteLine("--- 场景 1：碎片制造 + 整理 ---");
            var world = new World("Defrag1");
            const int N = 2000;

            var entities = world.CreateEntities(N, typeof(Position), typeof(Velocity));
            for (int i = 0; i < N; i++)
            {
                ref var pos = ref world.EntityManager.GetComponent<Position>(entities[i]);
                pos.X = i;
            }

            // 移除 i % 5 != 0 的 Velocity（80%）→ 每个 Chunk 剩 ~20%，产生多个瘦 Chunk
            for (int i = 0; i < N; i++)
                if (i % 5 != 0)
                    world.EntityManager.RemoveComponent<Velocity>(entities[i]);

            int beforeChunks = TotalChunkCount(world);
            int beforeEntities = world.EntityManager.EntityCount;
            var beforeSnapshot = Snapshot(world);

            world.EntityManager.CompactChunks();

            int afterChunks = TotalChunkCount(world);
            int afterEntities = world.EntityManager.EntityCount;
            var afterSnapshot = Snapshot(world);

            Console.WriteLine($"  整理前: chunk={beforeChunks}, 实体={beforeEntities}");
            Console.WriteLine($"  整理后: chunk={afterChunks}, 实体={afterEntities}");
            Console.WriteLine($"  [Position,Velocity] 快照: 前 {beforeSnapshot.Count} / 后 {afterSnapshot.Count}");

            bool snapEqual = beforeSnapshot.SetEquals(afterSnapshot);

            bool pass = afterChunks < beforeChunks
                && afterEntities == beforeEntities
                && snapEqual;
            Console.WriteLine(pass ? "  [PASS] Chunk 数下降、实体不变、查询快照一致" : "  [FAIL] 存在不一致");
            world.Dispose();
        }

        /// <summary>场景 2：Disposable 组件在整理搬移中走 move 语义，PersistentAllocator 平衡。</summary>
        private static void Scenario2_DisposableHookBalance()
        {
            Console.WriteLine("--- 场景 2：Disposable 组件搬移平衡 ---");
            var world = new World("Defrag2");
            const int N = 2000;

            var before = PersistentAllocator.GetStats();

            var entities = world.CreateEntities(N, typeof(DefragBuffer), typeof(Velocity));
            for (int i = 0; i < N; i++)
            {
                ref var buf = ref world.EntityManager.GetComponent<DefragBuffer>(entities[i]);
                buf.Data = new NativeArray<int>(16, Allocator.Persistent);
                buf.Data[0] = i;
            }

            // 移除 80% 的 Velocity → 触发整理搬移（多个瘦 Chunk 合并）
            for (int i = 0; i < N; i++)
                if (i % 5 != 0)
                    world.EntityManager.RemoveComponent<Velocity>(entities[i]);

            int beforeChunks = TotalChunkCount(world);
            world.EntityManager.CompactChunks();
            int afterChunks = TotalChunkCount(world);

            world.Dispose();

            var after = PersistentAllocator.GetStats();
            int da = after.Allocs - before.Allocs;
            int df = after.Frees - before.Frees;
            int dfo = after.Foreign - before.Foreign;
            Console.WriteLine($"  chunk: {beforeChunks} -> {afterChunks}, Allocs +{da}, Frees +{df}, Foreign +{dfo}");
            Console.WriteLine(da == df ? "  [PASS] 整理搬移无泄漏（move 语义）" : $"  [FAIL] 不平衡，泄漏 {da - df}");
        }

        /// <summary>收集 [Position,Velocity] 所有实体的 Position.X 快照（X 唯一，用作实体标识）。</summary>
        private static SortedSet<float> Snapshot(World world)
        {
            var set = new SortedSet<float>();
            foreach (var r in world.Query<Position, Velocity>())
                set.Add(r.Comp0.X);
            return set;
        }

        private static int TotalChunkCount(World world)
        {
            int total = 0;
            foreach (var arch in world.EntityManager.GetAllArchetypes())
                if (arch != null) total += arch.ChunkCount;
            return total;
        }
    }
}
