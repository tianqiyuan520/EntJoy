using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NativeTranspiler.Analyzer.Common
{
    /// <summary>
    /// job 后端**归属**清点（NT032）—— 把"漏写 <c>[NativeTranspile]</c>"从**静默降级**变成一条
    /// **可读的清点**。
    ///
    /// <para><b>判据只有一条</b>：<c>[NativeTranspile]</c> 在 ⇒ 原生；**不在 ⇒ 托管**。
    /// 不需要（也不引入）任何"我是托管"的标记 —— 缺属性本身就是托管的定义。</para>
    ///
    /// <para><b>为什么要报一条</b>（三处都不会发声，所以缺了它就只能靠反汇编 obj 才发现）：
    /// <list type="number">
    ///   <item>源生成器**看不见**未标属性的 struct —— 语法提供器的谓词要求 <c>AttributeLists.Count &gt; 0</c>，
    ///         转换再按属性符号过滤 ⇒ 未标记的 struct 从不进入生成器，NT001~NT031 全部以"已标记的 job"为输入；</item>
    ///   <item>C# 编译器认为解析**完全合法** —— 托管路径是手写的**泛型**扩展
    ///         <c>JobExtensions.Schedule&lt;T&gt;</c>（始终存在），原生路径是按 job 生成的**具体**扩展；
    ///         没标属性 = 具体扩展不存在 ⇒ 无歧义地落到泛型 ⇒ 零警告；</item>
    ///   <item>运行期 <c>JobScheduler.UseNative</c> 只表示"NativeDll 是否初始化成功"，与"**这个 job**
    ///         有没有原生内核"无关 ⇒ 也没有日志。</item>
    /// </list>
    /// 后果：语义仍然正确（跑同一份 C# <c>Execute</c>）⇒ **任何正确性测试都抓不到**；但性能量级完全不同，
    /// 且 <c>IJobParallelForBatch</c> 的托管回退是 <c>JobScheduler.SequentialBatchJob</c>（**单线程串行**）。
    /// 真实案例：游戏仓 <c>ZeroCellsJob</c>（清 351,233 个 int 的串行关键路径趟）到第七轮才靠 obj 反汇编发现。</para>
    ///
    /// <para><b>策略</b>（MSBuild 属性 <c>EntJoyJobIntent</c>，由 <c>EntJoy.Jobs.props</c> 的
    /// <c>CompilerVisibleProperty</c> 传到编译期）：
    /// <list type="bullet">
    ///   <item><c>warn</c>（**默认**）：仅在"本编译单元已有 ≥1 个 <c>[NativeTranspile]</c> job"时报一条
    ///         **聚合**清点 —— 即**混合项目**（有原生意图，同时也存在托管 job），提示"这些就是没有原生内核的
    ///         job，若有本意原生却漏写的，加属性"。纯托管项目（0 个原生 job）静默：那里"全托管"是**正常形态**；</item>
    ///   <item><c>strict</c>：无论有没有原生 job 都报，并升为 **error**（"每个 job 都必须原生"的项目用）；</item>
    ///   <item><c>off</c>：关闭。</item>
    /// </list>
    /// <b>默认值本身是零噪声的</b>：本仓实测 27 个含 job 的项目里，"混合"项目只有 5 个（且都是样例/基准），
    /// 其余 16 个纯托管项目（如 <c>EntJoy.ECS.Tests</c> 的 20 个 job）一个字都不报。</para>
    ///
    /// <para><b>保守性</b>：① 泛型 struct（<c>Arity &gt; 0</c>）跳过（原生路径本就不支持泛型 job）；
    /// ② 整个检查包在 try/catch 里 —— 它是**建议性**诊断，绝不能因为符号形状异常而把消费者的构建搞崩。</para>
    /// </summary>
    internal static class JobIntentChecker
    {
        public enum Policy { Off, Warn, Strict }

        /// <summary>EntJoy 的 job 接口短名（命名空间由 SymbolHelper.IsEntJoyJobInterface 校验）。</summary>
        private static readonly string[] JobInterfaceNames =
        {
            Config.IJob, Config.IJobFor, Config.IJobParallelFor, Config.IJobParallelForBatch,
            Config.IJobChunk, Config.IJobEntity,
        };

        /// <summary>读取 <c>EntJoyJobIntent</c>（未设 ⇒ <see cref="Policy.Warn"/>）。</summary>
        public static Policy ReadPolicy(AnalyzerConfigOptions options)
        {
            if (options == null) return Policy.Warn;
            if (!options.TryGetValue("build_property.EntJoyJobIntent", out var raw) || string.IsNullOrWhiteSpace(raw))
                return Policy.Warn;
            switch (raw.Trim().ToLowerInvariant())
            {
                case "off": case "false": case "0": return Policy.Off;
                case "strict": case "error": case "true": case "1": return Policy.Strict;
                default: return Policy.Warn;
            }
        }

        /// <summary>执行清点并上报诊断。任何异常都吞掉（建议性诊断，不得影响构建）。</summary>
        public static void Report(SourceProductionContext spc, Compilation compilation,
            AnalyzerConfigOptions options, IReadOnlyCollection<INamedTypeSymbol> nativeJobs)
        {
            try
            {
                var policy = ReadPolicy(options);
                if (policy == Policy.Off) return;

                var nativeAttr = AttributeHelper.GetAttributeSymbol(compilation);
                if (nativeAttr == null) return;

                bool anyNativeJob = nativeJobs != null && nativeJobs.Count > 0;
                if (policy == Policy.Warn && !anyNativeJob) return;   // 纯托管项目：全托管是正常形态

                var managedJobs = new List<INamedTypeSymbol>();
                foreach (var type in AllTypes(compilation.Assembly.GlobalNamespace))
                {
                    if (type.TypeKind != TypeKind.Struct) continue;
                    if (type.IsImplicitlyDeclared || type.IsStatic) continue;
                    if (type.Arity > 0) continue;   // 泛型 job 不支持原生路径，跳过
                    if (!SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly)) continue;
                    if (!IsJobStruct(type)) continue;
                    // 判据只有一条：[NativeTranspile] 在 ⇒ 原生；不在 ⇒ 托管。
                    bool hasNative = type.GetAttributes().Any(a =>
                        SymbolEqualityComparer.Default.Equals(a.AttributeClass, nativeAttr));
                    if (hasNative) continue;
                    managedJobs.Add(type);
                }

                if (managedJobs.Count == 0) return;

                var names = managedJobs.Select(t => t.Name).ToList();
                string list = string.Join(", ", names.Take(8)) + (names.Count > 8 ? ", …" : "");
                // 同一 ID 两种强度：默认 warning，strict 升级为 error（用带 effectiveSeverity 的重载，
                // 因为 Diagnostic.WithSeverity 是 protected internal，生成器里用不到）。
                var severity = policy == Policy.Strict ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning;
                spc.ReportDiagnostic(Diagnostic.Create(NativeTranspileValidator.ManagedJobsInventory,
                    managedJobs[0].Locations.FirstOrDefault(), severity, null, null,
                    names.Count, list, nativeJobs?.Count ?? 0, policy.ToString().ToLowerInvariant()));
            }
            catch { /* 建议性诊断：绝不因本检查让构建失败 */ }
        }

        private static bool IsJobStruct(INamedTypeSymbol type)
        {
            foreach (var iface in type.AllInterfaces)
                foreach (var name in JobInterfaceNames)
                    if (SymbolHelper.IsEntJoyJobInterface(iface, name)) return true;
            return false;
        }

        private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceOrTypeSymbol root)
        {
            foreach (var member in root.GetMembers())
            {
                if (member is INamespaceSymbol ns)
                {
                    foreach (var t in AllTypes(ns)) yield return t;
                }
                else if (member is INamedTypeSymbol type)
                {
                    yield return type;
                    foreach (var t in AllTypes(type)) yield return t;
                }
            }
        }
    }
}
