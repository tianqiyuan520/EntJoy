using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EntJoy.SourceGenerator.Utils
{
    /// <summary>
    ///  StructWorkItem 类
    ///     用于存储结构体相关信息
    /// </summary>
    internal class StructWorkItem
    {
        public readonly StructDeclarationSyntax StructDeclarationSyntax;
        public bool IsExist { get; private set; }
        public string TypeName { get; private set; }

        public StructWorkItem(StructDeclarationSyntax structDeclaration)
        {
            StructDeclarationSyntax = structDeclaration;
        }

        public void SetTypeName(string typeName)
        {
            TypeName = typeName;
        }

        public void SetIsExist(bool isExist)
        {
            IsExist = isExist;
        }
    }
}
