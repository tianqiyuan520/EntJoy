# JobSystem 帧间隔调度开销分析（2026-08-26）

> 背景：Godot `SpritesRandomMove`（100 万实体、C++ IJobChunk，`Schedule()+Complete()` 每帧一次）
> 每帧平均耗时 ~1.0ms（运算地板 0.71-0.87ms），Unity 对标 0.95ms。本文通过分阶段计时定位
> 调度侧开销来源，验证 tile 缓存方案，并分析与 Unity 的差距。
>
> 关联文档：`20260826-IJobChunk设计说明.md`（架构）、`20260826-JobSystem重构-性能基线.md`（基线）。

---

## 一、测量方法

- 主基准：`samples/EntJoySample/02_IJobChunkECS/IJobChunkMoveCompareTest/`（100 万实体，
  Sleep 节模拟「16ms 帧间隔后 Schedule+Complete」）。
- `ENTJOY_JOB_WORKERS=N`：全局 worker 线程数（`JobScheduler.Initialize` 支持）。
- `ENTJOY_BENCH_SPLIT=1`：C# 侧 Schedule/Complete 拆分计时。

## 二、开销分解（16ms 帧间隔，100 万实体，C++ IJobChunk，15 worker）

| 阶段 | 典型耗时 | 结论 |
|---|---|---|
| build（实体扫描/tile 构建，含 tile 缓存） | ~10-25μs（命中后 3-15μs）| 小 |
| 推送 224 任务 | ~10-20μs | 小 |
| **`notify_all`（futex 广播唤醒 park worker）** | **50-170μs，偶发 0.5-3.4ms** | **调度侧唯一大头** |
| retire 握手 | ~0-30μs | 小 |
| **executeSpan** | **~810μs**（紧循环 ~450μs） | **带宽/缓存冷地板** |
| GC | 0 次 | 排除 |

**关键认知**：`notify_all` 的 150μs「唤醒风暴」**与执行并行**（worker 同时醒来干活），
不构成真实挂钟损失——同步 S→C 总耗时 ≈ 执行地板 + ~100μs 固有串行。

## 三、验证过的优化杠杆：worker 线程数

| `ENTJOY_JOB_WORKERS` | Schedule p50 | ExecuteSpan 慢 tile | 完整基准 avg |
|---|---|---|---|
| 15（默认）| 229μs | 616μs | 1.065ms |
| **8（物理核）** | **101μs** | **131μs** | **1.027ms** |
| 4 | 69μs | — | 1.019ms |

8 worker（物理核）把慢 tile（off-CPU 滞留）砍 5 倍——15 worker / 8 物理核 SMT 超线导致
兄弟线程互抢（`cyclesPerNs=0.43` 证实 off-CPU）。但全局默认不宜改（compute-bound
Heavy C++ 17.7→28.6ms 回归），应用按负载显式设置。

## 四、已实施改动

### 4.1 Tile 布局缓存

**问题**：每 job 的 C++ build 段做两遍 O(chunks) 实体数扫描 + tile 构建（10-25μs），
同 query 多 job（ECS 多 system 处理同一 archetype）各自重复。

**解法**：`JobSystem_Scheduler.cpp` 新增 `TileLayoutCache`（16 槽，mutex）：
- key = `(unitsPtr, itemCount, workerCap, rangeSize, unitGeneration)`；
- `unitGeneration` = C# `StructuralVersion`，通过 P/Invoke → Export → Scheduler 签名链贯通，
  **解决了地址复用竞态**：C# 缓存重建时旧 HGlobal 地址被新 cache 复用 → 误命中旧 tile
  布局 → 执行错误实体区间。StructuralVersion 重建必变 → 立即 miss。
- 值 = tile 边界数组（纯值拷贝，无指针 → 无悬垂）；
- 命中：跳过两遍扫描，填 `batch->tiles`；未命中：原扫描构建 + 入缓存。

签名链：C# `NativeChunkJobs`（包装+delegate* 加 `uint unitGeneration`）→ C Export →
`Scheduler::ScheduleChunks/ChunkRanges/EntityBatches`（加参数）→
`ScheduleChunkBatchCore` → tile key。

### 4.2 编译修复

`IJobEntitySourceGenerator` 为 `[NativeTranspile]` IJobChunk/IJobEntity 补发
`ScheduleWithWorkerCapAndRangeSize` 扩展方法（对接既有 `NativeExports` 绑定）。

