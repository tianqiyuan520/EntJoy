# EntJoy.ECS IJobChunk 设计说明（2026-08-26）

> 本文档说明重构后的 IJobChunk 架构：三层职责、三种执行路径、缓存机制。
> 前置文档：`20260826-重构ECS-JobSystem方案.md`（方案）、`20260826-JobSystem重构-性能基线.md`（性能）

---

## 一、当前架构（三层）

```
用户 job.Schedule(query)
  │
  ├─ 托管调用层  ChunkJobScheduler（编排）
  │     ├─ 缓存命中 → 复用 ChunkJobData* 表
  │     ├─ 缓存未命中 → CollectMatchingChunks + FillChunkJobDataList（full）/ FillManagedChunkJobDataList（light）
  │     ├─ CreateChunkContextBlock（构造 context block）
  │     └─ 提交到 NativeChunkJobs
  │
  ├─ 托管回调层  ChunkJobCallbacks（反向 P/Invoke）
  │     └─ CreateChunkRangeCallback<T>：job 解析 + chunkTable[chunkId] + ComputeChunkMask + Execute
  │
  └─ Native 调用层  NativeChunkJobs（ABI/P-Invoke）
        ├─ 函数指针加载（幂等）
        ├─ 5 个提交入口（ScheduleChunkJobEx / ScheduleChunkRangeJobEx / EntityBatch*）
        ├─ ChunkCleanup（释放 chunk 表 + context 池）
        └─ ChunkArrayTable（托管 Chunk[] 引用，替代 GCHandle）
```

## 二、三种执行路径

| 路径 | 入口 | C++ 行为 | 回调层 |
|------|------|----------|--------|
| **托管 Schedule** | `ScheduleChunkCore` → cache hit → `ScheduleChunkRangeJobEx` | 实体数衡 tile + Chase-Lev + worker 反调 `rangeFunc(ctx, start, count)` | `CreateChunkRangeCallback<T>` — 从 `ChunkArrayTable` 取 Chunk[]，chunkId 索引，`ComputeChunkMask` |
| **托管 Run** | `ChunkJobExtensions.Run` → `ChunkExecution.ExecuteOnQuery` | 无 C++（主线程直跑） | 无回调（直接遍历 arch/chunk，调 `job.Execute`） |
| **Native Transpile** | `ScheduleChunkEntityBatchRawWithWorkerCapAndRangeSize` → `ScheduleNativeEntityBatchRawCore` | 实体数衡 tile + Chase-Lev + worker 执行原生 adapter（C++ wrapper 从 context 读 job 字段 + EntityBatchData） | 无托管回调（C++ 直接执行编译后的 Execute） |

### 管理路径的数据流

```
Schedule:
  1. 缓存命中：ChunkJobData*[] 已有 entityCount+componentCount+enableBitMaps+chunkHandle
  2. 构造 context block：[ChunkContextHeader][typeHashes][requiredIds][job blob]
     header: chunkArrayHandle → ChunkArrayTable[block] = Chunk[]（通过 ChunkArrayTable 管理引用，零 GCHandle）
  3. ScheduleChunkRangeJobEx(funcPtr=rangeCallback, context, cleanup, chunks, count, dep, mode)
  4. C++：entity-balanced tile → Chase-Lev → worker 调用 rangeCallback

Worker 调用回调：
  5. ResolveJob<T>(ctx, header) → job（blob 或 GCHandle box）
  6. ChunkArrayTable.TryGetValue(ctx) → Chunk[]
  7. chunkTable[cd->chunkHandle.ToInt32()] → Chunk 对象（O(1) 索引）
  8. ResolveEnabledTypes + ComputeChunkMask → ChunkEnabledMask（单组件零拷贝/多组件 AND）
  9. job.Execute(new ArchetypeChunk(chunk), mask)

Cleanup（ChunkCleanup）：
  10. 移除 ChunkArrayTable[ctx]
  11. 释放 enableBitMaps（每个 chunk 的 HGlobal）
  12. 释放 chunksPtr（ChunkJobData[] HGlobal）
  13. ChunkContextLeases 释放 cache lease
  14. 归还 context block 到 ContextPool
```

### Native Transpile 路径的数据流

