# SIMD 向量化架构文档

## 背景与动机

### 为什么需要自建 SIMD

NativeTranspiler 的 C++ 后端生成的代码遇到复杂循环模式时，MSVC 的自动矢量化器无法有效向量化：

| 障碍 | 具体问题 | 严重程度 |
|---|---|---|
| 条件 reduction | `if (x < best) best = x` — MSVC 不会自动生成 `vminps` | ❌ 致命 |
| AoS 内存布局 | `float2[]` 需要 gather，MSVC 不自动生成 | ⚠️ 阻碍 |
| 间接索引 | `arr[hash[i]]` — 编译器无法分析访问模式 | ❌ 致命 |
| 函数调用链 | `distancesq → lengthsq → dot` — 自动向量化分析难以看穿 | ⚠️ 阻碍 |

ISPC 能解决这些问题，但**平台有限**：

| 平台 | ISPC | 本方案 |
|---|---|---|
| Windows x64 (AVX2/SSE4) | ✅ | ✅ |
| Linux x64 | ✅ | ✅ |
| macOS x64 | ⚠️ 有限 | ✅ |
| **macOS ARM64 (M 系列)** | ❌ **不支持** | ✅ |
| **Linux ARM64** | ❌ **不支持** | ✅ |
| **Windows ARM64** | ❌ **不支持** | ✅ |

本方案的目标：**全平台通用的 C++ SIMD 向量化，不依赖 ISPC。**

---

## 架构总览

```
C# IJobParallelFor.Execute(index)
         │
         ▼
NativeTranspiler (编译时)
         │
         ├─ SimdEligibilityAnalyzer
         │    分析 Execute 体是否安全外层 SIMD
         │    (无 return/break/continue/函数调用)
         │
         ├─ OuterSimdGenerator
         │    ├─ IsSimpleSoaBody()?
         │    │   ├─ true  → 寄存器 SIMD (load → op → store)
         │    │   └─ false → Per-lane 标量 (gather → for(lane) → scatter)
         │    │
         │    └─ 生成 C++ SIMD 代码 + 标量余量循环
         │
         └─ 标量回退 (AutoSIMD=Disabled 或分析失败)
                ↓ 生成 .cpp
         NativeDll (C++ 运行时)
                ├─ NativeSIMD.h  — 底层 SIMD 操作
                ├─ SimdValue.h   — simd_value<T> 类型模板
                └─ EntJoy::Mathematics (float2/int2)
```

---

## 运行时层

### NativeSIMD.h

底层平台抽象，提供 `n_*` 函数。每个操作支持 4 个平台后端：

```cpp
static inline n_float n_add_ps(n_float a, n_float b) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
    return _mm256_add_ps(a, b);
#elif defined(NSIMD_SSE4)
    return _mm_add_ps(a, b);
#elif defined(NSIMD_NEON)
    return vaddq_f32(a, b);
#else
    return a + b;
#endif
}
```

#### 平台检测优先级

| 宏 | NSIMD_WIDTH | 条件 |
|---|---|---|
| `NSIMD_AVX2` | 8 | `__AVX2__`（VC++ `/arch:AVX2`） |
| `NSIMD_AVX` | 8 | `__AVX__`（VC++ `/arch:AVX`） |
| `NSIMD_SSE4` | 4 | `__SSE4_2__` / `__SSE__` / `_M_X64` |
| `NSIMD_NEON` | 4 | `__ARM_NEON` / `__aarch64__` / `_M_ARM64` |
| `NSIMD_SCALAR` | 1 | 以上都不命中 |

#### 支持的操作表

