using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;
using NativeTranspiler.Analyzer.Common;

namespace NativeTranspiler.Analyzer
{
    public static partial class CppJobGenerator
    {
        /// <summary>字段在生成 C++ 里的形态：容器需拆指针+长度，其余按指针/标量绑定。</summary>
        internal enum FieldKind
        {
            NativeList,
            Container,
            Pointer,
            Scalar
        }

        /// <summary>一个 job 字段的 ABI 槽位：偏移、类别与对应的 C++ 类型名。</summary>
        internal readonly struct FieldSlot
        {
            public FieldSlot(IFieldSymbol field, int offset, FieldKind kind, string cppType, string elementType)
            {
                Field = field;
                Offset = offset;
                Kind = kind;
                CppType = cppType;
                ElementType = elementType;
            }

            public IFieldSymbol Field { get; }
            public int Offset { get; }
            public FieldKind Kind { get; }
            public string CppType { get; }
            public string ElementType { get; }
        }

        /// <summary>
        /// 按 C# 声明顺序展开 job 字段的 ABI 槽位。
        /// 字段分类与类型映射只在这里做一次，各发射点只负责按自己的风格格式化。
        /// </summary>
        internal static List<FieldSlot> BuildFieldLayout(INamedTypeSymbol jobStruct)
        {
            var slots = new List<FieldSlot>();
            int currentOffset = 0;
            foreach (var field in jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                int offset = CalculateFieldOffset(field, ref currentOffset);
                string cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                string elementType = cppType;
                FieldKind kind;
                if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type))
                {
                    kind = NativeTranspiler.IsEntJoyContainerNamed(field.Type, Config.NativeList)
                        ? FieldKind.NativeList
                        : FieldKind.Container;
                    elementType = GetCppElementType(field.Type);
                }
                else if (field.Type is IPointerTypeSymbol)
                {
                    kind = FieldKind.Pointer;
                }
                else
                {
                    kind = FieldKind.Scalar;
                }
                slots.Add(new FieldSlot(field, offset, kind, cppType, elementType));
            }
            return slots;
        }
    }
}
