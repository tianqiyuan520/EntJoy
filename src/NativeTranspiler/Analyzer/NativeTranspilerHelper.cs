// using Microsoft.CodeAnalysis;
// using Microsoft.CodeAnalysis.CSharp;
// using Microsoft.CodeAnalysis.CSharp.Syntax;
// using System;
// using System.Collections.Generic;
// using System.Linq;
// using System.Text;

// namespace NativeTranspiler.Analyzer
// {
//     /// <summary>
//     /// 集中存放所有公用工具方法，消除各文件间的重复代码。
//     /// </summary>
//     internal static class NativeTranspilerHelper
//     {
//         // ==================== 1. 类型收集工具 ====================

//         /// <summary>从类型中递归收集用户自定义结构体（用于头文件包含）</summary>
//         public static void CollectIncludesFromType(ITypeSymbol type, HashSet<string> includes)
//         {
//             if (type is IPointerTypeSymbol ptr)
//             {
//                 CollectIncludesFromType(ptr.PointedAtType, includes);
//                 return;
//             }
//             if (NativeTranspiler.IsEntJoyPredefinedType(type))
//                 return;
//             if (type is INamedTypeSymbol named && named.IsGenericType)
//             {
//                 var defName = named.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
//                 if (defName == "EntJoy.Collections.NativeArray<T>" ||
//                     defName == "EntJoy.Collections.NativeList<T>" ||
//                     defName == "EntJoy.Collections.UnsafeList<T>")
//                     return;
//             }
//             if (type.IsValueType && !IsBuiltinUnmanaged(type))
//             {
//                 includes.Add(NativeTranspiler.GetStructHeaderFileName((INamedTypeSymbol)type));
//             }
//         }

//         /// <summary>从方法参数 + 局部变量收集用户结构体包含</summary>
//         public static List<string> CollectUserStructIncludes(IMethodSymbol method, Compilation compilation)
//         {
//             var includes = new HashSet<string>();
//             foreach (var param in method.Parameters)
//                 CollectIncludesFromType(param.Type, includes);

//             var methodSyntax = SymbolHelper.GetMethodSyntax(method);
//             if (methodSyntax?.Body != null)
//             {
//                 var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
//                 foreach (var localDecl in methodSyntax.Body.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
//                 {
//                     var localType = semanticModel.GetTypeInfo(localDecl.Declaration.Type).Type;
//                     if (localType != null)
//                         CollectIncludesFromType(localType, includes);
//                 }
//             }
//             return includes.OrderBy(x => x).ToList();
//         }

//         /// <summary>从 Job 结构体字段收集用户结构体包含</summary>
//         public static List<string> CollectUserStructIncludes(INamedTypeSymbol jobStruct)
//         {
//             var includes = new HashSet<string>();
//             foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
//                 CollectIncludesFromType(field.Type, includes);
//             return includes.OrderBy(x => x).ToList();
//         }

//         internal static bool IsBuiltinUnmanaged(ITypeSymbol type) =>
//             NativeTranspiler.IsBuiltinUnmanaged(type);

//         // ==================== 2. ISPC 类型映射 ====================

//         private static readonly Dictionary<string, string> CppToIspcTypeMap = new()
//         {
//             ["EntJoy::Mathematics::float2"] = "float2",
//             ["EntJoy::Mathematics::int2"] = "int2",
//             ["EntJoy::Mathematics::uint2"] = "uint2",
//             ["unsigned int"] = "unsigned int",
//             ["float"] = "float",
//             ["int"] = "int",
//             ["bool"] = "bool",
//         };

//         /// <summary>将 C++ 类型名映射为 ISPC 类型名</summary>
//         public static string ToIspcType(string cppType)
//         {
//             return CppToIspcTypeMap.TryGetValue(cppType, out var ispcType) ? ispcType : cppType;
//         }

//         public static bool IsVectorType(string cppType) =>
//             cppType == "EntJoy::Mathematics::float2" ||
//             cppType == "EntJoy::Mathematics::int2" ||
//             cppType == "EntJoy::Mathematics::uint2";

//         // ==================== 3. 导出宏 ====================

//         public static string GenerateExportMacros() => @"
// #ifdef __cplusplus
// #define EXTERNC extern ""C""
// #else
// #define EXTERNC
// #endif

// #ifdef _WIN32
// #define CALLINGCONVENTION __cdecl
// #else
// #define CALLINGCONVENTION
// #endif

// #ifdef DLL_IMPORT
// #define HEAD EXTERNC __declspec(dllimport)
// #else
// #define HEAD EXTERNC __declspec(dllexport)
// #endif

