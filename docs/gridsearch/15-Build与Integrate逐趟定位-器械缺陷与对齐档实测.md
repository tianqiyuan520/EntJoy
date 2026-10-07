# 15 · Build + Integrate 赤字的逐趟定位：器械缺陷、**真对齐档**、一个被自己否证的根因（2026-10-05）

> 接 [14-接续-Build与Integrate为何输给Unity.md](14-接续-Build与Integrate为何输给Unity.md)。
> 本文：**复现大盘** → **修掉两处器械缺陷**（doc14 §3.1 指定的下一步会因此得出错误结论）→
> **给出修正后的逐趟表** → **Unity 侧 batch/worker 对齐核对** → **两种对齐档实测**
> （旧法 `ENTJOY_TILES_PER_WORKER=2000` §4.4；**真法：关 JCC + batch 一致 §4.5**）→
> **H9 被自己的判决实验否证并撤回** → 收敛后的开放项（新首选 H12）。
>
> ▶ **后续（优化战役）从这里走 → [doc16 对齐档追平 Unity：战役计划与起点](16-对齐档追平Unity-战役计划与起点.md)**
> （固化了两侧账本已确证的常数、**已排除的 10 个落点**、per-tile 仪式的逐行证据与 F1–F5 方向）。

---

## 0. 结论（含一条**撤回**）

1. **大盘复现**：同会话配对，Unity 仍赢 Build（B/A = 0.79–0.95）与 Integrate（0.84–0.91），符号与量级同 doc14。
2. **⚠ 两处器械缺陷污染"下一步"**（§2）：
   - B 侧 `M4,build,*` 逐趟被**预热窗口放大 3.3×**（累加器从不复位）；
   - A 侧 `CPUBATTLE_DIAG_BUILDPASS=1` **自身把 Build 段抬高 0.19–0.51 ms/步**（`[M-20]` 指纹在 `Build()` 内、不在任何一趟计时里）。
   ⇒ **探针税 ≥ 待测赤字**；doc14 §3.1 按原样执行会把探针的 0.35 ms 读成"赤字确认"。
3. **赤字位置**：稳定落在**两个短趟** —— `prefixFinal`（A/B **1.57–2.06×**，5 次运行同号）与 `zero`（**1.48–1.58×**，5 次同号）；
   `count` 1.15–1.28×；`place` 方向不稳（0.97/1.20/1.37）。**历史靶子（Count/Place 原子争用）已不是主项。**
4. **❌ 撤回 H9（"`std::max(16,…)` 下限 → 只用 4 worker → `prefixFinal` 慢 2×"）**：
   对齐档恰好把这两个 64 项趟的 tile 数从 **rc=4 提到 rc=8**（JCC 实测 144/150 次），
   而 **`prefixFinal` 完全没动（0.1614 vs 0.1619）** ⇒ **worker 数不是 `prefixFinal` 慢的原因**。
   该假设在 §0 上一版里被列为"首选根因"，判决实验否证之。**`std::max(16,…)` 是真实的框架缺陷，但它不是本题赤字的解释。**
5. **旧的"对齐档"（只调全局 `ENTJOY_TILES_PER_WORKER=2000`）根本不是对齐**（§4.3–§4.4）：
   1M 趟只有 **~70%** 落在 Unity 粒度（chunk=63 ≈ Unity 的 64），其余 **29% 掉回 `chunk=125000`（8 个巨型静态块）**；
   且它让 Build **更差**（2.79 → 3.13，+12%），代价集中在 `count`（+17%）与 `prefixPartial`（+67%）。
   **复现 doc14 §1 的 0.8996→0.8183。**
6. **是框架问题吗？** —— **是"宿主/框架边界"的问题，不是内核的问题**：
   内核逐行同构、工等价、`/O2 + AVX2`、G 守卫折叠均已核实；
   Unity 自己的 **per-job 调度 floor 只有 0.21–0.95 µs**（§4.1 实测）⇒ **"B 少付调度"绝无可能解释 0.4 ms**。
7. **⭐ 真正的对齐档（JCC 关 + batch 逐调用点一致）本轮跑通，结论是把赤字放大**（§4.5）：
   段级 **Build B/A 0.52–0.66**（默认档 0.67–0.89）、**Integrate 0.74–0.81**（默认 0.87–0.95）；
   `count` 从 1.15–1.28× 恶化到 **1.87–1.92×**、`place` 从 0.97–1.37× 恶化到 **1.44–1.56×**；
   而与轨迹无关的三个短趟（`zero`/`prefixPartial`/`prefixFinal`）倍数与默认档**基本相同**。
   ⇒ **"对齐粒度"既不能消除赤字，也不是赤字的来源。**
   量化（A/A：同一输入、只改几何，372 次配对的边际）：**A 每多一个 tile 付 37–52 ns**（count/place）、14–21 ns（Melee）；
   而 Unity 的**空体受控**边际只有 **0.335 ns/batch**（15,625 批总 52.1 µs）⇒ **量级差 ~100×**。
   ⇒ **这是 JobSystem"每工作项认领/派发"的成本问题；A 选粗粒度是对它的理性补偿，不是选错。**（§6 的下一步据此改写）

