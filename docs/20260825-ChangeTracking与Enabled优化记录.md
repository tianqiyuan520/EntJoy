# 20260825：Change Tracking 与 EnabledComponent 优化记录

> **范围**：本会话对 EntJoy.ECS 的变更记录——Change Tracking 实现、EnabledComponent 过滤优化、
> Query API 整合、IJobChunk.Run 架构拆分、NativeTranspile 执行路径（ImmediateNative）与
> SourceGenerator（IJobEntity/IJobChunk）统一。含性能基准、决策与注意事项。
> **关联文档**：`Phase优先级分析与实施路线.md`、`20260822-设计决策记录-AI聊天讨论沉淀.md`

---

## 一、Change Tracking（Phase 4 遗留项）

### 1.1 实现

| 层 | 机制 |
|----|------|
| `Chunk` | `_version`（组件数据修改次数）、变更位掩码（每实体 1 bit，位于 Chunk 内存块尾部） |
| `Archetype` | `_globalVersion`（全局变更计数）；`Set<T>/SetRaw` 写入后统一走 `NotifyComponentChanged` |
| `ChunkMetadata` | 变更位掩码布局（`ChangedBitMaskOffset/Size`，每实体 1 bit；`enableChangeTracking=true` 时分配） |
| `QueryBuilder` | `WithChanged<T>()` / `ChangedSince(version)`（接口已定义；查询侧解析待实现） |

### 1.2 设计决策

- 采用 **Chunk 版本号 + 实体位掩码** 双层（对齐 Unity DOTS）：Chunk 级快速过滤 + 实体级精确判断。
- 实体位掩码是**过度设计**（见「注意事项」5.1），当前主要价值在接口与基础设施就绪。

---

## 二、EnabledComponent 过滤优化

### 2.1 优化链路

```
原始：逐实体 GetComponentEnabled() 判断          → 基准 1.0x
一级：Chunk 级分水岭 + 预计算组合位图             → ~12x
二级：SIMD(AVX2) 批量 AND + 提前退出              → ~15x
三级：ZeroSkip + SIMD（最终形态）                 → ~17-19x
```

### 2.2 关键实现

- **组合位图缓存**（`Archetype.GetOrComputeCombinedMask`）：
  - key = AllEnabled 类型组合哈希；每 chunk 缓存 (enableVersion, pinned 位图)
  - **pinned 托管数组**（`GCHandle.Pinned`）保证指针稳定；`chunk._enableVersion` 在实体增删/启用状态变化时递增，缓存据此失效（组件值 Set 不失效——位图与组件值无关）
  - Dispose 释放所有 pinned handle
- **多组件 SIMD**：`Avx2.And` 批量 256 位 + `Avx.TestZ` 提前退出；非 AVX2 回退逐元素
- **单组件零拷贝**：`ChunkExecution.ExecuteOnQuery` / `Run` 路径直接传原始 enableable 位图指针
- 并行调度路径（worker 线程）**不用共享缓存**（非线程安全），由 `ExecuteManagedChunk` 每次独立计算

### 2.3 性能要点（公平对比，同工作量=读 Position.X 累加）

| 方案 | 100K 全部 | 33K enabled 过滤 |
|------|----------|------------------|
| `Query` foreach | 0.152 ms | 0.155 ms |
| `IJobChunk.Run`（无 `[AggressiveOptimization]`） | 0.362 ms | 0.502 ms（TryGetNextRange） |
| `IJobChunk.Run`（**带 `[AggressiveOptimization]`**） | **0.081 ms** | **0.138 ms** |
| `IJobEntity.Run`（生成 adapter，自动标注） | **0.075 ms** | 0.105 ms |

**核心结论**：
1. `TryGetNextRange` 是稀疏数据（33% 启用）下的瓶颈——每个 enabled 实体几乎是孤立 range，33K 次**非内联调用**（每 ~11ns）。
2. **`IJobChunk` 慢的根因不是框架，而是用户 Job 的 `Execute` 缺少 `[MethodImpl(AggressiveInlining | AggressiveOptimization)]`**——JIT 未达最高优化（Execute 未内联进遍历、Span 边界检查未消除）。标注后与生成 adapter 追平。
3. `CountAllJob`（只累加 chunk.Count 不碰实体）的 0.01ms **不能**代表"遍历 100K 实体"的成本——benchmark 方法论必须统一实体级工作量。

---

## 三、Query API 整合

### 3.1 演进

```
旧：world.QueryWithEnabled<T0, T1>()            ← 独立 API（已删除）
新：world.Query<T0>().WithEnabled<T1>()         ← 链式
    ↓
QuerySelection<T0> ──WithEnabled<T1>()──▶ QueryEnumerable<T0, T1>
```

- 删除重复类型 `EnabledQueryEnumerable` / `EnabledQueryEnumerator` / `EntityQueryResult<T0>`
- **enable 过滤并入 `QueryEnumerator<T0, T1>`**（唯一枚举器）：
  - slot-0 漏查修复（跨 chunk 时新 chunk slot 0 须过位图检查，此前多返回 130 实体 → **33464→33334**）
  - SIMD 组合位图 + `BitOperations` 内联跳转

