# EntJoy.Mathematics

**中文** · [English](https://github.com/tianqiyuan520/EntJoy/blob/main/src/EntJoy.Mathematics/README.en.md)

数学类型与底层辅助，不依赖其他 EntJoy 包。

## 安装

```powershell
dotnet add package EntJoy.Mathematics
```

目标框架 `net8.0`，启用 `AllowUnsafeBlocks`；无需 C++ 工具链。使用上层模块时自动安装。

## 内容

| 类型 | 命名空间 | 用途 |
| --- | --- | --- |
| `float2` / `int2` / `uint2`、`math` | `EntJoy.Mathematics` | 向量与数学运算 |
| `BitMask`、`Hint` | `EntJoy` | 位图与分支提示 |
| `MemoryAddress` | `EntJoy.Debugger` | 调试时固定对象并查看地址 |

`Hint` 在纯 C# 中保留条件值，转译后表达分支提示。`MemoryAddress` 固定的对象需通过 `ReleasePinned` 释放固定状态。

四个包版本同步，当前发布目标为 win-x64。[发布与安装](https://github.com/tianqiyuan520/EntJoy#发布与安装) · [Collections](https://github.com/tianqiyuan520/EntJoy/blob/main/src/EntJoy.Collections/README.md)
