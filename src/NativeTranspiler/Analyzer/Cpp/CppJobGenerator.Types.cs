using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NativeTranspiler.Analyzer.Common;
using System;

namespace NativeTranspiler.Analyzer
{
    public static partial class CppJobGenerator
    {

        public static List<INamedTypeSymbol> CollectChunkNativeArrayTypes(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var result = new List<INamedTypeSymbol>();
            if (IsEntityJob(jobStruct))
            {
                var execute = jobStruct.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == Config.Execute);
                if (execute != null)
                {
                    foreach (var parameter in execute.Parameters)
                    {
                        if (NativeTranspiler.IsEntityType(parameter.Type))
                            continue;   // Entity 参数不是组件列
                        if (parameter.Type is INamedTypeSymbol componentType &&
                            !result.Any(t => SymbolEqualityComparer.Default.Equals(t, componentType)))
                        {
                            result.Add(componentType);
                        }
                    }
                }
                return result;
            }

            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == Config.Execute);
            var methodSyntax = executeMethod == null ? null : SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body == null) return result;

            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
            foreach (var invocation in methodSyntax.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol methodSymbol)
                    continue;
                if (methodSymbol.ContainingType?.ToDisplayString() != Config.TypeArchetypeChunk ||
                    (!Config.IsChunkArrayAccess(methodSymbol.Name)
                     && methodSymbol.Name != Config.GetEnableBitMapPtr))
                    continue;
                if (methodSymbol.TypeArguments.Length == 0 || methodSymbol.TypeArguments[0] is not INamedTypeSymbol componentType)
                    continue;
                if (!result.Any(t => SymbolEqualityComparer.Default.Equals(t, componentType)))
                    result.Add(componentType);
            }
            return result;
        }

        /// <summary>
        /// 收集 IJobChunk Execute 中 ArchetypeChunk.GetSharedComponent&lt;T&gt;() 调用的 shared 类型。
        /// 仅限 blittable（managed shared 不允许在 NativeTranspile job 中访问，由 validator 拦截）。
        /// </summary>
        /// <summary>检测 Job 的 Execute 是否调用 SendEvent（决定是否生成 __header 参数）。</summary>
        public static bool JobUsesSendEvent(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == Config.Execute);
            var methodSyntax = executeMethod == null ? null : SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body == null) return false;

            foreach (var invocation in methodSyntax.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                // xxx.SendEvent / 裸 SendEvent
                if (invocation.Expression is MemberAccessExpressionSyntax mac
                    && mac.Name.Identifier.Text == Config.SendEvent)
                    return true;
                if (invocation.Expression is IdentifierNameSyntax idn
                    && idn.Identifier.Text == Config.SendEvent)
                    return true;
            }
            return false;
        }

        public static List<INamedTypeSymbol> CollectSharedComponentTypes(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var result = new List<INamedTypeSymbol>();
            if (IsEntityJob(jobStruct)) return result;  // IJobEntity 不通过 chunk 参数读 shared（走 job 字段）

            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == Config.Execute);
            var methodSyntax = executeMethod == null ? null : SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body == null) return result;

            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
            foreach (var invocation in methodSyntax.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol methodSymbol)
                    continue;
                if (methodSymbol.ContainingType?.ToDisplayString() != Config.TypeArchetypeChunk ||
                    methodSymbol.Name != Config.GetSharedComponent ||
                    methodSymbol.TypeArguments.Length != 1)
                    continue;
                if (methodSymbol.TypeArguments[0] is not INamedTypeSymbol sharedType)
                    continue;
                if (!result.Any(t => SymbolEqualityComparer.Default.Equals(t, sharedType)))
                    result.Add(sharedType);
            }
            return result;
        }

        internal static List<string> CollectJobStructIncludes(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var includes = new HashSet<string>();
            void AddType(ITypeSymbol type)
            {
                if (type is IPointerTypeSymbol ptr)
                {
                    AddType(ptr.PointedAtType);
                    return;
                }
                // 泛型：先递归模板实参（EntJoy 侧泛型如 NativeComponentLookup<T> 自身由框架头提供，
                // 但实参是用户结构体时必须有头文件 —— 例如体内 `T* p = …` 会写全限定名 T*）。
                if (type is INamedTypeSymbol generic && generic.IsGenericType)
                {
                    foreach (var arg in generic.TypeArguments)
                        AddType(arg);
                    if (NativeTranspiler.IsEntJoyNativeContainerType(type))
                        return;
                }
                if (type is INamedTypeSymbol namedType &&
                    type.TypeKind == TypeKind.Struct &&
                    !NativeTranspiler.IsBuiltinUnmanaged(type) &&
                    !NativeTranspiler.IsEntJoyPredefinedType(type))
                {
                    includes.Add(NativeTranspiler.GetStructHeaderFileName(namedType));
                }
            }

            foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                AddType(field.Type);
            foreach (var type in CollectChunkNativeArrayTypes(jobStruct, compilation))
                AddType(type);
            // SharedComponent 类型头文件（blittable，GetSharedComponent<T>() 用）
            foreach (var type in CollectSharedComponentTypes(jobStruct, compilation))
                AddType(type);
            // SendEvent 事件类型头文件
            foreach (var type in CollectSendEventTypes(jobStruct, compilation))
                AddType(type);

            return includes.OrderBy(x => x).ToList();
        }

        /// <summary>收集 Execute 中 SendEvent 用到的事件类型。</summary>
        public static List<INamedTypeSymbol> CollectSendEventTypes(INamedTypeSymbol jobStruct, Compilation compilation)
        {
            var result = new List<INamedTypeSymbol>();
            var executeMethod = jobStruct.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == Config.Execute);
            var methodSyntax = executeMethod == null ? null : SymbolHelper.GetMethodSyntax(executeMethod);
            if (methodSyntax?.Body == null) return result;

            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
            foreach (var invocation in methodSyntax.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                // 判断是否 SendEvent
                bool isSendEvent = invocation.Expression is MemberAccessExpressionSyntax macS && macS.Name.Identifier.Text == Config.SendEvent;
                if (!isSendEvent && invocation.Expression is IdentifierNameSyntax idS && idS.Identifier.Text == Config.SendEvent)
                    isSendEvent = true;
                if (!isSendEvent) continue;

                // 显式泛型参数 SendEvent<T>
                if (invocation.Expression is MemberAccessExpressionSyntax mac2 && mac2.Name is GenericNameSyntax gn)
                {
                    foreach (var typeArg in gn.TypeArgumentList.Arguments)
                    {
                        var taType = semanticModel.GetTypeInfo(typeArg).Type;
                        // 用 IsStructBlittable 替代 IsUnmanagedType：对嵌套 struct 也正确判断
                        if (taType is INamedTypeSymbol named && IsStructBlittable(named))
                            result.Add(named);
                    }
                }
                // new 表达式参数 SendEvent(new XEvent {...})
                foreach (var arg in invocation.ArgumentList.Arguments)
                {
                    if (arg.Expression is ObjectCreationExpressionSyntax objCreate)
                    {
                        // 注意：不能只查 GetTypeInfo(objCreate.Type)。VS/MSBuild 的 Roslyn 对
                        // object-initializer 的 .Type（QualifiedNameSyntax/IdentifierNameSyntax）
                        // 始终返回表达式类型（两种引擎都可靠）。dotnet CLI 两者都行，VS 只有后者行。
                        var createdType = semanticModel.GetTypeInfo(objCreate).Type
                                       ?? semanticModel.GetTypeInfo(objCreate.Type).Type;
                        // 用 IsStructBlittable 代替 IsUnmanagedType：后者对嵌套 struct 会返回 false
                        // （Roslyn known issue：ContainingType 是 class 时内部 struct 即使全 unmanaged 也判 false）
                        if (createdType is INamedTypeSymbol named2 && IsStructBlittable(named2))
                            result.Add(named2);
                    }
                }
            }
            var distinct = new HashSet<INamedTypeSymbol>(result, SymbolEqualityComparer.Default);
            return distinct.ToList();
        }

        /// <summary>
        /// 手动递归检查 struct 是否 blittable（所有字段都是 unmanaged 类型）。
        /// 替代 IsUnmanagedType：对嵌套 struct 也能正确判断（Roslyn IsUnmanagedType 对嵌套类型会返回 false）。
        /// </summary>
        private static bool IsStructBlittable(INamedTypeSymbol type)
        {
            return IsStructBlittable(type, new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default));
        }

        private static bool IsStructBlittable(INamedTypeSymbol type, HashSet<INamedTypeSymbol> visited)
        {
            // 排除引用类型
            if (type.IsReferenceType) return false;
            // 必须有 layout 信息的 struct
            if (type.TypeKind != TypeKind.Struct) return false;

            // 简单快速路径：如果 Roslyn 自身判定为 unmanaged，直接通过
            if (type.IsUnmanagedType) return true;

            // 防止自递归 / 循环引用
            if (!visited.Add(type)) return true; // 已访问过，认为 blittable（结构体自身引用只可能通过指针）

            // 兜底：递归检查所有字段都是 blittable
            foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
            {
                if (field.IsStatic) continue;
                if (!IsTypeBlittable(field.Type, visited)) return false;
            }
            return true;
        }

        private static bool IsTypeBlittable(ITypeSymbol type)
        {
            return IsTypeBlittable(type, new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default));
        }

        private static bool IsTypeBlittable(ITypeSymbol type, HashSet<INamedTypeSymbol> visited)
        {
            if (type.IsReferenceType) return false;
            if (type.TypeKind == TypeKind.Pointer) return true;
            if (type is IArrayTypeSymbol) return false;
            if (type.TypeKind == TypeKind.Enum) return true; // enum 的 underlying type 必须 unmanaged
            if (type is INamedTypeSymbol named)
            {
                if (named.IsGenericType) return false; // 泛型可能含引用类型
                if (named.TypeKind == TypeKind.Struct) return IsStructBlittable(named, visited);
                // 基本数值类型都是 blittable
                switch (named.SpecialType)
                {
                    case SpecialType.System_Boolean:
                    case SpecialType.System_Byte:
                    case SpecialType.System_SByte:
                    case SpecialType.System_Int16:
                    case SpecialType.System_UInt16:
                    case SpecialType.System_Int32:
                    case SpecialType.System_UInt32:
                    case SpecialType.System_Int64:
                    case SpecialType.System_UInt64:
                    case SpecialType.System_Single:
                    case SpecialType.System_Double:
                    case SpecialType.System_Char:
                    case SpecialType.System_IntPtr:
                    case SpecialType.System_UIntPtr:
                        return true;
                }
                // EntJoy 自己的 unmanaged 类型
                if (named.ContainingNamespace?.ToDisplayString() == Config.NamespaceEntJoyECS &&
                    named.Name == "Entity")
                    return true;
                // EntJoy.Mathematics.* 下所有 blittable 数学类型（float2/int2/uint2 等）
                if (named.ContainingNamespace?.ToDisplayString() == Config.NamespaceEntJoyMathematics &&
                    named.IsValueType)
                    return true;
            }
            return false;
        }
    }
}
