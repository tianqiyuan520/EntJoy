using EntJoy.Collections;
using EntJoySample.SpritesRandomMove;

namespace EntJoySample;

public static class Program
{
    public static void Main(string[] args)
    {
        NativeJobScheduler.Initialize();

        using var sample = new SpritesRandomMoveLikeTest();

        while (true)
        {
            Console.WriteLine("请选择测试任务：");
            Console.WriteLine("  1. ECS IJobChunk");
            Console.WriteLine("  2. C# Job");
            Console.WriteLine("  3. Native C++ Job");
            Console.WriteLine("  4. Native ISPC Job");
            Console.WriteLine("  5. Parity Suite");
            Console.WriteLine("  0. Exit");
            Console.Write("输入序号: ");

            string? input = Console.ReadLine()?.Trim();
            Console.WriteLine();

            switch (input)
            {
                case "1":
                    sample.RunRealtimeLoop(MoveExecutionMode.EcsIJobChunk);
                    return;
                case "2":
                    sample.RunRealtimeLoop(MoveExecutionMode.CSharpJob);
                    return;
                case "3":
                    sample.RunRealtimeLoop(MoveExecutionMode.NativeCppJob);
                    return;
                case "4":
                    sample.RunRealtimeLoop(MoveExecutionMode.NativeIspcJob);
                    return;
                case "5":
                    sample.RunParitySuite();
                    return;
                case "0":
                    return;
                default:
                    Console.WriteLine("无效输入，请重新输入。\n");
                    break;
            }
        }
    }
}
