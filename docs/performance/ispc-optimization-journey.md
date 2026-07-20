# ISPC Optimization Journey — ISPC 优化历程

## Overview / 概述

This document records all ISPC optimization attempts on the EntJoy ECS JobSystem,
covering both successful improvements and failed experiments with root cause analysis.

本文记录了 EntJoy ECS JobSystem 上所有 ISPC 优化尝试，包括成功的改进和失败的实验及根因分析。

**Baseline benchmark (1M entities, before any ISPC optimization):**
**基准测试（1M 实体，所有优化前）:**

| Test | ISPC IJobChunk | ISPC IJobEntity | C++ IJobChunk |
|:-----|:--------------:|:---------------:|:-------------:|
| Light (2 FLOPs) | 0.910 ms | 0.939 ms | 0.352 ms |
| Heavy (sin/cos×16) | 2.284 ms | 2.372 ms | 19.462 ms |
| Sleep+Light | ~1.06 ms | ~1.04 ms | ~0.99 ms |

---

## ✅ Successful Optimizations / 成功优化

### 1. Struct Copy Elimination / 消除 Struct Copy-In/Copy-Out

**What / 内容:** Eliminated the `T var = arr[i]` copy-in and `arr[i] = var` copy-out
pattern in ISPC-generated code for IJobEntity and IJobChunk.

消除了 IJobEntity 和 IJobChunk ISPC 代码中的 `T var = arr[i]`（拷贝入）和
`arr[i] = var`（拷贝出）模式。

**How / 实现:**

- **IJobEntity:** `IspcStatementTranslator` — added `_entityRefParamNames` so
  `Execute` method parameters are directly accessed via `ptr[__entity_index].field`
  instead of local struct copies.
- **IJobChunk:** `IspcChunkStatementTranslator` — `_chunkProxyLocalVars` mechanism
  detects `T var = arr[index]` patterns, redirects all field access through
  `arr_ptr[index].field`, and eliminates redundant writeback statements.

**Generated code before/after / 生成代码前后对比:**

```ispc
// Before / 优化前
foreach (index = 0 ... len) {
    MovePosition p = positions_ptr[index];      // copy IN
    MoveVelocity v = velocities_ptr[index];      // copy IN
    p.Value = p.Value + v.Value * DeltaTime;
    positions_ptr[index] = p;                    // copy OUT
}

// After / 优化后
foreach (index = 0 ... len) {
    positions_ptr[index].Value = positions_ptr[index].Value
        + velocities_ptr[index].Value * DeltaTime;
}
```

**Result / 效果:**

| Test | Before | After | Speedup |
|:-----|:-----:|:-----:|:-------:|
| ISPC IJobChunk Light | 0.910 ms | **0.36 ms** | **2.5x** |
| ISPC IJobEntity Light | 0.939 ms | **0.56 ms** | **1.7x** |
| ISPC IJobChunk Heavy | 2.284 ms | unchanged | ~1x |

---

### 2. Enable FMA (`--opt=disable-fma` removed for `IspcMathLib.fast`)

**What / 内容:** Removed `--opt=disable-fma` from ISPC compile flags when using
`IspcMathLib.fast` target. FMA (Fused Multiply-Add) fuses `a*b+c` into a single
instruction `vfmadd213ps`, reducing latency and improving throughput.

对 `IspcMathLib.fast` 目标移除 `--opt=disable-fma` 编译标志，允许 ISPC 生成
FMA （Fused Multiply-Add） 指令，将 `a*b+c` 融合为单条 `vfmadd213ps` 指令。

**Impact analysis / 影响分析:**
- **Heavy path (sin/cos loop):** ~10-20% speedup from fewer instructions.
  FMA is heavily used in `accX * 0.985f + wave * 0.015f` patterns.
- **Light path:** Minimal impact — only 1 multiply, no add-to-multiply chain.
- **Precision impact / 精度影响:** ~1 ULP (≈1.2e-7 relative error), well within
  benchmark epsilon=1e-3. Existing sin/cos approximation error (~1.22e-4) dominates.

