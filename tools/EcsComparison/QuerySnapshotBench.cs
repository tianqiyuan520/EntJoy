using System;
using System.Diagnostics;
using EntJoy.ECS;

/// <summary>
/// 查询快照重建成本：一次结构变更后的首次查询（重建快照）与紧随其后的查询（复用快照）成对测量。
/// 逐帧结构变更的世界每查询每帧付一次重建；批量结构变更的 --struct 看不到这部分。
/// </summary>
internal static class QuerySnapshotBench
{
    public static void Run(int entities = 100_000, int rounds = 300)
    {
        using var world = new World("query-snapshot");
        var manager = world.EntityManager;
        for (int i = 0; i < entities; i++) manager.NewEntity(typeof(A), typeof(B));

        for (int i = 0; i < 8; i++) Iterate(world);
        for (int i = 0; i < 32; i++) Churn(manager);   // 预热结构变更入口，首次 JIT 不进计时
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long allocRebuild = 0, allocReuse = 0;
        double usRebuild = 0, usReuse = 0;
        for (int i = 0; i < rounds; i++)
        {
            Churn(manager);
            usRebuild += Timed(world, out long rebuildBytes);
            allocRebuild += rebuildBytes;
            usReuse += Timed(world, out long reuseBytes);
            allocReuse += reuseBytes;
        }

        Console.WriteLine($"QSNAP_HEAD entities={entities} rounds={rounds}");
        Console.WriteLine($"QSNAP_ROW phase=after-change us={usRebuild / rounds:F2} bytes={allocRebuild / rounds}");
        Console.WriteLine($"QSNAP_ROW phase=reuse us={usReuse / rounds:F2} bytes={allocReuse / rounds}");
        Console.WriteLine($"QSNAP_DELTA rebuild_us={(usRebuild - usReuse) / rounds:F2}");
    }

    private static void Churn(EntityManager manager)
    {
        var entity = manager.NewEntity(typeof(A), typeof(B));
        manager.DestroyEntity(entity);
    }

    private static double Timed(World world, out long bytes)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        Iterate(world);
        double us = (Stopwatch.GetTimestamp() - start) * 1_000_000.0 / Stopwatch.Frequency;
        bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        return us;
    }

    private static int Iterate(World world)
    {
        int count = 0;
        foreach (var item in world.Query<A, B>())
        {
            item.Comp0.Value += item.ReadOnlyComp1.Value;
            count++;
        }
        return count;
    }
}
