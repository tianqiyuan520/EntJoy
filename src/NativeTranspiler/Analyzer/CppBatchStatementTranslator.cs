using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NativeTranspiler.Analyzer
{
    public class CppBatchStatementTranslator : CppPointerStatementTranslator
    {
        private readonly string _originalIndexName;
        private readonly string _newIndexName;

        public CppBatchStatementTranslator(SemanticModel semanticModel, INamedTypeSymbol jobStruct,
            string originalIndexName, string newIndexName)
            : base(semanticModel, jobStruct)
        {
            _originalIndexName = originalIndexName;
            _newIndexName = newIndexName;
        }

        protected override void TranslateIdentifier(IdentifierNameSyntax identifier)
        {
            string name = identifier.Identifier.Text;
            if (name == _originalIndexName)
            {
                _builder.Append(_newIndexName);
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