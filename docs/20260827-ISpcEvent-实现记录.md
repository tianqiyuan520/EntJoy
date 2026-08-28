# 20260827：ISPC SendEvent 支持 — 实现过程与问题记录

> **状态**：实现中（卡在 ISPC 事件写入崩溃）
> **范围**：NativeTranspiler ISPC 后端支持 `EventBus.SendEvent`（对标 C++ 后端已实现的事件通道）

---

## 一、目标与现状

C++ 后端已支持 SendEvent（见 `20260827-EventChannel实现记录.md`）：
- `CppChunkStatementTranslator.TryTranslateSendEvent` 拦截调用
- 生成 `__EntJoyEventBuffer` 写入代码（InterlockedAdd 原子槽分配）
- ChunkContextHeader 含 event 字段，C# 运行时后端无关（分配/drain/cleanup 共用）

**ISPC 后端缺口**（本次实现目标）：
1. ISPC chunk wrapper 的 `__EntJoyChunkContextHeader` 缺 event 字段（布局 mismatch，88 vs 64 字节）
2. ISPC `_impl` 函数缺 event buffer 参数传递
3. `IspcChunkStatementTranslator` 无 SendEvent 拦截
4. 事件类型 ISPC 头文件 include 缺失
5. Common 头（EntJoyCommon.ispc）缺 `__EntJoyEventBuffer` 定义

## 二、已完成改动

| 文件 | 改动 |
|------|------|
| `IspcGenerator.cs` | wrapper header 补 3 个 event 字段 + `__EntJoyEventBuffer` POD；wrapper 传 event 参数；`_impl`/`_mt_impl` 签名加 `void* uniform __eventBufferHeaders, uniform int __eventBufferCount`；includes 加事件类型 |
| `IspcStatementTranslator.cs` | SendEvent 拦截（`TryTranslateSendEvent` + `GenerateSendEventIspc`），EventTypes 收集，`SetEventBufferParamName` |
| `CodeTemplates.cs` | `GenerateCommonIspcHeader` 加 `__EntJoyEventBuffer`（⚠ 实际未被使用，见下） |
| `NativeTranspilerGenerator.cs` | **真正的** `GenerateCommonIspcHeader`（L691 有独立副本）加 `__EntJoyEventBuffer` |
| `ChunkJobScheduler.cs` | `ScheduleNativeChunkRangeRawCore` 补 EventBuffer 分配（此前 ISPC chunk job 走此路径但无分配） |
| `ISpcEventJobTest.cs` | 新测试（单类型/多类型/多World/字段值） |

## 三、踩过的坑（已解决）

### 3.1 Common 头有两个副本
`CodeTemplates.GenerateCommonIspcHeader()` 和 `NativeTranspilerGenerator.GenerateCommonIspcHeader()` 重复。
**生成器实际用 NativeTranspilerGenerator 的副本** —— 改错文件导致"改了没用"。
⚠ 应合并去重。

### 3.2 ISPC 类型/语法约束（实测验证）
- `uniform void*` 非法（void 不能带 uniform 限定）→ 参数用 `void* uniform`（指针 uniform）
- `uniform int*` 合法；`varying→uniform` 指针 cast 禁止
- `atomic_add_global` 候选：
  - `varying int32(uniform int32* uniform ptr, varying int32 val)`
  - `uniform int32(uniform int32* uniform ptr, uniform int32 val)`
  - `varying int32(uniform int32* varying ptr, varying int32 val)`
- C 复合字面量 `= {a, b}` 不支持 → 逐字段赋值
- **uniform 局部变量不能声明在 divergent（varying 条件）块内** → SendEvent 代码必须内联表达式

### 3.3 varying struct 的 SoA 布局（关键）
ISPC 中 `sizeof(varying struct)` = lanes × 元素大小（AVX2 8-wide：`DeathSignal` 96 字节而非 12）。
**varying struct 指针的数组索引是 SoA 布局** → 直接 `((DeathSignal*)data)[idx] = tmp` 会越界写坏内存（堆损坏 0xC0000374）。

