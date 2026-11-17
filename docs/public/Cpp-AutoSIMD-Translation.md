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

### 2 分量向量元素

`float2` / `int2` / `uint2` 元素可以进向量路径：整元素按 gather 读、整元素按两个分量各取 lane 写回，寄存器类型统一为 `simd_value<float2>` / `simd_value<int2>`（`uint2` 与 `int2` 同布局，指针按位重新解释）。字段级读写（结构体里的 `uint2` 字段）走 `n_gather_*` 与字段偏移。

`uint2` 的无符号语义由 helper 承担：`/`、`%`、`>>` 在任一侧是无符号时映射到 `simd_div_u32` / `simd_mod_u32` / `simd_shr_u32`，标量与 pair 共用同一组重载。`+`、`-`、`*` 走补码运算，与原语义一致。复合赋值（`Values[i] *= 2`）与带对象初始化器的整体结构体赋值不在支持范围，生成 `SIMD_StructCompoundAssign` / `SIMD_ObjectInitializerStore` 标记，构建期失败而不是静默错值。

```powershell
dotnet test tests/NativeTranspiler.Tests/NativeTranspiler.Tests.csproj -c Release
dotnet run --project tools/NativeTranspilerFixture/NativeTranspilerFixture.csproj -c Release -p:FixtureIspc=true
dotnet build samples/EntJoySample/EntJoySample.csproj -c Release -t:Rebuild
dotnet run --project tools/AutoSIMDVerify/AutoSIMDVerify.csproj -c Release
dotnet run --project tools/AutoSIMDEdgeCases/AutoSIMDEdgeCases.csproj -c Release
dotnet run --project tools/AutoSIMDPerf/AutoSIMDPerf.csproj -c Release
```

两个 `AutoSIMD*` 工具从仓库 `bin/` 拷贝 `Native*.dll` 到自己的输出目录；日志里 `fallback=none` 才说明跑的是原生产物。32 位脚本用 `-m32` 编译一个包含 `NativeGeneratedSupport.h` 的探针，必须被 `static_assert` 拦下。

性能比较冻结两套完整产物，保持调度器二进制、输入、预热和测量帧数相同，交替运行多轮。单一负载的结果只说明该负载，不能作为所有 Job 都更快的结论。

### Fastest 档的实测误差

口径与 libm 测试一致：`err = |bits(out) - bits(ref)|`，`ref` 是 double 参考值按 round-to-nearest 折回的 float；结果次正规或为 0 时按 2^-149 的绝对误差计（AVX2、`NSIMD_WIDTH=8`、`/fp:precise`，`tools/MathUlpCheck`）。

| 函数 | 输入域 | max ULP | p99.9 | 均值 |
|---|---|---|---|---|
| sin | [-π, π] | 1.00 | 1.00 | 0.12 |
| sin | 到 ±1e4、1e6..1e7 | 1.00 | 1.00 | 0.16 |
| cos | [-π, π] / ±1e4 / 1e6..1e7 | 2.00 | 1.00 | 0.16 |
| log | [1e-37, 1e38] | 1.00 | 1.00 | 0.31 |
| exp | [-87, 88] | 1.00 | 1.00 | 0.06 |
| tan | [-1.5, 1.5] | 3.00 | 2.00 | 0.41 |
| atan | [-20, 20] | 2.00 | 1.00 | 0.36 |
| asin | [-1, 1] | 2.00 | 2.00 | 0.34 |
| acos | [-1, 1] | 3.00 | 2.00 | 0.44 |
| sinh | [-10, 10] | 2.00 | 1.00 | 0.27 |
| cosh | [-10, 10] | 1.00 | 1.00 | 0.23 |
| tanh | [-5, 5] | 2.00 | 2.00 | 0.27 |
| atan2 | 全象限 | 2.00 | 2.00 | 0.37 |
| pow | 底数 [0.01,10]，指数 [-3,3] | 1.00 | 1.00 | 0.02 |

sin/cos 的 `|x|` 归约在 double 里完成（三段 π/2），`|x| > 1e7` 时改走 libm 逐 lane 精确归约。log 在 `|x-1| <= 0.25` 走 `log(1+t)` 级数，其余用尾数归约加两段 ln2。asin/acos 的 `1-x²` 按 `(1-x)(1+x)` 计算，sinh/cosh/tanh 在 `|x| < 0.25` 走 Taylor，pow 走 SLEEF 的 exp2(y·log2 x) 路线：指数与尾数在 float 域取出，log2 只对 [1,2) 的尾数展开，log2/exp2 都在 double 里算，最后折回 float。
```powershell
dotnet test tests/NativeTranspiler.Tests/NativeTranspiler.Tests.csproj -c Release
dotnet run --project tools/NativeTranspilerFixture/NativeTranspilerFixture.csproj -c Release -p:FixtureIspc=true
dotnet build samples/EntJoySample/EntJoySample.csproj -c Release -t:Rebuild
dotnet run --project tools/AutoSIMDVerify/AutoSIMDVerify.csproj -c Release
dotnet run --project tools/AutoSIMDEdgeCases/AutoSIMDEdgeCases.csproj -c Release
dotnet run --project tools/AutoSIMDPerf/AutoSIMDPerf.csproj -c Release
```

