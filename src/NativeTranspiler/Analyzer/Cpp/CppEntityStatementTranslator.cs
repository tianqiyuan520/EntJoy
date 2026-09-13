using Microsoft.CodeAnalysis;

namespace NativeTranspiler.Analyzer
{
    /// <summary>
    /// IJobEntity（原生）Execute 体内的翻译器（P2-10）。
    ///
    /// 与 IJob/IJobParallelFor 一样，把 **job 字段**里的容器映射到生成函数的形参：
    ///   `Out[i]` → `Out_ptr[i]`，`Out.Length` → `Out_length`，`Out.GetUnsafePtr()` → `Out_ptr`，
    /// 指针字段 → `name_ptr`。
    ///
    /// 与 <see cref="CppPointerStatementTranslator"/> 的唯一差别：**不启用 wrap-safe int 算术**
    /// （IJobEntity 既有生成代码是裸算术，保持原形态以免无谓回归；IJobChunk 路径本来就带 wrap-safe）。
    /// </summary>
    public sealed class CppEntityStatementTranslator : CppPointerStatementTranslator
    {
        public CppEntityStatementTranslator(SemanticModel semanticModel, INamedTypeSymbol jobStruct,
            bool useFastMath = false, bool enableAutoSIMD = false)
            : base(semanticModel, jobStruct, useFastMath, enableAutoSIMD)
        {
        }

        protected override bool EnableWrapSafeIntArithmetic => false;
    }
}
