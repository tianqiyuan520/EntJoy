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
            var fields = jobStruct.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToArray();
            int currentOffset = 0;
            foreach (var field in fields)
            {
                int offset = CalculateFieldOffset(field, ref currentOffset);
                if (NativeTranspiler.IsEntJoyNativeContainerType(field.Type))
                {
                    string elementType = GetCppElementType(field.Type);
                    if (NativeTranspiler.IsEntJoyContainerNamed(field.Type, Config.NativeList))
                        sb.AppendLine($"    auto* {field.Name}_listData = *(EntJoy::Collections::UnsafeList<{elementType}>**)(__jobContext + {offset});");
                    else
                    {
                        sb.AppendLine($"    auto* {field.Name}_ptr = *({elementType}**)(__jobContext + {offset});");
                        string reference = mutableLengths ? "&" : "";
                        sb.AppendLine($"    int{reference} {field.Name}_length = *(int*)(__jobContext + {offset + 8});");
                    }
                }
                else
                {
                    string cppType = NativeTranspiler.MapCSharpTypeToCpp(field.Type);
                    string dereference = field.Type is IPointerTypeSymbol ? "*" : "";
                    sb.AppendLine($"    auto* {field.Name}_ptr = {dereference}({cppType}*)(__jobContext + {offset});");
                }
            }
            foreach (var field in fields)
            {
                if (NativeTranspiler.IsEntJoyContainerNamed(field.Type, Config.NativeList))
                {
                    string elementType = GetCppElementType(field.Type);
                    sb.AppendLine($"    EntJoy::Collections::UnsafeList<{elementType}>& {field.Name} = *{field.Name}_listData;");
                }
            }
            foreach (var field in fields)
            {
                if (!NativeTranspiler.IsEntJoyNativeContainerType(field.Type) && field.Type is not IPointerTypeSymbol)
                    AppendMaskFieldBinding(sb, field, mask, loopUse.Written.Contains(field.Name));
            }
        }
    }
}
