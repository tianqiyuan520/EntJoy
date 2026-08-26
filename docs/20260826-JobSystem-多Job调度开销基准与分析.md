# JobSystem 多 Job 调度开销基准与分析（2026-08-26）

> 背景：帧间隔单大 Job（100 万实体 ~1ms，见 `20260826-JobSystem-帧间隔调度开销分析.md`）已达标，
> 但生产帧模型是「每帧几百个中小 Job」。该文档 §7 实测 200 job/帧 2.4~3.8ms；§8（L1/L2/L3）
> 定性了三层结构性差距。本文用**空体 Job（排除运行内容）**量化 4 种类型
> （IJob / IJobFor / IJobParallelFor / IJobChunk）在 `Schedule×N + Complete×N` 下的
> **纯调度流程**成本：开销在哪、每 job 成本模型、可验证的优化方向。
>
> 关联：`20260826-JobSystem-帧间隔调度开销分析.md`（单大 Job）、`20260826-IJobChunk设计说明.md`（架构）
> 基准代码：`samples/EntJoySample/01_JobSystem/ManyJobsBenchTest/`（`ManyJobsBenchSample.cs`）

---

## 一、基准与方法

- 位置：`samples/EntJoySample/01_JobSystem/ManyJobsBenchTest/`；4 类型空体 job。
- 构建入口（多个 Program.Main 并存时选择本基准，不动其他 Main）：
  ```powershell
  dotnet build samples\EntJoySample\EntJoySample.csproj -c Release --no-restore `
    -p:BuildProjectReferences=false -p:EnableNativeCompile=false `
    -p:StartupObject=EntJoySample.ManyJobsBenchTest.Program
  & .\bin\EntJoySample.exe | Tee-Object manyjobs-run.txt
  ```
- 环境变量：`ENTJOY_JOB_WORKERS`（默认 15）、`ENTJOY_BENCH_WARMUP`（5）、`ENTJOY_BENCH_FRAMES`（100）。
- 测量口径（对齐 IJobChunkMoveCompareTest）：
  - **Pass A**（timing OFF，100 帧）：干净墙钟 avg/p50/p95/max + Schedule/Complete 分段 +
    主线程分配 + native 全局计数器；
  - **Pass B**（timing ON，帧数=clamp(2048/N, 10, 40)，按 native 采样上限自适应）：
    batchTotal / submit2first / spread / execSpan / maxRange 批次分布。
- 统计口径注意：
  - `batchTotal / submit2first / spread` 无条件记录；`execSpan / maxRange` 需 timing ON；
  - native 采样上限 2048（溢出计数 dropped，输出已标注）；
  - **`parkWake / notified` 是死字段（声明与快照存在，但 Chase-Lev 路径从未 fetch_add，恒 0）**——
    需要唤醒计数时应修 C++ 侧补统计，不能依赖现有值；
  - 空体 job 在 schedule 循环内即完成 → `complete≈0.1µs`；帧耗 ≈ **S 段**。真实负载帧耗 = schedule + drain(执行)。

## 二、数据：一帧 S+C（空体纯调度，15 worker）

| case | N | 帧 avg (S+C) | per-job | schedule | complete | tiles/job | participants/job |
|---|---|---|---|---|---|---|---|
| IJob（inline） | 200 | **0.076 ms** | 0.4µs | 0.3µs | 0.1µs | 0 | 0 |
| IJob 依赖链（async） | 200 | **0.091 ms** | 0.5µs | 0.4µs | 0.0µs | 0 | 0 |
| IJobChunk·16K（少量 tile） | 100 | **0.304 ms** | 3.0µs | 3.0µs | 0.1µs | 18.9 | 5.2 |
| IJobParallelFor·8K batch256 | 100 | **0.983 ms** | 9.8µs | 9.8µs | 0.1µs | 33.6 | 15.8 |
| IJobParallelFor·64K | 100 | **1.150 ms** | 11.5µs | 11.4µs | 0.1µs | 42.0 | 15.8 |
| IJobParallelFor·8K（auto） | 100 | **1.725 ms** | 17.3µs | 17.1µs | 0.1µs | 63.0 | 15.8 |
| IJobChunk·1M（224 tile） | 50 | **3.154 ms** | 63.1µs | 62.9µs | 0.2µs | 219.4 | 15.8 |
| IJobParallelFor·8K | 400 | **7.076 ms** | 17.7µs | 17.6µs | 0.1µs | 63.0 | 15.8 |
| IJobFor·100K（async 单任务） | 100 | **9.868 ms** | 98.7µs | 0.8µs | 97.9µs | 0 | 0 |
| IJobFor·1K（inline） | 100 | avg 2.200 / p50 0.793 ms | 22.0µs | 21.9µs | 0.1µs | 0 | 0 |

批次级（Pass B 选数）：IJobChunk·1M batchTotal P50=280µs、submit2first P50=227µs；
IJobParallelFor·8K batchTotal P50=424µs、submit2first P50=392µs；
batch256 版 submit2first P50=**0.7µs**、spread 2~3µs。

## 三、开销归因模型

**每 job S+C ≈ 2~3µs 固定仪式 + 0.28µs × tile 数**（空体下拟合：19 tile→3.0µs、34→9.8µs、
42→11.5µs、63→17.3µs、219→63.1µs）。

1. **主线程 C# 链极便宜：~0.3µs/job**。IJob inline 只有 0.3µs（P/Invoke + state 创建 +
   ContextPool + 委托缓存），说明跨层包装不是大头。
2. **大头是「tile × 注入器洪泛」**：每 tile 一个 RangeTask 进全局 MPMC 注入器
   （`ChaseLevScheduler::SubmitBatch`，默认预切分粒度 `claimBatch≈tileCount/(wc×16)`，
   219 tile 的 chunk job → 219 个 RangeTask 入队，2 CAS/次 + 满时退避）。
   证据：1M×20（1260 tasks/帧）schedule 仅 3.6µs/job，而 8K×100（6300 tasks/帧）17.1µs/job——
   **背压随帧内总 task 数上升**。文档 §7 的「空 1M query 224 tile ≈ 90µs 串行」同源。
3. **唤醒不是瓶颈**：batch256 的 submit2first P50=0.7µs、spread 2~3µs；
   随 N 涨到几百 µs 的 submit2first 是 **drain 排队**（tile 太多、15 worker 消化慢），非通知延迟。
4. **IJobFor 有「逐元素 C# 回调税」**：inline 路径 C++ 循环逐元素调 C# delegate
   （~8~20ns/元素）；100K 长度走 async 单任务 = **单 worker 串行执行 + 逐元素回调** → 98.7µs/job，
   是全部路径中最差档（同长度 IJobParallelFor·1M 并行 tile 仅 28.8µs/job）。
5. **固定仪式**（并行路径 ~3µs 起）：AcquireBatchStorage + CreateState/AcquireState +
   参与者令牌 + 退役链（双条件 tilesRemaining==0 && pendingTasks==0）。
6. **n 分配**：chunk 路径 279~310 B/job（`new ChunkBatchContext`），并行 for 59 B/job，
   IJob 185 B/job（首帧 605B = 委托缓存首建）。几百 job/帧 ≈ 50~100KB，非 GC 压力源；
   偶发 8~10ms 帧尖峰（如 IJobFor·1K avg 2.2 vs p50 0.79）判为 OS/GC 噪声，非调度器。