```
Schedule:
  1. 缓存命中：ChunkJobData*[] 有完整组件指针（compArrays/sizes/enableBitMaps/typeIndices/requiredArrays）
  2. context block + ScheduleEntityBatchJobEx(funcPtr=c++adapter, context, batches, count, dep, mode)
  3. C++：entity-balanced tile → Chase-Lev → worker 调用 c++adapter

C++ adapter 直接：
  4. job = *reinterpret_cast<T*>(context + headerSize + types + required)  // 偏移精确对齐 __EntJoyChunkContextHeader
  5. for each batch: positions = batch.componentArrays[0]; positions[i] += DeltaTime;  // 零托管开销

Cleanup：
  6. C++ 释放 EntityBatchData[] + componentArrays blocks + context block
```

## 三、缓存机制

### 为什么需要缓存

`ChunkJobData*` 表的构建需要：
- 遍历 archetype → chunks → 填充实体数、组件数、位图指针、chunkId
- 托管路径还要 GCHandle 或 Chunk[] 引用
- 对于 transpiler 路径，还要填充完整组件指针表（compArrays/sizes/enableBitMaps/typeIndices/requiredArrays）

这些工作在每次 Schedule 时做一次 = O(chunks) × 组件数 × 分配。缓存将它摊销到结构性变更时才重建。

### 三层缓存结构

| 缓存 | 键 | 载荷 | 失效 | 谁构建 |
|------|-----|------|------|--------|
| `RawChunkScheduleCache`（raw 模式 0） | (em, queryHash, requiredHash, 0) | 全指针表 `ChunkJobData*` | structuralVersion 变 | `BuildRawChunkScheduleCache` |
| `RawChunkScheduleCache`（managed 模式 1） | (em, queryHash, 0, 1) | 轻量表：entityCount+componentCount+enableBitMaps+chunkHandle(=chunkId)；另存 `ManagedChunkArray`（Chunk[] 直接引用，无 GCHandle） | structuralVersion 变 | `BuildManagedChunkScheduleCache` |
| `EntityBatchScheduleCache` | (em, queryHash, requiredHash, 3) | EntityBatchData* + 组件 blocks | structuralVersion 变 | `BuildEntityBatchScheduleCache` |

### ChunkArrayTable（替代 GCHandle）

```
ConcurrentDictionary<IntPtr, Chunk[]> ChunkArrayTable;
// key = context block 指针（每次 Schedule 唯一）
// value = Chunk[]（托管路径的 chunk 列表）
// 无 GCHandle，无泄漏；ChunkCleanup 调 TryRemove 释放
```

旧设计：每 chunk 一个 `GCHandle.Alloc(ChunkRef{...})` → 1000 chunk × 1000 次调度 = 100 万次 GCHandle 分配。
新设计：`RawChunkScheduleCache.ManagedChunkArray`（strong reference）+ `ChunkArrayTable`（ConcurrentDictionary）→ 0 GCHandle 分配。

### `BuildManagedChunkScheduleCache`（lightweight fill）

只为每 chunk 填 4 个字段：
```csharp
entityCount = chunk.EntityCount,
componentCount = compCount,
enableBitMaps = Marshal.AllocHGlobal(compCount * sizeof(void*)), // 仅 enabled 查询需要
chunkHandle = (IntPtr)ci  // chunkId，不是 GCHandle
```
组件数组指针/大小/类型索引/required 全为 null——组件数据访问永远走 `Chunk` 对象（不跨边界）。

## 四、关键设计决策

### 1. 只传 entityCount+chunkId 到回调层（不传组件指针）

| 字段 | 托管路径 | Transpiler 路径 |
|------|----------|-----------------|
| entityCount | ✅ C++ 需要（tile 分块） | ✅ C++ 需要 |
| componentCount | ✅ 需要（mask 解析） | — |
| enableBitMaps | ✅ 过滤查询需要 | ✅ 原生 adapter 自带 |
| chunkHandle | ✅ chunkId → Chunk[] 索引 | ✅ 原生 adapter 用 GCHandle/offset |
| componentArrays/sizes/typeIndices | ❌ 不传（走 Chunk 对象） | ✅ 完整指针表（C++ 需要） |

### 2. 始终不变的 Execute 签名

```csharp
void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask);
```

- `ChunkEnabledMask.Length == 0` 编码"无过滤"→ Unity 的 `useEnabledMask` bool 是冗余的
- `unfilteredChunkIndex` 只在 ECB sortKey 场景需要→ 保留原签名，需要时引入新接口

### 3. Mask 计算：`ComputeChunkMask` 单一真值来源

