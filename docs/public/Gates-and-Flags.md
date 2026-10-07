# 门控与环境变量契约（Gates and Flags）

本文是**对外契约**：列出所有运行期环境变量（原生 + 托管）与构建期开关，说明**默认值、语义、
能否关闭、以及证据在哪**。改动任何一个默认值都属于**行为变更**，必须走等价性断言
（原生 20 跑 + 转译器单测 + 夹具全断言 + 默认档发射产物逐字节比对）。

> 完整清单、逐条证据与历次"原判错误"的更正见
> [gridsearch/13-门控清单-缺陷清单与清理计划.md](../gridsearch/13-门控清单-缺陷清单与清理计划.md)。
> 清理后当前运行期 env 共 **43 个** = 原 41 个 + **2026-10-07 补登的 2 个**
> （`ENTJOY_JOB_COST_CACHE`、`ENTJOY_JOB_BATCH_BY_NAME`，见 D 节 —— 它们是"对齐档"的定义开关，
> 此前只散落在 gridsearch 的战役文档里，未进契约表）。

## 约定

- 解析时机：原生侧 `getenv` 一律在**首次调用时锁存**（函数内 `static`）⇒ 进程启动后改 env 无效。
- 构建期开关**不会因 env 变化触发重新生成**：改它必须 `dotnet build-server shutdown` + `-t:Rebuild`。
- **默认开的功能一律保留其 `=0` 关闭口**（那是验收与回退的入口，不是"无意义功能"）。

---

## A. 默认开启的性能功能（`=0` 关闭）

这些是**已验收**的优化，默认档即开启；`=0` 用于 A/B 与回退。**不要删它们的关闭口。**

| env | 默认 | 语义 | 作用域 |
|---|---|---|---|
| `ENTJOY_TILES_UNIFORM` | 开 | 等宽 GeneralRange **不物化** `tileBuffer`（算术推导 tile 边界） | 非 guided 的 General 路 |
| `ENTJOY_TILE_FASTPATH` | 开 | 把每-tile 的固定开销提到每批 | 同上（同 `thinTiles` 门） |
| `ENTJOY_CLAIM_ADAPT` | 开 | **逐 job 学习认领几何**（散开 / 邻近） | General 路 |
| `ENTJOY_WAKE_POLL` | 开 | 提交侧"需求感知"唤醒：有人在轮询注入器时**一个字节都不写** | 全部派发入口 |
| `ENTJOY_SPIN_NEEDS_WORK` | 开 | 大自旋窗只在"注入器里还有活"时给（否则退火 park，不抢 SMT） | worker 停靠 |
| `ENTJOY_PHYSCAP_SMALLJOB` | 开 | 小 job 的 worker 上限退到**物理核数**（避免 SMT 兄弟互抢） | 调度入口 |
| `ENTJOY_SYSTEM_READ_WRITE_ORDER` | 开 | ECS 读→写串行化（唯一被发现的静默竞态，已修 + 4 用例） | ECS 系统调度 |
| `ENTJOY_NATIVE_SINGLE_JOB` | 开 | 单 job 走原生快路径（`=0` 回退托管） | 托管入口 |

⚠ `TILES_UNIFORM` / `TILE_FASTPATH` / `CLAIM_ADAPT` 的默认档行为受
**`thinTiles` 门**（`cs <= kClaimSpanThinElems(16)`）影响，而该门**等价于"JCC 顶在下限 16 上"**
（= 这次调度的 `length` 够短），**不是** tile 厚薄判据；且"进不进这个 regime"本身不稳定。
详见 doc13 §2 与 gridsearch/09 §58。

## B. 已验收的策略阀（可调，非开关）

| env | 默认 | 语义 |
|---|---|---|
| `ENTJOY_JOB_WORKERS` | 逻辑核心 − 1 | worker 线程数（`>= 0` 生效） |
| `ENTJOY_CLAIM_SPAN` | **1024** | 每次认领的**元素跨度**（`=0` 关闭 ⇒ 复现旧行为）。薄 tile 摊薄认领、厚 tile 保持 worker 邻近 |
| `ENTJOY_CLAIM_BATCH` | 0 = 内置 4 | 认领粒子上限 cap（`step = clamp(tileCount/workers, 1, cap)`） |
| `ENTJOY_TILES_PER_WORKER` | 64 | flat parallel-for 的默认 tiles/worker（**只影响 flat 路**） |
| `ENTJOY_JCC_TARGET_US` | 6400 | `ResolveChunkSize` 的 `two_factor` 分支唯一消费者（目标每 tile 串行量，µs） |
| `ENTJOY_SPIN_BUSY` | 8192 | busy 自旋窗（pause 次数）。**只改自旋时长，不改变语义** |
| `ENTJOY_STATS` | 开 | 诊断统计总开关（`=0` 旁路全部纯诊断 RMW）。⚠ `g_backendBatchesOutstanding` **不**受它门控 |

