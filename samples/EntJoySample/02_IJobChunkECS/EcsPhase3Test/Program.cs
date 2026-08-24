using EntJoy.JobSystem;

namespace EntJoySample.EcsPhase3Test
{
    public static class Program
    {
        public static void Main()
        {
            NativeJobScheduler.Initialize();
            NativeJobScheduler.PrewakeWorkersOnce();

            using var test = new EcsPhase3Test();
            test.Run();
        }
    }
}
