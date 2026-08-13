# 通用 ISPC 风格全 SIMD 代码生成器方案

## Context

当前 `OuterSimdGenerator.cs` 有 4 条路径：

1. **Register SIMD** — 纯 SoA 读写（无控制流）
2. **Per-Lane** — gather 8 query → 逐个提取标量执行（零 SIMD 并行）
3. **ISPC ClosestPoint** — 硬编码的 mask-managed 8-wide 模式
4. **ISPC FindWithin** — SIMD cell compute + per-lane scan

问题：任何不匹配 ClosestPoint/FindWithin 模式的 Job，即使有 `AutoSIMD=Enabled`，都走 PerLane 标量提取——**8 个 query 零 SIMD 并行**。

## 核心架构

```
C# AST (Execute 方法体)
  │
  ▼
SimdEligibilityAnalyzer (放宽检查 — 允许所有控制流)
  │
  ▼
SimdVariableAnalyzer (新) — 变量活跃分析 → uniform vs varying
  │
  ▼
SimdControlFlowGenerator (新) — 通用 mask 化代码生成
  │  ├─ IfStatement → mask push/pop
  │  ├─ ForStatement → per-lane 计数循环
  │  ├─ WhileStatement → mask-managed while
  │  ├─ Break/Continue → mask kill/temporarily disable
  │  └─ Return → do-while wrapper exit
  │
  ▼
生成 C++ 代码 (全部 8-wide SIMD, 无 per-lane 提取)
  ```

## 关键技术

### 1. 变量分类: uniform vs varying

| 变量类型 | 说明 | C++ 类型 | 示例 |
|---------|------|---------|------|
| `uniform` | 所有 lane 值相同 | `float`, `int` | `GridDimensions`, `SortedLength` |
| `varying` | 每个 lane 值不同 | `simd_value<T>` | `index`, `q`, `cell` |
| `reduction` | 规约变量 | `simd_value<T>` + 水平归约 | `bestDistSq`, `bestIdx` |

**自动推导规则**：
- Execute 参数（index/entityIndex）：varying
- 从 `NativeArray[index]` 读取：varying
- 从标量字段读取：uniform
- 赋值的左值：与右值相同

### 2. Mask 管理堆栈

```
执行时的 mask 堆栈:
───────────────────────────────────
mask_stack[0] = all_true          // 初始
mask_stack[1] = mask_stack[0] & condition  // if 的 true 分支
mask_stack[2] = mask_stack[0] & ~condition // if 的 else 分支
                                  // else 结束 → pop mask_stack[2]
                                  // true 结束 → pop mask_stack[1]
                                  // 恢复 mask_stack[0]
```

```cpp
// if (condition) {
simd_mask saved_mask = current_mask;
simd_mask cond_mask = translate_condition(condition);
current_mask = saved_mask & cond_mask;                   // push true mask
// ... true 分支体 ...

// } else {
current_mask = saved_mask & ~cond_mask;                  // push else mask
// ... else 分支体 ...

// }
current_mask = saved_mask;                               // pop → restore
```

### 3. For 循环

```cpp
// C#: for (int i = start; i < end; i++)
// 生成:
simd_value<int> v_i = v_start;           // per-lane 起始值
simd_value<int> v_end = ...;             // per-lane 结束值
int max_iter = hmax(v_end - v_i);        // 计数循环
for (int iter = 0; iter < max_iter; iter++) {
    simd_mask v_active{ n_cmp_lt_epi32(v_i.v, v_end.v) };
    simd_mask saved = current_mask;
    current_mask = current_mask & v_active;
    // ... 循环体 ...
    current_mask = saved;
    v_i = v_i + 1;
}
```

### 4. Break / Continue

```cpp
// break: 杀死当前 lane
current_mask = current_mask & ~v_active;

// continue: 跳过剩余 body
goto _continue;
```

### 5. Return

**最终方案：`goto` 退出嵌套循环**

```cpp
// return 生成为 goto label，标签在外层循环作用域底部
for (int si = ...) {
    for (int lane = 0; lane < NSIMD_WIDTH; lane++) {
        // ... lane body ...
        for (int dx = ...) {
            for (int dy = ...) {
                for (int iCell = ...) {
                    if (found == MaxNeighbor)
                        goto _simd_exit;   // ← 跳出所有嵌套循环
                }
            }
        }
    }
    _simd_exit: ;  // ← 标签在 for(si) 作用域内、for(lane) 之外
}
```

**为什么不是 `do{}while(false)` + `break`？**
`break` 只退出最内层循环，在多级嵌套循环中无法跳过外层循环。`goto` 是 C++ 标准中唯一能退出任意深度嵌套循环的机制。

**为什么不是 `do{}while(false)` + `goto`？**
`goto` 在 `do{}while(false)` 内跳转到 `_simd_exit:` 标签，后者如果在 `do{}` 作用域内则等价于 `break`；如果在 `do{}` 作用域外则 MSVC C2362 报错。最终方案：标签放在 `for(lane)` 循环外、`for(si)` 循环内，`goto` 从 lane 内部任意嵌套层级直接跳到标签处。

**MSVC 兼容性**：
MSVC 的 C2362 错误在 `goto` 标签跨越了任何变量声明时触发。解决方法是把标签放在**所有变量声明之后**的作用域层级——即放在 `for(lane)` 循环后、`for(si)` 作用域内。此时 `si` 已在作用域中（for 循环变量），`lane` 及内部变量在标签处超出作用域，不触发 C2362。

## 已具备的基础设施

从 Full-Width SIMD 实验和后续修复中保留的 SIMD 原语：