> ⚠ **2026-10-07 独立复核（[doc17](17-独立复核-HEAD两档判据与Layer核验.md)）：本节的三条使用前提需更新。**
> 1. **Unity 那一侧的分母已复现**：`M4_DISP` 重跑（8 worker、60 rep）⇒ 1M 项 **batch=1 = 354 µs / batch=64 = 54.8 µs /
>    batch=1024 = 34.7 µs**，边际 **0.304 ns/declared batch**，空 `IJob` 往返 **1.20 µs** ⇒ 与 §4.1 在 8% 内一致。
>    **但 EntJoy 那一侧"13–52 ns/tile"本轮未重测** ⇒ 引用 ~40–100× 这个量级时，只有分母是新的。
> 2. **本节的结论 7 / H12（"每工作项成本"升为**新首选根因**）随后被否证**：doc16 §38 用交错配对复核
>    （认领令牌数降 **1000×**（25 万 → 244）而 Melee 只动 **−0.6%**）⇒ 该方向**已关闭**。
>    引用本节时**必须同时引用 §38 的关闭**，否则会重做已排除的落点。
> 3. **本节 §4.5 的"真对齐档"跨栈比值里有不可比的一段**：Integrate 在 Unity 侧**自证不可比**
>    （源码 `BattleBenchM4.cs:16-19` + 日志 `[M4-DISCLOSE]④` + `[M4-PROOF-5] 本档 af 全 0 ⇒ 首个执行步即全部回收`）
>    ⇒ `Integrate 0.74–0.81` 不能当作 codegen/粒度结论。见 doc17 §3。
> 4. 另：本节所有 `[M-1]` 读数现场 **assist 是开着的**（框架默认关、游戏默认开，脚本不设）——
>    doc17 §1.2 的 assist=0 对照显示该协议下关掉它反而更好（整步 1.024 / Melee 1.080）。

---

## 1. 复现大盘（同会话配对，A 用**无探针**臂）

B/A > 1 = EntJoy 更快。A = `A-plain`。

| 会话 | A Build | B Build | **B/A** | A Itg | B Itg | **B/A** | 负载（Melee A/B） |
|---|---|---|---|---|---|---|---|
| S1 | 2.45 | 2.3247 | **0.949** | 2.70 | 2.4164 | **0.895** | 86.6 / 89.1 |
| S2 | 2.92 | 2.2985 | **0.787** | 2.97 | 2.5002 | **0.842** | 100.1 / 91.5 ⚠ |
| S3 | 2.84 | 2.4529 | **0.864** | 2.80 | 2.5414 | **0.908** | 94.6 / 94.6 ✅ |

**Build 赤字 +0.13~+0.62 ms/步；Integrate 赤字 +0.26~+0.47 ms/步。**
A 的 Build **对负载敏感**（S1 2.45 → S3 2.84），B 的几乎不敏感（2.30–2.45）—— 这是跨会话漂移的主因。

---

## 2. ⚠ 两处器械缺陷

### 2.1 B 侧 `M4,build,*` 被预热窗口放大 ≈ 3.3×

`BattleBenchM4Entry.cs:251` 的 `m4BuildMs[6]` **每步都累加（含全部预热步）**，
而 `sBuild`（`BattleBenchM2.cs:210` `BuildTimed` 的宿主秒表）只在 `if (timed)` 分支累加；
两者最后同乘 `per = 1.0/steps` ⇒ **分子窗口不一致**。
本 campaign `M4_WARMUP ≈ 89–93`、`M4_STEPS ≈ 37–40`（`ab-3arm-2curve.ps1:120` 反推）⇒ 放大 ≈ `(W+S)/S ≈ 3.3×`。

**判据 `Σ逐趟 == sBuild × (W+S)/S`：**

| rep | W | S | Σ逐趟 | sBuild | 预测 | 观测/预测 |
|---|---|---|---|---|---|---|
| B-r2 | 89 | 40 | 8.0311 | 2.5136 | 8.1064 | **0.991** |
| B-r3 | 92 | 38 | 8.1517 | 2.4108 | 8.2475 | **0.988** |
| B-r4 | 92 | 40 | 8.4614 | 2.5471 | 8.4054 | **1.007** |
| B-r5 | 93 | 40 | 8.1720 | 2.6018 | 8.6510 | 0.945 |
| B-r6 | 93 | 40 | 8.2733 | 2.4927 | 8.2882 | **0.998** |
| B-r1 | 92 | 37 | 11.2416 | 2.6429 | 9.2144 | 1.220（冷启动离群） |

**恢复口径**：`逐趟真值 = 读数 × S/(W+S)`（一次性常数项 ≈ 0）。
修法：`BuildTimed` 的 `ms[]` 在预热结束处清零（`BattleBenchM1Flat.cs:315` 已是正确做法）。

### 2.2 A 侧 `CPUBATTLE_DIAG_BUILDPASS=1` 自己把 Build 抬高 0.19–0.51 ms/步

`CPUBattleSpatialHash.cs:198`：`_bpSteps % 32 == 0` 时在 `Build()` **内部**跑 `BpFingerprint()`（`[M-20]`），
它扫 `SortedIndex`（1.0M）+ `Counts`（351K）= **1.35M 次托管带检查索引**，
**在六趟计时之外、却在 `Build()` 之内** ⇒ 被 `BuildMs` 计入。