## C. 安全阀 / 兼容阀（默认保守）

| env | 默认 | 语义 |
|---|---|---|
| `ENTJOY_NATIVE_ALLOW_MISMATCHED_FALLBACK` | 关 | 允许加载**版本不匹配**的原生库（默认拒绝；`=1` 才放行） |
| `ENTJOY_WORKER_AFFINITY` | 关 | worker 绑核（默认交 OS 自由调度，避免 SMT 双线程死绑共享执行单元） |
| `ENTJOY_JCC_ROBUST` | 关 | JCC 健壮分类（环形窗中位数 + 最小样本 + 冷却 + 双向迟滞 + 周期探针）。**价值 = 可复现性，不是速度**：同一负载 8 rep 下默认档 chunk 呈**双带**、摆 **11.2×**（更早抽样见过 ~40×），开启后压到 **1.0001×**。**已按纪律验收（6 对同会话/逐对同号/五段全看）⇒ 结论"不提默认"，理由 = 性能中性无净收益**：整步在 **median 口径 +0.054 ms（2/6）** 与 **min 口径 −0.081 ms（4/6）** 下**符号相反**、都没达到"逐对同号"；`count` 段稳定略好（5/6、−0.06 ms）。⚠ 两臂共有的 ~2× 偶发停顿是 doc12 表 16 已定性的 **GC/OS 停顿**（只污染 median 口径），**不是**本开关的缺点。⇒ 定位为**可选的可复现性阀**（显式开启可换稳定，代价是偶发停顿）；或改用**钉住 `cs`**（`ENTJOY_JOB_BATCH_TABLE` / `ENTJOY_TILES_PER_WORKER`）这条更便宜的路径。⚠ 开启后被判 mem-bound 的 job 只写**粗**成本通道（`GetPerElemCost` 可能为 0，须读 `GetCoarseCost`） |

⚠ **不在框架 env 面、但影响所有跨栈读数：主线程 assist** —— 框架只提供托管 API
`NativeJobScheduler.SetMainThreadAssistEnabled`（导出 `JobSystem_SetMainThreadAssistEnabled`），
**框架默认是关**（`JobSystem.cpp:750 g_mainThreadAssistEnabled{false}`）；但**游戏仓自己把它默认打开**
（`ComputeShaderBattleSimulation/CPUBattle/Scripts/CPUBattleEcs.cs:625`：
`JobAssistOn = env("ENTJOY_ASSIST") != "0"`），并打印 `[CPUBattleEcs] JobSystem assist=开`。
⇒ **`ENTJOY_ASSIST` 是游戏侧 env，不在本框架契约内，且框架已删除该 env 的解析**（只留 API）；
**脚本之间也不一致**：doc09/doc10 时代的 `ab-aligned.ps1` 等**显式 pin `=0`**，而 2026-10-05 之后的战役脚本
（`qq-judge2`/`frozen-pairs2,3`/`cs1-cost`/`ab-3arm-2curve`/`perpass-*`）**先清空全部 `ENTJOY_*` 再只设自己那几个**
⇒ **assist 回落到"开"** ⇒ **doc15–doc17 的读数含 assist，doc10 的不含**，而 Unity 侧无对应物
（B 侧 `[M4-DISCLOSE]①` 自己标了这一点）。
引用任何跨栈数字时必须显式写明 assist 开/关 —— doc17 §1.2 的对照显示：在本协议下 assist **关**反而更好
（整步 1.024、Melee 1.080，对比开着的 1.021 / 1.067）。⚠ 该对照的两臂是**两次会话** ⇒ 含会话漂移，
**稳健的只是"两臂都 ≈1.02、都 6/6"**；"assist 是成本"若要落定需做**同会话** A/A。

## D. 诊断 / 器械（默认关，零开销）

