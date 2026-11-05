using EntJoy.Collections;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace NativeTranspilerFixture;

[NativeTranspile(Target = BackendTarget.Ispc)]
public struct IspcVariantJob : IJobParallelFor
{
    public NativeArray<int> Output;
    public bool First, Second;
    public void Execute(int index)
    {
        int value = index;
        if (First) value += 1;
        if (Second) value += 2;
        Output[index] = value;
    }
}

[NativeTranspile(Target = BackendTarget.Ispc, UseISPC_MT = true)]
public struct IspcTaskVariantJob : IJobParallelFor
{
    public NativeArray<int> Output;
    public bool First, Second;
    public void Execute(int index)
    {
        int value = index;
        if (First) value += 1;
        if (Second) value += 2;
        Output[index] = value;
    }
}

[NativeTranspile(Target = BackendTarget.Ispc)]
public struct IspcFillJob : IJobParallelFor
{
    public NativeArray<int> Output;
    public int Bias;
    public void Execute(int index) { Output[index] = index * 3 + Bias; }
}

[NativeTranspile(Target = BackendTarget.Ispc)]
public struct IspcFloatSelectionJob : IJobParallelFor
{
    public NativeArray<float> Input, Candidate, Output;
    public void Execute(int index)
    {
        float best = Input[index];
        float candidate = Candidate[index];
        if (best < candidate) best = candidate;
        Output[index] = best;
    }
}

public struct IspcEntityValue : IComponentData { public int Value; }

[NativeTranspile(Target = BackendTarget.Ispc)]
public struct IspcEntityJob : IJobEntity
{
    public int Bias;
    public void Execute(ref IspcEntityValue value) { value.Value = value.Value * 3 + Bias; }
}

[NativeTranspile(Target = BackendTarget.Ispc)]
public unsafe struct IspcOnceJob : IJob
{
    public NativeArray<int> Output;
    public void Execute() { System.Threading.Interlocked.Increment(ref ((int*)Output.GetUnsafePtr())[0]); }
}

public struct IspcSmallValue : IComponentData { public byte Value; }

[NativeTranspile(Target = BackendTarget.Ispc)]
public struct IspcChunkValueJob : IJobChunk
{
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
    {
        var values = chunk.GetComponentDataNativeArray<IspcSmallValue>();
        for (int i = 0; i < values.Length; i++)
        {
            var original = values[i];
            var cleared = original;
            cleared.Value = 0;
            values[i] = cleared;
            byte increment = 1;
            original.Value = (byte)(original.Value + increment);
            values[i] = original;
        }
    }
}

[NativeTranspile(Target = BackendTarget.Ispc)]
public struct IspcCounterJob : IJobFor
{
    public NativeArray<int> Output;
    public int Counter;
    public void Execute(int index) { Counter++; Output[index] = Counter; }
}

public struct IspcCounterValue : IComponentData { public int Value; }

[NativeTranspile(Target = BackendTarget.Ispc)]
public struct IspcCounterEntityJob : IJobEntity
{
    public int Counter;
    public void Execute(ref IspcCounterValue value) { Counter++; value.Value = Counter; }
}

[NativeTranspile(Target = BackendTarget.Ispc, UseISPC_MT = true)]
public struct IspcHelperMtJob : IJobParallelFor
{
    public NativeArray<int> Output;
    public bool Active;
    public void Execute(int index) { Output[index] = Fold(index) + (Active ? 10 : 20); }
    private static int Fold(int value)
    {
        for (int i = 0; i < 3; i++) value += i;
        return value;
    }
}

public static class IspcMethods
{
    [NativeTranspile(Target = BackendTarget.Ispc)]
    public static int Expression(int value) => value * 3 + 1;

    [NativeTranspile(Target = BackendTarget.Ispc)]
    public static int Fold(int seed)
    {
        int value = seed;
        for (int i = 0; i < 4; i++) value = value * 3 + i;
        return value;
    }

    [NativeTranspile(Target = BackendTarget.Ispc, UseISPC_MT = true)]
    public static void Series(NativeArray<int> output)
    {
        for (int i = 0; i < 19; i++) output[i] = i;
        for (int i = 0; i < 19; i++)
            for (int j = 0; j < 3; j++) output[i] += j;
    }
}