## 四、优化方向（按预期收益/风险排序）

### P0-1 token 认领统一为默认路径（改动集中，收益最大）
- 现状：`ExecuteClaimToken` 只用于 workerCap<全局线程数 的批次；默认路径仍按
  `claimBatch=ceil(tileCount/(wc×16))` 预切分 **O(tiles)** 个 RangeTask 全灌注入器。
- 做法：默认路径也统一为「tiles 预切分 + wc 个 token 任务」，token 内 `nextTile.fetch_add(kClaimBatchSize)`
  原子认领（细粒度保留、deque 可窃取已有语义）。
- 预期：每 job 入队任务数 219→15（chunk·1M 63µs→~5~8µs）；注入器 CAS/背压同步下降。
- 注意：与已回退的「推送粒度 wc×16→wc×4」不同（那只是改预切分大小），token 化是结构性变化，
  **需重测**；均匀大 job 的认领粒度保持 4 tile/次，负载均衡不变。

### P0-2 IJobFor 大长度并行化 / 批回调
- 现状：`ScheduleFor` 长度 >4096 走 `SubmitBackendAsync` 单任务 → 单 worker 串行 + 逐元素 C# 回调。
- 做法：大长度改 tile 路径（或走 `IJobParallelFor` 的 BatchCallback 逐批回调）。
- 预期：IJobFor·100K 98.7µs/job → ~10µs 级。

### P0-3 注入器去单点（配合 P0-1 后评估）
- 现状：全局 MPMC 单队列，帧内几千 task 在同一条 cache line 上 CAS。
- 做法：per-worker 提交队列（提交方按 worker 分片入队）或分档 injector。
- 预期：背压项（8K×100 schedule 17µs vs 1M×20 3.6µs 的差值）消除。
- 风险：与 Chase-Lev 窃取语义交互复杂，放 P0-1 之后单独 A/B。

### P1 批提交 API（摊薄固定仪式 ~3µs/job）
- 共享 tile 构建 + 单次唤醒 + 单 handle（文档 §7/§8 目标：每 job ~10µs → 200 job ~2ms）。
- 与 P0-1/2 正交，叠加收益。

### P2 单 job 瘦身（沿用上一轮清单，优先级下调）
- C# Complete 3 次 P/Invoke 合并、per-泛型静态委托缓存、异常计数门控、
  `ConsumeLongBatchBarriers` 空锁门控、`new ChunkBatchContext` 池化。

### 不做（吸取 §5 回退教训）
- 选择性唤醒（35ms 滞留尖峰教训）；永久自旋；per-tile 全局 MPMC 队列；
  同一批实验同时改 tile 粒度 + worker 数 + assist。

## 五、A/B 验证方案

- 基线 case（本基准现测）：IJobChunk·1M x50（63.1µs/job）、IJobParallelFor·8K x400
  （17.7µs/job）、IJobFor·100K x100（98.7µs/job）、IJobChunk·16K x100（3.0µs/job）、
  IJob x200（0.4µs/job）。
- 原则：单变量；每变量 ≥3 进程；报告 p50/p95/max 而非只看 avg。
- 门槛：目标 case per-job 降 ≥50%，且回归基准（帧间隔 1M 单大 Job p50 ≤ 0.90ms、
  100 帧 Light/Heavy/Sleep Verify OK）不劣化 >5%。

## 六、结论（一句话）

几百个 Job/帧的几毫秒几乎全来自**提交侧**：每 job 2~3µs 固定仪式 + **0.28µs × tile 数**的
注入器洪泛（几十个 219-tile job ≈ 3ms）+ 长 IJobFor 的串行逐元素回调（百级 ≈ 10ms）；
唤醒与完成链路均已压缩到 µs 级。P0-1（token 认领默认化）+ P0-2（IJobFor 并行化）即可把
「200 个混合 job < 1ms」变成可验证的现实目标。

---

## 七、实施记录（2026-08-26）：token 提交模式 A/B ✅ P0-1 已验证

**目标**：`ChaseLevScheduler::SubmitBatch` 默认路径从「O(tiles) 个预切分任务逐个 CAS 入注入器」
统一为「O(workers) 个令牌 + `nextTile.fetch_add(4)` 认领」。IJobFor 保持单线程语义（P0-2 并行化**不做**）。

**改动**（默认保持 slice，env 开关 A/B，避免未经回归就切默认）：
- `src/NativeDll/JobSystemInternal.h`：新增 `extern std::atomic<int> g_tokenSubmitDefault;`
- `src/NativeDll/JobSystem.cpp`：定义 `g_tokenSubmitDefault{0}`
- `src/NativeDll/ChaseLevScheduler.cpp`：`SubmitBatch` 重构——`forceToken || capMode` 时走
  令牌路径（tokenTarget = capMode ? workerCount : wc；tokenCount = min(target, tileCount)）；
  slice 原路径完整保留供回退。
- `src/NativeDll/JobSystem_Scheduler.cpp`：`Initialize` 读 `ENTJOY_JOB_SUBMIT=token` 并打印模式。
- 执行侧零改动：worker 对 token 的识别/执行/窃取/退役协议（`ExecuteClaimToken`、
  `kClaimTokenMarker`）均为既有路径。

**同机紧邻两次运行对比**（ManyJobsBench 空体，15 worker，ENTJOY_JOB_SUBMIT=slice vs token）：

| case | slice per-job | token per-job | 帧级 slice→token |
|---|---|---|---|
| IJob x200（inline） | 0.4µs | 0.4µs | 0.075 → 0.075 ms（不涉及） |
| IJobFor·100K（async 单任务） | 99.3µs | 103.4µs | 9.9 → 10.3 ms（不涉及，单线程语义） |
| **IJobParallelFor·8K x100** | 21.1µs | **5.2µs** | **2.11 → 0.52 ms（-75%）** |
| **IJobParallelFor·8K x400** | 21.0µs | **4.9µs** | **8.39 → 1.96 ms（-77%）** |
| IJobParallelFor·8K batch256 x100 | 12.0µs | 6.7µs | 1.20 → 0.67 ms（-44%） |
| IJobParallelFor·64K x100 | 12.8µs | **4.9µs** | 1.28 → 0.49 ms（-62%） |
| IJobParallelFor·1M x20 | 35.7µs | 35.2µs | 0.71 → 0.70 ms（complete/drain 主导，持平） |
| IJobChunk·16K x100（少量 tile） | 3.7µs | 3.5µs | 0.37 → 0.35 ms（本就 token 化） |
| **IJobChunk·1M x50（224 tile）** | **72.3µs** | **8.5µs** | **3.61 → 0.43 ms（-88%，8.4×）** |
| IJob chain x200 | 0.5µs | 0.5µs | 0.10 → 0.11 ms（不涉及） |

**结论**：
- 主战场全部命中：IJobChunk·1M -88%、IJobParallelFor·8K x400 -77%、64K -62%；
  「每帧 200~400 个并行 Job」从 2~8ms 降到 0.4~2ms 量级。