| env | 语义 |
|---|---|
| `ENTJOY_JCC_VERBOSE` | 打印 `ResolveChunkSize` 决策 + 退役学习快照 |
| `ENTJOY_DIAG_JCC` | JCC/分块**决策计数**仪器（每个 return 路径次数 + chunk 分布 + 每批 workerCount） |
| `ENTJOY_DIAG_NATIVE_PHASE` | `Complete()` 分段诊断 |
| `ENTJOY_DIAG_NATIVE_SCHED` | 原生 `Schedule` 分段 |
| `ENTJOY_DIAG_CSHARP_PHASE` / `_WINDOW` | 托管侧分段 |
| `ENTJOY_DIAG_E1` / `_MS` | E1 worker 忙比 / 相位尾部 |
| `ENTJOY_DIAG_TIMING` | 关停时打印 timing 直方图 |
| `ENTJOY_JOB_TILE_TRACE` | 逐 tile trace（会**关掉** tile-run 合并） |
| `ENTJOY_CLAIM_STAT` | 认领点 rdtsc 探针（量共享游标 cacheline 弹跳） |
| `ENTJOY_JOB_BATCH_TABLE` / `_DUMP` | 逐 job 内批档表（键 = 内核在本模块内的 **RVA**，非 ASLR 指针哈希）；`_DUMP` 打首见键的尺寸 |
| `ENTJOY_BATCHID_CALLBACK` | 逐批回调（把托管异常绑定到具体 batch） |
| `ENTJOY_DEBUG` | Dear ImGui 调试面板 |
| `ENTJOY_DUMP_BINDINGS` | 生成器侧：dump 形参绑定 |
| **`ENTJOY_JOB_COST_CACHE`** | `=0` **真关 JCC**（`JobCostCache`）：不再调用 `ResolveChunkSize` 的按成本求和分支，未命中批表的调用点改由 `ENTJOY_TILES_PER_WORKER` 兜底。**这是"对齐档"的两个定义开关之一**（原生初值与托管默认值都读同一 env，否则 C# 的 Initialize 会盖回去）。源码见 `JobSystem_Scheduler.cpp` / `JobSystem.cpp`；证据见 doc16 §40 |
| **`ENTJOY_JOB_BATCH_BY_NAME`** | 按 **job 名**逐调用点钉死内批：`<JobName>:<batch>[, …]`。命中即 `ResolveChunkSize` **不被调用**（等价于 JCC 全关 + 逐调用点对齐）。名字 = 托管 `Type.Name`，指针由 `BindingsGenerator` 在 `NativeExports` 静态构造里经 `JobSystem_BindBatchName` 绑定 ⇒ **不随重编漂移、不含工程命名空间**（`ENTJOY_JOB_BATCH_TABLE` 的 RVA 键**会**随重编整组漂移且失败静默，只保留作诊断/兼容）。未绑上的名字**逐个打印** + `resolved=N/M`；默认档零噪声。证据见 doc16 §40/§46 |

⚠ **补登说明（2026-10-07）**：这两个开关是 doc15–doc17 里"**对齐档**"（JCC 全关 + batchSize 逐调用点与 Unity 一致）
的**唯一定义器件**。此前它们只出现在 gridsearch 的战役文档中，不在本契约表里 ⇒ 引用"对齐档"读数时
**必须同时给出这两个 env**，否则无法复现。

诊断读数的**用法要点**：`[JOBWAKE] skipped=` 在 `wakePoll=ON`（默认）时**恒为 0** ——
真正的跳过数见 `[JOBWAKEPOLL]`。`[JOBF2F4] applied` 为 0 不代表 F2/F4 没生效，见 A 节的 `thinTiles` 说明。

## E. 构建期开关（生成器 / MSBuild）

| 开关 | 默认 | 语义 |
|---|---|---|
| `ENTJOY_GUARD_FOLD` | 开（`!= "0"`） | G 守卫折叠（消越界 UB 的等价变换；`=0` 回到未折叠） |
| `ENTJOY_LTO` | 关（`== "1"` 才开） | 给 `NativeTranspiled` 开 LTO（MSVC `/GL`+`/LTCG` 等价物）；生成器把开关写进 CMake |
| `ENTJOY_NT_SNAP_DIR` | — | 发射面快照目录（夹具的 emit-snapshot 基线） |
| `EntJoyAutoSimdMeasured` | error | AutoSIMD 显式放行（默认对未测量内核报错） |
| `ENTJOY_MSVC_EXTRA_FLAGS` | — | 追加给 MSVC/真机构建的额外开关（**实验用**：R21/R27/R28/R34 用它试过 `/Os`、`/GS-`、`/Qpar-`）。⚠ 真机 `NativeTranspiled` 是 **clang-cl** 编的（VS 自带 LLVM），**MSVC 专属开关会被静默忽略** ⇒ 用它得到的"空结果"不等于"MSVC 也这样"（doc16 §44.16） |

