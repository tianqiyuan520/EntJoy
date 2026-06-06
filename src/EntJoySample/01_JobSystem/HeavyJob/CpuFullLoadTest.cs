//using System.Diagnostics;
//using Environment = System.Environment;

//[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.system, UseISPC_MT = true)]
//public unsafe struct HeavyJob : IJobParallelFor
//{
//    public float* Input;
//    public float* Output;

//    public void Execute(int i)
//    {
//        float x = Input[i];
//        float res = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1);
//        Output[i] = (float)res;
//    }
//}

//public class CpuFullLoadTest
//{
//    private const int DATA_COUNT = 20_000_000;

//    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, UseISPC_MT = true, MathLib = NativeTranspiler.IspcMathLib.fast)]
//    public unsafe static void Fill(float* data, float* output)
//    {
//        for (int i = 0; i < DATA_COUNT; i++)
//        {
//            float x = data[i];
//            float res = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1);
//            output[i] = res;
//        }
//    }

//    public static void Main()
//    {
//        try
//        {
//            // 预热
//            RunTest();

//            double totalSingle = 0, totalParallel = 0, totalCustom = 0;
//            const int testCount = 10;
//            for (int i = 0; i < testCount; i++)
//            {
//                var (single, parallel, custom) = RunTest();
//                totalSingle += single;
//                totalParallel += parallel;
//                totalCustom += custom;
//            }

//            Console.WriteLine("C# JobSystem (指针访问)");
//            Console.WriteLine($"平均单线程:        {totalSingle / testCount:F3} ms");
//            Console.WriteLine($"平均 Parallel.For:  {totalParallel / testCount:F3} ms (加速比 {totalSingle / totalParallel:F2}x)");
//            Console.WriteLine($"平均自定义JobSystem:{totalCustom / testCount:F3} ms (加速比 {totalSingle / totalCustom:F2}x)");
//            Console.WriteLine($"逻辑核心数: {Environment.ProcessorCount}");
//        }
//        finally { }

//        Console.ReadLine();
//    }

//    private static (double singleMs, double parallelMs, double customMs) RunTest()
//    {
//        float[] data = new float[DATA_COUNT];
//        float[] output = new float[DATA_COUNT];

//        Random rand = new Random(42);
//        for (int i = 0; i < DATA_COUNT; i++)
//            data[i] = (float)rand.NextDouble() * 100f;

//        Array.Clear(output, 0, DATA_COUNT);
//        // ---------- 单线程（托管数组索引，基线）----------
//        Stopwatch sw = Stopwatch.StartNew();
//        //for (int i = 0; i < DATA_COUNT; i++)
//        //{
//        //    float x = data[i];
//        //    var res = Math.Sqrt(x) + Math.Sin(x) * Math.Cos(x) + Math.Log(x + 1);
//        //    output[i] = (float)res;
//        //}
//        unsafe
//        {
//            fixed (float* p = data)
//            fixed (float* p2 = output)
//            {
//                //Fill(p, p2);
//                NativeTranspiler.Bindings.NativeExports.Fill(p, p2);
//                //校对
//                //for (int i = 0; i < DATA_COUNT; i++)
//                //{
//                //    float x = data[i];
//                //    var res = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1);
//                //    if (i < 100 && Math.Abs(output[i] - (float)res) > 0.000001) Console.WriteLine("! 不同" + output[i] + " " + (float)res);
//                //}
//            }
//        }


//        sw.Stop();
//        double singleMs = sw.Elapsed.TotalMilliseconds;
//        Array.Clear(output, 0, DATA_COUNT);
//        // ---------- Parallel.For（托管数组索引）----------
//        sw.Restart();
//        var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
//        Parallel.For(0, DATA_COUNT, options, i =>
//        {
//            float x = data[i];
//            var res = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1);
//            output[i] = (float)res;
//        });
//        //Console.WriteLine("2 " + output[^1]);
//        sw.Stop();

//        double parallelMs = sw.Elapsed.TotalMilliseconds;
//        Array.Clear(output, 0, DATA_COUNT);
//        // ---------- 自定义 JobSystem（直接指针访问）----------
//        sw.Restart();
//        unsafe
//        {
//            // 固定数组，获取指针（期间 GC 不会移动数组）
//            fixed (float* pData = data, pOutput = output)
//            {
//                var job = new HeavyJob { Input = pData, Output = pOutput };
//                JobHandle handle = job.Schedule(DATA_COUNT, 10000000);
//                handle.Complete();
//                //Console.WriteLine("3 " + output[^1]);
//            }
//        }
//        sw.Stop();
//        double customMs = sw.Elapsed.TotalMilliseconds;

//        return (singleMs, parallelMs, customMs);
//    }
//}
