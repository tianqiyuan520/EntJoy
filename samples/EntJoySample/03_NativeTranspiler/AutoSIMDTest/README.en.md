# AutoSIMD

[中文](README.md) · **English**

Compares C#, scalar C++, AutoSIMD, and ISPC. Eight groups cover arithmetic, math functions, reduction, branching, Gather, movement, stress, and edge cases, alongside Entity/Chunk checks.

## Run

The `EntJoySample.AutoSIMDTest.Program` entry is currently commented out. Enable its source and entry, then run from the repository root:

```powershell
dotnet build samples/EntJoySample/EntJoySample.csproj -c Release --no-incremental -p:ENTJOY_STARTUP=EntJoySample.AutoSIMDTest.Program
.\bin\EntJoySample.exe
```

Requires .NET 8, CMake, Visual Studio C++ tools, and ISPC. The current ISPC target requires AVX2. The entry runs Entity/Chunk checks; `BenchmarkSuite` organizes individual Case measurements. Check the source for each case.

## Usage notes

- `ENTJOY_SAMPLE_SET=movecompare` excludes this directory. Build with the default sample set.
- The generator validates AutoSIMD option/job combinations. A vectorization request does not guarantee SIMD; inspect diagnostics and machine code.
- [gen_llvm_ir.bat](gen_llvm_ir.bat) emits textual IR for the first five ISPC groups. Set its ISPC path to your local installation first.

[C++/AutoSIMD rules](../../../../docs/public/Cpp-AutoSIMD-Translation.md) · [ISPC rules](../../../../docs/public/ISPC-Translation.md) · [Diagnostics](../../../../docs/public/NativeTranspiler-Boundaries-and-Diagnostics.md) (Chinese)
