# 11 · Unity JobSystem 设计对照 + own-batch（等待者直认）实验结论

> 2026-10-04。本文件回答三件事：
> ① doc10 §5① 那条"`Complete` 等待期让调用线程**执行**而非等待"到底试过没有、结果如何（**试过，判死**）；
> ② Unity 调度器的真实设计（**带官方出处**）与我们差在哪、为什么我们的同类补丁天然脆；
> ③ 下一步该走哪条、判据是什么。
> **同时更正 doc10 §4.2 的一条无效对比**（§4）。
>
> 口径纪律继承 doc10：绝对毫秒/微秒值**跨会话不可比**，只有**同会话、交错、位置平衡**的配对结论可引用。

---

## 0. 一句话结论

| # | 结论 | 证据 |
|---|---|---|
| 1 | **"等待者直认自己批的 tile"这条路走不通**：在"批 storage 池化复用 + 裸 `BatchState*`"的结构下，"票未结算 ⇒ 批不释放"这条不变量**封不住**（迟到结算会落到已回收复用的 storage）。四轮加固后仍：打开档 6 轮里 1 轮进程提前退出、1 轮卡死，并伴随 **118–5000 次** `g_backendBatchesOutstanding` 下溢 | §1 |
| 2 | **默认关、库零影响**：实现已**整体回退**（`src/NativeDll/*` 全部回到 HEAD），唯一保留的改动是测试侧看门狗 | §2 |
| 3 | Unity 的 `Complete()` 能在调用线程上执行 job，**不是因为它做了"等待者优先认领"**，而是因为三条结构前提：**job 在任一时刻只属于一个线程**、**job 数据在 Schedule 时复制进非托管内存**（与调度器生命周期解耦）、**失效检测靠版本号（generation）而非裸指针** | §3 |
| 4 | doc10 §4.2 的"s 提交侧更快（1.72 vs 3.00 µs）"**是无效对比**：拿我们的 **S-only** 比 Unity 的 **S+C 单发地板**（对方含派发+完成） | §4 |
| 5 | 下一步：要动 S+C 的 join 侧，**先加"批/槽位代次"**（顺带修掉一个**既有隐患** `[PENDINGTASKS-WRAP]`），或改走"注入器令牌优先"；否则转攻 doc10 §5② ③ 那两项 | §5 |

---

## 1. 实验：own-batch / direct claim（`ENTJOY_OWN_BATCH_CLAIM`，已回退）

### 1.1 设计（v3）

目标：对齐 Unity `Complete()` 的公开语义——"**prioritizes the job and any of its dependencies to run first in the queue, then attempts to execute the job on the thread which calls the Complete method**"（§3.3）。

| 部件 | 做法 |
|---|---|
| 预留票 | `ChaseLevScheduler::SubmitBatch` 里、**非切片批**上发布：`HandleState` 记 `owningBatch` + `ownBatchTicket`，并 `pendingTasks.fetch_add(1)`（**不动 `activeTasks`**，后者是 worker 的 park 谓词） |
| 等待者认领 | `Complete()` 自旋/阻塞前用一次原子 `exchange(false)` 消费票，然后**自包含地**从该批 `nextTile` 逐 tile 认领并执行（默认预算 `ENTJOY_OWN_BATCH_TILES=64`，0=不限） |
| 票的结算 | 由认领者收尾 `taskDone_` 完成；调用方从不 `Complete` 时，退役侧在 `tilesRemaining==0` 撤销票 |
| 为什么不用 `ExecuteClaimToken` | 那条路内含**切片认领 / F5 融合 / 认领组 TLS 记账**三条有状态路径；等待线程插进去会"认领了却没记账"⇒ `tilesRemaining` 永不归零。故新写一条只碰 `nextTile`、只走 `ChaseLevExecuteTile` 的入口，并把认领组强制置非活动（保存/恢复外层） |
| 只在非切片批发布 | 切片批的 worker 从 `sliceCursors[i]` 认领，`nextTile` 恒 0；给切片批发票只会制造"消费了票但无事可做"的空转 |

