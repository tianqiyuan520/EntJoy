using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using EntJoy.ECS.SourceGenerator;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// IJobEntity 适配器把实体级 Execute 落到块级循环上，形态决定了热路径是否在优化码里跑：
    /// 循环必须正序推进，方法必须带 AggressiveOptimization，否则分层编译的 tier-0 会覆盖整个测量窗口。
    /// </summary>
    public class JobEntityAdapterShapeTests
    {
        [Fact]
        public void AdapterLoopIsForwardAndFullyOptimized()
        {
            const string source = @"
using EntJoy.ECS;

public struct AdapterShapePos { public float Value; }
public struct AdapterShapeVel { public float Value; }

public struct AdapterShapeJob : IJobEntity
{
    public float DeltaTime;
    public void Execute(ref AdapterShapePos position, in AdapterShapeVel velocity)
    {
        position.Value += velocity.Value * DeltaTime;
    }
}";
            string adapter = GenerateAdapter(source, "AdapterShapeJob");
            Assert.Contains("MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization", adapter);
            Assert.Contains("for (int __idx = 0; __idx < __count; __idx++)", adapter);
            Assert.DoesNotContain("__idx = __count - 1", adapter);
        }

        private static string GenerateAdapter(string source, string jobName)
        {
            var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
            var tree = CSharpSyntaxTree.ParseText(source, parseOptions);
            var references = new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(IJobEntity).Assembly.Location),
            };
            var compilation = CSharpCompilation.Create(
                "JobEntityAdapterShape",
                new[] { tree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                new[] { new IJobEntitySourceGenerator().AsSourceGenerator() },
                parseOptions: parseOptions);
            driver = driver.RunGenerators(compilation);
            var result = driver.GetRunResult();
            var generated = result.Results
                .SelectMany(r => r.GeneratedSources)
                .FirstOrDefault(s => s.HintName.Contains(jobName));
            Assert.True(generated.HintName != null,
                "没有生成适配器 ⇒ 断言是空跑：" + string.Join(", ",
                    result.Results.SelectMany(r => r.GeneratedSources).Select(s => s.HintName)));
            return generated.SourceText.ToString().Replace("\r\n", "\n");
        }
    }
}