| 会话 | A-diag Build | A-plain Build | 探针税 |
|---|---|---|---|
| S1 | 2.96 | 2.45 | **+0.51（+20.8%）** |
| S2 | 3.11 | 2.92 | +0.19（+6.5%） |
| S3 | 3.34 | 2.84 | **+0.50（+17.6%）** |

一次指纹 ≈ 1.35M × ~7 ns ≈ 9–10 ms，每 32 步一次 ⇒ ≈ +0.30 ms/步，与实测同量级。
**连带后果**：diag 档下 `Σ六趟`（2.53–2.86）与 `Build 段`（2.96–3.34）必然不自洽 —— `[M-19]` 的 `Σ=` 自证只对它自己成立。
修法：把 `BpFingerprint/BpDump/BpPrint` 移出 `BuildMs` 计时范围，或降到每 1024 步一次。

---

## 3. 修正后的逐趟表

A = `[M-19]` 末窗；B = §2.1 恢复值。

| 趟 | A(S1) | A(S2) | A(S3) | B(S1) | B(S2) | B(S3) | A/B |
|---|---|---|---|---|---|---|---|
| `zero` | 0.1214 | 0.1289 | 0.1352 | 0.0819 | 0.0821 | 0.0859 | **1.48 / 1.57 / 1.58** |
| `count` | 0.8336 | 0.8575 | 0.9293 | 0.7249 | 0.7366 | 0.7287 | 1.15 / 1.16 / 1.28 |
| `prefixPartial` | 0.0460 | 0.0432 | 0.0475 | 0.0399 | 0.0410 | 0.0411 | 1.15 / 1.05 / 1.16 |
| `hostRewrite` | 0.0022 | 0.0022 | 0.0020 | 0.0001 | 0.0001 | 0.0001 | 0.002 ms，可忽略 |
| **`prefixFinal`** | **0.2012** | **0.1619** | **0.1657** | **0.0979** | **0.1030** | **0.1047** | **2.06 / 1.57 / 1.58** |
| `place` | 1.3252 | 1.6631 | 1.9818 | 1.3664 | 1.3867 | 1.4486 | 0.97 / 1.20 / 1.37 |
| **Σ** | 2.5296 | 2.8568 | 3.2615 | 2.3112 | 2.3495 | 2.4091 | 1.09 / 1.22 / 1.35 |

（B 原样读数示例 S1：`zero=0.2702 count=2.3923 pp=0.1318 hw=0.0003 pf=0.3230 place=4.5092`，× `40/132=0.30303`。）

**两条结构性观察**：
- `prefixPartial`（64 项、**每 cell 1 次读**）只差 **+2~+6 µs**；
  `prefixFinal`（64 项、**每 cell 读 + 2 次写**）差 **+60~+103 µs**。
  ⇒ 赤字随"每项访存量"放大，**不随 worker 数变化**（§4.2 直接验证）。
- `zero`（**单工作项**、1.4 MB 清零）差 +40~50 µs：A 11.6 GB/s vs B 17.1 GB/s。

---

## 4. Unity 侧的 batch / worker 对齐核对（本轮新测）

### 4.1 Unity 的调度 floor（`M4_DISP=1`，player，Burst，8 worker）

`BattleBenchDispatchFloor.cs` 是现成的空体器械。本轮实跑（`M4_DISP_REPS=60 M4_DISP_WARMUP=20`，全部 proof OK）：

| 配置 | 批次数 | 中位耗时 | 归一 |
|---|---|---|---|
| 1 个空 `IJob` 往返 | 1 | **0.95 µs** | — |
| 1 job × 1 项 | 1 | 2.2 µs | — |
| 100 job × 1 项 | 100 | 39.5 µs | **0.39 µs/job** |
| 1000 job × 1 项 | 1000 | 213.8 µs | **0.21 µs/job** |
| **1 job × 1,000,000 项，batch=1** | 1,000,000 | **382 µs** | 0.382 ns/项 |
| **1 job × 1,000,000 项，batch=64** | 15,625 | **52.1 µs** | 0.052 ns/项 |
| 1 job × 1,000,000 项，batch=1024 | 977 | 35.9 µs | 0.036 ns/项 |
| 派生：每个 declared batch（b1 vs b64） | — | **0.335 ns** | — |

**读法**：
- Unity 的**每个 job 调度 ≈ 0.2–1 µs** ⇒ Build 的六趟调度总开销 **≈ 6 µs，可忽略**。
  所以 B 的 Build（2.32–2.67 ms）**基本全是内核时间**，不存在"B 靠少调度赢"。
- batch=1 在此 N 下也只要 0.382 ms/1M ⇒ **doc08 的"`innerloopBatchCount=0 ⇒ 1`"即使成立，其代价上界也只有 0.38 ms/job**，
  在 Melee/Flow 这种 20–100 ms 的段里 < 1%。**它不是本题的量级。**
- 1M/batch=64 只要 52 µs ⇒ 8 个 worker 确实全部参与（0.052 ns/项 × 8 worker ≈ 1.5 cycle/项串行），**worker 数没被浪费**。

### 4.2 Unity 的 `innerloopBatchCount` 逐点表（player，M4 路径；`n=1,015,808`，`nLane=31,745`）

