# NativeAdapter 与 Query 开销分析（当前状态）

> 覆盖 commit `86919df`（布局推导 + sentinel）、native adapter 路径、调度粒度通用化、
> guided tile 调度（§7，默认开启）。
> GridSearch 代码未动（框架侧优化：NativeTranspiler + JobSystem）。

---

## 1. Native adapter 路径（消除托管桥）

### 1.1 问题

transpiled 并行 job（如 GridSearch 的 `ClosestPointJob`）原路径为：

```
C# Schedule → native worker → Execute_Batch(managed delegate) → 回到 C# 执行 body → 返回 native
```

每个 tile 一次 **native→managed→native** 双桥接，~3μs/tile。GridSearch query 60~390 tile 时桥接开销不可忽略（submit 总延迟 ~0.78ms 时占明显比例）。

### 1.2 改动

- `BindingsGenerator.cs:301`：非 MT 并行 job 的绑定静态构造改为取原生适配器指针
  `s_{Name}_BatchFuncPtr = Get_{Name}_Execute_AdapterPtr()`（DllImport getter，`BindingsGenerator.cs:410`），
  替代 `Marshal.GetFunctionPointerForDelegate`（managed delegate，`:294`）。
- 原生侧 `Execute_Adapter(void* context, int start, int count)` 按 transpiler 生成的字段偏移直接从
  context 读 job struct 字段，调用 `Execute_Batch`（ISPC/C++ 纯原生）。
- 效果：每 tile 从 3μs → ~0.3μs；QueryCore 0.78ms → **0.708ms**（p50，k=26）。

### 1.3 关键点

- 适配器偏移**全部由 transpiler 生成**（`CppJobGenerator.CalculateFieldOffset`），非手写 C++ → C# 结构变化自动跟随。
- 这依赖「布局推导」正确（见 §3）——此前正是布局硬编码导致 segmentfault。

---

## 2. 调度粒度通用化（tiles/worker，无 cost 注解）

### 2.1 演进

| 阶段 | 机制 | QueryCore p50 |
|---|---|---|
| 基线 | batch=0 → 默认粗 tile（~1667，60 tile） | 0.651 |
| 实验 | `QueryBatchSize=256`（~391 tile） | 0.582 |
| 发现 | transpiled job 绑定用 `32*ProcessorCount`=512 tile 绕过 ResolveChunkSize | — |
| **当前** | batch=0 + `TilesPerWorker=26`（~390 tile） | **0.708**（adapter 生效后） |

### 2.2 当前机制

- `JobSystem.cpp:751` `ResolveChunkSize(length, 0) = max(16, ceil(length / (W×k)))`，
  `k = g_configuredTilesPerWorker`（默认 16，`:78`）。
- `NativeJobScheduler.cs:730` `TilesPerWorker = 26` → `JobSystem_ConfigureTilesPerWorker`（Initialize 期，`:720`）。
- `BindingsGenerator.cs:508` 非 MT 分支 `actualBatchSize = innerBatchCount` 直通（不再 512 覆盖）。
- `GridSearch2D.cs`：`QueryBatchSize = 0`（回归默认），两处 SearchClosestPoint Schedule 走 tiles/worker。
- **scale-free**：batch = N/(W×k) 自动随 N 缩放；全部 batch=0 job 零注解受益。

### 2.3 k 定标

- k=26（rc=390）可变代价最优；k=52（更细 tile）**回归 0.722**：更细 tile 不缩小 straggler（数据相关代价方差），反而加 tile-claim 开销。
- 默认 16 = 可变代价（受益）与均匀代价 job（少付 claim）折中。

---

## 3. 布局推导 + Sentinel 兼容（Unity/Burst 式）

### 3.1 段错误根因

`#define DEBUG` 强制 `DisposeSentinel _sentinel` 进所有构建 → NativeArray 实际 40B，但 transpiler 硬编码 32B → 首容器后所有字段读垃圾指针 → exit 139。

### 3.2 修复（commit 86919df）

1. **移除 `#define DEBUG`**（`NativeArray.cs`）→ sentinel 真正仅 Debug 存在（对齐 `NativeList.cs` 的 `#if DEBUG`）。
2. **`CppJobGenerator` 布局改为实际字段递归推导**：
   - 删硬编码 32/24/20 早退 → 走 `GetStructSizeRecursive`（Sequential 逐字段对齐）。
   - 修两个递归 bug：引用类型字段（`DisposeSentinel`）= **8B 指针**（原算 4B）；枚举对齐 = **底层类型**（原算 1）。
   - Release 自动 = 32/24/20，Debug 带 sentinel 自动 = 40/32/20 —— **与运行时编译配置永远一致**。
3. **`NativeTranspiler.cs` 去重**：`ComputeStructSize`（C++ static_assert 用）委托同一递归，删第二份硬编码表 → 单一事实来源。
4. **C++ 侧宏门控**：`NativeContainers.h` 的 `NativeArray<T>`/`NativeList<T>` 加
   `#if defined(ENTJOY_ENABLE_SENTINEL) void* m_Sentinel; #endif`；`NativeTranspilerGenerator` 的 CMakeLists 模板加
   `option(ENTJOY_ENABLE_SENTINEL)`（Debug 原生构建对齐，Release 默认 OFF）。

