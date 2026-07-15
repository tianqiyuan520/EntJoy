using Godot;
using System.Threading;

public partial class JobDependencyTest : Node
{
    // 由于 StageJob 需要访问全局状态，使用静态变量（注意测试是串行执行，安全）
    private static int _globalStage;
    private static int _globalError;


    // 测试数据
    private const int TEST_COUNT = 5; // 每组测试重复次数

    public override void _Ready()
    {
        ResetGlobals();
        TestSingleDependency();
        TestCombinedDependency();
        TestChainDependency();
    }

    public override void _Process(double delta)
    {
        //ResetGlobals();
        //TestSingleDependency();
        //TestCombinedDependency();
        //TestChainDependency();
    }



    // 测试1：单个依赖
    private void TestSingleDependency()
    {
        GD.Print("\n=== 测试1：单依赖 ===");
        ResetGlobals();

        for (int i = 0; i < TEST_COUNT; i++)
        {
            _globalStage = 0;

            var jobA = new StageJob { TargetStage = 1 };
            var jobB = new StageJob { TargetStage = 2 };

            JobHandle handleA = jobA.Schedule();
            JobHandle handleB = jobB.Schedule(handleA); // jobB 依赖 jobA

            handleB.Complete(); // 等待所有完成

            if (_globalError > 0)
                GD.PrintErr($"单依赖测试失败，第 {i} 次");
        }
        if (_globalError == 0)
            GD.Print("单依赖测试通过 ✓");
    }

    // 测试2：多依赖合并 (CombineDependencies)
    // jobA 和 jobB 为 CounterJob（可任意顺序并行），jobC 为 VerifyJob 验证两者都已完成
    private void TestCombinedDependency()
    {
        GD.Print("\n=== 测试2：多依赖合并 ===");
        ResetGlobals();

        for (int i = 0; i < TEST_COUNT; i++)
        {
            _globalStage = 0;

            var jobA = new CounterJob();
            var jobB = new CounterJob();
            var jobC = new VerifyJob { ExpectedValue = 2 }; // 期望 A 和 B 都执行完毕

            JobHandle handleA = jobA.Schedule();
            JobHandle handleB = jobB.Schedule();
            JobHandle combined = JobHandle.CombineDependencies(handleA, handleB);
            JobHandle handleC = jobC.Schedule(combined); // C 仅在 A 和 B 都完成后执行

            handleC.Complete();

            if (_globalError > 0)
                GD.PrintErr($"多依赖合并测试失败，第 {i} 次");
        }
        if (_globalError == 0)
            GD.Print("多依赖合并测试通过 ✓");
    }

    // 测试3：依赖链 (A -> B -> C)
    private void TestChainDependency()
    {
        GD.Print("\n=== 测试3：依赖链 ===");
        ResetGlobals();

        for (int i = 0; i < TEST_COUNT; i++)
        {
            _globalStage = 0;

            var jobA = new StageJob { TargetStage = 1 };
            var jobB = new StageJob { TargetStage = 2 };
            var jobC = new StageJob { TargetStage = 3 };

            JobHandle handleA = jobA.Schedule();
            JobHandle handleB = jobB.Schedule(handleA);
            JobHandle handleC = jobC.Schedule(handleB);

            handleC.Complete();

            if (_globalError > 0)
                GD.PrintErr($"依赖链测试失败，第 {i} 次");
        }
        if (_globalError == 0)
            GD.Print("依赖链测试通过 ✓");
    }

    // 一个简单的 IJob，用于更新 stage 并验证
    /// <summary>
    /// 顺序依赖测试：检查 _globalStage 是否为 TargetStage-1，若是则推进到 TargetStage。
    /// 用于单依赖和依赖链测试（A→B→C 的执行顺序必须严格）。
    /// </summary>
    private struct StageJob : IJob
    {
        public int TargetStage; // 期望执行时的 stage 值

        public void Execute()
        {
            if (Interlocked.CompareExchange(ref _globalStage, TargetStage, TargetStage - 1) != TargetStage - 1)
            {
                // 如果当前 stage 不是 TargetStage-1，说明顺序错误
                Interlocked.Increment(ref _globalError);
            }
        }
    }

    /// <summary>
    /// 并发完成测试：递增计数器，不要求特定顺序。
    /// 用于 CombineDependencies 测试（A 和 B 可并行执行，C 需等二者都完成）。
    /// </summary>
    private struct CounterJob : IJob
    {
        public void Execute()
        {
            // 递增计数器，不要求顺序
            Interlocked.Increment(ref _globalStage);
        }
    }

    /// <summary>
    /// 验证计数器值是否符合预期。
    /// </summary>
    private struct VerifyJob : IJob
    {
        public int ExpectedValue; // 期望的计数器值

        public void Execute()
        {
            int current = Interlocked.CompareExchange(ref _globalStage, 0, 0); // 读当前值
            if (current != ExpectedValue)
            {
                Interlocked.Increment(ref _globalError);
            }
        }
    }



    // 重设静态变量（每个测试前调用）
    private void ResetGlobals()
    {
        _globalStage = 0;
        _globalError = 0;
    }

}
