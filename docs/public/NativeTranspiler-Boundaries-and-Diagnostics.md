# NativeTranspiler：边界、诊断与回归防线

> ✅ **已修复（2026-09-26）——AutoSIMD 向量化部分的复合赋值曾被当作覆盖写**
>
> 缺陷形态（修复前）：`m[i] |= 1UL << (i & 63)` 的向量化产物是
> `((unsigned long long*)(m_ptr))[n_extract_lane_epi32((v_i).v,__l)] = static_cast<unsigned long long>(...)`
> —— 运算符被丢弃、旧值未读（`+=`/`&=`/`<<=` 同族同样中招），**静默错值**。
>
> 修复：`SimdExpressionTranslator.EmitElementStore` 现在读取 `assign.OperatorToken`：复合赋值统一发射
> **逐 lane 标量 load-modify-store**（掩码感知），不再退化成普通 store；普通赋值与原有向量 store 路径不变。
> 回归用例：`tests/NativeTranspiler.Tests/NT12_NT13_SimdStoreAndLiteralTests.cs::CompoundAssignment_MustReadModifyWrite_NotOverwrite`
> （停用该守卫即红、启用即绿）。同批修复的还有 **NT-13**（SIMD 侧数值字面量后缀：`1UL`→`1ULL`，与标量路径共用
> `CppNumericLiteral.NormalizeSuffix`，避免"同一规则两处实现、只修一处"）。
>
> ⚠ **排查教训（写给后续维护者）**：本轮定位该缺陷时，我在生成的**不同层级**插入过抛异常探针，探针异常会被
> 生成器转成 **NT026** 并触发 fallback 发射 —— 于是"探针没打印"被误读成"这段代码没被执行"，导致一度错误
> 结论"缺陷不在 `EmitElementStore`"。**用探针定位生成器问题时，必须区分"探针未触发"与"探针触发但异常被上层吞掉/降级"**
> （`OuterSimdGenerator` 等位置有 `catch (Exception) { return ""; }`）。

