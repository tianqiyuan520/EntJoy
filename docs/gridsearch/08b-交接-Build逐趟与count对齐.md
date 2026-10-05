# 交接：Build 逐趟测量与 `count` 对齐（2026-10-01，自包含）

> ⚠ **2026-10-05 追记**：本文的 F5（`ENTJOY_TILE_RUN`）与全局器械 `ENTJOY_CLAIM_SLICE` / `ENTJOY_TILE_STRIDE` **均已删除**
> （切片机制保留，F6 在用 ⇒ 认领几何改走 `ClaimPolicy` / 批表第 4 字段 / F6）。
> **本文的 Build 逐趟口径仍然有效且是新会话的第一件器械**：两侧 6 趟列名一一对齐（A 侧 `CPUBATTLE_DIAG_BUILDPASS=1` 的 `[M-19]`，
> B 侧 CSV 的 `M4,build,{zero,count,prefixPartial,hostRewrite,prefixFinal,place}_ms`）。当前门控权威表：`docs/public/Gates-and-Flags.md`。

> 新会话只需读这一份 + 必要时抽查 `docs/gridsearch/08-…md` 的对应小节。所有数字都标注了来源小节与口径。
>
> ⚠ **2026-10-02 状态更新（读本文档前先看）**：文中的绝对数字**是"F2/F4 提默认之前"的旧世界**。
> 09 §18 把 F2（等宽 GeneralRange 不物化 tileBuffer）+ F4（每-tile 开销提到每批）**提为默认并按薄 tile 门控**后：
> 对齐档整步 **177.23 → 163.31 ms（−13.90，3/3）**，复验与 Unity **1.005×（打平）**；
> `count` 从 2.71 → **0.95**（本文档 §2.2 的 1.2754/1.291 已过期）。
> 09 §20 又逐条对齐了两侧内核源码与粒度：剩余 `zero`/`count`/`place`/`Integrate` 四项**都不是 JobSystem 调度问题**
> （本文档 §3 的"已证否"表已补上这三条新结论）。**§2.1/§2.2 的旧数字只作历史对照用。**
>
> ⚠⚠ **2026-10-02 第二轮更新（09 §29–§35，先读那一节再读本文）**：
> ① **`zero` 的 store 策略推断被否证**：可验证 codegen 的微基准显示 A 的真实路径（CRT memset/`rep stosb`）**比 B 的 AVX2 形状快 3%**
> （冷态 97.6 vs 100.9 µs / 1,404,932 B）⇒ **删掉本文档里一切"B 的存储策略更好"的说法**（§29）。
> ② **`Integrate` 的对齐档赤字拆开了**：同会话实测，把**认领跨度**从 4 tile 抬到 16 tile（声明仍 `:64`）⇒ **4.91 → 3.41 ms（−31%）**（§31.1）；
> 而**空体内核控制实验**证明 cs=64 时我们的框架仪式 **6.5 ns/工作项 vs Unity 7.0 = 平手** ⇒ 赤字在**内核调用层**不在调度层（§31.2）。
> ③ **派发地板有了同形状同窗口实测**（Unity 侧新增 `M4DISP` 臂 + IL2CPP 重编）：单发空 `IJob` EntJoy **1.08 µs** vs Unity **2.90–5.55 µs（中位，A 快）**；
> 但**连发 1000 个小 job 我们慢 2.6×**（0.84 vs 0.32 µs/job；折算 ≈0.21 ms/步）（§33）。
> ④ **两条测量纪律**：计时期间禁止并发（否则中位被抬高，主判据改用 min）；**harness 会泄漏 Godot 进程**（本轮清出 30+ 个）（§34）。
> ⑤ **撤回**：本文档若引用"cs=64 时我们慢 1.4×"，那是污染读数 —— 干净重测为**平手**（§33.4）。
>
> ⚠⚠⚠ **2026-10-02 第三轮更新（09 §36–§39，**先读那一节**，它推翻了上面 ③④ 的机理与 §35 的优先级）**：
> ⑥ **③④ 的机理作废**：那 30+ 个"泄漏的 Godot"是**尸体**（28 个全部 `Threads=0 / Handles=0 / 合计 0.9 MB`，
> `CommandLine` 空、`GetOwner` rc=2、`taskkill` Access denied）⇒ **不耗 CPU**，"各自保留 worker 池偷核"的说法作废（§36.1）。
> ⑦ **"机器从不安静"作废**：负载仪器自己在测自己 —— `$p.CPU` 单次访问 **10.5 ms**（381 进程 ⇒ 单次快照 ~9 s 墙钟）
> 而分母只有 0.6 s ⇒ 每进程核数**虚高 ≈14×**。真实负载是 **1–3 核**（`Verifier/5.08` 真值 ≈0.36）。改用系统计数器（§36.2）。
> ⑧ **最大的那条：B 的窗口与 A 的窗口在步号上不相交**。`lastStart` 是**最后窗口的起始步**（实测 windows 26/32/32/31
> ⇒ sum 121 ⇒ lastStart 90 ⇒ A 的窗口是 [90,121)），而 `RunB` 用 `lastStart-61` + `M4_STEPS=40` 测 **[29,69)**。
> 因为 **Melee 的代价是步号的函数**（warm 0/30/60/90/120/160 → B melee 108.6/114.8/125.9/119.6/121.3/126.0，
> 其它段全平），这**造出了一处不存在的 Melee 赤字**。修好后 Melee 回到**打平（B/A 0.988）**（§36.6/§38）。
> ⑨ **对齐档赤字重测为 ≈1.6%**（逐 rep 配对 B/A = 0.976/0.993/0.984），不是 1.0%；剩下均分给
> **Build 段（+0.86 ms）** 与 **Integrate（+0.75 ms）**（§38）。
> ⑩ **§31.1 的 −31% 不复现**：那是被 ⑦ 的坏尺子抬起来的。用修好的尺子 + 4 rep 旋转逐 rep 配对，
> 声明 1024 元素跨度对 Integrate 是 **−5.3%、4/4**，而**整步无效应**（中位 −0.27 ms，2/4）⇒ D1 降级为能力（§37）。
> ⑪ **D2 上限下调到 ≈0.22 ms**：原位标定（4× 少调用只买到 0.165 ms）⇒ 边际每次内核调用 ≈**56 ns**，
> 而不是微基准反推的 ~400 ns（§37.4）。
> ⑫ **新第一优先**：**Build 段的每元素成本**（zero/count/pf/place ≈ B/A 0.70–0.87，合计 ≈1.7 ms）（§39）。
>
> ⚠⚠⚠ **2026-10-02 第四轮更新（09 §40–§43；⑫ 那条"新第一优先"已被追到底并**关闭**）**：
> ⑬ **⑫ 关闭**：匹配调用粒度（EntJoy `cap=1` = 64 元素/次 = Unity `innerloopBatchCount` 的**同一粒度**）下，
> Build 段仍慢 27%、Integrate 仍慢 19%；把调用**粗化 4×** 只补回约 7 与 2 个点 ⇒ 每元素体代价是主项，
> **调用/认领前导是少数项**（§40.1）。
> ⑭ **D2（单结构体指针 ABI）否证**：夹具同体同输入、只改参数形状（96 扁平形参 vs 40 字段装一个结构体字段），
> **5/5 更慢 0.2–0.7%**（校验和一致）；且已核对生成的 `_Execute_Batch` 确实是 9 形参 vs 96+ 形参，**不是空转**（§40.2）。
> ⑮ **D3 关闭**：Integrate 环 A 420 指令/164 访存 vs B 399/160 —— **几乎相同**；disasm 报告里的"73 次栈访存"
> 是**分类**差异（A 从入参帧镜像取、B 从一个解包结构体的固定位移取，两者都是 L1 load），
> 被 ⑭ 的夹具实验直接证明**代价中性**。别名说也不成立（08 §32 打开 `__restrict` 后环内重载次数不变）。
> 唯一成立的约束是**寄存器压力**（33 活跃指针 vs ~9 GPR），而它由**宿主的字段数**决定，框架改不了。
> 另外 B 的 `BattleBenchM4.cs` 文件头**自己声明 Integrate 段不可比**（dump 只给 position/alive/state/team/hp）
> ⇒ 那一段的差额**不可归因**（§41）。
> ⑯ **D4 记账不动**：8 worker 比 1 worker 每次最小派发往返贵 **0.285 µs**（0.631 vs 0.346 µs/job），
> 机制 = `FastPath` 把 job **投递到 worker 池**（`JobSystem_Scheduler.cpp:186-215`）而 Unity 对未启动的小 job
> **在调用线程内联执行**；`ENTJOY_STATS=0` 与 `ENTJOY_SPIN_BUSY=0` **都无影响**（两个假设否证）。
> 上界 = 385 次提交/步 × 0.285 µs ≈ **0.07%/步** ⇒ 记账不动（§42）。
> ⑰ **对齐档最终状态：打平（A 快 0.5–0.9%，三次独立复现 1.005/1.008/1.008）**；默认档 **A 快 6.3%**。
> 本轮之后**没有量级 ≥1% 的已知杠杆**（§43.1）。
>
> ⚠⚠⚠ **2026-10-03 第五轮更新（09 §44–§49；⑰ 的"没有杠杆"已被推翻一半）**：
> ⑱ **⑯ 的两条子结论要改**：`ENTJOY_STATS=0`/`ENTJOY_SPIN_BUSY=0` 确实无影响，但"Unity 在调用线程内联执行
> 未启动的小 job"**是会话外知识、不是本项目实测**（已据此改正措辞，见 §44）。
> ⑲ **assist 是这条轴上唯一有正收益的既有开关**：对齐档 `ENTJOY_ASSIST=1` 稳定改善 `flow`
> （7/7 复现，−0.65…−1.0% 整步），place 5/7 弱正、count/integ 4/7、Build 段不动（§45）。
> ⑳ **连发小 job 的 W 依赖根因被定位**：不是"一次广播 = W 次 futex"，而是
> **生产者每条派发都要写一条 W 个 worker 正在轮询的 cacheline**（三条实测互锁，§47/§48）。
> ㉑ **§48 否证的 7 个候选之后，N12 是第一个真正成立的形态**（**已提为默认开**）：
> 提交侧改成"**醒着登记的人数 ≥ 本次派发需要的 worker 数**才不写唤醒字"（rayon 的 posted-without-storing +
> 两处 seq-cst fence）。微基准 6 趟配对：连发 W=8 **1.73×**／W=16 2.02×、逐个 round-trip W=8 **2.16×**／
> W=16 2.38×，W=1 无回归，且曲线在 W=1..16 上**变平**（`defer` 在 round-trip 形状无收益）；
> 游戏内对齐档整步 **−1.51 ms / 159.6 ms（3/3，steps-check ok）**，melee −1.39 ms；
> 两个臂各自对 Unity 的 B/A 由 **1.015 → 1.036**（9/9 pass 方向一致，但那次会话 steps-check SUSPECT）。
> ㉒ **⚠ 第一版实现让整机 2.2× 变慢，教训值得单独记住**：两条入口（小 job / 真并行趟）**不能用同一个谓词** ——
> 「有 1 个登记中的人就跳过」会让一整趟并行批只被 1~2 个 worker 拖着跑（melee 120 → 315 ms，22 s 内 118 → 54 步）。
> 分入口计数自证：真实宿主 49 686 次派发里 **batch 49 686 / work 119**，即负载几乎全在 `SubmitBatch`。
> 修法是谓词带 `need`（小 job=1，真并行趟=`batch->workerCount`）。**别把微基准的"每次只需 1 个 worker"
> 形状当成通用形状。**
> ㉓ 开关与器械：`ENTJOY_WAKE_POLL`（默认开，`=0` 关闭）、`tools/gate-run/n12-wakepoll-ab.ps1`（微基准 3 臂）、
> `tools/gate-run/n13-wakepoll-ingame.ps1`（游戏内 A-only / `-WithB` 两种）。生效证据走新导出
> `JobSystem_GetWakePollCounters`（`EMPTYPROBE` 行内 `wakePollSkipsWakes=`，或宿主 `[JOBWAKEPOLL]` 行）。
> ㉔ **落地后自查出两处**（§49.10）：① 诊断计数器自己**每条派发 `lock xadd` 全局行** —— 正是本改动要消除的
> 模式换个名字，改为 thread_local 累加 + 每 1024 次合并（结构性修正，不声称提速）；② 唤醒需求口径由
> `batch->workerCount`（上限）改为 `tokenCount`（= min(cap, workers, tiles) = 本趟真实令牌数，更准且仍保守）。
> 实测：**对本宿主是 no-op**（对齐档所有内核 tileCount 远大于 8 ⇒ 两者恒等；分入口计数两臂无系统差异），
> 保留理由是通用性 + 更准确。
> ㉕ **新增第 10 套件 `WakeLivenessTests`**（丢唤醒必须以失败而不是挂住结束）：形态 A 全体停靠 + 单 job
> （断言**必须**走慢路径）、B 全体停靠 + 批、C 叫热后连发（断言 skips>0，防假通过）、D 8 个长 job 占满后再发
> 小 job、E 150 次随机形状 + 随机空隙；两开关状态都 PASS，~1.0 s。另把 `DumpState` 补上
> `idlePollers=`/`parked=`/`wakePoll=`（丢唤醒的诊断全在这两个数上）。
> ㉖ 已知冗余（**未做也未测**）：x86 上 PushFence 与注入器的 locked CAS 冗余，去掉可省 ~10 ns/派发
> （≈连发 per-job 成本的 3%），但会把正确性押在 `MPMCInjector::Push` 的内存序实现细节上（§49.12）。
>
> ⚠⚠⚠ **2026-10-03 第六轮：缺陷专项（09 §50）—— 按"声明了但在产品配置下从不触发"这一类机械查**
> ㉗ **修掉两个"诊断撒谎"**：① `[JOBPHYS] workerThreads=` **恒为 0**（`Shutdown` 在打印前就
> `g_numThreads.store(0)`）—— 而这行存在意义正是"判定 A/B 臂是否真生效"，修后实测 `workerThreads=8`；
> 另核实它**没有**功能性后果（`SubmitBatch` 用 Schedule 时刻已写进批的 `workerCount`，关停 flush 不重解析）。
> ② `[JOBWAKE] notify_all skipped=0` 在 `wakePoll=ON`（默认）下是**死计数**（§7ah 旧守卫整段不执行），
> 真跳过数在下一行 `[JOBWAKEPOLL]`；已在该行标注，避免被读成"守卫从不触发"。
> ㉘ **补上 F5（`ENTJOY_TILE_RUN`，默认开）的生效证据** —— 此前**全代码库没有计数**证明融合真的发生过。
> 新增 `[JOBTILERUN] fusedRuns/avgRun/maxRun`，实测**两档都活**：默认档 201 561 次 / avgRun 3.82 tile
> （≈7460 元素/次）；对齐档 2 786 503 次 / avgRun 249.47 / maxRun 1024（按元素是 `:1` 内核 1024 元素/次，
> 正落在 §31 的最优区 1024–2048）。⇒ 之前"F5 生效"只是假设，现在是证据；且 `avgRun==1` 即可报警。
> ㉙ **查了但没问题的（负结果，免得下轮重做）**：`BatchStorage` 复用陈旧字段（release 整对象重建 +
> acquire 命中缓存仍复位 6 个标记 ⇒ 安全）；`JobSystemStatsNative` ABI 布局防御（已强制 + 当前一致）；
> 两条重复 Schedule 路径的 7 个共享字段奇偶（都在两条路上赋值）；`claimGeom` API 参数没被丢
> （Exports→declareGeom→claimGeomOverride→ShouldUseSpreadGeometry）。
> ㉚ 命名陷阱（非 bug）：`JOBBATCHTBL` 的 `hit=1` 是"第几次见到该 key"，不是"批表命中"；已核对 11 个
> 解析脚本**都只用 `applied=`** ⇒ 无实际风险。
> ㉛ 仍开着但不是缺陷：默认档 `busy_ratio=0.84` ⇒ ~16% worker 时间不在 tile 执行（退役链只占 0.06%，
> 差额是自旋等下一批），这是已量过的自旋权衡，且同一份自旋正是 N12 跳过唤醒的前提（§42/§46 已证调自旋
> 旋钮对整步无正收益）。
> ㉜ **F6（`ENTJOY_CLAIM_ADAPT`，默认开）也补了生效证据**（判据链有三处可静默退化：键为 0 / 没学到成本
> 样本 / 迟滞不翻）。实测默认档：`nokey=0 nosample=15 sliced=1847 interleaved=53564 flips=149`
> ⇒ "成本恒 0"那个历史坑**没有发生**（0.03%），自适应真动过 149 次；`nosample ≈ n` 即为回归告警线。
> ㉝ **F2/F4 也补了分支计数，并推翻了一条记在注释里的信念**：此前隐含"默认档=厚 tile ⇒ F2/F4 恒不生效"，
> 实测**默认档 applied=4 037 批（≈7%）、对齐档 45 556 批（≈80%）** ⇒ 两档都生效。⚠ 那 4 037 批的
> **来源尚未定位**（候选是 cs=16 的小内核，未验证；`[JCC-DIAG] chunk[2^k]` 数的是 **tile 数**不是 cs，
> 不能用它反推 —— 我第一版就是这么写错的，已单独提交撤回）。
>
> 🧾 **2026-10-03 结账（09 §51）：对齐档已经反超 Unity，落后的只剩 2 个段且都在宿主侧**
> ㉞ 逐趟（对齐档，N12 开，B/A，>1 = A 更快）：**整步 1.036（A 快 3.6%）**；
>   A **领先**：Melee 1.028（−3.21 ms）、Flow 1.057（−1.71 ms）、prefixPartial 1.297；
>   A **落后**：**Build 段 0.848（+0.486 ms）**、**Integrate 0.840（+0.475 ms）**、
>   prefixFinal 0.715（+0.052）、count 0.854（+0.141）、place 0.980（+0.034）、zero 0.827（+0.019）。
>   ⇒ **落后合计 0.96 ms（0.6% 的步），领先合计 4.92 ms**。
> ㉟ **为何落后**：① Build 段/Integrate 是**每元素体**代价 —— §31 空内核对照证明框架本身
>   **6.5 vs Unity 7.0 ns/工作项 = 打平**；§40.1 在**匹配调用粒度**下仍慢 27%/19%，粗化 4× 只补回
>   7/2 个点；§41 归因到**寄存器压力**（33 活跃指针 vs ~9 GPR，由宿主字段数决定）。
>   ② B 的 `BattleBenchM4.cs` **自己声明 Integrate 不可比**。③ prefixFinal 相对差最大但绝对只有
>   0.052 ms，且**元素数极少 ⇒ 疑似"每次调用固定开销"主导（推断，未测）**。
> ㊱ **下一步（09 §51.3，先测后改）**：第 1 步给每个 pass 加**调用数/元素数**计数（现在 `[M-19]` 只有 ms、
>   没有分母 ⇒ 无法区分"每次调用贵"还是"每元素贵"）；第 2 步把 Build 段那笔"没有名字"的开销（0.1–0.6 ms）
>   做成同窗口逐步配对可测（现在散度≈均值，量不准）；第 3 步定位那 4 037 批薄 tile 的来源。
>   收益上限 **~0.3 ms/步（0.2%）**。
> ㊲ **别再开**：把 Integrate/Build 的每元素代价当框架问题；调自旋旋钮；poke-one / skip-awake /
>   claim 切片 / 值绑定 / 标量限制（均已有否证记录）。
>
> 🧾 **2026-10-03 第七轮：取证 + 几何缺陷修复（09 §52）**
> ㊳ **新仪器 `[JOBPERKEY]`**（框架侧，宿主不改）：按 job 键（`JobFuncKey` = 内核 RVA，与批表/jobkeys.txt
>   同一键空间）统计 元素/批/调用/元素每次 + **内核自计时（抽样 1/32）**。开关 `ENTJOY_JOB_BATCH_TABLE_DUMP`。
>   ⚠ 第一版键用 `funcHash` ⇒ 它在对齐档被**有意置 0** ⇒ 仪器恰在目标档静默失效（0 行）；改用 `JobFuncKey`。
>   顺带正确性证据：**15/15 内核 `mismatch=0`** ⇒ 认领 + F5 融合不漏不重。
> ㊴ **成本模型自洽**（聚合内核 ms/步 ÷ ~8 与宿主 ms/步吻合 10–20%）：Melee 106 vs 94.5、Integrate 4.2 vs 3.66、
>   Place 2.2 vs 1.88、Count 1.5 vs 1.23、MarkDead 0.60 vs 0.61 ⇒ 这些 pass 的成本**基本就是它们自己的内核**。
> ㊵ **两个剂量-反应实验（各 3 趟配对，两臂 steps-check ok）**：
>   · **span**（256→1024→2048 元素/次）：Count 宿主 ms 0.981→0.881→0.914（3/3，−6%），但**内核 ticks/元素不动**
>     ⇒ 省的是框架**每次调用 ~16 ns**（调用数 4701→1175），上限很小。
>   · **geometry**（Adjacent→**Spread**）：**内核 ticks/元素 Count 25.0→23.9(−4.4%)、Place 49.0→46.4(−5.5%)、
>     Integrate 83.2→79.9(−3.9%)**；宿主 ms Place −8.8%、Integrate −5.7%、Count −4%；Build 段 −0.290(3/3)。
>     ⇒ **因果结论：几何改变每元素代价，span 不改变** —— 修正 §41 的隐含前提（"每元素代价框架改不了"）：
>     "元素→worker 的分配方式"**是框架可控的**，在 count/place/integrate 上值 4–5.5%（与宿主源码注释一致）。
>     但仍不足以追平：Count 23.9 vs Unity ≈21 ticks/元素 ⇒ 差额仍在内核体；geometry+span 合计回收
>     ~0.29–0.36 ms/步（0.25%），Build 段 +0.486→≈+0.20、Integrate +0.475→≈+0.31，两段仍落后。
> ㊶ **zero / prefixFinal / prefixPartial 与框架无关**（你的直觉正确）：zero 那趟**不是** ClearAll 内核
>   （ClearAll 整轮只跑 1 批），是宿主自己的 memset；prefixFinal/Partial 两栈 ms 几乎相同（0.142/0.033 vs
>   0.133/0.028）⇒ 宿主共用工作。
> ㊷ ✅ **缺陷已修**：`ClaimPolicy` 在调用点**用不了**且静默降级（生成的重载没有几何形参 ⇒ 传几何的调用点
>   绑到托管 `JobExtensions.Schedule<T>`：key 变堆地址、applied 15→12、place 2.2→5.3 ms —— 宿主注释已实测）。
>   改 `BindingsGenerator.cs`：`isParallelFor` 的签名末尾加 `ClaimPolicy claim = ClaimPolicy.Auto` 并透传
>   （两处参数顺序必须一致，否则 CS1503）。**同时补上这条轴此前完全没有的生效证据**：
>   `g_claimGeomDecl{Spread,Adjacent,Auto}` + `[JOBGEOM]` + 导出 `JobSystem_GetClaimGeomCounters`
>   + bench 臂 `BENCH_CLAIM_GEOM`。**端到端验收**：`claimGeom=spread/adjacent/auto` 三臂分别读到
>   `14000/0/0`、`0/14000/0`、`0/0/14000`（**只有原生路径能写这三个计数** ⇒ 同时证明"绑到原生重载"与
>   "几何到达认领层"），且 `each_index_exactly_once=1`、`native=True` 保持。
>   回归门：`dotnet build EntJoy.sln`（CI 口径）0 错误；jobs-only 守门 PASS（生成物零 ECS 耦合）；
>   native 10/10 × 2 状态全过。
>
> 🧾 **2026-10-03 第八轮：通用机制叠加 → 落后**没有**解决，但边界钉死了（09 §53）**
> ㊸ **叠加效果**（同一会话 3 趟配对）：`Integrate −0.180 ms (3/3)`、`prefixFinal −0.022 (3/3)`、
>   `count −0.034 (2/3)`、`Build 段 −0.100 (2/3)`；但 **B/A 仍 < 1**（integ 0.80→0.90、Build 0.76→0.80）
>   ⇒ 只回收约 1/3 赤字；整步 `steps-check SUSPECT` 不可用。
> ㊹ **⚠ 最重要的发现：对齐档的整步结论随机器状态翻转。** 本轮两臂 A 都落后（B/A 0.978 / 0.949，
>   B melee ≈91 ms / whole ≈120 ms），而上一轮 A 领先（1.036，B melee ≈120 ms / whole ≈159 ms）；
>   **两栈同时快 ~25%** ⇒ 差别在环境不在代码。**跨会话稳定落后的是 count/Integrate/prefixFinal/zero
>   （0.62–0.90），稳定领先的是 melee/flow。** 单会话整步 B/A 不可跨会话引用。
> ㊺ **找到"按值绑定"对最重要 job 类型恒不生效的根因（已修，默认输出逐位不变）**：判据在 **C# 源码**
>   里找循环，而 `IJobParallelFor`/`IJob` 是**逐元素**形态（`Execute(int index)`，源码无循环，循环由
>   transpiler 合成）⇒ `TripCount` 恒空 ⇒ 该优化对宿主最重要的 job 类型完全失效（生成物里六个标量
>   全是 `const T& X = *X_ptr;`，而循环体里的原子写让编译器每元素重载）。新增
>   `ENTJOY_VALUE_BIND=4` = 行程数 **或 循环内条件字段**。**验证**：管线确实按规则改输出（jobs-only
>   工程 mode 2：`const int& Delta` → `const int Delta`）；默认与 mode 3 逐位相同。
>   **未验证**：mode 4 对宿主内核的实际收益 —— 宿主源生成器被 **Roslyn 增量机制跳过**（C# 未变 ⇒
>   管线不重跑；mode 2 同样无变化，故不是规则没生效）。下一步：强制宿主重生成（删
>   `NativeTranspiler_Generated/*.cpp` 或 `.godot/mono/temp`）后用 `[JOBPERKEY]` 每键 ticks/元素对比。
> ㊻ **边界**：框架可控的轴（派发/唤醒/粒度/几何/参数形状/绑定形式）已逐条度量完，能拿的都拿了
>   （约回收 1/3）；剩下的 2/3 是**每元素体**（count = 散列寻址原子 RMW；Integrate = ~20 数组访问 +
>   33 活跃指针 ⇒ 内存延迟/寄存器压力），同一份 C# 在 IL2CPP 下每元素快 15–20% 而**指令数几乎相同**
>   （§41 420 vs 399）⇒ 差在动态（停顿/ILP）。**要真正解决必须动宿主内核体 = 你的"不改测试端"约束下不可行。**
>
> ⚠ **2026-10-03 更正（09 §54）**：上面这条"边界"划错了。`index < Length` 这道**承重守卫**的**文字**在宿主 C# 里
>   （`CPUBattleSpatialHash.cs:251/338`，全文仅 2 处），但它**为什么承重、以及能否消掉**都在框架侧：
>   框架调度器把最后一块切得**越过 `length`**（977×1024 = 1 000 448 > 1 000 000，08 §32.7），
>   而 Unity 的 `Execute(int i)` **没有**这道守卫（`BattleBenchM1Flat.cs:43-55`）——因为引擎给的是
>   "`i < length`"这条契约。⇒ **"框架给出这条契约 + transpiler 折叠该谓词"是不动宿主的通解**（09 §54.3/§54.4，形态 K1′+G）。
>   仍然确实在宿主侧的只有**数组物理布局/散列**（其 ABI 代理已实测更慢 5/5）与**循环分裂**（预注册要求：先在夹具做出环比 <1 的臂）。