**已改为**：`((uniform int*)data)[idx * stride + offset] = val`（uniform int* + 显式偏移，AoS 布局与 C# 一致）。
实测确认 C# `DeathSignal` = 12 字节（`Unsafe.SizeOf=12`），ISPC `uniform DeathSignal` = 12 字节。

## 四、卡点与根因（最终修正）

### 4.1 现象
- 事件写入**禁用**（只保留 atomic）→ 测试全 PASS（计数 5/3/4/3 正确）→ 分配/drain/atomic 全链路 OK
- 事件写入**启用** → `Schedule(query).Complete()` 堆损坏（0xC0000374）
- wrapper 诊断：header 有效（data/count/cap/esz 全对：data=0x...5490, count=0x...9CB0, cap=1024, esz=12/16）
- 独立 C++ 模拟（malloc + 同样 header 数组 + atomic + scatter 写）**完全正常**（count=7, 数据正确）

### 4.2 根因：atomic_add_global 是 fetch-add 语义，我错误地减了 1（不是原子不可靠！）
用户质疑"原子返回值不可靠不应该"，重新做干净测试（E:\Code\ispc_atomic_test）验证：

| 测试 | 结果 |
|------|------|
| `atomic_add_global(counter, 1) - 1` 做索引 | ❌ 错位：idx=-1（**越界写 → 堆损坏**），后续全错位 1 |
| **`atomic_add_global(counter, 1)` 直接做索引** | ✅ **全 PASS**（count=7，索引 0..6，AoS 数据逐项正确） |
| **SIMD `foreach` + 原子 + AoS 写**（无 -1） | ✅ **全 PASS**（保留并行！） |

**结论**：
- **ISPC 的 `atomic_add_global` 是 fetch-add，返回旧值**（= 槽位索引），**不需要减 1**
- **C++ 宏 `INTERLOCKED_ADD_AND_FETCH32(ptr, val)` 是 add-fetch（返回新值），减 1 得旧值**（`... - 1` 在 C++ 正确）
- 我之前**照搬 C++ 的 `... - 1` 模式**到 ISPC，导致 idx=-1 → 越界写 → 堆损坏
- **根因是语义翻译错误，不是原子不可靠**
- 之前误判"varying 原子返回对部分 lane 未定义"是 **idx=-1 越界写掩盖了正确数据**（ret[0]=-1 与初始化值 -1 混淆）

### 4.2b 原子操作语义三方对照（C# / C++ / ISPC）
| C# `Interlocked` | 返回 | C++ 宏（CodeTemplates） | 返回 | ISPC | 返回 | 匹配 |
|---|---|---|---|---|---|---|
| `Increment` | **新值** | `INTERLOCKED_INCREMENT_AND_FETCH32` = `_InterlockedIncrement` | **新值** | `atomic_add_global` | **旧值** | C++✅ / ISPC❌差1 |
| `Decrement` | **新值** | `INTERLOCKED_DECREMENT_AND_FETCH32` = `_InterlockedDecrement` | **新值** | `atomic_subtract_global` | **旧值** | C++✅ / ISPC❌差1 |
| `Add` | **新值** | `INTERLOCKED_ADD_AND_FETCH32` = `_InterlockedExchangeAdd + val` | **新值** | `atomic_add_global` | **旧值** | C++✅ / ISPC❌差1 |
| `Exchange` | **旧值** | `INTERLOCKED_EXCHANGE32` = `_InterlockedExchange` | **旧值** | （未实现） | — | C++✅ |
| `CompareExchange` | **旧值** | `INTERLOCKED_COMPARE_EXCHANGE32` = `_InterlockedCompareExchange` | **旧值** | （未实现） | — | C++✅ |

（GCC 侧 `__sync_add_and_fetch`=新值✅ / `__sync_lock_test_and_set`=旧值✅ / `__sync_val_compare_and_swap`=旧值✅，与 C++ 一致）