| 类别 | API | AVX2 | SSE4 | NEON | 标量 |
|---|---|---|---|---|---|
| Load | `n_load_ps` | `_mm256_loadu_ps` | `_mm_loadu_ps` | `vld1q_f32` | `*p` |
| Store | `n_store_ps` | `_mm256_storeu_ps` | `_mm_storeu_ps` | `vst1q_f32` | `*p=v` |
| Load int | `n_load_epi32` | `_mm256_loadu_si256` | `_mm_loadu_si128` | `vld1q_s32` | `*p` |
| Store int | `n_store_epi32` | `_mm256_storeu_si256` | `_mm_storeu_si128` | `vst1q_s32` | `*p=v` |
| Broadcast | `n_set1_ps` | `_mm256_set1_ps` | `_mm_set1_ps` | `vdupq_n_f32` | 直接返回 |
| Broadcast int | `n_set1_epi32` | `_mm256_set1_epi32` | `_mm_set1_epi32` | `vdupq_n_s32` | 直接返回 |
| Set int | `n_set_epi32` | `_mm256_set_epi32` | `_mm_set_epi32` | 手动构造 | 直接返回 |
| Add | `n_add_ps` | `_mm256_add_ps` | `_mm_add_ps` | `vaddq_f32` | `a+b` |
| Sub | `n_sub_ps` | `_mm256_sub_ps` | `_mm_sub_ps` | `vsubq_f32` | `a-b` |
| Mul | `n_mul_ps` | `_mm256_mul_ps` | `_mm_mul_ps` | `vmulq_f32` | `a*b` |
| FMA | `n_fmadd_ps` | `_mm256_fmadd_ps` | `_mm_fmadd_ps`(需FMA) | `vfmaq_f32` | `a*b+c` |
| Min | `n_min_ps` | `_mm256_min_ps` | `_mm_min_ps` | `vminq_f32` | `a<b?a:b` |
| Max | `n_max_ps` | `_mm256_max_ps` | `_mm_max_ps` | `vmaxq_f32` | `a>b?a:b` |
| Compare < | `n_cmp_lt_ps` | `_mm256_cmp_ps`(LT) | `_mm_cmplt_ps` | `vcltq_f32` | `a<b` |
| Compare > | `n_cmp_gt_ps` | `_mm256_cmp_ps`(GT) | `_mm_cmpgt_ps` | `vcgtq_f32` | `a>b` |
| Mask And | `n_and_mask` | `_mm256_and_ps` | `_mm_and_ps` | `vandq_u32` | `a&&b` |
| Mask Andnot | `n_andnot_mask` | `_mm256_andnot_ps` | `_mm_andnot_ps` | `vbicq_u32` | `!a&&b` |
| Blend | `n_blend_ps` | `_mm256_blendv_ps` | `_mm_blendv_ps` | `vbslq_f32` | `m?t:f` |
| Blend int | `n_blend_epi32` | `_mm256_blendv_epi8` | `_mm_blendv_epi8` | `vbslq_s32` | `m?t:f` |
| Gather | `n_gather_ps<stride>` | `_mm256_i32gather_ps` | 手动 gather | 手动 gather | 手动 gather |
| Gather int | `n_gather_epi32` | `_mm256_i32gather_epi32` | 手动 gather | 手动 gather | 手动 gather |
| All zero | `n_all_zero` | `_mm256_testz_ps` | `_mm_testz_ps` | `vtstq_u32` | `!mask` |
| HMin | `n_hmin_ps` | shuffle | shuffle | `vpmin_f32` | 直接返回 |
| HMax | `n_hmax_ps` | shuffle | shuffle | `vpmax_f32` | 直接返回 |
| HSum | `n_hsum_ps` | shuffle | shuffle | `vpadd_f32` | 直接返回 |
| HSum int | `n_hsum_epi32` | store+标量 | store+标量 | store+标量 | 直接返回 |
| HMax int | `n_hmax_epi32` | store+标量 | store+标量 | store+标量 | 直接返回 |
| HMin+Idx | `n_hmin_idx` | store+标量 | store+标量 | store+标量 | 直接返回 |
| Int add | `n_add_epi32` | `_mm256_add_epi32` | `_mm_add_epi32` | `vaddq_s32` | `a+b` |

### SimdValue.h — simd_value<T> 类型模板

对齐 `EntJoy.Mathematics` 的类型系统，提供 `simd_value<T>` 模板特化：