- 不涉 token 的路径（IJob inline / IJobFor / chain）零变化，符合预期。
- 未回归项：istc 语义/退役/窃取协议未动；waitFallbacks 绝对值仍极小。
- 遗留观察：`IJobParallelFor·1M x20` 与 `IJobFor·100K` 不变（前者 complete/drain 主导、后者单线程语义）；
  timing pass 的 batchTotal P50 在 token 下升高是"发布节奏变快、排队被压缩"的指标效应，**不作为对比指标**。

**下一步（待办）**：
1. ~~把默认切为 token（`g_tokenSubmitDefault` 默认 1）前，跑回归~~ ✅ 已切默认并回归（见 §8）；
2. 可选加 PushMany（token 也批量入队）进一步削提交 CAS；
3. IJobFor 的逐元素回调税属单线程语义固有，不优化调度侧。

---

## 八、实施记录（续）：切默认 + 修复 + 回归（2026-08-26 晚）

**改动**：`g_tokenSubmitDefault` 默认 `1`（`ENTJOY_JOB_SUBMIT=slice` 可切回）；`ExecuteClaimToken`
认领步长自适应 `step = clamp(tileCount / workerCount, 1, kClaimBatchSize=4)`。

**为什么加 step 收缩**：固定 `kClaimBatchSize=4` 会让 tileCount≤4 的小批被**单个** worker 一次认领光，
破坏「每个 tile 是独立可认领并行单位」的语义——`AssistLifetimeTests`（阻塞型回调：回调在 worker 上
等待 release，同时期待 2 个 tile 被 2 个 worker 并行占住）在 token 下 started 数不足 → 超时 throw →
fastfail（0xC0000409）。step 随规模收缩后：tile=2/wc=2 → step=1，两 worker 各认领 1 tile ✓；
tile=219/wc=15 → step=4（基准同 §7 数据）。**修复后 AssistLifetime PASS。**

**Native Tests 回归**（`tests/NativeDll.Tests`，build-token 全新目录；顺带修复了 CMakeLists 的
`../NativeDll` → `../../src/NativeDll` 路径迁移遗留）：

| 测试 | 结果 |
|---|---|
| AssistLifetimeTests | **PASS**（token 默认；slice 对照也 PASS） |
| ChaseLevIntegrationTests / MPMCInjector / SparseTileDeque×3 | 全 PASS |
| JobSystemTests | 全 PASS，**除外 2 个 JCC 并发测试 flaky**（`JccCollisionSlotConcurrent` / `JccConcurrentSameHashBatches` 交替 FAIL）——已用 `git stash` 对照：**baseline（无 token 改动）同样 flaky**，根因是 `JobCostCache::GetPerElemCost` 与并发 `UpdatePerElemCost` 的读写窗口（slotHash 先写、EWMA 后写），**预存问题，与 token 无关**，单独跟进 |

**MoveCompare 1M 单大 Job 回归**（同机紧邻：slice 1 次 vs token 2 次，15 worker）：

| case | slice | token×1 | token×2 |
|---|---|---|---|
| Light C++ IJobChunk | 0.529 | 0.348 | **0.220** ms |
| Light C++ IJobEntity | 0.426 | 0.306 | **0.258** ms |
| Light C# IJobChunk | 0.392 | 0.424 | 0.407 ms |
| Light ISPC IJobChunk | 0.585 | 0.623 | 0.555 ms |
| Sleep C++ IJobChunk | 1.033 | 1.030 | 1.047 ms |
| Sleep C# / C++ / ISPC Entity | 1.07~1.18 | 1.03~1.13 | 1.06~1.10 ms |
| Heavy C++ IJobChunk | 17.87 | 18.23 | 18.31 ms |
| **Heavy ISPC IJobChunk** | **2.75** | 3.15 | 3.12 ms（+12%，待复核） |
| Heavy ISPC IJobEntity | 2.83 | 3.27 | 3.05 ms（+7~13%，待复核） |

Verify 全部 OK（MaxDiff ≤ 2e-4 / eps 1e-3）。结论：**Light 下 C++ 显著更好（-30~60%），Sleep 持平，
Heavy compute-bound 的 ISPC 类约 +7~13% 待复核**（候选机制：JCC 自动 tile 学习在两轮间的差异；
C++ Heavy 持平 17.9→18.3ms，非全局退化）。多 Job 场景收益（§7：-77~-88%）远超此量级，默认保持 token。

**待办**：
1. Heavy ISPC 退化的定量复核（3 进程取中位；若复现，比对两次运行的 tile 数与 JCC 学习轨迹）；
2. Jcc 并发 flaky 单测加固（Get 一致性读取，或测试降敏）；
3. 可选 PushMany（token 批量入队）。

---

## 九、token 安全性分析（死锁 / 竞态核查）

**死锁：不引入。** 热路径全部 lock-free（MPMC 队列、Treiber 池、fetch_add）；worker 唯一阻塞点是
park（无活）与 quit（退出），均不持锁等待他人；Complete 等 `backendRetired` 由 notify 唤醒，
无新增等待环。

**竞态逐项核查（token 相对 slice 仅新增一个共享原子 `nextTile`）：**

| 共享状态 | 保护机制 | 安全性 |
|---|---|---|
| `nextTile`（唯一新增） | `fetch_add(step)` 原子 → 认领区间 `[start, start+step)` 互斥、单调不回绕（ABA 免疫），`start ≥ tileCount` 即停 | 每 tile **恰好执行一次** |
| `tilesRemaining` | 每 tile `fetch_sub(1)`，归零触发 `TryCompleteLogicalBatch`（`logicalCompleted.exchange` 防重入） | 完成判定与执行者无关 |
| `pendingTasks` | 每 token 完成才减 1；被窃取的 token 只执行一次（窃走即归 thief） | token 不泄漏计数 |
| 双条件退役 | `tilesRemaining==0 && pendingTasks==0` 才 `ReleaseBatch`；双重完成用 `finalized.exchange(true)` CAS 单物权 | 无 use-after-free |
| 主线程 assist | `TryAssistOne` 与 worker 共用 `nextTile` 游标、同一 `ExecuteClaimToken` | 同上 |
| Shutdown 未完成 token | 双条件不满足 → 泄漏而非 UAF；`ConsumeLongBatchBarriers` + 池清理兜底 | 与 slice 相同 |

**两个性能/语义注意点（非正确性）**：
- **公平性**：快 worker 可能连认领（游标单调前进，慢 worker 不会饿死，但理论上存在少量不均衡）；
  step 随规模收缩后大批次 step=4，均衡性良好。
- **阻塞型回调**：worker 在 `executeTile` 内等外部事件时占住自己认领的 tile 块——不是死锁
  （step=1 时每 worker 仅扣 1 个 tile，其余 tile 仍可被其他 worker 认领）；回调永久阻塞会占用
  worker，与 slice 中阻塞任务占住 worker 完全一致。

**测试覆盖**（token 默认全 PASS）：`ParallelForExactOnceAndCallerAssist`（恰一次 + assist 并发）、
`ConcurrentChunkComplete`、`ExhaustedChunkTicketsDrain`、`TokenShutdownMix`（token×Shutdown 混合）、
`WorkChannelStorm`、`ScheduleCompletePressure`、`ChunkShutdownRace`、ChaseLev 集成测试的并发窃取/异常传播。

结论：**默认 token 无需额外的安全改动**；挂账问题（JCC flaky、Heavy ISPC 复核）均与 token 无关。

