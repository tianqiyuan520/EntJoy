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
| `simd_value<int> * float` | 类型 | 内层循环 `simd_iteration * 0.03125f` 无 operator* 重载 |
| `MathF.Sin` 在 SyntaxFactory 节点 | 符号 | 名称回退可能生成错误类型名 |
| IJobEntity ISPC | 生成器 | IspcGenerator 对 IJobEntity 的 `ref`/`in` 参数无法找到 Execute body |

当前 Heavy 场景 fallback 到标量，结果正确但无 SIMD 加速。
