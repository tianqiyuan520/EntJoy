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
| [Gates-and-Flags.md](public/Gates-and-Flags.md) | **所有门控与环境变量的契约**：默认值、语义、能否关、证据在哪 |

## 当前结论（`gridsearch/`）—— 改代码前应读

| 文档 | 回答什么 | 状态 |
|---|---|---|
| [14-接续-Build与Integrate为何输给Unity.md](gridsearch/14-接续-Build与Integrate为何输给Unity.md) | **研究简报：为什么 Unity 赢 Build+Integrate**（数据 + 两侧已对齐的 Build 6 趟器械 + 假设/判决实验 + "不要重做"的历史结论） | **活跃，Build/Integrate 课题从这里开始** |
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
| [Phase优先级分析与实施路线.md](Phase优先级分析与实施路线.md) | 阶段优先级与实施路线（最大） |
| [ecs-evolution-plan-v2.md](ecs-evolution-plan-v2.md) | ECS 演进计划 v2 |
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
