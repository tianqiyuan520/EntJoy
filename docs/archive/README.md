# docs/archive — 历史快照与已完成工作记录

> 归档日期：2026-08-12（首轮）；**2026-08-22（追加轮：20260820~0821 会话产物 + 两份 AI 聊天记录）**。
> 这些文档记录**已完成或被取代**的工作，不再反映当前代码状态。
> **当前状态请以 docs/ 根目录的正式文档为准**：`00-项目布局与EntJoySample设计意图.md`、`ecs-evolution-plan-v2.md`、`ecs-evolution-plan-v3.md`、`20260822-设计决策记录-AI聊天讨论沉淀.md`（AI 聊天结论的正式沉淀，聊天原文在本目录）。

## 目录

| 子目录 | 内容 | 阶段 |
|---|---|---|
| [simd/](simd/) | Auto-SIMD 引擎设计与分析：`simd-architecture`、`simd-auto-*`、`auto-simd-*`、`fullwidth-simd-attempt`、`ispc-vs-cpp-simd-analysis`，附 3 个生成物快照（`.ll` / `.ispc` / 生成的 `.cpp`） | 已实现；引擎仍在 NativeTranspiler，基准套件 AutoSIMDTest 待集成 |
| [performance/](performance/) | 各 benchmark 结果快照（2026-07-20~28）：ISPC / Auto-SIMD / IJobEntity / cooperative chunk executor / job assist | 已结束，结果已并入 gridsearch/ 的 03/04/05 |
| [superpowers/](superpowers/) | Unified Job Assist、Cooperative Chunk Executor、Taskflow 尾延迟优化的计划与规格（2026-07-12~18） | 已完成 |
| [jobsystem/](jobsystem/) | JobSystem 早期稳定性审查、架构方案 B、优化任务清单、DRAM 温度瓶颈分析 | 已完成/已取代 |
| [layout/](layout/) | 项目布局与模块拆分（`00-项目布局`、`20260820-模块拆分与布局重构`） | 已实现（src/ 纯库 + samples/ 布局已生效） |

### 2026-08-22 追加归档（20260820~0821 会话产物，全部已实现/已收敛）

| 文件 | 内容 | 结论 |
|---|---|---|
| `jobsystem/20260820-ManagedJobSystem依赖链死锁-问题记录.md` | Managed 依赖链 ABA 丢回调 + lost-wakeup 死锁根治 | ✅ 已修复（`2cf0e56` + 后续；代际守卫 / 锁内条件变量 / 周期协助） |
| `layout/20260820-csharp-模块拆分与布局重构-状态记录.md` | NativeJobCore 抽取、目录重构、SourceGenerator 修复 | ✅ 已落地（遗留：未提交改动、ECS Phase B） |
| `simd/20260820-NativeTranspiler与SourceGenerator重构计划.md` | SourceGenerator 现代化 + NativeTranspiler 拆上帝类 + SIMD 路径收敛 | ✅ P0/P1/P2 与 §9 已实现；遗留交接项见 §四（需对拍环境） |
| `jobsystem/20260821-S5高竞争性能差距分析.md` | S5 高竞争追平 Misaki/TPool + transpile 3-10x 大杠杆 | ✅ 已收敛（Relaxed TrySteal / 亲和默认开 / 实体数衡 tile） |
| `jobsystem/20260821-Chase-Lev-Deque-重构计划.md` | Chase-Lev 重构计划（被实施历程取代） | ⏸ 计划文档，实施见下 |
| `jobsystem/20260821-MPMC-vs-ChaseLev-分析.md` | 调度模型对比分析 | 📘 分析结论已采纳（work stealing 为默认路径） |
| `jobsystem/20260821-Chase-Lev-实施历程与问题分析.md` | Chase-Lev 实施 + 12 项死锁/崩溃根因修复 + 性能 A/B | ✅ 已收敛（`PopBottom` SeqCst fence 终局修复；默认 `ENTJOY_USE_WORKSTEALING=1`） |
| `jobsystem/Jobsystem聊天分析.md` | JobSystem 生产就绪审计（AI 对话原文） | 📘 结论有效；3 处已被后续演进更新（见 `../20260822-设计决策记录-AI聊天讨论沉淀.md` §九） |
| `simd/EntJoy20260820-ai聊天.md` | NativeTranspiler 后端选择 / ECS 设计取舍（AI 对话原文） | 📘 结论已沉淀为 `../20260822-设计决策记录-AI聊天讨论沉淀.md` |

> 原 `docs/JobSystem环境变量说明.md` 已删除，env 变量全清单见
> [../gridsearch/04-基准测量方法论与调度开销分析.md](../gridsearch/04-基准测量方法论与调度开销分析.md) §1.4（8 个，含 `ENTJOY_GUIDED_*`、`ENTJOY_DIAG_TIMING` 等新增项）。
> 原 `docs/performance/next-optimization-plan.md` 已删除（任务已被后续 commit 完成）。