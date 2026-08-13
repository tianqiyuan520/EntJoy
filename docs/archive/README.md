# docs/archive — 历史快照与已完成工作记录

> 归档日期：2026-08-12。这些文档记录**已完成或被取代**的工作，不再反映当前代码状态。
> **当前状态请以 docs/ 的 01–05 系列为准**（位于 [gridsearch/](../gridsearch/)：[01-NativeAllocator-实现说明](../gridsearch/01-NativeAllocator-实现说明.md)、
> [02-NativeAllocator-Unity对齐分析与计划](../gridsearch/02-NativeAllocator-Unity对齐分析与计划.md)、
> [03-NativeAdapter-Query开销分析与调度优化](../gridsearch/03-NativeAdapter-Query开销分析与调度优化.md)、
> [04-基准测量方法论与调度开销分析](../gridsearch/04-基准测量方法论与调度开销分析.md)、
> [05-托管开销与GCHandle分析及内存局部性](../gridsearch/05-托管开销与GCHandle分析及内存局部性.md)）。
> 前瞻规划见 [../ecs-evolution-plan-v2.md](../ecs-evolution-plan-v2.md)。

## 目录

| 子目录 | 内容 | 阶段 |
|---|---|---|
| [simd/](simd/) | Auto-SIMD 引擎设计与分析：`simd-architecture`、`simd-auto-*`、`auto-simd-*`、`fullwidth-simd-attempt`、`ispc-vs-cpp-simd-analysis`，附 3 个生成物快照（`.ll` / `.ispc` / 生成的 `.cpp`） | 已实现；引擎仍在 NativeTranspiler，基准套件 AutoSIMDTest 待集成 |
| [performance/](performance/) | 各 benchmark 结果快照（2026-07-20~28）：ISPC / Auto-SIMD / IJobEntity / cooperative chunk executor / job assist | 已结束，结果已并入 gridsearch/ 的 03/04/05 |
| [superpowers/](superpowers/) | Unified Job Assist、Cooperative Chunk Executor、Taskflow 尾延迟优化的计划与规格（2026-07-12~18） | 已完成 |
| [jobsystem/](jobsystem/) | JobSystem 早期稳定性审查、架构方案 B、优化任务清单、DRAM 温度瓶颈分析 | 已完成/已取代 |

> 原 `docs/JobSystem环境变量说明.md` 已删除，env 变量全清单见
> [../gridsearch/04-基准测量方法论与调度开销分析.md](../gridsearch/04-基准测量方法论与调度开销分析.md) §1.4（8 个，含 `ENTJOY_GUIDED_*`、`ENTJOY_DIAG_TIMING` 等新增项）。
> 原 `docs/performance/next-optimization-plan.md` 已删除（任务已被后续 commit 完成）。
