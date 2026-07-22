# ISPC vs C++ SIMD — ClosestPoint 内循环对比分析

## 性能数据

| 版本 | 核心查询耗时 | 总查询耗时 | vs ISPC |
|------|------------|-----------|---------|
| C++ SIMD per-lane (寄存器提取) | 0.860 ms | 0.916 ms | 基准 |
| ISPC foreach | **0.583 ms** | **0.654 ms** | **~40% 更快** |

## ISPC LLVM IR 关键分析

生成命令：
```
ispc SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_Batch.ispc --emit-llvm-text -o closestpoint_ispc.ll --target=avx2-i32x8 --opt=fast-math
```

### 1. 外层 SIMD 映射（foreach → SIMD lanes）

ISPC 将 `foreach (index = ...)` 直接编译为 **8-wide AVX2 SIMD**，每个 lane 对应一个 query index：

```llvm
%iter_val54 = ...  ; per-lane index values
%mul__index_load61 = shl nsw <8 x i32> %iter_val54, splat (i32 3)  ; stride=8
```

对比我们的 C++ per-lane：
```cpp
for (int lane = 0; lane < NSIMD_WIDTH; lane++) {
    int index = si + lane;
    // 标量提取
}
```

**ISPC 优势**：不需要标量提取/插入循环，全部在 SIMD 寄存器中操作。

### 2. AoS gather（结构化加载）

QueryPositions（AoS float2，stride=8）：
```llvm
%v_1.i = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(
    <8 x float> undef,
    ptr readonly %QueryPositions_ptr,     ; base
    <8 x i32> %mul__index_load61,          ; offsets (index * 8)
    <8 x float> splat (float 0xFFFFFFFFE0000000),  ; mask
    i8 1)                                  ; scale=1
```

**注意**：C++ 的 `simd_value<float2>::gather` 也是同样的 `_mm256_i32gather_ps`，**无差别**。

### 3. 内层循环 SortedPositions 访问

最热路径 — 每个 query lane 遍历各自 `[start, end)` 范围的 SortedPositions：

```llvm
; 内层 for (int i = start; i < end; i++)
; ISPC 使用 mask 控制每个 lane 的活跃状态
for_loop:
  %"oldMask&test4604" = phi <8 x i32> [...]          ; 活跃 mask
  %bestIdx.04602 = phi <8 x i32> [...]                  ; 8-wide bestIdx
  %bestDistSq.04601 = phi <8 x float> [...]              ; 8-wide bestDistSq

  ; SortedPositions AoS gather (float2, stride=8)
  %v_1.i4182 = call <8 x float> @llvm.x86.avx2.gather.d.ps.256(
      <8 x float> undef, ptr readonly %SortedPositions_ptr,
      <8 x i32> %mul__i_load263,
      <8 x float> %mask.i, i8 1)

  ; distancesq: SIMD 距离计算
  ; ... fsub/fmul/fmadd ...

  ; min reduction: blendvps 条件更新 bestDistSq + bestIdx
  %less_distSq_load316_bestDistSq_load = fcmp olt <8 x float> %distSq, %bestDistSq.44588
  %blend.i.i = call <8 x float> @llvm.x86.avx.blendv.ps.256(
      %bestDistSq.44588, %distSq, %mask_as_float.i.i)
```

### 4. CellStartEnd 随机访问

```llvm
; int2 range = CellStartEnd[cellHash]
%v_1.i4180 = tail call <8 x i32> @llvm.x86.avx2.gather.d.d.256(
    <8 x i32> undef, ptr readonly %CellStartEnd..._data,
    <8 x i32> %mul__cellHash_load,
    <8 x i32> %new_mask185, i8 1)
```

int2（8 字节）的 gather，**不可避免**。

---

## 差异分析：ISPC vs C++ per-lane

### 差距 1：外层标量提取循环（~15% 开销）

| | ISPC | C++ per-lane |
|--|------|-------------|
| 外层映射 | `foreach` → 8 SIMD lanes 全部寄存器 | `for(lane) + n_extract_lane_f32` 逐个提取 |
| 控制流 | mask 管理，无标量提取 | 每个 lane 提取 qbuf 后走标量路径 |

ISPC 在内层循环的 mask 切换（如 nx/ny 边界检查）使用 `select <8 x i1>` 而不是 CPU 分支，无分支预测失败。

### 差距 2：mask 控制流（~15% 开销）

C++ per-lane 中每个 `if (nx >= GridDims.x) continue` 都是 CPU 分支。不同 lane 的分支行为不同时 → 分支预测惩罚。

ISPC 的 mask 模式：
```llvm
; 等效于 if ((uint)nx >= (uint)GridDims.x) continue
%greaterequal = icmp ult <8 x i32> %nx, %GridDims.x
%"oldMask&test122" = select <8 x i1> %greaterequal,
    <8 x i32> zeroinitializer, <8 x i32> %oldMask
```

### 差距 3：性能向量化 vs 寄存器利用率（~10% 开销）

ISPC 在内层循环中，`bestDistSq` 和 `bestIdx` 始终保持为 **8-wide SIMD 寄存器**，仅在循环结束后才需要标量化的结果。

C++ per-lane 每条 lane 有自己的标量 `bestDistSq`/`bestIdx`，完全在标量域运行。

---

## 结论

**核心差异不在 gather 模式（同样使用 AoS gather），而在控制流和寄存器利用**：

1. **ISPC 的 foreach 直接将 8 个 query 映射到 SIMD lanes**，不需要 `extract_lane` → 标量运算 → 写回的 roundtrip
2. **ISPC 的 mask 管理避免 CPU 分支预测惩罚**，特别是 nx/ny 边界检查这种 per-lane 不同的条件
3. **ISPC 维持 8-wide 寄存器**（bestDistSq/bestIdx）在整个内循环中

### 优化方向

如果要让 C++ SIMD 路径达到 ISPC 级别性能，需要：

1. **消除 `for(lane)` 提取循环** — 这是结构性问题，C++ 无法像 ISPC 的 foreach 那样在保持 `varying` 语义的同时做 SIMD
2. **改用 SoA 布局** — `SortedPositions_x[]` + `SortedPositions_y[]` 替换 `SortedPositions[i].x`/`.y`，让内层循环的加载变为 unit-stride load 而非 gather

**结论**：per-lane 路径的性能上限受限于标量提取循环的开销。要接近 ISPC 0.6ms 水平，需要 SoA 布局 + 内层循环向量化（即之前计划中的 `GenerateReductionSIMD` 路径）。详见 `simd-architecture.md` 中 `for_masked 内层循环` 优化方向。
