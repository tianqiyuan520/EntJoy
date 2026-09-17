# EntJoy.Jobs

**中文** | [English](#entjoyjobs-english)

EntJoy 的 JobSystem 与 NativeTranspiler 转译设施。提供托管与原生两套调度器、`JobHandle` 依赖模型、四个数组类 Job 接口，并把「把 C# Job 转译成 C++/ISPC 并在构建期本地编译」所需的**分析器、MSBuild 任务、预编译原生运行时与链接套件**一并带到消费方。

**不引用 `EntJoy.ECS` 也可以用它**：只写数组类 Job（`IJob` / `IJobFor` / `IJobParallelFor` / `IJobParallelForBatch`）的项目只需要这一个包。

## 内容

### Job 接口与调度（命名空间 `EntJoy.JobSystem`）

| 类型 | 说明 |
| --- | --- |
| `IJob` / `IJobFor` / `IJobParallelFor` / `IJobParallelForBatch` | 四个数组类 Job 接口：`Execute()` / `Execute(int index)` / `Execute(int startIndex, int count)`。 |
| `JobScheduler` | 托管侧统一入口：`Initialize(numThreads = 0)` / `Shutdown()`、`Schedule` / `ScheduleFor` / `ScheduleParallelFor` / `ScheduleBatch`（返回 `JobHandle`），以及 `WorkerCount` / `IsNative` / `PrewakeWorkersOnce()` / `LaunchDebuggerGUI()`。原生不可用时自动回退托管实现。 |
| `JobExtensions` | `Schedule` / `ScheduleBatch` 扩展方法，以及同步执行的 `Run(...)` 重载。 |
| `JobHandle` / `NativeJobHandle` | 依赖句柄与 `Complete()`。 |
| `NativeJobScheduler` | 原生调度器直连入口：`Schedule*` 返回 `NativeJobHandle`；调优开关 `TilesPerWorker` / `GuidedEnabled` / `GuidedK` / `GuidedFloor` / `JobCostCacheEnabled`；诊断类型 `NativeJobSystemStats` / `NativeTraceEvent` / `NativeTraceEventType`；直接调用记账 `BeginDirectCall` / `EndDirectCall` / `RecordDirectCall`。 |
| `ManagedJobScheduler`（`EntJoy.JobSystem.Managed`） | 纯托管回退调度器，附 `ManagedJobHandle` / `ManagedCompletion`。 |
| `BatchScope` / `ImplicitBatch` | 批处理作用域与隐式批开关。 |
| `JobProfiler` | Job 统计与聚合（`ProfilerEntry` / `AggregatedJobInfo` / `WorkerJobEntry` / `WorkerJobDetail`）。 |
| `ThreadCounter` / `CSharpPhaseDiag` | 线程计数与 C# 阶段诊断。 |

### 包内附着物（消费方不需要 NativeDll 源码）

| 路径 | 内容 |
| --- | --- |
| `runtimes/win-x64/native/NativeDll.dll` | 预编译原生运行时（原生调度器、Profiler、原生容器）。纯托管消费者由它直接复制到输出目录即可运行。 |
| `build/native/` | 链接套件：`NativeDll.lib` 导入库、`tasksys.cpp`、全部头文件。写 `[NativeTranspile]` Job 时链接它。 |
| `tools/` | `NativeTranspiler.dll`（源生成器/分析器）与 `NativeTranspiler.Tasks.dll`（调用 CMake 的 MSBuild 任务）。 |
| `buildTransitive/` | `EntJoy.Jobs.props` / `.targets`：自动接线分析器、编译目标与 `NativeDll.dll` 复制，无需手写 MSBuild。 |

### 工具链前提

- **只写托管 Job**：装包即用，**不需要** CMake / MSVC / ISPC。
- **写 `[NativeTranspile]` native Job**：需要 **CMake + MSVC（或 ClangCL）**，ISPC 可选（缺失时自动跳过 ISPC 后端）。构建期生成 C++/ISPC 并编译出 `NativeTranspiled.dll`。
- 生成器带自校验不变量：编译单元含 `IJobChunk` / `IJobEntity` 却未引用 `EntJoy.ECS` 报 **NT029**（Error）；未引用 ECS 但生成物出现 ECS 符号报 **NT030**（Warning）。

怎么写、怎么配的完整说明见 [`docs/public/Native-Jobs-Guide.md`](../../docs/public/Native-Jobs-Guide.md)。

## 引用

```xml
<PackageReference Include="EntJoy.Jobs" Version="1.0.0" />
```

- `net8.0` + `AllowUnsafeBlocks`；命名空间 `EntJoy.JobSystem`（托管回退在 `EntJoy.JobSystem.Managed`）。
- 依赖：`EntJoy.Collections`。
- 版本由 `src/Directory.Build.props` 的 `EntJoyVersion` 统一决定，四个包 lockstep 发布，当前仅 win-x64。

## 相关

- [`EntJoy.Collections`](../EntJoy.Collections/README.md) —— 本包的依赖。
- [`EntJoy.ECS`](../EntJoy.ECS/README.md) —— 需要 ECS 时改装入口包（它已依赖本包）。
- 根 [README](../../README.md#通过-nuget-使用) 的「通过 NuGet 使用」。

# EntJoy.Jobs (English)

[中文](#entjoyjobs) | **English**

EntJoy's JobSystem and NativeTranspiler infrastructure. It ships both the managed and native schedulers, the `JobHandle` dependency model, the four array-shaped job interfaces, and everything a consumer needs to transpile C# jobs to C++/ISPC and compile them locally: the **analyzer, the MSBuild task, the prebuilt native runtime, and the native link kit**.

**It works without `EntJoy.ECS`**: a project that only writes array-shaped jobs (`IJob` / `IJobFor` / `IJobParallelFor` / `IJobParallelForBatch`) needs this package alone.

## Contents

### Job interfaces and scheduling (namespace `EntJoy.JobSystem`)

| Type | Notes |
| --- | --- |
| `IJob` / `IJobFor` / `IJobParallelFor` / `IJobParallelForBatch` | The four array-shaped job interfaces: `Execute()` / `Execute(int index)` / `Execute(int startIndex, int count)`. |
| `JobScheduler` | The unified managed entry point: `Initialize(numThreads = 0)` / `Shutdown()`, `Schedule` / `ScheduleFor` / `ScheduleParallelFor` / `ScheduleBatch` (returning `JobHandle`), plus `WorkerCount` / `IsNative` / `PrewakeWorkersOnce()` / `LaunchDebuggerGUI()`. Falls back to the managed implementation when native is unavailable. |
| `JobExtensions` | `Schedule` / `ScheduleBatch` extension methods and synchronous `Run(...)` overloads. |
| `JobHandle` / `NativeJobHandle` | Dependency handles and `Complete()`. |
| `NativeJobScheduler` | Direct entry point to the native scheduler: `Schedule*` returning `NativeJobHandle`; tuning switches `TilesPerWorker` / `GuidedEnabled` / `GuidedK` / `GuidedFloor` / `JobCostCacheEnabled`; diagnostics `NativeJobSystemStats` / `NativeTraceEvent` / `NativeTraceEventType`; direct-call accounting via `BeginDirectCall` / `EndDirectCall` / `RecordDirectCall`. |
| `ManagedJobScheduler` (`EntJoy.JobSystem.Managed`) | Pure managed fallback scheduler with `ManagedJobHandle` / `ManagedCompletion`. |
| `BatchScope` / `ImplicitBatch` | Batch scope and the implicit-batch toggle. |
| `JobProfiler` | Job statistics and aggregation (`ProfilerEntry` / `AggregatedJobInfo` / `WorkerJobEntry` / `WorkerJobDetail`). |
| `ThreadCounter` / `CSharpPhaseDiag` | Thread counting and C#-phase diagnostics. |

### What else the package carries (consumers need no NativeDll sources)

| Path | Contents |
| --- | --- |
| `runtimes/win-x64/native/NativeDll.dll` | The prebuilt native runtime (native scheduler, profiler, native containers). Managed-only consumers just get it copied to their output directory. |
| `build/native/` | The link kit: `NativeDll.lib` import library, `tasksys.cpp`, and all headers. Link against it when you write `[NativeTranspile]` jobs. |
| `tools/` | `NativeTranspiler.dll` (source generator/analyzer) and `NativeTranspiler.Tasks.dll` (the MSBuild task that drives CMake). |
| `buildTransitive/` | `EntJoy.Jobs.props` / `.targets`: wires up the analyzer, the compile targets, and the `NativeDll.dll` copy — no hand-written MSBuild needed. |

### Toolchain requirements

- **Managed jobs only**: works out of the box, **no** CMake / MSVC / ISPC required.
- **`[NativeTranspile]` native jobs**: requires **CMake + MSVC (or ClangCL)**; ISPC is optional (the ISPC backend is skipped when missing). The build generates the C++/ISPC and compiles `NativeTranspiled.dll`.
- The generator self-checks an invariant: a compilation unit containing `IJobChunk` / `IJobEntity` without referencing `EntJoy.ECS` reports **NT029** (error); generated output referencing ECS symbols without that reference reports **NT030** (warning).

The full how-to-write / how-to-configure guide lives in [`docs/public/Native-Jobs-Guide.md`](../../docs/public/Native-Jobs-Guide.md) (Chinese).

## Reference it

```xml
<PackageReference Include="EntJoy.Jobs" Version="1.0.0" />
```

- `net8.0` + `AllowUnsafeBlocks`; namespace `EntJoy.JobSystem` (managed fallback in `EntJoy.JobSystem.Managed`).
- Dependencies: `EntJoy.Collections`.
- The version comes from `EntJoyVersion` in `src/Directory.Build.props`; all four packages are released in lockstep and are win-x64 only.

## See also

- [`EntJoy.Collections`](../EntJoy.Collections/README.md) — this package's dependency.
- [`EntJoy.ECS`](../EntJoy.ECS/README.md) — the entry package when you need ECS (it already depends on this one).
- [Using NuGet Packages](../../README.md#using-nuget-packages) in the root README.
