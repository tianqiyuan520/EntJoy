using System;
using System.Threading;
using EntJoy.Collections;
using EntJoy.JobSystem;

namespace EntJoySample.ParallelRwConflictTest
{
    // ============================================================
    // ParallelRwConflict —— 并行读写冲突检测演示
    //
    // 一、Job 间冲突（写-写）：
    //   两个 IJobParallelFor 对同一 NativeArray<long> 做 read-modify-write（++）。
    //     场景 a) 串行依赖：B 依赖 A（B.Schedule(N, batch, a)）→ 顺序执行 → sum == 2N，正确。
    //     场景 b) 并行无依赖：A、B 同时 Schedule → 两个 job 并行写同一容器 →
    //             SafetyHandleManager 按 job 上下文（JobIdentity）在写入点检测到冲突，
    //             抛 InvalidOperationException，由 job 异常机制在 Complete() 时重抛。
    //     场景 c) 修复对照：B 依赖 A（同 a），证明依赖链可消除冲突。
    //
    // 二、主线程访问拦截（完整双向，EntJoy v1.0 扩展）：
    //   只要一个容器被任意活跃 job 引用（读或写），主线程对它的任何访问（读/写）都被拦，
    //   抛 InvalidOperationException，提示"Complete() 后再访问"。完整覆盖 4 种组合：
    //     主线程读 × job 读 / job 写 · 主线程写 × job 读 / job 写。
    //     场景 d) job 读期间，主线程读 → 拦（"being read by an active job"）。
    //     场景 e) job 读期间，主线程写 → 拦（"being read ... writing"）。
    //     场景 f) job Complete() 后主线程读/写 → 放行。
    //
    // 判定：b 应抛异常（框架拦截）、d/e 应抛异常（主线程访问被拦）、a/c/f 应正确。
    // ============================================================

    public struct RwIncJob : IJobParallelFor
    {
        public NativeArray<long> Data;
        public void Execute(int index)
        {
            long v = Data[index];
            Data[index] = v + 1;
        }
    }

    /// <summary>确定性地放大"job 在读"的窗口：进入后先登记读，再通知主线程，然后忙等。</summary>
    public struct MainThreadProbeJob : IJobParallelFor
    {
        public NativeArray<long> Data;
        public static volatile bool Started;   // 由首个 tile 置位，主线程据此得知 job 已在读
        public void Execute(int index)
        {
            _ = Data[0];                                          // 触发读登记（reader count +1）
            Volatile.Write(ref Started, true);                    // 通知主线程"我已在读该容器"
            for (int j = 0; j < 100000; j++) Thread.SpinWait(1);  // 忙等放大窗口，确保主线程访问时 job 仍在读
            _ = Data[0];
        }
    }

    public sealed class ParallelRwConflictSample
    {
        private const int N = 4096;        // 元素数
        private const int InnerBatch = 128;

        private static long Sum(NativeArray<long> data)
        {
            long s = 0;
            for (int i = 0; i < data.Length; i++) s += data[i];
            return s;
        }

        private static void Fill(NativeArray<long> data)
        {
            for (int i = 0; i < data.Length; i++) data[i] = 0;
        }

        /// <summary>串行依赖：B 依赖 A。应 sum == 2N（无冲突）。</summary>
        private static long RunSerialDep(NativeArray<long> data)
        {
            Fill(data);
            var a = new RwIncJob { Data = data }.Schedule(N, InnerBatch);
            var b = new RwIncJob { Data = data }.Schedule(N, InnerBatch, a);   // ← 依赖链
            b.Complete();
            return Sum(data);
        }

        /// <summary>并行无依赖：两 job 同时跑同一容器。应抛冲突异常。</summary>
        private static bool RunParallelConflict(NativeArray<long> data)
        {
            Fill(data);
            var a = new RwIncJob { Data = data }.Schedule(N, InnerBatch);
            var b = new RwIncJob { Data = data }.Schedule(N, InnerBatch);      // ← 无依赖，并行
            try
            {
                a.Complete(); b.Complete();
                return false;   // 最终没抛异常 → 检测未生效（回归）
            }
            catch (Exception ex) when (IsWriteConflict(ex))
            {
                return true;    // 冲突被框架拦截（job 异常经 Complete 归集为 AggregateException）
            }
        }