public static class IspcBackendFixture
{
    public static unsafe void Run()
    {
        CheckFloatSelection();
        using (var output = new NativeArray<int>(17))
        {
            foreach (bool first in new[] { false, true })
            foreach (bool second in new[] { false, true })
            {
                var single = new IspcVariantJob { Output = output, First = first, Second = second };
                var tasks = new IspcTaskVariantJob { Output = output, First = first, Second = second };
                for (int mode = 0; mode < 4; mode++)
                {
                    if (mode == 0) single.Run(output.Length);
                    else if (mode == 1) single.Schedule(output.Length, 7).Complete();
                    else if (mode == 2) tasks.Run(output.Length);
                    else tasks.Schedule(output.Length, 7).Complete();
                    for (int i = 0; i < output.Length; i++)
                        if (output[i] != i + (first ? 1 : 0) + (second ? 2 : 0))
                            throw new Exception("ISPC boolean variants must agree across Run/Schedule/tasks.");
                }
            }
        }
        using (var onceOutput = new NativeArray<int>(1))
        {
            var once = new IspcOnceJob { Output = onceOutput };
            once.Run();
            if (onceOutput[0] != 1) throw new Exception("ISPC IJob must execute once.");
            once.Schedule().Complete();
            if (onceOutput[0] != 2) throw new Exception("ISPC scheduled IJob must execute once.");
        }
        using (var counters = new NativeArray<int>(17))
        {
            var job = new IspcCounterJob { Output = counters };
            job.Run(17);
            for (int i = 0; i < 17; i++)
                if (counters[i] != i + 1) throw new Exception("ISPC mutable field binding mismatch.");
        }
        nint library = System.Runtime.InteropServices.NativeLibrary.Load(System.IO.Path.Combine(AppContext.BaseDirectory, "NativeTranspiled.dll"));
        try
        {
            var fold = (delegate* unmanaged[Cdecl]<int*, int>)System.Runtime.InteropServices.NativeLibrary.GetExport(library,
                "SharpNative_NativeTranspilerFixture_IspcMethods_Fold");
            for (int seed = -4; seed < 8; seed++)
            {
                int expected = seed;
                for (int i = 0; i < 4; i++) expected = expected * 3 + i;
                if (fold(&seed) != expected) throw new Exception("ISPC static method mismatch.");
            }
            var expression = (delegate* unmanaged[Cdecl]<int*, int>)System.Runtime.InteropServices.NativeLibrary.GetExport(library,
                "SharpNative_NativeTranspilerFixture_IspcMethods_Expression");
            for (int value = -4; value < 8; value++)
                if (expression(&value) != value * 3 + 1) throw new Exception("ISPC expression method mismatch.");
            using var seriesOutput = new NativeArray<int>(19);
            var series = (delegate* unmanaged[Cdecl]<int*, int, int, void>)System.Runtime.InteropServices.NativeLibrary.GetExport(library,
                "SharpNative_NativeTranspilerFixture_IspcMethods_Series_mt");
            foreach (int tasks in new[] { 0, 1, 3, 23 })
            {
                series((int*)seriesOutput.GetUnsafePtr(), 19, tasks);
                for (int i = 0; i < 19; i++)
                    if (seriesOutput[i] != i + 3) throw new Exception("ISPC task ordering or partition mismatch.");
            }
            foreach (bool active in new[] { false, true })
            {
                var job = new IspcHelperMtJob { Output = seriesOutput, Active = active };
                string variant = active ? "true" : "false";
                var kernel = (delegate* unmanaged[Cdecl]<void*, int, int, int, void>)System.Runtime.InteropServices.NativeLibrary.GetExport(library,
                    $"SharpNative_Job_NativeTranspilerFixture_IspcHelperMtJob_Execute_Batch_{variant}_mt");
                foreach (int tasks in new[] { 0, 1, 3, 23 })
                {
                    kernel(&job, 0, 19, tasks);
                    for (int i = 0; i < 19; i++)
                        if (seriesOutput[i] != i + 3 + (active ? 10 : 20)) throw new Exception("ISPC MT helper or bool variant mismatch.");
                }
            }
        }
        finally { System.Runtime.InteropServices.NativeLibrary.Free(library); }
        foreach (int length in new[] { 0, 1, 3, 7, 8, 15, 16, 17, 127, 128, 129, 1031 })
        {
            using var output = new NativeArray<int>(length);
            var job = new IspcFillJob { Output = output, Bias = 11 };
            job.Run(length);
            Check();
            for (int i = 0; i < length; i++) job.Output[i] = -1;
            job.Schedule(length, 3).Complete();
            Check();

            void Check()
            {
                for (int i = 0; i < length; i++)
                    if (output[i] != i * 3 + 11) throw new Exception($"ISPC array mismatch at length={length}, index={i}");
            }
        }

        int savedCapacity = Archetype.ChunkCapacityOverride;
        Archetype.ChunkCapacityOverride = 128;
        try
        {
            using var world = new World("ISPC entity boundary");
            World.DefaultWorld = world;
            var entities = world.EntityManager.CreateEntities(259, typeof(IspcEntityValue));
            for (int path = 0; path < 2; path++)
            {
                for (int i = 0; i < entities.Length; i++)
                    world.EntityManager.SetComponent(entities[i], new IspcEntityValue { Value = i });
                var job = new IspcEntityJob { Bias = 11 };
                var query = new QueryBuilder().WithAll<IspcEntityValue>();
                if (path == 0) job.Run(query); else job.Schedule(query).Complete();
                for (int i = 0; i < entities.Length; i++)
                    if (world.EntityManager.GetComponent<IspcEntityValue>(entities[i]).Value != i * 3 + 11)
                        throw new Exception($"ISPC entity mismatch at path={path}, index={i}");
            }
            var smallEntities = world.EntityManager.CreateEntities(259, typeof(IspcSmallValue));
            for (int path = 0; path < 2; path++)
            {
                for (int i = 0; i < smallEntities.Length; i++)
                    world.EntityManager.SetComponent(smallEntities[i], new IspcSmallValue { Value = (byte)(i % 200) });
                var job = new IspcChunkValueJob();
                var query = new QueryBuilder().WithAll<IspcSmallValue>();
                if (path == 0) job.Run(query); else job.Schedule(query).Complete();
                for (int i = 0; i < smallEntities.Length; i++)
                    if (world.EntityManager.GetComponent<IspcSmallValue>(smallEntities[i]).Value != i % 200 + 1)
                        throw new Exception($"ISPC chunk value-copy mismatch at path={path}, index={i}");
            }
            var counterEntities = world.EntityManager.CreateEntities(17, typeof(IspcCounterValue));
            new IspcCounterEntityJob().Run(new QueryBuilder().WithAll<IspcCounterValue>());
            var sequence = counterEntities.Select(entity => world.EntityManager.GetComponent<IspcCounterValue>(entity).Value).OrderBy(value => value).ToArray();
            if (!sequence.SequenceEqual(Enumerable.Range(1, 17))) throw new Exception("ISPC entity field writeback mismatch.");
        }
        finally { Archetype.ChunkCapacityOverride = savedCapacity; }
        Console.WriteLine("PASS: ISPC single jobs, arrays, static methods, tasks, chunk value copies and entities match the scalar reference.");
    }

