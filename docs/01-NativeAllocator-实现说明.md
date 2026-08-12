# NativeAllocator 实现说明（当前状态）

> 记录 commit `799fe7a` 已实现的分配器改造。配套分析文档见 [02-NativeAllocator-Unity对齐分析与计划](./02-NativeAllocator-Unity对齐分析与计划.md)。
>
> 基线基准：`TestGridSearch`（100k pos + 100k queries，15 worker），PowerShell 运行。

---

## 1. 背景与问题定位

`TestGridSearch` 基准两处方差，均已定位到根因：

| 方差 | 现象 | 根因 |
|------|------|------|
| **Build 方差** | COLD p50 0.57↔0.77、尾宽 p95-p50≈0.32 | `Allocator.Persistent` 是裸 `Marshal.AllocHGlobal` 直通（零缓存），每轮 dispose→realloc 返回地址/物理页漂移 |
| **Query 方差** | QueryCore p50 0.59↔0.70、p95-p50≈0.2 | worker 紧密 schedule→complete 循环里偶发 park→wake（16ms 强制 park 实测 +0.27ms 全量 wake 成本） |

### 1.1 已量化结论

- Query 紧密循环 p50 稳定 0.598（已到 latency 下限），晃的是尾部；keep-warm 实验（见 §4）证明尾部不是 park→wake 延迟主导。
- Query tile 粒度 A/B（见 §5）证明平均/高百分位可优化，但尾部仍是逐帧随机噪声（带宽/系统层）。

---

## 2. Persistent free-list 分配器

### 2.1 设计

`src/EntJoy/Collections/PersistentAllocator.cs`（`public static unsafe class`）：

- **2 幂 size-class**：`SizeToClass(size) = ceil(log2(size))`，`MaxClassIndex=30`（2^30=1GB，超过直通 OS）。
- **块布局**（`HeaderSize=16`，payload 16 字节对齐，与 AllocHGlobal x64 对齐一致）：

  ```
  [0..3]   int classIndex   (0..30 可池化；-1 = 直通 OS；每次分配必写)
  [4..7]   int payloadSize   (payload 字节数)
  [8..15]  pad
  [16..]   payload           ← 返回给调用者
  ```

- **free-list**：`ConcurrentStack<IntPtr>[] _classes`（31 类，懒创建 via `Interlocked.CompareExchange`），每类上限 `MaxPerClass=64` 防内存无限增长。
- **存活表 `s_live`**：`ConcurrentDictionary<IntPtr, byte>` 记录所有本分配器发出的**块基址**（basePtr），用于 Free 时区分本分配器块与外来块。
- **统计**：`Interlocked` 计数 `Allocs/Frees/Hits/Misses/ToOS/Foreign`，暴露 `GetStats()`。

### 2.2 关键路径

```
Alloc(size):
  size<=0 → 1; idx = SizeToClass(size)
  idx>30 (超大) : base=AllocHGlobal(size+16); WriteHeader(base,-1,size); s_live[base]=0; miss++; return base+16
  pool hit      : pop class;                     WriteHeader(ptr,idx,1<<idx); s_live[ptr]=0; hit++;  return ptr+16
  pool miss     : base=AllocHGlobal((1<<idx)+16); WriteHeader(base,idx,1<<idx); s_live[base]=0; miss++; return base+16

Free(payload):
  base = payload-16
  !s_live.TryRemove(base) → 外来块：FreeHGlobal(payload)（LocalAlloc 基址→正确释放；CRT 块→静默泄漏，与改动前一致）; foreign++
  idx<0 → FreeHGlobal(base); toOS++
  类未满 → class.Push(base)；类满 → FreeHGlobal(base); toOS++
```

### 2.3 设计要点

