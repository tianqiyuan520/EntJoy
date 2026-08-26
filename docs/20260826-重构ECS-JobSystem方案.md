# 重构 ECS JobSystem 方案（2026-08-26）

> **状态**：方案评审中（未实施）
> **前置文档**：`项目现状总览.md`、`20260824-JobSystem-深度审查与压力测试报告.md`、`20260822-设计决策记录-AI聊天讨论沉淀.md`
> **事实依据**：开源 Unity.Entities 源码（needle-mirror/com.unity.entities master 分支）逐文件核实；本仓库 `src/EntJoy.ECS`、`src/EntJoy.Jobs`、`src/EntJoy.ECS.SourceGenerator`、`src/NativeTranspiler` 现状核对

---

## 一、背景与问题定义

### 1.1 现状

`IJobChunk` 是 EntJoy.ECS 并行化的唯一真实入口（`IJobEntity` 由 `IJobEntitySourceGenerator` 源码内联生成 IJobChunk 适配器，最终全部走 IJobChunk 通道）。当前调度栈：

```
用户 job.Schedule(query)
  └─ ChunkJobExtensions (World.DefaultWorld)
       └─ NativeEcsScheduler.ScheduleChunkCore<T>          ← 4 条路径分派
            ├─ A 原生 funcPtr 直调（NativeTranspiler ISPC）: ScheduleChunkJobEx
            ├─ B 托管区间回调（普通 C# job 热路径）      : ScheduleChunkRangeJobEx
            ├─ C 托管 chunk-array batch（托管引用 job）   : ScheduleParallelForBatch
            └─ D 兜底逐 chunk 托管回调                    : ScheduleChunkJobEx + GCHandle
                 └─ C++ ScheduleChunkBatchCore（实体数衡 tile + Chase-Lev 提交）
```

### 1.2 复杂度的四个来源（本方案要打击的目标）

| # | 来源 | 位置 | 性质 |
|---|------|------|------|
| 1 | **4 条调度路径 + 6 种 ChunkScheduleMode** | `NativeEcsScheduler.cs`、`JobSystem_Scheduler.cpp` | 冗余（热路径只有 1 条） |
| 2 | **重缓存：每 chunk 全套组件指针表** | `RawChunkScheduleCache` / `BuildRawChunkScheduleCache` | 载荷 O(chunk×组件数)，Unity 只存 8B 索引 |
| 3 | **3 份重复的 enabled-mask AND 实现** | `ResolveCombinedMask` / `ExecuteManagedChunk`(AVX2) / `ChunkExecution` | 重复代码，且执行期重复计算 |
| 4 | **跨语言内存所有权（GCHandle/lease/ContextPool/cleanup）** | `NativeEcsScheduler` / `NativeJobCore` | NativeTranspiler 直调路径的必然代价，但托管路径被波及 |

### 1.3 目标与红线

- **原则**：只取 Unity 的**机制优点**，不搬它的**历史包袱**（API 形态/句柄体系/旧设计）——逐项取舍见 §2.5「取/弃清单」。
- **目标**：行为不变（输出结果逐位一致）的前提下，把调度面收敛、**删除调度缓存（改每调度轻量收集）**、重复逻辑合并；为 `Chunk struct 化`（v3 Phase 1 遗留）和 AOT 修复铺路。
- **红线**：
  1. 保留惰性版本戳缓存（`RawChunkScheduleCache` 的失效检查是 O(1)，**缓存本身不能删**，只降载）；
  2. 保留实体数衡 tile（`BuildEntityBalancedTiles`，优于 Unity 的 `innerloopBatchCount=1`）；
  3. 保留 C++ 逐 chunk 入口给 ISPC funcPtr（签名是 per-chunk，不能删）；
  4. 隐藏当前未使用的 mode/统计字段的 ABI（`ValidateStatsLayout` 会校验结构体字节数）。

---

## 二、Unity 对照实证（本方案的事实基础）

### 2.1 IJobChunk 调度模型（源码结论，master 分支）

```
Schedule          → ScheduleInternal(Single)    → JobsUtility.Schedule(ref scheduleParams)
ScheduleParallel  → ScheduleInternal(Parallel)  → JobsUtility.ScheduleParallelFor(ref scheduleParams, totalChunkCount, 1)
Run               → ScheduleInternal(Run)       → 同一 thunk 主线程执行
```

- 迭代粒度裁决：**按 chunk 个数并行**（`totalChunkCount`），每批 1 个 chunk（源码自带 `// TODO(DOTS-5740): pick a better innerloopBatchCount`）。
  **不是**按实体个数；"per-entity index 反查 chunk" 的说法是幻觉（已核源码 `IJobChunk.cs`）。
