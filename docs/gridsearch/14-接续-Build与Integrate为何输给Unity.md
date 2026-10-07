# 14 · 接续：为什么 Unity 赢 Build + Integrate（2026-10-05 开题）

**新会话从这里开始。** 本文只做三件事：给准数据、给已就绪的器械、给"不要重做"的历史结论与判决实验。

---

## 0. 一句话结论（先读这段，它决定本题怎么开）

**Build 的赤字不是新问题**：根因（**认领顺序 → 相邻 tile 同时刻落给不同 worker → 争同一条 cell 计数器 cacheline**）
已在 2026-09-30 坐实，且通解候选已作为**按调用点声明认领几何**（`ClaimPolicy.Spread` / 批表第 4 字段 / F6 学习）落地。

**本轮真正的新事实是"两件事同时发生了"**：Build 赤字从历史 **87%** 收窄到 **13.4%**，
但 EntJoy 在 **Melee 的领先也从 7.3% 收窄到 1.6%** ⇒ 总账从 **+5.35%（6/6）** 变成 **≈0（3/6）**。

> ⇒ **本题必须与"Melee/Flow 的领先为何消失"同会话一起测。**
> 否则会陷入"修 Build、丢 Melee"的往复 —— 历史上的 `ENTJOY_CLAIM_SLICE` / `ENTJOY_CLAIM_BLOCK` 两次都正是这样被否证的。

---

## 1. 本轮实测（出厂默认档，6 对，末窗相位对齐，同会话）

比值为 **B/A，>1 = EntJoy 更快**。绝对值为两侧中位（ms）。

| 段 | EntJoy `def` | Unity `B` | **B/A** | 同号 | A−B（ms） |
|---|---|---|---|---|---|
| **Build** | 2.92 | **2.53** | **0.8996** | **0/6**（Unity 6/6 快） | **−0.38** |
| Flow | 24.19 | 24.36 | 1.0042 | 4/6 | +0.18 |
| **Melee** | 94.01 | 95.57 | 1.0075 | 3/6 | +1.56 |
| MarkDead | 0.55 | 0.71 | **1.2858** | **6/6**（EntJoy 6/6 快） | +0.16 |
| **Integrate** | 2.87 | **2.63** | **0.9245** | **0/6**（Unity 6/6 快） | **−0.24** |
| **总计** | 124.74 | 125.80 | **1.0001** | 3/6 | +1.07 |

对齐档（`ENTJOY_TILES_PER_WORKER=2000`，63 元素/tile）：Build 0.8183（0/6）、Integrate 0.8579（0/6）、总计 0.9810（2/6）。

**量级要诚实**：Build+Integrate 两段合计只 5.8 ms / 124.7 ms（4.6%），Unity 的净赢只有 **0.62 ms（0.5%）**。
**但符号 0/6、0/6 都是满号** ⇒ 这是**系统性**差异，不是噪声。它的价值在于**诊断**（同一根因可能还有更大的量没被吃到），
而不在于这 0.5 ms 本身。

> ⚠ **2026-10-07 独立复核（[doc17](17-独立复核-HEAD两档判据与Layer核验.md)）：本表的默认档读数已过期，且 Integrate 那一列不对等。**
> - **同一相位匹配器械在 HEAD（`89e135a`）上重测（6 轮）**：整步 **1.016（4/6）**、Build **0.829（0/6）**、
>   Flow 0.991、Melee 1.029、MarkDead 1.291、**Integrate 0.892（0/6）**。⇒ **Build 从 0.900 掉到 0.829**；
>   方向与"§43 的值绑定白名单**已在 HEAD 删除**、热内核回到按引用"一致，但跨会话 Build 漂移可达 18%
>   ⇒ **幅度未隔离**，不要引用成"白名单删除的代价 = 7%"。
> - **Integrate 不对等**：Unity 侧源码（`BattleBenchM4.cs:16-19`）与运行日志（`[M4-DISCLOSE]④`）两处自证
>   "**Integrate 段不可比**"（dump v2 无 velocity/knock/af ⇒ B 每步把 vel/knock/af/stuck 清零、一步回收尸体，
>   而 A 走全部分支）⇒ **本表的 Integrate 0.9245 不能解读为 codegen 差距**。见 doc17 §3。
> - §3.2 的 env 表**漏了三个当前必需的 env**：`ENTJOY_ASSIST`（**游戏侧** env、默认**开**。⚠ **脚本之间不一致**：
>   `ab-aligned.ps1` 等老脚本显式 pin `=0`，而 2026-10-05 之后的战役脚本先清空全部 `ENTJOY_*` 再只设自己那几个
>   ⇒ 回落到"开"；doc15–doc17 的读数因此含 assist，而 doc10 的不含。本协议下关掉它反而更快）、
>   `ENTJOY_JOB_COST_CACHE`、`ENTJOY_JOB_BATCH_BY_NAME`（后两个是"对齐档"的定义开关）。权威表见
>   [Gates-and-Flags](../public/Gates-and-Flags.md)。

