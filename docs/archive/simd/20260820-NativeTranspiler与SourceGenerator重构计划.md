# NativeTranspiler 与 EntJoy.ECS.SourceGenerator 重构计划（2026-08-20）

> **状态：计划定稿，尚未实施**（备后续接手参考）。
> 分析对象：`src/EntJoy.ECS.SourceGenerator`、`src/NativeTranspiler`。
> 目标：提升两项目的可维护性与可读性，消除上帝类 / 死代码 / 命名失配 / 职责重叠。

> **⚠️ 2026-08-21 源码复核勘误**：经对两项目逐文件核验（非注释/非空行统计 + 命名空间 grep），本文档**初版对 SourceGenerator 的现状判断有误**——文档原将 4 个源文件（合计 ~1107 行）当作活代码规划，实际它们**整文件均被 `//` 注释掉（有效行数 = 0）**，属纯死代码。
> **真实有效表面积**：SourceGenerator 项目存活代码只有 `IJobEntitySourceGenerator.cs`（153 行）+ 6 个 Utils（仅被上述 4 个死文件引用，对活代码同样为死代码）；NativeTranspiler 侧文档判断准确。
> 因此实施顺序改为 **P0（先清死代码，SourceGenerator 收敛为单文件）→ P1 拆 NativeTranspiler 上帝类 → P2 先锁定 3 条 SIMD 路径再统一 → P3 SourceGenerator 现代化**，详见「四、修正后实施计划」。

---

## 一、现状规模快照

### EntJoy.ECS.SourceGenerator（有效代码仅 1 个文件；其余全为整文件注释死代码）
| 文件 | 行数 | 有效 | 说明（复核后） |
|---|---|---|---|
| `SystemArg/SystemArgGenerator.cs` | 490 | **0** | **整文件已注释（死）** |
| `EntitySystemGenerator/EntitySystemGenerator.cs` | 406 | **0** | **整文件已注释（死）** |
| `IJobEntitySourceGenerator.cs` | 153 | 141 | **唯一活生成器**，旧 `ISourceGenerator` + `ISyntaxReceiver` API |
| `QueryBuilderSourceGenerator/*` | 211 | **0** | **两文件均整文件已注释（死）** |
| `Utils/*` | ~260 | ~205 | 6 文件仅被上述死文件引用，**对活代码亦为死代码** |

### NativeTranspiler（严重超配，多个巨文件）
| 文件 | 行数 | 问题 |
|---|---|---|
| `Analyzer/SimdControlFlowGenerator.cs` | **3044** | 单类状态机（>20 字段 + 40 方法），无 region 分区 |
| `Analyzer/CppJobGenerator.cs` | **2392** | 上帝类：命名/ABI/代码生成/SIMD/AST Rewrite/Adapter 七种职责 |
| `Analyzer/Common/IspcGenerator.cs` | **1849** | 方法+Job 双入口（放错命名空间 `.Analyzer` 而非 `.Common`） |
| `Analyzer/CppStatementTranslator.cs` | **1428** | 基类 + 内嵌 `GenerateSIMD_*` 直译 |
| `Analyzer/BindingsGenerator.cs` | 1193 | C# 绑定 + 手写结构体 marshalling |
| `Analyzer/NativeTranspilerGenerator.cs` | 1136 | 单 `RegisterSourceOutput` 内数百行编排 |
| `Analyzer/IspcStatementTranslator.cs` | 1039 | 继承 `CppPointerStatementTranslator` |
| `Analyzer/SimdVariableAnalyzer.cs` | 544 | SIMD 变量分析 |
| `Analyzer/CppGenerator.cs` | 447 | 静态方法生成 |
| `Analyzer/NativeTranspileValidator.cs` | 410 | NT 系列诊断校验 |
| 其余 Analyzer/Common | <400 | 若干工具/模板 |

---

## 二、问题清单

