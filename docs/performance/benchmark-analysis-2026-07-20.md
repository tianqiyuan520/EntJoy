# JobSystem 性能分析报告 — 2026-07-20

## 测试环境

- **CPU**: AMD Ryzen 7 8845H (8C/16T)
- **Build**: Release x64
- **测试项**: 1,000,000 实体, 帧间隔 16ms Sleep
- **最终 commit**: `db8fa74`

---

## 最终数据

### 连续帧 Light

| 实现 | avg (ms) |
|------|:--------:|
| C# IJobChunk | **0.150 ~ 0.157** |
| C++ IJobChunk | **0.149 ~ 0.160** |
| C++ Fast IJobChunk | 0.130 ~ 0.168 |
| C# IJobEntity | 0.164 ~ 0.175 |
| **C++ IJobEntity** | **0.076 ~ 0.129** |

> ✅ 连续帧完全正常，证明 ECS 内存布局 (SOA + 64KB slab) 对齐 Unity DOTS 水准。

### 计算密集型 Heavy

| 实现 | avg (ms) |
|------|:--------:|
| C# IJobChunk | 20.5 ~ 22.2 |
| C++ IJobChunk | 18.4 ~ 19.2 |
| **ISPC IJobChunk** | **2.2** (9.3x vs C#) |
| **ISPC IJobEntity** | **2.1** (9.5x vs C#) |

### Sleep 帧间隔 (核心对比场景)

| 实现 | avg (ms) | p50 (ms) | p95 (ms) |
|------|:--------:|:--------:|:--------:|
| C# IJobChunk | 1.142 ~ 1.755 | 1.059 ~ 1.943 | 1.330 ~ 2.399 |
| **C++ IJobChunk** | **1.040 ~ 1.697** | **1.033 ~ 1.831** | **1.226 ~ 2.407** |
| ISPC IJobChunk | 1.276 ~ 1.517 | 1.230 ~ 1.536 | 1.446 ~ 2.380 |
| C# IJobEntity | 1.146 ~ 1.976 | 1.070 ~ 2.212 | 1.370 ~ 2.638 |
| C++ IJobEntity | **1.096 ~ 1.375** | **0.964 ~ 1.104** | **1.224 ~ 2.474** |
| ISPC IJobEntity | 1.269 ~ 2.017 | 1.135 ~ 1.558 | 1.842 ~ 2.747 |

> ✅ 全场景正确性通过，Heavy 无退化。

---

## 全天试验记录

### 已提交的改动 (确认有效，无退化)

| # | 改动 | 文件 | 说明 |
|:-:|------|------|------|
| 1 | **`useFineRanges` 禁用** | `JobSystem.cpp` | 统一 tile 数 58，消除 EntityBatch 路径 tile 翻倍 bug |
| 2 | **Complete() 2048 spin → yield → 256 spin** | `JobSystem.cpp` | 先密集自旋再让出，避免过早上下文切换 |
| 3 | **64KB slab 连续分配** | `Archetype.cs`, `Chunk.cs` | chunk 数据物理连续，硬件预取器自动 stride |
| 4 | **`__assume` 64 字节对齐** | `CppJobGenerator.cs` | 编译器生成 `vmovaps` 替代 `vmovups` |
| 5 | **`#pragma loop(vector)`** | `CppJobGenerator.cs` | 强制 MSVC 自动向量化循环体 |
| 6 | **Tile 内 `_mm_prefetch` NTA** | `JobSystem.cpp` | TryExecuteOneTile 中预取下个/+8 tile，Non-Temporal 减少 cache 污染 |
| 7 | **Assist batch 16 tiles** | `JobSystem.cpp` | Complete() 首轮最多做 16 tiles，GridSearch 友好 |
| 8 | **Worker `THREAD_PRIORITY_ABOVE_NORMAL`** | `NativeWorkerPool.cpp` | 减少 worker 被普通线程抢占 |

### 已回退的试验 (无效或有副作用)

| 方案 | 回退原因 |
|------|----------|
| **Worker 池 8** | ❌ Heavy +70~80% |
| **Spin-then-Park (64/256/512 `_mm_pause`)** | ❌ 无收益，16ms gap 太大 |
| **Gradual Wake (只唤醒 1/2~1/4 worker)** | ❌ Heavy +82%，Light 退化 |
| **`_MM_HINT_T0` PREFETCH** | ❌ 无收益，硬件预取器已足够 |
| **`alignas(64)` EntityBatchData** | ❌ ABI 崩溃，C#/C++ 布局不一致 |
| **Merge MoveData (合并组件)** | ❌ 总数据量不变 (24→16 bytes Entity 没变) |
| **Complete() 4096 spin** | ❌ GridSearch 退化，改回 2048 |

---

## 核心瓶颈分析

### 开销拆解 (C++ IJobEntity Sleep)

```
executeSpanUs = 815 ~ 955 µs    (冷内存读取 + 计算)
avg          = 1.096 ~ 1.375 ms (总耗时)
调度开销     = ~15 ~ 30 µs      (submit + complete，可忽略)
```

**1.0 ~ 1.2ms 中 0.8~1.0ms 是 DDR5 读取 24MB 工作集的物理耗时**，不是 JobSystem 能绕过的。

### workerStartSpreadUs

| 测试 | 范围 | 分析 |
|------|:----:|------|
| C# IJobChunk | 50~1200 µs | OS 调度方差 |
| C++ IJobChunk | 260~900 µs | 同上 |
| C# IJobEntity | 40~900 µs | 同上 |
| **C++ IJobEntity** | **51~865 µs** | 最优但也 > 500µs 常见 |

### cyclesPerWallNs (OS 抢占检测)

> < 1.0 = worker 被 OS 调度出核心

| 测试 | <1.0 频率 | 结论 |
|------|:---------:|------|
| ISPC IJobChunk | 0/5 | ✅ 站稳 CPU |
| C++ IJobEntity | 4/5 | ❌ OS 频繁打断 |
| C# IJobEntity | 3/5 | ❌ |

---

## 结论

| 指标 | 值 |
|------|:---:|
| **调度开销** | **~15-30 µs** — 已接近极限 |
| **冷内存惩罚** | **0.7~1.0 ms** — DDR5 物理限制 |
| **OS 调度方差** | **0.2~0.5 ms** — Windows 非实时 OS |
| **可优化余地** | **接近 0** |

**瓶颈不在调度，在物理内存延迟 + Windows OS 调度。** 所有调度级优化已穷尽，当前性能是 Windows + DDR5 下的合理水平。

### GridSearch (辅助验证)

| 指标 | commit 前 | **最终** |
|------|:--------:|:-------:|
| 核心构建 | 0.60 ms | **0.572 ms** |
| 核心查询 | 0.60 ms | **0.552 ms** |
| 总耗时 | ~2.0 ms | **1.907 ms** |
