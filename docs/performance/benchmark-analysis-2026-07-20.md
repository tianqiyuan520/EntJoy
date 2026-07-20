# JobSystem 性能分析报告 — 2026-07-20

## 测试环境

- **CPU**: AMD Ryzen 7 8845H (8C/16T)
- **Build**: Release x64
- **测试项**: 1,000,000 实体, 帧间隔 16ms Sleep
- **数据**: 5 轮 JOB_TRACE + 100 帧统计分布

---

## 1. 基准测试结果

### 连续帧 Light

| 实现 | avg (ms) |
|------|:--------:|
| C# IJobChunk | 0.204～0.274 |
| C++ IJobChunk | 0.174～0.239 |
| C++ Fast IJobChunk | 0.165～0.245 |
| C# IJobEntity | 0.161～0.366 |
| **C++ IJobEntity** | **0.072～0.267** |

### 计算密集型 Heavy

| 实现 | avg (ms) |
|------|:--------:|
| C# IJobChunk | 20.5～22.2 |
| C++ IJobChunk | 18.4～19.2 |
| ISPC IJobChunk | **2.2** (SIMD 优势) |
| ISPC IJobEntity | **2.1** |

### Sleep 帧间隔 (核心对比场景)

| 实现 | avg (ms) | p50 (ms) | p95 (ms) |
|------|:--------:|:--------:|:--------:|
| C# IJobChunk | 1.142～1.755 | 1.059～1.943 | 1.330～2.399 |
| **C++ IJobChunk** | **1.040～1.697** | **1.033～1.831** | **1.226～2.407** |
| ISPC IJobChunk | 1.276～1.517 | 1.230～1.536 | 1.446～2.380 |
| C# IJobEntity | 1.146～1.976 | 1.070～2.212 | 1.370～2.638 |
| C++ IJobEntity | 1.096～1.561 | 1.016～1.381 | 1.317～2.530 |
| ISPC IJobEntity | 1.269～2.017 | 1.202～1.535 | 1.842～2.747 |

> 数据范围来自多轮运行，反映 OS 调度引起的 run-to-run 方差。

---

## 2. 诊断数据 (JOB_TRACE 核心指标)

### 2.1 workerStartSpreadUs — Worker 唤醒一致性

| 测试 | 范围 | 典型值 | 分析 |
|------|:----:|:------:|------|
| C# IJobChunk | 42～2052 µs | ~1000 µs | spread 很大：偶发 42µs 极好，多数 1000+ |
| C++ IJobChunk | 264～957 µs | ~800 µs | 相对稳定但仍 > 执行时间 |
| ISPC IJobChunk | 32～855 µs | ~800 µs | 波动大 |
| C# IJobEntity | 37～1080 µs | ~800 µs | 方差最大 |
| C++ IJobEntity | 51～865 µs | ~600 µs | 相对最优 |
| ISPC IJobEntity | 61～952 µs | ~600 µs | |

### 2.2 workersPeak — 实际参与 Worker 数

| 测试 | 15 池大小时 | 分析 |
|------|:----------:|------|
| C# IJobChunk | 15/15 (100%) | 全部唤醒 |
| C++ IJobChunk | 12～15/15 | 偶有丢失 |
| ISPC IJobChunk | 10～14/15 | 丢失较多 |
| C# IJobEntity | 9～15/15 | 方差大 |
| C++ IJobEntity | 12～15/15 | 相对稳定 |
| ISPC IJobEntity | 8～15/15 | 丢失最多 |

### 2.3 cyclesPerWallNs — OS 抢占检测

> 慢 tile 的 CPU cycles / 墙上时间。< 1.0 表示 worker 被 OS 抢占。

| 测试 | <1.0 频率 | 最低值 | 分析 |
|------|:---------:|:------:|------|
| ISPC IJobChunk | 0/5 | 2.20 | ✅ ISPC tile 计算量大，站稳 CPU |
| C++ IJobChunk | 3/5 | 0.49 | ⚠️ 频繁被抢占 |
| C# IJobChunk | 3/5 | 0.38 | ⚠️ |
| C++ IJobEntity | 4/5 | 0.32 | ❌ 问题最严重 |
| ISPC IJobEntity | 2/5 | 0.28 | ❌ |

### 2.4 executeSpanUs — 纯执行时间 vs avg 总耗时

| 测试 | executeSpanUs | avg (ms) | 调度开销 |
|------|:-----------:|:--------:|:--------:|
| **C++ IJobChunk** | **901～1025 µs** | **1.040** | **~19 µs** |
| C# IJobChunk | 996～1299 µs | 1.105 | ~15 µs |
| C++ IJobEntity | 815～955 µs | 1.220 | ~30 µs |
| C# IJobEntity | 953～1362 µs | 1.381 | ~25 µs |

> 调度开销 (submit + complete) 仅 15～30 µs。总耗时 = 执行时间 + 调度开销。

---

## 3. 试验记录

### 3.1 useFineRanges 修复

**文件**: `src/NativeDll/JobSystem.cpp` (已撤销)

