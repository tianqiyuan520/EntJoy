namespace EntJoySample.IJobChunkScheduleOverheadTest
{
    public static class Program
    {
        public static void Main()
        {
            NativeJobScheduler.Initialize();

            using var sample = new IJobChunkScheduleOverheadSample();
            sample.Run();
        }
    }
}