### 1.2 四轮加固：四个洞（这部分知识本身有价值，退役路径改动前请读）

| 轮 | 洞 | 现象 | 处理 |
|---|---|---|---|
| 1 | **票可能永不结算**：最后一个令牌**早于**最后一个 tile 结算时，`ChaseLevTaskDone` 的"归零触发"不成立（`fetch_sub` 返回 2 而非 1）⇒ 退役（含撤票）不被调用 | `pendingTasks` 永久停在 1 ⇒ 批永不物理退役 ⇒ `ReleaseBatch` / `g_backendBatchesOutstanding` 残留 ⇒ 之后的 `Shutdown()` / `WaitForBackendBatches` 阻塞 | 在**逻辑完成**单赢家点（`TryCompleteLogicalBatch` 的 `logicalCompleted` CAS 之后）撤票并结算——"`tilesRemaining` 归零"与"该函数被调用"是**同一事件**，必然执行且只执行一次 |
| 2 | **强制退役 UAF**：`ForceFinalizeBatch` / `AbortUnsubmittedBatch`（shutdown）无条件 `ReleaseBatch` | 与正在认领的等待线程构成 use-after-free | 加 `ownBatchBusy`：等待者**先**置 busy **再**消费票；退役方撤票失败（⇒ 等待者已得票）时按 seq_cst 全序必有 busy==true，于是有界等待 busy 落回 false 再释放 |
| 3 | **伪退役在飞批**：补触发只判 `tilesRemaining == 0` | 回收池里的新批在"已构造出 `tileCount`/`handle`、`tilesRemaining` 尚未发布"的窗口内满足该条件 ⇒ 把在飞新批置 `finalized`、`ReleaseBatch` 掉、并做一次**没有对应 +1** 的 `-1` | 追加判据 `logicalCompleted && tileCount != 0`（`logicalCompleted` 只由真正的最后 tile 完成者置位，回收后的新批恒 false） |
| 4 | **不变量本身达不到**：`taskDone_` 用**裸指针**标识批 | 迟到结算把已回收 storage 上 `pendingTasks` 从 0 减成 0xFFFFFFFF（`[PENDINGTASKS-WRAP]` 抓到 1 次实证） | **无法在现有设计内低成本修** ⇒ 判死 |

### 1.3 度量（同二进制；每轮 `JobSystemTests` 约 26s；位置无关，因是"单套件 6~8 轮"重复）

| 配置 | 轮数 | 结果 |
|---|---|---|
| **关闭档**（与打开档同二进制） | **11/11** | 全绿 56/56、~26s、`[OUTSTANDING-UNDERFLOW]` **0** 次、`[PENDINGTASKS-WRAP]` **0** 次 —— ⚠ 两个探针**只存在于带 own-batch 补丁的那次构建**，HEAD 已无该探针（见 12 §4①），故"关闭档 0 次"不可在 HEAD 复现 |
| 打开档（四轮加固后） | 6 | 2 轮干净（25s）；1 轮 **12s / 23 个 PASS 后进程提前退出**（无 FAIL、无 STALL ⇒ 非断言失败、非挂死，判为崩溃）；1 轮 46/56 **卡在 `TestJccLongRunStability`**（看门狗 45s 判死）；其余轮次 **118 / 1117 / 5009 / 5964 / 6876 / 8078 次** `g_backendBatchesOutstanding` 下溢 |

**关键判据（为什么这不是噪声）**：同一二进制、同一协议，**关闭档 0 次下溢、打开档数千次**。

### 1.4 为什么关闭档干净、打开档崩（要记住的机理）

