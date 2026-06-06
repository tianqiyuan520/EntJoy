//using System;
//using System.Diagnostics;
//using Environment = System.Environment;

//// ==================== Job 定义（全部变体） ====================
//public unsafe struct CSharpJob : IJobParallelFor
//{
//    public float* Input;
//    public float* Output;
//    public void Execute(int i)
//    {
//        float x = Input[i];
//        float res = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1);
//        Output[i] = res;
//    }
//}

//[NativeTranspiler.NativeTranspile]
//public unsafe struct CPPJob : IJobParallelFor
//{
//    public float* Input;
//    public float* Output;
//    public void Execute(int i)
//    {
//        float x = Input[i];
//        float res = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1);
//        Output[i] = res;
//    }
//}

//[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.system)]
//public unsafe struct ISPCSystemJob : IJobParallelFor
//{
//    public float* Input;
//    public float* Output;
//    public void Execute(int i)
//    {
//        float x = Input[i];
//        float res = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1);
//        Output[i] = res;
//    }
//}

//[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.@default)]
//public unsafe struct ISPCDefaultJob : IJobParallelFor
//{
//    public float* Input;
//    public float* Output;
//    public void Execute(int i)
//    {
//        float x = Input[i];
//        float res = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1);
//        Output[i] = res;
//    }
//}

//[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.fast)]
//public unsafe struct ISPCFastJob : IJobParallelFor
//{
//    public float* Input;
//    public float* Output;
//    public void Execute(int i)
//    {
//        float x = Input[i];
//        float res = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1);
//        Output[i] = res;
//    }
//}

//// 三个 MT Job（注意：调度时 innerBatchCount 必须设为 0 以避免重复并行）
//[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.system, UseISPC_MT = true)]
//public unsafe struct ISPCSystemMTJob : IJobParallelFor
//{
//    public float* Input;
//    public float* Output;
//    public void Execute(int i)
//    {
//        float x = Input[i];
//        float res = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1);
//        Output[i] = res;
//    }
//}

//[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.@default, UseISPC_MT = true)]
//public unsafe struct ISPCDefaultMTJob : IJobParallelFor
//{
//    public float* Input;
//    public float* Output;
//    public void Execute(int i)
//    {
//        float x = Input[i];
//        float res = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1);
//        Output[i] = res;
//    }
//}

//[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.fast, UseISPC_MT = true)]
//public unsafe struct ISPCFastMTJob : IJobParallelFor
//{
//    public float* Input;
//    public float* Output;
//    public void Execute(int i)
//    {
//        float x = Input[i];
//        float res = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1);
//        Output[i] = res;
//    }
//}

//// ==================== Fill 定义（全部变体） ====================
//public class IspccComparison
//{
//    private const int DATA_COUNT = 20_000_000;
//    // 非 MT 的 Job 使用的批大小
//    private const int BATCH_SIZE = 2048;
//    // MT Job 使用的批大小：必须为 0（或整长度），让外部只生成一个批次
//    private const int MT_BATCH_SIZE = 0;

//    public static unsafe void FillCSharp(float* data, float* output)
//    {
//        for (int i = 0; i < DATA_COUNT; i++)
//        {
//            float x = data[i];
//            float res = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1);
//            output[i] = res;
//        }
//    }

//    [NativeTranspiler.NativeTranspile]
//    public static unsafe void FillCPP(float* data, float* output)
//    {
//        for (int i = 0; i < DATA_COUNT; i++)
//        {
//            float x = data[i];
//            float res = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1);
//            output[i] = res;
//        }
//    }

//    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.system)]
//    public static unsafe void FillIspcSystem(float* data, float* output)
//    {
//        for (int i = 0; i < DATA_COUNT; i++)
//        {
//            float x = data[i];
//            float res = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1);
//            output[i] = res;
//        }
//    }

//    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.@default)]
//    public static unsafe void FillIspcDefault(float* data, float* output)
//    {
//        for (int i = 0; i < DATA_COUNT; i++)
//        {
//            float x = data[i];
//            float res = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1);
//            output[i] = res;
//        }
//    }

//    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.fast)]
//    public static unsafe void FillIspcFast(float* data, float* output)
//    {
//        for (int i = 0; i < DATA_COUNT; i++)
//        {
//            float x = data[i];
//            float res = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1);
//            output[i] = res;
//        }
//    }

//    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.system, UseISPC_MT = true)]
//    public static unsafe void FillIspcSystemMT(float* data, float* output)
//    {
//        for (int i = 0; i < DATA_COUNT; i++)
//        {
//            float x = data[i];
//            float res = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1);
//            output[i] = res;
//        }
//    }

