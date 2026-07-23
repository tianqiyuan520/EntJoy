# Full-Width SIMD 尝试记录

## 背景

ClosestPoint Job 的性能现状：

| 路径 | 耗时 | vs baseline |
|------|------|-------------|
| C++ per-lane（baseline） | 0.916 ms | — |
| C++ `GenerateReductionSIMD`（AutoSIMD） | 1.125 ms | ❌ 慢 22% |
| ISPC foreach（参考实现） | 0.643 ms | ✅ 快 42% |

ISPC 快 42% 的根因分析见 [ispc-vs-cpp-simd-analysis.md](ispc-vs-cpp-simd-analysis.md)，核心结论是 ISPC 的 `foreach` 提供了 C++ 无法直接表达的语义——"8 个不同的索引各自跑同一个函数体，函数体内的循环由 mask 控制同步"。

### 目标

在 C++ AutoSIMD 生成器（`OuterSimdGenerator.cs`）中新增一条代码生成路径，**参考 ISPC 的 foreach 做法**，让所有 8 个 query 的数据全程保留在 SIMD 寄存器中，消除 `for (lane = 0; lane < NSIMD_WIDTH; lane++)` 提取循环引起的 SIMD → 标量 → SIMD 往返开销。

## 实现

### 文件变更

| 文件 | 变更 | 行数 |
|------|------|------|
| `src/NativeDll/NativeSIMD.h` | 新增整数比较/运算/floor/转换/mask 操作 | ~100 |
| `src/NativeDll/SimdValue.h` | 新增 `simd_mask` 操作符、`simd_value` 比较/算术/构造函数 | ~80 |
| `src/NativeTranspiler/Analyzer/OuterSimdGenerator.cs` | 新增 `GenerateFullWidthSIMD` + `FWSIMD_*` 辅助方法 | ~1200 |
| `src/NativeTranspiler/Analyzer/CppJobGenerator.cs` | 更新构造函数调用签名 | 2 |

### 生成器设计

`GenerateFullWidthSIMD` 的设计思路：

1. **外层**：gather 8 个 QueryPositions，存入 `simd_value<EntJoy::Mathematics::float2> v_q`
2. **全程 SIMD**：所有局部变量变为 `simd_value<T>` 类型，使用 `v_name` 命名
3. **Mask 管理**：所有 `if` 语句的控制流用 `simd_mask` 替代分支
4. **变界内循环**：`for (int i = start; i < end; i++)` 用 `while(true) { mask &= (v_i < v_end); ... v_i += 1; }` 替代
5. **最终写回**：`Results[idx] = HashIndex[bestIdx].y` 用 per-lane 提取写回

关键机制：

- `FWSIMD_TranslateExpr()` — 将 C# 表达式翻译为 C++ SIMD 表达式
- `FWSIMD_GenerateSIMDCmp()` — 将比较运算符翻译为 `n_cmp_lt_ps/epi32` 等 SIMD 内联函数
- `FWSIMD_GenerateVaryingForLoop()` — `v_i = v_start; while(true) { ... v_i += 1; }` 模式
- `FWSIMD_GenerateIfStatement()` — 分类处理：`continue`（mask AND）、reduction（blend）、复杂（mask push/pop）
- `FWSIMD_ComponentString()` — 将 `float2/int2` 表达式分解为 x/y 分量操作
- `FWSIMD_RHSWithLaneExtract()` — 对 `HashIndex[bestIdx].y` 等路径在写回时做逐 lane 提取

## 结果

### 1. 崩溃：SIMD gather 越界访问

**症状**：`AccessViolationException`，发生在 `SimdValue.h` 的 gather 调用中。

**根因**：

`_mm256_i32gather_epi32` / `_mm256_i32gather_ps` 是 **unmasked gather**——它对所有 8 个 lane 都进行内存读取，不理会被 mask 掉的 lane。

当 `cell.x = 199, dx = 1`：
```
nx = cell.x + dx = 200
(uint)nx >= GridDims.x ? → true → lane 被 mask 掉
但 SIMD 计算仍对所有 lane 产生 cellHash = ny * 200 + 200 = 40000  // 越界！
合法范围 = 0 ~ 39999（200 × 200 grid）
CellStartEnd.Ptr[40000] → 访问越界内存 → AV
```

**Heisenbug 原因**：`printf` 等调试输出改变了 MSVC 的栈布局和寄存器分配，使越界访问刚好落在已映射的内存页面上。去掉调试输出后越界访问落到未映射页面 → 崩溃。

**修复**：对 cellHash 同时 clamp 上下界 `max(0, min(cellHash, GridDims.x * GridDims.y - 1))`。已保留在 `FWSIMD_GenerateCompoundDecl` 和 `FWSIMD_TranslateElementAccess` 的 int2 gather 路径中。

### 2. 性能灾难：内循环逐元素执行

**症状**：死循环（实为极慢），100K 查询 × 8 queries × 9 邻域 × avg 2.5 元素的内循环需要天文数字的迭代量。

**根因**：

ISPC 的 `foreach` 模型：
```
foreach (index = 0...N) {        // 8-wide SIMD：8 个 query 并行
    for (int i = start; i < end; i++) {  // per-lane 标量：每个 lane 独立运行
        SortedPositions[i]  →  AoS gather（标量 index）
        distSq = ...         →  标量运算
        if (distSq < best)   →  标量分支
    }
}
```
内层循环是 **per-lane 标量**的——每个 lane 有自己的 `i` 计数器，`i++` 只影响当前 lane。SIMD 并行度来自外层的 8 个 query。