    private static unsafe void CheckFloatSelection()
    {
        float[] edges = { BitConverter.Int32BitsToSingle(0x7FC12345), float.NegativeInfinity,
            float.PositiveInfinity, -1f, 0f, BitConverter.Int32BitsToSingle(int.MinValue), float.Epsilon, float.MaxValue };
        int checks = 0;
        foreach (int length in new[] { 0, 1, 7, 8, 9, 17, 33, 129 })
        {
            using var input = new NativeArray<float>(length);
            using var candidate = new NativeArray<float>(length);
            using var storage = new NativeArray<float>(length + 32);
            var output = NativeArray<float>.CreateView((float*)storage.GetUnsafePtr() + 3, length);
            for (int i = 0; i < length; i++)
            {
                ((float*)input.GetUnsafePtr())[i] = edges[i % edges.Length];
                ((float*)candidate.GetUnsafePtr())[i] = edges[(i + 3) % edges.Length];
            }
            Reset();
            var reference = new TranslationConditionalSelectionJob { Input = input, Candidate = candidate, Output = output };
            for (int i = 0; i < length; i++) reference.Execute(i);
            int[] expected = new ReadOnlySpan<int>(storage.GetUnsafePtr(), storage.Length).ToArray();
            foreach (int batch in new[] { 1, 7, 8, 31 })
            for (int path = 0; path < 2; path++)
            {
                Reset();
                var job = new IspcFloatSelectionJob { Input = input, Candidate = candidate, Output = output };
                if (path == 0) job.Run(length); else job.Schedule(length, batch).Complete();
                for (int i = 0; i < expected.Length; i++)
                {
                    int actual = ((int*)storage.GetUnsafePtr())[i];
                    if (actual != expected[i])
                        throw new Exception($"ISPC float selection: length={length}, batch={batch}, path={path}, offset={i}, actual=0x{actual:X8}, expected=0x{expected[i]:X8}");
                    checks++;
                }
            }
            void Reset() => new Span<int>(storage.GetUnsafePtr(), storage.Length).Fill(0x3E800000);
        }
        Console.WriteLine($"PASS: ISPC float selection comparisons={checks}; NaN payload, infinities, signed zero, subnormals and output guards match C#.");
    }
}