---

## 0. 一句话现状

对齐档（把 EntJoy 的粒度钉成 Unity 的）下 Build 比 Unity 慢 **+2.39 ms**，其中 **77% 在 `count` 一趟（2.71 vs 0.82 = 3.29×）**；
机制 = **每元素一次共享原子（两侧同形，Unity 的 asm 也是 `lock incl`）**，其有效代价受**认领窗口**支配（认领 4→1024 ⇒ count 2.71→1.29）。
**指令数已被证明不是原因**：把环内 7 次不变量重载全部提到环外（VB2+R1，asm 为证）后 `count` 只动 −1.2%。
当前最好 **1.291 ms**（认领 1024）/ **1.2754**（VB2+R1），目标 Unity **0.8242** ⇒ 差 **1.55×**，全部落在"原子有效代价 ≈0.36 + 非原子尾 ≈0.1–0.2"。

---

## 1. 术语与口径（必须遵守）

| 概念 | 含义 |
|---|---|
| **对齐档（mir）** | 用 `ENTJOY_JOB_BATCH_TABLE` 把 15 个内核的内批钉成 Unity 实测档（4 个 =64，11 个 =1）。**可复现**（tiling trace 逐位相同） |
| **默认档（def）** | 不设表。**不可复现**（同臂三次 `count` = 2.94 / 1.35 / 2.90） |
| **帧指纹（门控用）** | `[M-1]` 的 Melee：**对齐档 118–127 / 默认档 105–112**。**每次运行都必须门控**，不合格即作废 |
| **`applied=15/15`** | 需 `ENTJOY_JOB_BATCH_TABLE_DUMP=1`；统计 `key=… applied>0` 的个数，证明表生效 |
| 表格式 | `<key>:<batch>[:<claim>]`；**`key::claim` = 只覆盖认领、不改内批**（A2） |
| 逐趟 | `[M-19] Build 逐趟(窗口均/ms): zero/count/prefixPartial/hostRewrite/prefixFinal/place`（`CPUBATTLE_DIAG_BUILDPASS=1`，32 步窗口） |

