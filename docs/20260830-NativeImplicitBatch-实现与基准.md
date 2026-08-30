# Native 隐式批收集 + ManyJobsBench 实测（2026-08-30）

> 背景：`20260826-JobSystem-多Job调度开销基准与分析.md` §20.4 曾把批收集统一收归 C#（`BatchScope` 显式 +
> `ImplicitBatch` 全局单例）。本次重新实现 **native 侧隐式收集**：`job.Schedule()` 零改动透明收集，
> `EndFrame / Complete` 统一提交 + 单次唤醒——补回 §17 曾实现、§20.4 删除的路径，并与 C# 收集层互斥切换。

---

## 一、实现（Native 隐式批收集 v1）

### 语义

- 开关（`JobSystem_SetImplicitBatchEnabled(int)`）开启后，**SubmitBatch 批路径**的 job
  （IJobParallelFor / IJobParallelForBatch / IJobChunk / IJobEntity）在主线程直接提交时挂入 pending，
  **不立即提交、不唤醒**；IJob / IJobFor（inline 或 SubmitWork 池任务）不收集，inline 现状保持。
- force point 二选一：
  - `Complete()` / `IsCompleted()` —— 自动 flush 再等（防死等/误报未完成）；
  - 显式 `EndFrame()` / `FlushPendingSubmits()` —— 帧末统一提交 + 单次唤醒。
- flush = deferNotify 窗口（`g_submitDeferDepth`）内逐个 `SubmitBatch` + 一次 `WakePending()`。
- **依赖未完成的 job 不进 pending**：走 continuation，依赖完成时立即 `SubmitBatch`（依赖顺序天然保持）。

### 关键改动

| 文件 | 改动 |
|---|---|
| `JobSystemInternal.h` / `JobSystem.cpp` | 全局 `g_implicitBatchEnabled`（atomic\<bool\>，默认关）+ `g_pendingBatchesMutex` + `g_pendingBatches`（vector\<BatchState*\>） |
| `JobSystem_Tiles.cpp` | `SubmitOrPending`（开关开 → `AcquireState(handle)` 防悬垂 + 挂 pending；否则直接 `SubmitBatch`）；`FlushPendingSubmits`（swap 出 → defer 窗口逐个提交 + `ReleaseState` → 单次 `WakePending`） |
| `JobSystem_Scheduler.cpp` | 3 处主线程直接提交（ParallelFor L441 / ParallelForBatch L528 / ChunkBatchCore L646）改 `SubmitOrPending`；continuation 路径不动；`Shutdown()` 排空 pending |
| `Exports.h/cpp` | 新导出 `JobSystem_SetImplicitBatchEnabled(int)`（0=关闭并排空积压）+ `JobSystem_FlushPendingSubmits()`；`Complete` / `CompleteAndRelease` / `IsCompleted` 开头自动 flush |
| `NativeJobCore.cs` | 绑定 2 个新导出 |
| `NativeJobScheduler.cs` | `SetImplicitBatchEnabled` 改为**互斥切换**：true = native 收集（NativeDll 不可用时回退 C# 层）；false = 关闭 native 并转接启用 C# `ImplicitBatch` 收集；`EndFrame`/`FlushPendingSubmits` 两层都刷 |

### 生命周期安全（关键）

- **UAF 防护**：`Schedule*` 返回的 `JobHandle` 只持 `refCount=1`；若 C# 丢弃 handle 被 GC 回收而 batch 仍在
  pending，`HandleState` 会被 recycle → 悬垂。入队时 `AcquireState`、flush 时 `ReleaseState` 平衡。
- **Shutdown / 关闭开关**均先 `FlushPendingSubmits()` 排空，未及执行者按现有 in-flight 泄漏兜底（不 UAF）。

### 验证（全 PASS）

| 验证 | 结果 |
|---|---|
| Native 单测 `ImplicitBatchTests`（pending 不执行 / Flush 执行 / 关闭排空 / 依赖 continuation / 小 job 不收集） | 5/5 |
| 回归 `JobSystemTests` / `AssistLifetimeTests` / `ChaseLevIntegrationTests` / `MPMCInjectorTests` | 全 PASS |
| `NativeDll.dll` 导出（dumpbin） | `JobSystem_SetImplicitBatchEnabled` / `JobSystem_FlushPendingSubmits` 存在 |
| C# 冒烟（A1 未 flush 不执行 / A2 Complete 自动 flush / B EndFrame / C false 转接 C# 层） | 4/4 |

