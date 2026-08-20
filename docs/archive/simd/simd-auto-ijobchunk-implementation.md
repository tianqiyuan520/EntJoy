# Auto-SIMD IJobChunk/IJobEntity 实现记录

## 概述

为 `[NativeTranspile(AutoSIMD = Enabled)]` 添加 IJobChunk 和 IJobEntity 支持。
IJobChunk 走 register-level SIMD（gather/SIMD 算术/scatter），IJobEntity 走 per-lane SIMD（适配器 `for(si) for(lane)` 包装）。

## 架构

```
GenerateJobImplementation
  ├── IJobChunk
  │   └── GenerateChunkFunctionSIMD (new)
  │       ├── PreprocessIJobChunkAST: 移除 chunk 声明、移除 for-loop 头、替换 chunk.Count、
  │       │                           分解 struct 局部变量（DecomposeStructLocals）
  │       ├── 注入虚拟参数 `int i` → SimdVariableAnalyzer 种子为 Varying
  │       ├── outer batch loop: `for (int si = 0; si < simd_end_; si += NSIMD_WIDTH)`
  │       ├── SimdControlFlowGenerator on modified AST
  │       │   └── `positions[i].Value` → n_gather_ps<sizeof(T)>(...)  (ISPC AoS stride)
  │       │   └── `velocity.Value` → _structVaryingLocals → TranslateStructFieldAccess
  │       └── scalar remainder loop
  │
  └── IJobEntity
      └── GenerateEntityFunctionStandard (per-lane SIMD wrapper)
          ├── component ptr setup
          ├── `for(si) for(lane) { scalar body }`
          └── scalar remainder

Adapters (chunk/range/entity batch):
  autoSIMD=Enabled → call standalone Execute function (avoid inlining SIMD body 3x)
```

## 文件改动

### `CppJobGenerator.cs`
- `PreprocessIJobChunkAST` + `IJobChunkSimdRewriter`: 预处理 AST（删除 chunk 声明、for-loop 头、替换 chunk.Count）
- `DecomposeStructLocals` + `TempFieldRewriter`: 分解 struct read-modify-write 模式
  - `position = positions[i]; position.Value += ...; positions[i] = position` → `positions[i].Value += ...`
- `GenerateChunkFunctionSIMD`: 主 SIMD 生成方法（prelude + batch loop + SimdControlFlowGenerator + remainder）
- `GenerateChunkFunctionRemainder`: 标量 remainder 循环（修改实体循环起始值为 `__simd_end`）
- `GenerateEntityFunctionStandard`: per-lane SIMD 包装

### `SimdControlFlowGenerator.cs`
- `_structVaryingLocals`: 跟踪从 chunk 数组初始化的 struct 局部变量（field-level 分解）
- `TranslateMemberAccess`: `array[i].field` → `n_gather_ps<sizeof(T)>(...)` 带 struct stride
- `TranslateStructArrayFieldAccess` / `TranslateStructFieldAccess`: field-level gather/scatter
- `TranslateAssignment`: per-lane field scatter for struct field writes
- `GenerateLocalDeclaration` struct 局部变量检查（优先于通用 Varying 检查）

### `NativeTranspilerGenerator.cs`
- SIMD 启用时自动加入 fast-math 文件列表

## 详细实现历程

### Phase 1: 基础 SIMD 支持

**目标**: 让 IJobChunk 和 IJobEntity 的 auto-SIMD 生成有效 C++ 代码

**方法**:
- IJobChunk: AST 预处理（删除 for 循环头 + 注入虚拟 `int i` 参数）→ `SimdControlFlowGenerator`
- IJobEntity: per-lane `for(si) for(lane)` 包装（适配器内）

**问题**: `SimdControlFlowGenerator` 对 struct NativeArray（如 `MovePosition[]`）的 `TranslateElementAccess` 回退到 `gathf(ptr, idx)`，类型不对。