**同时注意（别漏）**：`MarkDead` 是 **6/6、EntJoy 快 28.6%**，也是满号但只有 0.16 ms。三段小项都有满号符号差异。

---

## 2. 两侧的段定义（口径必须对齐，否则不是同一件事）

| 段 | A 侧（EntJoy / Godot） | B 侧（Unity `W0Player.exe`） |
|---|---|---|
| **Build** | `CpuBattleShared.BuildMs` = `CPUBattleSpatialHash.Build()` —— **空间哈希 6 趟**：`zero → count → prefixPartial → hostRewrite → prefixFinal → place`，**逐趟 schedule（趟间有 Complete）** | `BattleBenchM4Entry.cs` 累计 `sBuild`；CSV 里**已按同一 6 趟拆好** |
| **Integrate** | `IntegrateJob`（8b：尸体倒计时/回收、受击进 HURT、击退、位置积分），在 `MarkDeadJob` 之后调度 | `sInteg` |
| **MarkDead** | `MarkDeadJob`（8b 前半：HP≤0 → DEATH，独立 pre-pass） | `sMark` |
| N | **1,015,808** 实体（单 chunk，容量 1,015,808） | 同一份 dump（`astate_v2s60.bin`） |

A 侧源码：`CPUBattleSystems.cs`（`BuildMs/IntegrateMs/MarkDeadMs` 定义 + 各 System）、`CPUBattleSpatialHash.cs`、`CPUBattleCombat.cs`（`MarkDeadJob` / `IntegrateJob`）。

---

## 3. 已就绪的器械（**零新增代码即可开跑**）

### 3.1 两侧 Build 逐趟**列名已一一对齐** —— 第一件事就是拉这张表

> ⚠ **2026-10-05 更正（见 [doc15](15-Build与Integrate逐趟定位-器械缺陷与对齐档实测.md) §2，不要按本节原样开跑）**：
> 两侧的逐趟器械**都带污染**，先修再读 ——
> ① B 侧 `M4,build,*`（`BattleBenchM4Entry.cs:680-685` / `BattleBenchM2.cs:210`）的累加器**含全部预热步且从不复位**，
> 而 `sBuild` 只累加 timed 步 ⇒ 读数被放大约 `(W+S)/S ≈ 3.3×`（本 campaign `M4_WARMUP≈89-93`、`M4_STEPS≈37-40`）；
> ② A 侧 `CPUBATTLE_DIAG_BUILDPASS=1` 里的 `BpFingerprint()`（`[M-20]`，`CPUBattleSpatialHash.cs:198`）
> 每 32 步在 `Build()` **内部**跑 1.35M 次托管索引，**在六趟计时之外、却在 `BuildMs` 之内**
> ⇒ 把 A 的 Build 段抬高 **+0.19~+0.51 ms/步**（同会话实测），即"探针税 ≥ 待测赤字"；
> ⇒ diag 档下 `[M-19] 的 Σ` 与 `[M-1] 的 Build` 必然不自洽（实测 2.53 vs 2.96）。
> 修法（B 清 `ms[]`、A 把指纹移出 `BuildMs`）与修好后的逐趟表见 doc15 §2–§3。

| A 侧（`CPUBATTLE_DIAG_BUILDPASS=1` → `[M-19]`） | B 侧（CSV 行） |
|---|---|
| `zero=` / `count=` / `prefixPartial=` / `hostRewrite=` / `prefixFinal=` / `place=`（`Σ=` 自证） | `M4,build,zero_ms` / `count_ms` / `prefixPartial_ms` / `hostRewrite_ms` / `prefixFinal_ms` / `place_ms` |

⇒ **把 Build 的 0.38 ms 赤字定位到具体哪一趟，不需要写任何新代码**（历史上 §7ar 的逐趟表就是这么来的）。

### 3.2 A 侧 env

