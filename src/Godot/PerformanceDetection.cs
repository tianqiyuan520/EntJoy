
using System;
using System.Diagnostics;

public static class PerformanceDetection
{
    public static int RecordCount = 0;
    public static Stopwatch stopwatch = new Stopwatch();

    public static double RecordTime = 0;

    public static void Start()
    {
        Reset();
        stopwatch.Restart();
    }

    public static void End()
    {
        stopwatch.Stop();
        Record(stopwatch.Elapsed.TotalMilliseconds);
    }

    public static void Record(double t)
    {
        RecordCount++;
        RecordTime += t;
    }

    public static double GetAverage()
    {
        return RecordTime / RecordCount;
    }

    public static void Reset()
    {
        RecordCount = 0;
        RecordTime = 0;
    }
}
