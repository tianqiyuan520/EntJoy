using System;
using System.Reflection;
using EntJoy.JobSystem;
using EntJoy.JobSystem.Managed;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// 测试用后端选择器：把「本次测试运行跑在哪条后端」变成**显式且可断言**的事实。
    ///
    /// 背景：`EntJoy.ECS.Tests` 的输出目录里默认没有 `NativeDll.dll`，而 `NativeJobCore` 的
    /// ModuleInitializer 会沿 `AppContext.BaseDirectory → 入口目录 → 程序集目录 → CWD 上溯` 探测，
    /// 于是「同一套测试」会随本地 `bin/` 是否恰好存在 NativeDll.dll 而**静默切换后端** ——
    /// 本地跑 Native、CI 跑 Managed，覆盖范围不一致却都显示绿。
    ///
    /// 用法：环境变量 `ENTJOY_TEST_BACKEND`：
    /// - `native`：要求 NativeDll.dll 真的被加载（否则**响亮失败**，不静默降级）；
    /// - `managed`：显式走纯 C# Managed 回退后端（用于覆盖回退路径）；
    /// - 未设置：保持历史行为（能加载 Native 就用 Native，否则 Managed），不做断言。
    /// </summary>
    internal static class TestBackend
    {
        private static readonly object Gate = new();
        private static bool _initialized;
        private static bool _forcedManaged;

        public static string Requested { get; } =
            (Environment.GetEnvironmentVariable("ENTJOY_TEST_BACKEND") ?? string.Empty).Trim().ToLowerInvariant();

        /// <summary>本次运行是否被显式要求走 Managed 回退。</summary>
        public static bool IsForcedManaged => _forcedManaged;

        /// <summary>本次运行实际使用的后端是否为 Native。</summary>
        public static bool IsNative => JobScheduler.IsNative;

        /// <summary>一次性初始化调度器；多次调用幂等（不同测试类共享同一后端）。</summary>
        /// <param name="workers">
        /// worker 数量；&lt;=0 时取 <c>max(2, ProcessorCount-1)</c>：并发类用例（如「兄弟 tile 仍在飞」）
        /// 需要至少 2 个执行槽，否则在 2 核 CI 上会退化成串行而看不到竞争（历史上就因此掩盖过缺陷）。
        /// </param>
        public static void EnsureInitialized(int workers = 0)
        {
            int count = workers > 0 ? workers : Math.Max(2, Environment.ProcessorCount - 1);
            lock (Gate)
            {
                if (_initialized) return;

                switch (Requested)
                {
                    case "":
                        JobScheduler.Initialize(count);
                        break;

                    case "native":
                        JobScheduler.Initialize(count);
                        if (!JobScheduler.IsNative)
                            throw new InvalidOperationException(
                                "ENTJOY_TEST_BACKEND=native 已设置，但 NativeDll.dll 未被加载（后端回退到了 Managed）。" +
                                "请先把 NativeDll.dll 放到测试输出目录再运行；这条断言存在的意义就是防止『本以为是 Native 覆盖、实际是 Managed』。");
                        break;
                    case "managed":
                        ForceManaged(count);
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"未知的 ENTJOY_TEST_BACKEND='{Requested}'（可选值：native / managed，或留空）。");
                }

                _initialized = true;
            }
        }

        private static void ForceManaged(int workers)
        {
            // 与 tools/JobSystemBugTests/Stage1_CSharpBridge 相同的强制手段：
            // 先把 UseNative 置回 false，再初始化 Managed 调度器，避免 Schedule 分派到未初始化的 native 路径。
            var prop = typeof(JobScheduler).GetProperty("UseNative", BindingFlags.Static | BindingFlags.NonPublic);
            if (prop == null)
                throw new InvalidOperationException(
                    "无法通过反射设置 JobScheduler.UseNative —— 测试后端强制手段已失效，请更新 TestBackend。");
            prop.SetValue(null, false);

            // ⚠ 还必须切到「回退后端」：ECS 的 chunk 调度是按 `NativeJobScheduler.UseFallback` 选择
            // 纯 C# 采集路径的（ScheduleChunkCore）。只翻 UseNative 会停在混合态 ——
            // 走 native 调度入口但 NativeDll 未初始化，chunk job 静默不执行（实测 Visited=0）。
            var fallbackProp = typeof(NativeJobScheduler).GetProperty(
                "UseFallback", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (fallbackProp == null)
                throw new InvalidOperationException(
                    "无法通过反射设置 NativeJobScheduler.UseFallback —— 测试后端强制手段已失效，请更新 TestBackend。");
            fallbackProp.SetValue(null, true);

            int count = workers > 0 ? workers : Math.Max(2, Environment.ProcessorCount - 1);
            ManagedJobScheduler.Initialize(count);
            _forcedManaged = true;

            if (JobScheduler.IsNative)
                throw new InvalidOperationException("强制 Managed 失败：JobScheduler 仍报告 Native 后端。");
        }
    }
}