- `JobChunkWrapper<T>`（blittable struct，含用户 job + chunk 缓存引用 + filter）**按值 memcpy 进原生 job 系统**；
- `JobChunkProducer<T>.Execute`：每 T 一个 Burst 编译的静态 thunk，反射数据只建一次（`EarlyJobInit`/`CreateJobReflectionData`）——等价物 = EntJoy `GetOrCreateDelegateCache<T, ChunkRangeJobFuncDelegate>`（已有，无需生成器）；
- worker 内 `JobsUtility.GetWorkStealingRange` 拿 [begin,end) chunk 下标区间，`for chunkIndex in [begin, end) → user.Execute(chunk, chunkIndex, useEnabledMask, mask)`。
- `Execute` 签名：`void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)`。

### 2.2 Unity 缓存三层模型（为什么 Unity 不需要指针表）

| 层 | 内容 | 关键源码 |
|----|------|----------|
| 查询缓存 | `UnsafeCachedChunkList`：每 chunk 只存 `ChunkIndex`(8B) + 2 个小 int 表 + `CacheValid` 标志；失效=1 条 store；访问时惰性 `Rebuild`（只拷索引） | `EntityQueryManager.cs` L1533-1604 |
| 存储布局 | chunk 元数据连续数组（`ChunkIndex→base` O(1)）；组件偏移存在 **archetype 的 `Offsets[]`** | `ArchetypeChunkArray.cs` L154-168 |
| 句柄缓存 | `ComponentTypeHandle<T>` 内的 `m_LookupCache`（Archetype*+index+offset），per-archetype 解析一次，后序 chunk O(1)；`GetNativeArray<T>` = base + Offsets[slot] | `ArchetypeChunkArray.cs`（`GetNativeArray<T>`、`GetChangeVersion` L400-411） |

即 Unity 的答案：**启用时一条 store 置无效；重建时惰性、只拷 8B 索引；组件指针从不缓存，靠 archetype 布局表 + handle 内 LookupCache 现场算；调度零分配。**

### 2.3 对照表（EntJoy vs Unity）

| 环节 | Unity | EntJoy 现状 | 差距 |
|------|-------|------------|------|
| 查询缓存哈希去重 | `EntityQueryManager` 按 hash 共享 QueryData | `(entityManager, queryHash, requiredHash, mode)` 字典 | ✅ 同思路 |
| 失效检查 | `CacheValid` 一条分支 | `StructuralVersion` 一次比较 | ✅ 同思路 |
| **重建载荷** | 只拷 8B chunk 索引 | 拷全套组件指针表（O(chunk×组件数) + 多轮 HGlobal） | ⚠️ 重一个数量级 |
| **组件下标解析** | `handle.m_LookupCache`（1 次 int 读） | `Archetype.componentTypeRecorder[typeof(T)]`（per-archetype 字典 O(1)） | ✅ 已够用，**决定不引入 TypeHandle**（见 §六） |
| chunk 元数据 | 连续数组，`ChunkIndex→base` O(1) | `Chunk` 是 class（对象引用） | ⚠️ 需 v3 Phase 1 struct 化 |
| enabled mask | 查询迭代器统一计算，随参数传入 | 3 处实现 + 执行期重复 AND | ⚠️ 重复 |

### 2.4 复杂度定性

"必要复杂度"可归为三类（与 `docs/archive/jobsystem/Jobsystem聊天分析.md` 结论一致）：
无锁编程（Chase-Lev/MPMC/Treiber/ABA 代际）、跨语言边界（C++/C# 双向）、历史兼容（NativeTranspiler/ISPC 直调）。
本方案**不触碰**这三类核心，只收敛"表面分派"与"重复实现"。

### 2.5 取/弃清单：只取 Unity 的优点，不搬历史包袱

> 判据：机制（并行模型、缓存策略、载荷）有没有真实收益 → 取；公共 API 形态、为 Burst/安全系统/旧容量假设服务的特化 → 弃。

