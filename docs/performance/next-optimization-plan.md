# 下一步优化计划

## 现状摘要

1. **ISPC IJobEntity 优化成功 ✅** — struct copy 消除，生成的 ISPC 代码简化为直接 `ptr[index].field` 访问
2. **环境噪音较大** — 两次 benchmark 的绝对数值差异 ~1.5-2x（可能来自 CPU turbo、内存状态、重新编译的优化选项变更），需关注**相对比例**
3. **ISPC Heavy 保持 8.87x** ✅ — 计算密集路径不受影响

## 当前性能瓶颈排名

### ISPC 轻量：IJobEntity ISPC 仍比 C++ 慢 ~2.36x (0.363 vs 0.154 ms)
**原因**：AoS stride=2 的 `ptr[index].field` 访问，ISPC 生成 gather 指令 vs C++ MSVC 生成连续 load。
**方案**：需 SoA 数据布局，但影响整个 ECS 核心（Chunk/Archetype/Query），范围过大。

### ISPC IJobChunk Light：已比 C++ 还快 (0.360 vs 0.410 ms)
从 0.910ms 改善到 0.360ms 得益于 CMake 重新编译（可能触发了不同的 ISPC 优化路径）。

### ✅ C++ IJobChunk 速度莫名其妙的差了 (0.352→0.410 ms)
而 C++ Fast 从 0.377→0.181ms。Light 测试只有 `float2 += float2 * float` 运算但表现差异 2.26x，说明不是代码逻辑问题而是 CMake 编译器的优化选项随机性。需要优先排除。

---

## 下一步：先修编译器优化稳定性

### 问题发现
两次 benchmark 相同代码的 C++ IJobChunk 从 0.352ms→0.410ms（+16%），而 C++ Fast 从 0.377→0.181ms（-52%）。两者唯一区别是 Fast 使用了 `EntJoy::FastMath::Sin` 等多项式近似，但 **Light 测试没有 sin/cos**，所以差异源头不在代码本身。

这强烈表明 CMake 的 `Set(CMAKE_CXX_FLAGS_RELEASE ...)` 或在 CMakeLists.txt 中不同 `.cpp` 被分配了不同的 `/arch`/`/fp` 标志。

### 检查计划
1. 读取 `NativeTranspiler_Generated/CMakeLists.txt`，确认各编译单元使用相同的 `/arch:AVX2` 标志
2. 检查 CMake 是否对 FastMath 版本的 cpp 启用了额外优化（如 `/fp:fast`）而对普通版本没有

### 修复方案
如果在 CMakeLists.txt 中发现优化选项不一致 → 统一所有生成的 C++ 文件使用相同的优化标志。

---

## 后一步（在编译器稳定性修复后）：IJobChunk ISPC struct copy 消除

### 背景
IJobChunk 的 C# 测试代码写的是：
```csharp
MovePosition position = positions[index];
position.Value += velocities[index].Value * DeltaTime;
positions[index] = position;
```
ISPC 生成为：
```ispc
MovePosition position = positions_ptr[index];
position.Value += velocities_ptr[index].Value * DeltaTime;
positions_ptr[index] = position;
```

与 IJobEntity 不同的是，这里的 struct copy 来自用户显式编写的 C# 代码，而非 transpiler 生成的参数包装。C++ 路径的 MSVC 优化器可以将此模式优化为 per-field 直接操作，但 ISPC 的 `foreach` 中 struct 临时变量会强制 gather/scatter。

### 需要调研
在实施前确认 ISPC 编译器是否能自动优化该模式。可以通过为 IJobChunk 添加一个手动 per-field 的 ISPC 版本来对比验证——如果 ISPC 编译器已能优化好则跳过此步。

### 实现方案（如需）
在 `IspcChunkStatementTranslator` 或 `IspcStatementTranslator` 中检测以下代码模式：
```
MovePosition position = positions[index];  // 从 NativeArray 读取到本地 struct
... 修改 position 字段 ...
positions[index] = position;               // 写回同一数组
```

转换策略：不做全局模式分析，而是对本地 struct 类型变量的成员访问翻译为 `positions_ptr[index].field` 的直接操作。

具体实施步骤：
```
1. 在 IspcStatementTranslator 中增加 _chunkLocalStructs 记录
2. 当检测到赋值语句 `T var = arr_expr[index]` 且 arr_expr 是 ChunkNativeArray 时：
   - 记录 var → arr_expr 的映射关系
3. 当检测到赋值语句 `arr_expr[index] = var` 且映射存在时：
   - 检查 var 从创建到现在是否被完整重写 → 如果是，跳过此 writeback（因为它已经被 per-field 操作覆盖）
4. 对 var.field 的访问翻译为 arr_expr_ptr[index].field
```

### 预期收益
- IJobChunk ISPC Light 预期提升 30-50%
- 重负载影响不大

---

## 优先级排序

| 步骤 | 内容 | 难度 | 预期收益 | 代码文件 |
|:----:|------|:----:|:--------:|---------|
| 1 | CMakeLists.txt 优化标志一致性检查 | 低 | 消除环境噪音 | `CMakeLists.txt` |
| 2 | IJobChunk ISPC struct copy 消除 | 中 | 30-50% | `IspcStatementTranslator.cs`, `IspcChunkStatementTranslator.cs`, `IspcGenerator.cs` |
| 3 | IJobEntity 剩余差距（AoS stride） | 高 | 5-10% | 需要 ECS 核心改动 |