两个 `AutoSIMD*` 工具从仓库 `bin/` 拷贝 `Native*.dll` 到自己的输出目录；日志里 `fallback=none` 才说明跑的是原生产物。32 位脚本用 `-m32` 编译一个包含 `NativeGeneratedSupport.h` 的探针，必须被 `static_assert` 拦下。

性能比较冻结两套完整产物，保持调度器二进制、输入、预热和测量帧数相同，交替运行多轮。单一负载的结果只说明该负载，不能作为所有 Job 都更快的结论。

### Fastest 档的实测误差

精度档只作用于向量 lane 路径：生成代码的标量尾巴（`::expf(x)` 这类）直接调 libm，同一 job 里向量段与标量段用的不是同一套实现。`NativeSIMD_math.h` 的 `SIMD_MATH_PRECISION == 1` 分支里 `sin`/`cos`/`log`/`exp` 是内联多项式（AVX2），`log10`/`tan`/`asin`/`acos`/`atan`/`atan2`/`sinh`/`cosh`/`tanh`/`pow`/`sincos` 由它们组合而成；只有 AVX512 与非 AVX2 平台仍走逐 lane 标量回退。实测（AVX2、8 宽、`/fp:precise`，`tools/MathUlpCheck`）：

| 函数 | 输入域 | absULP 最大 | absULP 均值 | relULP 最大 |
|---|---|---|---|---|
| sin | [-π, π] | 1.00 | 0.10 | 2.00 |
| cos | [-π, π] | 1.00 | 0.11 | 2.00 |
| sin | [0, 8π] | 12.06 | 2.59 | 11943 |
| sin | [-100, 100] | 60.00 | 11.85 | 53848 |
| sin | [-1000, 1000] | 508.00 | 107.37 | 370352 |
| sin | [-10000, 10000] | 8185.50 | 1183.35 | 4511307 |
| cos | [-10000, 10000] | 8174.63 | 1177.24 | 4333430 |
| log | [1e-37, 1e38] | 128.00 | 16.57 | 8428 |
| log | [0.999, 1.001] | 10.30 | 4.05 | 9606 |
| exp | [-1, 1] | — | — | 1.99 |
| exp | [-87, 88] | — | — | 2.00 |
| log10 | [1e-37, 1e38] | 128.00 | 11.68 | — |
| tan | [-1.5, 1.5] | 32.00 | 1.02 | 4.00 |
| atan | [-20, 20] | 2.00 | 0.69 | 2.53 |
| asin | [-1, 1] | 14.00 | 0.29 | 9.02 |
| acos | [-1, 1] | 16.00 | 1.06 | — |
| atan2 | 全象限 | 4.00 | 0.79 | 2.55 |
| sinh | [-10, 10] | 16384 | 287 | 51.9 |
| cosh | [-10, 10] | 16384 | 226 | 325 |
| tanh | [-5, 5] | 1.00 | 0.26 | — |
| pow (x>0) | 底数 [0.01,10] | — | — | 26.4 |

`absULP = |out - ref| / 2^-24`，`relULP = (|out - ref| / |ref|) / 2^-24`。sin/cos 的绝对误差随 `|x|` 线性增长：三次 π/2 归约在 float 下精度有限，`|x|` 越大残余误差越大。log 的相对误差在 75 个数量级上均值 0.55 ULP，接近 1 时由绝对误差主导。
### High 档

`SIMD_MATH_PRECISION == 2` 是 double 中间精度实现（AVX2）：8 个 float lane 拆成两组 4 lane double，算完折回 float。逐函数实测（同口径标准 ULP）：

| 函数 | max ULP | 相对 libm |
|---|---|---|
| sin / cos | 1.00 | 1.6–3.4× |
| tan | 1.00 | 1.61× |
| log | 1.00 | 2.96× |
| exp | 0.00 | 2.92× |
| atan / asin / acos | 0.00 | 1.17–2.96× |
| sinh / cosh / tanh | 0.00 | 2.40–3.36× |
| pow | 1.00 | 2.71× |

表中 0.00 表示该域内全部样本与正确舍入结果的位模式一致。非 AVX2 平台上 High 档回退到逐 lane libm（精确但无加速）。
### 端到端加速

`tools/AutoSIMDPerf` 用相同调度参数交替运行同一 job 的托管实现与 AutoSIMD 原生实现（1M 元素 × 9 轮取中位，托管基线同样走并行调度器）。三次独立运行的中位加速：

| 用例 | 加速范围 | 最差相对误差 |
|---|---|---|
| ExpLog | 5.66–7.06× | 2.2e-7 |
| Transcend（9 个超越函数） | 6.95–8.72× | 1.2e-6 |
| HighPrecision | 7.45–8.63× | 3.6e-7 |

读数随机器负载浮动，跨运行不可相减；同一轮内的托管/原生是两个配对样本。