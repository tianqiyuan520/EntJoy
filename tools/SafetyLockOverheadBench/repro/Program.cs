using System;
using System.Reflection;
using System.Threading;
using EntJoy.Collections;
using EntJoy.JobSystem;

internal struct MoveIndexer : IJobParallelFor
{
    public NativeArray<float> A, B, C, D;
    public float Dt;
    public void Execute(int index)
    {
        float a = A[index], b = B[index], c = C[index], d = D[index];
        A[index] = a + c * Dt; B[index] = b + d * Dt;
        C[index] = c; D[index] = d;
    }
}

internal struct MoveSpan : IJobParallelFor
{
    public NativeArray<float> A, B, C, D;
    public float Dt;
    public void Execute(int index)
    {
        Span<float> a = A.AsSpan(), b = B.AsSpan(), c = C.AsSpan(), d = D.AsSpan();
        float av = a[index], bv = b[index], cv = c[index], dv = d[index];
        a[index] = av + cv * Dt; b[index] = bv + dv * Dt;
        c[index] = cv; d[index] = dv;
    }
}

internal static class Program
{
    private static readonly Type Mgr = typeof(NativeArray<int>).Assembly.GetType("EntJoy.Collections.SafetyHandleManager")!;
    private static readonly FieldInfo FW = Mgr.GetField("_writerCtx", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly FieldInfo FR = Mgr.GetField("_readerCount", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly FieldInfo FCW = Mgr.GetField("_ctxWrites", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly FieldInfo FCR = Mgr.GetField("_ctxReads", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static int CtxWrites => ((System.Collections.ICollection)FCW.GetValue(null)!).Count;
    private static int CtxReads => ((System.Collections.ICollection)FCR.GetValue(null)!).Count;

    private static string Dirty()
    {
        var w = (nint[])FW.GetValue(null)!;
        var r = (int[])FR.GetValue(null)!;
        string s = "";
        for (int i = 0; i < w.Length; i++)
            if (w[i] != 0 || r[i] != 0) s += $" slot{i}(w={w[i]},r={r[i]})";
        return s.Length == 0 ? "-" : s;
    }

    private static void Main(string[] args)
    {
        int attempts = args.Length > 0 ? int.Parse(args[0]) : 40;
        int n = args.Length > 1 ? int.Parse(args[1]) : 262144;
        int batch = args.Length > 2 ? int.Parse(args[2]) : 16384;
        JobScheduler.Initialize();
        Console.WriteLine($"backend={(JobScheduler.IsNative ? "Native" : "Managed")} N={n} batch={batch} attempts={attempts}");

        var a = new NativeArray<float>(n, Allocator.Persistent);
        var b = new NativeArray<float>(n, Allocator.Persistent);
        var c = new NativeArray<float>(n, Allocator.Persistent);
        var d = new NativeArray<float>(n, Allocator.Persistent);
        for (int i = 0; i < n; i++) { a[i] = 100f; b[i] = 100f; c[i] = 1.5f; d[i] = 1f; }

        int hits = 0, maxW = 0, maxR = 0;
        for (int t = 1; t <= attempts; t++)
        {
            for (int r = 0; r < 20; r++)
            {
                var j = new MoveIndexer { A = a, B = b, C = c, D = d, Dt = 0.016f };
                JobScheduler.ScheduleParallelFor(ref j, n, batch).Complete();
            }
            if (t % 2 == 0) Thread.Sleep(t % 3 * 2);
            for (int r = 0; r < 20; r++)
            {
                var j = new MoveSpan { A = a, B = b, C = c, D = d, Dt = 0.016f };
                JobScheduler.ScheduleParallelFor(ref j, n, batch).Complete();
            }

            maxW = Math.Max(maxW, CtxWrites);
            maxR = Math.Max(maxR, CtxReads);
            Console.WriteLine($"[t{t}] done"); Console.Out.Flush();

            string bad = "";
            try { _ = a[0]; _ = b[0]; _ = c[0]; _ = d[0]; }
            catch (Exception ex) { bad = ex.Message; }
            if (bad.Length != 0)
            {
                hits++;
                Console.WriteLine($"attempt {t}: 触发！{bad}  残留: {Dirty()}");
                if (hits >= 3) break;
            }
        }
        Console.WriteLine($"触发器: 触发={hits}  残留状态={Dirty()}  _ctxWrites max={maxW} _ctxReads max={maxR}");

        JobScheduler.Shutdown();
        a.Dispose(); b.Dispose(); c.Dispose(); d.Dispose();
    }
}
