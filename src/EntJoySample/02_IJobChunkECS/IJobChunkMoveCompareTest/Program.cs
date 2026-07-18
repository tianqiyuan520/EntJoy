using EntJoy.Collections;

namespace EntJoySample.IJobChunkMoveCompareTest
{
    public static class Program
    {
        public static void Main()
        {
            NativeJobScheduler.Initialize();

            using var sample = new IJobChunkMoveCompareSample();
            sample.Run();

        }
    }
}
