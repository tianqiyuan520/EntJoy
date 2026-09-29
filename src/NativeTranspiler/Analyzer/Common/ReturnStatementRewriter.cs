using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NativeTranspiler.Analyzer.Common
{
    /// <summary>
    /// 批内 <c>return;</c> 的 C++ 语义重写（NT-02 / NT-07）。
    ///
    /// C# 的 <c>return;</c>（Execute 体内）只结束**本次 index / 本次实体**；C++ 的 <c>return;</c>
    /// 会退出整个导出函数。各 C++ 后端因此把体包进 <c>do { ... } while(false)</c>，并把
    /// index 层级的 <c>return;</c> 换成 <c>break;</c>（见 docs/public/NativeTranspiler-Boundaries-and-Diagnostics.md §2）。
    ///
    /// 但 <c>return;</c> 若嵌在体内**循环**里，<c>break;</c> 只会跳出那个内层循环，剩余语句照跑 ——
    /// 静默错值。这种形态在 C++ 里无法表达（与 ISPC 侧同一限制），因此写唯一标记让构建期
    /// 扫描（NativeCompileTask.CheckGeneratedMarkers 认 <c>__ENTJOY_UNSUPPORTED</c> 前缀）失败，
    /// 而不是生成语义错误的代码。
    /// </summary>
    public static class ReturnStatementRewriter
    {
        /// <summary>嵌套循环内 return 的标记名（与 ISPC 的 <c>__ENTJOY_UNSUPPORTED_STMT__ISPC_ReturnInsideNestedLoopInBatch</c> 同构）。</summary>
        public const string CppReturnInsideNestedLoop = UnsupportedMarkers.Stmt + "Cpp_ReturnInsideNestedLoop";

        /// <summary>
        /// 把翻译产物里的 <c>return;</c> 逐条重写成 C++ 批语义：
        ///   · index 层级 ⇒ <paramref name="indexLevelReplacement"/>（通常是 <c>break;</c>，需外层 do-while 包裹）
        ///   · 嵌在循环里 ⇒ <c>{ /*&lt;标记&gt;*/ }</c>（保留合法语句形状，构建期扫描报错）
        /// 重写按**源码顺序**与 ReturnStatementSyntax 一一对应（翻译器逐语句顺序输出）。
        /// 产物里若还有多余的 <c>return;</c>（多于源码里的 return 语句），按 index 层级处理。
        /// </summary>
        public static string Rewrite(string translatedBody, BlockSyntax? body, string indexLevelReplacement)
        {
            if (string.IsNullOrEmpty(translatedBody)) return translatedBody;
            const string token = "return;";
            if (translatedBody.IndexOf(token, StringComparison.Ordinal) < 0) return translatedBody;

            var flags = NestedLoopFlags(body);
            var sb = new StringBuilder(translatedBody.Length + 16);
            int occurrence = 0;
            int cursor = 0;
            while (true)
            {
                int next = translatedBody.IndexOf(token, cursor, StringComparison.Ordinal);
                if (next < 0)
                {
                    sb.Append(translatedBody, cursor, translatedBody.Length - cursor);
                    break;
                }
                sb.Append(translatedBody, cursor, next - cursor);
                bool nested = occurrence < flags.Count && flags[occurrence];
                sb.Append(nested ? $"{{ /*{CppReturnInsideNestedLoop}*/ }}" : indexLevelReplacement);
                occurrence++;
                cursor = next + token.Length;
            }
            return sb.ToString();
        }

        /// <summary>按源码顺序给出每条 <c>return;</c> 是否嵌在循环里（无体返回空表）。</summary>
        public static List<bool> NestedLoopFlags(BlockSyntax? body)
        {
            if (body == null) return new List<bool>();
            return body.DescendantNodes().OfType<ReturnStatementSyntax>().Select(IsInsideLoop).ToList();
        }

        /// <summary>return 与 Execute 体之间是否夹着循环语句。</summary>
        public static bool IsInsideLoop(ReturnStatementSyntax returnStmt)
        {
            foreach (var ancestor in returnStmt.Ancestors())
            {
                if (ancestor is ForStatementSyntax || ancestor is ForEachStatementSyntax
                    || ancestor is WhileStatementSyntax || ancestor is DoStatementSyntax)
                    return true;
                // 到方法/局部函数边界就停（不把"外层另一个方法里的循环"算进来）
                if (ancestor is MethodDeclarationSyntax || ancestor is LocalFunctionStatementSyntax)
                    return false;
            }
            return false;
        }
    }
}
