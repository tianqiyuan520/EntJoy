using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NativeTranspiler.Analyzer.Common
{
    /// <summary>
    /// job 后端归属清点（NT032）：列出"实现了 job 接口但没有 <c>[NativeTranspile]</c>"的 struct。
    /// 判据只有一条：属性在 ⇒ 原生；不在 ⇒ 托管（缺属性本身就是托管的定义，无需额外标记）。
    /// <para>
    /// 为什么值得有一条这样的诊断：未标属性的 struct 源生成器根本看不见，托管路径又是始终存在的泛型扩展
    /// </para>
    /// <para>
    /// 策略（MSBuild 属性 <c>EntJoyJobIntent</c>）：<c>off</c>（默认）关闭；<c>warn</c> 只在混合项目
    /// （本单元已有原生 job）报一条聚合清点；<c>strict</c> 一律报并升级为 error（"每个 job 都必须原生"的项目用）。
    /// </para>
    /// 保守性：泛型 struct 跳过（原生路径不支持）；整个检查包在 try/catch 里（建议性诊断，不得影响构建）。
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

        /// <summary>读取 <c>EntJoyJobIntent</c>（未设 ⇒ <see cref="Policy.Off"/>：本诊断默认关闭）。</summary>
        public static Policy ReadPolicy(AnalyzerConfigOptions options)
        {
            if (options == null) return Policy.Off;
            if (!options.TryGetValue("build_property.EntJoyJobIntent", out var raw) || string.IsNullOrWhiteSpace(raw))
                return Policy.Off;
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
