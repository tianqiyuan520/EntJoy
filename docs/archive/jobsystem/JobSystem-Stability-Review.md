# EntJoy JobSystem 稳定性深度审查报告

> 审查日期：2026-07-20
> 审查范围：C# 调度器（NativeJobScheduler.cs）、C++ JobSystem（JobSystem.cpp/h）、NativeWorkerPool、跨语言互操作层
> 审查类型：代码静态分析 + 架构验证

---

## 总论

原分析结论基本正确：**C#/C++ 边界管理严谨，原生 JobSystem 核心稳定，具备生产级可靠性。不存在致命的内存泄漏或死锁风险。**

但本审查在原分析基础上，通过逐行追溯发现了若干原分析遗漏的细节——包括死代码残留、句柄表空洞、ARM 内存屏障隐患等。以下是完整拆解。

---

## 一、C# 端内存管理（GCHandle 与上下文）

### 1.1 GCHandle 配对校验 ✅ 无泄漏

所有 `GCHandle.Alloc` 调用均有对应的 `Free()` 路径，形成完整闭环：

| 句柄类型 | 分配位置 | 释放位置 | 防护机制 |
|----------|---------|----------|---------|
| `WeakTrackResurrection`（per-chunk 全局表） | `ScheduleChunkCore`:1187 | `ChunkCleanup`:2363 | cleanupInProgress 防双放 |
| `Normal`（cache ChunkJobData） | `BuildManagedChunkScheduleCache`:1933 | `RawChunkScheduleCache.TryDisposeNow`:2118 | leaseCount 引用计数 |
| `Normal`（cache lease） | `CreateChunkContextBlock`:2303 | `ChunkCleanup`:2388-2395 | Interlocked.Exchange 防双放 |
| `Normal`（ManagedJobBox） | `AllocManagedContext`:3108 | `ManagedCleanup`:3137 | cleanupByCpp 标志 |
| `Pinned`（TempBuffer） | `TempBuffer.cs`:21 | `TempBuffer.cs`:31 | 显式释放 |

### 1.2 异常路径安全 ✅

`ScheduleChunkCore` 的 catch 块（:1269-1297）区分两种场景：
- **contextBlock 已创建**：调用 `ChunkCleanup(contextBlock)`，走完整释放路径
- **分配未完成**：手动遍历 `chunksPtr[0..chunkCount]`，逐个 `Marshal.FreeHGlobal` 每个 per-chunk 分配，再释放所有预分配 GCHandle

### 1.3 双释放防护 ✅

`ChunkCleanup`:2350：
```csharp
if (Interlocked.CompareExchange(ref header->cleanupInProgress, 1, 0) != 0) return;
```
防止 C++ 端因逻辑异常回调两次 cleanup。

### 1.4 🔴 发现：`_chunkGCHandles` 列表空洞问题

`_chunkGCHandles` 是全局 append-only `List<GCHandle>`。`ChunkCleanup` 释放句柄后仅**修剪尾部连续空条目**（:2365-2367），中间的空洞不清除。

**场景**：
1. Schedule A（chunks 0-4）→ 占用索引 0-4
2. Schedule B（chunks 0-2）→ 占用索引 5-7（A 未完成）
3. A 完成 → 索引 0-4 设为 `default`，但尾部 5-7 仍活跃 → 0-4 成为空洞

**影响**：低。不会导致泄露或正确性错误，但 `List` 内部数组可能保留大量 `default` 条目。可通过在 `ThrowRecordedJobExceptions` 安全点触发压缩来缓解。

---

## 二、C++ JobSystem 核心

### 2.1 无内存泄漏 ✅

三个核心对象池均经过 Shutdown 清空验证：

| 池 | 上限 | 获取路径 | 归还路径 | Shutdown 清理 |
|----|------|---------|----------|--------------|
| `g_statePool` | 4096 | `CreateState`:549-550 | `RecycleState`:538-542 | `JobSystem.cpp`:1988 |
| `g_batchStoragePool` | 256 | `AcquireBatchStorage`:938-954 | `ReleaseBatchStorage`:987-1009 | `ClearBatchStoragePool`:1987 |
| `g_taskflowPool` | 1024 | `AcquireTaskflow` | 归还入池 | `JobSystem.cpp`:1989 |

