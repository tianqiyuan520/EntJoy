using EntJoy.Collections;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;
using NativeTranspiler;

// NuGet 消费冒烟测试（ECS 侧）：本工程只通过 PackageReference 消费 EntJoy，不引用仓库内任何路径。
//   [1] 托管 ECS：建实体 + QueryChunks 遍历
//   [2] 原生 JobSystem：包内 NativeDll.dll（props 从 runtimes\win-x64\native 复制到输出）
//   [3] [NativeTranspile] native job（包内分析器生成 C++，包内 tools + build\native 编出 NativeTranspiled.dll）
//       [3a] 数组 job（IJobParallelFor）与托管结果 parity
//       [3b] chunk job（IJobChunk）：原生转译版与托管版结果一致
//       [3c] 逐实体 job（IJobEntity）：原生转译版与托管版结果一致
//   断言一律按**全局实体索引**（chunk 内下标只是局部索引）

public struct Position : IComponentData
{
    public EntJoy.Mathematics.float2 Value;
}

public struct Velocity : IComponentData
{
    public EntJoy.Mathematics.float2 Value;
}

public struct ManagedAddJob : IJobParallelFor
{
    public NativeArray<int> Values;
    public int Delta;

    public void Execute(int index)
    {
        Values[index] += Delta;
    }
}

[NativeTranspile(Target = BackendTarget.Cpp)]
public struct NativeAddJob : IJobParallelFor
{
    public NativeArray<int> Values;
    public int Delta;

    public void Execute(int index)
    {
        Values[index] += Delta;
    }
}

[NativeTranspile(Target = BackendTarget.Cpp)]
public struct NativeMoveChunkJob : IJobChunk
{
    public float DeltaX;

    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        Span<Position> positions = chunk.GetComponentDataSpan<Position>();
        for (int i = 0; i < positions.Length; i++)
        {
            Position p = positions[i];
            p.Value = new EntJoy.Mathematics.float2(p.Value.x + DeltaX, p.Value.y);
            positions[i] = p;
        }
    }
}

public struct ManagedMoveChunkJob : IJobChunk
{
    public float DeltaX;

    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        Span<Position> positions = chunk.GetComponentDataSpan<Position>();
        for (int i = 0; i < positions.Length; i++)
        {
            Position p = positions[i];
            p.Value = new EntJoy.Mathematics.float2(p.Value.x + DeltaX, p.Value.y);
            positions[i] = p;
        }
    }
}

[NativeTranspile(Target = BackendTarget.Cpp)]
public struct NativeMoveEntityJob : IJobEntity
{
    public float DeltaX;

    public void Execute(ref Position position, in Velocity velocity)
    {
        position.Value = new EntJoy.Mathematics.float2(position.Value.x + DeltaX, position.Value.y);
    }
}

public struct ManagedMoveEntityJob : IJobEntity
{
    public float DeltaX;

    public void Execute(ref Position position, in Velocity velocity)
    {
        position.Value = new EntJoy.Mathematics.float2(position.Value.x + DeltaX, position.Value.y);
    }
}

public static class Program
{
    private const int EntityCount = 1000;
    private const int N = 4096;
    private const int Delta = 7;

    private static int _failures;

    public static int Main()
    {
        NativeJobScheduler.Initialize();
        try
        {
            RunEcsAndNativeJobs();
            RunNativeJobSmoke();
            RunTranspiledArrayJobParity();

            Console.WriteLine(_failures == 0
                ? "PASS: NuGet ECS consumer (managed ECS + native JobSystem + transpiled chunk/entity/array jobs)."
                : $"FAIL: {_failures} check(s) failed.");
            return _failures == 0 ? 0 : 1;
        }
        finally
        {
            NativeJobScheduler.Shutdown();
        }
    }

