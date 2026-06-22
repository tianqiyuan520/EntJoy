# EntJoy

[![Ask DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/tianqiyuan520/EntJoy)

**中文** | [English](#english)

EntJoy 是一个由 **C#、C++ 和 ISPC** 编写的 Archetype ECS 与 JobSystem 技术栈。它借鉴 Unity DOTS 的数据导向设计：实体数据按 Archetype 和 Chunk 连续存储，工作通过统一 JobSystem 并行调度，同一份 C# Job 还可以由 Source Generator 转译为 C++ 或 ISPC 后端。

项目目前提供：

- Archetype/Chunk ECS、Entity、Component、Query 和 Enableable Component。
- `IJob`、`IJobFor`、`IJobParallelFor`、`IJobParallelForBatch`、`IJobChunk` 和 `IJobEntity`。
- `JobHandle` 依赖、组合依赖和 `Complete()` 协作执行。
- C#、C++、ISPC 共用的原生工作线程调度器。
- 将受支持的 C# Job 自动生成 C++/ISPC 代码的 NativeTranspiler。
- `NativeArray<T>`、`NativeList<T>`、原子操作和数学类型等底层工具。
- 覆盖功能、正确性和性能对比的 [EntJoySample](src/EntJoySample)。

> 当前仓库仅适配并验证了 **Windows x64、.NET 8、MSVC 和 Intel ISPC** 工具链。GCC、G++ 和 Clang 暂不属于当前支持范围。API 仍在演进，现阶段通过源码项目引用使用，尚未发布 NuGet 包。

## 目录

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
5. **NativeTranspiler** 将标记了 `[NativeTranspile]` 的受支持 C# 代码生成 C++ 或 ISPC。
6. **NativeDll** 编译生成的代码，并提供统一的原生调度器和运行时 ABI。

| 目录 | 作用 |
| --- | --- |
| [`src/EntJoy`](src/EntJoy) | ECS、Query、JobSystem、Native Collections 和基础运行时 |
| [`src/EntJoy.SourceGenerator`](src/EntJoy.SourceGenerator) | ECS Job 的 C# Source Generator |
| [`src/NativeTranspiler`](src/NativeTranspiler) | C# 到 C++/ISPC 的生成器与分析器 |
| [`src/NativeTranspiler.Tasks`](src/NativeTranspiler.Tasks) | 从 MSBuild 调用 CMake 的自定义任务 |
| [`src/NativeDll`](src/NativeDll) | C++ JobSystem、Profiler 和原生容器支持 |
| [`src/EntJoySample`](src/EntJoySample) | 功能验证、用法示例和性能测试 |
| [`external/cpp-taskflow`](external/cpp-taskflow) | JobSystem 使用的 Taskflow 子模块 |

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

> 当前生成的 ISPC 样例使用 `avx512skx-i32x16` 目标。运行 ISPC 后端前，请确认 CPU 支持对应的 AVX-512 指令；否则请只运行 C#、C++ 后端，或修改生成器目标后重新构建。

### 3. 克隆仓库

Taskflow 是 Git 子模块，因此请递归克隆：

```powershell
git clone --recursive https://github.com/tianqiyuan520/EntJoy.git
cd EntJoy
```

如果已经使用普通 `git clone`，补充初始化子模块：

```powershell
git submodule update --init --recursive
```

### 4. Release 构建

```powershell
dotnet build src/EntJoySample/EntJoySample.csproj -c Release
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

当前启用的入口位于 [`IJobChunkMoveCompareTest/Program.cs`](src/EntJoySample/02_IJobChunkECS/IJobChunkMoveCompareTest/Program.cs)。样例工程采用“同一时间只启用一个 `Program.Main`”的约定；切换样例时，请注释当前入口并取消目标目录中 `Program.cs` 的注释。

## 配置自己的项目

EntJoy 当前通过源码引用使用。最简单的做法是在仓库 `src` 下创建项目，并保持它与 `EntJoy`、`NativeDll` 等目录的相对位置。完整可用配置请参考 [`EntJoySample.csproj`](src/EntJoySample/EntJoySample.csproj)。

下面是一个与当前构建链匹配的项目文件模板。将 `MyEntJoyApp` 放在 `src/MyEntJoyApp` 时，可以直接使用这些相对路径：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <BaseOutputPath>..\..\bin</BaseOutputPath>
    <OutputPath>..\..\bin</OutputPath>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
  </PropertyGroup>

  <ItemGroup>
    <Compile Remove="NativeTranspiler_Generated\**" />
    <EmbeddedResource Remove="NativeTranspiler_Generated\**" />
    <None Remove="NativeTranspiler_Generated\**" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\EntJoy\EntJoy.csproj" />
    <ProjectReference Include="..\EntJoy.SourceGenerator\EntJoy.SourceGenerator.csproj"
                      OutputItemType="Analyzer"
                      ReferenceOutputAssembly="false" />
    <ProjectReference Include="..\NativeTranspiler\NativeTranspiler.csproj"
                      OutputItemType="Analyzer"
                      ReferenceOutputAssembly="false" />
    <ProjectReference Include="..\NativeTranspiler.Tasks\NativeTranspiler.Tasks.csproj"
                      ReferenceOutputAssembly="false"
                      Private="false"
                      BuildReference="false" />
  </ItemGroup>

  <UsingTask TaskName="NativeTranspiler.Tasks.NativeCompileTask"
             AssemblyFile="$(MSBuildThisFileDirectory)..\NativeTranspiler.Tasks\bin\$(Configuration)\netstandard2.0\NativeTranspiler.Tasks.dll"
             TaskFactory="TaskHostFactory" />

  <Target Name="BuildNativeTranspilerTasks"
          BeforeTargets="CompileCppWithCustomTask"
          Inputs="$(MSBuildThisFileDirectory)..\NativeTranspiler.Tasks\NativeCompileTask .cs;$(MSBuildThisFileDirectory)..\NativeTranspiler.Tasks\NativeTranspiler.Tasks.csproj"
          Outputs="$(MSBuildThisFileDirectory)..\NativeTranspiler.Tasks\bin\$(Configuration)\netstandard2.0\NativeTranspiler.Tasks.dll">
    <MSBuild Projects="$(MSBuildThisFileDirectory)..\NativeTranspiler.Tasks\NativeTranspiler.Tasks.csproj"
             Targets="Build"
             Properties="Configuration=$(Configuration)" />
  </Target>

  <Target Name="CompileCppWithCustomTask"
          AfterTargets="CoreCompile"
          DependsOnTargets="BuildNativeTranspilerTasks"
          Condition="Exists('$(MSBuildThisFileDirectory)NativeTranspiler_Generated\CMakeLists.txt')">
    <Message Importance="high" Text="Starting native compilation for generated C++ code..." />
    <NativeCompileTask NativeCodeGenDir="$(MSBuildThisFileDirectory)NativeTranspiler_Generated\" />
  </Target>

  <Target Name="CopyNativeDll" AfterTargets="CompileCppWithCustomTask">
    <ItemGroup>
      <NativeDll Include="$(MSBuildThisFileDirectory)NativeTranspiler_Generated\build\Release\NativeDll.dll" />
    </ItemGroup>
    <Copy SourceFiles="@(NativeDll)"
          DestinationFolder="$(OutputPath)"
          SkipUnchangedFiles="false" />
  </Target>
</Project>
```

如果项目不在上述目录结构中，需要同时调整 `ProjectReference`、`NativeCompileTask` 和原生运行时的相对路径。

## ECS 示例

### 核心概念

- **Entity**：轻量 ID，本身不保存业务行为。
- **Component**：实现 `IComponentData` 的 unmanaged 数据结构。
- **Archetype**：一组确定的组件类型；组件集合相同的实体属于同一个 Archetype。
- **Chunk**：同一 Archetype 内连续存储实体和组件数据的内存块。
- **Query**：选择满足组件条件的 Archetype 和 Chunk。

下面的示例创建位置和速度组件、生成实体，并同步遍历匹配的 Chunk：

```csharp
using EntJoy;
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

- 最小 Chunk Job：[`SimpleIJobChunkTest`](src/EntJoySample/02_IJobChunkECS/SimpleIJobChunkTest)
- 百万实体 C#/C++/ISPC 对比：[`IJobChunkMoveCompareTest`](src/EntJoySample/02_IJobChunkECS/IJobChunkMoveCompareTest)
- ECS 样例集合：[`02_IJobChunkECS`](src/EntJoySample/02_IJobChunkECS)

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

`IJobChunk` 每次接收一个匹配的 Chunk，适合手工控制组件数组遍历：

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

更多示例见 [`01_JobSystem`](src/EntJoySample/01_JobSystem) 和 [`02_IJobChunkECS`](src/EntJoySample/02_IJobChunkECS)。

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

完整对比代码：

- [`03_NativeTranspiler`](src/EntJoySample/03_NativeTranspiler)
- [`IJobChunkMoveCompareSample.cs`](src/EntJoySample/02_IJobChunkECS/IJobChunkMoveCompareTest/IJobChunkMoveCompareSample.cs)
- [`NativeTranspiler_Generated`](src/EntJoySample/NativeTranspiler_Generated)

## 样例项目

[`src/EntJoySample`](src/EntJoySample) 包含以下案例：

### 01 JobSystem

- [`CSharpJobManagedContextTest`](src/EntJoySample/01_JobSystem/CSharpJobManagedContextTest)：比较 unmanaged raw-copy 与 managed `GCHandle` Job context，并覆盖 `IJob`、`IJobParallelFor` 和 `IJobChunk`。
- [`HeavyJob`](src/EntJoySample/01_JobSystem/HeavyJob)：重计算和 CPU 满负载 Job 实验。
- [`IJobChunkScheduleOverheadTest`](src/EntJoySample/01_JobSystem/IJobChunkScheduleOverheadTest)：比较 C#、C++、ISPC `IJobChunk` 空任务与极轻任务的固定调度开销。
- [`JobProfilerTest`](src/EntJoySample/01_JobSystem/JobProfilerTest)：验证 Job Profiler 的采样和统计功能。

### 02 IJobChunk ECS

- [`SimpleIJobChunkTest`](src/EntJoySample/02_IJobChunkECS/SimpleIJobChunkTest)：创建组件和实体、构造查询并调度 `IJobChunk` 的最小示例。
- [`IJobChunkMoveCompareTest`](src/EntJoySample/02_IJobChunkECS/IJobChunkMoveCompareTest)：对 100 万实体运行 C#、C++、ISPC 的 `IJobChunk`/`IJobEntity` Light、Heavy 和 Sleep 对比，并验证结果一致性。
- [`SpritesRandomMoveLikeTest`](src/EntJoySample/02_IJobChunkECS/SpritesRandomMoveLikeTest)：百万实体持续移动场景，对比 ECS Chunk、C# Job、Native C++ Job 和 Native ISPC Job，并提供 parity 验证。

### 03 NativeTranspiler

- [`MovementTest`](src/EntJoySample/03_NativeTranspiler/MovementTest)：展示数组和 ECS 移动 Job 从 C# 生成到 C++/ISPC，并进行帧循环和正确性验证。
- [`StaticMethodTest`](src/EntJoySample/03_NativeTranspiler/StaticMethodTest)：验证静态方法及其调用的 NativeTranspiler 转译。
- [`ISPCMT`](src/EntJoySample/03_NativeTranspiler/ISPCMT)：比较普通 ISPC 与 `UseISPC_MT` 多线程执行模式。

### 04 Native Collections

- [`NativeListTest`](src/EntJoySample/04_NativeCollections/NativeListTest)：验证 `NativeList<T>` 的分配、访问和释放。
- [`NativeColletionStructTest`](src/EntJoySample/04_NativeCollections/NativeColletionStructTest)：验证 Native Collection 作为结构体字段使用的场景。
- [`AtomicTest`](src/EntJoySample/04_NativeCollections/AtomicTest)：验证并行 Job 中的原子加法等原子操作。

### 05 Algorithms

- [`GridSearch`](src/EntJoySample/05_Algorithms/GridSearch)：二维网格构建、最近点和范围搜索实验。

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

### Taskflow 头文件不存在

初始化子模块：

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
- [Taskflow](https://github.com/taskflow/taskflow)：原生任务执行与依赖调度基础设施。
- [Intel ISPC](https://ispc.github.io/)：面向 SPMD/SIMD 的原生计算后端。

感谢这些项目的作者和贡献者公开他们的工作，使 EntJoy 能够在已有经验之上继续探索 C#、C++ 与 SIMD ECS 技术栈。

---

<a id="english"></a>

# EntJoy (English)

[中文](#entjoy) | **English**

EntJoy is an Archetype ECS and JobSystem stack written in **C#, C++, and ISPC**. Inspired by the data-oriented design of Unity DOTS, it stores entities by Archetype and Chunk, schedules work through a unified JobSystem, and can transpile supported C# jobs to C++ or ISPC with source generators.

The project currently provides:

- Archetype/Chunk ECS, entities, components, queries, and enableable components.
- `IJob`, `IJobFor`, `IJobParallelFor`, `IJobParallelForBatch`, `IJobChunk`, and `IJobEntity`.
- `JobHandle` dependencies, combined dependencies, and cooperative `Complete()` execution.
- A native worker scheduler shared by C#, C++, and ISPC backends.
- NativeTranspiler source generation from supported C# jobs to C++ or ISPC.
- Low-level utilities such as `NativeArray<T>`, `NativeList<T>`, atomics, and math types.
- Functional, correctness, and performance samples in [EntJoySample](src/EntJoySample).

> The repository currently supports and verifies only the **Windows x64, .NET 8, MSVC, and Intel ISPC** toolchain. GCC, G++, and Clang are not currently supported. APIs are still evolving. EntJoy is consumed through project references and is not yet published as a NuGet package.

## Contents

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
5. **NativeTranspiler** generates C++ or ISPC from supported C# code marked with `[NativeTranspile]`.
6. **NativeDll** compiles generated code and provides the shared native scheduler and runtime ABI.

| Directory | Purpose |
| --- | --- |
| [`src/EntJoy`](src/EntJoy) | ECS, queries, JobSystem, Native Collections, and managed runtime |
| [`src/EntJoy.SourceGenerator`](src/EntJoy.SourceGenerator) | C# source generator for ECS jobs |
| [`src/NativeTranspiler`](src/NativeTranspiler) | C#-to-C++/ISPC generator and analyzer |
| [`src/NativeTranspiler.Tasks`](src/NativeTranspiler.Tasks) | Custom MSBuild task that invokes CMake |
| [`src/NativeDll`](src/NativeDll) | C++ JobSystem, profiler, and native container support |
| [`src/EntJoySample`](src/EntJoySample) | Usage, correctness, and performance samples |
| [`external/cpp-taskflow`](external/cpp-taskflow) | Taskflow Git submodule used by JobSystem |

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

> Generated ISPC samples currently target `avx512skx-i32x16`. Confirm that the machine supports the corresponding AVX-512 instructions before running the ISPC backend. Otherwise, use the C# or C++ backend, or change the generator target and rebuild.

### 3. Clone

Taskflow is a Git submodule, so clone recursively:

```powershell
git clone --recursive https://github.com/tianqiyuan520/EntJoy.git
cd EntJoy
```

For an existing non-recursive clone:

```powershell
git submodule update --init --recursive
```

### 4. Build Release

```powershell
dotnet build src/EntJoySample/EntJoySample.csproj -c Release
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

The active entry point is currently [`IJobChunkMoveCompareTest/Program.cs`](src/EntJoySample/02_IJobChunkECS/IJobChunkMoveCompareTest/Program.cs). EntJoySample keeps only one `Program.Main` enabled at a time. To switch samples, comment the current entry point and uncomment the `Program.cs` in the target sample directory.

## Configure Your Own Project

EntJoy is currently consumed from source. The simplest layout is to create a project under the repository's `src` directory so it remains a sibling of `EntJoy`, `NativeDll`, and the generator projects. See [`EntJoySample.csproj`](src/EntJoySample/EntJoySample.csproj) for the complete working configuration.

The following template matches the current build pipeline. It can be used directly when `MyEntJoyApp` is located at `src/MyEntJoyApp`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <BaseOutputPath>..\..\bin</BaseOutputPath>
    <OutputPath>..\..\bin</OutputPath>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
  </PropertyGroup>

  <ItemGroup>
    <Compile Remove="NativeTranspiler_Generated\**" />
    <EmbeddedResource Remove="NativeTranspiler_Generated\**" />
    <None Remove="NativeTranspiler_Generated\**" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\EntJoy\EntJoy.csproj" />
    <ProjectReference Include="..\EntJoy.SourceGenerator\EntJoy.SourceGenerator.csproj"
                      OutputItemType="Analyzer"
                      ReferenceOutputAssembly="false" />
    <ProjectReference Include="..\NativeTranspiler\NativeTranspiler.csproj"
                      OutputItemType="Analyzer"
                      ReferenceOutputAssembly="false" />
    <ProjectReference Include="..\NativeTranspiler.Tasks\NativeTranspiler.Tasks.csproj"
                      ReferenceOutputAssembly="false"
                      Private="false"
                      BuildReference="false" />
  </ItemGroup>

  <UsingTask TaskName="NativeTranspiler.Tasks.NativeCompileTask"
             AssemblyFile="$(MSBuildThisFileDirectory)..\NativeTranspiler.Tasks\bin\$(Configuration)\netstandard2.0\NativeTranspiler.Tasks.dll"
             TaskFactory="TaskHostFactory" />

  <Target Name="BuildNativeTranspilerTasks"
          BeforeTargets="CompileCppWithCustomTask"
          Inputs="$(MSBuildThisFileDirectory)..\NativeTranspiler.Tasks\NativeCompileTask .cs;$(MSBuildThisFileDirectory)..\NativeTranspiler.Tasks\NativeTranspiler.Tasks.csproj"
          Outputs="$(MSBuildThisFileDirectory)..\NativeTranspiler.Tasks\bin\$(Configuration)\netstandard2.0\NativeTranspiler.Tasks.dll">
    <MSBuild Projects="$(MSBuildThisFileDirectory)..\NativeTranspiler.Tasks\NativeTranspiler.Tasks.csproj"
             Targets="Build"
             Properties="Configuration=$(Configuration)" />
  </Target>

  <Target Name="CompileCppWithCustomTask"
          AfterTargets="CoreCompile"
          DependsOnTargets="BuildNativeTranspilerTasks"
          Condition="Exists('$(MSBuildThisFileDirectory)NativeTranspiler_Generated\CMakeLists.txt')">
    <Message Importance="high" Text="Starting native compilation for generated C++ code..." />
    <NativeCompileTask NativeCodeGenDir="$(MSBuildThisFileDirectory)NativeTranspiler_Generated\" />
  </Target>

  <Target Name="CopyNativeDll" AfterTargets="CompileCppWithCustomTask">
    <ItemGroup>
      <NativeDll Include="$(MSBuildThisFileDirectory)NativeTranspiler_Generated\build\Release\NativeDll.dll" />
    </ItemGroup>
    <Copy SourceFiles="@(NativeDll)"
          DestinationFolder="$(OutputPath)"
          SkipUnchangedFiles="false" />
  </Target>
</Project>
```

If your project uses a different directory layout, update the project references, `NativeCompileTask`, and native runtime paths together.

## ECS Example

### Concepts

- **Entity** is a lightweight ID without application behavior.
- **Component** is an unmanaged data struct implementing `IComponentData`.
- **Archetype** describes a fixed component set shared by a group of entities.
- **Chunk** stores entity and component arrays contiguously for an Archetype.
- **Query** selects Archetypes and Chunks by component conditions.

The following example defines position and velocity components, creates entities, and synchronously iterates matching Chunks:

```csharp
using EntJoy;
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

- Minimal Chunk job: [`SimpleIJobChunkTest`](src/EntJoySample/02_IJobChunkECS/SimpleIJobChunkTest)
- One-million-entity C#/C++/ISPC comparison: [`IJobChunkMoveCompareTest`](src/EntJoySample/02_IJobChunkECS/IJobChunkMoveCompareTest)
- ECS sample collection: [`02_IJobChunkECS`](src/EntJoySample/02_IJobChunkECS)

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

`IJobChunk` receives one matching Chunk at a time and gives explicit control over component-array iteration:

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

See [`01_JobSystem`](src/EntJoySample/01_JobSystem) and [`02_IJobChunkECS`](src/EntJoySample/02_IJobChunkECS) for working samples.

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

Generated files live under `NativeTranspiler_Generated` and normally should not be edited manually.

Working sources:

- [`03_NativeTranspiler`](src/EntJoySample/03_NativeTranspiler)
- [`IJobChunkMoveCompareSample.cs`](src/EntJoySample/02_IJobChunkECS/IJobChunkMoveCompareTest/IJobChunkMoveCompareSample.cs)
- [`NativeTranspiler_Generated`](src/EntJoySample/NativeTranspiler_Generated)

## Samples

[`src/EntJoySample`](src/EntJoySample) contains the following cases:

### 01 JobSystem

- [`CSharpJobManagedContextTest`](src/EntJoySample/01_JobSystem/CSharpJobManagedContextTest): compares unmanaged raw-copy and managed `GCHandle` job contexts across `IJob`, `IJobParallelFor`, and `IJobChunk`.
- [`HeavyJob`](src/EntJoySample/01_JobSystem/HeavyJob): heavy-compute and full-CPU-load job experiments.
- [`IJobChunkScheduleOverheadTest`](src/EntJoySample/01_JobSystem/IJobChunkScheduleOverheadTest): compares fixed scheduling overhead for empty and very light C#, C++, and ISPC `IJobChunk` workloads.
- [`JobProfilerTest`](src/EntJoySample/01_JobSystem/JobProfilerTest): validates Job Profiler sampling and statistics.

### 02 IJobChunk ECS

- [`SimpleIJobChunkTest`](src/EntJoySample/02_IJobChunkECS/SimpleIJobChunkTest): minimal component, entity, query, and `IJobChunk` scheduling example.
- [`IJobChunkMoveCompareTest`](src/EntJoySample/02_IJobChunkECS/IJobChunkMoveCompareTest): runs Light, Heavy, and Sleep C#/C++/ISPC `IJobChunk` and `IJobEntity` comparisons over one million entities and verifies parity.
- [`SpritesRandomMoveLikeTest`](src/EntJoySample/02_IJobChunkECS/SpritesRandomMoveLikeTest): continuous one-million-entity movement using ECS Chunk, C# Job, Native C++ Job, and Native ISPC Job modes with parity validation.

### 03 NativeTranspiler

- [`MovementTest`](src/EntJoySample/03_NativeTranspiler/MovementTest): generates array and ECS movement jobs from C# to C++/ISPC, including frame-loop and correctness validation.
- [`StaticMethodTest`](src/EntJoySample/03_NativeTranspiler/StaticMethodTest): validates transpilation of static methods and calls.
- [`ISPCMT`](src/EntJoySample/03_NativeTranspiler/ISPCMT): compares regular ISPC with the `UseISPC_MT` multithreaded mode.

### 04 Native Collections

- [`NativeListTest`](src/EntJoySample/04_NativeCollections/NativeListTest): validates `NativeList<T>` allocation, access, and disposal.
- [`NativeColletionStructTest`](src/EntJoySample/04_NativeCollections/NativeColletionStructTest): validates Native Collections stored in struct fields.
- [`AtomicTest`](src/EntJoySample/04_NativeCollections/AtomicTest): validates atomic addition and related atomic operations in parallel jobs.

### 05 Algorithms

- [`GridSearch`](src/EntJoySample/05_Algorithms/GridSearch): experiments with 2D grid construction, nearest-point lookup, and range search.

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

### Taskflow headers are missing

```powershell
git submodule update --init --recursive
```

### `NativeDll.dll` was not generated

Inspect the build log for CMake, MSVC, or ISPC errors. If necessary, remove the generated `build` cache and rebuild Release. A successful build produces `bin\NativeDll.dll`.

### Why is Debug much slower?

Debug builds reduce JIT, C#, C++, and linker optimization and may enable additional checks. Scheduling and large-entity benchmarks must use `-c Release` without an attached debugger.

### The ISPC program reports an illegal instruction

Current ISPC samples target AVX-512 SKX. Verify CPU support or change the NativeTranspiler ISPC target and regenerate native code.

## Acknowledgements and Inspirations

EntJoy's design and implementation are informed by:

- [Unity DOTS / Entities](https://unity.com/dots), the primary design reference for data-oriented ECS, Chunks, and Job workflows.
- [coinsoundsbetter/EntJoy](https://github.com/coinsoundsbetter/EntJoy), the starting point of the early project.
- [Arch](https://github.com/genaray/Arch), a reference for high-performance Archetype ECS design.
- [Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS), a reference for C# ECS APIs and data layout.
- [Taskflow](https://github.com/taskflow/taskflow), native task execution and dependency infrastructure.
- [Intel ISPC](https://ispc.github.io/), the SPMD/SIMD native compute backend.

Thanks to the authors and contributors of these projects for making their work available and enabling EntJoy to continue exploring a combined C#, C++, and SIMD ECS stack.
