using EntJoy.JobSystem;

namespace EntJoySample.ECS
{
    public static class Program
    {
        public static void Main()
        {
            Console.WriteLine("=== EntJoy ECS Test ===\n");
            try
            {
                // ECS 基准需要原生 worker（C++ Chase-Lev 调度器）；缺失时 Schedule 路径无 worker 可执行
                JobScheduler.Initialize();
                Console.WriteLine($"JobSystem initialized: {NativeJobScheduler.JobWorkerCount} workers\n");

                // 原有示例
                //ScheduleGraphDemo.Run();
                //EntityBuilderDemo.Run();

                // Shared Component per-chunk 存储测试
                //SharedComponentDemo.Run();

                // Change Tracking 测试
                ChangeTrackingDemo.Run();

                // EnabledComponent 三种方案性能对比
                //EnabledComparisonBenchmark.Run();

                // NativeTranspile IJobChunk: Schedule / Run(ImmediateNative) 冒烟
                //NativeJobSmokeTest.Run();

                // IJobEntity.Run enabled 开关对比
                //IJobEntityEnabledBenchmark.Run();

                // ECS JobSystem 重构回归标尺：schedule-only 微基准
                //ScheduleOverheadBenchmark.Run();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
            Console.WriteLine("\n=== All ECS Demos Complete ===\n");
        }
    }
}