# EntJoy.ECS

**中文** · [English](https://github.com/tianqiyuan520/EntJoy/blob/main/src/EntJoy.ECS/README.en.md)

基于 Archetype / Chunk 的 ECS，提供实体、组件、查询、System、关系、事件和 ECS Job。

## 安装

```powershell
dotnet add package EntJoy.ECS
```

目标框架 `net8.0`，启用 `AllowUnsafeBlocks`。本包包含 ECS 源生成器，并依赖 Jobs → Collections → Mathematics；使用 ECS 时只需安装本包。

## 使用边界

- `World` 管理数据生命周期；结构变化前完成访问相关数据的 Job。
- `IJobEntity` 按实体处理组件，`IJobChunk` 按 Chunk 访问组件列；调度扩展位于 `EntJoy.ECS.JobSystem`。
- 普通 C# 使用无需本地原生编译工具；转译 Job 需要 CMake + MSVC 或 ClangCL，ISPC 后端还需 Intel ISPC。
- 当前原生包提供 win-x64。四个包版本同步，发布与安装说明见[项目首页](https://github.com/tianqiyuan520/EntJoy#发布与安装)。

## 关系

关系通过 IRelationComponent 和统一的 RelationshipComponent 特性声明。默认单目标；Hierarchy 开启单父级与防环，Multiple 开启多目标列表，MaxTargets 指定 Job 可读的固定容量，ExclusiveTarget 保证目标持有者唯一，DeleteWithTarget 开启随目标删除。支持正反查询、ECB、组复制、Prefab 和快照。Job 可使用只读关系列、精确目标筛选或调用方拥有的目标副本；独立边数据用普通边实体承载。

建模、完整使用代码和可运行场景见 [Relationship 样例](../../samples/EntJoySample/17_Relationship/README.md)。

[最小示例](https://github.com/tianqiyuan520/EntJoy#ecs-示例) · [样例](https://github.com/tianqiyuan520/EntJoy/tree/main/samples/EntJoySample/09_ECS) · [运行时契约](https://github.com/tianqiyuan520/EntJoy/blob/main/docs/public/Runtime-Contracts-and-Known-Limitations.md)
