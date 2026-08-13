# ISPC 性能优化分析报告

## 最终 Benchmark 数据

```
==========================================================================================================
            Static─         IJob──          IJobFor─         IJobPF──
  Case      C#     C++    SIMD   ISPC   C#     C++    SIMD   ISPC   C#     C++    SIMD   ISPC   C#     C++    SIMD   ISPC
  ────      ───── ───── ───── ───── ───── ───── ───── ───── ───── ───── ───── ───── ───── ───── ───── ─────
  SimpleArith     0.271     0.013     0.012     0.016     0.354     0.325     0.326     0.322     2.699     2.176     2.166     2.152     0.226     0.224     0.210     0.190
  MathFuncs     2.548     1.951     0.065     0.062     2.595     2.599     2.617     2.711     4.134     4.165     4.092     4.086     0.461     0.447     0.426     0.448
  Reduce       12.275     1.811     1.786     1.818    13.887    13.970    14.425    13.737    14.919    18.969    18.956    19.086     6.151     6.254     6.241     6.247
  ComplexFlow     0.530     0.382     0.013     0.017     0.578     0.578     0.591     0.580     2.007     2.389     2.431     2.400     0.221     0.233     0.228     0.228
  GatherReduce    36.686    13.381    31.785     9.848    34.867    38.983    39.289    36.720    49.408    45.302    56.655    41.975     7.333     8.017     7.497     8.508
----------------------------------------------------------------------------------------------------------
  Stc=Static(direct call) | Job=Execute | For=Schedule | PF=Schedule(64)


            IJobChunk───                 IJobEntity───
  Case      C++    SIMD   VZ     ISPC   C++    SIMD   VZ     ISPC
  ────      ───── ───── ───── ───── ───── ───── ───── ─────
  LightMove 0.208 0.346 1.041   0.475 0.437   N/A 0.463
  HeavyMove 18.560 18.615 18.791 2.992 23.221 23.814 23.971 23.727
```

## 已完成的优化

### 1. 多层嵌套循环翻译优化

**问题**：ISPC 代码生成器将外层 `for` 转译成 `foreach`，使 index 为 varying，内层 `arr[idx*K+j]` 变为 gather 指令。

**解决**：检测到外层 `for` 的 body 中有嵌套 for/while 时，改为 `for (uniform int i) { foreach (j) { arr[i*K+j]; reduce_min() } }`。

```
优化前 (foreach + for → gather):
  foreach (i = 0 ... N) {              ← i 为 varying
    for (int j = 0; j < 100; j++) {
      arr[i*100 + j];                   ← GATHER！
    }
    result[i] = b;
  }

优化后 (uniform for + foreach → sequential load):
  for (uniform int i = 0; i < N; i++) { ← i 为 uniform
    foreach (j = 0 ... 100) {           ← j 为 varying 但连续
      arr[i*100 + j];                   ← 顺序 SIMD load！
    }
    result[i] = reduce_min(b);
  }
```

**覆盖路径**：Static/IJob/IJobFor/IJobPF/IJobChunk/IJobEntity

### 2. 内层 for 变量 uniform 修饰

**问题**：`_insideForeach=true` 时内层 for 回退到 `base.TranslateForStatement()`，输出 `int j` 而非 `uniform int j`，ISPC 默认视为 varying。

**解决**：在 `MethodIspcTranslator` 和 `IspcStatementTranslator` 的 fallback 路径中输出 `for (uniform int ...)`。

### 3. do-while 支持

`CppStatementTranslator` 新增 `DoStatementSyntax` case 和 `TranslateDoStatement` 方法。

### 4. For/PF 路径调度优化

```
GenerateIspcFunction:
  有嵌套循环 → for (uniform int i) + 内层 foreach + reduce_min
  无嵌套循环 → foreach（原有行为）
```

### 5. Build 系统

- ISPC 编译加 `-O3` 标志
- 目标从 `avx512skx-i32x16` 改为 `avx2-i32x8`（兼容性）
- 预处理 CMakeLists.txt 和 run_ispc.bat 同步更新

## 优化效果

