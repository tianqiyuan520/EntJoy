# ManagedJobSystem 依赖链死锁 — 问题记录（2026-08-20）

> **状态：已定位两个根因并修复（探针 60/60 无死锁 + 结果正确）。**
> 落盘前状态：未彻底解决（有概率死锁）。本文档记录完整复现路径、症状、已修复项与根因结论。
> 新增提交（本修复）：见 diff —— `ManagedJobScheduler.cs` / `ManagedJobHandle.cs` / `ManagedMPMCQueue.cs`。
> 早期竞态尝试：`a347ec7`（一次 OnCompleted/Signal 双执行竞态修复，死锁仍概率出现）。

---

## 一、根因总结（本修复定位到的两处，均已消除）

### 根因 A：完成即归还 + Reset 与依赖注册之间的 ABA 丢回调（串行/并发均涉及）

`Signal()` 完成即“自动归还 + `Reset()`”（`Remaining` 0→1、`_done.Reset()`、清 `_onComplete`、`Generation++`）与
`ChainAfter`/`EnqueueAfterGlobal` 的 `dep.IsCompleted` 判断 + `dep.OnCompleted(...)` 之间竞态：

1. A 完成后被重置为 `Remaining=1`，晚到的依赖注册读到 `IsCompleted==false`，把 `Propagate` 挂进 `A._onComplete` 后复查仍为 false → 不派发。
2. 回调永久滞留于“已归还/待重租”槽位 → 依赖链断：B 不入队 → C 不完成 → `h3.Complete()` 永久阻塞。

**修复**：
- 归还时**不再** `Reset()`：完成态（`Remaining=0`、`_done=Set`）在待重租期间保持持久，完整 `Reset()` 推迟到 `RentCompletion` 分配时执行。→ 已完成槽位恒被读为“已完成”，`OnCompleted` 的 `IsCompleted` 复查恒为其派发。
- 依赖挂接 `ChainAfter`/`EnqueueAfterGlobal` 增加**代际守卫**（传入 `dependsOn.Gen`）：槽位 `Generation` 已前进（原依赖完成/过期/被重租）时就地直接 `enqueue()`，**绝不把续体注册到已被复用的对象上**——杜绝跨链 ABA 丢回调。
- `RentCompletion` 在 `Reset()` 前先 `DispatchComplete()`（经 `Interlocked.Exchange` 至多一次）作兜底：把并发注册到回收槽位上的遗留续体派发掉，绝不静默丢弃。

### 根因 B：worker 空闲等待 lost-wakeup（高并发 phase 暴露）

`ParkIdle()` 的 `Monitor.Wait` 无超时且入队在锁外（`Publish` 只做 PulseAll）：若 enqueue+Pulse 落在 worker “spin→Wait” 空窗内，
worker 永久入睡；配合 `CompleteSchedule` 协作预算用尽后线程也进入 `Wait` → “入队了但无人消费”。
诊断特征：卡住 completion `Remaining>0`、`workQueueOccupancy==0`、全部 worker `WaitSleepJoin`。

**修复**：`ParkIdle` 改为**带超时** `Monitor.Wait(_workMonitor, ParkIdleTimeout)`（默认 4ms），
即使 PulseAll 丢失，worker 也在毫秒级自行醒来重查队列，保证入队任务必然被消费。

### 兜底护栏：Native 式周期协助（无墙钟超时）+ 无丢失唤醒 worker park

- `CompleteSchedule` 协作预算耗尽后进入 **Native 式周期协助**（对齐 Native `Complete()` 的 `wait_for` + 依赖链回访）：
  以 `CompleteAssistInterval` 周期苏醒并 `TryExecuteAnyTask` 协助推进链，直到完成。**无墙钟超时**
  → 合法长任务（worker 活跃 / 队列有活 / 等待上游）**绝不误报死锁**；completion 完成时 `_done` 立即唤醒。
- `ParkIdle` 改为**锁内条件变量**：检查与 `Monitor.Wait` 在同一把锁内原子衔接，入队后必有 `Publish`（持同一把锁 `PulseAll`）
  → **无 lost-wakeup 空窗**，去掉 4ms 超时兜底（对齐 Native futex 原子 check+wait，无超时 park）。
- `Remaining` 初始化全部改 `Interlocked.Exchange`，消除"普通写 vs `Signal` 的 `Interlocked.Decrement`"竞态
  （正是周期协助曾暴露的 `Remaining=-5` 过量 Signal 根因；原子化后并发压测不再复现）。
- 保留对象池 + 代际守卫（C# 无 RAII，无法复刻 Native 的 refcount 生命周期节点）。

---

## 二、复现与验证命令

---

