using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler;

// NuGet 消费冒烟测试（Jobs-only 侧）：只通过 PackageReference 消费 EntJoy.Jobs —— 本工程**没有**
// EntJoy.ECS 引用，也没有任何仓库路径引用。用 [NativeTranspile] 写 4 种数组形态的 native job，
// 与托管结果做 parity；这同时验证"转译器产物不再耦合 ECS"这一能力在包消费下成立。

[NativeTranspile(Target = BackendTarget.Cpp)]
public struct PkgSingleJob : IJob
{
    public NativeArray<int> Values;
    public int Delta;

    public void Execute()
    {
        for (int i = 0; i < Values.Length; i++)
        {
            Values[i] += Delta;
        }
    }
}

[NativeTranspile(Target = BackendTarget.Cpp)]
public struct PkgForJob : IJobFor
{
    public NativeArray<int> Values;
    public int Delta;

    public void Execute(int index)
    {
        Values[index] += Delta;
    }
}

[NativeTranspile(Target = BackendTarget.Cpp)]
public struct PkgParallelForJob : IJobParallelFor
{
    public NativeArray<int> Values;
    public int Delta;

    public void Execute(int index)
    {
        Values[index] += Delta;
    }
}

[NativeTranspile(Target = BackendTarget.Cpp)]
public struct PkgBatchJob : IJobParallelForBatch
{
    public NativeArray<int> Values;
    public int Delta;

    public void Execute(int startIndex, int count)
    {
        for (int i = startIndex; i < startIndex + count; i++)
        {
            Values[i] += Delta;
        }
    }
}

// 托管对照（无 [NativeTranspile]）：走同一原生调度器的 C# 回调路径
public struct ManagedSingleJob : IJob
{
    public NativeArray<int> Values;
    public int Delta;

    public void Execute()
    {
        for (int i = 0; i < Values.Length; i++)
        {
            Values[i] += Delta;
        }
    }
}

public struct ManagedParallelForJob : IJobParallelFor
{
    public NativeArray<int> Values;
    public int Delta;

    public void Execute(int index)
    {
        Values[index] += Delta;
    }
}

public static class Program
{
    private const int N = 4096;
    private const int Delta = 7;
    private const int Batch = 64;

    private static int _failures;

    public static int Main()
    {
        NativeJobScheduler.Initialize();
        var single = new NativeArray<int>(N, Allocator.Persistent);
        var forJob = new NativeArray<int>(N, Allocator.Persistent);
        var parallelFor = new NativeArray<int>(N, Allocator.Persistent);
        var batch = new NativeArray<int>(N, Allocator.Persistent);
        var managedSingle = new NativeArray<int>(N, Allocator.Persistent);
        var managedParallelFor = new NativeArray<int>(N, Allocator.Persistent);
        try
        {
            Init(single); Init(forJob); Init(parallelFor); Init(batch);
            Init(managedSingle); Init(managedParallelFor);

            new PkgSingleJob { Values = single, Delta = Delta }.Schedule().Complete();
            new PkgForJob { Values = forJob, Delta = Delta }.Schedule(N, Batch).Complete();
            new PkgParallelForJob { Values = parallelFor, Delta = Delta }.Schedule(N, Batch).Complete();
            new PkgBatchJob { Values = batch, Delta = Delta }.Schedule(N, Batch).Complete();

            var mSingle = new ManagedSingleJob { Values = managedSingle, Delta = Delta };
            NativeJobHandle hs = NativeJobScheduler.Schedule(ref mSingle);
            NativeJobScheduler.Complete(ref hs);

            var mParallel = new ManagedParallelForJob { Values = managedParallelFor, Delta = Delta };
            NativeJobHandle hp = NativeJobScheduler.ScheduleParallelFor(ref mParallel, N, Batch);
            NativeJobScheduler.Complete(ref hp);

            Check("IJob/Cpp", single, managedSingle);
            Check("IJobFor/Cpp", forJob, managedSingle);
            Check("IJobParallelFor/Cpp", parallelFor, managedParallelFor);
            Check("IJobParallelForBatch/Cpp", batch, managedParallelFor);

            Console.WriteLine(_failures == 0
                ? "PASS: NuGet Jobs-only consumer (4 transpiled array-job shapes, no EntJoy.ECS reference)."
                : $"FAIL: {_failures} check(s) failed.");
            return _failures == 0 ? 0 : 1;
        }
        finally
        {
            single.Dispose(); forJob.Dispose(); parallelFor.Dispose(); batch.Dispose();
            managedSingle.Dispose(); managedParallelFor.Dispose();
            NativeJobScheduler.Shutdown();
        }
    }

    private static void Init(NativeArray<int> v)
    {
        for (int i = 0; i < v.Length; i++) v[i] = i;
    }

    private static void Check(string shape, NativeArray<int> native, NativeArray<int> reference)
    {
        for (int i = 0; i < native.Length; i++)
        {
            if (native[i] != reference[i])
            {
                Console.WriteLine($"  FAIL [{shape}]: index {i} got {native[i]}, expected {reference[i]}");
                _failures++;
                return;
            }
        }
        Console.WriteLine($"  PASS [{shape}]");
    }
}
