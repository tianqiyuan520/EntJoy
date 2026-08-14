/*
 * 统一入口见 10_GPU_FinalCompare/Program.cs（一次跑完 HeavyMove + GridSearch + LightMove）。
 * 本文件保留为 08 独立入口：取消注释 + 注释掉 10 的 Main 即可单独跑 HeavyMove/LightMove。
 */
/*
using EntJoy;

namespace EntJoySample.GpuIlgpu
{
    public static class Program
    {
        public static void Main()
        {
            NativeJobScheduler.Initialize();
            NativeJobScheduler.PrewakeWorkersOnce();
            try
            {
                using var bench = new GpuIlgpuBenchmark();
                bench.Run();
            }
            finally
            {
                NativeJobScheduler.Shutdown();
            }
        }
    }
}
*/