### 3.3 验证

- Release 递归结果 == 旧硬编码 → 生成 native 工件**字节级不变**。
- benchmark 正确性 `74945 ... 87386` 不变，QueryCore p50 ~0.71-0.73（无回归）。

---

## 4. Query 开销分析

### 4.1 开销在哪（实测）

| 项 | 值 | 说明 |
|---|---|---|
| submit2first（提交→首个 worker 认领） | 9~28μs | 提交 + wake 延迟 |
| tile-claim（每 tile 一次原子 fetch_add + 回调） | ~10-20μs 总量 | rc=390，单 tile 几十 ns |
| **straggler tile（最慢单个 tile 执行）** | **80~853μs** | **主因：数据相关代价方差** |
| 100k fallback 全表扫描 | 从不触发 | ignoreSelf=false → 自身点恒在 3×3 cell 内是候选 |

- query 原生 batch 总耗时 ≈ C# QueryCore（无隐藏 C# 开销）。
- 尾部（p99-max）来自 dense-cell tile：单 tile 内 129 query × 3×3 cell × ~900 dist 检查。
- **尾宽不是 wake 延迟**（keep-warm spin 实验负结果，见 `keep-warm-spin-negative-result.md`）。

### 4.2 调度开销能解决吗？

**不能 —— 且不需要。** 调度开销是上述占比最小项（~30-50μs 总量），work-stealing（worker 动态
`fetch_add` 认领 tile）已把空闲最小化。真正的瓶颈是 **per-query 代价方差 + 内存带宽**：
- straggler 是「某 tile 恰好含多个 dense-cell query」，粒度切细不缩小它（k=52 已证）。
- 框架级真正杠杆是**数据布局**（SortedPositions/HashIndex 的 AoS 缓存局部性 / SoA 化），
  但这属带宽优化、不属调度；当前 GridSearch 代码禁止改动。

> **后续修正（§7 guided）**：粒度切细不能缩小 straggler，但**细粒度放在哪**可以——
> guided 只把细 tile 放尾部（chunk ∝ 剩余工作量），定向钳死密度型 straggler，
> QueryCore p50 0.637 → 0.583（-7~14%）且认领数不爆炸。数据布局杠杆仍不动 GridSearch。

### 4.3 框架优化点（已做/可做）

| 优化 | 状态 |
|---|---|
| native adapter（消托管桥） | ✅ 已做（§1） |
| tiles/worker 通用化粒度（k=26） | ✅ 已做（§2） |
| guided tile 调度（chunk ∝ 剩余工作量） | ✅ 已做（§7，默认开启 k=4/floor=16） |
| 布局推导 + sentinel 兼容 | ✅ 已做（§3） |
| 上下文 buffer 池化（免每 schedule 分配） | 可做（预期小收益） |
| 数据布局 SoA / 预排序（带宽） | 可做（框架层 API，GridSearch 不动） |

---

## 5. 验证命令与验收

```powershell
cd "e:\GODOT\Project\EntJoy"
dotnet build src/EntJoySample/EntJoySample.csproj -c Release
./bin/EntJoySample.exe
```

验收：
- `查询结果前10个`：`74945 21160 15114 75587 37949 80702 88467 19643 11454 87386`。
- `DIAG|` 行：`tilesPerWorker=26|workerCount=15|queryBatch=0`；stdout 首行 `JobSystem|guided=True|k=4|floor=16`。
- `GridSearch-QueryCore` p50 0.58-0.60ms（guided k=4 默认，跨运行 ±0.05）。

---

## 6. 修改文件清单

| commit | 文件 | 改动 |
|---|---|---|
| 86919df | `CppJobGenerator.cs` | 布局递归推导 + 引用类型/枚举修复 |
| 86919df | `NativeTranspiler.cs` | `ComputeStructSize` 去重委托 |
| 86919df | `NativeTranspilerGenerator.cs` | CMakeLists 模板加 sentinel option |
| 86919df | `NativeArray.cs` | 移除 `#define DEBUG` |
| 86919df | `NativeContainers.h` | NativeArray/NativeList 宏门控 sentinel 字段 |
| （本次） | `BindingsGenerator.cs` | 非 MT 分支 adapter 指针 + actualBatchSize 直通 |
| （本次） | `NativeJobScheduler.cs` | TilesPerWorker=26 + Configure 导出 |
| （本次） | `Exports.h/.cpp`、`JobSystem.h/.cpp` | `JobSystem_ConfigureTilesPerWorker` |
| （本次） | `GridSearch2D.cs`、`TestGridSearch.cs` | QueryBatchSize=0 回归 + DIAG 行 |
| （本次） | `JobSystem.cpp` | `GuidedTileCount`/`BuildGuidedTiles` + `ConfigureGuided` + 三处 tile 构建替换（§7） |
| （本次） | `JobSystem.h`/`Exports.h`/`Exports.cpp` | `JobSystem_ConfigureGuided` 导出（§7） |
| （本次） | `NativeJobScheduler.cs` | GuidedEnabled/K/FLOOR + `ENTJOY_GUIDED_*` env 钩子（§7） |