## 五、尝试过并回退的方案

| 方案 | 结果 | 处理 |
|---|---|---|
| 推送粒度 `wc×16 → wc×4` | submit 无变化 | 回退 |
| 主线程内联前 N 个 tile | 带宽受限下零和 | 回退 |
| 批推送（injector 批量入队） | pushLoop 10→3μs 但帧耗时无变化 | 回退 |
| 链式唤醒（worker 接力扩散）| Schedule p50 229→59μs 但 Complete +155μs，净无收益 | 回退 |
| 延迟广播 + 首任务判重 | 空 job 10/帧 948→689μs，重 job 总耗时 +50μs | 回退（JCC 成本驱动可消此差，但实现面大） |
| JCC 前置判重（ECS 路径接入 cost 学习）| 重 job avg 1.065→1.044ms；空 job 不变 | 回退（依赖 JCC 管线改动，保留成本） |
| "轻分支唤 2 个" | 空 job 负优化（唤醒纯花，无并行收益）| 回退 |
| 默认 worker=物理核 | compute-bound Heavy C++ -61% 回归 | 回退（保留 ProcessorInfo 工具） |

## 六、自适应 tile（JCC）能否解决？

**不能**（本场景）。

1. ECS chunk/entity 路径不走 JCC（`funcHash/totalElements` 在该路径恒 0，学习直接跳过）。
2. 实验：tile 数 ×4（taskCount 改）→ submit 无变化；只改 worker 数 → Schedule p50 变化大。=> 瓶颈在唤醒，不在 tile。
3. JCC 的 mem-bound 分类承认：带宽受限 job 加 tile 不线性提速。

自适应 tile 收益域：计算受限、代价不均（GridSearch 式 gather）；对均匀 mem-bound 大 job
的帧间隔同步测试无意义。

## 七、多空 job 基准（当前代码，空 100 万 query）

| N/帧 | 每 job p50 | 整帧 |
|---|---|---|
| 1 | 127μs | 146μs |
| 10 | 54-63μs | 679μs |
| 50 | 37-50μs | 2.4-3.8ms |
| 100 | 71μs | 7.3ms |
| 200 | 37μs | 8.2ms |

比 Unity 同量级（~0.3-1ms）差 **10-25 倍**。每 job 固定链条（notify ~8μs + build ~2μs
+ 单 worker 串行执行 ~30-50μs + retire ~5μs）是瓶颈。**批量调度 API（共享 tile/唤醒 +
摊薄 P/Invoke）是生产场景最终解**（目标每 job ~10μs → 200 job ~2ms）。

## 八、为什么 Unity 几百个 Job 快、而我们慢

三层结构性差距（我们每 job 37-130μs vs Unity ~1-4μs）：

**L1 唤醒模型**：Unity `Schedule()` 只是无锁队列入队（~1μs），不触碰 OS 线程；worker
常驻热/短自旋，**不 park 不逐 job 唤醒**。我们每个 job 走完整提交链 + 唤醒（30-130μs）。

**L2 调度仪式量**：我们每 job 固定链条含 224 个 RangeTask 预切 + retire 链（~20-40μs）；
Unity 一个入队项 + 完成标志。

**L3 执行层**：Unity 直接迭代 chunk；我们每 tile `TryExecuteOneTile` ~400ns 固定检查
（空 job 224 tile ≈ 90μs 串行）。

"空 100 万 query"是极端（224 tile）；真实轻 job（几千实体）我们 ~15-25μs，差距 5-10 倍。

**未验证线索**：通用 `IJobParallelFor` 路径无 tile 仪式，空 job S+C 可能近 Unity 量级。
批量调度 API 应优先做"轻量 batch 通道"而非复用 Chase-Lev 重型通道。

## 九、实验环境与局限

- AMD Ryzen 7 8845H（8C/16T）、Windows 平衡电源、NativeDll Release（ClangCL）。
- 工具链：cmake 4.3 + clang-cl（VS2022 Community）+ ISPC 1.30。
- Unity 数据为社区公开基准（1-4μs 空 job），非同机复测。
- 帧间隔模式（Schedule+Complete 每帧串行）是框架性能的硬测试（Unity 同模式 0.95ms）。
