# EntJoy.ECS

**中文** | [English](#entjoyecs-english)

EntJoy 的 Archetype ECS（headless）。这是**唯一需要直接引用的包**：原生 JobSystem 与 NativeTranspiler 设施随依赖 `EntJoy.Jobs` 到达，ECS 源生成器随包提供（`analyzers/dotnet/cs/`），无需任何仓库源码或子模块。

## 内容

| 领域 | 主要类型 |
| --- | --- |
| World | `World`（`IDisposable`，partial：实体分组索引 / 关系 / 报告）、`WorldSnapshot`、`WorldEntityBuilderExtensions`。 |
| Entity | `Entity`、`EntityManager`（partial：共享组件 / 关系 / 多关系 / Observer）、`EntityBuilder`、`EntityIndexInWorld`。 |
| Archetype / Chunk | `Archetype`（含 `RemovalResult`）、`ArchetypeChunk`、`Chunk`、`ChunkMetadata`、`ChunkMemoryPool`、`ChunkEnabledMask`、`ChunkEnumerable` / `ChunkEnumerator` / `ChunkResult<T0,T1>`、`DeferredCommandBuffer`（含 `ParallelWriter`）。 |
| Component | `IComponentData`、`ISharedComponentData`、`IEnableableComponent`、`ICopyable` / `ICopyable<T>`、`ECSComponentAttribute`、`Prefab`、`ComponentType`、`ComponentTypeManager`、`ComponentMeta` / `ComponentFieldMeta` / `ComponentMetaRegistry`。 |
| Query | `EntityQuery`、`QueryKey`、`QueryBuilder`、`QuerySelection<T0>` / `QuerySelection<T0,T1>`、`ComponentLookup<T>`、`QueryEnumerable` / `QueryEnumerator` / `EntityQueryResult<T0,T1>`。 |
| Relation | `RelationIndex`、`RelationListStore`、`RelationSlot`、`IRelationComponent`，属性 `ExclusiveRelation` / `MultiRelation` / `ExclusiveTarget` / `OnTargetDeleted`。 |
| Observer | `ComponentObserver`、`ObserverHandle`、`ObserverEvents`、`ReactiveAttribute`。 |
| Event | `EventBus`、`EventStream<T>`、`EventBuffer`。 |
| System | `ISystem`、`ISystemWithState` / `SystemState`、`SystemRunner`、`ScheduleGraph` / `SystemSlot`，调度属性 `Read` / `Write` / `Order` / `RunWhen` / `OrderBefore` / `OrderAfter`，`DisableAutoCreationAttribute`，`PerformanceReport` / `SystemTiming`、`EventCounter`。 |
| ECS Job（`EntJoy.ECS.JobSystem`） | `IJobChunk`、`IJobEntity`、`ChunkJobScheduler`、`ChunkJobCallbacks`、`ChunkJobExtensions`、`NativeChunkJobs`（`ChunkJobData` / `ChunkData` / `EntityBatchData`）。 |
| 内存与工具 | `MemoryReport` / `ArchetypeMemoryInfo`、`Utils`。 |

配套的 **ECS 源生成器**为 `IJobEntity`、原生绑定与调度扩展生成代码，随包以分析器形式加载，不需要在消费工程里引用生成器工程。

> `IJobChunk` / `IJobEntity` / `SendEvent` 属于 ECS 能力，必须引用本包；只写数组类 Job（`IJob` / `IJobFor` / `IJobParallelFor` / `IJobParallelForBatch`）的项目可以只引用 [`EntJoy.Jobs`](https://github.com/tianqiyuan520/EntJoy/blob/main/src/EntJoy.Jobs/README.md)。

## 工具链前提

- **只用托管 ECS（不写 `[NativeTranspile]`）**：装包即用，**不需要** CMake / MSVC / ISPC。
- **写 native Job**：需要 **CMake + MSVC（或 ClangCL）**，ISPC 可选。构建期由包内分析器生成 C++/ISPC，再由包内 MSBuild 任务编译 `NativeTranspiled.dll` 并链接包内预编译 `NativeDll.lib`。

## 引用

```xml
<ItemGroup>
  <PackageReference Include="EntJoy.ECS" Version="1.0.0" />
</ItemGroup>
```

- `net8.0` + `AllowUnsafeBlocks`；命名空间 `EntJoy.ECS`（ECS Job 在 `EntJoy.ECS.JobSystem`）。
- 依赖：`EntJoy.Collections`、`EntJoy.Jobs`（后者再依赖 `EntJoy.Mathematics`），**不需要**显式引用它们。
- 版本由 `src/Directory.Build.props` 的 `EntJoyVersion` 统一决定，四个包 lockstep 发布，当前仅 win-x64。

## 相关

- [`EntJoy.Jobs`](https://github.com/tianqiyuan520/EntJoy/blob/main/src/EntJoy.Jobs/README.md) —— 原生 JobSystem 与 NativeTranspiler 设施。
- [`EntJoy.Collections`](https://github.com/tianqiyuan520/EntJoy/blob/main/src/EntJoy.Collections/README.md) / [`EntJoy.Mathematics`](https://github.com/tianqiyuan520/EntJoy/blob/main/src/EntJoy.Mathematics/README.md)。
- 根 [README](https://github.com/tianqiyuan520/EntJoy/blob/main/README.md#ecs-示例) 的 ECS 示例与[通过 NuGet 使用](https://github.com/tianqiyuan520/EntJoy/blob/main/README.md#通过-nuget-使用)。

# EntJoy.ECS (English)

[中文](#entjoyecs) | **English**

EntJoy's Archetype ECS (headless). This is **the only package you need to reference directly**: the native JobSystem and NativeTranspiler infrastructure arrive through the `EntJoy.Jobs` dependency, and the ECS source generator is shipped inside the package (`analyzers/dotnet/cs/`), so no repository sources or submodules are required.

## Contents

| Area | Main types |
| --- | --- |
| World | `World` (`IDisposable`, partial: entity group index / relations / reporting), `WorldSnapshot`, `WorldEntityBuilderExtensions`. |
| Entity | `Entity`, `EntityManager` (partial: shared components / relations / multi-relations / observer), `EntityBuilder`, `EntityIndexInWorld`. |
| Archetype / Chunk | `Archetype` (with `RemovalResult`), `ArchetypeChunk`, `Chunk`, `ChunkMetadata`, `ChunkMemoryPool`, `ChunkEnabledMask`, `ChunkEnumerable` / `ChunkEnumerator` / `ChunkResult<T0,T1>`, `DeferredCommandBuffer` (with `ParallelWriter`). |
| Component | `IComponentData`, `ISharedComponentData`, `IEnableableComponent`, `ICopyable` / `ICopyable<T>`, `ECSComponentAttribute`, `Prefab`, `ComponentType`, `ComponentTypeManager`, `ComponentMeta` / `ComponentFieldMeta` / `ComponentMetaRegistry`. |
| Query | `EntityQuery`, `QueryKey`, `QueryBuilder`, `QuerySelection<T0>` / `QuerySelection<T0,T1>`, `ComponentLookup<T>`, `QueryEnumerable` / `QueryEnumerator` / `EntityQueryResult<T0,T1>`. |
| Relation | `RelationIndex`, `RelationListStore`, `RelationSlot`, `IRelationComponent`, and the `ExclusiveRelation` / `MultiRelation` / `ExclusiveTarget` / `OnTargetDeleted` attributes. |
| Observer | `ComponentObserver`, `ObserverHandle`, `ObserverEvents`, `ReactiveAttribute`. |
| Event | `EventBus`, `EventStream<T>`, `EventBuffer`. |
| System | `ISystem`, `ISystemWithState` / `SystemState`, `SystemRunner`, `ScheduleGraph` / `SystemSlot`, the scheduling attributes `Read` / `Write` / `Order` / `RunWhen` / `OrderBefore` / `OrderAfter`, `DisableAutoCreationAttribute`, `PerformanceReport` / `SystemTiming`, `EventCounter`. |
| ECS jobs (`EntJoy.ECS.JobSystem`) | `IJobChunk`, `IJobEntity`, `ChunkJobScheduler`, `ChunkJobCallbacks`, `ChunkJobExtensions`, `NativeChunkJobs` (`ChunkJobData` / `ChunkData` / `EntityBatchData`). |
| Memory and utilities | `MemoryReport` / `ArchetypeMemoryInfo`, `Utils`. |

The bundled **ECS source generator** emits code for `IJobEntity`, the native bindings, and the scheduling extensions; it loads as an analyzer from the package, so consumers never reference the generator project.

> `IJobChunk` / `IJobEntity` / `SendEvent` are ECS features and require this package. A project that only writes array-shaped jobs (`IJob` / `IJobFor` / `IJobParallelFor` / `IJobParallelForBatch`) can reference [`EntJoy.Jobs`](https://github.com/tianqiyuan520/EntJoy/blob/main/src/EntJoy.Jobs/README.md) alone.

## Toolchain requirements

- **Managed ECS only (no `[NativeTranspile]`)**: works out of the box, **no** CMake / MSVC / ISPC required.
- **Native jobs**: requires **CMake + MSVC (or ClangCL)**; ISPC is optional. At build time the packaged analyzer emits the C++/ISPC, and the packaged MSBuild task compiles `NativeTranspiled.dll` against the packaged prebuilt `NativeDll.lib`.

## Reference it

```xml
<ItemGroup>
  <PackageReference Include="EntJoy.ECS" Version="1.0.0" />
</ItemGroup>
```

- `net8.0` + `AllowUnsafeBlocks`; namespace `EntJoy.ECS` (ECS jobs live in `EntJoy.ECS.JobSystem`).
- Dependencies: `EntJoy.Collections` and `EntJoy.Jobs` (the latter depends on `EntJoy.Mathematics`) — **no need to reference them yourself**.
- The version comes from `EntJoyVersion` in `src/Directory.Build.props`; all four packages are released in lockstep and are win-x64 only.

## See also

- [`EntJoy.Jobs`](https://github.com/tianqiyuan520/EntJoy/blob/main/src/EntJoy.Jobs/README.md) — native JobSystem and NativeTranspiler infrastructure.
- [`EntJoy.Collections`](https://github.com/tianqiyuan520/EntJoy/blob/main/src/EntJoy.Collections/README.md) / [`EntJoy.Mathematics`](https://github.com/tianqiyuan520/EntJoy/blob/main/src/EntJoy.Mathematics/README.md).
- The [ECS example](https://github.com/tianqiyuan520/EntJoy/blob/main/README.md#ecs-example) and [Using NuGet Packages](https://github.com/tianqiyuan520/EntJoy/blob/main/README.md#using-nuget-packages) in the root README.