| Unity 特性 | 归类 | 处置 | 理由 |
|------------|------|------|------|
| chunk 列表 → 按 **chunk 个数**并行 for | ✅ 优点（模型本质） | 已同构，保留 | 并行粒度与 chunk 内存连续性兼得；per-entity 索引是幻觉 |
| 查询级 chunk 缓存（版本戳失效 + 惰性重建 + 只存索引） | ⚠️ 有条件优点 | **v1 弃**：每调度 O(chunk) 轻量收集（计数永鲜、无失效逻辑）；仅当基准（§B.4）显示收集成本显著再恢复"形状缓存 + 计数增量" | Unity 靠它免每帧收集；但 EntJoy 实体增删即失效 + 计数必须新鲜 → 缓存摊薄收益存疑，用实测裁决 |
| archetype 预计算布局（`Offsets[]`） | ✅ 优点 | 已等价：`componentTypeRecorder` per-archetype 字典 O(1) | 组件定位 O(1) 已达成，**不必复刻其存储结构** |
| 单一调度路径（`JobChunkWrapper<T>` + 通用 JobsUtility） | ✅ 优点 | Phase A 收敛为单一路径 | 消除 4 路径/6 mode 分派 |
| 单一 mask 计算点（`UnsafeChunkCacheIterator`） | ✅ 优点 | Phase C 合并 3 份实现 | 去重 + 可前置到缓存构建期 |
| 实体数衡 tile | ✅ **我们更强** | 保留 | Unity 自己留了 `TODO(DOTS-5740)`；我们已按实体数切 tile |
| `Execute(chunk, unfilteredChunkIndex, useEnabledMask, in v128)` | ❌ 包袱（v128 是 chunk 容量 ≤128 的产物；`useEnabledMask` 是给无状态的 v128 打的 bool 补丁；`unfilteredChunkIndex` 为 ECB sortKey 特化） | **弃**：保持 `(chunk, in mask)` | `ChunkEnabledMask.Length==0` 已编码"无过滤"，无需 bool；小众索引需求未来用新接口引入 |
| `ComponentTypeHandle<T>` / `m_LookupCache` | ❌ 包袱（Burst 编译时代码生成 + 安全句柄 + `DidChange` 版本的产物） | **弃** | 托管 JIT 路径无对应需求；字典查找已 O(1)，为它付 job struct 字段/更新纪律是负收益 |
| 老 `IJobForEach` 生成实体级迭代 | ❌ 包袱（早已废弃的代码生成早期设计） | **弃** | 与 IJobChunk 模型冲突 |
| `CalculateBaseEntityIndexArray(Async)` 等配套数组 | ❌ 包袱（`unfilteredChunkIndex` 的配套） | **弃** | 小众场景，不为它常驻开销 |
| Burst 编译执行链 | ❌ 包袱（Unity 依赖链） | 弃；**等价物 = NativeTranspiler** | funcPtr 直调已实现同款收益 |
| MatchingArchetypes 增量维护（`AddArchetypeIfMatching`） | ⚠️ 可选优点 | 暂不学：缓存重建时全扫可接受（结构变更频率低） | 高频结构变更时再评估增量收益 |

---

## 三、改进方案总览

```
Phase A（2-3 天）：三层拆分 + 管线收敛 —— 托管调用/托管回调/Native调用 分离；4 路径 → 2
    ↓
Phase B（2-3 天）：删除调度缓存 → 每调度轻量收集 —— 无 lease/失效逻辑；规模基准裁决是否恢复形状缓存
    ↓
Phase C（1-2 天）：单一 mask 迭代器（Execute 签名保持不变）
    ↓
Phase D（0.5-1 天）：AOT 反射消灭（现状勘误后仅 2 处，低优先）
```

每阶段结束跑 §五 回归清单，可独立合入。

---

## Phase A：三层拆分 + 管线收敛（架构目标，行为不变）

### A.0 架构分层：托管调用 / 托管回调 / Native 调用

现状问题：`NativeEcsScheduler.cs`（~1800 行）是"上帝类"，混装五类职责——
P/Invoke 指针加载、调度缓存/租赁、chunk 打包、托管回调工厂、调度编排 + transpiler 原始入口。
**本方案的架构目标 = 按执行方向拆成三层，每层单一职责、可独立测试、互不渗透**：

```
┌─ 托管调用层  ChunkJobScheduler（新）
│   公开入口 Schedule<S>(ref job, query, dep, workerCap, rangeSize, writtenComponents)
│   · 每调度轻量收集：Chunk[] + entityCount[] + 前置 mask（§Phase B）
│   · 构建 context（job blob / GCHandle box + header）
│   · 提交 Native 调用层 + TrackEntityJob
│   · Run（主线程直跑）/ RunImmediate（transpiler 委托）
│
├─ 托管回调层  ChunkJobCallbacks（新）
│   · 反向 P/Invoke 回调工厂：CreateChunkRangeCallback<S>（每 S 缓存一次的 thunk）
│   · 执行期：ctx → job；chunks[chunkId] → Execute（带/不带前置 mask）
│   · 执行深度 / 异常捕获边界（EnterJobExecution / RecordJobException）
│   · 只被 C++ worker 经 Native 调用层反向调用；不碰调度与收集
│
└─ Native 调用层  NativeChunkJobs（新）
    · ABI 结构移入：ChunkJobData / ChunkContextHeader / EntityBatchData / ChunkScheduleMode
    · P/Invoke 函数指针加载（幂等） + 5 个提交入口
    · ChunkCleanup（chunk 列表释放 + context 池归还）
    · 无业务逻辑；托管调用层与 transpiler 生成代码都经它
```

