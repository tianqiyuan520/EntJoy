// AutoSIMD 测试入口
// 验证：
//   1. IJobEntity + AutoSIMD 走 ChunkRange 真 SIMD 路径
//   2. IJobChunk + AutoSIMD 走 EntityBatch + SimdControlFlowGenerator 真 SIMD 路径

using EntJoy.JobSystem;

namespace EntJoySample.AutoSIMDTest
{
    public static class Program
    {
        public static int Main()
        {
            try
            {
                NativeJobScheduler.Initialize();
                int rc1 = IJobEntityAutoSIMDTest.RunAll();
                int rc2 = IJobChunkAutoSIMDEntityBatchTest.RunAll();
                Console.Out.Flush();
                return (rc1 == 0 && rc2 == 0) ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[Main caught] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                Console.Out.Flush();
                return 99;
            }
        }
    }
}
