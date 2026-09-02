using System;
using EntJoy.Collections;
using EntJoy.ECS;

namespace EntJoySample.ECS
{
    /// <summary>
    /// 内存分析器示例：展示 MemoryReport 的原生分配/释放/泄漏、Chunk 数、碎片率、slab 占用，
    /// 并对比碎片整理前后 + World.Dispose 后的报告。
    /// </summary>
    public static class MemoryProfilerDemo
    {
        public struct ProfilerBuffer : IComponentData, IDisposable
        {
            public NativeArray<int> Data;
            public void Dispose() => Data.Dispose();
        }

        public static void Run()
        {
            Console.WriteLine("=== 内存分析器 Demo ===\n");
            var world = new World("MemoryProfiler");
            const int N = 2000;

            // 创建实体 + 分配 buffer
            var entities = world.CreateEntities(N, typeof(ProfilerBuffer), typeof(Velocity));
            for (int i = 0; i < N; i++)
            {
                ref var buf = ref world.EntityManager.GetComponent<ProfilerBuffer>(entities[i]);
                buf.Data = new NativeArray<int>(16, Allocator.Persistent);
                buf.Data[0] = i;
            }

            // 制造碎片：移除 80% 的 Velocity
            for (int i = 0; i < N; i++)
                if (i % 5 != 0)
                    world.EntityManager.RemoveComponent<Velocity>(entities[i]);

            PrintReport("碎片整理前", world.GetMemoryReport());

            world.EntityManager.CompactChunks();

            PrintReport("碎片整理后", world.GetMemoryReport());

            world.Dispose();

            PrintReport("World.Dispose 后", world.GetMemoryReport());

            Console.WriteLine("\n=== 内存分析器 Demo Complete ===\n");
        }

        private static void PrintReport(string label, MemoryReport r)
        {
            Console.WriteLine($"--- {label} ---");
            Console.WriteLine($"  原生内存: alloc={r.NativeAllocs}, free={r.NativeFrees}, 在用/泄漏={r.NativeLeakEstimate}, foreign={r.NativeForeign}");
            Console.WriteLine($"  未释放容器: {r.LeakedContainers}");
            Console.WriteLine($"  Chunk: 总数={r.TotalChunkCount}, 瘦={r.ThinChunkCount}, 实体={r.TotalEntityCount}");
            Console.WriteLine($"  Slab: {r.TotalSlabBytes / 1024} KB ({r.TotalSlabBytes / 65536} 个 slab)");
            foreach (var a in r.Archetypes)
            {
                Console.WriteLine($"    [{a.TypeSignature}] chunk={a.ChunkCount}, 实体={a.EntityCount}, 容量={a.Capacity}, 利用率={a.Utilization:P0}, slab={a.SlabBytes / 1024}KB");
            }
        }
    }
}