**⚠ 不可复现的根因已查清**：**仿真本身不可逐位复现**（§27.7：三次运行在**步 32** 的状态指纹就分叉，连"不敏感"的 `ΣSortedIndex`、`ΣCounts` 也不同）。
根因候选：`PlaceCellsJob` 的共享原子槽位分配（`CPUBattleSpatialHash.cs:263`，**Unity 侧同款** ⇒ A/B 仍公平）＋ Melee 的平局判定读该顺序。
⇒ **产品档（默认档）的 Build A/B 无法验收**；能验收的只有"确定性帧"（对齐档）。

---

## 2. 结论总表

### 2.1 Build 逐趟对照（对齐档，3 对中位，工等价已核：A `length=1e6`/存活 998,292 ↔ B `active_count` 997,986）

| 趟 | EntJoy (A) | Unity (B) | A−B | A/B |
|---|---|---|---|---|
| zero | 0.0093 | 0.0881 | −0.079 | 0.11 |
| **count** | **2.7116** | **0.8242** | **+1.887** | **3.29** |
| prefixPartial | 0.0841 | 0.0456 | +0.039 | 1.84 |
| hostRewrite | 0.0024 | 0.0001 | +0.002 | — |
| prefixFinal | 0.1832 | 0.1325 | +0.051 | 1.38 |
| place | 2.1586 | 1.6667 | +0.492 | 1.30 |
| **Σ** | **5.1492** | **2.7571** | **+2.392** | 1.87 |

