using EntJoy.SourceGenerator.Utils;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace EntJoy.SourceGenerator.SystemArgs
{
    enum ExtendArgsType
    {
        None,
        SystemOrQuery,
        QueryBuilder
    }

    [Generator]
    internal sealed class SystemArgsGenerator : ISourceGenerator
    {
        public static int ArgsMinCount = 3;
        public void Initialize(GeneratorInitializationContext context)
        {
            //注册监听器
            context.RegisterForSyntaxNotifications(() => new SystemArgSyntaxReceiver());
        }


        public void Execute(GeneratorExecutionContext context)
        {
            var syntaxRecevier = context.SyntaxReceiver as SystemArgSyntaxReceiver;
            //if (syntaxRecevier.CandidatedArgs.Count == 0) return;

            var codeWriter = new CodeWriter();
            // 进行代码生成
            foreach (var argsCount in syntaxRecevier.CandidatedArgs)
            {
                if (argsCount >= ArgsMinCount)
                {
                    // ISystem的扩展

                    var sourceTextStr = AppendISystemBody(codeWriter, argsCount);
                    var sourceText1 = SourceText.From(sourceTextStr, Encoding.UTF8);
                    //Debugger.Launch();
                    context.AddSource("QueryLambda" + $"args-{argsCount}.g.cs", sourceText1);
                    codeWriter.Clear();

                    // World_Query的扩展
                    sourceTextStr = AppendWorldQueryBody(codeWriter, argsCount);
                    sourceText1 = SourceText.From(sourceTextStr, Encoding.UTF8);
                    //Debugger.Launch();
                    context.AddSource("World_Query" + $"args-{argsCount}.g.cs", sourceText1);
                    codeWriter.Clear();
                }
            }

            foreach (var argsCount in syntaxRecevier.CandidatedArgs2)
            {
                //Debugger.Launch();
                if (argsCount >= ArgsMinCount)
                {
                    var sourceTextStr = AppendComponentTypesBody(codeWriter, argsCount);
                    var sourceText1 = SourceText.From(sourceTextStr, Encoding.UTF8);
                    context.AddSource("ComponentTypes" + $"args-{argsCount}.g.cs", sourceText1);
                    codeWriter.Clear();

                    sourceTextStr = AppendQueryBuilderBody(codeWriter, argsCount);
                    sourceText1 = SourceText.From(sourceTextStr, Encoding.UTF8);
                    context.AddSource("QueryBuilder" + $"args-{argsCount}.g.cs", sourceText1);
                    codeWriter.Clear();
                }
            }
        }

        
        private static string AppendISystemBody(in CodeWriter codeWriter, int argsCount)
        {
            StringBuilder generalArgs = new StringBuilder();
            StringBuilder generalArgsLimit = new StringBuilder();
            StringBuilder generalparams = new StringBuilder();
            for (int i = 0; i < argsCount; i++)
            {
                generalArgs.Append("T" + i + (i == argsCount - 1 ? "" : ","));
                generalArgsLimit.Append($"    where T{i} : struct{(i == argsCount - 1 ? "" : "\n")}");
                generalparams.Append($"ref T{i} t{i}{(i == argsCount - 1 ? "" : ", ")}");
            }

            var SourceText =
$@"
namespace EntJoy
{{
    public interface ISystem<{generalArgs.ToString()}>
{generalArgsLimit.ToString()}
    {{
        void Execute(ref Entity entity, {generalparams});
    }}
}}
";
            codeWriter.AddLine(SourceText);



            return codeWriter.ToString();
        }

        private static string AppendWorldQueryBody(in CodeWriter codeWriter, int argsCount)
        {
            StringBuilder generalArgs = new StringBuilder();
            StringBuilder generalArgsLimit = new StringBuilder();
            StringBuilder generalparams = new StringBuilder();
            StringBuilder generalExp = new StringBuilder();
            StringBuilder generalExp2 = new StringBuilder();
            for (int i = 0; i < argsCount; i++)
            {
                generalArgs.Append("T" + i + (i == argsCount - 1 ? "" : ","));
                generalArgsLimit.Append($"            where T{i} : struct{(i == argsCount - 1 ? "" : "\n")}");
                generalparams.Append($"ref archQuery{i}[j]{(i == argsCount - 1 ? "" : ", ")}");
                generalExp.Append($"                    var archQuery{i} = arch.GetQueryStructArray<T{i}>().GetAllData();{(i == argsCount - 1 ? "" : "\n")}");
                generalExp2.Append($"                            ref var t{i} = ref archQuery{i}[j];{(i == argsCount - 1 ? "" : "\n")}");
            }

            var SourceText =
$@"
namespace EntJoy
{{
    public partial class World  // World类部分定义
    {{
        public void Query<{generalArgs.ToString()}>(in QueryBuilder builder, in ISystem<{generalArgs.ToString()}> lambda)
{generalArgsLimit.ToString()}
        {{
            unchecked
            {{
                for (int i = 0; i < archetypeCount; i++)  // 遍历所有原型
                {{
                    var arch = allArchetypes[i];
                    if (arch == null || !arch.IsMatch(builder)) continue;

                    var EntityQuery = arch.GetEntities();
{generalExp.ToString()}
                    int entityIndex = 0;  // 实体索引
                    int len = arch.EntityCount;
                    int limitCount = builder.LimitCount;
                    for (int j = 0; j < len; j++)
                    {{
                        if (limitCount != -1 && entityIndex > limitCount) break;
                        {{
                            ref var ent = ref EntityQuery[j];
                            lambda.Execute(ref ent, {generalparams});  // 执行回调
                            entityIndex++;
                        }}
                    }}
                }}
            }}

        }}
    }}
}}
";
            codeWriter.AddLine(SourceText);



            return codeWriter.ToString();
        }

        private static string AppendQueryBuilderBody(in CodeWriter codeWriter, int argsCount)
        {
            StringBuilder generalArgs = new StringBuilder();
            StringBuilder generalArgsLimit = new StringBuilder();
            for (int i = 0; i < argsCount; i++)
            {
                generalArgs.Append("T" + i + (i == argsCount - 1 ? "" : ","));
                generalArgsLimit.Append($"            where T{i} : struct{(i == argsCount - 1 ? "" : "\n")}");
            }

            var SourceText =
$@"
using System;
using System.Collections.Generic;
using System.Linq;
namespace EntJoy
{{
    public partial struct QueryBuilder
    {{
        public QueryBuilder WithAll<{generalArgs}>()
{generalArgsLimit}
        {{
            var preCompTypes = All == null ? new List<ComponentType>() : All.ToList();
            preCompTypes.AddRange(ComponentTypes<{generalArgs}>.Share);
            All = preCompTypes.ToArray();
            return this;
        }}
        public QueryBuilder WithAny<{generalArgs}>()
{generalArgsLimit}
        {{
            var preCompTypes = Any == null ? new List<ComponentType>() : Any.ToList();
            preCompTypes.AddRange(ComponentTypes<{generalArgs}>.Share);
            Any = preCompTypes.ToArray();
            return this;
        }}
        public QueryBuilder WithNone<{generalArgs}>()
{generalArgsLimit}
        {{
            var preCompTypes = None == null ? new List<ComponentType>() : None.ToList();
            preCompTypes.AddRange(ComponentTypes<{generalArgs}>.Share);
            None = preCompTypes.ToArray();
            return this;
        }}
    }}
}}
";
            codeWriter.AddLine(SourceText);



            return codeWriter.ToString();
        }

        private static string AppendComponentTypesBody(in CodeWriter codeWriter, int argsCount)
        {
            StringBuilder generalArgs = new StringBuilder();
            StringBuilder generalArgsLimit = new StringBuilder();
            StringBuilder generalArgsExp = new StringBuilder();
            for (int i = 0; i < argsCount; i++)
            {
                generalArgs.Append("T" + i + (i == argsCount - 1 ? "" : ","));
                generalArgsLimit.Append($"        where T{i} : struct{(i == argsCount - 1 ? "" : "\n")}");
                generalArgsExp.Append($"            typeof(T{i}){(i == argsCount - 1 ? "" : ",\n")}");
            }

            var SourceText =
$@"
namespace EntJoy
{{
    /// <summary>
    /// 组件类型容器（{argsCount}个组件）
    /// </summary>
    internal sealed class ComponentTypes<{generalArgs}>
{generalArgsLimit}
    {{
        /// <summary>共享的组件类型数组</summary>
        public static ComponentType[] Share = new ComponentType[]  // 共享组件类型数组
        {{
{generalArgsExp}
        }};
    }}
}}
";
            codeWriter.AddLine(SourceText);



            return codeWriter.ToString();
        }



    }


    // 筛选语法节点
    internal sealed class SystemArgSyntaxReceiver : ISyntaxReceiver
    {
        public HashSet<int> CandidatedArgs { get; } = new HashSet<int>(); //扩展ISytem和World_Query
        public HashSet<int> CandidatedArgs2 { get; } = new HashSet<int>(); //扩展QueryBuilder

        // 遍历 AST节点
        public void OnVisitSyntaxNode(SyntaxNode syntaxNode)
        {

            if (TryGetWorkItem(syntaxNode, out var ArgsCount,out ExtendArgsType Type))
            {
                if(Type == ExtendArgsType.SystemOrQuery)
                {
                    if (CandidatedArgs.Contains(ArgsCount))
                    {
                    }
                    else
                    {
                        CandidatedArgs.Add(ArgsCount);
                    }
                }
                if (Type == ExtendArgsType.QueryBuilder)
                {
                    if (CandidatedArgs2.Contains(ArgsCount))
                    {
                    }
                    else
                    {
                        CandidatedArgs2.Add(ArgsCount);
                    }
                }
            }
        }

        //语法节点接收器
        private static bool TryGetWorkItem(SyntaxNode syntaxNode, out int ArgsCount,out ExtendArgsType Type)
        {
            Type = ExtendArgsType.None;
            if (syntaxNode is StructDeclarationSyntax structDeclarationSyntax && structDeclarationSyntax.BaseList != null)
            {
                Type = ExtendArgsType.SystemOrQuery;
                var baseList = structDeclarationSyntax.BaseList;
                ArgsCount = 2;
                foreach (var baseType in baseList.Types)
                {
                    if (baseType is SimpleBaseTypeSyntax simpleBaseTypeSyntax)
                    {
                        var typeArgumentList = simpleBaseTypeSyntax.Type as GenericNameSyntax;

                        if (typeArgumentList != null && typeArgumentList.Identifier.Text == "ISystem")
                        {
                            // 检查类型参数是否为 T0 和 T1 这样的标识符
                            //var firstArgument = typeArgumentList.TypeArgumentList.Arguments[0];
                            //var secondArgument = typeArgumentList.TypeArgumentList.Arguments[1];

                            //if (firstArgument is IdentifierNameSyntax firstIdentifier && firstIdentifier.Identifier.Text == "T0" &&
                            //    secondArgument is IdentifierNameSyntax secondIdentifier && secondIdentifier.Identifier.Text == "T1") 
                            ArgsCount = typeArgumentList.TypeArgumentList.Arguments.Count;
                        }
                    }
                }


                if (ArgsCount >= SystemArgsGenerator.ArgsMinCount)
                {
                    return true;
                }
                ArgsCount = 0;
                
                return false;
            }

            // 情况2：检查是否调用了 QueryBuilder.WithAll<T0, T1,...>()
            if (syntaxNode is InvocationExpressionSyntax invocation)
            {
                if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                    memberAccess.Name is GenericNameSyntax genericMethod &&
                    (genericMethod.Identifier.Text == "WithAll" || genericMethod.Identifier.Text == "WithAny" || genericMethod.Identifier.Text == "WithNone"))
                {
                    
                    Type = ExtendArgsType.QueryBuilder;
                    ArgsCount = genericMethod.TypeArgumentList.Arguments.Count;
                    if (ArgsCount >= SystemArgsGenerator.ArgsMinCount)
                    {
                        return true;
                    }
                }
            }


            ArgsCount = 0;
            return false;

        }
    }

    //存储该系统的泛型参数
    internal sealed class SystemArgs
    {
        public List<string> Args;
    }

}


