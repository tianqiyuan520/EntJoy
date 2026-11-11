using System;
using System.Diagnostics;

public static class ComparisonTiming
{
    public const int Warmup = 50;
    public const int Samples = 12;
    public const int Repetitions = 8192;

    public static double[] Measure(Action run)
        => Measure(run, Warmup, Repetitions);

    public static double[] Measure(Action run, int warmup, int repetitions)
    {
        for (int i = 0; i < warmup; i++) run();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
        var times = new double[Samples];
        for (int sample = 0; sample < times.Length; sample++)
        {
            var timer = Stopwatch.StartNew();
            for (int i = 0; i < repetitions; i++) run();
            timer.Stop();
            times[sample] = timer.Elapsed.TotalMilliseconds * 1000 / repetitions;
        }
        return times;
    }

    public static string Describe(double[] times)
        => Describe(times, Repetitions);

    public static string Describe(double[] times, int repetitions)
    {
        double sum = 0;
        foreach (double time in times) sum += time;
        Array.Sort(times);
        return $"mean_us={sum / times.Length:F3} median_us={(times[5] + times[6]) / 2:F3} min_us={times[0]:F3} max_us={times[11]:F3} samples={Samples} repetitions={repetitions} values_us={string.Join(",", Array.ConvertAll(times, t => t.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)))}";
    }
}
