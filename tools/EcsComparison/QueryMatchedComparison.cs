using System;

public static class QueryMatchedComparison
{
    public static void Run()
    {
        foreach (int components in new[] { 2, 3 })
        {
            Measure(components, "EntJoyQuery", true, false);
            Measure(components, "EntJoyChunk", true, true);
            Measure(components, "ArchInlineQuery", false, false);
            Measure(components, "ArchChunk", false, true);
        }
    }

    private static void Measure(int components, string name, bool entJoy, bool chunk)
    {
        var probe = new SystemComparison { Components = components };
        try
        {
            probe.Setup();
            int baseline = entJoy ? 4 : 2;
            Action run = entJoy
                ? (chunk ? probe.EntJoyChunk : probe.EntJoy)
                : (chunk ? probe.ArchChunk : probe.Arch);
            double[] times = ComparisonTiming.Measure(run);
            int calls = ComparisonTiming.Warmup + ComparisonTiming.Samples * ComparisonTiming.Repetitions;
            if (entJoy) probe.VerifyEnt(baseline + calls);
            else probe.VerifyArch(baseline + calls);
            Console.WriteLine($"QUERY_MATCHED method={name} components={components} baseline_calls={baseline} warmup_calls={ComparisonTiming.Warmup} timed_calls={calls - ComparisonTiming.Warmup} {ComparisonTiming.Describe(times)}");
        }
        finally
        {
            probe.Cleanup();
        }
    }
}