- `g_backendBatchesOutstanding` 是 `WaitForBackendBatches()` 的 `!= 0` 自旋条件。它被多减一次就转负（`0xFFFFFFFF`），此后该自旋只能在"值恰好路过 0"时才退出 ⇒ **间歇挂死**（不是必然，所以历史上表现为"2/8、3/8"这种随机率）。
- **下溢本身是既有隐患**（**推断（未测）：关闭档同样会发生 `pendingTasks` 回绕**，只是没人检查、也没人依据该计数行动 —— 注意这句**不是实测**：`[PENDINGTASKS-WRAP]` 探针只存在于带补丁的构建，HEAD 里原本不存在。
  **2026-10-04 更新**：已按 12 §3.3 把探针重建到 HEAD（`[JOBGEN] staleSettleDropped / pendingTasksWrap`，每次 Shutdown 一行，并加代次校验拒绝迟到结算）⇒ 关闭档 **304×3 次 Shutdown 读数 0/0** ⇒ 该推断目前读作"**机制上可能、实测未发**"（首次迟到结算需要先有先验违反，而 HEAD 的双条件退役正是防它的）；打开档新增了两条"依据 `tilesRemaining==0` 就退役"的触发 ⇒ 把**计数损坏**放大成**伪退役在飞批**（释放别人的 storage）。
- 这正是 §3.5 要讲的那件事：Unity 用**版本号**判定"这次访问/结算属于哪一代"，我们用的是**裸指针**。

---

## 2. 测量方法论（本轮最重要的可复用产出，已落盘）

| 项 | 旧做法（错） | 新做法（已实装） |
|---|---|---|
| 判挂死 | 外部 85s 超时 | 进程内看门狗：45s 无新 PASS ⇒ 打印现场 + `rc=3` |
| 定位卡点 | stdout 尾部猜 | `[WD]` 每秒一行，含 `lastPass` + `g_backendBatchesOutstanding` |
| 判"慢 vs 卡" | 分不开（本套件实为 **26s**，85s 早已远超；跑到 46/56 停住就是**真卡死**） | 直接看 `[t=…ms]` 剖面，无需猜 |
| 成本 | 每次重跑等满超时，靠"重复 N 次看挂几次"补 | 一轮 26s 即可定论 |

- 开关：`ENTJOY_TEST_WATCHDOG=1`（**默认关**；纯测试器械，`src/NativeDll` 零改动）。
- 文件：`tests/NativeDll.Tests/JobSystemTests.cpp`（唯一保留改动：+131 行）。
- 本轮实测耗时剖面（默认档，总 26.1s）：`JccWaveCostVariance` **10.5s**、`TransitiveAssistDrivesDependencyChain` 3.9s、`NestedCompleteResolvesWithoutWorkerExhaustion` 3.9s、`DependentChunkRangeCooperation` 3.7s —— 其余 52 个用例合计约 4s。

---

## 3. Unity 是怎么设计的（带官方出处）

### 3.1 队列拓扑：2022.2 起是"每线程一条本地队列"

- **2017.3 原型**：全局一条无锁 MPMC 队列 + 一个无锁栈（只用于把"依赖未满足的 job"插到队首重试）+ 一个信号量；主线程入全局队列并唤醒 worker。
- **2022.2 重构**：去掉主线程与 worker 之间的共享状态 ⇒ **每线程一条队列 + 每线程一个信号量/futex**。主线程调度时入**主线程自己那条队列**；worker 里再 schedule 入 worker 自己的队列。worker 循环（Unity 博客原文伪代码）：

```
while (!scheduler.isQuitting) {
    Job* pJob = m_worker_queue[m_workerId].dequeue();   // 先看自己的
    if (pJob == nullptr) pJob = StealFromOtherQueues();  // 空了才偷
    if (pJob) { WakeWorkers(); ExecuteJob(pJob); }       // worker 互相唤醒
    else if (ShouldSleep()) m_semaphores[m_workerId].Wait(1);
}
```

- **worker 线程数默认 = 逻辑核数 − 1**（留一核给主线程）；主线程只负责"保证至少一个 worker 醒着"。
- Unity 官方明确：C# Job API 只允许主线程调度，但**原生调度器支持多线程调度（包括 job 里调度 job）**——与 EntJoy 同构，但 EntJoy 是**批内 tile 级**并发认领，Unity 是 **job 级**。

### 3.2 批 + flush：`Schedule()` 不派发