| 段 | 调用点 | length | 显式 batch | 工作项数 |
|---|---|---|---|---|
| Build `zero` | `Bb0M1ZeroJob.Schedule()`（IJob） | — | — | **1**（单工作项） |
| Build `count` | `BattleBenchM2.cs:225` | n | **64** | 15,872 |
| Build `prefixPartial` | `BattleBenchM2.cs:231` | **64** | **0** | 64（若 0⇒1） |
| Build `prefixFinal` | `BattleBenchM2.cs:242` | **64** | **0** | 64（若 0⇒1） |
| Build `place` | `BattleBenchM2.cs:251` | n | **64** | 15,872 |
| Integrate | `BattleBenchM4Entry.cs:462` | n | **64** | 15,872 |
| Melee | `BattleBenchM4Entry.cs:425` | n | **0** | — |
| MarkDead | `BattleBenchM4Entry.cs:436` | n | **0** | — |
| Spawn | `BattleBenchM4Entry.cs:354` | n | **0** | — |
| AliveBit | `BattleBenchM4Entry.cs:477` | nLane | **0** | — |
| Flow presence | `BattleBenchM2.cs:292` | 351,232 | **64** | 5,489 |
| Flow BFS 波 | `BattleBenchM2.cs:299` | w0+w1 | **0** | — |

Worker 数：`M4_WORKERS=8`，CSV `M4,whole,workers_readback,8` ✓（与 A 的 `ENTJOY_JOB_WORKERS=8` 一致）。

### 4.3 只用全局 `ENTJOY_TILES_PER_WORKER` 的"对齐档"：它和默认档**都没对齐**

`ENTJOY_JCC_VERBOSE=1` 实测（同一次 12 s 跑内各组 `[JCC]` 计数）：

| 档 | length | 决策分布 | ⇒ 实际几何 |
|---|---|---|---|
| **默认**（tpw=64） | 1,000,000 | 444× `MEM-BOUND→tpw chunk` / 82× `TWO-FACTOR chunk=125000 rc=8` | **512×1954** 为主，间歇掉到 **8×125000** |
| | 64 | 74× `MEM-BOUND→tpw chunk`（=16 ⇒ rc=4）/ 2× `rc=8` | **4×16** |
| **对齐**（tpw=2000） | 1,000,000 | 370× `MEM-BOUND→tpw chunk`（=63 ⇒ rc=15,874）/ **154× `TWO-FACTOR chunk=125000 rc=8`** / 7× `SCHED-DOMINATED chunk=31250 rc=32` | **~70% 是 15,874×63（≈Unity 的 15,625×64）、29% 掉回 8×125000** |
| | 64 | **144× `TWO-FACTOR chunk=8 rc=8`** / 6× `SCHED-DOMINATED chunk=16 rc=4` | **8×8** |

⇒ **"对齐档"只有约 70% 的时间真的在 Unity 粒度上**：tile 数由**一个全局 tpw 常数 + 一个有状态的 EWMA 分类器**共同决定，
同一个 job 的几何会随全局设置与学习历史翻转。**这不是一个稳定的对齐基线。**

### 4.4 该 tpw 版"对齐档"**更差**（复现 doc14 §1）

rep2（负载最对齐：Melee 93.7 / 94.1 / 95.1）：

| 臂 | Build | Integrate | Melee | **B/A（Build）** |
|---|---|---|---|---|
| A 默认档 | **2.79** | 3.09 | 94.10 | **0.886** |
| A 对齐档（tpw=2000） | **3.13（+12.2%）** | 4.27 | 93.66 | **0.790** |
| B | 2.4727 | 2.5288 | 95.10 | — |

（rep1 同向：默认 2.63 → 对齐 3.04，+15.6%。⇒ 与 doc14 §1 的"对齐档 Build 0.8183 < 默认档 0.8996"一致。）

**代价落点（`[M-19]`，取负载最接近的两次：对齐 rep2 Melee 92.16 vs 默认 S2 Melee 90.59）**：

| 趟 | 默认 | 对齐 | Δ |
|---|---|---|---|
| `zero` | 0.1289 | 0.1355 | +5% |
| `count` | 0.8575 | 1.0067 | **+17%** |
| `prefixPartial` | 0.0432 | 0.0722 | **+67%** |
| `prefixFinal` | 0.1619 | 0.1614 | **0%（rc 4→8 无效）** |
| `place` | 1.6631 | 1.5744 | −5% |

⇒ **对齐档的代价集中在 `count` 与 `prefixPartial`；而赤字项 `prefixFinal`/`zero` 完全不受影响。**

### 4.5 ⭐ **真正的对齐档：JCC 关 + batch 逐调用点一致**（本轮新做；对齐后赤字**更大**）

§4.3–§4.4 的"对齐档"（`ENTJOY_TILES_PER_WORKER=2000`）**不是对齐**：它只改一个全局常数，JCC 照样参与决策，
~30% 的调度仍掉回 8 个巨型块。**正确的对齐 = ① 关掉 JCC + ② batchSize 与 Unity 逐调用点逐一相同。**

**实现**：`ENTJOY_JOB_BATCH_TABLE="<key>:<batch>,…"` —— 命中即 **`ResolveChunkSize` 根本不被调用**
（`JobSystem_Scheduler.cpp:944-946` 的 `forced` 分支）⇒ 决策层面没有 JCC。键是**内核在其模块内的 RVA**，
**与构建绑定**（任何改动生成代码的重构都会移动它）。

