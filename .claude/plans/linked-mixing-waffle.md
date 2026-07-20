# ISPC 优化计划

## 当前状态

已完成的优化:
- IJobChunk/IJobEntity 的 struct copy-in/copy-out 消除 (struct proxy 机制)
- per-field 分解已回退 (AoS 下 gather/scatter 翻倍反而更慢)

当前 benchmark 数据 (Light, 1M entities):

| 路径 | 耗时 | 纯计算(来自Sleep) | 调度开销占比 |
|------|:----:|:----------------:|:----------:|
| ISPC IJobChunk | 0.429 ms | ~0.095 ms | **~78%** |
| ISPC IJobEntity | 0.561 ms | ~0.099 ms | **~82%** |
| C++ IJobChunk | 0.185 ms | ~0.970 ms(Sleep含调度) | — |

核心洞察: **ISPC Light 的调度开销占比高达 78-82%**，实际计算只需 ~0.1ms。优化方向就是削减调度开销。

---

## 优化 1：移除 `--opt=disable-fma` (最简高收益)

**位置**: 两个 `CMakeLists.txt` (由 `IspcGenerator.cs` 的 ISPC 编译命令生成)

当前 ISPC flags:
```cmake
--target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma
```

`--opt=disable-fma` 禁用了 FMA (Fused Multiply-Add)，强制 mul+add 序列使用两条独立指令。在 AVX-512 上 `vfmadd213ps` 延迟与单独 `vmulps` 相同但吞吐翻倍。

影响度:
- Heavy 计算: `accX = accX * 0.985f + wave * 0.015f` 这类模式非常常见
- 预计收益: **10-20%** Heavy 计算加速
- Light: 基本无影响 (瓶颈在内存带宽)
- 精度: FMA 单次舍入 vs 两次舍入，差异约 1 ULP (~1.2e-7)，已在现有 `epsilon=0.001` 验证阈值内

**改动**: 删除 `--opt=disable-fma`，或改为仅在 Debug 下使用。

---

## 优化 2：增大 ISPC Tile 粒度 (最大收益)

**问题**: `ResolveEcsBatchRangeSize()` 统一为所有后端生成 ~58 个 tiles (15 workers × 4 tiles/worker)。但 ISPC 内部已是 16-wide SIMD。Light 场景 ISPC 实际计算 ~0.095ms 但调度开销将总时间推到 0.429ms。

**方案**: ISPC wrapper 层使用更大的 `rangeSize` 参数，减少 ISPC 函数的调用次数。

具体实施: 修改 `CppJobGenerator.cs` / `IspcGenerator.cs` 中 ISPC wrapper 代码生成，在 `_RangeAdapter` 和 `_EntityBatchAdapter` 中做批处理合并。

**改动前 (IJobChunk ISPC RangeAdapter)**:
```cpp
for (int __chunkIndex = __startIndex; __chunkIndex < __endIndex; ++__chunkIndex) {
    Adapter(context, &__chunks[__chunkIndex]);  // 每次调用都触发一次 ISPC 完整函数调用
}
```

**改动后**: 在 wrapper adapter 中合并粒度，或让 C# 侧传递更大的 rangeSize。

更简单的方案: 在 `CppJobGenerator.cs` 生成 Adapter 代码时，修改 `BuildChunkJobParameters` / `BuildEntityBatchJobParameters` 中传入更大的 rangeSize。

或者最直接的: 在 ISPC wrapper 的 `_RangeAdapter` 中识别到 tiles 是连续的 chunk 时，合并为一个大的 ISPC tile。

预计收益:
- ISPC IJobChunk Light: 0.429ms → **~0.15-0.20ms** (减少 ~60% 调度开销)
- ISPC Heavy: 基本不变 (计算本身已主导)

---

## 优化 3：ISPC Wrapper 中加 `__assume` 对齐提示

**位置**: `IspcGenerator.GenerateCppChunkWrapper()` `IspcGenerator.GenerateCppEntityBatchWrapper()`