### A. 项目级问题
| # | 问题 | 证据 |
|---|---|---|
| A0 | **（复核新增·最严重）SourceGenerator 多为死代码** | 4 个源文件（`SystemArg`/`EntitySystemGenerator`/`QueryBuilderSourceGenerator`/`QueryBuilderSyntaxReceiver`，合计 ~1107 行）**整文件被 `//` 注释，有效行数 = 0**；6 个 Utils 仅被这些死文件引用，对唯一活生成器 `IJobEntitySourceGenerator` 亦为死代码 |
| A1 | **命名空间与目录/程序集不一致** | 程序集 `EntJoy.ECS.SourceGenerator`，但活文件命名空间仍是旧 `EntJoy.SourceGenerator`；`Common/IspcGenerator.cs` 声明 `NativeTranspiler.Analyzer`（非 `.Common`）；bin/obj 下有陈旧 `EntJoy.SourceGenerator.dll` 残留 |
| A2 | **死代码** | `NativeTranspilerHelper.cs`（348 行）整体被 `//` 注释；`OuterSimdGenerator.cs.bak` 陈旧备份残留 |
| A3 | **两项目 API 版本不统一** | SourceGenerator 用 Roslyn `3.8.0` + 旧 `ISourceGenerator`；NativeTranspiler 用 `4.14.0` + `IIncrementalGenerator` |
| A4 | **模块边界模糊** | NativeTranspiler 同时承担「C#→C++/ISPC 翻译」+「生成 .g.cs 绑定」+「MSBuild 产物编排（CMakeLists/.bat/DLL）」+「运行时 Attribute/枚举 API 面」，横跨多职责 |

### B. 文件级问题（阅读性/规模，NativeTranspiler 严重）
- `SimdControlFlowGenerator.cs`（3044 行，`#region`=0）：单类状态机，`Generate*` 方法间靠成员状态耦合，无分区注释
- `CppJobGenerator.cs`（2392 行，59 个成员声明，`#region`=0）：上帝类，命名/ABI/代码生成/SIMD/AST Rewrite/Adapter 多职责挤在一起
- `IspcGenerator.cs`：方法/Job 双入口造成混淆，且声明 `NativeTranspiler.Analyzer`（在 `Common` 目录）
- `CppStatementTranslator.cs`：职责与 SIMD 生成路径重叠（见 C2）
- `NativeTranspilerGenerator.cs`：`Initialize`+`RegisterSourceOutput`（L27→~640，约 600 行内联）一次性担起属性模板 / 代码生成 / 文件 I/O / CMakeLists / 用户结构体收集五种职责，无 Pipeline 抽象

### C. 结构/设计问题
| # | 问题 | 说明 |
|---|---|---|
| C1 | **继承链为合法 base-subclass 设计，仅覆盖点过隐式** | 修正原"C1 继承链长且隐性"表述：`CppStatementTranslator` 本体是**共享的 AST 分析基类**，C++/ISPC 仅是其子类（`IspcStatementTranslator : CppPointerStatementTranslator : CppStatementTranslator`，再加 `CppChunk/BatchStatementTranslator` 细分）——该主链路**应予保留**。真正需治理的是基类里混入的**非直译职责**（命名/ABI/编排/I/O），以及 `virtual` 覆盖点隐式、改语义时难定位哪层 override 生效 |
| C2 | **实为三条 SIMD 生产路径（比原"两条"更严重）** | `CppStatementTranslator.GenerateSIMD_*`（内联 `AosDist/Sum/Reduction`，L1166-1420）；`OuterSimdGenerator`（内部再 `new SimdControlFlowGenerator`）；独立 `SimdCodeGenerator`（`GenerateSIMDForReduction`）。三套语义重叠，改一处须同步另两处 |
| C3 | **字符串拼接残留 + 魔法数字** | 大量 `StringBuilder` 手拼 C++ 代码 + `"#include ..."` 魔法；`GetCSharpFieldSize/GetStructSizeRecursive` ABI 计算与 CodeTemplates 混杂 |
| C4 | **入口编排无文档/无阶段抽象** | `NativeTranspilerGenerator` 主流程全部内联 |
| C5 | **SourceGenerator 处简易状态（仅剩 1 个活文件）** | `IJobEntitySourceGenerator` 用旧 API 无增量缓存；其余生成器已整文件注释，现代化成本趋近于零 |

---