- `Schedule()` 只把 job 放进**本线程的队列**，**不唤醒任何人**；到 `Complete()` / `ScheduleBatchedJobs()` 才 flush 并按需唤醒
  （博客原文："Unity maintains a global batch which is flushed whenever a call to `JobHandle.Complete()` is called"；
  **源码口径更准确**：2022.2 起是"主线程自己的局部队列"——`JobHandle.bindings.cs:78–84`，见 12 §2.2）。
- Unity 给用户的第一条建议就是"尽量晚、尽量少 `Complete()`，用依赖代替显式等待"。
- **对我们的直接含义**：拿"我们的 `s`（只 Schedule）"去比"Unity 的 S+C"是错的；Unity 的 `s`-only 现实中几乎不存在（它的 S 成本被推迟到 C 里一并付清）。

### 3.3 `Complete()` 语义（官方原文）

> "The job system automatically **prioritizes the job and any of its dependencies to run first in the queue**, then **attempts to execute the job on the thread which calls the Complete method**."

即：① 把**这个 job 及其依赖**插队到队首；② 然后在调用线程上**执行它自己那个 job**。

### 3.4 并行拆分粒度

- 文档口径：`IJobParallelFor` 按 `batchSize` **把工作预切成若干个 job**，job 数量由 worker 数、长度、batchSize 决定（简单 job 建议 batch 32–128）。
- EntJoy 是另一种取舍：per-worker Chase-Lev deque + 共享注入器 + 批内 `nextTile` **tile 级原子认领**（可切片 / 可融合 / 可引导式自收缩）。**粒度更细 ⇒ 尾部均衡更好，但"谁在什么时候有权碰这个批"的账本复杂得多**。

### 3.5 生命周期与安全：最该抄的一层

1. **Schedule 时把 job struct `memcpy` 到非托管内存**，"使 C# job 结构体的生命周期与 job 在调度器中的生命周期解耦"（job 可能在依赖图上等很久）。
2. **失效检测靠版本号**：`AtomicSafetyHandle` 存一个 **version number**，每次访问与"关联条目的版本号"比对，不匹配即判失效（官方原文："The version number it stores no longer matches the version number of the associated entry…"）；配套还有 `UseSecondaryVersion` / `DisposeSentinel` / 引用计数。⇒ **这就是"代次（generation）+ 引用计数"**。
   **源码级证据（2026-10-04 补齐，详见 12 §2.5）**：字段 `internal IntPtr versionNode; internal int version;`；版本以 `VersionIncrement = 1<<4` 递增（低 3 位留给 Read/Write/Dispose 标志位）；比对是**掩码后比较** `handle.version != ((*versionPtr) & ReadWriteDisposeCheck)`（`AtomicSafetyHandle.bindings.cs:69–89` / `:413–418` / `:433–440`，递增点 `:230–241`）。官方 manual（Copying NativeContainer structures · Version numbers）："each record contains a version number… `Release()` increments the version number on the central record. After this, the record can be reused"。
   ⇒ **关键性质**：回收方**不等**旧引用，只让它们此后失效 —— 这正是本文件 §1 那条"票未结算 ⇒ 批不释放"的不变量做不到的事。
3. 安全系统**只在 Editor / playmode 开**（`ENABLE_UNITY_COLLECTIONS_CHECKS`），player 里全部编掉 ⇒ 发布零开销。
4. 对 EntJoy 的直接含义：我们上一轮要解决的是"**迟到结算落到已回收 storage**"，正解是加**代次/版本**（判定"这次结算属于哪一代批"）；落点必须在 **`BatchStorage`**、不能放 `BatchState`（`ReleaseBatchStorage` 用 `destroy_at` + placement new 整体重建 `batch`，放进去会在"正好该前进"的那一刻被清零 —— 12 §3.2），**而不是在 `pendingTasks` 里为等待者预留一张票**。

### 3.6 Unity 自己承认的开销剖面（对"我们到底慢在哪"很重要）

Unity 官方博客给的确定性 job 图（500 job、每个 0.5–1 µs）：