// #if defined(_MSC_VER) || defined(__clang__)
//   #define RESTRICT __restrict
// #else
//   #define RESTRICT __restrict__
// #endif
// ";

//         public static string GenerateAtomicMacros() => @"
// // Cross-compiler atomic macros
// #ifdef _MSC_VER
// #include <intrin.h>
// #define INTERLOCKED_FETCH_ADD(ptr, val) _InterlockedExchangeAdd((long*)(ptr), (val))
// #define INTERLOCKED_FETCH_SUB(ptr, val) _InterlockedExchangeAdd((long*)(ptr), -(val))
// #define INTERLOCKED_EXCHANGE(ptr, val)   _InterlockedExchange((long*)(ptr), (val))
// #define INTERLOCKED_ADD_AND_FETCH(ptr, val)   (_InterlockedExchangeAdd((long*)(ptr), (val)) + (val))
// #define INTERLOCKED_INCREMENT_AND_FETCH(ptr)  _InterlockedIncrement((long*)(ptr))
// #define INTERLOCKED_DECREMENT_AND_FETCH(ptr)  _InterlockedDecrement((long*)(ptr))
// #define INTERLOCKED_SUB_AND_FETCH(ptr, val)   (_InterlockedExchangeAdd((long*)(ptr), -(val)) - (val))
// #define INTERLOCKED_COMPARE_EXCHANGE(ptr, oldVal, newVal) _InterlockedCompareExchange((long*)(ptr), (newVal), (oldVal))
// #else
// #define INTERLOCKED_FETCH_ADD(ptr, val) __sync_fetch_and_add((ptr), (val))
// #define INTERLOCKED_FETCH_SUB(ptr, val) __sync_fetch_and_sub((ptr), (val))
// #define INTERLOCKED_EXCHANGE(ptr, val)   __sync_lock_test_and_set((ptr), (val))
// #define INTERLOCKED_ADD_AND_FETCH(ptr, val)   __sync_add_and_fetch((ptr), (val))
// #define INTERLOCKED_INCREMENT_AND_FETCH(ptr)  __sync_add_and_fetch((ptr), 1)
// #define INTERLOCKED_DECREMENT_AND_FETCH(ptr)  __sync_sub_and_fetch((ptr), 1)
// #define INTERLOCKED_SUB_AND_FETCH(ptr, val)   __sync_sub_and_fetch((ptr), (val))
// #define INTERLOCKED_COMPARE_EXCHANGE(ptr, oldVal, newVal) __sync_val_compare_and_swap((ptr), (oldVal), (newVal))
// #endif
// ";

//         // ==================== 4. ISPC UnsafeList 上下文结构体生成 ====================

//         /// <summary>生成 ISPC 中 NativeList 对应的 UnsafeList_Context 结构体</summary>
//         public static string GenerateUnsafeListContextStruct(ITypeSymbol elementType)
//         {
//             var cppElem = NativeTranspiler.MapCSharpTypeToCpp(elementType);
//             var ispcElem = ToIspcType(cppElem);
//             var sb = new StringBuilder();
//             sb.AppendLine($"struct UnsafeList_Context_{ispcElem} {{");
//             sb.AppendLine($"    void* uniform _data;");
//             sb.AppendLine($"    uniform int _length;");
//             sb.AppendLine($"    uniform int _capacity;");
//             sb.AppendLine($"    uniform int _allocator;");
//             sb.AppendLine($"    void (* uniform ResizeFunc)(void* uniform * uniform _data, uniform int * uniform _length, uniform int * uniform _capacity, uniform int * uniform _allocator, uniform int newSize, uniform bool clear);");
//             sb.AppendLine("};");
//             return sb.ToString();
//         }

//         /// <summary>获取方法参数中的 NativeList 字段列表</summary>
//         public static List<IParameterSymbol> GetNativeListParams(IMethodSymbol method) =>
//             method.Parameters
//                 .Where(p => NativeTranspiler.IsEntJoyNativeContainerType(p.Type) && p.Type.Name == "NativeList")
//                 .ToList();

//         /// <summary>获取 Job 结构体中的 NativeList 字段列表</summary>
//         public static List<IFieldSymbol> GetNativeListFields(INamedTypeSymbol jobStruct) =>
//             jobStruct.GetMembers().OfType<IFieldSymbol>()
//                 .Where(f => !f.IsStatic && NativeTranspiler.IsEntJoyNativeContainerType(f.Type) && f.Type.Name == "NativeList")
//                 .ToList();

//         // ==================== 5. 唯一类型比较器 ====================