- **线程安全（必须）**：[GridSearch2D.cs:432](src/EntJoySample/05_Algorithms/GridSearch/GridSearch2D.cs#L432) 的 `CellStartEnd.Resize` 在 worker 线程 job 内增长 Persistent NativeList。
- **外来块护栏**：杜绝"对内部指针减 HeaderSize 再释放"导致的堆损坏（STATUS_HEAP_CORRUPTION 0xc0000374）。
- **对齐**：payload 16 字节对齐（当前 Persistent 类型 float2/int2/float/int 均 ≤8，16 对齐充裕）。

---

## 3. 原生 UnsafeList 回调修复（0xc0000374）

### 3.1 根因

原生 `UnsafeList::EnsureCapacity` 直接 `free(Ptr)`。但 Persistent 块是 C# 池化块（payload = base+16），`free(Ptr)` 是**内部指针释放** → 堆损坏 0xc0000374。

### 3.2 方案：托管分配器回调

`src/NativeDll/NativeContainers.h`（手写原生，可安全修改）：

```cpp
using PersistentAllocCallback = void* (*)(int32_t size);
using PersistentFreeCallback  = void  (*)(void* ptr);

// 函数局部静态 → 跨 TU 单实例
inline PersistentAllocCallback& PersistentAllocFn() { static PersistentAllocCallback fn = nullptr; return fn; }
inline PersistentFreeCallback&  PersistentFreeFn()  { static PersistentFreeCallback  fn = nullptr; return fn; }
inline void RegisterPersistentAllocator(PersistentAllocCallback alloc, PersistentFreeCallback free) {
    PersistentAllocFn() = alloc; PersistentFreeFn() = free;
}
```

- `UnsafeList::EnsureCapacity` / `Dispose`：`alloc != nullptr ? alloc(bytes) : malloc(bytes)`；释放 `fre != nullptr ? fre(Ptr) : free(Ptr)`。**回调未注册时回退 malloc/free**（原生独立构建/原生创建列表场景自洽）。
- C# 侧 `NativeJobScheduler.cs` 经 `JobSystem_RegisterPersistentAllocator` 以 `[UnmanagedCallersOnly(CallConvs = CallConvCdecl)]` 包装注册到 `PersistentAllocator.Alloc/Free`。

### 3.3 效果

- native 永不释放内部指针；hitRate **98.8%**、`foreign=0`、`toOS=0`、EXIT 0。
- 顺带消除既有静默泄漏：Windows 跨堆 free（CRT free 进程堆块、LocalFree CRT 块）都会静默失败（~320KB/迭代，原生扩容块）。

---

## 4. worker keep-warm 实验（已还原）

`NativeWorkerPool` WorkerLoop 加 Hot(`_mm_pause` 自旋 50/100/300µs)→Warm(yield)→Cold(park) 自适应等待，epoch 门控 = `outstandingBatches==0 && now-lastSubmit>spinWindow → 提前 park`。

| 数据点 | 结论 |
|--------|------|
| 紧循环（QueryCore） | p50 0.67-0.74 噪声带内无变化；300µs 时 hotSpin=2105（worker 确实不 park）但 p95-p50 仍 ~0.38，比基线 0.34 还宽 → **尾宽不是 park→wake 延迟主导** |
| 16ms 睡眠 | 300µs 回归 p95-p50 0.285→0.430 —— worker 在帧内 job 过渡段自旋，与主线程 Complete assist 争抢 |

**结论**：worker 侧自旋攻击错了杠杆。Query 尾宽真实来源 = per-query 代价方差（网格密度相关 cell 扫描）+ 内存带宽（15 worker 同时打同一批 grid 数组）+ Complete 等待路径。已还原为裸 park，**保留 `parkWakeCount`/`hotSpinHits` 计数器**作诊断（DIAG 行输出）。

---

## 5. Query tile 粒度优化

> ⚠️ **已演进**：本节的 `QueryBatchSize=256` 实验结论已被「tiles/worker 通用化粒度」取代。
> 现状：`QueryBatchSize=0`（回归默认 ResolveChunkSize），粒度由 `NativeJobScheduler.TilesPerWorker=26` 控制，
> 详见 `docs/03-NativeAdapter-Query开销分析与调度优化.md` §2。

### 5.1 改动

- `GridSearch2D.cs`：`public static int QueryBatchSize = 256;`，两个 `SearchClosestPoint` 的 `Schedule` 用它（:152/:183）；`SearchWithin` 与 build jobs 保持默认。
- `TestGridSearch.cs`：`ENTJOY_QUERY_BATCH` env 钩子覆盖 QueryBatchSize；header 与 DIAG 行打印。

### 5.2 A/B 数据（QueryCore，纯 job 查询）

| queryBatch | tiles | p50 | p95 | p99 | max | 尾宽 p95-p50 |
|---|---|---|---|---|---|---|
| 0（基线，~1667 粗 tile） | 60 | 0.651 | 0.808 | 0.861 | 1.164 | 0.157 |
| **256（采纳）** | ~391 | **0.582** | **0.757** | **0.832** | **0.925** | 0.175 |
| 512 | ~196 | 0.630 | 0.865 | 1.041 | 1.087 | 0.235 |

- 正确性：`查询结果前10个` 三组一致（74945 21160 15114 75587 37949 80702 88467 19643 11454 87386）。
- 256 全指标赢家（p50 **-10.6%**，高百分位全降）；尾宽未塌 → 尾部是逐帧随机噪声（带宽/系统层），非静态负载不均。512 更差（原子抢 tile 开销吃收益）。

---

## 6. 基准验证命令与验收指标

### 6.1 运行命令（PowerShell）

```powershell
cd "e:\GODOT\Project\EntJoy"
$env:ENTJOY_PERSISTENT_POOL_STATS="1"; dotnet run -c Release --no-build --project src/EntJoySample/EntJoySample.csproj
```

可用 env：

| 变量 | 作用 |
|------|------|
| `ENTJOY_BENCH_WARMUP` / `ENTJOY_BENCH_FRAMES` | 预热 5 / 采样 100 |
| `ENTJOY_BENCH_SLEEP=1` | 16ms 帧间隙模式 |
| `ENTJOY_QUERY_BATCH` | 覆盖 QueryBatchSize（0=粗 tile） |
| `ENTJOY_PERSISTENT_POOL_STATS=1` | 打印 `PERSISTENT_POOL\|` 分配器统计 |

### 6.2 验收指标

- `PERSISTENT_POOL|`：hitRate >95%、`foreign=0`、`toOS` 小。
- `查询结果前10个`：`74945 21160 15114 75587 37949 80702 88467 19643 11454 87386` 不变。
- `GridSearch-QueryCore` p50 ~0.58、`GridSearch-BuildCore-Cold` p50 ~0.51-0.54（跨运行收敛 ±0.02ms）。
- `DIAG|` 行完整（workerCount、parkWake、queryBatch 等）。

---

## 7. 修改文件清单（commit 799fe7a）

| 文件 | 改动 |
|------|------|
| `src/EntJoy/Collections/PersistentAllocator.cs` | **新增**：free-list 分配器 + `GetStats()` |
| `src/EntJoy/Collections/UnsafeUtility.cs` | `Malloc`/`Free` Persistent 分支接入 |
| `src/NativeDll/NativeContainers.h` | 托管分配器回调 + `UnsafeList` 扩容/释放走回调 |
| `src/NativeDll/Exports.h` / `Exports.cpp` | `JobSystem_RegisterPersistentAllocator` 导出 |
| `src/EntJoy/JobSystem/NativeJobScheduler.cs` | `[UnmanagedCallersOnly]` 包装 + Initialize 注册 |
| `src/NativeDll/NativeWorkerPool.h` / `.cpp` | keep-warm 还原，保留 parkWake/hotSpin 计数 |
| `src/NativeDll/JobSystem.cpp` | `KeepWorkersWarm` 空操作 + 统计读取 |
| `src/EntJoySample/05_Algorithms/GridSearch/GridSearch2D.cs` | `QueryBatchSize=256` |
| `src/EntJoySample/05_Algorithms/GridSearch/TestGridSearch.cs` | `ENTJOY_QUERY_BATCH` 钩子 + DIAG 行 |