    // ---- [1] 托管 ECS + [3b] chunk job + [3c] entity job ----
    private static void RunEcsAndNativeJobs()
    {
        using var world = new World("NuGetConsumerWorld");
        World.DefaultWorld = world;
        ref EntityManager em = ref world.EntityManager;

        for (int i = 0; i < EntityCount; i++)
        {
            Entity e = em.NewEntity(typeof(Position), typeof(Velocity));
            em.Set(e, new Position { Value = new EntJoy.Mathematics.float2(i, 0) });
            em.Set(e, new Velocity { Value = new EntJoy.Mathematics.float2(1, 0) });
        }

        // [1] 托管遍历：每个实体 x += 1 ⇒ x = g + 1
        int entities = 0;
        foreach (var chunk in world.QueryChunks<Position, Velocity>())
        {
            Span<Position> positions = chunk.GetSpan0();
            Span<Velocity> velocities = chunk.GetSpan1();
            for (int i = 0; i < chunk.Length; i++)
            {
                Position p = positions[i];
                p.Value += velocities[i].Value;
                positions[i] = p;
            }
            entities += chunk.Length;
        }
        Check("[1] managed ECS chunk iteration", entities == EntityCount, $"entities={entities}");

        var query = new QueryBuilder().WithAll<Position, Velocity>();

        // [3b-1] 原生转译 chunk job：x += 3 ⇒ g + 4
        new NativeMoveChunkJob { DeltaX = 3f }.Schedule(query).Complete();
        int bad = CountPositionMismatch(world, 4f);
        Check("[3b] transpiled native chunk job (IJobChunk)", bad == 0, $"mismatched={bad}");

        // [3b-2] 托管 chunk job：x += 1 ⇒ g + 5（与原生版同形态，验证两者一致）
        new ManagedMoveChunkJob { DeltaX = 1f }.Schedule(query).Complete();
        bad = CountPositionMismatch(world, 5f);
        Check("[3b] managed chunk job parity (IJobChunk)", bad == 0, $"mismatched={bad}");

        // [3c-1] 原生转译逐实体 job：x += 10 ⇒ g + 15
        new NativeMoveEntityJob { DeltaX = 10f }.Schedule(query).Complete();
        bad = CountPositionMismatch(world, 15f);
        Check("[3c] transpiled native entity job (IJobEntity)", bad == 0, $"mismatched={bad}");

        // [3c-2] 托管逐实体 job：x += 1 ⇒ g + 16
        new ManagedMoveEntityJob { DeltaX = 1f }.Schedule(query).Complete();
        bad = CountPositionMismatch(world, 16f);
        Check("[3c] managed entity job parity (IJobEntity)", bad == 0, $"mismatched={bad}");
    }

    private static int CountPositionMismatch(World world, float expectedOffset)
    {
        int bad = 0;
        int globalIndex = 0;
        foreach (var chunk in world.QueryChunks<Position, Velocity>())
        {
            Span<Position> positions = chunk.GetSpan0();
            for (int i = 0; i < chunk.Length; i++)
            {
                if (positions[i].Value.x != globalIndex + expectedOffset) bad++;
                globalIndex++;
            }
        }
        return bad;
    }

    // ---- [2] 原生 JobSystem（包内 NativeDll.dll） ----
    private static void RunNativeJobSmoke()
    {
        var values = new NativeArray<int>(N, Allocator.Persistent);
        try
        {
            for (int i = 0; i < N; i++) values[i] = i;

            var job = new ManagedAddJob { Values = values, Delta = Delta };
            NativeJobHandle handle = NativeJobScheduler.ScheduleParallelFor(ref job, N, 64);
            NativeJobScheduler.Complete(ref handle);

            int mismatched = 0;
            for (int i = 0; i < N; i++)
            {
                if (values[i] != i + Delta) mismatched++;
            }
            Check("[2] native JobSystem (NativeDll.dll from package)", mismatched == 0, $"mismatched={mismatched}");
        }
        finally
        {
            values.Dispose();
        }
    }

    // ---- [3a] 转译后的数组 job 与托管结果 parity ----
    private static void RunTranspiledArrayJobParity()
    {
        var native = new NativeArray<int>(N, Allocator.Persistent);
        var managed = new NativeArray<int>(N, Allocator.Persistent);
        try
        {
            for (int i = 0; i < N; i++)
            {
                native[i] = i;
                managed[i] = i;
            }

            new NativeAddJob { Values = native, Delta = Delta }.Schedule(N, 64).Complete();

            var managedJob = new ManagedAddJob { Values = managed, Delta = Delta };
            NativeJobHandle h = NativeJobScheduler.ScheduleParallelFor(ref managedJob, N, 64);
            NativeJobScheduler.Complete(ref h);

            int mismatched = 0;
            for (int i = 0; i < N; i++)
            {
                if (native[i] != managed[i]) mismatched++;
            }
            Check("[3a] transpiled vs managed array-job parity", mismatched == 0, $"mismatched={mismatched}");
        }
        finally
        {
            native.Dispose();
            managed.Dispose();
        }
    }

    private static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine(ok ? $"  PASS {name}" : $"  FAIL {name}: {detail}");
        if (!ok) _failures++;
    }
}
