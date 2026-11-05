# ISPC 转译

ISPC 后端保留 C# 的值拷贝、执行次数、循环顺序和无检查整数回绕语义。并行索引由入口发射，普通语句翻译不把源循环改写为 `foreach`。循环展开由 ISPC 编译器决定。

## 执行入口

| 入口 | 执行方式 |
|---|---|
| `IJob`、普通静态方法 | lane 0 单次调用，局部值使用 uniform 类型，保留源循环及返回值 |
| `IJobParallelFor`、`IJobFor` | 按调度区间分配索引；原子返回值、可写 Job 值字段等需要顺序绑定时使用单 lane 索引循环 |
| `IJobEntity` | 按实体分配 lane；可写 Job 值字段使用顺序实体循环 |
| `IJobChunk` | 对已验证的实体数组循环生成 `foreach_tiled`，保留局部结构体值副本 |
| lane 帮助函数 | 每个活动 lane 执行完整函数，不把函数内部迭代分配给其它 lane |
| 显式 `UseISPC_MT` 静态方法 | 将从 0 开始、步长为 1、常量终点的顶层 int 循环分段执行；每个源循环完成后再执行下一条语句。调用者保证同一源循环的迭代可并行 |

Chunk 实体循环使用 `i = 0; i < view.Length; i++` 或等价的单位步长，长度可来自只读局部别名。Chunk 视图访问使用当前实体索引；跨索引访问、退出整个实体循环的 `break`、写入外层局部状态及 Job 值字段等未证明可并行的形态明确拒绝。实体循环外仅接受已识别的纯数学及容器元数据调用，原子和其它未证明的副作用调用明确拒绝。每个实体内部的普通循环保留执行顺序。

批索引层的 `return` 只结束该索引；源嵌套循环内的批返回明确拒绝。lane 帮助函数与单次入口保留函数返回。ISPC `ref` 局部使用 NT035 或不支持标记拒绝，帮助函数的 `ref/out` 参数使用现有物化槽桥接。

布尔字段特化通过共享的 `BoolVariants` 枚举组合并生成后缀；普通入口、多任务入口、C++ wrapper 与托管绑定使用同一套命名。`Run` 与调度路径共用原生适配器的变体选择，多任务调度使用对应组合的 task 入口。

## 实现职责

| 文件 | 职责 |
|---|---|
| `IspcTypeMapper` | C++ 到 ISPC 的类型拼写、原生内存指针的 pointee/address 限定 |
| `IspcStatementTranslator.Uniformity` | 基于符号和数据流判断 uniform 表达式、只读局部变量 |
| `IspcStatementTranslator.ControlFlow` | 局部声明、条件、循环、返回及明确的不支持标记 |
| `IspcStatementTranslator.Expressions` | 值、转换、成员、元素和算术表达式 |
| `IspcStatementTranslator.Calls`、`.Events` | 函数/原子调用、ref/out 桥接及事件写入 |
| `IspcGenerator.Jobs`、`.Chunk`、`.Method`、`.Helper` | 入口执行模型和对应的 C++ wrapper |
| `IspcGenerator.Arguments` | 参数、容器上下文及回写 |

新增类型统一修改 `IspcTypeMapper`；指针在发射时确定限定，不用生成后的正则替换修补。新增语法放入对应语句或表达式模块；uniform 推断统一放入 `.Uniformity`，不依赖 Job 名称。新增入口使用明确的执行模型，不在表达式翻译中判断入口并行策略。

## 验证

生成测试位于 `tests/NativeTranspiler.Tests/Ispc*Tests.cs`。真实构建和运行使用：

```powershell
dotnet run --project tools/NativeTranspilerFixture/NativeTranspilerFixture.csproj -c Release -p:FixtureIspc=true
```

新增能力同时验证生成结构与原生结果，包括空区间、SIMD 尾部、Chunk 尾部、值副本、可写字段和整数极值。ISPC 源及递归 include 都是原生编译依赖。性能比较冻结两套产物，保持输入和语言语义选项相同。
