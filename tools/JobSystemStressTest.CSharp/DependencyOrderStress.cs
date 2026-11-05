using System;
using System.Threading;
using EntJoy.JobSystem;
using EntJoy.JobSystem.Managed;

namespace JobSystemStressTest;

struct DependencyOrderJob : IJob
{
    public int[] Values, Visits, Errors;
    public int Index, ParentStart, ParentCount;
    public ManualResetEventSlim? Gate;

    public void Execute()
    {
        if (Gate != null && !Gate.Wait(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("Dependency root gate was not released.");
        for (int i = ParentStart; i < ParentStart + ParentCount; i++)
            if (Volatile.Read(ref Values[i]) != i + 1) Interlocked.Increment(ref Errors[0]);
        Interlocked.Increment(ref Visits[Index]);
        Volatile.Write(ref Values[Index], Index + 1);
    }
}

static class DependencyOrderStress
{
    public static void Run(bool native)
    {
        foreach (int width in new[] { 1, 8 })
        {
            int layers = width == 1 ? 512 : 16;
            int count = 1 + width * layers;
            int[] values = new int[count], visits = new int[count], errors = [0];
            using var gate = new ManualResetEventSlim(false);
            var nativeHandles = new NativeJobHandle[native ? count : 0];
            var managedHandles = new ManagedJobHandle[native ? 0 : count];
            try
            {
                for (int index = 0; index < count; index++)
                {
                    int parentStart = index == 0 ? 0 : index <= width ? 0 : 1 + ((index - 1) / width - 1) * width;
                    int parentCount = index == 0 ? 0 : index <= width ? 1 : width;
                    var job = new DependencyOrderJob
                    {
                        Values = values, Visits = visits, Errors = errors,
                        Index = index, ParentStart = parentStart, ParentCount = parentCount,
                        Gate = index == 0 ? gate : null
                    };
                    if (native)
                    {
                        var parents = new NativeJobHandle[parentCount];
                        Array.Copy(nativeHandles, parentStart, parents, 0, parentCount);
                        var dependency = NativeJobScheduler.CombineDependencies(parents);
                        try { nativeHandles[index] = NativeJobScheduler.Schedule(ref job, dependency); }
                        finally { NativeJobScheduler.Release(dependency); }
                    }
                    else
                    {
                        var parents = new ManagedJobHandle[parentCount];
                        Array.Copy(managedHandles, parentStart, parents, 0, parentCount);
                        managedHandles[index] = ManagedJobScheduler.Schedule(ref job, ManagedJobHandle.CombineDependencies(parents));
                    }
                }
                if (Volatile.Read(ref values[count - 1]) != 0)
                    throw new Exception("A dependent job completed while its root was gated.");
                gate.Set();
                if (native)
                {
                    var combined = NativeJobScheduler.CombineDependencies(nativeHandles);
                    NativeJobScheduler.Complete(ref combined);
                }
                else ManagedJobHandle.CombineDependencies(managedHandles).Complete();

                if (errors[0] != 0) throw new Exception($"Dependency order/visibility failures: {errors[0]}, width={width}");
                for (int i = 0; i < count; i++)
                    if (values[i] != i + 1 || visits[i] != 1)
                        throw new Exception($"Dependency node {i}: value={values[i]}, visits={visits[i]}, width={width}");
            }
            finally
            {
                gate.Set();
                if (native)
                    for (int i = 0; i < count; i++) NativeJobScheduler.Complete(ref nativeHandles[i]);
                else
                    foreach (var handle in managedHandles) handle.Complete();
            }
        }
    }
}
