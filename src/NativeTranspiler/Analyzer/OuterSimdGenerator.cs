using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NativeTranspiler.Analyzer
{
    /// <summary>
    /// 外层 SIMD 生成器。
    /// gather 8 输入 → per-lane 标量 Execute → scatter 输出。
    /// 使用 simd_value<T> 模板对齐 EntJoy.Mathematics 类型系统。
    /// </summary>
    public class OuterSimdGenerator
    {
        private readonly string _idx;
        private readonly Dictionary<string, string> _boolFields;

        public OuterSimdGenerator(string indexVarName, Dictionary<string, string>? boolFieldValues = null)
        {
            _idx = indexVarName;
            _boolFields = boolFieldValues ?? new Dictionary<string, string>();
        }

        public string Generate(string scalarBody)
        {
            var sb = new StringBuilder();
            sb.AppendLine("    // --- Outer SIMD: gather 8 inputs, per-lane Execute ---");
            sb.AppendLine("    int simd_end_ = __startIndex + ((__count) / NSIMD_WIDTH) * NSIMD_WIDTH;");
            sb.AppendLine("    if (simd_end_ > __startIndex)");
            sb.AppendLine("    {");
            sb.AppendLine("        simd_value<int> v_base = simd_value<int>::sequence(0);");
            sb.AppendLine("        for (int si = __startIndex; si < simd_end_; si += NSIMD_WIDTH)");
            sb.AppendLine("        {");
            sb.AppendLine("            simd_value<int> v_i = v_base + si;");
            sb.AppendLine("            simd_value<EntJoy::Mathematics::float2> v_q =");
            sb.AppendLine("                simd_value<EntJoy::Mathematics::float2>::gather(QueryPositions_ptr, v_i);");
            sb.AppendLine("            float qx_buf[8]; v_q.x.store(qx_buf);");
            sb.AppendLine("            float qy_buf[8]; v_q.y.store(qy_buf);");
            sb.AppendLine("            for (int lane = 0; lane < NSIMD_WIDTH; lane++)");
            sb.AppendLine("            {");
            sb.AppendLine("                int index = si + lane;");
            foreach (var kvp in _boolFields)
                sb.AppendLine($"                bool {kvp.Key} = {kvp.Value};");
            sb.AppendLine("                EntJoy::Mathematics::float2 qbuf; qbuf.x() = qx_buf[lane]; qbuf.y() = qy_buf[lane];");

            foreach (var line in scalarBody.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None))
            {
                var l = line.TrimEnd();
                if (string.IsNullOrEmpty(l)) continue;
                if (l.Contains("QueryPositions_ptr"))
                    l = l.Replace("QueryPositions_ptr[index]", "qbuf");
                sb.Append("                ").AppendLine(l);
            }

            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine($"    for (int {_idx} = simd_end_; {_idx} < __startIndex + __count; ++{_idx})");
            sb.AppendLine("    {");
            foreach (var line in scalarBody.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                sb.Append("    ").AppendLine(line.TrimEnd());
            }
            sb.AppendLine("    }");
            return sb.ToString();
        }
    }
}