⚠ **2026-10-07 有一条"加了又撤"的开关，记在这里以免后人重走**：曾实现过构建期属性
`EntJoyNativeUnityBatch`（`0` = 交付档单 TU / `N>0` = 每 N 个发射件一批的"开发档"，两档各占
`build` / `build-batchN` 目录与独立哈希清单）。**同日撤销**，因为实测它解决错了问题：
"改一个 job 体 ~20 s"的真因是生成器 D1 清理误删 `ispc.exe` 的产物 `SharpNative_*_ispc.h`
（见下），**修那个 bug** 后端到端 **19.8–22.8 s → 9.5 s**；而该开关最好的一档反而**更慢**
（同一处改动：批 0 = **2.70 s** / 1 个 unity obj，批 8 = **4.89 s** / 18 个 —— 小 TU 各付一次编译器启动）。
⇒ **交付档单 TU（`CMAKE_UNITY_BUILD_BATCH_SIZE 0`）保持不变，也不需要暴露成开关**；
`set(...)` 用普通形式（非 CACHE）即可：它**遮蔽**缓存项，天然免疫"旧缓存把交付档改掉"。

⚠ **同日撤回一条原读数**：曾写成"单 TU 13.4 s → 批 8 3.3 s（本仓实测）"，**不成立**，已删。
那份对照比较的是两份**不同代**的生成工程文件（`build` 的 `ZERO_CHECK.vcxproj` 停在 2026-09-13、
`.vcxproj` 停在 10-07 22:57，而 CMakeLists 已 23:19），且 `cmake --build --target NativeTranspiled`
**不跑 ZERO_CHECK** ⇒ 老目录的工程文件根本不参与重生成。
**真因（2026-10-07 已修）**：生成器的 D1 清理（`PruneStaleGeneratedFiles`）按"不在本次写出集合里就删"，
而 `SharpNative_*_ispc.h` 是 **`ispc.exe -h` 的产物**（CMake `add_custom_command` 的 OUTPUT）、生成器从不写它
⇒ 每次生成把这 31 个头删光 ⇒ CMake 判定 31 个 ISPC 步骤输出缺失、全部重跑（≈13 s）。
修复后：生成后头数 0 → **31**，原生 **15.9 s → 2.80 s**，端到端 **19.8–22.8 s → 9.5 s**。
逐条证据见 [热重载实现规划.md](../热重载实现规划.md) §39。

> **codegen 的三个开关已删除、行为已固定**（2026-10-04，不再是可配置项）：
> · `ENTJOY_VALUE_BIND` → 固定为"字段**参与某处循环的行程数**则按值绑定"；
> · `ENTJOY_SCALAR_RESTRICT` → 固定为**窄档**（只给出现在循环内的纯值字段形参加 `__restrict`）；
> · `ENTJOY_LIST_RESTRICT` → 固定为**开**（仅当该 job 只有一个 NativeList 且其长度决定行程数；
>   两个可能别名 ⇒ 加 restrict 就是说谎/UB）。
> 三者都是已验收的默认档行为，删的只是"选另一支"的历史臂；详见 doc13 §5.4/§5.4b。

⚠ **已删除、不要再引用**：`ENTJOY_PACK_SCALARS`（默认关且开启即构建失败，整条通路已删）、
`ENTJOY_SCHED_PRIO`、`ENTJOY_COMPLETE_SPIN`(+`_ADAPT`/`_BIGNS`)、`ENTJOY_SPIN_HOT_US`、
`ENTJOY_WAKE_POLL_NEED`、`ENTJOY_DEFER_WAKE`、`ENTJOY_CLAIM_BLOCK`、`ENTJOY_CLAIM_GUIDED`、
`ENTJOY_CLAIM_SLICE`、`ENTJOY_TILE_STRIDE`、`ENTJOY_JCC_MEMBOUND_COARSE`、`ENTJOY_PHYSCAP_MAXELEM`。
删除判据与逐条证据见 doc13 §5。

## F. 未决（保留观察）

| env | 默认 | 状态 |
|---|---|---|
| `ENTJOY_GUIDED_TILES` / `_K` / `_FLOOR` | 关 | driven by `g_guidedEnabled`（**guided 的 tile 尺寸**策略，chunk ∝ 剩余）。与已删的 `ENTJOY_CLAIM_GUIDED`（**认领预算**收缩）是**不同机制**；文档对该轴**无否证结论** ⇒ 暂不删。它经公开导出 `JobSystem_ConfigureGuided` + 托管 `NativeJobScheduler.SetGuidedEnabled` 暴露 ⇒ 删除会动 ABI，需单独决策 |
| `ENTJOY_FORCE_INNER_BATCH` | 0 = 关 | 强制显式内批 + 跳过 JCC；同时是 `JOB_BATCH_TABLE` 未命中时的**活回退支** |
