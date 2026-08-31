# JobSystem Chase-Lev 重构与安全修复（2026-08-22）

> 日期：2026-08-22
> 提交：`7b9935d`
> 范围：Managed JobSystem Chase-Lev 重构 + Native JobSystem 安全修复与热路径优化 + 对抗性审查落地

---

## 一、Managed JobSystem Chase-Lev 重构

### 1.1 架构变更

旧 MPMC → 标准 Chase-Lev（crossbeam 模型）：

```
Schedule<T>()
  → 预切分为 ManagedTileTask（范围任务）
  → 推入 Injector（ManagedMPMCQueue，跨线程入口）

WorkerLoop[i]:
  1. PopBottom(myDeque[i])              ← LIFO, owner-only, 零竞争
  2. Injector.TryDequeue → PushBottom   ← 从 Injector 拉取推入 deque
  3. StealTop(other deque)              ← 窃取其他 worker 的任务
  4. Park（有界自旋 + SemaphoreSlim）

主线程 assist:
  TryAssistOne → 从 Injector 或 worker deque 窃取并执行
```

### 1.2 新增组件

| 组件 | 文件 | 职责 |
|---|---|---|
| `ManagedTileTask` | `ManagedTileTask.cs` | Chase-Lev 范围任务结构体（对齐 C++ `TileTask` + `RangeTask`）|
| `ManagedWorkStealingDeque` | `ManagedWorkStealingDeque.cs` | 无锁双端队列（SeqCst fence 防 x86 双认领，StealTop CAS 4次重试）|
| `ManagedTileTaskPool` | `ManagedTileTaskPool.cs` | Treiber 无锁空闲栈（替代旧 bump allocator，消除 wrap-around 双占用）|

### 1.3 关键设计决策

| 决策 | 选择 | 原因 |
|---|---|---|
| 池化策略 | Treiber 空闲栈 + 32位 ABA 计数 | 真正回收（对比旧 bump allocator 会 wrap-around 复用未释放 task）|
| 池耗尽兜底 | 池外分配（PoolIndex=-1）| 不阻塞不丢任务（Release 跳过归还，GC 回收引用）|
| Park 哨兵 | 永远检查 injector/deque | 不依赖 activeTasks（避免更新时序竞态）|
| Shutdown 排空 | 协作排空 Injector + deque | 遗留任务不丢失（completion 正确触发）|

---

## 二、Native JobSystem 安全修复

### 2.1 漏洞修复清单

| 漏洞 | 严重度 | 修复 |
|---|---|---|
| RangeTaskPool bump allocator wrap-around 双占用 | 🔴 死锁/UAF | Treiber 空闲栈 + ABA 防护 |
| SubmitBatch Acquire 失败跳过任务 | 🔴 死锁 | 堆分配兜底（poolIndex=UINT32_MAX → delete）|
| Stop() 未排空 Injector | 🔴 死锁 | Worker 协作排空 Injector + deque |
| park 竞态（activeTasks 时序） | 🟠 竞态 | 永远检查 injector/deque |
| ExecuteTileTask 空 Runner 泄漏 | 🟠 池泄漏 | finally 释放回池 |
| StealTop CAS 无重试 | 🟡 性能 | 4 次有限重试 |
| 非 Windows park 用 yield | 🟡 CPU 空转 | 统一 C++20 atomic::wait |
| ISPC 任务上限 exit(1) | 🔴 崩溃 | 128 → 2048（~3300 万任务）|
| Stats 布局无防护 | 🟡 堆损坏 | ValidateStatsLayout 运行时断言 |

### 2.2 循环依赖约束

依赖图必须是无环 DAG。循环依赖导致 Complete() 永久阻塞。运行时不做检测（开销大），已文档化约束（`JobSystem.h` + `ManagedJobScheduler.cs`）。

---

## 三、Native 热路径优化（S5 4.4ms → 3.5ms）

### 3.1 自适应 batch 粒度

旧：固定 `kClaimBatchSize=4` → S5 taskCount=25000（tileCount=100000）。
新：`claimBatch = max(1, (tileCount + wc*16-1) / (wc*16))` → S5 taskCount=195（tileCount=390）。

### 3.2 热路径裁剪

| 裁剪 | 说明 |
|---|---|
| PushTraceEvent 快速守卫 | `g_traceEnabled` 提为命名空间可见，`TryExecuteOneTile` 一次内联 load 跳过 3 次跨 TU call |
| RecordWorkerEntry relaxed 化 | MonotonicNowNs 仅首/末 worker 调用，fetch_add 改 relaxed |
| 删 SetCurrentBatchId(0) | 省 50% C++→C# 跨边界回调（异常绑定在执行期间正确）|

### 3.3 性能结果

| 场景 | Managed Chase-Lev | Native Chase-Lev | 改善前 |
|---|---|---|---|
| S1 分片加法 | 0.226ms | 0.306ms | — |
| S2 空任务 | 0.339ms | 0.386ms | — |
| S3 依赖链 | 0.757ms | 0.926ms | — |
| S4 调度延迟 | 0.000ms | 0.001ms | — |
| S5 高竞争 | 2.955ms | **3.45ms** | 4.4ms（改善 23%）|

---

## 四、跨语言交互与防御性检查

| 检查 | 实现 |
|---|---|
| C#/C++ Stats 布局对齐 | `JobSystem_GetStatsSize()` 导出 + `ValidateStatsLayout()` 运行时断言 |
| 统计结构增字段防护 | 新增字段时若不同步，Initialize 立即抛异常 |

---

## 五、代码整洁性

### 5.1 删除的屎山
- `SubmitBatch` 重复 `pendingTasks` 块（编辑失误）
- 10+ 处「修复前…死锁 105 轮」「对齐 Managed 的修复」**历史修复叙事注释**

### 5.2 保留的关键不变式（维护必需）
- `PopBottom` SeqCst fence 原因（双执行防护）
- 循环依赖 DAG 无环约束（C++ + C#）
- 池耗尽调用方契约（不跳过任务 + 兜底分配）
- 布局防御（`JobSystem_GetStatsSize` + `ValidateStatsLayout`）

---

## 六、验证

- `ChaseLevIntegrationTests` PASS（含 105 轮依赖链）
- `SparseTileDequeTests` PASS
- `JobSystemTests` PASS
- S1-S5 完整 benchmark 全通过，**无死锁**
- S5 Native **3.45ms**（改善前 4.4ms，改善 23%）