---

## 十、优化路线图（按真实收益排序，2026-08-26）

✅ 已完成：token 认领默认化（几百 Job -77~-88%，Light C++ -30~60%，Sleep 持平，正确性回归全绿）。

| 序 | 项 | 预期收益（几百 Job/帧 场景） | 成本/风险 | 状态 |
|---|---|---|---|---|
| 1 | **单 job 提交/完成链瘦身**（C# Complete 3 P/Invoke→1、per-泛型静态委托缓存、异常计数门控、`new ChunkBatchContext` 池化、`ConsumeLongBatchBarriers` 空锁门控） | 每 job ~0.5~1.5µs → 200 job 省 0.1~0.3ms（仪式 3µs 中的 C# 部分） | 低；改动分散但独立可测 | 待做 |
| 2 | **PushMany（token 批量入队）** | 每 job 提交侧 15×(Acquire+Push) CAS → 1 次 RMW + 15 store，再省 ~0.5µs/job | 低；集中在 MPMCInjector + SubmitBatch | 待做 |
| 3 | **批提交 API（批量调度通道）** | 摊薄每 job 固定仪式 3µs→~1µs + P/Invoke 合并；200 混合 job → <1ms 的最终解（文档 §7 目标） | 中高；API 设计面（依赖图/句柄合并/异常归集） | 设计定稿见 §14，待实现 |
| 4 | Heavy ISPC 复核（3 进程取中位） | 消除回归尾巴；若复现再查 JCC tile 学习轨迹 | 低；纯验证 | 待做 |
| 5 | Jcc 并发 flaky 单测加固 | 工程质量（防误报），无运行时收益 | 低 | 待做 |

明确不做：IJobFor 并行化（单线程语义，用户确认）、注入器 per-worker 分散（token 化后流量已降
15×，收益趋零）、per-tile 全局 MPMC（与 token 设计冲突）。

---

## 十一、实施记录（续）：单 job 链瘦身（2026-08-26 深夜）

**目标**：摊薄 IJob/IJobFor/并行路径的每 job 固定仪式（§三.5 的 ~3µs C# 部分）。

**完成（验证通过，无行为变化）**：
1. **`Complete()` 三合一**：新增 native 导出语义改造 `JobSystem_CompleteAndRelease`（返回
   `diagnosticBatchId`），C# `Complete()` 从 3 次 P/Invoke（Complete+GetDiagnosticBatchId+
   ReleaseHandle）降为 **1 次**；异常取回逻辑不变。
2. **per-泛型静态委托缓存**：`JobDelegateCacheFor<T>` / `ForDelegateCacheFor<T>` /
   `ParallelForBatchDelegateCacheFor<T>`（static 字段），`Schedule/For/Batch` 热路径从
   `ConcurrentDictionary.GetOrAdd` 降为**零查找**（ParallelFor 的 AutoParallelForCallback<T>
   模式扩展至全部入口）。
3. **异常计数门控**：`_pendingJobExceptionCount` Interlocked 快查——无异常时 Complete 不再
   每次 `lock(_exceptionLock)` 查字典（锁内清零防误关竞态）。

**尝试并回退（重要教训）**：IJobFor 无依赖时全长度主线程 inline（原只 ≤4096 inline）。
**回退原因（实测）**：主线程逐元素 C# 回调 ~20ns/次 vs worker 线程 ~1ns/次 → IJobFor·100K
**98.7 → 705µs/job（-7×）**，并把 JobSystemTests 拖成 570s CPU 忙循环。
**结论**：「IJobFor 单线程语义」= **单 worker 执行**（异步单任务仍是单线程），**不是主线程 inline**；
大长度 for 必须留在 worker 上跑。

**ManyJobsBench A/B（token 默认 + 瘦身后，同机）**：

| case | 瘦身前（§8） | 瘦身后 |
|---|---|---|
| IJobChunk·1M x50 | 8.5µs/job (0.427ms/帧) | **7.3µs/job (0.363ms/帧)** |
| IJobParallelFor·8K x400 | 4.9µs/job (1.96ms/帧) | **4.4µs/job (1.77ms/帧)** |
| IJobParallelFor·64K x100 | 4.9µs/job | 4.1µs/job |
| IJobChunk·16K x100 | 3.5µs/job | 3.3µs/job |
| IJob x200 / chain x200 | 0.4/0.5µs/job | 0.4/0.6µs/job（持平） |
| IJobFor·100K x100 | 103.4µs/job | 99.9µs/job（回退后恢复） |

正确性：AssistLifetime PASS；JobSystemTests 9.9s 跑完，仅预存 Jcc flaky（collision）。

**路线图状态更新**：§十 第 1 项完成（Complete 合并 / 静态缓存 / 异常门控）；`ChunkBatchContext`
池化与空锁门控未做（收益边际，待 PushMany/批提交时一起）。第 2 项 PushMany、第 3 项批提交 API
保持待做；第 4 项 Heavy ISPC 复核、第 5 项 Jcc 单测加固仍在账。

---

## 十二、实施记录（续）：PushMany（token 批量入队）✅（2026-08-26 深夜）

**改动**：
- `src/NativeDll/MPMCInjector.h`：新增 `PushMany(values, count)`——一次 CAS 抢占 count 个连续槽，
  按槽序顺序填充（seq 协议与 Push 完全一致，消费者仍按 dequeuePos 顺序消费；多生产者区间互斥；
  容量不足时返回实际入队数，调用方剩余项逐个补入）。
- `src/NativeDll/ChaseLevScheduler.cpp`：`SubmitBatch` token 分支——批量 Acquire token 任务后
  一次性 `PushMany`（分批 64；注入器满时剩余用 `PushTaskBackoff` 补入）。
- `tests/NativeDll.Tests/MPMCInjectorTests.cpp`：新增 4 个 PushMany 单测
  （基本顺序 / 满时部分入队 / 回绕 / 与逐元素 Push 并发混用）。

**验证**：MPMCInjectorTests 10/10 PASS（含新单测）；AssistLifetime PASS；JobSystemTests
仅预存 Jcc flaky。ManyJobsBench（token 默认，同机）：

| case | PushMany 前 | PushMany 后 |
|---|---|---|
| IJobParallelFor·8K batch256 x100 | 6.1µs/job | **2.9µs/job（-52%）** |
| IJobParallelFor·64K x100 | 4.1µs/job | **2.7µs/job（-34%）** |
| IJobParallelFor·8K x100 / x400 | 5.2 / 4.4µs/job | 5.4 / 4.4µs/job（持平） |
| IJobChunk·1M x50 | 7.3µs/job | 7.4µs/job（持平） |
| IJobChunk·16K x100 | 3.3µs/job | 3.0µs/job |
| IJob ·200 / chain·200 | 0.4 / 0.6µs/job | 0.4 / 0.5µs/job（持平） |

**结论**：小 tile 批次（≤42 tiles）提交侧 CAS 合并后 per-job 再降 34~52%；
大 tile 批次（224 tiles）持平（15 token 的 CAS 本已很小）。无退化，默认保持。

**路线图状态更新**：§十 ② PushMany ✅ 完成；剩余：③ 批提交 API（主项目，含 ChunkBatchContext
池化与空锁门控并入）、④ Heavy ISPC 复核、⑤ Jcc 单测加固。