| 操作 | 函数 | 来源 |
|------|------|------|
| 整数比较 → mask | `n_cmp_lt/gt/eq/le/ge/ult_epi32` | Full-Width |
| int min/max | `n_min_epi32`, `n_max_epi32` | Full-Width |
| floor | `n_floor_ps` | Full-Width |
| float→int 截断 | `n_cvttps_epi32` | Full-Width |
| mask 逻辑 | `n_and_mask`, `n_not_mask`, `n_andnot_mask` | Full-Width |
| mask 检测 | `n_all_zero`, `n_any_true` | Full-Width |
| blend | `n_blend_ps`, `n_blend_epi32` | Full-Width |
| 水平归约 | `hmin/hmax/hsum/hmin_idx` for float + int | Full-Width |
| gather | `n_gather_ps<stride>`, `n_gather_epi32<stride>` | Full-Width |
| masked gather | `n_gather_masked_ps<stride>` | 新增 |
| int2/float2 类型 | `simd_value<float2/int2>` | Full-Width |
| `simd_mask` 类型 | `operator&/~/&=`, `any_true/all_false` | Full-Width |

## 需要新增的原语

### `SimdValue.h`

- `simd_mask::all_false()` — 全 0 mask
- `simd_mask operator|(simd_mask, simd_mask)` — OR
- `simd_mask operator^`, `operator-=` 等

### `NativeSIMD.h`

- `n_or_mask`, `n_xor_mask`
- `n_cmp_ne_ps` / `n_cmp_ne_epi32`（当前用 `n_not_mask(n_cmp_eq_...)` 替代）

## 实施阶段

### Phase 0: 基础设施加固（~2 天）

- `SimdValue.h` + `NativeSIMD.h`：补充必要的 mask 操作

### Phase 1: 变量分析器（~3 天）

**新文件**：`src/NativeTranspiler/Analyzer/SimdVariableAnalyzer.cs`

```csharp
class SimdVariableInfo {
    public string Name;
    public VarKind Kind;  // Uniform, Varying, Reduction
    public string? CppType;
    public string? InitSIMDExpr;
}

class SimdVariableAnalyzer {
    Dictionary<string, SimdVariableInfo> Analyze(MethodDeclarationSyntax method);
    // 遍历 AST，对每个变量标记 uniform/varying/reduction
}
```

### Phase 2: 通用控制流生成器（~5 天）

**新文件**：`src/NativeTranspiler/Analyzer/SimdControlFlowGenerator.cs`

```csharp
class SimdControlFlowGenerator {
    StringBuilder sb;
    SimdMask currentMask;
    Dictionary<string, SimdVariableInfo> variables;
    Stack<SimdMask> maskStack;
    
    void Generate(SyntaxList<StatementSyntax> stmts);
    void GenerateIf(IfStatementSyntax stmt);
    void GenerateFor(ForStatementSyntax stmt);
    void GenerateWhile(WhileStatementSyntax stmt);
    void GenerateBreak(BreakStatementSyntax stmt);
    void GenerateContinue(ContinueStatementSyntax stmt);
    void GenerateExpression(ExpressionSyntax expr);
    string TranslateExpr(ExpressionSyntax expr, VarKind targetKind);
}
```

### Phase 3: 统一入口 + 适配器（~2 天）

`OuterSimdGenerator.cs` 入口分派：

```csharp
public string Generate(string scalarBody) {
    if (IsFullySIMDCapable())
        return GenerateFullSIMDFromAST();
    else if (IsSimpleSoaBody())
        return GenerateRegisterSIMD(scalarBody);
    ...
}
```

### Job 类型适配

| Job 类型 | Execute 签名 | SIMD 外层生成 |
|----------|-------------|--------------|
| `IJob` | `Execute()` | 无外层 for，内部 if/for/while 仍 mask 化 |
| `IJobParallelFor` | `Execute(int index)` | `for(si = 0; si < n; si += 8)` |
| `IJobFor` | `Execute(int index)` | 同上 |
| `IJobChunk` | `Execute(ArchetypeChunk, int index)` | 同上，chunk 数据 gather |
| `IJobEntity` | `Execute(ref Position, ref Velocity)` | `for(si=0; si<n; si+=8)` + entity batch |

### Phase 4: 测试和基准（~1 天）

1. 现有 ClosestPoint 走新通路 → 0.58ms 不退化
2. FindWithin 走新通路 → 不退化
3. 新增混合控制流 Job → 结果正确
4. 验证 uniform/varying 自动检测正确

## 关键经验（从 Full-Width 实验学习）

1. **从 AST 生成，不是从文本匹配**：Full-Width 失败的核心原因是在已生成的 C++ 文本上做 Regex 模式匹配，无法处理嵌套控制流。AST 级生成可以精确知道变量类型和作用域。

2. **内循环不做 mask 化**：ISPC 的 `foreach` 对内循环不做 mask 管理——每个 lane 的 `i` 独立推进，但所有 lane 同步迭代。用 `for(int iter)` 计数循环 + `hmax` 确定迭代次数的方式已在 `e2d27a4` 中验证。

3. **只对 if/else 做 mask push/pop**：Full-Width 试图把所有控制流（dx/dy 循环、continue、if）都变成 mask 操作。正确的做法是只对 **if 条件分支**做 mask push/pop。循环用计数循环，continue/break 用 goto。

## 风险

1. **MSVC 代码体积膨胀** — mask push/pop + for(iter) 生成大量 SIMD 代码。`__forceinline` 已证明不稳定。如果发生需要函数拆分
2. **Loop-invariant 提升** — 当前手动 hoist 了 GridOrigin 等。自动生成器需要 hoist 分析
3. **NEON 兼容性** — 需要重新验证