//    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.@default, UseISPC_MT = true)]
//    public static unsafe void FillIspcDefaultMT(float* data, float* output)
//    {
//        for (int i = 0; i < DATA_COUNT; i++)
//        {
//            float x = data[i];
//            float res = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1);
//            output[i] = res;
//        }
//    }

//    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.fast, UseISPC_MT = true)]
//    public static unsafe void FillIspcFastMT(float* data, float* output)
//    {
//        for (int i = 0; i < DATA_COUNT; i++)
//        {
//            float x = data[i];
//            float res = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x) + MathF.Log(x + 1);
//            output[i] = res;
//        }
//    }

//    // ==================== Main 与 RunTest ====================
//    public static void Main()
//    {
//        RunTest(); // 预热

//        const int testCount = 3;

//        // Job 累计时间
//        double totalCSharpJob = 0, totalCPPJob = 0;
//        double totalISPCSystemJob = 0, totalISPCDefaultJob = 0, totalISPCFastJob = 0;
//        double totalISPCSystemMTJob = 0, totalISPCDefaultMTJob = 0, totalISPCFastMTJob = 0;

//        // Fill 累计时间
//        double totalFillCSharp = 0, totalFillCPP = 0;
//        double totalFillIspcSystem = 0, totalFillIspcDefault = 0, totalFillIspcFast = 0;
//        double totalFillIspcSystemMT = 0, totalFillIspcDefaultMT = 0, totalFillIspcFastMT = 0;

//        for (int i = 0; i < testCount; i++)
//        {
//            Console.Write(".");
//            var r = RunTest();

//            totalCSharpJob += r.jobCSharpMs;
//            totalCPPJob += r.jobCPPMs;
//            totalISPCSystemJob += r.jobISPCSystemMs;
//            totalISPCDefaultJob += r.jobISPCDefaultMs;
//            totalISPCFastJob += r.jobISPCFastMs;
//            totalISPCSystemMTJob += r.jobISPCSystemMTMs;
//            totalISPCDefaultMTJob += r.jobISPCDefaultMTMs;
//            totalISPCFastMTJob += r.jobISPCFastMTMs;

//            totalFillCSharp += r.fillCSharpMs;
//            totalFillCPP += r.fillCPPMs;
//            totalFillIspcSystem += r.fillIspcSystemMs;
//            totalFillIspcDefault += r.fillIspcDefaultMs;
//            totalFillIspcFast += r.fillIspcFastMs;
//            totalFillIspcSystemMT += r.fillIspcSystemMTMs;
//            totalFillIspcDefaultMT += r.fillIspcDefaultMTMs;
//            totalFillIspcFastMT += r.fillIspcFastMTMs;
//        }

//        double baseline = totalFillCSharp / testCount;

//        Console.WriteLine($"数据量: {DATA_COUNT:N0}，重复 {testCount} 次取平均");
//        Console.WriteLine($"逻辑核心: {Environment.ProcessorCount}\n");
//        Console.WriteLine("{0,-30} {1,10} {2,10} {3,10}", "方法", "时间(ms)", "加速比", "内批大小");
//        Console.WriteLine(new string('-', 62));

//        // 输出 Job 部分
//        Print("CSharp Job (托管)", totalCSharpJob / testCount, baseline, BATCH_SIZE);
//        Print("CPP Job", totalCPPJob / testCount, baseline, BATCH_SIZE);
//        Print("ISPC system Job", totalISPCSystemJob / testCount, baseline, BATCH_SIZE);
//        Print("ISPC default Job", totalISPCDefaultJob / testCount, baseline, BATCH_SIZE);
//        Print("ISPC fast Job", totalISPCFastJob / testCount, baseline, BATCH_SIZE);
//        Print("ISPC system MT Job", totalISPCSystemMTJob / testCount, baseline, MT_BATCH_SIZE == 0 ? "whole" : MT_BATCH_SIZE.ToString());
//        Print("ISPC default MT Job", totalISPCDefaultMTJob / testCount, baseline, MT_BATCH_SIZE == 0 ? "whole" : MT_BATCH_SIZE.ToString());
//        Print("ISPC fast MT Job", totalISPCFastMTJob / testCount, baseline, MT_BATCH_SIZE == 0 ? "whole" : MT_BATCH_SIZE.ToString());

//        Console.WriteLine();