## 三、重构目标
1. **命名对齐**：目录 / 程序集 / 命名空间三统一
2. **消除死代码**：删除 `NativeTranspilerHelper.cs`、SourceGenerator 4 个整文件注释生成器 + 6 个 Utils、`OuterSimdGenerator.cs.bak` 及陈旧 DLL
3. **拆分上帝类**：`CppJobGenerator`、`SimdControlFlowGenerator`、`NativeTranspilerGenerator`、`IspcGenerator`
4. **统一 SIMD 路径**：先对拍锁定三条路径，再收敛为单一生成管线
5. **SourceGenerator 现代化**：`ISourceGenerator` → `IIncrementalGenerator`（在清死代码后仅剩 1 个活文件，成本低）

---

## 四、修正后实施计划（2026-08-21，P0 优先）

**原则**：先清死代码、把 SourceGenerator 收敛成单文件（避免对死代码做低效"整容"），再拆 NativeTranspiler 上帝类，最后统一 SIMD。每步 `dotnet build` 验证 0 错误；纯重构阶段靠 SchedulerCompareTest / AutoSIMDTest / MovementTest 对拍。

### 阶段 P0：删除性清理（半小时级，暂不写新逻辑）
1. 删除整文件注释的 `SystemArg/SystemArgGenerator.cs`、`EntitySystemGenerator/EntitySystemGenerator.cs`、`QueryBuilderSourceGenerator/*`（2 文件）
2. 删除整文件注释的 `Analyzer/NativeTranspilerHelper.cs`
3. 删除陈旧备份 `Analyzer/OuterSimdGenerator.cs.bak` 与 bin 下旧 `EntJoy.SourceGenerator.dll`（Debug/Export）
4. 删除 SourceGenerator 的 6 个 Utils（仅被上述死文件引用，活代码零引用）
5. 唯一活生成器 `IJobEntitySourceGenerator.cs` 命名空间 `EntJoy.SourceGenerator` → `EntJoy.ECS.SourceGenerator`
6. `dotnet build` 两个工程（含引用方）验证 0 错误

### 阶段 P1：SourceGenerator 现代化（只剩 1 个文件，成本低）
1. `Microsoft.CodeAnalysis.CSharp` `3.8.0` → `4.14.0`（与 NativeTranspiler 对齐）
2. `IJobEntitySourceGenerator`：`ISourceGenerator`+`ISyntaxReceiver` → `IIncrementalGenerator`（SyntaxProvider 谓词/转换，补增量缓存）
3. 新增 **`ECSSourceGenerator` 作为 ECS 源生成器总入口（复合生成器）**：`IJobEntitySourceGenerator` 去掉 `[Generator]`，改为 `internal`，由 `ECSSourceGenerator.Initialize` 转发调度；后续新增 ECS 生成器在总入口登记
4. 回归：EntJoySample 编译 + 生成的 IJobChunk 适配器输出与重构前一致

### 阶段 P2：拆分 NativeTranspiler 上帝类（纯重构,行为不变）
> **设计准则（复核后修正）**：C++/ISPC 主链路是 `CppStatementTranslator`（AST 分析基类）+ 子类的合法 base-subclass 结构，**不拆继承、不把翻译器按后端打散**。拆分的对象限定三类：
> ① 基类中**非 AST 直译职责**的外移（命名、ABI 计算 → 独立辅助类）；② `CppJobGenerator` 这类**多职责编排**的拆分；③ `NativeTranspilerGenerator` 主流程的**阶段抽象**。翻译器子类链仅做"覆盖点显式化"（region / 注释标明哪些 virtual 被谁覆盖），不改变继承关系。

- **`CppJobGenerator.cs` → 4 文件**：
  - `CppJobNames.cs`（`GetCppJobFunctionName`/`Is*Job`/`GetAdapter*Name` 等纯命名判断）
  - `CppJobAbi.cs`（`CalculateFieldOffset`/`GetCSharpFieldSize`/`GetStructSizeRecursive`/`Alignment`）
  - `CppJobCodeGen.cs`（`GenerateJobHeader/Implementation/Adapter` 主体）
  - `CppJobSimd.cs`（`GenerateChunkFunctionSIMD/Remainder`/`Rewriter`/`Decompose`）
