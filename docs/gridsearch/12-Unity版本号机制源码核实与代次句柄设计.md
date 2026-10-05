# 12 · 用 anysearch 重查：Unity 版本号（代次）句柄的源码级核实 + 对 11 §5A/C 的映射

> ⚠ **2026-10-05 追记**：§ 内的 `tools/gate-run/run-native-tests.ps1` 描述已过时 ——
> F5（`ENTJOY_TILE_RUN`）**已整体删除**（doc13 §5.19）⇒ runner 由"2 趟（off/on）× 10 套件"变为**单趟 × 10 套件**（另可选 `-ForceFine`）。
> 当前门控权威表：`docs/public/Gates-and-Flags.md`。

> 2026-10-04。本轮任务：**重新用 anysearch 查阅资料**，补齐 `11-Unity-JobSystem设计对照与own-batch结论.md` §3.5 里
> "`AtomicSafetyHandle` 的源码页抓取失败，故只采用官方 API 文档口径"这个缺口。
> 本文件是**资料核实记录**：库零改动、测试零改动、不含任何新的性能测量（因此不涉及"绝对 ms 跨会话不可比"那条纪律）。
> ⚠ 后续轮次在本文件上追加了**同会话测量**（§5 K/相位/地板、§6.7 同形跨栈臂、§6.9 托管路径），这些章节遵守"绝对 ms/µs 跨会话不可比、只引用同会话配对"的纪律。
> 所有引文都给到 **文件:行号** 或 **页面 + 小节名**，可逐条点开复核；无法核实的部分在 §2.8 / §4 明确标注。

---

## 0. 一句话结论

| # | 结论 | 证据 |
|---|---|---|
| 1 | Unity 的失效检测 = **中心记录里的版本号 + 每个句柄存副本 + 释放时递增 + 每次访问比对（比对前掩码掉低 3 位标志位）**。此前只有 API 文档口径，本轮由 **C# 参考源码 + 官方 manual** 双向核实 | §2.5 |
| 2 | `Complete()` 的两条语义（先插队、再试图在调用线程上执行）自 **2018.1** 起未变，源码注释与官方文档同句；`CompleteAll` 多一句限定 "if they aren't executing on the worker threads yet" | §2.1 |
| 3 | `Schedule()` 只把 job 放进**本地队列**，`ScheduleBatchedJobs()` / `Complete()` 才 flush 唤醒 ⇒ "全局批（global batch）"是 2022.2 博客口径，**措辞已修正**（结论本身不变） | §2.2 / §4③ |
| 4 | 对 11 §5A 的含义：对应物不是"再造一条更严的不变量"，而是**让迟到的结算被识别并拒绝**。Unity 也不等旧句柄：`Release()` 直接递增版本，旧引用此后自动失效 | §3.1 |
| 5 | 代次字段的落点有一条硬约束：**必须放 `BatchStorage`，不能放 `BatchState`** —— `ReleaseBatchStorage` 对 `storage->batch` 做 `destroy_at` + placement new 整体重建（`JobSystem_Tiles.cpp:557–564`），放进去会在"正好需要代次前进"的那一刻被清零 | §3.2 |
| 6 | doc11 的**三处不一致已完成更正**（用户指示"直接改 docs"）：① §1.4 那句"关闭档同样会发生 `pendingTasks` 回绕"已改标**推断（未测）**（该探针随 own-batch 一起回退、不在 HEAD）；② §3 出处注已换成源码级链接；③ §3.2 的"global batch"已补现代口径"主线程本地队列"。doc10 同步加了指针 | §4 |
| 7 | **探索方向 K 已量**（不用新计数器）：K = **415–516 次 Schedule+Complete/步**，其中 **96% 是同一个键 `00003710` = Flow BFS 波前 job**（宿主"每波一个 job + Complete 即 barrier"，~400 波/步）。同会话逐 rep 配对（默认档 3/3）：A 的波循环**每步慢 1.20–1.35 ms**（每波 +2.8…3.3 µs）⇒ 按判定线（≥0.4 ms/步）**join 侧是真赤字** ⇒ 走 join 侧 | §5 |
| 8 | **C 项已实装并验收**（代次校验 + 回绕/拒绝探针 + 一个正向用例）：单套件 3/3 轮 81 PASS、304×3 次 Shutdown 全部 `[JOBGEN]=0`、十套件 4 配置全绿、同会话耗时持平 | §3.3 |
| 9 | **每波的相位分解（`[E1]`）纠正了 §5.1 的一个判断**：退役链逐项合计只有 **1.85 µs/批**（EWMA 的 7.97/11.04 µs 被大 job 拉高），每波框架相位合计 ~5.5–8.7 µs，而每波总成本 ~35 µs ⇒ 下一杠杆是"每波并发度/入场"（meanConc 6.47/8、busy 0.758），不是退役尾部。零代码的 `ENTJOY_SPIN_HOT_US` 臂为空结论（1/3 同号） | §5.3–§5.4 |
| 10 | **轴三的"join 侧"分支被同形地板对照关闭**（同会话）：A 的游戏内空 job 地板 = **2.83–3.13 µs/波**（格子 1792，1/12 字段；格子=1 时仅 0.57 µs），B（Unity `W0_PROBES=4`）**2.43–2.50 µs/作业** ⇒ 同形比 **1.13–1.29×**（离机那条 3.60× 在游戏里不成立），框架自有敞口 **0.13–0.28 ms/步**。粒度臂（波前键内批 8/32/128）**证伪**（tiles 变 15×、波前不动）⇒ 改走 doc10 §5②③ + K1′+G | §5.5–§5.6 |
| 11 | **§5②③ 收口**（同会话每键实测）：`prefixFinal`/`prefixPartial`/`zero` **每步只有 1 次 Schedule+Complete**，而同形框架地板 0.57–2.8 µs/次 ⇒ 框架固定成本 ≤0.6–2.8 µs/步，与 0.04–0.09 ms/步的赤字差 1–2 个数量级 ⇒ **这三趟的成本在内核/宿主编排，不在框架派发**；唯一"批/步"高的是波循环（398/步，已在 §5.5 关闭） | §6.6 |
| 12 | **真正同形的跨栈臂已建**（§5.5 的口径缺口补上）：Unity 侧新增 `W0_SHAPE` 可驱动臂、A 侧 `[M-15]` 探针加形状旋钮（`_CELLS/_BATCHES/_WAVES/_REPEATS/_EMPTY`）。**现行口径（A 多窗取 min）交错 7 rep**：A 3.66 vs B 2.67 µs = **median 1.369× / min 1.327×**（逐 rep 1.28–1.41；A 侧散布 ±5%）。差距机制（同一次运行内的 21 组合矩阵）＝**第 2 块一次性摊开 +1.2 µs**（Unity 只 +0.59），每块边际两边相同（A 6 ns / B 8 ns）⇒ 折算 **0.34–0.40 ms/步 < 布局噪声底** | §6.7 |
| 13 | **"Unity 会不会也有 assist 这类问题"**：会，定义上就是——官方 `JobHandle.Complete` 原文＝"先把这个 job 插到队首、再**尝试在调用线程上执行它**"；且本机实测 Unity 默认 `JobWorkerCount=15`（逻辑核−1）+ 主线程协助 = 16 线程/8 物理核，与 CSBS 的 15+assist **同一暴露面** ⇒ 真正还差的只有 **own-batch 优先**（我们那条处于回退态） | §6.8 |
| 14 | **"池化 job 盒子 + 缓存 trampoline"是空操作＋错靶**：ctx 早已池化（blittable 走 `AllocContext`→`ContextPool`，与转译路共用同一个池）、trampoline 早已按 T 缓存；`GCHandle` 盒子只有"含托管引用的 job"会走。托管路径的 2× 来自**每 unit 托管回调帧 + 托管逐元素循环**（实测 ≈0.4 ns/项），池化盒子收不回来；真要池化则引入"复用错盒/双重 Free/声明提前释放"四类风险 | §6.9 |
| 15 | ⛔ **否证**：assist 的整步效应在 **8W 与 15W 都没挺过 5 rep**（8W：中位 +0.38、2/5 更快；15W：中位 +1.20、2/5 更快；极差 ±8…±12 ms）⇒ "15W 开 assist 更慢 +2.2 ms" 与 "8W 快 1.3 ms" 都是 **3 rep 伪影**；真效应只在 **Flow ≈ −0.2…−0.6 ms/步** ⇒ **不据此改线程预算/默认策略** | §6.10 |
| 16 | ⛔ **自我更正**：上一版记的 `(cells=128,batch=512)` 19.2 µs / "24× 病态 / JCC 形状切换"**不成立**。代码侧显式 `batch>0 ⇒ rc=1 ⇒ ScheduleFastPath`（tiling/JCC 不参与），且同形状换窗长即回正常（250 波 0.80 / 4000 波 0.76）⇒ 真身是**一次 ~15–19 ms 偶发停顿落进单窗**。器械已修（`_REPEATS` 多窗取 min，CSBS `742ee14`）；修复后该格 0.77 µs。教训：单窗 ≥1000 波的形状矩阵不耐停顿，"某形状慢 24×"先怀疑器械 | §6.7 |
| 17 | **优化第一个杠杆**：分相器械（`_SPLIT=1`）显示"提交+等待"（流水线口径）比交错往返低 **+0.26 µs（1 块）→ +1.89 µs（124 块）** ⇒ 差额＝**每 job 一次跨线程握手**（与 `JobSystem_Scheduler.cpp:279` 的历史注释同源、也正是 Unity 用"调用线程执行"消掉的那段）；现成器械 `ENTJOY_SCHED_PRIO` 5 rep 配对差 ≤0.04 µs＝**空结论**；下一杠杆＝给 `rc<=1` 且无未完成依赖加**调用线程内联执行**（风险清单见 §6.11） | §6.11 |
| 18 | **廉价杠杆已穷尽**：`SCHED_PRIO`／`COMPLETE_SPIN`（2 块 4/4 −0.32 但波前量级反退化 ⇒ 不采用）／`WAKE_POLL(+NEED)`／`SPIN_HOT_US`／`SPIN_NEEDS_WORK`／`PHYSCAP_SMALLJOB`／`CLAIM_SLICE`／`CLAIM_GUIDED`／`DEFER_WAKE` 全部中性或更差（共 11 个 env）；F2/F4 因 `cs≤16` 门、JCC 因显式 batch **根本不在路径**。**唯一能动往返的是 worker 池规模**（16 块：2 worker 1.74 → 8 worker 2.60 µs），但那是**空体**性质、"限参与"对真 job 净亏（且 `ApplyPhysCoreCapForSmallJob` 已实现该政策）。⇒ 只剩两条结构性路径（调用线程执行 ／ 减少 job 数），天花板 0.34–0.40 ms/步 < 噪声底 ⇒ **不实装**，需使用者确认 | §6.12 |
| 19 | ⛔ **结构性①已试作原型＝破坏不变量**：`ENTJOY_INLINE_OWN=1`（`FastPath` 改为调用线程内联执行）使 `JobSystemTests` **rc=1 / 13 s 早退**（`executed=7`，收尾 `FAIL system broken after worker-thread shutdown attempt`）；加主线程守卫后 `skipped=0` 仍红 ⇒ 根因是**绕过后端退役记账**（lambda 只做 `CompleteState`，退役/引用平衡在 `SubmitBackendAsync` 包装层）。**原型已回退**（工作树零改动，基线 rc=0）。⇒ **轴三派发侧结案**：1.33× / 0.84–0.99 µs/job，机制＝每 job 一次跨线程握手，11 env + 2 不在路径 + 1 结构性原型全出结论，剩余 0.34–0.40 ms/步 < 噪声底 ⇒ 不实装，需明确授权 | §6.13 |
| 20 | **代码清理（用户指示"清理代码"）**：**6 个死字段**（`directAssistClaims` / `exhaustedTickets` / `scheduleToPublishEwmaNs` / `publishToFirstMainClaimEwmaNs` / `publishToFirstWorkerClaimEwmaNs` / `queueLockWaitEwmaNs`；全仓只有赋 0、无自增）**只加标注不删** —— 它们是 **C ABI** 且被 Unity 侧 port 镜像同一布局，删需两侧同步；删掉 `run-native-tests.ps1` 里 own-batch 的**两趟失效回归**（env 已无人读 ⇒ 永久全绿、零覆盖）并复跑全绿；`SchedSubmitProbe` 的 stale 注释改正。**`src/NativeDll` 行为改动 = 0** | §6.14 |
| 21 | **托管↔原生边界不是 P/Invoke**（本机 `UnityEngine.CoreModule.dll` 6000.3.2f1 的 **IL 级**核实）：`JobHandle`/`JobsUtility` 全是 `[NativeMethod]`/`[FreeFunction]` 的 **icall**（Mono icall 表 / IL2CPP 直链），整个 Jobs 命名空间 `pinvokeimpl`/`extern` 命中 **0** 条；`Schedule` 那层 `_Injected` 是 Mono"按值返回 struct"的约定，与返回 `IntPtr` 的我们无关。**逐条读完后：能搬的都已在我们仓里**（缓存函数指针直呼 / 静态泛型元数据缓存 / 认领摊薄 `step=clamp(tileCount/workers,1,capEff)`），剩下两条（`CompleteAll`、`DeferArraySize`）是"我们的调用形状用不到" ⇒ **派发侧无动作**；`JobHandle` 布局核到 = `{uint64 jobGroup; int32 version; int32 debugVersion; IntPtr debugInfo}`（24 B），且 `Complete()` 托管侧唯一比较点是 `jobGroup==0` ⇒ 版本比对确在 native（§2.8 行 3 结案） | §6.15 |
| 22 | **内核体两条预注册项都已出结论**（2026-10-04）：① **G（守卫折叠）的每元素收益 = 无可测收益** —— 同源两构建 + 位置平衡配对，Count/Place 环比 1.027/1.023、符号 1/3 与 1/3，而 G 碰不到的 ClearAll/Integrate 同批漂移 **+38% / −6.8%** ⇒ 判据③未通过（保留为等价变换，不记为优化；"0.03–0.15 ms/步"作废）；② **§54.5 门槛第一次执行 = 未通过** —— 夹具新增 `BenchTwoPassJob`（两遍切分降 live-set，校验和逐位相同），**3/3 复跑环比 1.03/1.06/1.16 ⇒ "降活跃值/循环分块"这条轴关闭**。⇒ **内核体的框架侧杠杆至此全部出结论**；剩余差距仍在宿主内核的数据布局/访问形态（§54.5/§54.6，口径不放宽） | doc09 §56 / §57 |
| 23 | **doc09 §51.3 那条"唯一已知异常但机理未定"已结案**（2026-10-04）："默认档 4 037 个批落进薄 tile 门"的**机理 = 门与 JCC 硬编码下限撞值** —— `kClaimSpanThinElems = 16` 恰好等于 `ResolveChunkSize` 五处 `std::max(16,…)` 的下限 ⇒ `cs <= 16` 实际是"**`cs` 撞在下限上**"，与 tile 厚薄/内核身份无关。撞它的最短路 = 空体/短 body 被分类成 **mem-bound** ⇒ `tpwChunk = max(16, ceil(len/(W·tpw)))` ⇒ **`len ≤ 16·W·tpw`（默认 8192）时 `cs` 恒 16**（实测 6/6 rep `thin`≈100%，`MEM-BOUND → tpw chunk` 打印条数 ≈ 决策数）。⚠ 且"进不进该 regime"**随进程翻**（同一 `len=1024`：会话内既见 6/6 恒薄、也见 2/3 不薄）⇒ 该门既不是薄厚判据、也不是 `length` 的稳定函数；原回归判据"薄 tile 占比骤降为 0 即回归"**作废**。⚠ 本轮**先犯过一次错并自查更正**：上一版据"`--no-build` + `bin/` 里旧 `NativeDll.dll`"的扫描写过"塌点 512/128"，改用当前二进制后不成立（§58.2b / §58.2c）。⚠ `kClaimSpanThinElems` 同时被 F1（认领跨度）用 ⇒ 改它会同时移动两条轴的阈值 | doc09 §58 |
| 24 | ✅ **现象与缓解均已用等负载对照证实（经独立复查修正后重做）**：CSBS 默认档"同一个 job（同键、同 `length`）拿到 34–45× 不同的 `cs`"**已在原生侧复现**（同一二进制同一负载，键 `000053e0` 的 `tiles` 呈**双带** ~68–71k / ~765–768k，8 rep 摆 **11.2×**；更早 16 rep 抽样见过 39.90×/40.75×）。**缓解已证**：先修掉 `JCC_ROBUST=1` 让 `JobSystemTests` 47/57 失败的真缺陷（用例只读细通道，而 mem-bound 档只写粗通道）⇒ 两臂现均 57/57；再在**两臂同完整负载**下对照：默认 **11.2×** vs `JCC_ROBUST=1` **1.0001×**。**机理**（逐批重判 ⇒ 双稳）方向一致但未逐行证明。**实践含义**：默认档按趟 A/B 应先钉住 `cs`；`JCC_ROBUST` **提默认已按纪律验收 ⇒ 不提**（理由 = 性能中性无净收益：median 口径 +0.054/2-6 与 min 口径 −0.081/4-6 符号相反，均未达"逐对同号"） | doc09 §59.2b / doc13 §5.8·§5.9b |

