# NativeTranspiler：边界、诊断与回归防线

> 适用：`src/NativeTranspiler`（源生成器）+ `src/NativeTranspiler.Tasks`（原生编译任务）
> 最后核对：2026-09-13（对应本轮修复：静默降级标记、批内 `return;` 语义、`IJobParallelForBatch`、
> `MathPrecision.High` 诚实化、生成器陈旧门控、CI 覆盖、回归夹具）
>
> 本文只写"容易踩、且踩了不报错"的部分。API 用法见 README。

## 1. 构建期防线：静默降级一律失败

生成器遇到**无法转译**的语句/表达式时，不得静默丢弃，必须写唯一标记：

| 标记 | 含义 |
|---|---|
| `__ENTJOY_UNSUPPORTED_STMT__<构造>` | 语句级：整条语句无法转译（历史上 `unchecked { }` 被丢成空函数体 = C++ UB） |
| `__ENTJOY_UNSUPPORTED_EXPR__<构造>` | 表达式级：该表达式被替换成占位符会算错（历史上 `histPtr[key]++` 被翻成 `0;`） |

- 单一来源：`Analyzer/Common/UnsupportedMarkers.cs`（新增"跳过某个构造"的分支时一律走它）。
- 拦截点：`NativeCompileTask.CheckGeneratedMarkers` **只认前缀 `__ENTJOY_UNSUPPORTED`**，
  扫描范围 `NativeTranspiler_Generated/*.cpp` + `*.ispc`，命中即 **error 让构建失败**，并打印文件名+行号+标记名。
- 该扫描**必须早于**增量哈希短路（否则"上一轮的坏产物 + 本轮跳过"会把坏内核当最新交付）。
- 负向自检：`tools/NativeTranspilerFixture/negative-check.ps1`（把含 `unchecked { }` 的夹具编进来，
  断言构建**失败**且报错含标记名）。

## 2. 批调度语义（`return;` 与循环）

三种"批调度" job 在 C++ 侧的形态完全不同：

| 接口 | C# 形态 | C++ 生成形态 | 体内 `return;` 的语义 |
|---|---|---|---|
| `IJobParallelFor` / `IJobFor` | `Execute(int index)` | `for (index = __startIndex; ...) { <体> }` | **只结束本次 index**；体内出现 `return` 时，循环体被包进立即调用 lambda `[&](){ ... }()`，使 `return` 不会穿出整个批 |
| `IJobParallelForBatch` | `Execute(int startIndex, int count)` | 直接 `<体>`（**不生成**循环），`startIndex→__startIndex`、`count→__count` | 退出本次区间调用（与 C# 一致，无需包装） |
| `IJob` | `Execute()` | 单次调用 | 退出整个 job（与 C# 一致） |

- ⚠ **`do { } while(false)` 不能修这个问题**：它只重定向 `break`，`return` 照样退出函数。
  实测：1024 元素单批、index 0 处 `return;` ⇒ 只有 index 0 被处理（其余 1023 个静默丢失）。
- 体内无 `return` 的 job **产物与改动前逐字相同**（不引入任何额外代码/性能变化）。
- ISPC 侧：批索引循环是 `for (uniform int ...)` 时，index 层级的 `return;` 降级为 `continue;`；
  **嵌套循环里的 `return;` 无法表达** ⇒ 写 `__ENTJOY_UNSUPPORTED_STMT__ISPC_ReturnInsideNestedLoopInBatch`
  让构建失败（旧行为是静默跳过整批剩余 index）。

## 3. `IJobParallelForBatch` 的支持范围

- 支持：`Target = Cpp` + `AutoSIMD.Disabled`（默认）。调度走 `ScheduleParallelForBatchRaw`，
  绑定层与 `IJobParallelFor` 同一形态：`job.Schedule(arrayLength, innerBatchCount)`。
- 不支持：`Target = Ispc`、`AutoSIMD = Enabled/Vectorize`（这些路径只认逐 index 的 `Execute(int)` 形态）
  ⇒ 报 **NT025**（error），而不是静默按别的形态生成。
- 为什么值得用：调度粒度是这批 job 的性能主导项。逐 index 任务（`IJobParallelFor` + `batchSize=0`
  的自动分块）在 1M 元素上约 140ms 的调度开销；改成"少数任务各跑一段"后实测降到 ~11ms。