| env | 作用 |
|---|---|
| `CPUBATTLE_AUTODEPLOY=1` `CPUBATTLE_NO_RENDER_PATH=1` `CPUBATTLE_AUTOEXIT=<s>` `CPUBATTLE_STATS_WINDOW=5` | 基准运行必需 |
| `ENTJOY_JOB_WORKERS=8` | worker 数（与 Unity 侧 `M4_WORKERS=8` 对齐） |
| `CPUBATTLE_DIAG_BUILDPASS=1` | **Build 6 趟逐趟计时**（`[M-19]`，走 Godot 日志） |
| `CPUBATTLE_FLOW_DISPATCH_PROBE_CELLS/WAVES/BATCH/REPEATS/SPLIT/EMPTY` | `[M-15]` 派发探针；**`_SPLIT=1` 把"只提交"与"只等待"分相**、`_REPEATS=n` 多窗取 min |
| `ENTJOY_FORCE_INNER_BATCH=<n>` | 强制显式内批（**绕过 JCC**，等同"调用方显式传 batch"） |
| `ENTJOY_TILES_PER_WORKER=<n>` | 只调**全局**粒度（`2000` ⇒ ~63 元素/tile）。⚠ **2026-10-05 实测：它只在 ~70% 的调度上生效** —— 1M 趟有 29% 走 `TWO-FACTOR chunk=125000 rc=8`（8 个巨型静态块），比例随 EWMA 分类器翻转；**所以这不是"对齐档"**，见 [doc15](15-Build与Integrate逐趟定位-器械缺陷与对齐档实测.md) §4.3–§4.4 |
| `ENTJOY_CLAIM_ADAPT=0/1` | F6 per-job 认领几何学习（**默认开**） |
| `ENTJOY_JOB_BATCH_TABLE` / `_DUMP=1` | **⭐ 真对齐档的正确器械**：逐 job 内批（命中即 **`ResolveChunkSize` 不被调用 ⇒ JCC 全关**）。格式 `<模块RVA>:<batch>[, …]`；键**与构建绑定**，每次重建必须用 `_DUMP=1` + `tools/gate-run/pe-exports.ps1` 重推。本构建 15 个键 ↔ Unity 逐调用点的完整对照表见 [doc15 §4.5](15-Build与Integrate逐趟定位-器械缺陷与对齐档实测.md)；自证看 `[JOBBATCHTBL] applied=` 与 `[JCC] R lines = 0` |
| `ENTJOY_JCC_TARGET_US=<n>` | 目标每 tile 串行量（默认 6400） |
| `ENTJOY_JCC_VERBOSE=1` / `ENTJOY_DIAG_JCC=1` | JCC 决策取证 |

运行时只看 stdout：`[JOBF2F4]`（F2/F4 生效）、`[JOBGEOM] declared spread=/adjacent=/auto=`、`[JOBF6] claimAdapt= flips=`、`[JOBPERKEY]`、`[JOBBATCHTBL]`；
看 Godot 日志：`[M-1]`（五段）、`[M-19]`（Build 逐趟）、`[M-15]`（派发）、`[M-16]`（BFS 波调度）。

### 3.3 B 侧 env + CSV

`M4_PLAYER_RUN=1` `M4_WORKERS=8` `M4_DUMP=<同一 dump>` `M4_WARMUP=<warm>` `M4_STEPS=<steps>` `M4_CSV=<path>`；
可选 `M4_INTEGRATE`（**Integrate 开关** ⇒ 隔离"派遣开销 vs 计算"）、`M4_EARLYSTOP`、`M4_WALLS`、`M2_ARM`。
`W0Player.exe -batchmode -nographics -logFile <log>`，工作目录 = `Build\W0Player`。

---

## 4. 历史结论 —— **不要重做**（出处：`docs/gridsearch/07` §7ar + 本仓 `docs/handoff-ABC-and-open-items.md` 顶部第十二轮块）

1. **Build 赤字的根因已坐实 = 共享计数器争用，不是核函数、不是粒度**。三条独立证据（真实 dump，8 worker）：
   ① **去原子**消融：Count 在 62/977/15625 tiles = **0.709/0.743/0.924 ms（粒度无关）**，含原子 = 0.781/3.211/2.178 ⇒ **原子项 0.07~2.47 ms（35× 摆动）**；
   ② **打乱输入 index 序**：原子项塌到 0.265/0.278/0.283 ms；
   ③ **只改认领顺序**（当时器械 `ENTJOY_TILE_STRIDE`）：3907 tiles 5.573→**3.530**、977 tiles 5.744→**3.827**。
   ⇒ 根因 = `nextTile.fetch_add` 把**相邻** tile 交给**同时刻的不同 worker**（真实数据在 index 序上空间连贯）。
2. **同粒度逐趟（Editor，n=1e6）Σ = 5.244 vs 5.115 ⇒ 1.03×，互有胜负**（EntJoy 的 `place` 还快 11%）
   ⇒ **两边一样快，"很多轴追上了整体没追上"就是因为争用是框架亲手造的、不在任何单点轴上。**
