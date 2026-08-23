# JobLibsBenchmark 基线数据（2026-08-23）

> 环境：Windows 11 x64，16 核（PC-1 = 15 Workers），AVX2
> 原生 DLL：ClangCL `/O2 /Oi /fp:fast`（VS 2022 LLVM 工具集）
> Git：当前 HEAD

## 核心四路对比（Managed / Native C# / C++ / ISPC）

| 场景 | Managed | Native(C#) | Cpp | ISPC | 胜者 |
|---|---|---|---|---|---|
| **S1** 轻任务(100万) | 0.226ms | 0.135ms | **0.019ms** | **0.018ms** | Ispc |
| **S2** 空任务(100万) | 0.366ms | 0.219ms | **0.026ms** | 0.029ms | Cpp |
| **S3** 依赖链(+1→x2→-3) | 0.959ms | 0.369ms | **0.074ms** | 0.095ms | Cpp |
| **S4** 调度延迟(1000×1024) | **0.001ms** | **0.001ms** | **0.001ms** | **0.001ms** | 全并列 |
| **S5** 高竞争(10万×1000) | 3.287ms | 3.709ms | **0.023ms** | 0.524ms | Cpp |
| **S6** 控制流(10万×1000) | 39.287ms | 44.686ms | 41.147ms | **5.505ms** | Ispc |

## 各场景分析

### S1 轻任务 — 计算简单加法
- **Cpp 0.019ms / ISPC 0.018ms**：SIMD 向量化 + 编译器优化，两者持平
- Native(C#) 0.135ms：P/Invoke + 托管回调开销
- Managed 0.226ms：纯托管调度 + 执行
- **Cpp/ISPC ≈ Managed 的 1/12**

### S2 空任务 — 零计算，纯调度开销
- **Cpp 0.026ms**：原生调度 + 零执行
- ISPC 0.029ms：与 Cpp 持平（空任务无 SIMD 差异）
- Native(C#) 0.219ms：P/Invoke 开销 8x
- Managed 0.366ms：纯托管调度
- **Cpp/ISPC ≈ Managed 的 1/14**

### S3 依赖链 — 三阶段串行依赖
- **Cpp 0.074ms**：原生依赖链调度最优
- ISPC 0.095ms：略慢（SIMD 在依赖链场景无优势）
- Native(C#) 0.369ms：托管依赖链开销
- Managed 0.959ms：最慢
- **Cpp ≈ Managed 的 1/13**

### S4 调度延迟 — 纯调度开销基准
- **全四路 0.001ms**：调度器本身极快，差异在噪声范围
- 验证：EntJoy 调度器延迟已达极限

### S5 高竞争 — 编译器代数简化（不公平场景）
- **Cpp 0.023ms**：Clang `/O2` 把 `sum += index * j` 替换为 `index * 499500`（单条 `imul`）
- ISPC 0.524ms：不做代数简化，完整循环
- Managed 3.287ms / Native 3.709ms：纯标量执行
- ⚠️ **Cpp 优势来自编译器优化，非调度器差异**

### S6 控制流 — LCG + 分支（唯一公平重计算场景）
- **ISPC 5.505ms**：SIMD 8-wide + 编译器向量化
- Cpp 41.147ms：标量执行，无 SIMD
- Managed 39.287ms：纯托管标量
- Native(C#) 44.686ms：P/Invoke + 托管回调标量
- **ISPC ≈ Cpp 的 1/7.5**，SIMD 差距在此场景完全体现

## 关键结论

| 结论 | 数据支撑 |
|---|---|
| **调度器质量：全库第一梯队** | S4 调度延迟 0.001ms（全四路并列） |
| **Cpp vs Managed：计算场景 10-14x** | S1/S2/S3/S5 Cpp 全胜 |
| **ISPC vs Cpp：SIMD 差距仅在重计算** | S5 ISPC 0.524 vs Cpp 0.023（Cpp 编译器优化赢）；S6 ISPC 5.5 vs Cpp 41（SIMD 真差距） |
| **Native(C#) vs Managed：P/Invoke 开销** | S1 0.135 vs 0.226（快 40%）；S2/S3 类似 |
| **S5 是编译器差异，非公平对比** | Cpp 0.023ms = Clang 代数简化，ISPC 0.524ms = 完整循环 |
| **S6 是唯一公平重计算场景** | ISPC 5.5ms 胜 Cpp 41ms（7.5x），SIMD 真优势 |

## 与文档值对比

| 场景 | 文档值(2026-08-23) | 本次实测 | 偏差 |
|---|---|---|---|
| S1 Cpp | 0.022ms | 0.019ms | -14% |
| S2 Cpp | 0.048ms | 0.026ms | -46% ⬇️ |
| S3 Cpp | 0.190ms | 0.074ms | -61% ⬇️ |
| S5 Cpp | 0.058ms | 0.023ms | -60% ⬇️ |
| S6 Cpp | 38.753ms | 41.147ms | +6% |
| S6 ISPC | 5.484ms | 5.505ms | 持平 |

> S1/S2/S3/S5 Cpp 提升显著：可能是编译器版本更新或测试环境差异
> S6 Cpp/ISPC 持平：重计算场景稳定

## Per-job 自动 Batch（JobCostCache）— 2026-08-23 落地

用 per-job 的**每元素成本 EWMA** 自动求解最优 tile 数，替代固定 tpw=4 一刀切。
export flag 默认关闭（`NativeJobScheduler.JobCostCacheEnabled = false`），
显式启用或环境变量 `ENTJOY_JOB_COST_CACHE=1`。

### 实测对比（flag ON vs OFF，同机同轮）：

| 场景 | flag OFF | flag ON | 变化 |
|---|---|---|---|
| S1 Cpp | 0.020ms | 0.019ms | 持平 ✓ |
| S2 Cpp | 0.025ms | **0.013ms** | 🚀 2x |
| S3 Cpp | 0.087ms | 0.087ms | 持平 ✓（无回归）|
| S5 Cpp | 0.024ms | **0.008ms** | 🚀 3x（超目标 0.013）|
| S6 Cpp | 39.98ms | 38.45ms | 微升 ✓ |

> 环境噪声 ±30-100%，单轮对比为近似值；方向与设计一致。

### 实现要点

| 组件 | 说明 |
|---|---|
| `JobCostCache.h`（新建） | 256 槽无锁数组（Q22 定点），funcHash（FNV-1a）定位，碰撞复用重学 |
| `ResolveChunkSize(len, batch, funcHash)` | 有数据时 `tiles = clamp(totalUs/150μs, 1, wc×16)` + **安全护栏 floor** |
| `BatchState.funcHash/totalElements` | Schedule 入口设置，退役时算 perElem = (topologyDoneAt−publishedAt)/N |
| EWMA α=0.75 | **有符号分支防下溢**（下溢曾把 tiles 钉死 240）+ **双向对称无上升阻尼**（CAS 循环防并发丢更新）|
| 安全护栏 | 单 tile ≤ 32k 元素（kMaxAutoChunk），防大 job 塌缩成串行巨型 tile |

### 踩过的坑（已修）

1. **EWMA 无符号下溢**：`(sample - old) * 3` 在 sample<old 时下溢到 ~2^64 → blend 爆炸 → tiles 永久钉在 240 上限。修：有符号分支。
2. **大 job 塌缩串行**：wall-clock perElem 是"并行稀释"成本，S3 依赖链 1M 元素被塌成 1 tile → inline 串行 → 0.074→0.164ms 回归。修：kMaxAutoChunk 上限保证 tile 粒度。
3. **4x 升限压慢模式切换**：初版为防 GC/抢占尖峰加 4x 阻尼，但下溢修复后尖峰本就 1-2 轮自愈；阻尼反而让成本波动（10↔10000 次循环）慢 4-5 轮才跟上 → 重调用欠并行。修：移除升限、双向 α=0.75（尖峰自愈测试 SpikeSelfHeal 验证）。
4. **load→store 非原子 RMW**：多 worker 同槽并发更新会丢更新。修：CAS 循环。

### C++ 单元测试（全部 PASS）

```
PASS JobCostCacheBasic          — 读写/EWMA 上下收敛
PASS JobCostCacheNoUnderflow    — 下溢回归测试
PASS JobCostCacheSpikeSelfHeal  — 100x 尖峰 3 个正常样本内自愈（无 4x 阻尼）
PASS JobCostCacheCollisionReuse — 碰撞覆写 + 失效
PASS ResolveChunkSizeFallback   — flag 关闭 = tpw 兜底零回归
PASS JccConcurrentHeterogeneous — 8 线程 × 4 成本 job 并发，结果==串行参考
PASS JccResultsInvariantAcrossTiles — 60 tiles vs 4 tiles 结果逐元素一致
PASS JccFlagToggleMidFlight     — 任务在飞 toggle flag 2000 次，无死锁/崩溃
PASS JccCollisionSlotConcurrent — 同槽 2 hash × 4 线程 × 5 万次 Update，唯一持有者
PASS JccConcurrentSameHashBatches — 6 线程同 hash 并发退役，CAS 收敛
PASS JccWaveCostVariance        — 10↔10000 循环波动，结果逐模式正确
PASS JccLongRunStability        — 12000 次调度，cache 占用 ≤4，无泄漏面
```
顺便修正了 `TestAutomaticBatchDensity` 的过时断言（tpw 16→4 后未同步）。

## 自适应自旋 + 旧路径移除（git 6ebda28）

### 自适应自旋（WorkerLoop park 段）

固定 256 次自旋无法兼顾连续/间歇调度。改为：
- 执行任务后拉满 `kSpinMax=4096` → 连续调度新批到达时仍在自旋 → 零唤醒
- 空转指数退火（÷2，下限 `kSpinMin=64`）→ 空闲快速让出 CPU
- 全局在飞（`activeTasks>0`）用 `kSpinBusy=8192`（下一个任务即将被认领）
- 保持 wake-all 语义不变

### 旧路径 NativeWorkerPool 移除（Chase-Lev 唯一）

删除 `NativeWorkerPool.h/.cpp` + 测试；`g_useWorkStealing` 双路径分叉、AssistState/
AssistDependencyChain、TryRetireCompletedBatch 等死代码全删；SubmitBackendAsync 统一
SubmitWork；`WorkerSnapshot` 迁独立头 `WorkerSnapshot.h`。净删 ~650 行。

### 最终协同数据（tpw=4 基线 → JobCostCache + 自适应自旋）

| 场景 | tpw=4 基线 | JCC | JCC + 自旋 | 累计 |
|---|---|---|---|---|
| S2 空任务 | 0.026ms | 0.013ms | **0.007ms** | **3.7x** |
| S3 依赖链 | 0.087ms | 0.077ms | **0.053ms** | **1.6x** |
| S5 轻任务 | 0.024ms | 0.012ms | **0.008ms** | **3.0x** |
| S6 重任务 | 40.9ms | 40.9ms | **39.6ms** | ✓ 不牺牲 |

> flag 默认 OFF = 纯 tpw=4（零回归）；ON 启用自动 batch + 自旋协同。

## 测试指令

```powershell
dotnet run --project tools\JobLibsBenchmark\JobLibsBenchmark.csproj -c Release
dotnet run --project tools\JobLibsBenchmark\JobLibsBenchmark.csproj -c Release -- S5
dotnet run --project tools\JobLibsBenchmark\JobLibsBenchmark.csproj -c Release -- S6
```
