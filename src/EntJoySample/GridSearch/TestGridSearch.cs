using System.Diagnostics;
using Vector3 = System.Numerics.Vector3;
using EntJoy.Collections;
using EntJoy.Mathematics;


public class TestGridSearch
{
    public static void Main()
    {
        int N = 100000;
        int K = 100000;

        var pos = new Vector3[N];
        var queries = new Vector3[K];
        var rnd = new Random(1234);
        for (int i = 0; i < N; i++)
            pos[i] = new Vector3((float)(rnd.NextDouble() * 200 - 100), (float)(rnd.NextDouble() * 200 - 100), 0);
        for (int i = 0; i < K; i++)
            queries[i] = new Vector3((float)(rnd.NextDouble() * 200 - 100), (float)(rnd.NextDouble() * 200 - 100), 0);

        var nativePos = new NativeArray<float2>(N, Allocator.Persistent);
        var nativeQueries = new NativeArray<float2>(K, Allocator.Persistent);
        for (int i = 0; i < N; i++) nativePos[i] = new float2(pos[i].X, pos[i].Y);
        for (int i = 0; i < K; i++) nativeQueries[i] = new float2(queries[i].X, queries[i].Y);

        var gsb = new GridSearch2D(-1f, 200);

        const int warmup = 2;
        const int iterations = 1000;

        Console.WriteLine("=== GridSearch2D SoA + ISPC MT 测试 ===");
        Console.WriteLine("预热次数: {0}, 迭代次数: {1}\n", warmup, iterations);

        for (int i = 0; i < warmup; i++)
        {
            gsb.InitializeGrid(nativePos).Complete();
            gsb.SearchClosestPoint(nativeQueries).Dispose();
        }

        double totalBuild = 0.0;
        double totalQuery = 0.0;
        GridSearch2D.BuildTimings sumTimings = default;

        for (int i = 0; i < iterations; i++)
        {
            var swBuild = Stopwatch.StartNew();
            var handle = gsb.InitializeGrid(nativePos);
            handle.Complete();
            swBuild.Stop();
            double buildMs = swBuild.Elapsed.TotalMilliseconds;
            totalBuild += buildMs;

            var swQuery = Stopwatch.StartNew();
            var queryResults = gsb.SearchClosestPoint(nativeQueries);
            swQuery.Stop();
            double queryMs = swQuery.Elapsed.TotalMilliseconds;
            totalQuery += queryMs;

            var timings = gsb.LastBuildTimings;
            sumTimings.DisposeNative += timings.DisposeNative;
            sumTimings.CreateAndCopy += timings.CreateAndCopy;
            sumTimings.BoundingBox += timings.BoundingBox;
            sumTimings.HashCounting += timings.HashCounting;
            sumTimings.PrefixAndFill += timings.PrefixAndFill;
            sumTimings.ElementPlacement += timings.ElementPlacement;
            sumTimings.CoreBuildTotal += timings.CoreBuildTotal;
            sumTimings.QueryTotal += timings.QueryTotal;

            if (i == iterations - 1)
            {
                var resultsArray = new int[queryResults.Length];
                queryResults.CopyTo(resultsArray);
                Console.WriteLine("查询结果前10个: {0}", string.Join(" ", resultsArray[..10]));
            }

            queryResults.Dispose();

            if ((i + 1) % 10 == 0) Console.Write(".");
        }
        Console.WriteLine();

        double avgBuild = totalBuild / iterations;
        double avgQuery = totalQuery / iterations;

        Console.WriteLine("\n--- 平均详细计时 ---");
        Console.WriteLine("[Init] 释放 NativeCollections: {0:F3} ms", sumTimings.DisposeNative / iterations);
        Console.WriteLine("[Init] 创建 NativeCollections + 复制数据: {0:F3} ms", sumTimings.CreateAndCopy / iterations);
        Console.WriteLine("[Init] 包围盒计算: {0:F3} ms", sumTimings.BoundingBox / iterations);
        Console.WriteLine("[Init] 哈希分配+计数: {0:F3} ms", sumTimings.HashCounting / iterations);
        Console.WriteLine("[Init] 前缀和+填充起止: {0:F3} ms", sumTimings.PrefixAndFill / iterations);
        Console.WriteLine("[Init] 元素放置: {0:F3} ms", sumTimings.ElementPlacement / iterations);
        Console.WriteLine("[Init] 核心构建总耗时: {0:F3} ms", sumTimings.CoreBuildTotal / iterations);
        Console.WriteLine("[Init] 核心查询总耗时: {0:F3} ms", sumTimings.QueryTotal / iterations);

        Console.WriteLine("\n=== 平均性能结果 ===");
        Console.WriteLine("平均构建时间: {0:F3} ms", avgBuild);
        Console.WriteLine("平均查询时间: {0:F3} ms", avgQuery);
        Console.WriteLine("总耗时 (构建+查询): {0:F3} ms", avgBuild + avgQuery);

        gsb.Dispose();
        nativePos.Dispose();
        nativeQueries.Dispose();

        Console.WriteLine("\n测试完成。");
        Console.Read();
    }
}