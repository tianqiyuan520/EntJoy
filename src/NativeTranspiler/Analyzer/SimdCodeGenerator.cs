using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NativeTranspiler.Analyzer
{
    /// <summary>
    /// 根据 LoopPattern 生成 SIMD C++ 代码。
    /// 生成向量化主循环 + 水平规约 + 标量余量循环。
    /// </summary>
    public class SimdCodeGenerator
    {
        private readonly LoopPattern _pattern;

        public SimdCodeGenerator(LoopPattern pattern)
        {
            _pattern = pattern;
        }

        /// <summary>
        /// 生成完整的 batch 函数体（替换现有的标量 for 循环）。
        /// </summary>
        /// <param name="scalarBody">CppBatchStatementTranslator 翻译的标量体</param>
        public string Generate(string scalarBody)
        {
            var sb = new StringBuilder();

            // ===== 1. 循环变量 =====
            string idx = _pattern.IndexVarName;

            // ===== 2. SIMD 前奏：广播不变量 =====
            sb.AppendLine($"    // --- SIMD 向量化 (auto, WIDTH={GetSimdWidthDefine()}) ---");
            foreach (var inv in _pattern.Invariants)
            {
                sb.AppendLine($"    n_float v_{Sanitize(inv)}_broadcast = n_set1_ps({inv});");
            }

            // ===== 3. 索引基向量 =====
            sb.AppendLine($"    n_int v_i_base = n_set_epi32(7, 6, 5, 4, 3, 2, 1, 0);");

            // ===== 4. SIMD 规约变量声明 =====
            foreach (var red in _pattern.Reductions)
            {
                if (red.Kind == ReductionKind.Sum)
                {
                    sb.AppendLine($"    n_float v_{red.TargetField} = n_set1_ps(0);");
                }
                else
                {
                    sb.AppendLine($"    n_float v_{red.TargetField} = n_set1_ps({red.TargetField});");
                    if (red.IndexField != null)
                    {
                        sb.AppendLine($"    n_int v_{red.IndexField} = n_set1_epi32({red.IndexField});");
                    }
                }
            }

            // ===== 5. SIMD 主循环 =====
            sb.AppendLine($"    int simd_end = __startIndex + ((__count) / {GetSimdWidthDefine()}) * {GetSimdWidthDefine()};");
            sb.AppendLine($"    int {idx} = __startIndex;");
            sb.AppendLine($"    for (; {idx} < simd_end; {idx} += {GetSimdWidthDefine()})");
            sb.AppendLine("    {");
            sb.AppendLine($"        n_int v_i = n_add_epi32(v_i_base, n_set1_epi32({idx} - __startIndex));");

            // 为每个 reduction 生成 SIMD 操作
            foreach (var red in _pattern.Reductions)
            {
                sb.AppendLine(GenerateSIMDForReduction(red));
            }

            sb.AppendLine("    }");

            // ===== 6. 水平规约 =====
            foreach (var red in _pattern.Reductions)
            {
                sb.AppendLine(GenerateHorizontalReduction(red));
            }

            // ===== 7. 标量余量循环 =====
            sb.AppendLine($"    for (; {idx} < __startIndex + __count; ++{idx})");
            sb.AppendLine("    {");
            // 缩进标量体
            foreach (var line in scalarBody.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None))
            {
                if (line.Length == 0) continue;
                sb.Append("    ").AppendLine(line);
            }
            sb.AppendLine("    }");

            return sb.ToString();
        }

        /// <summary>
        /// 为单个规约操作生成 SIMD 循环体代码
        /// </summary>
        private string GenerateSIMDForReduction(ReductionOp red)
        {
            var s = new StringBuilder();

            switch (red.Kind)
            {
                case ReductionKind.Min:
                case ReductionKind.Max:
                    // gather data → min/max
                    if (red.DataField != null)
                    {
                        string op = red.Kind == ReductionKind.Min ? "n_min_ps" : "n_max_ps";
                        AppendGather(s, red.DataField);
                        s.AppendLine($"    v_{red.TargetField} = {op}(v_{red.TargetField}, v_data_{Sanitize(red.DataField)}_ps);");
                    }
                    break;

                case ReductionKind.MinIdx:
                case ReductionKind.MaxIdx:
                    // gather data → compare → min + blend index
                    if (red.DataField != null)
                    {
                        string cmpOp = red.Kind == ReductionKind.MinIdx ? "n_cmp_lt_ps" : "n_cmp_gt_ps";
                        AppendGather(s, red.DataField);
                        s.AppendLine($"    n_mask v_mask_{Sanitize(red.TargetField)} = {cmpOp}(v_data_{Sanitize(red.DataField)}_ps, v_{red.TargetField});");
                        s.AppendLine($"    v_{red.TargetField} = n_min_ps(v_{red.TargetField}, v_data_{Sanitize(red.DataField)}_ps);");
                        if (red.IndexField != null)
                        {
                            s.AppendLine($"    v_{red.IndexField} = n_blend_epi32(v_{red.IndexField}, v_i, v_mask_{Sanitize(red.TargetField)});");
                        }
                    }
                    break;

                case ReductionKind.Sum:
                    if (red.DataField != null)
                    {
                        AppendGather(s, red.DataField);
                        s.AppendLine($"    v_{red.TargetField} = n_add_ps(v_{red.TargetField}, v_data_{Sanitize(red.DataField)}_ps);");
                    }
                    break;

                case ReductionKind.CondAssign:
                    // conditional assignment with blend
                    // For phase 1, fallback to simple blend pattern
                    if (red.DataField != null)
                    {
                        AppendGather(s, red.DataField);
                        // Use n_blend_ps with a default condition (true)
                        s.AppendLine($"    v_{red.TargetField} = n_blend_ps(v_{red.TargetField}, v_data_{Sanitize(red.DataField)}_ps, n_mask);");
                    }
                    break;
            }

            return s.ToString();
        }

        /// <summary>
        /// 生成 gather 代码
        /// </summary>
        private void AppendGather(StringBuilder s, string dataField)
        {
            string safe = Sanitize(dataField);
            // 检查是否是 float2 类型（AoS，需要 stride gather）
            // 默认使用 float 数组 gather（stride=4）
            s.AppendLine($"    n_float v_data_{safe}_ps = n_gather_ps((const float*){dataField}_ptr, v_i, sizeof({GetFieldCppType(dataField)}));");
        }

        /// <summary>
        /// 生成水平规约代码
        /// </summary>
        private string GenerateHorizontalReduction(ReductionOp red)
        {
            var s = new StringBuilder();

            switch (red.Kind)
            {
                case ReductionKind.Min:
                case ReductionKind.Max:
                    s.AppendLine($"    {red.TargetField} = n_hmin_ps(v_{red.TargetField});");
                    break;

                case ReductionKind.MinIdx:
                case ReductionKind.MaxIdx:
                    s.AppendLine($"    if (v_i_base_updated) {{}} // temporary");
                    s.AppendLine($"    {red.TargetField} = n_hmin_ps(v_{red.TargetField});");
                    if (red.IndexField != null)
                    {
                        s.AppendLine($"    {red.IndexField} = n_hmin_idx(v_{red.TargetField}, v_{red.IndexField});");
                    }
                    break;

                case ReductionKind.Sum:
                    s.AppendLine($"    {red.TargetField} += n_hsum_ps(v_{red.TargetField});");
                    break;

                case ReductionKind.CondAssign:
                    // For conditional assigns, horizontal reduction is simpler
                    break;
            }

            return s.ToString();
        }

        /// <summary>
        /// 获取 SIMD 宽度定义的宏名
        /// </summary>
        private static string GetSimdWidthDefine()
        {
            return "NSIMD_WIDTH";
        }

        /// <summary>
        /// 将字段名转为安全的标识符
        /// </summary>
        private static string Sanitize(string name)
        {
            return name.Replace(".", "_").Replace("->", "_").Replace("()", "");
        }

        /// <summary>
        /// 获取字段的 C++ 类型（简化版，实际逻辑可扩展）
        /// </summary>
        private static string GetFieldCppType(string fieldName)
        {
            // 默认 float 大小
            return "float";
        }
    }
}
