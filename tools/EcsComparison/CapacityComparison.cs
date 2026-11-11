using BenchmarkDotNet.Attributes;
using EntJoy.ECS;

[MemoryDiagnoser]
public class CapacityComparison
{
    [Params(2, 3)] public int Components { get; set; }
    [Params(128, 1024)] public int Capacity { get; set; }
    private SystemComparison comparison;

    [GlobalSetup]
    public void Setup()
    {
        Archetype.ChunkCapacityOverride = Capacity;
        comparison = new SystemComparison { Components = Components };
        comparison.Setup();
    }

    [Benchmark] public void EntJoyChunk() => comparison.EntJoyChunk();
    [Benchmark] public void EntJoyCpp() => comparison.EntJoyJobCpp();
    [Benchmark] public void ArchChunk() => comparison.ArchChunk();

    [GlobalCleanup]
    public void Cleanup()
    {
        comparison.Cleanup();
        Archetype.ChunkCapacityOverride = 0;
    }
}