```cpp
template<typename T> struct simd_value;

// 基本类型（原生 SIMD 寄存器）
simd_value<float>   → n_float (AVX2: __m256 × 1)
simd_value<int>     → n_int   (AVX2: __m256i × 1)

// 复合类型（拆为多个寄存器）
simd_value<EntJoy::Mathematics::float2>
    → { simd_value<float> x; simd_value<float> y; }  (2 个寄存器)

simd_value<EntJoy::Mathematics::int2>
    → { simd_value<int> x; simd_value<int> y; }       (2 个寄存器)
```

#### 主要操作

| 操作 | simd_value<float> | simd_value<int> | simd_value<float2> |
|---|---|---|---|
| `broadcast(s)` | `n_set1_ps(s)` | `n_set1_epi32(s)` | — |
| `load(p)` | `n_load_ps(p)` | `n_load_epi32(p)` | — |
| `store(p)` | `n_store_ps(p, v)` | `n_store_epi32(p, v)` | — |
| `sequence(n)` | — | `n_set_epi32(n...)` | — |
| `gather(base, idx)` | AoS 字段 gather | int 数组 gather | 同时 gather x/y |
| `+ - *` | 运算符重载 | 运算符重载 | — |
| `min / max` | 全 SIMD | — | — |

#### 全局操作

| API | 功能 |
|---|---|
| `blend(f, t, mask)` | 条件选择 |
| `hmin(v)` | 水平最小值 |
| `hmax(v)` | 水平最大值 |
| `hsum(v)` | 水平求和 |
| `hmin_idx(v_val, v_idx)` | 最小值 + 对应索引 |

#### simd_mask

独立于 `simd_value<T>` 的掩码类型：

```cpp
struct simd_mask {
    n_float m;  // 用 float 寄存器的位表示 mask
    static simd_mask all_true();
    bool all_false() const;
};
```

---

## 编译时分析层

### SimdEligibilityAnalyzer

分析 `Execute` 方法体，判断是否适合外层 SIMD。

**准入条件**（外层严格检查 `CheckStatement`）：
- 无 return / break / continue
- 无 while / do 循环（for 循环允许）
- 无函数调用（`EntJoy.Mathematics.math` 和 `System.MathF` 除外）
- 无间接索引（`arr[hash[i]]`）

**允许的（安全）**：
- for 循环（每通道独立跑自己的，互不干扰）
- if-else（blend 处理）

**for 体内宽松检查 `CheckStatementLoose`**：
- 允许 break/continue（仅影响当前通道，不影响其他 SIMD lane）
- 递归检查嵌套 for / if-else

### OuterSimdGenerator

根据 body 复杂度选择生成路径：

```csharp
public string Generate(string scalarBody)
{
    if (IsSimpleSoaBody())       // 无 for/while/if
        return GenerateRegisterSIMD(scalarBody);
    else                         // 有控制流
        return GeneratePerLane(scalarBody);
}
```

#### 路径 1：寄存器 SIMD（简单 SoA body）

```cpp
// 生成代码示例：pos += vel * dt
for (int si = __startIndex; si < simd_end_; si += NSIMD_WIDTH)
{
    simd_value<float> v_pos = simd_value<float>::load(&pos_ptr[si]);
    simd_value<float> v_vel = simd_value<float>::load(&vel_ptr[si]);
    v_pos = v_pos + v_vel * dt;
    v_pos.store(&pos_ptr[si]);
}
```

**加速**：4-8x（取决于 SIMD 宽度）

#### 路径 2：Per-lane（复杂 body）

```cpp
// 生成代码示例：ClosestPoint 等复杂 body
for (int si = __startIndex; si < simd_end_; si += NSIMD_WIDTH)
{
    simd_value<float2> v_q = gather(QueryPositions_ptr, v_i);  // 8 路 gather（MLP）
    // 从寄存器提取，不走 buffer → 需要 n_extract_lane_f32
    for (int lane = 0; lane < NSIMD_WIDTH; lane++)
    {
        float2 qbuf;
        qbuf.x() = n_extract_lane_f32(v_q.x.v, lane);
        qbuf.y() = n_extract_lane_f32(v_q.y.v, lane);
        // 标量 Execute(lane)
    }
}
```

**加速来源**：gather 的 MLP（memory-level parallelism）
**避免**：buffer store → load 的额外内存流量

---

## per-lane 优化

