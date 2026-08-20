# Auto-SIMD 追平 ISPC 可行性分析报告

## 背景

HeavyMove IJobChunk 基准测试（1M entities），Auto-SIMD 始终无法追平 ISPC 的性能。经过 7 轮代码生成优化和 6 组系统性实验，记录了所有尝试和结论。

## 最终性能数据

所有实验在同一台机器上运行（AVX2 8-wide，除 ISPC 默认 AVX-512）：

| # | 实验 | 编译器 | HeavyMove | LightMove | 与 ISPC 差距 |
|---|------|--------|:---------:|:---------:|:------------:|
| 0 | MSVC + 展开 16 次（基线） | MSVC | ~20ms | ~0.50ms | 10x |
| 1 | **ClangCL** 换编译器后端 | **ClangCL** | **~17ms** | **~0.17ms** | 8.5x |
| 2 | 紧凑 for 循环（不展开） | ClangCL | ~17ms | ~0.12ms | 8.5x |
| 3 | 裸 `__m256` 去 simd_value 包装 | ClangCL | ~17ms | ~0.17ms | 8.5x |
| 4 | 数组 sinf/cosf 自动向量化 | ClangCL | ~17ms | ~0.13ms | 8.5x |
| 5 | `#pragma unroll(disable)` PHI 结构 | ClangCL | ~19ms | ~0.24ms | 9.5x |
| 6 | **标量 sinf/cosf + `#pragma clang loop vectorize`** | **ClangCL** | **~17ms** | ~0.13ms | 8.5x |
| — | **ISPC AVX-512** (默认) | ISPC→LLVM | **~2.0ms** | ~0.50ms | 1x |
| — | **ISPC AVX2** (同宽度对比) | ISPC→LLVM | **~2.9ms** | ~0.51ms | 1x |

## 已排除的因素（都不是根因）

| 因素 | 实验 | 结论 |
|------|------|------|
| 编译器后端 | 1 vs 0 | ClangCL 仅改善 15%（20→17ms） |
| 循环展开 | 2 vs 1 | 紧凑循环和展开效果相同 |
| Wrapper 开销 | 3 vs 2 | 裸 `__m256` 和 `simd_value<T>` 一样 |
| LLVM IR 结构 | 5 vs 1 | 24 PHI 节点 vs 4 PHI 节点——无差别 |
| 标量自动向量化 | 6 | Clang 无法处理嵌套循环+循环携带依赖 |
| ISA 宽度 | ISPC AVX2 | ISPC AVX2 = 2.9ms，AVX-512 = 2.0ms |
| 数学库精度 | SLEEF vs ISPC | 两者都用多项式，精度级别相同 |

## 根因分析

### 核心问题：循环携带依赖（Loop-Carried Dependency）

HeavyMove 内循环有 16 次迭代，每次迭代的 `accX`/`accY` 依赖前一次的值：

```csharp
for (int iteration = 0; iteration < 16; iteration++) {
    float wave = MathF.Sin(accX + iteration * 0.03125f) + ...;
    accX = accX * 0.985f + wave * 0.015f + ...;  // ← accX 依赖自身
    accY = accY * 0.982f - wave * 0.012f + ...;  // ← accY 依赖自身
}
```

这种模式导致：
1. **C++ 编译器（MSVC/Clang）无法向量化外层的实体循环**——内层迭代间的依赖阻止了向量化器将循环展开
2. **ISPC 的 SPMD 编译器能处理**——`foreach` + `for` 嵌套中，ISPC 编译器知道 `it` 是 uniform，8 个实体的 `accX` 是独立的，可以用 PHI 节点管理跨迭代状态

### 为什么 Clang 的 auto-vectorizer 失败

独立测试（简化代码）中 Clang 能生成 `@llvm.sin.v8f32`，但在 ECS 实际代码中失败：

```
loop not vectorized: value that could not be identified as
reduction is used outside the loop
```

原因：`accX` 和 `accY` 在内循环中计算，在外循环中使用。向量化器无法证明这是 reduction 模式。

### ISPC 的 LLVM IR 优势

ISPC 生成的 LLVM IR 有 **24 个 PHI 节点、12 个 CFG 标签、10 个回边**。我们的 ClangCL 生成的 IR 只有 **4 个 PHI 节点、0 个循环标签、0 个回边**——全部被展开成直线条代码。

ISPC 的代码结构紧凑（热循环 ~85 条指令），CPU 可在 µop cache 中保持循环体，寄存器分配更高效。

## 尝试过的解决方案

### ✅ 成功：ClangCL 工具链支持

在 CMakeLists.txt 生成器中添加 ClangCL 检测分支，`cmake -T ClangCL` 即可使用 LLVM 后端：

- **LightMove**: 0.5ms → 0.12ms（**4x 改善**，超过 ISPC）
- **HeavyMove**: 20ms → 17ms（15% 改善）
- 跨平台兼容：Windows ClangCL、Linux Clang、macOS Apple Clang

### ✅ 成功：run_clangcl.bat 生成

transpiler 在 `NativeTranspiler_Generated/` 下生成 `run_clangcl.bat`，一键 ClangCL 编译。

### ✅ 部分成功：AutoSIMD.Vectorize 模式

新增 `AutoSIMD.Vectorize` 枚举 + 代码生成路径。生成纯标量循环 + `#pragma clang loop vectorize(enable)`。对 HeavyMove 无效（auto-vectorizer 放弃），但其他负载可能受益。

### ❌ 失败：所有 intrinsic 侧优化

- 不展开循环 → 17ms
- 裸 `__m256` 去包装 → 17ms
- 自实现 SLEEF vs 编译器 sinf → 17ms
- AVX-512 16-wide（ISPC 的对比）→ ISPC 自身也只从 2.9ms 降到 2.0ms

## 关键结论

```
┌─────────────────────────────────────────────────────────────────┐
│  Auto-SIMD (C++ intrinsic 方式) 无法在 HeavyMove 上追平 ISPC   │
│                                                                 │
│  根因：内循环的 accX/accY 循环携带依赖是                       │
│        C++ 编译器堆栈无法处理的结构                             │
│        ISPC 的 SPMD 编译器能编译为 PHI 节点循环                │
│                                                                 │
│  这是编译器架构层面的差距，不是优化参数能解决的                 │
└─────────────────────────────────────────────────────────────────┘
```

## 实际路线

| 负载类型 | 适合后端 | 原因 |
|---------|---------|------|
| **Heavy**（sin/cos/sqrt 内循环） | **ISPC** | 10x 优势，不可替代 |
| **Light**（简单算术） | **Auto-SIMD + ClangCL** | 比 ISPC 还快 |
| **全平台覆盖** | Auto-SIMD | ISPC 有的平台它都有 |

transpiler 已经支持 ISPC 代码生成（`Target = BackendTarget.Ispc`），和 Auto-SIMD 可以共存。

## 文件变更记录

| 提交 | 改动 |
|------|------|
| `2cb8dbe` | ClangCL 感知的 CMakeLists 生成 |
| `198448f` | run_clangcl.bat 生成（带 build-dir 清理） |
| `0e41c62` | AutoSIMD.Vectorize 枚举 + 代码生成 + 测试用例 |

## 参考

- [ISPC optimization journey](../performance/ispc-optimization-journey.md)
- [Auto-SIMD 实现记录](simd-auto-ijobchunk-implementation.md)
- [记忆文件](../../../.claude/projects/e--GODOT-Project-EntJoy/memory/clangcl-ispc-gap-analysis.md)
