using EntJoy.JobSystem;
// 10_SIMD 已从编译排除（见 EntJoySample.csproj）；恢复时取消本注释与下方 Run() 调用
//using EntJoySample.SIMD;

namespace EntJoySample.ECS
{
    public static class Program
    {
        // 当前入口已切换到 01_JobSystem/IJobInlineProbeTest（README 入口约定：仅保留一个非注释 Main）
        public static void Main()
        {
            Console.WriteLine("=== EntJoy ECS Test ===\n");
            try
            {
                // ECS 基准需要原生 worker（C++ Chase-Lev 调度器）；缺失时 Schedule 路径无 worker 可执行
                JobScheduler.Initialize();
                //Console.WriteLine($"JobSystem initialized: {NativeJobScheduler.JobWorkerCount} workers\n");

                // 10_SIMD: ISPC vs AutoSIMD vs Cpp 对比 + 压力测试（对照 C# oracle 找翻译 bug）
                //SimdCompareTest.Run();

                // Observer 测试（组件生命周期事件 push 回调）
                //ObserverDemo.Run();

                // Shared Component per-chunk 存储测试（分组/Set/查询过滤/流式 API/变更追踪）
                //SharedComponentDemo.Run();

                // Event Channel 测试
                //EventChannelDemo.Run();

                // Event Channel + Managed Job 测试
                //EventChannelJobTest.Run();

                // Native Event Job 测试（NativeTranspile SendEvent）
                //NativeEventJobTest.Run();

                // ISPC Event Job 测试（NativeTranspile ISPC SendEvent）
                //ISpcEventJobTest.Run();

                // Change Tracking 测试
                //ChangeTrackingDemo.Run();

                // EnabledComponent 三种方案性能对比
                //EnabledComparisonBenchmark.Run();

                // NativeTranspile IJobChunk: Schedule / Run(ImmediateNative) 冒烟
                //NativeJobSmokeTest.Run();

                // IJobEntity.Run enabled 开关对比
                //IJobEntityEnabledBenchmark.Run();

                // ECS JobSystem 重构回归标尺：schedule-only 微基准
                //ScheduleOverheadBenchmark.Run();

                // 查询缓存基准：共享注册表 + 增量刷新收益
                //EntityQueryCacheBenchmark.Run();

                // N 元组查询示例：world.Query<T0, T1, T2>()（SourceGenerator 生成）
                //QueryTupleDemo.Run();

                //关系基准：Add/Get/ Has / WithRelationship 性能基线
                //RelationBenchmark.Run();

                // IJobEntity 访问关系列验证（步长一致性）
                //RelationBenchmark.VerifyIJobEntityRelationAccess();

                // IJobChunk 访问关系列验证（步长一致性）
                //RelationBenchmark.VerifyIJobChunkRelationAccess();

                // NativeTranspiler 关系访问验证（[NativeTranspile] IJobChunk/IJobEntity）
                //RelationNativeJobTest.Run();

                // [ECSComponent] 标记组件示例（不写 : IComponentData，源生成器自动补齐接口）
                //ECSComponentDemo.Run();

                // System 注册生成示例（SystemRegistry.RegisterAll 一行注册本程序集所有 ISystem）
                //SystemRegistrationDemo.Run();

                // Reactive 处理器示例（[Reactive] 自动注册 Observer，组件事件 push 回调）
                //ReactiveDemo.Run();

                // 组件持有 NativeCollection 时的内存问题复现（DestroyEntity/RemoveComponent 泄漏）
                ComponentLifecycleMemoryDemo.Run();

                // 多 World 隔离：两个 World 各自跑 SystemRunner，验证 System 不串扰
                MultiWorldIsolationDemo.Run();

                // Chunk 碎片整理（S33）：制造碎片 + CompactChunks + 验证 chunk 数/实体/查询/生命周期平衡
                ChunkDefragDemo.Run();

                // 内存分析器示例：MemoryReport 原生分配/泄漏/碎片/slab 统计
                MemoryProfilerDemo.Run();
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