数据流（托管路径，每次调度）：

```
调用层: collect(Chunk[], entityCount[], masks) → context(job+header)
   → 原生层: ScheduleChunkRangeJobEx(funcPtr, ctx, cleanup, {entityCount,chunkId}表, dep)
   → C++: 实体数衡 tile → Chase-Lev
   → worker: rangeFunc(ctx, start, count)
   → 回调层: chunks[chunkId] → job.Execute(chunk, mask)
```

**transpiler 路径只走 Native 调用层 + 专用 Collector（建组件指针表），完全不经过托管回调层**——两条执行线从根上解耦。

文件映射：

| 层 | 新文件 | 从 `NativeEcsScheduler.cs` 迁出 |
|----|--------|--------------------------------|
| 托管调用 | `JobSystem/ChunkJobScheduler.cs` | `ScheduleChunkCore` 编排、`CollectMatchingChunks`、`CreateChunkContextBlock`、`TrackEntityJob` 调用、Run 系列 |
| 托管回调 | `JobSystem/ChunkJobCallbacks.cs` | `CreateChunkRangeCallback<T>`、`ExecuteManagedChunk`、mask 解析（§Phase C 并入迭代器） |
| Native 调用 | `JobSystem/NativeChunkJobs.cs` | P/Invoke 指针字段/加载（`LoadNativeChunkPointers`）、5 个提交入口、`ChunkCleanup`、ABI 结构 |
| 删除 | — | 全部缓存/租赁字典（§Phase B）、`CreateChunkCallback<T>`、`RawChunkBatchContext`/`ManagedChunkBatchContext` |

### A.1 收敛目标（拆层后路径自然合并）

```
现状 4 路径          →   收敛后 2 逻辑路径
  A 原生 funcPtr       →   P1 transpiler 路径（原生层 + 专用 Collector；保留 C++ 逐 chunk 入口给 ISPC）
  B 托管区间回调 ✅热路径 →   P2 托管路径（调用层 + 回调层；含托管引用 job 统一 box）
  C 托管 array-batch    →   删除（并入 P2：context 统一 [header][job blob 或 GCHandle]）
  D 兜底逐 chunk 回调   →   删除
```

追加迁移项：`NativeTranspiler/Analyzer/Common/BindingsGenerator.cs` 生成的 `NativeExports.Schedule_<Job>` 等调用目标从 `NativeEcsScheduler.*` 迁移到 `NativeChunkJobs.*` + 专用 Collector（一次性生成器输出改名）。

### A.2 删除清单（文件级）

| 删除对象 | 位置 | 说明 |
|----------|------|------|
| 托管逐 chunk 回调 | `CreateChunkCallback<T>` | 仅 D 兜底用；区间回调已覆盖 |
| 托管 chunk-array batch | `CreateChunkArrayBatchCallback<T>` / `AllocRawChunkBatchContext` / `AllocManagedChunkBatchContext` / `RawChunkBatchCleanup` / `RawChunkBatchContext` / `ManagedChunkBatchContext` | 托管引用 job 改走 P2：context 按标志解析 blob 或 GCHandle box |
| `ExecuteManagedChunk` 旧实现 | 拆层时移入回调层，Phase C 并入单一迭代器 | — |
| 未使用的 mode | `ChunkScheduleMode`（PublishNoAssist/DeferTinyOnly/DeferredPublish/DeferredPublishNoAssist） | C# 侧从未传过（仅 PublishAssist/ImmediateNative 被使用）；**C++ 枚举与统计字段先保留 ABI**，确认无第三方使用后再清 |
| 全部缓存/租赁 | `_rawChunkScheduleCaches` 等 + `RawChunkScheduleCache`(lease) | §Phase B |

### A.3 保留清单（明确不动）

- `ScheduleChunkRangeJobEx`（P2 热路径 = 当前路径 B，原样迁入 Native 层）
- `ScheduleEntityBatchJobEx` / `ScheduleAndCompleteEntityBatchJobEx`（transpiler 实体批 + `RunChunkImmediate`）
- `JobSystem_ScheduleChunkJobEx` 的 **C++ 逐 chunk 入口**（ISPC funcPtr 是 per-chunk 签名 `void(*)(void*, const ChunkJobData*)`）
- 实体数衡 tile、Chase-Lev 提交、依赖挂接（`JobSystem_Scheduler.cpp` 不动）
- `RunChunkImmediate` / `RunChunkRangeImmediate`（ImmediateNative 零唤醒）