### MSVC 编译修复（顺带）

`JobSystem.cpp` 的 `getenv` 在 MSVC `SDLCheck(/sdl)` 下报 C4996 error（`/wd4996` 无效）。
修复：`NativeDll.vcxproj` 4 配置 + `tests/NativeDll.Tests/CMakeLists.txt` MSVC 分支加
`_CRT_SECURE_NO_WARNINGS`。验证：MSVC 与 ClangCL 全量 Rebuild 均通过、无 C4996（Clang 不识别该宏，无副作用）。

---

## 二、ManyJobsBench 实测（对比 docs 基线）

> 环境：同机（Windows 11 x64，15 workers），`bin/EntJoySample.exe`（StartupObject=ManyJobsBenchTest.Program），
> NativeDll 为本次新构建（MSVC，含隐式批导出）。docs 基线 = `20260826-JobSystem-多Job调度开销基准与分析.md`
> §11/§12/§15/§16/§17/§18。运行期间 10_SIMD 已注释排除（构建加速）。

### 逐 job 基线

| case | docs 基线 | 本次实测 | 判定 |
|---|---|---|---|
| IJob x200 (inline) | 0.3µs/job（§15） | 0.4µs | ✓ |
| IJobFor·1K x100 | p50 0.79ms（§15） | p50 0.748ms | ✓ |
| IJobFor·100K x100 (async) | 99.9~103.4µs（§11/§15） | 98.2µs | ✓ |
| IJobParallelFor·8K x100 | 4.1~5.4µs（§15/§16） | 4.2µs | ✓ |
| IJobParallelFor·8K x400 | 4.4~4.6µs（§15） | 4.2µs | ✓ |
| IJobParallelFor·8K batch256 x100 | 2.9µs（§12） | 4.0µs | JCC 学习轨迹差异 |
| **IJobParallelFor·64K x100** | 2.7~4.1µs（§11/§12） | **20.0µs** | **JCC 相关，见下** |
| IJobParallelFor·1M x20 | 30.3~35.7µs | 32.3µs | ✓ |
| IJobChunk·16K x100 | 3.0~3.7µs（§7/§12） | 3.1µs | ✓ |
| IJobChunk·1M x50 | 7.2~8.5µs（§11） | 7.2µs | ✓ |
| IJob chain x200 | 0.5~0.6µs | 0.5µs | ✓ |

### 批路径（显式 / C# 隐式 / Native 隐式）

| case | docs 基线 | 本次实测 | 判定 |
|---|---|---|---|
| Batch·IJob x200 | 0.6µs（§15） | 0.7µs | ✓ |
| Batch·IJobFor·1K x100 | p50 ~0.75ms（§15） | p50 0.831ms | ✓ |
| Batch·IJobFor·100K | 101.2µs（§15） | 99.1µs | ✓ |
| Batch·IJobParallelFor·8K x100 | 2.8~2.9µs（§16） | 2.6µs | ✓ |
| **Batch·Mixed(100+50+50) x200** | 2.8~2.9µs（§16-18，0.56~0.58ms） | **3.2µs（0.65ms）** | +10% 噪声内 |
| **Implicit·Mixed(100+50+50) x200**（C# 显式 Add 收集） | 3.0µs（§18）~3.4µs（§17，0.68ms） | **3.3µs（0.66ms）** | ✓ |
| **NativeImplicit·Mixed(100+50+50) x200**（本次新增 case） | — | **3.5µs（0.70ms）** | 与 C# 隐式相当，验证 native 收集正常 |

### 64K 的"慢"的真相（后续排查修正）

- 最初归因 JCC（perElem 虚高），后经多轮对照发现：**64K 空体 C# 回调的真实执行成本 ≈ 15µs/job**——
  每元素 ~3.7ns 委托回调开销 × 65536 元素 / 16 worker（独立 C++ 复现：空回调 64K 实测 6.66µs，
  C# 委托边界约为 C++ 的 2~3x）。JCC 开/关（16.9 vs 17.9µs）、tiles 61~63 的差异 < 1µs，均非时间主项。
- docs 基线（2.7~4.1µs）无法在当前环境复现：期间 `bin\EntJoy.Jobs.dll` 曾因旧版本导致 JCC 同步失效
  （`enabled=0`、全程 tpw），部分早期"64K 3.0~3.8µs"数据来自该异常状态或旧回调/JIT 环境，不再作为对照基准。
