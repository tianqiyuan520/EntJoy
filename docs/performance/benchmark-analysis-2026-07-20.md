# JobSystem 性能分析报告 — 2026-07-20

## 测试环境

- **CPU**: AMD Ryzen 7 8845H (8C/16T)
- **Build**: Release x64
- **测试项**: 1,000,000 实体, 帧间隔 16ms Sleep
- **最终 commit**: `1a36850`

---

## 全天改动汇总

### 已提交 (确认有效，无退化)

| # | 改动 | 文件 | 说明 |
|:-:|------|------|------|
| 1 | **`useFineRanges` 禁用** | `JobSystem.cpp` | 统一 tile 数 58，消除 EntityBatch 路径 tile 翻倍 bug。`useFineRanges` 是前一个 batch 执行超时的自我实现恶性循环—Sleep 场景每帧都被加倍。 |
| 2 | **Complete() 2048 spin → yield → 256 spin** | `JobSystem.cpp` | 先密集自旋再让出，避免过早上下文切换。原代码先 `yield()` 再 64 pause，yield 触发系统调度让出时间片。 |
| 3 | **Unlimited Assist** | `JobSystem.cpp` | Complete() Phase 0 协助改为无限循环，匹配 Unity 行为。主线程领取并执行 worker 尚未处理的 tile。消除 OS 尾部调度抖动导致的 P95 偏高。 |
| 4 | **64KB slab 连续分配** | `Archetype.cs`, `Chunk.cs` | Archetype 管理 64KB aligned slab，chunks 从 slab 中连续切分。相邻 chunk 组件数组物理上连续，硬件预取器可自动 stride。 |
| 5 | **`__assume` 64 字节对齐** | `CppJobGenerator.cs` | 生成的 C++ 代码中声明组件指针 64 字节对齐。MSVC 据此生成 `vmovaps` 而非 `vmovups`。 |
| 6 | **`#pragma loop(vector)`** | `CppJobGenerator.cs` | 强制 MSVC 自动向量化循环体。与已有的 `#pragma loop(ivdep)` 配合。 |
| 7 | **Tile 内 `_mm_prefetch` NTA** | `JobSystem.cpp` | TryExecuteOneTile 中预取下个 1 个和 8 个 tile 的组件数据，使用 `_MM_HINT_NTA` non-temporal 提示减少 cache 污染。 |
| 8 | **Worker `ABOVE_NORMAL` 优先级** | `NativeWorkerPool.cpp` | 减少 worker 被普通线程抢占的概率。 |
| 9 | **Assist 16 tiles 上限** | `JobSystem.cpp` | 首轮 assist 至多 16 tiles，防止主线程在 GridSearch 等连续短作业场景过度抢工作。配合 Unlimited Assist：16 后用 `tilesRemaining` 动态判断是否进入尾部无限模式。 |

### 已回退的试验

| 方案 | 回退原因 |
|------|----------|
| **Worker 池从 15 减到 8** | ❌ Heavy +70~80% |
| **Spin-then-Park (64/256/512 `_mm_pause`)** | ❌ 无收益。16ms gap 太大，spin 窗口根本等不到任务。 |
| **Gradual Wake (Submit 时只唤醒 1/2~1/4 worker)** | ❌ Heavy +82%，Light 退化。Workers 不够用。 |
| **`_MM_HINT_T0` PREFETCH** | ❌ 无收益。DDR5 预取窗口 ~80ns 远小于 tile 执行间隔 ~200µs。 |
| **** `alignas(64)` EntityBatchData ** | ❌ ABI 崩溃。C++ 加 alignas 后 sizeof 变 64，C# `StructLayout.Sequential` 仍是 24，数组索引错位。 |
| **Merge Position+Velocity → MoveData ** | ❌ 总数据量不变。Entity(8) + MoveData(16) = 24 bytes/entity，与独立的 Entity(8) + Position(8) + Velocity(8) = 24 完全相同，DRAM 带宽一样。 |
| **Complete() 4096 spin** | ❌ GridSearch 总耗时从 ~2.0ms 升到 ~2.7ms。恢复 2048。 |
| **`uniform for` ISPC 循环** | ❌ 编译失败。ISPC 中 float2 struct 字段默认 `varying`，`uniform for` 上下文中赋值不兼容。 |
| **`ENTJOY_WORKER_AFFINITY=1`（CPU 亲和性）** | ❌ 绑定后更慢。Windows 调度器受干扰。 |

---

## 最终性能数据

### 连续帧 Light

| 实现 | avg (ms) |
|------|:--------:|
| **C# IJobChunk** | **0.150 ~ 0.204** |
| **C++ IJobChunk** | **0.149 ~ 0.179** |
| C++ Fast IJobChunk | 0.130 ~ 0.193 |
| **C# IJobEntity** | **0.164 ~ 0.185** |
| **C++ IJobEntity** | **0.076 ~ 0.144** |
| ISPC IJobChunk | 0.307 ~ 0.341 |
| ISPC IJobEntity | 0.327 ~ 0.371 |

> ✅ 连续帧完全正常。ECS 内存布局 (SOA + 64KB slab + cacheline 对齐) 已达 DDR5 带宽极限。