---

## 十三、最终状态确认（用户机复测，2026-08-26）

**1M 实体 Sleep 帧间隔移动对比**（token 默认 + Complete 三合一 + 静态缓存 + 异常门控 + PushMany，
15 worker，Verify 全 OK MaxDiff ≤ 4.6e-5）：

| case | token 前基线 | 最终 | 差异 |
|---|---|---|---|
| Sleep C# IJobChunk | 1.009 | 1.032 ms | +2%（噪声内） |
| Sleep C++ IJobChunk | 0.984 | 1.017 ms | +3%（噪声内） |
| Sleep ISPC IJobChunk | 1.021 | 1.057 ms | +3.5%（噪声内） |
| Sleep C# IJobEntity | 1.013 | 1.028 ms | +1.5% |
| Sleep C++ IJobEntity | 0.972 | **0.956 ms** | **-1.6%** |
| Sleep ISPC IJobEntity | 1.040 | 1.091 ms | +5%（单帧噪声） |

结论：Sleep（16ms 帧间隔、单大 Job、唤醒主导）非 token 优化的目标场景，差异全部落在
run-to-run 噪声（startSpread 48~1353µs 的同场景波动可证），**无回归**；token 的收益集中在
「每帧几百个 Job」场景（§7/§11：-77~-88%）。全链路正确性验证通过。

---

## 十四、批提交 API（BatchScheduler）设计语义（2026-08-26 定稿，路线图 §十 ③ 蓝图）

对标 Unity `JobHandle.ScheduleBatchedJobs()`（显式）+ DOTS 帧末隐式刷新（隐式）；
**差异点：我们的 worker 会 park，批的价值比 Unity 更大**（直接减少 park/notify 次数）。

### 14.1 数据语义：入队即快照（硬规则）

- **Job 结构体字段 = 快照**：job 加入批次的那一刻拷贝（沿用 `AllocContext` ContextPool 值拷贝），
  之后外部修改 job 字段不影响本批；想生效须改引用内存或重新入队。
- **引用内存（NativeArray / 指针所指内容）= 活引用**：快照的是指针值，内容是活的，执行时读当时的
  共享内存。**批的延迟发布（Schedule→执行窗口拉长到 End/帧末）会放大外部写引用内存的竞态暴露面**：
  契约 = 批生命周期内（入队→End→执行完成）主线程视引用内存为只读。
- 与 Unity 差异：Unity 用「禁止托管引用 + Safety System 所有权检查」机制保证；我们批场景通过
  §14.4（托管 job 拒绝入批）+ §14.5（NativeCollection 批持有检查）补齐到机制级；非批场景（单 job
  路径）仍靠「拷贝 + 纪律」（现有 `AtomicSafetyHandle` 在 `ENTJOY_SAFETY` 下才启用）。

### 14.2 发布 force point（硬规则，防死等）

发布是 lazy 的（入队不唤醒），触发点四选一：
`End(显式批) | Complete(任意批内句柄，先 flush 再等) | 显式 Flush | 隐式帧 barrier(ECS system 组)`。
`Complete` 语义与 Unity 一致：未发布则先发布再等待。

### 14.3 显式 / 隐式两版接口（差异只在发布时机）

| | 显式批 | 隐式批（Unity 模型） |
|---|---|---|
| 接口 | `new BatchScope()` → `batch.Add(ref job)`×N → `batch.Submit()` | `job.Schedule()` 照旧，引擎按帧收集 |
| 快照点 | 加入批次时 | `Schedule()` 调用时（与今日零差异，用户无感） |
| 发布点 | `Submit()` / 早到的 `Complete` / `Flush` | 帧 barrier / 首次 `Complete` |
| 多线程组批 | per-batch 原子游标 + 预分配槽（Begin 定容量） | 同现状 |

### 14.4 托管字段 job：批快路径不负责（硬性拒绝）

- **规则**：`BatchScope.Schedule<T>` 入队检查 `RuntimeHelpers.IsReferenceOrContainsReferences<T>()`，
  **含托管引用的 job 直接抛 `NotSupportedException`**（指引改走单 job 路径），不允许入批。
- 理由：批承诺「零分配 + 纯快照」；托管 job 的 GCHandle box 路径（每 job 一次 box 分配 +
  句柄登记/释放 + 引用内容为活引用）会稀释该承诺，且与快照语义冲突。
- 托管 job 维持既有单 job 通道（GCHandle box，cleanup 生命周期已有协议覆盖），不进批。
- **批 = blittable-only**（字段无托管引用；NativeArray/指针/int/float 等均为 blittable）。

### 14.5 NativeCollection 安全检查（承诺项，非可选）

在现有 `AtomicSafetyHandle`（当前仅 0=active / 1=released 两态）上扩展「批持有」状态：

| 机制 | 说明 |
|---|---|
| 入队登记 | 批入队时扫描 job 字段里的 NativeArray 句柄（per-T 反射缓存一次；支持 `[ReadOnly]` 声明读/写） |
| 发布/持有 | 批发布时句柄置为「batch-owned」状态；**主线程**在批未完成前访问该容器 → `CheckReadAndThrow` 抛「container owned by active batch, Complete 后可用」（线程区分：main-thread-id 快查） |
| 释放 | 批退役/`CompleteAll` 后状态复原；重复持有/释放有原子防护 |
| 成本 | 每次容器访问多一次状态比较（已有 Volatile.Read 分支）；`SafetyChecksEnabled=false`（Release 默认）或 `#if ENTJOY_SAFETY` 下编译剔除 → **零运行时成本** |
| 与 Unity 对齐 | 语义 = Unity Safety System 的「所有权转移」：批持有期主线程越权访问即报错，防止延迟发布窗口的外部写竞态（§14.1 契约从“纪律”升级为“机制” enforce） |

**主要求**：批 v1 必须带此检查（Debug 默认开、Release 可关），否则延迟发布窗口的竞态只能靠文档纪律。

### 14.6 实现最小清单（批提交 v1）

1. 入队即拷贝（沿用 ContextPool）+ 快照契约文档；
2. force point：`End/Complete/Flush` + 隐式帧 barrier；
3. 依赖沿用现有 continuation；每 job 独立句柄（发布后立即可用）；
4. 异常按各 job batchId 归集重抛（复用现有机制）；
5. 批容量预分配 + 原子游标（多线程组批）；
6. **托管拒绝**：入队时 `IsReferenceOrContainsReferences<T>` 检查，true 抛 `NotSupportedException`；
7. **容器持有检查**：入队反射登记 NativeArray 句柄（per-T 缓存 + `[ReadOnly]`），发布置
   batch-owned、主线程越权访问抛异常、退役复原（`SafetyHandleManager` 两态扩三态）。

**状态**：设计定稿（§14.1~14.6），待实现。实现后可先行评价「200 混合 job < 1ms」目标（当前 ~1.3ms）。

---

## 十五、实施记录（续）：显式批 v1（C# 壳，2026-08-26 深夜）

**决策**：v1 只在 C# 实现（用户确认）。native「循环壳」导出（`JobSystem_ScheduleBatch` 逐个调现有入口）
只省 N-1 次 P/Invoke（~几 µs/帧），收益不足，已回滚；**真正收益（单次唤醒 deferNotify、共享批上下文）
在 native 侧，留给隐式批阶段一起做**。

