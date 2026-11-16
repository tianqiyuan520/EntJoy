using Microsoft.CodeAnalysis;

namespace NativeTranspiler.Analyzer.Common
{
    internal static class CppIntegerSemantics
    {
        public static bool TryGetSignedStorage(SpecialType type, out string signed, out string unsigned)
        {
            switch (type)
            {
                case SpecialType.System_Int32: signed = "int"; unsigned = "unsigned"; return true;
                case SpecialType.System_Int64: signed = "long long"; unsigned = "unsigned long long"; return true;
                default: signed = unsigned = ""; return false;
            }
        }

        /// <summary>
        /// C 风格转换的操作数是否需要再包一层括号：顶层出现运算符、逗号或空白时必须加。
        /// 逐字符扫描并跟踪 () 与 [] 深度；字符串/字符字面量按普通字符处理（多包一层括号仍然正确）。
        /// </summary>
        public static bool NeedsParensForCast(string operand)
        {
            int depth = 0;
            foreach (char c in operand)
            {
                if (c == '(' || c == '[' || c == '{') { depth++; continue; }
                if (c == ')' || c == ']' || c == '}') { depth--; continue; }
                if (depth == 0 && (char.IsWhiteSpace(c) || "+-*/%<>=!&|^?:,".IndexOf(c) >= 0))
                    return true;
            }
            return false;
        }

        /// <summary>把操作数整理成可直接跟在转换后面的形式：`x` 或 `(x + 1)`。</summary>
        public static string CastOperand(string operand)
            => NeedsParensForCast(operand) ? "(" + operand + ")" : operand;

        /// <summary>成员访问（`.v` / `.m`）的操作数：复合表达式才加括号，多余的外层括号一并去掉。</summary>
        public static string MemberOperand(string operand)
        {
            while (IsWholeParenthesized(operand))
            {
                string inner = operand.Substring(1, operand.Length - 2);
                if (NeedsParensForCast(inner)) return operand;
                operand = inner;
            }
            // 前置转换 `(T)x` 与一元运算符（`*p` / `&x` / `-x` / `!x` / `~x`）比成员访问结合得松，必须补括号。
            if (operand.Length > 0 && (operand[0] == '(' || "+-*&!~".IndexOf(operand[0]) >= 0))
                return "(" + operand + ")";
            return CastOperand(operand);
        }

        /// <summary>首个字符的 `(` 是否正好收在末尾（整段被一层括号完整包住）。</summary>
        public static bool IsWholeParenthesized(string text)
        {
            if (text.Length < 2 || text[0] != '(' || text[text.Length - 1] != ')') return false;
            int depth = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '(') depth++;
                else if (text[i] == ')')
                {
                    depth--;
                    if (depth == 0) return i == text.Length - 1;
                }
            }
            return false;
        }

        /// <summary>字段地址表达式：偏移 0 直接用 job 指针，其余按字节偏移。</summary>
        public static string JobAddress(string jobPointer, int offset)
            => offset == 0 ? jobPointer : $"(char*){jobPointer} + {offset}";
    }
}