**本构建的 15 个键（本轮重新导出，非引用 doc08 的旧表 —— 旧表已因重建而失效）**：
`[JOBBATCHTBL]` dump + `tools/gate-run/pe-exports.ps1`（自写 PE 导出表解析器，无 dumpbin 也能做）：

| key (RVA) | EntJoy 内核 | Unity 对应调用点 | 表值 | 命中后 tiles |
|---|---|---|---|---|
| `00001990` | `CountCellsJob` | `M2.cs:225` `Schedule(n, 64)` | **64** | 15,625 |
| `00010ab0` | `PlaceCellsJob` | `M2.cs:251` `Schedule(n, 64)` | **64** | 15,625 |
| `00006040` | `IntegrateJob` | `M4Entry.cs:462` `Schedule(n, 64)` | **64** | 15,625 |
| `000053b0` | `FlowPresenceJob` | `M2.cs:292` `Schedule(n, 64)` | **64** | 15,625 |
| `00010f60` | `PrefixSumPartialJob` | `M2.cs:231` `Schedule(64, 0)` | **1** | 64 |
| `00010cf0` | `PrefixSumFinalJob` | `M2.cs:242` `Schedule(64, 0)` | **1** | 64 |
| `0000c4f0` | `MeleeSimJob` | `M4Entry.cs:425` `Schedule(n, 0)` | **1** | 1,000,000 |
| `00006bd0` | `MarkDeadJob` | `M4Entry.cs:436` `Schedule(n, 0)` | **1** | 1,000,000 |
| `00011970` | `SpawnJob` | `M4Entry.cs:354` `Schedule(n, 0)` | **1** | 1,000,000 |
| `00001720` | `ClearAllJob` | `M4Entry.cs:332` `Schedule(n, 0)` | **1** | 1,000,000 |
| `00003a80` / `00005650` / `000051d0` | `FlowClearJob` / `FlowSeedJob` / `FlowGradJob` | `M2.cs:317/330/369` `Schedule(n, 0)` | **1** | 351,232 |
| `00005510` / `00003700` | `FlowSeedInitJob` / `FlowBfsWaveJobDual` | `M2.cs:353/299` `Schedule(…, 0)` | **1** | 18,836 |

**生效自证**（`ENTJOY_JOB_BATCH_TABLE_DUMP=1`，一次 12 s 跑）：

```
[JOBBATCHTBL] key=00001990 N=1000000 tiles=15625 applied=64 hit=1   ← Count/Place/Integrate/FlowPresence
[JOBBATCHTBL] key=00010f60 N=64      tiles=64    applied=1  hit=1   ← 两个前缀趟
[JOBBATCHTBL] key=0000c4f0 N=1000000 tiles=1000000 applied=1 hit=1 ← Melee/MarkDead/Spawn/ClearAll
[JCC] R lines = 0                                                  ← JCC 决策一次都没发生
[JOBF2F4] uniformTiles=ON applied=22006
```

（旁证：`From` 本构建的 `tiles=15625` 对应 A 的 `n=1,000,000`；Unity 侧 `n=1,015,808` ⇒ 15,872 —— 同粒度。）

**结果（2 对；`A-mirror` = 表；`A-mirror-nof6` = 表 + `ENTJOY_CLAIM_ADAPT=0`）**

| rep | A-def Build | **A-mirror Build** | A-mirror-nof6 | B Build | B/A def | **B/A mirror** | B/A nof6 |
|---|---|---|---|---|---|---|---|
| 1 | 3.66 | **3.97** | 4.28 | 2.4402 | 0.667 | **0.615** | 0.570 |
| 2 | 2.73 | **4.67** | 3.68 | 2.4256 | 0.888 | **0.519** | 0.659 |

| rep | A-def Itg | **A-mirror Itg** | B Itg | B/A def | **B/A mirror** |
|---|---|---|---|---|---|
| 1 | 2.73 | **3.20** | 2.6054 | 0.954 | **0.814** |
| 2 | 2.87 | **3.38** | 2.4879 | 0.867 | **0.736** |

⇒ **真正对齐之后，Build 赤字从 0.67–0.89 恶化到 0.52–0.66；Integrate 从 0.87–0.95 恶化到 0.74–0.81。**

**逐趟（`A-mirror-diag [M-19]` vs B 恢复值）**

| 趟 | A-mir(1) | B(1) | A/B | A-mir(2) | B(2) | A/B | 默认档 A/B（对照） |
|---|---|---|---|---|---|---|---|
| `zero` | 0.1185 | 0.0912 | 1.30 | 0.1320 | 0.0859 | 1.54 | 1.48 / 1.57 / 1.58 |
| **`count`** | **1.4386** | 0.7715 | **1.87** | **1.4855** | 0.7735 | **1.92** | 1.15 / 1.16 / 1.28 |
| `prefixPartial` | 0.0562 | 0.0421 | 1.34 | 0.0562 | 0.0391 | 1.44 | 1.15 / 1.05 / 1.16 |
| `hostRewrite` | 0.0022 | 0.0001 | — | 0.0023 | 0.0001 | — | — |
| `prefixFinal` | 0.1507 | 0.1038 | 1.45 | 0.1784 | 0.1058 | 1.69 | 1.57 / 1.57 / 1.58 |
| **`place`** | **2.1122** | 1.4641 | **1.44** | **2.1763** | 1.3931 | **1.56** | 0.97 / 1.20 / 1.37 |
| **Σ** | 3.8784 | 2.3268 | **1.67** | 4.0308 | 2.2525 | **1.79** | 1.09 / 1.22 / 1.35 |

