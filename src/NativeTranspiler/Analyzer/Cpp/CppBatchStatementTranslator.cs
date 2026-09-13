using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NativeTranspiler.Analyzer
{
    public class CppBatchStatementTranslator : CppPointerStatementTranslator
    {
        private readonly string _originalIndexName;
        private readonly string _newIndexName;
        private readonly string _originalCountName;
        private readonly string _newCountName;

        public CppBatchStatementTranslator(SemanticModel semanticModel, INamedTypeSymbol jobStruct,
            string originalIndexName, string newIndexName, bool useFastMath = false, bool enableAutoSIMD = false,
            string originalCountName = null, string newCountName = null)
            : base(semanticModel, jobStruct, useFastMath, enableAutoSIMD)
        {
            _originalIndexName = originalIndexName;
            _newIndexName = newIndexName;
            // IJobParallelForBatch 的 Execute(int startIndex, int count)：第二个形参同样要映射到 C++
            // 侧批函数形参名 `__count`（否则体内引用 `count` 会生成未声明的标识符）。
            _originalCountName = originalCountName;
            _newCountName = newCountName;
        }

        protected override void TranslateIdentifier(IdentifierNameSyntax identifier)
        {
            string name = identifier.Identifier.Text;
            if (name == _originalIndexName)
            {
                _builder.Append(_newIndexName);
                return;
            }
            if (_originalCountName != null && name == _originalCountName)
            {
                _builder.Append(_newCountName);
                return;
            }
            // 委托给基类处理常量内联、指针字段和值字段
            base.TranslateIdentifier(identifier);
        }

        protected override void TranslateAssignment(AssignmentExpressionSyntax assignment)
        {
            // 批处理中赋值语句的处理与基类完全相同，直接调用基类
            base.TranslateAssignment(assignment);
        }
    }
}
