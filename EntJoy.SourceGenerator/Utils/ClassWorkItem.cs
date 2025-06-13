using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EntJoy.SourceGenerator.Utils
{
    /// <summary>
    ///  ClassWorkItem 类
    ///     用于存储类相关信息
    /// </summary>
    internal class ClassWorkItem
    {
        public readonly ClassDeclarationSyntax ClassDeclarationSyntax;
        public bool IsExist { get; private set; }
        public string TypeName { get; private set; }

        public ClassWorkItem(ClassDeclarationSyntax classDeclaration)
        {
            ClassDeclarationSyntax = classDeclaration;
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
