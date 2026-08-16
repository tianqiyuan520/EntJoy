// ============================================================
// WgslStatementTranslator.cs — 把 GPU Job 的 Execute body 翻译为 WGSL 语句。
// 基于 CppPointerStatementTranslator，覆盖 WGSL 与 C++ 的差异点：
//   类型映射、标量字段→uniform 成员、数学调用、字面量、类型提升、三元→select。
// 支持形态：IJobParallelFor / IJobFor（索引=global_invocation_id.x）、IJob。
// ============================================================
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NativeTranspiler.Analyzer
{
    public class WgslStatementTranslator : CppPointerStatementTranslator
    {
        private const string ParamsPrefix = "jobParams";
        private readonly string _indexParamName;

        public WgslStatementTranslator(SemanticModel semanticModel, INamedTypeSymbol jobStruct, string indexParamName)
            : base(semanticModel, jobStruct)
        {
            _indexParamName = indexParamName;
        }

        /// <summary>入口函数体内的语句缩进一级（由 WgslGenerator 调用）</summary>
        public void SetEntryIndent() => _indentLevel = 1;

        // ---------- 表达式入口 ----------

        protected override void TranslateExpression(ExpressionSyntax expr)
        {
            switch (expr)
            {
                case LiteralExpressionSyntax lit:
                    if (lit.IsKind(SyntaxKind.DefaultLiteralExpression))
                        TranslateDefaultLiteral(expr);
                    else
                        TranslateLiteral(lit);
                    break;
                case ConditionalExpressionSyntax cond: TranslateConditional(cond); break;
                default: base.TranslateExpression(expr); break;
            }
        }

        private void TranslateDefaultLiteral(ExpressionSyntax expr)
        {
            var t = _semanticModel.GetTypeInfo(expr).Type;
            string w = WgslTypes.ToWgslType(t);
            _builder.Append(w ?? "/*default*/").Append("()");
        }

        private void TranslateLiteral(LiteralExpressionSyntax lit)
        {
            var tok = lit.Token;
            switch (tok.Kind())
            {
                case SyntaxKind.NumericLiteralToken:
                    string text = tok.Text;
                    if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    { _builder.Append(text); return; }
                    if (text.EndsWith("f", StringComparison.OrdinalIgnoreCase) ||
                        text.EndsWith("d", StringComparison.OrdinalIgnoreCase) ||
                        text.EndsWith("m", StringComparison.OrdinalIgnoreCase))
                    { _builder.Append(WgslTypes.ToWgslFloatLiteral(text)); return; }
                    _builder.Append(text); return;
                case SyntaxKind.TrueLiteralExpression: _builder.Append("true"); return;
                case SyntaxKind.FalseLiteralExpression: _builder.Append("false"); return;
                default: _builder.Append(tok.Text); return;
            }
        }

        // ---------- 标识符 ----------

        protected override void TranslateIdentifier(IdentifierNameSyntax id)
        {
            if (TryInlineConstant(id)) return;
            string name = id.Identifier.Text;
            if (_valueParameterNames.Contains(name))
            { _builder.Append(ParamsPrefix).Append('.').Append(name); return; }
            if (_nativeArrayListNames.Contains(name) || _nativeListNames.Contains(name))
            { _builder.Append(name); return; }
            if (name == _indexParamName) { _builder.Append('i'); return; }
            base.TranslateIdentifier(id);
        }

        // ---------- 赋值 ----------

        protected override void TranslateAssignment(AssignmentExpressionSyntax a)
        {
            var leftType = _semanticModel.GetTypeInfo(a.Left).Type;
            if (a.Left is IdentifierNameSyntax id && _valueParameterNames.Contains(id.Identifier.Text))
                _builder.Append(ParamsPrefix).Append('.').Append(id.Identifier.Text);
            else
                TranslateExpression(a.Left);
            _builder.Append(' ').Append(a.OperatorToken.Text).Append(' ');
            TranslateWithConversion(a.Right, leftType);
        }

        /// <summary>C# 隐式数值提升：int/uint→float、vec2i/vec2u→vec2f</summary>
        private void TranslateWithConversion(ExpressionSyntax expr, ITypeSymbol? targetType)
        {
            var et = _semanticModel.GetTypeInfo(expr).Type;
            if (targetType == null || et == null || SymbolEqualityComparer.Default.Equals(targetType, et))
            { TranslateExpression(expr); return; }
            string tw = WgslTypes.ToWgslType(targetType), ew = WgslTypes.ToWgslType(et);
            if (tw == null || ew == null) { TranslateExpression(expr); return; }
            if (WgslTypes.IsFloatScalar(targetType) && WgslTypes.IsIntScalar(et))
            { _builder.Append(tw).Append('('); TranslateExpression(expr); _builder.Append(')'); return; }
            if (WgslTypes.IsMathVectorType(targetType) && WgslTypes.IsMathVectorType(et) && tw == "vec2f" && (ew == "vec2i" || ew == "vec2u"))
            { _builder.Append(tw).Append('('); TranslateExpression(expr); _builder.Append(')'); return; }
            TranslateExpression(expr);
        }

        // ---------- 成员访问 ----------

        protected override void TranslateMemberAccess(MemberAccessExpressionSyntax ma)
        {
            var et = _semanticModel.GetTypeInfo(ma.Expression).Type;
            string m = ma.Name.Identifier.Text;

            if (WgslTypes.IsMathVectorType(et))
            {
                if (m == "zero") { _builder.Append(WgslTypes.ToWgslType(et) ?? "vec2f").Append("()"); return; }
                if (m == "x" || m == "y" || m == "z" || m == "w")
                { TranslateExpression(ma.Expression); _builder.Append('.').Append(m); return; }
            }

            bool isNA = et != null && NativeTranspiler.IsEntJoyNativeContainerType(et) && et.Name == "NativeArray";
            if (isNA && m == "Length")
            {
                if (ma.Expression is IdentifierNameSyntax id && _nativeArrayListNames.Contains(id.Identifier.Text))
                { _builder.Append("arrayLength(&").Append(id.Identifier.Text).Append(')'); return; }
            }

            string? cv = TranslateConstantMember(et, m);
            if (cv != null) { _builder.Append(cv); return; }

            base.TranslateMemberAccess(ma);
        }

        private static string? TranslateConstantMember(ITypeSymbol? et, string m)
        {
            string? tn = et?.ToDisplayString();
            if (tn == "float" || tn == "System.Single" || tn == "double" || tn == "System.Double")
            {
                return m switch
                {
                    "MaxValue" => "3.402823466e+38",
                    "MinValue" => "-3.402823466e+38",
                    "Epsilon" => "1.175494351e-38",
                    "PositiveInfinity" => "3.402823466e+38",
                    "NegativeInfinity" => "-3.402823466e+38",
                    "NaN" => "(0.0 / 0.0)",
                    "PI" => "3.141592653589793",
                    "E" => "2.718281828459045",
                    "Tau" => "6.283185307179586",
                    _ => null
                };
            }
            if (tn == "int" || tn == "System.Int32")
            {
                return m switch
                {
                    "MaxValue" => "2147483647",
                    "MinValue" => "-2147483648",
                    _ => null
                };
            }
            return null;
        }

        // ---------- 元素访问（NativeArray[i] → 直接 buffer 名，无 _ptr 后缀） ----------

        protected override void TranslateElementAccess(ElementAccessExpressionSyntax ea)
        {
            var et = _semanticModel.GetTypeInfo(ea.Expression).Type;
            if (et != null && NativeTranspiler.IsEntJoyNativeContainerType(et) && et.Name == "NativeArray")
            {
                TranslateExpression(ea.Expression);
                _builder.Append('[');
                if (ea.ArgumentList.Arguments.Count > 0)
                    TranslateExpression(ea.ArgumentList.Arguments[0].Expression);
                _builder.Append(']');
                return;
            }
            base.TranslateElementAccess(ea);
        }

        // ---------- 方法调用 ----------

        protected override void TranslateInvocation(InvocationExpressionSyntax inv)
        {
            if (_semanticModel.GetSymbolInfo(inv).Symbol is IMethodSymbol ms)
            {
                var ct = ms.ContainingType;

                if (ct?.Name == "NativeArray" && NativeTranspiler.IsEntJoyNativeContainerType(ct) && ms.Name == "GetUnsafePtr")
                {
                    var tgt = (inv.Expression as MemberAccessExpressionSyntax)?.Expression;
                    if (tgt is IdentifierNameSyntax id && _nativeArrayListNames.Contains(id.Identifier.Text))
                        _builder.Append(id.Identifier.Text);
                    else
                        TranslateExpression(tgt);
                    return;
                }

                if (ct?.ToDisplayString() == "EntJoy.Collections.UnsafeUtility")
                {
                    var args = inv.ArgumentList.Arguments;
                    if (ms.Name == "ReadArrayElement" && args.Count >= 2)
                    { TranslateExpression(args[0].Expression); _builder.Append('['); TranslateExpression(args[1].Expression); _builder.Append(']'); return; }
                    if (ms.Name == "WriteArrayElement" && args.Count >= 3)
                    { TranslateExpression(args[0].Expression); _builder.Append('['); TranslateExpression(args[1].Expression); _builder.Append("] = "); TranslateExpression(args[2].Expression); return; }
                    if (ms.Name == "ArrayElementAsRef" && args.Count >= 2)
                    { TranslateExpression(args[0].Expression); _builder.Append('['); TranslateExpression(args[1].Expression); _builder.Append(']'); return; }
                }

                if (ms.IsStatic)
                {
                    string? ft = ct?.ToDisplayString();
                    if (ft == "System.Math" || ft == "System.MathF") { TranslateMathCall(ms.Name, inv); return; }
                    if (ft == "EntJoy.Mathematics.math") { TranslateEntJoyMathCall(ms.Name, inv); return; }
                    if (ft == "EntJoy.Hint")
                    { if (inv.ArgumentList.Arguments.Count > 0) TranslateExpression(inv.ArgumentList.Arguments[0].Expression); else _builder.Append("true"); return; }
                }
            }
            _builder.Append("/* unsupported call: ").Append(inv.ToString().Replace("*/", "* /")).Append(" */");
        }

        private void TranslateMathCall(string name, InvocationExpressionSyntax inv)
        {
            string? w = name switch
            {
                "Sin" => "sin", "Cos" => "cos", "Sqrt" => "sqrt", "Abs" => "abs",
                "Min" => "min", "Max" => "max", "Floor" => "floor", "Ceiling" => "ceil",
                "Ceil" => "ceil", "Pow" => "pow", "Exp" => "exp", "Log" => "log",
                "Atan2" => "atan2", "Round" => "round", "Sign" => "sign", "Clamp" => "clamp",
                _ => null
            };
            if (w == null) { _builder.Append("/* unsupported MathF.").Append(name).Append(" */"); return; }
            _builder.Append(w).Append('(');
            for (int i = 0; i < inv.ArgumentList.Arguments.Count; i++)
            { if (i > 0) _builder.Append(", "); TranslateExpression(inv.ArgumentList.Arguments[i].Expression); }
            _builder.Append(')');
        }

        private void TranslateEntJoyMathCall(string name, InvocationExpressionSyntax inv)
        {
            var args = inv.ArgumentList.Arguments;
            switch (name)
            {
                case "lengthsq":
                    _builder.Append("dot("); TranslateExpression(args[0].Expression);
                    _builder.Append(", "); TranslateExpression(args[0].Expression); _builder.Append(')');
                    return;
                case "distancesq":
                    _builder.Append("dot(("); TranslateExpression(args[0].Expression);
                    _builder.Append(" - "); TranslateExpression(args[1].Expression);
                    _builder.Append("), ("); TranslateExpression(args[0].Expression);
                    _builder.Append(" - "); TranslateExpression(args[1].Expression); _builder.Append("))");
                    return;
                case "lerp":
                    _builder.Append("mix(");
                    for (int i = 0; i < args.Count; i++) { if (i > 0) _builder.Append(", "); TranslateExpression(args[i].Expression); }
                    _builder.Append(')');
                    return;
                case "dot": case "length": case "normalize": case "abs": case "min": case "max":
                case "clamp": case "floor": case "ceil": case "sin": case "cos": case "sqrt":
                case "pow": case "exp": case "log": case "atan2": case "round": case "sign":
                    _builder.Append(name).Append('(');
                    for (int i = 0; i < args.Count; i++) { if (i > 0) _builder.Append(", "); TranslateExpression(args[i].Expression); }
                    _builder.Append(')');
                    return;
                default:
                    _builder.Append("/* unsupported math.").Append(name).Append(" */");
                    return;
            }
        }

        // ---------- 二元表达式（类型导向提升） ----------

        protected override void TranslateBinaryExpression(BinaryExpressionSyntax b)
        {
            string op = b.OperatorToken.Text;
            if (op == "&&" || op == "||")
            { TranslateExpression(b.Left); _builder.Append(' ').Append(op).Append(' '); TranslateExpression(b.Right); return; }

            var lt = _semanticModel.GetTypeInfo(b.Left).Type;
            var rt = _semanticModel.GetTypeInfo(b.Right).Type;
            bool lv = WgslTypes.IsMathVectorType(lt), rv = WgslTypes.IsMathVectorType(rt);

            bool cmp = op is "==" or "!=" or "<" or "<=" or ">" or ">=";
            if (cmp && (lv || rv)) { _builder.Append("all("); TranslateBinaryOperands(b, lt, rt, lv, rv); _builder.Append(')'); return; }

            TranslateBinaryOperands(b, lt, rt, lv, rv);
        }

        private void TranslateBinaryOperands(BinaryExpressionSyntax b, ITypeSymbol? lt, ITypeSymbol? rt, bool lv, bool rv)
        {
            string op = b.OperatorToken.Text;

            if (lv && !rv)
            {
                string wv = WgslTypes.ToWgslType(lt);
                TranslateExpression(b.Left);
                _builder.Append(' ').Append(op).Append(' ').Append(wv).Append('(');
                string sw = WgslTypes.ToWgslScalarOfVector(wv);
                if (WgslTypes.ScalarKindOf(rt) != sw)
                { _builder.Append(sw).Append('('); TranslateExpression(b.Right); _builder.Append(')'); }
                else
                    TranslateExpression(b.Right);
                _builder.Append(')');
                return;
            }
            if (!lv && rv)
            {
                string wv = WgslTypes.ToWgslType(rt);
                _builder.Append(wv).Append('(');
                string sw = WgslTypes.ToWgslScalarOfVector(wv);
                if (WgslTypes.ScalarKindOf(lt) != sw)
                { _builder.Append(sw).Append('('); TranslateExpression(b.Left); _builder.Append(')'); }
                else
                    TranslateExpression(b.Left);
                _builder.Append(')').Append(' ').Append(op).Append(' ');
                TranslateExpression(b.Right);
                return;
            }
            if (!lv && !rv && WgslTypes.IsIntScalar(lt) && WgslTypes.IsFloatScalar(rt))
            { _builder.Append("f32("); TranslateExpression(b.Left); _builder.Append(") ").Append(op).Append(' '); TranslateExpression(b.Right); return; }
            if (!lv && !rv && WgslTypes.IsFloatScalar(lt) && WgslTypes.IsIntScalar(rt))
            { TranslateExpression(b.Left); _builder.Append(' ').Append(op).Append(" f32("); TranslateExpression(b.Right); _builder.Append(')'); return; }

            TranslateExpression(b.Left);
            _builder.Append(' ').Append(op).Append(' ');
            TranslateExpression(b.Right);
        }

        // ---------- 三元 → select ----------

        protected override void TranslateConditional(ConditionalExpressionSyntax c)
        {
            _builder.Append("select(");
            TranslateExpression(c.WhenFalse);
            _builder.Append(", ");
            TranslateExpression(c.WhenTrue);
            _builder.Append(", ");
            TranslateExpression(c.Condition);
            _builder.Append(')');
        }

        // ---------- 局部声明 ----------

        protected override void TranslateLocalDeclaration(LocalDeclarationStatementSyntax ld)
        {
            var t = _semanticModel.GetTypeInfo(ld.Declaration.Type).Type;
            string w = WgslTypes.ToWgslType(t);
            if (w == null) { AppendIndent(); _builder.AppendLine($"/* unsupported local type: {t?.ToDisplayString()} */"); return; }
            foreach (var v in ld.Declaration.Variables)
            {
                AppendIndent();
                _builder.Append("var ").Append(v.Identifier.Text).Append(" : ").Append(w);
                if (v.Initializer != null)
                { _builder.Append(" = "); TranslateWithConversion(v.Initializer.Value, t); }
                _builder.AppendLine(";");
            }
        }

        // ---------- for 循环 ----------

        protected override void TranslateForStatement(ForStatementSyntax f)
        {
            AppendIndent();
            _builder.Append("for (");
            if (f.Declaration != null)
            {
                var t = _semanticModel.GetTypeInfo(f.Declaration.Type).Type;
                string w = WgslTypes.ToWgslType(t) ?? "i32";
                var vars = f.Declaration.Variables;
                for (int i = 0; i < vars.Count; i++)
                {
                    if (i > 0) { _builder.Append("/* multi-declarator */ "); break; }
                    var v = vars[i];
                    _builder.Append("var ").Append(v.Identifier.Text).Append(" : ").Append(w);
                    if (v.Initializer != null) { _builder.Append(" = "); TranslateExpression(v.Initializer.Value); }
                }
            }
            else if (f.Initializers.Count > 0)
            {
                for (int i = 0; i < f.Initializers.Count; i++)
                { if (i > 0) _builder.Append(", "); TranslateExpression(f.Initializers[i]); }
            }
            _builder.Append("; ");
            if (f.Condition != null) TranslateExpression(f.Condition);
            _builder.Append("; ");
            for (int i = 0; i < f.Incrementors.Count; i++)
            { if (i > 0) _builder.Append(", "); TranslateExpression(f.Incrementors[i]); }
            _builder.AppendLine(")");

            if (f.Statement is BlockSyntax blk) TranslateBlock(blk, skipOuterBraces: false);
            else if (f.Statement is EmptyStatementSyntax) { _indentLevel++; AppendIndent(); _builder.AppendLine(";"); _indentLevel--; }
            else TranslateBodiedStatement(f.Statement);
        }

        // ---------- if / while：WGSL 要求控制体必须带大括号 ----------

        protected override void TranslateIfStatement(IfStatementSyntax ifStmt)
        {
            AppendIndent();
            _builder.Append("if (");
            TranslateExpression(ifStmt.Condition);
            _builder.AppendLine(")");
            TranslateBodiedStatement(ifStmt.Statement);
            if (ifStmt.Else != null)
            {
                AppendIndent();
                _builder.AppendLine("else");
                TranslateBodiedStatement(ifStmt.Else.Statement);
            }
        }

        protected override void TranslateWhileStatement(WhileStatementSyntax whileStmt)
        {
            AppendIndent();
            _builder.Append("while (");
            TranslateExpression(whileStmt.Condition);
            _builder.AppendLine(")");
            TranslateBodiedStatement(whileStmt.Statement);
        }

        /// <summary>把任意语句体包进大括号（WGSL 语法要求）</summary>
        private void TranslateBodiedStatement(StatementSyntax stmt)
        {
            if (stmt is BlockSyntax blk) { TranslateBlock(blk, skipOuterBraces: false); return; }
            if (stmt is EmptyStatementSyntax) { _indentLevel++; AppendIndent(); _builder.AppendLine(";"); _indentLevel--; return; }
            _indentLevel++;
            AppendIndent();
            _builder.AppendLine("{");
            _indentLevel++;
            TranslateStatement(stmt);
            _indentLevel--;
            AppendIndent();
            _builder.AppendLine("}");
            _indentLevel--;
        }

        // ---------- 转换 / 创建 / do-while / return ----------

        protected override void TranslateCastExpression(CastExpressionSyntax cast)
        {
            var t = _semanticModel.GetTypeInfo(cast.Type).Type;
            string w = WgslTypes.ToWgslType(t);
            if (w == null) { _builder.Append("/* unsupported cast */"); return; }
            _builder.Append(w).Append('(');
            TranslateExpression(cast.Expression);
            _builder.Append(')');
        }

        protected override void TranslateObjectCreation(ObjectCreationExpressionSyntax oc)
        {
            var t = _semanticModel.GetTypeInfo(oc).Type ?? _semanticModel.GetTypeInfo(oc.Type).Type;
            string w = WgslTypes.ToWgslType(t) ?? t?.Name ?? "/*type*/";
            _builder.Append(w).Append('(');
            var args = oc.ArgumentList?.Arguments ?? new SeparatedSyntaxList<ArgumentSyntax>();
            for (int i = 0; i < args.Count; i++)
            { if (i > 0) _builder.Append(", "); TranslateExpression(args[i].Expression); }
            _builder.Append(')');
        }

        protected override void TranslateDoStatement(DoStatementSyntax d)
        {
            AppendIndent();
            _builder.AppendLine("/* do-while 在 WGSL 中不支持 */");
        }

        protected override void TranslateReturnStatement(ReturnStatementSyntax r)
        {
            AppendIndent();
            _builder.Append("return");
            if (r.Expression != null) { _builder.Append(' '); TranslateExpression(r.Expression); }
            _builder.AppendLine(";");
        }
    }
}
