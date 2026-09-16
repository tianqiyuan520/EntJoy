using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace EntJoy.JobSystem
{
    /// <summary>
    /// C# 侧调度分段诊断（`ENTJOY_DIAG_CSHARP_PHASE=1`）：**攒满一个窗口后只打印一次**。
    ///
    /// 为什么必须攒够再打：控制台/重定向输出的单次成本可达 100～200 µs，而诊断打印落在
    /// schedule+complete 的计时区之内 ⇒ 逐次打印会把 chunk 派发的实测值放大约 10×。
    ///
    /// 本实现按 series 独立计窗：每个 series 最多收 <see cref="WindowSize"/> 个样本，
    /// 满窗时一次性打印汇总（n / mean / p50 / min / max）并**永久关闭该 series 的采样**
    /// ⇒ 被打印污染的至多是“满窗那一次”调度，而不是窗口内每一次；窗口关闭后连计时调用都不再发生。
    ///
    /// 关闭时（未设环境变量）所有入口在 JIT 后都是“一次静态 bool 读 + 早退”，不进热路径。
    /// </summary>
    public static class CSharpPhaseDiag
    {
        /// <summary>每个 series 的采样窗口：攒满即打印一次汇总并**开启下一窗**（可调 `ENTJOY_DIAG_CSHARP_PHASE_WINDOW`）。
        /// 窗口越大，被打印污染的那一次调度占比越小（1024 ⇒ 0.1%）。</summary>
        public static readonly int WindowSize = ReadWindow();

        /// <summary>最多打印多少个窗口（防长跑刷屏）。</summary>
        public const int MaxWindows = 8;

        private static int ReadWindow()
        {
            string? raw = Environment.GetEnvironmentVariable("ENTJOY_DIAG_CSHARP_PHASE_WINDOW");
            return int.TryParse(raw, out int v) && v > 0 ? v : 1024;
        }

        /// <summary>`ENTJOY_DIAG_CSHARP_PHASE=1` 时启用。</summary>
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("ENTJOY_DIAG_CSHARP_PHASE") == "1";

        private sealed class Series
        {
            public readonly List<double> Samples = new();
            public int Windows;
        }

        private static readonly Dictionary<string, Series> s_series = new();
        private static readonly long s_freq = Stopwatch.Frequency;

        static CSharpPhaseDiag()
        {
            // 进程退出时冲刷未满窗的 series：样本数少于窗口的场景（例如探针只调度 7 次）
            // 否则什么都不会打印。退出期的打印不在任何计时区内，无扰动风险。
            if (Enabled) AppDomain.CurrentDomain.ProcessExit += static (_, __) => Flush();
        }

        /// <summary>单调时间戳（未启用时调用方不应调用；启用后用于分段计时）。</summary>
        public static long Now() => Stopwatch.GetTimestamp();

        /// <summary>把两个时间戳换算成微秒。</summary>
        public static double Us(long fromTicks, long toTicks) => (toTicks - fromTicks) * 1_000_000.0 / s_freq;

        /// <summary>该 series 是否仍在采样（窗口数未用尽）。</summary>
        public static bool Sampling(string series)
        {
            if (!Enabled) return false;
            lock (s_series)
                return !s_series.TryGetValue(series, out var s) || s.Windows < MaxWindows;
        }

        /// <summary>追加一个样本；满窗时打印一次汇总并开启下一窗。</summary>
        public static void Add(string series, double us)
        {
            if (!Enabled) return;
            string? line = null;
            lock (s_series)
            {
                if (!s_series.TryGetValue(series, out var s))
                {
                    s = new Series();
                    s_series[series] = s;
                }
                if (s.Windows >= MaxWindows) return;
                s.Samples.Add(us);
                if (s.Samples.Count >= WindowSize)
                {
                    s.Windows++;
                    line = Format(series, s.Samples, s.Windows);
                    s.Samples.Clear();
                }
            }
            // 打印放在锁外：既不拖长临界区，也不让其它 series 的采样被本 series 的 I/O 挡住。
            if (line != null) Console.WriteLine(line);
        }

        /// <summary>冲刷所有 series 的当前未满窗（进程退出前 / 手动诊断用）。</summary>
        public static void Flush()
        {
            if (!Enabled) return;
            List<string> lines = new();
            lock (s_series)
            {
                foreach (var kv in s_series)
                {
                    if (kv.Value.Samples.Count == 0 || kv.Value.Windows >= MaxWindows) continue;
                    kv.Value.Windows++;
                    lines.Add(Format(kv.Key, kv.Value.Samples, kv.Value.Windows));
                    kv.Value.Samples.Clear();
                }
            }
            foreach (var l in lines) Console.WriteLine(l);
        }

        /// <summary>清空全部 series（测试用）。</summary>
        public static void Reset()
        {
            lock (s_series) s_series.Clear();
        }

        private static string Format(string series, List<double> xs, int window)
        {
            var sorted = new List<double>(xs);
            sorted.Sort();
            double total = 0;
            foreach (var v in sorted) total += v;
            double mean = total / sorted.Count;
            double p50 = sorted[sorted.Count / 2];
            return $"[CPHS] {series} win{window}: n={sorted.Count} mean={mean:F2}us p50={p50:F2}us min={sorted[0]:F2} max={sorted[sorted.Count - 1]:F2} total={total:F1}us";
        }
    }
}