### 问题

当前 per-lane 实现中，gather 后的值通过 buffer 传递：

```cpp
simd_value<float2> v_q = gather(...);
float qx_buf[8]; v_q.x.store(qx_buf);  // SIMD → 内存
float qy_buf[8]; v_q.y.store(qy_buf);
for (...) {
    qbuf.x() = qx_buf[lane];            // 内存 → 标量
}
```

store → load 的往返抵消了 gather 的 MLP 收益。

### 解决方案

绕过 buffer，直接从 SIMD 寄存器提取每个 lane 的值：

```cpp
simd_value<float2> v_q = gather(...);   // 8 路 gather 仍然在
for (...) {
    qbuf.x() = n_extract_lane_f32(v_q.x.v, lane);
    qbuf.y() = n_extract_lane_f32(v_q.y.v, lane);
}
```

### 新增 API：n_extract_lane_f32

```cpp
static inline float n_extract_lane_f32(n_float v, int lane) {
#if defined(NSIMD_AVX2)
    // AVX2: lane 0-3 → 低 128，lane 4-7 → 高 128
    __m128 lo = _mm256_castps256_ps128(v);
    __m128 hi = _mm256_extractf128_ps(v, 1);
    __m128 sel = lane < 4 ? lo : hi;
    int idx = lane & 3;
    // shuffle 把目标 lane 移到位置 0
    return _mm_cvtss_f32(_mm_shuffle_ps(sel, sel, _MM_SHUFFLE(idx, idx, idx, idx)));
#elif defined(NSIMD_SSE4)
    return _mm_cvtss_f32(_mm_shuffle_ps(v, v, _MM_SHUFFLE(lane, lane, lane, lane)));
#elif defined(NSIMD_NEON)
    return vgetq_lane_f32(v, lane);
#else
    return v;
#endif
}
```

这个优化是 per-lane 路径的关键——保留 gather 的 MLP 收益，消除 buffer 的 store/load 开销。

---

## 平台覆盖

| 平台 | ISA | WIDTH | NativeSIMD.h | SimdValue.h | Exports.cpp 输出 |
|---|---|---|---|---|---|
| Windows x64 (Release) | AVX2 | 8 | ✅ `_mm256_*` | ✅ simd_value<float>/<int>/<float2>/<int2> | `[SIMD] AVX2 8-wide` |
| Windows x64 (Debug) | AVX2 | 8 | ✅ Debug 配置已加 /arch:AVX2 | ✅ | `[SIMD] AVX2 8-wide` |
| Windows x64 (无 AVX2) | SSE4 | 4 | ✅ `_M_X64` 检测 | ✅ 4-wide 自动适配 | `[SIMD] SSE4 4-wide` |
| Linux x64 | AVX2 | 8 | ✅ GCC/Clang `__AVX2__` | ✅ | `[SIMD] AVX2 8-wide` |
| macOS x64 | AVX2/SSE4 | 8/4 | ✅ Apple Clang | ✅ | 自动检测 |
| macOS ARM64 (M1/M2/M3) | NEON | 4 | ✅ `arm_neon.h` + 手动 gather | ✅ | `[SIMD] NEON 4-wide` |
| Linux ARM64 | NEON | 4 | ✅ | ✅ | `[SIMD] NEON 4-wide` |
| Windows ARM64 | NEON | 4 | ✅ `_M_ARM64` | ✅ | `[SIMD] NEON 4-wide` |

### NEON 支持细节

NEON 没有 gather 指令（`vld1q_f32` 只能连续加载）。`n_gather_ps<stride>` 在 NEON 上回退到手动标量循环：

```cpp
static inline n_float n_gather_ps(const float* base, n_int indices) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
    return _mm256_i32gather_ps(base, indices, stride);
#else
    int idx[8]; n_store_epi32(idx, indices);
    float val[8];
    for (int i = 0; i < WIDTH; i++)
        val[i] = *(const float*)((const char*)base + idx[i] * stride);
    return n_load_ps(val);
#endif
}
```

虽然 gather 在 NEON 上是标量间接加载，但外层 SIMD 的 **MLP 收益**仍然存在——8 个（AVX2）或 4 个（NEON）独立的内存请求可以并行执行。