---

## 1. 器械与例外记录（方法）

### 1.1 anysearch 用法与本轮踩到的失败面

| 站点 | `anysearch extract` 结果 |
|---|---|
| `unity.com/blog/...`（2022.2 part 2） | ✅ 可取全文 |
| `github.com`（blob 页） | ❌ `API Error: Unable to extract content from the URL. (request_id: …)` |
| `raw.githubusercontent.com` | ❌ 同上 |
| `cdn.jsdelivr.net`（GitHub 镜像） | ❌ 同上 |
| `docs.unity3d.com`（手册页） | ❌ 同上 |

**替代路径（本轮实际走的）**：① anysearch 检索定位文件/页面 → ② GitHub REST（`/git/trees/master?recursive=1`、`/contents/...`）定位**真实路径**
→ ③ 直连 raw 取原文（`python requests`，取的是**同一份官方文件**）。
⇒ 出处可信度不变（引文可点开 GitHub 原文逐行对照），但要如实记录：**extract 对 github / docs.unity3d.com 这两类站点当前不可用**，别在下轮把它当"没查过"。

> **追记（§6.15 那轮，仍失败）**：`github.com` / `raw.githubusercontent.com` / `cdn.jsdelivr.net` / `docs.unity3d.com` 的 `extract` **全部返回同一个 `API Error: Unable to extract content from the URL.`**，
> 连 `api.github.com` 的 JSON 也一样；本机 `web_fetch` 直连则 `fetch failed`（出网只剩 anysearch 的**检索** API）。
> ⇒ 本轮换成**完全离线的渠道**并成为 §6.15 的证据源：本机 Unity 二进制 IL + `PackageCache` 里的 DOTS 包源码（同版本、命令见 §7）。
> 检索 API 本身可用，且 `code.snippet` 垂直域能返回**代码片段**（用它确认了 `Runtime/Jobs/Managed/IJobParallelForTransform.cs:104` 那类行的真实内容）。

### 1.2 本轮取到的官方文件（不复用旧结论的那部分）

| 文件 | 用途 |
|---|---|
| `Modules/ManagedKernel/Managed/Jobs/AtomicSafetyHandle.bindings.cs` | 版本号机制的源码（字段/常量/比对/递增） |
| `Modules/ManagedKernel/Managed/Jobs/JobHandle.bindings.cs` | `Complete` 语义、`ScheduleBatchedJobs` 语义、绑定名 |
| `Modules/ManagedKernel/Managed/Jobs/IJobParallelFor.cs` | `batchSize` 口径（11 §3.4） |
| `docs.unity3d.com/6000.2/Manual/job-system-copy-nativecontainer.html` | 官方 manual 的 "Version numbers" / "Secondary version numbers" 两节 |

> 注意：这三个 `.cs` 在 master 上的路径是 `Modules/ManagedKernel/Managed/Jobs/`（**不是** 旧版的 `Runtime/Export/Jobs/`）——
> 按旧路径取会 404。这也是本轮"抓取失败"的另一半原因。
> （检索索引里还能搜到更老的 `Runtime/Jobs/ScriptBindings/JobHandle.bindings.cs` ⇒ 该索引不总是 master，别用它当路径依据。）

> **追记（§6.15 的证据源，与本表互补）**：GitHub 侧只有**托管半边**，而"边界机制 / 句柄布局 / 认领结构"这三问公开仓答不了（原生在 `Runtime/Jobs/JobSystem.h`，不在仓里）。
> 本轮改用**本机同版本二进制**（`UnityEngine.CoreModule.dll`，6000.3.2f1）+ **本工程 `PackageCache` 的 `com.unity.entities` 源码**——
> 后者正好补上 `Modules/.../Jobs/` 里没有的 DOTS chunk 派发层。命令见 §7，结论见 §6.15。

---

## 2. 逐条核实（doc11 §3 的主张 → 来源 → 原文）

### 2.1 `Complete()` 语义（doc11 §3.3）

