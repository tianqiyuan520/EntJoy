using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using NativeTranspiler.Analyzer.Common;

namespace NativeTranspiler.Analyzer
{
    public static class NativeTranspileValidator
    {
        public static readonly DiagnosticDescriptor InvalidReturnTypeError = new("NT001", "Invalid return type", "[NativeTranspile] method '{0}' return type '{1}' must be unmanaged or void", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor InvalidParameterTypeError = new("NT002", "Invalid parameter type", "[NativeTranspile] method '{0}' parameter '{1}' type '{2}' must be unmanaged", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor InvalidLocalVariableTypeError = new("NT003", "Invalid local variable type", "[NativeTranspile] method '{0}' local variable '{1}' type '{2}' must be unmanaged", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor DisallowedMethodCallError = new("NT004", "Disallowed method call", "[NativeTranspile] method '{0}' cannot call '{1}' because its signature contains non‑unmanaged types or it is not a static method in the same assembly", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor ManagedObjectCreationError = new("NT005", "Managed object creation", "[NativeTranspile] method '{0}' cannot create managed object of type '{1}'", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor ReferenceTypeUsageError = new("NT006", "Reference type usage", "[NativeTranspile] method '{0}' uses reference type '{1}' which is not allowed", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor InvalidJobTypeError = new("NT007", "Invalid Job type", "[NativeTranspile] can only be applied to structs. '{0}' is not a struct.", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor MissingJobInterfaceError = new("NT008", "Missing Job interface", "[NativeTranspile] struct '{0}' must implement IJob, IJobParallelFor, IJobFor, or IJobChunk.", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor InvalidJobFieldError = new("NT009", "Invalid Job field", "[NativeTranspile] struct '{0}' field '{1}' type '{2}' must be unmanaged.", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor MissingExecuteMethodError = new("NT010", "Missing Execute method", "[NativeTranspile] struct '{0}' must contain an Execute method.", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor DisallowedChunkDataAccessError = new("NT012", "Disallowed chunk data access", "[NativeTranspile] IJobChunk method '{0}' cannot call '{1}'. Use ArchetypeChunk.GetComponentDataNativeArray<T>() for native chunk data access.", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor InvalidJobEntityError = new("NT013", "Invalid IJobEntity", "[NativeTranspile] IJobEntity struct '{0}' only supports C++/ISPC backend and Execute(ref/in unmanaged component) parameters.", "NativeTranspiler", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor InvalidGpuJobError = new("NT014", "Invalid GPU Job", "[NativeTranspile] GPU Job '{0}': {1}", "NativeTranspiler", DiagnosticSeverity.Error, true);

        // 预定义的系统 API 白名单
        private static readonly HashSet<string> AllowedStaticMethods = new()
        {
            "System.Math.Abs", "System.MathF.Abs",
            "System.Math.Acos", "System.MathF.Acos",
            "System.Math.Asin", "System.MathF.Asin",
            "System.Math.Atan", "System.MathF.Atan",
            "System.Math.Atan2", "System.MathF.Atan2",
            "System.Math.Ceiling", "System.MathF.Ceiling",
            "System.Math.Cos", "System.MathF.Cos",
            "System.Math.Cosh", "System.MathF.Cosh",
            "System.Math.Exp", "System.MathF.Exp",
            "System.Math.Floor", "System.MathF.Floor",
            "System.Math.Log", "System.MathF.Log",
            "System.Math.Log10", "System.MathF.Log10",
            "System.Math.Max", "System.MathF.Max",
            "System.Math.Min", "System.MathF.Min",
            "System.Math.Pow", "System.MathF.Pow",
            "System.Math.Round", "System.MathF.Round",
            "System.Math.Sin", "System.MathF.Sin",
            "System.Math.Sinh", "System.MathF.Sinh",
            "System.Math.Sqrt", "System.MathF.Sqrt",
            "System.Math.Tan", "System.MathF.Tan",
            "System.Math.Tanh", "System.MathF.Tanh",
            "System.Math.Truncate", "System.MathF.Truncate",
            "System.Threading.Interlocked.Increment",
            "System.Threading.Interlocked.Decrement",
            "System.Threading.Interlocked.Add",
            "System.Threading.Interlocked.Exchange",
            "System.Threading.Interlocked.CompareExchange",
            "System.Threading.Interlocked.Read",
            "EntJoy.Mathematics.math.dot",
            "EntJoy.Mathematics.math.lengthsq",
            "EntJoy.Mathematics.math.length",
            "EntJoy.Mathematics.math.normalize",
            "EntJoy.Mathematics.math.abs",
            "EntJoy.Mathematics.math.min",
            "EntJoy.Mathematics.math.max",
            "EntJoy.Mathematics.math.clamp",
            "EntJoy.Mathematics.math.lerp",
            "EntJoy.Mathematics.math.floor",
            "EntJoy.Mathematics.math.ceil",
            "EntJoy.Mathematics.math.distancesq",
            "EntJoy.Collections.UnsafeUtility.ArrayElementAsRef",
            "EntJoy.Hint.Likely",
            "EntJoy.Hint.Unlikely",
        };

        public static bool ValidateMethod(IMethodSymbol method, Compilation compilation, out List<Diagnostic> diagnostics)
        {
            diagnostics = new List<Diagnostic>();

            var attrSym = compilation.GetTypeByMetadataName("NativeTranspiler.NativeTranspileAttribute");
            if (attrSym != null && AttributeHelper.GetBackendTarget(method, attrSym) == NativeTranspiler.BackendTarget.Gpu)
            {
                diagnostics.Add(Diagnostic.Create(InvalidGpuJobError, method.Locations.FirstOrDefault(),
                    method.Name, "BackendTarget.Gpu 仅支持 Job 结构体（IJobParallelFor / IJobFor / IJob），静态方法请使用 Cpp/Ispc"));
                return false;
            }

            if (!IsUnmanagedTypeOrVoid(method.ReturnType))
                diagnostics.Add(Diagnostic.Create(InvalidReturnTypeError, method.Locations.FirstOrDefault(), method.Name, method.ReturnType.ToDisplayString()));

            foreach (var p in method.Parameters)
                if (!IsUnmanagedType(p.Type))
                    diagnostics.Add(Diagnostic.Create(InvalidParameterTypeError, p.Locations.FirstOrDefault(), method.Name, p.Name, p.Type.ToDisplayString()));

            var methodSyntax = SymbolHelper.GetMethodSyntax(method);
            if (methodSyntax?.Body == null)
                return diagnostics.Count == 0;

            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);

            foreach (var node in methodSyntax.Body.DescendantNodes())
            {
                switch (node)
                {
                    case LocalDeclarationStatementSyntax localDecl:
                        var localType = semanticModel.GetTypeInfo(localDecl.Declaration.Type).Type;
                        if (localType != null && !IsUnmanagedType(localType))
                            foreach (var v in localDecl.Declaration.Variables)
                                diagnostics.Add(Diagnostic.Create(InvalidLocalVariableTypeError, v.GetLocation(), method.Name, v.Identifier.Text, localType.ToDisplayString()));
                        break;

                    case InvocationExpressionSyntax invocation:
                        var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                        if (symbolInfo.Symbol is IMethodSymbol calledMethod)
                        {
                            if (!IsAllowedMethodCall(calledMethod, compilation))
                                diagnostics.Add(Diagnostic.Create(DisallowedMethodCallError, invocation.GetLocation(), method.Name, calledMethod.ToDisplayString()));
                        }
                        break;

                    case ObjectCreationExpressionSyntax objCreation:
                        var createdType = semanticModel.GetTypeInfo(objCreation.Type).Type;
                        if (createdType != null && !IsUnmanagedType(createdType))
                            diagnostics.Add(Diagnostic.Create(ManagedObjectCreationError, objCreation.GetLocation(), method.Name, createdType.ToDisplayString()));
                        break;

                    case IdentifierNameSyntax identifier:
                        var typeInfo = semanticModel.GetTypeInfo(identifier);
                        var idSymbolInfo = semanticModel.GetSymbolInfo(identifier);
                        if (idSymbolInfo.Symbol is ITypeSymbol || idSymbolInfo.Symbol is IMethodSymbol)
                            break;
                        if (typeInfo.Type != null && typeInfo.Type.IsReferenceType && typeInfo.Type.SpecialType != SpecialType.System_String)
                            diagnostics.Add(Diagnostic.Create(ReferenceTypeUsageError, identifier.GetLocation(), method.Name, typeInfo.Type.ToDisplayString()));
                        break;
                }
            }

            return diagnostics.Count == 0;
        }

        public static bool ValidateJobStruct(INamedTypeSymbol structSymbol, Compilation compilation, out List<Diagnostic> diagnostics)
        {
            diagnostics = new List<Diagnostic>();

            if (!structSymbol.IsValueType)
                diagnostics.Add(Diagnostic.Create(InvalidJobTypeError, structSymbol.Locations.FirstOrDefault(), structSymbol.Name));

            bool isChunkJob = structSymbol.AllInterfaces.Any(i => SymbolHelper.IsEntJoyJobInterface(i, "IJobChunk"));
            bool isEntityJob = structSymbol.AllInterfaces.Any(i => SymbolHelper.IsEntJoyJobInterface(i, "IJobEntity"));
            bool implementsJob = structSymbol.AllInterfaces.Any(i =>
                SymbolHelper.IsEntJoyJobInterface(i, "IJob") || SymbolHelper.IsEntJoyJobInterface(i, "IJobParallelFor") ||
                SymbolHelper.IsEntJoyJobInterface(i, "IJobFor") || SymbolHelper.IsEntJoyJobInterface(i, "IJobChunk") ||
                SymbolHelper.IsEntJoyJobInterface(i, "IJobEntity"));
            if (!implementsJob)
                diagnostics.Add(Diagnostic.Create(MissingJobInterfaceError, structSymbol.Locations.FirstOrDefault(), structSymbol.Name));

            foreach (var field in structSymbol.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                if (!IsUnmanagedType(field.Type))
                    diagnostics.Add(Diagnostic.Create(InvalidJobFieldError, field.Locations.FirstOrDefault(), structSymbol.Name, field.Name, field.Type.ToDisplayString()));
            }

            var executeMethod = structSymbol.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == "Execute");
            if (executeMethod == null)
            {
                diagnostics.Add(Diagnostic.Create(MissingExecuteMethodError, structSymbol.Locations.FirstOrDefault(), structSymbol.Name));
                return diagnostics.Count == 0;
            }

            if (isEntityJob)
            {
                var attrSymbol = compilation.GetTypeByMetadataName("NativeTranspiler.NativeTranspileAttribute");
                var target = AttributeHelper.GetBackendTarget(structSymbol, attrSymbol);
                if ((target != NativeTranspiler.BackendTarget.Cpp && target != NativeTranspiler.BackendTarget.Ispc) ||
                    executeMethod.Parameters.Length == 0 ||
                    executeMethod.Parameters.Any(p => (p.RefKind != RefKind.Ref && p.RefKind != RefKind.In) || !IsUnmanagedType(p.Type)))
                {
                    diagnostics.Add(Diagnostic.Create(InvalidJobEntityError, executeMethod.Locations.FirstOrDefault(), structSymbol.Name));
                }
            }
            else
            {
                var attrSymbol2 = compilation.GetTypeByMetadataName("NativeTranspiler.NativeTranspileAttribute");
                var target2 = AttributeHelper.GetBackendTarget(structSymbol, attrSymbol2);
                if (target2 == NativeTranspiler.BackendTarget.Gpu)
                    ValidateGpuJobStruct(structSymbol, executeMethod, compilation, diagnostics);
            }

            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body != null)
            {
                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);

                foreach (var node in methodSyntax.Body.DescendantNodes())
                {
                    switch (node)
                    {
                        case LocalDeclarationStatementSyntax localDecl:
                            var localType = semanticModel.GetTypeInfo(localDecl.Declaration.Type).Type;
                            if (localType != null && !IsUnmanagedType(localType) && !(isChunkJob && IsAllowedChunkSpanLocal(localDecl, semanticModel)))
                                foreach (var v in localDecl.Declaration.Variables)
                                    diagnostics.Add(Diagnostic.Create(InvalidLocalVariableTypeError, v.GetLocation(), executeMethod.Name, v.Identifier.Text, localType.ToDisplayString()));
                            break;

                        case InvocationExpressionSyntax invocation:
                            var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                            if (symbolInfo.Symbol is IMethodSymbol calledMethod)
                            {
                                if (isChunkJob && IsDisallowedChunkDataAccess(calledMethod))
                                    diagnostics.Add(Diagnostic.Create(DisallowedChunkDataAccessError, invocation.GetLocation(), executeMethod.Name, calledMethod.ToDisplayString()));
                                else if (!IsAllowedMethodCall(calledMethod, compilation, isChunkJob))
                                    diagnostics.Add(Diagnostic.Create(DisallowedMethodCallError, invocation.GetLocation(), executeMethod.Name, calledMethod.ToDisplayString()));
                            }
                            break;

                        case ObjectCreationExpressionSyntax objCreation:
                            var createdType = semanticModel.GetTypeInfo(objCreation.Type).Type;
                            if (createdType != null && !IsUnmanagedType(createdType))
                                diagnostics.Add(Diagnostic.Create(ManagedObjectCreationError, objCreation.GetLocation(), executeMethod.Name, createdType.ToDisplayString()));
                            break;

                        case IdentifierNameSyntax identifier:
                            var typeInfo = semanticModel.GetTypeInfo(identifier);
                            var idSymbolInfo = semanticModel.GetSymbolInfo(identifier);
                            if (idSymbolInfo.Symbol is ITypeSymbol || idSymbolInfo.Symbol is IMethodSymbol)
                                break;
                            if (typeInfo.Type != null && typeInfo.Type.IsReferenceType && typeInfo.Type.SpecialType != SpecialType.System_String)
                                diagnostics.Add(Diagnostic.Create(ReferenceTypeUsageError, identifier.GetLocation(), executeMethod.Name, typeInfo.Type.ToDisplayString()));
                            break;
                    }
                }
            }

            return diagnostics.Count == 0;
        }

        /// <summary>
        /// GPU Job 专项校验（Milestone 1 约束）：
        ///   仅支持 IJobParallelFor / IJobFor / IJob；
        ///   字段仅支持 NativeArray<T>（T 为 host-shareable）或标量 float/int/uint/enum；
        ///   不支持 NativeList/指针/bool 标量/double/long/用户 struct 标量字段、do-while。
        /// </summary>
        private static void ValidateGpuJobStruct(INamedTypeSymbol structSymbol, IMethodSymbol executeMethod,
            Compilation compilation, List<Diagnostic> diagnostics)
        {
            bool supportedKind = CppJobGenerator.IsParallelForJob(structSymbol) ||
                                 CppJobGenerator.IsForJob(structSymbol) ||
                                 CppJobGenerator.IsIJob(structSymbol);
            if (!supportedKind)
            {
                diagnostics.Add(Diagnostic.Create(InvalidGpuJobError, structSymbol.Locations.FirstOrDefault(),
                    structSymbol.Name, "BackendTarget.Gpu 仅支持 IJobParallelFor / IJobFor / IJob（IJobChunk/IJobEntity 暂不支持）"));
            }

            foreach (var field in structSymbol.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type))
                {
                    if (field.Type.Name != "NativeArray")
                    {
                        diagnostics.Add(Diagnostic.Create(InvalidGpuJobError, field.Locations.FirstOrDefault(),
                            structSymbol.Name, $"字段 '{field.Name}'：NativeList/UnsafeList 暂不支持 GPU，仅 NativeArray"));
                        continue;
                    }
                    var elem = ((INamedTypeSymbol)field.Type).TypeArguments[0];
                    if (!IsWgslHostShareable(elem))
                    {
                        diagnostics.Add(Diagnostic.Create(InvalidGpuJobError, field.Locations.FirstOrDefault(),
                            structSymbol.Name, $"字段 '{field.Name}' 的元素类型 '{elem.ToDisplayString()}' 不支持 GPU（支持 float/int/uint/float2/int2/uint2 及仅含这些的 struct）"));
                    }
                }
                else if (field.Type is IPointerTypeSymbol)
                {
                    diagnostics.Add(Diagnostic.Create(InvalidGpuJobError, field.Locations.FirstOrDefault(),
                        structSymbol.Name, $"字段 '{field.Name}'：指针字段暂不支持 GPU"));
                }
                else
                {
                    var st = field.Type.SpecialType;
                    bool okScalar = st is SpecialType.System_Single or SpecialType.System_Int32 or SpecialType.System_UInt32;
                    bool isEnum = field.Type.TypeKind == TypeKind.Enum;
                    if (!okScalar && !isEnum)
                    {
                        diagnostics.Add(Diagnostic.Create(InvalidGpuJobError, field.Locations.FirstOrDefault(),
                            structSymbol.Name, $"字段 '{field.Name}' 类型 '{field.Type.ToDisplayString()}' 不支持 GPU（标量仅支持 float/int/uint/enum）"));
                    }
                }
            }

            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body != null)
            {
                var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
                foreach (var node in methodSyntax.Body.DescendantNodes())
                {
                    if (node is DoStatementSyntax)
                    {
                        diagnostics.Add(Diagnostic.Create(InvalidGpuJobError, node.GetLocation(),
                            structSymbol.Name, "do-while 循环在 WGSL 中不支持"));
                    }
                }
            }
        }

        /// <summary>判断类型是否为 WGSL host-shareable（数组元素可用）</summary>
        private static bool IsWgslHostShareable(ITypeSymbol type)
        {
            if (WgslTypes.ToWgslType(type) != null)
                return WgslTypes.ToWgslType(type) != "f64"; // double 需要可选扩展
            if (type.IsValueType && type.TypeKind == TypeKind.Struct &&
                !NativeTranspiler.IsBuiltinUnmanaged(type) && !NativeTranspiler.IsEntJoyPredefinedType(type))
            {
                foreach (var f in ((INamedTypeSymbol)type).GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                {
                    if (!IsWgslHostShareable(f.Type))
                        return false;
                }
                return true;
            }
            return false;
        }

        public static bool IsUnmanagedType(ITypeSymbol type)
        {
            if (type is IPointerTypeSymbol)
                return true;
            if (NativeTranspiler.IsEntJoyNativeContainerType(type))
                return true;

            var fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (fullName == "EntJoy.Mathematics.float2" ||
                fullName == "EntJoy.Mathematics.int2" ||
                fullName == "EntJoy.Mathematics.uint2")
                return true;

            if (type.IsValueType && !type.IsReferenceType)
            {
                if (type.SpecialType is SpecialType.System_Boolean or SpecialType.System_Char or
                    SpecialType.System_SByte or SpecialType.System_Byte or
                    SpecialType.System_Int16 or SpecialType.System_UInt16 or
                    SpecialType.System_Int32 or SpecialType.System_UInt32 or
                    SpecialType.System_Int64 or SpecialType.System_UInt64 or
                    SpecialType.System_Single or SpecialType.System_Double or
                    SpecialType.System_IntPtr or SpecialType.System_UIntPtr)
                    return true;

                if (type.TypeKind == TypeKind.Enum)
                    return true;

                if (type.TypeKind == TypeKind.Struct)
                {
                    var namedType = (INamedTypeSymbol)type;
                    foreach (var field in namedType.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                    {
                        if (!IsUnmanagedType(field.Type))
                            return false;
                    }
                    return true;
                }
            }
            return false;
        }

        public static bool IsUnmanagedTypeOrVoid(ITypeSymbol type) =>
            type.SpecialType == SpecialType.System_Void || IsUnmanagedType(type);

        /// <summary>
        /// 检查方法调用是否被允许。
        /// </summary>
        private static bool IsAllowedMethodCall(IMethodSymbol method, Compilation compilation, bool allowChunkMethods = false)
        {
            // 1. 允许对容器类型的实例方法调用 (NativeList, NativeArray)
            if (!method.IsStatic)
            {
                var containingType = method.ContainingType;
                if (containingType != null && NativeTranspiler.IsEntJoyNativeContainerType(containingType))
                    return true;
                if (allowChunkMethods && containingType?.ToDisplayString() == "EntJoy.ArchetypeChunk" &&
                    (method.Name == "GetComponentDataNativeArray" || method.Name == "GetComponentDataSpan"))
                    return true;
                return false;
            }

            // 2. 专门放行 System.Runtime.CompilerServices.Unsafe 类的所有静态方法
            var containingTypeName = method.ContainingType?.ToDisplayString();
            if (containingTypeName == "System.Runtime.CompilerServices.Unsafe")
                return true;

            // 3. 系统白名单（静态方法）
            var fullName = method.ContainingType.ToDisplayString() + "." + method.Name;
            if (AllowedStaticMethods.Contains(fullName))
                return true;

            // 4. 标记了 [NativeTranspile] 的方法
            if (method.GetAttributes().Any(ad => ad.AttributeClass?.Name == "NativeTranspileAttribute"))
                return true;

            // 5. 同一程序集中的用户定义静态方法，且签名符合非托管要求
            if (SymbolEqualityComparer.Default.Equals(method.ContainingAssembly, compilation.Assembly))
            {
                if (!IsUnmanagedTypeOrVoid(method.ReturnType))
                    return false;
                foreach (var p in method.Parameters)
                {
                    if (!IsUnmanagedType(p.Type))
                        return false;
                }
                return true;
            }

            return false;
        }

        private static bool IsDisallowedChunkDataAccess(IMethodSymbol method)
        {
            if (method.ContainingType?.ToDisplayString() != "EntJoy.ArchetypeChunk")
                return false;
            return method.Name == "GetComponentDataPtr";
        }

        private static bool IsAllowedChunkSpanLocal(LocalDeclarationStatementSyntax localDecl, SemanticModel semanticModel)
        {
            var localType = semanticModel.GetTypeInfo(localDecl.Declaration.Type).Type;
            if (localType?.Name != "Span" || localType.ContainingNamespace?.ToDisplayString() != "System")
                return false;

            foreach (var variable in localDecl.Declaration.Variables)
            {
                if (variable.Initializer?.Value is not InvocationExpressionSyntax invocation)
                    return false;
                if (semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
                    return false;
                if (method.ContainingType?.ToDisplayString() != "EntJoy.ArchetypeChunk" || method.Name != "GetComponentDataSpan")
                    return false;
            }

            return true;
        }

        public static List<IFieldSymbol> GetConditionalReadOnlyFields(INamedTypeSymbol jobStruct, SemanticModel semanticModel)
        {
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == "Execute");
            if (executeMethod == null)
                return new List<IFieldSymbol>();

            var methodSyntax = SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body == null)
                return new List<IFieldSymbol>();

            var fields = jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToList();

            var assignedFields = new HashSet<IFieldSymbol>();
            foreach (var node in methodSyntax.Body.DescendantNodes())
            {
                ISymbol? assignedSymbol = null;
                if (node is AssignmentExpressionSyntax assignment)
                    assignedSymbol = semanticModel.GetSymbolInfo(assignment.Left).Symbol;
                else if (node is PostfixUnaryExpressionSyntax postfix &&
                         (postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression)))
                    assignedSymbol = semanticModel.GetSymbolInfo(postfix.Operand).Symbol;
                else if (node is PrefixUnaryExpressionSyntax prefix &&
                         (prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression)))
                    assignedSymbol = semanticModel.GetSymbolInfo(prefix.Operand).Symbol;

                if (assignedSymbol is IFieldSymbol field && fields.Contains(field))
                    assignedFields.Add(field);
            }

            var conditionalReadFields = new HashSet<IFieldSymbol>();
            foreach (var node in methodSyntax.Body.DescendantNodes())
            {
                if (node is IdentifierNameSyntax id)
                {
                    var symbol = semanticModel.GetSymbolInfo(id).Symbol;
                    if (symbol is IFieldSymbol field && fields.Contains(field))
                    {
                        if (IsInConditionContext(id))
                            conditionalReadFields.Add(field);
                    }
                }
            }

            return fields.Where(f => !assignedFields.Contains(f) && conditionalReadFields.Contains(f)).ToList();
        }

        private static bool IsInConditionContext(SyntaxNode node)
        {
            var parent = node.Parent;
            while (parent != null)
            {
                if (parent is IfStatementSyntax ifStmt && ifStmt.Condition.Contains(node)) return true;
                if (parent is WhileStatementSyntax whileStmt && whileStmt.Condition.Contains(node)) return true;
                if (parent is DoStatementSyntax doStmt && doStmt.Condition.Contains(node)) return true;
                if (parent is ForStatementSyntax forStmt && forStmt.Condition != null && forStmt.Condition.Contains(node)) return true;
                if (parent is ConditionalExpressionSyntax cond && cond.Condition.Contains(node)) return true;
                if (parent is BinaryExpressionSyntax binary &&
                    (binary.IsKind(SyntaxKind.LogicalAndExpression) || binary.IsKind(SyntaxKind.LogicalOrExpression)))
                    return true;
                parent = parent.Parent;
            }
            return false;
        }
    }
}