`BatchDescriptor`（NativeWorkerPool）通过 `freeDescriptors` 向量池化（`NativeWorkerPool.cpp`:85）。

### 2.2 无死锁 ✅

| 机制 | 代码位置 | 防护说明 |
|------|---------|---------|
| Worker 窃取 | `NativeWorkerPool.cpp`:105 | `std::try_to_lock` + 跳过忙碌受害者 |
| LocalPartition CAS | `JobSystem.cpp`:1146-1170 | 打包 64-bit atomic，前端从低 32 位取、窃取从高 32 位取，无冲突 |
| Main 线程辅助 | `JobSystem.cpp`:1753-1772 | 仅执行纯计算 Tile，**不获取** `g_executorMutex` |
| Complete 多级等待 | `JobSystem.cpp`:1741-1806 | Phase 0 辅助 → Phase 1 密集自旋 → Phase 2 yield → Phase 3 `completed.wait` |
| 关闭流程 | `JobSystem.cpp`:1973-1990 | `g_shuttingDown.store(true)` → 停止接收 → join 线程 → 清空池 |

### 2.3 `timeBeginPeriod(1)` ✅ 已确认存在

位于 `Scheduler::Initialize`:1939：
```cpp
::SetPriorityClass(::GetCurrentProcess(), ABOVE_NORMAL_PRIORITY_CLASS);
::timeBeginPeriod(1);
```

这是 Windows 游戏进程的标准做法——将系统计时器精度从默认 ~15.6ms 提升到 1ms，使 semaphore wait/notify 和条件变量超时更灵敏。**无 `timeEndPeriod` 配对**，因为这是进程级别的设定，OS 在进程退出时自动清理。

### 2.4 🔴 发现：ARM 架构下 `HandleStateView` 内存屏障风险

`HandleStateView.Completed`（`NativeJobHandle.cs`:83-88）：
```csharp
private byte _completed;  // 映射 C++ atomic<bool> completed

public bool Completed => Thread.VolatileRead(ref _completed) != 0;
```

- C++ 写入侧：`state->completed.store(true, std::memory_order_release)`
- C# 读取侧：`Thread.VolatileRead`（对应 acquire）

**在 x86/x64 上安全**（x86 的 mov 指令自带 acquire/release 语义）。
**在 ARM 上不被保证**——C++ `atomic<bool>` 的 release 写入与 C# `VolatileRead` 是来自不同编译器的不同屏障原语，ARM 弱内存模型下可能破坏 happens-before。

**严重度**：当前项目聚焦 x64，风险低。若移植到 ARM（Mac Silicon / 服务器），此问题需要修复。

---

## 三、跨语言互操作层对齐

### 3.1 结构体布局 ✅ 完全匹配

| 结构体 | C# 定义 | C++ 定义 | x64 大小 | 匹配状态 |
|--------|---------|----------|---------|---------|
| `EntityBatchData` | `[Sequential]` `NativeJobScheduler.cs`:33-39 | `EntityBatchData.h`:3-8 | 24 字节 | ✅ 完全一致 |
| `ChunkJobData` | `[Sequential]` `NativeJobScheduler.cs`:18-30 | `ChunkJobData.h`:7-18 | 72 字节 | ✅ 字段顺序/类型一致 |

### 3.2 调用约定 ✅ 100% Cdecl

- C# 侧：`delegate* unmanaged[Cdecl]<...>` 和 `[UnmanagedFunctionPointer(CallingConvention.Cdecl)]`
- C++ 侧：`extern "C"` + `__cdecl`/`CALLINGCONVENTION`

### 3.3 边界类型 ✅ 仅 POD + IntPtr

- 从不传递 C++ 标准库类型（`std::function`、`std::vector`）
- `enableBitMaps`：C# 构建 `void**` 数组，C++ 视为不透明指针传递，由回调转交 C# 解释

### 3.4 清理回调注册 ✅

C# 注册 5 个清理委托（`_cleanup`、`_managedCleanup`、`_rawChunkBatchCleanup`、`_chunkCleanup`、`_managedChunkCleanup`），通过 `Marshal.GetFunctionPointerForDelegate` 转为 C 函数指针传给 C++，C++ 在 `TryCompleteLogicalBatch`:1464-1467 中调用。

---

## 四、异常处理路径

### 4.1 异常捕获机制 ✅

