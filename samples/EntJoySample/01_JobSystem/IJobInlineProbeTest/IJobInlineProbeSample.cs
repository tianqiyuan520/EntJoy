using System;
using System.Diagnostics;
using System.Threading;
using EntJoy.JobSystem;

namespace EntJoySample.IJobInlineProbeTest
{
    // ============================================================
    // IJobInlineProbe —— 验证 IJob / IJobFor 的 Schedule 一律异步提交
    // （对齐 Unity JobSystem：调用线程只提交，不执行）。
    //
    // 判定依据：
    //   a) Schedule() 调用墙钟耗时 —— 一律 µs 级（仅提交）；若 ≈ job 工作总量
    //      则说明又出现 inline 同步执行（阻塞），即回归。
    //   b) job 内执行线程 —— 必须为 worker 线程，不得是主线程。
    //
    // 用例（小/大 × 两种接口）：
    //   IJob    Light : 空体
    //   IJob    Heavy : 忙等 50ms                  → 必须异步（若阻塞 50ms 即回归）
    //   IJobFor n=100  Light : 每元素空
    //   IJobFor n=100  Heavy : 每元素忙等 0.5ms     → 总数 ~50ms，必须异步
    //   IJobFor n=100000 Light : 每元素空
    // ============================================================

    // ---- 执行线程记录（job 内写入，Complete 后读取） ----
    internal static class ExecTrace
    {
        public static int ThreadId;
        public static bool IsThreadPool;
    }

    public struct LightIJob : IJob
    {
        public void Execute()
        {
            ExecTrace.ThreadId = Environment.CurrentManagedThreadId;
            ExecTrace.IsThreadPool = Thread.CurrentThread.IsThreadPoolThread;
        }
    }