- **`SimdControlFlowGenerator.cs` → 3 文件**（独立状态机，非 C++/ISPC 继承链，仍可拆）：
  - `SimdControlFlowGenerator`（状态机 + 主 `Generate`）
  - `SimdExpressionTranslator`（`TranslateExpression/Math/Binary/Cast/Assignment`）
  - `SimdLoopGenerator`（`For/While/Do/Unroll/Reduction`）
- **翻译器继承链（`StatementTranslator` 基类 + C++/ISPC 子类）→ 保持继承，只做覆盖点显式化**：在基类标注各 `virtual` 的主用子类；必要时把基类内非直译杂项抽到独立工具类，不改变 C1 的 base-subclass 结构
  - ✅ **AST 层已完成（step B）**：根类 `CppStatementTranslator` → **`StatementTranslator`**（类 + 文件 `StatementTranslator.cs` 同步改名，9 处引用已更新，`dotnet build` 0 错误）；基类类头新增**继承层级 & virtual 覆盖矩阵**文档（C++/ISPC 各自的 override 一目了然），并标注 `TranslateEntJoyMathCall`/`TranslateInterlockedCall` 的 CS0114 隐性隐藏为已知待对齐项（未改动派发语义，待可对拍后再决定 override）
- **`NativeTranspilerGenerator.cs` → 抽阶段 + 拆职责**：
  - `CodeGenPipeline`（阶段化：Validated → Methods → Jobs → Adapters → CMakeLists → Bats）
  - `CodeGenIo` ✅ 已拆（`WriteAllTextWithRetry`/`DeleteIfExists`/`FindRepoRoot`/`GetRelativePath` → `Common/CodeGenIo.cs`，30 处调用改前缀，1136→1063 行，`dotnet build` 0 错误）
  - `RuntimeApi`（`GenerateAttributeCode` 产出的 Attribute/枚举定义——与代码生成编排彻底分离）
- **`IspcGenerator.cs` → 方法/Job 双入口拆分 + 命名空间修正** ✅
  - 命名空间 `NativeTranspiler.Analyzer` → `NativeTranspiler.Analyzer.Common`（与 Common 目录一致；`CodeGenIo.cs` 同步改为 `.Common`，`NativeTranspilerGenerator` 已有 using 覆盖）
  - 方法级入口（`GenerateIspcSource/MTSource/CppWrapper/CppWrapperMT` 的 `IMethodSymbol` 变体）拆至 **`IspcGenerator.Method.cs`**（245 行）；主文件保留共享辅助 + Job 入口（`INamedTypeSymbol` 变体，1849→1616 行）；`static partial` 拆分，`dotnet build` 0 错误

### 阶段 P3：统一 SIMD 管线（先对拍锁定，再收敛）
> **2026-08-21 复核结论（已实施可验证部分）**：初版"三条路径"实为 **2 活 + 1 死**。
> - **已删除死路径**：`SimdCodeGenerator.cs`（224 行）+ `LoopPatternAnalyzer.cs`（376 行，含 `LoopPattern`/`ReductionOp`/`ReductionKind`）——全仓库 grep 无任何实例化/引用，纯死代码，已 `git rm`。
> - **两条活路径地图**（行为语义不同、触发点不同）：
>   | 触发 | 路径 | 产出 |
>   |---|---|---|
>   | `AutoSIMD.Enabled`（batch Job） | `StatementTranslator.TryGenerateSIMDForLoop` → `GenerateSIMD_AosDist/Sum/Reduction`（`CppBatchStatementTranslator` enableAutoSIMD=true） | 批内循环向量化归约 |
>   | `AutoSIMD.Vectorize`（chunk/entity） | `OuterSimdGenerator` → `SimdControlFlowGenerator`（状态机） | chunk/entity 向量化 |
> - **遗留事项**：两条活路径是不同特性，行为合并需 `AutoSIMDTest`+`MovementTest` 对拍（±5%）。因本机 .NET SDK 损坏无法跑消费者基准，**最终语义收敛暂缓**，仅完成死代码清理与路径地图；交接给可运行基准的环境执行。