所有回调节点（IJob、IJobFor、IJobParallelFor、IJobChunk 等）的 catch 块调用 `RecordJobException(exception)`（:2984-2990），而不是让异常传播到 C++ 侧。

### 4.2 异常重抛 ✅

`ThrowRecordedJobExceptions`（:3002-3021）：
- 单异常 → `ExceptionDispatchInfo.Capture(original).Throw()` 保留原始堆栈
- 多异常 → `AggregateException` 聚合抛出

### 4.3 🔴 发现：`_jobExceptions` ConcurrentQueue 是死代码

```csharp
// NativeJobScheduler.cs:248-249
private static readonly ConcurrentQueue<ExceptionDispatchInfo> _jobExceptions = new();
private static int _recordedJobExceptionCount;
```

这两行只在声明位置出现，没有任何其他代码对其进行读写。实际异常记录使用 `_recordedJobExceptions`（`List<ExceptionDispatchInfo>` + `_exceptionLock`）。这是重构遗留物，应清理。

### 4.4 🔴 发现：异常缺少作业类型元数据

`RecordJobException` 只记录了 `ExceptionDispatchInfo`，但回调节点在编译时已有 `hash`（作业名哈希）。异常被抛出时无法追溯到具体是哪个 Job Type 失败。

建议改为：
```csharp
private static void RecordJobException(ulong jobNameHash, Exception exception)
```
存储 `(ulong JobNameHash, ExceptionDispatchInfo)` 元组。

---

## 五、缓存与引用计数

### 5.1 RawChunkScheduleCache ✅ 安全

`_leaseCount` 原子增减（`:2061-2091`），`RetainLease()` 使用 TOCTOU 双检：
```csharp
Interlocked.Increment(ref _leaseCount);
if (Volatile.Read(ref _disposed) != 0) { ReleaseLease(); throw ...; }
```

`CacheLease.Dispose()` 使用 `Interlocked.Exchange` 防止重复释放。

### 5.2 缓存失效 ✅

`ClearRawChunkScheduleCaches`（`:966-1015`）在 `EntityManager.Dispose` 时调用，遍历字典查找匹配的 `EntityManager`，先 `Dispose()` 再移除。`Dispose()` 设置 `_retired=1`，等 `_leaseCount` 归零时 `TryDisposeNow` 实际释放。

---

## 六、验证完整性检查清单

### 已确认的正面结论

- [x] GCHandle 所有分配/释放路径闭环
- [x] 异常路径不残留句柄或原生内存
- [x] 双释放防护（cleanupInProgress + Interlocked 原子操作）
- [x] C++ 对象池 Shutdown 零泄漏
- [x] Worker 窃取无锁竞争（try_to_lock + LocalPartition 打包原子）
- [x] Main 线程辅助不获取调度器锁
- [x] 跨语言 POD 布局完全对齐
- [x] 调用约定 100% Cdecl
- [x] `timeBeginPeriod(1)` 在 `Scheduler::Initialize` 正确调用

### 发现的问题（r 原分析遗漏）

| # | 问题 | 严重度 | 说明 |
|---|------|--------|------|
| 1 | `_chunkGCHandles` 空洞积累 | 低 | 非泄露，仅 List 容量不缩小，不影响正确性 |
| 2 | `_jobExceptions` + `_recordedJobExceptionCount` 死代码 | 低 | 重构遗留，无任何读写 |
| 3 | `RecordJobException` 缺少作业类型信息 | 中 | AggregateException 无法追溯到具体作业类型 |
| 4 | ARM 下 `HandleStateView.Completed` 内存序不保证 | 中低 | x64 当前安全，ARM 移植时需修复 |

---

## 七、结论

**可以投入生产。** 这套 JobSystem 在架构设计和内存管理上已达到商用级别质量。核心调度路径（Work-Stealing、Partition、Main 线程 Assist）对标 Unity JobSystem 的同级实现，且通过 `delegate* unmanaged` 消除了 P/Invoke 的 GC 挂起开销。

建议在 1.0 发布前解决以下高优先级项：
1. **P0**：清理 `_jobExceptions` 死代码
2. **P1**：增强 `RecordJobException` 加入作业类型哈希便于诊断
3. **P1**：考虑对 `_chunkGCHandles` 增加软上限断言（如超过 10000 时触发警告）
4. **P2**：在 `NativeWorkerPool::Stop` 中增加超时强退看门狗