    public struct HeavyIJob : IJob
    {
        public int SpinMs;
        public void Execute()
        {
            ExecTrace.ThreadId = Environment.CurrentManagedThreadId;
            ExecTrace.IsThreadPool = Thread.CurrentThread.IsThreadPoolThread;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < SpinMs) { /* 忙等：占用当前线程 */ }
        }
    }

    public struct LightForJob : IJobFor
    {
        public void Execute(int index)
        {
            ExecTrace.ThreadId = Environment.CurrentManagedThreadId;
            ExecTrace.IsThreadPool = Thread.CurrentThread.IsThreadPoolThread;
        }
    }

    public struct LightParForJob : IJobParallelFor
    {
        public void Execute(int index)
        {
            ExecTrace.ThreadId = Environment.CurrentManagedThreadId;
            ExecTrace.IsThreadPool = Thread.CurrentThread.IsThreadPoolThread;
        }
    }

    public struct HeavyPerElemForJob : IJobFor
    {
        public int SpinMicrosPerElem;
        public void Execute(int index)
        {
            ExecTrace.ThreadId = Environment.CurrentManagedThreadId;
            ExecTrace.IsThreadPool = Thread.CurrentThread.IsThreadPoolThread;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalMilliseconds * 1000 < SpinMicrosPerElem) { /* 每元素忙等 */ }
        }
    }

    public sealed class IJobInlineProbeSample : IDisposable
    {
        private readonly int _mainThreadId = Environment.CurrentManagedThreadId;

        /// <summary>只计 Schedule() 墙钟耗时（不含 Complete），reps 次取中位数。</summary>
        private static double ScheduleMedianMs(Func<JobHandle> schedule, int reps)
        {
            var samples = new double[reps];
            for (int i = 0; i < reps; i++)
            {
                var sw = Stopwatch.StartNew();
                var h = schedule(); // 仅计时 Schedule
                sw.Stop();
                samples[i] = sw.Elapsed.TotalMilliseconds;
                h.Complete();
            }
            Array.Sort(samples);
            return samples[reps / 2];
        }

        public void Run()
        {
            Console.WriteLine("=== IJob / IJobFor Schedule Inline Probe ===\n");
            Console.WriteLine($"  Main thread id      : {_mainThreadId}");
            Console.WriteLine($"  Workers             : {JobScheduler.WorkerCount}");
            Console.WriteLine($"  Native backend      : {JobScheduler.IsNative}\n");

            Console.WriteLine("  [接口]      [case]                        Schedule 耗时   执行线程  判定");
            Console.WriteLine("  ---------------------------------------------------------------------------");

            Probe("IJob", "Light(空体)", 101, () => new LightIJob().Schedule(), expectAsync: true);
            Probe("IJob", "Heavy(50ms忙等)", 3, () => new HeavyIJob { SpinMs = 50 }.Schedule(), expectAsync: true);

            Probe("IJobFor", "n=100   Light", 101, () => new LightForJob().Schedule(100), expectAsync: true);
            Probe("IJobFor", "n=100   Heavy(0.5ms/elem)", 3, () => new HeavyPerElemForJob { SpinMicrosPerElem = 500 }.Schedule(100), expectAsync: true);
            Probe("IJobFor", "n=100000 Light", 101, () => new LightForJob().Schedule(100_000), expectAsync: true);

            RunScheduleModes();
        }

        // ============================================================
        // 第二部分：空 job × 三模式调度性能（100 job/轮，5 轮中位）
        //   模式 1 S+C         : 每 job Schedule 后立即 Complete（逐次往返）
        //   模式 2 只S         : 100 个 Schedule 全部提交 → 最后统一 Complete
        //   模式 3 ImplicitBatch: 开 native 隐式批 → Schedule 挂 pending →
        //                         EndFrame() 统一提交 + 单次唤醒 → 统一 Complete
        // 接口覆盖：IJob / IJobFor（不收集） + IJobParallelFor（SubmitBatch 批路径，隐式批生效）
        // ============================================================
        private const int ModesReps = 5;

        private static void ModeSyncComplete(int jobCount, Func<JobHandle> schedule)
        {
            for (int i = 0; i < jobCount; i++) { var h = schedule(); h.Complete(); }
        }

        private static void ModeScheduleOnly(int jobCount, Func<JobHandle> schedule)
        {
            var hs = new JobHandle[jobCount];
            for (int i = 0; i < jobCount; i++) hs[i] = schedule();
            for (int i = 0; i < jobCount; i++) hs[i].Complete();
        }

        private static void ModeImplicitBatch(int jobCount, Func<JobHandle> schedule)
        {
            NativeJobScheduler.SetImplicitBatchEnabled(true);
            try
            {
                var hs = new JobHandle[jobCount];
                for (int i = 0; i < jobCount; i++) hs[i] = schedule(); // 挂 pending，不提交
                NativeJobScheduler.EndFrame();                          // force point：统一提交 + 单次唤醒
                for (int i = 0; i < jobCount; i++) hs[i].Complete();
            }
            finally
            {
                NativeJobScheduler.SetImplicitBatchEnabled(false); // 关闭 native 收集（排空）
                ImplicitBatch.SetEnabled(false);                   // 转接后的 C# 层也关闭 → 恢复默认全关
            }
        }

        private static double MedianTotalMs(Action mode)
        {
            mode(); // 预热
            var samples = new double[ModesReps];
            for (int i = 0; i < ModesReps; i++)
            {
                var sw = Stopwatch.StartNew();
                mode();
                sw.Stop();
                samples[i] = sw.Elapsed.TotalMilliseconds;
            }
            Array.Sort(samples);
            return samples[ModesReps / 2];
        }

        /// <summary>IB 模式下单独测 Schedule 阶段（仅挂 pending，不含 EndFrame/Complete）耗时。</summary>
        private static double IbScheduleOnlyMs(int jobCount, Func<JobHandle> schedule)
        {
            var samples = new double[ModesReps];
            for (int r = 0; r < ModesReps; r++)
            {
                NativeJobScheduler.SetImplicitBatchEnabled(true);
                var sw = Stopwatch.StartNew();
                var hs = new JobHandle[jobCount];
                for (int i = 0; i < jobCount; i++) hs[i] = schedule(); // 仅挂 pending
                sw.Stop();
                samples[r] = sw.Elapsed.TotalMilliseconds;
                NativeJobScheduler.EndFrame();                          // 统一提交 + 单次唤醒
                for (int i = 0; i < jobCount; i++) hs[i].Complete();
                NativeJobScheduler.SetImplicitBatchEnabled(false);
                ImplicitBatch.SetEnabled(false);
            }
            Array.Sort(samples);
            return samples[ModesReps / 2];
        }

        private static void RunScheduleModes()
        {
            Console.WriteLine();
            Console.WriteLine("=== 空 job 调度性能（5 轮中位）===");
            Console.WriteLine("  接口                   模式            总耗时    每job     说明");
            Console.WriteLine("  ---------------------------------------------------------------------");

            // IJob（不收集）
            ModeRow("IJob (100)", "S+C", () => ModeSyncComplete(100, () => new LightIJob().Schedule()),
                "逐次往返", 100);
            ModeRow("IJob (100)", "只S", () => ModeScheduleOnly(100, () => new LightIJob().Schedule()),
                "批量提交无逐次等待", 100);
            ModeRow("IJob (100)", "ImplicitBatch", () => ModeImplicitBatch(100, () => new LightIJob().Schedule()),
                $"IJob 不收集；Schedule 阶段 {IbScheduleOnlyMs(100, () => new LightIJob().Schedule()):F3} ms", 100);

            // IJobFor n=1000（不收集）
            ModeRow("IJobFor n=1000 (100)", "S+C", () => ModeSyncComplete(100, () => new LightForJob().Schedule(1000)),
                "逐次往返", 100);
            ModeRow("IJobFor n=1000 (100)", "只S", () => ModeScheduleOnly(100, () => new LightForJob().Schedule(1000)),
                "批量提交无逐次等待", 100);
            ModeRow("IJobFor n=1000 (100)", "ImplicitBatch", () => ModeImplicitBatch(100, () => new LightForJob().Schedule(1000)),
                $"IJobFor 不收集；Schedule 阶段 {IbScheduleOnlyMs(100, () => new LightForJob().Schedule(1000)):F3} ms", 100);

            // IJobParallelFor n=8192 × 100（SubmitBatch 批路径 → 隐式批生效）
            ModeRow("IJobParFor n=8192 (100)", "S+C", () => ModeSyncComplete(100, () => new LightParForJob().Schedule(8192, 128)),
                "逐次往返", 100);
            ModeRow("IJobParFor n=8192 (100)", "只S", () => ModeScheduleOnly(100, () => new LightParForJob().Schedule(8192, 128)),
                "批量提交，逐次唤醒", 100);
            ModeRow("IJobParFor n=8192 (100)", "ImplicitBatch", () => ModeImplicitBatch(100, () => new LightParForJob().Schedule(8192, 128)),
                $"挂 pending；Schedule 阶段 {IbScheduleOnlyMs(100, () => new LightParForJob().Schedule(8192, 128)):F3} ms", 100);

            // 放大 job 数 ×1000：唤醒次数 1000 vs 1，隐式批收益显性化
            ModeRow("IJobParFor n=1024 (1000)", "S+C", () => ModeSyncComplete(1000, () => new LightParForJob().Schedule(1024, 64)),
                "逐次往返", 1000);
            ModeRow("IJobParFor n=1024 (1000)", "只S", () => ModeScheduleOnly(1000, () => new LightParForJob().Schedule(1024, 64)),
                "批量提交，1000 次唤醒", 1000);
            ModeRow("IJobParFor n=1024 (1000)", "ImplicitBatch", () => ModeImplicitBatch(1000, () => new LightParForJob().Schedule(1024, 64)),
                $"挂 pending；Schedule 阶段 {IbScheduleOnlyMs(1000, () => new LightParForJob().Schedule(1024, 64)):F3} ms", 1000);
        }

        private static void ModeRow(string iface, string mode, Action run, string note, int jobCount)
        {
            double total = MedianTotalMs(run);
            Console.WriteLine($"  {iface,-22} {mode,-14} {total,7:F3} ms {total / jobCount * 1000,7:F2} µs  {note}");
        }

        private void Probe(string iface, string label, int reps, Func<JobHandle> schedule, bool expectAsync)
        {
            // 预热（JIT + native 路径稳定），丢弃
            var warm = schedule(); warm.Complete();

            double scheduleMs = ScheduleMedianMs(schedule, reps);
            bool blocked = scheduleMs > 1.0;
            bool onMain = ExecTrace.ThreadId == _mainThreadId;
            bool actualAsync = !blocked && !onMain;

            string verdict;
            if (blocked)
                verdict = $"[BLOCKED] 主线程被占 {scheduleMs:F1}ms（inline 同步执行）";
            else if (onMain)
                verdict = "inline（主线程直跑，µs 级）";
            else
                verdict = $"异步（worker#{ExecTrace.ThreadId}{(ExecTrace.IsThreadPool ? ",线程池" : "")}）";

            bool mismatch = actualAsync != expectAsync;
            string flag = mismatch ? "  ⚠ 与预期不符" : "";
            Console.WriteLine($"  {iface,-10} {label,-25} {scheduleMs,11:F3} ms   {ExecTrace.ThreadId,6}   {verdict}{flag}");
        }

        public void Dispose() { }
    }
}
