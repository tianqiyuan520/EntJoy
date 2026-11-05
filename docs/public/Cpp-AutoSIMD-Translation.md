# C++ 与 AutoSIMD 转译

C++ 后端按源语句生成标量代码；AutoSIMD 在可向量化的批内生成向量值、活动掩码及标量余量。优化以符号、类型和循环控制分析为依据。入口调度、上下文布局、语句翻译各自负责一层语义。

## 执行与数值规则

- 普通 C++ 入口保留源循环顺序、值拷贝和字段回写。索引入口的 `return` 结束当前索引。
- 原语法树中的字段、参数、局部变量按 Roslyn 符号绑定；同名局部变量保持局部含义，`this.Field` 访问 Job 字段。Chunk 改写节点通过 `SourceSyntax` 来源注解查询原树的符号、类型和循环控制；新建节点使用显式声明及容器参数表。
- 标量 C++ 的 `int`、`long` 加减乘、取负、自增自减和相应复合赋值使用同宽无符号运算表达回绕。`long` 使用 `long long`，字面量使用 `LL/ULL`；混合 `int/uint` 运算采用 C# 的结果位宽。复合赋值先确定左值并读取旧值，再求右值，各求值一次。
- AutoSIMD 的 varying `bool` 存为 0/1 整数向量；条件使用 `simd_mask`，两种表示在消费位置转换。逻辑运算和布尔条件不依赖生成文本中是否包含某个名字。
- 条件取反直接反转最终掩码；浮点条件更新保留比较与掩码选择，NaN、相等值和符号零按源分支选择。
- 每条数组写入使用所在语句的活动掩码；同一函数中的其它写入保留各自的条件与顺序。标量余量复用 C++ 语句翻译。
- `BoolVariants` 统一 C++、ISPC、托管绑定的布尔组合顺序与导出后缀。`Run` 通过原生适配器调用，与调度路径共用变体选择。
- `AutoSIMD.Vectorize` 向 C++ 编译器请求循环向量化，默认实现与适配器一起使用无 fast-math、禁止自动 FMA 融合的编译单元。显式 `CppMathLib.fast` 使用快速浮点配置。手工 SIMD intrinsic 的误差界按所选数学模式验证，逐位一致性需要单独验证。
- `AutoSIMD.Enabled` 的实现与适配器共用精确浮点编译选项；这些单元作为对象库完整链接，按名称解析的入口保留在最终动态库中。

## 循环选择

`IntForLoop` 识别单个只读 `int` 计数器、关系条件和常量非零步长。计数器在循环体中被写入、取址或作为引用暴露时，不进入该优化。

| 条件 | 生成方式 |
|---|---|
| 完整行程和最后一次增量可证明安全，且满足展开预算 | 按源顺序展开 |
| uniform 边界 | 标量计数器与每 lane 的循环体掩码，每轮重新求边界 |
| varying 归约，`<`、步长 1，值边界不变且无 `break/return` | 无符号行程计数与单调活动掩码；翻转符号位后使用 signed `hmax` 求最大行程 |
| 其它已识别的 varying `for` | 每轮重新求边界的 mask loop，保留 `<=`、`>=` 及退出状态 |

行程预计算只接受可证明不变的局部值或值参数表达式。内存读取、可变边界、引用别名和包含端点的循环使用逐轮条件。`break` 移除该 lane 的后续迭代，`continue` 只结束本次循环体，`return` 还影响循环后的语句。

Chunk SIMD 接受单个从 0 开始、步长 1、以组件视图长度或 `chunk.Count` 为界的完整实体循环；循环外仅有组件视图绑定，组件访问使用当前实体索引。内层 int 循环复用原树分析，支持固定、可变、包含端点与反向步长，以及内层 `break/continue`。

通过存储隔离检查的结构体副本拆为局部字段向量，循环内保留局部值，仅在源写回位置提交修改过的字段。局部变量按原始符号生成内部名字，相邻作用域可复用不同类型的同名变量。字段读取和写回区分 32 位整数、浮点及对应双分量类型，gather 按组件实际步幅寻址。

逐 Chunk 副作用、退出实体循环、跨索引依赖、可观察的副本别名、未支持的字段宽度、SendEvent 等使用原树生成标量入口。批适配器中的 `return` 只结束当前 Chunk。代码生成异常由生成器诊断报告。

## 实现职责

| 文件 | 职责 |
|---|---|
| `CppJobGenerator`、`.Entries` | 后端和入口分派、各类 Job 的执行函数 |
| `CppJobGenerator.Fields`、`.AdapterFields` | 字段用途分析、上下文与适配器字段绑定 |
| `CppJobGenerator.Adapters`、`.RangeAdapters`、`.ChunkAdapters` | C ABI 适配器及变体分派 |
| `CppJobGenerator.Types`、`.ChunkSimd` | 依赖类型收集、Chunk 预处理与 SIMD 入口 |
| `CppPointerStatementTranslator`、`CppBatchStatementTranslator` | 标量语句、符号绑定、批参数映射及整数语义 |
| `SimdVariableAnalyzer`、`SimdVectorizability` | 值分类与向量化能力判定 |
| `SimdExpressionTranslator`、`.Operators` | 表达式分派、符号、布尔表示、运算与条件值 |
| `SimdExpressionTranslator.Memory`、`.Stores`、`.Calls` | 内存读取、掩码写入、函数与数学调用 |
| `SimdControlFlowGenerator`、`.Branches`、`.Locals` | 语句分派、分支掩码、变量与广播提升、lane 的退出状态 |
| `SimdLoopGenerator` | 循环条件、步长、展开和归约行程 |
| `SymbolHelper.GetBoundSymbol/GetBoundType`、`IntForLoop`、`CppIntegerSemantics` | 原树语义查询边界、循环契约与整数位宽 |

新增语法放入对应翻译模块，新增类型扩展现有类型映射。修改向量化能力时，同步检查校验与发射路径。生成文本中的局部改写只处理固定的入口衔接；存储语义直接在语法节点处决定。

## 支持边界与验证

这是一组已实现的 C# 子集规则。原始用户标识符仍需能直接用于目标语言，例如 C++ 关键字 `signed` 不适合作为局部变量名。AutoSIMD 的宽整数、引用、调用副作用、跨 lane 共享写入及其它复杂形态以现有能力校验和生成标记为边界，不能从标量 C++ 的支持推断其向量版本也受支持。无检查整数规则不提供 C# 的溢出异常。

边界诊断见 [NativeTranspiler-Boundaries-and-Diagnostics.md](NativeTranspiler-Boundaries-and-Diagnostics.md)。生成结构测试位于 `tests/NativeTranspiler.Tests`；原生结果对照包括 `TranslationSemanticsFixture` 的掩码、同名绑定、布尔值、求值顺序、整数极值与循环边界。

```powershell
dotnet test tests/NativeTranspiler.Tests/NativeTranspiler.Tests.csproj -c Release
dotnet run --project tools/NativeTranspilerFixture/NativeTranspilerFixture.csproj -c Release -p:FixtureIspc=true
dotnet build samples/EntJoySample/EntJoySample.csproj -c Release
```

性能比较冻结两套完整产物，保持调度器二进制、输入、预热和测量帧数相同，交替运行多轮。单一负载的结果只说明该负载，不能作为所有 Job 都更快的结论。
