using Microsoft.CodeAnalysis;
using System.Linq;
using System.Text;
using NativeTranspiler.Analyzer.Common;

namespace NativeTranspiler.Analyzer
{
    public static partial class CppJobGenerator
    {
        // 三种 Chunk adapter 共用字段布局与绑定规则；单 Chunk 入口保留可写长度引用。
        private static void AppendChunkAdapterFields(StringBuilder sb, INamedTypeSymbol jobStruct,
            IFieldSymbol? mask, FieldLoopUse loopUse, bool mutableLengths = false)
        {
            var slots = BuildFieldLayout(jobStruct);
            foreach (var slot in slots)
            {
                string name = slot.Field.Name;
                string address = CppIntegerSemantics.CastOperand(
                    CppIntegerSemantics.JobAddress("__jobContext", slot.Offset));
                switch (slot.Kind)
                {
                    case FieldKind.NativeList:
                        sb.AppendLine($"    auto* {name}_listData = *(EntJoy::Collections::UnsafeList<{slot.ElementType}>**){address};");
                        break;
                    case FieldKind.Container:
                        sb.AppendLine($"    auto* {name}_ptr = *({slot.ElementType}**){address};");
                        string reference = mutableLengths ? "&" : "";
                        sb.AppendLine($"    int{reference} {name}_length = *(int*){CppIntegerSemantics.CastOperand(CppIntegerSemantics.JobAddress("__jobContext", slot.Offset + 8))};");
                        break;
                    default:
                        string dereference = slot.Kind == FieldKind.Pointer ? "*" : "";
                        sb.AppendLine($"    auto* {name}_ptr = {dereference}({slot.CppType}*){address};");
                        break;
                }
            }
            foreach (var slot in slots)
            {
                if (slot.Kind == FieldKind.NativeList)
                    sb.AppendLine($"    EntJoy::Collections::UnsafeList<{slot.ElementType}>& {slot.Field.Name} = *{slot.Field.Name}_listData;");
            }
            foreach (var slot in slots)
            {
                if (slot.Kind == FieldKind.Scalar)
                    AppendMaskFieldBinding(sb, slot.Field, mask, loopUse.Written.Contains(slot.Field.Name));
            }
        }
    }
}
