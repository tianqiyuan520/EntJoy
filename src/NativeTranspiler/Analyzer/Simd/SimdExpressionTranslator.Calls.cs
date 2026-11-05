using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NativeTranspiler.Analyzer.Common;

namespace NativeTranspiler.Analyzer
{
    public partial class SimdControlFlowGenerator
    {

        private string TranslateInvocation(InvocationExpressionSyntax invocation)
        {
            var symbol = SymbolHelper.GetBoundSymbol(_semanticModel, invocation) as IMethodSymbol;
            if (symbol == null)
            {
                // Fallback: try name-based matching for common math functions
                // (GetSymbolInfo can fail on SyntaxFactory-created AST nodes)
                var ident = invocation.Expression as MemberAccessExpressionSyntax;
                string? fnName = ident?.Name.Identifier.Text;
                if (fnName != null)
                {
                    // Try TranslateMathFFunction first (has Sin/Cos/Sqrt/SLEEF cases for PascalCase names)
                    string fc1 = TranslateMathFFunction(fnName, invocation);
                    if (!fc1.Contains("/* unknown") && !fc1.Contains("EntJoy::Mathematics"))
                        return fc1;
                    // Fallback: TranslateMathFunction for EntJoy mathematics functions
                    string fc2 = TranslateMathFunction(fnName, invocation);
                    if (!fc2.Contains("/* unknown */"))
                        return fc2;
                }
                return $"/*{UnsupportedMarkers.Expr}SIMD_UnresolvedCall*/ 0";
            }

            string containingType = symbol.ContainingType?.ToDisplayString() ?? "";
            string methodName = symbol.Name;

            // EntJoy.Mathematics.math functions
            if (containingType == "EntJoy.Mathematics.math")
            {
                return TranslateMathFunction(methodName, invocation);
            }

            // System.MathF / System.Math
            if (containingType == "System.MathF" || containingType == "System.Math")
            {
                return TranslateMathFFunction(methodName, invocation);
            }

            // NativeArray.GetUnsafePtr
            if (NativeTranspiler.IsEntJoyContainerNamed(symbol.ContainingType, Config.NativeArray) && methodName == Config.GetUnsafePtr)
            {
                if (invocation.Expression is MemberAccessExpressionSyntax ma
                    && ma.Expression is IdentifierNameSyntax id)
                {
                    return NativeArrayBase(id, id.Identifier.Text);
                }
            }

            // UnsafeUtility.ArrayElementAsRef<T>(ptr, i) — 求值上下文（写上下文在 TranslateAssignment）
            if (InvocationName(invocation) == Config.ArrayElementAsRef && TryGetArrayElementAsRef(invocation) is { } asRefRead)
                return TranslateArrayElementAsRefValue(asRefRead.ElemType, asRefRead.PtrExpr, asRefRead.IdxExpr);

            // Fallback: emit as regular function call
            string funcCall = "";
            if (invocation.Expression is MemberAccessExpressionSyntax member)
            {
                funcCall = $"{TranslateExpression(member.Expression)}.{methodName}(";
            }
            else
            {
                funcCall = $"{methodName}(";
            }

            for (int i = 0; i < invocation.ArgumentList.Arguments.Count; i++)
            {
                if (i > 0) funcCall += ", ";
                funcCall += TranslateExpression(invocation.ArgumentList.Arguments[i].Expression);
            }
            funcCall += ")";
            return funcCall;
        }