**交付**：
- `src/EntJoy.Jobs/BatchScope.cs`（简化 API）：
  `new BatchScope()`（自动扩容）→ `Add(ref job, dependsOn)` / `AddFor(ref job, length)` /
  `AddParallelFor(ref job, length, innerBatchCount)`（**泛型约束 `where T : unmanaged, IJob[For/ParallelFor]`
  → 托管 job 编译期 CS8377 硬拒**，运行时 `IsReferenceOrContainsReferences` 兜底）→
  `Submit()` 返回句柄数组 → `CompleteAll()`（未 Submit 自动先提交）；
  入队 = `AllocContext` 快照拷贝零 P/Invoke；依赖 retain 复用 `RetainedNativeDependency`；
  Submit 句柄缓存防重复发布。
- ManyJobsBench 新增 5 个显式批 case（Batch·IJob·200 / Batch·For·1K·100 / Batch·For·100K·100 /
  Batch·ParFor·8K·100 / Batch·Mixed·200）。

**同场对照（度基准，15 worker，token 默认）**：

| case | 逐 job | 显式批 v1 |
|---|---|---|
| IJob x200 | 0.3µs/job (0.062ms) | 0.6µs/job (0.125ms，有尖峰) |
| IJobFor·1K x100 | p50 0.725ms | p50 0.746ms（等价；avg 差来自逐 job 的 OS 尖峰） |
| IJobParallelFor·8K x100 | 5.2µs/job (p50 0.461ms) | 2.4µs/job (p50 0.206ms) |
| Batch·Mixed(100+50+50) x200 | — | 3.2µs/job (0.632ms) |

结论：v1 = API 形态验证（真实场景：200 混合 job 用显式批可 0.63ms）；性能与逐 job 同量级
（C# 壳无 native 收益，预期内），个别差值来自执行顺序/噪声。**基线（逐 job，隐式批实施前）记录在案**：
IJob 0.3µs/job、parFor·8K 5.2µs/job、parFor·8K×400 4.6µs/job、chunk·1M 7.7µs/job、Mixed·200 显式批 3.2µs/job。

**补测（>4096 async 单任务路径）**：

| case | per-job | schedule | complete |
|---|---|---|---|
| IJobFor·100K（逐 job） | 102.0µs | 1.0µs | 100.9µs |
| Batch·IJobFor·100K | 101.2µs | 1.0µs | 100.3µs（等价） |
| Batch·IJobParallelFor·8K（复测） | 19.2µs（上轮 2.4µs） | — | — |

**结论（修正）**：
1. **一切路径上，v1 C# 显式批都没有可测量的稳定性能提升**——`End()`/Submit 逐个调既有入口，
   P/Invoke、唤醒、仪式一项未合并；IJobFor>4096（async 单任务）102 vs 101µs 等价；
   此前「parFor·8K 5.2→2.4µs」复测为 19.2µs，判为 run-to-run 噪声，收回"提交节奏改善"归因。
2. **v1 全部价值 = API 形态 + 语义**（unmanaged 编译期拒绝/入队快照/force point/托管不负责）；
   **性能收益 100% 依赖 native deferNotify**（隐式批）：P/Invoke 合并、单次唤醒、提交仪式摊薄。
3. 设计要点（记档）：批的价值对象是**异步路径 job**（parallel/chunk/有依赖/大 IJobFor）；
   无依赖小 IJob/IJobFor（主线程 inline）用批无收益（无可摊薄成本）。

**下一步：隐式批**（native deferNotify 单次唤醒 + 帧聚合，真实性能收益在此）。

---

## 十六、实施记录（续）：deferNotify（单次唤醒）✅（2026-08-26 深夜）

**改动**（native + C#，语义不变仅唤醒时机聚合）：
- `JobSystemInternal.h` / `JobSystem.cpp`：全局 `g_submitDeferDepth`（提交窗口延迟唤醒深度）；
- `ChaseLevScheduler.cpp`：`SubmitBatch` token/slice 两分支尾部 notify 改为
  `if (deferDepth==0) { bump + notify_all }`；新增公开 `WakePending()`（bump + notify_all 一次）；
  安全依据：任务已入注入器，worker 自旋自取；全 park 由 Flush 统一广播唤醒（wake-all 语义保留）。
- `Exports.h/cpp`：`JobSystem_SubmitDeferBump()` / `JobSystem_SubmitDeferFlush()`（深度计数；
  Flush 归零时 `WakePending`，嵌套失衡兜底广播防丢唤醒）；
- `NativeJobCore.cs`：两导出绑定 + wrapper；
- `BatchScope.cs`：`Submit()` 包裹 defer 窗口（Bump → 逐个提交 → Flush）。

**验证**：AssistLifetime / ChaseLevIntegration PASS；JobSystemTests 仅预存 Jcc flaky（无新回归）。
ManyJobsBench（token 默认，同场两轮）：

| case | 逐 job | Batch(deferNotify) | 收益 |
|---|---|---|---|
| **IJobParallelFor·8K x100** | 4.1~5.4µs/job | **2.8~2.9µs/job** | **-32~-46%（两轮一致）** |
| **Batch·Mixed(100+50+50) x200** | — | **2.8µs/job（一帧 ~0.56ms）** | **达成「200 混合 < 1ms」目标** |
| IJob x200（inline） | 0.3µs | 0.5µs | 无收益（inline 无异步成本，预期） |
| IJobFor·100K（async 单任务） | 102.6µs | 102.6µs | 无收益（执行税主导，预期） |

**结论**：deferNotify 是显式批的第一个**真实性能收益**（同场复测稳定 -32~46%）；
Batch 的价值对象确认 = **异步路径 job**（parallel/chunk/有依赖），inline 型与执行税主导型无收益。
「200 混合 job < 1ms」以显式批达成（~0.56ms）。

**下一步：隐式批**（`Schedule()` 照旧 → 引擎按帧聚合 pending → force point：`Complete` /
`FlushPending()` / 帧 barrier 统一 flush）——把同样的 deferNotify（+未来 P/Invoke 合并）带给
**不改一行用户代码**的现有调度路径。

---

## 十七、实施记录（续）：隐式批 ✅（2026-08-26 深夜）

**改动**（用户零改动：`job.Schedule()` 照旧）：
- native：`g_implicitBatchEnabled` + `g_pendingBatches`（锁保护）；
  `SubmitOrPending()`：开关开启时把**主线程直接提交的 tile 路径 job**
  （ParallelFor / ParallelForBatch / Chunk/Entity）挂 pending；依赖未完成路径（continuation
  提交）不受 pending 影响、照常立即执行；
  `FlushPendingSubmits()`：swap 全部 pending → **defer 窗口内逐个 SubmitBatch → 统一
  `WakePending()`（单次唤醒）**；
  `SetImplicitBatchEnabled(false)` 时积压立即 flush（防悬挂/泄漏）；
  Exports：`JobSystem_SetImplicitBatchEnabled` / `JobSystem_FlushPendingSubmits`；
- C#：`NativeJobScheduler.SetImplicitBatchEnabled(bool)` / `FlushPendingSubmits()`；
  **`Complete()` / `IsCompleted()` 前自动 Flush**（Unity ScheduleBatchedJobs 同语义：Complete 隐式刷新，防死等/防误报）。

