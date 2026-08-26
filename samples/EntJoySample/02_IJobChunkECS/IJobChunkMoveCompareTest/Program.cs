using EntJoy.JobSystem;

namespace EntJoySample.IJobChunkMoveCompareTest
{
    public static class Program
    {
        public static void Main()
        {
            JobScheduler.Initialize();

            using var sample = new IJobChunkMoveCompareSample();
            sample.Run();
        }
    }
}