源码注释（[JobHandle.bindings.cs:23–34](https://github.com/Unity-Technologies/UnityCsReference/blob/master/Modules/ManagedKernel/Managed/Jobs/JobHandle.bindings.cs#L23-L34)）：

> "The job system automatically prioritizes the job and any of its dependencies to run first in the queue, then attempts to execute the job on the thread which calls the Complete method."

`Complete()` 实现：`jobGroup == 0` 直接返回，否则调 native `ScheduleBatchedJobsAndComplete`（:28–34, :86–87）。
`CompleteAll` 的措辞多一句限定（:36–38）："…then attempts to execute all the jobs **if they aren't executing on the worker threads yet**."
官方文档同句：[6000.3 Complete](https://docs.unity.com/en-us/engine/6000.3/script-reference/unity/jobs/jobhandle/complete)；
最老可查版本 [2018.1 Complete](https://docs.unity3d.com/2018.1/Documentation/ScriptReference/Unity.Jobs.JobHandle.Complete.html) 已是同句 ⇒ **语义至少 2018.1 起未变**。

⇒ doc11 §3.3 引用正确；补一条：Unity 自己的措辞是 "**attempts to** execute"，即"尽力在调用线程上跑"，而不是"必然在调用线程上跑"。

### 2.2 `Schedule` 入队 / 批 / flush（doc11 §3.2）

源码注释（[JobHandle.bindings.cs:78–84](https://github.com/Unity-Technologies/UnityCsReference/blob/master/Modules/ManagedKernel/Managed/Jobs/JobHandle.bindings.cs#L78-L84)）：

> "By default, jobs are only put on a **local queue** when using job Schedule methods. `ScheduleBatchedJobs` makes them available to the worker threads to execute. The job system intentionally delays job execution until you call `ScheduleBatchedJobs` manually because the cost of waking up worker threads can be expensive…"

博客（即 doc11 出处）：「Unity maintains a **global batch** which is flushed whenever a call to `JobHandle.Complete()` is called.」

⇒ **修正措辞**：2022.2 起批次簿记落在**主线程自己的局部队列**（§2.3 的每线程队列），"global batch" 是博客的通俗说法。
⇒ **结论不变**：Unity 现实里不存在"只 S 不 C"的形态，所以"我们 S-only 1.72 µs vs Unity 3.00 µs"那条对比依旧无效（doc11 §4）。

### 2.3 每线程队列 / worker 循环 / worker 数（doc11 §3.1）

博客原文（2022.2 part 2，均已核）：
- "…the main difference is the removal of the shared state between the main thread and worker threads. Instead, we make the queues and semaphores (or futex…) local to each worker thread. Now, when the main thread schedules a job, it's enqueued into the main thread's queue rather than a global queue."
- worker 循环伪代码：先取自己队列 → 空则 `StealFromOtherQueues()` → 有活则 `WakeWorkers(); ExecuteJob(pJob);` → 否则 `ShouldSleep()` 后 `Wait`。
- "The job scheduler creates as many worker threads as there are virtual cores on the CPU, minus one by default."
- "Worker threads are now responsible for waking up other worker threads, and the main thread is responsible for ensuring that at least one worker thread is awake when it schedules a job."

⇒ 与 doc11 §3.1 一致（博客即出处，无需改动）。

### 2.4 `Schedule` 时把 job 数据复制进非托管内存（doc11 §3.5.1）

博客原文：

> "When scheduling a job, the C# job binding layer copies the job struct into an unmanaged memory allocation. This allows the lifetime of the C# job struct to be **disconnected from the job lifetime in the job system**, since this is affected by the job's dependencies and overall load on the platform. The job system then conditionally performs safety checks in **Editor playmode builds** to ensure a job is safe to run."

旁证（[IJobParallelFor.cs:148](https://github.com/Unity-Technologies/UnityCsReference/blob/master/Modules/ManagedKernel/Managed/Jobs/IJobParallelFor.cs#L148)）："Note that **worker threads always operate on a local copy of the job struct**."

⇒ 与 doc11 §3.5.1 一致。

### 2.5 失效检测靠版本号（doc11 §3.5.2）—— 本轮补齐源码级证据

**(a) 字段与常量**（[AtomicSafetyHandle.bindings.cs:69–89](https://github.com/Unity-Technologies/UnityCsReference/blob/master/Modules/ManagedKernel/Managed/Jobs/AtomicSafetyHandle.bindings.cs#L69-L89)）：

```csharp
internal const int Read = 1 << 0; Write = 1 << 1; Dispose = 1 << 2;   // 低 3 位 = 访问标志
internal const int TempVersion = 1 << 3;
internal const int VersionIncrement = 1 << 4;                          // 版本以 16 递增（让开标志位）
internal const int ReadCheck  = ~(Write | Dispose);
internal const int ReadWriteDisposeCheck = ~(Read | Write | Dispose);  // 比对时掩掉标志位
internal IntPtr versionNode;   // 指向"中心记录"（裸指针）
internal int    version;       // 本句柄持有的版本副本
```

**(b) 比对点**（同一表达式的两处，差别只在"抛异常 vs 返回 false"）：
[`CheckExistsAndThrow` :413–418](https://github.com/Unity-Technologies/UnityCsReference/blob/master/Modules/ManagedKernel/Managed/Jobs/AtomicSafetyHandle.bindings.cs#L413-L418)、
[`IsHandleValid` :433–440](https://github.com/Unity-Technologies/UnityCsReference/blob/master/Modules/ManagedKernel/Managed/Jobs/AtomicSafetyHandle.bindings.cs#L433-L440)：

```csharp
var versionPtr = (int*)handle.versionNode;
if (handle.version != ((*versionPtr) & ReadWriteDisposeCheck))   // 掩码后比较
    throw new ObjectDisposedException("The NativeArray has been disposed, it is not allowed to access it");
```

**(c) 递增点**（`CheckWriteAndBumpSecondaryVersion`，:230–241）：`ptr[1] = ptr[1] + VersionIncrement;`
⇒ 中心记录是 **int 数组**：`ptr[0]` = 主版本|标志，`ptr[1]` = 二级版本（`UseSecondaryVersion`/`SetBumpSecondaryVersionOnScheduleWrite`，:155–180）。

**(d) 失效条件**（源码注释 :399–404，即 `CheckExistsAndThrow` 的官方口径）：版本号不匹配 / 别处调用了 `Release()`（引用同一内存区）/ 二级版本不匹配。

**(e) 官方 manual**（[Copying NativeContainer structures · Version numbers](https://docs.unity3d.com/6000.2/Documentation/Manual/job-system-copy-nativecontainer.html)）：

> "Instead, each record contains a version number. A copy of the version number is stored inside each `AtomicSafetyHandle` that references that record. When a NativeContainer is disposed of, Unity calls `Release()`, **which increments the version number on the central record**. After this, **the record can be reused** for other NativeContainer instances."
> "Each remaining `AtomicSafetyHandle` compares its stored version number against the version number in the central record to test whether the NativeContainer has been disposed of."

⇒ **Unity 版"代次句柄"的四件套**：中心记录 / 句柄留副本 / 回收时递增 / 访问时比对（掩码掉标志位）。
⇒ 关键性质：**回收方不需要等旧引用**——`Release()` 不等，旧引用此后自己失效。这正是 11 §1 那条"票未结算 ⇒ 批不释放"不变量想做却做不到的事。

### 2.6 安全系统只在 Editor / playmode（doc11 §3.5.3）

博客原文（§2.4 引文后半句）："…conditionally performs safety checks in **Editor playmode builds**…"。
源码侧：`CheckExistsAndThrow` / `CheckWriteAndBumpSecondaryVersion` 等带 `[Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]`（:228、:411），发布档编译期整段消失。

⇒ 与 doc11 §3.5.3 一致。**对我们的额外含义**：Unity 把这一层的代价放在**编辑期/冷路径**；我们的代次比对也应只放在**结算路径**（token 结算、退役），绝不放 tile 热路径。

### 2.7 并行拆分粒度 `batchSize`（doc11 §3.4）

[IJobParallelFor.cs:27–29](https://github.com/Unity-Technologies/UnityCsReference/blob/master/Modules/ManagedKernel/Managed/Jobs/IJobParallelFor.cs#L27-L29) 原文：

> "Unity automatically splits the work into chunks of no less than the provided `batchSize`, and schedules an appropriate number of jobs based on the number of worker threads, the length of the array and the batch size… A simple job, for example adding a couple of Vector3 to each other should have a batch size of 32 to 128."

另有 :121 `innerloopBatchCount`："the number of iterations which workstealing is performed over"。
⇒ 与 doc11 §3.4 一致（我们 tile 级原子认领是另一种取舍：粒度更细、账本更复杂）。

### 2.8 不可核实项（诚实清单，别当成已证）

1. **原生内部**：`[NativeHeader("ManagedKernel/Jobs/AtomicSafetyHandle.h")]`（:67）只暴露头名，头文件不在公开仓 ⇒ "`Release()` 具体在哪里递增版本"只在 native。
2. **`Complete` 在调用线程上执行 job 的实现**在 native（C# 侧只有绑定 :86–87 `ScheduleBatchedScriptingJobsAndComplete`）。
3. ~~`JobHandle` 自己也有 `version` / `debugVersion` / `debugInfo`（:16–21），但 C# 侧**没有任何比较点** ⇒ 由 native 校验（本文件不作结论）。~~
   **已结案（§6.15，本机 6000.3.2f1 `UnityEngine.CoreModule.dll` IL 4313–4316 / 4324–4337）**：
   布局 = `{uint64 jobGroup; int32 version; int32 debugVersion; IntPtr debugInfo}`（24 B，x64）；
   `Complete()` 的**唯一**托管比较点是 `jobGroup == 0`（为真直接返回），**没有任何 `version` 比对** ⇒ 失效判定确实在 native。
4. "prioritizes … first in the queue" 在 2022.2 之后的实现：博客给出的 worker 循环里已**看不到** 2017.3 的那个"插入队首重试"的无锁栈（2017.3 伪代码有 `m_stack.pop()`）⇒ 优先级机制现状未核实。
5. doc11 §3.6 的 Unity 侧数据（2.43/4.56 µs、0.5 µs×20 worker 等）：博客原文可核（"For jobs that take 0.5μs, once there are 20 workers, the frame updates as fast as not using the job system at all"），但那是**别人的机器**，仍不可与本机数字混用。

---

## 3. 映射到 EntJoy（HEAD，库零改动）

### 3.1 对照：我们缺的不是"更严的纪律"，是一个"身份"

| | Unity | EntJoy（HEAD） |
|---|---|---|
| 被标识对象 | 中心记录（`versionNode` 指向；可复用） | `BatchState`（`BatchStorage` 的第一个成员；池化复用） |
| 句柄/副本 | `AtomicSafetyHandle{ versionNode, version }` | `RangeTask{ BatchState* batch }`（`RangeTaskPool.h:28`）、`TaskDoneFn = void(*)(BatchState*)`（`ChaseLevScheduler.h:49`） |
| 回收点 | `Release()` → 中心记录版本 **+16** | `ReleaseBatch`→`ReleaseBatchStorage`（`JobSystem_Tiles.cpp:807` / `:557`），storage 回 per-thread 缓存/共享池 |
| 迟到访问 | 每次 `Check*` 比对版本 ⇒ 旧句柄 `IsHandleValid == false` | **无**：`ChaseLevTaskDone(batch)` 直接 `pendingTasks.fetch_sub(1)`（`JobSystem_Tiles.cpp:1812–1817`） |
| 所依赖的性质 | **任何旧引用自动失效**（单侧，回收方无需配合） | **票未结算 ⇒ 批不释放**（多侧，要求所有持有者配合） |

`pendingTasks` 的损坏点很具体：`SubmitBatch` 里是 **`store(tokenCount)`**（`JobSystem_Scheduler.cpp:618`），即"这一代批"的计数由 store 一次性写定；
一个上一代的迟到 `fetch_sub` 打进来的就是刚写好的新值 ⇒ 与 11 §1.2 洞 4（`[PENDINGTASKS-WRAP]`）完全对应。

### 3.2 落点：代次必须放 `BatchStorage`，不能放 `BatchState`

- `ReleaseBatchStorage`（`JobSystem_Tiles.cpp:557–564`）对批状态做的是
  `std::destroy_at(&storage->batch); new (&storage->batch) BatchState();` —— **整体重建**。
  把 `generation` 放进 `BatchState`，会在"归还池、正好该让代次前进"的那一刻被清零 ⇒ 旧句柄与新批**恒等**，失效检测失效。
- `BatchStorage` 只有 `batch` 被重建（:721–732），其余成员跨复用保留 ⇒ 新加的 generation 放这里，语义与 Unity 的"中心记录"一致。
- `batch` 是 `BatchStorage` 的第一个成员，且 `batch.storage` 恒指回自己（构造 :727、Acquire :538、Release :564）
  ⇒ 结算路径取 `batch->storage->generation` 即可，不必额外加指针/索引。
- 与 `AcquireBatchStorage` 的"复用即复位"清单（:540–549：F2 `uniformTileSize`、F5 `fuseTileSize/fuseRuns`、`claimCapOverride`、`claimGeomOverride`、`claimSpanOverride`）同级，但方向相反：
  **generation 是这份清单里唯一"复用时要 ++ 而不是清零"的字段**（对应 Unity 的 `VersionIncrement`）。这几个字段的注释里已经写着"必须在 Acquire 里清零，否则继承陈旧值"——验证了"池化复用 + 忘复位"是这个仓库反复踩的坑型。

### 3.3 C 项：**已实装**（2026-10-04，库改动 + 一个正向用例）

**目标**：让迟到的 `taskDone_` 被**识别并拒绝**，而不是"当成合法操作执行"。拒绝一次结算 = 不 `fetch_sub`、不进 `TryFinalizeChaseLevBatch`、不减 `g_backendBatchesOutstanding`，只累加一个诊断计数。

**实装清单**（行号为改动后）：

| 位置 | 改动 |
|---|---|
| `src/NativeDll/JobSystemInternal.h:741` | `BatchStorage` 加 `std::atomic<uint32_t> generation{ 0 }`（**不在** `BatchState` 里）；`:514` 两个诊断计数 extern；`:1164` `ChaseLevTaskDone` 加代次参数 |
| `src/NativeDll/JobSystem_Tiles.cpp:564` | `ReleaseBatchStorage` 里 `generation.fetch_add(1, release)` —— **在 storage 进池之前**（否则下一个 Acquire 者会以旧代次开局） |
| `src/NativeDll/RangeTaskPool.h:35,97` / `SparseTileDeque.h:41` | `RangeTask` / `TileTask` 各加 `uint32_t batchGen`（deque 是 sparse 的、可混存多批 ⇒ 必须逐元素携带）；`Release` 时复位 |
| `src/NativeDll/ChaseLevScheduler.h:49,183` | `TaskDoneFn = void(*)(BatchState*, uint32_t)`；`ExecuteClaimToken(..., uint32_t batchGen, TileAccount)` |
| `src/NativeDll/ChaseLevScheduler.cpp` | 令牌创建处抄代次（`:623` 取 `batch->storage->generation`、`:691` 写进 RangeTask、`:1291` 写进 TileTask）；**8 个 `taskDone_` 结算点**、**8 个 `ExecuteClaimToken` 调用点**全部透传（这就是"令牌是唯一执行/结算入口"的账） |
| `src/NativeDll/JobSystem_Tiles.cpp:1826–1850` | `ChaseLevTaskDone`：先比 `batch->storage->generation`，不等 ⇒ `g_staleSettleDropped++` 并 return；再把 `fetch_sub` 的返回值分成 `0`（回绕实证 `g_pendingTasksWrap++`，**不改变行为**——旧代码此时同样不退役）与 `1`（触发退役） |
| `src/NativeDll/JobSystem_Scheduler.cpp:486` | Shutdown 打一行 `[JOBGEN] staleSettleDropped=N pendingTasksWrap=M` —— 这就是 11 §1.4 那条推断**首次可测**的探针（11 §1.3 的两个探针字符串已随 own-batch 一起回退、HEAD 里没有） |
| `tests/NativeDll.Tests/JobSystemTests.cpp:1795` | **正向用例** `TestStaleSettlementRejectedByGeneration`：取一块 storage → 同代次结算生效（2→1）→ 归还池（代次前进）→ 取回同一块开"新一代"→ **上一代的迟到结算必须被拒**（`pendingTasks` 仍为 2、不 `finalized`、拒绝计数 +1）→ 同代次结算仍生效 → 人为置 0 触发回绕探针（证明探针不是死代码）→ **复原两个全局计数**（避免污染套件级 `[JOBGEN]` 判据） |

**为什么必须做"正向用例"**：全绿 + `[JOBGEN]=0` 只能证明"从未触发"，不能证明"机制生效"。本用例里两次调用**只差一个代次参数**、结果分别是 1 与 2 ⇒ 同时证明了"同代次照常结算"和"异代次被拒"，即用例有鉴别力（若删掉代次校验，第 (3) 步立刻失败）。

**验收结果（2026-10-04 本会话）**：

| 项 | 结果 |
|---|---|
| 构建 | `cmake --build build-nativeDll-tests --config Release` ✅（仅既有 C4273 警告） |
| 单套件 ×3 轮（`ENTJOY_TEST_WATCHDOG=1`） | 3/3 `exit=0`、**81 PASS**（原 80 + 新用例）、0 FAIL、0 STALL；末条 PASS 时间戳 ≈24.8 s。⚠ **未做"改动前 vs 改动后"的同会话配对**，故这只说明"没有明显的量级回归"，不是性能结论（doc11 §2 记的 26.1 s 属另一会话，按纪律不可比） |
| `[JOBGEN]` | 每轮 **304 次 Shutdown 全部** `staleSettleDropped=0 pendingTasksWrap=0` |
| 十套件 ×4 配置（`tools\gate-run\run-native-tests.ps1`） | **每套 rc=0**（TILE_RUN=off / on / 中间块 / own-batch 臂共 4 块） |

**⇒ 对 11 §1.4 那条推断的实测回答**：关闭档在 **304×3 次 Shutdown**里没有出现 `pendingTasks` 回绕（探针现已在 HEAD）。这不等于"推断错误"，而是"在现行代码路径下**未观察到**"——首次迟到结算需要先有先验违反（HEAD 的双条件正是防它的），故 §1.4 那句仍应读作"机制上可能、实测未发"。

**未覆盖（留给 A）**：只守**结算**侧。"迟到令牌**执行** tile"需要更早的先验违反才可能，本次不改执行侧行为（见 `ChaseLevTaskDone` 的注释）。

### 3.4 A 项 / B 项 / 另一条 Unity 参照（不改 doc11 §5 的判据）

- **A = 2.5 那一整套**：中心记录（storage 槽位）+ 句柄副本（token）+ 回收时递增 + 结算时比对。C 是 A 的第一个消费者（只挂 token 结算一侧）。
- **B = Unity `Complete` 的第①条**（"prioritizes … to run first in the queue"）的对应物。第②条（"attempts to execute … on the thread which calls Complete"）在**批内并发认领**的 tile 级结构上不成立（doc11 §3.7 末两行）⇒ 走 B **不需要**引入"等待者执行/直认"路径；11 §1 判死的正是把②硬塞进来的做法。
- **另一条 Unity 侧参照（比 A 保守）**：`EnforceAllBufferJobsHaveCompletedAndRelease`（[AtomicSafetyHandle.bindings.cs:252–264](https://github.com/Unity-Technologies/UnityCsReference/blob/master/Modules/ManagedKernel/Managed/Jobs/AtomicSafetyHandle.bindings.cs#L252-L264)）的注释写着：先等**所有**针对该句柄的 job 完成，再 Release，"irregardless of any potential jobs that are still running"。
  这正是 `ForceFinalizeBatch` 那条"强制退役前先等在飞结算"的路线的官方对应物——但它会在 shutdown/异常路径上引入等待，与 A 的"不等、只失效"是两种取舍。
- 背景旁证（非决策依据）：池化复用的 ABA/[tagged pointer 与 free list](https://moodycamel.com/blog/2014/solving-the-aba-problem-for-lock-free-free-lists) 是同一族做法；`AtomicSafetyHandle` 是它在引擎里的工业实例。

---

## 4. 本轮发现的三处不一致（**已按用户指示直接改入 doc10 / doc11**）

| # | 位置 | 现文与问题 | 已改成 |
|---|---|---|---|
| ① | doc11 §1.4 vs §1.3 | §1.4 写"下溢本身是既有隐患（**关闭档同样会发生** `pendingTasks` 回绕，只是没人检查）"；§1.3 关闭档行却写 `[PENDINGTASKS-WRAP]` **0 次**。实测事实：`git grep -i "PENDINGTASKS-WRAP\|OUTSTANDING-UNDERFLOW"` 在 **HEAD 只剩 doc10 那一行** ⇒ 两个探针字符串**随 own-batch 实现一起回退了**；§1.3 的"关闭档 0 次"只能是在**带探针的实验构建**上（env 关档）测的 | doc11 §1.4 改标"**推断（未测）**"；§1.3 表格那行加"⚠ 探针只存在于带补丁的构建，HEAD 不可复现"；§5 的 C 行判据加"先重建探针"。**2026-10-04 已按此实装探针**（`[JOBGEN]`，§3.3）：关闭档 304×3 次 Shutdown 读数 **0/0** |
| ② | doc11 §3 出处注 | "`AtomicSafetyHandle` 的源码页抓取失败…未引用源码内部字段" | 换成 master 真实路径的三个源码链接 + 官方 manual "Version numbers" 链接，并指向本文件 §2.5 |
| ③ | doc11 §3.2 | "Unity maintains a global batch"（博客口径） | 补现代口径：**主线程自己的局部队列**（`JobHandle.bindings.cs:78–84`，见本文件 §2.2） |

doc10 同步改动：文件头的 2026-10-04 更新块加 ③（指向本文件）；§6.1 开头插入"**先量敞口（优先级 0）**"一段（判定式 K × 6.3 µs 与分支指向本文件 §5.1/§5.2）。

**关于"回绕/下溢 0 次"这个判据的可测性**（写给 C 的验收）：
- HEAD 的测试侧 watchdog 每秒打印 `g_backendBatchesOutstanding`（`tests/NativeDll.Tests/JobSystemTests.cpp:2771–2785`；45s 无 PASS 则打印现场并 `_Exit(3)`，:2788–2794）。
  下溢会显示成 ~4.29e9 这种荒谬值 ⇒ **可以看出**，但 1s 采样一次、不适合当"0 次"的统计判据。
- `pendingTasks` 回绕在 HEAD **完全不可观测**（探针随实验回退）。
⇒ 建议：把"代次校验命中（拒绝结算）次数"作为**诊断计数**（沿用"诊断计数永不 gate 热路径"的既有纪律，`JobSystemInternal.h:349–350`），它同时是 C 的验收量；然后再谈"回绕 0 次"。

**另记一条代码路径推断（明确标为未实测，故不进 §0 结论表）**：
HEAD 的 `TryFinalizeChaseLevBatch` 只判 `tilesRemaining==0 && pendingTasks==0`（`JobSystem_Tiles.cpp:1707–1712`）。
若 `pt` 被上一代的迟到结算减坏，`ChaseLevTaskDone` 的 `fetch_sub(1)==1` 分支（:1815）会在"真实令牌仍处于『tile 做完、taskDone 未发』窗口"时触发退役检查 ⇒ 理论上可把**在飞的新批**退役（与 doc11 §1.2 洞 3 同源，但**不需要** own-batch 补丁）。
但它要求"第一次迟到结算已经发生"，而 HEAD 的双条件本身正是防这个的 ⇒ **可达性未验证**。
这也是 §3.3 把代次校验放在 token 入口的价值：这条推断路径一次性关死。

---

## 5. 探索方向（基于 doc10 三轴的现状）

**一句话**：整步口径上，框架侧已经没有"大倍数"杠杆了 —— 默认档 `ratio_min 1.046` 的赢面主要来自 Melee（配置敏感，0.97–1.08），
赤字集中在 Build / Integrate（宿主为主）；轴三的 `3.60×` 是**空 job 微基准 / API 质量**指标，
**它在整步里的敞口尚未量化**。所以第一件事不是改代码，而是**把 `3.60×` 换算成 ms/步**。

### 5.1 ✅ 已量：K 与它的机制（2026-10-04，复用既有 A 日志，零新跑）

**器械**：不需要新计数器 —— `[JOBPERKEY]` 的 `batches=` 就是"该 kernel 的 Schedule 调用数"
（`g_perKeyBatches` 在 Schedule 路径 +1，`JobSystem_Scheduler.cpp:740/1075`），而 `ab-aligned.ps1:115` **本来就带
`ENTJOY_JOB_BATCH_TABLE_DUMP=1`** ⇒ 既有 6 份 A 日志（`n22default` / `n22aligned` 各 3 rep）已够用。
步数口径：`[M-20] 步=N` 的累计值，且"每步一次"的键恰好等于 N（自证）。

| 档 | rep | 步 | K = Σbatches | **K/步** | 最大键 `00003710` | 占 K |
|---|---|---|---|---|---|---|
| 默认（产品档） | r1 | 160 | 66 496 | **415.6** | 398.65/步 | 96% |
| 默认 | r2 | 128 | 66 075 | **516.2** | 495.16/步 | 96% |
| 默认 | r3 | 160 | 66 905 | **418.2** | 401.10/步 | 96% |
| 对齐 | r1/r2/r3 | 128/128/160 | 61 161 / 58 701 / 66 899 | 477.8 / 458.6 / 418.1 | 458.4 / 439.9 / 401.1 | 96% |

**K 的 96% 来自一个键：`00003710` = Flow BFS 波前 job。** 宿主侧确证
（`ComputeShaderBattleSimulation/CPUBattle/Scripts/CPUBattleFlowField.cs:290–352`）：

```csharp
// ── FlowBFS 波前循环（双阵营合并调度）：每波一个并行 job，宿主 Complete() 即 barrier ──
wave.Schedule(waveCount0 + waveCount1, WaveBatch).Complete();
```

- 宿主自己的注释："**波数=深度（~380）**"、"重置后本波循环的 **393 条**即全部样本" ⇒ 与实测 398–495 波/步吻合；
- 每批平均 1 712 元素 = 本波 frontier 格数；`[JOBBATCHTBL] N=18836` = 网格格数（首见批的 length）；
- ⇒ **这一项不是"每 job 一次的通用派发"，而是每步 ~400 次的 barrier 往返**：每次 `Complete()` 都是同步点，
  **每波往返 100% 落在关键路径上**（不是可重叠的并行 job）。

**每波成本**（= 宿主 `[M-12]` 的 **波前** ms ÷ 波数/步）：默认档 **33.9 / 28.1 / 33.3 µs**，对齐档 31.8 / 39.0 / 33.1 µs。
与 doc10 §3 的 `[E1]` 相位（起手 1.87 + 起步散布 19.30 + 末 tile→拓扑完成 7.97 ≈ 29 µs）同量级
⇒ **主要相位是"起步散布（唤醒/入场）"与"退役尾部"**，不是一个笼统的"派发开销"。

### 5.2 ✅ 同会话配对：赤字就在波循环里（默认档 3/3 rep）

B 侧同会话日志（`n22default/B-r*.csv`）**有同形的 `M4,flowpass,wave_ms`** ⇒ 可以逐 rep 配对（这正是 §5 要的证据形态）：

| rep | A `[M-12] 波前` | B `wave_ms` | Δ（A−B） | 每波 Δ | 判定 |
|---|---|---|---|---|---|
| def r1 | 13.53 ms | 12.19 ms | **+1.34 ms/步** | +3.3 µs | A 慢 |
| def r2 | 13.90 ms | 12.55 ms | **+1.35 ms/步** | +2.8 µs | A 慢 |
| def r3 | 13.37 ms | 12.17 ms | **+1.20 ms/步** | +3.0 µs | A 慢 |
| mir r1 / r2 / r3 | 14.59 / 17.15 / 13.26 | 12.54 / 12.15 / 13.79 | +2.05 / +5.00 / −0.53 | +2.4 / +17.7 / −3.2 | **噪声大**（r2 的 A 与 r3 的 B 各有一次异常），不作结论 |

⇒ **默认档（产品档）3/3 同号：A 的波循环每步慢 1.20–1.35 ms，折合每波 +2.8…3.3 µs。**

**判定（按 §5.2 的判定线）**：Δ ≈ **1.2–1.35 ms/步 ≫ 0.4 ms/步** ⇒ 当时判为"join 侧是真赤字"。
⚠ **该判定已在 §5.5 被同形地板对照推翻**：那 1.2–1.35 ms/步 不是框架地板差（游戏内同形地板 1.13–1.29×、框架只占波前 ~9%），
而是**波内内核/宿主工作**（"不改测试端"下框架动不了）⇒ 分支改走 doc10 §5②③ + K1′+G。

### 5.3 每波的相位分解（2026-10-04，`ENTJOY_DIAG_E1=1` 单轮；`[E1]` 数据，诊断档不作 A/B 依据）

`tools/gate-run/n23spin/S2_e1.stdout.txt`（默认档、8 worker、assist=0、本会话）：

| 量 | 值 | 读法 |
|---|---|---|
| batches n | **66 897** | 与 §5.1 的 K 同量级 ✓（~418/步 × 160 步） |
| wave 批（8/16/32 tiles） | 23 338 / 21 777 / 19 698，wall 1 129.1 / 433.9 / 719.6 ms | 合计 **64 813 批、2 282.6 ms ⇒ 35.2 µs/批**（= 每波总墙时） |
| 大批（512 tiles） | n=1 906，wall 14 713.6 ms | Melee/大 pass，**与波循环不同形**，别混算 |
| `submitToFirstWorker` | **3.60 µs** | 每波框架相位里最大的单项 |
| `lastTileToTopologyDone` | EWMA 11.04 µs，但**退役链逐项合计只有 1.85 µs/批**（cas 0.03 + timing 0.26 + barrier 0.12 + cleanup 0.51 + CompleteState 0.88 + RecordTopology 1.09，max 371.7） | ⇒ **退役尾部不是杠杆**（此前 §5.1 用 `[E1]` EWMA 7.97 µs 判断"尾巴大"，本轮用逐项分解纠正） |
| `workerStartSpread` | EWMA 33.83 µs；按 entered 分组 **w8 的 mean=3.2 µs**（max 1 898.7） | EWMA 被大 job 离群值拉高；微波的真实入场散布只有 ~3 µs |
| `meanConc` / `busy_ratio` | **6.47 / 8**（81%）、**0.7582** | 每波并行度只用到 6.5/8 |
| steal/dispatch | `steal success=137`、`parkWake=21 383`（0.32/批）、`hotSpin=518 609`（7.75/批）、`emptyExits=540 019` | 窃取几乎不发生（注入器路径主导）；停靠/自旋是主要"非计算"活动 |
| 并发直方图 | c5:33 742、c6:26 936、c7:974 | 绝大多数波只跑到 5–6 个 worker |

⇒ **结论（本轮最重要的方向修正）**：每波的**框架相位合计只有 ~5.5–8.7 µs**（submit 3.60 + 退役 1.85 + 入场 ~3），
而每波总成本 **~35 µs** ⇒ 框架占比 16–25%，**剩下的是 BFS 真计算 + 并发度损失**。
所以下一杠杆是**每波的并发度/入场（6.47→更高）**，不是"退役尾部"，也不是笼统的"派发开销"。

### 5.4 本轮已跑的臂：`ENTJOY_SPIN_HOT_US`（零代码）——**空结论**

按 §5.3 之前的假设（"worker 在波间 park/wake"）先扫了现成旋钮，A-only、轮转臂序、3 rep（`n23spin/A-hot{0,300}-r*.stdout.txt`）：

| rep | 波前 hot0 | 波前 hot300 | Δ | Flow Δ |
|---|---|---|---|---|
| r1 | 14.48 ms | 14.47 ms | **−0.01** | +0.23 |
| r2 | 14.42 ms | 14.59 ms | **+0.17** | +0.39 |
| r3 | 15.02 ms | 14.15 ms | **−0.87** | −1.21 |

⇒ **符号不稳定（1/3 更快）⇒ 不构成结论**（本仓库的判据要求 ≥3/3 同号）。机制上也说得通：波与波之间几乎无缝
（宿主 Complete 后立刻 Schedule 下一波），worker 本就有活可领，热窗主要作用在 **pass 边界**而不是波循环内部。

### 5.5 ✅ 决定性对照（本轮）：**同形空 job 地板——离机那条 3.60× 在游戏里不成立**

§5.2 的敞口估计（`K × 6.3 µs ≈ 2.6 ms/步`）用的是**离机微基准**（`tools/SchedSubmitProbe`，len=7936/batch=64，N=1000 空 job 连发）。
本轮用**宿主自带的同形探针**把它换掉 —— 两边都是"每波一次 `Schedule+Complete`、空 job、8 worker、同一会话"：

| 空 job 探针（同会话） | A = EntJoy | B = Unity |
|---|---|---|
| 器械 | 宿主 `[M-15]`（`CPUBATTLE_FLOW_DISPATCH_PROBE=1`，392 波，格子 1/512/1792/4096，1 与 12 字段） | `W0_PROBES=4`（`empty_parallel_floor_burst` / `burst_per_job_burst`） |
| 结果 | **0.57 / 3.09 / 2.83 / 2.84 µs·波⁻¹**（1 字段）；3.12 / **3.13** / 3.21（12 字段） | **2.500 µs**（floor）、**2.434 µs**（burst 1000） |
| 证据 | `n24gran/S3_dispatch.stdout.txt` | `n24gran/unity-probe.csv` |

- 真实波前 = 13.4–13.6 ms/步 ÷ ~400 波 = **33.5–34.5 µs/波** ⇒ **框架地板只占 ~9%**，其余是 BFS 真活；
- 同形地板比 **A/B ≈ 1.13–1.29×**（**不是**离机那条 3.60×）；且 A 在"格子=1"时只有 0.57 µs ⇒ 那 3 µs 主要是**每元素/每调用**开销，不是每波派发；
- ⇒ **游戏内、框架自有**的超额 ≈ (2.83−2.50)…(3.13−2.43) µs/波 × ~400 波 = **0.13–0.28 ms/步**。

**判定（按 §5.2 的判定线重算）**：框架自有敞口 **0.13–0.28 ms/步 ⇒ 落在 "<0.2 转 §5②③" 与灰区之间**，与"2.6 ms/步"的旧估计差一个量级
⇒ **join 侧（B 令牌优先 / A 有界自旋）不再作为主攻**：即使把每波地板压到 0，上限也只有 ~1.2 ms/步，而实测地板已与 Unity 同量级。
⇒ 按目标的分支规则 **改走 doc10 §5②③（小 pass 每调用前导）+ K1′+G**（后者同时是唯一"赤字+真 UB"项）。
⇒ 同时记录：**doc10 §4 的"`sc` 3.60× 是唯一同形可比"这条结论在游戏内不成立**（同形地板 1.13–1.29×）。
⇒ ⚠ **本节的口径后来被 §6.7 修正**（两侧 (长度, 内批, 次数, 体量) 都不同，"同形"名不副实）：
把两侧都做成可驱动后，真同形是 **A 3.71 µs vs B Unity 2.63 µs = 1.41×**，且差距在**每 job 扇出/合并**、不在每 job 固定派发。
⇒ 离机探针（§5.5 记的"3× 未查"）也在 §6.9 定位：它走的是**托管路径**（`SchedSubmitProbe` 的 job 未转译），
成本 = 每 unit 托管回调帧 + **托管逐元素循环**（实测 ≈0.4 ns/项），不是"每次调用编组/托管 thunk 占比不同"。
两者都不影响 §5.5 的分支判定（敞口仍远低于布局噪声底）。

### 5.6 本轮另一条被证伪的假设：**每波粒度/尾部均衡**（零代码，4 臂 × 1 rep 筛）

用批表**只给波前键**改内批（`ENTJOY_JOB_BATCH_TABLE=00003710:<n>`；生效证据 = `tiles` 变化）：

| 臂 | tiles（波前键） | tiles/波 | calls/batch | 波前 ms | Melee ms（**不受该臂影响**） |
|---|---|---|---|---|---|
| ref（JCC 自适应） | 1 565 224 | 24.4 | 9.51 | 13.38 | 80.11 |
| `:8` | **13 766 910** | 214.6 | 9.18 | 13.49 | 79.57 |
| `:32` | 3 461 481 | 53.9 | 16.60 | 13.21 | 79.79 |
| `:128` | **895 977** | 14.0 | 8.80 | 13.98 | 79.32 |

⇒ 粒度**确实生效**（tiles 变化 15×），但**波前不动**（13.21–13.98，非单调 ±0.6 ms），而未受影响的 Melee 自己就漂 ±0.8 ms
⇒ **"波太小/尾部不均衡"被证伪**：波前的成本不随 tile 粒度变化（也与 F5 融合把 24 tiles 压成 ~9.5 次调用一致）。

**仍继续用现成 env 的其余候选**（若下一步回 join 侧再用）：`ENTJOY_CLAIM_SPAN` 对波前**不适用**（只在 `itemsPerTile ≤ 16` 时生效，波前是 ~180 元素/tile）、`ENTJOY_CLAIM_BATCH` 同理（`step=clamp(tileCount/workers,1,cap)`，波前 tileCount/workers≈1 ⇒ cap 无效）。

### 5.7 工具状态（本会话，必须记录）

- `tools/gate-run/ab-aligned.ps1` 在本会话里 **9/9 次 A 运行启动即 SIGSEGV**（`signal 11`、stdout 全空、~4 s 退出，
  `n23spin/A-hot0-r1-a1.stdout.txt` 等 0 字节 + `.err.txt` 崩溃栈）；而**同一命令行手工启动完全正常**
  （`n23spin/S1.stdout.txt`：22.4 s、`[M-19]`×5、`[M-1] 步均`×4、`[JOBPERKEY]`×15、无崩溃）。
  ⇒ 不是库/部署问题（部署 DLL 仍是 10-03 的 `sha256=675A932E…`，err.txt 自证）；本文件 §5.4 的数据是用
  scratch driver（`%TEMP%\n23spin-driver.ps1`，复用同一命令行 + 1.5 s 间隔 + 轮转臂序）拿到的。
  **下轮若要跑 harness，先确认它的 Godot 启动在本会话是否恢复**，否则继续用 driver 并把证据落在 `n23spin/`。
- 注意 `CPUBATTLE_AUTODEPLOY=1` 的语义是"**1 秒后自动开战**"，不是部署 DLL；漏设它会得到"零 job 的空跑"
  （`[JOBGEOM] auto=0`、无 `[M-1] 步均`）—— 本轮为此浪费了 18 次无效运行，记在这里。

### 5.8 无论走哪个分支都该做的两件（代价小、判据明确）

1. **C 项**：既有隐患，同时是"迟到结算是否真在关闭档发生"的**唯一探针** —— 它直接解决 §4① 那条推断（✅ 已做，§3.3）。
2. **K1′+G**（doc10 §5④ / 09 §54.4 已预注册）：消掉每元素 `index < Length` 谓词，顺带消掉"最后一块越界"这个**真 UB** —— 落后清单里唯一"既赤字、又是正确性"的项。

### 5.9 不再投入

| 方向 | 原因 |
|---|---|
| `place`（0.756–0.898）/ `Integrate`（0.873） | 宿主数据结构与寄存器压力，"不改测试端"下动不了（doc10 §5⑤⑥） |
| "对齐档落后 X%" 的叙事 | 镜像档本身对 EntJoy 次优（默认档 1.046）；引用必须同时给默认档（doc10 §2.3） |
| 等待者直认 tile | 已判死（11 §1）；§3.1 说明它缺的正是"身份"这一层 |
| 提忙比 / 更多 worker / 更细分块 | physcap：细小 job 上 15 worker 比 8 worker 慢 3.6×（doc10 §3） |
| **退役尾部**（原以为 7.97 µs/波） | §5.3 逐项分解实测只有 **1.85 µs/批**；EWMA 那个数被大 job 拉高 ⇒ 不值得动 |

### 5.10 执行顺序（建议 + 现状）

`0` 量敞口 —— ✅ **已完成**（§5.1/§5.2：K=415–516/步，波循环是主源）
→ `1` C —— ✅ 已完成（§3.3：代次校验 + `[JOBGEN]` 探针 + 正向用例，十套件全绿）
→ `2` join 侧：`ENTJOY_SPIN_HOT_US` ❌ 空结论（§5.4）→ 粒度/认领几何 ❌ 证伪（§5.6）→ **同形地板对照 ❌ 关闭该分支**（§5.5：框架自有 0.13–0.28 ms/步，且地板与 Unity 同量级）
→ `3` **当前分支：doc10 §5②③（小 pass 每调用前导）+ K1′+G**
   —— ✅ **K1′ 经核实"早已实装"**（§6.1）、✅ **G 已实装且判据①/②/④ 达标**（§6.2/§6.3）、
   ⚠ 但 **G 的性能收益被构建布局噪声淹没、本次不可判定**（§6.4）
→ `4` **doc10 §5②（`prefixFinal` 每调用前导）与 §5③（`zero`）** —— ✅ **已收口**（§6.6：两趟都只有 **1 批/步**，
   同形地板 0.57–2.8 µs/次 ⇒ 框架固定成本 ≤0.6–2.8 µs/步，与 0.04–0.09 ms/步的赤字差 1–2 个数量级）
⇒ **框架侧落后清单到此全部结清**；剩余为**宿主归属**：`place`（散列原子取槽）、`Integrate`（寄存器压力/布局）、
   `count`（共享原子）、以及 `zero` 的内核 write loop（"memset 式整块写"，预期 ≤0.04 ms/步，**低于实测布局噪声底**
   ⇒ 按 09 §40.3 门槛需先做夹具臂）。每步验收仍固定为位置平衡 `A,B,B,A`；注意 §5.7 的工具状态与 §6.5 的四条协议坑。

---

## 6. K1′+G 实装与测量（2026-10-04；doc10 §5④ 的低分支动作）

### 6.1 ✅ 更正：**K1′ 其实已经实装**（doc09 §54.4 说"从未实现"是陈旧的）

逐条核对元素区间的三条构造路径，**全部都钳到 `totalElements`**：

| 路径 | 位置 | 钳位 | 引入 |
|---|---|---|---|
| 物化 tile 表 | `JobSystem_Scheduler.cpp:768–773` | `count = std::min(cs, length - first)` | `edf611e`（**2026-08-15**） |
| uniform 算术推导（F2） | `JobSystem_Tiles.cpp:616–624` | `if (first + n > total) n = total - first;` | `9ba7ba2`（2026-10-02） |
| guided 可变块 | `JobSystem_Tiles.cpp:180–190` | `if (size > remaining) size = remaining;` | `edf611e`（2026-08-15） |
| F5 融合 run | `JobSystem_Tiles.cpp:1611–1613` | `count = (first64+spanned > total) ? total-first : spanned` | 2026-10-01/02 |

⇒ **"调度长度 ≤ 数组长度"今天已是框架保证**，`index < Length` 对**数组越界**不再承重。
⇒ 因此：① doc09 §54.3/§54.4 的"框架没给出 Unity 那条契约 / K1′ 从未实现"是**陈旧记录**；
② 宿主里的注释（`ComputeShaderBattleSimulation/CPUBattle/Scripts/CPUBattleSpatialHash.cs:247–250`，写"最后一块会越过 length（977×1024=1,000,448）"）同样陈旧 —— 那是 K1 实验（2026-10-01 之前）时的状态；
③ 08 §32.7 的 K1 分叉**可以解释为**"当时命中的路径与今天不同"（10-02 才补 uniform/F5 的钳位），但**我无法从当前代码回证当时的具体路径**，故不写死机理。

### 6.2 ✅ G（守卫折叠）已实装

`src/NativeTranspiler/Analyzer/Cpp/CppJobGenerator.cs`（+105/−2 行）：

- **AST 只是检测**（`TryDetectIndexLengthGuard`）：找**无 else** 的 `if (<index> < <job 的 int 字段> && …)`，
  首合取项左侧是 index 形参、右侧解析为**本 job 的非静态 int 字段**（`Length` 这类）；要求**至少还有其它合取项**。
- **剥离在生成文本上做**（`StripFirstGuardConjunct`）：正则 `(\bif\s*\(\s*)index\s*<\s*Length\s*&&\s*` → `$1`（只首个）。
  同时把合成循环上界改成 `std::min(__startIndex + __count, Length)`。
- **构建期开关** `ENTJOY_GUARD_FOLD`（`=0` 关闭 = 逐位回到旧行为）—— 折叠是生成期变换，A/B 必须**同一源码两次构建**。
- ⚠ **第一版用 `ReplaceNode` 重写 AST 再翻译，直接炸**：重写树是游离的，Roslyn `CheckSyntaxNode` 会沿父链回溯到根
  ⇒ `ArgumentException: 语法节点不在语法树中`（CSBS 构建里表现为 `NT026`）。**这正是"AST 检测 + 文本剥离"的原因**。

### 6.3 判据对账（doc09 §54.4 的四条）

| 判据 | 结果 |
|---|---|
| ① 生成物里 `index < Length` 消失、`for` 上界为 `min(…)` | ✅ **同源两臂对照**：`noG` 臂 `guardInBody=1 / foldedBound=0`（DLL `5CDDF9A8`）；`G` 臂 `guardInBody=0 / foldedBound=1`（DLL `705E902E`）。全目录仅 `CountCellsJob`/`PlaceCellsJob` 两个文件被折 |
| ② `[JOBPERKEY]` `elemsCalled == elems` 不变、`mismatch=0` | ✅ 两臂的 COUNT/PLACE 全部 `elemsCalled == elems`（1e6/步）、`mismatch=0`（跨构建按**首见序=角色**对齐——重建会换 key 空间） |
| ③ 对齐档 Melee 落回 118–127、整步不退化 | ⚠ **不可判定**（见 §6.4） |
| ④ 转译器单测 / jobs-only / `dotnet build EntJoy.sln` / 原生 10/10 | ✅ 转译器单测 **101/101**、`JobsOnlyTranspilerCheck` 0 错误、原生十套件 **4 配置全 rc=0**；`dotnet build EntJoy.sln` 有**一个既有**失败（`samples/EntJoySample/…/ManyJobsBenchTest/Program.cs` CS0017 多入口点，与本次改动无关） |

### 6.4 ⚠ 性能：**噪声淹没，未能判定**（这是本轮最重要的诚实结论）

A-only、轮转臂序、3 rep、同会话（`n26kg/`）：逐 rep 配对（G − noG，负 = G 更快）：

| rep | count Δ | place Δ | prefixFinal Δ | zero Δ | Build Δ | **Melee Δ（G 不该碰）** | 整步 Δ |
|---|---|---|---|---|---|---|---|
| r1 | +0.056 | −0.029 | −0.054 | −0.015 | −0.02 | **+1.44** | +0.88 |
| r2 | +0.042 | +0.241 | −0.046 | −0.027 | +0.17 | **+1.11** | +2.04 |
| r3 | +0.835 | +0.588 | −0.027 | −0.007 | +1.55 | **+13.18** | +17.36（**作废**：该轮 128 步 vs 对照 160 步，窗口不等且处于晚期战场） |

⇒ 两个**受影响**的趟（count/place）方向不一致、量级 ≤0.24 ms；而**未受影响**的 Melee 3/3 变慢 ≥1.1 ms
⇒ **两构建间的布局/对齐噪声（单 TU 布局效应那一类）≈ ≥1.1 ms/步，远大于 doc09 §54.4 预期的 0.03–0.15 ms** ⇒ **本次无法判定 G 的收益**。
另外：旧生成物里 `const int Length = *Length_ptr;` **本来就是每批一次**的（不是 §54.4 假设的每元素一次），
所以 G 省下的实际只有"每元素一次比较+分支"，这与"效应落在噪声以下"是自洽的。

> **2026-10-04 后续（doc09 §56/§57，判据③已单独重测）**：用 `tools/gate-run/gfold-build.ps1`（**同一源码两次构建**：
> GF0=`ENTJOY_GUARD_FOLD=0` / GF1=默认折叠；先 `dotnet build-server shutdown` 再 `-t:Rebuild`）+ §55 的 `n20` 协议
> （每臂 discovery、位置平衡、逐 rep 配对），16 跑全部过门（`applied=15/15`、`mismatch=0`）：
> Count/Place 环比 **1.027 / 1.023**、逐 rep 符号 **1/3、1/3**（中位 **+0.68 / +0.77 ns/elem**）；而**G 根本碰不到的**
> ClearAll / Integrate 在同一批里漂移 **+38% / −6.8%** ⇒ **判据③未通过**：G 作为等价变换保留（零回归、与 K1′ 一起消越界 UB），
> 但**不得记为性能改进**，§54.4 的"预期 0.03–0.15 ms/步"作废。
> 同轮还执行了 doc09 §54.5 的预注册门槛（夹具新增 `BenchTwoPassJob.cs`：两遍切分以缩小 live-set）：
> 校验和逐位相同 + **3/3 复跑环比 1.03 / 1.06 / 1.16 ⇒ 门槛未通过、"降活跃值"这条轴关闭**。
> ⚠ 一条可复用的方法学：宿主机 `[JOBPERKEY]` per-element 口径在本机**打不到 2%**（没有任何"未受处理的对照"能定噪声带），
> 以后 <3% 的每元素结论要走夹具或自带对照臂。

**处置建议**：G 保留（严格更少工作、且让内核自足），但**不宣称性能收益**；若要投产判定，得按 doc09 §40.3 的门槛
先在夹具里做出环比 <1 的臂（或大幅增加 rep 并用同源多构建重复），而不是在这个噪声底上硬读 0.1 ms。

### 6.5 本轮踩到的四条协议坑（都已定位，供下轮复用）

1. **`ReplaceNode` 重写树 → 语义模型失效**（`NT026`）：必须"AST 检测 + 文本剥离"。（§6.2）
2. **`Get-Content -Raw | Set-Content` 往返毁文件编码**：该文件原本无 BOM，往返后带 BOM 且中文全乱、字符串字面量里出现裸换行（CS1010）。
   ⇒ 文本编辑一律用 `edit` 工具；本次已 `git checkout` 回退后用 `edit` 重放（`+105/−2`，无 BOM）。
3. **MSBuild 增量不会因 env 变化重跑生成器**：`ENTJOY_GUARD_FOLD` 两臂第一次拿到**同一个** DLL（`07CC2B03`）⇒ 必须 `-t:Rebuild`。
4. **重建 `NativeTranspiled` 会换掉 kernel key（RVA）空间**：`[JOBPERKEY]` 的 `key=` 与批表键在两个构建间不可比
   （本轮 `00003710` 只在 noG 构建里存在）⇒ 跨构建只能按"**首见序 = 派发序 = 角色**"对齐，或每构建各推一次 `derive-jobkeys.ps1`。

---

### 6.6 ✅ §5②③ 收口：**"每调用前导"在框架调度器一侧已无可摘的果子**（同会话实测）

判据本来就是"每调用前导"，所以**关键是每步几个 Schedule**。同会话单跑（默认档、8 worker、DUMP=1+BULDPASS=1，`n27passes/S1.stdout.txt`，160 步）
按角色（首见序）读 `[JOBPERKEY]`：

| 角色（首见序） | 批/步 | 调/步 | 元素/步 | 元素/调 | 对应 `[M-19]` 趟 |
|---|---|---|---|---|---|
| 7 | **1.00** | 7.6 | **64** | **8.4** | `prefixFinal`（0.1591 ms/步） |
| 8 | **1.00** | 7.9 | **64** | **8.1** | `prefixPartial`（0.0469 ms/步） |
| 5 | **1.00** | 10.3 | 1 000 000 | 97 561 | `zero`（0.1325 ms/步） |
| 6 / 9 / 12 | 1.00 | 126.5 | 1 000 000 | 7 905 | `count` / `place` / `Integrate` |
| 14 | 398.54 | 3 553.1 | 682 637 | 192 | Flow 波循环（已在 §5.5 关闭） |

- 同会话的框架地板（§5.5 的宿主 `[M-15]` 空 job 探针，同形 392 波 × `Schedule+Complete`）：
  **格子=1 → 0.57 µs/次；格子=512/1792/4096 → 2.83–3.21 µs/次**。
- ⇒ `prefixFinal`/`prefixPartial`/`zero` 每步**只有 1 次 Schedule+Complete** ⇒ 框架固定成本 **≤0.6–2.8 µs/步**，
  而它们的赤字是 **0.04–0.09 ms/步** ⇒ **差 1–2 个数量级**；这三趟的成本在**内核体/宿主编排**，不在框架派发。
- ⇒ `count`/`place`/`Integrate` 同理（1 批/步）。
- ⇒ 适配器侧每调用前导也已在库里量过：**≈11.3 ns/次**（`JobSystemInternal.h:215–221` 引 08 §14 实测），
  `prefixFinal` 7.6 调/步 ⇒ **0.09 µs/步**，同样可忽略。
- ⇒ 唯一"批/步"高的角色是**波循环（398/步）**，它已在 §5.5 用同形地板对照关闭。

**剩余（记账，不在本轮做）**：`zero` 的框架侧方向"整块写走 memset"预期 ≤0.04 ms/步，
且**低于本轮实测的两构建布局噪声底（未受影响的 Melee ≥1.1 ms/步，§6.4）** ⇒ 按 09 §40.3 的门槛，
**先在夹具里做出环比 <1 的臂**才允许动生成器；**§5②③ 的框架调度器一侧到此收口**。

---

### 6.7 ✅ 本轮新建：**真正同形的跨栈臂**（两侧形状都可驱动）

§5.5 的对照有一处口径缺口：Unity 侧只有一个固定形状（`W0_PROBES=4`：len=7936、batch=64、1000 次 `Schedule+Complete`），
A 侧探针固定 (cells 表, batch=0, 392 波, **每元素一次写**)。两侧的 (长度, 内批, 次数, **体量**) 全都不同
⇒ "1.13–1.29×" 只是"同生成器、不同尺寸"的比较。本轮把两侧都做成**可驱动**：

| 侧 | 器械 | 旋钮 |
|---|---|---|
| B = Unity | **新增** `Assets/Scripts/BattleBench/BattleBenchShapeSweep.cs`（门 `W0_SHAPE=1`，CSV 组 `W0SHAPE`，`RuntimeInitializeOnLoadMethod` 独立入口） | `W0_SHAPE_LEN/BATCH/JOBS/REPS/WARMUP/BULK/CSV`；调用形状＝`Schedule(len,batch).Complete()` × N（与 A 侧 `sc`/`W0_P4` 逐字同形）；体＝**真空体**（`if (i==0) Proof[0]++`，每个 job 实例恰好一次，每 rep 断言 delta==N） |
| A = EntJoy | 宿主 `[M-15]` 探针**加形状旋钮**：`CPUBATTLE_FLOW_DISPATCH_PROBE_CELLS/_CELLS12/_BATCH/_WAVES/_EMPTY` | `_EMPTY=1` 用新 `FlowProbeEmptyJob`（整批只在 i==0 写一次＝与 Unity 空体同形）；四个旋钮缺省＝旧口径逐字不变 |

- 新增臂**不动**任何冻结臂：Unity 侧只加一个文件（`git status` 只有 `??`；IL2CPP 重建 `result=Succeeded errors=0 time=00:01:30`）；
  A 侧部署 `NativeTranspiled.dll D2A2BD15 → D0B80CF5`（`NativeDll.dll` 仍 `0C144D76`，本项不动框架）；
- A 侧顺带修掉一个**潜在越界**：旧探针把 `Sink[i]` 写进**固定 4096 int** 的数组，格子表一旦抬到 >4096 就是越界写 ⇒ 现在按请求的最大格子数分配。

**同形结果（同会话、8 worker、空体、`Schedule+Complete`；A 侧 250 波 × 4 窗取 min，B 侧每 rep 25 次取中位）**：

| 轮次 | A 转译原生（游戏内） | B Unity Burst（player） | 比 |
|---|---|---|---|
| 首轮 3 rep（A 单窗 1000 波） | 3.71 µs（3.53–3.78） | 2.63 µs | 1.41× |
| 交错 7 rep（A 单窗 1000 波，`n34shapeAB/`） | median 4.28（3.73–5.52 = **±25%**） | median 2.76（2.61–2.85） | median 1.55× / min 1.43×（逐 rep 1.31–**2.00**） |
| **交错 7 rep（A 改 250 波×4 窗取 min，`n42abmin/`）＝现行口径** | **median 3.66 / min 3.43 / max 3.77（±5%）** | **median 2.67 / min 2.59 / max 2.76** | **median 1.369× / min 1.327×**（逐 rep **1.28–1.41**） |

- ⇒ **前两行的高值与大散布是器械造成的**：单窗 1000 波时一次 ~15–19 ms 的偶发停顿就能抬整条线（详见下面的"伪影更正"）。
  改成"多窗取 min"后 A 侧散布 **±25% → ±5%**，比值区间 **1.31–2.00 → 1.28–1.41**。
- ⇒ **现行可报的同形差距 ＝ 1.33×（min）/ 1.37×（median）**，绝对差 (A−B) = **0.84–0.99 µs/job**。
- 记账：B 侧每 rep 25 次取中位（已抗单次停顿）；更早一次 5 rep 冒烟给 2.93 ⇒ 少 rep 的中位数偏高 ~12%。

**形状矩阵（A 侧：**同一次运行内**量完 21 个 (格子, 内批) 组合 ⇒ 排除跨运行漂移；空体、8 worker、assist=0、**250 波 × 4 窗取 min**、n=3，`n40minrep/`）**：

| cells ↓ \ batch → | 64 | 512 | 7936 |
|---|---|---|---|
| 1 | 0.78 | 0.78 | 0.76 |
| 64 | 0.79 | 0.76 | 0.76 |
| 128 | **2.02** | 0.77 | 0.78 |
| 256 | 2.20 | 0.88 | 0.76 |
| 512 | 2.54 | 0.85 | 0.82 |
| 1792 | 2.66 | 2.38 | 1.07 |
| 7936 | **3.41** | 2.77 | 2.24 |

（µs/波；器械：`_CELLS` + `_BATCHES`（内批列表）+ **`_REPEATS`（多窗取 min）**。）

- **"摊开代价"是一次性的、发生在第 2 块**：64/64（1 块）0.79 → 128/64（2 块）**2.02**（**+1.2 µs**），
  之后每多一块只 +5–7 ns（8 块 2.54、28 块 2.66、124 块 3.41）
  ⇒ **A 的每 job 成本 ≈ 0.78（固定）+ 1.2（一次性摊开）+ 6 ns×块数**；单块内哪怕 7936 项也只 2.24 µs（~0.19 ns/项）。
- B 侧（Unity，独立运行）：1 项 1.10、8 块 1.69、28 块 2.02、64 块 2.25、124 块 2.63、1 块(7936 项) 2.62、7936 块 6.16（≈0.64 ns/块）。
  ⇒ **B 的地板更高（1.10 vs 0.78）但摊开几乎免费（1 块→8 块只 +0.59 µs）**；A 的地板更低、摊开要 +1.2 µs。
- ⇒ 交叉点就是**摊开**。**两者每块的边际几乎相同（A ≈6 ns、B ≈8 ns）** ⇒ **整个差距＝那一次性摊开**（A +1.2 µs vs B +0.59 µs）。
- ⛔ **伪影更正（自我更正上一版）**：上一版记的 `(128,512)` 19.2 µs / "24× 病态 / JCC 形状切换"**不成立**：
  ① 代码侧：显式 `batch>0` ⇒ `cs = reqBatch` ⇒ `rc = 1` ⇒ **`ScheduleFastPath`**（`JobSystem_Scheduler.cpp:997–1005`），tiling/JCC 根本不参与；
  ② 换窗长即可推翻：同形状 **250 波 = 0.80 µs、4000 波 = 0.76 µs**（`n39stall/`）；而 4000 波那次停顿改落到 **`(128,64)`**（5.73 µs/波 ≈ 2.1 + 14.5 ms/4000）
  ⇒ 真身是**一次 ~15–19 ms 的偶发停顿（GC/OS）落进单窗**，把整条线抬成"19 µs/波"（= 19 ms/1000 波）；因为该线只有一个数，看起来就像形状病态。
  ③ 器械已修（`_REPEATS`，CSBS `742ee14`）；修复后 21 组合重测（上表）该格 = **0.77**。
  ⇒ 教训：**单窗 ≥1000 波的形状矩阵不耐停顿 ⇒ 必须多窗取 min**；"某形状慢 24×"这种量级**先怀疑器械**再怀疑内核。

**折算到游戏（记账）**：按交错 7 rep 的成对差 (A−B) = **0.84 µs（min）…0.99 µs（median）**，× ~400 波/步 = **0.34–0.40 ms/步**；
**低于本仓两构建布局噪声底（±1.1…±3.5 ms/步，§6.4）** ⇒ 不足以支撑"再攻 join 侧等待策略"。
**但方向明确**：差距**只在"第 2 块那一次性摊开（+1.2 µs，Unity 只 +0.59）"**，每块边际两边几乎相同（A 6 ns / B 8 ns）；
要收就压这一次性摊开（唤醒 N 个 worker + 物化 tile 表 + 建 state + 合并）或减少每步的 job 数，
而不是动 join 的等待/窃取策略。（证据目录：`tools/gate-run/n31shape/`、`n40minrep/`、`n42abmin/`，均未入库。）

**"转译原生内核能否追上 Unity" —— 现行判定**：
- **派发口径接近追平**：成对差折算 **0.34–0.40 ms/步 < 布局噪声底**；真实波前 33.5–34.5 µs/波、框架地板只占 ~9%（§5.5）
  ⇒ 这 0.8–1.0 µs/job 在真活里被摊掉；且 1 项/1 块时 A 反而**更快**（0.78 vs 1.10 µs）。
- **没追平的是"摊开"**：Unity 1 块→8 块只 +0.59 µs（更多块＝更多并行，16 块甚至比 1 块更快）；
  A 从 1 块（0.79）到 2 块（2.02）就 **+1.2 µs**，之后只缓慢变慢。
- ⇒ **下一步（若要继续收）**：① 把那次 +1.2 µs 拆开（提交侧 vs 等待/合并侧）——器械已有，加个"分相"旋钮即可；
  ② 若在等待/合并侧 ⇒ 动唤醒/合并策略有据；③ 若在提交侧 ⇒ 动 tile 表物化/state 创建有据。
  两者都属于框架级改动，**收益天花板仍是 0.34–0.40 ms/步**，不建议以大于此的理由动宿主算法结构。
- ⇒ 与 doc09 §20 的结论一致：剩余赤字在**内核体（内存带宽/别名/调用约定）**，不在调度层。

### 6.8 ✅ Unity 会不会有 assist 这类问题？（官方原文 + 本机实测）

1. **Unity 的 `Complete` 本身就是 assist，而且更强**：官方 6000.2 `JobHandle.Complete` 原文——
   "The job system automatically prioritizes the job and any of its dependencies to run first in the queue, then
   **attempts to execute the job on the thread which calls the Complete method**."
   ⇒ 它先**把等待的那一个 job（及其依赖）提到队首**，再让**调用线程自己执行**。
   这正是我们那边做过、又**因正确性退回**的 **own-batch 优先**：`ENTJOY_OWN_BATCH_CLAIM=1`（探针 `directAssist` 计数即此路径，现恒 0）
   —— 打开档 6 轮内 **1 次进程提前退出 + 1 次卡死**，伴随 118–5000 次 `g_backendBatchesOutstanding` 下溢，根因是"裸指针标识批 + 池化复用"封不住
   （doc10:207 / doc11 §1）⇒ **不是"Unity 这么做而我们没做"，而是"我们做过、已知会崩"**。
   ⚠ 顺带记录：`tools/gate-run/run-native-tests.ps1` 的第三趟（`ENTJOY_OWN_BATCH_CLAIM=1`）是该实现退回后**遗留的失效段**，现在等于空跑（env 已无人读）。
2. **线程预算**：Unity manual「Job system overview」原文——"The job system ensures that there are only enough threads to
   match the capacity of the CPU cores … This differs from other job systems that rely on techniques such as thread pooling,
   where it's easier to inefficiently create more threads than CPU cores."
   **本机实测（player 回读）**：`JobWorkerMaximumCount=15`、`JobWorkerCount_default=15`（16 逻辑核）
   ⇒ Unity 默认＝**逻辑核−1 个 worker + 主线程协助** = **16 个可运行线程 / 8 个物理核**，
   与 CSBS 的 `ENTJOY_JOB_WORKERS=15 + assist` **同一暴露面**。
3. ⇒ **Unity 并不免疫**，它只是更容易被压回去：社区里"高 `JobWorkerCount` 反而更慢"就是同一现象
   （Unity Discussions「Poor performance using high JobWorkerCount」、thegamedev.guru《Excessive Multithreading Hurts Your Performance》），
   解法与我们 §6.5 的结论一致：把 worker 数按**物理核**压回去。
4. ⇒ 结论：**问题同类、机制同源**；差别只有两点：① Unity 的协助是**own-batch 优先**（我们那条实现处于回退态，`directAssist` 恒 0）；
   ② 调用时机两边都是"只在调用者阻塞期间"（我们三处调用点见 §6.10），机制上并不比 Unity 更"无界"。
   ⚠ 原先推的"15 worker 时协助线程＝第 16 个执行者 ⇒ 退化 ⇒ 应按物理核压 worker/关 assist"**已在 §6.10 被 5 rep 否证**
   （整步效应落在 ±8…±12 ms 噪声里）⇒ **不要据此改线程预算或默认策略**；
   与 Unity 对齐真正还差的只有 **own-batch 优先**这一条，而它只值 Flow 上的 −0.2…−0.6 ms/步（§6.10），要做得先解决统计分辨率。

### 6.9 ✅ "池化 job 盒子 + 缓存 trampoline"会不会引发 bug（源码事实 + 本会话实测）

**先纠正上一轮的一处定位错误**：托管路径**不是**"每次 Schedule 都 `GCHandle.Alloc`"。

| 事实 | 位置 | 含义 |
|---|---|---|
| blittable 的 job（`!IsReferenceOrContainsReferences<T>`）走 `AllocContext` → **`ContextPool`**（64B 分桶 + 4 字节长度前缀 + `Cleanup` 回池） | `NativeJobCore.cs:1202–1213`、`:974–1015`、`:1231–1240` | **ctx 本来就已经池化**，且与转译路 `RentMarshalledContext`（`:1222–1229`）**共用同一个池与同一个 `Cleanup`** ⇒ "按 Native 那样池化"**已经就是现状** |
| 只有**含托管引用**的 job 走 `AllocManagedContext` → `GCHandle.Alloc(new ManagedJobBox<T>)`，完成点 `Free` | `:1182–1186`、`:1188–1200` | 这才是"盒子"的**唯一**分支；而且这类 job 在三个 `TrySchedule*` 入口**根本进不了原生 adapter**（前置就带 `!IsReferenceOrContainsReferences<T>` 门） |
| trampoline **已按 T 缓存一次**（静态泛型 + `Marshal.GetFunctionPointerForDelegate` 在类型初始化时一次） | `:1026–1054` | "缓存 trampoline"是**空操作**（已是现状） |
| 托管 `IJobParallelFor` 的适配器是 `BatchJobFunc`：**每个 unit 一次托管回调**，回调内**在托管里**跑 `for (i=start..end) job.Execute(i)` | `:1307–1337`（关键在 `:1325`） | ⇒ 每元素成本＝**托管空循环**，不是"每元素一次 native↔managed 切换" |

**本会话实测（离机 `tools/SchedSubmitProbe`，`sc` 模式，8 worker，len=7936，park=0，median / min ns·job⁻¹）**：

| batch | 64 | 512 | 4096 | 7936 | `assist=1`（b=64） |
|---|---|---|---|---|---|
| ns/job | 7 555 / 6 414 | 8 399 / 8 132 | 6 527 / 5 510 | **5 192 / 4 939** | 7 913 / 7 370 |

单 unit（batch=len）的**项数**曲线：len=1 → 0.90、64 → 1.10、512 → 2.26、**7936 → 5.17 µs** ⇒ **≈0.4 ns/项**（托管空循环）。
同形三臂（同会话、同 8 worker、同 (7936, 64, 1000)、同空体）：**托管 7.55 µs vs 转译（游戏内）3.71 µs vs Unity 2.63 µs**；
`PROBE_ASSIST=1` 反而 **7.91 µs**（`assistTiles=254796`；这是**探针内**的微基准抖动，与 §6.10 的整步结论无关）。

⇒ **结论：池化盒子 / 缓存 trampoline 都收不回这 2×**（两项都已经在做，且都不是主导项）。
主导项是 **① 每 unit 的托管回调帧 + ② 托管逐元素循环**；要动它们只有两条路：把内核**转译**（现状就是答案），
或**减少托管进入次数**（每步少 Schedule 几次——正是 §6.7 指出的扇出杠杆）。

**若仍然要做 GCHandle 盒池化，风险清单（全是"回收/复用"类，不是性能类）**：
1. **迟到/重复归还 ⇒ 复用错盒**：ctx 指针被下一个 job 复用、旧 job 仍有 tile 在跑 ⇒ 那个 tile 读到**新 job 的字段**（静默错数据）。与刚修完的 C 项（`batchGen`）**同一失效类**，盒子也必须带代次/身份；
2. **归还前没清空 `box.Job`** ⇒ 旧 job 引用的托管对象**继续活着**（泄漏），并被下一个租户看见；
3. **双重 `Free`**（池与完成点都释放）——现存代码用 `handle.IsAllocated` 兜了一次，池化后这个兜底不再成立；
4. **安全声明仍只能在 job 完成点释放**（`ManagedCleanup:1191–1197` 注释：按 tile 释放会提前清空兄弟 tile 的读计数）——池化不解除这条约束，反而扩大了"盒子被提前复用"的窗口。

⇒ 一句话：**收益是每个 Schedule 省一次 `GCHandle.Alloc`，代价是引入一整套代次/身份校验**；而这条路只有"含托管引用的 job"才会走，
且这类 job **注定慢**（连原生 adapter 都进不去）⇒ **不建议做**。（想收托管路径就转译，或减少进入次数。）

---

### 6.10 ⛔ **否证**：assist 的整步效应（8W 与 15W 都没挺过 5 rep）

源起：上一轮 3 rep 的记录是"**8 worker（=物理核）开 assist 更快**（整步中位 −1.30 ms，3/3）、
**15 worker（>物理核）开 assist 更慢**（整步中位 +2.23 ms，最坏 +9.09，集中在 Melee）"，
并据此推出过"协助线程＝第 16 个执行者 ⇒ 应按物理核关 assist / 压 worker"。本轮把两档都补到 **5 rep**
（同会话、轮转臂序、每臂 7 条 `[M-15]` 探针、`[M-1]` 取最后窗口，证据 `n33assist8/`、`n32assist15/`）：

| 配置 | 整步 Δ = assist1 − assist0（负 = 开更快） | 更快 | median | min: a0 → a1 |
|---|---|---|---|---|
| **8 worker**（5 rep，本轮） | +12.35 / +1.08 / −0.85 / +0.38 / −1.17 | 2/5 | **+0.38**（去离群 +12.35 后 −0.24） | 116.11 → 117.46 |
| **15 worker**（5 rep，本轮） | +1.20 / −4.33 / −7.77 / +5.52 / +4.48 | 2/5 | **+1.20** | 81.78 → 80.29（−1.49） |
| 8 worker（上一轮 3 rep） | −1.30 / −0.98 / −2.08 | 3/3 | −1.30 | — |
| 15 worker（上一轮 3 rep） | +9.09 / +2.23 / −0.78 | 1/3 | +2.23 | — |

- **逐趟看**：唯一方向稳定的是 **Flow**（assist 直接作用的那一趟）——8W：+0.76/−0.72/−0.02/−0.24/−0.98（**4/5 更快**，median **−0.24 ms**）；
  15W：+0.30/−1.28/−1.26/+0.27/+0.09（2/5，median +0.09，**min 21.16 → 21.03 变好**）。
  Melee（与波循环无关，但 assist 也会执行它的 tile）两档都是噪声（8W 有一次 **+11.04** 的离群，15W 中位 +0.70）。
- ⇒ **两条结论都要改**：① "15W 开 assist 更慢 +2.2 ms" 是 **3 rep 伪影**；② "8W 开 assist 快 1.3 ms" 同样没挺过 5 rep。
  真效应只落在 **Flow ≈ −0.2…−0.6 ms/步**（量级已接近单趟噪声）= **机制有效、整步读不出来**。
- ⇒ **处置**：**不实现**"按物理核关 assist / 把 worker 压到物理核"的默认策略（前提不成立），**不动任何默认**。
  另记一笔：8W 的整步绝对水平（~117 ms）比 15W（~85 ms）**差 38%** ⇒ 单为 assist 去选 8W 本来就不成立。
- 机制事实（本轮核实，与策略无关）：assist 的调用点只有三处、且都在 `Complete` 阻塞期间
  （`JobSystem_State.cpp:1500` spin2048 每 16 次 / `:1523` spin256 每 16 次 / `:1545` block-wait 每轮 ≤16 次）；
  实现是 `TryAssistOne()` → `StealAndExecute(0)`（**通用窃取**，`ChaseLevScheduler.cpp:1118–1123`）；
  探针的 `directAssist` 恒为 0（`JobSystem.cpp:1268` 只置 0）⇒ **own-batch 优先确实处于回退态**，这是与 Unity 的唯一机制差。
- ⇒ 若还想判定 assist，必须换更高分辨率的统计量：固定步窗口、≥9 rep、以 **min** 为主统计量，
  或直接用不受 Melee 抖动影响的机制量（`assistTiles`、`[E1]` 相位）。

---

### 6.11 优化第一个杠杆：**分相器械**把"第 2 块那 +1.2 µs"指向"每 job 一次跨线程握手"

给 A 侧探针加 `_SPLIT=1`（口径与 `tools/SchedSubmitProbe` 的 `s`/`c` 一致：先 N 次 Schedule 连发计时，再 N 次 Complete 计时；两段都多窗取 min）。
batch=64、8 worker、空体、250 波 × 4 窗、n=3（`n43split/`）：

| cells | 提交 µs | 等待 µs | 提交+等待 | **交错往返**（§6.7 矩阵） | 差额 |
|---|---|---|---|---|---|
| 1 | 0.42 | 0.11 | 0.54 | 0.78 | +0.25 |
| 64（1 块） | 0.41 | 0.12 | 0.53 | 0.79 | +0.26 |
| 128（2 块） | 0.72 | 0.11 | 0.83 | **2.02** | **+1.19** |
| 512（8 块） | 1.18 | 0.11 | 1.29 | 2.54 | +1.25 |
| 1792（28 块） | 1.18 | 0.11 | 1.29 | 2.66 | +1.37 |
| 7936（124 块） | 1.34 | 0.16 | 1.52 | **3.41** | **+1.89** |

- **读法**：分相是**流水线**口径（250 个 job 连发后才一起等 ⇒ 等待几乎免费，0.11–0.16 µs）；交错是**串行**口径
  ⇒ 差额＝**每 job 一次"发布→唤醒/认领→执行→结算→叫醒等待者"的握手延迟**。这与 §279 行那条历史注释同源
  （`JobSystem_Scheduler.cpp:279–282`："单发 schedule+complete 地板 8.2 µs 里 ~6.6 µs 是**提交线程被唤醒的 worker 抢占后回不来**"）。
- **这正是 Unity 用"调用线程执行自己那个 job"消掉的那一段**（§6.8 官方原文）。我们代码侧**没有**这条路径：
  `rc<=1` 与 batch 路径都必须经 `ScheduleFastPath → FastPath → SubmitBackendAsync`（`JobSystem_Scheduler.cpp:186–215`，**把 work 发布给 worker 池**），
  调用线程一定在 `Complete` 上等一次跨线程交接。
- **已试的现成器械否掉了**：`ENTJOY_SCHED_PRIO=1`（把提交线程抬到 ABOVE_NORMAL）5 rep 配对，cells=64/128/7936 三形状差 **−0.04…+0.04 µs**
  （`n44prio/`）⇒ **空结论，不采用**（与它默认关一致）。
- **下一杠杆（已定候选，未实装）**：给"单块（`rc<=1`）且无未完成依赖"的 job 加**调用线程内联执行**
  （`Schedule` 时直接把 work 跑完并把 state 置完成 ⇒ `Complete` 立即返回），等价于 Unity 的 Complete-on-caller。
  **风险清单（必须逐条处理）**：① 语义变化——`Schedule` 不再立即返回，单块重 job（如 `zero`:1e6 元素/1 块）会在调用线程跑完
  （Unity 亦是如此，且调用者本来就在 Complete 上等，但**"Schedule 后不立刻 Complete"的调用点会被阻塞**）；
  ② 有未完成 `dependsOn` 时必须退回异步；③ 从 worker 内嵌套 `Schedule` 的情形必须排除（破坏"提交即让出"）；
  ④ 异常归属（`RecordStateException` / `SetCurrentBatchId` / 调试执行窗口）两条路径必须一致；
  ⑤ env 门控 + 原生十套件 + 同会话配对验收（判据：2 块与 124 块往返 ≥3/5 更快，且整步不回归）。

---

### 6.12 廉价杠杆清单**已穷尽**：唯一能动往返的是"worker 池规模"，其余全是否证

§6.11 把+1.2 µs 指向"每 job 一次跨线程握手"后，本轮把**所有现成 env 杠杆**在**同形探针**上做完（250 波 × 4 窗取 min，轮转臂序，每格 4 rep 配对）：

| 杠杆 | 判据（cells=128 / 1024，batch=64，8 worker） | 结论 |
|---|---|---|
| `ENTJOY_SCHED_PRIO=1`（抬高提交线程优先级） | 三形状 ≤0.04 µs，n=5 | ⛔ 空 |
| `ENTJOY_COMPLETE_SPIN`=8192 / 32768 | 2 块：**4/4，−0.21 / −0.32 µs**；**波前量级（16/28/64 块）反而 1/4、1/4、3/4（0.12 µs 量级退化）** | ⛔ **不采用**（与 §7ai/§7aq 历史一致：拉长自旋只对小 batch 有用，对波前有害） |
| `ENTJOY_WAKE_POLL=1` / `+WAKE_POLL_NEED=1` | 3/4、3/4，中位 **−0.07 µs** | ⛔ 噪声内 |
| `ENTJOY_SPIN_HOT_US=0` / `ENTJOY_SPIN_NEEDS_WORK=1` | 3/4、1/4，中位 −0.03 / +0.12 µs | ⛔ 噪声内 |
| `ENTJOY_PHYSCAP_SMALLJOB=0` | 3/4、1/4，中位 −0.08 / +0.06（且 shape 未触发该门：target 8 = 物理核 8） | ⛔ 不适用 |
| F2 `TILES_UNIFORM` / F4 `TILE_FASTPATH` | **不适用**：门是 `cs ≤ kClaimSpanThinElems=16`（`JobSystemInternal.h:201`），探针 cs=64、游戏波前 ~1953 元素/tile 都是**厚 tile** | ⛔ 不适用 |
| JCC | **不在路径**：显式 `batch>0 ⇒ cs=reqBatch`（`JobSystem_Scheduler.cpp:997`） | ⛔ 不适用 |
| assist | §6.10 已否证 | ⛔ 空 |

**唯一移动了往返的东西＝ worker 池规模**（同配置、空体、n=2，`n49wscale/`）：

| 形状 | 8 worker | 2 worker | Δ |
|---|---|---|---|
| 128（2 块） | 2.03 / 2.19 | **1.51 / 1.75** | −0.48 µs |
| 1024（16 块） | 2.55 / 2.59 | **1.73 / 1.74** | **−0.83 µs** |

- 体**为空**⇒ 这个差不是算力，是"**8 个 worker 为一个 2–16 块的小 job 一起醒着/轮询/抢注入器**"的代价
  （`ResolveWorkerTarget` 已把 target 夹到 `min(池, rc)`，但 `WakePending()` 是 `notify_all`；
  而 §上表显示"不 notify"的现成机制（wake-poll）在本探针下已经等于常态 ⇒ 没有剩余空间）。
- ⇒ **结论（本轮）：派发侧的廉价杠杆已穷尽**。要把 1.33× 收掉只能动"**小 batch 时不让整个池参与**"或"**调用线程执行自己那份工作**"，
  两者都是结构性改动，且天花板仍是 **0.34–0.40 ms/步**（§6.7 折算，低于布局噪声底）。
- ⚠ **器械教训（本轮新增）**：`CPUBATTLE_AUTOEXIT=22` 只够 8 worker 跑到第 120 步（探针触发点）；
  4 worker 时 22 s 只到 **第 96 步** ⇒ 探针**根本没跑**（`n47workers/A-w4-r1` 0 条 vs `n48w4long` 3 条，`AUTOEXIT=70` 后到第 288 步）。
  ⇒ **凡是改 worker 数的臂，`AUTOEXIT` 必须按 worker 数放大**，否则会拿到"静默空结果"。

**第二轮补充（claim 几何 / 延迟唤醒也是空）**：`ENTJOY_CLAIM_SLICE=1`（2/4、2/4、0/4，中位 +0.01/+0.26/+0.19）、
`ENTJOY_CLAIM_GUIDED=1`（1/4、1/4、0/4，+0.04/+0.21/+0.41）、`ENTJOY_DEFER_WAKE=1`（1/4、1/4、2/4，+0.16/+0.15/+0.02）
—— cells=128/1024/4096、n=4 配对（`n52claim/`）⇒ **全部中性或更差**。至此**11 个现成 env 杠杆 + 2 个不在路径的机制**都已出结论。

**worker 参与曲线的形状与"为什么它不能变现"**（同配置 `AUTOEXIT=70`、空体、n=2，`n49wscale/` + `n53curve/`）：

| 形状 | 2 worker | 4 worker | 6 worker | 8 worker |
|---|---|---|---|---|
| 128（2 块） | 1.51 / 1.75 | 1.64 / 2.15 | 1.90 / 2.06 | 1.96 / 2.06 |
| 1024（16 块） | 1.73 / 1.74 | 1.88 / 2.26 | 2.42 / 2.53 | 2.58 / 2.62 |
| 4096（64 块） | — | 2.35 / 2.73 | 2.80 / 3.02 | 2.83 / 2.90 |

- 曲线单调：**限制参与**在空体上值 **0.5–0.9 µs/次**（16 块：2w 1.74 → 8w 2.60）。
- ⚠ **但它不能变现**：这是**空体**性质——空 job 里"多一个醒着的 worker"只有争用没有算力，所以"少参与"看着赚；
  真实波前每波 ~28 µs 真活、~18 个 tile，**少一半 worker 就少一半算力**，而多出来的 idle worker 并不在关键路径上
  ⇒ **"限参与"对真 job 是净亏**。而且这条政策**已经存在**（`ApplyPhysCoreCapForSmallJob`，按"每 worker 元素数 ≤256"封顶，默认开），
  只是在本机 8 worker = 8 物理核时不生效。
- ⇒ **最终判定（本案可下的最强结论）**：剩下的 1.33× ＝ 每 job 一次跨线程握手（±1.2 µs，Unity 0.59），
  它的**便宜收法已穷尽**；能动的只有两条**结构性**路径——① 调用线程执行自己那份工作（Unity 语义，改 `Schedule` 返回语义）；
  ② 减少每步 job 数（持久内核，改宿主算法结构）。两者的收益天花板都是 **0.34–0.40 ms/步**（≈整步 0.3%），
  **低于本仓两构建布局噪声底（±1.1…±3.5 ms/步）** ⇒ 按仓库自己的判据（09 §40.3：先在夹具里做出"环比 <1 且可观测"的臂），
  **本轮不实装**；要动必须先由使用者确认愿意承担语义变更。

---

### 6.13 结构性候选①（"调用线程执行自己那份工作"）**已试作原型：破坏既有不变量 ⇒ 连同①一起收口**

按 §6.11/§6.12，唯一剩下的是两条结构性路径。本轮把①做成 **env 门控原型**（`ENTJOY_INLINE_OWN=1`，默认关）：
把 `FastPath`（单工作项路径：`rc<=1` / `length<=64` / 单 `IJob`）改成**在调用线程上内联跑完**
（`RunFastPathInline` 与异步窗口逐项同构：`DebugBeginExec` / `SetCurrentBatchId` / `work()` / `cleanup` / `CompleteState` / `ReleaseState`），
于是 `Complete` 立即返回、零跨线程交接。

**结果：原生套件立刻红。**

| 配置 | `JobSystemTests` | 现象 |
|---|---|---|
| `ENTJOY_INLINE_OWN=0`（默认） | **rc=0 / 26 s / 全 PASS** | 基线（本文件所有 `src/NativeDll` 改动都是零，见下） |
| `ENTJOY_INLINE_OWN=1`（首版，无限定） | **rc=1 / 13 s 早退** | `[JOBINLINE] executed=7` ⇒ 收尾检查 `FAIL system broken after worker-thread shutdown attempt` |
| `ENTJOY_INLINE_OWN=1` + **主线程守卫**（`g_mainThreadId` 且非关停） | **仍然 rc=1 / 13 s** | `executed=7 skipped=0` ⇒ **7 次都在主线程**，守卫没帮上 |

- ⇒ 失败**不是**"worker 内嵌套"（风险清单③，守卫已排除），而是**绕过了后端退役记账**：异步窗口的 lambda 只做 `CompleteState`，
  **真正的退役/引用平衡由 `SubmitBackendAsync` 的包装层做**（`FastPath:216` 注释"failure path performs cleanup and terminalization"）。
  只补一次 `ReleaseState` 并不等价 ⇒ 收尾的"worker 线程关停尝试"检查判系统损坏。
- ⇒ **原型已回退**（`git checkout src/NativeDll/JobSystem_Scheduler.cpp` ⇒ 工作树零改动，基线套件复测 **rc=0**）：
  一个"打开就红"的 env 门是地雷，不入库；做法与结论留在本文。
- **判定**：要让①可用，必须**完整镜像后端的退役协议**（批次在途计数、state 引用平衡、retire 通知、异常归属）——
  改动面远大于它值的钱（**0.34–0.40 ms/步 < 布局噪声底 ±1.1–3.5 ms/步**）。按仓库判据（09 §40.3：先做出"环比 <1 且可观测"的臂），**① 不实装**。

**⇒ 轴三「派发侧」结案（四句话）**：
1. 现状差距＝**1.33×（min）/ 1.37×（median）**，绝对 **0.84–0.99 µs/job**（交错 7 rep，A 侧多窗取 min，逐 rep 1.28–1.41）；
2. 机制＝**每 job 一次跨线程握手**（交错往返 −（提交+等待）= +0.26 µs（1 块）→ +1.89 µs（124 块）；Unity 的同一笔只值 +0.59）；
3. 杠杆台账：**11 个现成 env + 2 个"不在路径"的机制（F2/F4、JCC）+ 1 个结构性原型**，全部出结论；唯一能动往返的 worker 池规模是**空体性质**、不能变现；
4. 剩余收益 **0.34–0.40 ms/步**（≈整步 0.3%）< 布局噪声底 ⇒ **不实装**。
   要真正收窄，需要**明确授权承担框架语义变更**，且新判据必须**在夹具里可观测**（不能靠整步 ±8 ms 的抖）。

---

### 6.14 代码清理记录（2026-10-04，用户指示"清理代码"）

| 项 | 处理 | 依据 / 约束 |
|---|---|---|
| `directAssistClaims` 等 **6 个死字段**（`directAssistClaims` / `exhaustedTickets` / `scheduleToPublishEwmaNs` / `publishToFirstMainClaimEwmaNs` / `publishToFirstWorkerClaimEwmaNs` / `queueLockWaitEwmaNs`） | **只加"死字段"标注，不删** | 全仓只有"赋 0"、**无一处自增**（`JobSystem.cpp` 的 `ResetStatsSnapshot` 段）——own-batch / 协作块执行器那次实验的 append-only 影子（`docs/archive/superpowers/plans/2026-07-16-…`）。⚠ 本结构是 **C ABI**：`Exports.h` 同序 + C# `NativeJobScheduler.cs` 同序 + **Unity 侧 port**（`Assets/Scripts/Jobsystem/EntJoy.Port/NativeJobScheduler.cs`）镜像同一布局 ⇒ 单方面删会错位；要删必须**两侧同步重建**。夹在中间的 `publishToCompletionEwmaNs` 是**活字段**（`GetStatsSnapshot` 载入并参与打印），不能一起删 |
| `tools/gate-run/run-native-tests.ps1` 的 own-batch 两趟回归 | **删除** | 那两趟设 `ENTJOY_OWN_BATCH_CLAIM=1` / `ENTJOY_OWN_BATCH_TILES=64,0`，而**这些 env 在 HEAD 已无人读** ⇒ 永久全绿、什么都没验证，会让人误以为新路径有覆盖（该实现已因正确性退回：doc10:207 / 11 §1 / 本文件 §6.8）。清理后 runner = 2 趟（`TILE_RUN` off/on）× 10 套件，**已复跑全绿** |
| `tools/SchedSubmitProbe/Program.cs` 的 `directAssist` 注释 | **改为"死字段、恒 0"** | 原注释写"`ENTJOY_OWN_BATCH_CLAIM=1` 时应 >0"，而该 env 已不存在 |
| `tools/gate-run/n31shape/*`（本轮新写的 driver）、探针的 `_CELLS12/_BATCHES/_REPEATS/_SPLIT` | **保留** | 都是本轮结论的证据器械，且已写进 §6.7/§6.11/§6.12 |
| 结构性原型 `ENTJOY_INLINE_OWN` | **已整体回退**（`git checkout`），CSBS 侧 DLL 也已从干净源码重建 | 见 §6.13：默认关也会坏原生不变量；"打开就红"的 env 门不入库 |

- **`src/NativeDll` 净行为改动 = 0**：本次只加注释（3 个 tracked 文件：`JobSystem.h` / `Exports.h` / `NativeJobScheduler.cs`），随后**原生十套件 2 配置复跑全绿**、CSBS 侧 `NativeDll.dll` 由干净源码重建。
- **当前部署指纹（CSBS `.godot\mono\temp\bin\Debug\`，2026-10-04 收尾态）**：`NativeDll.dll = A190D51A`、`NativeTranspiled.dll = 282F0877`
  （此前带原型的构建是 `D77425FA / 748DF9A3`，已废弃；更早的 `0C144D76 / D0B80CF5` 属 C/G 项验收态）。
  ⚠ 重建会换 kernel key（RVA）空间 ⇒ `[JOBPERKEY]` 的 key 跨构建不可比（§6.5 第 4 条）。
- `tools/` 目录**未被 git 跟踪**（`git ls-files tools/gate-run` 为空）⇒ 上面的 runner/probe 清理**只在本地生效**，不会随 clone 带走；证据目录同理。

---

### 6.15 托管↔原生边界的**机制**核实（IL 级）：**不是 P/Invoke**；以及"能借鉴什么"的结账

**为什么换渠道**：`extract` 对 github / raw / jsDelivr / docs.unity3d.com 本轮仍**全部失败**（§1.1 追记），
且 `Modules/ManagedKernel/Managed/Jobs/*.cs` 只是**托管半边**（原生调度器在 `Runtime/Jobs/JobSystem.h`，不在公开仓）。
替代渠道 = **本机装的那一份 Unity**（与基准臂同版本），可信度更高：

| 渠道 | 取到的东西 | 为什么比 master 参考源更强 |
|---|---|---|
| `C:\Program Files\Unity\Hub\Editor\6000.3.2f1\Editor\Data\Managed\UnityEngine\UnityEngine.CoreModule.dll`（SHA256 `38D13A9B…F1E3`，§7）经 `dotnet-ildasm` 全量反汇编 | `JobHandle` / `JobsUtility` / `JobRanges` / `JobScheduleParameters` / `ScheduleMode` 的**字段布局、方法体 IL、绑定属性 blob** | 反的是**我们实际在跑的那个二进制**；master 参考源是下一个版本 |
| `Library/PackageCache/com.unity.entities@e90944159b94/Unity.Entities/IJobChunk.cs` | DOTS 的 chunk 派发**真源码**（调度 / 窃取 / 反射数据缓存 / release 下的安全检查摘除） | 本工程实际依赖的那一版包源码 |

#### 6.15.1 边界是 icall，不是 P/Invoke（IL 证据）

类上的 `.custom` = `[NativeType(Header = "Runtime/Jobs/ScriptBindings/JobsBindings.h")]` + `[NativeHeader("Runtime/Jobs/JobSystem.h")]`（IL 4821–4822）——
**这两个字符串就是原生实现的头文件路径**，也顺带确认：C# 从 `Runtime/Jobs/` 搬去了 `Modules/ManagedKernel/Managed/Jobs/`，**原生头没搬**。

方法体全无、只挂绑定属性（属性 blob 解出的实参，逐字）：

| 托管方法 | 属性实参（IL 行） | 备注 |
|---|---|---|
| `JobHandle.ScheduleBatchedJobs()` | `NativeMethod("ScheduleBatchedScriptingJobs", IsFreeFunction=1, IsThreadSafe=1)`（4446） | 原生名**不叫** `ScheduleBatchedJobs` |
| `JobHandle.Complete()` → `ScheduleBatchedJobsAndComplete` | `NativeMethod("…AndComplete", IsFreeFunction=1, IsThreadSafe=1, ThrowsException=1)`（4450） | `ThrowsException` ⇒ 异常要跨边界回来 |
| `JobHandle.IsCompleted` → `…AndIsCompleted`（4454）、`CompleteAll` → `…AndCompleteAll`（4458） | 同上 | `IsThreadSafe=1` 与 2020 年论坛那条 "can only be called from the main thread" **不矛盾**：那是老绑定层的行为，本版属性明标线程安全 |
| `JobsUtility.Schedule` | `FreeFunction("ScheduleManagedJob", ThrowsException=1, IsThreadSafe=1)`（4869） | 体是 10 字节包装 → `Schedule_Injected`（4870–4879） |
| `JobsUtility.ScheduleParallelFor` | `FreeFunction("ScheduleManagedJobParallelFor")`（4882） | |
| `JobsUtility.ScheduleParallelForDeferArraySize` | `FreeFunction("…ParallelForDeferArraySize")`（4897） | **长度调度时未知**的那条腿 |
| `JobsUtility.GetWorkStealingRange` | `NativeMethod(IsFreeFunction=1, IsThreadSafe=1)`（4865） | 只有"窃取"才跨边界 |
| `JobsUtility.PatchBufferMinMaxRanges` | icall（4942）+ `[Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]`（4943） | release 下**每 chunk 这段安全补丁被整个摘掉** |

⇒ **不是 `DllImport`/`pinvokeimpl`**（整个 dump 里 Jobs 命名空间下 `extern` 命中 **0** 条），而是**生成式绑定**：Mono 走 **icall 表**、IL2CPP 走**直接 C++ 链接**，都不经过 P/Invoke 的桩。
`Schedule` 里那层 `_Injected` 是 Mono 对"**按值返回 struct**"的经典约定，不是我们这种返回 `IntPtr` 的签名会遇到的东西。

#### 6.15.2 我们这边已经是对等物 ⇒ 边界不是差距来源

| 维度 | Unity（IL / 包源码） | EntJoy（HEAD） | 判定 |
|---|---|---|---|
| 跨边界机制 | icall（`[FreeFunction]`，无 marshalling、无桩） | 启动期 `NativeLibrary.GetExport` 取址，热路径走**缓存函数指针** `delegate* unmanaged[Cdecl]`（`NativeJobCore.cs:109–162`、`:427–545`） | **对等**，且我们连 `_Injected` 那层都不需要 |
| 每类型元数据 | `JobStruct<T>.jobReflectionData` = `SharedStatic<IntPtr>` + `[BurstDiscard] Initialize()`（IL 3677–3692）；`IJobChunk` 同构（`IJobChunk.cs:341–348`） | 静态泛型类 `JobDelegateCacheFor<T>.Cache` / `ParallelForBatchDelegateCacheFor<T>.Cache`（`NativeJobCore.cs:1026–1050`，热路径 `NativeJobScheduler.cs:320/360/435`） | **对等**（热路径都零查表；`ConcurrentDictionary` 只在自定义委托兜底路） |
| 每次 Schedule 的额外托管开销 | 多一次 `JobValidationInternal.CheckReflectionDataCorrect<T>`（IL 3580） | 无 | 我们更少 |
| 首次认领的摊薄 | `JobRanges.StartEndIndex` 由原生写好，`GetJobRange` **纯托管读指针**（IL 4826–4862）；只有窃取 icall | `step = clamp(tileCount/workers, 1, capEff)`（`ChaseLevScheduler.cpp:845–847`）⇒ 一次原子领走 `step` 个 tile；`ENTJOY_CLAIM_BLOCK=1` 更是每 worker 一整块（`:848–856`） | **对等**（同形臂 124 块 / 8 worker ⇒ `step=capEff=4` ⇒ 每批 ~31 次原子，对逐块认领省 4×；Unity 首段静态发放 ~8 次 icall + 少量窃取） |
| 句柄表示 | **24 B 值类型**：`{uint64 jobGroup; int32 version; int32 debugVersion; IntPtr debugInfo}`（IL 4313–4316）；`Complete()` 的**唯一**托管比较点 = `jobGroup == 0`（IL 4324–4337） | `HandleState*` **不透明指针 + 引用计数**（`Exports.cpp:456/470`，`g_liveHandleStates`） | **结构性差异**，但方向已定（§3/§5.5 的代次比对就是 Unity 这条）⇒ 不新增动作 |
| 多句柄等待 | `CompleteAll(2/3/`NativeArray<JobHandle>`)`：`localloc` 栈数组 + **一次** icall + `initobj` 清零（IL 4339–4429） | 无批量腿（逐句柄 `Complete`） | 见 §6.15.3 📋 |
| `ScheduleMode` 取值 | `Run` / `Batched`【**已 Obsolete**：用 `Parallel` 或 `Single`】/ `Parallel` / `Single`（IL 4801–4809）；`JobType` 整体 Obsolete（4811–4817） | 我们自有形状 | 与 §2.2/§4③ 一致 |

#### 6.15.3 "能借鉴什么"的结账

| 候选 | 判据 | 结论 |
|---|---|---|
| 边界改成 icall 式直呼 | 我们已是缓存函数指针直呼（6.15.2 第 1 行） | ⛔ **无可摘** |
| 每类型元数据换 `SharedStatic` 式静态缓存 | 已是静态泛型缓存 | ⛔ **无可摘** |
| **首段范围免原子 / 静态分区 + 只对超额窃取**（Unity `JobRanges` 那条） | `step = clamp(tileCount/workers, 1, capEff)` 已是同一效果，`ENTJOY_CLAIM_BLOCK` 是其极端形且**早已试过**（`:848–856`，默认关） | ⛔ **已在实现里**（我原本把这条当"未测的新杠杆"，读代码后**自我否掉**） |
| `CompleteAll`（K 个句柄一次等） | 我们的形状是"每 job 一个句柄、逐次 `Complete`"（§6.7 同形臂）；只有**同时等 K>1 个句柄**的调用点才可能受益 | 📋 本仓当前**无此形状**；将来若有，Unity 有现成先例 |
| `ScheduleParallelForDeferArraySize`（长度 execute 时才定） | 显式 batch 路 `ResolveChunkSize` 早返回 ⇒ 不做 tiling 决策（§6.7 行 16） | 📋 不适用（将来支持 "count-at-execute" 才有意义） |
| release 摘掉每 chunk 安全补丁 | 我们无该层 | ⛔ 无对象 |
| 11 个 env + `INLINE_OWN` 原型 | §6.12 / §6.13 | ⛔ 已出结论，不重走 |

**结论**：把 Unity 的作业系统**读完**之后，能搬的**都已经在仓里**（边界机制、每类型缓存、认领摊薄），
剩下两条是"我们的调用形状用不到" ⇒ **没有一条落在 0.34–0.40 ms/步这条噪声底之上**，与 §6.12 / §6.13 同结论：**派发侧无动作**。
本节的价值不是新杠杆，而是**用本机二进制的 IL 把"边界机制"这条彻底排除**——1.33–1.37× 的差额只能仍落在原生调度器
"发布→唤醒→认领→结算→叫醒等待者"那条**每 job 一次**的握手上（§6.11）。

---

## 7. 出处与复核命令

**官方页面**：[Complete (6000.3)](https://docs.unity.com/en-us/engine/6000.3/script-reference/unity/jobs/jobhandle/complete)、
[Complete (2018.1)](https://docs.unity3d.com/2018.1/Documentation/ScriptReference/Unity.Jobs.JobHandle.Complete.html)、
[CheckExistsAndThrow](https://docs.unity.com/en-us/engine/6000.7/script-reference/unity/collections/lowlevel/unsafe/atomicsafetyhandle/checkexistsandthrow)、
[Copying NativeContainer structures](https://docs.unity3d.com/6000.2/Documentation/Manual/job-system-copy-nativecontainer.html)、
[IJobParallelFor](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Unity.Jobs.IJobParallelFor.html)、
[Improving job system performance scaling in 2022.2 – part 2](https://unity.com/blog/engine-platform/improving-job-system-performance-2022-2-part-2)。

**源码（master 路径 `Modules/ManagedKernel/Managed/Jobs/`）**：
[AtomicSafetyHandle.bindings.cs](https://github.com/Unity-Technologies/UnityCsReference/blob/master/Modules/ManagedKernel/Managed/Jobs/AtomicSafetyHandle.bindings.cs)、
[JobHandle.bindings.cs](https://github.com/Unity-Technologies/UnityCsReference/blob/master/Modules/ManagedKernel/Managed/Jobs/JobHandle.bindings.cs)、
[IJobParallelFor.cs](https://github.com/Unity-Technologies/UnityCsReference/blob/master/Modules/ManagedKernel/Managed/Jobs/IJobParallelFor.cs)。

```powershell
# 检索入口（anysearch 优先）
python "$env:USERPROFILE\.dsh\skills\anysearch\scripts\anysearch_cli.py" search "<query>" --max_results 6
python "$env:USERPROFILE\.dsh\skills\anysearch\scripts\anysearch_cli.py" batch_search --query "..." --query "..."

# extract 对 github / docs.unity3d.com 当前不可用（§1.1）；替代：GitHub API 定位真实路径 + raw 直取。
# 本轮取原文的临时脚本在 %TEMP%\dsh-anysearch\（未入库）；两个关键路径：
#   https://api.github.com/repos/Unity-Technologies/UnityCsReference/git/trees/master?recursive=1
#   https://raw.githubusercontent.com/Unity-Technologies/UnityCsReference/master/Modules/ManagedKernel/Managed/Jobs/<file>.cs

# 本文件涉及的本仓位置（只读核对）
git grep -n -i "PENDINGTASKS-WRAP\|OUTSTANDING-UNDERFLOW"          # 期望：只剩 doc10 那一行
```

```powershell
# §6.15 的**离线**渠道：本机 Unity 二进制 IL + 本工程 DOTS 包源码（extract 全挂时的替代，见 §1.1 追记）
$env:DOTNET_ROLL_FORWARD='LatestMajor'   # dotnet-ildasm 0.12.2 声明 3.0 运行时；本机只有 5.0+ ⇒ 必须 roll-forward
$dll='C:\Program Files\Unity\Hub\Editor\6000.3.2f1\Editor\Data\Managed\UnityEngine\UnityEngine.CoreModule.dll'
(Get-FileHash $dll -Algorithm SHA256).Hash
#   期望 38D13A9BA1121180DFC5DBCCB11B3F36E1B92E16C513E6A8E7FF2A457938F1E3（D:\Unity\6000.3.11f1 那份是 BAA2DFC3…，别混用）
dotnet-ildasm $dll -o "$env:TEMP\ujob\CoreModule.il" -f        # 2 s / 19 MB / 329351 行
Select-String "$env:TEMP\ujob\CoreModule.il" -Pattern '^\.class .*Unity\.Jobs'
#   逐条核对点：JobHandle 4310+ / JobRanges 4793 / ScheduleMode 4801 / JobType 4811 / JobsUtility 4819+
#   属性 blob 的行尾注释已把字符串解出来（如 "ScheduleBatchedScriptingJobs"、"Runtime/Jobs/JobSystem.h"）
# 注意：`-i <Type>` 选择器在本机静默产出 0 字节文件 ⇒ 只能全量 dump 后再 Select-String

# DOTS 的 chunk 派发真源码（本工程实际依赖的那一版；`ScheduleInternal` / `GetWorkStealingRange` / `JobChunkProducer<T>`）
"E:\UnityProject\TestProject\TestProject\Library\PackageCache\com.unity.entities@e90944159b94\Unity.Entities\IJobChunk.cs"
```

---

## 8. 交接提示词（可直接粘贴到新会话）

```text
仓库：E:\GODOT\Project\EntJoy（Godot + C#/原生混合 ECS，自带原生 JobSystem）。
先读 docs/gridsearch/10（状态收口）、11（Unity 对照 + own-batch 判死）、12（Unity 版本号机制的源码级核实 + 代次句柄设计）。

已定的结论（不要重走）：
1) "等待者直认自己那批的 tile"（own-batch）**因正确性退回**（打开档 6 轮内 1 次进程提前退出 + 1 次卡死 + `g_backendBatchesOutstanding` 下溢；
   doc10:207 / 11 §1）。相关 env 已无人读，`run-native-tests.ps1` 里那两趟回归已删（12 §6.14）。
2) Unity 的失效检测 = 中心记录 + 每句柄存版本副本 + 回收时递增 + 访问时掩码比对（12 §2.5，源码+manual 双证）；
   对应我们的做法是给 BatchStorage 加代次（不是 BatchState：ReleaseBatchStorage 会整体重建 batch，12 §3.2）。
3) **C 项已实装并验收**（12 §3.3）：BatchStorage.generation + 令牌携带 batchGen + ChaseLevTaskDone 校验；
   探针 `[JOBGEN] staleSettleDropped / pendingTasksWrap` 每次 Shutdown 一行；正向用例
   TestStaleSettlementRejectedByGeneration 证明"异代次被拒、同代次照常"。
   验收：单套件 3/3 轮 81 PASS、304×3 次 Shutdown 全 0、十套件 4 配置 rc=0。
4) **轴三"派发侧"已结案（12 §6.7–§6.13，不要重走）**：
   - 旧口径 `sc` 8.72 vs Unity 2.43 = 3.60× **不成立**：8.72 µs 是**托管路径**的离机探针（`tools/SchedSubmitProbe` 的 job 未转译），
     且两侧 (长度, 内批, 次数, 体量) 都不同形；
   - **真同形 = A 3.66 µs vs Unity 2.67 µs ＝ median 1.369× / min 1.327×**（交错 7 rep、A 侧 250 波×4 窗取 min）；
   - 机制＝**每 job 一次跨线程握手**（交错往返 −（提交+等待）= +0.26（1 块）→ +1.89 µs（124 块）；Unity 同一笔 +0.59）；
   - 杠杆台账：**11 个现成 env ＋ F2/F4 与 JCC（都不在路径）＋ 结构性①（试作即坏原生不变量）** 全部出结论；
   - 剩余收益 **0.34–0.40 ms/步 < 布局噪声底 ±1.1–3.5 ms/步** ⇒ **不实装**。要动必须"使用者明确授权 + 新判据在夹具里可观测"。
   - **边界机制也已排除（12 §6.15，本机二进制 IL 级）**：Unity 的 `JobHandle`/`JobsUtility` 是 `[NativeMethod]`/`[FreeFunction]` 的 **icall**
     （**不是** P/Invoke），而我们热路径是**缓存函数指针直呼** + **静态泛型元数据缓存** ⇒ 两边同构（连 `Schedule` 的 `_Injected` 那层我们都不需要）；
     `CompleteAll` / `ScheduleParallelForDeferArraySize` 属于"我们的调用形状用不到"。⇒ **1.33–1.37× 不能归给托管↔原生边界**。
   - 下一候选不在派发侧，而在**内核体**（doc09 §20：内存带宽/别名/调用约定）。

测量纪律（本轮新增三条，务必沿用）：
- **形状/微基准探针一律"多窗取 min"**（`ENTJOY_JOB_TILE_TRACE` 那类单窗 ≥1000 波的形状矩阵**不耐停顿**：
  一次 ~15–19 ms 的 GC/OS 停顿会把整条线抬成"19 µs/波"，看起来像形状病态——本轮为此做过一次自我更正，12 §6.7）；
- **改 worker 数的臂必须放大 `CPUBATTLE_AUTOEXIT`**（4 worker 时 22 s 只到第 96 步 ⇒ 第 120 步的探针**静默不跑**）；
- **看探针是否真跑到**：解析前先确认 `[M-19]`/`[M-15]` 行数，别把"空结果"读成"无差异"。

测量纪律：单套件一轮 ~26s；判挂死必须 ENTJOY_TEST_WATCHDOG=1（45s 无 PASS 打印现场并 rc=3），
不要用外部 85s 超时；绝对 ms/µs 跨会话不可比，只引用同会话位置平衡（A,B,B,A）配对结论，min 为主统计量；
重建 NativeDll/NativeTranspiled 后必须重推 tools/gate-run/derive-jobkeys.ps1。
```