> 0.5 µs 的 job 在 **20 个 worker** 时收益已被开销吃平，用满 31 核反而**慢近一倍**；1 µs 的 job 用 31 worker **几乎没有提升**。

全部来自：共享队列/栈上的 CAS 争用 + 唤醒/睡眠的上下文切换 + 忙等。他们的对策就是 §3.1/§3.2 的"本地队列 + 延迟批 + 减少唤醒 + 依赖优先"。

⇒ **我们那个 `sc` 8.72 µs vs Unity 2.43 µs（3.60×）的对比，必须带上两个前提**：① 其中 **8.72 µs 是托管路径**的离机探针
（job 未转译：每 unit 一次托管回调 ＋ 回调内托管逐元素循环 ≈0.4 ns/项，12 §6.9 给出源码位置），**不是**游戏里的转译内核；②Unity 的 2.43 µs 也是在同一类开销结构下拿到的，而且它的调度单位比我们粗（job vs tile），"每次"的工作量并不相同。

### 3.7 对照表：为什么我们的同类补丁天然脆

| 维度 | Unity | EntJoy |
|---|---|---|
| 调度单位 | job（粗），按 batchSize 预切 | tile（细），批内原子游标 |
| 队列 | 每线程一条 FIFO 本地队列 + 窃取 | per-worker Chase-Lev deque + 共享注入器 |
| 派发 | 延迟成批，`Complete` 时 flush | 提交即推令牌入注入器 |
| `Complete` | 优先插队 + **在调用线程上跑它自己那个 job** | 自旋 + 通用协助（扫注入器/偷 deque） |
| **job/批是否可能被多方同时认领** | **否**：job 在任一时刻只属于一个线程 ⇒ "等待者参与"天然安全 | **是**：N 个令牌 + 共享 `nextTile` + 等待者可能插入 ⇒ 批内并发认领 |
| 失效防护 | **版本号句柄 + 引用计数** + job 数据复制解耦 | 池化复用 + **裸指针** + 手写双条件退役 |

**这张表的最后两行就是上一轮失败的根因**：Unity 那条语义对他们是**免费**得到的（结构上不存在"批内多方共享"），对我们则要求凭空造一套生命周期不变量，而我们把"执行入口"也另开了一套（`AssistOwnBatch` 绕开认领组/切片/融合三条有状态路径）——每绕开一条就多一类账目洞。