**结论**：
- **C#→C++ 原子：语义完全正确**（C++ 宏逐条对齐 C# Interlocked 返回值）
- **C#→ISPC 原子：返回值语义不匹配**（`atomic_add_global`/`atomic_subtract_global` 是 fetch-add/sub 返回旧值，C# Add/Increment/Decrement 返回新值）——**若用户代码使用返回值会差 1**
- **现有生成器规避**：`GenerateIspcFunction` L1297-1300 `useUniformLoop = usesReturnValue` 检测到返回值被使用就退化 uniform for + `if (programIndex != 0) return;`（防 8 lane 重复执行）——**但返回值仍是旧值，语义差 1 未修正**（潜在 bug，未暴露因为现有测试原子返回值多用于计数器盲递增）
- **SendEvent 场景不受影响**：`SendEvent` 返回 void，槽位分配就是要旧值（fetch-add 恰好正确），无返回值语义问题
- **教训**：`for (uniform int)` 在 ISPC 中所有 GANG lane 都会执行循环体，必须 `if (programIndex != 0) return;`（现有生成器 L1331 已知此点；我此前 uniform-for 测试漏了这行导致"错位"误判）

### 4.3 最终方案：SIMD foreach + fetch-add 原子（保留并行）
```ispc
foreach_tiled (i = 0 ... __entity_count) {
    if (healths_ptr[i].Current <= 0) {
        varying int idx = atomic_add_global(buf->count, (varying int)1);  // fetch-add：直接返回旧值=槽位
        ((uniform int*)buf->data)[idx * stride + off] = ...;              // AoS 逐字段 scatter 写
    }
}
```
- **保留 `foreach_tiled` SIMD 并行**（不需要 uniform-for 退化）
- count 由原子维护（每 active lane 独立 +1），无需回写
- 每个 lane 拿唯一槽位，AoS 布局与 C# 一致

### 4.4 AutoSIMD 与 SendEvent 的支持矩阵（已查证）
| 后端路径 | SendEvent 支持 |
|---------|---------------|
| C++ 标准（GenerateChunkFunctionStandard） | ✅ CppChunkStatementTranslator 拦截 |
| C++ AutoSIMD=Vectorize（GenerateChunkFunctionVectorize） | ✅ 用 CppChunkStatementTranslator + clang pragma 自动向量化 |
| C++ AutoSIMD=Enabled（GenerateChunkFunctionSIMD） | ❌ 走 SimdControlFlowGenerator，**fallback 条件不含 SendEvent**，SendEvent 不会正确翻译（未支持组合） |
| ISPC | ✅ **SIMD + fetch-add 原子**（本次实现） |

### 4.5 已完成的 ISPC 改动（SIMD 原子方案，已实现并验证）
- `IspcChunkStatementTranslator.TryTranslateChunkArrayForEach`：保持 `foreach_tiled`（SIMD），不退化
- `GenerateSendEventIspc`：`varying int idx = atomic_add_global(buf->count, (varying int)1)`（**无 -1**）+ AoS 逐字段写（uniform int* + 显式偏移）
- `IspcGenerator`：wrapper header 补 event 字段（88 字节对齐 C#）、`__EntJoyEventBuffer` POD（`uniform int* data/count`）、event 参数传递、事件类型 include
- `NativeTranspilerGenerator.GenerateCommonIspcHeader`：加 `__EntJoyEventBuffer` 定义
- `ChunkJobScheduler.ScheduleNativeChunkRangeRawCore`：补 EventBuffer 分配 + world 绑定（ISPC chunk job 走此路径）
- **测试 ISpcEventJobTest 4/4 PASS**（单类型 5/多类型 3+4/多World 5+3/字段值 Amount={5,10,20}），C++ 回归 4/4 PASS，Event Channel 5/5 + 1/1 PASS

### 4.5b 后续修复（用户要求三项）
1. **C#→ISPC 原子返回值修复**（`IspcStatementTranslator.TranslateInterlockedCall`）：
   - ISPC `atomic_add_global`/`atomic_subtract_global` 是 fetch-add/sub（返回旧值），C# `Interlocked.Add/Increment/Decrement` 返回新值
   - 修复：翻译后补回增量 → `(atomic_add_global(ptr, val) + val)` / `(atomic_add_global(ptr, 1) + 1)` / `(atomic_subtract_global(ptr, 1) - 1)`，返回值语义与 C# 一致（实测 out=1,2,3,4）