- 07 §(f) 的"A 5.21 vs B 2.62（2.0×）"：同协议实测 **1.87×** ⇒ 量级对、**归因错**（主因是 `count`，不是 place 的散列写）。
- Build 数值**强依赖窗口/相位**：同配置 `AUTOEXIT=22` 得 5.9~6.0、`24` 得 5.88~6.03、`45` 得 4.46 ⇒ **跨协议不可比**。

### 2.2 `count` 的关键量（全部对齐档、门控通过）

| 配置 | count (ms) |
|---|---|
| 认领 4（内置） | 2.94 / (A+0 臂) 1.839 |
| 认领 16 / 64 / 1024 | 1.34~1.49 / 1.25 / **1.291** |
| 认领-only（`::1024`，不改内批） | 1.356（§27.2） |
| **VB2+R1（指令对齐后）** | **1.2754** |
| **Unity** | **0.8242** |

- **认领敏感度强**：4→64 让 count **−1.43**（F2/F4 开关两种状态都复现）⇒ 争用是主因。
- **指令对齐无效**：VB2+R1 把 7 次重载提到序言后 count 只 **−0.016**（§32.2）。
- **Unity 侧 asm**：`Bb0M1FlatCountJob`（hash `df2f83b6931c44aac336f5977eb2e4de`）主函数 91 行，**唯一一处 `lock`**：
  `16b: lock / 16c: incl (%rdx,%r8,4)` ⇒ **每元素一次共享原子，与我们同形** ⇒ 目标公平。