//        // 输出 Fill 部分
//        Print("Fill CSharp (纯 C#)", totalFillCSharp / testCount, baseline, "N/A");
//        Print("Fill CPP", totalFillCPP / testCount, baseline, "N/A");
//        Print("Fill ISPC system", totalFillIspcSystem / testCount, baseline, "N/A");
//        Print("Fill ISPC default", totalFillIspcDefault / testCount, baseline, "N/A");
//        Print("Fill ISPC fast", totalFillIspcFast / testCount, baseline, "N/A");
//        Print("Fill ISPC system MT", totalFillIspcSystemMT / testCount, baseline, "N/A");
//        Print("Fill ISPC default MT", totalFillIspcDefaultMT / testCount, baseline, "N/A");
//        Print("Fill ISPC fast MT", totalFillIspcFastMT / testCount, baseline, "N/A");

//        Console.ReadLine();
//    }

//    private static void Print(string name, double avgMs, double baselineMs, string batchInfo)
//    {
//        double speedup = baselineMs / avgMs;
//        Console.WriteLine("{0,-30} {1,10:F3} {2,10:F2}x {3,10}", name, avgMs, speedup, batchInfo);
//    }
//    private static void Print(string name, double avgMs, double baselineMs, int batchSize)
//        => Print(name, avgMs, baselineMs, batchSize.ToString());

//    private static unsafe (double jobCSharpMs, double jobCPPMs,
//                           double jobISPCSystemMs, double jobISPCDefaultMs, double jobISPCFastMs,
//                           double jobISPCSystemMTMs, double jobISPCDefaultMTMs, double jobISPCFastMTMs,
//                           double fillCSharpMs, double fillCPPMs,
//                           double fillIspcSystemMs, double fillIspcDefaultMs, double fillIspcFastMs,
//                           double fillIspcSystemMTMs, double fillIspcDefaultMTMs, double fillIspcFastMTMs)
//    RunTest()
//    {
//        float[] data = new float[DATA_COUNT];
//        float[] output = new float[DATA_COUNT];
//        var rand = new Random(42);
//        for (int i = 0; i < DATA_COUNT; i++)
//            data[i] = (float)rand.NextDouble() * 100f;

//        var sw = new Stopwatch();
//        void Clear() => Array.Clear(output, 0, DATA_COUNT);

//        // ================= Job 测试 =================
//        double csharpJobMs, cppJobMs, ispcSystemMs, ispcDefaultMs, ispcFastMs;
//        double ispcSystemMTMs, ispcDefaultMTMs, ispcFastMTMs;

//        // 非 MT Job 使用 BATCH_SIZE
//        Clear(); fixed (float* pData = data, pOut = output)
//        {
//            var job = new CSharpJob { Input = pData, Output = pOut };
//            sw.Restart(); job.Schedule(DATA_COUNT, BATCH_SIZE).Complete();
//            sw.Stop();
//        }
//        csharpJobMs = sw.Elapsed.TotalMilliseconds;

//        Clear(); fixed (float* pData = data, pOut = output)
//        {
//            var job = new CPPJob { Input = pData, Output = pOut };
//            sw.Restart(); NativeTranspiler.Bindings.NativeExports.Schedule_CPPJob(ref job, DATA_COUNT, BATCH_SIZE).Complete();
//            sw.Stop();
//        }
//        cppJobMs = sw.Elapsed.TotalMilliseconds;

//        Clear(); fixed (float* pData = data, pOut = output)
//        {
//            var job = new ISPCSystemJob { Input = pData, Output = pOut };
//            sw.Restart(); NativeTranspiler.Bindings.NativeExports.Schedule_ISPCSystemJob(ref job, DATA_COUNT, BATCH_SIZE).Complete();
//            sw.Stop();
//        }
//        ispcSystemMs = sw.Elapsed.TotalMilliseconds;

//        Clear(); fixed (float* pData = data, pOut = output)
//        {
//            var job = new ISPCDefaultJob { Input = pData, Output = pOut };
//            sw.Restart(); NativeTranspiler.Bindings.NativeExports.Schedule_ISPCDefaultJob(ref job, DATA_COUNT, BATCH_SIZE).Complete();
//            sw.Stop();
//        }
//        ispcDefaultMs = sw.Elapsed.TotalMilliseconds;

//        Clear(); fixed (float* pData = data, pOut = output)
//        {
//            var job = new ISPCFastJob { Input = pData, Output = pOut };
//            sw.Restart(); NativeTranspiler.Bindings.NativeExports.Schedule_ISPCFastJob(ref job, DATA_COUNT, BATCH_SIZE).Complete();
//            sw.Stop();
//        }
//        ispcFastMs = sw.Elapsed.TotalMilliseconds;

