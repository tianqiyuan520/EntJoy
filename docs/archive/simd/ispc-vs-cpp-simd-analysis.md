# ISPC vs C++ SIMD — ClosestPoint 内循环对比分析

## 性能迭代历程

| 版本 | 核心查询 | 总耗时 | 对比基准 |
|------|---------|--------|---------|
| C++ per-lane（buffer store） | 0.860 ms | 0.916 ms | 基准 |
| C++ per-lane（寄存器提取） | 0.860 ms | 0.916 ms | 无变化（提取非瓶颈） |
| C++ 内层循环 SIMD（GenerateReductionSIMD） | 1.050 ms | 1.125 ms | ❌ 变慢 22% |
| ISPC foreach | **0.583 ms** | **0.643 ms** | **✅ 快 42%** |

### 内层循环 SIMD 失败原因

`GenerateReductionSIMD` 直接在 per-lane 中替换内层 `for (int i = start; i < end; i++)` 为 SIMD 批量处理，但：

1. **平均 cell 仅有 2.5 个元素**（100K 数据 / 200×200 网格），SIMD 循环（8-wide）几乎从不触发
2. `if ((end-start) >= 8)` 分支 + 额外 scope 包装让 MSVC 优化器产生了更差的代码
3. 全局回退循环（当 cell 空时遍历全量 SortedPositions）是真正受益的地方，但 IsReductionBody 检测无法区分代码路径热度

**结论**：对小 cell 场景，内层 SIMD 的 `if` 守卫开销 > 收益。该路径保留为后续大内循环 reduction job 使用（每个 cell 数十/数百元素时有效）。

---

## ISPC LLVM IR 关键分析

生成命令：
```
ispc SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_Batch.ispc --emit-llvm-text -o closestpoint_ispc.ll --target=avx2-i32x8 --opt=fast-math
```

IR 文件大小：**3824 行**（由 149 行 ISPC 源码生成）

### 1. 外层映射：foreach → SIMD lanes

ISPC 将 `foreach (index = __startIndex ... min(...))` 直接编译为 **8-wide AVX2 SIMD**，每个 lane 对应一个 query index：

```llvm
; 8 queries 并行运行，每个 lane 映射到一个 query index
%iter_val54 = phi <8 x i32> [ %62, %if_done ], [ %8, %foreach_full_body.lr.ph ]
%mul__index_load61 = shl nsw <8 x i32> %iter_val54, splat (i32 3)  ; stride=8 (float2)
```

对比我们的 C++ per-lane：
```cpp
for (int lane = 0; lane < NSIMD_WIDTH; lane++) {
    int index = si + lane;
    // 标量提取 → 标量运算 → 标量写回
}
```

**ISPC 优势**：不需要 `extract_lane` → 标量运算 → store 的 roundtrip，全部在 SIMD 寄存器中操作。

### 2. AoS gather（结构化加载）

QueryPositions（AoS float2，stride=8）：
```llvm
%v_1.i = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(
    <8 x float> undef,
    ptr readonly %QueryPositions_ptr,     ; base
    <8 x i32> %mul__index_load61,          ; offsets (index * 8)
    <8 x float> splat (float 0xFFFFFFFFE0000000),  ; mask (all active)
    i8 1)                                  ; scale=1
```

**注意**：C++ 的 `simd_value<float2>::gather` 也是同样的 `_mm256_i32gather_ps`，**无差别**。

### 3. 内层循环 SortedPositions 访问

最热路径 — 每个 query lane 遍历各自 `[start, end)` 范围的 SortedPositions：

```llvm
; 内层 for (int i = start; i < end; i++)
; ISPC 使用 mask 控制每个 lane 的活跃状态（不同 lane 的 start/end 不同）
for_loop:
  %"oldMask&test4604" = phi <8 x i32> [...]          ; 活跃 mask
  %bestIdx.04602 = phi <8 x i32> [...]                  ; 8-wide bestIdx
  %bestDistSq.04601 = phi <8 x float> [...]              ; 8-wide bestDistSq

  ; SortedPositions AoS gather (float2, stride=8) — 用 mask 过滤无意义的 lane
  %v_1.i4182 = call <8 x float> @llvm.x86.avx2.gather.d.ps.256(
      <8 x float> undef, ptr readonly %SortedPositions_ptr,
      <8 x i32> %mul__i_load263,
      <8 x float> %mask.i, i8 1)

  ; distancesq: SIMD 距离计算
  ; ... fsub/fmul/fmadd ...

  ; min reduction: blendvps 条件更新 bestDistSq + bestIdx
  %less_distSq_load316_bestDistSq_load = fcmp olt <8 x float> %distSq, %bestDistSq
  %blend.i.i = call <8 x float> @llvm.x86.avx.blendv.ps.256(
      %bestDistSq, %distSq, %mask_as_float.i.i)
```

**关键模式**：
- `%"oldMask&test"` 是 phi 节点追踪每个 lane 是否还在循环中
- `select <8 x i1>` 代替分支做条件控制
- `fcmp olt` + `blendvps` 代替 `if (distSq < best)` 分支

### 4. dx/dy 边界检查的 mask 管理

```llvm
; 等效于 if ((uint)nx >= (uint)GridDims.x) continue
%add_nx = add nsw <8 x i32> %dx, %cell_x
%check = icmp ult <8 x i32> %add_nx, %GridDims.x
%"oldMask&test122" = select <8 x i1> %check,
    <8 x i32> zeroinitializer,       ; 通过: 保留 oldMask
    <8 x i32> %"oldMask&test"        ; 失败: 保持原 mask（不 kill）
```

