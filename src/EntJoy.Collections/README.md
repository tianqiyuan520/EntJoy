# EntJoy.Collections

**中文** · [English](https://github.com/tianqiyuan520/EntJoy/blob/main/src/EntJoy.Collections/README.en.md)

原生容器、内存分配与 Job 并发访问检查。依赖 EntJoy.Mathematics。

## 安装

```powershell
dotnet add package EntJoy.Collections
```

目标框架 `net8.0`，启用 `AllowUnsafeBlocks`；本模块无需 C++ 工具链。使用 ECS 或 Jobs 时由依赖自动安装。

## 内容与约定

- `NativeArray<T>`：连续数组；`NativeList<T>`：可增长列表；`UnsafeList<T>`：无安全检查的列表。
- `Allocator`、`UnsafeUtility` 与分配器管理原生内存；`SharedBlob<T>` 保存引用计数共享数据。
- 容器完成使用后调用 `Dispose()`；视图不拥有底层数据，Job 完成前保持数据有效。
- 安全句柄检查任务访问冲突；同一任务内部的并行索引互斥仍由调用方保证。
- 共享 Blob 复制引用使用 `Clone()`；外部指针的生命周期由其拥有者负责。

四个包版本同步，当前发布目标为 win-x64。[发布与安装](https://github.com/tianqiyuan520/EntJoy#发布与安装) · [容器样例](https://github.com/tianqiyuan520/EntJoy/tree/main/samples/EntJoySample/04_NativeCollections) · [安全检查契约](https://github.com/tianqiyuan520/EntJoy/blob/main/docs/public/Runtime-Contracts-and-Known-Limitations.md)