2. **AutoSIMD=Enabled + SendEvent fallback**（`CppJobGenerator.GenerateChunkFunctionSIMD` + `GenerateJobAdapter`）：
   - fallback 条件加 `usesSendEvent`（SendEvent 无法在 SimdControlFlowGenerator 翻译 → 用 CppChunkStatementTranslator 标量）
   - fallback 补 `}` 闭合函数体（此前缺）
   - RangeAdapter 调用 Execute 时补传 `__header` 参数
   - 新增测试 Test 5：AutoSIMD=Enabled job + SendEvent → 5/5 PASS
3. **测试全量 PASS**：ISPC 5/5（含 AutoSIMD Test 5）+ C++ 4/4 + Event Channel 5/5+1/1

### 4.6 测试经验
- **Target.Id 恒为 0**：`NewEntity` 创建的 Entity 组件 Id 默认 0（C++ 后端同样）——不是 ISPC 问题；字段值验证应聚焦非 Entity 字段（如 Amount）
- 干净测试目录（E:\Code\ispc_atomic_test）避免旧 obj/头文件污染，是定位根因关键
- **教训**：跨后端翻译原子操作必须先确认返回值语义（fetch-add vs add-fetch），不能机械照搬 C++ 宏模式

## 五、结论

### 5.1 ISPC SendEvent 最终方案
**SIMD `foreach_tiled` + fetch-add 原子**：
```ispc
varying int idx = atomic_add_global(buf->count, (varying int)1);  // 返回旧值=槽位，无 -1
((uniform int*)buf->data)[idx * stride + off] = ...;
```
**保留 SIMD 并行**（无需退化标量），count 由原子维护，AoS 布局与 C# 一致。

### 5.2 关键认知修正
- **ISPC 原子返回值完全可靠**（LLVM 标准实现）
- 之前误判"原子不可靠"的根因：**我照搬 C++ 宏 `INTERLOCKED_ADD_AND_FETCH32 - 1`（add-fetch 减 1），但 ISPC `atomic_add_global` 是 fetch-add 直接返回旧值，多减 1 → idx=-1 越界写 → 堆损坏**
- 用户质疑"不应该呀"促使重新验证，确认是翻译语义错误而非编译器问题

### 5.3 AutoSIMD 与 SendEvent 支持矩阵（最终）
| 后端路径 | SendEvent |
|---------|----------|
| C++ 标准 | ✅ 完整 |
| C++ AutoSIMD=Vectorize | ✅（标量代码 + clang 自动向量化 pragma） |
| C++ AutoSIMD=Enabled（SimdControlFlowGenerator） | ❌ 未支持组合（fallback 不含 SendEvent，需补） |
| ISPC | ✅（SIMD + fetch-add 原子，本次实现） |

### 5.4 遗留
- AutoSIMD=Enabled + SendEvent 组合未支持（可后续在 `GenerateChunkFunctionSIMD` fallback 条件加 `JobUsesSendEvent` 检测）
- ISPC MT（UseISPC_MT）路径的 SendEvent 未经测试（`GenerateIspcChunkMTSource` 已加 event 参数，但测试未覆盖）
- 事件类型含非 4 字节字段（如 double/自定义大 struct）的 AoS 偏移未验证（当前按 4 字节 int 步长 + Entity 8 字节特判）

## 六、测试判定标准（ISpcEventJobTest）
1. 单事件类型：5 死 → DeathSignal 5 条
2. 多事件类型：3 死 + 4 伤 → 3/4 分流
3. 多 World：world1=5, world2=3 隔离
4. 字段值：Target.Id/Amount 与预期一致（验证 AoS 布局）
5. 回归：C++ NativeEventJobTest 4 项必须仍 PASS
