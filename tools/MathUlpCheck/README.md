# MathUlpCheck

量 `Fastest`（`SIMD_MATH_PRECISION=1`）档内联多项式的误差。参考值用 double 计算后按 round-to-nearest 折回 float。

两个口径：

- `absULP = |out - ref| / 2^-24`：以 float 在 1.0 处的 ulp 为单位。`sin`/`cos` 的结果绝对值 ≤ 1，这一口径即绝对精度。
- `relULP = (|out - ref| / |ref|) / 2^-24`：相对精度。结果接近 0 时会爆炸，只统计 `|ref| ≥ 1e-3` 的样本。

```powershell
cmake -S tools/MathUlpCheck -B tools/MathUlpCheck/build -G "Visual Studio 17 2022" -T ClangCL -A x64
cmake --build tools/MathUlpCheck/build --config Release
tools/MathUlpCheck/build/Release/MathUlpCheck.exe
```

工具链参数与 EntJoy 原生产物一致：AVX2、`NSIMD_WIDTH=8`、无 fast-math。改多项式系数或换精度档后重跑本工具，再比对下表。