---

## 7. Guided tile 调度（chunk ∝ 剩余工作量）

> 目标：定向收紧 straggler 尾宽，不动 GridSearch（框架侧 JobSystem 优化）。

### 7.1 背景：尾宽来自 per-query 代价方差

- QueryCore p50 ~0.708ms 已到带宽/代价下限；尾部 p95-p50 ~0.2-0.4ms。
- 主因 **per-query 代价方差**：dense-cell query 单 tile 最高 80~853μs。
- uniform 固定 tile（k=26 → 257 query/tile）被 worker 认领后不可再分 → 含多个 dense-cell
  query 的 tile 成为 straggler。
- 已证伪：**k=52（均匀更细）** 认领数暴涨 2x（781）而尾部仍 128 固定大块，straggler 没被钳死；
  **keep-warm spin** 对紧循环无效（尾宽不是 wake 延迟，见 `keep-warm-spin-negative-result.md`）。

### 7.2 方案：OpenMP schedule(guided) / TBB auto_partitioner 同族

```
chunk = max(floor, ceil(remaining / (W × k)))
```

- **头部 chunk 大**（首个 ≈ N/(W×k)）：大 tile 内 query 多 → 代价被平均律平滑
  （Poisson 相对方差 ∝ 1/√N），**天然不是 straggler**。
- **尾部 chunk 小到 floor**（默认 16）：单 tile 最坏代价从 257×900 收紧到
  max(平滑大块, 16×900)，**密度型 straggler 被定向钳死**。
- **总认领数 ≈ W×k×ln(N/floor)**：N=100k, W=15, k=2 → ~262，比 k=26 的 390 还少。
- 与 k=52 的本质区别：guided 的细粒度**只出现在尾部**，认领数不爆炸。

> 注意：chunk 按剩余工作量算（`remaining/(W×k)`），**不是**从 cs 每步减半——后者会在 floor 上
> 切出 6250 个 tile，重演 k=52 的认领爆炸。

### 7.3 k / floor 语义（为何默认 k=4, floor=16）

| 参数 | 语义 | 性质 |
|---|---|---|
| `k` | tiles/worker（比例） | **scale-free**：随 N/W 自动缩放，通用 |
| `floor` | 最小 tile 大小（绝对迭代数） | **仅影响尾部**，8~64 一般安全 |

A/B 扫描（QueryCore，100 帧稳态）：
- **k**：k=1 尾部失控（p99 1.349）；k=2 p50 0.606；**k=4 甜点**（p50 0.583-0.596）；k=8 认领开销回归。
- **floor**：8 / 32 均差于 16 → 默认 16。

### 7.4 实现（JobSystem.cpp）

- `GuidedTileCount`/`BuildGuidedTiles`（:822/:840）静态 helper，返回实际 tile 数。
- 三处 tile 构建循环替换：`ScheduleParallelFor`（:2146）、`ScheduleParallelForBatch`（:2208）、
  `ScheduleChunkBatchCore`（:2291），`guided` 时用返回值设 `batch->tileCount`。
- `Scheduler::ConfigureGuided(enabled, k, floor)`（:2088）→ 3 个全局。
- **护栏**：`rc <= 1` 仍走快速路径；`batchSize > 0`（用户显式指定）仍 uniform；
  ECS `ResolveEcsBatchRangeSize` 不动；`ResolveChunkSize` 本身不改（仍用于求 worker 数）。
- **认领循环零改动**：`WorkerAtomicRangeLoop`/`AssistExecuteOneTile` 已按 `ExecutionTile.itemCount`
  逐 tile 认领，guided 只是 tile 非均匀，worker 按 0,1,2,... 顺序认领 → 大块先被抢走。

### 7.5 env 门控（A/B 无需重编译）

| 变量 | 默认 | 作用 |
|---|---|---|
| `ENTJOY_GUIDED_TILES` | 1（开启） | 0 = 关，回退 uniform |
| `ENTJOY_GUIDED_K` | 4 | tiles/worker |
| `ENTJOY_GUIDED_FLOOR` | 16 | 最小 tile |

NativeJobScheduler.cs `ConfigureGuidedFromEnv()`（:752）→ `JobSystem_ConfigureGuided`
（C# 封装 :583 → 导出 `JobSystem_ConfigureGuided`，Exports.cpp:68）。

### 7.6 实测

| 指标 | baseline（uniform k=26） | guided（k=4, floor=16） |
|---|---|---|
| QueryCore p50 | 0.637 | **0.583-0.596（-7~14%）** |
| QueryCore p95 | 0.84 | **改善** |
| BuildCore-Steady（uniform 代价 job） | 0.545 | 0.544（不回归） |
| 正确性 `74945 ... 87386` | — | 不变（只改调度不改语义） |