## 4. 诊断清单（生成器报出的）

| ID | 级别 | 内容 |
|---|---|---|
| NT008 | error | 未实现任何 Job 接口（消息列出全部允许的接口，含 `IJobParallelForBatch`） |
| NT003 / NT006 | error | 局部数组/托管类型、引用类型静态字段等**语言能力边界**，消息里给出替代写法（NativeArray/UnsafeList 传入、if/else 或静态查表代替 `switch` 表达式） |
| NT018 | error | `AutoSIMD = Vectorize` 但 job 不是 `IJobChunk`/`IJobEntity`（该路径未实现，会被静默丢弃） |
| NT019 / NT020 / NT021 / NT022 | error | 属性与后端不匹配（ISPC 不读 AutoSIMD / MathPrecision / CppMathLib / UseISPC_MT） |
| **NT023** | warning | `MathPrecision = High` **没有实现**：`NativeSIMD_math.h` 的 `== 2` 分支为空，产物与 `IEEE` 逐字相同；`Fastest` 才有 AVX2/AVX512 内联多项式 |
| **NT024** | warning | `AutoSIMD = Enabled`（IJobParallelFor/IJobFor/IJob）：实测整步比标量基线**慢 ~10%**，且命中原子/取引用/用户静态辅助函数的 job 会整段退回 per-lane 标量循环 |
| **NT025** | error | `IJobParallelForBatch` + 非 Cpp 后端或 AutoSIMD（见 §3） |

> warning 不阻断生成：`NativeTranspilerGenerator` 只在存在 **error** 时终止（否则会把"事实告知"变成全员停工）。

## 5. 已知边界（当前不打算改）

1. **AutoSIMD（`AutoSIMD.Enabled`，IJobParallelFor 路径）**：能编译、语义正确，但**无收益**
   （实测整步 +10.3%；16/18 个 job 因体内含原子/取引用/静态辅助函数而整体退回 per-lane 标量循环）。
   真向量路径仍有阻塞项（`cfgPtr[<varying>]` 的标量包装形状），未修完 —— 因期望收益为负而暂停。
2. **ISPC 的 uniform 标量循环边界**：`Target = Ispc` 的 job 里，走 uniform 串行路径（体内有自修改
   `NativeArray` 或使用 `Interlocked` 返回值）的 job 会在"uniform 调用点 vs varying 签名的 helper"处失败。
   当前实测（以 CPU 百万同屏工程的 19 个 job 为例）：**全部可编译（19/19）**。
   ISPC 相对 Cpp 实测无收益（步均 +6.8ms；uniform 路径的 job 接入后整步再 +13ms、约 129-133ms vs Cpp 113-119ms）
   —— **能编 ≠ 值得用**，该工程交付态仍是 Cpp。
   - 已修 ①：**标量返回值桥接** —— uniform 模式下调用同程序集的用户 helper，返回值（ISPC 侧是 varying）
     自动包 `extract(<call>, 0)`。语义依据：uniform 路径一次只处理一个 index，各 lane 值相同，取 lane 0 等价标量。
     实测：`SpawnJob`（`float lockR = killRange * (0.85f + 0.3f * HashUnit(uid))`）。
   - 已修 ②：**指针形参转型** —— 裸指针形参在 helper 侧是 `uniform T * varying`，uniform 实参是
     `uniform T * uniform` ⇒ 插显式转型。**只动指针级，绝不动 pointee 的 uniform/varying**。
   - 已修 ③：**`ref`/`out` 形参物化槽** —— helper 侧是 `varying T * uniform`，而 uniform 上下文里的
     `&局部量` 是 `uniform T * uniform`，两者**不能**靠类型转换糊过去。生成器改为：
     ```ispc
     {                                  // 自带宽括号：C# 的 `if (c) Helper(ref x);` 是无括号单语句
         varying T __ej_uref0[1];       // 物化槽（结构体逐字段广播）
         __ej_uref0[0].f = (varying F)x.f;
         Helper(&__ej_uref0[0], ...);   // 其余实参按上面两条转型
         x.f = extract(__ej_uref0[0].f, 0);
     }
     ```
     实测：`IntegrateJob` 调 `CpuObstacle.ProjectOutOfWalls(ref pos, ref vel, …)` 现在可编，
     且整局 `[AliveCurve]` 与 Cpp 基线在相同仿真时刻一致（t≈17.4s：993,665 vs 993,664）。
     桥接范围：ref/out 仅支持**标量或仅含标量字段的结构体**；形参含 NativeArray/NativeList（一个 C# 形参
     对应两个 ISPC 形参）或结构体按值传参时**不做桥接**，仍走原路径（会报普通的 ISPC 重载错误）。
   - 两条 ISPC 硬约束（探针实测，别再试）：`*(varying T * uniform)p` 是 **gang 宽连续（SOA）**布局
     （lane i 读 p[i]，实测 `100 101 102 … 107`）⇒ 把 `&uniformLocal` 强转成 varying pointee 会读到
     **该局部量之后的栈内存**；`extract()` **只支持标量**（struct 报 `Unable to find any matching overload`）。
