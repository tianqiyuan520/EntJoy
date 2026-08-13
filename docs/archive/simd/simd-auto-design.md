# Auto-SIMD 设计文档

## 一、概述

Auto-SIMD 是 NativeTranspiler 的一个特性：在 C# 方法/结构体上标注 `[NativeTranspile(AutoSIMD = Enabled)]`，转译器自动生成 SIMD 向量化的 C++ 代码，替代标量 CRT 函数。

### 支持范围

| 维度 | 说明 |
|---|---|
| 执行模型 | 静态函数（static method）、IJob、IJobFor、IJobParallelFor |
| 目标语言 | C++（MSVC 编译） |
| 后端 | AVX2 (8-wide)、SSE4 (4-wide)、NEON (4-wide) |
| 精度 | SIMD_MATH_PRECISION=1(Fastest ~3.5ULP) / 2(High ~1.0ULP) / 3(IEEE scalar) |

---

## 二、架构分层

```
C# 源码  →  Source Generator (NativeTranspilerGenerator)
                    │
                    ▼
              CppGenerator / CppJobGenerator
                    │
                    ▼
              ┌─────┴─────┐
              │           │
        静态方法       Job 结构体
         (static)   (IJob/For/PF)
              │           │
              ▼           ▼
    SimdControlFlowGenerator
              │
              ▼
         n_xxx_ps 抽象层 (NativeSIMD.h)
              │
              ▼
       AVX2 / SSE4 / NEON / Scalar
```

### 2.1 路径选择

**静态方法** → `CppGenerator.GenerateSimdViaCFG()`：
- StrideAnalyzer 自动选择最佳向量化维度（外层/内层）
- 外层向量化：批循环 + SimdControlFlowGenerator
- 内层向量化：标量外层 + SIMD 内层（归约模式）

**Job 结构体** → `CppJobGenerator`：
- IJob → `SimdControlFlowGenerator` 直接生成
- IJobFor/PF → `OuterSimdGenerator` + `SimdControlFlowGenerator`

---

## 三、StrideAnalyzer — 符号表达式分析

核心算法：对数组索引表达式 `expr(loop_vars)` 关于每个循环变量求偏导系数，选 stride 最小的变量向量化。

```csharp
// 输入: a[i * 100 + j]
//   ∂/∂i = 100 → stride 400 bytes → 向量化 i 需 gather
//   ∂/∂j = 1   → stride 4 bytes  → 向量化 j 连续 load ✅
```

### 实现

`GetStrideCoeff(ExpressionSyntax expr, string var)` 递归 AST 树求导：
- `IdentifierNameSyntax(var)` → 1
- `LiteralExpressionSyntax` → 0
- `BinaryExpression(*)` → 系数相乘
- `BinaryExpression(+/-)` → 系数相加
- `ParenthesizedExpression/CastExpression` → 递归

### 间接 gather 检测

两种模式：
1. 直接：`a[b[i]]` → 参数是 `ElementAccessExpressionSyntax`
2. 变量中转：`idx = b[i]; a[idx]` → 扫描赋值/声明，跟踪变量来源

检测到间接 gather 时，回退到外层向量化（更多并行）。

---

## 四、代码生成

### 4.1 外层向量化（默认路径）

```cpp
// C#: for (int i = 0; i < count; i++) result[i] = a[i] * b[i] + c[i];

int vec_count = (count / NSIMD_WIDTH) * NSIMD_WIDTH;
simd_value<int> v_base = simd_value<int>::sequence(0);
if (vec_count > 0) {
    for (int si = 0; si < vec_count; si += NSIMD_WIDTH) {
        simd_value<int> v_i = v_base + si;
        // SimdControlFlowGenerator 生成:
        //   a[v_i] → gathf(a_ptr, v_i.v) 或 n_load_ps(a_ptr + si) [连续优化]
        //   b[v_i] → same
        //   result[v_i] = ... → per-lane scatter 或 n_store_ps [连续优化]
    }
}
// 标量 remainder
for (int i = vec_count; i < count; i++) { ... }
```

### 4.2 内层向量化（归约模式）

```cpp
// C#: for (int i = 0; i < count; i++) {
//        for (int j = 0; j < 100; j++) {
//          if (a[i*100+j] < best) best = a[i*100+j];
//        } result[i] = best; }

for (int i = 0; i < count; i++) {
    n_float v_best = n_set1_ps(FLT_MAX);  // 或 -FLT_MAX (max)
    int base = i * 100;
    for (int j = 0; j < 100; j += NSIMD_WIDTH) {
        v_best = n_min_ps(v_best, n_load_ps(a_ptr + base + j));
        //       ↑ n_max_ps 如果 if (v > best)
    }
    // horizontal min
    float lane[NSIMD_WIDTH]; n_store_ps(lane, v_best);
    float h = lane[0];
    for (int i = 1; i < NSIMD_WIDTH; i++)
        if (lane[i] < h) h = lane[i];  // 比较方向由 stride analyzer 推导
    result_ptr[i] = h;
}
```

### 4.3 连续索引优化

在 `TranslateElementAccess` / `TranslateAssignment` 中：