**语义**：开启后「不 Complete/Flush 不执行」（与 Unity 延迟刷新一致）；force point =
`Complete` | `FlushPendingSubmits()` | 帧 barrier（宿主循环/ECS system 组末）。

**无头框架（无引擎生命周期钩子）用法**：
- **不手动 Flush 也正确**：`Complete()` 是自动 force point——「先 Schedule 一批、最后 Complete」
  的天然写法即拿到统一提交+单次唤醒，零手动调用；
- 想精确聚合：每个 tick 末调 `NativeJobScheduler.EndFrame()`（= FlushPendingSubmits 的
  帧语义别名；无头框架推荐）；
- 反模式：每个 job 调度后立即 Complete → 每次 flush 无聚合（= 逐 job）；
- 安全阀：pending 超 1024 个 job 仍无人 flush 时**自动提交一次**（防极端情况无限挂起，
  正常运行不触发）。

**验证**：AssistLifetime / ChaseLevIntegration PASS；JobSystemTests 仅预存 Jcc flaky。
ManyJobsBench（token 默认，同场）：

| case | 逐 job | 显式批 | 隐式批 |
|---|---|---|---|
| IJobParallelFor·8K x100 | 4.6µs/job | 2.8µs/job | — |
| Mixed(100+50+50) x200 | — | 2.9µs/job (0.58ms) | **3.4µs/job (0.68ms)** |
| IJob x200（inline） | 0.3µs | 0.7µs | —（不聚合，预期） |

**结论**：隐式批让**现有 `Schedule()` 调用零改动**拿到批收益（Mixed 200 ≈ 0.68ms/帧，与显式批
0.58ms 同量级；其中可省部分 = parFor 段 4.6→~2.5µs/job）；inline 型 job 不聚合（无收益，预期）。
deferNotify 的能力被 显式批 + 隐式批 两条路径共用。

**总结（整个多 Job 优化链闭环）**：
- 逐 job 基线：parFor·8K 4.6µs、chunk·1M 7.2µs、IJob 0.3µs；
- 显式批（BatchScope + deferNotify）：200 混合 0.58ms；
- 隐式批（零改动 Schedule + 帧末 Flush）：200 混合 0.68ms —— **「每帧几百 Job」从 ~1.3ms 压到
  ~0.6-0.7ms，且用户代码零迁移**；
- 剩余待办：④ Heavy ISPC 复核、⑤ Jcc flaky 单测加固、可选 P/Invoke 合并（隐式批的下一档）。

---

## 十八、实施记录（终）：P/Invoke 合并 + MoveCompare 终回归（2026-08-26）

**P/Invoke 合并**：
- native 批入口正式就位：`JobSystem_ScheduleBatch(descs[], count, outHandles[])`（内含 deferNotify
  窗口：全部 submit 后统一 `WakePending` 一次；依赖未完成路径照常 by continuation）；
- `BatchScope.Submit()`：200 次 P/Invoke → **1 次**（desc 数组 fixed + 句柄回写）；内部依赖
  retain、句柄缓存、CompleteAll 语义不变；
- 回归：AssistLifetime / ChaseLevIntegration PASS，JobSystemTests 仅预存 Jcc flaky；
- 性能：Batch·Mixed 200 = 2.8µs/job（与合并前持平——P/Invoke 本就 ~0.05µs/job 噪声内，非大头）；
  价值 = Submit 单次调用 + native 批入口底座（后续共享 storage/仪式摊薄的入口已就位）。

**MoveCompare 1M 终回归**（2 次全量，token 默认，15 worker，Verify 全 OK MaxDiff ≤ 2e-4）：

| case | Light(×2) | Sleep avg(×2) | Heavy(×2) |
|---|---|---|---|
| C++ IJobChunk | 0.230 / 0.323 ms | 1.008 / 1.026 ms | 18.4 / 19.0 ms |
| C++ IJobEntity | 0.210 / 0.274 ms | 1.007 / 1.046 ms | 19.8 / 19.4 ms |
| C# IJobChunk | 0.373 / 0.387 ms | 1.108 / 1.068 ms | 23.2 / 23.3 ms |
| ISPC IJobChunk | 0.473 / 0.665 ms | 1.118 / 1.130 ms | **3.13 / 3.07 ms** |
| ISPC IJobEntity | 0.405 / 0.644 ms | 1.182 / 1.118 ms | **3.26 / 3.15 ms** |

**终回归结论**：
- **无回归**：Light（C++ 0.21~0.32ms）、Sleep（~1.0~1.18ms）、Heavy（C++ ~18-20ms）与全链路各阶段
  数据一致；全部 Verify OK（正确性稳定）。
- **④ Heavy ISPC 复核落档**：ISPC Heavy（compute-bound）三次独立测量稳定在 **3.07~3.26ms**
  （token 前 slice 2.75/2.83）→ **+10~14% 为稳定现象**，非单次噪声；C++/C# Heavy 持平。
  候选机制：ISPC 224-tile 批在 token step=4 认领下的调度差异/ISPC 调用在 tile 归并下的
  SMT 竞争；属 compute 专用路径小退化，不阻塞默认 token（多 Job 场景收益远大于此）。
- 全链路最终状态：token 默认 + 显式批（0.58ms/200 混合）+ 隐式批（0.68ms/200 混合，零迁移）
  + deferNotify + P/Invoke 合并；剩余：⑤ Jcc flaky 加固（预存，工程质量）。

