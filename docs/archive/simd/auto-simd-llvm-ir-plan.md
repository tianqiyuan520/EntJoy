# Auto SIMD 转译器直出 LLVM IR 实现计划

## 背景

HeavyMove IJobChunk（1M 实体）基准测试数据：

| 版本 | HeavyMove | 与 ISPC AVX2 差距 |
|------|:---------:|:----------------:|
| Auto SIMD (MSVC) | ~20ms | ~10x |
| Auto SIMD (ClangCL) | ~17ms | ~5.8x |
| ISPC AVX2 | ~2.9ms | 1x |

经过 7 轮实验验证，C++ 编译器前端（ClangCL）即使给了同样的内联 sin/cos 多项式和紧凑循环结构，也无法生成和 ISPC SPMD 编译器相同质量的 LLVM IR。差距在编译器前端架构层面，不是换参数能解决的。

**方案**：转译器直接生成 LLVM IR（`.ll` 文件），跳过 C++ 编译步骤，直接用 `llc` 编译成机器码。

## 方案对比

| | 转译器直出 LLVM IR | 修改 ISPC 源码 |
|---|---|---|
| 代码位置 | transpiler 新增 C# 文件 | ISPC 的 C++ 源码 |
| 代码量 | ~1500 行 C# | ~500 行 C++（需先读懂 60K+ 行 ISPC） |
| 技术栈 | 与 transpiler 统一（C#） | 不同技术栈（C++） |
| 构建依赖 | 仅 `llc`（ClangCL 自带） | ISPC 编译器 + `llc` |
| 维护 | 无外部依赖 | 需维护 ISPC 分支 |

**选择：转译器直出 LLVM IR。** 技术栈统一、少一个依赖、完全可控。

## 执行步骤

### Phase 0：环境确认

确认开发环境中 `llc` 可用，测试最小的 `.ll` → `.obj` 编译链路。

### Phase 1：手写原型验证

手写 HeavyMove 计算内核的 `.ll` 文件（200-300 行），镜像 ISPC 的 LLVM IR 结构：
- 外层实体 batch 循环
- 内层 PHI 节点循环（16 次迭代）
- 内联 sin/cos 多项式（SLEEF 系数，与 `NativeSIMD_math.h` 相同）
- `@llvm.sqrt.v8f32`（通用内建函数）
- `@llvm.masked.gather.v8f32`（通用的 gather）
- 逐 lane extractelement + store 写回

用 `llc -O2` 编译后集成到 benchmark，验证性能是否接近 ISPC（目标 3-5ms）。

### Phase 2：转译器集成

新建 `src/NativeTranspiler/Analyzer/Common/LlvmIrGenerator.cs`。

复用现有基础设施：
- `SimdVariableAnalyzer` — 变量 uniform/varying 分类
- `SimdEligibilityAnalyzer` — SIMD 安全性检查
- `SimdControlFlowGenerator` 的循环分析

新增代码：
- LLVM IR 表达式翻译（将 C# AST 表达式树翻译成 LLVM IR 操作）
- PHI 节点循环生成（小常数 uniform 循环走 PHI 路径）
- Gather/scatter 生成
- 数学函数映射（`MathF.Sin` → 内联多项式 LLVM IR）
- 函数签名和导出

在 `NativeTranspilerGenerator.cs` 中新增 `BackendTarget.LlvmIr` 枚举和对应的代码输出路径。

### Phase 3：构建集成

- CMakeLists.txt 生成器中集成 `llc` 调用
- 生成 `run_llvm.bat` 编译脚本（类似已有 `run_clangcl.bat`）
- 没有 `llc` 的平台 fallback 回 C++ 代码生成
- 链接生成的 `.obj` 到最终 DLL

### Phase 4：跨平台验证

- x86 Windows (AVX2) — benchmark 与 ISPC 对比
- ARM64 — 验证 NEON 指令生成质量
- Linux/macOS — 验证 `llc` 可用性和 ELF/Mach-O 输出

