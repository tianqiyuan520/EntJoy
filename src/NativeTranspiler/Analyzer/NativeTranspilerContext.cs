using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;

namespace NativeTranspiler.Analyzer
{
    public class NativeTranspilerContext
    {
        public Compilation Compilation { get; }
        public AnalyzerConfigOptionsProvider Options { get; }
        public ImmutableArray<IMethodSymbol?> MethodSymbols { get; }
        public ImmutableArray<INamedTypeSymbol?> JobStructSymbols { get; }

        public NativeTranspilerContext(
            Compilation compilation,
            AnalyzerConfigOptionsProvider options,
            ImmutableArray<IMethodSymbol?> methodSymbols,
            ImmutableArray<INamedTypeSymbol?> jobStructSymbols)
        {
            Compilation = compilation;
            Options = options;
            MethodSymbols = methodSymbols;
            JobStructSymbols = jobStructSymbols;
        }

        public string GetProjectDirectory()
        {
            Options.GlobalOptions.TryGetValue("build_property.projectdir", out var projectDir);
            return projectDir ?? System.IO.Directory.GetCurrentDirectory();
        }
    }
}