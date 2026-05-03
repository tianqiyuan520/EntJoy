using Godot;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using System.Threading.Tasks;
using Environment = System.Environment;


public struct AdditionJobSIMD : IJobParallelForBatch
{
    public float[] A;
    public float[] B;
    public float[] Result;

    public unsafe void Execute(int startIndex, int count)
    {
        fixed (float* aPtr = A, bPtr = B, resultPtr = Result)
        {
            float* a = aPtr + startIndex;
            float* b = bPtr + startIndex;
            float* result = resultPtr + startIndex;

            int i = 0;
            if (Avx2.IsSupported)
            {
                // 256-bit 向量，一次处理 8 个 float
                int vectorSize = 8;
                for (; i <= count - vectorSize; i += vectorSize)
                {
                    var va = Avx.LoadVector256(a + i);
                    var vb = Avx.LoadVector256(b + i);
                    var vr = Avx.Add(va, vb);
                    Avx.Store(result + i, vr);
                }
            }
            else if (Vector.IsHardwareAccelerated)
            {
                int vectorSize = Vector<float>.Count;
                for (; i <= count - vectorSize; i += vectorSize)
                {
                    var va = Vector.Load(a + i);
                    var vb = Vector.Load(b + i);
                    var vr = va + vb;
                    Vector.Store(vr, result + i);
                }
            }
            // 剩余元素
            for (; i < count; i++)
                result[i] = a[i] + b[i];
        }
    }

    //public unsafe void Execute(int startIndex, int count)
    //{
    //    int end = startIndex + count;

    //    fixed (float* aPtr = A, bPtr = B, resultPtr = Result)
    //    {
    //        float* a = aPtr + startIndex;
    //        float* b = bPtr + startIndex;
    //        float* result = resultPtr + startIndex;

    //        int i = 0;
    //        int vectorSize = Vector<float>.Count;
    //        for (; i <= count - vectorSize; i += vectorSize)
    //        {
    //            var va = Vector.Load(a + i);
    //            var vb = Vector.Load(b + i);
    //            var vr = va + vb;
    //            Vector.Store(vr, result + i);
    //        }
    //        // 剩余标量
    //        for (; i < count; i++)
    //            result[i] = a[i] + b[i];
    //    }
    //}



}


public unsafe struct AdditionJob : IJobParallelFor
{
    public float[] A;
    public float[] B;
    public float[] Result;
    //public int* sum;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Execute(int i)
    {
        Result[i] = A[i] + B[i];

        //System.Threading.Interlocked.Add(ref *sum, 1);
    }
}

public partial class JobAdditionTest : Node
{
    // 测试数据
    private float[] _a;
    private float[] _b;
    private float[] _result;
    private int _dataCount = 1_000_000;
    private int _time = 0;

    public override void _Ready()
    {
        Start();
        TestSingleThread();
    }

    public override void _Process(double delta)
    {
        RunTest();
    }

    private void Start()
    {
        // 分配或调整数组大小
        if (_a == null || _a.Length != _dataCount)
        {
            _a = new float[_dataCount];
            _b = new float[_dataCount];
            _result = new float[_dataCount];
        }

        // 初始化数据（每个元素分别设为 1.0 和 2.0）
        for (int i = 0; i < _dataCount; i++)
        {
            _a[i] = 1.0f;
            _b[i] = 2.0f;
        }
    }


    private unsafe void RunTest()
    {
        _time++;

        Stopwatch sw = Stopwatch.StartNew();


        int sum = 0;
        var job = new AdditionJob
        {
            A = _a,
            B = _b,
            Result = _result,
            //sum = &sum,
        };

        var counter = new ThreadCounter();

        sw.Start();

        JobHandle handle = job.Schedule(_dataCount, innerBatchCount: 0, default, counter);
        handle.Complete();
        sw.Stop();

        double parallelMs = sw.Elapsed.TotalMilliseconds;
        int usedThreads = counter.Count;

        GD.Print($"sum: {sum} / {_dataCount}");

        Array.Clear(_result, 0, _result.Length);

        sw.Restart();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
        };

        Parallel.For(0, _dataCount, options, i =>
        {
            _result[i] = _a[i] + _b[i];
        });

        sw.Stop();
        double singleMs = sw.Elapsed.TotalMilliseconds;


        // 更新 UI（必须在主线程）
        CallDeferred(nameof(UpdateUI), singleMs, parallelMs, usedThreads);


        // check ans
        //bool allCorrect = true;
        //for (int i = 0; i < _dataCount; i++)
        //{
        //    if (_result[i] != 3.0f)
        //    {
        //        allCorrect = false;
        //        GD.Print($"Error at index {i}: {_result[i]}");
        //        break;
        //    }
        //}
        //GD.Print($"All correct: {allCorrect}");
    }


    private void RunTestSIMD()
    {
        Stopwatch sw = Stopwatch.StartNew();

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
        };

        Parallel.For(0, _dataCount, options, i =>
        {
            _result[i] = _a[i] + _b[i];
        });

        sw.Stop();
        double singleMs = sw.Elapsed.TotalMilliseconds;

        var job = new AdditionJobSIMD
        {
            A = _a,
            B = _b,
            Result = _result
        };

        //var counter = new ThreadCounter();

        sw.Restart();
        int batchSize = 0;
        JobHandle handle = job.ScheduleBatch(_dataCount, batchSize: batchSize);
        handle.Complete();
        sw.Stop();

        double parallelMs = sw.Elapsed.TotalMilliseconds;

        //int usedThreads = counter.Count;
        int usedThreads = 0;

        // 更新 UI（必须在主线程）
        CallDeferred(nameof(UpdateUI), singleMs, parallelMs, usedThreads);
    }

    private void UpdateUI(double singleMs, double parallelMs, int usedThreads)
    {
        double speedup = singleMs / parallelMs;
        var text = $"数据量: {_dataCount:N0}\n" +
                            $"Parallel.For : {singleMs:F3} ms\n" +
                            $"Job System : {parallelMs:F3} ms\n" +
                            $"加速比: {speedup:F2}x\n" +
                            $"实际线程数: {usedThreads} (逻辑核心: {Environment.ProcessorCount})\n";
        GD.Print(text);
        //GD.Print(_result[^1]);
    }


    private void TestSingleThread()
    {
        // 标量版本（手动循环，不使用 Parallel）
        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < _dataCount; i++)
            _result[i] = _a[i] + _b[i];
        sw.Stop();
        double scalarMs = sw.Elapsed.TotalMilliseconds;

        // SIMD 版本（直接调用 Execute 处理全部数据）
        var job = new AdditionJobSIMD { A = _a, B = _b, Result = _result };
        sw.Restart();
        job.Execute(0, _dataCount);
        sw.Stop();
        double simdMs = sw.Elapsed.TotalMilliseconds;

        GD.Print($"单线程标量: {scalarMs:F3} ms, SIMD: {simdMs:F3} ms, 加速比: {scalarMs / simdMs:F2}x");
    }
}
