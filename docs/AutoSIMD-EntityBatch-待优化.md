# EntityBatch AutoSIMD / ISPC Adapter — 已重构

## 重构完成（2025-07）

### 改动概述

参照 ChunkRange 的 `GenerateChunkFunctionSIMD` 写法，重构 EntityBatch adapter 以支持 AutoSIMD 真 SIMD 和 ISPC。

### 修改文件

| 文件 | 改动 |
|------|------|
| `CppChunkStatementTranslator.cs` | 构造函数增加 `enableAutoSIMD` 参数透传给基类 `CppPointerStatementTranslator` |
| `CppJobGenerator.cs` (GenerateJobAdapter) | EntityBatch adapter AutoSIMD 分支改用 `SimdControlFlowGenerator` 生成真 SIMD 代码；ISPC 分支跳过 C++ 适配器（由 `IspcGenerator.GenerateCppEntityBatchWrapper` 处理） |

### AutoSIMD 路径（真 SIMD）

EntityBatch adapter 在 `autoSIMD == Enabled` 时的代码生成流程：

1. 提取 Execute 参数 → 组件数组指针（`paramName_ptr = reinterpret_cast<...>(__batchData->componentArrays[i])`）
2. 构造 fake `MethodDeclaration`（带 `entityIdx` 参数）给 `SimdVariableAnalyzer`
3. `SimdControlFlowGenerator.Generate()` 生成 mask-managed SIMD C++ 代码
4. 生成 SIMD batch loop（`for si; v_i = v_base + si`）+ 标量余量循环
5. SIMD 生成失败时自动回退 per-lane 标量路径

关键代码位置：`CppJobGenerator.cs` → `GenerateJobAdapter` → EntityBatch adapter AutoSIMD 分支

### ISPC 路径

ISPC EntityBatch 由 `IspcGenerator.GenerateCppEntityBatchWrapper` 独立生成，C++ adapter 不再重复生成（添加 `!isIspcJob` 条件跳过）。

### 与 ChunkRange 路径的对比

| | ChunkRange AutoSIMD | EntityBatch AutoSIMD（重构后） |
|---|---|---|
| SIMD 生成 | `SimdControlFlowGenerator` | `SimdControlFlowGenerator`（同） |
| 数据来源 | `__chunkData->requiredComponentArrays[i]` | `__batchData->componentArrays[i]` |
| 组件数组声明 | prelude 中声明 `paramName_ptr` | batch 循环内声明 `paramName_ptr` |
| 余量循环 | `GenerateChunkFunctionRemainder` | 内联 `CppChunkStatementTranslator` |

## 原有问题记录（已解决）

### lane-loop 伪向量化（已修复）

旧代码在 AutoSIMD 分支生成：
```cpp
for (int lane = 0; lane < NSIMD_WIDTH; lane++) {
    int __entity_index = si + lane;
    // 标量 body
}
```
现在通过 `SimdControlFlowGenerator` 生成真正的 mask-managed SIMD 代码。

### extern "C" 作用域问题（已规避）

不再尝试在 C++ 块作用域内声明 `extern "C"` 函数，改为直接在 adapter 内联 SIMD 代码。

### ChunkData/ChunkJobData 签名不匹配（已规避）

EntityBatch adapter 直接使用 `EntityBatchData*`，不再尝试调用独立 `_Execute` 函数。