回调层（`ChunkJobCallbacks`）和 Run 路径（`ChunkExecution`）统一调用 `ChunkJobScheduler.ComputeChunkMask(chunk, allEnabledTypes)`：
- 单组件：零拷贝（直传 bitmap）
- 多组件：`TempBuffer.GetBuffer` + `Buffer.MemoryCopy` + 逐 ulong AND
- `ResolveEnabledTypes(header, chunk)`：回调层需要，把哈希反解为 `ComponentType[]`

### 4. Transpiler C++ 的 Header 镜像

`CppJobGenerator` / `IspcGenerator` 硬编码生成：
```c++
struct __EntJoyChunkContextHeader {
    int chunkCount; void* queryAllEnabledTypes; int allEnabledCount; int gcHandleStartIndex;
    void* chunksPtr; int cleanupInProgress; int ownsChunkData;
    void* requiredComponentTypeIds; int requiredComponentTypeIdCount;
    int jobIsBoxed; void* chunkArrayHandle;
};
```
**必须**与 C# `ChunkContextHeader` 逐字段对齐（否则 adapter 偏移错位 → job 字段读错 → sumX=0）。已修复：补齐 `ownsChunkData`（之前缺失但碰巧对齐）+ `jobIsBoxed` / `chunkArrayHandle`。

## 五、文件清单（重构后）

```
src/EntJoy.ECS/JobSystem/
├── NativeChunkJobs.cs        ← ABI/P-Invoke/5提交入口/ChunkCleanup/ChunkArrayTable/ChunkContextHeader
├── ChunkJobCallbacks.cs      ← 托管回调层：CreateChunkRangeCallback<T> + ResolveJob<T>（boxed/裸字节）
├── ChunkJobScheduler.cs      ← 托管调用层：Schedule* 编排 + 缓存 + CollectMatchingChunks + FillChunkJobDataList + CreateChunkContextBlock + ComputeChunkMask + ResolveEnabledTypes
├── ChunkExecution.cs         ← Run 路径：主线程直跑（调 ComputeChunkMask）
├── IJobChunk.cs              ← 接口定义
├── IJobEntity.cs             ← 标记接口（由 SG 生成适配器）
├── NativeEcsScheduler.cs     ← 已删除
```

## 六、复杂度来源（vs Unity）

| 复杂度 | Unity | EntJoy | 说明 |
|--------|-------|--------|------|
| 偏移对齐 | `__EntJoyChunkContextHeader` C++ mirror | 同左（必须手动同步） | **Unity 用 Burst JIT，EntJoy 用 NativeTranspiler** → C++ 需要手写 header 镜像 |
| 缓存 | EntityQuery 缓存 chunk 列表 + ComponentTypeHandle 预计算 | RawChunkScheduleCache（HGlobal 表） | Unity 的 Burst 消除了指针表需求；EntJoy 托管路径的 caches 已尽量轻量（entityCount+chunkId） |
| Mask | UnsafeChunkCacheIterator（查询层统一） | ComputeChunkMask（共享工具） | 已合并到一个真值来源 |
| 反射 | NativeJobCore.CreateJobReflectionData | 无反射（Phase D） | ✅ 已解决 |

## 七、添加测试（三种路径覆盖）

建议在 `samples/EntJoySample/09_ECS/` 下新增 `JobChunkPathTest.cs`，覆盖：

1. **托管 Schedule**：`EmptyChunkJob.Schedule(query).Complete()` → 验证无崩溃（已有 `ScheduleOverheadBenchmark`）
2. **托管 Run**：`SumAllJob.Run(query)` → 验证 sum 值正确（已有 `EnabledComparisonBenchmark`）
3. **Native Schedule**：`[NativeTranspile] CppJob.Schedule(query).Complete()` → 验证 sumX（已有 `NativeJobSmokeTest`）
4. **Native Run**：`[NativeTranspile] CppJob.Run(query)` → 验证 sumX（已有 `NativeJobSmokeTest`）
5. **过滤路径**：`SumEnabledJob.Run(query.WithEnabled<Active>())` → 验证 mask 正确（已有 `EnabledComparisonBenchmark`）

现有基准已充分覆盖三种路径。可选：添加有状态的非空 `IJobChunk` Schedule 测试（目前 `ScheduleOverheadBenchmark` 用空 job，`EnabledComparisonBenchmark` 只用 Run），以验证托管路径在真实负载下的数据正确性。
