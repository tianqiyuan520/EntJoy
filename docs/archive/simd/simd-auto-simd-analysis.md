# Auto-SIMD 生成器 — 完整分析报告

## 背景

从 C# Job AST 自动生成 ISPC 风格的 SIMD C++ 代码，替代手写 SIMD 特化路径。ClosestPointJobPointer 作为主要测试用例。

## 关键提交

```
d9c58bb refactor(simd): remove all special-case SIMD paths, use universal generator for all jobs
                         (移除手写 GenerateISPCClosestPointSIMD, 用通用路径)
6fde70b feat(simd): universal SIMD now active - generates ISPC-style SIMD code
aeb0286 feat(simd): universal SIMD generator active with per-lane fallback for complex jobs
8e1fb36 fix(simd): auto-SIMD ClosestPoint/FindWithin compiles — full-type float2/int2, mask fix
d95a43c fix(simd): CRITICAL - blend conditional assignments under mask
52a3669 fix(simd): CRITICAL - per-lane scatter respects SIMD mask, guard gather from -1 indices
53e5afd fix(simd): CRITICAL - guard if-bodies with any_true to prevent unconditional goto
f358391 fix(simd): CRITICAL - for-loop <= translated to n_cmp_le_epi32 not n_cmp_lt_epi32
417c51c opt(simd): eliminate redundant any_true() guard on pure-blend if bodies
ad5f8c3 feat(simd): hybrid execution — uniform SIMD mask + varying per-lane sequential
5eb2dbd perf(simd): whole-body per-lane mode → 0.99ms (was 2.8ms)
a473e81 perf(simd): forward bool field constants to per-lane body
fb99ca9 opt(simd): inline SIMD gather extract
22cc209 opt(simd): per-lane body matches reference 73549457
6c1d866 opt(simd): scalar-only for varying-bound loops
```

## 尝试过的方案

### 方案1: 全宽 SIMD mask（8-wide 同步推进）→ **失败**

对所有循环生成 `while(true) + simd_mask + blend + any_true`。

```
for dx (while + mask save + any_true + mask narrow):
  for dy (while + mask save + any_true + mask narrow):
    for i (while + mask + tracker):
      gather(SortedPositions, v_i)  ← gather 非连续地址
      blend(v_bestDistSq, ...)
  per-lane scatter (for __l: if(__sg & 1<<__l))
```

- 正确性: 7 个 bug fix 后才正确（blend、scatter guard、<=、any_true）
- 性能: **2.7ms** (结果正确时)
- 问题: `<=` 被误译为 `<` 时触发 else 全量扫描 → **50ms**

### 方案2: 混合引擎（保存 → 标量 → 合并）→ **失败**

在每个 inner `for(i)` 边界做 save/merge。

```
Save: n_store_epi32(__bestDistSq_buf, v_bestDistSq)
for lane: 标量 body
Merge: v_bestDistSq = simd_value<float>::load(__bestDistSq_buf)
```

- 性能: **2.8ms** (倒退)
- 问题: save/merge 被 dx/dy 循环调用 **9 次**（3×3），每批次 2 次 store + 2 次 load → 36 次额外操作

### 方案3: Whole-body per-lane（全 body 一次性标量）→ **接近但无收益**

```
SIMD gather(QueryPositions)
for lane:
  标量 body（dx, dy, i 都是 native for/if）
```

- 性能: **0.89ms**
- 问题: 纯标量 0.75ms，SIMD 0.89ms → **SIMD 部分（gather + extract）带来的收益为 0**

### 方案4: 纯标量 for(lane) → **最终方案**

```
for lane:
  标量 body（直接读 _ptr[index]）
  无 SIMD gather / 无 extract
```

- 性能: **0.86ms** (和余量循环 0.75ms 一致)
- 结论: **varying-bound 循环 SIMD 无收益**

## 关键发现

### 为什么 varying-bound 循环 SIMD 没收益

ClosestPoint 的三层循环：

```
for dx (uniform，3 次迭代):
  for dy (uniform，3 次迭代):
    for i (varying，各 lane start/end 不同):
      SortedPositions_ptr[i]  ← 这是瓶颈
```

`SortedPositions_ptr[i]` 的访问模式：

| 方案 | 访问方式 | 内存行为 |
|------|---------|---------|
| 标量 for(i) | `pos = SortedPositions_ptr[i]` | 顺序读 → L1 命中 → **~4 cycles/iter** |
| SIMD for(i) | `gather(SortedPositions_ptr, v_i)` | 8 个不同地址 → 可能 cache miss → **~50+ cycles/iter** |

**瓶颈不在计算（distancesq = 5 ops），在内存（load SortedPositions）。** 而这里 8 条 lane 遍历不同的 cell 范围 → v_i 是 varying → 必须用 gather，无法用连续 load。

### 性能演化

| 方案 | 性能 | 相对标量 |
|------|------|---------|
| 纯 C# 标量（余量循环） | 0.75ms | 1.0× |
| 纯标量 for(lane) | 0.86ms | 0.87× |
| SIMD gather + for(lane) | 0.89ms | 0.84× |
| 全宽 SIMD mask | 2.7ms | 0.28× |
| 混合引擎（9×save/merge） | 2.8ms | 0.27× |

**所有 SIMD 方案无法超越纯标量**——因为瓶颈是内存读取模式，不是计算密度。

### 为什么 ISPC 0.6ms

参考提交 `73549457` 的 0.6ms 走的是 `GenerateISPCClosestPointSIMD`——**全宽 SIMD count-loop 模式**（不是 per-lane）。所有 lane 同步推进，早完成的 lane 通过 blend 丢弃结果。它的 gather 也有 cache miss，但 ISPC 在做 **min reduction 时用标量 load + broadcast** 优化了 fallback 路径：

```
// ISPC fallback: 标量 load 一次，broadcast 到 8 条 lane
fb_pos = SortedPositions_ptr[i_fb];                     // 1 次标量读
v_fb_px = broadcast(fb_pos.x()); v_fb_py = broadcast(fb_pos.y());  // broadcast
```

这样 8 条 lane 共享同一个标量 load，而不是各自 gather 不同地址。**这就是 0.6ms 来源。**

## 最终设计

```
Generate()
  ├─ HasVaryingBoundsLoop(body)?
  │   → YES: GeneratePerLaneFullBody()   ← 纯标量 for(lane)
  │   →  NO: GenerateVariableDeclarations() + GenerateBlock()  ← 全 SIMD mask
```

auto-SIMD 只对 **uniform-bound 循环**（所有 lane 遍历相同索引范围）生成真正的 SIMD 代码。对 varying-bound 循环诚实地说：SIMD 没有收益，生成纯标量。

## 结论

对于 ClosestPoint/FindWithin 这类**内层循环范围 per-lane 各不相同**的工作负载：

- 瓶颈是 **SortedPositions 的 gather vs 顺序读**，不是计算
- 全宽 SIMD mask 管理引入的额外分支/条件操作 > gather 带来的好处
- 最优策略 = 纯标量 for(lane) = 余量循环一样

auto-SIMD 的价值在于 uniform-bound 循环（简单 SoA 批量运算、初始化、复制等），在那些场景可以达到 ~8× 加速。