## 关键技术决策

### PHI 节点 vs alloca

方案 A（简单）：用 `alloca` + `load`/`store`，`llc` 的 `-mem2reg` pass 自动转 PHI。代码生成简单，性能可能稍低。

方案 B（最优）：直接生成 PHI 节点循环，和 ISPC 的输出一致。代码生成稍复杂，性能最优。

**选择方案 B**——性能是目标，complexity 可控。

### sin/cos

不依赖 `@llvm.sin.v8f32`（LLVM 后端可能降级为标量 sinf 调用）。直接内联 SLEEF 多项式（和 `NativeSIMD_math.h` 中的 `_n_sin_avx2` 相同）：

```llvm
; range reduction
%mul = fmul <8 x float> %phase, <float 0x3FE45F3060000000...>  ; * 2/π
%round = call <8 x float> @llvm.round.v8f32(<8 x float> %mul)
%qi = fptosi <8 x float> %round to <8 x i32>
; ... 多项式展开 ...
```

这个多项式用的是 `fmul`/`fadd`/`round`/`select` 等通用 LLVM 指令，所有平台都能执行。

### Gather

使用 `@llvm.masked.gather.v8f32`：
```llvm
; 计算指针向量
%ptr = getelementptr float, ptr %base, <8 x i64> %offsets
; gather
%val = call <8 x float> @llvm.masked.gather.v8f32(<8 x ptr> %ptr, i32 4, <8 x i1> %mask, <8 x float> undef)
```

x86 上 `llc` 会降级为 `VPGATHERDD`，ARM 上降级为 `ld1` 或逐元素加载。

### Scatter

和 ISPC 一样，逐 lane extractelement + store：
```llvm
%val0 = extractelement <8 x float> %result, i64 0
%ptr0 = getelementptr float, ptr %base, i64 %offset0
store float %val0, ptr %ptr0
; ... lane 1-7 重复 ...
```

因为 AoS 布局限制，scatter 无法用 `@llvm.masked.scatter`（需要 SoA 布局才行）。

## 文件变更清单

| 文件 | 动作 |
|------|------|
| `src/NativeTranspiler/Analyzer/Common/LlvmIrGenerator.cs` | **新建** |
| `src/NativeTranspiler/Analyzer/NativeTranspilerGenerator.cs` | 增加 `.ll` 输出路径 |
| `src/NativeTranspiler/Analyzer/NativeTranspiler.cs` 或枚举定义 | 增加 `BackendTarget.LlvmIr` |
| `src/EntJoySample/NativeTranspiler_Generated/*.ll` | 生成的 LLVM IR 文件 |
| CMakeLists.txt 生成逻辑 | 集成 `llc` 编译 |
| `run_llvm.bat` | **新建** LLVM 编译脚本 |

## 验证目标

| 阶段 | 预期 HeavyMove | 里程碑 |
|------|:-------------:|--------|
| Phase 0 (环境确认) | — | `llc` 编译测试通过 |
| Phase 1 (手写原型) | 3-5ms | LLVM IR 路径验证可行 |
| Phase 2 (转译器集成) | ~3ms | 自动生成 IR 正确 |
| Phase 3 (构建集成) | ~3ms | 全自动编译链路 |
| Phase 4 (跨平台) | x86:~3ms, ARM:~?ms | 全平台验证 |

## 风险

| 风险 | 概率 | 缓解 |
|------|------|------|
| LLVM IR 生成的 PHI 节点循环性能不如 ISPC | 中 | Phase 1 手写原型先验证 |
| ARM64 NEON 上 `<8 x float>` 需要拆分成 2×`<4 x float>` | 中 | Phase 1 测试 ARM target，必要时在 IR 中手动拆分 |
| 没有 `llc` 的环境 | 低 | fallback C++ 生成 |
| LLVM IR 版本兼容 | 低 | 使用 ClangCL 自带的 LLVM 版本 |