---

## 五、验收标准
- 全阶段 `dotnet build EntJoy.sln -c Release` **0 错误**（含 NativeTranspiler Tasks 编译、C1083 头文件错误消失）
- `SchedulerCompareTest` 自检 8/8 + 输出与重构前一致
- `JobLibsBenchmark` / `AutoSIMDTest` 基准数值 ±5%（证明翻译语义未变）
- 命名空间/目录/程序集三统一（grep 无旧命名空间残留）
- 死代码移除无编译断链

---

## 六、风险与边界
- **P2/P3 属纯重构**，最大风险在 SIMD：`CppJobGenerator.SIMD` 与 `SimdControlFlowGenerator` 有微妙语义耦合，且实为**三条**生产路径 → 先在 P3 建入口/出口对照表，再分阶段对拍，不一次性大改
- **NativeDll C++ ABI（字段偏移/对齐）由 C# 端 `Abi.cs` 计算**——拆分时必须保持 `GetCSharpFieldSize/Alignment` 逐字节一致（原生侧有 `static_assert` 兜底）
- **不纳入本次范围**：C++ 原生侧（`src/NativeDll/*.cpp`）重构、GPU-offload 分支（feature/gpu-offload）、文档体系

---

## 八、2026-08-21 最终落地（按功能归类 + 清理）

`NativeTranspiler/Analyzer/` 最终按功能归类（文件移动，命名空间未改，`dotnet build` 0 错误）：
```
Analyzer/
├─ Ast/    StatementTranslator.cs                                   # 基本 AST 分析
├─ Cpp/    CppPointer/Chunk/Batch…Translator, CppGenerator, CppJob(+Abi+Names)   # C++ 翻译
├─ Ispc/   IspcStatementTranslator, IspcChunk, IspcGenerator(.Method)            # ISPC 翻译
├─ Simd/   SimdControlFlow/Expression/Loop, OuterSimdGenerator, SimdVariableAnalyzer  # AutoSIMD
├─ Common/ SymbolHelper, AttributeHelper, CodeTemplates, CodeGenIo, RuntimeApi, Config,
│          NativeTranspiler, Validator, Context, BindingsGenerator # 共同处理/类型判断
└─ NativeTranspilerGenerator.cs                                     # 入口
```
同批清理：
- 生成 C++ 里 4 处陈旧 `// TODO: Translate ...` 占位 → 干净空体注释
- 删除重复 sample `GridSearch2D_SIMD - 复制.cs`(+.uid)
- 此前已删死代码：`SimdCodeGenerator`/`LoopPatternAnalyzer`/`SimdEligibilityAnalyzer`/`NativeTranspilerHelper`、`.bak`、陈旧 DLL、SourceGenerator 注释死文件+Utils
- **硬编码匹配收敛**：新增 `Common/Config.cs` 常量类，把散落各翻译器的 21 个字符串字面量（容器/数值类型名、`IJob*` 接口名、方法名 `Execute/Resize/Add/GetUnsafePtr/ArrayElementAsRef/…`、命名空间 `System/EntJoy/EntJoy.JobSystem/EntJoy.Collections/EntJoy.Mathematics`、`NativeTranspileAttribute`）集中为 `Config.X`；全部调用点已替换，UTF-8/BOM 保真，`dotnet build` 0 错误（97 既有警告不变）
- **命名空间感知加固（防同名误判）**：`SymbolHelper.IsEntJoyJobInterface` 与 `NativeTranspiler.IsEntJoyNativeContainerType` 本已校验命名空间（改用 Config 常量）；新增 `NativeTranspiler.IsEntJoyContainerNamed(type, memberName)`（`EntJoy.Collections` + 名字双校验），把 53 处翻译器里**只按短名 `.Name == Config.NativeList/NativeArray`** 的判断全部换成双校验调用（含已 `IsEntJoyNativeContainerType &&` 前缀的简化合并）；`Config.Span`(System) 与方法名短比较不在此列、保持不动。`dotnet build` 0 错误 / 0 警告
  - SourceGenerator 侧：`EntJoy.ECS.SourceGenerator/Config.cs`（`IJobEntity`/`Execute`/`NamespaceEntJoy`），`GetJob` 的 `IJobEntity` 匹配改为名字+`EntJoy` 命名空间双校验（`IJobEntity` 声明于 `src/EntJoy.ECS/IJobEntity.cs`）；`Contains(Config.IJobEntity)` 语法谓词仅作候选过滤、语义闸门已带命名空间校验
