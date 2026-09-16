using System;
using System.Diagnostics;
using System.Threading;
using EntJoy.Collections;
using EntJoy.JobSystem;

namespace SafetyInterceptProbe
{
    /// <summary>
    /// 只读 <see cref="Data"/>（job 内读 ⇒ 走 <c>RegisterRead</c> + <c>_readerCount</c> 登记路径），
    /// 进入后自旋等待主线程放行 —— 以便主线程在 job **运行期**尝试访问该容器。
    /// </summary>
    public struct ReadWhileSpinJob : IJobParallelFor
    {
        public NativeArray<int> Data;   // 只读（本探针的被测路径）
        public NativeArray<int> Out;    // 写另一个容器，避免与 Data 的写者登记混淆
        public int N;

        internal static int Started;
        internal static int Gate;
        internal static int LastCtx;      // 体内看到的执行上下文（0 = 内联在调用线程执行）
        internal static int ExecCount;

        public void Execute(int index)
        {
            if (index >= N) return;
            LastCtx = (int)JobIdentity.CurrentContext;
            Out[index] = Data[index];
            Interlocked.Increment(ref Started);
            Interlocked.Increment(ref ExecCount);
            long spin = 0;
            while (Volatile.Read(ref Gate) == 0 && spin < 2_000_000_000L) spin++;
        }
    }

    internal static class Program
    {
        private const int N = 4096;
        private const int Batch = 64;   // 多 tile ⇒ 派到 worker（单 tile 会内联执行，ctx==0，测不到 job 侧登记）
        private static int _fail;

        private static int Main()
        {
            Console.WriteLine("=== 安全检查语义负向用例（P1-10：逐访问登记快路径不得吞掉违规）===");
            Console.WriteLine("    说明：读写持有追踪在 Debug(ENTJOY_SAFETY) 与 Release(ENTJOY_SAFETY_BOUNDS) 下是**同一段代码**，");
            Console.WriteLine("          两种配置都验拦截；两轮都跑是为了覆盖 **ctx 被复用**（连续两个 job 拿到同一执行上下文）时");
            Console.WriteLine("          快路径不得误判「已登记」而漏计数。\n");
            // 用**默认（Native）调度器**：本框架推荐路径，且其完成协议正确（release 在 job 的 finally 内、
            // 先于句柄完成）。Managed 回退后端的完成协议缺陷见 EntJoy docs/public/Runtime-Contracts-and-Known-Limitations.md。
            JobScheduler.Initialize();

            var data = new NativeArray<int>(N, Allocator.Persistent);
            var outp = new NativeArray<int>(N, Allocator.Persistent);
            for (int i = 0; i < N; i++) data[i] = i + 1;

            // ── 0) 正控：无 job 时主线程读写正常 ──
            int v0 = data[0];
            data[0] = v0;
            Check("0 正控：无 job 时主线程读写正常", v0 == 1, $"data[0]={v0}");

            // ── 1/2) 连续两轮：job 运行期主线程访问必须被拦截 ──
            //    第 2 轮是快路径回归的关键：新 ctx + 新 epoch 时必须**重新登记**，
            //    否则 _readerCount 为 0 ⇒ 主线程访问不再被拦截（静默漏检）。
            RunRound(data, outp, "1");
            RunRound(data, outp, "2");

            // ── 3) 正控：job 结束后主线程可写 ──
            data[1] = 42;
            Check("3 正控：job 结束后主线程可写", data[1] == 42, $"data[1]={data[1]}");

            data.Dispose();
            outp.Dispose();
            Console.WriteLine(_fail == 0 ? "\nPASS：安全检查负向用例全部通过（违规被拦截、正控正常）"
                                         : $"\nFAIL：{_fail} 项判据失败");
            return _fail == 0 ? 0 : 1;
        }

        private static void RunRound(NativeArray<int> data, NativeArray<int> outp, string tag)
        {
            ReadWhileSpinJob.Gate = 0;
            ReadWhileSpinJob.Started = 0;
            ReadWhileSpinJob.LastCtx = -12345;
            ReadWhileSpinJob.ExecCount = 0;

            var handle = new ReadWhileSpinJob { Data = data, Out = outp, N = data.Length }
                .Schedule(data.Length, Batch);
            if (tag == "1") Console.WriteLine($"    调度后端：{handle.GetType().Name}（Native / Managed 两条后端的完成协议不同，见下方 KNOWN 注）");

            var sw = Stopwatch.StartNew();
            while (Volatile.Read(ref ReadWhileSpinJob.Started) == 0 && sw.ElapsedMilliseconds < 5000) { }
            bool running = !handle.IsCompleted;
            Check($"{tag}a job 已在 worker 上运行", ReadWhileSpinJob.Started > 0,
                $"_started={ReadWhileSpinJob.Started}，已完成={!running}，体内 ctx={ReadWhileSpinJob.LastCtx}（0=内联执行）");

            bool readBlocked = false, writeBlocked = false;
            string readMsg = "未抛出", writeMsg = "未抛出";
            try { int _ = data[0]; }
            catch (InvalidOperationException ex) { readBlocked = true; readMsg = Head(ex.Message); }
            try { data[0] = 7; }
            catch (InvalidOperationException ex) { writeBlocked = true; writeMsg = Head(ex.Message); }

            Check($"{tag}b job 运行期主线程**读**被拦截", readBlocked, readMsg);
            Check($"{tag}c job 运行期主线程**写**被拦截", writeBlocked, writeMsg);

            Volatile.Write(ref ReadWhileSpinJob.Gate, 1);
            handle.Complete();

            // 完成后主线程必须可访问（正控）。在 **Native 默认后端**上 release 发生在 job 的 finally 内、
            // 先于句柄完成 ⇒ 这里应当 0 次重试；保留极短重试只是为了在 Managed 回退后端下也能如实报数
            // （该后端"完成计数先归零、读声明后释放"，见 docs/public/Runtime-Contracts-and-Known-Limitations.md）。
            int retries = 0;
            while (true)
            {
                try { long _ = data[0]; break; }
                catch (InvalidOperationException)
                {
                    if (retries++ > 2000)
                    {
                        Check($"{tag}d Complete 后主线程可读（含重试）", false, "重试 2000 次仍被拦截");
                        return;
                    }
                    Thread.Sleep(1);
                }
            }

            long bad = 0;
            for (int i = 0; i < outp.Length; i++)
                if (outp[i] != data[i]) bad++;
            Check($"{tag}d Complete 后主线程可读 + 计算结果正确", bad == 0,
                $"不一致={bad}，完成后首次可读的重试次数={retries}");
        }

        private static string Head(string msg)
            => msg.Length <= 72 ? msg : msg.Substring(0, 72) + "…";

        private static void Check(string name, bool ok, string detail)
        {
            if (!ok) _fail++;
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}  ({detail})");
        }
    }
}
