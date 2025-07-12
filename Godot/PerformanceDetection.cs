
using System;

public static class PerformanceDetection
{
    public static int RecordCount = 0;
    public static DateTime start;
    public static DateTime end;

    public static double RecordTime = 0;

    public static void Start()
    {
        Reset();
        start = DateTime.Now;
    }

    public static void End()
    {
        end = DateTime.Now;
        Record((end - start).TotalMilliseconds);
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
