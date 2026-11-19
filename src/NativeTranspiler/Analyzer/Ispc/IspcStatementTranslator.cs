using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using System;
using System.Collections.Generic;
using System.Linq;
using NativeTranspiler.Analyzer.Common;

namespace NativeTranspiler.Analyzer
{
    public partial class IspcStatementTranslator : CppPointerStatementTranslator
    {
        protected override bool EmitLoopUnrollHints => false;
        // 整数回绕由 ISPC 的 --wrap-signed-int 保证，表达式使用 ISPC 原生形式。
        protected override bool EnableWrapSafeIntArithmetic => false;

        private readonly Dictionary<string, bool> _constBoolFields = new();
        protected bool _useUniformVars;

        protected readonly HashSet<string> _entityRefParamNames = new();

        /// <summary>是否在 foreach 体内部（内层循环变量需加 uniform 修饰）</summary>
        protected bool _insideForeach;

        /// <summary>设置 foreach 上下文标志（由外部生成器在发射 foreach 后调用）</summary>
        public void SetInsideForeach(bool value) => _insideForeach = value;

        /// <summary>
        /// 是否在 ISPC 的批索引循环（`for (uniform int index = __startIndex; ...)` 或 foreach）体内。
        /// 只由外部生成器在发射批索引循环时置位 —— 与"嵌套循环"区分开，用于 `return;` 的正确降级。
        /// </summary>
        protected bool _insideBatchIndexLoop;

        /// <summary>设置批索引循环上下文标志（由外部生成器在发射批索引循环后调用）</summary>
        public void SetInsideBatchIndexLoop(bool value) => _insideBatchIndexLoop = value;

        /// <summary>批索引循环的相对迭代变量名（零基 foreach 的 induction variable）。</summary>
        public const string BatchRelativeVar = "__batchRel";

        private string _batchStartExpr;
        private IParameterSymbol _batchIndexParam;

        /// <summary>
        /// 进入批内相对寻址模式：<paramref name="startExpr"/> 是批起点（uniform），
        /// <paramref name="indexParam"/> 是批索引形参。按符号而非名字识别批索引：
        /// 体内同名局部（`for (int index = …)`）不能当成批索引。
        /// </summary>
        public void SetBatchRelativeIndex(string startExpr, IParameterSymbol indexParam)
        {
            _batchStartExpr = startExpr;
            _batchIndexParam = indexParam;
        }

        private bool IsBatchIndex(IdentifierNameSyntax identifier)
            => _batchIndexParam != null
                && SymbolEqualityComparer.Default.Equals(
                    _semanticModel.GetSymbolInfo(identifier).Symbol, _batchIndexParam);

        private System.Func<Microsoft.CodeAnalysis.CSharp.Syntax.LiteralExpressionSyntax, string> _flatLiteralFormatter;
        private bool _formattingFlatLiteral;

        /// <summary>
        /// 摊平时按槽替换数值常量：传入的委托对每个常量返回要发射的文本（null 表示照常发射）。
        /// 只在摊平判据已经证明语句同构后使用，替换按语法节点定位，不扫描已生成的文本。
        /// </summary>
        public void SetFlatLiteralFormatter(
            System.Func<Microsoft.CodeAnalysis.CSharp.Syntax.LiteralExpressionSyntax, string> formatter)
            => _flatLiteralFormatter = formatter;

        /// <summary>
        /// 委托里翻译各槽常量时会再次进入字面量翻译，这里挡住重入，否则无限递归。
        /// </summary>
        protected string FormatFlatLiteral(Microsoft.CodeAnalysis.CSharp.Syntax.LiteralExpressionSyntax literal)
        {
            if (_flatLiteralFormatter == null || _formattingFlatLiteral) return null;
            _formattingFlatLiteral = true;
            try { return _flatLiteralFormatter(literal); }
            finally { _formattingFlatLiteral = false; }
        }

        public IspcStatementTranslator(SemanticModel semanticModel, INamedTypeSymbol jobStruct,
            string? constBoolFieldName, bool constBoolValue, bool useUniformVars = false)
            : base(semanticModel, jobStruct)
        {
            if (constBoolFieldName != null)
                _constBoolFields[constBoolFieldName] = constBoolValue;
            _useUniformVars = useUniformVars;
        }

        public IspcStatementTranslator(SemanticModel semanticModel, IMethodSymbol method,
            string? constBoolFieldName, bool constBoolValue, bool useUniformVars = false)
            : base(semanticModel, method)
        {
            if (constBoolFieldName != null)
                _constBoolFields[constBoolFieldName] = constBoolValue;
            _useUniformVars = useUniformVars;
        }

        /// <summary>
        /// 支持多个 bool 条件字段的构造函数
        /// </summary>
        public IspcStatementTranslator(SemanticModel semanticModel, INamedTypeSymbol jobStruct,
            List<IFieldSymbol>? constBoolFields, List<bool>? constBoolValues, bool useUniformVars = false)
            : base(semanticModel, jobStruct)
        {
            if (constBoolFields != null && constBoolValues != null)
            {
                for (int i = 0; i < constBoolFields.Count; i++)
                    _constBoolFields[constBoolFields[i].Name] = constBoolValues[i];
            }
            _useUniformVars = useUniformVars;
        }

        public IspcStatementTranslator(SemanticModel semanticModel, IMethodSymbol method,
            List<IFieldSymbol>? constBoolFields, List<bool>? constBoolValues, bool useUniformVars = false)
            : base(semanticModel, method)
        {
            if (constBoolFields != null && constBoolValues != null)
            {
                for (int i = 0; i < constBoolFields.Count; i++)
                    _constBoolFields[constBoolFields[i].Name] = constBoolValues[i];
            }
            _useUniformVars = useUniformVars;
        }

        /// <summary>将表达式翻译为 ISPC 文本并返回，不影响 _builder 的当前状态。</summary>
        protected string CaptureExpressionText(ExpressionSyntax expr)
        {
            int savedLen = _builder.Length;
            TranslateExpression(expr);
            string text = _builder.ToString(savedLen, _builder.Length - savedLen);
            _builder.Length = savedLen;
            return text;
        }
    }
}