3. **通解候选（切片认领）被游戏内 A/B 否证"全局打开"**：Build 4.09→**2.88（−1.21，3/3）**、Flow 30.20→28.26（−1.94，3/3），
   但 **Melee 122.58→128.48（+5.90，3/3 更差）**、整步 **+3.08（3/3 更差）**。
   `ENTJOY_CLAIM_BLOCK` 更早同型（Build −0.69(10/10)、Melee +3.97、整步 +6.85(0/10)）。
   ⇒ **裁定：没有全局通解 —— Build 要"粗/切片"、Melee 要"细/动态"，两者最优相反。**
4. **已落地的合规形态 = 按 job 限定**：`ClaimPolicy`（调用点声明 `Spread`/`Adjacent`）+ F6 按 job 学习（`ENTJOY_CLAIM_ADAPT`，默认开）+ 批表第 4 字段。
5. **`kTargetTileUs` 150→6400 已进默认**（Melee −8~−11 ms，6/6 同号），**代价是 Build 4.0 → ~5.1 ms**（接受的权衡）。
6. ⚠ **两条被自己撤回的错误结论**（引以为戒）：
   - 用**合成分布**扫粒度会得到与真实输入**反号**的结论 ⇒ **粒度标定必须用真实输入**；
   - "给 Build 显式 `innerBatch`" 收益 ≈ 0（游戏内 Build **对粒度不敏感**：粗档 4.97 vs `FORCE_INNER_BATCH=64` 4.92，Δ−0.05）。
   - 方法论：**不得用"有没有打印"推断"有没有走到"**（当年用 grep 只看到 mem-bound 行，误判 `kTargetTileUs` 无效）。

**⚠ HEAD 上的器械已变**：`ENTJOY_CLAIM_SLICE` 与 `ENTJOY_TILE_STRIDE` 这两个全局 env **已随清理删除**；
但**切片机制本身保留**（F6 在用）。现在要选切片/相邻，只能走 **`ClaimPolicy` / 批表第 4 字段 / F6**。
`docs/public/Gates-and-Flags.md` 是当前门控的权威表。

---

## 5. 假设清单 + 判决实验（每条都能唯一判定）

| # | 假设 | 判决实验 | 若成立的样子 |
|---|---|---|---|
| **H1** | **每趟/每次派发的固定开销**：A 的 Build 是 **6 趟串行 schedule**，每趟都有 submit→barrier | ① Build 逐趟表（§3.1）看赤字集中在**哪一趟**；② `[M-15] _SPLIT=1` 量"只提交" vs "只等待"；③ 数两侧 pass 数 | 赤字落在**固定的每趟增量**上，与元素数无关 |
| **H2** | **worker/SMT 数不等**（A 侧 `workerThreads=15`/`physicalCores=8`，B 侧纯 8） | 扫 `ENTJOY_JOB_WORKERS`；`ENTJOY_PHYSCAP_SMALLJOB=0/1` | 赤字随 worker 数/physCap 变 |
| **H3** | **粒度/均衡**（Build 已测"不敏感"，**Integrate 没测过**） | `ENTJOY_FORCE_INNER_BATCH=<n>` × `ENTJOY_TILES_PER_WORKER` 于 Integrate | 赤字随粒度单调/非单调变化 |
| **H4** | **barrier 位置**：`MarkDeadJob` 与 `IntegrateJob` 之间有 `Complete()` | `M4_INTEGRATE=0` 关掉 B 的 Integrate 作负控；A 侧把两 job 合成一趟（需改宿主） | 赤字出现在"两段各自"而非"两段之间" |
| **H5** | **认领几何/共享计数器争用**（历史根因，Build 已证；**Integrate 未单独测**） | 批表第 4 字段给 Build/Integrate 的 key 声明 `s`(Spread) vs `a`(Adjacent)，同会话 A/A | Spread 让该段变快且**不同时**伤 Melee（因为只按 key 声明） |
| **H6** | **工作量不等价**（Unity 做得更少/走的是别的算法） | 两侧逐趟 + 工作量证明（B 侧 `[M4-PROOF-*]`、`M4-WORK`；A 侧 `[M-19] Σ` 自证 ≤ 段） | 某趟 B 侧的"工作单位数"少于 A |
| **H7** | **生成码差异**（转译 C++ vs Unity IL2CPP/Burst 的循环体） | 同算法在 `JobLibsBenchmark` / `JobSystemPerfBench` 里做孤立微基准（同 N、同趟） | 孤立基准里也有同向差异 |
| **H8** | **决定性分辨"派遣 vs 计算"** | **扫 N**（改 dump / 改单位数），看 A−B 赤字是**恒定**还是**随 N 线性** | 恒定 ⇒ H1/H4/H5；线性 ⇒ H3/H7 |

