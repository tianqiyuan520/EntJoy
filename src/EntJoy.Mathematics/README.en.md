# EntJoy.Mathematics

[中文](https://github.com/tianqiyuan520/EntJoy/blob/main/src/EntJoy.Mathematics/README.md) · **English**

Math types and utilities with no other EntJoy package dependencies.

## Install

```powershell
dotnet add package EntJoy.Mathematics
```

Target `net8.0` and enable `AllowUnsafeBlocks`. No C++ toolchain is needed. Higher-level modules install it automatically.

## Contents

| Types | Namespace | Purpose |
| --- | --- | --- |
| `float2` / `int2` / `uint2`, `math` | `EntJoy.Mathematics` | Vectors and math |
| `BitMask`, `Hint` | `EntJoy` | Bitmaps and branch hints |
| `MemoryAddress` | `EntJoy.Debugger` | Pin objects and inspect addresses for debugging |

`Hint` preserves the condition in C# and emits branch hints when transpiled. Release objects pinned by `MemoryAddress` with `ReleasePinned`.

All four packages share a version; the current release target is win-x64. [Releases](https://github.com/tianqiyuan520/EntJoy/blob/main/README.en.md#releases-and-installation) · [Collections](https://github.com/tianqiyuan520/EntJoy/blob/main/src/EntJoy.Collections/README.en.md)