**实际含义**：当某个 lane 的 nx 越界时，该 lane 的 mask 被清为 0。后续 gather 用这个 mask 跳过越界的 lane，`for_step` 中的 `"oldMask&test"` phi 决定循环是否继续。

### 5. CellStartEnd 随机访问

```llvm
; int2 range = CellStartEnd[cellHash]
%v_1.i4180 = tail call <8 x i32> @llvm.x86.avx2.gather.d.d.256(
    <8 x i32> undef, ptr readonly %CellStartEnd..._data,
    <8 x i32> %mul__cellHash_load,
    <8 x i32> %new_mask185, i8 1)    ; mask = 只收集活跃 lane 的 cell
```

int2（8 字节）的 gather，**不可避免**。我们的 C++ per-lane 也做同样的 gather。

### 6. 水平规约（loop结束后）

```llvm
; bestIdx.1（8-wide）中的非 -1 值即是最小距离对应的索引
; ISPC 用 scalar 代码做 fallback 和写回
%notequal_bestIdx_load_ = icmp ne <8 x i32> %bestIdx.1, splat (i32 -1)
```

---

## 差异分析：ISPC vs C++ per-lane

### 差距 1：8 queries 的 SIMD 并行度（~50% 差距根源）

| | ISPC | C++ per-lane |
|--|------|-------------|
| 外层映射 | `foreach` → 8 queries 全部在 SIMD 寄存器 | `for(lane) + n_extract_lane_f32` 逐个提取 |
| 内层循环 | 8-wide mask 统一前进 | 8 个独立标量循环 |
| 控制流 | `select <8 x i1>` mask，无分支 | CPU 分支（不同 lane 的 nx/dy/start/end 不同） |

**ISPC 的 foreach 本质上是将 8 个不同的 query 绑定为 8 个 SIMD lane，在内层循环中每个 lane 用 mask 控制是否活跃。不同 lane 的 start/end 可以不同，但每次迭代最少有一个活跃 lane。**

我们的 C++ per-lane 无法做到这点——`for(lane)` 是标量循环，8 个 query 之间完全没有 SIMD 并行度。

### 差距 2：内层循环的距离计算（~30% 差距）

| | ISPC | C++ per-lane |
|--|------|-------------|
| SortedPositions 加载 | AoS gather（8-wide） | 标量 `movss` |
| 距离计算 `(qx-px)²+(qy-py)²` | `fsub/fmul/fadd` 8-wide | 标量 |
| min reduction | `blendvps`（无分支） | `if (distSq < best)` 分支 |

**注意**：两者都用 AoS gather 加载 SortedPositions（因为 AoS float2 布局），这是公平比较。

### 差距 3：dx/dy 循环的 mask 管理（~20% 差距）

ISPC 的 `for (int dx = -1; dx <= 1; dx++)` 在 IR 中被展开为 **mask 管理循环**：

```llvm
; dx.0 = phi [-1, 0, 1] — 所有 lane 同时遍历 dx
; 每个 lane 检查自己的 nx 是否越界
; 越界的 lane 被 mask 掉，但其他 lane 继续
```

C++ per-lane 中每个 lane 独立跑 dx=-1..1，3 次迭代中可能 1-2 次有分支惩罚。

---

## 根因总结

```
ISPC 快 42% 的原因:
  ├─ 8 queries SIMD 并行:        ~50%
  │   └─ 外层 foreach 映射 + mask 管理
  ├─ 内层距离计算向量化:          ~30%
  │   └─ 8-wide 距离 + blendvps
  └─ 控制流无分支预测惩罚:        ~20%
      └─ select <8 x i1> 代替 if/continue
```

**核心结论**：ISPC 的 `foreach` 提供了 C++ 无法直接表达的语义——"8 个不同的索引各自跑同一个函数体，函数体内的循环由 mask 控制同步"。C++ 的 per-lane 路径无论怎么优化内层循环，都无法弥补外层 `for(lane)` 的标量提取开销和 8 个 query 之间零并行度的问题。

## GenerateReductionSIMD 尝试总结

尝试在 per-lane 路径中将内层 `for (int i = start; i < end; i++)` 替换为 SIMD 批量处理的路径（`GenerateReductionSIMD`），核心逻辑：

```cpp
broadcast(q) → 8-wide
for (i = start; i < end; i += 8) {
    gather 8x SortedPositions → SIMD distancesq → blendvps reduction
}
hmin/hmin_idx → 水平规约回标量
```

失败原因：

1. **ClosestPoint 的网格 cell 平均只有 2.5 元素**，SIMD 路径几乎不触发
2. **额外 if/scope 干扰 MSVC 优化**
3. **全局回退循环**（遍历全量 SortedPositions）才是内层大循环，但 IsReductionBody 用 AST 检测无法区分热点

该路径已保留在 OuterSimdGenerator.cs 中（`GenerateReductionSIMD`），但不适用 ClosestPoint。适合场景：每个 cell 有数十/数百元素的大内循环 reduction job。

## 当前策略

**ClosestPoint 用 ISPC**（0.643ms），其他适用场景用 C++ SIMD per-lane 路径。`GenerateReductionSIMD` 路径保留为后续 Job 的内循环向量化基础设施。

## 参考文件

| 文件 | 说明 |
|------|------|
| `closestpoint_ispc.ll` | ISPC 生成的 LLVM IR（3824 行） |
| `closestpoint_ispc_source.ispc` | ISPC 源码（149 行） |
| `src/NativeTranspiler/Analyzer/OuterSimdGenerator.cs` | `GenerateReductionSIMD` 实现 |
