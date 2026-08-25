namespace EntJoySample.ECS
{
    public static class Program
    {
        public static void Main()
        {
            Console.WriteLine("=== EntJoy ECS Test ===\n");
            try
            {
                // 原有示例
                //ScheduleGraphDemo.Run();
                //EntityBuilderDemo.Run();

                // Change Tracking 测试
                //ChangeTrackingDemo.Run();

                // EnabledComponent 三种方案性能对比
                EnabledComparisonBenchmark.Run();

                // NativeTranspile IJobChunk: Schedule / Run(ImmediateNative) 冒烟
                NativeJobSmokeTest.Run();

                // IJobEntity.Run enabled 开关对比
                IJobEntityEnabledBenchmark.Run();
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