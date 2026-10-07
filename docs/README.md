# EntJoy 文档索引

本仓文档分四类。**看文档先看这张表**：`gridsearch/` 是当前有效结论，`public/` 是对外契约，
`archive/` 与 `research/` 是**历史**（保留备查，不要当作现状）。

## 对外契约（`public/`）—— 使用者应读

| 文档 | 回答什么 |
|---|---|
| [Native-Jobs-Guide.md](public/Native-Jobs-Guide.md) | 怎么用这套 Job/ECS：API 入口与用法 |
| [Runtime-Contracts-and-Known-Limitations.md](public/Runtime-Contracts-and-Known-Limitations.md) | 运行时保证与**已知限制**（含不能做什么） |
| [NativeTranspiler-Boundaries-and-Diagnostics.md](public/NativeTranspiler-Boundaries-and-Diagnostics.md) | 转译器支持/不支持的 C# 子集、诊断开关 |
| [NativeArray-Index-Safety-Overhead-and-Fixes.md](public/NativeArray-Index-Safety-Overhead-and-Fixes.md) | NativeArray 索引安全检查的开销与修法 |
| [Gates-and-Flags.md](public/Gates-and-Flags.md) | **所有门控与环境变量的契约**：默认值、语义、能否关、证据在哪。⚠ 2026-10-07 补登 3 个：`ENTJOY_JOB_COST_CACHE`、`ENTJOY_JOB_BATCH_BY_NAME`（"对齐档"的定义开关）与构建期 `ENTJOY_MSVC_EXTRA_FLAGS`；并加了"**主线程 assist 不在框架 env 面、却影响所有跨栈读数**"的说明（见 [doc17](gridsearch/17-独立复核-HEAD两档判据与Layer核验.md) §2.3） |

## 当前结论（`gridsearch/`）—— 改代码前应读