//         internal class TypeSymbolComparer : IEqualityComparer<ITypeSymbol>
//         {
//             public bool Equals(ITypeSymbol x, ITypeSymbol y) => SymbolEqualityComparer.Default.Equals(x, y);
//             public int GetHashCode(ITypeSymbol obj) => SymbolEqualityComparer.Default.GetHashCode(obj);
//         }

//         // ==================== 6. 数学函数映射表 ====================

//         private static readonly Dictionary<string, string> CppMathFunctionMap = new()
//         {
//             ["Abs"] = "std::abs",
//             ["Acos"] = "std::acos",
//             ["Asin"] = "std::asin",
//             ["Atan"] = "std::atan",
//             ["Atan2"] = "std::atan2",
//             ["Ceiling"] = "std::ceil",
//             ["Cos"] = "std::cos",
//             ["Cosh"] = "std::cosh",
//             ["Exp"] = "std::exp",
//             ["Floor"] = "std::floor",
//             ["Log"] = "std::log",
//             ["Log10"] = "std::log10",
//             ["Max"] = "std::max",
//             ["Min"] = "std::min",
//             ["Pow"] = "std::pow",
//             ["Round"] = "std::round",
//             ["Sin"] = "std::sin",
//             ["Sinh"] = "std::sinh",
//             ["Sqrt"] = "std::sqrt",
//             ["Tan"] = "std::tan",
//             ["Tanh"] = "std::tanh",
//             ["Truncate"] = "std::trunc",
//         };

//         /// <summary>映射 C# System.Math 到 C++ 函数名</summary>
//         public static string? GetCppMathFunction(string methodName) =>
//             CppMathFunctionMap.TryGetValue(methodName, out var cppFunc) ? cppFunc : null;

//         /// <summary>映射 C# System.Math 到 ISPC 函数名</summary>
//         public static string? GetIspcMathFunction(string methodName) => methodName switch
//         {
//             "Sin" => "sin",
//             "Cos" => "cos",
//             "Sqrt" => "sqrt",
//             "Exp" => "exp",
//             "Log" => "log",
//             "Abs" => "abs",
//             "Floor" => "floor",
//             "Ceiling" => "ceil",
//             _ => methodName.ToLower()
//         };

//         private static readonly Dictionary<string, string> EntJoyMathFunctionMap = new()
//         {
//             ["dot"] = "EntJoy::Mathematics::dot",
//             ["lengthsq"] = "EntJoy::Mathematics::lengthsq",
//             ["length"] = "EntJoy::Mathematics::length",
//             ["normalize"] = "EntJoy::Mathematics::normalize",
//             ["abs"] = "EntJoy::Mathematics::abs",
//             ["min"] = "EntJoy::Mathematics::min",
//             ["max"] = "EntJoy::Mathematics::max",
//             ["clamp"] = "EntJoy::Mathematics::clamp",
//             ["lerp"] = "EntJoy::Mathematics::lerp",
//             ["floor"] = "EntJoy::Mathematics::floor",
//             ["ceil"] = "EntJoy::Mathematics::ceil",
//             ["distancesq"] = "EntJoy::Mathematics::distancesq",
//         };

//         /// <summary>映射 EntJoy.Mathematics.math 到 C++ 函数名</summary>
//         public static string? GetCppEntJoyMathFunction(string methodName) =>
//             EntJoyMathFunctionMap.TryGetValue(methodName, out var cppFunc) ? cppFunc : null;

//         // ==================== 7. 通用 Resize Callback 生成 ====================

//         /// <summary>生成 ISPC wrapper 中的 UnsafeList Resize 回调</summary>
//         public static string GenerateResizeCallback(ITypeSymbol elementType)
//         {
//             var cppElem = NativeTranspiler.MapCSharpTypeToCpp(elementType);
//             var ispcElem = ToIspcType(cppElem);
//             var sb = new StringBuilder();
//             sb.AppendLine($"static void UnsafeList_Resize_{ispcElem}_callback(void** data, int* length, int* capacity, int* allocator, int newSize, bool clear) {{");
//             sb.AppendLine($"    using Alloc = EntJoy::Collections::Allocator;");
//             sb.AppendLine($"    EntJoy::Collections::UnsafeList<{cppElem}> tmp;");
//             sb.AppendLine($"    tmp.Ptr = static_cast<{cppElem}*>(*data);");
//             sb.AppendLine($"    tmp.Length = *length;");
//             sb.AppendLine($"    tmp.Capacity = *capacity;");
//             sb.AppendLine($"    tmp.Allocator = static_cast<Alloc>(*allocator);");
//             sb.AppendLine($"    EntJoy::Collections::NativeArrayOptions opts = clear ? EntJoy::Collections::NativeArrayOptions::ClearMemory : EntJoy::Collections::NativeArrayOptions::UninitializedMemory;");
//             sb.AppendLine($"    tmp.Resize(newSize, opts);");
//             sb.AppendLine($"    *data = tmp.Ptr;");
//             sb.AppendLine($"    *length = tmp.Length;");
//             sb.AppendLine($"    *capacity = tmp.Capacity;");
//             sb.AppendLine($"    *allocator = static_cast<int>(tmp.Allocator);");
//             sb.AppendLine("}");
//             return sb.ToString();
//         }

