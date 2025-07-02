// 文件：EntitySystemGenerator.cs
using EntJoy.SourceGenerator.Utils;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static SystemSyntaxReceiver;

[Generator]
public class EntitySystemGenerator : ISourceGenerator
{
    public void Initialize(GeneratorInitializationContext context)
    {
        context.RegisterForSyntaxNotifications(() => new SystemSyntaxReceiver());
    }

    public void Execute(GeneratorExecutionContext context)
    {
        if (context.SyntaxReceiver is not SystemSyntaxReceiver receiver)
            return;

        foreach (var systemInfo in receiver.CandidateSystems)
        {
            var structDecl = systemInfo.StructDeclaration;
            var model = context.Compilation.GetSemanticModel(structDecl.SyntaxTree);
            INamedTypeSymbol structSymbol = model.GetDeclaredSymbol(structDecl);

            if (structSymbol == null || !IsSystemInterface(structSymbol))
                continue;

            var source = GenerateBatchSystem(systemInfo, structSymbol);
            context.AddSource($"{structSymbol.Name}.g.cs", SourceText.From(source, Encoding.UTF8));
        }
    }

    private bool IsSystemInterface(INamedTypeSymbol symbol)
    {
        return symbol.Interfaces.Any(i =>
            i.Name == "ISystem" &&
            i.IsGenericType);
    }

    private string GenerateBatchSystem(SystemInfo systemInfo, INamedTypeSymbol symbol)
    {
        var structDecl = systemInfo.StructDeclaration;
        var executeMethod = systemInfo.ExecuteMethod;
        var componentParams = systemInfo.ComponentParameters;

        // 获取原结构体名称
        var originalName = symbol.Name;
        //var newName = originalName;

        // 获取包含类（如果存在）
        var containingClass = symbol.ContainingType;
        string namespaceName = NamespaceHelper.GetNamespacePath(symbol.ContainingNamespace);


        // 生成组件数组参数
        var arrayParams = componentParams
            .Select(p => $"{p.Type}* _Generated_{p.Name}")
            .Append("int _Generated_Count");

        bool HasUnsafeToken = false;
        //判断是否有unsafe 关键词
        foreach (var item in systemInfo.StructDeclaration.ChildNodesAndTokens())
        {
            if (item.IsNode && item.AsNode() is UnsafeStatementSyntax unsafeStatementSyntax)
            {
                HasUnsafeToken = true;
            }
        }


        // 构建该类的名称
        var typeName = new StringBuilder()
            .Append(HasUnsafeToken ? "" : "unsafe ")
            .Append("partial ")
            .Append(systemInfo.StructDeclaration.Keyword.ValueText)
            .Append(" ")
            .Append(systemInfo.StructDeclaration.Identifier.ToString())
            .Append(systemInfo.StructDeclaration.TypeParameterList)
            .Append(" ")
            .Append(systemInfo.StructDeclaration.ConstraintClauses.ToString());




        var codeWriter = new CodeWriter();

        //var source = new StringBuilder();


        codeWriter.AppendLine("using EntJoy;");
        codeWriter.AppendLine("using Godot;");

        // 添加命名空间
        if (!string.IsNullOrEmpty(namespaceName))
        {
            codeWriter.AppendLine($"namespace {namespaceName}");
            codeWriter.BeginBlock();
        }

        string fieldsString = string.Join("        // 复制所有字段\n        ", GetFields(structDecl));
        fieldsString = "";
        string indexCopy = $"// 创建组件数组引用\n            {string.Join("\n            ", componentParams.Select(p => $"var {p.Name}Array = _{p.Name};"))}";
        indexCopy = "";
        //类中的闭包
        if (containingClass != null)
        {
            var current = containingClass;
            while (current != null)
            {
                codeWriter.AppendLine($"partial class {current.Name}");
                codeWriter.BeginBlock();
                current = current.ContainingType;
            }
        }
        //主体
        codeWriter.AppendLine($@"
{typeName}
{{
    {fieldsString}
    public unsafe void _execute({string.Join(", ", arrayParams)})
    {{
        unchecked
        {{
            {indexCopy}
            
            for (int i = 0; i < _Generated_Count; i++)
            {{
                // 获取组件引用 (使用原始参数名称)
{string.Join("\n", componentParams.Select(p =>
                        {
                            //if (p.Type == "Entity") return "";
                            return $"                ref var {p.Name} = ref _Generated_{p.Name}[i];";

                        }))}
                
                // 原始执行逻辑
{GetOriginalBody(executeMethod)}
            }}
        }}
    }}
}}
");

        //关闭 类中的闭包
        if (containingClass != null)
        {
            var current = containingClass;
            while (current != null)
            {
                codeWriter.EndBlock();
                current = current.ContainingType;
            }
        }

        // 关闭命名空间
        if (!string.IsNullOrEmpty(namespaceName))
        {
            codeWriter.EndBlock();
        }

        return codeWriter.ToString();
    }

    private IEnumerable<string> GetFields(StructDeclarationSyntax structDecl)
    {
        return structDecl.Members
            .OfType<FieldDeclarationSyntax>()
            .Select(f => f.ToFullString().Trim());
    }

    private string GetOriginalBody(MethodDeclarationSyntax executeMethod)
    {
        if (executeMethod?.Body == null)
            return "{ /* 未找到原始方法体 */ }";

        string content = "";

        foreach (var item in executeMethod.Body.ChildNodes())
        {
            content += "                ";
            content += item.ToFullString().Trim() + "\n";
        }

        //return executeMethod.Body
        //    .ToFullString()
        //    .Trim()
        //    .Trim('{', '}')  // 移除外层大括号
        //    .Trim();
        return content;
    }
}

// 用于识别候选结构体的语法接收器
public class SystemSyntaxReceiver : ISyntaxReceiver
{
    //储存参数类型与名称
    public class ComponentParamInfo
    {
        public string Type { get; set; }
        public string Name { get; set; }
    }

    public class SystemInfo
    {
        public StructDeclarationSyntax StructDeclaration { get; set; }
        public MethodDeclarationSyntax ExecuteMethod { get; set; }
        public List<ComponentParamInfo> ComponentParameters { get; set; } = new();
    }

    public List<SystemInfo> CandidateSystems { get; } = new();

    public void OnVisitSyntaxNode(SyntaxNode syntaxNode)
    {
        // 检查嵌套在类中的结构体
        if (syntaxNode is StructDeclarationSyntax structDecl &&
           (structDecl.Parent is ClassDeclarationSyntax || !(structDecl.Parent is ClassDeclarationSyntax)))
        {
            // 检查是否实现了ISystem接口
            if (structDecl.BaseList?.Types.Any(t =>
                t.Type is GenericNameSyntax gns &&
                gns.Identifier.Text == "ISystem"
                ) == true)
            {
                // 查找Execute方法
                var executeMethod = structDecl.Members
                    .OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m => m.Identifier.Text == "Execute");

                if (executeMethod == null) return;

                // 解析方法参数（跳过第一个Entity参数）
                var parameters = executeMethod.ParameterList.Parameters
                    //.Skip(1)  // 跳过第一个Entity参数
                    .Where(p => p.Type != null)
                    .Select(p => new ComponentParamInfo
                    {
                        Type = p.Type!.ToString(),
                        Name = p.Identifier.Text
                    })
                    .ToList();

                if (parameters.Count == 0) return;

                CandidateSystems.Add(new SystemInfo
                {
                    StructDeclaration = structDecl,
                    ExecuteMethod = executeMethod,
                    ComponentParameters = parameters
                });
            }
        }
    }
}