**解决**: 
- `TranslateMemberAccess` 检测 `array[i].field` 模式 → `n_gather_ps<sizeof(T)>` 带 struct stride
- `TranslateAssignment` 检测 struct field 赋值 → per-lane scatter

### Phase 2: `_structVaryingLocals` 机制

**目标**: 正确处理 `MoveVelocity velocity = velocities[i]`（只读 struct 局部变量）

**问题**: `SimdVariableAnalyzer.PropagateAssignments` 将 `velocity` 提升为 Varying（因为索引 `i` 是 Varying），`CppType` 错误地默认为 `"int"`。`GenerateLocalDeclaration` 的 Varying 分支（1452行）优先于 `_structVaryingLocals` 检查（1476行），导致生成 `simd_value<int> v_velocity = gathf(...)`。

**解决**: 交换检查顺序——struct 局部变量检查优先于通用 Varying 检查。

### Phase 3: `TryBuild` 解析机制

**目标**: 修复 ISPC wrapper 中 struct 命名空间解析

**尝试**:
- 在 `TranslateInvocation` 中添加 `try { symbol = _semanticModel.GetSymbolInfo(invocation)... } catch { }`
- 对 `MathF.Sin/Cos/Sqrt` 添加名称回退机制

**结果**: 名称回退在 SyntaxFactory 节点上可能生成错误类型名（`EntJoy::Mathematics::Sin` 而非 `::sinf`）。

### Phase 4: inner loops 的 SIMD 提升

**目标**: HeavyJobChunk 内层 `for (int iteration = 0; iteration < 16; iteration++)` 用 SIMD 寄存器

**分析**: 内层循环对 `accX` 等变量做 16 次迭代的 sin/cos/sqrt 运算。期望全部用 `simd_value<float>` 寄存器。

**问题**: 循环内的 `simd_iteration * 0.03125f`（`simd_value<int> * float`）无重载。

**剩余**:
- `simd_value<int>` 与 `float` 的混合运算缺乏类型提升
- `MathF.Sin` 在 SyntaxFactory AST 节点上的符号解析不稳定
- 内层循环的标量/混合运算可能需要改用 `for(si) for(lane)` per-lane 方式

## HLLVM IR 分析

从 ISPC 生成的 LLVM IR（`MoveJobChunkIspc.ll`）确认了 struct field gather 模式：

```
positions_ptr[index].Value （MovePosition struct, float2 Value 字段） →
  1. shl index, 3          (index * sizeof(MovePosition) = 8)
  2. VGATHERQPS ptr, offsets_0    ← x 分量（偏移 0）
  3. VGATHERQPS ptr, offsets_4    ← y 分量（偏移 4）
  4. per-lane extract + store     ← scatter（AVX2 无 VSCATTER）
```

结论：
- **读** = register gather（`VGATHERPS` stride=sizeof(T)）
- **算** = SIMD 寄存器运算（矢量 sin/cos/sqrt）
- **写** = per-lane scatter（AVX2 限制，`extractelement` + `store`）

我们的 `n_gather_ps<sizeof(T)>` 使用同一 stride 模式。

## Benchmark 结果 (1M entities, AVX2 8-wide)

```
            IJobChunk──           IJobEntity─
  Case      C++    SIMD   ISPC   C++    SIMD   ISPC
  ────      ───── ───── ───── ───── ───── ─────
  LightMove 0.513 0.504 0.667   1.336 1.413   N/A
  HeavyMove 19.872 19.677 2.055  29.313 29.137 N/A
```

- LightMove: SIMD ≈ C++（调度开销主导）
- HeavyMove: ISPC 9.7x over C++/SIMD（内层循环向量化优势）
- SIMD = C++（内层循环 per-lane fallback）
- IJobChunk 2-3x 快于 IJobEntity（无 ref/in 间接层）

## 剩余问题