C++ EntityBatch Adapter 有对齐 assume:
```cpp
__assume((intptr_t)positions_ptr % 64 == 0);
```

ISPC wrapper 没有。64 字节对齐的指针让编译器生成 `vmovdqa` (对齐 load/store) 而非 `vmovdqu` (非对齐)。这对 AVX-512 16-wide 的差异更明显。

**改动**: 在 ISPC wrapper 的 Adapter 函数中，对每个 `chunkData->requiredComponentArrays[i]` 或 `batchData->componentArrays[i]` 的 reinterpret_cast 后追加 `__assume`。

预计收益: **2-5%** Light 和 Heavy 均受益。

---

## 优化 4：ISPC wrapper adapter 简化

**位置**: `IspcGenerator.GenerateCppChunkWrapper()` 和 `GenerateCppEntityBatchWrapper()`

当前 adapter 代码每个 batch 都做一次完整的 __EntJoyChunkContextHeader 解析:
```cpp
auto* __header = (__EntJoyChunkContextHeader*)context;
int __headerSize = (int)sizeof(__EntJoyChunkContextHeader);
int __typesDataSize = __header->allEnabledCount * (int)sizeof(int);
int __requiredTypesDataSize = __header->requiredComponentTypeIdCount * (int)sizeof(int);
char* __jobContext = (char*)context + __headerSize + __typesDataSize + __requiredTypesDataSize;
```

`_RangeAdapter` 在 for 循环外先解析一次 header，但 `_Adapter` (单 chunk 回调) 每次都要解析 — 虽然很小但 ISPC Light 场景一次 chunk 调用只有 ~1.6µs 计算，header 解析开销占比不小。

**改动**: 在 `_RangeAdapter` 中将 header 解析提升到循环外，传入已解析的 jobContext 指针到辅助函数。

---

## 优化 5：ISPC `foreach_tiled` 实验

**位置**: `IspcChunkStatementTranslator.TryTranslateChunkArrayForEach()` 中 `foreach` 的生成

当前生成 `foreach (index = 0 ... len)`。对于 AoS 布局，`foreach_tiled` 可能改善缓存行利用:
```ispc
foreach_tiled (index = 0 ... len, 4 * programCount) { ... }
```

这可控制 ISPC 的 gang 内迭代是 interleaved 还是 blocked。需要实测验证。

---

## 优化 6：多版本编译 + 运行时选择

**位置**: `NativeDll.vcxproj` / `CMakeLists.txt`

当前只编译一个 ISPC target (`avx512skx-i32x16`)。在没有 AVX-512 的 CPU (大多数消费级) 上，ISPC 回退到 SSE 或 AVX 窄向量，性能显著下降。

方案: 编译多个 ISPC target 并运行时检测 CPU 能力，挑选最佳版本。这是 Unity Burst 做的。

但这是较大工程，需要:
1. CMakeLists.txt 生成多个 `.obj` 版本
2. 运行时 CPU feature 检测
3. 函数指针分派

---

## 优先级评估

| 优先级 | 优化 | 难度 | Light收益 | Heavy收益 | 关键文件 |
|--------|------|:----:|:---------:|:---------:|---------|
| ⭐⭐⭐ | 移除 `--opt=disable-fma` | 极低~5min | — | 10-20% | `CMakeLists.txt` (生成器) |
| ⭐⭐⭐ | Tile 粒度调优 | 中~2h | **~60%** | 忽略 | `IspcStatementTranslator` / wrapper生成 |
| ⭐⭐ | `__assume` 对齐 | 低~30min | 2-5% | 2-5% | `IspcGenerator.cs` |
| ⭐ | Adapter header 解析简化 | 低~30min | 1-3% | 忽略 | `IspcGenerator.cs` |
| ⭐ | `foreach_tiled` 实验 | 低~15min | 不确定 | 不确定 | `IspcChunkStatementTranslator.cs` |

## 验证方法

每次改动后: `dotnet build src/EntJoySample -c Release` → 运行 benchmark → 对比 Light/Heavy/Sleep 三项指标，重点监控 `Verify : OK` 确认精度未退化。