| 文档 | 回答什么 | 状态 |
|---|---|---|
| [17-独立复核-HEAD两档判据与Layer核验.md](gridsearch/17-独立复核-HEAD两档判据与Layer核验.md) | **独立复核（2026-10-07）：最新数据 + 逐层核验**。不引用旧账本，在 HEAD（`NativeTranspiled.dll` md5 `FC4C19909A`、132,096 B）上重测两档：**对齐档整步 1.021（6/6）、默认档 1.016（4/6）、对齐档+`ENTJOY_ASSIST=0` 1.024（6/6）**；分段 Build 0.83 / Flow 0.91 / Melee 1.07 / MarkDead 0.76 / Integrate 0.83（对齐档）。**四条更正**：① §45.6 的"判据 0.976/0.989"出自白名单未删的树 —— HEAD 热内核**全部按引用**（生成码普查 `byValue=17 / byRef=178`）⇒ §43 的 Build +21.5% 不在树里；② §44.19 的 1.069 不等于 HEAD；③ `ENTJOY_ASSIST` 的"协议口径=0"与脚本实际不符（游戏默认开；老脚本 pin 0、新战役脚本回落到开）；④ **Integrate 段不可比**（Unity 侧源码与运行日志双自证 + 输入每步清零）⇒ §42/§44.8 对它的 codegen/访存归因**撤回**。另：复现 Unity 调度 floor（354/54.8/34.7 µs、0.304 ns/batch）与 cs=1 每项 0.230 ns；**§43.3 的 hoist 税不复现**（+1.3%、2/3，且该消融变体结构上无效） | **活跃，最新复核** |
| [16-对齐档追平Unity-战役计划与起点.md](gridsearch/16-对齐档追平Unity-战役计划与起点.md) | **优化战役的主账本**：固化两侧账本已确证的常数、**已排除的落点（别重做）**、F1–F5 方向与实施顺序，按轮次记账至 **§43（Round 33）**。**当前状态**：§43 证实"每元素固定开销差"的机制（MSVC 把内联 `_InterlockedIncrement` 当全内存屏障 ⇒ 环内循环不变量每元素重载），并发现修法（标量值绑定白名单）**早已实现却只能由 env 打开**；已落成**默认开启** ⇒ **Build 的 B/A 0.766 → 0.999**。剩余靶子：**Flow 的 `FlowBfsWaveJobDual`（+2.3 ms）+ `FlowGradJob`（+0.86 ms）**、Integrate（+0.35 ms） | **活跃**；⚠ 见 [doc17](gridsearch/17-独立复核-HEAD两档判据与Layer核验.md)：本行"白名单已落成默认开启 ⇒ Build 0.766→0.999"**在 HEAD 已失效**（白名单已整体删除，热内核按引用，Build 实测 0.83） |
| [15-Build与Integrate逐趟定位-器械缺陷与对齐档实测.md](gridsearch/15-Build与Integrate逐趟定位-器械缺陷与对齐档实测.md) | **逐趟定位结果 + 真对齐档**：① 赤字落在 `prefixFinal`/`zero` 两个短趟；② 旧的 `tpw=2000` "对齐档"只有 ~70% 真在 Unity 粒度上；③ **真对齐（关 JCC + batch 逐调用点一致，靠 `ENTJOY_JOB_BATCH_TABLE`）后 Build/Integrate 赤字反而更大**（Build B/A 0.52–0.66）⇒ **不是粒度问题**；④ 量出 **A 每工作项 13–52 ns vs Unity 0.335 ns/batch（受控空体）**；⑤ 否证"tile 数 < worker 数"这一根因；⑥ Unity 侧 `innerloopBatchCount` 逐点表 + 调度 floor；⑦ 两处会污染下一步的器械缺陷 | **活跃，诊断结论（含一条撤回）**；⚠ 三条使用前提已由 [doc17](gridsearch/17-独立复核-HEAD两档判据与Layer核验.md) 更新：① Unity 侧 floor 已复现（354/54.8/34.7 µs、0.304 ns/batch）但 E 侧 13–52 ns/tile 未重测；② 本档的 **H12"每工作项成本"已被 doc16 §38 关闭**（引用时必须同时引用）；③ §4.5 的跨栈比值里 **Integrate 不可比**；④ 所有 `[M-1]` 读数现场 **assist 是开着的** |
| [14-接续-Build与Integrate为何输给Unity.md](gridsearch/14-接续-Build与Integrate为何输给Unity.md) | **研究简报：为什么 Unity 赢 Build+Integrate**（数据 + 两侧已对齐的 Build 6 趟器械 + 假设/判决实验 + "不要重做"的历史结论） | **活跃**；⚠ 其 §3.1「先开 `CPUBATTLE_DIAG_BUILDPASS=1`」与 §3.3 B 侧 `M4,build,*` 已被 **doc15 §2** 证伪为带污染，读数前先读 doc15；⚠ 其 §1 的默认档表已被 [doc17](gridsearch/17-独立复核-HEAD两档判据与Layer核验.md) 重测（整步 1.016、**Build 0.900 → 0.829**），且该表的 **Integrate 列不对等**（Unity 侧自证该段不可比） |
| [13-门控清单-缺陷清单与清理计划.md](gridsearch/13-门控清单-缺陷清单与清理计划.md) | **门控全清单、缺陷清单、清理计划与执行记录**（含历次"原判错误"更正） | 活跃，清理campaign 的主账本 |
| [12-Unity版本号机制源码核实与代次句柄设计.md](gridsearch/12-Unity版本号机制源码核实与代次句柄设计.md) | Unity 版本号/代次机制源码核实；§6.12 的"廉价杠杆已穷尽"台账 | 活跃 |
| [11-Unity-JobSystem设计对照与own-batch结论.md](gridsearch/11-Unity-JobSystem设计对照与own-batch结论.md) | Unity JobSystem 设计对照；own-batch 结论 | 活跃 |
| [10-三轴终局对比-默认档对齐档与Job调度.md](gridsearch/10-三轴终局对比-默认档对齐档与Job调度.md) | 三轴（默认档/对齐档/Job 调度）终局对比 | 活跃 |
| [09-count几何根因与统一调度.md](gridsearch/09-count几何根因与统一调度.md) | count 几何根因与统一调度；**§49 唤醒、§58 薄批门、§59 未定项** | 活跃（近 4000 行，结论表在文首） |
| [08-逐Job档表与Unity-batch0语义实测.md](gridsearch/08-逐Job档表与Unity-batch0语义实测.md) | 逐 job 档表与 Unity `batchSize=0` 语义实测 | 活跃 |
| [08b-交接-Build逐趟与count对齐.md](gridsearch/08b-交接-Build逐趟与count对齐.md) | 交接：Build 逐趟与 count 对齐 | 活跃 |
| [07-框架完善与托管-vs-原生对比.md](gridsearch/07-框架完善与托管-vs-原生对比.md) | 托管 vs 原生逐项对比（最大的实验记录，461 KB） | 活跃但冗长 |
| [07b-同粒度实测数据与派生表.md](gridsearch/07b-同粒度实测数据与派生表.md) | 同粒度实测数据与派生表 | 活跃 |
| [06-Unity-Burst对比实测.md](gridsearch/06-Unity-Burst对比实测.md) | 与 Unity Burst 的对比实测 | 活跃 |
| [05-托管开销与GCHandle分析及内存局部性.md](gridsearch/05-托管开销与GCHandle分析及内存局部性.md) | 托管开销、GCHandle、内存局部性 | 活跃 |
| [04-基准测量方法论与调度开销分析.md](gridsearch/04-基准测量方法论与调度开销分析.md) | **测量方法论**（怎么测才不算自欺：min-of-windows、配对、相位对齐） | 活跃，方法论必读 |
| [03-NativeAdapter-Query开销分析与调度优化.md](gridsearch/03-NativeAdapter-Query开销分析与调度优化.md) | NativeAdapter query 开销与调度优化 | 活跃 |
| [01-NativeAllocator-实现说明.md](gridsearch/01-NativeAllocator-实现说明.md) | NativeAllocator 实现说明 | 活跃 |