//         /// <summary>从一组类型中生成唯一 Resize 回调</summary>
//         public static string GenerateDistinctResizeCallbacks(IEnumerable<ITypeSymbol> elementTypes)
//         {
//             var sb = new StringBuilder();
//             var seen = new HashSet<string>();
//             foreach (var elem in elementTypes)
//             {
//                 var key = elem.ToDisplayString();
//                 if (seen.Add(key))
//                     sb.AppendLine(GenerateResizeCallback(elem));
//             }
//             return sb.ToString();
//         }

//         // ==================== 8. 构建上下文填充代码 ====================

//         /// <summary>为 ISPC wrapper 生成 NativeList 上下文填充代码</summary>
//         public static string BuildContextFillCode(IEnumerable<IFieldSymbol> nativeListFields, string prefix = "")
//         {
//             var sb = new StringBuilder();
//             foreach (var field in nativeListFields)
//             {
//                 var elemType = ((INamedTypeSymbol)field.Type).TypeArguments[0];
//                 var cppElem = NativeTranspiler.MapCSharpTypeToCpp(elemType);
//                 var ispcElem = ToIspcType(cppElem);
//                 string name = prefix + field.Name;
//                 sb.AppendLine($"    ispc::UnsafeList_Context_{ispcElem} {name}_ctx;");
//                 sb.AppendLine($"    {name}_ctx._data = {name}_listData->Ptr;");
//                 sb.AppendLine($"    {name}_ctx._length = {name}_listData->Length;");
//                 sb.AppendLine($"    {name}_ctx._capacity = {name}_listData->Capacity;");
//                 sb.AppendLine($"    {name}_ctx._allocator = static_cast<int>({name}_listData->Allocator);");
//                 sb.AppendLine($"    {name}_ctx.ResizeFunc = UnsafeList_Resize_{ispcElem}_callback;");
//             }
//             return sb.ToString();
//         }

//         /// <summary>为 ISPC wrapper 生成 NativeList 上下文回写代码</summary>
//         public static string BuildContextUpdateCode(IEnumerable<IFieldSymbol> nativeListFields, string prefix = "")
//         {
//             var sb = new StringBuilder();
//             foreach (var field in nativeListFields)
//             {
//                 var elemType = ((INamedTypeSymbol)field.Type).TypeArguments[0];
//                 var cppElem = NativeTranspiler.MapCSharpTypeToCpp(elemType);
//                 string name = prefix + field.Name;
//                 sb.AppendLine($"    {name}_listData->Length = {name}_ctx._length;");
//                 sb.AppendLine($"    {name}_listData->Capacity = {name}_ctx._capacity;");
//                 sb.AppendLine($"    {name}_listData->Ptr = static_cast<{cppElem}*>({name}_ctx._data);");
//                 sb.AppendLine($"    {name}_listData->Allocator = static_cast<EntJoy::Collections::Allocator>({name}_ctx._allocator);");
//             }
//             return sb.ToString();
//         }

//         /// <summary>为 ISPC wrapper 生成参数列表中的 NativeList 部分</summary>
//         public static string BuildNativeListParam(IFieldSymbol field)
//         {
//             var elemType = ((INamedTypeSymbol)field.Type).TypeArguments[0];
//             var cppElem = NativeTranspiler.MapCSharpTypeToCpp(elemType);
//             return $"EntJoy::Collections::UnsafeList<{cppElem}>* RESTRICT {field.Name}_listData";
//         }

//         public static string BuildNativeArrayParam(IFieldSymbol field)
//         {
//             var elemType = ((INamedTypeSymbol)field.Type).TypeArguments[0];
//             var cppElem = NativeTranspiler.MapCSharpTypeToCpp(elemType);
//             return $"{cppElem}* RESTRICT {field.Name}_ptr, int {field.Name}_length";
//         }
//     }
// }
