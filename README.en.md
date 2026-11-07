# EntJoy

Write data and jobs in C#. Execute them with C#, C++, or ISPC. EntJoy supplies the game framework; Godot, Unity, or a custom engine can supply rendering.

[中文](README.md) · **English** · [Guide](docs/public/Native-Jobs-Guide.md) · [Samples](#samples) · [Tests and tools](#tests-and-tools) · [DeepWiki](https://deepwiki.com/tianqiyuan520/EntJoy)

## Design

Data, scheduling, and execution are separate layers. ECS stores components in Archetypes and Chunks. JobSystem manages dependencies and parallel execution. NativeTranspiler translates supported C# jobs into C++ or ISPC at build time.

| Module | Responsibility |
| --- | --- |
| [EntJoy.ECS](src/EntJoy.ECS/README.en.md) | Entities, components, queries, Systems, and ECS jobs |
| [EntJoy.Jobs](src/EntJoy.Jobs/README.en.md) | Managed/native schedulers, JobHandle, transpilation, and build integration |
| [EntJoy.Collections](src/EntJoy.Collections/README.en.md) | Native containers, allocators, and safety checks |
| [EntJoy.Mathematics](src/EntJoy.Mathematics/README.en.md) | Math types and utilities |

Dependencies follow `ECS → Jobs → Collections → Mathematics`. Array jobs can use Jobs without ECS. Ordinary C# jobs prefer the native scheduler and fall back to Managed when the native runtime is unavailable. Transpiled kernels require the native runtime.

## Releases and installation

All four packages share a version, currently **1.0.2**, defined by [EntJoyVersion](src/Directory.Build.props).

| Package | NuGet |
| --- | --- |
| EntJoy.ECS | [![NuGet](https://img.shields.io/nuget/v/EntJoy.ECS)](https://www.nuget.org/packages/EntJoy.ECS) |
| EntJoy.Jobs | [![NuGet](https://img.shields.io/nuget/v/EntJoy.Jobs)](https://www.nuget.org/packages/EntJoy.Jobs) |
| EntJoy.Collections | [![NuGet](https://img.shields.io/nuget/v/EntJoy.Collections)](https://www.nuget.org/packages/EntJoy.Collections) |
| EntJoy.Mathematics | [![NuGet](https://img.shields.io/nuget/v/EntJoy.Mathematics)](https://www.nuget.org/packages/EntJoy.Mathematics) |

[nuget.org](https://www.nuget.org/profiles/tianqiyuan520) supports anonymous restore. The [GitHub Packages mirror](https://nuget.pkg.github.com/tianqiyuan520/index.json) requires a classic PAT with `read:packages`; configure `packageSourceMapping` when using it. Pushing a matching `v*` version tag triggers the [release workflow](.github/workflows/publish-nuget.yml), which runs Release Gate and package consumer checks before publishing to both feeds.

### Using NuGet packages

Install `EntJoy.ECS` for ECS applications, or `EntJoy.Jobs` for jobs alone. Dependencies are installed automatically.

```powershell
dotnet add package EntJoy.ECS
```

Target `net8.0` and enable `AllowUnsafeBlocks`. Ordinary C# jobs use the bundled runtime without a local C++ toolchain. Transpilation requires CMake and MSVC or ClangCL; the ISPC backend also requires Intel ISPC.

Source integration and build settings are covered in the [guide](docs/public/Native-Jobs-Guide.md) (Chinese).

## Job example

Define an array job:

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

Initialize the scheduler once, submit work, and wait:

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

## ECS example

Create an entity with position and velocity, then query and update it:

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

## NativeTranspiler example

Add this attribute before the `AddJob` declaration above to compile the same logic into C++. Scheduling stays the same:

```csharp
[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
```

Use `BackendTarget.Ispc` for ISPC. See the [NativeTranspiler samples](samples/EntJoySample/03_NativeTranspiler) for array and ECS jobs.

## Samples

| Sample | Contents |
| --- | --- |
| [JobSystem](samples/EntJoySample/01_JobSystem) | Scheduling, dependencies, access checks, and Profiler |
| [ECS jobs](samples/EntJoySample/02_IJobChunkECS) | Chunk/Entity jobs and movement comparisons |
| [NativeTranspiler](samples/EntJoySample/03_NativeTranspiler) | C++, ISPC, and AutoSIMD |
| [Collections](samples/EntJoySample/04_NativeCollections) | Native containers and atomics |
| [ECS](samples/EntJoySample/09_ECS) | Queries, Systems, events, relations, and snapshots |
| [Native Execution](samples/EntJoySample/16_NativeExecution/README.en.md) | Native execution, dependencies, and column views |
| [All samples](samples/EntJoySample/README.en.md) | Algorithms, SIMD, hot reload, and entry-point selection |

## Tests and tools

`tests/` verifies correctness and contracts. `tools/` contains native builds, end-to-end fixtures, stress tests, and benchmarks.

| Entry | Purpose |
| --- | --- |
| [EntJoy.ECS.Tests](tests/EntJoy.ECS.Tests) | ECS and Managed/Native JobSystem regressions |
| [NativeDll.Tests](tests/NativeDll.Tests) | C++ scheduling, queues, wakeups, and lifetime |
| [NativeTranspiler.Tests](tests/NativeTranspiler.Tests/README.en.md) | Generated code and diagnostics |
| [HotReload.Tests](tests/HotReload.Tests) | Hot-reload contracts |
| [JobsOnlyTranspilerCheck](tests/JobsOnlyTranspilerCheck) | Transpilation without ECS dependencies |
| [NuGetConsumer](tests/NuGetConsumer) · [NuGetJobsConsumer](tests/NuGetJobsConsumer) | Package consumption and native execution |
| [DocLinksCheck](tests/DocLinksCheck) | Documentation links and anchors |
| [NativeDllCore](tools/NativeDllCore) | Core native runtime build |
| [NativeTranspilerFixture](tools/NativeTranspilerFixture) | Transpilation, native compilation, and execution checks |
| [JobSystemStressTest](tools/JobSystemStressTest) · [CSharp](tools/JobSystemStressTest.CSharp) | Native and C# stress tests |
| [ISPCWidthBoundaryTest](tools/ISPCWidthBoundaryTest) | SIMD widths, tails, and boundaries |
| [SafetyInterceptProbe](tools/SafetyInterceptProbe) | Container concurrency checks |
| [JobLibsBenchmark](tools/JobLibsBenchmark) · [JobSystemPerfBench](tools/JobSystemPerfBench) | Framework comparisons and scheduling costs |

Local files under `tools/` are ignored by default; selected tools are tracked. JobLibsBenchmark has only part of its sources tracked, so project and dependency setup is still local. See [Release Gate](.github/workflows/jobsystem-ci.yml) for CI commands and coverage.

## Usage notes

- Native NuGet assets currently target **Windows x64**. Managed tests run on Windows/Linux; this does not imply native package support on every platform.
- Wait for jobs before accessing or releasing their data, or making related structural changes. Use `JobHandle` for ordering.
- Jobs are copied by value. Callers remain responsible for concurrent access to referenced managed objects.
- NativeTranspiler accepts a C# subset with supported unmanaged data and APIs. See [translation diagnostics](docs/public/NativeTranspiler-Boundaries-and-Diagnostics.md) (Chinese).
- Current ISPC samples target AVX2. Benchmark in Release with consistent compute-thread limits and verify the active backend.

Design references: [Unity DOTS](https://unity.com/dots), [coinsoundsbetter/EntJoy](https://github.com/coinsoundsbetter/EntJoy), [Arch](https://github.com/genaray/Arch), [Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS), and [Intel ISPC](https://ispc.github.io/). EntJoy is not affiliated with, endorsed by, or sponsored by Unity Technologies.