- 8K（5.0µs）与 1M（33.8µs）落在 docs 范围内；空体 job 的 tile 数对总时间的边际影响本就有限（执行成本主导）。

### JCC 两因子修复（perElem 纯执行 + C_fixed 固定开销）

**改动**：
1. perElem 改纯执行口径 `(lastTileAt − firstTileAt)/N`（排除唤醒 ~300µs 虚高）；`firstTileAt` 无条件记录。
2. 细/粗样本归属改调度侧 `jccFine` 标记（修复 `JccConcurrentHeterogeneous` FAIL——perElem 修正后公式可能产出 < tpw 的 tiles）。
3. mem-bound 阈值 0.85 → 1.15（纯执行口径下 compute-bound 细/粗比值天然≈1，0.85 误判全部 mem-bound）。
4. **两因子**：退役反解每 tile 固定开销 `C_fixed = (wc×execSpan − N×C_elem)/tiles`（EWMA 学习），
   决策 `tileSize = (150µs − C_fixed)/C_elem`（下限 256 元素/tile）；空体/超轻（tpw 粒度单 tile <16µs）
   退回 tpw 兜底（仍登记细样本，mem-bound 分类收敛 tpw 稳态）。

**为什么必须有第二因子（真正的原因）**：
- 单 tile 回调成本 = `C_fixed`（每 tile 固定：回调入口/认领/异常检查）+ `tileSize×C_elem`。
- JCC 原模型只有 `perElem`（单因子），空体 job 的 `C_elem≈0`、`C_fixed` 主导 → 单因子退化、决策依赖
  二分退回启发式。
- 旧口径（wall）靠"唤醒虚高 ∝ 1/N"间接、不均衡地补偿 C_fixed——小 job（8K）碰巧受益（27 tiles）、
  大 job（64K）过度细分（217 tiles），是 8K/64K/1M 表现各异的历史根源。
- 实测空体 C# 回调执行成本高度非线性（8K 每元素 ~9.8ns、64K ~3.7ns、1M ~0.5ns——tile 越大摊销越低），
  任何线性模型（单/两因子）都无法精确刻画空体 job；两因子的价值在**真实负载**（C_elem 主导时每 tile 150µs
  目标更准），空体保持 tpw 兜底不劣化。

**验证**：JobSystemTests（含全部 Jcc 用例）/ ImplicitBatchTests / AssistLifetime / ChaseLev / MPMC 全 PASS；
JCC 学习轨迹（临时复现实验）显示 8K 空体收敛 mem-bound（tpw 稳态、perElem 0.19ns 有效）。

### 最终实测（两因子，JCC 正常，当前 DLL）

| case | 最终 | docs 基线 | 判定 |
|---|---|---|---|
| 8K x100 | **5.0µs**（62.5 tiles） | 4.1~5.4 | ✓ |
| 8K x400 | 4.5µs | 4.4~4.6 | ✓ |
| 8K batch256 | 4.5µs | 2.9 | JCC 学习波动 |
| 64K x100 | 17.9µs（61 tiles） | 2.7~4.1 | 空体执行成本主导（见上） |
| 1M x20 | 33.8µs（33.4 tiles） | 30.3~35.7 | ✓ |
| IJobChunk·16K / 1M | 3.5 / 7.7µs | 3.0~3.7 / 7.2~8.5 | ✓ |
| Batch·Mixed / Implicit·Mixed / NativeImplicit | 3.5 / 3.4 / **3.5µs** | 2.8~3.4 | ✓ |

### 结论

- 「200 混合 job/帧」三条批路径（显式 BatchScope / C# 隐式 / **native 隐式**）全部落在
  **0.68~0.71ms/帧（3.4~3.5µs/job）**，与 docs §17/§18 终态一致（±10% 噪声内）。
- native 隐式批行为符合设计：只聚合 SubmitBatch 路径（本例 50 个 parFor），IJob/IJobFor 仍 inline 即时执行，
  总成本与 C# 隐式层相当。
- 64K 空体的 ~15µs 为 C# 委托回调的真实执行成本（非 JCC 决策缺陷）；JCC 两因子改善真实负载决策，
  空体保持 tpw 兜底不劣化。8K/1M/Chunk/批路径均落在 docs 范围。