```cpp
// 裸 SIMD 索引 → 连续 load/store
v_i                      → n_load_ps(ptr + si)     // exact match

// uniform 基准 + SIMD 索引 → 连续 load/store
i*100 + v_j              → n_load_ps(ptr + (i*100) + si)  // 扩展匹配

// 其他 → gather/scatter (SimdControlFlowGenerator 通用路径)
v_i * 100 + v_j          → gathf(ptr, v_i.v)      // 非连续
dataX[idx]               → gathf(ptr, idx.v)       // 间接 gather
```

---

## 五、SimdControlFlowGenerator — 统一 SIMD 控制流

所有控制流模式通过 mask push/pop 实现全 SIMD 向量化：

| C# 模式 | SIMD 生成策略 |
|---|---|
| `for (int i = 0; i < N; i++)` | 标量循环 + SIMD broadcast / 批循环 |
| `if/else if/else` | mask push/pop + blend |
| `break` | kill lane via simd_tracker |
| `continue` | goto body-end label |
| `return` | goto __simd_func_exit |
| `MathF.Sin(x)` | n_sin_ps(x) → 内联 AVX2 多项式 |
| `a[i] = expr` | per-lane scatter / n_store_ps（连续优化）|

---

## 六、内联数学函数

从 SLEEF 源码 `sleefsimdsp.c` 提取系数，用 `n_xxx_ps` 抽象层重写：

| 函数 | 来源 | 多项式项数 | 实现 |
|---|---|---|---|
| sin | xsinf (u35) | 4项 | `_n_sin_avx2` |
| cos | xcosf / sin(x+π/2) | 4项 | `_n_cos_avx2` |
| log | xlogf | 5项 | `_n_log_avx2` |
| sqrt | HW 指令 | — | `n_sqrt_ps` |
| tan | sin/cos | — | 待实现 |
| exp | xexpf | — | 待实现 |

所有函数 `static inline`，零函数调用开销。

---

## 七、NativeSIMD 抽象层

跨平台 SIMD 类型和函数，一份代码适配所有后端：

| 平台 | n_float | n_int | n_mask | NSIMD_WIDTH |
|---|---|---|---|---|
| AVX2 | `__m256` | `__m256i` | `__m256` | 8 |
| SSE4 | `__m128` | `__m128i` | `__m128` | 4 |
| NEON | `float32x4_t` | `int32x4_t` | `uint32x4_t` | 4 |
| Scalar | `float` | `int` | `bool` | 1 |

提供的运算：
- load/store: `n_load_ps`, `n_store_ps`, `n_load_epi32`, `n_store_epi32`
- 广播: `n_set1_ps`, `n_set1_epi32`
- 算术: `n_add_ps`, `n_sub_ps`, `n_mul_ps`, `n_div_ps`, `n_fmadd_ps`
- 比较: `n_cmp_lt_ps`, `n_cmp_gt_ps`, `n_cmp_eq_ps`, `n_cmp_ge_ps`, `n_cmp_le_ps`
- mask 逻辑: `n_not_mask`, `n_and_mask`, `n_or_mask`
- blend: `n_blendv_ps`
- 极值: `n_min_ps`, `n_max_ps`
- 特殊: `n_sqrt_ps`, `n_round_ps`, `n_gather_ps`, `n_cvttps_epi32`

---

## 八、性能

| 案例 | 静态方法 SIMD | Job IJob SIMD | ISPC | 策略 |
|---|---|---|---|---|
| SimpleArith | 0.012ms | 0.75ms | 0.013ms | 连续 load/store |
| MathFuncs | 0.063ms | 3.1ms | 0.062ms | n_sin_ps/n_cos_ps/n_log_ps 内联 |
| ComplexFlow | 0.010ms | 1.3ms | 0.018ms | branchless blend |
| Reduce | 1.8ms | 36ms | 14ms | 内层向量化（连续 load + SIMD min） |
| GatherReduce | 31ms | 80ms | 24ms | 间接 gather，外层向量化 |

静态方法快于 Job（无调度开销）。Reduce/GatherReduce 的内层归约/gather 是剩余瓶颈。

---

## 九、关键文件

| 文件 | 职责 |
|---|---|
| `src/NativeDll/NativeSIMD.h` | 跨平台 SIMD 抽象层 |
| `src/NativeDll/NativeSIMD_math.h` | 内联数学函数（sin/cos/log 等） |
| `src/NativeDll/SimdValue.h` | simd_value<T> 封装 |
| `src/NativeTranspiler/Analyzer/CppGenerator.cs` | 静态方法 SIMD 路由 + StrideAnalyzer |
| `src/NativeTranspiler/Analyzer/CppJobGenerator.cs` | Job 结构体 SIMD 生成 |
| `src/NativeTranspiler/Analyzer/SimdControlFlowGenerator.cs` | 统一 SIMD 控制流生成器 |
| `src/NativeTranspiler/Analyzer/SimdVariableAnalyzer.cs` | 变量分析 |
| `src/NativeTranspiler/Analyzer/OuterSimdGenerator.cs` | 批循环 + SIMD 包装 |
| `src/NativeTranspiler/Analyzer/NativeTranspilerGenerator.cs` | Source Generator 入口 |