## 一、症状

在 **依赖链场景（S3）** 有概率死锁，程序永久卡住无输出。两个基准程序均复现：

1. `samples/EntJoySample/01_JobSystem/SchedulerCompareTest/SchedulerCompareSample.cs`
   - 场景 3：`MeasureChain_Managed` —— Managed 依赖链
2. `tools/JobLibsBenchmark/Program.cs`
   - S3：`MeasureManagedChain` —— Managed 依赖链

典型输出（卡住前最后一行是 S2/S3 之后）：
```
S2 空任务(100万)            Managed=0.612 ...
S3 依赖链(+1→x2→-3)        // ← 有时在这里卡住（概率性，非必然）
```

**关键特征**：
- 只 `Complete()` 末端 handle（h3），中间 handle（h1/h2）不主动 Complete——依赖链中间 completion 由调度器**自动归还**。
- 偶发：同一程序多次运行，有时跑完 S1-S5 全通过，有时卡在 S3。概率约 1/2。
- `SelfCheckManaged` 的依赖链自检（n=1<<20，单次）**必然通过**；只在**长时间/多次测量循环**（Warmup 20 + Measure 100 帧 × 3 job）后暴露。

---

## 二、死锁语义

`MeasureManagedChain` 结构：
```csharp
var h1 = ManagedJobScheduler.Schedule(ref j1, ArrayLength, 0);       // completion A
var h2 = ManagedJobScheduler.Schedule(ref j2, ArrayLength, 0, h1);  // B，ChainAfter(A→B)
var h3 = ManagedJobScheduler.Schedule(ref j3, ArrayLength, 0, h2);  // C，ChainAfter(B→C)
h3.Complete();  // CompleteSchedule(C) → C.Wait() 永久阻塞（若 C 永不完成）
```

死锁发生在 **C 的 Remaining 永远到不了 0**，或 **B 的任务从未入队**（链条断裂），
导致 `h3.Complete()` 的 `_done.Wait()` 无限期阻塞。

---

## 三、已做的修复尝试（提交 `a347ec7`）

### 修复：OnCompleted 与 Signal 的**回调双重执行**竞态

**文件**：`src/EntJoy.Jobs/ManagedJobHandle.cs`

**原问题**：
```csharp
// Old OnCompleted：
internal void OnCompleted(Action callback) {
    ...CAS 把 callback 加入 _onComplete...
    if (IsCompleted) callback();          // ① 已完成则同步调用
}
// Old Signal：
internal void Signal() {
    if (Decrement(Remaining) == 0) {
        _done.Set();
        var c = Volatile.Read(ref _onComplete);
        if (c != null) { Volatile.Write(ref _onComplete, null); c(); }  // ② 也调
        ...
    }
}
```
当 `Signal` 减到 0 后、读/清 `_onComplete` 前，与 `OnCompleted` 的 `IsCompleted` 检查竞态时，
`_onComplete` 回调可能被执行两次。对依赖链，`Propagate`（= `EnqueueSlices`）执行两次 →
`completion.Remaining = n` 被覆盖重设 → 计数错乱 → 可能有 job 分片重复/丢失 → 死锁隐患。

**新实现**：引入 `DispatchComplete()`，用 `Interlocked.Exchange(ref _onComplete, null)` 原子取出，
`OnCompleted` 与 `Signal` 共享之，保证回调**最多派发一次**。
```csharp
private void DispatchComplete() {
    var c = Interlocked.Exchange(ref _onComplete, null);
    if (c != null) c();
}
internal void OnCompleted(Action callback) {
    ...CAS 加入 _onComplete...
    if (IsCompleted) DispatchComplete();
}
internal void Signal() {
    if (Interlocked.Decrement(ref Remaining) == 0) {
        _done.Set();
        DispatchComplete();
        if (_autoReturn==1 && _returned==0) AutoReturnCompletion(this);
    }
}
```

**效果**：此修复正确消除了一次确凿的双执行竞态，但**死锁仍概率出现**——说明还有第二个根因或在别处。

---

## 四、待验证的诊断方案（下一步优先做）

**给等待加超时 + 状态 dump**，让下一次复现直接暴露真凶，而非继续盲修：

1. `ManagedCompletion.Wait()` 增加 `internal bool Wait(TimeSpan timeout)` 重载（用 `_done.Wait(timeoutMs)`）。
2. `CompleteSchedule` 中 `if (!completion.IsCompleted) completion.Wait()` 改为——
   ```csharp
   if (!completion.IsCompleted && !completion.Wait(DiagnosticTimeout))
   {
       DumpDeadlockDiagnostics(completion);   // 输出下面清单
       throw new InvalidOperationException("ManagedJobScheduler 依赖链疑似死锁");
   }
   ```