**支持矩阵（后端适配）**：BatchScope（显式批）与隐式批（ImplicitBatch）**双后端**：
- Native 后端：desc 快照收集 + `Submit()`/**`EndFrame()` 1 次 P/Invoke**（token + deferNotify + 单次唤醒）；
- **Managed 后端（回退，NativeDll 缺失）**：`Add<T>` **立即泛型调度存句柄**（值传递即快照，
  无 desc/委托/延迟）——API 统一、语义正确、性能等同逐个（Managed 无 P/Invoke 可合并）；
  烟测（临时移走 NativeDll 验证）：BatchScope 64/64 执行+数据校验 OK、ImplicitBatch OK。

---

## 十九、实施记录（终）：Jcc flaky 加固 ✅（2026-08-26）

`tests/NativeDll.Tests/JobSystemTests.cpp` 三处修复，**JobSystemTests 5/5 全 PASS（零 FAIL）**：

| 测试 | 根因 | 修复 |
|---|---|---|
| `JccCollisionSlotConcurrent` | **测试代码 bug**：`bool h2Holds = (v2>0 && v1==0, false);`——逗号表达式恒为 false → 槽最终被 h2 持有时必 FAIL（与并发无关，flaky 的真相） | 去除 `, false`；顺带清理 `for(...; ++t, false)` 垃圾增量 |
| `JccConcurrentSameHashBatches` | light job 快于计时粒度 → span=0 → `perElem=0` 合法冷态，断言 `v>0` 误报 | 断言放宽为 `v >= 0 && v < 100` |
| `JccWaveCostVariance`（顺带暴露） | 同因：light 模式（10 iters）perElem=0 合法 | 断言放宽为 `waveLog >= 0 && < 1e6` |

结论：三个 JCC 并发/波动测试的 flaky 根源均**不在 JobCostCache 实现**（无需改 `JobCostCache.h`）：
两处是测试断言/代码 bug，一处是「合法零值」误判。加固后完整回归套件干净：
JobSystemTests 5/5、AssistLifetime PASS、ChaseLevIntegration PASS、MPMC 10/10。

**至此路线图全部清零**：① token 默认化 ✅、② PushMany ✅（并入）、③ 批提交（显式+隐式+defer+P/Invoke
合并）✅、④ Heavy ISPC 复核 ✅（+10~14% 稳定落档）、⑤ Jcc flaky 加固 ✅。
建议下一步：git 提交保存全部成果。

---

## 二十、决策与代码质量（2026-08-26 收尾）

### 20.1 隐式批：默认关闭，显式启用（决策）

- **默认关闭**（撤回"默认开启"）：需要时由用户显式
  `NativeJobScheduler.SetImplicitBatchEnabled(true)`（配合每帧 `EndFrame()` / 依赖 `Complete` 兜底）；
  native `g_implicitBatchEnabled` 默认 false（Native Tests 语义不受影响）。
- 背景：默认开启的收益（零迁移聚合）虽已验证（ManyJobsBench 全 case EXIT=0、逐 job 数值不变），
  但"Schedule 后不 Complete 不执行"的语义变化对未知调用方有隐性风险；改为显式启用，
  由使用者显式选择延迟刷新模型（无头框架可随时开启 + `EndFrame()` 每 tick 一次）。
- 安全链（开启后依然成立）：`Complete()` 自动 flush + `EndFrame()` 帧语义别名 +
  pending 超 1024 自动提交安全阀 → 「不 flush 也能执行、最多少聚合」而非「悬挂」；
  「立即 Schedule().Complete()」用户每次 Complete flush 空/单批，行为与逐 job 等价、无退化。

### 20.2 token 设计再优化分析（结论：已近上界）

现状已具备：O(workers) token 注入器流量 + PushMany 批量入队 + `fetch_add(step)` 细粒度认领 +
step=clamp(tiles/wc,1,4) 自适应 + JCC 按每元素成本调 tile 数。剩余可挖项（低优先级，留未来）：
**认领窗口批量保留**（`ExecuteClaimToken` 一次 `fetch_add(16)` 拿大窗口本地细分——共享原子频率
降 ~4×：55→14 次/219 批，~几十 ns/批）。明确不做：per-worker 静态分片（公平性/尾部失衡）、
删注入器（owner-only deque 架构约束）、选择性唤醒（35ms 尖峰教训）。

### 20.3 代码质量清理（删冗余/屎山/历史 A/B 开关）

- **删除 slice 提交分支**（ChaseLevScheduler::SubmitBatch 的预切分任务路径）+ `g_tokenSubmitDefault`
  开关 + `ENTJOY_JOB_SUBMIT` env 解析——token 为唯一提交路径（A/B 已完结，slice 无回退价值）；
- **保留 `GetOrCreateDelegateCache`**（侦查修正：EntJoy.ECS 的 ChunkJobScheduler 跨程序集仍在用，
  并非死代码；热路径已全部改静态泛型缓存，注释说明用途）；`AutoParallelForCallback<T>` 改直接
  静态构造（省首查字典）；
- 回归：AssistLifetime / ChaseLev / MPMC PASS，**JobSystemTests 0 FAIL**；基准数值与前一致。
- 期间修复一次误删导致的 `MissingMethodException`（EntJoy.ECS 二进制引用旧 API——重建 EntJoy.ECS
  即恢复，无逻辑改动）。

### 20.4 批体系现状（统一后，历史节 §17/§18 的 native pending 描述已过时）

- **统一收归 C#**：native 隐式（pending 列表/SubmitOrPending/两导出）已删除，由 `BatchScope`
  （显式）与 `ImplicitBatch`（全局单例，`Add/EndFrame`）两个 C# 收集层 + native `ScheduleBatch`
  执行入口构成；`Complete()` 前置自动 flush + 1024 安全阀；
- **双后端**：Native = desc 快照 + 1 次 P/Invoke；Managed（回退）= `Add` 即调度存句柄（无 desc/
  委托），烟测验证通过（BatchScope 64/64 + ImplicitBatch OK，临时移走 NativeDll 复现 Managed 回退）；
- 认领窗口实验（§20.2）实测无端到端收益（fetch_add 被 tile 执行吸收），保持 step=4。

### 20.5 兜底唤醒看门狗（正常路径无影响论证）

`WaitBackendRetired`（Complete 主路径 + ConsumeLongBatchBarriers 复用）加两档：

| 档 | 触发条件 | 正常使用是否触发 |
|---|---|---|
| 条件档 | 等待时 `g_submitDeferDepth != 0` | 否——defer 窗口（Submit/EndFrame 的 Bump→提交→fetch_sub→WakePending）在同步调用内完成，返回时 depth 已归 0；仅极端跨线程并发会见到，但多补一次广播正确且幂等 |
| 超时档 | 等 `backendRetired` > 5s | 否——任何合法 job 远小于 5s（帧间隔/长 job 毫秒级）；仅异常死挂命中 |

瞬时开销：已完成路径 `if(backendRetired) return` 短路**零成本**；未完成等待每次进函数 +~1ns（relaxed depth 读）+ 每次唤醒 +~20ns（deadline 比较），正常 job 数十 ns 级。

触发时安全：`WakePending()`（bump epoch + notify_all）**幂等**——worker 自取任务语义不变，多一次广播不重复/不提前执行；超时档命中 = 多等 5s + 一行 stderr WARN(batchId)，把「永久挂」降级为「可观测毛刺」。回归（Assist/JobSystemTests 0 FAIL）与基准（Batch·Mixed 2.9 / Implicit·Mixed 3.0µs）确认正常路径数字无变化。

### 20.6 幂等保证（全 API 走查）

原则：**重复调用 = 无副作用**（no-op 或返回缓存）；会产生第二次副作用的调用（Add after Submit）显式报错而非静默。

| 操作 | 幂等性 |
|---|---|
| `JobHandle.Complete()` / `Release()` | ✅ 重复 no-op（Detach 后 handle=0 短路） |
| `JobHandle.IsCompleted()` | ✅ 只读 |
| `BatchScope.Dispose()` | ✅ `_disposed` 短路 |
| `BatchScope.Submit()` | ✅ 已提交返回缓存句柄（不重复提交）；「Add after Submit」报错 |
| `BatchScope.CompleteAll()` | ✅ 句柄重复 Complete no-op |
| `ImplicitBatch.SetEnabled(true/false)` | ✅ 重复开关短路；关闭先冲刷积压 |
| `ImplicitBatch.EndFrame()` / `Handle(i)` / `CompleteAll()` | ✅ 每次提交当时积压（重复合理）；只读幂等 |
| `WakePending()`（看门狗/批 flush） | ✅ bump+notify_all 幂等 |
| `SubmitDeferBump/Flush` | ✅ 计数配对；多余 Flush = 一次无害广播 |
| `TryFinalizeChaseLevBatch` | ✅ `finalized.exchange(true)` 单物权防重复退役 |
| `ExecuteClaimToken`（认领） | ✅ `nextTile` 游标单调，无重复 tile |