# NativeTranspiler.Tests

**中文** · [English](README.en.md)

xUnit / .NET 8 测试。进程内运行 Roslyn 生成器，检查 C++ / ISPC 文本、C# 绑定和诊断；原生编译与运行由 [NativeTranspilerFixture](../../tools/NativeTranspilerFixture) 验证。

## 运行

在仓库根目录执行，无需 C++ / ISPC 工具链：

```powershell
dotnet test tests/NativeTranspiler.Tests/NativeTranspiler.Tests.csproj -c Release
```

单组测试追加 `--filter "FullyQualifiedName~NT01_ElementStore"`。

## 编写测试

使用 `GeneratorHarness.EmitForJob(source)`；它要求识别到 Job，避免空产物误通过。需要测试无 Job 输入时使用 `EmitFor`。

`EmitResult.Cpp`、`Bindings`、`Diagnostics` 分别提供原生文本、C# 绑定和诊断。

- 被测 struct 标记 `[NativeTranspile]`；属性由生成器注入，替身源码使用该属性并引用对应命名空间。
- 替身接口与容器按 `GeneratorHarness.Stubs` 的命名空间和名称声明。
- 断言覆盖运算与控制流语义、数组访问、SIMD 尾部、原生绑定、诊断和构建参数。

## 发射快照

设置输出目录后运行快照测试：

```powershell
$env:ENTJOY_NT_SNAP_DIR = Join-Path $env:TEMP 'entjoy-emit-snapshot'
dotnet test tests/NativeTranspiler.Tests/NativeTranspiler.Tests.csproj -c Release --filter "FullyQualifiedName~EmitSnapshot"
Remove-Item Env:ENTJOY_NT_SNAP_DIR
```

未设置目录时快照用例跳过。使用对应构建的产物比较生成文本；端到端正确性仍以编译和运行断言为准。

[转译边界与诊断](../../docs/public/NativeTranspiler-Boundaries-and-Diagnostics.md) · [CI](../../.github/workflows/jobsystem-ci.yml)
