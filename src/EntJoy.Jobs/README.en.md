# EntJoy.Jobs

[中文](https://github.com/tianqiyuan520/EntJoy/blob/main/src/EntJoy.Jobs/README.md) · **English**

Independent JobSystem with Managed/native schedulers, JobHandle dependencies, and C# → C++/ISPC transpilation and build tools.

## Install

```powershell
dotnet add package EntJoy.Jobs
```

Target `net8.0` and enable `AllowUnsafeBlocks`. Array jobs need no ECS reference. ECS applications can install EntJoy.ECS instead.

## Jobs and execution

| Interface | Purpose |
| --- | --- |
| `IJob` | One task |
| `IJobFor` | Indexed loop inside one task |
| `IJobParallelFor` | Parallel independent indices |
| `IJobParallelForBatch` | Contiguous batches |

Initialize with `JobScheduler.Initialize()`, submit with `Schedule`, express dependencies with `JobHandle`, wait with `Complete()`, and finish with `JobScheduler.Shutdown()`. The calling thread can assist execution.

Ordinary C# jobs prefer the native scheduler and fall back to Managed when unavailable. Transpiled jobs require the native runtime. Job structs are copied by value; callers ensure thread safety for managed references.

## Native toolchain

The package includes win-x64 `NativeDll.dll`, link assets, analyzers, and MSBuild integration. Ordinary C# jobs need no local compiler. `[NativeTranspile]` needs CMake and MSVC or ClangCL; ISPC also requires Intel ISPC. Deploy generated `NativeTranspiled.dll` alongside `NativeDll.dll`.

All four packages share a version. [Releases](https://github.com/tianqiyuan520/EntJoy/blob/main/README.en.md#releases-and-installation) · [Example](https://github.com/tianqiyuan520/EntJoy/blob/main/README.en.md#job-example) · [Transpilation guide](https://github.com/tianqiyuan520/EntJoy/blob/main/docs/public/Native-Jobs-Guide.md) · [Runtime contracts](https://github.com/tianqiyuan520/EntJoy/blob/main/docs/public/Runtime-Contracts-and-Known-Limitations.md) (guides in Chinese)