> **回归网运行须知（2026-09-26）**：`tools/AutoSIMDVerify`（23 项）与 `tools/AutoSIMDEdgeCases`（140 项）
> 通过 `DllImport`/`NativeLibrary` 加载 `NativeDll.dll` / `NativeTranspiled.dll`，而加载器**优先
> `AppContext.BaseDirectory`**。因此运行前必须把仓库 `bin\` 下的这两个 DLL 复制到工具的输出目录
> （`tools/*/bin/Release/`），否则会**静默**测到构建目录里残留的旧 DLL
> （实测：工具目录里留着 9/08–9/13 的旧 DLL 时报 `EntryPointNotFoundException`；
> 刷新后 AutoSIMDVerify 23/23、AutoSIMDEdgeCases 140/140 全过）。

> **ISPC 后端"静默错值"面审计（2026-09-27，逐条读码而非信注释）**：
> - **`return` 语义**：foreach 内裸 `return;` → `continue;`（等价跳过当前 lane）；批索引循环内裸 `return;` → `continue;`（等价结束本 index）；**嵌套循环内** `return` → 写 `__ENTJOY_UNSUPPORTED_STMT__ISPC_ReturnInsideNestedLoopInBatch`（构建失败）；foreach 内**带值** `return` → 降级 base，让 ISPC 编译器**报错**（暴露语义问题而非错译）。
> - **`SendEvent` 参数形态**：字段值形态（`Target = entities[i]`）走 `TranslateIspcNestedFieldWrite`，**已正确处理**；整个实参非对象创建（`SendEvent(已有变量)`）原本**静默丢写**，现发标记 ⇒ 构建失败。
> - **事件类型含非 4B 字段**（double/bool/byte/short/long）：生成 `__ENTJOY_UNALIGNED_EVENT_TYPE_…` 这一**故意的编译错误**（ISPC uniform struct 的 stride 假设会错），不是静默错布局。
> - **未知语句/表达式**：ISPC 翻译器继承 `CppPointerStatementTranslator → StatementTranslator`，`default:` 分支写 `UnsupportedMarkers.Stmt/Expr`。
> - **`IsIspcExtractableScalar` 返回 false**：回落 `base.TranslateInvocation`（不是丢弃）；`[Hint]` 包装被剥离属于**语义中性**（剥离的是提示而非语义）。
> - 结论：除上述已修的那一处，**未再发现静默错值路径**。⚠ 方法提醒：源码注释可能描述的是**历史缺陷**（如 `IspcStatementTranslator` 里"静默丢掉剩余 index"的注释，其下方代码已用 `continue` 修好）——判断必须以代码为准。

> 适用：`src/NativeTranspiler`（源生成器）+ `src/NativeTranspiler.Tasks`（原生编译任务）
> 最后核对：2026-09-13（第五批：**整数字面量后缀**、`ref` 局部支持、job 头文件收集、IJobEntity 体翻译器；
> 前四批：静默降级标记、批内 `return;` 语义、`IJobParallelForBatch`、`MathPrecision.High` 诚实化、
> 生成器陈旧门控、CI 覆盖、回归夹具）
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
| **NT024** | **error**（2026-09-27 由 warning 升级） | `AutoSIMD = Enabled`（IJobParallelFor/IJobFor/IJob）：实测整步比标量基线**慢 ~10%** ⇒ 默认不许开。要开必须**显式声明已量过**：MSBuild 属性 `<EntJoyAutoSimdMeasured>true</EntJoyAutoSimdMeasured>`（由 `EntJoy.Jobs.props` 的 `CompilerVisibleProperty` 传给分析器）。job 被排除后调用点会报 CS0103 —— 这是故意的"响亮失败" |
| **NT031** | error | `AutoSIMD = Enabled/Vectorize`（Cpp 后端）但 body 命中发射侧"不可向量化"判据（`Interlocked.*` / `UnsafeUtility.ArrayElementAsRef` / 用户静态辅助函数被喂 varying 实参 / 裸指针 + varying 下标 + 宽元素）⇒ 整个 body 退回 **per-lane 标量循环**（产物正确但**一点 SIMD 都没有**）。判据与发射侧共用同一份实现（`SimdVectorizability.HasNonVectorizableCall`），报错信息里指名具体构造。要么去掉该构造，要么显式 `AutoSIMD = Disabled` 说明"标量是有意为之"。**已知缺口**：`IJobEntity` 路径的 body 会被实体循环改写，判据需要对齐改写后的方法，暂未纳入（其余 job 形态覆盖） |
| **NT025** | error | `IJobParallelForBatch` + 非 Cpp 后端或 AutoSIMD（见 §3） |
| **NT026** | error | 生成器**自身崩溃**（NRE 等）：把异常堆栈落盘到 `%TEMP%/entjoy-native-transpiler-crash.txt` 并报出，避免历史上"`CS8785` + 连坐 `CS0234 Bindings 缺失`"这种看不出原因的失败 |
| **NT027** | error | `ref` 局部的**元素类型无法解析**（如 `ref var` 且无法推断）：无法生成 `T& x = …`。显式写出元素类型即可；其余 `ref` 局部**已支持**（见 §8.2） |
| **NT029** | error | 编译里出现 `IJobChunk`/`IJobEntity`（ECS）job，却检测不到 `EntJoy.ECS` 引用。**正常不可能发生**（这两个接口的类型定义就在 EntJoy.ECS 内）⇒ 出现即说明"job 种类判定与类型可见性不一致"，必须修生成器 |
| **NT030** | warning | 编译未引用 `EntJoy.ECS`，但生成的 bindings 仍出现 ECS 符号（`EntJoy.ECS`/`World`/`QueryBuilder`/`ArchetypeChunk`/`EntityManager`/`ChunkJobScheduler`/`ChunkJobData`/`ChunkEnabledMask`）⇒ 某个 ECS 相关发射点漏了条件化。消息里列出具体符号；若这些名字是你自己的类型，改名即可（词边界匹配，`MyWorldJob` 不误报） |
| **NT028** | warning | 本批有 `[NativeTranspile]` job 未通过校验、已从绑定生成中排除：其余 job 照常产出，被排除的 job 不会生成 `Schedule` 绑定（调用点报 `CS0103`，而不是整包 `CS0234`）。消息里列出被排除的 job，按各自的 error 诊断逐个修即可 |
| **NT032** | warning | 本编译单元的**托管内核清单**：`{N}` 个 job 实现了接口但没有 `[NativeTranspile]`（会静默走托管内核，语义正确、性能量级不同），另有 `{M}` 个原生 job。本意原生却漏写属性的，补上属性即可 |

> warning 不阻断生成：`NativeTranspilerGenerator` 只在存在 **error** 时终止（否则会把"事实告知"变成全员停工）。
> 反过来说，**已经实测负收益/不生效的模式一律用 error**、不用 warning：NT024（AutoSIMD 实测慢 ~10%）、
> NT031（AutoSIMD 整段退回 per-lane、等于没开）都属于这一类 —— warning 的后果是"用户照样能开一个
> 慢 10%（或干脆没向量化）的模式，只是构建日志里多一行"。
>
> 样例项目（`samples/EntJoySample`）显式声明 `<EntJoyAutoSimdMeasured>true</EntJoyAutoSimdMeasured>`：
> 它的 AutoSIMD job **就是**被测对象，并由 `tools/AutoSIMDVerify`（23/23）+ `tools/AutoSIMDEdgeCases`
> （140/140）逐值对照过 C# 标量基线。实测：把该属性关掉后，样例里 **35 处**（distinct 文件:行）
> `AutoSIMD = Enabled` 会立刻变成 error；而 NT031 在样例上是 **0 处**（样例的 AutoSIMD job 都可向量化）。

## 5. 已知边界（当前不打算改）

1. **AutoSIMD（`AutoSIMD.Enabled`，IJobParallelFor 路径）**：能编译、语义正确，但**无收益**
   （实测整步 +10.3%；16/18 个 job 因体内含原子/取引用/静态辅助函数而整体退回 per-lane 标量循环）。
   真向量路径仍有阻塞项（`cfgPtr[<varying>]` 的标量包装形状），未修完 —— 因期望收益为负而暂停。
   **2026-09-27（B3）后不再是"静默可开"**：这两种形态都改成 error（NT024 默认禁止、NT031 禁止
   per-lane 退回），见 §4；要把某个 job 的 AutoSIMD 留下来，得在项目里显式声明已量过。
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

- **CI job `native-transpiler-regression`**（`.github/workflows/jobsystem-ci.yml`）：
  `dotnet run tools/NativeTranspilerFixture -c Release`（真做一次生成 → 标记扫描 → CMake/ClangCL(或 MSVC 回退)
  → 原生编译 → 运行时语义断言）+ `negative-check.ps1`（负向门禁自检）。
  ⚠ 尚未挂进 `release-gate`：GitHub runner 上 clang-cl 组件是否存在未验证，先观察若干轮。
- **CI job `nuget-consumer-test`**（2026-09-17 新增，**已进 release-gate**）：跑
  `tests/NuGetConsumer/run.ps1` —— stage 预编译原生制品 → `dotnet pack` 四包 → 清 NuGet 缓存 →
  两个 `PackageReference`-only 消费者 build+run（`tests/NuGetConsumer` ECS 侧、`tests/NuGetJobsConsumer` Jobs-only 侧）。
  这是 CI 里唯一覆盖"**消费者从包消费**"的 job；不依赖 ISPC / clang-cl（只编 Cpp 后端，缺 ClangCL 回退 MSVC）。
- **发布 workflow `Publish NuGet`**（`.github/workflows/publish-nuget.yml`，2026-09-17 新增）：**仅由 `push v* tag` 触发**，
  没有手动入口（失败的 tag run 可在页面上 Re-run jobs）。顺序 = 先跑 `tests/NuGetConsumer/run.ps1` 当发布门禁 →
  推 GitHub Packages（`GITHUB_TOKEN` + `packages: write`，带 `--skip-duplicate`，作为镜像源）→ `NuGet/login@v1`
  用 OIDC 换一次性临时 API key → 推 nuget.org（**不带** `--skip-duplicate`：版本号忘提时必须让 workflow 失败，
  该开关会静默跳过并返回 0）。nuget.org 侧依赖仓库外的 Trusted Publishing 策略（Repository Owner / Repository /
  Workflow File = `tianqiyuan520` / `EntJoy` / `publish-nuget.yml`，Glob `EntJoy.*`，Scope "Push new packages and package versions"）。
  发版流程：改 `src/Directory.Build.props` 的 `EntJoyVersion`（四包 lockstep）→ 提交推分支 → 打并推 `v*` tag。
  ⚠ 未加"tag 名必须等于 `v$(EntJoyVersion)`"的守卫：打错 tag 会晚到 nuget.org 推送步骤才失败。
  ⚠ **`dotnet nuget push` 的通配符在 Windows 下只认反斜杠**：`"artifacts/packages/*.nupkg"`（正斜杠）会直接报
  `error: File does not exist (…)` 而**不推送任何包**（v1.0.0 首次发布即因此失败：门禁绿、GH Packages 步骤红、
  OIDC 与 nuget.org 步骤被跳过）。必须写 `"artifacts\packages\*.nupkg"`（本机对照实测确认，与绝对/相对路径无关）。
- **CI 步骤 `Jobs-only transpiler guard`**（在必检 `framework-test` 内）：跑 `tests/JobsOnlyTranspilerCheck/check.ps1`，
  断言"无 ECS 引用的工程能用 `[NativeTranspile]` 数组 job"（4 条断言 + 含 NT029/NT030 不得出现），
  并做过负向自证（耦合回退 → `FAIL[1]`；加 ECS 引用绕过 → `FAIL[3]`）。
- 背景：`framework-test` 用 `-p:EnableNativeCompile=false` 构建，`NativeCompileTask` 被条件跳过
  ⇒ **生成的 C++/ISPC 从不被编译、静默降级标记也从不被扫描**（由上面两个 job 补齐）。
- 夹具 `tools/NativeTranspilerFixture`（带原生编译目标，可直接 `dotnet run`）当前断言：
  1. `IJobParallelForBatch` 单批：每个 index 恰好处理一次、`count` 形参可用；
  2. `IJobParallelForBatch` 多批（64）：批边界长度正确；
  3. `IJobParallelFor` 批内 `return;` **只跳过本次 index**；
  4. `return;` 前后语句的可见性（前面执行、后面跳过）。
- 相关既有工具：`tools/AutoSIMDVerify`（AutoSIMD 与 C# 基线的逐值对照，23/23）——改 SIMD 生成器后**必跑**。

## 7.1 Jobs-only 解耦与包分发（2026-09-17）

**能力**：只引用 `EntJoy.Collections` / `EntJoy.Jobs`（**不引用 ECS**）的项目也能用 `[NativeTranspile]` 把
数组类 job（`IJob`/`IJobFor`/`IJobParallelFor`/`IJobParallelForBatch`）转译为 native 并运行。
`IJobChunk`/`IJobEntity`/`SendEvent` 仍属 ECS 能力（类型定义在 `EntJoy.ECS` 内，无引用时无法表达）。

**实现**：生成的 bindings 里三处 ECS 相关发射改为**按 job 种类条件化**——
`using EntJoy.ECS` / `using EntJoy.ECS.JobSystem`、`ChunkJobFuncDelegate`（形参含 `ChunkJobData*`）、
以及数组 job 的 `Schedule_*` 签名里那个**从未被使用**的 `World world = null`（数组 job 跑在裸 JobSystem 上，
没有 World 概念；ECS 类 job 的 `World` 参数与多 World 支持**保持不变**）。

**自校验不变量**：见 §4 的 **NT029/NT030** —— 解耦不再只靠"仓库内守卫项目跑一遍"来保证，
生成器自身会在每次生成后核对"是否引用 ECS"与"生成物是否含 ECS 符号"是否一致。

**包分发（prebuilt-native 模式）**：`EntJoy.Jobs` 包内含预编译 `NativeDll.dll` + 链接套件
（`NativeDll.lib` + 头 + `tasksys.cpp`）+ 分析器 + MSBuild 任务；包 props 会设
`EntJoyPrebuiltNativeDir`，生成器据此产出**只编 `NativeTranspiled`、链接包内 `.lib`** 的 CMakeLists
（不再 `add_library(NativeDll …)`、不编 imgui）⇒ 消费者**不需要** NativeDll 源码、也不需要 imgui 子模块。
写法与配置细节见 [Native Job：怎么写、怎么配](Native-Jobs-Guide.md)。

## 8. 第五批修复（2026-09-13）：三处"语法对、语义错"的转译缺陷

> 共同点：都不是编译错误，而是**生成的 C++ 语义与 C# 不一致**（前两个静默错值、第三个只在 unity build 分组变化时才报错）。
> 三者都是"框架侧通解修复"，消费方代码已还原成直白写法。

### 8.1 整数字面量后缀：C# 的 `long`/`ulong` 是 64 位，C++ 的不是

| 项 | 内容 |
|---|---|
| 现象 | 原生内核算出的位图/掩码**静默错值**：50,000 实体 enable 位图 782 个字里 781 个字错；体内探针 `(int)((1UL << 40) >> 32)` 实测 **-4**（C# 语义应为 **256**） |
| 根因 | 数值字面量 token 原样输出 ⇒ C# `1UL`（`ulong`，64 位）→ C++ `1UL`（`unsigned long`，**Windows/LLP64 下 32 位**）。`1UL << b`（b 可达 63）触发 `shift count >= width of type`（UB，clang 按 `& 31` 折叠）。`1L` 同理（C++ `long` 32 位） |
| 独立复现 | 把生成的循环抄成独立 clang-cl 程序（`/O2`）：`word0=0x00000000FFFFFFFF popcount=25006` + 警告 `shift count >= width of type`，与消费方输出逐位一致 |
| 修法 | `StatementTranslator.NormalizeNumericLiteral(text)` 钩子（默认恒等 ⇒ ISPC 不受影响）；C++ 后端覆盖：`UL`/`LU` → `ULL`、`L` → `LL`（`U`/`u` 两语言同为 32 位，保留）。十六进制分支同样过钩子（`0x1UL` → `0x1ULL`） |
| 自检 | 生成产物里出现 `1ULL` / `0ULL`；消费方体内探针回到 256，位图 782 字全对 |

**写代码时的建议**：64 位掩码/移位一律用 `1UL << b` 这类**C# 语义**写法即可（转译器负责后缀），
不要再为了"稳妥"手写 `(ulong)(1)` 之类的绕法。

### 8.2 `ref` 局部已支持（此前在 `Nullable=enable` 下打崩生成器）

- 形态：`ref EntityLocateB e = ref Lookup.Locate[id];` → C++ `EntJoy::ECS::EntityLocateB& e = Lookup.Locate[neighborId];`
  （`StatementTranslator.TranslateLocalDeclaration`：`RefTypeSyntax` ⇒ 输出 `T&`，并把初始化器的 `ref` 前缀剥掉）。
  引用即引用 ⇒ 体内后续赋值天然写回，**不需要**退出时回写。
- 为什么以前必须绕：`Nullable=enable` 下 `GetTypeInfo(Type).Type` 对 `ref T` 返回 null
  ⇒ 旧代码把它直接喂给 `MapCSharpTypeToCpp`（NRE）⇒ 生成器整体崩溃（`CS8785`）⇒ bindings 不生成 ⇒
  消费方连坐 `CS0234: 命名空间 "NativeTranspiler" 中不存在 "Bindings"`。
- 现在的防线：类型解析回退到**声明符号类型**（`ILocalSymbol.Type`）；真解析不到才报 **NT027**（error）；
  生成器任何未捕获异常报 **NT026** 并把堆栈落盘。
- ISPC/其它后端同样走基类的这条路径（本轮未改 ISPC 的字面量行为）。

### 8.3 生成的 job 头文件必须包含"体内用到的"用户结构体

- 现象：`no member named 'X' in namespace 'Y'` —— 体内 `LPos* p = …` 会写出**全限定 C++ 名**，
  但 job 的 `.h` 没 include `Y_X.h`。
- 为什么长期没暴露：unity build 把该类型头文件从**别的 TU** 带了进来 ⇒ 依赖编译顺序"偶然可见"，
  本轮新增文件改变 unity 分组后才显形。
- 根因：`CppJobGenerator.CollectJobStructIncludes` 只从**字段类型**收集，且泛型只在"EntJoy 容器类型"时递归
  ⇒ `NativeComponentLookup<T>`（非容器）的实参、以及体内局部类型都不收集。
- 修法：泛型**一律先递归实参**（EntJoy 容器类型仍提前返回；EntJoy 自身的泛型继续走原"结构体头文件"分支）。

### 8.4 IJobEntity 原生体改用 `CppEntityStatementTranslator`

- 现象：原生 `IJobEntity` 体内写 `Out[i] = …`（`Out` 是 `NativeArray<int>` 字段）报
  `use of undeclared identifier 'Out'` —— 生成函数的形参其实叫 `Out_ptr`。
- 根因：IJobEntity 的 Execute 体用了**基类 `StatementTranslator`**（它不认识"job 字段 → 生成函数形参"的映射），
  只靠字符串替换处理组件参数。
- 修法：新增 `CppEntityStatementTranslator : CppPointerStatementTranslator`（容器字段 → `_ptr`/`_length`、
  `GetUnsafePtr()` → `_ptr`、指针字段 → `_ptr`），并**关闭 wrap-safe int 算术**
  （IJobChunk 路径本来就带 wrap-safe；IJobEntity 既有产物是裸算术，保持原形态避免无谓回归）。
  Vectorize / Standard / EntityChunk 三处生成点统一换用。
- 验收：原生 IJobEntity + `NativeArray` 辅助表 + `Entity` 参数逐实体不一致=0
  （样例 `samples/EntJoySample/13_EnableBitMapNative` 第 [8] 段）。

---

## 9. 代码生成开关的实测边界（2026-09-16，来自一个 1M 单位真实内核的配对实验）

> 来源：Godot/EntJoy 项目 `CPUBattle`（100 万单位、8Hz、15 worker、同一冻结状态）对 `MeleeSimJob` 的成对实验。
> 方法：**同会话 + 逐轮交替臂序 + n≥6–8**，两臂只差一个变量（除注明外两臂同一 `NativeDll`）；判据为 Melee 段中位比值（A/B）。

### 9.1 唯一有效的方向：让同 TU 代码能内联

| 做法 | 实测 | 说明 |
|---|---|---|
| **unity-build 批大小阈值**（`≤96 拆批`）→ 169 个生成文件被拆成 **13 个 TU** | Melee **−10.0% / −9.6%**（两次独立验收） | 生成内核调用的静态辅助函数落在**别的 TU** ⇒ 无 LTO ⇒ 无法内联。阈值已由 `96` 改为 `512`（本仓提交 `426c048`）⇒ 发射件变为 `CMAKE_UNITY_BUILD_BATCH_SIZE 0`（单 TU） |

### 9.2 其余"代码生成侧"开关：本工程实测为中性或负（**别再重复试**）

| 开关 / 做法 | Melee 中位比值 | 判读 |
|---|---|---|
| `-O3 -funroll-loops`（只加在 NativeTranspiled） | 1.0009 | 中性 |
| 值绑定 `const T& X = *X_ptr;` → `const T X = *X_ptr;` | 1.0287（5/6 轮更差） | 负 |
| 指针别名声明单点下沉 / 按使用块复制下沉 | 1.0402（4/6 轮更差） | 负 |
| 形参打包（96 → 65 形参，`ENTJOY_PACK_SCALARS=1`） | 1.0005 | 中性 |
| `AutoSIMD = Enabled`（IJobParallelFor） | 生成物**完全没有 SIMD**（per-lane 标量包装，0 个 `simd_mask`） | 诊断 **NT024**（默认 error：实测慢 ~10%）与 **NT031**（body 不可向量化 ⇒ 整段退回 per-lane）现在都把这条堵在构建期 |
| **`-mllvm -inline-threshold=2000`**（把剩余 4 处外呼也内联） | **1.1132（8/8 更差）** | 负，且很重 |
| **PGO**（`-fprofile-instr-generate` 训练 + `-fprofile-instr-use`） | **1.2224（8/8 更差）** | 负，且很重 |

**规律**：只有"**减少外呼**"为正；任何"**往内核里再加代码**"（更激进内联、PGO 引导的更激进内联/展开）都明显为负 —— 该内核已贴在**寄存器/栈流量极限**上（其栈引用密度 ~26%，元素循环里同时存活约 22 个值 vs ~14 个 GP 寄存器）。

参考对照：同算法在 Unity Burst（AOT/AVX2）下为 **1465 条指令**，本框架为 **1891 条（多 29%）**；但**外呼数（8 vs 7）、栈引用密度（26.8% vs 26.2%）、浮点算术（141 vs 142）三项都与对手持平**——多出来的 ~425 条集中在**整数搬移（+146 `mov`）、地址计算（+54 `lea`/`movsxd`）、条件判断（+62）与分支（+84）**。⇒ 差距在"搬数 / 算地址 / 再判一次"这一层，不在数学、不在调用、不在溢出。生成内核的**静态普查**（指令数 / 栈引用比 / 外呼数 / **按类别的指令构成** / 函数体字节）在本项目中是发现这类结构性缺陷最快的手段，建议纳入回归夹具。
⚠ 统计口径：**分词后按操作码字段**归类，不要用行首/行内正则猜指令 —— 曾用 `-match '^call'` 匹配以地址开头的行，把外呼数恒算成 0，从而写出"对手 0 外呼/完全内联"的错误结论。

### 9.2b 接管复核（2026-09-26）：`+29% 数据搬运` 能否用生成器局部改动解决 → **不能**（有证据）

一审结论"补对齐/非负/无别名事实"这句**不能照做**，逐条复核如下（工具 `tools/CodegenAsmProbe`，MSVC `/O2 /arch:AVX2`）：

| 设想的事实 | 复核结果 |
|---|---|
| **无别名**（给分量指针加 `__restrict`） | 铺开后 15 内核合计仅 **−2.1% 指令**（`mov` −6.7%，但 `lea/movsxd` **+2.0% 反升**），且 `snap_autosimd_chunk` **回归 +56.5%**；别名判定本身也不完整（同一 `NativeArray` 二次绑定、带副作用索引时都不该加）⇒ 已把 `RESTRICT` 宏定义为空（见 `CodeTemplates.cs`，保留宏名便于后续逐处收窄再启用） |
| **对齐**（`__assume(ptr % 64 == 0)`） | **不成立**：分量指针来自 `componentArrays[]`，框架**没有** 64B 对齐保证 ⇒ 该 `__assume` 是 UB 提示（编译期可据此丢弃边界处理）。已退化为无副作用表达式 `(void)(...)`；`/O2` 下它不产生指令，即"补对齐事实"这条**目前无安全可用的事实可补** |
| **非负**（`__assume(idx >= 0)`） | 同样无契约保证（调用方可传负 `__startIndex/__count`）⇒ 不可加 |
| **减少整数搬移**（值绑定 / 别名声明作用域收窄） | §9.2 已实测为负（1.0287 / 1.0402） |

**普查可复现性（同一次复核内验证）**：同样的生成物 + 同样的 flags 连跑两次，9 个非 `@emit` 内核**逐项完全一致**（例：`melee_pack` 310 / mov 85 / lea 32 / cmp 31 / branch 40 —— 两次相同）⇒ 该仪器可用于 before/after 决策。
⚠ 但**两臂必须都刷新 `@emit` 语料**：`-SkipSnapshot` 那一臂会静默丢掉 6 个 `@emit` 内核（TOTAL 1548 vs 1955），TOTAL 直接不可比（实测踩到）。

**本仓当前（提交 `62d892d`）Melee 形状内核的定格数据**（fixture `BenchMeleeScanPackJob`，MSVC `/O2 /arch:AVX2`）：
`total 310`、`scaffold 188（60.6%：mov 85 / lea+movsxd 32 / cmp+test 31 / j* 40）`、`fpArith 10`、`fpMove 42`、`call 0`、栈引用 24.2%、函数体 929 B。

**判读（可执行结论）**：多出来的 425 条**集中在标量脚手架**，机制是"元素循环里同时存活约 22 个值 > ~14 个 GP 寄存器"⇒ 溢出/重载/地址重算，属**后端寄存器分配与活跃区间**问题，**不是**生成器少写了几条语句。所有已试的生成器局部改动（restrict / 值绑定 / 别名作用域 / `-O3 -funroll-loops` / 内联阈值 / PGO / 形参打包）实测**中性或为负**；因此：
1. **不要再尝试**"在生成代码里补舍入/对齐/非负假设"这类局部补丁 —— 无安全事实可补，且历史尝试全为负；
2. 想真正收敛这 29%，只剩两条路：**（a）改内核源码以缩短活跃区间**（属内核/样例工作，非生成器）；**（b）宽 SIMD 形状改走 ISPC 后端**（`Target = Ispc`，框架已支持，见 §7/§8）；
3. 任何后续尝试**必须**用同会话交错 A/B 的**墙钟**判定，不能只看普查指令数 —— `__restrict` 正是反例：指令 −2.1% 却无墙钟收益、还带来单内核 +56.5% 回归。

### 9.2c B2 内核结构实验（2026-09-27）：逐格元数据预计算 → 静态指令 −4.8%，**墙钟无收益**

路线（a）"改内核源码以缩短活跃区间"在本仓的唯一可测载体是 Melee 形状的**夹具内核**（外部工程那个
内核源码不在本仓）。本轮把 §9.2b 判读指向的方向做成一支实验臂并量到底：

- **实验臂**：`tools/NativeTranspilerFixture/BenchOffsetsJob.cs` 的 `BenchMeleeScanOffsetsJob` ——
  与 `BenchMeleeScanPackJob`（= 普查里的 `melee_pack`）**逐行相同**，只改一处：把热循环里逐格计算的
  `j % 9 - 4` / `4 - j / 9` / `oy * cellsW` 换成调用方预计算的 `CellDelta[jj] = ox - oy * cellsW`
  ⇒ 循环体少一次 `ScanOrder[jj]` 载入、两处"除以 9"的魔数乘序列、一次乘法。
- **语义等价**：bench 模式用同一输入 + 每臂前置状态清零（该内核会读回自己写下的 `KD2` 槽位，清零是
  公平 A/B 的前提）对拍 `KD2` 与 `KPeer` 校验和，**逐位相同**（`Report(...)` 计入夹具断言门）。

**普查（MSVC `/O2 /arch:AVX2`，`tools/CodegenAsmProbe`，`-KernelFilter melee`）**

| kernel | total | mov | lea* | cmp | branch | scaffold | stack% | bytes |
|---|---|---|---|---|---|---|---|---|
| `melee_pack`（基线，**与 `e1-base.json` 逐项相同**） | 310 | 85 | 32 | 31 | 40 | 188 | 24.2 | 929 |
| `melee_offsets`（本变体） | **295** | 83 | **29** | 30 | 40 | **182** | **21.0** | **890** |

⇒ total **−15（−4.8%）**、scaffold **−6（−3.2%）**、函数体 −39 B、栈引用 24.2% → 21.0%。
（`melee_pack` 与 `e1-base.json` 完全一致 ⇒ 该仪器与本轮的生成物/标志可复现。）

**墙钟（同会话交错 A/B：8 对、臂序逐对交替、每臂 6 次重复 × 1e6 元素）**

| 臂 | median | p95 |
|---|---|---|
| `melee_pack` | 164.9 ns/elem | 178.8 |
| `melee_offsets` | 162.6 ns/elem | 181.8 |

对级比值 `off/pack` 落在 **0.93–1.11**、中位 ≈ **0.99** ⇒ **测不出收益**（该夹具自身噪声地板 ~±10%，
见 `CodegenAsmProbe/README.md`）。

**判读**：静态指令砍掉 ~5% 却不改变墙钟 ⇒ 本形状是**内存/延迟受限**（~165 ns/元素由
`CellStart[hash]` / `SortedIndex[s]` / `Positions[i]` 的随机访问主导），与 §9.2b 的结论一致：
多出来的脚手架**不是墙钟瓶颈**（属后端寄存器分配与访问模式问题），也再次证明 §9.2 的规则——
**不能用静态指令数替代墙钟判定**。

**复现**：
`dotnet run --project tools\NativeTranspilerFixture\NativeTranspilerFixture.csproj -c Release -- bench`
（打印 8 对交错 A/B + 中位/p95 + 两条等价性断言；已并入本轮门禁 `fixture-bench`）；
普查用 `tools\CodegenAsmProbe\probe.ps1 -KernelFilter melee`（`kernels.txt` 已追加 `melee_offsets` 条目；
⚠ 普查工具与基线不在版本库，见未决清单 #3）。

**剩余（仍未收敛的 29%）**：真正要收敛的是**外部工程那个内核**（1M 单位真实内核，源码不在本仓），
本轮只能证明"这类可静态消掉的逐格整数运算不是瓶颈"；另一条路 ISPC 后端在该外部工程上的实测是
**负收益**（§5：Cpp 113–119 ms vs ISPC 129–133 ms），故本仓 **B2 到此为止**：生成器侧杠杆（§9.2 的
"不要再试"清单）与内核结构侧的可静态消项均已量到底，剩下的只有"在真实内核上做数据布局/访问模式
重构"这一件仓外工作。


**换工具链版本已验证无用（2026-09-16 实测）**：VS 自带 clang **19.1.5** 与官方 **LLVM 23.1.1**（独立解压、`-DCMAKE_CXX_COMPILER` 指定）编译同一份生成物 ⇒ **整个 DLL 的指令流逐条相同**（18,073 条；Melee 内核 1891 条/9360 B/栈引用/外呼数/类别计数全等）。
⇒ **不要靠升级 LLVM 版本来改善内核代码质量**；差距在**喂给 LLVM 的 IR 与选项**（Burst 从 IL 生成 IR 时带别名/对齐/假设元数据，而生成代码目前只给了 `__restrict` 形参）。~~下一步应做的是在生成代码里补对齐/非负/无别名事实~~ ⚠ **这句已被 §9.2b 的复核推翻**（对齐/非负无契约可依、`__restrict` 实测为负）——请以 §9.2b 的结论为准。
⚠ 换工具链或被跳过时的两个陷阱：`NativeCompileTask` 有"输入哈希未变即跳过"的早退门；CMake 缓存会沿用已记录的编译器路径（工具集名不变就不 reconfigure）⇒ 换工具链**必须先删 `build\`**。

**采样佐证（WPR CPU profile，同一次运行，需管理员）**：进程级 168,168 个采样点中，**生成内核（NativeTranspiled.dll）占 76.3%**、托管宿主 8.3%、**框架运行期（NativeDll.dll：JobSystem/Collections/调度）只占 8.2%**、其它 ~7%。
内核内 **MeleeSimJob 占生成内核的 64.3%**，而其内部 **67% 的采样落在候选内循环**；该循环里 **20.7% 的采样点落在带栈操作数的指令上**（形参/基址重载、循环状态溢出），最热单指令则是 d² 计算链与门控（`vsubps` 5.3% / `vucomiss` 5.1% / `vmovshdup` 4.5%）。
⇒ 结论：**"优化框架运行期"的量级上限≈8%；生成代码那 76% 属后端代码生成质量**，与上面的开关结论一致。

**框架运行期再拆一层（同 trace，`NativeDll.dll` 13,777 采样点）**：**87.8%（占进程 7.19%）落在同一条 worker"等活"自旋循环**（`ChaseLevScheduler::WorkerLoop` park 段，`pause; dec eax; jne` + 双队列 head/tail 检查），在 15 个 worker 上均匀分布（每线程约 3.6% 的时间）≈ **1 个核当量**；`JobSystem_Complete` ≈3.9%、其余内部函数/调试钩子 ≈8.3% ⇒ **框架真正的簿记工作只占进程 1.0%**。
⚠ 但这条自旋**不该动**：`ENTJOY_SPIN_BUSY` 8192→256 的配对 A/B 是 1.0115 / 1.0067 / 1.0004（无收益），机制是 Zen 4 的 `_mm_pause` 会让出 SMT 执行槽、不吃内存带宽 ⇒ **"烧掉的 CPU"不等于墙钟成本**；本采样只把它量化为 ~1 core 并再次确认它不是成本。

### 9.3 量测这类改动时的四个坑（本项目实际踩过）

1. **构建期开关必须强制重编**：`dotnet build` 增量命中时**生成器根本不跑**（实测 4.4 s 空跑、发射件未变）⇒ 加 `--no-incremental`；且环境变量会被 **MSBuild 复用节点粘住** ⇒ 设/清变量前 `dotnet build-server shutdown`。
2. **改 job 字段列表 = 改原生 ABI**：此时 A/B **不能只换原生 DLL**，必须同时换托管程序集（绑定签名来自生成代码），否则两臂当场全废（实测 8/8 轮 INCOMPLETE）。
3. **栈流量统计必须 `[rsp]` + `[rbp]` 一起数**：clang-cl 默认省略帧指针、Burst 保留 rbp 帧；只数 `[rsp]` 会得出"对侧少 6 倍"的假象（真实两侧相同）。
4. **不要拿二进制哈希当"是否同一版本"的判据**（PE 时间戳/常量地址会变）；等价性用同会话配对性能 + 生成物比对。
5. **普查工具的两臂必须对称刷新语料**：`probe.ps1 -SkipSnapshot` 会静默丢掉 6 个 `@emit` 内核（TOTAL 从 1955 掉到 1548）⇒ TOTAL 不可比；只有 `-KernelFilter` 收窄时必须确认两臂过滤一致。

## 10. 生成物清理（D1）的契约：**只删自己写出的文件**（2026-10-07 修）

`PruneStaleGeneratedFiles` 的作用是"删掉本次没生成、但上次遗留的产物"，口径是
`SharpNative_*` + 扩展名 `.cpp/.h/.ispc`，判据是"**在不在本次写出集合里**"。
这个口径曾经吃掉**别人的产物**，代价是每次重新生成都重编一次全部 ISPC：

- `SharpNative_<X>_ispc.h`（含 `_mt` 多目标变体 `<X>_mt_ispc.h`）是 **`ispc.exe -h` 的输出**，
  在生成的 CMake 里声明为 `add_custom_command(OUTPUT …)`；**生成器从不写它** ⇒ 它永远不在写出集合里
  ⇒ 每次生成都被删 ⇒ CMake 判定 ISPC 步骤输出缺失 ⇒ **31 个 `ispc.exe` 全部串行重跑**（每次 ~0.4 s）。
- 实测（`EntJoySample`，改一个 job 体的一个常量）：生成后 `*_ispc.h` 数量 **0**，
  直接 `cmake --build … --target NativeTranspiled` **15.9 s / ISPC 31 次**，端到端 `dotnet build` **19.8–22.8 s**。

**修后的规则**：`<X>.ispc` 仍是本次期望产物 ⇒ 保留 `<X>_ispc.h`；`.ispc` 真没了（job 被删）才连头一起删。
同一处改动实测：头数 **31**、原生 **2.80 s / ISPC 0 次**、端到端 **9.5 s**；
D1 原目的未削弱（用伪造的 `.ispc` + 同名头验证过：两个都被清掉）。

**对消费者的含义**：`SharpNative_*_ispc.h` 归 **ISPC 编译步骤**所有，不是生成器的产物 ——
任何"按前缀清理生成目录"的工具/脚本都必须把它们排除；删它们不会报错，只会让下一次原生编译整份重做。
另：交付档的 UNITY 批大小保持 `0`（单 TU）。曾为此加过一个 `EntJoyNativeUnityBatch` 开关，
同日撤销 —— 拆批实测反而更慢（同一处改动：单 TU 2.70 s / 18 个小批 4.89 s），
见 [Gates-and-Flags.md](Gates-and-Flags.md) E 节与 §9.1 的内联结论。
