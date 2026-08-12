# 托管开销 / GCHandle 分析 与 内存局部性再评估

> 覆盖 commit `86919df`（布局推导）+ 当前 dev 状态。回答四个问题：
> (1) ISPC 翻译里的 prefetch 现状；(2) 托管开销能否解决 / GCHandle 是否有开销；
> (3) 已记录到本文；(4) 重新分析可提升处（内存热页/缓存局部性），参考其他项目。
> GridSearch 代码仍冻结（框架侧优化）。

---

## 1. Prefetch 现状清点（2026-08-12 实测后）

| 层 | 位置 | 现状 |
|---|---|---|
| ISPC 翻译（生成/手写 `*.ispc`） | `SharpNative_..._Execute_Batch.ispc` 等 | **0 处**。实验加的 `prefetch_l1/l2` 已完全还原（核对产物） |
| C++ 调度器 tile 预取 | `JobSystem.cpp:1698` `PrefetchNextTileData` | `_mm_prefetch NTA`，仅 `EntityBatchRange`/`ChunkCallbacks`/`ChunkRange`（ECS chunk/entity job） |
| 文档提及 | `docs/03 §8.4`、`docs/04 §4` | 均为 prefetch **负结果**的记录，非生产代码 |
| 历史实验 | `docs/performance/benchmark-analysis-2026-07-20.md` | 旧 NTA 变体实验（同 `PrefetchNextTileData` 一系） |

**结论**：GridSearch 的 query 路径（`TileKind::GeneralRange`）在 `PrefetchNextTileData` 两个分支都不命中 → no-op。
`PrefetchNextTileData` 服务的是 IJobChunk 顺序遍历（计算密集），与 gather-bound query 是两类负载。
**不删**：负面证据仅覆盖 gather 型负载；删它只改变 chunk job 行为，无依据。

---

## 2. 托管开销（transpiled 并行 job 路径）

### 2.1 每 schedule 的 C# 成本构成

| 项 | 位置 | 量级 | 可消除 |
|---|---|---|---|
| `Marshal.AllocHGlobal(180B)` 未池化 | `BindingsGenerator.cs:454` | ~0.2-1μs（堆锁） | ✅ ContextPool |
| `Unsafe.CopyBlockUnaligned` 拷贝 | `:456` | 可忽略 | — |
| 完成时 native→managed 清理桥（delegate→`Marshal.FreeHGlobal`） | `:315-319` | ~0.5-1μs | 原生池化（收益仍 <1%） |
| Schedule+Complete 两次 P/Invoke + delegate 缓存 + 异常检查 | — | ~1μs | 合并 P/Invoke |
| `Marshal.FreeHGlobal` | `:317` | ~0.2-1μs | ✅ ContextPool |

实测 C# 层合计 **~6-12μs/帧**（`docs/04 §5.1`，QueryCore p50=646μs 的 1-2%）。

### 2.2 解决方案（按收益）

1. **ContextPool 替代 AllocHGlobal/FreeHGlobal（值得做）**：绑定改为 `NativeJobScheduler.ContextPool.Rent(size)`，
   cleanup delegate 改为 `ContextPool.Return(context, size)`（size 是编译期常量，可烘焙进绑定）。免堆分配/释放。
   单 job 省 ~1-2μs；**一帧几百个 transpiled job 时是几百次堆分配 → 数百 μs**，这才是它的价值场景（本基准只有 1 个 job，收益 <0.5%）。
2. 合并 Schedule+Complete 的 P/Invoke（EntityBatch 已有 `ScheduleAndComplete` 先例）→ ~0.5μs。
3. 上下文改原生池持有以杀 native→managed 清理桥 → 改动所有权模型，<1%，不做。

### 2.3 为何不在 GCHandle 上省（本路径）

transpiled 并行 job 路径 **不用 GCHandle**（用 AllocHGlobal + 函数指针）。GCHandle 只存在于托管上下文路径（§3），
GridSearch 基准不经过它。→ 当前 query 性能与 GCHandle 无关。

---

## 3. GCHandle 开销（真实，但在 IJobChunk 路径）

### 3.1 使用清单（NativeJobScheduler.cs）

| 位置 | 用途 | 频次 |
|---|---|---|
| `:1404` / `:1562` / `:1679` | chunk 调度：每 chunk `GCHandle.Alloc(WeakTrackResurrection)` | **每 schedule × chunk 数** |
| `:3326` | `AllocManagedContext`（托管 job box，Normal） | 每 schedule × 1 |
| `:3332` / `:3341` | chunk batch 上下文（Normal） | 每 schedule × 1 |
| `:2521` | `_chunkContextLeases`（ConcurrentDictionary<IntPtr,GCHandle>） | 每 schedule × 1 |
| `TempBuffer.cs:21` | 单 pinned handle | 一次性 |

### 3.2 量级

`GCHandle.Alloc` 是 GC 表操作，~100-200ns/次。10 万实体 / ~128 per chunk ≈ **780 次/schedule** → 大 chunk job 上百 μs。
清理在 `:2576-2585`（完成时逐槽 Free）。`WeakTrackResurrection` 还参与 finalization，额外 GC 压力。

### 3.3 缓解（已做 / 候选）

**已做：transpiled chunk job 不装箱（2026-08-12，框架侧，零 GridSearch 改动）**

原生 adapter 回调只读 `ChunkJobData` 原始指针（`entityArray`/`componentArrays`/`enableBitMaps`），从不读
`chunkHandle`（`ChunkJobData.h:15` 注释：仅在 C# 托管回调中恢复 Chunk 对象）。因此 transpiled IJobChunk
（funcPtr ≠ 0）在 cache 未命中 / enable filter 的 fallback 路径上**不再每 chunk `GCHandle.Alloc(WeakTrackResurrection)`**：

