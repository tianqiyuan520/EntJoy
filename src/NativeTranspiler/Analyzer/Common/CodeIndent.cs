using System.Text;

namespace NativeTranspiler.Analyzer.Common
{
    /// <summary>生成文本的缩进工具：把整段译文嵌进外层块时统一加缩进，不改动其它字符。</summary>
    internal static class CodeIndent
    {
        /// <summary>
        /// 给多行文本每行加 <paramref name="levels"/> 级（4 空格）缩进，保留原有行尾。
        /// 预处理指令（`#if` / `#pragma` / `#endif`）保持第 0 列，与文件里的其它指令一致。
        /// </summary>
        public static string Shift(string text, int levels)
        {
            if (string.IsNullOrEmpty(text) || levels <= 0) return text;
            string pad = new string(' ', levels * 4);
            string[] lines = text.Split('\n');
            var sb = new StringBuilder(text.Length + lines.Length * pad.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                bool crlf = line.EndsWith("\r");
                if (crlf) line = line.Substring(0, line.Length - 1);
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("#", System.StringComparison.Ordinal))
                    sb.Append(trimmed);
                else
                {
                    if (line.Trim().Length > 0) sb.Append(pad);
                    sb.Append(line);
                }
                if (i < lines.Length - 1) sb.Append(crlf ? "\r\n" : "\n");
            }
            return sb.ToString();
        }
    }
}
