using EntJoy.JobSystem;

namespace EntJoySample.SchedulerCompareTest
{
    public static class Program
    {
        public static void Main()
        {
            NativeJobScheduler.Initialize();
            NativeJobScheduler.PrewakeWorkersOnce();

            using var sample = new SchedulerCompareSample();
            sample.Run();
        }
    }
}