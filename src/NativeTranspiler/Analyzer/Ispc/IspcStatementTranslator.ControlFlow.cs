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
    public partial class IspcStatementTranslator
    {

        // Keep branch form in ISPC for better static-path stability on this workload.
        protected override bool EnableBranchlessSimpleIfRewrite() => false;

        protected override void TranslateIfStatement(IfStatementSyntax ifStmt)
        {
            var (innerCondition, hintKind) = ExtractHintFromCondition(ifStmt.Condition);

            if (hintKind != HintKind.None)
            {
                // ISPC does not support __builtin_expect or [[likely]].
                // Silently strip the Hint wrapper and emit a normal if-statement.
                AppendIndent();
                _builder.Append("if (");
                TranslateExpression(innerCondition);
                _builder.AppendLine(")");

                // Translate true-branch body
                if (ifStmt.Statement is BlockSyntax block)
                    TranslateBlock(block, skipOuterBraces: false);
                else
                {
                    _indentLevel++;
                    AppendIndent();
                    TranslateStatement(ifStmt.Statement);
                    _indentLevel--;
                }

                // Translate else-branch if present
                if (ifStmt.Else != null)
                {
                    AppendIndent();
                    _builder.AppendLine("else");
                    if (ifStmt.Else.Statement is BlockSyntax elseBlock)
                        TranslateBlock(elseBlock, skipOuterBraces: false);
                    else
                    {
                        _indentLevel++;
                        AppendIndent();
                        TranslateStatement(ifStmt.Else.Statement);
                        _indentLevel--;
                    }
                }
                return;
            }

            // No hint: use base implementation (which also handles branchless rewrite etc.)
            base.TranslateIfStatement(ifStmt);
        }

        protected override void TranslateLocalDeclaration(LocalDeclarationStatementSyntax localDecl)
        {
            if (localDecl.Declaration.Variables.Any(variable => variable.Initializer?.Value is RefExpressionSyntax))
            {
                AppendIndent();
                _builder.AppendLine($"// {UnsupportedMarkers.Stmt}ISPC_RefLocal");
                return;
            }
            AppendIndent();
            var type = _semanticModel.GetTypeInfo(localDecl.Declaration.Type).Type;
            var cppType = NativeTranspiler.MapCSharpTypeToCpp(type!);
            var ispcType = IspcTypeMapper.FromCpp(cppType);
            // ISPC 不支持在逗号声明中引用同列表的兄弟变量（如 float dx=x, dy=y, d=dx*dx; 中 d 看不到 dx/dy）
            // 每个变量独立声明，以分号结束
            for (int i = 0; i < localDecl.Declaration.Variables.Count; i++)
            {
                var variable = localDecl.Declaration.Variables[i];
                if (i > 0) { _builder.AppendLine(); AppendIndent(); }
                bool uniformLocal = CanDeclareUniformLocal(localDecl, variable);
                if (type is IPointerTypeSymbol pointer)
                    _builder.Append(IspcTypeMapper.PointerTo(NativeTranspiler.MapCSharpTypeToCpp(pointer.PointedAtType), _useUniformVars));
                else
                {
                    if (_useUniformVars || uniformLocal) _builder.Append("uniform ");
                    _builder.Append(ispcType);
                }
                _builder.Append(' ').Append(variable.Identifier.Text);
                if (variable.Initializer != null)
                {
                    _builder.Append(" = ");
                    TranslateExpression(variable.Initializer.Value);
                }
                _builder.Append(';');
                if (uniformLocal && _semanticModel.GetDeclaredSymbol(variable) is ILocalSymbol local)
                    _uniformLocals.Add(local);
            }
            _builder.AppendLine();
        }

        protected override void TranslateReturnStatement(ReturnStatementSyntax returnStmt)
        {
            // ISPC 不允许在 foreach 体内 return（见 G645884FA）。void return 语义等价于跳过当前 lane → continue。
            if (_insideForeach)
            {
                if (_insideBatchIndexLoop && HasEnclosingLoop(returnStmt))
                {
                    AppendIndent();
                    _builder.AppendLine($"// {UnsupportedMarkers.Stmt}ISPC_ReturnInsideNestedLoopInBatch");
                    return;
                }
                if (returnStmt.Expression != null)
                {
                    // 带值 return 在 foreach 中无等价表达，降级为 base（会让 ISPC 报错，暴露语义问题而非静默错译）
                    base.TranslateReturnStatement(returnStmt);
                    return;
                }
                AppendIndent();
                _builder.AppendLine("continue;");
                return;
            }

            // 批索引循环（`for (uniform int index = __startIndex; ...)`）里裸写 `return;` 会退出整个导出函数，
            // 静默丢掉剩余 index（C++ 标量路径已用 do-while 包裹修正同样的问题，见 CppJobGenerator）。
            if (_insideBatchIndexLoop)
            {
                if (returnStmt.Expression == null && !HasEnclosingLoop(returnStmt))
                {
                    // index 层级：等价于"结束本次 index" → continue 到下一个 index。
                    AppendIndent();
                    _builder.AppendLine("continue;");
                    return;
                }
                // 嵌套循环里的 return 无法用 continue 表达（continue 只会继续内层循环）。
                // 写唯一标记让构建失败，而不是生成"跳过整批"的错误代码。
                AppendIndent();
                _builder.AppendLine($"// {UnsupportedMarkers.Stmt}ISPC_ReturnInsideNestedLoopInBatch");
                return;
            }

            base.TranslateReturnStatement(returnStmt);
        }

        /// <summary>return 与 Execute 体之间是否夹着循环语句（用于区分"index 层级 return"与"嵌套循环内 return"）。</summary>
        private static bool HasEnclosingLoop(ReturnStatementSyntax returnStmt)
        {
            foreach (var ancestor in returnStmt.Ancestors())
            {
                if (ancestor is ForStatementSyntax || ancestor is ForEachStatementSyntax
                    || ancestor is WhileStatementSyntax || ancestor is DoStatementSyntax)
                    return true;
                if (ancestor is MethodDeclarationSyntax)
                    return false;
            }
            return false;
        }

        // 普通循环保留 C# 的顺序；并行索引仅由入口或已验证的 Chunk 循环发射。
        protected override void TranslateForStatement(ForStatementSyntax loop) => EmitUniformFor(loop);

        protected void EmitUniformFor(ForStatementSyntax forStmt)
        {
            // 检查初始化值和边界是否为 uniform-safe（字面量/字段）。
            // 如果边界表达式引用局部变量（可能 varying），不能强制 uniform int。
            bool uniform = _useUniformVars || IntForLoop.TryAnalyze(forStmt, _semanticModel, out var loop)
                && (_semanticModel.GetConstantValue(loop.Start).HasValue || IsUniformExpr(loop.Start))
                && (_semanticModel.GetConstantValue(loop.Bound).HasValue || IsUniformExpr(loop.Bound));

            AppendIndent();
            _builder.Append("for (");
            if (forStmt.Declaration != null)
            {
                var type = _semanticModel.GetTypeInfo(forStmt.Declaration.Type).Type;
                if (type is IPointerTypeSymbol pointer)
                    _builder.Append(IspcTypeMapper.PointerTo(NativeTranspiler.MapCSharpTypeToCpp(pointer.PointedAtType), uniform));
                else
                {
                    if (uniform) _builder.Append("uniform ");
                    _builder.Append(IspcTypeMapper.FromCpp(NativeTranspiler.MapCSharpTypeToCpp(type!)));
                }
                _builder.Append(' ');
                for (int i = 0; i < forStmt.Declaration.Variables.Count; i++)
                {
                    if (i > 0) _builder.Append(", ");
                    var variable = forStmt.Declaration.Variables[i];
                    _builder.Append(variable.Identifier.Text);
                    if (variable.Initializer != null)
                    {
                        _builder.Append(" = ");
                        TranslateExpression(variable.Initializer.Value);
                    }
                }
            }
            else
            {
                for (int i = 0; i < forStmt.Initializers.Count; i++)
                {
                    if (i > 0) _builder.Append(", ");
                    TranslateExpression(forStmt.Initializers[i]);
                }
            }
            _builder.Append("; ");
            if (forStmt.Condition != null) TranslateExpression(forStmt.Condition);
            _builder.Append("; ");
            for (int i = 0; i < forStmt.Incrementors.Count; i++)
            {
                if (i > 0) _builder.Append(", ");
                TranslateExpression(forStmt.Incrementors[i]);
            }
            _builder.AppendLine(")");
            if (forStmt.Statement is BlockSyntax block)
                TranslateBlock(block, skipOuterBraces: false);
            else
            {
                AppendIndent();
                _builder.AppendLine("{");
                _indentLevel++;
                TranslateStatement(forStmt.Statement);
                _indentLevel--;
                AppendIndent();
                _builder.AppendLine("}");
            }
        }


        protected override void TranslateExpressionStatement(ExpressionStatementSyntax exprStmt)
        {
            // SendEvent 拦截（ISPC EventBuffer 写入）
            if (_eventBufferParamName != null &&
                exprStmt.Expression is InvocationExpressionSyntax invocation &&
                TryTranslateSendEvent(invocation))
            {
                return;
            }

            // uniform 模式 + ref/out 形参的用户 helper 调用：需要先物化槽（语句序列），单独拦截。
            if (exprStmt.Expression is InvocationExpressionSyntax uniformHelperCall
                && TryTranslateUniformHelperCallWithRefOut(uniformHelperCall))
            {
                return;
            }


            base.TranslateExpressionStatement(exprStmt);
        }
        protected override void TranslateWhileStatement(WhileStatementSyntax whileStmt)
        {
            if (_insideForeach)
            {
                AppendIndent();
                _builder.Append("while (");
                TranslateExpression(whileStmt.Condition);
                _builder.AppendLine(")");
                if (whileStmt.Statement is BlockSyntax block)
                    TranslateBlock(block, skipOuterBraces: false);
                else
                {
                    _indentLevel++; AppendIndent();
                    TranslateStatement(whileStmt.Statement); _indentLevel--;
                }
            }
            else
            {
                base.TranslateWhileStatement(whileStmt);
            }
        }

    }
}
