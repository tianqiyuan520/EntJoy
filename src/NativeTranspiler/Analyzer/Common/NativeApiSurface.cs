using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace NativeTranspiler.Analyzer.Common
{
    /// <summary>
    /// 原生（C++/ISPC）侧允许调用的 API 面：白名单、整类映射、「不参与发射」清单的单一来源。
    /// 新增 API 只改本文件。
    /// </summary>
    internal static class NativeApiSurface
    {
        /// <summary>映射为 C++ 内建/运算符的种类（不生成自己的定义）。</summary>
        internal enum ApiKind
        {
            SystemMath,
            Interlocked,
            EntJoyMath,
            UnsafeUtility,
            Hint,
        }

        /// <summary>发射语义入口：调用被翻译器就地展开（如 SendEvent），不判签名、不生成定义。</summary>
        private static readonly string[] EmitEntryTypeNames =
        {
            Config.TypeWorld,
            Config.TypeEntityManager,
            Config.TypeSystemAPI,
        };

        /// <summary>「类型全名.方法名」→ 种类。</summary>
        private static readonly Dictionary<string, ApiKind> MappedStatics = new()
        {
            // System.Math / System.MathF
            ["System.Math.Abs"] = ApiKind.SystemMath, ["System.MathF.Abs"] = ApiKind.SystemMath,
            ["System.Math.Acos"] = ApiKind.SystemMath, ["System.MathF.Acos"] = ApiKind.SystemMath,
            ["System.Math.Asin"] = ApiKind.SystemMath, ["System.MathF.Asin"] = ApiKind.SystemMath,
            ["System.Math.Atan"] = ApiKind.SystemMath, ["System.MathF.Atan"] = ApiKind.SystemMath,
            ["System.Math.Atan2"] = ApiKind.SystemMath, ["System.MathF.Atan2"] = ApiKind.SystemMath,
            ["System.Math.Ceiling"] = ApiKind.SystemMath, ["System.MathF.Ceiling"] = ApiKind.SystemMath,
            ["System.Math.Clamp"] = ApiKind.SystemMath, ["System.MathF.Clamp"] = ApiKind.SystemMath,
            ["System.Math.Cos"] = ApiKind.SystemMath, ["System.MathF.Cos"] = ApiKind.SystemMath,
            ["System.Math.Cosh"] = ApiKind.SystemMath, ["System.MathF.Cosh"] = ApiKind.SystemMath,
            ["System.Math.Exp"] = ApiKind.SystemMath, ["System.MathF.Exp"] = ApiKind.SystemMath,
            ["System.Math.Floor"] = ApiKind.SystemMath, ["System.MathF.Floor"] = ApiKind.SystemMath,
            ["System.Math.Log"] = ApiKind.SystemMath, ["System.MathF.Log"] = ApiKind.SystemMath,
            ["System.Math.Log10"] = ApiKind.SystemMath, ["System.MathF.Log10"] = ApiKind.SystemMath,
            ["System.Math.Max"] = ApiKind.SystemMath, ["System.MathF.Max"] = ApiKind.SystemMath,
            ["System.Math.Min"] = ApiKind.SystemMath, ["System.MathF.Min"] = ApiKind.SystemMath,
            ["System.Math.Pow"] = ApiKind.SystemMath, ["System.MathF.Pow"] = ApiKind.SystemMath,
            ["System.Math.Round"] = ApiKind.SystemMath, ["System.MathF.Round"] = ApiKind.SystemMath,
            ["System.Math.Sin"] = ApiKind.SystemMath, ["System.MathF.Sin"] = ApiKind.SystemMath,
            ["System.Math.Sinh"] = ApiKind.SystemMath, ["System.MathF.Sinh"] = ApiKind.SystemMath,
            ["System.Math.Sqrt"] = ApiKind.SystemMath, ["System.MathF.Sqrt"] = ApiKind.SystemMath,
            ["System.Math.Tan"] = ApiKind.SystemMath, ["System.MathF.Tan"] = ApiKind.SystemMath,
            ["System.Math.Tanh"] = ApiKind.SystemMath, ["System.MathF.Tanh"] = ApiKind.SystemMath,
            ["System.Math.Truncate"] = ApiKind.SystemMath, ["System.MathF.Truncate"] = ApiKind.SystemMath,

            // IsNaN / IsInfinity：ToDisplayString() 对 C# 关键字别名返回 "float"/"double"
            ["System.Single.IsNaN"] = ApiKind.SystemMath, ["System.Double.IsNaN"] = ApiKind.SystemMath,
            ["float.IsNaN"] = ApiKind.SystemMath, ["double.IsNaN"] = ApiKind.SystemMath,
            ["System.Single.IsInfinity"] = ApiKind.SystemMath, ["System.Double.IsInfinity"] = ApiKind.SystemMath,
            ["float.IsInfinity"] = ApiKind.SystemMath, ["double.IsInfinity"] = ApiKind.SystemMath,

            // System.Threading.Interlocked
            ["System.Threading.Interlocked.Increment"] = ApiKind.Interlocked,
            ["System.Threading.Interlocked.Decrement"] = ApiKind.Interlocked,
            ["System.Threading.Interlocked.Add"] = ApiKind.Interlocked,
            ["System.Threading.Interlocked.Exchange"] = ApiKind.Interlocked,
            ["System.Threading.Interlocked.CompareExchange"] = ApiKind.Interlocked,
            ["System.Threading.Interlocked.Read"] = ApiKind.Interlocked,

            // EntJoy.Mathematics.math
            ["EntJoy.Mathematics.math.dot"] = ApiKind.EntJoyMath,
            ["EntJoy.Mathematics.math.lengthsq"] = ApiKind.EntJoyMath,
            ["EntJoy.Mathematics.math.length"] = ApiKind.EntJoyMath,
            ["EntJoy.Mathematics.math.normalize"] = ApiKind.EntJoyMath,
            ["EntJoy.Mathematics.math.abs"] = ApiKind.EntJoyMath,
            ["EntJoy.Mathematics.math.min"] = ApiKind.EntJoyMath,
            ["EntJoy.Mathematics.math.max"] = ApiKind.EntJoyMath,
            ["EntJoy.Mathematics.math.clamp"] = ApiKind.EntJoyMath,
            ["EntJoy.Mathematics.math.lerp"] = ApiKind.EntJoyMath,
            ["EntJoy.Mathematics.math.floor"] = ApiKind.EntJoyMath,
            ["EntJoy.Mathematics.math.ceil"] = ApiKind.EntJoyMath,
            ["EntJoy.Mathematics.math.distancesq"] = ApiKind.EntJoyMath,

            ["EntJoy.Collections.UnsafeUtility.ArrayElementAsRef"] = ApiKind.UnsafeUtility,
            ["EntJoy.Hint.Likely"] = ApiKind.Hint,
            ["EntJoy.Hint.Unlikely"] = ApiKind.Hint,
        };

        /// <summary>整类放行：其静态方法全部映射为内建。</summary>
        private const string TypeCompilerUnsafe = "System.Runtime.CompilerServices.Unsafe";

        /// <summary>不参与 C++/ISPC 发射的包（内容已映射为内建）。</summary>
        private static readonly HashSet<string> NoEmissionTypeNames = new()
        {
            "EntJoy.Mathematics.math",
            "EntJoy.Collections.UnsafeUtility",
            "EntJoy.Hint",
        };

        /// <summary>白名单查询；命中则给出种类。</summary>
        internal static bool TryGetMappedKind(string typeDotMethod, out ApiKind kind)
            => MappedStatics.TryGetValue(typeDotMethod, out kind);

        /// <summary>整类放行查询。</summary>
        internal static bool IsMappedType(string? containingTypeFullName)
            => containingTypeFullName == TypeCompilerUnsafe;

        /// <summary>是否为发射语义入口类型（World / EntityManager / SystemAPI）。</summary>
        internal static bool IsEmitEntryType(ITypeSymbol? containingType, Compilation compilation)
        {
            if (containingType == null) return false;
            foreach (var name in EmitEntryTypeNames)
                if (SymbolEqualityComparer.Default.Equals(containingType, compilation.GetTypeByMetadataName(name)))
                    return true;
            return false;
        }

        /// <summary>该类型是否不参与 C++/ISPC 发射。</summary>
        internal static bool SkipsEmission(string? containingTypeFullName)
            => containingTypeFullName != null && NoEmissionTypeNames.Contains(containingTypeFullName);

        /// <summary>ArchetypeChunk 上允许在原生内核里调用的方法。</summary>
        internal static bool IsAllowedChunkMethod(IMethodSymbol method, Compilation compilation)
            => SymbolEqualityComparer.Default.Equals(method.ContainingType, compilation.GetTypeByMetadataName(Config.TypeArchetypeChunk))
               && (method.Name == Config.GetComponentDataNativeArray
                   || method.Name == Config.GetComponentDataSpan
                   || method.Name == Config.GetEnableBitMapPtr);
    }
}
