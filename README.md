# EntJoy

**中文** · [English](README.en.md)

用 C# 编写数据与任务，用 C#、C++ 或 ISPC 执行。EntJoy 是不内置渲染器的游戏框架，可由 Godot、Unity 或自研引擎接入渲染层。

[使用指南](docs/public/Native-Jobs-Guide.md) · [运行时契约](docs/public/Runtime-Contracts-and-Known-Limitations.md) · [Sample](#sample-导航) · [Tests 与 Tools](#tests-与-tools) · [DeepWiki](https://deepwiki.com/tianqiyuan520/EntJoy)

## 项目设计

数据、调度与执行分层：ECS 按 Archetype / Chunk 连续存储组件；JobSystem 管理任务依赖与并行执行；NativeTranspiler 在构建期将受支持的 C# Job 转译为 C++ / ISPC。

| 模块 | 职责 |
| --- | --- |
| [EntJoy.ECS](src/EntJoy.ECS/README.md) | 实体、组件、查询、System 与 ECS Job |
| [EntJoy.Jobs](src/EntJoy.Jobs/README.md) | 托管 / 原生调度器、JobHandle、原生转译与构建接线 |
| [EntJoy.Collections](src/EntJoy.Collections/README.md) | 原生容器、分配器与安全检查 |
| [EntJoy.Mathematics](src/EntJoy.Mathematics/README.md) | 数学类型与底层辅助 |

依赖方向为 `ECS → Jobs → Collections → Mathematics`。只写数组 Job 时可单独使用 Jobs。普通 C# Job 优先使用原生调度器，原生运行时不可用时回退托管调度器；转译后的原生内核需要原生运行时。

## 发布与安装

四个包同步发布；当前仓库版本为 **1.0.2**，版本由 [EntJoyVersion](src/Directory.Build.props) 统一管理。

| 包 | NuGet |
| --- | --- |
| EntJoy.ECS | [![NuGet](https://img.shields.io/nuget/v/EntJoy.ECS)](https://www.nuget.org/packages/EntJoy.ECS) |
| EntJoy.Jobs | [![NuGet](https://img.shields.io/nuget/v/EntJoy.Jobs)](https://www.nuget.org/packages/EntJoy.Jobs) |
| EntJoy.Collections | [![NuGet](https://img.shields.io/nuget/v/EntJoy.Collections)](https://www.nuget.org/packages/EntJoy.Collections) |
| EntJoy.Mathematics | [![NuGet](https://img.shields.io/nuget/v/EntJoy.Mathematics)](https://www.nuget.org/packages/EntJoy.Mathematics) |

[nuget.org](https://www.nuget.org/profiles/tianqiyuan520) 支持匿名还原；[GitHub Packages](https://nuget.pkg.github.com/tianqiyuan520/index.json) 是镜像源，需要带 `read:packages` 权限的 classic PAT，建议配置 `packageSourceMapping`。推送与版本一致的 `v*` tag 后，[发布流程](.github/workflows/publish-nuget.yml) 先运行 Release Gate 和包消费验证，再发布到两个源。

### 通过 NuGet 使用

使用 ECS，安装 `EntJoy.ECS`；只写 Job，改装 `EntJoy.Jobs`。所需依赖自动安装。

```powershell
dotnet add package EntJoy.ECS
```

项目使用 `net8.0` 并启用 `AllowUnsafeBlocks`。普通 C# Job 使用包内预编译运行时，无需本地 C++ 工具链；原生转译需要 CMake + MSVC 或 ClangCL，ISPC 后端还需 Intel ISPC。

源码接入与构建配置见[使用指南](docs/public/Native-Jobs-Guide.md)。

## Job 示例

定义一个数组任务：

```csharp
using EntJoy.Collections;
using EntJoy.JobSystem;

public struct AddJob : IJobParallelFor
{
    public NativeArray<int> Values;
    public void Execute(int index)
    {
        Values[index] += 1;
    }
}
```

初始化一次调度器，提交任务并等待结果：

```csharp
JobScheduler.Initialize();
try
{
    using var values = new NativeArray<int>(1000, Allocator.Persistent);
    new AddJob { Values = values }.Schedule(values.Length, 64).Complete();
}
finally
{
    JobScheduler.Shutdown();
}
```

## ECS 示例

创建带位置与速度的实体，再查询更新：

```csharp
using EntJoy.ECS;

public struct Position : IComponentData { public float X; }
public struct Velocity : IComponentData { public float X; }
```

```csharp
using var world = new World("Game");
var em = world.EntityManager;
var entity = em.NewEntity(typeof(Position), typeof(Velocity));
em.Set(entity, new Position { X = 0 });
em.Set(entity, new Velocity { X = 1 });

foreach (var item in world.Query<Position, Velocity>())
    item.Comp0.X += item.Comp1.X;
```

## NativeTranspiler 示例

在上面的 `AddJob` 声明前添加属性，即可将同一份逻辑编译为 C++，调度方式不变：

```csharp
[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
```

改为 `BackendTarget.Ispc` 使用 ISPC。完整的数组与 ECS 转译示例见 [NativeTranspiler Sample](samples/EntJoySample/03_NativeTranspiler)。

## Sample 导航

| Sample | 内容 |
| --- | --- |
| [JobSystem](samples/EntJoySample/01_JobSystem) | 调度、依赖、并行读写检查与 Profiler |
| [ECS Job](samples/EntJoySample/02_IJobChunkECS) | Chunk / Entity Job 与移动对比 |
| [NativeTranspiler](samples/EntJoySample/03_NativeTranspiler) | C++ / ISPC 与 AutoSIMD |
| [Collections](samples/EntJoySample/04_NativeCollections) | 原生容器与原子操作 |
| [ECS](samples/EntJoySample/09_ECS) | 查询、System、事件、关系与快照 |
| [Native Execution](samples/EntJoySample/16_NativeExecution/README.md) | 原生执行、依赖与列视图 |
| [全部 Sample](samples/EntJoySample/README.md) | 算法、SIMD、热重载等，以及入口切换方式 |

## Tests 与 Tools

`tests/` 验证正确性与使用契约；`tools/` 提供原生构建、端到端夹具、压力测试和性能测量。

| 入口 | 用途 |
| --- | --- |
| [EntJoy.ECS.Tests](tests/EntJoy.ECS.Tests) | ECS 与 Managed / Native JobSystem 回归 |
| [NativeDll.Tests](tests/NativeDll.Tests) | C++ 调度、队列、唤醒与生命周期 |
| [NativeTranspiler.Tests](tests/NativeTranspiler.Tests/README.md) | 生成代码与诊断 |
| [HotReload.Tests](tests/HotReload.Tests) | 热重载契约 |
| [JobsOnlyTranspilerCheck](tests/JobsOnlyTranspilerCheck) | 无 ECS 依赖的转译检查 |
| [NuGetConsumer](tests/NuGetConsumer) · [NuGetJobsConsumer](tests/NuGetJobsConsumer) | 包消费与原生执行 |
| [DocLinksCheck](tests/DocLinksCheck) | 文档链接与锚点 |
| [NativeDllCore](tools/NativeDllCore) | 构建核心原生运行时 |
| [NativeTranspilerFixture](tools/NativeTranspilerFixture) | 转译、原生编译与运行断言 |
| [JobSystemStressTest](tools/JobSystemStressTest) · [CSharp](tools/JobSystemStressTest.CSharp) | 原生与 C# 压力测试 |
| [ISPCWidthBoundaryTest](tools/ISPCWidthBoundaryTest) | SIMD 宽度、尾部与边界 |
| [SafetyInterceptProbe](tools/SafetyInterceptProbe) | 容器并发访问检查 |
| [JobLibsBenchmark](tools/JobLibsBenchmark) · [JobSystemPerfBench](tools/JobSystemPerfBench) | 跨框架与调度开销测量 |

`tools/` 默认忽略本地文件，部分工具已入库；其中 JobLibsBenchmark 仅跟踪部分源码，运行所需工程与依赖配置仍需本地准备。CI 的构建命令与验收范围以 [Release Gate](.github/workflows/jobsystem-ci.yml) 为准。

## 注意事项

- 当前 NuGet 原生包仅提供 **Windows x64**；托管测试在 Windows / Linux 运行，不代表原生包支持所有平台。
- Job 完成后才能读写或释放其占用的数据；结构变化前完成相关 Job，任务顺序通过 `JobHandle` 表达。
- Job 按值复制；携带托管对象引用时，对象的并发访问仍由调用方保证。
- NativeTranspiler 支持 C# 子集，原生 Job 使用受支持的非托管数据与 API。边界见[转译诊断](docs/public/NativeTranspiler-Boundaries-and-Diagnostics.md)。
- 当前 ISPC 样例目标为 AVX2；性能测量使用 Release、固定执行线程口径，并核查实际后端。

设计参考 [Unity DOTS](https://unity.com/dots)、[coinsoundsbetter/EntJoy](https://github.com/coinsoundsbetter/EntJoy)、[Arch](https://github.com/genaray/Arch)、[Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS) 和 [Intel ISPC](https://ispc.github.io/)。EntJoy 与 Unity Technologies 无隶属、认可或赞助关系。
