using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EntJoy.Collections;
using EntJoy.JobSystem;

namespace NativeTranspilerFixture;

public static unsafe class NativeHandleContractProbe
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Run()
    {
        using var proof = new NativeArray<int>(1024, Allocator.Persistent);
        nint module = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "NativeDll.dll"));
        var live = (delegate* unmanaged[Cdecl]<long>)NativeLibrary.GetExport(module, "JobSystem_GetLiveHandleCount");

        foreach (int bytes in new[] { 1, 8, 16, 96, 4096 })
        {
            nint context = NativeJobScheduler.RentJobContext(bytes);
            try
            {
                if ((nuint)context % (nuint)(2 * IntPtr.Size) != 0)
                    throw new Exception("Pooled job context lost its native alignment.");
            }
            finally { ((delegate* unmanaged[Cdecl]<nint, void>)NativeJobScheduler.PooledJobContextCleanupPtr)(context); }
        }

        var old = new DispatchParallelJob { Proof = proof }.Schedule(1, 1);
        var stale = old;
        old.Complete();
        for (int i = 0; i < 4096; i++)
        {
            var current = new DispatchParallelJob { Proof = proof }.Schedule(1, 1);
            stale.Complete();
            if (!current._nativeHandle.IsValid || !stale.IsNull) throw new Exception("Stale handle reached a reused slot.");
            current.Complete();
        }

        var handles = new JobHandle[1024];
        Check("serial", [MethodImpl(MethodImplOptions.AggressiveOptimization)] () => new DispatchSerialJob { Proof = proof }.Schedule().Complete(), 1);
        Check("parallel_direct", [MethodImpl(MethodImplOptions.AggressiveOptimization)] () => new DispatchParallelJob { Proof = proof }.Schedule(1, 1).Complete(), 1);
        Check("parallel_adapter", [MethodImpl(MethodImplOptions.AggressiveOptimization)] () =>
        {
            var job = new DispatchParallelJob { Proof = proof };
            var handle = NativeJobScheduler.ScheduleParallelFor(ref job, 1, 1);
            NativeJobScheduler.Complete(ref handle);
        }, 1);
        Check("one_in_1024", [MethodImpl(MethodImplOptions.AggressiveOptimization)] () =>
        {
            handles[0] = new DispatchParallelJob { Proof = proof }.Schedule(1, 1);
            JobHandle.CombineDependencies(handles).Complete();
        }, 1);
        Check("two_handles", [MethodImpl(MethodImplOptions.AggressiveOptimization)] () =>
        {
            var a = new DispatchParallelJob { Proof = proof, JobIndex = 0 }.Schedule(1, 1);
            var b = new DispatchParallelJob { Proof = proof, JobIndex = 1 }.Schedule(1, 1);
            JobHandle.CombineDependencies(a, b).Complete();
        }, 2);
        Check("1000_jobs", [MethodImpl(MethodImplOptions.AggressiveOptimization)] () =>
        {
            for (int j = 0; j < 1000; j++) handles[j] = new DispatchParallelJob { Proof = proof, JobIndex = j }.Schedule(1, 1);
            JobHandle.CombineDependencies(handles).Complete();
        }, 1000);
        Check("1000_jobs_multibatch", [MethodImpl(MethodImplOptions.AggressiveOptimization)] () =>
        {
            for (int j = 0; j < 1000; j++) handles[j] = new DispatchParallelJob { Proof = proof, JobIndex = j }.Schedule(1024, 64);
            JobHandle.CombineDependencies(handles).Complete();
        }, 1000);

        using (var visits = new NativeArray<int>(1031, Allocator.Persistent))
        using (var output = new NativeArray<int>(1031, Allocator.Persistent))
        {
            foreach (var claim in new[] { ClaimPolicy.Auto, ClaimPolicy.Spread, ClaimPolicy.Adjacent })
                foreach (int batch in new[] { 1, 3, 64, 1024, 2048 })
                {
                    for (int i = 0; i < visits.Length; i++) ((int*)visits.GetUnsafePtr())[i] = 0;
                    new DispatchVisitJob { Visits = visits }.Schedule(visits.Length, batch, default, claim).Complete();
                    for (int i = 0; i < visits.Length; i++)
                        if (visits[i] != 1) throw new Exception("Indexed range missed or repeated an index.");

                    for (int i = 0; i < visits.Length; i++) ((int*)visits.GetUnsafePtr())[i] = 0;
                    new BatchFillJob { Out = output, Visits = visits }.Schedule(visits.Length, batch, default, claim).Complete();
                    for (int i = 0; i < visits.Length; i++)
                        if (visits[i] != 1 || output[i] != Math.Min(batch, visits.Length - i / batch * batch))
                            throw new Exception("Explicit batch boundaries changed.");
                }
        }

        for (int i = 0; i < 128; i++)
        {
            var current = new DispatchParallelJob { Proof = proof }.Schedule(1, 1);
            var copy = current;
            Parallel.Invoke(() => { var h = copy; h.Complete(); }, () => { var h = copy; h.Complete(); });
            if (!current.IsNull) throw new Exception("Concurrent completion retained ownership.");
        }

        var chain = default(JobHandle);
        for (int i = 0; i < 4096; i++) chain = new DispatchParallelJob { Proof = proof }.Schedule(1, 1, chain);
        chain.Complete();
        if (live() != 0) throw new Exception("Dependency chain was not reclaimed.");

        CheckBorrowedLifetime();

        // A shared ancestor can be retired through either completed branch; the
        // other branch retains its own native dependency references.
        for (int i = 0; i < 128; i++)
        {
            var parent = new DispatchParallelJob { Proof = proof }.Schedule(1, 1);
            var left = new DispatchParallelJob { Proof = proof, JobIndex = 1 }.Schedule(1, 1, parent);
            var right = new DispatchParallelJob { Proof = proof, JobIndex = 2 }.Schedule(1, 1, parent);
            var first = JobHandle.CombineDependencies(parent, left);
            var second = JobHandle.CombineDependencies(parent, right);
            first.Complete();
            second.Complete();
        }
        if (live() != 0) throw new Exception("Shared dependency graph was not reclaimed.");

        CheckAssistedShutdown();

        var beforeShutdown = new DispatchParallelJob { Proof = proof }.Schedule(1, 1);
        NativeJobScheduler.Shutdown();
        NativeJobScheduler.Initialize();
        var afterRestart = new DispatchParallelJob { Proof = proof }.Schedule(1, 1);
        beforeShutdown.Complete();
        if (!afterRestart._nativeHandle.IsValid) throw new Exception("Shutdown token reached a restarted scheduler.");
        afterRestart.Complete();

        void CheckBorrowedLifetime()
        {
            var context = (GateContext*)NativeMemory.AllocZeroed((nuint)sizeof(GateContext));
            var handle = NativeJobScheduler.ScheduleRaw(
                (nint)(delegate* unmanaged[Cdecl]<GateContext*, void>)&WaitForGate, (nint)context,
                (nint)(delegate* unmanaged[Cdecl]<GateContext*, void>)&GateCleanup);
            Task waiter = null!;
            try
            {
                if (!SpinWait.SpinUntil(() => Volatile.Read(ref context->Started) != 0, 5000))
                    throw new Exception("Scheduled work did not start asynchronously.");
                var copy = handle;
                waiter = Task.Run(() => NativeJobScheduler.Complete(ref copy));
                NativeJobScheduler.Release(handle);
                for (int i = 0; i < 4096; i++) new DispatchParallelJob { Proof = proof }.Schedule(1, 1).Complete();
            }
            finally
            {
                Volatile.Write(ref context->Allow, 1);
                if (waiter != null) waiter.GetAwaiter().GetResult();
                NativeJobScheduler.Complete(ref handle);
                if (!SpinWait.SpinUntil(() => Volatile.Read(ref context->Cleaned) == 1, 5000))
                    throw new Exception("Released in-flight work lost its cleanup.");
                NativeMemory.Free(context);
            }
            if (!SpinWait.SpinUntil(() => live() == 0, 5000)) throw new Exception("Borrowed native state was not reclaimed.");
        }

        void CheckAssistedShutdown()
        {
            NativeJobScheduler.SetMainThreadAssistEnabled(true);
            int workers = NativeJobScheduler.JobWorkerCount;
            var contexts = (GateContext*)NativeMemory.AllocZeroed((nuint)((workers + 1) * sizeof(GateContext)));
            var blockers = new NativeJobHandle[workers];
            try
            {
                for (int i = 0; i < workers; i++)
                    blockers[i] = NativeJobScheduler.ScheduleRaw(
                        (nint)(delegate* unmanaged[Cdecl]<GateContext*, void>)&WaitForGate, (nint)(contexts + i),
                        (nint)(delegate* unmanaged[Cdecl]<GateContext*, void>)&GateCleanup);
                if (!SpinWait.SpinUntil(() =>
                    {
                        for (int i = 0; i < workers; i++) if (Volatile.Read(ref contexts[i].Started) == 0) return false;
                        return true;
                    }, 5000)) throw new Exception("Workers did not enter their gated jobs.");
                var attempt = NativeJobScheduler.ScheduleRaw(
                    (nint)(delegate* unmanaged[Cdecl]<GateContext*, void>)&AttemptShutdown, (nint)(contexts + workers),
                    (nint)(delegate* unmanaged[Cdecl]<GateContext*, void>)&GateCleanup);
                NativeJobScheduler.Complete(ref attempt);
                if (contexts[workers].Started != 1) throw new Exception("Assisted shutdown probe did not execute.");
            }
            finally
            {
                for (int i = 0; i < workers; i++) Volatile.Write(ref contexts[i].Allow, 1);
                for (int i = 0; i < workers; i++) NativeJobScheduler.Complete(ref blockers[i]);
                NativeMemory.Free(contexts);
            }
            var subsequent = new DispatchParallelJob { Proof = proof }.Schedule(1, 1);
            if (!subsequent._nativeHandle.IsValid) throw new Exception("Rejected shutdown closed scheduling admission.");
            subsequent.Complete();
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Check(string name, Action body, int jobs)
        {
            // Warm the compiled dispatch, worker attachment and pool high-water
            // marks before measuring steady-state allocations.
            for (int i = 0; i < 5000; i++) body();
            long beforeProof = Sum();
            long beforeLocal = GC.GetAllocatedBytesForCurrentThread();
            long beforeAll = GC.GetTotalAllocatedBytes(true);
            long t0 = Stopwatch.GetTimestamp();
            const int repetitions = 1000;
            for (int i = 0; i < repetitions; i++) body();
            long t1 = Stopwatch.GetTimestamp();
            long allBytes = GC.GetTotalAllocatedBytes(true) - beforeAll;
            long localBytes = GC.GetAllocatedBytesForCurrentThread() - beforeLocal;
            if (Sum() - beforeProof != repetitions * jobs) throw new Exception("Execution proof failed: " + name);
            if (live() != 0) throw new Exception("Unreclaimed handles: " + name);
            Console.WriteLine($"[ALLOC] {name} local={localBytes} all={allBytes} us/call={Stopwatch.GetElapsedTime(t0, t1).TotalMicroseconds / repetitions:F3}");
            if (localBytes != 0 || allBytes != 0) throw new Exception("Managed allocations in " + name);
        }

        long Sum() { long total = 0; for (int i = 0; i < proof.Length; i++) total += proof[i]; return total; }
        Console.WriteLine("PASS: native ownership, stale copies, concurrency, dependency cleanup and zero allocations.");
    }

    private struct GateContext { public int Started, Allow, Cleaned; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void WaitForGate(GateContext* context)
    {
        Volatile.Write(ref context->Started, 1);
        while (Volatile.Read(ref context->Allow) == 0) Thread.SpinWait(32);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void GateCleanup(GateContext* context) => Interlocked.Increment(ref context->Cleaned);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void AttemptShutdown(GateContext* context)
    {
        NativeJobScheduler.Shutdown();
        Volatile.Write(ref context->Started, 1);
    }
}
