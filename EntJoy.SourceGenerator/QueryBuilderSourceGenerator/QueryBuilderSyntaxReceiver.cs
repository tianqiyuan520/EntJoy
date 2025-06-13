//using EntJoy.SourceGenerator.Utils;
//using Microsoft.CodeAnalysis;
//using Microsoft.CodeAnalysis.CSharp;
//using Microsoft.CodeAnalysis.CSharp.Syntax;
//using System.Collections.Generic;
//using System.Diagnostics;
//using System.Linq;
//using System.Text;

//namespace EntJoy.SourceGenerator.QueryBuilderSourceGenerator
//{
//    // 筛选语法节点
//    internal sealed class QueryBuilderSyntaxReceiver : ISyntaxReceiver
//    {
//        public Dictionary<string, List<ClassWorkItem>> CandidatedWorkItems { get; } = new Dictionary<string, List<ClassWorkItem>>();

//        // 遍历 AST节点
//        public void OnVisitSyntaxNode(SyntaxNode syntaxNode)
//        {
//            //判断该节点是否符合要求
//            if (TryGetWorkItem(syntaxNode, out var classWorkItem))
//            {
//                //符合要求就放入 CandidatedWorkItems
//                if (CandidatedWorkItems.ContainsKey(classWorkItem.TypeName))
//                {
//                    CandidatedWorkItems[classWorkItem.TypeName].Add(classWorkItem);
//                }
//                else
//                {
//                    CandidatedWorkItems.Add(classWorkItem.TypeName, new List<ClassWorkItem>() { classWorkItem });
//                }
//            }
//        }

//        //语法节点接收器，处理类声明节点，查找QueryBuilderAttribute属性，生成ClassWorkItem
//        private static bool TryGetWorkItem(SyntaxNode syntaxNode, out ClassWorkItem classWorkItem)
//        {
//            //查找具有属性的类的声明语句
//            if (syntaxNode is ClassDeclarationSyntax classDeclarationSyntax && classDeclarationSyntax.AttributeLists.Count > 0)
//            {
//                var attributes = from attributeList in classDeclarationSyntax.AttributeLists
//                                 from attriute in attributeList.Attributes
//                                 select attriute;

//                var item = new ClassWorkItem(classDeclarationSyntax);

//                //是否 匹配到合适 的属性
//                foreach (var attribute in attributes)
//                {
//                    var attribuiteName = attribute.Name.ToString();
//                    switch (attribuiteName)
//                    {
//                        case var name when
//                            name == QueryBuilderSourceGenerator.QueryBuilderAttributeName ||
//                            name == QueryBuilderSourceGenerator.QueryBuilderAttributeName + "Attribute" ||
//                            name == Def.Dom_Generateds + "." + QueryBuilderSourceGenerator.QueryBuilderAttributeName ||
//                            name == Def.Dom_Generateds + "." + QueryBuilderSourceGenerator.QueryBuilderAttributeName + "Attribute" :
//                            item.SetIsExist(true);

//                            break;
//                    }
//                }

//                if (item.IsExist)
//                {
//                    // 读取类声明节点
//                    var typeDeclarationSyntax = item.ClassDeclarationSyntax as TypeDeclarationSyntax;
//                    // 构建该类的名称
//                    var typeName = new StringBuilder().
//                        Append("partial ")
//                        .Append(typeDeclarationSyntax.Keyword.ValueText)
//                        .Append(" ")
//                        .Append(typeDeclarationSyntax.Identifier.ToString())
//                        .Append(typeDeclarationSyntax.TypeParameterList)
//                        .Append(" ")
//                        .Append(typeDeclarationSyntax.ConstraintClauses.ToString());
//                    item.SetTypeName(typeName.ToString());
//                    classWorkItem = item;
//                    return true;

//                }
//            }

//            classWorkItem = null;
//            return false;
//        }
//    }


    
//}
