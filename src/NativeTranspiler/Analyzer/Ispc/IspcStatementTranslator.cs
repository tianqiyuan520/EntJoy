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
        private string CaptureExpressionText(ExpressionSyntax expr)
        {
            int savedLen = _builder.Length;
            TranslateExpression(expr);
            string text = _builder.ToString(savedLen, _builder.Length - savedLen);
            _builder.Length = savedLen;
            return text;
        }
    }
}