### A.4 验收

- 拆层后**编译等价**（仅文件/类迁移，逻辑零改动）+ 三层各自可单测；
- `09_ECS` 全部基准通过；`NativeJobSmokeTest` C++/ISPC 双路径数值不变；
- schedule-only 微基准（1000 次空 IJobChunk Schedule+Complete 取均）与重构前差异 < 5%。

---

## Phase B：删除调度缓存，改为每调度轻量收集（v1）

### B.0 为什么可以不要缓存（设计依据，源码实证）

**C++ 调度器对每个 chunk 只需要 `entityCount`**：`JobSystem_Tiles.cpp` 的 `UnitEntityCount` 只读 `chunks[unit].entityCount` / `entityBatches[unit].entityCount` 来做实体数衡 tile（L47-52）；托管路径的 C# 回调只需要 `chunkId → Chunk`。**组件指针 / 位图 / 类型索引表，托管路径根本不读**——它们只为 NativeTranspiler funcPtr 服务。

缓存存在的唯一理由曾是：摊销"把匹配 chunk 列表交给 C++"的构建成本。但现状三个事实推翻它：

1. **失效粒度过粗**：`EntityManager.cs` 中 `structuralVersion++` 出现在 NewEntity(391)/批量创建(438)/DestroyEntity(490)/AddComponent(798)/RemoveComponent(858)——**任何实体增删都会全量失效**。高频增删场景下缓存等于**每帧全量重建**，摊薄收益为零；
2. **载荷太重**：托管路径复用了为 funcPtr 服务的全指针表（O(chunk×组件数) + 每 chunk 一个 GCHandle，1 万 chunk ≈ 0.1-0.3ms 级）；
3. **计数必须新鲜**：实体数衡 tile 依赖提交时的 entityCount，缓存计数天然可能过期 → 反正要重新收集，缓存没省下这趟活。

### B.1 无缓存调度（托管路径）

每次 `Schedule`：

1. **一趟收集**：遍历匹配 archetype 的 `ChunkSpan`，产出 `(chunkId → Chunk[])` + `entityCount[]`（**全新鲜**，复用 `CollectMatchingChunks` 的匹配逻辑）；
2. **单个 GCHandle** 保活 `Chunk[]`（替代现状每 chunk 一个 GCHandle，成本 ÷chunk 数）；
3. **瞬时缓冲**：`{entityCount, chunkId}` 传给 C++（`chunkHandle` 字段改存 chunkId），cleanup 回调释放（复用现有兜底路径的 `ownsChunkData` 机制）；
4. 回调：`ctx → Chunk[] → chunks[cd->chunkHandle]`，组件访问照旧走 `Archetype.GetComponentTypeIndex<T>` 字典。

**删除**：`_rawChunkScheduleCaches` / `_managedChunkScheduleCaches` / `_entityBatchScheduleCaches`、`RawChunkScheduleCache`(lease/RetainLease/Dispose)、`EntityBatchScheduleCache`、`RawChunkScheduleCacheKey`、全部 `StructuralVersion` 失效校验。
**收益**：失效逻辑清零、计数永鲜、每 chunk GCHandle 消失、跨语言内存所有权只剩"单数组 + 一个 cleanup"。

### B.2 transpiler 路径（同样先删缓存）

- funcPtr 需要的组件指针表**每次调度临时构建** + cleanup 释放（现有 `FillChunkJobDataList` 机制即可，去掉缓存层）；
- 跑基准对比：**稳定负载（无增删）**下若退化明显，仅为此路径恢复"形状缓存"（见 B.3 备选），不拖托管路径下水。

### B.3 备选（仅基准裁决后）：形状缓存 + 计数增量

若 §B.4 显示"10 万+ chunk × 高频调度"下每调度收集成本 > C++ 调度开销的 20%：

- 恢复 `EntityQuery` 已有的 MatchingChunks 雏形（它本就带 `_cachedStructuralVersion` 惰性刷新）；
- 计数改为 **AddEntity/DestroyEntity 增量更新**（每次触碰 1 个 chunk，O(1)），形状变更（建/删 chunk）才全量；
- 关键差异：缓存载荷上限 = `(chunkId, entityCount)` 16B/chunk，**绝不恢复组件指针表缓存**。

### B.4 验收（含裁决基准）

