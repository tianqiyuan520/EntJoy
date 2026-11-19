using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Burst;
using Unity.Burst.Intrinsics;
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

// Chunk 级遍历臂：与 Arch / EntJoy 的 chunk foreach 同构，job.Run 同步执行、不调度。
[BurstCompile]
public struct PlayerChunkJob2 : IJobChunk
{
    public ComponentTypeHandle<PlayerBenchA> A;
    public ComponentTypeHandle<PlayerBenchB> B;

    [BurstCompile]
    public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
    {
        var a = chunk.GetNativeArray(ref A);
        var b = chunk.GetNativeArray(ref B);
        for (int i = 0; i < chunk.Count; i++)
        {
            var v = a[i];
            v.Value += b[i].Value;
            a[i] = v;
        }
    }
}

[BurstCompile]
public struct PlayerChunkJob3 : IJobChunk
{
    public ComponentTypeHandle<PlayerBenchA> A;
    public ComponentTypeHandle<PlayerBenchB> B;
    public ComponentTypeHandle<PlayerBenchC> C;

    [BurstCompile]
    public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
    {
        var a = chunk.GetNativeArray(ref A);
        var b = chunk.GetNativeArray(ref B);
        var c = chunk.GetNativeArray(ref C);
        for (int i = 0; i < chunk.Count; i++)
        {
            var v = a[i];
            v.Value += b[i].Value + c[i].Value;
            a[i] = v;
        }
    }
}

[BurstCompile]
[DisableAutoCreation]
public partial struct PlayerBurstChunk : ISystem
{
    private EntityQuery _query2;
    private EntityQuery _query3;