3. **语言能力**（见 NT003/NT006）：局部数组、`static readonly` 引用类型、`switch` 表达式、
   白名单外的 `MathF.*`（如 `MathF.Sign`，且 .NET 语义对 NaN 抛异常）都不支持；
   生成器现在会**失败并指出替代写法**，而不是猜。
4. **`SimdMathPrecision` 的 `High`**：只有 `Fastest`（AVX2/AVX512 多项式）与 `IEEE`（逐通道标量）两条真实路径。

## 6. 生成器陈旧门控（"改了生成器但产物没重生成"）

Roslyn 的 `CoreCompile` 内容哈希门控会让"只改生成器、不改 C# 源"的构建**整轮不跑源生成器**，
而 `NativeCompileTask` 的哈希门控又跳过 CMake —— 结果是"跑的仍是上一版产物"且**没有任何提示**（多次踩过）。

现在有两道显式防线：

1. **内容哈希版本戳**：生成器每次运行写 `NativeTranspiler_Generated/generator.stamp`
   （生成器程序集 MD5 + 版本 + 时间）。任务比对"戳里的哈希 vs 当前生成器哈希"，
   不一致即报 **warning**（用内容而不是时间戳，重编但内容未变不会误报）。
   注意：该文件**不进** native 依赖哈希（内容含时间戳会让每次构建都重编 CMake）。
2. **生成器程序集进 native 依赖**：`CollectDependencies` 把 `src/NativeTranspiler/bin/<Cfg>/netstandard2.0/NativeTranspiler.dll`
   计入内容哈希 ⇒ 生成器一变，至少不会"跳过编译却以为最新"。

强制重生成配方（warning 里也会打印）：删 `<项目>/.godot/mono/temp/obj/<Cfg>`（非 Godot 项目是 `obj/<Cfg>`）
+ `NativeTranspiler_Generated/build` + `NativeTranspiler_Generated/native_compile.hash`，再重新构建。

## 7. CI 与回归夹具

- **CI 新增 job `native-transpiler-regression`**（`.github/workflows/jobsystem-ci.yml`）：
  `dotnet run tools/NativeTranspilerFixture -c Release`（真做一次生成 → 标记扫描 → CMake/ClangCL(或 MSVC 回退)
  → 原生编译 → 运行时语义断言）+ `negative-check.ps1`（负向门禁自检）。
  ⚠ 尚未挂进 `release-gate`：GitHub runner 上 clang-cl 组件是否存在未验证，先观察若干轮。
- 背景：原来的 `framework-test` 用 `-p:EnableNativeCompile=false` 构建，`NativeCompileTask` 被条件跳过
  ⇒ **生成的 C++/ISPC 从不被编译、静默降级标记也从不被扫描**。
- 夹具 `tools/NativeTranspilerFixture`（带原生编译目标，可直接 `dotnet run`）当前断言：
  1. `IJobParallelForBatch` 单批：每个 index 恰好处理一次、`count` 形参可用；
  2. `IJobParallelForBatch` 多批（64）：批边界长度正确；
  3. `IJobParallelFor` 批内 `return;` **只跳过本次 index**；
  4. `return;` 前后语句的可见性（前面执行、后面跳过）。
- 相关既有工具：`tools/AutoSIMDVerify`（AutoSIMD 与 C# 基线的逐值对照，23/23）——改 SIMD 生成器后**必跑**。
