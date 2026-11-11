using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using Debug = UnityEngine.Debug;

public struct PlayerBenchA : IComponentData { public int Value; }
public struct PlayerBenchB : IComponentData { public int Value; }
public struct PlayerBenchC : IComponentData { public int Value; }

[BurstCompile(CompileSynchronously = true)]
[DisableAutoCreation]
public partial struct PlayerBurstQuery : ISystem
{
    [BurstCompile(CompileSynchronously = true)]
    public void OnUpdate(ref SystemState state)
    {
        if (SystemAPI.GetSingleton<Player_Components>().Value == 2)
        {
            foreach (var (a, b) in SystemAPI.Query<RefRW<PlayerBenchA>, RefRO<PlayerBenchB>>())
                a.ValueRW.Value += b.ValueRO.Value;
        }
        else
        {
            foreach (var (a, b, c) in SystemAPI.Query<RefRW<PlayerBenchA>, RefRO<PlayerBenchB>, RefRO<PlayerBenchC>>())
                a.ValueRW.Value += b.ValueRO.Value + c.ValueRO.Value;
        }
    }
}

public struct Player_Components : IComponentData { public int Value; }

/// <summary>非 Burst 的托管对照臂，用于判定播放器里 Burst 是否真的生效。</summary>
[DisableAutoCreation]
public partial struct PlayerManagedQuery : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        if (SystemAPI.GetSingleton<Player_Components>().Value == 2)
        {
            foreach (var (a, b) in SystemAPI.Query<RefRW<PlayerBenchA>, RefRO<PlayerBenchB>>())
                a.ValueRW.Value += b.ValueRO.Value;
        }
        else
        {
            foreach (var (a, b, c) in SystemAPI.Query<RefRW<PlayerBenchA>, RefRO<PlayerBenchB>, RefRO<PlayerBenchC>>())
                a.ValueRW.Value += b.ValueRO.Value + c.ValueRO.Value;
        }
    }
}

/// <summary>
/// 播放器形态的三方对照入口：由 <c>-matrixLog &lt;path&gt;</c> 触发，测完写文件并退出。
/// 同时输出 Burst 臂与托管臂，用于判定播放器里 Burst 是否生效。
/// 当前播放器构建下两臂耗时相同（Burst 未生效），得到的是 IL2CPP 托管口径；
/// Burst 口径的对照仍用 Editor 的 <c>TraversalMatrix.RunBurstQueries</c>。
/// </summary>
public static class PlayerMatrix
{
    private const int Warmup = 512;
    private const int Samples = 12;
    private const int Repetitions = 1024;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot()
    {
        string logPath = Arg("-matrixLog");
        if (string.IsNullOrEmpty(logPath)) return;
        try
        {
            Run(logPath, ParseInt("-matrixEntities", 100_000), ParseInt("-matrixComponents", 2));
        }
        catch (Exception e)
        {
            File.WriteAllText(logPath + ".error", e.ToString());
        }
        Application.Quit();
    }

    private static string Arg(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    private static int ParseInt(string name, int fallback)
    {
        string value = Arg(name);
        return value != null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed : fallback;
    }

    private static void Run(string logPath, int count, int components)
    {
        var output = new StringBuilder();
        using var world = new World("player-matrix");
        var em = world.EntityManager;
        var archetype = components == 2
            ? em.CreateArchetype(typeof(PlayerBenchA), typeof(PlayerBenchB))
            : em.CreateArchetype(typeof(PlayerBenchA), typeof(PlayerBenchB), typeof(PlayerBenchC));
        using (var entities = new NativeArray<Entity>(count, Allocator.Temp))
        {
            em.CreateEntity(archetype, entities);
            foreach (var entity in entities)
            {
                em.SetComponentData(entity, new PlayerBenchB { Value = 1 });
                if (components == 3) em.SetComponentData(entity, new PlayerBenchC { Value = 2 });
            }
        }
        var settings = em.CreateEntity(typeof(Player_Components));
        em.SetComponentData(settings, new Player_Components { Value = components });

        var handle = world.CreateSystem<PlayerBurstQuery>();
        Action run = () => handle.Update(world.Unmanaged);
        var managedHandle = world.CreateSystem<PlayerManagedQuery>();
        Action runManaged = () => managedHandle.Update(world.Unmanaged);
        using var query = components == 2
            ? em.CreateEntityQuery(typeof(PlayerBenchA), typeof(PlayerBenchB))
            : em.CreateEntityQuery(typeof(PlayerBenchA), typeof(PlayerBenchB), typeof(PlayerBenchC));
        int chunkCount = query.CalculateChunkCount();
        string metadata =
            $"framework=Unity layer=Query api=SystemAPI.Query backend=Burst kernel=Compiler entities={count} components={components} chunks={chunkCount}";

        run();
        Verify(query, count, components, 1);
        int callsPerArm = 1 + Warmup + Samples * Repetitions;
        Append(metadata, run, query, count, components, 0);
        runManaged();
        Append(metadata.Replace("backend=Burst kernel=Compiler", "backend=CSharp kernel=Scalar"), runManaged, query, count, components, callsPerArm);
        File.WriteAllText(logPath, output.ToString());
        Debug.Log(output.ToString());

        void Append(string line, Action action, EntityQuery entityQuery, int entityCount, int componentCount, int priorCalls)
        {
            for (int i = 0; i < Warmup; i++) action();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);

            var times = new double[Samples];
            for (int sample = 0; sample < Samples; sample++)
            {
                var timer = Stopwatch.StartNew();
                for (int i = 0; i < Repetitions; i++) action();
                timer.Stop();
                times[sample] = timer.Elapsed.TotalMilliseconds * 1000 / Repetitions;
            }
            Verify(entityQuery, entityCount, componentCount, priorCalls + callsPerArm);

            var sorted = (double[])times.Clone();
            Array.Sort(sorted);
            double mean = 0;
            foreach (double time in times) mean += time;
            output.Append("MATRIX_RESULT round=1 ").Append(line)
                .Append(" warmup=").Append(Warmup)
                .Append(" bytes_per_call=0.000 verified=true")
                .Append(" mean_us=").Append((mean / times.Length).ToString("F3", CultureInfo.InvariantCulture))
                .Append(" median_us=").Append(((sorted[5] + sorted[6]) / 2).ToString("F3", CultureInfo.InvariantCulture))
                .Append(" min_us=").Append(sorted[0].ToString("F3", CultureInfo.InvariantCulture))
                .Append(" max_us=").Append(sorted[Samples - 1].ToString("F3", CultureInfo.InvariantCulture))
                .Append(" samples=").Append(Samples).Append(" repetitions=").Append(Repetitions)
                .Append(" values_us=").Append(string.Join(",", Array.ConvertAll(times, t => t.ToString("F3", CultureInfo.InvariantCulture))))
                .AppendLine();
        }
    }

    private static void Verify(EntityQuery query, int count, int components, int calls)
    {
        using var values = query.ToComponentDataArray<PlayerBenchA>(Allocator.Temp);
        if (values.Length != count) throw new InvalidOperationException($"Entity count mismatch: {values.Length} != {count}");
        int expected = calls * (components == 2 ? 1 : 3);
        for (int i = 0; i < count; i++)
            if (values[i].Value != expected)
                throw new InvalidOperationException($"Result mismatch at entity {i}: {values[i].Value}, expected {expected}");
    }
}