| Case (Static) | 优化前 ISPC | 优化后 ISPC | 对比 C++ | 提升 |
|:-------------|:----------:|:----------:|:--------:|:----:|
| **Reduce** | 13.629 ms | **1.818 ms** | 1.811 ms | **7.5x** |
| **GatherReduce** | 21.859 ms | **9.848 ms** | 13.381 ms | **2.2x，超 C++ 36%** |
| ComplexFlow | 0.030 ms | **0.017 ms** | 0.382 ms | 改善 |
| HeavyMove (IJobChunk) | 1.989 ms | **2.992 ms** | 18.560 ms | **6.2x** |
| SimpleArith/MathFuncs | — | — | — | 持平 |

## IJobEntity ISPC 问题

### 数据

```
IJobChunk C++:    18.6ms  (kernel ~16ms + ECS ~3ms)
IJobChunk ISPC:   3.0ms   (kernel ~0ms + ECS ~3ms)     ✅ 6x
IJobEntity C++:   23.2ms  (kernel ~16ms + ECS ~7ms)
IJobEntity ISPC:  23.7ms  (kernel ~0ms + ECS ~24ms)    ❌ 无改善
```

### 已排除的可能根因

| 假设 | 试验 | 结果 |
|------|------|------|
| EntityBatch 调度开销 | ChunkRange 调度 | ❌ 同样 24ms |
| `TrackEntityJob` / `RegisterActiveJob` | 跳过全部 entity tracking | ❌ 同样 23ms |
| 查询缓存 miss | `BuildRawChunkScheduleCache` 直写 | ❌ 同样 23ms |
| Generic 类型约束差异 | 非泛型直写 | ❌ 同样 23ms |
| Chunk vs Batch 数据结构 | 3 种调度路径对比 | ❌ 全部 23ms |
| 参数顺序差异 | IJobChunk 与 IJobEntity wrapper 对比 | ❌ 结构一致 |

### Unity 方案的问题

生成 IJobChunk 适配器（`_ChunkAdapter : IJobChunk`）+ `[NativeTranspile(Target = Ispc)]`，让调度走 chunk 路径——方案在理论上正确，但实现上遇到引导循环问题：适配器由源代码生成器在第一遍编译中生成，但需要第二遍编译才能被转译器处理。

### 根因定位

~21ms 额外开销存在于所有调度路径，说明问题不在 C# 调度层。最可能的根因在 C++ 侧的 `JobSystem_ScheduleChunkRangeJobEx` 中——对 IJobEntity 类型的 job 可能有不同的 worker 分配策略或依赖管理逻辑。需要性能分析工具（ETW/PerfView）进一步定位。

### 建议

- **IJobEntity 重度计算 → 改写为 IJobChunk ISPC**，已证明 6x 加速
- **IJobEntity 轻量计算 → C++ SIMD 版本即可**，ISPC 无额外收益
- 如果未来 ECS 核心支持 chunk 级 entity 遍历，IJobEntity ISPC 的加速会自然生效

## 文件改动清单

| 文件 | 改动 |
|------|------|
| `NativeTranspiler/Analyzer/Common/IspcGenerator.cs` | MethodIspcTranslator 嵌套循环检测 + uniform-for + foreach + reduce_min；GenerateIspcFunction For/PF 路径优化 |
| `NativeTranspiler/Analyzer/IspcStatementTranslator.cs` | 添加 `_insideForeach`/`_insideUniformFor`/`_varyingAccumulatorVars`；重写 TranslateForStatement/TranslateWhileStatement/TranslateLocalDeclaration/TranslateExpressionStatement |
| `NativeTranspiler/Analyzer/IspcChunkStatementTranslator.cs` | foreach_tiled body 设置 `_insideForeach` 防止嵌套 foreach |
| `NativeTranspiler/Analyzer/CppStatementTranslator.cs` | 新增 do-while 支持（`TranslateDoStatement`） |
| `NativeTranspiler/Analyzer/NativeTranspilerGenerator.cs` | ISPC 目标改为 `avx2-i32x8`；添加 `-O3` 标志 |
| `NativeTranspiler/Analyzer/BindingsGenerator.cs` | IJobEntity ISPC → `ScheduleIspcEntityRangeRaw`（轻量 chunk 调度）+ 对应字段/DllImport |
| `NativeTranspiler/Analyzer/Common/IspcGenerator.cs` | 直写调度 `ScheduleIspcEntityRangeRaw`（跳过 entity tracking） |
| `EntJoy/JobSystem/NativeJobScheduler.cs` | 新增 `ScheduleIspcEntityRangeRaw<T>` 方法 |
| 手写 ISPC 参考文件 | `SimpleReduce.ispc` / `GatherReduce.ispc` 同步更新 |
