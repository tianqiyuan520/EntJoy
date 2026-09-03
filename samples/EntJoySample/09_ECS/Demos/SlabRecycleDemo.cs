using System;
using EntJoy.ECS;

namespace EntJoySample.ECS
{
    /// <summary>
    /// slab 复用/压缩验证：创建大量实体填满多个 slab → 销毁全部（压缩回收空 slab）→
    /// 重新创建（复用空洞，slab 不超初次）。
    /// </summary>
    public static class SlabRecycleDemo
    {
        public static void Run()
        {
            Console.WriteLine("=== slab 复用/压缩 Demo ===\n");
            var world = new World("SlabRecycle");
            const int N = 20000;

            // 1. 创建大量实体，填满多个 slab
            var entities = world.CreateEntities(N, typeof(Position), typeof(Velocity));
            var r1 = world.GetMemoryReport();
            int slab1 = (int)(r1.TotalSlabBytes / 65536);
            Console.WriteLine($"创建 {N} 实体后: slab={slab1}, chunk={r1.TotalChunkCount}");

            // 2. 销毁全部实体 → 空 slab 归还（压缩）
            for (int i = 0; i < N; i++)
                world.EntityManager.DestroyEntity(entities[i]);
            var r2 = world.GetMemoryReport();
            int slab2 = (int)(r2.TotalSlabBytes / 65536);
            Console.WriteLine($"销毁全部后:   slab={slab2}, chunk={r2.TotalChunkCount}");

            // 3. 重新创建 → 复用空洞，slab 不超初次
            var entities2 = world.CreateEntities(N, typeof(Position), typeof(Velocity));
            var r3 = world.GetMemoryReport();
            int slab3 = (int)(r3.TotalSlabBytes / 65536);
            Console.WriteLine($"重新创建后:   slab={slab3}, chunk={r3.TotalChunkCount}");

            bool compressed = slab2 < slab1;      // 空 slab 已归还
            bool reused = slab3 <= slab1;         // 复用空洞，不超初次
            Console.WriteLine();
            Console.WriteLine(compressed && reused
                ? "[PASS] slab 复用/压缩生效（销毁回收 + 重建复用）"
                : "[FAIL] slab 未正确回收/复用");

            world.Dispose();
            Console.WriteLine("\n=== slab 复用/压缩 Demo Complete ===\n");
        }
    }
}
