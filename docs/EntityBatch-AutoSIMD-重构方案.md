# EntityBatch AutoSIMD 重构方案 — 已实施（2026-08-28）

## 状态：已实施 ✅

本方案已完整落地，见 `AutoSIMD-EntityBatch-待优化.md`（最终状态）与提交 `112f1e5`。

> 下方保留重构前的原始方案作为历史记录，标注「已实施」说明当前实现方式。

## 重构目标

让 EntityBatch adapter 支持 AutoSIMD（和 ISPC 隐式），保留 EntityBatch 路径的 cache 优势（16B vs 72B ChunkJobData）。

## 当前问题（重构前）

1. `CppChunkStatementTranslator`（`CppJobGenerator.cs`）构造函数不接受 `enableAutoSIMD` 参数
2. `AppendEntityBatchAdapter`（已删除）和 `GenerateJobAdapter` 创建 `CppChunkStatementTranslator` 时不传 `enableAutoSIMD`
3. 结果：EntityBatch adapter 的 AutoSIMD 分支生成 lane-loop 伪向量化代码
4. 之前尝试直接修改 EntityBatch adapter 调用独立 SIMD 函数失败：
   - `extern "C"` 在 C++ 块作用域（for 循环内）非法
   - `ChunkData`/`ChunkJobData` 签名不匹配
   - **根因**：LINQ `Aggregate` 在空序列上抛 `InvalidOperationException` → Transpiler source generator 静默失败 → Bindings 不生成 → ECS source generator 生成的 adapter 引用不存在的命名空间 → 28 个 C# 编译错误

## 重构方案（已实施）

### ✅ 步骤 1：修改 CppChunkStatementTranslator 构造函数（已实施）

`src/NativeTranspiler/Analyzer/Cpp/CppChunkStatementTranslator.cs`：

```csharp
public CppChunkStatementTranslator(SemanticModel semanticModel, INamedTypeSymbol jobStruct,
    List<INamedTypeSymbol> requiredComponentTypes, List<INamedTypeSymbol>? requiredSharedTypes = null,
    bool useFastMath = false, bool enableAutoSIMD = false)
    : base(semanticModel, jobStruct, useFastMath, enableAutoSIMD)  // 传递到基类
```

### ✅ 步骤 2：GenerateJobAdapter EntityBatch 分支用 SimdControlFlowGenerator（已实施）

**与原始方案不同**：不是用 `CppPointerStatementTranslator(enableAutoSIMD: true)` 简单翻译，而是完整复用 ChunkRange 的 SIMD 流程：

1. `PreprocessIJobChunkAST`（含 `DecomposeStructLocals`）预处理 body
2. 组件数组指针声明（batch 级）
3. fake `MethodDeclaration` + `SimdVariableAnalyzer`
4. `SimdControlFlowGenerator` 生成 mask-managed SIMD 代码
5. SIMD batch loop + 标量余量循环（`__simd_end` 起）
6. `usesSendEvent` 显式拦截；其余异常 → per-lane 标量回退（`simdSb` 临时缓冲）

### ✅ 步骤 3：删除死代码 AppendEntityBatchAdapter（已实施）

`AppendEntityBatchAdapter` 从未被调用，且与 `GenerateJobAdapter` 的 EntityBatch 分支逻辑重复（约 190 行），已删除。

### ✅ 步骤 4：BindingsGenerator 路由启用（已实施）

AutoSIMD job 从「走 ChunkRange」改为「走 EntityBatch」：
- 声明/初始化/extern：`!isIspc && !hasShared`
- 调度主路径：`isIspc3 || hasShared3` → ChunkRange；否则 EntityBatch
- worker-cap/RunImmediate：同分流（shared → `ScheduleChunkRawWithWorkerCap*` + ChunkFuncPtr）

## 风险评估（重构后复盘）

- **之前失败的根因**（LINQ Aggregate 空序列）已在重构中规避：`PreprocessIJobChunkAST` 返回后检查 `entityLoopIv`/`chunkArrays` 空，显式 throw 走 catch 回退
- `enableAutoSIMD` 是简单 `bool` 参数，传递不会引发 LINQ 异常
- 所有 SIMD 生成写入临时 `simdSb`，失败时无半截代码残留

## 验证结果

- NativeTranspiler 编译：0 错误
- EntJoySample 完整构建（含 C++ 生成）：0 错误
- 测试：8 PASS / 0 FAIL（AutoSIMD Light 零误差、CPP Light 1 ULP 内、Heavy 全通过）
- 生成的 EntityBatch adapter .cpp 含真 SIMD 代码（`simd_value` + `n_sin_ps` + `__simd_end` 余量循环）

## 后续工作

如果 EntityBatch 路径后续有调用者（例如 shared components 导致 ChunkRange 不可用），EntityBatch adapter 已准备好支持 AutoSIMD。需要：
- `EntityBatchData` 增加 `sharedValuePtrs` 字段
- 余量循环路径测试覆盖
