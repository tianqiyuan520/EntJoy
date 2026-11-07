# AutoSIMD

**中文** · [English](README.en.md)

比较 C#、标量 C++、AutoSIMD 与 ISPC。包含算术、数学函数、归约、分支、Gather、移动、压力与边界八组案例，以及 Entity / Chunk 验证。

## 运行

入口 `EntJoySample.AutoSIMDTest.Program` 当前以注释形式保存。启用对应源码和入口后，在仓库根目录执行：

```powershell
dotnet build samples/EntJoySample/EntJoySample.csproj -c Release --no-incremental -p:ENTJOY_STARTUP=EntJoySample.AutoSIMDTest.Program
.\bin\EntJoySample.exe
```

需要 .NET 8、CMake、Visual Studio C++ 工具链与 ISPC；当前 ISPC 目标要求 AVX2。默认入口调用 Entity / Chunk 验证；各 Case 的测量由 `BenchmarkSuite` 组织，内容以对应源码为准。

## 注意事项

- `ENTJOY_SAMPLE_SET=movecompare` 会排除此目录；使用默认样例集构建。
- AutoSIMD 的属性组合与 Job 形态受生成器校验，向量化请求不保证生成 SIMD；以诊断和机器码为准。
- [gen_llvm_ir.bat](gen_llvm_ir.bat) 用于生成前五组 ISPC 的文本 IR，运行前将脚本内 ISPC 路径改为本机位置。

[C++ / AutoSIMD 规则](../../../../docs/public/Cpp-AutoSIMD-Translation.md) · [ISPC 规则](../../../../docs/public/ISPC-Translation.md) · [诊断](../../../../docs/public/NativeTranspiler-Boundaries-and-Diagnostics.md)
