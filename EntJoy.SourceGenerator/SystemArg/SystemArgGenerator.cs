using EntJoy.SourceGenerator.Utils;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Generic;
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
            context.AddSource("Test.cs", "//生成器已启动");
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

                    context.AddSource("World_Query" + $"args-{argsCount}.g.cs", sourceText1);
                    codeWriter.Clear();

                    // World_MultiQuery的扩展
                    sourceTextStr = AppendWorldMultiQueryBody(codeWriter, argsCount);
                    sourceText1 = SourceText.From(sourceTextStr, Encoding.UTF8);
                    context.AddSource("World_MultiQuery" + $"args-{argsCount}.g.cs", sourceText1);
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

        //构造 ISystem
        private static string AppendISystemBody(in CodeWriter codeWriter, int argsCount)
        {
            StringBuilder generalArgs = new StringBuilder();
            StringBuilder generalArgsLimit = new StringBuilder();
            StringBuilder generalparams = new StringBuilder();
            StringBuilder generalparamsArray = new StringBuilder();
            for (int i = 0; i < argsCount; i++)
            {
                generalArgs.Append("T" + i + (i == argsCount - 1 ? "" : ","));
                generalArgsLimit.Append($"    where T{i} : struct{(i == argsCount - 1 ? "" : "\n")}");
                generalparams.Append($"ref T{i} t{i}{(i == argsCount - 1 ? "" : ", ")}");
                generalparamsArray.Append($"T{i}* _t{i}{(i == argsCount - 1 ? "" : ", ")}");
            }

            var SourceText =
$@"
namespace EntJoy
{{
    public interface ISystem<{generalArgs.ToString()}> : GetArchetypeChunkID
{generalArgsLimit.ToString()}
    {{
        virtual unsafe void _execute(Entity* _entity, {generalparamsArray},int _Count, int LimitCount) {{}}        
        virtual void Execute(ref Entity entity, {generalparams}) {{}}
        virtual void Execute({generalparams}) {{}}
    }}
}}
";
            codeWriter.AppendLine(SourceText);



            return codeWriter.ToString();
        }
        //构造 WorldQuery
        private static string AppendWorldQueryBody(in CodeWriter codeWriter, int argsCount)
        {
            StringBuilder generalArgs = new StringBuilder();
            StringBuilder generalArgsLimit = new StringBuilder();
            StringBuilder generalparams = new StringBuilder();
            StringBuilder generalExp = new StringBuilder(); //表达式
            StringBuilder GeneralExp2_Index = new StringBuilder();
            generalExp.Append($"Entity* entities = (Entity*)chunk.GetEntityPointer().ToPointer();\n");

            for (int i = 0; i < argsCount; i++)
            {
                GeneralExp2_Index.Append($"                    int t{i}Index = archetype.GetComponentTypeIndex<T{i}>();\n");
                generalArgs.Append("T" + i + (i == argsCount - 1 ? "" : ","));
                generalArgsLimit.Append($"            where T{i} : struct{(i == argsCount - 1 ? "" : "\n")}");
                generalparams.Append($"components{i}{(i == argsCount - 1 ? "" : ", ")}");
                generalExp.Append($"                        T{i}* components{i} = (T{i}*)chunk.GetComponentArrayPointer(t{i}Index).ToPointer();\n");
            }

            var SourceText =
$@"
using System.Runtime.CompilerServices;
namespace EntJoy
{{
    public partial class World  // World类部分定义
    {{
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void Query<{generalArgs.ToString()}>(in QueryBuilder builder, in ISystem<{generalArgs.ToString()}> system)
{generalArgsLimit.ToString()}
        {{
            int entityCounter = 0; // 记录查询到的实体数量
            int limitCount = builder.LimitCount;
            unchecked
            {{
                for (int i = 0; i < archetypeCount; i++)
                {{
                    var archetype = allArchetypes[i];
                    if (archetype != null && archetype.IsMatch(builder))
                    {{
{GeneralExp2_Index.ToString()}
                        var chunks = archetype.GetChunks();
                        for (int j = 0; j < chunks.Count; j++)
                        {{
                            var chunk = chunks[j];
                            int count = chunk.EntityCount;
                            if (count == 0) continue;
                            system.GetArchetypeID(i);
                            system.GetChunkID(j);
                            {generalExp.ToString()}
                            {{
                                system._execute(entities, {generalparams}, count, limitCount - entityCounter);
                            }}
                            entityCounter += count;
                            if (limitCount != -1 && entityCounter >= limitCount) break;
                        }}
                    }}
                }}
            }}

        }}
    }}
}}
";
            codeWriter.AppendLine(SourceText);



            return codeWriter.ToString();
        }
        //构造 QueryBuilder
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
    partial struct QueryBuilder
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
            codeWriter.AppendLine(SourceText);



            return codeWriter.ToString();
        }


        //构造 World MultiQuery 多进程
        private static string AppendWorldMultiQueryBody(in CodeWriter codeWriter, int argsCount)
        {
            StringBuilder generalArgs = new StringBuilder();
            StringBuilder generalArgsLimit = new StringBuilder();
            StringBuilder generalparams = new StringBuilder();
            StringBuilder generalparams2 = new StringBuilder();
            StringBuilder generalExp = new StringBuilder(); //表达式
            StringBuilder GeneralExp2_Index = new StringBuilder();
            StringBuilder GeneralExp2_Index2 = new StringBuilder();
            generalExp.Append($"Entity* entities = (Entity*)chunk.GetEntityPointer().ToPointer();\n");

            for (int i = 0; i < argsCount; i++)
            {
                GeneralExp2_Index.Append($"                    int t{i}Index = archetype.GetComponentTypeIndex<T{i}>();\n");
                GeneralExp2_Index2.Append($"int t{i}Index{(i == argsCount - 1 ? "" : ", ")}");
                generalArgs.Append("T" + i + (i == argsCount - 1 ? "" : ","));
                generalArgsLimit.Append($"            where T{i} : struct{(i == argsCount - 1 ? "" : "\n")}");
                generalparams.Append($"components{i}{(i == argsCount - 1 ? "" : ", ")}");
                generalparams2.Append($"t{i}Index{(i == argsCount - 1 ? "" : ", ")}");
                generalExp.Append($"                        T{i}* components{i} = (T{i}*)chunk.GetComponentArrayPointer(t{i}Index).ToPointer();\n");
            }

            var SourceText =
$@"
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
namespace EntJoy
{{
    public partial class World  // World类部分定义
    {{
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void MultiQuery<{generalArgs}>(QueryBuilder builder, ISystem<{generalArgs}> system)
{generalArgsLimit}
        {{
            static void RunSystem(Chunk chunk, {GeneralExp2_Index2}, ISystem<{generalArgs}> system, int LimitCount, int ArchID, int ChunkID)
            {{
                int count = chunk.EntityCount;
                system.GetArchetypeID(ArchID);
                system.GetChunkID(ChunkID);
                {generalExp}
                    system._execute(entities, {generalparams}, count, LimitCount);
            }}

            unchecked
            {{
                int entityCounter = 0; 
                int limitCount = builder.LimitCount;

                for (int i = 0; i < archetypeCount; i++)
                {{
                    var archetype = allArchetypes[i];
                    if (archetype != null && archetype.IsMatch(builder))
                    {{
            {GeneralExp2_Index}
                    
                        List<ValueTask> tasks = new();

                        var chunks = archetype.GetChunks();
                        for (int j = 0; j < chunks.Count; j++)
                        {{
                            var chunk = chunks[j];
                            int count = chunk.EntityCount;
                            if (count == 0) continue;
                            int spareCount = limitCount - entityCounter;
                            int archetypeID = i;
                            int chunkID = j;
                            Task task = Task.Run(() =>
                            {{
                                RunSystem(chunk, {generalparams2}, system, spareCount, archetypeID, chunkID);
                            }}
                            );
                            tasks.Add(new ValueTask(task));
                            entityCounter += count;
                            if (limitCount != -1 && entityCounter >= limitCount) break;

                        }}
                        Task.WhenAll(tasks.Select(v => v.AsTask()));
                    }}
                }}
            }}
        }}
    }}
}}
";
            codeWriter.AppendLine(SourceText);



            return codeWriter.ToString();
        }




        //构造 ComponentType
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
            codeWriter.AppendLine(SourceText);



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

            if (TryGetWorkItem(syntaxNode, out var ArgsCount, out ExtendArgsType Type))
            {
                if (Type == ExtendArgsType.SystemOrQuery)
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
        private static bool TryGetWorkItem(SyntaxNode syntaxNode, out int ArgsCount, out ExtendArgsType Type)
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

}