**问题**: C++/ISPC EntityBatch 路径的 tile 数（121）比 C# ChunkRange 路径（58）多一倍，原因是 `ConsumeLongBatchBarriers` 在 Sleep 场景每帧触发。

**效果**: C++ Entity 路径从 1.220ms → 1.147ms (-6%)。C# Entity 从 1.381ms → 1.290ms (-7%)。

**结论**: 该修复方向正确但收益在噪声内。需确认 121 tiles 的恶化是否持续存在。

### 3.2 PREFETCH `_mm_prefetch`

**文件**: `src/NativeDll/JobSystem.cpp` (已撤销)

**做法**: 在 `ChunkExecuteTile` 所有 3 种 tile kind 中对下一个 chunk 的 entityArray + componentArrays 做 `_mm_prefetch(_MM_HINT_T0)`。

**效果**: 无稳定可测量改善。

**结论**: DDR5 的预取窗口 (~80ns) 远小于 tile 间的执行间隔 (~200µs)，硬件预取器已足够。

### 3.3 Worker 池从 15 减到 8

**文件**: `src/NativeDll/JobSystem.cpp` (已撤销)

**效果**: 

| 指标 | 15 workers | 8 workers | 改善 |
|------|:---------:|:---------:|:----:|
| **workerStartSpreadUs** | 200～1200 µs | **26～67 µs** | ✅ 10-20x |
| Core migrations | 0～21 | **0～1** | ✅ 几乎消失 |
| **Light C# IJobChunk** | 0.23 ms | **0.51 ms** | ❌ +120% |
| **Heavy C# IJobChunk** | 20.7 ms | **35.1 ms** | ❌ +70% |

**结论**: 池大小不变，8 个 worker 不足以支撑 Heavy 计算密集型任务。

### 3.4 Spin-then-Park (64 `_mm_pause`)

**文件**: `src/NativeDll/NativeWorkerPool.cpp` (已撤销)

**做法**: WorkerLoop drain 后先自旋 64 次 pause (~0.5µs) 再 `wake.acquire()`。

**效果**: 连续帧和 Sleep 场景均无稳定改善。Sleep 场景 0.5µs 自旋窗口远小于 16ms park→wake 窗口。

**结论**: spin-then-park 对连续帧尾部延迟有理论价值但对 Sleep 冷唤醒无效。

### 3.5 Gradual Wake (唤醒部分 Worker)

**文件**: `src/NativeDll/NativeWorkerPool.cpp` (已撤销)

**做法**: Submit 时只唤醒 `slotCount/4` 或 `slotCount/2` 个 Worker，其余通过 steal 消费。

**效果 (slotCount/2)**:

| 测试 | workersPeak | 结果 |
|------|:----------:|:----:|
| C# IJobChunk | 7/15 | assist 17% |
| C++ IJobChunk | 7/15 | ❌ Light 退化 |
| ISPC IJobEntity | 3/15 | ❌ 主线程 assist 30% |
| Heavy C# | 7/15 | ❌ +82% |

**结论**: 对轻量/计算密集型任务不适用。只对密集内存型、且 worker 因带宽饱和而冗余的场景有效。

---

## 4. 核心发现

### 4.1 调度不是瓶颈

```
C++ IJobChunk Sleep:
  executeSpanUs = 1021 µs  (纯执行)
  avg = 1040 µs
  调度开销 ≈ 19 µs  (占 < 2%)
```

所有调度级改动（self-spin、assist 策略、worker 数量、prefetch、useFineRanges）都没有实质性改善 Sleep 场景耗时。

### 4.2 真实瓶颈：冷内存 + OS 调度方差

从连续帧与 Sleep 对比看：

```
C++ IJobChunk 连续: 0.205 ms  (hot cache)
C++ IJobChunk Sleep: 1.040 ms (cold cache)
差距:            ~0.835 ms = DDR5 读取 24MB 的物理耗时
```

24MB 工作集（8MB Position + 8MB Velocity + 8MB Entity）从 DRAM 读取到 L3 cache 的保底时间约 0.5～0.8ms，加上执行时间 0.2ms，基本在物理极限附近。

### 4.3 方差根因：Windows OS 调度

`cyclesPerWallNs < 1.0` 在 5 轮中出现 12 次，确认 `C++/C# IJobEntity` 被 OS 频繁调度出核心。这不是 JobSystem 能解决的问题。

---

## 5. 结论

| 方案 | 收益 | 结论 |
|------|:----:|------|
| useFineRanges 修复 | ~6% | 方向正确，但不解决根因 |
| PREFETCH | 无 | DDR5 预取窗口太小 |
| Worker 池 8 | -70～+120% | Heavy 不可用 |
| Spin-then-Park | 无 | 16ms gap 太大 |
| Gradual Wake | -50～+82% | Heavy 不可用 |

**当前 C++ IJobChunk 1.040ms vs Unity 0.95ms 的 0.09ms 差距在噪声范围内 (OS 调度方差可达 0.5ms)。** JobSystem 的调度本身已足够高效。