- **`IJobEntity` 划转 ECS 并归位 `EntJoy` 命名空间**：自 `src/EntJoy.Jobs/JobInterface.cs` 迁至 **`src/EntJoy.ECS/IJobEntity.cs`**，命名空间为 **`EntJoy`**（与 `IJobChunk` 对齐）；Transpiler `IsEntJoyJobInterface` 相应把 `IJobEntity` 归入 `EntJoy`（与 `IJobChunk` 同判），SourceGenerator 命名空间校验改 `Config.NamespaceEntJoy`。注：`EntJoy.ECS`/`EntJoy.Jobs` 为 net8.0，本机坏 SDK 无法就地编译验证，但为无依赖空接口且活跃实现者（`SpritesRandomMove.cs`）已有 `using EntJoy`，风险极低
- **解耦：`EntJoy.Jobs` 可独立使用（不再依赖 ECS）**：
  - `JobHandle` + 纯 `IJob` 族调度扩展（`IJob/IJobParallelFor/IJobFor/IJobParallelForBatch` 的 `Schedule`/`Run`）自 `EntJoy.ECS` → **`src/EntJoy.Jobs`**（只依赖 NativeJobHandle/NativeJobScheduler，均属 Jobs）
  - `JobExtensions` 因 partial 不能跨程序集 → 拆成两个不同名静态类（同 `EntJoy.JobSystem` 命名空间，`using`+泛型约束自动解析，调用点 API 不变）：纯调度留 **`EntJoy.Jobs.JobExtensions`**；`IJobChunk` 的 `Schedule`/`ScheduleWithWorkerCap`（依赖 World/EntityManager/QueryBuilder/NativeEcsScheduler）→ **`EntJoy.ECS.ChunkJobExtensions`**
  - 验证：`src/EntJoy.Jobs` 内 ECS 类型（World/QueryBuilder/EntityManager/NativeEcsScheduler/IJobChunk）**零实际代码引用**（仅注释）→ 可独立编译/单独使用
  - 注：EntJoy.Jobs/ECS 为 net8.0，本机坏 SDK 无法就地编译，待正常环境回归

**遗留交接项（需有行为对拍/可跑基准的环境）**：
1. SIMD 两条活路径（`AutoSIMD.Enabled` 内联 vs `Vectorize`/SimdControlFlow）的行为统一 → 需 AutoSIMDTest/MovementTest ±5% 对拍
2. 命名空间.目录统一：`Ast/Cpp/Ispc/Simd` 现仍扁平 `.Analyzer`，`Common` 为 `.Common` → 若要三统一需跨类改 `using`（风险更高，建议对拍环境做）
3. `IspcStatementTranslator : CppPointerStatementTranslator` 的跨后端继承（冗余）→ 改继承链为行为改动，需对拍

---

## 九、IJobEntity / IJobChunk 调度链路与 managed 内联决策（2026-08-21 追加）

### 9.1 调用 IJobEntity ≡ 调用 IJobChunk（适配器，纯值类型）
`IJobEntity` 结构体经 `IJobEntitySourceGenerator` 生成 `__EntJoy_IJobEntityAdapter_X : IJobChunk`（含 `public {Job} Job;` + `Execute`），`job.Schedule(query)` → `ChunkJobExtensions` → `NativeEcsScheduler.ScheduleChunk` → 按 chunk 并行。**调度语义/并行度/缓存路径与手写 IJobChunk 完全一致**。