### 2.3 残差拆分（§32.6，用已有测量）

| 量 | 值 |
|---|---|
| 我们对齐档 count（认领 1024） | 1.28 |
| 我们**去原子**后（07 §(f) 消融） | ≈0.92 |
| ⇒ **我们的原子开销** | **≈0.36** |
| Unity count（含每元素 `lock incl`） | 0.824 |
| ⇒ 非原子侧我们比 Unity 多 | **~0.1–0.2**（每元素 3 次过滤载入 vs Unity 2 次，多的是 `index < Length` 守卫） |

---

## 3. 已证否的假设（**不要重走**）

| 假设 | 结论 | 证据 |
|---|---|---|
| 存在"单一静态认领几何"通吃所有 job | ⛔ 否 | §15.3/§23.1（同一几何对 Build/Melee 反号） |
| 认领点原子自适应有空间 | ⛔ 否 | §21（认领原子聚合 ≤0.5 ms/步） |
| 环内 7 次不变量重载是 count 的瓶颈 | ⛔ 实测否 | §32.1/§32.2（对齐后 −1.2%） |
| "Unity 没有每元素原子" | ⛔ 否证 | §32.3（它的 asm 也是 `lock incl`） |
| per-job 在线学习（F6） | ⛔ 整步不兑现 | §15.4（Build −1.9 但整步 +1.65） |
| 转译器"共享原子私有化" | ⛔ 只值 −0.6 | §24（claim 级 spike） |
| 撤 `index < Length` 守卫（K1） | ⛔ **破坏正确性** | §32.7（框架 tiling 最后一块越界） |
| `SCALAR_RESTRICT=1` 单独用 | ⛔ 无效 | §30.2（生成器直接发字面量，不经宏） |
| `VALUE_BIND=2` 单独用 | ⛔ 只把重载搬到栈 | §31 |
| **"对齐档残余差距 = JobSystem 调度问题"** | ⛔ **否（2026-10-02，09 §20）** | 通解（F2/F4 提默认）后对齐档整步 **1.005×**；剩余 `zero`/`count`/`place`/`Integrate` 四项，逐条对齐两侧源码 + 粒度（两侧都 64）+ 反汇编后确认：**四项都不是调度**，而是 ① 内存带宽/别名信息（zero 10 vs 26 GB/s、count/place 的 `[ReadOnly]` 载入合并）② 转译器调用约定（Integrate 96 形参→栈流量） |
| **"`place` 要 Adjacent、`count` 要 Spread（同趟内反号）"** | ⛔ **读表错误，二者同向** | 09 §20.3 四组几何对照：count `Spread` 0.95 vs `Adjacent` 1.48–1.56；place `Spread` 1.75–1.93 vs `Adjacent` 2.41–2.58 ⇒ **`Spread+Spread` 是四组最优** |
| **`ENTJOY_CLAIM_GUIDED` 的"两会话矛盾"** | ⛔ **问题已消失（09 §19.2）** | 新结构下对齐档只有 **977 次认领**，尾部不平衡上界 ≈整步 0.025% ⇒ 可回收量在噪声下；且 guided 与 F2/F5 互斥（开它反而丢 −13.9 ms）。**不必再测** |
| **"存在比 Spread/Adjacent 更均衡的全局认领方案"** | ⛔ **否（09 §21.1）** | 同会话四臂：`GUIDED` ❌、`CLAIM_SLICE` ❌（Melee +20 ms）、`TILE_STRIDE` ❌❌**灾难**（Melee 122→473/**567**、整步 32→58/76）、`CLAIM_BLOCK` ⛔（08 l8：整步 +6.85/0-10）。机制：`count/place` 要"把 worker 推开"、`Melee` 要"让 worker 靠近同一批 cell"，**单一全局方案必然在这两者间重新分配**；现有 per-call-site 声明（count/place=Spread、Melee=Adjacent）就是端点 |
| **"Unity count 更快是因为它没有每元素原子 / 调度更好"** | ⛔ **否（09 §21.2，08 §32.3）** | U 侧 `Bb0M1FlatCountJob` 反汇编**同样每元素一次 `lock incl`**；粒度同 64、几何已最优、代码只差承重守卫 ⇒ 差距在 **Burst 的 `[ReadOnly]` 别名证明 ⇒ 载入合并**（编译器质量），不是调度 |

---

## 4. 器械与命令（**必读**）

### 4.1 重发生成物（改 env 生效的唯一配方）
```powershell
dotnet build-server shutdown
dotnet build <CSBS>\ComputeShaderBattleSimulation.csproj -c Debug --no-incremental   # ≥25s 才算真重发
cmake --build <CSBS>\NativeTranspiler_Generated\build --config Release --target NativeTranspiled
cmake --build <CSBS>\NativeTranspiler_Generated\build --config Release --target NativeDll
# 部署到 .godot\mono\temp\bin\Debug\（NativeDll.dll + NativeTranspiled.dll 两个都要）
```
- ⚠ **看错误数**：6 秒构建 = **失败**（我踩过：逐字字符串里写双引号 ⇒ 7 个编译错误）。
- ⚠ **重发后 RVA 必变 ⇒ 表键必须按导出名重推**（用空表 + dump 拿 `key=…` 的首见顺序，与 §2 表序一致：1=FlowPresence 2=FlowClear 3=FlowSeed 4=FlowGrad 5=ClearAll 6=Spawn **7=Count** 8=PrefixFinal 9=PrefixPartial **10=Place** 11=Melee 12=MarkDead 13=Integrate 14=FlowSeedInit 15=BfsWave；64 档=1/7/10/13）。
- ⚠ 父 shell 里逐轮改 `$env:` 再跑 Godot **只有第一轮生效** ⇒ **每个臂各起一个独立 `powershell.exe` 子进程**。

### 4.2 运行与门控（模板）
```powershell
$env:ENTJOY_JOB_BATCH_TABLE='<15键表>'
$env:ENTJOY_JOB_BATCH_TABLE_DUMP='1'
$env:CPUBATTLE_AUTODEPLOY='1'; $env:CPUBATTLE_NO_RENDER_PATH='1'; $env:CPUBATTLE_AUTOEXIT='22'
$env:CPUBATTLE_STATS_WINDOW='5'; $env:ENTJOY_JOB_WORKERS='8'; $env:CPUBATTLE_DIAG_BUILDPASS='1'
& <godot> --path <CSBS> --log-file <log> res://CPUBattle/Scenes/CPUBattleEcs.tscn *> <stdout>
```
门控：`applied=15/15` **且** `[M-1] Melee ∈ [118,127]`（对齐档）。

### 4.3 器械清单（都在位）
| 器械 | 作用 |
|---|---|
| `ENTJOY_JOB_BATCH_TABLE`（第 3 字段 `:claim`） | 按 job 内批 + **按 job 认领上限**；`key::claim` = 只覆盖认领（A2） |
| `CPUBATTLE_DIAG_BUILDPASS=1` | A 侧 `[M-19]` 六段逐趟（32 步窗口）+ `[M-20]` 状态指纹 |
| `[M-20] 指纹` | `SortedXi=Σ SortedIndex[i]*(i+1)`（顺序敏感）+ `SortedSum`/`CountsSum`（不敏感，作对照） |
| `ENTJOY_JOB_TILE_TRACE=<K>` | 每键前 K 次调度打 `tiles` ⇒ 看 tiling 演化/稳态 |
| `ENTJOY_CLAIM_STAT=1` | 认领点 rdtsc（聚合 ≤0.5 ms/步 ⇒ 认领原子不是杠杆） |
| `ENTJOY_VALUE_BIND=2` + `ENTJOY_SCALAR_RESTRICT=1` | **必须一起**：把环内不变量重载提到环外（asm 已验证） |
| Unity 侧 | `M4_PLAYER_RUN=1 M4_WORKERS=8 M4_DUMP=astate_v2s60.bin M4_STEPS=<n> M4_WARMUP=<w> M4_CSV=<csv>`；`CpuHashFlat.BuildTimed` 导出 `M4,build,*_ms` 六趟；B 侧计时**含预热步** ⇒ 除以 `(warmup+steps)` 才与 `M4,seg,build_ms` 对齐（自证差 0.14%） |

---

## 5. 当前状态（可交回）

| 项 | 值 |
|---|---|
| 生成物 | **规范态** `SharpNative_Job_CPUBattle_CountCellsJob_Execute.cpp` = `16541A4EE5A8`（引用绑定）；Count asm **78 行 / 7 次环内重载** |
| 部署 | `NativeDll.dll = D842C6CE0705`、`NativeTranspiled.dll = B48E870F7A72`（`.godot\mono\temp\bin\Debug\`） |
| 守卫 | `CPUBattleSpatialHash.cs` 的 `if (index < Length …)` **2 处**（Count + Place，承重，勿删） |
| EntJoy 工作树（未提交） | 8 个 `src/NativeDll/*` + `tests/NativeDll.Tests/JobSystemTests.cpp` + `src/NativeTranspiler/Analyzer/Common/CodeTemplates.cs`（仅注释）+ `docs/gridsearch/07,07b`；`docs/gridsearch/08-…md` 为未跟踪 |
| CSBS 工作树 | `CPUBattle/Scripts/CPUBattleSpatialHash.cs`（探针：`[M-19]`/`[M-20]`）+ 6 个**既有**改动（不是我改的） |
| 原生测试 | 9/9 ×（`TILE_RUN` 关/开）✓（最后一次在 §31/§32 之间跑过） |
| Unity 玩家档 | 已重编一次（`BuildTimed` 六趟 + `M4,build,*` 导出）；旧档保留在 `Build/W0Player_prev_20261001_prepass` |

---

## 6. 下一步（按优先级）

### ① K1′（框架侧，**推荐先做**）：让"调度长度 ≤ 数组长度"成为框架保证
- **做法**：在分片/合并路径（`JobSystem_Tiles.cpp` 的 tile/run 执行处、`ChaseLevScheduler` 的 `executorRun_`/`executor_` 调用点）把最后一块钳到 `length`：
  `count = min(tileEnd, length) − start`（`length` 由 batch 携带，`batch->totalElements` 已有）。
- **然后**才撤内核里承重的 `index < Length` 守卫（Count **和** Place），对齐 Unity 的 2 次过滤载入。
- **顺带修掉一个真 UB**：现在 `tiles × innerBatch > length`（例 977×1024 = 1,000,448）——最后一块总是越界，只是被守卫挡住了。
- **判据（缺一不可）**：① 新增框架自检"每步 `Σ count == length`"；② `[M-19] count` 1.2754 → 预期 **−0.1~0.2**；③ Melee 落回 **118–127**；④ 默认档五段全看；⑤ 原生 9/9 + 全套门禁（81 转译器单测 / fixture / ecs-native）。

### ② A 的"按批声明"API（框架通用能力）
- 规格：`docs/gridsearch/08-…md` §26.5 / §27。要点：**调用点声明**（不按名字特判）+ **全局默认不变** + **缺声明回退** + 表项允许 `key::claim`。
- 已实测收益（**确定性帧**内）：只给 Count+Place 放大认领 ⇒ **Build 段 −0.94、整步 −1.7~2.1，Melee 不退**（§26.9）。

### ③ P1 / P2 决策（产品档可复现性）
- **P1**：`PlaceCellsJob` 用前缀和基数 + 确定性偏移替代共享原子分配槽位（内核侧，改变与 Unity 的对等前提）。
- **P2**：接受不可复现，只在确定性帧验收，把产品档收益标注为"§26.9 实测 + 推论"。

### ④ 若 ①② 都压不动
如实结论：**在"不改内核数据结构"约束下 `count` 停在 ~1.28（Unity 0.82，1.55×），成因为"每元素共享原子的有效代价"**；下一步只能是分片计数器/换布局（更大内核改动）或接受。

---

## 7. 文档索引（都在 `docs/gridsearch/08-逐Job档表与Unity-batch0语义实测.md`，2445 行）

| 节 | 内容 |
|---|---|
| §2–§11 | Unity 表键↔job、`innerloopBatchCount=0 ⇒ 1` 探测、确定档表、口径与器械坑 |
| §12 | F1/F1b/F2/F3/F4 修复清单与验收协议 |
| §13 | 默认档/对齐档 vs Unity（**含跨会话口径警告**） |
| §14 | F5（连续等宽 tile 合并）根因、R1/R2、修复后残余 |
| §15 | F5b / F6 / 器械复核（框架每工作项成本 7.56→0.02 ns/tile） |
| §16 | "入口现在一样吗"三层口径（**§16.2 已被 §17 推翻**） |
| §17 | 机器码复核：Burst 也环内重物化 ⇒ "提到环外"建议撤销 |
| §18 | 终局同会话对比（默认档 +2.0%、对齐档 −1.5%）、已做优化清单 |
| §19 | 相位归属（两帧消融：位置+d² 1.9× 跨帧恒定） |
| §20 | 认领几何能否通解（Unity=统一机制+每 job 常量；文献全是状态驱动） |
| §21 | 认领点探针：认领原子聚合 ≤0.5 ms/步 ⇒ 无空间 |
| §22 | "Build 快 ⇒ Melee 慢"的机制拆解（busy_ratio 证据） |
| §23 | 旋转 n=3 粒度扫描 + 同义控制组（**取舍的量化**） |
| §24 | ② 转译器私有化 spike：−0.6 ⇒ 不值得 |
| §25 | 对齐档 Build 不敏感（**已被 §26.3 推翻**） |
| §26 | ⭐ Build 逐趟同协议对照（3 对）+ 判据 + 实现规格（**核心**） |
| §27 | A2 实现与自证、tiling 可观测、**仿真不可复现**（§27.7） |
| §28 | ⭐ 机器码对比：count 环内 7 次不变量重载（生成器 `const T&`） |
| §29–§31 | 仓库早已有两个旋钮（`VALUE_BIND`/`SCALAR_RESTRICT`）、重发配方、VB2 单独无效 |
| §32 | ⭐ 指令对齐达成但**不是瓶颈**；Unity 侧同样 `lock incl`；残差拆分；**K1/K1′** |
