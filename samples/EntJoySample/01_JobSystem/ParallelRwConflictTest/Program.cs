using EntJoy.JobSystem;

namespace EntJoySample.ParallelRwConflictTest
{
    public static class Program
    {
        public static void Main()
        {
            JobScheduler.Initialize();
            new ParallelRwConflictSample().Run();
        }
    }
}