---

## 四、Run 架构拆分（ECS vs NativeScheduler）

### 4.1 拆分结构

| 路径 | 位置 | 职责 |
|------|------|------|
| **普通 IJobChunk/IJobEntity.Run** | `ChunkExecution.cs`（新增，ECS 侧） | 主线程直接遍历 Chunk 调 Execute，**不经过 NativeJobCore** |
| **`[NativeTranspile]` Job.Run** | `NativeExports.RunImmediate_*` → C++ `ImmediateNative` | 主线程直执**翻译后的 C++ Job**，零 worker 唤醒 |
| **Schedule（并行）** | `NativeEcsScheduler.cs` | 真正 native 调度（P/Invoke + 缓存 + 租赁） |

- `ChunkExecution.ExecuteOnQuery`：单组件零拷贝 / 多组件走 Archetype 位图缓存 / 无过滤直执行
- `Run` 前 `CompleteActiveJobs()` 防与并行 Job 数据竞争
- **`Run` 保持 `void` 契约**（与 Unity 一致）：Job 输出经共享内存（指针/NativeArray），不靠值类型字段回读

### 4.2 ImmediateNative（C++ 侧真正的同步执行）

**问题**：`NativeExports.Schedule_*.Complete()` 走 worker 调度——即使立即 Complete 也有 worker 唤醒/上下文切换开销；且 C++ `ScheduleChunkBatchCore` 的 `ChunkScheduleMode` 参数此前**被解析但未生效**。

**修复**（`src/NativeDll/JobSystem_Scheduler.cpp`）：
```cpp
// ImmediateNative：主线程同步执行，零 worker 唤醒（依赖已完成时）
if (depOk && (mode == ChunkScheduleMode::ImmediateNative || (rc <= 1 && workerCap <= 1)))
{ RunSyncJob(...); }
```
**注意**：NativeDll 需 **ClangCL** 工具集编译（MSVC + SDLCheck 会把既有 `getenv` C4996 当错误）；产物拷贝到仓库根 `bin/NativeDll.dll`。

### 4.3 Native 冒烟验证

```
Schedule path  : sumX=1000   ✅ 并行路径
Run path       : sumX=2000   ✅ Run 直执翻译后 C++（+1000）
Run 100x       : 0.28 ms     ✅ 零 worker 唤醒（1000 实体）
```

---

## 五、SourceGenerator 统一（IJobEntity / IJobChunk）

### 5.1 改动

- **BindingsGenerator**：不再为 `[NativeTranspile] IJobChunk` 生成扩展方法（只留 IJob / ParallelFor / For）；`RunImmediate_*` 按 entity/ISPC 选 funcPtr
- **ECS.SourceGenerator（`IJobEntitySourceGenerator`）**：
  - `[NativeTranspile] IJobChunk` 与 IJobEntity 一致：在 **job 所在命名空间** 生成 `Schedule` / `ScheduleWithWorkerCap` / `Run`
  - **Run → `NativeExports.RunImmediate_*`**（直执，不再 Schedule+Complete）
  - **适配器 Execute 生成 BitOperations 内联循环**（修复此前**忽略 enabledMask 的 bug** + 与 Query 同路径性能）：
    ```
    if (enabledMask.Length == 0) → 全遍历
    else → ulong* bits + TrailingZeroCount 内联跳转
    ```
  - 适配器 struct 标 `unsafe`；自动带 `[MethodImpl(AggressiveInlining | AggressiveOptimization)]`

### 5.2 可见性

- IJobChunk/IJobEntity 扩展方法与用户代码同命名空间 → **不再依赖 `using NativeTranspiler.Bindings`** 才命中原生路径
- 非 `[NativeTranspile]` IJobChunk 不生成扩展（`ChunkJobExtensions` 直接可用）

### 5.3 Native 路径的 EnableComponent 过滤现状（⚠️ 未实现）

| 路径 | 执行者 | WithEnabled |
|------|--------|:---:|
| 托管 IJobEntity | C# 生成 adapter（BitOps 内联循环） | ✅ |
| 托管 IJobChunk | `ChunkExecution`/`ExecuteManagedChunk`（C#） | ✅ |
| **Native IJobEntity（Cpp/ISPC）** | **C++ Range adapter（lite ChunkData）** | ❌ 静默忽略 |
| **Native IJobChunk（ISPC）** | **C++ Range adapter** | ❌ 静默忽略 |
| **Native IJobChunk（Cpp）** | **C++ EntityBatch adapter** | ❌ 显式抛 `NotSupportedException` |

