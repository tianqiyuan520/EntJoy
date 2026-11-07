# NativeTranspiler.Tests

[中文](README.md) · **English**

xUnit/.NET 8 tests that run the Roslyn generator in-process and check C++/ISPC text, C# bindings, and diagnostics. [NativeTranspilerFixture](../../tools/NativeTranspilerFixture) covers native compilation and execution.

## Run

From the repository root, without a C++/ISPC toolchain:

```powershell
dotnet test tests/NativeTranspiler.Tests/NativeTranspiler.Tests.csproj -c Release
```

Select a group with `--filter "FullyQualifiedName~NT01_ElementStore"`.

## Writing tests

Use `GeneratorHarness.EmitForJob(source)`. It requires a recognized job, preventing empty output from passing unnoticed. Use `EmitFor` when testing input without jobs.

`EmitResult.Cpp`, `Bindings`, and `Diagnostics` contain native text, C# bindings, and diagnostics.

- Mark the test struct with `[NativeTranspile]`. The generator injects this attribute; use it and its namespace in test source.
- Declare stub interfaces and containers with the names and namespaces in `GeneratorHarness.Stubs`.
- Cover arithmetic and control flow, array access, SIMD tails, native bindings, diagnostics, and build arguments.

## Output snapshots

Set an output directory, then run the snapshot test:

```powershell
$env:ENTJOY_NT_SNAP_DIR = Join-Path $env:TEMP 'entjoy-emit-snapshot'
dotnet test tests/NativeTranspiler.Tests/NativeTranspiler.Tests.csproj -c Release --filter "FullyQualifiedName~EmitSnapshot"
Remove-Item Env:ENTJOY_NT_SNAP_DIR
```

The snapshot test skips when the directory is unset. Compare outputs from the relevant builds; compilation and execution checks remain necessary for end-to-end correctness.

[Translation boundaries](../../docs/public/NativeTranspiler-Boundaries-and-Diagnostics.md) (Chinese) · [CI](../../.github/workflows/jobsystem-ci.yml)