        private string TranslateMathFunction(string methodName, InvocationExpressionSyntax invocation)
        {
            var args = invocation.ArgumentList.Arguments;

            switch (methodName)
            {
                case "min":
                case "max":
                {
                    if (args.Count >= 2)
                    {
                        string a = TranslateExpression(args[0].Expression);
                        string b = TranslateExpression(args[1].Expression);
                        VarKind k0 = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        VarKind k1 = _varAnalyzer.ClassifyExpression(args[1].Expression);
                        if (k0 >= VarKind.Varying || k1 >= VarKind.Varying)
                        {
                            string func = methodName == "min" ? "min" : "max";
                            // Ensure both are SIMD by broadcasting uniform ones
                            if (k0 < VarKind.Varying && k1 >= VarKind.Varying)
                                a = $"{b}.broadcast({a})";
                            else if (k0 >= VarKind.Varying && k1 < VarKind.Varying)
                                b = $"{a}.broadcast({b})";
                            return $"{func}({a}, {b})";
                        }
                        return $"{methodName}({a}, {b})";
                    }
                    break;
                }

                case "clamp":
                {
                    if (args.Count >= 3)
                    {
                        string v = TranslateExpression(args[0].Expression);
                        string lo = TranslateExpression(args[1].Expression);
                        string hi = TranslateExpression(args[2].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                        {
                            // For float2/int2: decompose to component-wise clamp
                            if (args[0].Expression is IdentifierNameSyntax clampId
                                && _float2VaryingVars.Contains(clampId.Identifier.Text)
                                && _variables.TryGetValue(clampId.Identifier.Text, out var clampInfo))
                            {
                                string simdType = GetSIMDTypeString(clampInfo.CppType);
                                // simdType is "simd_value<EntJoy::Mathematics::int2>" — use directly, NOT wrapping in simd_value<>
                                return $"{simdType}(max(min({v}.x, {hi}.x()), {lo}.x()), max(min({v}.y, {hi}.y()), {lo}.y()))";
                            }
                            // Default: use friend functions max/min (works for all SIMD types)
                            return $"max(min({v}, {hi}), {lo})";
                        }
                        return $"EntJoy::Mathematics::clamp({v}, {lo}, {hi})";
                    }
                    break;
                }

                case "abs":
                {
                    if (args.Count >= 1)
                    {
                        string v = TranslateExpression(args[0].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                            return $"simd_max({v}, -{v})"; // simple SIMD abs
                        return $"EntJoy::Mathematics::abs({v})";
                    }
                    break;
                }

                case "floor":
                {
                    if (args.Count >= 1)
                    {
                        string v = TranslateExpression(args[0].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                            return $"{v}.floor()";
                        return $"EntJoy::Mathematics::floor({v})";
                    }
                    break;
                }

                case "MaxValue":
                    return "std::numeric_limits<float>::max()";
                case "MinValue":
                    return "std::numeric_limits<float>::lowest()";

                case "distancesq":
                {
                    if (args.Count >= 2)
                    {
                        string a = TranslateExpression(args[0].Expression);
                        string b = TranslateExpression(args[1].Expression);
                        VarKind k0 = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        VarKind k1 = _varAnalyzer.ClassifyExpression(args[1].Expression);
                        if (k0 >= VarKind.Varying || k1 >= VarKind.Varying)
                        {
                            // Expand as SIMD using .x/.y member access on whole-type simd_value<float2>
                            // Keep inline: explicit temps increase register pressure, MSVC CSE is sufficient
                            string ax = $"{a}.x", ay = $"{a}.y";
                            string bx = $"{b}.x", by = $"{b}.y";

                            return $"({ax} - {bx}) * ({ax} - {bx}) + ({ay} - {by}) * ({ay} - {by})";
                        }
                        return $"EntJoy::Mathematics::distancesq({a}, {b})";
                    }
                    break;
                }

                case "dot":
                {
                    if (args.Count >= 2)
                    {
                        string a = TranslateExpression(args[0].Expression);
                        string b = TranslateExpression(args[1].Expression);
                        VarKind k0 = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (k0 >= VarKind.Varying)
                        {
                            // Use member access on whole-type: v_a.x * v_b.x + v_a.y * v_b.y
                            string ax = $"{a}.x", ay = $"{a}.y";
                            string bx = $"{b}.x", by = $"{b}.y";
                            return $"{ax} * {bx} + {ay} * {by}";
                        }
                        return $"EntJoy::Mathematics::dot({a}, {b})";
                    }
                    break;
                }
            }

            // Default: emit as regular function call
            string call = $"EntJoy::Mathematics::{methodName}(";
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) call += ", ";
                call += TranslateExpression(args[i].Expression);
            }
            call += ")";
            return call;
        }

        private string TranslateMathFFunction(string methodName, InvocationExpressionSyntax invocation)
        {
            var args = invocation.ArgumentList.Arguments;

            switch (methodName)
            {
                case "Min":
                case "Max":
                {
                    if (args.Count >= 2)
                    {
                        string a = TranslateExpression(args[0].Expression);
                        string b = TranslateExpression(args[1].Expression);
                        VarKind k0 = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (k0 >= VarKind.Varying)
                        {
                            string func = methodName == "Min" ? "min" : "max";
                            return $"{func}({a}, {b})";
                        }
                        return $"std::{methodName.ToLower()}({a}, {b})";
                    }
                    break;
                }

                case "Sqrt":
                {
                    if (args.Count >= 1)
                    {
                        string v = TranslateExpression(args[0].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                        {
                            // SIMD sqrt via native instruction
                            return $"simd_value<float>{{ n_sqrt_ps({v}.v) }}";
                        }
                        return $"std::sqrt({v})";
                    }
                    break;
                }

                case "Abs":
                {
                    if (args.Count >= 1)
                    {
                        string v = TranslateExpression(args[0].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                            return $"simd_max({v}, -{v})";
                        return $"std::abs({v})";
                    }
                    break;
                }

                case "Clamp":
                case "clamp":
                {
                    // Math.Clamp / math.clamp → SIMD: clamp(v, lo, hi) = min(max(v, lo), hi)
                    if (args.Count >= 3)
                    {
                        string v = TranslateExpression(args[0].Expression);
                        string lo = TranslateExpression(args[1].Expression);
                        string hi = TranslateExpression(args[2].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                            return $"min(max({v}, {lo}), {hi})";
                        return $"std::min(std::max({v}, {lo}), {hi})";
                    }
                    break;
                }

                // Lightweight native SIMD (no SLEEF needed)
                case "Ceiling":
                {
                    if (args.Count >= 1)
                    {
                        string v = TranslateExpression(args[0].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                            return $"simd_value<float>{{ n_ceil_ps({v}.v) }}";
                        return $"std::ceil({v})";
                    }
                    break;
                }
                case "Round":
                {
                    if (args.Count >= 1)
                    {
                        string v = TranslateExpression(args[0].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                            return $"simd_value<float>{{ n_round_ps({v}.v) }}";
                        return $"std::round({v})";
                    }
                    break;
                }
                case "Truncate":
                {
                    if (args.Count >= 1)
                    {
                        string v = TranslateExpression(args[0].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                            return $"simd_value<float>{{ n_trunc_ps({v}.v) }}";
                        return $"std::trunc({v})";
                    }
                    break;
                }

                // SLEEF transcendental functions (single-argument)
                case "Sin":  case "Cos":  case "Tan":
                case "Asin": case "Acos": case "Atan":
                case "Sinh": case "Cosh": case "Tanh":
                case "Exp":  case "Log":  case "Log10":
                {
                    if (args.Count >= 1)
                    {
                        string v = TranslateExpression(args[0].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                        {
                            string sleefFn = $"n_{methodName.ToLowerInvariant()}_ps";
                            return $"simd_value<float>{{ {sleefFn}({v}.v) }}";
                        }
                        return $"std::{methodName.ToLowerInvariant()}({v})";
                    }
                    break;
                }

                // SLEEF two-argument functions
                case "Atan2":
                {
                    if (args.Count >= 2)
                    {
                        string a = TranslateExpression(args[0].Expression);
                        string b = TranslateExpression(args[1].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                            return $"simd_value<float>{{ n_atan2_ps({a}.v, {b}.v) }}";
                        return $"std::atan2({a}, {b})";
                    }
                    break;
                }
                case "Pow":
                {
                    if (args.Count >= 2)
                    {
                        string a = TranslateExpression(args[0].Expression);
                        string b = TranslateExpression(args[1].Expression);
                        VarKind kv = _varAnalyzer.ClassifyExpression(args[0].Expression);
                        if (kv >= VarKind.Varying)
                            return $"simd_value<float>{{ n_pow_ps({a}.v, {b}.v) }}";
                        return $"std::pow({a}, {b})";
                    }
                    break;
                }
            }

            // Fallback: lowercase mapping (MathF.Sin → sin for SIMD ADL, std::sin for scalar)
            bool anyVarying = false;
            for (int i = 0; i < args.Count; i++)
                if (_varAnalyzer.ClassifyExpression(args[i].Expression) >= VarKind.Varying)
                    { anyVarying = true; break; }
            string funcPrefix = anyVarying ? "" : "std::";
            string call = $"{funcPrefix}{methodName.ToLowerInvariant()}(";
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) call += ", ";
                call += TranslateExpression(args[i].Expression);
            }
            call += ")";
            return call;
        }

        /// <summary>取被调用方法名（`X.M(...)` 或 `M(...)`）。</summary>
        private static string InvocationName(ExpressionSyntax expr) => expr switch
        {
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
            IdentifierNameSyntax id => id.Identifier.Text,
            _ => ""
        };

        /// <summary>Check if an invocation is a gather/gathf call (produces clamped indices).</summary>
        private static bool IsGatherCall(InvocationExpressionSyntax inv)
        {
            if (inv.Expression is IdentifierNameSyntax id)
                return id.Identifier.Text == "gather" || id.Identifier.Text == "gathf";
            if (inv.Expression is MemberAccessExpressionSyntax ma)
                return ma.Name.Identifier.Text == "gather" || ma.Name.Identifier.Text == "gathf";
            return false;
        }
    }
}
