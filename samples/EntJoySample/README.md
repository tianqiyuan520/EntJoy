# EntJoySample

**中文** · [English](README.en.md)

框架用法、正确性检查与性能样例。默认入口为 `TestGridSearch`。

## 导航

| 目录 | 内容 |
| --- | --- |
| [01 JobSystem](01_JobSystem) | 托管上下文、调度开销、重任务、Profiler 与读写冲突 |
| [02 ECS Job](02_IJobChunkECS) | 最小 Chunk Job、百万实体移动与后端对比 |
| [03 NativeTranspiler](03_NativeTranspiler) | 移动、静态方法、ISPC 多线程与 [AutoSIMD](03_NativeTranspiler/AutoSIMDTest/README.md) |
| [04 Collections](04_NativeCollections) | NativeList、结构体容器与原子操作 |
| [05 GridSearch](05_Algorithms/GridSearch) | 网格搜索、最近点与范围查询 |
| [06 HotField](06_HotFieldHandle) | 对象字段与 SoA 存储共享 |
| [08 Random Access](08_EntityRandomAccess) | 实体随机访问成本 |
| [09 ECS](09_ECS) | 查询、变更追踪、System、事件、关系、Observer 与快照 |
| [10 SIMD](10_SIMD) | C# / C++ / AutoSIMD / ISPC 正确性与性能 |
| [12 Native Lookup](12_EntityNativeLookup) | 实体定位、跨 Chunk 访问与 ECB 批量操作 |
| [13 Enable Bitmap](13_EnableBitMapNative) | 组件启用位图与原生 Job |
| [13 Hot Reload](13_HotReload) | 原生 Job 热重载 |
| [14 AutoSIMD Writeback](14_AutoSimdChunkWriteback) | 多组件 SIMD 写回 |
| [16 Native Execution](16_NativeExecution/README.md) | 列视图、依赖合并、上下文与布局 |

## 构建与入口

需要 .NET 8、CMake、Visual Studio C++ 工具链与 PATH 中的 ISPC。带子模块克隆仓库，在仓库根目录执行：

```powershell
dotnet build samples/EntJoySample/EntJoySample.csproj -c Release
.\bin\EntJoySample.exe
```

用 `-p:ENTJOY_STARTUP=<全限定入口类型>` 切换入口，加 `--no-incremental` 确保重新编译。部分样例以注释形式保存，运行前需启用对应源码与 Main。

例如，运行 Native Execution：

```powershell
dotnet build samples/EntJoySample/EntJoySample.csproj -c Release --no-incremental -p:ENTJOY_STARTUP=EntJoySample.NativeExecution.Program
.\bin\EntJoySample.exe
```

`IJobChunkMoveCompareTest` 还需 `-p:ENTJOY_SAMPLE_SET=movecompare`；工程默认编译 AutoSIMD 样例集，这两个集合互斥。

## 热重载

启用 `13_HotReload/Program.cs` 的入口后，将宿主与日常构建输出分开：

```powershell
dotnet build samples/EntJoySample/EntJoySample.csproj -c Release --no-incremental -p:ENTJOY_STARTUP=EntJoySample.HotReload.Program -o artifacts/hotreload-demo
.\artifacts\hotreload-demo\EntJoySample.exe
```

在另一个终端修改内核并重新构建：

```powershell
dotnet build samples/EntJoySample/EntJoySample.csproj -c Release -p:ENTJOY_STARTUP=EntJoySample.HotReload.Program
```

宿主监视 `bin/NativeTranspiled.dll`，从独立目录运行可避免锁住待替换的构建输出。

## 使用约定

- 性能样例使用 Release、无调试器，保持线程数、电源模式和后台负载一致。
- Job 按值复制；修改 Job 自身字段不会回写调用方，托管引用的并发访问由调用方保证。
- Job 完成后读取结果或释放数据；`NativeTranspiler_Generated/` 为构建生成物。
- Native Lookup 的原生 Job 使用受支持的原生访问方式；原生分块内索引不等同于全局实体索引。
- Enable Bitmap 样例使用单大 Chunk；性能和索引假设以各样例源码为准。

[使用指南](../../docs/public/Native-Jobs-Guide.md) · [运行时契约](../../docs/public/Runtime-Contracts-and-Known-Limitations.md) · [转译边界](../../docs/public/NativeTranspiler-Boundaries-and-Diagnostics.md)
