using EntJoy.MovementTest;

/// <summary>
/// EntJoySample 入口点
/// 运行 100w 实体位移基准测试
/// </summary>
public class Program
{
    public static void Main()
    {
        NativeJobScheduler.Initialize();
        NativeJobScheduler.SetSpinDuration(100_000);

        //Console.WriteLine("EntJoySample - 100w 实体位移性能测试");
        //Console.WriteLine("=====================================\n");

        //// ========= 测试 1: NativeArray 传统基准（1000次连续求平均） =========
        //Console.WriteLine("【测试 1】NativeArray 模拟实体位移（1000次迭代求平均）");
        //Console.WriteLine("------------------------------");
        //var nativeTest = new MoveEntitiesTest();
        //try
        //{
        //    nativeTest.RunAll();
        //}
        //finally
        //{
        //    nativeTest.Dispose();
        //}

        //Console.WriteLine();

        //// ========= 测试 2: ECS 传统基准（1000次连续求平均） =========
        //Console.WriteLine("【测试 2】ECS World + IJobChunk（1000次迭代求平均）");
        //Console.WriteLine("------------------------------");
        //var ecsTest = new EcsMoveTest();
        //try
        //{
        //    ecsTest.RunAll();
        //}
        //finally
        //{
        //    ecsTest.Dispose();
        //}

        //Console.WriteLine();

        // ========= 测试 3: ISPC Job 正确性专项测试 =========
        //Console.WriteLine("【测试 3】ISPC Job 正确性专项测试");
        //Console.WriteLine("------------------------------");
        //IspcJobTest.Run();

        //Console.WriteLine();

        // ========= 测试 4: NativeArray 帧循环测试（100帧 × 16ms间隔） =========
        Console.WriteLine("【测试 4】NativeArray 帧循环风格（100帧 × 16ms间隔）");
        Console.WriteLine("------------------------------");
        var nativeFrameTest = new MoveEntitiesFrameTest();
        try
        {
            nativeFrameTest.RunAll();
        }
        finally
        {
            nativeFrameTest.Dispose();
        }

        //Console.WriteLine();

        //// ========= 测试 4: ECS 帧循环测试（100帧 × 16ms间隔） =========
        //Console.WriteLine("【测试 4】ECS 帧循环风格（100帧 × 16ms间隔）");
        //Console.WriteLine("------------------------------");
        //var ecsFrameTest = new EcsMoveFrameTest();
        //try
        //{
        //    ecsFrameTest.RunAll();
        //}
        //finally
        //{
        //    ecsFrameTest.Dispose();
        //}

        //// ========= ISPC 帧循环独立验证 =========
        //Console.WriteLine("\n【测试 5】ISPC 帧循环独立验证（10 帧，每帧对比）");
        //Console.WriteLine("------------------------------");
        //IspcFrameValidator.Run();

        //Console.WriteLine("\n全部测试完成。");

    }
}
