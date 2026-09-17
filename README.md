# EntJoy

[![](https://img.shields.io/badge/powered_by-dsh-4D6BFE?style=flat-square&logo=deepseek&logoColor=white)](https://github.com/deepseek-ai/deepseek-harness)

[![Ask DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/tianqiyuan520/EntJoy)

**中文** | [English](#entjoy-english)

> **项目定位：** EntJoy 是一个 **Headless GameFramework**——提供 Archetype ECS、并行 JobSystem、NativeTranspiler（C#→C++/ISPC）和跨平台原生运行时，不内置渲染器。渲染层由上层应用按需接入（Godot、Unity、自研引擎均可）。
>
> **Disclaimer:** EntJoy is not affiliated with, endorsed by, or sponsored by Unity Technologies.

EntJoy 是一个由 **C#、C++ 和 ISPC** 编写的 Archetype ECS 与 JobSystem 技术栈。它借鉴 Unity DOTS 的数据导向设计：实体数据按 Archetype 和 Chunk 连续存储，工作通过统一 JobSystem 并行调度，同一份 C# Job 还可以由 Source Generator 转译为 C++ 或 ISPC 后端。

项目目前提供：

- Archetype/Chunk ECS、Entity、Component、Query 和 Enableable Component。
- `IJob`、`IJobFor`、`IJobParallelFor`、`IJobParallelForBatch`、`IJobChunk` 和 `IJobEntity`。
- `JobHandle` 依赖、组合依赖和 `Complete()` 协作执行。
- C#、C++、ISPC 共用的原生工作线程调度器。
- 将受支持的 C# Job 自动生成 C++/ISPC 代码的 NativeTranspiler。**它也可用于纯 JobSystem 项目**：只引用 `EntJoy.Collections` / `EntJoy.Jobs`（不引用 ECS）时，数组类 Job（`IJob`/`IJobFor`/`IJobParallelFor`/`IJobParallelForBatch`）同样可以转译为 native 并运行；`IJobChunk`/`IJobEntity`/`SendEvent` 属于 ECS 能力，需引用 `EntJoy.ECS`。
- `NativeArray<T>`、`NativeList<T>`、原子操作和数学类型等底层工具。
- 覆盖功能、正确性和性能对比的 [EntJoySample](samples/EntJoySample)。

> 当前仓库仅适配并验证了 **Windows x64、.NET 8、MSVC 和 Intel ISPC** 工具链。GCC、G++ 和 Clang 暂不属于当前支持范围。API 仍在演进。支持两种消费方式：**NuGet 包**（`EntJoy.ECS` / `EntJoy.Jobs` / `EntJoy.Collections` / `EntJoy.Mathematics`，仅 win-x64）与**源码项目引用**；发布由 `v*` tag 触发，见[通过 NuGet 使用](#通过-nuget-使用)。

## 通过 NuGet 使用

4 个包版本 lockstep（由 [`src/Directory.Build.props`](src/Directory.Build.props) 的 `EntJoyVersion` 唯一决定），仅 **win-x64**：

```powershell
dotnet add package EntJoy.ECS      # 只装这一个：ECS + 原生 JobSystem + NativeTranspiler
dotnet add package EntJoy.Jobs     # 只写 Job、不用 ECS 时改装这一个
```

| Project | 作用 | 包 |
| --- | --- | --- |
| [`src/EntJoy.ECS`](src/EntJoy.ECS/README.md) | ECS 运行时 + ECS Source Generator（**只装这一个即可**，原生 JobSystem 与 NativeTranspiler 随依赖到达）。 | [![NuGet Version](https://img.shields.io/nuget/v/EntJoy.ECS)](https://www.nuget.org/packages/EntJoy.ECS) |
| [`src/EntJoy.Jobs`](src/EntJoy.Jobs/README.md) | JobSystem + 预编译 `NativeDll.dll` + 原生链接套件 + NativeTranspiler 分析器与任务（只写 Job、不用 ECS 时装它）。 | [![NuGet Version](https://img.shields.io/nuget/v/EntJoy.Jobs)](https://www.nuget.org/packages/EntJoy.Jobs) |
| [`src/EntJoy.Collections`](src/EntJoy.Collections/README.md) | `NativeArray` / `NativeList` / `UnsafeList` / `UnsafeUtility`、分配器与 `AtomicSafetyHandle` / `DisposeSentinel` 安全检查。 | [![NuGet Version](https://img.shields.io/nuget/v/EntJoy.Collections)](https://www.nuget.org/packages/EntJoy.Collections) |
| [`src/EntJoy.Mathematics`](src/EntJoy.Mathematics/README.md) | 数学类型与 `BitMask` / `Hint` / `MemoryAddress` 等底层辅助。 | [![NuGet Version](https://img.shields.io/nuget/v/EntJoy.Mathematics)](https://www.nuget.org/packages/EntJoy.Mathematics) |

- **不写 `[NativeTranspile]` 的纯 C# 项目**：装包即用，**不需要** CMake / MSVC / ISPC（包内 `NativeDll.dll` 会自动复制到输出目录）。
- **写 `[NativeTranspile]` native job**：本机需要 **CMake + MSVC（或 ClangCL）**，ISPC 可选；构建期由包内分析器生成 C++/ISPC，再由包内 MSBuild 任务编译 `NativeTranspiled.dll` 并链接包内预编译 `NativeDll.lib`。消费者**不需要** NativeDll 源码或 imgui 子模块。

发布渠道：

| Feed | 还原需要凭据？ | 说明 |
| --- | --- | --- |
| **nuget.org** | 否 | 目前唯一可匿名还原的渠道；由 [`Publish NuGet`](.github/workflows/publish-nuget.yml) 在 `v*` tag 上发布 |
| **GitHub Packages**（`nuget.pkg.github.com/tianqiyuan520`） | **是**：classic PAT（`read:packages`） | 镜像源。该 feed 对公开包同样不支持匿名还原，且建议配 `packageSourceMapping`，否则它鉴权偶发失败会拖垮整个 restore |

发布流程：改 `EntJoyVersion` → 提交推送 → 打并推 `v*` tag 即自动发布（workflow 先跑 `tests/NuGetConsumer/run.ps1` 门禁，再向上面两个 feed 推送）。

## 目录

- [通过 NuGet 使用](#通过-nuget-使用)
- [架构概览](#架构概览)
- [安装](#安装)
- [配置自己的项目](#配置自己的项目)
- [ECS 示例](#ecs-示例)
- [JobSystem 示例](#jobsystem-示例)
- [NativeTranspiler 示例](#nativetranspiler-示例)
- [样例项目](#样例项目)
- [常见问题](#常见问题)
- [设计启发与致谢](#设计启发与致谢)

## 架构概览

EntJoy 将托管层的易用性与原生执行后端组合在一起：

1. **ECS** 将拥有相同组件集合的实体放入同一个 Archetype，并在 Chunk 中连续保存组件数组。
2. **Query** 使用 `WithAll`、`WithAny`、`WithNone` 和 `WithEnabled` 选择匹配的 Chunk。
3. **JobSystem** 把 for、batch、chunk 或 entity 工作提交给原生工作线程，并通过 `JobHandle` 表达依赖。
4. **Source Generator** 为 `IJobEntity`、原生绑定和调度扩展生成代码。
5. **NativeTranspiler** 将标记了 `[NativeTranspile]` 的受支持 C# 代码生成 C++、ISPC、WGSL（wgpu）或 CUDA（`.cu` → cubin）。
6. **NativeDll** 编译生成的代码，并提供统一的原生调度器、GPU 执行后端（wgpu / CUDA 驱动 API）和运行时 ABI。

| 目录 | 作用 |
| --- | --- |
| [`src/EntJoy.ECS`](src/EntJoy.ECS) | ECS、Query、JobSystem、Native Collections 和基础运行时 |
| [`src/EntJoy.ECS.SourceGenerator`](src/EntJoy.ECS.SourceGenerator) | ECS Job 的 C# Source Generator |
| [`src/NativeTranspiler`](src/NativeTranspiler) | C# 到 C++/ISPC/WGSL/CUDA 的生成器与分析器 |
| [`src/NativeTranspiler.Tasks`](src/NativeTranspiler.Tasks) | 从 MSBuild 调用 CMake 的自定义任务 |
| [`src/NativeDll`](src/NativeDll) | C++ JobSystem、Profiler、原生容器与 wgpu/CUDA GPU 执行后端 |
| [`samples/EntJoySample`](samples/EntJoySample) | 功能验证、用法示例和性能测试 |

## 安装

### 1. 安装必需工具

当前推荐使用以下环境：

- [Git](https://git-scm.com/download/win)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Visual Studio Build Tools 2022 或 Visual Studio 2022](https://visualstudio.microsoft.com/downloads/)
  - 安装 **使用 C++ 的桌面开发**（Desktop development with C++）工作负载
  - 安装 MSVC v143 C++ x64/x86 build tools
  - 安装 Windows 10 或 Windows 11 SDK
- [CMake](https://cmake.org/download/)，安装时加入 `PATH`
- [Intel ISPC](https://github.com/ispc/ispc/releases)

### 2. 配置 ISPC

1. 从 ISPC Releases 下载 Windows 压缩包并解压，例如：

   ```text
   C:\Tools\ispc-v1.xx.x-windows
   ```

2. 将包含 `ispc.exe` 的 `bin` 目录加入用户或系统 `PATH`：

   ```text
   C:\Tools\ispc-v1.xx.x-windows\bin
   ```

3. 修改 `PATH` 后重新打开终端和 Visual Studio。推荐在 **Developer PowerShell for VS 2022** 中验证：

   ```powershell
   dotnet --version
   cmake --version
   ispc --version
   where.exe cl
   ```

这些命令应分别找到 .NET SDK、CMake、ISPC 和 MSVC 编译器。

> 当前原生构建仅适配 MSVC。请不要使用 MinGW GCC/G++ 或 Clang 替换 `cl.exe`；相关生成器、编译参数、ISPC object 和 DLL 输出路径尚未完成适配。

> 当前生成的 ISPC 样例使用 `avx2-i32x8` 目标（生成器的 ISPC 编译命令行固定为 `--target=avx2-i32x8`）。运行 ISPC 后端前请确认 CPU 支持 AVX2；否则请只运行 C#、C++ 后端，或修改生成器目标后重新构建。

### 3. 克隆仓库

仓库有 1 个子模块（`src/NativeDll/thirdParty/imgui`，Dear ImGui 调试面板），必须带子模块克隆：

```powershell
git clone --recurse-submodules https://github.com/tianqiyuan520/EntJoy.git
cd EntJoy
```

### 4. Release 构建

```powershell
dotnet build samples/EntJoySample/EntJoySample.csproj -c Release
```

这条命令会自动完成以下步骤：

1. 编译 EntJoy、Source Generator 和 NativeTranspiler。
2. 生成 C# bindings、C++ 和 ISPC 源码到 `NativeTranspiler_Generated`。
3. 通过 MSBuild 任务调用 CMake。
4. 通过 MSVC 编译 C++，通过 ISPC 编译 SIMD kernel。
5. 生成 `NativeDll.dll` 并复制到仓库根目录的 `bin`。

首次构建会比增量构建更慢。生成代码和原生源码没有变化时，后续构建会通过内容哈希跳过不必要的 CMake 编译。

### 5. 运行样例

```powershell
.\bin\EntJoySample.exe
```

当前启用的入口位于 [`SchedulerCompareTest/Program.cs`](samples/EntJoySample/01_JobSystem/SchedulerCompareTest/Program.cs)，首次运行时自动执行 Managed JobSystem 正确性自检；切换样例请注释当前入口并取消目标目录中 `Program.cs` 的注释。

## 配置自己的项目

有两种方式，按"项目是否在仓库内"选择：

### 方式 A：NuGet 包（推荐，仓库外项目）

四个包（版本 lockstep，当前 **1.0.0**，仅 **win-x64**）与各自作用见上文[通过 NuGet 使用](#通过-nuget-使用)；**只装 `EntJoy.ECS` 即可**，其余随依赖到达。

```xml
<ItemGroup>
  <PackageReference Include="EntJoy.ECS" Version="1.0.0" />
</ItemGroup>
```

- **只用 C#（不写 `[NativeTranspile]`）**：装包即用，**不需要** CMake / MSVC / ISPC。包内 `NativeDll.dll` 会自动复制到输出目录，原生 JobSystem 开箱可用。
- **要写 `[NativeTranspile]` native job**：需要本机有 **CMake + MSVC（或 ClangCL）**；ISPC 可选（缺失时自动跳过 ISPC 后端）。构建期由包内分析器生成 C++/ISPC，再由包内 MSBuild 任务编译出 `NativeTranspiled.dll`（链接包内预编译 `NativeDll.lib`），消费者**不需要** NativeDll 源码或 imgui 子模块。

包从哪来、还原要不要凭据：见[通过 NuGet 使用](#通过-nuget-使用)。本地出包与端到端验证：

```powershell
# 出包到 artifacts\packages，并从本地 feed 还原 + 构建 + 运行冒烟测试
powershell -NoProfile -ExecutionPolicy Bypass -File tests\NuGetConsumer\run.ps1
```

[`tests/NuGetConsumer`](tests/NuGetConsumer) 是只用 `PackageReference` 的冒烟测试（托管 ECS + 原生 JobSystem + 转译的 `IJobParallelFor` / `IJobChunk` / `IJobEntity` native job），[`tests/NuGetJobsConsumer`](tests/NuGetJobsConsumer) 是 **Jobs-only** 冒烟测试（只装 `EntJoy.Jobs`，不引用 ECS，四种数组 job 形态转译为 native）。两者都可作为包消费的最小参照。

### 方式 B：源码引用（仓库内项目）

把工程放在仓库下**两层目录**（例如 `samples/MyEntJoyApp`）——这样 `..\..\src\` 正好指向仓库的 `src`——并 **import 共享的 MSBuild 接线**，原生转译链（分析器 / 任务 / 编译目标 / DLL 复制）全部由它提供：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\EntJoy.ECS.SourceGenerator\EntJoy.ECS.SourceGenerator.csproj"
                      OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
    <ProjectReference Include="..\..\src\EntJoy.ECS\EntJoy.ECS.csproj" />
  </ItemGroup>

  <Import Project="..\..\src\EntJoy.Jobs\msbuild\EntJoy.Jobs.props" />
  <Import Project="..\..\src\EntJoy.Jobs\msbuild\EntJoy.Jobs.targets" />
</Project>
```

仓库内的三个消费方（[`EntJoySample`](samples/EntJoySample)、[`Godot`](samples/Godot)、[`NativeTranspilerFixture`](tools/NativeTranspilerFixture)）都是这个写法。项目不在 `src` 下时，用 `-p:EntJoyNativeDllDir=<abs path to src\NativeDll>` 指定原生源码目录即可。

## ECS 示例

### 核心概念

- **Entity**：轻量 ID，本身不保存业务行为。
- **Component**：实现 `IComponentData` 的 unmanaged 数据结构。
- **Archetype**：一组确定的组件类型；组件集合相同的实体属于同一个 Archetype。
- **Chunk**：同一 Archetype 内连续存储实体和组件数据的内存块。
- **Query**：选择满足组件条件的 Archetype 和 Chunk。

下面的示例创建位置和速度组件、生成实体，并同步遍历匹配的 Chunk：

```csharp
using EntJoy.ECS;
using EntJoy.Mathematics;

public struct Position : IComponentData
{
    public float2 Value;
}

public struct Velocity : IComponentData
{
    public float2 Value;
}

using var world = new World("GameWorld");
World.DefaultWorld = world;

ref EntityManager entityManager = ref world.EntityManager;
for (int i = 0; i < 10_000; i++)
{
    Entity entity = entityManager.NewEntity(typeof(Position), typeof(Velocity));
    entityManager.Set(entity, new Position { Value = new float2(i, 0) });
    entityManager.Set(entity, new Velocity { Value = new float2(1, 0) });
}

var query = new QueryBuilder().WithAll<Position, Velocity>();

foreach (var chunk in SystemAPI.QueryChunks<Position, Velocity>())
{
    Span<Position> positions = chunk.GetSpan0();
    Span<Velocity> velocities = chunk.GetSpan1();

    for (int i = 0; i < chunk.Length; i++)
    {
        Position position = positions[i];
        position.Value += velocities[i].Value;
        positions[i] = position;
    }
}
```

`World.Dispose()` 会先完成仍在使用该 World 数据的 Job，再释放 ECS 内存。执行结构变化（创建、销毁实体或增删组件）之前，也应先完成相关 Job。

更多示例：

- 最小 Chunk Job：[`SimpleIJobChunkTest`](samples/EntJoySample/02_IJobChunkECS/SimpleIJobChunkTest)
- 百万实体 C#/C++/ISPC 对比：[`IJobChunkMoveCompareTest`](samples/EntJoySample/02_IJobChunkECS/IJobChunkMoveCompareTest)
- ECS 样例集合：[`02_IJobChunkECS`](samples/EntJoySample/02_IJobChunkECS)

## JobSystem 示例

### 初始化与关闭

在提交 Job 前初始化原生调度器。线程数为 `0` 时由调度器根据机器自动选择：

```csharp
NativeJobScheduler.Initialize();

try
{
    // Create worlds and schedule jobs here.
}
finally
{
    NativeJobScheduler.Shutdown();
}
```

### IJobParallelFor

`IJobParallelFor` 适合按索引并行处理连续数组。`innerBatchCount` 控制每次领取的工作粒度：

```csharp
using EntJoy.Collections;

public struct AddJob : IJobParallelFor
{
    public NativeArray<int> Values;
    public int Delta;

    public void Execute(int index)
    {
        Values[index] += Delta;
    }
}

using var values = new NativeArray<int>(1_000_000, Allocator.Persistent);

JobHandle handle = new AddJob
{
    Values = values,
    Delta = 1
}.Schedule(values.Length, innerBatchCount: 4096);

handle.Complete();
```

不要在 Job 完成前释放它正在访问的 `NativeArray<T>`。

### Job 依赖

将前一个 `JobHandle` 传给后一个 Job，即可保证执行顺序而不必在两者之间阻塞主线程：

```csharp
JobHandle first = new AddJob
{
    Values = values,
    Delta = 1
}.Schedule(values.Length, 4096);

JobHandle second = new AddJob
{
    Values = values,
    Delta = 2
}.Schedule(values.Length, 4096, first);

second.Complete();
```

多个前置任务可用 `JobHandle.CombineDependencies(first, second)` 合并。

### IJobChunk

`IJobChunk` 每次接收一个匹配的 Chunk，适合手工控制组件数组遍历（调度扩展方法位于 `EntJoy.ECS.JobSystem`，需要 import）：

```csharp
public struct MoveChunkJob : IJobChunk
{
    public float DeltaTime;

    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        Span<Position> positions = chunk.GetComponentDataSpan<Position>();
        Span<Velocity> velocities = chunk.GetComponentDataSpan<Velocity>();

        for (int i = 0; i < positions.Length; i++)
        {
            Position position = positions[i];
            position.Value += velocities[i].Value * DeltaTime;
            positions[i] = position;
        }
    }
}

new MoveChunkJob { DeltaTime = 1f / 60f }
    .Schedule(new QueryBuilder().WithAll<Position, Velocity>())
    .Complete();
```

### IJobEntity

`IJobEntity` 使用更简洁的逐实体 `Execute` 签名。Source Generator 会根据参数推导组件访问方式和查询需求：

```csharp
public struct MoveEntityJob : IJobEntity
{
    public float DeltaTime;

    public void Execute(ref Position position, in Velocity velocity)
    {
        position.Value += velocity.Value * DeltaTime;
    }
}

var moveQuery = new QueryBuilder().WithAll<Position, Velocity>();
new MoveEntityJob { DeltaTime = 1f / 60f }
    .Schedule(moveQuery)
    .Complete();
```

### 如何选择 Job 类型

| 类型 | 使用场景 |
| --- | --- |
| `IJob` | 单个通用任务 |
| `IJobFor` | 在一个 Job 中串行执行索引循环 |
| `IJobParallelFor` | 并行处理独立索引 |
| `IJobParallelForBatch` | 以连续批次处理数组，降低回调和调度次数 |
| `IJobChunk` | 直接访问 ECS Chunk 和组件数组 |
| `IJobEntity` | 用简洁的逐实体签名处理 ECS 组件 |

更多示例见 [`01_JobSystem`](samples/EntJoySample/01_JobSystem) 和 [`02_IJobChunkECS`](samples/EntJoySample/02_IJobChunkECS)。

## NativeTranspiler 示例

NativeTranspiler 允许保留 C# Job 定义和 `Schedule` API，同时将 `Execute` 生成到 C++ 或 ISPC。下面三个 Job 表达相同的移动逻辑：

```csharp
public struct MoveJobCSharp : IJobEntity
{
    public float DeltaTime;

    public void Execute(ref Position position, in Velocity velocity)
    {
        position.Value += velocity.Value * DeltaTime;
    }
}

[NativeTranspiler.NativeTranspile(
    Target = NativeTranspiler.BackendTarget.Cpp)]
public struct MoveJobCpp : IJobEntity
{
    public float DeltaTime;

    public void Execute(ref Position position, in Velocity velocity)
    {
        position.Value += velocity.Value * DeltaTime;
    }
}

[NativeTranspiler.NativeTranspile(
    Target = NativeTranspiler.BackendTarget.Ispc,
    MathLib = NativeTranspiler.IspcMathLib.fast)]
public struct MoveJobIspc : IJobEntity
{
    public float DeltaTime;

    public void Execute(ref Position position, in Velocity velocity)
    {
        position.Value += velocity.Value * DeltaTime;
    }
}
```

三个后端使用相同的查询和调度方式：

```csharp
var query = new QueryBuilder().WithAll<Position, Velocity>();

new MoveJobCSharp { DeltaTime = dt }.Schedule(query).Complete();
new MoveJobCpp    { DeltaTime = dt }.Schedule(query).Complete();
new MoveJobIspc   { DeltaTime = dt }.Schedule(query).Complete();
```

可选配置：

```csharp
// C++ fast math
[NativeTranspiler.NativeTranspile(
    Target = NativeTranspiler.BackendTarget.Cpp,
    CppMathLib = NativeTranspiler.CppMathLib.fast)]

// ISPC fast math with ISPC task-based multithreading
[NativeTranspiler.NativeTranspile(
    Target = NativeTranspiler.BackendTarget.Ispc,
    MathLib = NativeTranspiler.IspcMathLib.fast,
    UseISPC_MT = true)]
```

NativeTranspiler 不是完整的 C# 编译器。被转译的 Job 应遵守以下约束：

- Job 字段、参数和局部数据优先使用 unmanaged 类型。
- 可以使用 EntJoy 支持的数学类型、Native Collections 和已实现的表达式/控制流。
- 不要在转译代码中分配托管对象，或依赖 `string`、普通数组、class、反射、GC 和不受支持的 .NET API。
- 生成器诊断（`NT001` 等）应当作为构建错误处理，不要手工绕过生成代码。
- 生成文件位于项目的 `NativeTranspiler_Generated`，会在构建时更新，通常不应手工编辑。

### 怎么写、怎么配（速查）

| 主题 | 要点 |
| --- | --- |
| 支持形态 | 数组类：`IJob` / `IJobFor` / `IJobParallelFor` / `IJobParallelForBatch`（**不引用 ECS 也能用**）；ECS 类：`IJobChunk` / `IJobEntity`（需 `EntJoy.ECS`，调度扩展在 `EntJoy.ECS.JobSystem`，须 `using`） |
| 属性选项 | `Target`(Cpp/Ispc)、`MathLib`、`CppMathLib`、`UseISPC_MT`、`AutoSIMD`、`MathPrecision`、`DisabledAutoRefresh`；选项与后端不匹配会被 **error** 拦下（NT018–NT022、NT025） |
| 属性（MSBuild） | `EntJoyNativeDllDir`（原生目录）、`EntJoyPrebuiltNativeDir`（非空=链接预编译 NativeDll，包模式用）、`EnableNativeCompile=false`（跳过 CMake）；手写接线时**必须**把它们加进 `CompilerVisibleProperty` |
| CMake 选项 | `NATIVE_SIMD_LEVEL`(AUTO/AVX2/AVX/SSE4/NEON/SCALAR)、`NATIVE_SIMD_MATH_PRECISION`(1/2/3)、`ENTJOY_ENABLE_SENTINEL`(默认 OFF) |
| 工具链 | 纯 C#：仅 .NET 8 SDK；Cpp 后端：+ CMake/MSVC（缺 ClangCL 自动回退）；ISPC 后端：+ `ispc` 在 `PATH` |
| 产物 | `<项目>/NativeTranspiler_Generated/`（cpp/h/ispc + `CMakeLists.txt` + 哈希门控文件 + `build/Release/{NativeDll,NativeTranspiled}.dll`）；运行期两个 DLL 必须同目录 |
| 常见报错 | `CS0234/CS0246` + **NT030** = 生成物耦合 ECS；`__ENTJOY_UNSUPPORTED_*`/`silently-degraded` = job 内用了未支持构造（构建失败，含文件行号） |

完整说明（含属性表、后端兼容矩阵、排错速查、最小可运行参考）：[`docs/public/Native-Jobs-Guide.md`](docs/public/Native-Jobs-Guide.md)。

完整对比代码：

- [`03_NativeTranspiler`](samples/EntJoySample/03_NativeTranspiler)
- [`IJobChunkMoveCompareSample.cs`](samples/EntJoySample/02_IJobChunkECS/IJobChunkMoveCompareTest/IJobChunkMoveCompareSample.cs)
- [`NativeTranspiler_Generated`](samples/EntJoySample/NativeTranspiler_Generated)

## 样例项目
> 新增的框架验收样例与探针（2026-09-13）见下一节。

### 框架能力验收样例（原生编译 + 运行）

- [`12_EntityNativeLookup`](samples/EntJoySample/12_EntityNativeLookup)：blittable 实体定位表 + job-safe 跨 chunk 随机访问（`NativeComponentLookup<T>`，体内用 `ref` 局部）+ ECB 批量创建/写列/销毁/清空 + 非分配批量创建 + 回收池重建。
- [`13_EnableBitMapNative`](samples/EntJoySample/13_EnableBitMapNative)：原生 `IJobChunk` **读/写**逐组件 enable 位图（`GetEnableBitMapPtr<T>()`，与托管逐实体 + `WithEnabled` 查询全量一致）+ 原生 `IJobEntity` + `NativeArray` 辅助表 + `Entity` 参数。
- [`14_AutoSimdChunkWriteback`](samples/EntJoySample/14_AutoSimdChunkWriteback)：`IJobChunk` 双组件整结构体回写（AutoSIMD vs 标量 C++ vs C# 基线，逐实体一致）。
- 统一入口：[`12_EntityNativeLookup/Program.cs`](samples/EntJoySample/12_EntityNativeLookup/Program.cs)（三段依次运行）；
  文档：[`docs/public/Runtime-Contracts-and-Known-Limitations.md`](docs/public/Runtime-Contracts-and-Known-Limitations.md)、
  [`docs/public/NativeTranspiler-Boundaries-and-Diagnostics.md`](docs/public/NativeTranspiler-Boundaries-and-Diagnostics.md)。

### 探针（`tools/`，均为可运行工程、退出码即判据）

| 探针 | 覆盖 |
|---|---|
| `EntityLocateProbe` | 定位表 vs 托管表（创建/销毁/压缩/Id 复用后）一致 |
| `EntityCommandBufferProbe` | ECB 批量创建/写列/回放分配 |
| `EntityBulkDestroyProbe` | 批量销毁 / `DestroyAllInArchetype` |
| `EntityDestroyIsolationProbe` | 销毁隔离 / 幽灵实体 / 表容量 |
| `EcsShapeCostProbe` | 定位表 lookup 成本、结构变更成本、chunk 压缩、批量创建 |
| `EcbParallelWriterProbe` | `ParallelWriter`（单线程 + **跨 tile 共享**原子占位 + 容量不足失败路径） |
| `SafetyInterceptProbe` | 安全检查语义负向用例（快路径不得吞掉违规；必须用 Native 后端） |

[`samples/EntJoySample`](samples/EntJoySample) 包含以下案例：

### 01 JobSystem

- [`CSharpJobManagedContextTest`](samples/EntJoySample/01_JobSystem/CSharpJobManagedContextTest)：比较 unmanaged raw-copy 与 managed `GCHandle` Job context，并覆盖 `IJob`、`IJobParallelFor` 和 `IJobChunk`。
- [`HeavyJob`](samples/EntJoySample/01_JobSystem/HeavyJob)：重计算和 CPU 满负载 Job 实验。
- [`IJobChunkScheduleOverheadTest`](samples/EntJoySample/01_JobSystem/IJobChunkScheduleOverheadTest)：比较 C#、C++、ISPC `IJobChunk` 空任务与极轻任务的固定调度开销。
- [`JobProfilerTest`](samples/EntJoySample/01_JobSystem/JobProfilerTest)：验证 Job Profiler 的采样和统计功能。
- [`ParallelRwConflictTest`](samples/EntJoySample/01_JobSystem/ParallelRwConflictTest)：演示并行读写冲突检测——Job 间交叉写冲突、以及主线程在 Job 活跃期访问原生容器时被拦、`Complete()` 后放行。

### 02 IJobChunk ECS

- [`SimpleIJobChunkTest`](samples/EntJoySample/02_IJobChunkECS/SimpleIJobChunkTest)：创建组件和实体、构造查询并调度 `IJobChunk` 的最小示例。
- [`IJobChunkMoveCompareTest`](samples/EntJoySample/02_IJobChunkECS/IJobChunkMoveCompareTest)：对 100 万实体运行 C#、C++、ISPC 的 `IJobChunk`/`IJobEntity` Light、Heavy 和 Sleep 对比，并验证结果一致性。
- [`SpritesRandomMoveLikeTest`](samples/EntJoySample/02_IJobChunkECS/SpritesRandomMoveLikeTest)：百万实体持续移动场景，对比 ECS Chunk、C# Job、Native C++ Job 和 Native ISPC Job，并提供 parity 验证。

### 03 NativeTranspiler

- [`MovementTest`](samples/EntJoySample/03_NativeTranspiler/MovementTest)：展示数组和 ECS 移动 Job 从 C# 生成到 C++/ISPC，并进行帧循环和正确性验证。
- [`StaticMethodTest`](samples/EntJoySample/03_NativeTranspiler/StaticMethodTest)：验证静态方法及其调用的 NativeTranspiler 转译。
- [`ISPCMT`](samples/EntJoySample/03_NativeTranspiler/ISPCMT)：比较普通 ISPC 与 `UseISPC_MT` 多线程执行模式。

### 04 Native Collections

- [`NativeListTest`](samples/EntJoySample/04_NativeCollections/NativeListTest)：验证 `NativeList<T>` 的分配、访问和释放。
- [`NativeColletionStructTest`](samples/EntJoySample/04_NativeCollections/NativeColletionStructTest)：验证 Native Collection 作为结构体字段使用的场景。
- [`AtomicTest`](samples/EntJoySample/04_NativeCollections/AtomicTest)：验证并行 Job 中的原子加法等原子操作。

### 05 Algorithms

- [`GridSearch`](samples/EntJoySample/05_Algorithms/GridSearch)：二维网格构建、最近点和范围搜索实验。（2026-08 整体注释停用）

### 06 HotField Handle

- [`HotFieldHandle`](samples/EntJoySample/06_HotFieldHandle)：HotField 可行性原型——普通 class + `[HotFieldEntity]` 属性 → 字段级 SoA 存储（`HotStore`）+ int 索引 + `ref` 属性重定向，System（`IJobParallelFor`）直接消费同一存储。验证「OOP 游戏代码与 plain class 逐字节相同（无感）、Attribute 机械部分零成本、OOD↔DOD 共享存储结果一致；1M 密集 OOP 的 SoA 结构税如实报告（批量走 System）」。

### 08 Entity Random Access

- [`RandomAccess`](samples/EntJoySample/08_EntityRandomAccess)：稀疏 Entity 随机访问开销基准（ComponentLookup 优化）。

性能样例请使用 Release x64、关闭调试器，并保持电源模式和后台负载一致。README 不固定记录单台机器的结果；请在目标硬件上运行样例获得可比较数据。

## 常见问题

### CMake 找不到 ISPC

确认 `ispc.exe` 所在目录已加入 `PATH`，然后完全重启终端和 Visual Studio：

```powershell
where.exe ispc
ispc --version
```

### 找不到 MSVC `cl.exe`

确认 Visual Studio Installer 已安装“使用 C++ 的桌面开发”、MSVC v143 和 Windows SDK，并在 Developer PowerShell for VS 2022 中构建。

### 子模块缺失

克隆时若漏带子模块（`src/NativeDll/thirdParty/imgui` 为空，CMake 报 `Cannot find source file: .../thirdParty/imgui/imgui.cpp`），执行：

```powershell
git submodule update --init --recursive
```

### 没有生成 `NativeDll.dll`

检查构建日志中的 CMake、MSVC 或 ISPC 错误。需要时删除生成目录中的 `build` 缓存后重新执行 Release 构建。成功后应存在：

```text
bin\NativeDll.dll
```

### Debug 为什么明显更慢

Debug 构建会减少 JIT、C#、C++ 和链接器优化，还可能启用额外检查。调度和百万实体基准必须使用 `-c Release`，并且不要附加调试器。

### ISPC 程序启动时出现非法指令

当前 ISPC 样例目标为 AVX-512 SKX。确认 CPU 支持对应指令，或修改 NativeTranspiler 的 ISPC target 并重新生成原生代码。

## 设计启发与致谢

EntJoy 的设计和实现受到以下项目与技术的启发：

- [Unity DOTS / Entities](https://unity.com/dots)：数据导向 ECS、Chunk 和 Job 工作流的主要设计参照。
- [coinsoundsbetter/EntJoy](https://github.com/coinsoundsbetter/EntJoy)：项目早期版本的起点。
- [Arch](https://github.com/genaray/Arch)：高性能 Archetype ECS 的设计参考。
- [Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS)：C# ECS API 与数据布局的实现参考。
- [Intel ISPC](https://ispc.github.io/)：面向 SPMD/SIMD 的原生计算后端。

感谢这些项目的作者和贡献者公开他们的工作，使 EntJoy 能够在已有经验之上继续探索 C#、C++ 与 SIMD ECS 技术栈。

---

<a id="english"></a>

# EntJoy (English)

[中文](#entjoy) | **English**

> **Positioning:** EntJoy is a **Headless GameFramework** — providing Archetype ECS, a parallel JobSystem, NativeTranspiler (C#→C++/ISPC), and cross-platform native runtimes, with no built-in renderer. The rendering layer is plugged in by the upper-layer application (Godot, Unity, custom engines, etc.).
>
> **Disclaimer:** EntJoy is not affiliated with, endorsed by, or sponsored by Unity Technologies.

EntJoy is an Archetype ECS and JobSystem stack written in **C#, C++, and ISPC**. Inspired by the data-oriented design of Unity DOTS, it stores entities by Archetype and Chunk, schedules work through a unified JobSystem, and can transpile supported C# jobs to C++ or ISPC with source generators.

The project currently provides:

- Archetype/Chunk ECS, entities, components, queries, and enableable components.
- `IJob`, `IJobFor`, `IJobParallelFor`, `IJobParallelForBatch`, `IJobChunk`, and `IJobEntity`.
- `JobHandle` dependencies, combined dependencies, and cooperative `Complete()` execution.
- A native worker scheduler shared by C#, C++, and ISPC backends.
- NativeTranspiler source generation from supported C# jobs to C++ or ISPC. **It also works for pure JobSystem projects**: with only `EntJoy.Collections` / `EntJoy.Jobs` referenced (no ECS), array-shaped jobs (`IJob`/`IJobFor`/`IJobParallelFor`/`IJobParallelForBatch`) transpile and run natively; `IJobChunk`/`IJobEntity`/`SendEvent` are ECS features and require `EntJoy.ECS`.
- Low-level utilities such as `NativeArray<T>`, `NativeList<T>`, atomics, and math types.
- Functional, correctness, and performance samples in [EntJoySample](samples/EntJoySample).

> The repository currently supports and verifies only the **Windows x64, .NET 8, MSVC, and Intel ISPC** toolchain. GCC, G++, and Clang are not currently supported. APIs are still evolving. Two consumption modes are supported: **NuGet packages** (`EntJoy.ECS` / `EntJoy.Jobs` / `EntJoy.Collections` / `EntJoy.Mathematics`, win-x64 only) and **source project references**; releases are triggered by pushing a `v*` tag — see [Using NuGet Packages](#using-nuget-packages).

## Using NuGet Packages

Four packages, lockstep version (driven solely by `EntJoyVersion` in [`src/Directory.Build.props`](src/Directory.Build.props)), **win-x64 only**:

```powershell
dotnet add package EntJoy.ECS      # the only one you need: ECS + native JobSystem + NativeTranspiler
dotnet add package EntJoy.Jobs     # use this instead if you only write jobs, without ECS
```

| Project | Purpose | Package |
| --- | --- | --- |
| [`src/EntJoy.ECS`](src/EntJoy.ECS/README.md) | ECS runtime + ECS source generator (**this is the only one you need**; the native JobSystem and NativeTranspiler arrive as dependencies). | [![NuGet Version](https://img.shields.io/nuget/v/EntJoy.ECS)](https://www.nuget.org/packages/EntJoy.ECS) |
| [`src/EntJoy.Jobs`](src/EntJoy.Jobs/README.md) | JobSystem + prebuilt `NativeDll.dll` + native link kit + NativeTranspiler analyzer and MSBuild task (use it when you only write jobs, without ECS). | [![NuGet Version](https://img.shields.io/nuget/v/EntJoy.Jobs)](https://www.nuget.org/packages/EntJoy.Jobs) |
| [`src/EntJoy.Collections`](src/EntJoy.Collections/README.md) | `NativeArray` / `NativeList` / `UnsafeList` / `UnsafeUtility`, allocators, and `AtomicSafetyHandle` / `DisposeSentinel` safety checks. | [![NuGet Version](https://img.shields.io/nuget/v/EntJoy.Collections)](https://www.nuget.org/packages/EntJoy.Collections) |
| [`src/EntJoy.Mathematics`](src/EntJoy.Mathematics/README.md) | Math types plus low-level helpers such as `BitMask` / `Hint` / `MemoryAddress`. | [![NuGet Version](https://img.shields.io/nuget/v/EntJoy.Mathematics)](https://www.nuget.org/packages/EntJoy.Mathematics) |

- **Pure C# projects (no `[NativeTranspile]`)**: works out of the box, **no** CMake / MSVC / ISPC required (the packaged `NativeDll.dll` is copied to the output directory).
- **Writing `[NativeTranspile]` native jobs**: requires **CMake + MSVC (or ClangCL)** locally; ISPC is optional. At build time the packaged analyzer emits the C++/ISPC, and the packaged MSBuild task compiles `NativeTranspiled.dll` against the packaged prebuilt `NativeDll.lib`. Consumers need **neither** the NativeDll sources **nor** the imgui submodule.

Feeds:

| Feed | Credentials for restore? | Notes |
| --- | --- | --- |
| **nuget.org** | No | the only feed that allows anonymous restore today; published by [`Publish NuGet`](.github/workflows/publish-nuget.yml) on `v*` tags |
| **GitHub Packages** (`nuget.pkg.github.com/tianqiyuan520`) | **Yes** — classic PAT with `read:packages` | mirror feed. It does not allow anonymous restore even for public packages, and you should add `packageSourceMapping` so that its intermittent auth failures cannot break the whole restore |

Releasing: bump `EntJoyVersion` → commit and push → push a `v*` tag; the workflow runs the `tests/NuGetConsumer/run.ps1` gate first, then pushes to both feeds above.

## Contents

- [Using NuGet Packages](#using-nuget-packages)
- [Architecture](#architecture)
- [Installation](#installation)
- [Configure Your Own Project](#configure-your-own-project)
- [ECS Example](#ecs-example)
- [JobSystem Example](#jobsystem-example)
- [NativeTranspiler Example](#nativetranspiler-example)
- [Samples](#samples)
- [Troubleshooting](#troubleshooting)
- [Acknowledgements and Inspirations](#acknowledgements-and-inspirations)

## Architecture

EntJoy combines a convenient managed API with native execution backends:

1. **ECS** groups entities with the same component set into an Archetype and stores component arrays contiguously in Chunks.
2. **Query** selects matching Chunks through `WithAll`, `WithAny`, `WithNone`, and `WithEnabled`.
3. **JobSystem** submits for, batch, chunk, or entity work to native workers and expresses dependencies with `JobHandle`.
4. **Source Generator** emits code for `IJobEntity`, native bindings, and scheduling extensions.
5. **NativeTranspiler** generates C++, ISPC, WGSL (wgpu), or CUDA (`.cu` → cubin) from supported C# code marked with `[NativeTranspile]`.
6. **NativeDll** compiles generated code and provides the shared native scheduler, GPU execution backends (wgpu / CUDA driver API), and runtime ABI.

| Directory | Purpose |
| --- | --- |
| [`src/EntJoy.ECS`](src/EntJoy.ECS) | ECS, queries, JobSystem, Native Collections, and managed runtime |
| [`src/EntJoy.ECS.SourceGenerator`](src/EntJoy.ECS.SourceGenerator) | C# source generator for ECS jobs |
| [`src/NativeTranspiler`](src/NativeTranspiler) | C#-to-C++/ISPC generator and analyzer |
| [`src/NativeTranspiler.Tasks`](src/NativeTranspiler.Tasks) | Custom MSBuild task that invokes CMake |
| [`src/NativeDll`](src/NativeDll) | C++ JobSystem, profiler, and native container support |
| [`samples/EntJoySample`](samples/EntJoySample) | Usage, correctness, and performance samples |

## Installation

### 1. Install prerequisites

The currently recommended environment is:

- [Git](https://git-scm.com/download/win)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Visual Studio Build Tools 2022 or Visual Studio 2022](https://visualstudio.microsoft.com/downloads/)
  - Install the **Desktop development with C++** workload
  - Install MSVC v143 C++ x64/x86 build tools
  - Install a Windows 10 or Windows 11 SDK
- [CMake](https://cmake.org/download/) added to `PATH`
- [Intel ISPC](https://github.com/ispc/ispc/releases)

### 2. Configure ISPC

1. Download and extract a Windows release, for example:

   ```text
   C:\Tools\ispc-v1.xx.x-windows
   ```

2. Add the directory containing `ispc.exe` to the user or system `PATH`:

   ```text
   C:\Tools\ispc-v1.xx.x-windows\bin
   ```

3. Restart your terminal and Visual Studio after changing `PATH`. Verify the toolchain from **Developer PowerShell for VS 2022**:

   ```powershell
   dotnet --version
   cmake --version
   ispc --version
   where.exe cl
   ```

> The native build currently supports MSVC only. Do not replace `cl.exe` with MinGW GCC/G++ or Clang; the generated build options, ISPC objects, and DLL output paths have not yet been adapted for those toolchains.

> Generated ISPC samples currently target `avx2-i32x8` (the generator emits `--target=avx2-i32x8`). Confirm that the machine supports AVX2 before running the ISPC backend. Otherwise, use the C# or C++ backend, or change the generator target and rebuild.

### 3. Clone

The repository has one submodule (`src/NativeDll/thirdParty/imgui`, the Dear ImGui debug panel); clone with submodules:

```powershell
git clone --recurse-submodules https://github.com/tianqiyuan520/EntJoy.git
cd EntJoy
```

### 4. Build Release

```powershell
dotnet build samples/EntJoySample/EntJoySample.csproj -c Release
```

The build automatically:

1. Compiles EntJoy, its source generator, and NativeTranspiler.
2. Generates C# bindings and C++/ISPC source under `NativeTranspiler_Generated`.
3. Invokes CMake through a custom MSBuild task.
4. Compiles C++ with MSVC and SIMD kernels with ISPC.
5. Builds `NativeDll.dll` and copies it to the root `bin` directory.

The first build is slower than incremental builds. When generated and native sources have not changed, content hashes avoid unnecessary CMake compilation.

### 5. Run a sample

```powershell
.\bin\EntJoySample.exe
```

The active entry point is currently [`SchedulerCompareTest/Program.cs`](samples/EntJoySample/01_JobSystem/SchedulerCompareTest/Program.cs), which runs a Managed JobSystem correctness self-check on first launch; to switch samples, comment the current entry and uncomment `Program.cs` in the target directory.

## Configure Your Own Project

Two options depending on whether your project lives inside this repository:

### Option A: NuGet packages (recommended for out-of-repo projects)

Four packages (lockstep version, currently **1.0.0**, **win-x64 only**); see [Using NuGet Packages](#using-nuget-packages) above for what each one contains — **`EntJoy.ECS` is the only one you need to reference**, the rest arrive as dependencies.

```xml
<ItemGroup>
  <PackageReference Include="EntJoy.ECS" Version="1.0.0" />
</ItemGroup>
```

- **C# only (no `[NativeTranspile]`)**: works out of the box, **no** CMake / MSVC / ISPC required. The packaged `NativeDll.dll` is copied to the output directory, so the native JobSystem is available immediately.
- **Writing `[NativeTranspile]` native jobs**: requires **CMake + MSVC (or ClangCL)** locally; ISPC is optional (the ISPC backend is skipped when missing). At build time the packaged analyzer generates the C++/ISPC, and the packaged MSBuild task compiles `NativeTranspiled.dll` against the packaged prebuilt `NativeDll.lib`. Consumers need **neither** the NativeDll sources **nor** the imgui submodule.

Where do the packages come from, and does restore need credentials? See [Using NuGet Packages](#using-nuget-packages). Local pack + end-to-end verification:

```powershell
# pack into artifacts\packages, then restore from that local feed, build and run the smoke test
powershell -NoProfile -ExecutionPolicy Bypass -File tests\NuGetConsumer\run.ps1
```

[`tests/NuGetConsumer`](tests/NuGetConsumer) is a `PackageReference`-only smoke test (managed ECS + native JobSystem + transpiled `IJobParallelFor` / `IJobChunk` / `IJobEntity` native jobs), and [`tests/NuGetJobsConsumer`](tests/NuGetJobsConsumer) is the **Jobs-only** smoke test (only `EntJoy.Jobs`, no ECS reference, four array-job shapes transpiled to native). Both double as minimal package-consumption references.

### Option B: Source reference (projects inside this repository)

Place the project **two levels below the repository root** (e.g. `samples/MyEntJoyApp`, so that `..\..\src\` resolves to the repo `src`) and **import the shared MSBuild wiring**, which provides the whole native transpile chain (analyzer / task / compile targets / DLL copy):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\EntJoy.ECS.SourceGenerator\EntJoy.ECS.SourceGenerator.csproj"
                      OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
    <ProjectReference Include="..\..\src\EntJoy.ECS\EntJoy.ECS.csproj" />
  </ItemGroup>

  <Import Project="..\..\src\EntJoy.Jobs\msbuild\EntJoy.Jobs.props" />
  <Import Project="..\..\src\EntJoy.Jobs\msbuild\EntJoy.Jobs.targets" />
</Project>
```

The three in-repo consumers ([`EntJoySample`](samples/EntJoySample), [`Godot`](samples/Godot), [`NativeTranspilerFixture`](tools/NativeTranspilerFixture)) all use exactly this form. If your project does not live under `src`, point `-p:EntJoyNativeDllDir=<abs path to src\NativeDll>` at the native sources.

## ECS Example

### Concepts

- **Entity** is a lightweight ID without application behavior.
- **Component** is an unmanaged data struct implementing `IComponentData`.
- **Archetype** describes a fixed component set shared by a group of entities.
- **Chunk** stores entity and component arrays contiguously for an Archetype.
- **Query** selects Archetypes and Chunks by component conditions.

The following example defines position and velocity components, creates entities, and synchronously iterates matching Chunks:

```csharp
using EntJoy.ECS;
using EntJoy.Mathematics;

public struct Position : IComponentData
{
    public float2 Value;
}

public struct Velocity : IComponentData
{
    public float2 Value;
}

using var world = new World("GameWorld");
World.DefaultWorld = world;

ref EntityManager entityManager = ref world.EntityManager;
for (int i = 0; i < 10_000; i++)
{
    Entity entity = entityManager.NewEntity(typeof(Position), typeof(Velocity));
    entityManager.Set(entity, new Position { Value = new float2(i, 0) });
    entityManager.Set(entity, new Velocity { Value = new float2(1, 0) });
}

var query = new QueryBuilder().WithAll<Position, Velocity>();

foreach (var chunk in SystemAPI.QueryChunks<Position, Velocity>())
{
    Span<Position> positions = chunk.GetSpan0();
    Span<Velocity> velocities = chunk.GetSpan1();

    for (int i = 0; i < chunk.Length; i++)
    {
        Position position = positions[i];
        position.Value += velocities[i].Value;
        positions[i] = position;
    }
}
```

`World.Dispose()` completes jobs still using that World before releasing ECS memory. Complete related jobs before structural changes such as creating or destroying entities or adding and removing components.

Related samples:

- Minimal Chunk job: [`SimpleIJobChunkTest`](samples/EntJoySample/02_IJobChunkECS/SimpleIJobChunkTest)
- One-million-entity C#/C++/ISPC comparison: [`IJobChunkMoveCompareTest`](samples/EntJoySample/02_IJobChunkECS/IJobChunkMoveCompareTest)
- ECS sample collection: [`02_IJobChunkECS`](samples/EntJoySample/02_IJobChunkECS)

## JobSystem Example

Initialize `NativeJobScheduler` before submitting jobs and shut it down after all worlds and jobs have been released. Passing zero threads lets the scheduler choose automatically:

```csharp
NativeJobScheduler.Initialize();

try
{
    // Create worlds and schedule jobs here.
}
finally
{
    NativeJobScheduler.Shutdown();
}
```

### IJobParallelFor

Use `IJobParallelFor` for independent indices in a contiguous array. `innerBatchCount` controls work granularity:

```csharp
using EntJoy.Collections;

public struct AddJob : IJobParallelFor
{
    public NativeArray<int> Values;
    public int Delta;

    public void Execute(int index)
    {
        Values[index] += Delta;
    }
}

using var values = new NativeArray<int>(1_000_000, Allocator.Persistent);

JobHandle handle = new AddJob
{
    Values = values,
    Delta = 1
}.Schedule(values.Length, innerBatchCount: 4096);

handle.Complete();
```

Do not dispose a `NativeArray<T>` while a job is still accessing it.

### Dependencies

Pass one `JobHandle` to the next job to preserve ordering without blocking the main thread between jobs:

```csharp
JobHandle first = new AddJob
{
    Values = values,
    Delta = 1
}.Schedule(values.Length, 4096);

JobHandle second = new AddJob
{
    Values = values,
    Delta = 2
}.Schedule(values.Length, 4096, first);

second.Complete();
```

Use `JobHandle.CombineDependencies(first, second)` when a job has multiple prerequisites.

### IJobChunk

`IJobChunk` receives one matching Chunk at a time and gives explicit control over component-array iteration (the scheduling extensions live in `EntJoy.ECS.JobSystem` and must be imported):

```csharp
public struct MoveChunkJob : IJobChunk
{
    public float DeltaTime;

    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        Span<Position> positions = chunk.GetComponentDataSpan<Position>();
        Span<Velocity> velocities = chunk.GetComponentDataSpan<Velocity>();

        for (int i = 0; i < positions.Length; i++)
        {
            Position position = positions[i];
            position.Value += velocities[i].Value * DeltaTime;
            positions[i] = position;
        }
    }
}

new MoveChunkJob { DeltaTime = 1f / 60f }
    .Schedule(new QueryBuilder().WithAll<Position, Velocity>())
    .Complete();
```

### IJobEntity

`IJobEntity` offers a concise per-entity `Execute` signature. The source generator derives component access and query requirements from its parameters:

```csharp
public struct MoveEntityJob : IJobEntity
{
    public float DeltaTime;

    public void Execute(ref Position position, in Velocity velocity)
    {
        position.Value += velocity.Value * DeltaTime;
    }
}

var moveQuery = new QueryBuilder().WithAll<Position, Velocity>();
new MoveEntityJob { DeltaTime = 1f / 60f }
    .Schedule(moveQuery)
    .Complete();
```

Choose a job type according to the work shape:

| Type | Intended use |
| --- | --- |
| `IJob` | One general task |
| `IJobFor` | A serial index loop inside one job |
| `IJobParallelFor` | Independent indices processed in parallel |
| `IJobParallelForBatch` | Contiguous batches with fewer callbacks and scheduling operations |
| `IJobChunk` | Direct ECS Chunk and component-array access |
| `IJobEntity` | Concise per-entity ECS component access |

See [`01_JobSystem`](samples/EntJoySample/01_JobSystem) and [`02_IJobChunkECS`](samples/EntJoySample/02_IJobChunkECS) for working samples.

## NativeTranspiler Example

NativeTranspiler preserves the C# job definition and scheduling API while generating the supported `Execute` body for C++ or ISPC. These three jobs express the same movement operation:

```csharp
public struct MoveJobCSharp : IJobEntity
{
    public float DeltaTime;

    public void Execute(ref Position position, in Velocity velocity)
    {
        position.Value += velocity.Value * DeltaTime;
    }
}

[NativeTranspiler.NativeTranspile(
    Target = NativeTranspiler.BackendTarget.Cpp)]
public struct MoveJobCpp : IJobEntity
{
    public float DeltaTime;

    public void Execute(ref Position position, in Velocity velocity)
    {
        position.Value += velocity.Value * DeltaTime;
    }
}

[NativeTranspiler.NativeTranspile(
    Target = NativeTranspiler.BackendTarget.Ispc,
    MathLib = NativeTranspiler.IspcMathLib.fast)]
public struct MoveJobIspc : IJobEntity
{
    public float DeltaTime;

    public void Execute(ref Position position, in Velocity velocity)
    {
        position.Value += velocity.Value * DeltaTime;
    }
}
```

All backends use the same query and scheduling form:

```csharp
var query = new QueryBuilder().WithAll<Position, Velocity>();

new MoveJobCSharp { DeltaTime = dt }.Schedule(query).Complete();
new MoveJobCpp    { DeltaTime = dt }.Schedule(query).Complete();
new MoveJobIspc   { DeltaTime = dt }.Schedule(query).Complete();
```

Optional modes:

```csharp
// C++ fast math
[NativeTranspiler.NativeTranspile(
    Target = NativeTranspiler.BackendTarget.Cpp,
    CppMathLib = NativeTranspiler.CppMathLib.fast)]

// ISPC fast math with ISPC task-based multithreading
[NativeTranspiler.NativeTranspile(
    Target = NativeTranspiler.BackendTarget.Ispc,
    MathLib = NativeTranspiler.IspcMathLib.fast,
    UseISPC_MT = true)]
```

NativeTranspiler is not a complete C# compiler. Transpiled jobs should use unmanaged fields, parameters, and local data; supported EntJoy math and Native Collections; and supported expressions and control flow. Avoid managed allocation, `string`, regular arrays, classes, reflection, GC-dependent behavior, and unsupported .NET APIs. Treat generator diagnostics such as `NT001` as build errors rather than editing generated code.

### How to write and configure (quick reference)

| Topic | Key points |
| --- | --- |
| Supported shapes | Array-shaped: `IJob` / `IJobFor` / `IJobParallelFor` / `IJobParallelForBatch` (**usable without any ECS reference**); ECS-shaped: `IJobChunk` / `IJobEntity` (require `EntJoy.ECS`; scheduling extensions live in `EntJoy.ECS.JobSystem` and must be imported) |
| Attribute options | `Target` (Cpp/Ispc), `MathLib`, `CppMathLib`, `UseISPC_MT`, `AutoSIMD`, `MathPrecision`, `DisabledAutoRefresh`; mismatched option/backend combinations are hard **errors** (NT018–NT022, NT025) |
| MSBuild properties | `EntJoyNativeDllDir` (native dir), `EntJoyPrebuiltNativeDir` (non-empty ⇒ link the prebuilt NativeDll; used by package mode), `EnableNativeCompile=false` (skip CMake); when wiring by hand you **must** list them in `CompilerVisibleProperty` |
| CMake options | `NATIVE_SIMD_LEVEL` (AUTO/AVX2/AVX/SSE4/NEON/SCALAR), `NATIVE_SIMD_MATH_PRECISION` (1/2/3), `ENTJOY_ENABLE_SENTINEL` (OFF by default) |
| Toolchain | C# only: .NET 8 SDK; Cpp backend: + CMake/MSVC (falls back to MSVC when ClangCL is absent); ISPC backend: + `ispc` on `PATH` |
| Outputs | `<project>/NativeTranspiler_Generated/` (cpp/h/ispc + `CMakeLists.txt` + hash-gate files + `build/Release/{NativeDll,NativeTranspiled}.dll`); both native DLLs must end up in the same directory at runtime |
| Common failures | `CS0234/CS0246` plus **NT030** = generated code coupled to ECS; `__ENTJOY_UNSUPPORTED_*` / `silently-degraded` = unsupported construct inside the job (build fails with file + line) |

Full details (option tables, backend compatibility matrix, troubleshooting, minimal runnable references): [`docs/public/Native-Jobs-Guide.md`](docs/public/Native-Jobs-Guide.md).

Generated files live under `NativeTranspiler_Generated` and normally should not be edited manually.

Working sources:

- [`03_NativeTranspiler`](samples/EntJoySample/03_NativeTranspiler)
- [`IJobChunkMoveCompareSample.cs`](samples/EntJoySample/02_IJobChunkECS/IJobChunkMoveCompareTest/IJobChunkMoveCompareSample.cs)
- [`NativeTranspiler_Generated`](samples/EntJoySample/NativeTranspiler_Generated)

## Samples

[`samples/EntJoySample`](samples/EntJoySample) contains the following cases:

### 01 JobSystem

- [`CSharpJobManagedContextTest`](samples/EntJoySample/01_JobSystem/CSharpJobManagedContextTest): compares unmanaged raw-copy and managed `GCHandle` job contexts across `IJob`, `IJobParallelFor`, and `IJobChunk`.
- [`HeavyJob`](samples/EntJoySample/01_JobSystem/HeavyJob): heavy-compute and full-CPU-load job experiments.
- [`IJobChunkScheduleOverheadTest`](samples/EntJoySample/01_JobSystem/IJobChunkScheduleOverheadTest): compares fixed scheduling overhead for empty and very light C#, C++, and ISPC `IJobChunk` workloads.
- [`JobProfilerTest`](samples/EntJoySample/01_JobSystem/JobProfilerTest): validates Job Profiler sampling and statistics.
- [`ParallelRwConflictTest`](samples/EntJoySample/01_JobSystem/ParallelRwConflictTest): demonstrates parallel read/write conflict detection — cross-job write conflicts, main-thread access blocked while a job references a native container, and release after `Complete()`.

### 02 IJobChunk ECS

- [`SimpleIJobChunkTest`](samples/EntJoySample/02_IJobChunkECS/SimpleIJobChunkTest): minimal component, entity, query, and `IJobChunk` scheduling example.
- [`IJobChunkMoveCompareTest`](samples/EntJoySample/02_IJobChunkECS/IJobChunkMoveCompareTest): runs Light, Heavy, and Sleep C#/C++/ISPC `IJobChunk` and `IJobEntity` comparisons over one million entities and verifies parity.
- [`SpritesRandomMoveLikeTest`](samples/EntJoySample/02_IJobChunkECS/SpritesRandomMoveLikeTest): continuous one-million-entity movement using ECS Chunk, C# Job, Native C++ Job, and Native ISPC Job modes with parity validation.

### 03 NativeTranspiler

- [`MovementTest`](samples/EntJoySample/03_NativeTranspiler/MovementTest): generates array and ECS movement jobs from C# to C++/ISPC, including frame-loop and correctness validation.
- [`StaticMethodTest`](samples/EntJoySample/03_NativeTranspiler/StaticMethodTest): validates transpilation of static methods and calls.
- [`ISPCMT`](samples/EntJoySample/03_NativeTranspiler/ISPCMT): compares regular ISPC with the `UseISPC_MT` multithreaded mode.

### 04 Native Collections

- [`NativeListTest`](samples/EntJoySample/04_NativeCollections/NativeListTest): validates `NativeList<T>` allocation, access, and disposal.
- [`NativeColletionStructTest`](samples/EntJoySample/04_NativeCollections/NativeColletionStructTest): validates Native Collections stored in struct fields.
- [`AtomicTest`](samples/EntJoySample/04_NativeCollections/AtomicTest): validates atomic addition and related atomic operations in parallel jobs.

### 05 Algorithms

- [`GridSearch`](samples/EntJoySample/05_Algorithms/GridSearch): experiments with 2D grid construction, nearest-point lookup, and range search. (Entirely commented out since 2026-08)

### 06 HotField Handle

- [`HotFieldHandle`](samples/EntJoySample/06_HotFieldHandle): HotField feasibility prototype — an ordinary class + `[HotFieldEntity]` attribute → field-level SoA storage (`HotStore`) + int index + `ref`-property redirection, with Systems (`IJobParallelFor`) consuming the same store directly. Verifies that OOP game code stays byte-identical to a plain class (seamless), the attribute machinery is zero-cost, and OOD↔DOD share storage with identical results; the dense-1M OOP SoA structural tax is reported honestly (bulk goes through Systems).

### 08 Entity Random Access

- [`RandomAccess`](samples/EntJoySample/08_EntityRandomAccess): sparse Entity random-access overhead benchmark (ComponentLookup optimization).

Run performance samples in Release x64 without a debugger, and keep power mode and background load consistent. This README intentionally avoids fixed results from one machine; run the samples on the target hardware for meaningful comparisons.

## Troubleshooting

### CMake cannot find ISPC

Add the directory containing `ispc.exe` to `PATH`, then fully restart the terminal and Visual Studio:

```powershell
where.exe ispc
ispc --version
```

### MSVC `cl.exe` is missing

Install Desktop development with C++, MSVC v143, and a Windows SDK through Visual Studio Installer. Build from Developer PowerShell for VS 2022.

### Submodules are missing

If the checkout is missing the `src/NativeDll/thirdParty/imgui` submodule (CMake fails with `Cannot find source file: .../thirdParty/imgui/imgui.cpp`), initialize it:

```powershell
git submodule update --init --recursive
```

### `NativeDll.dll` was not generated

Inspect the build log for CMake, MSVC, or ISPC errors. If necessary, remove the generated `build` cache and rebuild Release. A successful build produces `bin\NativeDll.dll`.

### Why is Debug much slower?

Debug builds reduce JIT, C#, C++, and linker optimization and may enable additional checks. Scheduling and large-entity benchmarks must use `-c Release` without an attached debugger.

### The ISPC program reports an illegal instruction

Current ISPC samples target AVX-512 SKX. Verify CPU support or change the NativeTranspiler ISPC target and regenerate native code.

### Build fails with `NativeTranspiler generated silently-degraded code`

The generator writes a unique marker (`__ENTJOY_UNSUPPORTED_STMT__…` / `__ENTJOY_UNSUPPORTED_EXPR__…`) for any
construct it cannot translate, and `NativeCompileTask` refuses to compile that output — this is deliberate: a
silently dropped statement used to produce code that compiled but computed nothing (or an empty function body).
Locate the reported `file(line): // __ENTJOY_UNSUPPORTED_STMT__<construct>`, then either rewrite that job or extend
the generator. Boundaries, workarounds and the full diagnostic table (NT001–NT025) live in
[`docs/public/NativeTranspiler-Boundaries-and-Diagnostics.md`](docs/public/NativeTranspiler-Boundaries-and-Diagnostics.md).

### The build looks like it used stale generated code

Roslyn's `CoreCompile` content-hash gating can skip the source generator entirely when only the *generator* changed,
while the native compile task then skips CMake — so the previous artifacts get compiled again. The task now detects
this (`generator.stamp` vs the generator assembly hash) and emits a warning with the exact recipe: delete
`<project>\.godot\mono\temp\obj\<Configuration>` (or `obj\<Configuration>`),
`NativeTranspiler_Generated\build` and `NativeTranspiler_Generated\native_compile.hash`, then rebuild.

### Where are the NativeTranspiler rules and limits documented?

[`docs/public/NativeTranspiler-Boundaries-and-Diagnostics.md`](docs/public/NativeTranspiler-Boundaries-and-Diagnostics.md):
the silent-degradation gate, batch-loop `return` semantics, `IJobParallelForBatch` support, ISPC call-site bridging
(return value / pointer / `ref`-`out` slot), the NT diagnostic table, staleness gating, and the CI + regression fixture
(`tools/NativeTranspilerFixture`).

## Acknowledgements and Inspirations

EntJoy's design and implementation are informed by:

- [Unity DOTS / Entities](https://unity.com/dots), the primary design reference for data-oriented ECS, Chunks, and Job workflows.
- [coinsoundsbetter/EntJoy](https://github.com/coinsoundsbetter/EntJoy), the starting point of the early project.
- [Arch](https://github.com/genaray/Arch), a reference for high-performance Archetype ECS design.
- [Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS), a reference for C# ECS APIs and data layout.
- [Intel ISPC](https://ispc.github.io/), the SPMD/SIMD native compute backend.

Thanks to the authors and contributors of these projects for making their work available and enabling EntJoy to continue exploring a combined C#, C++, and SIMD ECS stack.