3. `DumpDeadlockDiagnostics` 输出：
   - 卡住的 completion：`Remaining` / `autoReturn` / `returned` / `Generation` / `SlotIndex`
   - 全部 worker 线程是否 alive（`_workers[i].IsAlive`）
   - 全局队列是否可测量出队（`ManagedMPMCQueue` 增加诊断 `Count` 或尝试 TryDequeue）
   - 各线程栈快照（`StackTrace`）

> 用户将超时设为 **30s** 即可。

---

## Native Chunk 实体平衡基准 —— 基线（优化前，chunk-count tile）

基准工具：`tools/NativeEntityTileBench`（直接 P/Invoke `JobSystem_ScheduleChunkRangeJobEx`，合成 8192 个 ChunkJobData、每 chunk 存活实体数各异，60 帧取均值）。

| profile | totalEntities | avg ms/frame |
|---|---|---|
| balanced（全满1024） | 8,388,608 | 0.425 |
| half（全512） | 4,194,304 | 0.218 |
| random（0..1024） | 4,211,181 | 0.215 |
| **clustered（满块聚前10%）** | 838,656 | **0.134** |
| skewed（满块交错10%） | 839,680 | **0.051** |

**结论信号**：clustered 与 skewed 实体总数几乎相同，但 clustered 慢 2.6× —— 这是按 chunk 数切 tile 时满块聚集导致的负载失衡（少数 worker 拿重 tile、其余空转）。**实体数衡 tile 优化后 clustered 应趋近 skewed（~0.05ms）**，即可作为量化提升的判据。

复现命令：`cd tools\NativeEntityTileBench && dotnet run -c Release`

---

## 五、尚存的候选根因（需要上面诊断确认）

| # | 候选 | 原因依据 |
|---|------|---------|
| 1 | completion **归还 + Reset** 与 `ChainAfter` 的 `IsCompleted` 判定竞态：A 完成后被 `AutoReturnCompletion` Reset（Remaining 1→0），若此时 `Schedule(j2,…,h1)` 才来 `ChainAfter(A,…)`，`A.IsCompleted` 读到被 Reset 后的值 | 依赖链 handle 持有的是 completion 引用，不持有代际保护中的完成态 |
| 2 | `CompleteSchedule` 协作预算（2048 次）耗尽后 `C.Wait()` 期间，**依赖的入队回调用 `Publish()` 唤醒的 worker 被提前 park** 或回调回调错过 | worker `ParkIdle` 先自旋 5000 再 Monitor.Wait；PulseAll 与 Wait 的时序 |
| 3 | `EnumerableSlices` 内 `while(!TryEnqueue){ TryDequeue(out other) ExecuteTask(other); }` 的**队满自执行**在依赖链下误执行「尚未满足依赖」的任务 | 队满时取出执行 `other`，若 other 是依赖链上游，可能重复 Signal / 提前消费 |
| 4 | `SharedRange` 路径与 `StaticSlices` 路径在依赖链中的混合 | S3 用 `innerBatch=0→StaticSlices`；自检用 `8192→SharedRange`，两者都通过但长时间循环暴露异常 |

---

## 六、复现与验证命令

```powershell
# 基准（JobLibsBenchmark，net10.0）
cd tools\JobLibsBenchmark
dotnet run -c Release            # 反复跑，S3 有概率卡

# 调度器对比（EntJoySample）
dotnet run --project samples\EntJoySample\EntJoySample.csproj -c Release
```

> 诊断探针 `tools/MChainDeadlockProbe`（gitignored）已建：循环 200 次依赖链 + 并发压测，
> 用于在修复后快速回归验证死锁是否消除。添加超时 dump 后也可用于抓现场。

---

## 七、参考文件

| 文件 | 说明 |
|------|------|
| `src/EntJoy.Jobs/ManagedJobHandle.cs` | `ManagedCompletion.OnCompleted/Signal/DispatchComplete/Wait` |
| `src/EntJoy.Jobs/ManagedJobScheduler.cs` | `ChainAfter/EnqueueAfterGlobal/CompleteSchedule/ScheduleStaticSlices/ScheduleSharedRange/ParkIdle/WorkerLoop` |
| `src/EntJoy.Jobs/ManagedMPMCQueue.cs` | 全局无锁队列（TryEnqueue 队满时自执行逻辑在此） |
| `tools/JobLibsBenchmark/Program.cs` | `MeasureManagedChain`（复现点） |
| `samples/EntJoySample/01_JobSystem/SchedulerCompareTest/SchedulerCompareSample.cs` | `MeasureChain_Managed`（复现点） |
