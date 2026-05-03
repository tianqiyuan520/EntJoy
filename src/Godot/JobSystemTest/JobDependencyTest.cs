using Godot;
using System.Threading;

public partial class JobDependencyTest : Node
{
    // 由于 StageJob 需要访问全局状态，使用静态变量（注意测试是串行执行，安全）
    private static int _globalStage;
    private static int _globalError;

    // 共享状态，用于验证执行顺序
    private int _stage = 0;
    private int _errorCount = 0;

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
        _errorCount = 0;

        for (int i = 0; i < TEST_COUNT; i++)
        {
            _stage = 0;

            var jobA = new StageJob { TargetStage = 1 };
            var jobB = new StageJob { TargetStage = 2 };

            JobHandle handleA = jobA.Schedule();
            JobHandle handleB = jobB.Schedule(handleA); // jobB 依赖 jobA

            handleB.Complete(); // 等待所有完成

            if (_errorCount > 0)
                GD.PrintErr($"单依赖测试失败，第 {i} 次");
        }
        if (_errorCount == 0)
            GD.Print("单依赖测试通过 ✓");
    }

    // 测试2：多依赖合并 (CombineDependencies)
    private void TestCombinedDependency()
    {
        GD.Print("\n=== 测试2：多依赖合并 ===");
        _errorCount = 0;

        for (int i = 0; i < TEST_COUNT; i++)
        {
            _stage = 0;

            var jobA = new StageJob { TargetStage = 1 };
            var jobB = new StageJob { TargetStage = 2 };
            var jobC = new StageJob { TargetStage = 3 }; // 依赖 A 和 B 都完成

            JobHandle handleA = jobA.Schedule();
            JobHandle handleB = jobB.Schedule();
            JobHandle combined = JobHandle.CombineDependencies(handleA, handleB);
            JobHandle handleC = jobC.Schedule(combined);

            handleC.Complete();

            if (_errorCount > 0)
                GD.PrintErr($"多依赖合并测试失败，第 {i} 次");
        }
        if (_errorCount == 0)
            GD.Print("多依赖合并测试通过 ✓");
    }

    // 测试3：依赖链 (A -> B -> C)
    private void TestChainDependency()
    {
        GD.Print("\n=== 测试3：依赖链 ===");
        _errorCount = 0;

        for (int i = 0; i < TEST_COUNT; i++)
        {
            _stage = 0;

            var jobA = new StageJob { TargetStage = 1 };
            var jobB = new StageJob { TargetStage = 2 };
            var jobC = new StageJob { TargetStage = 3 };

            JobHandle handleA = jobA.Schedule();
            JobHandle handleB = jobB.Schedule(handleA);
            JobHandle handleC = jobC.Schedule(handleB);

            handleC.Complete();

            if (_errorCount > 0)
                GD.PrintErr($"依赖链测试失败，第 {i} 次");
        }
        if (_errorCount == 0)
            GD.Print("依赖链测试通过 ✓");
    }

    // 一个简单的 IJob，用于更新 stage 并验证
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



    // 重设静态变量（每个测试前调用）
    private void ResetGlobals()
    {
        _globalStage = 0;
        _globalError = 0;
    }

}