- `ChunkContextHeader` 加 `ownsChunkData` 字段：把「拥有 chunk 数据缓冲区」与「拥有 GCHandle」解耦。
- `ScheduleChunkCore` / `ScheduleNativeChunkRawCore` / `ScheduleNativeChunkRangeRawCore` 三个 fallback：
  `funcPtr ≠ 0` 时跳过 GCHandle 分配（`gcHandleStartIndex = -1`、`chunkHandle = IntPtr.Zero`），
  仅 `funcPtr == 0`（纯托管回调）才装箱。
- `ChunkCleanup` 改读显式 `ownsChunkData`（释放每 chunk AllocHGlobal 缓冲区 + chunksPtr），
  GCHandle 释放单独按 `gcHandleStartIndex >= 0` 门控。
- 与既有 raw-cache fast-path（早已 `gcHandleStartIndex=-1`）语义一致；10 万实体 / ~128 per chunk
  从 ~780 次 GCHandle.Alloc/Free per schedule → 0（仅 cache 未命中 fallback 触发的路径生效，
  steady-state 走 raw cache 本就不装箱）。

**候选（未做）**：chunk→GCHandle 映射跨 schedule 缓存（chunk 是稳定托管对象，句柄只在 chunk 销毁时释放，
job 期间复用）。对纯托管 IJobChunk（funcPtr==0）有意义；transpiled 路径已不需。与 Unity 的本质差异：
Unity chunk 内存是 archetype 持有的原生指针，根本不装箱 → 无 per-chunk 句柄。

> 注意：这不影响 GridSearch 基准（不走 IJobChunk）；只对 IJobChunk/IJobEntity 工作负载（`docs/03 §8.2` 的 6x SIMD 案例）有意义。

---

## 4. 重新分析：还能挤哪（内存热页 / 缓存局部性，参考其他项目）

### 4.1 分层现状（实测，`docs/04 §5`）

| 层 | 量级 | 占比 | 已到地板? |
|---|---|---|---|
| C# 调度 + 连接 | ~6-12μs | 1-2% | 可再省 <1%（§2.2） |
| C++ 调度（wake+claim+guided） | ~15-30μs | 3-5% | guided 已到收益递减 |
| **C++ 执行（ISPC gather）** | **~600μs** | **~93%** | **内存延迟地板** |

### 4.2 "内存热页"的真实含义

执行段的 600μs 不是"缺热页"——工作集（`SortedPositions` 800KB + `HashIndex` 400KB + `CellStartEnd`）已在 L2/L3 热区。
真正的问题是**每 cache line 的利用**：cell 平均 2.5 点 ≈ 20B，一个 64B cache line 填不满 1/3；每 query 随机跳 9 个
cell → 每次 gather 大概率 miss，但 miss **已被 CPU OOO 掩盖**（MLP 充足，prefetch 负结果即证据）。所以：

- **加大缓存 / 预取不是杠杆**（已证伪）。
- **杠杆 = 让同一条 cache line 承载更多命中**，即**数据布局**，不是调度、不是翻译质量。

### 4.3 参考其他项目（可搬的范式）

| 项目 | 做法 | 对 GridSearch 的启示 | 可行? |
|---|---|---|---|
| **Unity DOTS** | chunk-major SoA：16KB chunk 内组件连续 → 顺序遍历带宽友好 | 这是它快的唯一来源，不是调度（`docs/03 §8.3`） | GridSearch 是空间哈希随机 gather，**布局冻结** |
| **TBB `auto_partitioner`** | 剩余驱动 chunk | 已落地为 guided（`docs/03 §7`） | ✅ 已做 |
| **Rayon** | 递归切分 + 尾部偷取 | guided 的确定性版本已覆盖 | 不需 |
| **数据导向设计**（经典 DOD） | AoS→SoA、按访问键重排 | 见 4.4 | 框架层 API，GridSearch 需解冻 |

### 4.4 若解冻 GridSearch，三个真实方向（按收益）

1. **`SortedPositions` SoA 化**（X/Y 分离数组）：gather 从 16B/cell 拆成 8B+8B，cache line 承载翻倍 → 20-40%。
2. **按 cell 重排 query**：同 cell 邻域的 query 相邻 → 每 tile 内 9 个 cell 的 gather 高度复用 → tile 内局部性暴增。
3. **稠密 cell 内层 SIMD 特化**：只收 straggler 尾（80~853μs 的稠密 tile），均值收益未知。

以上均属**带宽/布局**优化，不在调度范畴；当前全部被"GridSearch 冻结"挡住。

### 4.5 一句话结论

调度已到收益递减区（还能挤 ~3-5%）；执行 600μs 是内存延迟地板且 MLP 已充分（预取无效）；
托管/GCHandle 是真实成本但分别 <1% 和仅在 chunk 路径。**下一个大杠杆是数据布局（SoA/重排），框架侧能做的
是开放布局 API 并解冻 GridSearch**——在此之前框架侧剩余是 ContextPool（§2.2.1）+ chunk GCHandle 缓存（§3.3），合计 <1-2%。

---

## 5. 修改文件

| 文件 | 状态 |
|---|---|
| `docs/05-托管开销与GCHandle分析及内存局部性.md` | 新建（本文） |
| `src/EntJoy/JobSystem/NativeJobScheduler.cs` | **transpiled chunk fallback 不装箱**（§3.3，`ChunkContextHeader.ownsChunkData` + 三处 fallback 门控 + `ChunkCleanup`） |
| （未动代码） | ContextPool 化 transpiled 绑定（§2.2.1，候选）；chunk GCHandle 跨 schedule 缓存（§3.3，候选） |