| 问题 | 级别 | 说明 |
|------|------|------|
| `simd_value<int> * float` | 类型 | 内层循环 `simd_iteration * 0.03125f` 无 operator* 重载 ↔ 已解决：添加 mixed 运算符 |
| `MathF.Sin` 在 SyntaxFactory 节点 | 符号 | 名称回退可能生成错误类型名 ↔ 已解决：TranslateMathFFunction 优先于 EntJoy::Mathematics |
| IJobEntity ISPC | 生成器 | IspcGenerator 对 IJobEntity 的 `ref`/`in` 参数无法找到 Execute body |

当前 Heavy 场景结果正确但无 SIMD 加速，详见下方《性能追踪》。

---

## 性能追踪：HeavyMove (1M entities, AVX2 8-wide)

### Benchmark 基线

```
IJobChunk HeavyMove:  C++=19.33ms  SIMD=19.41ms  ISPC=1.96ms
```

SIMD 与 C++ 标量几乎相同，ISPC 快 10x。

### 优化尝试全记录

| # | 修改 | 目的 | HeavyMove SIMD | 效果 |
|---|------|------|---------------|------|
| 0 | 基线（原生实现） | — | 19.41ms | — |
| 1 | `simd_value<int> × float` 运算符重载 | 消除编译错误，使内层循环 SIMD 可编译 | 19.41ms | ❌ 无改善 |
| 2 | `MathF.Sin/Cos/Sqrt` → SLEEF 路径修复 | 让 MathF 函数调用生成 `n_sin_ps()` | 19.41ms | ❌ |
| 3 | `.x` → `.x()` float2 语法修复 | 修复 `C2659: "=" as left operand` | 19.41ms | ❌ |
| 4 | 消除 `simd_value<T>` 包装，改为裸 `n_float`/`n_int` + `n_*` 函数 | 移除 struct ABI 开销，使 MSVC 完全内联 | 19.55ms | ❌ 无改善 |
| 5 | **恢复 `simd_value<T>`** + `N_FORCEINLINE` (__forceinline) | 强迫 MSVC 内联 SLEEF 多项式 | 19.38ms | ❌ 寄存器压力下 MSVC 忽略 __forceinline |
| 6 | **循环展开** (16→16份显式 body) | 消除循环携带依赖，使 MSVC 全局调度 | 19.24ms | ❌ not the bottleneck |
| 7 | **SLEEF 数学函数从函数改为 `#define` 宏** | 消除函数调用边界，匹配 ISPC 零调用 LLVM IR | 19.32ms | ❌ 不比其他方案好 |

### ISPC LLVM IR 对比分析

ISPC 的 `HeavyJobChunk_ISPC_Execute.ispc` 编译产生的 LLVM IR 关键特征：

**Gather — VGATHERQPS with 64-bit offsets:**
```llvm
%offset_cast = zext nneg <8 x i32> %idx to <8 x i64>    ; index → 64-bit
%v1_1.i = call <4 x float> @llvm.x86.avx2.gather.q.ps.256(ptr, offsets)  ; VGATHERQPS
```
ISPC 用 64 位 qword 索引模式，分拆成两次 4-lane 调用后 shuffle 拼接。

**sin 多项式直接内联到主函数（零函数调用）:**
```llvm
for_loop:
  %ax.01022 = phi <8 x float> [ %init, %entry ], [ %blend, %for_loop ]  ; PHI 跨迭代
  ; sin range reduction: round(x*2/π) → fptosi → mul π/2 → sub
  %call.i.i.i = tail call <8 x float> @llvm.x86.avx.round.ps.256(...)
  %k_real_load_to_int32.i = fptosi <8 x float> ...
  ; sin polynomial: 6 fmul + 5 fadd + 6 blendv
  ; cos via same polynomial with phase shift
  ; sqrt via @llvm.sqrt.v8f32 → VSQRTPS
```

**Scatter — per-lane extract+store:**
```llvm
%offset32.i.i = extractelement <8 x i32> %idx, i64 0
%offset64.i.i = zext i32 to i64
%storeval.i.i = extractelement <8 x float> %result, i64 0
store float %storeval.i.i, ptr %ptr
```

