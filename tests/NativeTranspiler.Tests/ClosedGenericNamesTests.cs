using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace NativeTranspiler.Tests;

public class ClosedGenericNamesTests
{
    [Fact]
    public void DifferentValueTypesAndHelpers_KeepDistinctDefinitions()
    {
        var result = GeneratorHarness.EmitForJob(@"
using NativeTranspiler; using EntJoy.JobSystem;
namespace App {
public unsafe struct View<T> where T : unmanaged { public T* Data; }
public static unsafe class Access {
 public static T* At<T>(View<T> view, int index) where T : unmanaged { return view.Data + index; }
}
[NativeTranspile] public unsafe struct ReadJob : IJobParallelFor {
 public View<int> Ints; public View<long> Longs;
 public void Execute(int index) {
  *Access.At(Ints, index) = index;
  *Access.At(Longs, index) = index;
 }
}}");
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var types = Directory.GetFiles(result.OutputDir, "App_View*.h");
        var helpers = Directory.GetFiles(result.OutputDir, "SharpNative_App_Access_At*.h");
        Assert.Equal(2, types.Length);
        Assert.Equal(2, helpers.Length);
        var bodies = types.Select(File.ReadAllText).ToArray();
        Assert.Single(bodies.Where(s => s.Contains("int* Data;")));
        Assert.Single(bodies.Where(s => s.Contains("long long* Data;")));
        var functions = helpers.Select(File.ReadAllText).ToArray();
        Assert.Single(functions.Where(s => s.Contains("int* SharpNative_")));
        Assert.Single(functions.Where(s => s.Contains("long long* SharpNative_")));
        foreach (var type in types)
            Assert.Contains(Path.GetFileNameWithoutExtension(type).Substring("App_".Length), result.Cpp);
    }
}
