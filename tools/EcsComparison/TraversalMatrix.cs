using System;
using System.Globalization;
using System.Runtime.InteropServices;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;
using NativeTranspiler;

public struct EntChunkTwo : IJobChunk
{
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
    {
        var a = chunk.GetComponentDataSpan<A>();
        var b = chunk.GetComponentDataSpan<B>();
        SystemComparison.AddTwoSpans(a, b);
    }
}

public struct EntChunkThree : IJobChunk
{
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
    {
        var a = chunk.GetComponentDataSpan<A>();
        var b = chunk.GetComponentDataSpan<B>();
        var c = chunk.GetComponentDataSpan<C>();
        SystemComparison.AddThreeSpans(a, b, c);
    }
}

// 空 Execute：测 Job 逐 Chunk 调度的基础开销。
public struct EntChunkNoop : IJobChunk
{
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
    {
    }
}

[NativeTranspile(Target = BackendTarget.Cpp)]
public struct EntChunkTwoCpp : IJobChunk
{
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
    {
        var a = chunk.GetComponentDataSpan<A>();
        var b = chunk.GetComponentDataSpan<B>();
        int count = chunk.Count;
        for (int i = 0; i < count; i++) a[i].Value += b[i].Value;
    }
}

[NativeTranspile(Target = BackendTarget.Cpp)]
public struct EntChunkThreeCpp : IJobChunk
{
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
    {
        var a = chunk.GetComponentDataSpan<A>();
        var b = chunk.GetComponentDataSpan<B>();
        var c = chunk.GetComponentDataSpan<C>();
        int count = chunk.Count;
        for (int i = 0; i < count; i++) a[i].Value += b[i].Value + c[i].Value;
    }
}

public static class TraversalMatrix
{
    public static void Run(bool verifyOnly, bool chunkJobOnly = false, bool queryOnly = false, bool rawChunkOnly = false)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Console.WriteLine($"MATRIX_ENV framework=dotnet runtime={RuntimeInformation.FrameworkDescription.Replace(' ', '_')} os={RuntimeInformation.OSDescription.Replace(' ', '_')} arch_package=1.2.8.1-alpha chunk_override={Archetype.ChunkCapacityOverride} tiered_compilation={Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default"} gc_before_measure=full managed_vector_width={System.Numerics.Vector<int>.Count}");
        foreach (string name in new[] { nameof(EntChunkTwoCpp), nameof(EntChunkThreeCpp) })
            if (NativeJobScheduler.GetNativeExportPtr($"Get_SharpNative_Job__global_namespace__{name}_Execute_AdapterPtr") == IntPtr.Zero)
                throw new InvalidOperationException("Native chunk job export missing: " + name);

        int rounds = verifyOnly ? 1 : TraversalProtocol.Rounds;
        int first = rawChunkOnly ? 8 : chunkJobOnly ? 7 : 0;
        int last = queryOnly ? 1 : chunkJobOnly ? 8 : 10;
        for (int round = 0; round < rounds; round++)
        foreach (int count in verifyOnly ? new[] { 129 } : TraversalProtocol.EntityCounts)
        foreach (int components in new[] { 2, 3 })
        for (int index = first; index < last; index++)
            Measure(round, count, components, round % 2 == 0 ? index : first + last - 1 - index, verifyOnly);
    }

    private static void Measure(int round, int count, int components, int method, bool verifyOnly)
    {
        var probe = new SystemComparison { Components = components, EntityCount = count };
        try
        {
            probe.Setup();
            Action run;
            string name, framework = "EntJoy", layer = "Query", backend = "CSharp";
            switch (method)
            {
                case 0: run = probe.EntJoy; name = "foreach"; break;
                case 1: run = probe.Arch; framework = "Arch"; name = "InlineQuery"; break;
                case 2: run = probe.EntJoyJobCSharp; name = "IJobEntity.Run"; break;
                case 3: run = probe.EntJoyJobCpp; name = "IJobEntity.Run"; backend = "Cpp"; break;
                case 4: run = probe.EntJoyChunk; layer = "Chunk"; name = "foreach"; break;
                case 5: run = probe.ArchChunk; layer = "Chunk"; framework = "Arch"; name = "foreach"; break;
                case 6: run = probe.EntJoyChunkJobCSharp; layer = "Chunk"; name = "IJobChunk.Run"; break;
                case 7: run = probe.EntJoyChunkJobCpp; layer = "Chunk"; name = "IJobChunk.Run"; backend = "Cpp"; break;
                case 8: run = probe.EntJoyRawChunks; layer = "Chunk"; name = "Archetype.ChunkSpan"; break;
                default: run = probe.ArchCachedChunks; layer = "Chunk"; framework = "Arch"; name = "CachedQuery.foreach"; break;
            }
            bool entJoy = framework == "EntJoy";
            int baseline = entJoy ? 4 : 2;
            void Verify(int calls)
            {
                if (entJoy) probe.VerifyEnt(baseline + calls);
                else probe.VerifyArch(baseline + calls);
                probe.VerifyInputs(entJoy);
            }
            run();
            Verify(1);
            string kernel = backend == "Cpp" ? "Compiler" : layer == "Chunk" ? "ScalarRefs" : "Scalar";
            string metadata = $"round={round + 1} framework={framework} layer={layer} api={name} backend={backend} kernel={kernel} entities={count} components={components} chunks={(entJoy ? probe.ChunkCount() : probe.ArchChunkCount())}";
            if (verifyOnly)
            {
                Console.WriteLine($"MATRIX_VERIFIED {metadata}");
                return;
            }
            double[] times = ComparisonTiming.Measure(run, TraversalProtocol.Warmup, TraversalProtocol.Repetitions);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < TraversalProtocol.AllocationCalls; i++) run();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Verify(1 + TraversalProtocol.Warmup + ComparisonTiming.Samples * TraversalProtocol.Repetitions + TraversalProtocol.AllocationCalls);
            Console.WriteLine($"MATRIX_RESULT {metadata} warmup={TraversalProtocol.Warmup} bytes_per_call={allocated / (double)TraversalProtocol.AllocationCalls:F3} verified=true {ComparisonTiming.Describe(times, TraversalProtocol.Repetitions)}");
            if (method == 7 && allocated != 0)
                throw new InvalidOperationException($"Native chunk Run allocated {allocated} managed bytes after warmup: {metadata}");
        }
        finally { probe.Cleanup(); }
    }
}
