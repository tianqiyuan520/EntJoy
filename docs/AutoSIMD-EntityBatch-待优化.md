# EntityBatch AutoSIMD / ISPC Adapter — 已重构（2026-08-28）

## 状态：已完成 ✅

参照 ChunkRange 的 `GenerateChunkFunctionSIMD` 写法，EntityBatch adapter 现已支持 AutoSIMD 真 SIMD 和 ISPC。

## 改动概述

### 代码修改

| 文件 | 改动 |
|------|------|
| `CppChunkStatementTranslator.cs` | 构造函数增加 `enableAutoSIMD` 参数透传给基类 `CppPointerStatementTranslator` |
| `CppJobGenerator.cs` (GenerateJobAdapter) | EntityBatch adapter 新增 AutoSIMD 分支（`SimdControlFlowGenerator` 真 SIMD）；ISPC job 跳过 C++ 适配器（由 `IspcGenerator.GenerateCppEntityBatchWrapper` 处理）；删除死代码 `AppendEntityBatchAdapter`；adapter AutoSIMD 时 include `SimdValue.h` |
| `BindingsGenerator.cs` | 路由统一为 `!isIspc && !hasShared`：AutoSIMD/普通 C++ → EntityBatch；ISPC / shared → ChunkRange。声明/初始化/extern/调度/worker-cap/RunImmediate 六处一致 |

### AutoSIMD 路径（真 SIMD）

EntityBatch adapter 在 `autoSIMD == Enabled` 时的代码生成流程：

1. `PreprocessIJobChunkAST` 预处理（含 `DecomposeStructLocals` → 结构体 read-modify-write 分解为字段级 gather/scatter）
2. 组件数组指针声明（`paramName_ptr = reinterpret_cast<...>(__batchData->componentArrays[i])`）
3. 构造 fake `MethodDeclaration`（带 `entityIdx` 参数）给 `SimdVariableAnalyzer`
4. `SimdControlFlowGenerator.Generate()` 生成 mask-managed SIMD C++ 代码
5. SIMD batch loop（`for si; v_i = v_base + si`）+ 标量余量循环（`__simd_end` 起）
6. 失败回退：`usesSendEvent` 显式拦截；其余异常走 per-lane 标量（`simdSb` 临时缓冲，无半截代码残留）

关键代码位置：`CppJobGenerator.cs` → `GenerateJobAdapter` → EntityBatch adapter AutoSIMD 分支

### ISPC 路径

ISPC EntityBatch 由 `IspcGenerator.GenerateCppEntityBatchWrapper` 独立生成，C++ adapter 不再重复生成（`!isIspcJob` 条件跳过）。

### 路由逻辑（最终）

| Job 类型 | 调度路径 |
|----------|----------|
| IJobChunk + AutoSIMD | **EntityBatch**（SimdControlFlowGenerator 真 SIMD） |
| IJobChunk + 普通 C++ | EntityBatch（标量） |
| IJobChunk + ISPC | ChunkRange |
| IJobChunk + shared components | ChunkRange（EntityBatch 不支持 shared） |
| IJobEntity + 任意后端 | ChunkRange |

## 验证结果（2026-08-28 实测）

```
=== IJobEntity / IJobChunk Test (N=100000) ===
  Light IJobEntity AutoSIMD          MaxDiff=0        Mismatch=0   [PASS]  10.3ms
  Heavy IJobEntity AutoSIMD          MaxDiff=1.9e-6   Mismatch=0   [PASS]   3.4ms
  Light IJobEntity CPP (no AutoSIMD) MaxDiff=2.4e-7   Mismatch=0   [PASS]   1.4ms
  Heavy IJobEntity CPP (no AutoSIMD) MaxDiff=3.8e-6   Mismatch=0   [PASS]   3.6ms
  Light IJobChunk CPP (no AutoSIMD)  MaxDiff=2.4e-7   Mismatch=0   [PASS]   2.5ms
  Heavy IJobChunk CPP (no AutoSIMD)  MaxDiff=3.8e-6   Mismatch=0   [PASS]   3.1ms
=== IJobChunk + AutoSIMD + EntityBatch Test (N=100000) ===
  Light IJobChunk AutoSIMD+EntityBatch  MaxDiff=0      Mismatch=0  [PASS]  1.4ms
  Heavy IJobChunk AutoSIMD+EntityBatch  MaxDiff=3.8e-6  Mismatch=0  [PASS]  2.0ms
```

**8 PASS / 0 FAIL**。

### 浮点容差说明

- **AutoSIMD Light 零误差**：Precise 库 fast-math OFF，`n_mul_ps/n_add_ps` 与 C# 标量严格一致
- **CPP 标量 Light 1 ULP**（2.4e-7）：C++ 编译器对 `a + b*c` 做 FMA 融合（`fma(v, dt, p)`），1 次舍入 vs C# 2 次舍入 → 测试用 `1e-6` 容差
- **Heavy**：16 次 sin/cos 迭代累积 ~3.8e-6，`1.5e-3` 容差覆盖

## 原有问题记录（全部已解决）

### lane-loop 伪向量化（已修复）

旧代码生成：
```cpp
for (int lane = 0; lane < NSIMD_WIDTH; lane++) {
    int __entity_index = si + lane;  // 标量逐 lane
}
```
现在通过 `SimdControlFlowGenerator` 生成真正的 mask-managed SIMD 代码。

### extern "C" 作用域问题（已规避）

不再尝试在 C++ 块作用域内声明 `extern "C"` 函数，改为直接在 adapter 内联 SIMD 代码。

### ChunkData/ChunkJobData 签名不匹配（已规避）

EntityBatch adapter 直接使用 `EntityBatchData*`，不再尝试调用独立 `_Execute` 函数。

### 死代码 AppendEntityBatchAdapter（已删除）

从未被调用，且与 `GenerateJobAdapter` 的 EntityBatch 分支逻辑重复（约 190 行），已删除。

## 后续工作（可选）

- 余量循环路径测试覆盖（N 非整倍 `NSIMD_WIDTH` 用例）
- 余量循环删除逻辑统一（`GenerateChunkFunctionRemainder` 加 entityBatch 开关复用）
- EntityBatchData 增加 `sharedValuePtrs` 字段以支持未来 shared 场景（当前由外层条件排除）
