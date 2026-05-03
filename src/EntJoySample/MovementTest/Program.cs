//using EntJoy.MovementTest;

///// <summary>
///// EntJoySample 入口点
///// 运行 100w 实体位移基准测试（两种方式对比）
///// </summary>
//public class Program
//{
//    public static void Main()
//    {
//        Console.WriteLine("EntJoySample - 100w 实体位移性能测试");
//        Console.WriteLine("=====================================\n");

//        // ========= 测试 1: NativeArray 模拟 =========
//        Console.WriteLine("【测试 1】NativeArray 模拟实体位移");
//        Console.WriteLine("------------------------------");
//        var nativeTest = new MoveEntitiesTest();
//        try
//        {
//            nativeTest.RunAll();
//        }
//        finally
//        {
//            nativeTest.Dispose();
//        }

//        Console.WriteLine();

//        // ========= 测试 2: ECS World + IJobChunk =========
//        Console.WriteLine("【测试 2】ECS World + IJobChunk");
//        Console.WriteLine("------------------------------");
//        var ecsTest = new EcsMoveTest();
//        try
//        {
//            ecsTest.RunAll();
//        }
//        finally
//        {
//            ecsTest.Dispose();
//        }

//        Console.WriteLine("\n全部测试完成。");
//    }
//}
