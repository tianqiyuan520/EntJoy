# Native Execution

**中文** · [English](README.en.md)

演示原生 Job 的列视图、依赖合并、上下文所有权与组件布局。

## 运行

需要 .NET 8、CMake、Visual Studio C++ 工具链与 PATH 中的 ISPC。在仓库根目录执行：

```powershell
dotnet build samples/EntJoySample/EntJoySample.csproj -c Release --no-incremental -p:ENTJOY_STARTUP=EntJoySample.NativeExecution.Program
.\bin\EntJoySample.exe
```

成功输出 `PASS: native execution examples.`。本例核对结果，不测量性能。

## 内容

| 文件 | 演示 |
| --- | --- |
| [NativeExecutionDemo.cs](NativeExecutionDemo.cs) | 连续 / 分段列视图、辅助函数、依赖、位开关、固定循环、布局与快照 |
| [CopiedContextDemo.cs](CopiedContextDemo.cs) | Raw 提交的上下文复制、池化与清理 |

## 使用边界

- `SampleColumn<T>` 是样例自有槽位视图。数据和块表由调用方拥有，Job 完成前保持地址、容量与槽位顺序稳定。
- 分段视图除末块外使用固定容量，槽位编号不等于 Entity 身份；跨 Archetype 的定位与实体代次需由 ECS 处理。
- `ScheduleCopiedRaw*` 复制上下文字节，内部指针仍引用原数据；数据持续有效至完成。
- 池化上下文与 `PooledJobContextCleanupPtr` 配对，提交后由调度器回收。
- 显式 Batch 保留 start/count 边界；逐索引 Range 回调处理完整传入范围。
- Raw 的 C# 回调仍执行 C#；`[NativeTranspile]` 与生成入口执行转译内核。
- 组件列采用 CLR 无引用值布局；快照恢复校验布局与程序集，支持边界见运行时契约。

生成器负责稳定指针分析、内联、受限循环优化与 SIMD 语义检查；实际向量化结果以生成代码和机器码为准。池化不表示整个框架零分配。

## 诊断

进程启动前设置 `ENTJOY_DIAG_NATIVE_BIND=1` 可查看原生绑定，`ENTJOY_MAIN_ASSIST=0` 可观察纯 worker 路径。其他配置见[开关说明](../../../docs/public/Gates-and-Flags.md)。

[运行时契约](../../../docs/public/Runtime-Contracts-and-Known-Limitations.md) · [转译边界](../../../docs/public/NativeTranspiler-Boundaries-and-Diagnostics.md) · [C++ / AutoSIMD](../../../docs/public/Cpp-AutoSIMD-Translation.md)