- **新增基准**：10k / 100k chunk × schedule-only × spawny（每帧 ±1 万实体）/ stable 四组合，测 `Schedule()+Complete()` 墙钟；
- **裁决阈值**：无缓存 vs 缓存差异 < 20% 即采纳无缓存方案（预期：高频增删明显更快——省掉每帧全表重建 + 每 chunk GCHandle）；
- 正确性：mask 前置（收集期计算组合位图，复用 `GetOrComputeCombinedMask`）后与现行为逐位一致（复用 `EnabledComparisonBenchmark`）。

---

## Phase C：单一 mask 迭代器（Execute 签名保持不变）

### C.0 决策：签名不动，`useEnabledMask`/`unfilteredChunkIndex` 都不引入

`IJobChunk.Execute` 保持 `void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)`：

- `ChunkEnabledMask.Length == 0` 已编码"无过滤"（生成器适配器正是按 `if (__enabledMask.Length == 0)` 分支），Unity 的 `useEnabledMask` bool 是**冗余的**；
- `unfilteredChunkIndex` 仅在"按 chunk 写 NativeArray 下标 / ECB sortKey"的小众场景需要——**不做**；将来若需要，以新接口或扩展方法引入，不动公共签名。

### C.1 单一迭代器

新增 `ChunkIterator`（对齐 Unity `UnsafeChunkCacheIterator` 的思路，但**不改变公共签名**）：

- 唯一一处 enabled-mask 计算（合并 `ResolveCombinedMask`、`ExecuteManagedChunk`、`ChunkExecution` 三份）；
- **AVX2 提前退出分支必须保留**；
- 空 chunk 跳过、组合位图产出统一在此；Phase B.1 的"缓存构建期 mask"作为其加速路径（执行期零 AND）。

---

## Phase D：AOT 反射消灭（低优先，现状勘误）

### D.1 现状勘误

`项目现状总览.md` §11.5 记录的 3 处反射已过时（`NativeJobScheduler.cs:922`、`EntityManager.cs:871` 均已不存在）。**当前实际反射面（`src` 全量 grep）仅 2 处，且都只在"job 同时实现 `IJobParallelFor` 与 `IJobParallelForBatch`"的罕见路径触发**：

| 位置 | 现状 | 触发条件 |
|------|------|----------|
| `NativeJobCore.cs:752`（`AutoParallelForCallback<T>.Build`） | `MakeGenericMethod` | T 双接口时 |
| `ManagedJobScheduler.cs:925`（`SelectRunner<T>`） | `MakeGenericType(t).GetField("Runner")` | T 双接口时 |

### D.2 方案

- 首选：**免生成器改造**——新增恒等泛型静态类 `DualRunnerCache<T> where T : struct, IJobParallelFor, IJobParallelForBatch`，把两条反射路径收敛为普通泛型静态字段读取；仅当约束不满足（C# 无法在编译期表达"T 满足双约束"）时兜底保留反射。
- 次选：SourceGenerator 扫描双接口 job，生成注册代码（对齐 Unity `RegisterGenericJobType`/`EarlyJobInit` 模式）。
- 收益：AOT（iOS/主机/Godot）消除 `MakeGeneric*`；非热路径，无性能影响。
- 备注：这不是"把 IJobChunk 生成成 JobParallelFor"——**不需要也不应该**用生成器做调度适配（见 §六）。

---

## 四、性能影响评估

| 改动 | 落在热路径？ | 性能影响 | 风险 |
|------|------------|----------|------|
| A：删托管逐 chunk 回调（D 兜底） | ❌ 冷路径 | 无 | 低 |
| A：删托管 array-batch（C），托管引用 job 并入区间回调 | 托管引用 job | 无（同为区间调度，GCHandle 解析次数不变） | 低 |
| A：mode 6→2 | ❌ 其余未使用 | 无（C++/统计字段先保 ABI） | 低 |
| B：删缓存 + 每调度轻量收集 | ✅ 热路径 | 高频增删：**正向**（不再每帧全表重建 + 每 chunk GCHandle）；稳定大世界：**待测**（收集 O(chunk) vs 缓存命中） | 中（§B.4 阈值裁决） |
| B：transpiler 路径同删缓存 | ✅ transpiler 热路径 | 稳定负载可能微退（每调度建指针表），基准裁决 | 中（必要时仅该路径恢复形状缓存） |
| C：单一 mask 迭代器（签名不动） | ✅ 带过滤 job | 算法不变则无；收集期前置 mask 后变快 | 中（AVX2 必保） |
| D：AOT 反射消除 | ❌ 冷路径 | 无 | 低 |
| ⚠️ **删缓存本身** | **热路径** | **大概率退化**（每帧重建 O(chunk×组件) 表） | **高：不做** |

