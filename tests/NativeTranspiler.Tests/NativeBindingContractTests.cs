using System;
using System.IO;
using System.Linq;
using EntJoy.Collections;
using EntJoy.JobSystem;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NativeTranspiler.Analyzer;
using Xunit;

namespace NativeTranspiler.Tests;

public class NativeBindingContractTests
{
    [Theory]
    [InlineData("IJob", "", "Values[0] = 7")]
    [InlineData("IJobFor", "int index", "Values[index] = 7")]
    [InlineData("IJobParallelFor", "int index", "Values[index] = 7")]
    [InlineData("IJobParallelForBatch", "int first, int count", "Values[first] = count")]
    public void ExpressionBodiedExecute_IsRejectedBeforeNativeEmission(string jobInterface, string parameters, string expression)
    {
        string source = $@"
using EntJoy.Collections; using EntJoy.JobSystem; using NativeTranspiler;
[NativeTranspile] public struct ArrowJob : {jobInterface} {{
    public NativeArray<int> Values;
    public void Execute({parameters}) => {expression};
}}";
        var result = GeneratorHarness.EmitFor(source);
        Assert.Contains(result.Diagnostics, d => d.Id == "NT034" && d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain("Schedule_ArrowJob", result.Bindings);
    }

    [Fact]
    public void ArrayJobBindings_PreservePublicSchedulingContracts()
    {
        const string source = @"
using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler;
[NativeTranspile] public struct Serial : IJob {
    public NativeArray<int> Values;
    public void Execute() { Values[0] = 7; }
}
[NativeTranspile] public struct For : IJobFor {
    public NativeArray<int> Values;
    public void Execute(int index) { Values[index] = index; }
}
[NativeTranspile] public struct Parallel : IJobParallelFor {
    public NativeArray<int> Values;
    public void Execute(int index) { Values[index] = index; }
}
[NativeTranspile] public struct Batch : IJobParallelForBatch {
    public NativeArray<int> Values;
    public void Execute(int startIndex, int count) {
        for (int i = startIndex; i < startIndex + count; i++) Values[i] = i;
    }
}
public static class Consumer {
    public static JobHandle Schedule(NativeArray<int> values, JobHandle dependency) {
        var a = new Serial { Values = values }.Schedule(dependency);
        var b = new For { Values = values }.Schedule(1031, 64, a);
        var c = new Parallel { Values = values }.Schedule(1031, 64, b, ClaimPolicy.Spread);
        return new Batch { Values = values }.Schedule(1031, 64, c, ClaimPolicy.Adjacent);
    }
}";
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
            .Split(Path.PathSeparator)
            .Concat(new[] { typeof(JobHandle).Assembly.Location, typeof(NativeArray<int>).Assembly.Location })
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var references = paths.Select(path => MetadataReference.CreateFromFile(path));
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var compilation = CSharpCompilation.Create("BindingConsumer",
            new[] { CSharpSyntaxTree.ParseText(source, parseOptions) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        string projectDir = Path.Combine(Path.GetTempPath(), "entjoy-nt-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectDir);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new NativeTranspilerGenerator().AsSourceGenerator() },
            parseOptions: parseOptions, optionsProvider: GeneratorHarness.CreateOptions(projectDir));
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var generated, out var diagnostics);
        var errors = diagnostics.Concat(generated.GetDiagnostics())
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(d => d.ToString())));
    }
}