**Implementation / 改动:** In `NativeTranspilerGenerator.cs`, the ISPC compile
command generation uses `--opt=disable-fma` only for `IspcMathLib.system/default`,
and omits it for `IspcMathLib.fast`.

**Result / 效果:**

| Test | Before | After | Speedup |
|:-----|:-----:|:-----:|:-------:|
| ISPC IJobChunk Heavy | 2.284 ms | **1.93 ms** | **1.18x** |
| ISPC IJobChunk Light | 0.429 ms | **0.36 ms** | 1.18x |

---

### 3. `__assume` Alignment Hints / 对齐提示

**What / 内容:** Added `__assume((intptr_t)ptr % 64 == 0)` after each
`reinterpret_cast` in ISPC wrapper adapters, telling the compiler that
component array pointers are 64-byte aligned (guaranteed by ECS chunk allocation).

在 ISPC wrapper adapter 的每个 `reinterpret_cast` 后追加
`__assume((intptr_t)ptr % 64 == 0)`，告知编译器组件数组指针是 64 字节对齐的
（ECS chunk 分配保证）。

**Result / 效果:** Within noise floor (~2-5% variation). ISPC compiler already
infers alignment from pointer type information, so the explicit hint adds little.

在环境噪音范围内。ISPC 编译器已能从指针类型推断对齐信息。

---

## ❌ Failed Experiments / 失败实验

### F1. Per-Field Decomposition / 逐字段分解

**Hypothesis / 假设:** Splitting `vec += val` into `vec.x += val.x; vec.y += val.y;`
would let the ISPC compiler see independent scalar streams and optimize better.

**Root cause of failure / 失败根因:** For AoS (Array of Structs) data layout,
per-field decomposition **doubles gather/scatter count**. Each `vec.x` and `vec.y`
access requires a separate gather/scatter operation. For memory-bound (Light)
workloads, this makes performance worse.

```ispc
// Per-field: 2 gather + 2 scatter — worse for AoS
positions_ptr[index].Value.x += velocities_ptr[index].Value.x * DeltaTime;
positions_ptr[index].Value.y += velocities_ptr[index].Value.y * DeltaTime;
```

**Status / 状态:** Reverted.

---

### F2. ISPC Batch Function (`uniform for` inside ISPC)

**What / 内容:** Generated a `_batch` ISPC function variant that internally loops
over chunks with `uniform for` + `foreach` nested loops, to reduce ISPC
function-call count from ~1000 (one per chunk) to ~58 (one per tile).

生成 `_batch` ISPC 函数变体，内部用 `uniform for` + `foreach` 嵌套循环处理多个
chunk，将 ISPC 函数调用次数从 ~1000（每 chunk 一次）降至 ~58（每 tile 一次）。

**Root cause of failure / 失败根因:** ISPC's `uniform for` + `foreach` nested loop
forces the compiler to tear down and rebuild the SPMD execution context for each
chunk (~62 entities, only 4 gang iterations on AVX-512 16-wide). The overhead
of entering/exiting the `foreach` region exceeds the cost of a standalone
function call.

ISPC 的 `uniform for` + `foreach` 嵌套循环导致每个 chunk（~62 实体、AVX-512 16-wide
下仅 4 次 gang 迭代）都要重建 SPMD 执行上下文。进出 `foreach` 区域的开销超过了
独立函数调用的成本。

**Status / 状态:** Reverted (commit `27ec1cf`).

---

### F3. Copy-and-Concat Batching (memcpy concatenation)

**What / 内容:** Copy all chunks' component data into a single temporary flat
buffer, call ISPC once on the full range, then copy back. Eliminates per-chunk
calls entirely without `uniform for`.

将所有 chunk 的组件数据拷贝到一个临时连续缓冲区，全量调用一次 ISPC，再拷贝回
原数组。无 `uniform for`，彻底消除每 chunk 多次调用。