**读法（这是本轮最强的结论）**：
1. **对齐粒度后，两个原子重趟（`count`/`place`）大幅恶化**：`count` 1.87–1.92×、`place` 1.44–1.56×。
   即 **在完全相同的粒度（15,625×64）下，A 的原子趟仍比 B 慢 ~2×/1.5×**。
2. **三个与仿真轨迹无关的短趟（`zero`/`prefixPartial`/`prefixFinal`）倍数与默认档基本一致**
   （1.30–1.54 / 1.34–1.44 / 1.45–1.69）⇒ **改粒度对它们毫无帮助**，与 §5 的否证一致。
3. ⇒ **赤字不是"粒度选错"**：A 的默认粗粒度（512 块）反而是对自身每-tile 成本的**理性补偿**。

**量化：A 的每工作项成本 ≈ Unity 的 100×**

| 量 | 测法 | 值 |
|---|---|---|
| A `count` 边际 | A/A：512 块 → 15,625 块（同输入，工作量几乎相同：都是处理约 1M 存活单位） | **+0.605 / +0.556 ms ÷ 15,113 tiles = 40.0 / 36.8 ns/tile** |
| A `place` 边际 | 同上 | **+0.787 / +0.195 ms ⇒ 52.1 / 12.9 ns/tile** |
| （旁证，带污染）A `MeleeSimJob` | def → mirror-plain（~1M tiles） | +13.9 / +21.0 ms ÷ ~1e6 ≈ **13.9 / 21.0 ns/tile** ⚠ 见下 |
| **Unity 边际（受控空体）** | `M4_DISP`，batch 1 vs 64 vs 1024 | **0.335 ns/batch**；15,625 批总计 **52.1 µs = 3.3 ns/batch** |

⇒ A 每工作项 13–52 ns vs Unity 0.335–3.3 ns —— **10–150×，中心 ~40×**。
（Unity 那侧是空体、纯调度；A 那侧含该粒度下内核侧的额外开销，故"~100×"应读作**上界**；
但即便只认 3.3 ns/batch 的总量口径，A 在 15,625 个 tile 上单是 `count`+`place` 就多付 ≥1.3 ms，**超过默认档的全部赤字**。）

⚠ **本节的绝对数字带轨迹分叉污染**：A 的几何一旦确定，`SortedIndex` 的 cell 内顺序就确定
（`[M-20]` 早已记录该顺序是竞态产物），而 Melee 的平局判定读它 ⇒ mirror 臂的仿真轨迹与 default/B 不同
（Melee：mirror 99.4–116.3 vs def 90.5–93.0 vs B 91.7–92.4）。故：
- **主证据（不受污染）**：`count`/`place` 的 **A/A 边际**（同一输入、同一会话、工作量几乎相同，只有几何变）
  与 **Unity 的受控空体 floor**；
- **带污染**：`MeleeSimJob` 那一行（它的成本本来就由轨迹支配）与**跨栈的 1.87–1.92×**（是有偏上界）。

---

## 5. 判决结果：H9 **被否证**，撤回

**H9（上一版本文的首选根因）**：`ResolveChunkSize` 的 `std::max(16, …)` 下限 ⇒ 64 项前缀趟 `chunk=16` ⇒ `rc=4`
⇒ 只用 4/8 个 worker ⇒ `prefixFinal` 慢 2×。

**预测**：把 rc 从 4 提到 8，`prefixFinal` 应从 ~0.16–0.20 → ~0.08–0.10。
**实测**：对齐档做到了 rc=8（144/150 次），**`prefixFinal` = 0.1614（vs 默认 0.1619）—— 一点没动。**

⇒ **否证。** `ResolveChunkSize` 的 `std::max(16, …)` 下限**是真缺陷**（`length ≤ 16` 时 `rc ≤ 1` 会整段串行在调用线程，
`length < 16×W` 时 tile 数 < worker 数），**但它不是 Build 赤字的解释**。

**从否证里得到的正向线索**：`prefixFinal` 与 `prefixPartial` 是**同一 length、同一 rc、同一 worker 数**的两个趟，
差别只在**每 cell 的访存量**（1 读 vs 1 读 + 2 写）：

| | 每 cell 访存 | A−B 绝对差 |
|---|---|---|
| `prefixPartial` | 1 读 | **+2 ~ +6 µs** |
| `prefixFinal` | 1 读 + 2 写 | **+60 ~ +103 µs** |

⇒ 赤字不是"并行度"，是**每-cell 访存效率**（RMW/写分配/别名），与 `zero`（1.4 MB 清零，A 11.6 vs B 17.1 GB/s）同族。

### 5.1 假设表（对 doc14 §5 的更新）