//        // MT Job 使用 MT_BATCH_SIZE (0)，使原生函数一次处理整个数组
//        Clear(); fixed (float* pData = data, pOut = output)
//        {
//            var job = new ISPCSystemMTJob { Input = pData, Output = pOut };
//            sw.Restart(); NativeTranspiler.Bindings.NativeExports.Schedule_ISPCSystemMTJob(ref job, DATA_COUNT, MT_BATCH_SIZE).Complete();
//            sw.Stop();
//        }
//        ispcSystemMTMs = sw.Elapsed.TotalMilliseconds;

//        Clear(); fixed (float* pData = data, pOut = output)
//        {
//            var job = new ISPCDefaultMTJob { Input = pData, Output = pOut };
//            sw.Restart(); NativeTranspiler.Bindings.NativeExports.Schedule_ISPCDefaultMTJob(ref job, DATA_COUNT, MT_BATCH_SIZE).Complete();
//            sw.Stop();
//        }
//        ispcDefaultMTMs = sw.Elapsed.TotalMilliseconds;

//        Clear(); fixed (float* pData = data, pOut = output)
//        {
//            var job = new ISPCFastMTJob { Input = pData, Output = pOut };
//            sw.Restart(); NativeTranspiler.Bindings.NativeExports.Schedule_ISPCFastMTJob(ref job, DATA_COUNT, MT_BATCH_SIZE).Complete();
//            sw.Stop();
//        }
//        ispcFastMTMs = sw.Elapsed.TotalMilliseconds;

//        // ================= Fill 测试 =================
//        double fillCSharpMs, fillCPPMs, fillIspcSystemMs, fillIspcDefaultMs, fillIspcFastMs;
//        double fillIspcSystemMTMs, fillIspcDefaultMTMs, fillIspcFastMTMs;

//        Clear(); fixed (float* pData = data, pOut = output)
//        {
//            sw.Restart();
//            FillCSharp(pData, pOut); sw.Stop();
//        }
//        fillCSharpMs = sw.Elapsed.TotalMilliseconds;

//        Clear(); fixed (float* pData = data, pOut = output)
//        {
//            sw.Restart();
//            NativeTranspiler.Bindings.NativeExports.FillCPP(pData, pOut);
//            sw.Stop();
//        }
//        fillCPPMs = sw.Elapsed.TotalMilliseconds;

//        Clear(); fixed (float* pData = data, pOut = output)
//        {
//            sw.Restart();
//            NativeTranspiler.Bindings.NativeExports.FillIspcSystem(pData, pOut);
//            sw.Stop();
//        }
//        fillIspcSystemMs = sw.Elapsed.TotalMilliseconds;

//        Clear(); fixed (float* pData = data, pOut = output)
//        {
//            sw.Restart();
//            NativeTranspiler.Bindings.NativeExports.FillIspcDefault(pData, pOut);
//            sw.Stop();
//        }
//        fillIspcDefaultMs = sw.Elapsed.TotalMilliseconds;

//        Clear(); fixed (float* pData = data, pOut = output)
//        {
//            sw.Restart();
//            NativeTranspiler.Bindings.NativeExports.FillIspcFast(pData, pOut);
//            sw.Stop();
//        }
//        fillIspcFastMs = sw.Elapsed.TotalMilliseconds;

//        Clear(); fixed (float* pData = data, pOut = output)
//        {
//            sw.Restart();
//            NativeTranspiler.Bindings.NativeExports.FillIspcSystemMT(pData, pOut);
//            sw.Stop();
//        }
//        fillIspcSystemMTMs = sw.Elapsed.TotalMilliseconds;

//        Clear(); fixed (float* pData = data, pOut = output)
//        {
//            sw.Restart();
//            NativeTranspiler.Bindings.NativeExports.FillIspcDefaultMT(pData, pOut);
//            sw.Stop();
//        }
//        fillIspcDefaultMTMs = sw.Elapsed.TotalMilliseconds;

//        Clear(); fixed (float* pData = data, pOut = output)
//        {
//            sw.Restart();
//            NativeTranspiler.Bindings.NativeExports.FillIspcFastMT(pData, pOut);
//            sw.Stop();
//        }
//        fillIspcFastMTMs = sw.Elapsed.TotalMilliseconds;

//        return (csharpJobMs, cppJobMs,
//                ispcSystemMs, ispcDefaultMs, ispcFastMs,
//                ispcSystemMTMs, ispcDefaultMTMs, ispcFastMTMs,
//                fillCSharpMs, fillCPPMs,
//                fillIspcSystemMs, fillIspcDefaultMs, fillIspcFastMs,
//                fillIspcSystemMTMs, fillIspcDefaultMTMs, fillIspcFastMTMs);
//    }
//}