**现状说明**：
- C++ 生成侧（`CppJobGenerator`）仅有字段**预留**（`__chunkDataLite.enableBitMaps = nullptr; // 预留 IEnableComponent`、`allEnabledCount` header 字段），**无任何过滤逻辑**
- `NativeEcsScheduler.ScheduleNativeEntityBatchRawCore` 对 AllEnabled 直接 `throw NotSupportedException`（Cpp IJobChunk 走此路径）
- Range 路径（entity/ISPC chunk）**静默忽略**：header 携带过滤信息但 C++ adapter 不消费

**"Range 路径"说明**：JobSystem 的 range 调度（`ScheduleChunkRanges`，回调 `(context, chunks, startIndex, count)`，worker 处理一段多 chunk）。两条实现：
- **托管**：`CreateChunkRangeCallback`（C#）→ `ExecuteRawChunk` → `ResolveCombinedMask`，**已支持过滤**（托管 IJobChunk 用）
- **Native**：`s_*_ChunkRangeFuncPtr`（C++ 适配器）→ 直接调 C++ Execute，**无过滤**

**补支持路线（推荐，未实施）**：C++ adapter 读取 header 的 `allEnabledCount`+类型哈希+位图，在 C++ 循环内跳过 disabled 实体（~30 行）；或先 C# 过滤再调 C++（破坏 native 直跑，不推荐）。

---

## 六、NativeEcsScheduler 去重（代码质量）

- 删除 6 个死代码方法（`RunChunkRawImmediate`/`RunEntityRawImmediate`/`ScheduleChunkRaw`/`ScheduleEntityBatchRawWithWorkerCapAndRangeSize`/`ScheduleAndCompleteEntityBatchRaw`/`ScheduleNativeChunkRawImmediateCore`，全仓零调用）
- 三处 fallback ChunkJobData 填充 → `CollectMatchingChunks` + `FillChunkJobDataList`
- ChunkRef 解包 → `ResolveChunk`；位图合并 → `ResolveCombinedMask`；job 指针解析 → `ResolveJobFromContext`
- 史山变量 `IntPtr h1268/h1285/...` → `handle`
- 文件 2040 → 1643 行（-19%）；清理历史叙事注释（"Phase X.X" / "Change Tracking:" 标签）

---

## 七、注意事项 / 遗留项

### 7.1 已知问题（未修，谨慎）

| 项 | 影响 |
|----|------|
| **Change Tracking 实体位掩码在 swap-pop 时不随实体移动**（`RemoveEntity` 只迁移 enableable 位） | `IsEntityChanged` 指向错误实体 |
| `ChunkMetadata` 默认 `enableChangeTracking=true`，所有 chunk 多分配位掩码内存 | 未启用追踪也占用 `EntityCapacity/8` 字节/chunk |
| `Set<T>/SetRaw` 热路径 3 次额外写（Interlocked + 位掩码） | 所有 Set 调用附加开销 |
| `Query` 双组件枚举器逐实体 `MoveNext/Current` 未完全内联 | 无过滤 0.15ms vs adapter 0.076ms（2 倍） |
| `TryGetNextRange` 无 `[AggressiveInlining]` | 稀疏过滤遍历 33K 次调用 ~0.36ms 开销 |

### 7.2 使用约定

- **手写 IJobChunk 的 `Execute` 务必标注**：
  ```csharp
  [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
  public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask) { ... }
  ```
- **Job 输出经共享内存**（指针/NativeArray 字段），不读值类型字段回传
- **NativeDll 用 ClangCL 编译**（MSVC SDLCheck 报既有 C4996）
- 组合位图缓存仅主线程路径使用（Run / 查询）；并行调度每次独立计算

### 7.3 性能锚点（100K 实体、33% enabled、公平读 X）

```
IJobEntity.Run (adapter)  OFF 0.076ms / ON 0.105ms   ← 参考基准
IJobChunk.Run (标注后)     ALL 0.081ms / RANGE 0.138ms
Query                     ALL 0.152ms / EN 0.155ms
```

---

## 八、涉及文件清单

**src/EntJoy.ECS**
- `Chunk/Chunk.cs`、`Archetype/ChunkMetadata.cs`、`Archetype/Archetype.cs`
- `Query/QueryBuilder.cs`、`Query/QueryEnumerable.cs`、`Query/QuerySelection.cs`（新增）
- `JobSystem/ChunkExecution.cs`（新增）、`JobSystem/NativeEcsScheduler.cs`
- `Chunk/ChunkJobExtensions.cs`、`Entity/EntityManager.cs`（CompleteActiveJobs 内部）

**src/EntJoy.ECS.SourceGenerator**
- `IJobEntitySourceGenerator.cs`、`Config.cs`

**src/NativeTranspiler**
- `Analyzer/Common/BindingsGenerator.cs`

**src/NativeDll（需 ClangCL 重编译 + 拷贝到 bin/）**
- `JobSystem_Scheduler.cpp`（ImmediateNative）

**samples/EntJoySample/09_ECS**
- `EnabledComparisonBenchmark.cs`、`IJobEntityEnabledBenchmark.cs`、`NativeJobSmokeTest.cs`（新增/重写）
- `Components.cs`（ActiveComponent）、`Program.cs`