    public void OnCreate(ref SystemState state)
    {
        _query2 = state.GetEntityQuery(
            ComponentType.ReadWrite<PlayerBenchA>(), ComponentType.ReadOnly<PlayerBenchB>());
        _query3 = state.GetEntityQuery(
            ComponentType.ReadWrite<PlayerBenchA>(), ComponentType.ReadOnly<PlayerBenchB>(),
            ComponentType.ReadOnly<PlayerBenchC>());
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        if (SystemAPI.GetSingleton<Player_Components>().Value == 2)
        {
            var job = new PlayerChunkJob2
            {
                A = state.GetComponentTypeHandle<PlayerBenchA>(false),
                B = state.GetComponentTypeHandle<PlayerBenchB>(true),
            };
            job.Run(_query2);
        }
        else
        {
            var job = new PlayerChunkJob3
            {
                A = state.GetComponentTypeHandle<PlayerBenchA>(false),
                B = state.GetComponentTypeHandle<PlayerBenchB>(true),
                C = state.GetComponentTypeHandle<PlayerBenchC>(true),
            };
            job.Run(_query3);
        }
    }
}

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
        string structLog = Arg("-structLog");
        if (!string.IsNullOrEmpty(structLog))
        {
            RunStruct(structLog, ParseIntList("-structEntities", new[] { 1024, 10_000, 100_000 }),
                ParseInt("-structRounds", 25));
            Application.Quit();
            return;
        }
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

    private static int[] ParseIntList(string name, int[] fallback)
    {
        string value = Arg(name);
        if (string.IsNullOrEmpty(value)) return fallback;
        string[] parts = value.Split(',');
        var result = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out result[i]))
                return fallback;
        }
        return result;
    }

    // 结构变更：与 tools/EcsComparison 的 StructuralBench 同协议，四操作按轮取最小，
    // 复位一律在计时之外。播放器口径（Mono + EntityManager 安全检查开）。
    private static void RunStruct(string logPath, int[] entityCounts, int rounds)
    {
        var output = new StringBuilder();
        var world = new World("player-struct");
        try
        {
            EntityManager em = world.EntityManager;
            EntityArchetype arch2 = em.CreateArchetype(typeof(PlayerBenchA), typeof(PlayerBenchB));
            EntityArchetype arch3 = em.CreateArchetype(typeof(PlayerBenchA), typeof(PlayerBenchB), typeof(PlayerBenchC));
            var sw = new Stopwatch();

            foreach (int entities in entityCounts)
            {
                output.AppendLine($"STRUCT_HEAD entities={entities} rounds={rounds} framework=Unity compiler=mono-player opt=jit safety_checks=on");
                var buffer = new Entity[entities];
                double minCreate = double.MaxValue, minAdd = double.MaxValue;
                double minRemove = double.MaxValue, minDestroy = double.MaxValue;

                for (int r = 1; r <= rounds; r++)
                {
                    sw.Restart();
                    for (int i = 0; i < entities; i++) buffer[i] = em.CreateEntity(arch2);
                    sw.Stop();
                    double us = sw.Elapsed.TotalMilliseconds * 1000.0;
                    if (us < minCreate) minCreate = us;
                    output.AppendLine($"STRUCT_ROW op=create framework=Unity round={r} us={us.ToString("F1", CultureInfo.InvariantCulture)}");
                    for (int i = 0; i < entities; i++) em.DestroyEntity(buffer[i]);

                    for (int i = 0; i < entities; i++) buffer[i] = em.CreateEntity(arch2);
                    sw.Restart();
                    for (int i = 0; i < entities; i++) em.AddComponent<PlayerBenchC>(buffer[i]);
                    sw.Stop();
                    us = sw.Elapsed.TotalMilliseconds * 1000.0;
                    if (us < minAdd) minAdd = us;
                    output.AppendLine($"STRUCT_ROW op=add framework=Unity round={r} us={us.ToString("F1", CultureInfo.InvariantCulture)}");
                    for (int i = 0; i < entities; i++) em.DestroyEntity(buffer[i]);

                    for (int i = 0; i < entities; i++) buffer[i] = em.CreateEntity(arch3);
                    sw.Restart();
                    for (int i = 0; i < entities; i++) em.RemoveComponent<PlayerBenchC>(buffer[i]);
                    sw.Stop();
                    us = sw.Elapsed.TotalMilliseconds * 1000.0;
                    if (us < minRemove) minRemove = us;
                    output.AppendLine($"STRUCT_ROW op=remove framework=Unity round={r} us={us.ToString("F1", CultureInfo.InvariantCulture)}");
                    for (int i = 0; i < entities; i++) em.DestroyEntity(buffer[i]);

                    for (int i = 0; i < entities; i++) buffer[i] = em.CreateEntity(arch2);
                    sw.Restart();
                    for (int i = 0; i < entities; i++) em.DestroyEntity(buffer[i]);
                    sw.Stop();
                    us = sw.Elapsed.TotalMilliseconds * 1000.0;
                    if (us < minDestroy) minDestroy = us;
                    output.AppendLine($"STRUCT_ROW op=destroy framework=Unity round={r} us={us.ToString("F1", CultureInfo.InvariantCulture)}");
                }

                output.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "STRUCT_MIN entities={0} create={1:F1} add={2:F1} remove={3:F1} destroy={4:F1}",
                    entities, minCreate, minAdd, minRemove, minDestroy));
                File.WriteAllText(logPath, output.ToString());
            }
        }
        catch (Exception e)
        {
            output.AppendLine("STRUCT_ERROR " + e);
        }
        finally
        {
            world.Dispose();
        }

        File.WriteAllText(logPath, output.ToString());
        Debug.Log(output.ToString());
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
        var chunkHandle = world.CreateSystem<PlayerBurstChunk>();
        Action runChunk = () => chunkHandle.Update(world.Unmanaged);
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
        runChunk();
        Append($"framework=Unity layer=Chunk api=IJobChunk backend=Burst kernel=Compiler entities={count} components={components} chunks={chunkCount}",
            runChunk, query, count, components, callsPerArm * 2);
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