我的错误实现：
```
v_i = v_start;
while (true) {                              // 8-wide SIMD：所有 lane 一起推进
    mask &= (v_i < v_end);                  // SIMD mask 管理（每步都有开销）
    gather(SortedPositions_ptr, v_i);       // SIMD gather（8 元素同时读）
    SIMD distSq;                            // SIMD 运算
    blend(best, distSq, mask);              // SIMD 归约
    v_i += 1;                               // 全部 lane 步进 1
}
```
这导致每处理 **1 个元素** 就要做一轮完整的 SIMD gather + 运算 + mask。对于平均 2.5 元素/ cell 的场景，SIMD 开销远大于收益。

### 3. 双 null 赋值的 bug

`FWSIMD_GenerateAssignment` 中有个 bug：遇到 `cell = clamp(cell, ...)` 这种 SIMD-to-SIMD 赋值时，先检查 `rhsHasSIMD` 为 true 就设置 `rhs = null`，然后走到 `IdentifierNameSyntax` 分支只输出 `v_cell = null`（空值）。

修复：SIMD-to-SIMD 赋值时直接翻译 RHS 表达式。

## 经验教训

### 1. ISPC 的「全宽 SIMD」不等于「整个函数体 SIMD」

ISPC 的 `foreach` 将外层迭代映射到 SIMD lane，但**内层循环是 per-lane 标量的**。C++ 要达到类似效果，正确方式应该是：

```
外层：gather 8 queries（SIMD）
per-lane 提取：for (lane = 0..7) {
    标量循环体（包括内层 reduction for 循环）
    内层有机会时可用 SIMD 批处理（GenerateReductionSIMD 的模式）
}
```

而不是试图把所有控制流变成 SIMD mask。

### 2. unmasked gather 需要 upper bound clamp

AVX2 的 `_mm256_i32gather_ps` 即使被 mask 掉的 lane 也会执行内存读取。这意味着在 random access 场景中，所有 lane 的索引必须在合法范围内——单纯 mask 是不够的。

### 3. 小 cell 场景不适合 inner SIMD 批处理

ClosestPoint 的 cell 平均 2.5 元素，SIMD 批处理的 `if (end - start >= 8)` 守卫基本不触发。ISPC 在这里快的原因是 **外层 8 个 query 的并行度**，不是内层的向量化。

### 4. MSVC 的优化器不可预测

同样的 `if ((end - start) >= NSIMD_WIDTH)` 守卫，在 `GenerateReductionSIMD` 的 `GenerateSimdInnerLoop` 中让 MSVC 生成了更差的代码（加 scope 干扰了优化），但在 ISPC 编译器（LLVM）中没有类似问题。

## 当前策略

```
ClosestPoint 使用 ISPC（0.643ms）
其他场景使用 C++ SIMD per-lane（fallback）
GenerateReductionSIMD 路径保留为后续大内循环 reduction job 的基础设施
Full-Width SIMD 路径已完全移除（此实验证明此路线不可行）
```

## 保留的基础设施

虽然 Full-Width SIMD 路径被移除，以下基础设施改动已保留：

### NativeSIMD.h

| 新增函数 | 用途 |
|---------|------|
| `n_cmp_lt/gt/eq/le/ge_ult_epi32` | 整数比较 → `n_mask` |
| `n_min/max_epi32` | 整数逐元素 min/max |
| `n_sub_epi32`, `n_mullo_epi32` | 整数算术 |
| `n_floor_ps` | 向量化 floor（含 NEON 回退） |
| `n_cvttps_epi32` | float → int 截断转换 |
| `n_not_mask` | mask 取反 |
| `n_any_true` | mask 非空检测 |
| `n_hmin_epi32` | int 水平归约 min |

### SimdValue.h

| 新增内容 | 用途 |
|---------|------|
| `simd_mask::any_true()` | mask 非空检测 |
| `simd_mask::operator&` / `~` / `&=` | mask 逻辑运算 |
| `simd_value<float>::floor()` | 向量化 floor |
| `simd_value<int>::convert(simd_value<float>)` | float → int 转换 |
| `simd_value<int>::min/max` | int min/max |
| `simd_value<int>` 的 `-` `*` `<` `>` `==` 等操作符 | 完整比较/算术 |
| `simd_value<float2/int2>` 的 broadcast 构造函数 | scalar → SIMD 广播 |
| `simd_value<float2/int2>::min/max` | 复合类型逐元素比较 |
| `simd_max()`, `simd_min()` 全局函数 | 避免 Windows min/max 宏冲突 |

## 参考文件

| 文件 | 说明 |
|------|------|
| [ispc-vs-cpp-simd-analysis.md](ispc-vs-cpp-simd-analysis.md) | ISPC vs C++ SIMD 原始分析 |
| [simd-architecture.md](simd-architecture.md) | SIMD 架构设计文档 |
| [NativeSIMD.h](../src/NativeDll/NativeSIMD.h) | 新增的 SIMD 原语 |
| [SimdValue.h](../src/NativeDll/SimdValue.h) | 新增的 simd_value 操作 |
| [OuterSimdGenerator.cs](../src/NativeTranspiler/Analyzer/OuterSimdGenerator.cs) | 最终代码（不含 Full-Width SIMD） |