## 计划类（`docs/` 根目录）

| 文档 | 回答什么 |
|---|---|
| [Phase优先级分析与实施路线.md](Phase优先级分析与实施路线.md) | 阶段优先级与实施路线（最大）。§24 是最早的热重载规划（当时的结论"未开工"**已过期**）：**2026-10-07 已落地**——`NativeHotReloadWatcher`（轮询 + 内容寻址副本 `.auto<sha8>`）+ `NativeReloadResult` + 布局指纹守卫，宿主在安全点换模块；**不用 AssemblyLoadContext**（那是当时被低估的阻塞点的绕法），改用 `GetExport` 取代被缓存的 `DllImport`。可跑样例与三条命令见 [samples/EntJoySample/README.md §13_HotReload](../samples/EntJoySample/README.md)；契约见 [Gates-and-Flags.md](public/Gates-and-Flags.md) |
| [ecs-evolution-plan-v2.md](ecs-evolution-plan-v2.md) | ECS 演进计划 v2。⚠ 2026-10-07 加了**执行状态表**（逐 Phase 对照 `src/`+`tests/`）：Phase 1–8 基本已落地；**真正的未做只剩 Phase 9**（`S36 ManagedComponentStore` / `S37 NativeProjection`，3–5 周）**与基准/验证方案**（`06_ArchBenchmark` 不存在）。**已决策不做/暂缓的别当待办**：4.3≡5.2 One-Frame（→ Event Channel 替代）、5.5 DI（暂缓）。⚠ 本表第一版曾把 2.3 Chunk lazy zero / 3.4 BatchOperations 误判为未做（实为 ✅ S17 / ✅ S12）—— 权威台账是 [Phase优先级分析与实施路线.md](Phase优先级分析与实施路线.md) |
| [JobSystem下一步优化任务清单.md](JobSystem下一步优化任务清单.md) | JobSystem 下一步任务清单 |
| [v1.0.0-发布前并发与内存审查.md](v1.0.0-发布前并发与内存审查.md) | v1.0.0 发布前并发/内存审查 |
| [NuGet打包规划.md](NuGet打包规划.md) | NuGet 打包规划 |
| [优化Jobsystem.md](优化Jobsystem.md)、[Unity风格JobSystem架构优化方案B.md](Unity风格JobSystem架构优化方案B.md) | 早期方案稿（结论已被后续实测取代） |
| [07-EntityRandomAccess优化计划.md](07-EntityRandomAccess优化计划.md)、[hotfield-EntityRandomAccess优化计划.md](hotfield-EntityRandomAccess优化计划.md) | EntityRandomAccess/HotField 优化计划（同内容两份） |
| [EntityBatch-AutoSIMD-重构方案.md](EntityBatch-AutoSIMD-重构方案.md)、[NativeAllocator-Unity对齐分析与计划.md](NativeAllocator-Unity对齐分析与计划.md) | 单项重构方案 |
| [handoff-ABC-and-open-items.md](handoff-ABC-and-open-items.md) | 交接：A/B/C 与未决项（208 KB） |

## 历史（**不要引用为现状**）

- [`archive/`](archive/README.md) —— 2026-07 ~ 2026-09 的设计/实测记录，含 `archive/ecs/`、
  `archive/jobsystem/`、`archive/simd/`、`archive/performance/`、`archive/hotfield/`、
  `archive/gpu-offload/`、`archive/superpowers/`。
  其中 [archive/20260916-从代码注释移出的历史修改思路.md](archive/20260916-从代码注释移出的历史修改思路.md)
  是**上一轮**把代码注释里的历史搬出的产物（本轮起改为**直接删除**，见 doc13 §5.6）。
- `research/` —— 外部资料抓取（Bevy 源码片段、deepwiki HTML），仅供查证，非本仓设计依据。

## 维护约定

1. **结论进 `gridsearch/`，契约进 `public/`，历史进 `archive/`**；代码注释只描述**现状**与
   **不变量**，不写日期/实验臂/实测数字（判据见 doc13 §5.6）。
2. 每次"原判错误"的更正**追加**在本账本（doc13 §5），**不改写**历史段落。
3. 新增文档请在本表登记一行（一句话说明它回答什么问题）。