**建议的实验顺序**：`§3.1 逐趟表`（零成本）→ `H8 扫 N`（分辨派遣/计算）→ `H5 批表声明几何`（零重编，直接试通解）→ `H1 [M-15] 分相`。

---

## 6. 必守纪律与已知陷阱

1. **只认同会话配对**（逐对交替顺序、逐对同号）。**跨会话不可信**：本会话实测 B 自己的中位在两个会话间就漂了 **−3.4%**，比待测效应还大 ⇒ 2% 量级的效应**只能**同会话 A/A。
2. **A-vs-B 只认"最后一窗"（相位对齐，`warm = lastStart−61`）**。**不要用 `min-of-windows` 作 A/B 判据**：A 侧逐窗负载**爬升**（92→125 ms），A 最便宜的窗是更轻的负载，对 A 系统性有利。`min-of-windows` 只能用于 A/A。
3. **先证"两侧同工"**：不同工就不是同一测量（H6）。
4. **陈旧 DLL 会静默回退托管**：跑前必须看 stderr 的 `Loaded NativeDll: …\Debug\…` + `dll self-proof: … fallback=none` + 无 `ABI mismatch`。（本会话就踩过：部署 DLL 比源码旧一天，且是 ABI 2 ⇒ 会回退托管。）
5. **`ENTJOY_TILE_RUN` 已删**（2026-10-05，见 doc13 §5.19）⇒ 别再按老文档设它；`run-native-tests.ps1` 现在是**一趟**十套件。
6. **Unity 侧必须用 player（`W0Player.exe`）而不是 Editor**：Editor 的安全检查税 ≈ 2.0×，逐趟只能读"形状+总量"。
7. **`-ForceFine`（`ENTJOY_FORCE_INNER_BATCH=1`）与 `JobSystemTests` 的两个前提冲突**，已在用例内清零（doc13 §5.18）—— 但**用它做性能 A/B 时记得它同时绕过 JCC 与几何学习**，等于把 A 侧的自适应关掉，未必是公平臂。
8. 跑之前静默机器（会占 8 核）；`AUTOEXIT` 决定窗口数。

---

## 7. 现成脚本（`tools/gate-run/`，本地保留、不入库）

| 脚本 | 用途 |
|---|---|
| `ab-3arm-2curve.ps1` + `parse-3arm-2curve.ps1` | 本轮三轮对比（默认档 / Unity 粒度档 / Unity），含 loader gate、陈旧件删除、挂住丢样、逐窗输出 |
| `ab-tilerun-aa.ps1` | 同会话 A/A 模板（**把 arm 名换掉即可复用于本**：它是"两臂交替 + 逐 (对,窗) 配对 + 末窗汇总"的完整实现） |
| `ab-final-vs-unity-w8.ps1` | 两侧都 8 worker 的 A-vs-B 单段（Melee） |
| `ab-3arm-unitygran.ps1` / `ab-unitygran.ps1` | 粒度臂的 A 侧对照 |
| `run-native-tests.ps1` | 原生十套件（删除 `TILE_RUN` 后为单趟） |

**复现本轮基线**（PowerShell，注意 `pwsh` 不在 PATH ⇒ 用 `powershell.exe -File`）：

```powershell
cd E:\GODOT\Project\EntJoy
powershell -NoProfile -ExecutionPolicy Bypass -File tools\gate-run\ab-3arm-2curve.ps1 -Reps 6 -Curve off
powershell -NoProfile -ExecutionPolicy Bypass -File tools\gate-run\parse-3arm-2curve.ps1
```

产物：`tools/gate-run/3arm-2curve/off/{rows.csv,mech.csv,A-*.log,B-r*.csv}`（`rows.csv` 已带每窗五段 + B 侧五段，可直接重算任何汇总）。

---

## 8. 本轮数据的原始出处

- 三轮对比：`tools/gate-run/3arm-2curve/off/`（6 对）、`…/on/`（`TILE_RUN` 敏感性，已废弃的门控）
- 记录与裁定：`docs/gridsearch/13-门控清单-缺陷清单与清理计划.md` §5.16–§5.19
- 历史根因与逐趟：`docs/gridsearch/07` §7ar（含逐趟表、三条消融证据、切片夹具数据）
- 历史裁定摘要：`docs/handoff-ABC-and-open-items.md` 顶部第十二轮块（(1)–(12)）
