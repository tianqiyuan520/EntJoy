# Auto-SIMD 基准测试套件

跨后端性能对比：C# (managed) / C++ (scalar) / C++ SIMD (Auto-SIMD) / ISPC

## 测试案例

| Case | 计算模式 | 测试重点 |
|------|---------|---------|
| 1_SimpleArith | `a[i]*b[i]+c[i]` | 纯算术吞吐 |
| 2_MathFunctions | `sqrt+sin*cos+log` | 数学库 SIMD 质量 |
| 3_SimpleReduce | `if(v<best)best=v` | SIMD blend/归约 |
| 4_ComplexFlow | `if/else if/else` | mask 管理+多分支 |
| 5_GatherReduce | `gather+dist+reduce` | gather+inner loop+mask |

## Job 变体（每个 Case 5 个）

| 变体 | Job 类型 | 后端 | 当前状态 |
|------|---------|------|---------|
| `_CSharp` | IJobParallelFor | C# managed | ✅ 可用 |
| `_Cpp` | IJobParallelFor | C++ scalar | ✅ 可用 |
| `_SIMD` | IJobFor | Auto-SIMD | ✅ 可用 |
| `_SIMD_IJob` | IJob | Auto-SIMD (无 batch) | ✅ 可用（2026-07-23 新增） |
| `_ISPC` | IJobParallelFor | ISPC | ✅ 可用 |

## LLVM IR 分析

ISPC → LLVM IR 文件参考归档：`docs/archive/simd/closestpoint_ispc.ll`。

生成方法：
```bash
cd src/EntJoySample/03_NativeTranspiler/AutoSIMDTest
gen_llvm_ir.bat
```

分析重点：
- **mask 传播**：ISPC 如何通过 phi+select 实现无分支 mask 管理
- **gather 模式**：`llvm.x86.avx2.gather.d.ps.256` vs `_mm256_i32gather_ps`
- **blend 指令**：`llvm.x86.avx.blendv.ps.256` 的生成条件
- **循环展开**：ISPC 的 `foreach` 和 `for(uniform)` 如何映射到 LLVM IR
- **FMA 融合**：`fmadd` 指令的生成情况

## 运行

```bash
# 1. 构建 NativeDll
dotnet build src/EntJoySample

# 2. 运行基准测试
# （待集成到主 Program.cs）
```