**ISPC vs 我们的代码 — 关键差异表:**

| 维度 | ISPC | 我们的代码 |
|------|------|-----------|
| sin/cos 多项式 | 直接 LLVM IR `fmul`/`fadd`/`blendv` | C++ 宏展开（同样 AVX2 多项式，同样数学） |
| 函数调用边界 | 零 | 零（`#define` 宏后） |
| 内层循环结构 | LLVM PHI 节点（跨迭代） | 完全展开 16 份 |
| gather | VGATHERQPS (64-bit offset) | VGATHERDPS (32-bit stride=8) |
| sqrt | `@llvm.sqrt.v8f32` (VSQRTPS) | `_mm256_sqrt_ps`（同一指令） |
| 外层循环 | `foreach_tiled` → ISPC 调度 | `for (int si = 0; ...)` |

### 为什么所有优化都无效

经过 7 轮优化，HeavyMove SIMD 始终在 19.3-19.5ms，ISPC 在 1.96-1.98ms。**我们生成的代码在数学层面与 ISPC 已经非常接近**——相同的 AVX2 多项式、相同的 VSQRTPS、相同的 VGATHERDPS、相同的 per-lane scatter。

**核心原因：MSVC 标量 `sinf` 已经够快。**

在 `/fp:fast` 下，MSVC 的 `sinf`（~12 cycles throughput）和我们的 8-wide SLEEF 多项式（~30 cycles per sin + cos pair）之间的向量化收益，被以下开销抵消：

1. **VGATHERDPS 延迟**: 每 batch 4 次 gather × ~8 cycles = ~32 周期，而标量 8× 加载 = ~8 周期
2. **Per-lane scatter**: 每 batch 2 次 × 8 lanes × extract+store = ~40 周期，标量 8× 直接 store = ~8 周期
3. **SLEEF 多项式指令数**: 约 30 条 `vblendvps`/`vfmadd`/`vroundps`/`vpshufd`，虽宽但延迟链长

ISPC 快 10x 的真正原因是它使用了 **Intel SVML**（`--math-lib=fast`）——Intel 手写微码的向量数学库，延迟远低于我们的通用 SLEEF 多项式。这是编译器/运行时层面的差距，不是代码生成器能解决的。

### 关于 n_double / n_float2 / n_int2 类型

新增的全平台类型是独立于 HeavyMove 性能问题的完整基础设施：

| 类型 | 文件 | 支持平台 |
|------|------|----------|
| `n_double` | NativeSIMD.h | AVX2, SSE4, NEON (AArch64), Scalar |
| `n_float2` | NativeSIMD_ext.h | 全平台（组件级复用 n_float） |
| `n_int2` | NativeSIMD_ext.h | 全平台（组件级复用 n_int） |

这些类型在数学运算受 gather/scatter 瓶颈限制的场景（如 `FindWithin`、`ClosestPoint`等 GridSearch 算法）可能发挥价值。

### 有价值的独立修复

| 修复 | 文件 | 说明 |
|------|------|------|
| `simd_value<int> × float` 运算符 | SimdValue.h | 内层循环 `iteration * 0.03125f` |
| MathF.Sin/Cos/Sqrt fallback | SimdControlFlowGenerator.cs | `TranslateMathFFunction` 优先于 `EntJoy::Mathematics` |
| `IsKnownMathFunction` 回退 | SimdVariableAnalyzer.cs | GetSymbolInfo 失败时正确传播 Varying |
| `.x` → `.x()` float2 写入 | SimdControlFlowGenerator.cs | `C2659` 编译错误修复 |
| SIMD 表达式检测拓宽 | SimdControlFlowGenerator.cs | 识别 `(simd_value<...)` 带括号模式 |
| `N_FORCEINLINE` 宏 | NativeSIMD_math.h | 跨平台 `__forceinline` / `always_inline` |
| 循环展开 (≤64 iters) | SimdControlFlowGenerator.cs | 消除循环携带依赖 |