**出处**：[JobHandle.Complete（Unity 6.2）](https://docs.unity3d.com/6000.2/Documentation/ScriptReference/Unity.Jobs.JobHandle.Complete.html)、[Improving job system performance scaling in 2022.2 – part 2](https://unity.com/blog/engine-platform/improving-job-system-performance-2022-2-part-2)、[Job system overview](https://docs.unity3d.com/6000.2/Documentation/Manual/job-system-overview.html)、[AtomicSafetyHandle.CheckExistsAndThrow](https://docs.unity.com/en-us/engine/6000.5/script-reference/unity/collections/lowlevel/unsafe/atomicsafetyhandle/checkexistsandthrow)、[IJobParallelFor](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Unity.Jobs.IJobParallelFor.html)、[Jobs preferences（安全系统开关）](https://docs.unity3d.com/6000.6/Documentation/Manual/preferences-jobs.html)。
**2026-10-04 更新**：源码已取到（旧路径 `Runtime/Export/Jobs/` 会 404；master 实际在 `Modules/ManagedKernel/Managed/Jobs/`）：
[AtomicSafetyHandle.bindings.cs](https://github.com/Unity-Technologies/UnityCsReference/blob/master/Modules/ManagedKernel/Managed/Jobs/AtomicSafetyHandle.bindings.cs)、
[JobHandle.bindings.cs](https://github.com/Unity-Technologies/UnityCsReference/blob/master/Modules/ManagedKernel/Managed/Jobs/JobHandle.bindings.cs)、
[IJobParallelFor.cs](https://github.com/Unity-Technologies/UnityCsReference/blob/master/Modules/ManagedKernel/Managed/Jobs/IJobParallelFor.cs)；
官方 manual 的 "Version numbers" / "Secondary version numbers" 见 [Copying NativeContainer structures](https://docs.unity3d.com/6000.2/Documentation/Manual/job-system-copy-nativecontainer.html)。
引文与行号见 12 §2/§6（本文件 §3.5.2 的字段级证据来自那里）。

---

## 4. 更正 doc10 §4.2："提交侧 EntJoy 更快"不成立

**原文**（doc10 §4.1/§4.2）：`s` only **1.72 µs**（8 W）"比 Unity 的单发地板 **3.00 µs** 还低"。

**问题**：1.72 µs 是**我们的 S-only**（只 Schedule，不等待）；3.00 µs 是 Unity 的 **S+C 单发地板**（含派发 + 完成 + 连接）。两者**不同形**，不能相减也不能相比。按 §3.2，Unity 现实中根本不存在"只 S 不 C"的形态。

**同形口径下唯一可比的**是 `sc` vs Unity burst：8 W **8.72 vs 2.43 = 3.60×**（doc10 §4.1）——⚠ **这条最终不成立**（两次更正见下）。

> ⚠ **2026-10-04 再更正（12 §5.5）**：这条"成立"只在**离机微基准**口径下成立。**游戏内同形对照**（宿主 `[M-15]` 空 job 探针
> vs Unity `W0_PROBES=4`，同会话、同 8 worker）当时给出 **1.13–1.29×**，而真实波前 34.5 µs/波里框架只占 ~9%
> ⇒ 轴三的"join 侧"分支据此**关闭**。
> ⛔ **2026-10-04 三更正（12 §6.7/§6.9/§6.13，本条最终）**：① 那个 **8.72 µs 来自托管路径** ——
> `tools/SchedSubmitProbe` 的 job **没有转译**：每 unit 一次**托管回调**，回调内**在托管里**跑逐元素循环（实测 ≈0.4 ns/项，源码位置见 12 §6.9），
> 与游戏里的转译内核不是一回事；② 上面那次"游戏内 1.13–1.29×"两侧 (长度, 内批, 次数, **体量**) 仍不同形。
> 两侧都做成**可驱动**后，真同形＝**A 3.66 µs vs Unity 2.67 µs ＝ median 1.369× / min 1.327×**（交错 7 rep、A 侧 250 波×4 窗取 min，12 §6.7）。
> 差距机制＝**每 job 一次跨线程握手**（+0.26 µs（1 块）→ **+1.89 µs（124 块）**；Unity 同一笔只值 +0.59）；
> 杠杆已穷尽（11 个 env ＋ 2 个不在路径的机制 ＋ 1 个结构性原型"试作即坏原生不变量"），剩余 **0.34–0.40 ms/步 < 布局噪声底** ⇒ **本轴结案**（12 §6.12/§6.13）。

**待补的臂**（记为待办）：要谈"提交侧谁快"，需要给 Unity 侧加一个**只 `ScheduleBatchedJobs()` 不 `Complete`** 的臂（`W0_PROBES` 目前没有这一项），或给 EntJoy 侧加"`ScheduleBatchedJobs()` 等价语义"的臂，再同形比。

---

## 5. 下一步（按代价排序）

**2026-10-04 补充（12 §5）**：轴三的**整步敞口已量**：K = **415–516 次 Schedule+Complete/步**，其中 **96% 是 Flow BFS 波前 job**
（宿主"每波一个 job、`Complete()` 即 barrier"，~400 波/步）。同会话逐 rep 配对（默认档 3/3）**A 的波循环每步慢 1.20–1.35 ms**
（每波 +2.8…3.3 µs）⇒ 远超 0.4 ms/步判定线 ⇒ **本表 A/B 两项按"真实产品赤字"处理，且靶子是"每波往返"而不是笼统的 `sc`**。
先做**零代码**的 env 门控扫描（`ENTJOY_SPIN_HOT_US`，读 `[M-12] 波前`），拿不到收益再动代码。

**2026-10-04 追加（12 §5.3–§5.7）**：① 每波相位分解把**"退役尾部"从候选里划掉**（`[E1]` 逐项实测 **1.85 µs/批**；
`lastTileToTopologyDone=7.97 µs` 是 EWMA，被大 job 拉高）；② `ENTJOY_SPIN_HOT_US` 臂**空结论**（1/3 同号）、
粒度臂（波前键内批 8/32/128）**证伪**（tiles 变 15×、波前不动）；③ **同形空 job 地板对照（同会话）关闭了 join 侧分支**：
A 2.83–3.13 µs/波 vs B 2.43–2.50 µs/作业 ⇒ 框架自有敞口仅 0.13–0.28 ms/步
⇒ **本表 A/B 两项不再作为主攻**，改走 doc10 §5②③ + K1′+G（12 §5.10）。

| # | 选项 | 做什么 | 代价 | 验收判据 |
|---|---|---|---|---|
| **A** | **加代次/版本（落点硬约束见 12 §3.2；C 已完成其一半）** | 每次结算/退役都校验"本次结算属于哪一代"；`g_backendBatchesOutstanding` 的加减也带代次校验；并扩到**执行**侧（迟到令牌不得执行 tile）。**代次放 `BatchStorage`（池槽位），不放 `BatchState`**——后者会被 `ReleaseBatchStorage` 整体重建 | 中（动退役路径） | C 已给底座（12 §3.3）：代次 + 拒绝计数 + 探针 + 正向用例；A 剩余 = 执行侧校验 + 退役/计数路径全带代次；关闭档**逐位不变**、开档 `outstanding` 恒 ≥ 0 |
| **B** | **注入器令牌优先** | 不给等待者造新机制：批的 worker 令牌本就以 `RangeTask` 形式走注入器、有成熟所有权规则；给等待线程"优先取本批令牌"的提示。**靶子 = Flow 波循环的每波往返（~400 次/步，barrier 串行）** | 中 | **同会话配对 `[M-12] 波前` ≥3/3 同号下降**（当前赤字 1.20–1.35 ms/步，12 §5.2）且 `[M-1]` Flow/整步不退；`sc` 微基准下降只作辅证 |
| **C** | **先修既有隐患** `[PENDINGTASKS-WRAP]` —— ✅ **已实装（2026-10-04）** | 迟到结算会把已回收 storage 上的 `pendingTasks` 从 0 减坏（**与 own-batch 无关**；"关闭档同样发生"目前是**推断（未测）**——探针原只存在于带补丁的构建，见 12 §4①）。实装内容：`BatchStorage.generation` + 令牌携带代次 + `ChaseLevTaskDone` 校验拒绝 + `[JOBGEN]` 探针 + 正向用例（12 §3.3） | 小 | **已达**：单套件 3/3 轮 81 PASS、304×3 次 Shutdown `[JOBGEN]` 全 0、十套件 4 配置 `rc=0`（套件耗时 24.8–24.9 s；**未与改动前做同会话配对**，故不作性能结论）。**A 仍未做**：本次只守**结算**侧 |
| **D** | 放弃 S+C join 侧，转攻 doc10 §5② ③ | 小 pass 每调用前导（`prefixFinal` / `zero`）、`K1′+G` 消每元素谓词 | 小 | doc10 §5 表内各自判据 |
| **⛔** | "等待者直认 tile"（本文件 §1） | — | — | **已判死，不要再走**（doc10 §6.2 应同步收录） |

---

## 6. 复现命令

```powershell
# 构建（库 + 全部测试目标）
cmake --build build-nativeDll-tests --config Release

# 单套件（约 26s/轮）——判挂死必须带看门狗，不要用外部超时
cd build-nativeDll-tests\Release
$env:ENTJOY_TEST_WATCHDOG='1'          # 默认关；开了才有 [t=..ms] 与 [WD] 行
.\JobSystemTests.exe

# 十套件 × 多轮回归
& tools\gate-run\run-native-tests.ps1
```

## 7. 交接提示词（可直接粘贴到新会话）

```text
仓库：E:\GODOT\Project\EntJoy（Godot + C#/原生混合 ECS，自带原生 JobSystem）。
先读 docs/gridsearch/10-三轴终局对比-默认档对齐档与Job调度.md（当前状态收口）、
docs/gridsearch/11-Unity-JobSystem设计对照与own-batch结论.md（本文件）、
docs/gridsearch/12-Unity版本号机制源码核实与代次句柄设计.md（Unity 版本号机制的源码级证据 + 代次落点 + 探索方向），
机制历史在 docs/gridsearch/09-count几何根因与统一调度.md。

已定的结论（不要再重新走一遍）：
1) "让 Complete 的等待线程直接认领自己那批的 tile"（own-batch/direct claim）已判死：
   在"批 storage 池化复用 + 裸 BatchState*"结构下，"票未结算 ⇒ 批不释放"封不住
   （迟到结算会落到已回收复用的 storage）⇒ 打开档会伪退役在飞批、把
   g_backendBatchesOutstanding 减成负数、间歇挂死/崩溃。实现已整体回退，库零改动。
   细节与四个洞见 doc11 §1。
2) doc10 §5① 的"Complete 等待期让调用线程执行"要改成已判死的记录（doc11 §4/§5）。
3) doc10 §4.2 的"s 提交侧更快（1.72 vs 3.00 µs）"是无效对比（我们的 S-only 对 Unity 的
   S+C 地板），已更正；同形唯一可比的是 sc vs Unity burst：8W 8.72 vs 2.43 = 3.60×。

工作树现状（2026-10-04 更新）：库已改动 —— **C 项（代次校验）已实装**（BatchStorage.generation + 令牌携带
代次 + ChaseLevTaskDone 校验 + [JOBGEN] 探针），测试侧多了正向用例 TestStaleSettlementRejectedByGeneration
（另 +131 行是 ENTJOY_TEST_WATCHDOG=1 的测试看门狗）。验收全绿，见 12 §3.3。

测量纪律（本轮踩过坑，务必遵守）：
- 该套件一轮只有 ~26s。判"挂死"必须用 ENTJOY_TEST_WATCHDOG=1（45s 无 PASS 会打印现场并
  以 rc=3 退出）；不要用外部 85s 超时，那会把"慢"和"卡死"混在一起，逼你反复重跑。
- 绝对 ms/µs 跨会话不可比；只有同会话、交错、位置平衡（A,B,B,A）的配对结论可引用，min 为主统计量。
- 重建 NativeDll/NativeTranspiled 之后必须重推 tools/gate-run/derive-jobkeys.ps1，否则批表 applied=0。

下一步优先级（doc11 §5；2026-10-04 重排）：
0. **量轴三的整步敞口**：敞口 ≈ K × 6.3 µs（K = 每步 Schedule+Complete 次数；既有
   GetStatsSnapshot().batchStorageReturned 就是每 job 一次的退役计数）。≥0.4 ms/步才值得动 join 侧。
1. C：✅ 已完成（代次校验 + 拒绝/回绕探针 + 正向用例；单套件 3/3 轮 81 PASS、十套件 4 配置全绿）。
   **同时澄清**：探针实测关闭档 304×3 次 Shutdown 全 0 ⇒ "关闭档同样会回绕"目前是"机制可能、实测未发"。
2. A（剩余部分）：把代次扩到**执行**侧与全部退役/计数路径（C 只守结算侧）；这是 Unity 用
   AtomicSafetyHandle 版本号解决的问题，落点必须放 BatchStorage。
3. B：改走"注入器令牌优先"（复用既有 RangeTask 生命周期，不造新机制）。
4. D：若敞口小，转攻 doc10 §5② ③（小 pass 每调用前导、K1′+G 消每元素谓词），代价小、判据明确。

我（用户）的偏好：先把 bug 解决干净再回归；一次运行能定论就别重复跑；报告要直接给结论+证据，
不要长时间无监督的循环测试。
```
