using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace NativeTranspilerFixture;

[NativeTranspile]
public struct EmptyContextJob : IJob
{
    public void Execute() { }
}

public struct EightContextValues { public int A, B, C, D, E, F, G, H; }
public struct EightContextGroups { public EightContextValues A, B, C, D, E, F, G, H; }
public struct LargeContextValues { public EightContextGroups A, B, C, D, E; }

[NativeTranspile]
public struct LargeContextJob : IJobParallelFor
{
    public NativeArray<int> Values;
    public LargeContextValues Payload;
    public void Execute(int index)
    {
        Values[index] = Payload.E.H.H;
    }
}

public static class CopiedContextFixture
{
    public static void Run()
    {
        var empty = new EmptyContextJob().Schedule();
        if (empty.IsNull) throw new System.Exception("Empty context lost its completion handle.");
        empty.Complete();
        using var values = new NativeArray<int>(1031);
        var payload = new LargeContextValues();
        payload.E.H.H = 317;
        new LargeContextJob { Values = values, Payload = payload }.Schedule(values.Length, 64).Complete();
        for (int i = 0; i < values.Length; ++i)
            if (values[i] != 317) throw new System.Exception("Large context changed its payload.");
        using var visits = new NativeArray<int>(1031);
        new DispatchVisitJob { Visits = visits }.Schedule(visits.Length, 64, default, ClaimPolicy.Spread).Complete();
        for (int i = 0; i < visits.Length; ++i)
        {
            if (visits[i] != 1) throw new System.Exception("Copied indexed adapter changed coverage.");
        }
        visits.AsSpan().Clear();
        new BatchFillJob { Out = values, Visits = visits }.Schedule(values.Length, 64).Complete();
        for (int i = 0; i < values.Length; ++i)
            if (visits[i] != 1 || values[i] != System.Math.Min(64, values.Length - i / 64 * 64))
                throw new System.Exception("Copied Batch adapter changed its boundary.");
        System.Console.WriteLine("PASS: generated empty/large contexts, indexed coverage and Batch boundaries.");
    }
}
