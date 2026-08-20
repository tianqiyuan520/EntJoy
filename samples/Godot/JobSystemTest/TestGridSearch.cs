
//using Godot;
//using System;
//using System.Diagnostics;
//using System.Reflection;
//using Vector3 = System.Numerics.Vector3;

//public struct AAA : IJob
//{
//    public int aa;
//    public void Execute()
//    {
//        aa = 99;
//        GD.Print(1231);
//    }
//}


//public partial class TestGridSearch : Node
//{
//    public static int N = 100000;
//    public static int K = 100000;

//    public static Vector3[] pos = null;
//    public static Vector3[] queries = null;

//    public override void _Ready()
//    {
//        //var aaaa = new AAA();
//        //aaaa.Schedule().Complete();
//        //GD.Print(aaaa.aa);
//        RunBenchmark();
//    }


//    private static void RunBenchmark()
//    {

//        Random random = new Random(1235);
//        for (int i = 0; i < 5; ++i)
//        {
//            Console.WriteLine(random.NextDouble());
//        }

//        string posPath = "user://pos.bin";
//        string queriesPath = "user://queries.bin";

//        {
//            Console.WriteLine("Generating new random data...");
//            pos = new Vector3[N];
//            for (int i = 0; i < N; i++)
//            {
//                pos[i] = new Vector3(
//                    (float)RandomExtensions.RandRange(-100.0f, 100.0f),
//                    (float)RandomExtensions.RandRange(-100.0f, 100.0f),
//                    0
//                );
//            }

//            queries = new Vector3[K];
//            for (int i = 0; i < K; i++)
//            {
//                queries[i] = new Vector3(
//                    (float)RandomExtensions.RandRange(-100.0f, 100.0f),
//                    (float)RandomExtensions.RandRange(-100.0f, 100.0f),
//                    0
//                );
//            }

//        }

//        Console.WriteLine("pos (first 10):");
//        for (int i = 0; i < 10; i++)
//            Console.WriteLine(pos[i]);

//        Console.WriteLine("queries (first 10):");
//        for (int i = 0; i < 10; i++)
//            Console.WriteLine(queries[i]);

//        // 测试不同的网格分辨率
//        int[] gridSizes = { 200 };
//        float bestCreation = float.MaxValue;
//        float bestQuery = float.MaxValue;
//        int bestGridSize = 0;
//        int[] bestResults = null;

//        foreach (int gridSize in gridSizes)
//        {
//            Console.WriteLine($"\nTesting grid size: {gridSize}");

//            float meanCreation = 0.0f;
//            float meanQuery = 0.0f;
//            int timesTest = 1000;
//            int[] lastresults = null;

//            Stopwatch sw = new Stopwatch();

//            for (int i = 0; i < timesTest; i++)
//            {
//                sw.Restart();

//                var gsb = new GridSearch2DPointer(-1f, gridSize);
//                var handle = gsb.InitializeGrid(pos);
//                handle.Complete();

//                sw.Stop();
//                float res1 = (float)sw.Elapsed.TotalMilliseconds;
//                if (i != 0) meanCreation += res1; // 跳过第一次（热身）

//                sw.Restart();
//                int[] results = gsb.SearchClosestPoint(queries);
//                //int[] results = new int[queries.Length];
//                lastresults = results;
//                sw.Stop();
//                float res2 = (float)sw.Elapsed.TotalMilliseconds;
//                if (i != 0) meanQuery += res2; // 跳过第一次（热身）

//                gsb.Dispose();
//            }

//            meanCreation /= (timesTest - 1);
//            meanQuery /= (timesTest - 1);

//            Console.WriteLine($"Creation {meanCreation.ToString("f3")}ms");
//            Console.WriteLine($"Queries {meanQuery.ToString("f3")}ms");

//            if (meanQuery < bestQuery)
//            {
//                bestCreation = meanCreation;
//                bestQuery = meanQuery;
//                bestGridSize = gridSize;
//                bestResults = lastresults;
//            }
//        }

//        Console.WriteLine($"\nBest grid size: {bestGridSize}");
//        Console.WriteLine($"Best Creation {bestCreation.ToString("f3")}ms");
//        Console.WriteLine($"Best Queries {bestQuery.ToString("f3")}ms");

//        Console.WriteLine("result (first 10): ");
//        for (int i = 0; i < 10; i++)
//            Console.WriteLine(bestResults[i]);

//        //Console.Read();
//    }

//}

//public static class RandomExtensions
//{
//    // 共享的 Random 实例（非线程安全，简单示例使用）
//    private static readonly Random random = new Random(1235);

//    /// <summary>
//    /// 返回指定范围内的随机整数 [min, max)
//    /// </summary>
//    /// <param name="min">包含的下限</param>
//    /// <param name="max">不包含的上限</param>
//    /// <returns>随机整数</returns>
//    public static int RandRange(int min, int max)
//    {
//        if (min >= max)
//            throw new ArgumentOutOfRangeException(nameof(min), "min 必须小于 max");
//        return random.Next(min, max);
//    }

//    /// <summary>
//    /// 返回指定范围内的随机双精度浮点数 [min, max)
//    /// </summary>
//    /// <param name="min">包含的下限</param>
//    /// <param name="max">包含的上限</param>
//    /// <returns>随机双精度浮点数</returns>
//    public static double RandRange(double min, double max)
//    {
//        if (min >= max)
//            throw new ArgumentOutOfRangeException(nameof(min), "min 必须小于 max");
//        return random.NextDouble() * (max - min) + min;
//    }

//    /// <summary>
//    /// 返回指定范围内按步长 step 的随机整数，从 min 开始，每次增加 step，直到小于 max
//    /// 例如 RandRange(0, 20, 3) 可能返回 0, 3, 6, 9, 12, 15, 18
//    /// </summary>
//    /// <param name="min">起始值（包含）</param>
//    /// <param name="max">上限（不包含）</param>
//    /// <param name="step">步长（必须为正）</param>
//    /// <returns>随机整数</returns>
//    public static int RandRange(int min, int max, int step)
//    {
//        if (step <= 0)
//            throw new ArgumentOutOfRangeException(nameof(step), "step 必须为正数");

//        // 计算可能的个数（向上取整）
//        int count = (max - min + step - 1) / step;
//        // 检查最后一个值是否达到或超过 max，若是则减少计数
//        int lastValue = min + (count - 1) * step;
//        if (lastValue >= max)
//        {
//            count--;
//        }

//        if (count <= 0)
//            throw new ArgumentException("指定的范围和步长内没有有效值");

//        int index = random.Next(count);
//        return min + index * step;
//    }
//}