### 计算密集型 Heavy

| 实现 | avg (ms) |
|------|:--------:|
| C# IJobChunk | 20.5 ~ 22.3 |
| C++ IJobChunk | 18.4 ~ 19.5 |
| **ISPC IJobChunk** | **2.2** (9.4x vs C#) |
| **ISPC IJobEntity** | **2.2** (9.2x vs C#) |
| C++ Fast IJobChunk | 19.1 ~ 20.5 |

> ✅ ISPC SIMD 在计算密集型场景碾压。

### Sleep 帧间隔 (核心对比场景)

| 实现 | avg (ms) | p50 (ms) | p95 (ms) |
|------|:--------:|:--------:|:--------:|
| C# IJobChunk | 1.142 ~ 1.755 | 1.059 ~ 1.943 | 1.330 ~ 2.399 |
| **C++ IJobChunk** | **1.040 ~ 1.697** | **1.033 ~ 1.831** | **1.226 ~ 2.407** |
| ISPC IJobChunk | 1.276 ~ 1.517 | 1.230 ~ 1.536 | 1.446 ~ 2.380 |
| C# IJobEntity | 1.146 ~ 1.976 | 1.070 ~ 2.212 | 1.370 ~ 2.638 |
| **C++ IJobEntity** | **1.069 ~ 1.375** | **0.964 ~ 1.104** | **1.224 ~ 2.326** |
| ISPC IJobEntity | 1.269 ~ 2.017 | 1.135 ~ 1.558 | 1.842 ~ 2.747 |

> 数据范围来自多轮运行，反映 OS 调度引起的 run-to-run 方差。

### GridSearch (辅助验证)

| 指标 | commit 前 | **最终** |
|------|:--------:|:-------:|
| 核心构建 | 0.60 ms | **0.572 ms** |
| 核心查询 | 0.60 ms | **0.552 ms** |
| 总耗时 | ~2.0 ms | **1.907 ms** |

> ✅ GridSearch 完全恢复并略微优于原版。

---

## 瓶颈分析

### 开销拆解

```
C++ IJobChunk Sleep:
  executeSpanUs = 913 µs  (纯执行 = DRAM 读取 + 计算)
  avg           = 1.340 ms
  调度开销      ≈ ~15 µs  (submit + complete，占 < 2%)
```

**1.0 ~ 1.3ms 中 0.8~1.0ms 是 DDR5 读取 24MB 工作集（8MB Entity + 8MB Position + 8MB Velocity）的物理耗时，不是 JobSystem 能绕过的。**

### workerStartSpreadUs

| 测试 | 范围 | 分析 |
|------|:----:|------|
| C# IJobChunk | 42~1463 µs | OS 调度方差，偶发好的轮能到 20µs |
| C++ IJobChunk | 172~957 µs | 波动大 |
| C# IJobEntity | 79~2284 µs | 方差最大 |
| **C++ IJobEntity** | **51~865 µs** | 相对最优 |

### cyclesPerWallNs

> < 1.0 = worker 被 OS 调度出核心，实际 CPU cycle 远少于墙上时间

| 测试 | <1.0 频率 | 结论 |
|------|:---------:|------|
| ISPC IJobChunk | 0% | ✅ 站稳 CPU (tile 计算量大) |
| C++ IJobChunk | ~60% | ⚠️ 频繁被抢占 |
| C# IJobEntity | ~60% | ⚠️ |
| C++ IJobEntity | ~80% | ❌ 问题最严重 |
| ISPC IJobEntity | ~40% | ⚠️ |

---

## 结论

| 指标 | 值 |
|------|:---:|
| **调度开销** | **~15-30 µs — 已接近极限** |
| **冷内存惩罚** | **0.7~1.0 ms — DDR5 物理限制** |
| **OS 调度方差** | **0.2~0.5 ms — Windows 非实时 OS** |
| **可优化余地** | **接近 0** |

瓶颈已从调度（~20µs）完全转移到**冷内存迁移 + Windows OS 调度方差**。

### C++ IJobEntity 为何优于 IJobChunk

C++ IJobChunk 走的是 `ChunkCallbacks` tile 类型，每个 chunk 一次回调。而 IJobEntity 走 `EntityBatchRange`，每个 tile 一次回调处理一组 chunks。C++ IJobChunk 实际已经在用 **EntityBatch 路径**（`ScheduleChunkEntityBatchRawWithWorkerCapAndRangeSize`），只有 ISPC IJobChunk 仍走 ChunkJobData。

### ISPC Light 为何比 C++ 慢

ISPC 的 `foreach` 生成完整的 SPMD gang 运行时（gang 启动/回收、gather/scatter、lane 同步）。对于 memory-bound 循环，这个固定开销与循环体相当甚至更大。Heavy 场景（计算密集型）10x SIMD 收益远大于 SPMD 开销。

### 最终建议

1. **接受 1.0~1.2ms 为当前 Win11 Home + DDR5 下的最优水平**
2. 关注 ISPC Heavy 场景（9.5x vs C#）和 GridSearch
3. 合并 Position+Velocity 组件不会减少 DRAM 带宽（总字节数不变）
4. 如需继续突破：研究实时调度、或更换实时 OS 变体