| # | 假设 | 本轮证据 | 判定 |
|---|---|---|---|
| **H1** | **每趟/每次派发的固定开销** | Unity floor 受控实测 0.21–0.95 µs/job（§4.1）；A 侧边际 **13–52 ns/tile**（§4.5） | **✅ 升为首选根因**（A 侧待 `[M-15]` 定量） |
| H3 | 粒度/均衡 | 真对齐档（§4.5）把 count/place 恶化 1.4–1.9×；短趟倍数不变 | **否 —— 粒度不是来源** |
| H5 | 认领几何/共享计数器争用 | `place` 方向不稳；F6 已生效（`sliced=1055 interleaved=32059`） | **已被覆盖** |
| H6 | 工作量不等价 | 逐行同构 + 池/墙/列宽/`chunkSize=5488`/`CellCount=351,232` 全部对齐 | **否** |
| H7 | 生成码差异 | G 折叠、`__forceinline` 助手、`/O2 + AVX2`、DLL 与 `build\Release` 同 MD5 | **否（整段层面）**；**短 RMW 内核未排除** |
| ~~H9~~ | tile 数 < worker 数 | 对齐档把 rc 4→8 而 `prefixFinal` 不变 | **❌ 已否证（本文撤回）** |
| **H10** | 短 job 几何在 512 块 / 8 块之间摆动 | `[JCC] length=1M` 444×512块 与 82×8块；对齐档 29% 掉回 8 块 | **成立，但关掉 JCC 后赤字反而更大 ⇒ 不是来源** |
| **H11** | 短 job 的每-cell 访存效率（RMW/写分配/分配布局） | `pp` +2~6 µs vs `pf` +60~103 µs，同 rc 同 worker | **仍开放**（`zero`/`pf`/`pp` 的 1.3–1.7× 需要它） |
| **H12** | **每工作项（tile 认领/派发）成本** | A/A 边际 13–52 ns/tile vs Unity 受控 0.335–3.3 ns/batch（§4.5） | **✅ 新首选根因** |

---

## 6. 下一步（按"证据强度 ÷ 成本"排序；★ = 因 §4.5 而改判）

1. **器械修复（必须先做，否则任何逐趟读数都带 3.3× / 探针税）**：
   - B：`BattleBenchM2.cs:210` `BuildTimed` 在预热结束清零 `ms[]`；
   - A：`CPUBattleSpatialHash.cs` 把 `BpFingerprint/BpDump/BpPrint` 移出 `BuildMs`。
   验收判据：`Σ六趟 ≈ Build 段`（diag 档，|残差| < 2%）。
2. ★ **攻 H12（新首选）：量出 A 的 per-tile 认领/派发成本，并定位它花在哪。**
   已有对照面：Unity 空体边际 **0.335 ns/batch**（`M4_DISP`，本仓已有器械）；A 现测 **13–52 ns/tile**（A/A 边际）。
   分解建议（按成本递增）：
   - ① **`ENTJOY_JCC_TARGET_US` / 表批值二维扫**（同一 job 在 64 / 256 / 1024 元素/tile 上的 A 曲线），
     与 Unity 的 `M4_DISP` 同形状曲线叠图 ⇒ 得到"每 tile"与"每元素"两个系数；
   - ② 用 `[M-15]` 派发探针（`_SPLIT=1` 分"只提交"/"只等待"）把 A 的每 tile 成本拆成
     **认领原子 / 唤醒 / 收敛** 三段；
   - ③ 读 `ChaseLevScheduler` 的认领游标与 `TryExecuteOneTile`/`TileAcctGroupFlush` 路径
     （doc 07 §16.43(q/r) 已把落点指到 `ChaseLevScheduler:828-844` / `:179-183`），确认 15,625 个 tile 时
     是否每个 tile 都付一次原子/一次计数回写。
3. **H11（仍开放）**：`zero`/`prefixFinal`/`prefixPartial` 的 1.3–1.7× 与轨迹无关、也与粒度无关，
   说明它是**每-cell 访存 / 短 job 固定成本**。判据：给 `ZeroCellsJob` 的 `Length` 放大 4× 看是否线性
   （线性 ⇒ 内核；常数 ⇒ 派遣）；给 `prefixFinal` 做独立微基准（`JobLibsBenchmark` 已有同算法器械）。
4. **查分配布局**：`Counts` 与 `CellStart` 的相对偏移（4K 别名 / 写分配）在 A 与 B 可能不同。
   判据：把 A 的 `CellStart` 分配偏移人为错开 4 KB，看 `prefixFinal` 是否变化。
5. **H8（扫 N）**：Integrate 只能靠它（内核已证同构）；应在 1/2 之后做，且**必须在真对齐档与默认档各做一遍**。
6. **不要做的事**：不要再试"给这些小 job 调粒度/调 `kTargetTileUs`" —— §4.5 已证明对齐粒度会让赤字变大；
   `ENTJOY_TILES_PER_WORKER` 那条路（doc14 §3.2）在真对齐档面前已被取代。

---

## 7. 复现命令与产物

