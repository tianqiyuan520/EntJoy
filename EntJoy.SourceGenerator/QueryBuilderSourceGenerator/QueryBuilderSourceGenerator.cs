//using EntJoy.SourceGenerator.Utils;
//using Microsoft.CodeAnalysis;
//using Microsoft.CodeAnalysis.Text;
//using System.Collections.Generic;

//using System.Text;

//namespace EntJoy.SourceGenerator.QueryBuilderSourceGenerator
//{
//    [Generator]
//    internal sealed class QueryBuilderSourceGenerator : ISourceGenerator
//    {
//        public const string QueryBuilderAttributeName = "Querybuilder";


//        public void Initialize(GeneratorInitializationContext context)
//        {
//            //注册监听器
//            context.RegisterForSyntaxNotifications(() => new QueryBuilderSyntaxReceiver());
//            //context.RegisterForSyntaxNotifications(() => new SystemArgSyntaxReceiver());
//        }


//        public void Execute(GeneratorExecutionContext context)
//        {


//            #region 生成 attribute
//            string QueryBuilderAttributeSourceText = Def.Dom_Declarations +
//$@"
//using System;

//namespace {Def.Dom_Generateds}
//{{
//    [AttributeUsage(AttributeTargets.Class)]
//    public sealed class {QueryBuilderAttributeName}Attribute : Attribute
//    {{

//    }}
//}}
//";
//            //var modleName = context.Compilation.SourceModule.Name; //编译后的程序集名称
//            //if (modleName.StartsWith("Godot.")) return;

//            var sourceText0 = SourceText.From(QueryBuilderAttributeSourceText, Encoding.UTF8);
//            context.AddSource(QueryBuilderAttributeName + "Attribute.g.cs", sourceText0);
//            #endregion


//            var syntaxRecevier = context.SyntaxReceiver as QueryBuilderSyntaxReceiver;
//            if (syntaxRecevier.CandidatedWorkItems.Count == 0) return;

//            var codeWriter = new CodeWriter();
//            // 进行代码生成
//            foreach (var workItems in syntaxRecevier.CandidatedWorkItems.Values)
//            {
//                var workItem = workItems[0];
//                var sentenceModel = context.Compilation.GetSemanticModel(workItem.ClassDeclarationSyntax.SyntaxTree);

//                if (sentenceModel.GetDeclaredSymbol(workItem.ClassDeclarationSyntax) is INamedTypeSymbol typeSymbol && typeSymbol != null)
//                {
//                    //获取 class 名称
//                    string typeName = TypeDeclarationSyntaxHelper.WriteTypeName(sentenceModel, workItem.ClassDeclarationSyntax);
//                    // 获取命名空间
//                    string nameSpaceName = NamespaceHelper.GetNamespacePath(typeSymbol.ContainingNamespace);

//                    var sourceTextStr = AppendClassBody(codeWriter, sentenceModel, nameSpaceName, typeName, workItems);
//                    var sourceText1 = SourceText.From(sourceTextStr, Encoding.UTF8);
//                    //Debugger.Launch();
//                    context.AddSource(typeSymbol.Name + ".g.cs", sourceText1);
//                    codeWriter.Clear();
//                }
//            }
//        }

//        // 追加类体
//        private static string AppendClassBody(in CodeWriter codeWriter, in SemanticModel semanticModel, string nameSpaceName, string typeName, List<ClassWorkItem> WorkItems)
//        {
//            //说明该类为自动生成的
//            codeWriter.AddLine(Def.Dom_Declarations);
//            codeWriter.AddLine();
//            codeWriter.AddLine("using System;");
//            codeWriter.AddLine();
//            // 判断 namespace 是否存在
//            if (!string.IsNullOrEmpty(nameSpaceName))
//            {
//                codeWriter.AddLine($"namespace {nameSpaceName}");
//                codeWriter.BeginBlock();
//            }
//            codeWriter.AddLine(typeName);
//            codeWriter.BeginBlock();

//            //添加字段
//            foreach (var workItem in WorkItems)
//            {
//                AppendPrivateField(codeWriter, semanticModel, workItem);
//            }
//            codeWriter.EndBlock();
//            if (!string.IsNullOrEmpty(nameSpaceName))
//            {
//                codeWriter.EndBlock();
//            }


//            return codeWriter.ToString();
//        }

//        // 添加私有字段
//        private static void AppendPrivateField(in CodeWriter codeWriter, in SemanticModel semanticModel, ClassWorkItem workItem)
//        {
//            var className = workItem.ClassDeclarationSyntax.Identifier.ValueText;
//            var SourceText =
//$@"

//";
//            codeWriter.AddLine(SourceText);

//        }
//    }
//}
