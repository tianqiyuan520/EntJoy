# EntJoy.Mathematics

**中文** | [English](#entjoymathematics-english)

EntJoy 的数学与底层辅助包：SIMD 友好的 POD 向量、静态数学函数、位掩码、编译期分支提示和调试用地址工具。它是四个包中的**叶子依赖**——不依赖任何其他 EntJoy 包。

## 内容

| 类型 | 命名空间 | 说明 |
| --- | --- | --- |
| `float2` / `int2` / `uint2` | `EntJoy` | SIMD 友好的 POD 向量：`IEquatable<T>`、blittable，可直接放进 `NativeArray<T>`。 |
| `math` | `EntJoy` | 静态数学函数集合（`public static partial class`）。 |
| `BitMask` | `EntJoy` | 基于 `ulong*` 位图的 `ref struct`，`this[int]` 带边界检查。生成的 `ChunkResult<…>` 用它从 chunk 的 enableable 位图构造。 |
| `Hint` | `EntJoy` | 分支预测提示：`Hint.Likely(cond)` / `Hint.Unlikely(cond)`。纯 C# 运行时是恒等函数；转译到 C++ 输出 `[[likely]]` / `[[unlikely]]`，转译到 ISPC 输出 `__builtin_expect`。 |
| `MemoryAddress` | `EntJoy.Debugger` | **仅调试用**：`GetAddress(obj)` 会 pin 住对象并返回其地址，`ReleasePinned` 前地址才有效。 |

## 引用

```xml
<PackageReference Include="EntJoy.Mathematics" Version="1.0.0" />
```

- `net8.0` + `AllowUnsafeBlocks`；**纯托管**，不需要 CMake / MSVC / ISPC。
- 依赖：无。
- 版本由 `src/Directory.Build.props` 的 `EntJoyVersion` 统一决定，四个包 lockstep 发布，当前仅 win-x64。

## 相关

- [`EntJoy.Collections`](https://github.com/tianqiyuan520/EntJoy/blob/main/src/EntJoy.Collections/README.md) —— 唯一直接依赖本包的库。
- 根 [README](https://github.com/tianqiyuan520/EntJoy/blob/main/README.md#通过-nuget-使用) 的「通过 NuGet 使用」。

# EntJoy.Mathematics (English)

[中文](#entjoymathematics) | **English**

EntJoy's math and low-level helper package: SIMD-friendly POD vectors, static math functions, bit masks, compile-time branch hints, and a debug-only address helper. It is the **leaf dependency** of the four packages — it depends on no other EntJoy package.

## Contents

| Type | Namespace | Notes |
| --- | --- | --- |
| `float2` / `int2` / `uint2` | `EntJoy` | SIMD-friendly POD vectors: `IEquatable<T>` and blittable, so they can be stored directly in `NativeArray<T>`. |
| `math` | `EntJoy` | Static math helpers (`public static partial class`). |
| `BitMask` | `EntJoy` | `ref struct` over a `ulong*` bitmap with a bounds-checked `this[int]`. Generated `ChunkResult<…>` uses it to wrap a chunk's enableable bitmaps. |
| `Hint` | `EntJoy` | Branch prediction hints: `Hint.Likely(cond)` / `Hint.Unlikely(cond)`. Identity functions in pure C#; `[[likely]]` / `[[unlikely]]` when transpiled to C++, `__builtin_expect` when transpiled to ISPC. |
| `MemoryAddress` | `EntJoy.Debugger` | **Debug output only**: `GetAddress(obj)` pins the object and returns its address, valid until `ReleasePinned`. |

## Reference it

```xml
<PackageReference Include="EntJoy.Mathematics" Version="1.0.0" />
```

- `net8.0` + `AllowUnsafeBlocks`; **pure managed**, no CMake / MSVC / ISPC required.
- Dependencies: none.
- The version comes from `EntJoyVersion` in `src/Directory.Build.props`; all four packages are released in lockstep and are win-x64 only.

## See also

- [`EntJoy.Collections`](https://github.com/tianqiyuan520/EntJoy/blob/main/src/EntJoy.Collections/README.md) — the only library that depends on this package.
- [Using NuGet Packages](https://github.com/tianqiyuan520/EntJoy/blob/main/README.md#using-nuget-packages) in the root README.