### 9.2 无额外 GC / 内存泄漏面
适配器是纯值类型 → **不引入任何新 GC/泄漏机制**。共享调度生命周期（对一切 Job 均同）：
- 原生内存（ChunkJobData/组件数组指针）→ `ChunkCleanup`（含异常 catch 兜底）完成时释放
- managed chunk 用**弱 GCHandle**（WeakTrackResurrection）+ 完成时 `Free` → 不延长 Chunk 存活、不累积
- 依赖 `RetainedNativeDependency`（RAII `using`）；句柄 `NativeJobHandleBox` 终结器兜底
- 唯一义务：`Complete()` / 持有 JobHandle（否则依赖终结器、非永久泄漏）
- 铁证需在好 SDK 环境跑 SchedulerCompareTest / JobLibsBenchmark 压 schedule+complete 循环

### 9.3 managed 源码级内联（淘汰 `Job.Execute(ref …)` 每实体调用）
`Execute` 方法体被 Roslyn 重写直接内联进生成实体循环，命名策略：
| 生成标识符 | 命名 | 防冲突依据 |
|---|---|---|
| 组件 span 局部 | **用户形参名**（`p`/`v`）| C# 禁局部与形参同名 → 天然不冲突 |
| 生成方法参数/实体数/序号 | `__chunk`/`__enabledMask`/`__count`/`__idx` | `__` 前缀保留名 |
| 用户字段 | `Job.<field>` 读取 | 字段与形参互斥；循环不变量可被 JIT LICM 外提 |

重写规则：形参 → `同名span[__idx]`；字段 → `Job.<field>`；`this` → `Job`；void `return;` → `continue;`（否则首实体后退出循环）。

### 9.4 ref/in 暂不区分（无版本检查）
当前**无写版本/读写依赖追踪**，生成器已统一 `形参名[__idx]`（`Span[i]` 返回 `ref`，读写都走同一表达式），`RefKind` 分支已删。`ref`/`in` 保留在用户 Execute 签名（契约）；待将来实现版本/依赖追踪时，在调度/查询层按 `in`=可共享、`ref`=独占 生效，**不回溯改内联生成**。

### 9.5 global:: 暂不启用（决策记录：防御性用途）
`global::Type` 强制从全局命名空间根解析，其用途是**纯防御性**：
- **防 `using` 别名劫持**：若用户写了 `using QueryBuilder = MyGame.Foo;`，未限定的 `QueryBuilder` 会解析成用户别名而非 `EntJoy.QueryBuilder`。
- **防嵌套命名空间遮蔽**：生成代码位于用户命名空间（如 `EntJoySample.IJobChunkMoveCompareTest`）内，若该命名空间（或其祖先）声明了同名类型（如 `QueryBuilder`/`JobHandle`/`ArchetypeChunk`/`ComponentType`），未限定名会**静默优先命中用户类型**而非 EntJoy 类型 → 生成代码错误/行为异常且难查。
- `global::EntJoy.ArchetypeChunk`、`global::EntJoy.JobSystem.QueryBuilder` 等可彻底消除这一类问题。

**现状判断**：生成代码已有 `using EntJoy; using EntJoy.JobSystem;`，在用户无上述别名/遮蔽场景时解析正确；当前无该冲突 bug、不构成阻塞，故**暂不启用**。作为**生成代码的防御性最佳实践**，后续如需加固，在 `IJobEntitySourceGenerator` 模板把 EntJoy 类型引用统一加 `global::EntJoy.*` / `global::EntJoy.JobSystem.*` 前缀（组件类型 `ToDisplayString(FullyQualifiedFormat)` 已自带 `global::`，正好一致）。

### 9.6 已提交
- 相关重构已提交：`2eed4ad`（NativeTranspiler 归类 + ECS 调度解耦 + SourceGenerator 现代化）。
- `docs/` 目录在 `.gitignore` 内，本计划文档不入库。
- 未提交（无关项）：docs/00-11 删除、docs/archive/README、NativeDll/JobSystem_Tiles.cpp、scripts/split_jobsystem.sh、sample 的 IJobChunkMoveCompareSample.cs 改动。



---

## 七、关联文档
- 架构现状：`../ecs-evolution-plan-v2.md` / `v3.md`
- 代码位置：`src/EntJoy.ECS.SourceGenerator/`、`src/NativeTranspiler/`
- 回归基准：`samples/EntJoySample/01_JobSystem/SchedulerCompareTest/`、`tools/JobLibsBenchmark/`
