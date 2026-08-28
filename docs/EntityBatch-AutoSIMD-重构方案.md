# EntityBatch AutoSIMD 重构方案

## 重构目标

让 EntityBatch adapter 支持 AutoSIMD（和 ISPC 隐式），保留 EntityBatch 路径的 cache 优势（16B vs 72B ChunkJobData）。

## 当前问题

1. `CppChunkStatementTranslator`（`CppJobGenerator.cs:32`）构造函数不接受 `enableAutoSIMD` 参数
2. `AppendEntityBatchAdapter`（`CppJobGenerator.cs:983`）和 `GenerateJobAdapter`（`CppJobGenerator.cs:1576`）创建 `CppChunkStatementTranslator` 时不传 `enableAutoSIMD`
3. 结果：EntityBatch adapter 的 AutoSIMD 分支生成 lane-loop 伪向量化代码
4. 之前尝试直接修改 EntityBatch adapter 调用独立 SIMD 函数失败：
   - `extern "C"` 在 C++ 块作用域（for 循环内）非法
   - `ChunkData`/`ChunkJobData` 签名不匹配
   - **根因**：LINQ `Aggregate` 在空序列上抛 `InvalidOperationException` → Transpiler source generator 静默失败 → Bindings 不生成 → ECS source generator 生成的 adapter 引用不存在的命名空间 → 28 个 C# 编译错误

## 重构方案：让 CppChunkStatementTranslator 支持 AutoSIMD

### 步骤 1：修改 CppChunkStatementTranslator 构造函数

`src/NativeTranspiler/Analyzer/Cpp/CppChunkStatementTranslator.cs:32-37`

```csharp
// 当前
public CppChunkStatementTranslator(SemanticModel semanticModel, INamedTypeSymbol jobStruct, 
    List<INamedTypeSymbol> requiredComponentTypes, List<INamedTypeSymbol>? requiredSharedTypes = null, 
    bool useFastMath = false)
    : base(semanticModel, jobStruct, useFastMath)
{
    _requiredComponentTypes = requiredComponentTypes;
    _requiredSharedTypes = requiredSharedTypes ?? new List<INamedTypeSymbol>();
}

// 改为
public CppChunkStatementTranslator(SemanticModel semanticModel, INamedTypeSymbol jobStruct, 
    List<INamedTypeSymbol> requiredComponentTypes, List<INamedTypeSymbol>? requiredSharedTypes = null, 
    bool useFastMath = false, bool enableAutoSIMD = false)
    : base(semanticModel, jobStruct, useFastMath, enableAutoSIMD)  // 传递到基类
{
    _requiredComponentTypes = requiredComponentTypes;
    _requiredSharedTypes = requiredSharedTypes ?? new List<INamedTypeSymbol>();
}
```

### 步骤 2：修改 GenerateJobAdapter 传递 enableAutoSIMD

`src/NativeTranspiler/Analyzer/Cpp/CppJobGenerator.cs:1576`

```csharp
// 当前
var tr = new CppChunkStatementTranslator(sm, jobStruct, rt, st, useFastMath);

// 改为
var tr = new CppChunkStatementTranslator(sm, jobStruct, rt, st, useFastMath, 
    enableAutoSIMD: autoSIMD == NativeTranspiler.AutoSIMD.Enabled);
```

### 步骤 3：修改 AppendEntityBatchAdapter 接受 autoSIMD 参数

`src/NativeTranspiler/Analyzer/Cpp/CppJobGenerator.cs:983`

```csharp
// 当前
private static void AppendEntityBatchAdapter(INamedTypeSymbol jobStruct, Compilation compilation, 
    StringBuilder sb, bool useFastMath, NativeTranspiler.AutoSIMD autoSIMD = Disabled)

// 已经在签名中接受 autoSIMD ！但从未被调用，需要在 GenerateJobAdapter 中调用时传递。
```

实际上 `AppendEntityBatchAdapter` 函数已经接受 `autoSIMD` 参数（默认值 Disabled），但函数体内对 `autoSIMD == Enabled` 的处理是 lane-loop 伪向量化（line 1040-1099）。

**核心修改**：`AppendEntityBatchAdapter` 函数体内的 AutoSIMD 分支（line 1040-1099）需要替换——不再生成 lane-loop，而是用 `CppPointerStatementTranslator`（带 `enableAutoSIMD=true`）生成真 SIMD 代码。

### 步骤 4：替换 AppendEntityBatchAdapter 的 AutoSIMD 分支

将 line 1040-1099 的伪向量化代码替换为：
```csharp
if (autoSIMD == NativeTranspiler.AutoSIMD.Enabled)
{
    // 真 SIMD：使用 CppPointerStatementTranslator 启用 AutoSIMD
    var sm2 = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
    var tr2 = new CppPointerStatementTranslator(sm2, jobStruct, useFastMath, enableAutoSIMD: true);
    // 翻译 body（for 循环内的 entity 迭代）
    sb.AppendLine("        {");
    sb.AppendLine("            const EntityBatchData* __batchData = &__batches[__batchIndex];");
    // ... 翻译 body ...
    sb.AppendLine("        }");
}
```

## 风险评估

**关键风险**：之前修改 `CppJobGenerator.cs` 导致 NativeTranspiler source generator 静默失败（28 errors）。
- **之前失败的根因**：LINQ `Aggregate` 在空序列上抛 `InvalidOperationException`
- **避免方法**：始终检查空序列/空集合，用条件分支或显式默认值

**本次重构更安全**：
- `enableAutoSIMD` 是简单 `bool` 参数，传递不会引发 LINQ 异常
- `AppendEntityBatchAdapter` 已经是 dead code（没有调用者），修改它不会影响现有 job
- 修改 `CppChunkStatementTranslator` 构造函数是纯加法操作（加默认参数），向后兼容

## 预期效果

修改后：
- EntityBatch adapter 在 AutoSIMD 模式下生成真 SIMD 代码（与 ChunkRange adapter 同样的 simd_value + n_sin_ps AVX2）
- `AppendEntityBatchAdapter` 函数虽然当前未被调用，但修改后可以在未来使用 EntityBatch 路径的 AutoSIMD job 中提供真 SIMD
- 不影响现有 ChunkRange 路径（AutoSIMD IJobChunk/IJobEntity 仍然走 ChunkRange）

## 验证步骤

1. 编译 NativeTranspiler（0 错误）
2. 清理 sample 的 obj/ 缓存
3. 编译 sample（0 错误）
4. 跑 09_ECS 测试套件（确保 ISpcEventJobTest 5 项 PASS）
5. 跑 IJobEntity AutoSIMD 测试（Light + Heavy PASS）
6. 确认生成的 EntityBatch adapter .cpp 文件含 SIMD 代码（grep simd_value / n_sin_ps）

## 后续工作

如果 EntityBatch 路径后续有调用者（例如 shared components 导致 ChunkRange 不可用），EntityBatch adapter 已准备好支持 AutoSIMD。