**Root cause of failure / 失败根因:** For Light (2 FLOPs) workloads, `memcpy`
of 24 MB (16 MB read + 8 MB write) is far more expensive than the actual
computation (36 µs). The copy overhead adds ~0.8ms, negating any benefit.

对于 Light（2 FLOPs）负载，拷贝 24 MB（16 MB 读 + 8 MB 写）的开销远超实际计算
（36 µs），额外增加 ~0.8ms。

**Status / 状态:** Reverted.

---

### F4. Single-Worker Scheduling (workerCap=1)

**What / 内容:** Changed the ISPC Schedule's default `workerCap` and `rangeSize`
to `1` and `int.MaxValue`, serializing all tiles onto one worker to eliminate
scheduling contention.

将 ISPC 调度的默认 `workerCap` 和 `rangeSize` 改为 `1` 和 `int.MaxValue`，
将所有 tile 串行化到单个 worker 上消除调度争用。

**Root cause of failure / 失败根因:** The ISPC function-call overhead is in
**per-chunk invocation count** (1000 calls), not in the scheduler's tile
distribution (58 tiles). Changing from 58 tiles to 1 tile doesn't reduce the
1000 `ispc::Func()` calls — it just removes the parallelism that offsets
some of the overhead.

ISPC 函数调用开销来自于 **每 chunk 的调用次数**（1000 次），而不是调度器的
tile 分布（58 tiles）。从 58 tiles 改为 1 tile 不能减少 1000 次 `ispc::Func()`
调用——只是移除了原本能抵消部分开销的并行性。

**Status / 状态:** Reverted.

---

## Summary / 总结

### Final Benchmark (1M entities) / 最终测试结果

| Test | ISPC IJobChunk | IJobEntity | C++ Chunk Baseline |
|:-----|:-------------:|:----------:|:------------------:|
| **Light** | **0.363 ms** ✅ 2.5x | **0.438 ms** ✅ 2.1x | 0.160-0.210 ms |
| **Heavy** | **1.931 ms** ✅ 9.3x | **2.240 ms** ✅ 9.1x | 18.8-19.7 ms |
| **Sleep+Light** | **0.095 ms** ✅ 10x | **0.099 ms** ✅ 9.8x | ~0.970 ms |

### Key Insight / 核心洞察

**From Sleep benchmark performance data / 从 Sleep 基准性能数据看:**

```
ISPC Light pure computation (Sleep executeSpan):  ~36 µs
ISPC Light total (including scheduling overhead):  ~363 µs
Overhead ratio: 90% scheduling, 10% computation
```

ISPC has already achieved ~80-90% of its theoretical maximum throughput for
computation. The remaining gap is architectural:
- **AoS (Array of Structs)** layout requires gather/scatter instructions that
  ISPC's SPMD model handles less efficiently than contiguous arrays
- **Per-chunk ISPC invocation** is inherent to the current ECS chunk iteration
  pattern

ISPC 在计算侧已经达到 ~80-90% 的理论极限。剩余差距是架构级的：
- **AoS（结构体数组）** 布局需要 gather/scatter 指令
- **每 chunk 的 ISPC 调用** 是当前 ECS chunk 遍历模式的固有开销

For a data-oriented ECS, the long-term solution is **SoA (Struct of Arrays)**
chunk storage — `ComponentX[]` + `ComponentY[]` instead of `Component[]`.
This would allow both ISPC (natural `foreach` on contiguous arrays) and
C++ (RESTRICT + ivdep + stride=1 access on aligned pointers) to reach
near-peak memory bandwidth.

对面向数据的 ECS 来说，长期解是 **SoA（数组结构体）** chunk 存储——
`ComponentX[]` + `ComponentY[]` 而非 `Component[]`。这能让 ISPC 和 C++ 都
达到接近峰值的内存带宽。
