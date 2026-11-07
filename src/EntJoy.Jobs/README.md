# EntJoy.Jobs

**中文** · [English](https://github.com/tianqiyuan520/EntJoy/blob/main/src/EntJoy.Jobs/README.en.md)

独立 JobSystem，提供托管 / 原生调度器、JobHandle 依赖，以及 C# → C++ / ISPC 的转译与构建工具。

## 安装

```powershell
dotnet add package EntJoy.Jobs
```

目标框架 `net8.0`，启用 `AllowUnsafeBlocks`。只写数组 Job 时无需引用 ECS；使用 ECS 时安装 EntJoy.ECS 即可。

## 任务与执行

| 接口 | 用途 |
| --- | --- |
| `IJob` | 单个任务 |
| `IJobFor` | 单任务内的索引循环 |
| `IJobParallelFor` | 并行处理独立索引 |
| `IJobParallelForBatch` | 按连续批次处理 |

使用 `JobScheduler.Initialize()` 初始化，通过 `Schedule` 提交、`JobHandle` 表达依赖、`Complete()` 等待，最后调用 `JobScheduler.Shutdown()`。调用线程可协助执行。

普通 C# Job 优先走原生调度器，原生运行时不可用时回退托管实现。转译 Job 需要原生运行时；Job 结构按值复制，托管引用的线程安全由调用方保证。

## 原生工具链

包内提供 win-x64 的 `NativeDll.dll`、链接套件、分析器与 MSBuild 接线。普通 C# Job 无需本地编译工具；`[NativeTranspile]` 需要 CMake + MSVC 或 ClangCL，ISPC 后端还需 Intel ISPC。生成的 `NativeTranspiled.dll` 与 `NativeDll.dll` 需一同部署。

四个包版本同步。[发布与安装](https://github.com/tianqiyuan520/EntJoy#发布与安装) · [Job 示例](https://github.com/tianqiyuan520/EntJoy#job-示例) · [转译指南](https://github.com/tianqiyuan520/EntJoy/blob/main/docs/public/Native-Jobs-Guide.md) · [运行时契约](https://github.com/tianqiyuan520/EntJoy/blob/main/docs/public/Runtime-Contracts-and-Known-Limitations.md)