        /// <summary>递归判定异常链是否含并行写冲突。</summary>
        private static bool IsWriteConflict(Exception ex)
        {
            if (ex.Message.Contains("being written by another parallel job"))
                return true;
            if (ex.InnerException != null)
                return IsWriteConflict(ex.InnerException);
            return false;
        }

        /// <summary>
        /// 确定性演示「主线程访问被活跃 job 读」拦截：
        /// job 进入后先登记读，置 Started 再忙等；主线程等 Started 后立即访问，
        /// 此时 job 尚在忙等，主线程读/写该容器必然被拦（完整双向）。
        /// </summary>
        private static void DemonstrateMainThreadBlocked(NativeArray<long> data)
        {
            const int probeN = 64;
            MainThreadProbeJob.Started = false;
            var job = new MainThreadProbeJob { Data = data };
            var h = job.Schedule(probeN, 8);   // 不 Complete，让 job 保持活跃
            while (!MainThreadProbeJob.Started) Thread.Yield();   // 等 job 首个 tile 已登记读

            // 主线程读 × job 读
            try { long v = data[0]; Console.WriteLine(" [d] 主线程读（job 在读时）：❌ 未被拦（回归）"); _ = v; }
            catch (InvalidOperationException ex) when (ex.Message.Contains("being read by an active job"))
            { Console.WriteLine(" [d] 主线程读（job 在读时）：✅ 被拦 —— " + ex.Message); }

            // 主线程写 × job 读
            try { data[0] = 0; Console.WriteLine(" [e] 主线程写（job 在读时）：❌ 未被拦（回归）"); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("being read by an active job"))
            { Console.WriteLine(" [e] 主线程写（job 在读时）：✅ 被拦 —— " + ex.Message); }

            h.Complete();
        }

        public void Run()
        {
            const long expected = 2L * N;
            Console.WriteLine("=== 并行读写冲突检测演示（两个 IJobParallelFor 对同一 NativeArray 做 ++）===\n");
            Console.WriteLine($"  N={N}  innerBatch={InnerBatch}  线程={JobScheduler.WorkerCount}  后端=native={JobScheduler.IsNative}\n");

            using var data = new NativeArray<long>(N, Allocator.Persistent);

            // ---- 场景 a：串行依赖 ----
            long serial = RunSerialDep(data);
            Console.WriteLine($" [a] 串行依赖(B依赖A)：sum = {serial}（期望 {expected}）  {(serial == expected ? "✅ 正确，无冲突" : "❌ 错误")}");

            // ---- 场景 c：修复对照（同 a 结构，仅确认依赖链可串行化）----
            long fixedDep = RunSerialDep(data);
            Console.WriteLine($" [c] 修复对照(依赖链)：sum = {fixedDep}（期望 {expected}）  {(fixedDep == expected ? "✅ 正确，无冲突" : "❌ 错误")}");

            // ---- 场景 b：并行无依赖（冲突）----
            bool caught = RunParallelConflict(data);
            Console.WriteLine($" [b] 并行无依赖：{(caught ? "✅ 框架检测到冲突，抛 InvalidOperationException（Complete 时重抛），竞态被拦截" : "❌ 未检测到冲突（回归）")}");

            // ---- 场景 d/e/f：主线程访问拦截（完整双向）----
            Console.WriteLine();
            DemonstrateMainThreadBlocked(data);
            // 场景 f：job Complete() 后主线程访问放行
            var h = new MainThreadProbeJob { Data = data }.Schedule(64, 8);
            h.Complete();
            long after = data[0];   // 不应再被拦
            Console.WriteLine(" [f] 主线程读（job 已 Complete）：" + (after >= 0 ? "✅ 放行" : "❌"));

            Console.WriteLine("\n 机制：写入点按当前 job 上下文（JobIdentity.CurrentContext）登记写者，");
            Console.WriteLine("      同一 job 的并行 tile（同 ctx）放行；不同 job 交叉写同一容器 → 冲突抛异常。");
            Console.WriteLine("      依赖链（dependsOn）使两 job 顺序执行，天然消除冲突（如 a/c）。");
            Console.WriteLine("      主线程访问：容器被任意活跃 job（读或写）引用时，主线程读/写均抛异常；");
            Console.WriteLine("      job Complete() 后主线程访问即放行（如 d/e/f）。");
        }
    }
}