```powershell
# A 默认档逐趟（含探针税）+ 无探针基线 + 相位对齐 B
powershell -NoProfile -ExecutionPolicy Bypass -File tools\gate-run\perpass-build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools\gate-run\perpass-rep.ps1 -Reps 2 -Jcc
# A "只改全局 tpw" 的对齐档（tpw=2000）三臂 + JCC 取证  —— 注意：这不是真对齐，见 §4.3
powershell -NoProfile -ExecutionPolicy Bypass -File tools\gate-run\perpass-aligned.ps1 -Reps 2 -Jcc
# ★ 真对齐档：JCC 关 + batch 逐调用点一致（表键与构建绑定）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\gate-run\perpass-mirror.ps1 -Verify
powershell -NoProfile -ExecutionPolicy Bypass -File tools\gate-run\perpass-mirror.ps1 -Reps 2 -NoF6
powershell -NoProfile -ExecutionPolicy Bypass -File tools\gate-run\compare-mirror.ps1
# 表键重建（任何重建后必须重推）：dump + 自写 PE 导出表解析（无需 dumpbin）
#   ENTJOY_JOB_BATCH_TABLE_DUMP=1 <run>   ->  [JOBBATCHTBL] key=<8hex>
#   powershell -File tools\gate-run\pe-exports.ps1 -Path <NativeTranspiled.dll> -Filter _Execute_Adapter
# Unity 调度 floor（player，空体，自带 proof）
#   M4_DISP=1 M4_WORKERS=8 M4_DISP_REPS=60 M4_DISP_WARMUP=20 M4_CSV=...
```

| 产物 | 内容 |
|---|---|
| `tools/gate-run/perpass/` | S1 三件套（`A-diag.log` 的 `[M-19]`×6 + `[M-20]`×6） |
| `tools/gate-run/perpass2/` | S2/S3 三件套 |
| `tools/gate-run/perpass-aligned/` | tpw=2000 对齐档（**非真对齐**）：三臂 + `[JCC]` 分布 |
| **`tools/gate-run/perpass-mirror/`** | **真对齐档**：`mirror-table.txt`（15 键）、`A-verify.*`（`[JOBBATCHTBL]` 自证）、`A-mirror-{diag,plain}-r*`、`A-mirror-nof6-plain-r*`、`A-def-plain-r*`、`B-r*` |
| **`tools/gate-run/keymap/`** | `exports.txt` / `exports_nativedll.txt`（RVA→内核名）+ `dump.*`（`[JOBBATCHTBL]`/`[JOBPERKEY]`） |
| `tools/gate-run/jccprobe/jcc.stdout.txt` | 默认档 66,373 行 `[JCC]` |
| `tools/gate-run/dispfloor/disp.csv` | Unity floor：batch{1,64,1024} × {1M, 1024×M} + proof |
| 历史 B 逐趟（原样未恢复） | `tools/gate-run/3arm-2curve/off/B-r*.csv` |

---

## 8. 局限

1. **A 的 `[M-19]` 出生在 diag 档**：指纹每 32 步污染一次缓存（1.35M 访存）⇒ 绝对值可能整体偏高，
   `place`/`count` 这类访存敏感趟偏高更多。`zero`/`prefixFinal` 的**倍数**在 5 次运行上稳定，故结论不依赖该偏差；
   `place` 的 0.97→1.37 摆动**未定性**。
2. **会话负载未完全对齐**（S2 差 9%、对齐档 rep1 差 16%）；§4.4 已取最接近的一对，并逐行标注。
3. ~~`[JCC]` 的 `hash=` 是函数指针哈希 ⇒ 未做 hash→job 名映射~~ **已解决**：
   用 `ENTJOY_JOB_BATCH_TABLE_DUMP=1` 拿 `[JOBBATCHTBL]` 的模块-RVA 键，再用自写
   `tools/gate-run/pe-exports.ps1` 解析 `NativeTranspiled.dll` 的导出表 ⇒ **RVA→内核名逐一对上**（§4.5 表）。
   结果证实 `N=64` 的内核**只有** `PrefixSumPartialJob`/`PrefixSumFinalJob` 两个 ⇒ §4.3 的归属**成立**，
   §5 的否证（rc 4→8 而 `prefixFinal` 不动）作用对象确认无误。
   ⚠ 但**键与构建绑定**：doc08 §2 的旧键表已因重建失效（例：`CountCellsJob` 从 `00001950` → `00001990`），
   以后每次重建都必须重推。
4. **真对齐档的轨迹分叉（§4.5 的主要局限）**：几何一旦钉死，`SortedIndex` 的 cell 内顺序就钉死，
   而 Melee 的平局判定读它 ⇒ mirror 臂的仿真轨迹与 default/B 不同
   （Melee 99.4–116.3 vs 90.5–93.0 vs 91.7–92.4）。**故跨栈的 1.87–1.92× 是有偏上界**；
   本节结论主要依赖 **(a) A/A 同输入的每-tile 边际** 与 **(b) Unity 受控空体 floor**，两者都不受该污染。
   若要消掉这个偏差，需要一条**轨迹不敏感**的对照（例如在固定 dump 上跑 N 步不推进战斗的纯 Build 循环）。
5. `A-mirror-nof6`（`ENTJOY_CLAIM_ADAPT=0`）两 rep 方向相反（rep1 4.28 更差 / rep2 3.68 更好）⇒ **F6 在此配置下未判**。
6. 本文**未改任何代码**（框架与游戏仓均未动）；§4.5 的镜像表、`[JCC] R lines = 0`、`applied=` 与 floor 数字均为**实测**，
   §4.4 为实测，其余为算术预测。
5. Unity floor 的 `batch=0` 语义**本轮未直接测**（器械只扫 {1,64,1024}）；doc08 的"`0⇒1`"仍是 Editor 结论、
   带同版本假设。本轮只给出其**代价上界 0.38 ms/1M 项**（§4.1），故该未决项不影响本题量级。