---

## 性能预期

### 寄存器 SIMD 路径（简单 SoA 算术）

| 场景 | NSIMD_WIDTH=8 | NSIMD_WIDTH=4 | 说明 |
|---|---|---|---|
| `pos += vel * dt` | **~8x** | **~4x** | 连续 load → SIMD 算术 → store |
| `total += mass[i] * acc[i]` | **~6x** | **~3x** | 乘加 + 水平规约 |
| `if (val > max) max = val` | **~6x** | **~3x** | max reduction + blend |

### Per-lane 路径（复杂 body）

| 场景 | 预期 | 说明 |
|---|---|---|
| `ClosestPoint` | ≈ 标量（或略差） | gather 8 queries 的 MLP 收益被控制流开销抵消 |
| 复杂 ECS（有 if 但 gatherable） | **1.2-1.5x** | 输入 gather 有一定 MLP 收益 |

---

## 代码结构

```
src/
├── NativeDll/
│   ├── NativeSIMD.h       ← 底层 SIMD 操作（平台检测 + n_* 函数）
│   ├── SimdValue.h        ← simd_value<T> 类型模板
│   ├── NativeMath.h       ← float2/int2 数学类型（包含 NativeSIMD.h）
│   └── Exports.cpp        ← DLL 入口（输出 [SIMD] 信息）
│
└── NativeTranspiler/
    └── Analyzer/
        ├── SimdEligibilityAnalyzer.cs  ← Execute 体 SIMD 安全性分析
        ├── OuterSimdGenerator.cs       ← 外层 SIMD 代码生成
        └── CppJobGenerator.cs          ← 调度标量/SIMD + 适配函数生成
            ├─ GenerateJobHeader        ─ .h 头文件
            ├─ GenerateJobImplementation─ .cpp 实现（IJob / IJobChunk / IJobEntity / ParallelFor）
            │   ├─ 标量（MSVC ivdep）
            │   └─ AutoSIMD=Enabled
            │       ├─ Register SIMD（简单 SoA）
            │       └─ Per-lane（复杂 body，寄存器提取）
            ├─ GenerateJobAdapter       ─ 适配函数（消除 C# 委托桥接）
            │   ├─ _Adapter（ParallelFor）
            │   ├─ _RangeAdapter（IJobChunk 范围调度）
            │   └─ _EntityBatchAdapter（IJobEntity 批量调度）
            └─ 辅助函数集               ─ CalculateFieldOffset / GetCppElementType / …（~500 行）
```

## 关键文件说明

| 文件 | 职责 | 行数 |
|---|---|---|
| `NativeSIMD.h` | 平台检测、所有 n_* 函数、水平规约、gather、lane 提取 | ~520 |
| `SimdValue.h` | simd_value<float>/<int>/<float2>/<int2>、blend、hmin/hmax/hsum | ~130 |
| `SimdEligibilityAnalyzer.cs` | 分析 Execute 体是否安全外层 SIMD（两层校验：外层严格，for 体内宽松） | ~270 |
| `OuterSimdGenerator.cs` | 寄存器 SIMD 或 per-lane（寄存器提取）代码生成 | ~200 |
| `CppJobGenerator.cs` | AutoSIMD 分派、Standard/Variant/Bool 变体、适配函数生成（Adapter/RangeAdapter/EntityBatchAdapter） | ~1380 |

---

## 后续优化方向

1. ~~**per-lane 寄存器提取**：已实现 `n_extract_lane_f32`/`n_extract_lane_epi32`，OuterSimdGenerator 已使用寄存器代替 buffer，保留 gather MLP 收益~~ ✅
2. **for_masked 内层循环**：对包含内循环的 body 在 SIMD 层面处理
3. **IJobEntity 适配**：ECS 组件系统天然 SoA，寄存器 SIMD 路径收益最大
4. **IJobChunk 适配**：chunk 内组件连续数组，与 IJobEntity 同理
5. **NEON gather 优化**：NEON 无 gather，但可用 `vld4q_f32` 做结构化加载优化 AoS 访问
