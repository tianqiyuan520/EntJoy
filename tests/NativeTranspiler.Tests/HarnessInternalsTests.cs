using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using Xunit.Abstractions;

namespace NativeTranspiler.Tests
{
    public class HarnessInternalsTests
    {
        private readonly ITestOutputHelper _out;
        public HarnessInternalsTests(ITestOutputHelper output) => _out = output;

        private static void Dump(string name, string text)
            => File.WriteAllText(Path.Combine(Path.GetTempPath(), name), text);

        [Fact]
        public void StubCompilation_HasNoErrors()
        {
            var (compilation, _, _, _) = GeneratorHarness.RawRun(@"
using EntJoy.Collections;
public struct ProbeStruct { public NativeArray<float> F; }
");
            var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            Dump("entjoy-probe-stubs.txt", string.Join("\n", errors.Select(e => e.Id + " " + e.GetMessage() + " @ " + e.Location)));
            Assert.Empty(errors);
        }

        /// <summary>post-init 输出是否对本生成器自己的 RegisterSourceOutput 可见。</summary>
        [Fact]
        public void PostInitOutputVisibility()
        {
            var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
            var compilation = CSharpCompilation.Create(
                "PostInitProbe",
                new[] { CSharpSyntaxTree.ParseText("namespace ProbeNs { [System.Serializable] public struct S { public int F; } } public class X { }", parseOptions) },
                new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var driver = CSharpGeneratorDriver.Create(new[] { new PostInitProbeGenerator().AsSourceGenerator() }, null, parseOptions);
            var run = driver.RunGenerators(compilation).GetRunResult();
            Dump("entjoy-probe-postinit.txt",
                "diags=" + string.Join(" | ", run.Diagnostics.Select(d => d.Id + ":" + d.GetMessage()))
                + "\ntrees=" + string.Join(", ", run.GeneratedTrees.Select(t => t.FilePath))
                + "\nresult=" + string.Join(" || ", run.GeneratedTrees.Select(t => t.GetText().ToString().Trim())));
        }

        private sealed class PostInitProbeGenerator : IIncrementalGenerator
        {
            public void Initialize(IncrementalGeneratorInitializationContext context)
            {
                context.RegisterPostInitializationOutput(ctx =>
                    ctx.AddSource("Probe.PostInit.g.cs", "namespace ProbeNs { public class PostInitType { } }"));
                var comp = context.CompilationProvider;
                context.RegisterSourceOutput(comp, (spc, c) =>
                {
                    var t = c.GetTypeByMetadataName("ProbeNs.PostInitType");
                    spc.AddSource("Probe.Result.g.cs", $"// found={(t != null)}");
                });

                // ─── SyntaxProvider 里能不能看到 post-init 类型 + 属性 ───
                var structs = context.SyntaxProvider.CreateSyntaxProvider(
                    predicate: (n, _) => n is StructDeclarationSyntax s && s.AttributeLists.Count > 0,
                    transform: (ctx, _) =>
                    {
                        var decl = (StructDeclarationSyntax)ctx.Node;
                        var sym = ctx.SemanticModel.GetDeclaredSymbol(decl);
                        var attr = ctx.SemanticModel.Compilation.GetTypeByMetadataName("ProbeNs.PostInitType");
                        int attrCount = sym?.GetAttributes().Length ?? -1;
                        return $"{decl.Identifier.Text}|declaredSymbol={(sym != null)}|postInitVisible={(attr != null)}|attrs={attrCount}";
                    }).Collect();

                context.RegisterSourceOutput(structs, (spc, arr) =>
                    spc.AddSource("Probe.SyntaxProvider.g.cs", "// " + string.Join(" ;; ", arr)));
            }
        }
    }
}
