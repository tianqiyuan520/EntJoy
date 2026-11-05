using Microsoft.CodeAnalysis;

namespace NativeTranspiler.Analyzer.Common
{
    internal static class CppIntegerSemantics
    {
        public static bool TryGetSignedStorage(SpecialType type, out string signed, out string unsigned)
        {
            switch (type)
            {
                case SpecialType.System_Int32: signed = "int"; unsigned = "unsigned"; return true;
                case SpecialType.System_Int64: signed = "long long"; unsigned = "unsigned long long"; return true;
                default: signed = unsigned = ""; return false;
            }
        }
    }
}