> 重要事实：C# 侧调度写 context 仅 0.0001ms，100% 调度开销在 P/Invoke→C++ 内部（0.045ms，连续调度 3-7μs）。因此 Phase A 的收益是**维护性与 API 面**，Phase B 才是**性能**阶段；不要期待收敛本身带来提速。

---

## 五、回归验证清单（每阶段必跑）

1. `09_ECS/EnabledComparisonBenchmark`：无过滤 + enabled 过滤的 Run/Query 对比（mask 正确性）；
2. `09_ECS/IJobEntityEnabledBenchmark`：生成器适配器 + enabled 位图跳转（Phase C 后 adapter 重生成验证）；
3. `09_ECS/NativeJobSmokeTest`：`[NativeTranspile]` Schedule + RunImmediate 双路径（C++/ISPC 数值不变）；
4. `JobLibsBenchmark` S1-S6：纯 IJob 族不受 ECS 重构波及；
5. **schedule-only 微基准**：连续 1000 次空 IJobChunk `Schedule()+Complete()` 取均，与重构前差异 < 5%；
6. `IJobChunkMoveCompareTest`（100 万实体 C#/C++/ISPC 全路径）——若有回归测试入口则纳入。

---

## 六、不做什么（防过度设计）

| ❌ 不做 | 原因 |
|--------|------|
| 用代码生成器把 IJobChunk 生成成 JobParallelFor | Unity 也不做（`JobChunkProducer<T>` 是泛型运行时 thunk）；EntJoy `GetOrCreateDelegateCache<T>` 已等价 |
| per-entity 索引调度 | 与 Unity 实证结论相悖，且破坏 chunk 内存连续性收益 |
| 维持全局多字典缓存 + lease/引用计数机制 | v1 直接**删除**：改每调度轻量收集（无失效逻辑、计数永鲜）；仅当 §B.4 基准显示收集成本显著，才为所需路径恢复"形状缓存 + 计数增量"（载荷上限 16B/chunk，**绝不恢复组件指针表缓存**） |
| 移除 NativeTranspiler 直调路径/并入托管路径 | Burst 缺席下 funcPtr 是唯一"worker 零跨语言"通道 |
| 引入 Burst/IL2CPP 依赖 | 定位是无头 C# 框架，Burst 不在依赖范围 |
| 引入 ComponentTypeHandle\<T\> | `Archetype.GetComponentTypeIndex<T>` 已是 per-archetype 字典 O(1)；Unity 需要 handle 是为了 Burst 编译 + 安全系统 + 变更版本，托管路径没有这些需求，per-chunk 1-3 次字典查找相对 chunk 工作量可忽略——**为不需要的东西付复杂度** |
| 改 `IJobChunk.Execute` 签名 | `ChunkEnabledMask.Length==0` 已编码"无过滤"；`useEnabledMask` 冗余、`unfilteredChunkIndex` 属小众需求，均不引入，保持 `(chunk, in mask)` |
| 照搬 Unity 的公共 API 形态（签名/句柄/迭代器类型） | 只取机制优点（§2.5），不搬 API 形态——签名、TypeHandle、v128 位掩码都是 Unity 历史条件的产物 |

---

## 七、实施顺序与工时（预估）

| 序号 | 内容 | 工时 | 依赖 |
|------|------|------|------|
| 1 | Phase A：走查 `ScheduleChunkCore` 各路径触发条件，确认 C/D 冷路径可删清单 | 0.5d | — |
| 2 | Phase A 实施 + §五 1/2/5 回归 | 1-1.5d | 1 |
| 3 | Phase B.1：删缓存字典/lease，改每调度轻量收集（单 GCHandle 保活 Chunk[] + chunkId 表） | 1-1.5d | 2 |
| 4 | Phase B.2：规模基准（10k/100k × spawny/stable）裁决是否恢复形状缓存 | 1d | 3 |
| 5 | Phase C：`ChunkIterator` 合并（签名不动，mask 前置到收集期） | 1d | 4 |
| 6 | Phase D 反射消除（可与 2 并行） | 0.5-1d | — |
| 7 | 文档更新：`项目现状总览.md`（§11.5 勘误 + 本方案落地状态） | 0.5d | 全部 |

---

## 四、额外完成项（方案外的架构改进）

> 以下改进在 A-D Phase 之外完成，提升了架构质量和可维护性。

### 4.1 TrackEntityJob 双后端支持

`TrackEntityJob` 参数从 `NativeJobHandle` 改为 `JobHandle`（兼容 Native/Managed）。
- `EntityManager.TrackEntityJob` 接受 `JobHandle`，内部按 `_nativeHandle`/`_managedHandle` 判断
- Managed fallback 调用 `TrackEntityJob` → 结构变更时只等影响该 archetype 的 job（Selective Wait）
- C++ 路径仍返回 `NativeJobHandle`（通过 `FromNative()` 辅助）

