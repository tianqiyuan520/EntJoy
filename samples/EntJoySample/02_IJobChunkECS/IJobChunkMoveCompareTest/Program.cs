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

            // 显式在主线程关停：`AppDomain.ProcessExit` 回调跑在**非主线程**上，原生侧会拒绝
            // （防 worker 自 join 死锁）⇒ `[JOBPHYS]` 等 Shutdown 期诊断永不打印、原生资源不释放。
            // 放在所有测量之后，不影响任何读数。
            JobScheduler.Shutdown();
        }
    }
}
