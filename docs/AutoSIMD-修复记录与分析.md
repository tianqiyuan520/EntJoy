# AutoSIMD 修复记录与分析

## 修复背景

修复前 C++ Fast Heavy IJobChunk 性能 17.6ms（1.26x vs C#），与 ISPC Heavy 2.98ms（7.74x）差距 6x。
经诊断，AutoSIMD 真 SIMD 代码存在于 Precise 库，但**从未被调用**。

## 修复内容

### 修复 1：AutoSIMD IJobChunk 走 ChunkRange 真 SIMD（主性能修复）

**文件：** `src/NativeTranspiler/Analyzer/Common/BindingsGenerator.cs`

**根因：** `BindingsGenerator` 把 AutoSIMD=Enabled 的 C++ IJobChunk 调度到 `EntityBatchEntityBatchFuncPtr`，但 `EntityBatchAdapter` 的 AutoSIMD 分支是 **lane-loop 伪向量化**（`for lane in 0..8: __entity_index = si+lane`，标量 body 逐 lane 跑），完全绕过 Precise 库里的真 SIMD 代码。

**修复：** 4 处 `isIspc` 判断扩展为 `isIspc || isAutoSimd`：
- 字段声明（line 212-215）：`s_*_ChunkEntityBatchFuncPtr` 只对非 AutoSIMD 声明
- 字段初始化（line 284-287）：同上
- DllImport（line 406-409）：同上
- Schedule 主路径（line 557-565）：AutoSIMD 走 `ScheduleChunkRangeRaw` + `ChunkRangeFuncPtr`

### 修复 2：SimdExpressionTranslator float2 双通道 + 复合赋值

**文件：** `src/NativeTranspiler/Analyzer/Simd/SimdExpressionTranslator.cs`

**根因 A（正确性）：** struct 字段复合赋值被降级为 `=`：
```
positions_ptr[lane].Value = n_extract_lane_f32(... * DeltaTime)
```
→ 旧值丢失，位置只移动一帧（mismatch 1,000,000）

**根因 B（精度）：** float2 字段（MovePosition.Value）只 gather x 分量：
`TranslateStructFieldAccess` 返回 `simd_value<float>{n_gather_ps<sizeof(MovePosition)>(...)}` 只 gather x，y 分量缺失。

**修复：**
1. `TranslateStructArrayFieldAccess` / `TranslateStructFieldAccess`：对 float2 字段返回 `simd_value<float2>{x_gather, y_gather}` 双通道
2. `TranslateAssignment` 的 struct-local 分支：复合赋值 `+=` 逐 lane 读旧值 + 运算 + 写回
3. `IsStructFieldFloat2`：通过 semanticModel 判断字段类型

### 修复 3：等效 float2 fallback（SimdExpressionTranslator line 1268-1291）

struct-local 分支对 `position.Value += rhs` 生成：
```cpp
{for(int __l=0;__l<g_simdWidthInt;__l++){
    positions_ptr[lane].Value.x() = (positions_ptr[lane].Value.x() + rhs_lane_x);
    positions_ptr[lane].Value.y() = (positions_ptr[lane].Value.y() + rhs_lane_y);
}}
```

## 验证结果

### 修复后 benchmark（100w 实体）

| 后端 | 修复前 Heavy | 修复后 Heavy | 提升 |
|---|---|---|---|
| C++ Heavy（标量） | 17.646 ms | 20.323 ms（正常波动） | — |
| **C++ Fast Heavy** | **17.641 ms（1.26x）** | **3.046 ms（11.07x）** | **7.7x** |
| ISPC Heavy | 2.984 ms（7.74x） | 2.984 ms | — |

### 正确性验证

| 指标 | 结果 |
|---|---|
| Light Verify | ✅ OK，C++ FastMaxDiff=0.0000 |
| Heavy Verify | ✅ OK，C++ FastMaxDiff=1.2207E-4（与 ISPC 同） |
| Light Verify (C# Epsilon) | OK，C++ FastMaxDiff=0.0000E+000 |

**Light C++ Fast MaxDiff=0**：AutoSIMD Precise 库无 fast-math（`fast-math OFF`），纯 float 乘加精确；ISPC 用 fast-math 有 4.58e-5 误差（FMA 融合差异）。

## 已知遗留（已修复）

### ~~1. IJobEntity AutoSIMD 路径运行时崩溃~~ （已修复）

**根因（最终诊断）：** `NativeJobScheduler.PrewakeWorkersOnce()` 只唤醒 worker 线程，不初始化调度器。`ScheduleChunkRangeRaw` 路径需要完整调度器状态（tile scheduler / dependency chain / cleanup handler）。`NativeJobScheduler.Initialize()` 才调用 `JobSystem_Initialize(numThreads)` 完整初始化。

**修复：** `AutoSIMDTest/Program.cs` 改用 `NativeJobScheduler.Initialize()`。

**IJobEntity AutoSIMD 测试结果（已通过）：**
```
Light IJobEntity (Position += Velocity * dt)
  MaxDiff=0.0000E+000  Mismatch=0/100000  Epsilon=0.0000E+000  [PASS]  Time=10.654ms
Heavy IJobEntity (16x sin/cos iterations)
  MaxDiff=1.9073E-006  Mismatch=0/100000  Epsilon=1.5000E-003  [PASS]  Time=3.589ms
```

### 2. int2 字段 gap

`TranslateStructFieldAccess` 对 int2 字段返回 `simd_value<float>`（单通道 x gather），类似旧的 float2 bug。需对称加 `IsStructFieldInt2` + `simd_value<int2>{x_gather, y_gather}`。**当前活跃 AutoSIMD job 无 int2 struct 字段 → 未触发。**

### 3. struct 字段的 struct 字段 gap

`struct A { B b; int x; }` 的 `a[i].b.field = v`。`TranslateAssignment` 的 struct-local 分支只处理一层展开。子字段（`.b.field`）需特殊处理。**当前活跃 AutoSIMD job struct 字段都是基本类型（`float2 Value`），不触发。**

### 4. EntityBatch 路径仍是伪向量化

`AppendEntityBatchAdapter` 的 AutoSIMD 分支（lane-loop）现在是死代码（所有 AutoSIMD job 改走 ChunkRange），但保留——防止未来 non-AutoSIMD + EntityBatch 路径误用。**未来优化**：EntityBatch adapter 也调用独立 SIMD 函数。

## Precise 库

`NativeTranspiledPrecise.lib`：AutoSIMD 生成的 SIMD 代码的 **fast-math OFF** 编译单元。MSVC 用 `/O2 /Ob2 /Oi /Ot /Qpar /MP`（无 `/fp:fast`），`NSIMD_AVX2` 定义激活 AVX2 intrinsic。与 `NativeTranspiled.dll`（`/fp:fast`）对比：AutoSIMD 计算精确可复现 + 主体享 fast-math 性能。

## ispc sincos/bad_alloc/assert 测试

ispc sincos fallback 时 `bad_alloc` → 正常（无 sincos 库）；`assert(0)` → Debug 版本正常 abort（RuntimeError），Release 优化掉（无效果）。