### 4.2 JobScheduler 统一调度器

`EntJoy.Jobs/JobScheduler.cs`：自动选择 Native/Managed。
- `Initialize()` try-catch C++ 初始化 → UseFallback → ManagedJobScheduler
- `Schedule/ScheduleParallelFor/ScheduleFor/ScheduleBatch` 根据 UseNative 路由
- Managed fallback 的 `IJobFor`/`IJobParallelForBatch`：顺序包装器（`SequentialForJob<T>`/`SequentialBatchJob<T>`），避免泛型约束冲突
- `JobExtensions.Schedule*` 改调 `JobScheduler`（不再直接碰 NativeJobScheduler/ManagedJobScheduler）

### 4.3 Native fallback

`ChunkJobScheduler.ScheduleChunkManagedFallback`：
- `ChunkJobCollector.CollectAndBuildManaged` → `ManagedChunkParallelJob<T>` → `JobScheduler.ScheduleParallelFor`
- 纯 C# 路径：无 ChunkContextHeader、无 ChunkArrayTable、无 ChunkJobCallbacks
- `JobHandle` 双后端：`_nativeHandle`（C++）或 `_managedHandle`（Managed），`Complete()` 自动路由

### 4.4 EntJoy.Jobs 零 ECS 字眼 + 文件夹分类

```
EntJoy.Jobs/
├── Native/       ← C++ 后端（NativeJobScheduler + NativeJobCore）
├── Managed/      ← 纯 C# 后端（ManagedJobScheduler + 所有辅助类）
└── (根目录)      ← 共享/公共（JobScheduler + JobHandle + JobExtensions + JobInterface + NativeJobHandle）
```

所有注释/文档中移除 "ECS" 字眼（`AssemblyInfo.InternalsVisibleTo` 保留，跨项目必要）。

### 4.5 struct 内存安全性确认

- 热路径 `new` 均为 struct（值类型）→ 栈构造，零 GC
- `ManagedChunkParallelJob<T>` 的 `Chunk[]` 引用字段通过 struct boxing（GCHandle）保活 → 执行期 safe
- C++ 路径：`Unsafe.CopyBlockUnaligned` 将 job 拷贝到 HGlobal（非托管内存）→ 副本完全独立于栈原件
- GCHandle 唯一不可省略处：managed-reference job 的 boxing（防止 GC 回收正在执行的 job）
- 所有 blittable job 整条调度链路零 GCHandle

---

## 附录：参考资料

- [Unity.Entities/IJobChunk.cs（master）](https://github.com/needle-mirror/com.unity.entities/blob/master/Unity.Entities/IJobChunk.cs) —— `JobChunkWrapper<T>`、`ScheduleInternal`（L280-336）、`JobChunkProducer<T>.ExecuteInternal`（L367-460）
- [Unity.Entities/Iterators/EntityQueryManager.cs](https://github.com/needle-mirror/com.unity.entities/blob/master/Unity.Entities/Iterators/EntityQueryManager.cs) —— `UnsafeCachedChunkList`（L1533-1604）、`InvalidateChunkCache`（L1806）
- [Unity.Entities/Iterators/EntityQuery.cs](https://github.com/needle-mirror/com.unity.entities/blob/master/Unity.Entities/Iterators/EntityQuery.cs) —— `GetMatchingChunkCache`（L2005-2017）
- [Unity.Entities/Iterators/ArchetypeChunkArray.cs](https://github.com/needle-mirror/com.unity.entities/blob/master/Unity.Entities/Iterators/ArchetypeChunkArray.cs) —— `GetNativeArray(EntityTypeHandle)`（L154-168）、`GetNativeArray<T>`（LookupCache 用法）、`GetChangeVersion`（L400-411）
- [Implement IJobChunk 官方手册（6.5）](https://docs.unity3d.com/Packages/com.unity.entities@6.5/manual/iterating-data-ijobchunk-implement.html)
- 本仓库相关：`src/EntJoy.ECS/JobSystem/NativeEcsScheduler.cs`、`src/EntJoy.ECS/Chunk/ChunkJobExtensions.cs`、`src/EntJoy.ECS/JobSystem/ChunkExecution.cs`、`src/EntJoy.ECS.SourceGenerator/IJobEntitySourceGenerator.cs`、`src/EntJoy.Jobs/NativeJobCore.cs`

---

*本文档为方案评审稿；任何 Phase 实施前应先在 GitHub Issue / 评审会话中确认删除清单与 ABI 保留项。*