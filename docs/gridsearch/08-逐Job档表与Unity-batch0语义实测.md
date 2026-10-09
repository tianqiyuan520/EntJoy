# 08 — 「按 job 给粒度」机制落地 + **Unity `innerloopBatchCount=0` 语义实测** + 确定档同会话 A/B（2026-10-01）

> ⚠ **2026-10-05 追记（二）**：本表"**其余 15 处 = 1**"这一行所依赖的 `0 ⇒ 1` 语义**仍是 Editor 结论**（§3 自陈的
> "同版本共享实现"假设未被 player 复核）。本轮用 Unity **自带的空体器械** `BattleBenchDispatchFloor`（`M4_DISP=1`，player）
> 量出了它的**代价上界**：`1 job × 1,000,000 项` 空体，`batch=1`（1,000,000 批）**0.382 ms**、
> `batch=64`（15,625 批）**0.052 ms**、`batch=1024` **0.0359 ms**；单个空 `IJob` 往返 **0.95 µs**。
> ⇒ **即使 `0 ⇒ 1` 成立，每 1M 项也只值 ≤0.38 ms**，在 Melee/Flow 那种 20–100 ms 的段里 <1%，
> **不改变本表任何跨栈结论的量级**。数据与逐调用点表见 [doc15](15-Build与Integrate逐趟定位-器械缺陷与对齐档实测.md) §4.1–§4.2。

> ⚠ **2026-10-05 追记**：F5（`ENTJOY_TILE_RUN`）**已整体删除**；`ENTJOY_CLAIM_SLICE` / `ENTJOY_TILE_STRIDE` 也已删除
> （切片机制保留，F6 在用）。**凡把 `ENTJOY_TILE_RUN` 写进"对齐档/镜像档"臂配置的地方（§14、§16.34、`mir1_run_f` 等），
> 该臂现在无法照原样复刻** —— 请用 `ClaimPolicy` / 批表第 4 字段 / F6 表达认领几何。当前门控权威表：`docs/public/Gates-and-Flags.md`。

> 本文是 [`07b-同粒度实测数据与派生表.md`](07b-同粒度实测数据与派生表.md) **§9**（逐 job 镜像 Unity 的 batchSize）的**续篇**：
> 07b §9 当时**只能逐段组装**（EntJoy 只有全局单值旋钮，且 Unity 的 `0` 语义"官方未载"），并把它列为
> 第 2 条未证前提。本文把两件事都做掉了：**给 EntJoy 加了逐 job 档表**、**把 Unity 的 `0` 量出来 = 1**，
> 于是"对齐 batchSize 后比较"从**组装 + 上界**变成**真跑 + 定值**。
>
> **引用纪律**：本文所有跨栈数字都是**同会话配对**；A-vs-A 与 A-vs-B 是**两个不同会话**（见 §5/§6 表头）。
> 原始档在 `tools/gate-run/jobbatchtbl/`（**`tools/` 在 `.gitignore` 内**，不进 git ⇒ 关键读数已全部抄进本文）。

## 0. 结论摘要（可引用）

| 问题 | 答案 | 依据 |
|---|---|---|
| **Unity 的 `innerloopBatchCount = 0` 是什么？** | **1**（= 每个工作项 1 次迭代，即"夹到 1"） | §3：`T(0)=9.092 ≈ T(1)=9.182`，且 `T(0)` 落在 `T(1)` 与 `T(2)=7.014` **之间**；模型反解 `k_eff=1.034` |
| Unity 的逐调用点**确定档** | **4 处显式 64**（Build·Count/Place、Flow·PresenceClear、Integrate）+ **其余 15 处 = 1** | §4（本地逐行核对 `BattleBenchM1Flat.cs` / `M3.cs` / `M4Entry.cs`） |
| EntJoy 能表达这张表吗 | **能**：`ENTJOY_JOB_BATCH_TABLE`（逐 job、默认关、表空时默认档逐位不变） | §1 |
| **确定档下 EntJoy vs Unity（同会话 6 对）** | **`B/A = 0.650` ⇒ EntJoy 慢 54%（6/6）** | §6 |
| **EntJoy 默认档（JCC 自适应）vs Unity（同会话 6 对）** | 仅作**参照**（非本次比较口径）：`B/A = 1.066` | §6 |
| 粒度策略这一项值多少（A-vs-A、同 DLL 只切 env） | **整步 +89.38 ms（6/6）**；Melee +46.5 / Flow +29.9 / MarkDead +6.4 | §5/§6 |
| 其中"JCC 自适应/学习"值多少 | **未分离**（mir 臂把 JCC 决策与学习一起旁路）⇒ 需第三臂 C（钉在 JCC 稳态值） | §7.4 |
| 框架"每 job 机制"的贡献 | **≈ 0**（每-job 地板 1.22 µs；空体同粒度 3.55 vs 3.76 µs 打平） | §7.2 |
| **不依赖 `0` 语义的硬赤字**（两侧同档） | **Build 1.81×**、**Integrate 1.54×**（都显式 64；与粒度无关） | §6 |
| 落后量的性质 | **每工作项认领代价**：空体实测 **4.79 ns/批**；1e6 工作项级才暴露 | §3.4/§7.2 |
| **⭐ 对齐档残余（~13%）到底是什么？** | **每-tile 一次内核调用**（间接调用 ×2 + 适配器逐字段拆 context（Melee 94 个）+ ~58 参数封送）：实测 **Melee 11.3–13.4 ns/次、MarkDead 3.2 ns/次** | §14.2–§14.4 |
| **⭐ 修完之后还差多少** | 对齐档 `B/A`：**0.836（F1）→ 0.946（+F5）→ ≈0.986（+F2+F4）**；残余 ~3% 全在**体**（Melee +4.6 ms、Build/Integrate +3.1 ms） | §14.6/§14.8 |

## 1. 新能力：`ENTJOY_JOB_BATCH_TABLE`（逐 job 内批档表，默认关）

**用途**：逐 job 镜像 Unity 的 `innerloopBatchCount` 表。**`ENTJOY_FORCE_INNER_BATCH` 是全局单值，表达不了这张表**
（这正是 07b §9 只能"逐段组装"的原因）。

**语义（4 条）**
1. 只在 **auto**（`batchSize<=0`）时生效；**显式 `batchSize>0` 完全不受影响**（调用方意图优先）。
2. **命中 ⇒ 等价于调用方显式传该内批**：`forced = tableBatch` ⇒ `cs = forced`（`ResolveChunkSize` **根本不调用**）
   且 `funcHash = 0`（退役路径不学习、`jccFine=false`）。
3. **未命中 ⇒ 回落 `ENTJOY_FORCE_INNER_BATCH`**（若设）⇒ 再回落 JCC 自适应。
4. **表为空 + dump 关 ⇒ 默认档逐位不变**（`needKey / forced / funcHash` 与改动前同式）。

**"命中即旁路 JCC"的三条链路（可核）**

| 环节 | 代码 | 命中时 |
|---|---|---|
| 分块决策 | `JobSystem_Scheduler.cpp:831-839`（`cs = forced>0 ? forced : ResolveChunkSize(...)`） | `ResolveChunkSize` 不被调用 ⇒ `target=6400`/MEM-BOUND 分类/formula 分支全不参与 |
| 成本学习 | `JobSystem_Tiles.cpp:1524`（`batch->funcHash != 0 && totalElements > 0`） | `funcHash=0` ⇒ 不写 `UpdatePerElemCost/UpdatePerTileCost` |
| 自旋自适应 | `JobSystem_Tiles.cpp:946`（`g_completeSpinAdaptEnabled && batch->funcHash != 0`） | 读不到（该开关本就默认关） |
| 决策入口守卫 | `JobSystem_State.cpp:1161`（`if (funcHash != 0 && g_jobCostCacheEnabled)`） | 即使被调用也不进 JCC 分支 |

⇒ **本负载 15 个内核全部命中 ⇒ 游戏并行-for 流量上一次 JCC 决策都不剩**（= `ENTJOY_FORCE_INNER_BATCH` 的"逐 job 版"，
与 07b 的 `f1`/`f64` 两臂**同一等价类**；也与 Unity 对等 —— Unity 每个调用点都是写死的常数，引擎侧没有任何按 job 自适应）。
⚠ 但 **JCC 自适应本身值多少，本实验没有分离**（见 §7.4）。

**键：内核在"其所属模块"内的 RVA（不是 `HashFuncPtr`）**

⚠⚠ **踩过的坑（重要）**：`funcHash`（`HashFuncPtr` 对**函数指针值**做 FNV-1a）**在同一构建内跨进程不稳定** ——
它受 **ASLR** 影响。实测 6 次同一 DLL、同一 env 的 dump 运行：**5 次给同一组 15 个键，1 次整组 15 个键全变**
（`168bb32a…` ↔ `15b7eadf…`）。⇒ 用它做表会**静默半失效**（表在、但不命中），本轮的识别实验就先被它骗过一次。
改用 **模块内 RVA**（`JobFuncKey()`：`GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS)` + 缓存）后 **6/6 稳定**。

**制表 recipe**
```text
1) ENTJOY_JOB_BATCH_TABLE_DUMP=1 跑一轮  → stdout 出现（一轮一次，首见去重）
   [JOBBATCHTBL] key=<8hex RVA> N=<len> tiles=<rc> applied=<0|表值>
2) dumpbin /exports NativeTranspiled.dll | findstr _Execute_Adapter$
   → RVA → job 名（导出名即 SharpNative_Job_<命名空间>_<Job 类型>_Execute_Adapter）
3) ENTJOY_JOB_BATCH_TABLE="<key>:<n>[,<key>:<n>...]"（十六进制键、逗号分隔）
```
⚠ **表与构建绑定**：任何改变生成代码布局的重构都会改 RVA，必须**重新制表**（脚本里已注明）。

**代码位置**：`src/NativeDll/JobSystem_Scheduler.cpp:818-841`（挂钩）、`src/NativeDll/JobSystem.cpp`（env 解析 /
`LookupJobBatch` / `JobFuncKey` / `NoteJobBatchTableHash`）、`src/NativeDll/JobSystemInternal.h:211-233`（声明与语义）。

## 2. 键 → job 名（本构建，15 个内核；由 `dumpbin /exports` 对齐，非推断）

| key (RVA) | N | EntJoy job | Unity 对该 job 的档 |
|---|---|---|---|
| `000051f0` | 1,000,000 | **FlowPresenceJob** | **64** |
| `00001950` | 1,000,000 | **CountCellsJob**（Build·Count） | **64** |
| `00010910` | 1,000,000 | **PlaceCellsJob**（Build·Place） | **64** |
| `00005e60` | 1,000,000 | **IntegrateJob** | **64** |
| `0000c310` | 1,000,000 | MeleeSimJob | 0 → **1** |
| `000069f0` | 1,000,000 | MarkDeadJob | 0 → **1** |
| `00011790` | 1,000,000 | SpawnJob | 0 → **1** |
| `00001720` | 1,000,000 | ClearAllJob | 0 → **1** |
| `00003960` | 351,232 | FlowClearJob | 0 → **1** |
| `00005480` | 351,232 | FlowSeedJob | 0 → **1** |
| `00005010` | 351,232 | FlowGradJob | 0 → **1** |
| `00010da0` | 64 | PrefixSumPartialJob | 0 → **1** |
| `00010b30` | 64 | PrefixSumFinalJob | 0 → **1** |
| `00005340` | 18,836 | FlowSeedInitJob | 0 → **1** |
| `00003690` | 18,836 | FlowBfsWaveJobDual | 0 → **1** |

（`AliveBitJob` 有导出但本配置恒不跑 —— 与 07 §13.8 注一致；`ZeroCellsJob` 走 `IJob` 路径不经过本表。）

**独立交叉验证**：① 首见顺序与 N 在**指针哈希版 dump** 与 **RVA 版 dump** 两次**逐位一致**；
② 单键拨到 `1` 的"逐段归属"实验（n=1，45 s 稳态）：`000051f0`→Flow +7.0、`00001950`→Build +5.0、
`00010910`→Build +7.4、`0000c310`→**Melee +40.6**、`00005e60`→**Integrate +22.5**、`00011790`(Spawn)/`00001720`(ClearAll)
→ 只在残差/整步上动（两者本就不在 `partsMs` 五段内）—— 与导出名表**完全一致**。

## 3. Unity 的 `0` = **1**（实测）

### 3.1 器械与方法（不预设 `0` 的含义）
`Assets/Scripts/BattleBench/BatchZeroProbe.cs`（Unity **6000.3.2f1**，= 编出 `W0Player` 的同一版本），
`Unity.exe -batchmode -nographics -quit -projectPath <工程> -executeMethod BatchZeroProbe.Run -logFile <abs>`。
同一个 `IJobParallelFor`（N=10⁶、每元素 1 次 byte 写、`[BurstCompile]` 未加 ⇒ 每元素代价恒定），
对 `k = 0,1,2,3,4,6,8,12,16,24,32,48,64,128` **各 9 轮**、轮内轮转顺序消漂移；warmup 另含一次 `k=0` 以确认不抛异常。

**模型**：`T(k) = a + b·(N/k) + c·N`（每-job 地板 + 每批认领 + 每元素体）。
对参考档 `REF=64` 取差 ⇒ **`c·N` 项被消掉**，只剩 `D(k) = b·N·(1/k − 1/64)`；
用**显式 k**（批大小按构造已知）拟合 `b·N`，再用 `D(0)` 反解 `0` 的有效批大小 `k_eff`。

### 3.2 原始读数（n=9/档，workers=15，maxJobThreads=128，N=10⁶）

| k | ms 中位 | ms 最小 | | k | ms 中位 | ms 最小 |
|---|---|---|---|---|---|---|
| **0** | **9.092** | **8.521** | | 8 | 4.666 | 4.523 |
| **1** | **9.182** | **8.540** | | 12 | 4.993 | 4.545 |
| 2 | 7.014 | 6.122 | | 16 | 4.902 | 4.506 |
| 3 | 6.351 | 5.572 | | 24 | 4.631 | 4.077 |
| 4 | 5.302 | 4.972 | | 32 | 4.559 | 4.272 |
| 6 | 5.381 | 4.739 | | 48 | 4.556 | 4.350 |
| | | | | 64 | 4.534 | 4.129 |
| | | | | 128 | 4.773 | 3.836 |

### 3.3 两条**互相独立**的判据 ⇒ 都指向 1

1. **无模型判据（最硬）**：`T(k)` 对 k 单调。`T(0)=9.092` 落在 `T(1)=9.182` 与 `T(2)=7.014` **之间**；
   k 是正整数 ⇒ **`k_eff` 只能是 1**（若 `0` 是 2 或更大，`T(0)` 必须 ≤ 7.01 ms）。`T(0)/T(1) = 0.990`（同值；最小档同结论 8.521 vs 8.540）。
2. **模型反解**：`k∈{1,2,3,4,6,8}` 最小二乘 ⇒ `b·N = 4.7884 ms`（⇒ 空体 **4.79 ns/批**）；
   代入 `D(0)=4.558 ms` ⇒ `1/k_eff = 1/64 + D(0)/(b·N) = 0.96751` ⇒ **`k_eff = 1.034`**。

**模型自检（显式档残差，ms）**：k=1 −0.07、2 +0.16、3 +0.30、4 −0.35、6 +0.12、8 −0.39、16 +0.14、32 −0.05
⇒ 在 4.5~9.2 ms 的跨度上残差 ≤0.4 ms，模型成立。

### 3.4 与 player 侧读数的**一致性**（不是矛盾）
B 侧自注实测（同文件 `BattleBenchM4Entry.cs:460`）：Integrate `0→4.32` vs `64→2.88 ms`、Build `6.12` vs `3.15 ms`。
若 `0≡1`，则额外 `984,375` 次认领 ⇒ **Integrate 1.46 ns/批、Build 3.02 ns/批**。
空体探针给 4.79 ns/批 ⇒ **同量级、重体下更低**（认领延迟被真实计算掩盖）—— 与 `0≡1` 自洽。
⚠ 但**两者不同宿主，绝对值不可混用**（器械纪律同 07 §7(l6)(b) 的注）。

### 3.5 残留假设（必须一起带）
语义在**引擎层**（同一版本号的 Editor 与 player 共用 job system 实现），但我是在 **Editor** 里量的。
要 100% 闭环：① 把探针编进 `W0Player` 再量一次；或 ② 把 Unity 侧 15 处 `0` 改成**显式 1** 重编
（数值零变化，但从此配置自证、流程里不再有"未载语义"这一项）。

## 4. Unity 的确定档表（`0` 已经不存在）

| Unity 调用点（本地核对） | 原值 | 确定值 |
|---|---|---|
| `BattleBenchM1Flat.cs:239` Build·Count / `:252` Build·Place | 64 | **64** |
| `BattleBenchM3.cs:662` Flow·PresenceClear | 64 | **64** |
| `BattleBenchM4Entry.cs:460` Integrate | 64 | **64** |
| `BattleBenchM1Flat.cs:242/249` PrefixSum ×2 | 0 | **1** |
| `BattleBenchM3.cs:685/686/701/704/726/744/751/777` Flow 其余 8 处 | 0 | **1** |
| `BattleBenchM4Entry.cs:330/352` ClearAll / Spawn | 0 | **1** |
| `BattleBenchM4Entry.cs:423/434/475` Melee / MarkDead / AliveBit | 0 | **1** |

⇒ EntJoy 的镜像档表 = **`{FlowPresenceJob, CountCellsJob, PlaceCellsJob, IntegrateJob} = 64`，其余 11 个内核 = 1**（本文称 `mir1`）。

## 5. 实测 A：A-vs-A（EntJoy 默认 vs 镜像档，同 DLL 只切 env）

**协议**：`tools/gate-run/ab-jobbatchtable.ps1`（`-Reps 6 -KList 1,8`，臂序逐 rep 轮转，8 worker，45 s/次、尾 5 s 窗）。
**自校验**：12 个镜像臂全部 `applied=15, missing=0`（15 个键**逐个**确认被表接管）。

**各臂中位（ms）**

| 臂 | 整步 | Build | Flow | Melee | MarkDead | Integrate |
|---|---|---|---|---|---|---|
| **def**（`batchSize=0` → JCC） | **156.37** | 5.10 | 30.81 | 116.97 | 0.54 | 2.93 |
| **mir1**（对齐 Unity 确定档） | **245.80** | 4.99 | 59.19 | 164.05 | 6.91 | 3.75 |
| mir8（把"0 档"当 8 的备用臂） | 186.97 | 4.72 | 34.29 | 141.42 | 1.55 | 4.07 |

**逐对配对 Δ（镜像 − def，正 = 镜像更慢）**

| 段 | **mir1** | 同号 | **mir8** | 同号 | 赤字占比(mir1) |
|---|---|---|---|---|---|
| 整步 | **+89.82** | **6/6** | **+30.20** | **6/6** | — |
| Melee | +47.56 | 6/6 | +24.45 | 6/6 | **53.0%** |
| Flow | +28.29 | 6/6 | +3.40 | 6/6 | 31.5% |
| MarkDead | +6.33 | 6/6 | +1.02 | 6/6 | 7.0% |
| Integrate | +0.82 | 6/6 | +1.13 | 6/6 | 0.9% |
| **Build** | **−0.03** | 3/6（噪声） | **−0.37** | 0/6（更快） | ≈0 |

**口径交叉验证**（与 07b 的全局臂同协议）：
`mir1` 的 Build 4.99 / Integrate 3.75（表给 64）对应全局 `f1` 档的 **18.62 / 23.39**；
Melee 164.05 / MarkDead 6.91 与 `f1` 档 **166.72 / 6.91** 吻合；Flow 59.19 < `f1` 的 65.66（因 PresenceClear 被表保持 64）
⇒ **表只改了该改的 job**。`mir1` 整步 245.80 与 07b §9 的**逐段组装值 248.35** 相差 **−1.0%** ⇒ 组装法可信、且现已由真跑取代。

## 6. 实测 B：确定档**同会话 A/B**（`tools/gate-run/ab-jobbatchtable-ab.ps1`，n=6，每个 A 臂各配一个相位对齐的 B）

**两臂 × 逐段（A = EntJoy，B = Unity `W0Player`）**
> ⚠ **口径（2026-10-01 用户明确）：本次比较只用"与 Unity 相同 batchSize"的 `mir1` 臂。**
> `def` 臂仅作**参照**（它把粒度自由度也算进差里），**不作为 EntJoy-vs-Unity 的结论口径**。

| 段 | **def** A / B | `B/A` | 同号 | **mir1** A / B | `B/A` | 同号 |
|---|---|---|---|---|---|---|
| **整步** | 155.93 / 166.27 | **1.066（EntJoy 快 6.2%）** | 1/6 慢 | 248.30 / 161.87 | **0.650（EntJoy 慢 54%）** | **6/6 慢** |
| Build（两侧 64） | 5.05 / 2.82 | 0.567（慢 1.76×） | 6/6 | 5.09 / 2.82 | **0.554（慢 1.81×）** | 6/6 |
| Flow | 30.75 / 32.62 | 1.061（快 6%） | 1/6 | 60.38 / 32.66 | 0.541（慢 1.85×） | 6/6 |
| Melee（两侧 1） | 116.59 / 126.35 | 1.084（快 7.8%） | 1/6 | 164.75 / 121.87 | 0.740（慢 1.35×） | 6/6 |
| MarkDead（两侧 1） | 0.56 / 0.88 | 1.568（快 36%） | 0/6 | 6.92 / 0.89 | **0.128（慢 7.8×）** | 6/6 |
| Integrate（两侧 64） | 2.95 / 2.58 | 0.849（慢 1.18×） | 6/6 | 4.00 / 2.60 | **0.649（慢 1.54×）** | 6/6 |

**A 侧配对 Δ（mir1 − def，同 DLL 只切 env，n=6）**：整步 **+89.38（6/6）**；Melee **+46.48**、Flow **+29.90**、
MarkDead **+6.41**、Integrate +0.98、Build +0.14。⇒ 与 §5 的 A-only 会话 `+89.82` 相差 **0.5%**（两会话自洽）。

**`def` 的自校验**：`B/A = 1.066` 与 07 §7ar(m) 同协议的 **1.0449** / §7ar(i) 的 1.0535 同量级同向 ⇒ 器械/协议没漂。

## 7. 归因与裁定

1. **胜负完全由"粒度策略"决定**：同一个 EntJoy、同一个 DLL、同一台机器，只切粒度档 ⇒
   **从"比 Unity 慢 54%"翻成"比 Unity 快 6.2%"**。这 89.4 ms 就是**框架粒度策略（JCC）的定价**。
2. **"每 job 调度机制"贡献 ≈ 0**：每-job 地板 **1.22 µs**（07 §7(l6)(b)）；空体在**自身默认粒度**下
   **3.55 µs ≈ Unity 3.76 µs（打平）**。⇒ 07 §7ar(m) 那个 **1.9×** 是"空 job 把分母压到 0"的形状产物，**不是** 1.9× 的框架差距。
3. **落后量的性质 = 每工作项认领代价**（空体 **4.79 ns/批**，07b 的 Melee 斜率 **28.0 ns/tile**，空体器械 **27 ns/tile**）：
   1e6 工作项级才暴露 —— 而 JCC 把工作项数从 15,625 压到 ~19–60，等于把这笔钱收回去。
4. **未分离项（诚实标注）**：`mir1` 臂把 **JCC 决策 + 成本学习一起旁路**了（§1）。所以要回答"JCC 值多少"，
   `mir1 − def` 里同时含 ① **粒度值**（JCC 稳态 ~512 tiles ↔ Unity 的 1/64）与 ② **自适应/学习本身**。
   **分离方案（第三臂 C，未跑）**：把表钉在 **JCC 稳态会选的值**（本会话 dump：1e6 job ≈ **1953**、351232 ≈ **686**、
   18836 ≈ **37**、64 → **16** 元素/批），JCC 仍旁路 ⇒ `C − mir1` = 纯"粒度值"，`def − C` = "自适应/学习"。
   只切 env，无需重编。
5. **不依赖 `0` 语义的硬赤字**（两侧同档、与粒度无关）：**Build 1.81×**、**Integrate 1.54×**。
   其中 Build 在 **64× 粒度变化下 Δ=0**（§5 表）⇒ 独立复证"Build 的成本由**原子 RMW 争用 + 散列写**支配"（07 §7ar(k)/§7(l7)）。
6. **对 07b §9 结论 1 的更新**：`1.38×（Melee）~ 8.1×（MarkDead）` 这些**不再是上界**——Unity 的 `0` 已量出 = 1，
   而那三行本来就是"按 `0≡1` 取镜像档"算的。**改用本文 §6 的同会话配对值**：
   Melee **1.35×**、Flow **1.85×**、MarkDead **7.8×**、Build **1.81×**、Integrate **1.54×**、整步 **1.54×**（`B/A=0.650`）。
   （差异来源：① 同会话配对 vs 跨会话；② `mir1` 里 PresenceClear 保持 64 使 Flow 从 2.04× 降到 1.85×。）

## 8. 口径与已知缺口

1. **Editor vs player**（§3.5）：`0` 的语义实测在 Editor，残留"同版本共享实现"这一条假设。
2. **`b = 4.79 ns/批` 是空体 + 15 worker 的**上界**；player 重体下由 0→64 反推得 1.46–3.02 ns/批。**两者不可混用**。
3. **RVA 键与构建绑定**（§1 尾）：换构建必须重新制表，否则表静默不命中（会静默退回 JCC/全局档）。
4. **MarkDead 的比值最夸张（7.8× / 128×`B/A`）但绝对值最小**（+6.4 ms）：排优先级要用**绝对差**。
5. **A-vs-A 与 A-vs-B 是两会话**（§5 vs §6），各自表头已标注；跨会话只可引区间。
6. **`mir8` 臂已作废**（它只是"`0` 含义未知"时的对冲）——`0≡1` 量出后，确定档就是 `mir1`。
7. 本轮的 A 侧读数比 07 的会话**快 2~4%**（`def` 156.37 vs 162.52）⇒ 同机漂移，跨会话勿直接相减。

## 9. 复现

```powershell
# 0) 制表（一圈）：dump 出 key/N/tiles
$env:ENTJOY_JOB_BATCH_TABLE_DUMP='1'; Remove-Item Env:\ENTJOY_JOB_BATCH_TABLE -EA 0
#    （A 侧命令同 07b §1；AUTOEXIT=45 / STATS_WINDOW=5 / ENTJOY_JOB_WORKERS=8）
#    再看 RVA→名：dumpbin /exports NativeTranspiled.dll | findstr _Execute_Adapter$

# 1) A-vs-A：默认 vs 镜像档（含 applied 自校验）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\gate-run\ab-jobbatchtable.ps1 `
  -Reps 6 -KList 1,8 -Mode both -Dump 1
powershell -NoProfile -ExecutionPolicy Bypass -File tools\gate-run\parse-jobbatchtable.ps1

# 2) 确定档同会话 A/B（A(def/mir1) 各自配相位对齐的 B）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\gate-run\ab-jobbatchtable-ab.ps1 -Reps 6
powershell -NoProfile -ExecutionPolicy Bypass -File tools\gate-run\parse-jobbatchtable-ab.ps1

# 3) Unity `0` 语义探针（Editor，无需 IL2CPP）
& "C:\Program Files\Unity\Hub\Editor\6000.3.2f1\Editor\Unity.exe" -batchmode -nographics -quit `
  -projectPath E:\UnityProject\TestProject\TestProject -executeMethod BatchZeroProbe.Run -logFile <abs>
```
**原始档**：`tools/gate-run/jobbatchtbl/{pair-run.txt, ab-run.txt, unity-bz.log}`（+ 同名目录下 `.log`/`.stdout.txt`）。

⚠ **器械坑（本轮踩到的，供下次省时间）**
1. `tools/gate-run/*.ps1` 必须**纯 ASCII**（07b §1 已记）；非 ASCII 注释 + PS 5.1 按 ANSI 解码会吞掉整个 `param` 块。
2. **PowerShell 变量名大小写不敏感**：`$K` 与 `$k`、`$A` 与 `$a` **是同一个变量** —— 本轮两次踩到
   （`foreach ($k in $kill)` 被 `[int]$K` 参数约束 ⇒ 转型报错；`foreach ($a in $arms){ $A = ... }` 把循环变量覆盖成数组 ⇒ 汇总静默为空）。
3. **双引号里的冒号会被当驱动器限定符**：`"$h:1"` 展开成 `"1"`（表解析出 0 项、静默空跑）⇒ 用 `($h + ':1')`。
4. **`powershell -File` 传数组会丢**：`-Ks 1,8` 被文化解析成单个 `18` ⇒ 参数改用逗号分隔字符串再 `-split ','`。
5. 制表 dump 只在 **表为空时** 也要能打 ⇒ `needKey` 必须把 dump 旗标算进去（本轮改过一次才修好）。
6. `AUTOEXIT=8` 太短 ⇒ 连一条 `[M-1] 步均` 都不打（**非稳态窗口会骗人**）；识别实验要用 45 s 稳态协议。
7. **内层 `-match` 会覆盖 `$Matches`**：`if ($nm -match '...$') { $map[$Matches[1]] = ... }` 里 `$Matches[1]` 已被内层替换为空
   ⇒ **先取出** `$rva=$Matches[1]; $nm=$Matches[2]`，再用 `$nm.EndsWith(...)` 判定。
8. **构建期开关的 A/B：三条自证缺一不可**（本轮又踩一次）：
   ① 生成器**不覆盖已有生成物** ⇒ 必须删 `SharpNative_*`；② MSBuild 节点**粘环境变量** ⇒ `dotnet build-server shutdown`
   或 `-p:UseSharedCompilation=false`；③ **改了生成代码就会移动 RVA ⇒ 表键失效**（`ENTJOY_JOB_BATCH_TABLE` 必须**按每次重建重推**），
   且**每轮必须校验 `applied` 等于表值**（命中数 = 15/15）；只统计 dump 行数会给出"表在但没命中"的**假阴性/假阳性**。
   ⇒ 本轮 F3 第一次跑出的"镜像臂 ≈155/115"就是这个假象（详见 §12.7）。
9. **`[string]` 参数会把同名变量"钉成字符串"**（本轮第三次踩大小写不敏感，且**最隐晦的一次**）：
   `param([string]$Arms = 'a,b,c,d')` 之后写 `$arms = @($Arms -split ',')` —— `$Arms`/`$arms` **是同一个变量**，
   赋值时数组被**强制转回 String**（`$OFS`=' ' 连接）⇒ `$arms.Count` 变成 **1**，而 `$arms[0]` 是**字符串首字符**
   （实测臂名变成 `m`，跑了 3 轮"单臂"会话、还各自配了 Unity B：全是废数据，浪费 ~20 min）。
   **规避**：局部变量换名（`$armList`）+ 臂清单**硬编码在脚本体内**，不要用逗号分隔字符串参数。
10. **`ENTJOY_CLAIM_SPAN` 有 4096 上限**（`JobSystem.cpp`）：`SPAN=8192/65536` 与 `4096` 等价
   ⇒ 想用"更长认领跨度"做实验，必须**先放宽这个 clamp**（本轮器械侧就踩到：8192 与 65536 读数几乎一样，4.78 vs 4.70 ns/tile）。
11. **两个学习器不能共用同一张"槽位哈希表"**（F6 第一版的实际 bug）：JCC 的每-job 学习器用
   `JobCostCache::slotHash[slot]` 做"槽还是不是同一个 job"的校验；我给"认领几何学习器"复用了同一张表，
   但用的键不同（JCC 用 `HashFuncPtr` 的 FNV 哈希、F6 用内核 RVA）⇒ 两者**互相把对方的 `slotHash` 覆盖** ⇒
   各自不断"重学"（F6 永远停在探索期、50% 的派发走切片）⇒ 默认档 **Melee +7 ms、整步 +6.8（4 轮中 3 轮）**。
   **规避**：每个学习器各自一张校验表（F6 v2 改成"只读 JCC 的 perElem、不写入"⇒ 结构上不可能再撞）。

## 10. 与 07b §9 的关系（一句话）

07b §9 = **逐段组装 + `0` 语义未证**；本文 = **机制落地 + `0` 量出为 1 + 真跑的同会话 A/B**。
两版的**结构结论一致**（"对齐 batchSize 后 EntJoy 落后，代价在每工作项"），
但**具体数字以本文 §6 为准**（同会话配对），`mir8` 臂作废。

## 11. 深入分析：对齐档下"具体差在哪" + JobSystem 的 per-item 缺陷（2026-10-01 追加）

### 11.1 差在哪（按段，取自 §6 的 mir1 vs B）

| 段 | Δ(EntJoy−Unity) ms | 占整步赤字 | **该段在对齐档的 item 数/步** |
|---|---|---|---|
| **Melee** | **+42.9** | **50%** | 1.0e6（1 次派发） |
| **Flow** | **+27.7** | **32%** | ~1.3e6（presence 1.0e6 + 4×351,232 + 波前 ~300 次派发） |
| MarkDead | +6.0 | 7% | 1.0e6 |
| Build | +2.3 | 2.7% | **仅 31,250**（Count/Place 各 15,625，两侧都是 64） |
| Integrate | +1.4 | 1.6% | **仅 15,625**（两侧都是 64） |
| 残差/未计时（Spawn/ClearAll/排序） | +6.1 | 7% | — |
| **整步** | **+86.4** | 100% | **≈4.1e6 items/步** |

（item 数来源：**实测**。`ENTJOY_JCC_VERBOSE=1` 下每次派发打一行 `[JCC] R length=`，按 length 分组并除以步数
（`length=1000000` 恰好 **5.00 次/步**，故步数由它精确定出）：**总元素/步 = 8.08e6**；
对齐档把其中 4 个 job（presence/Count/Place/Integrate，各 1e6 元素）降到 64 元素/批 ⇒ items ≈ 8.08e6 − 4×(1e6−15,625) ≈ **4.1e6**。）

⇒ 赤字集中在 **item 数最多的两段**；而 **Build/Integrate 的 item 数只有 1.5万~3万**（比 Melee 少 30–60×），
它们的 +2.3 / +1.4 ms **不可能**由"每-item 税"解释 ⇒ **两处不同的地方**（§11.4）。
**同档附近的斜率**（只把某个 job 从 1 放到 8、其余不动，即 §5 的 `mir1` vs `mir8` 中位差 ÷ item 数差）
给出每-item 边际：**Melee 25.9 ns、MarkDead 6.1 ns**（用在 §11.2(c) 与 §11.3）。

### 11.2 JobSystem 的每工作项成本：**≈7.3 ns/item，且 ~70% 不并行**（实测）

**器械**：`tools/SchedTileBench`（同源转译 C++ 空体，`BENCH_SWEEP`、`BENCH_JOB=empty|work`），N=10⁶、2 job/帧。
（⚠ 跨宿主绝对值不必与游戏侧混用；下表**同器械内自洽**。）

**(a) 空体阶梯（W=8，N=10⁶）——每-item 成本在 2e3~1e6 tiles 上近乎常数**

| batch | tiles/job | ns/job（中位） | **ns/tile** | 相邻差分 ns/tile |
|---|---|---|---|---|
| 0（auto） | 32 | 6,450 | 202 | — |
| 512 | 1,954 | 20,550 | 10.5 | 7.3 |
| 64 | 15,625 | 147,150 | 9.4 | 9.3 |
| 8 | 125,000 | 827,350 | 6.6 | 6.2 |
| 4 | 250,000 | 1,706,200 | 6.8 | 7.0 |
| **1** | **1,000,000** | **7,079,000** | **7.1** | **7.16** |

⇒ **边际 ≈7 ns/item**（旧文档里"27 ns/tile"是 len=7936 + 1000 job/帧 那个形状的均值，含每-job 地板的摊薄，
不可当边际值用）。

**(b) worker 扫描（N=10⁶，batch=1）——不是争用，是不并行**

| W | ns/job | ns/tile |
|---|---|---|
| 1 | 9,987,350 | 9.99 |
| 4 | 8,755,775 | 8.76 |
| 8 | 7,749,250 / 7,308,275 | 7.75 / 7.31 |

⇒ **8 个 worker 只买到 1.37×**（Amdahl ⇒ **可并行份额仅 ~31%，~70% 串行**）；且**每-tile 成本不随 W 增长**
（不是认领原子争用）。**拟合分解**：`~6 ns/tile 串行（提交线程物化 tile 数组）+ ~1.5 ns/tile 并行`，
与两个端点都吻合（W=1: 6+2=8≈10；W=8: 6+0.25≈6.3≈7.3）。
⚠ **"物化"这一步是从代码位置（`JobSystem_Scheduler.cpp:874-883` 的 O(tileCount) 填 `tileBuffer`，1e6 tiles = 16 MB）
+ W 扫描推出的**：**尚未用"跳过物化"的实现直接隔离**（见 §11.4 的待办）。

**(c) 内核每调用一次的前导（`BENCH_JOB=work`，ops=20，同器械）**

| batch | 空体 ns/job | work ns/job | work−empty | 每次调用多出 |
|---|---|---|---|---|
| 0（auto,128 tiles） | 13,175 | 3,191,100 | 3.18 ms | ← 体本身 ≈3.0 ms（1M 元素 × 20 ops） |
| 64 | 194,650 | 3,217,300 | 3.02 ms | ~0 |
| 8 | 1,063,350 | 4,326,575 | 3.26 ms | ~2.2 ns/call |
| **1** | **7,308,275** | **14,342,450** | **7.03 ms** | **~4.0 ns/call** |

⇒ 参数少的 work job 每-call 前导 ≈**4 ns**。而**游戏里 Melee**（58 参数扁平封送）的每-item 边际 = **25.9 ns**
（§5 的 k=8→k=1：(164.05−141.42) ms ÷ 875,000）− 框架 7 ns ⇒ **≈19 ns/call**。
⇒ **前导成本随"封送参数/指针个数"放大**（07 §7(l7)(a)：Integrate 循环内 57 条 `[rsp]` 重载源于 `_Batch` 58 个参数的扁平封送），
且细粒度下**按每次调用付**、粗粒度下**按每 ~2000 元素付**。

### 11.3 与 Unity 的最小差异隔离：**MarkDead（体最轻、item 数相同、batch 值相同）**

| | item 数 | ms | **ns/item** |
|---|---|---|---|
| **EntJoy**（mir1） | 1,000,000 | **6.92** | **6.9** |
| EntJoy 空体器械（同 tile 数） | 1,000,000 | 7.08–7.31 | 7.1–7.3 |
| **Unity**（同一 batch=1） | 1,000,000 | **0.88** | **0.9** |

⇒ EntJoy 的 6.92 ms ≈ 它自己的**空体**器械在同一 item 数下的读数 ⇒ **MarkDead 的整段差距 100% 是 JobSystem 机器**，
**每-item 机器成本 ≈7 ns vs Unity ≈0.9 ns（~7.7×）**。这是全文最干净的一条隔离（同 job、同元素数、同 batch、同 worker 数）。

### 11.4 结论：对齐档下"具体是哪里的性能问题"（按证据强度排序）

| # | 机制 | 证据 | 对齐档量级 | 能解释哪些段的差距 |
|---|---|---|---|---|
| **1** | **每工作项路径 ~7 ns，且 ~70% 不并行**（提交线程 O(T) 物化 tile 数组 + 每-tile 路径） | §11.2(a)(b) | 4.1e6 item × 7 ns ≈ **29 ms/步** | **Melee / MarkDead / Flow**（item 1e6 级）✔ |
| **2** | **Unity 同形只付 ~0.9 ns/item** | §11.3 | 差距 ≈ 25 ms/步（同 4.1e6 item） | 同上 ✔ |
| **3** | **内核每-call 前导**（58 参数扁平封送 ⇒ 每次调用数十条栈重载） | §11.2(c) + 07 §7(l7)(a) | Melee 的体在"1 元素/调用"下 **+17 ms（+12%）**（§5 的 k=8→k=1 差分） | Melee / Flow 的其余部分 ✔ |
| **4** | **薄派发次数**（Flow ~300 次/步 × 每-job 地板 ~6.8 µs） | §11.2(a) 截距 | ~2 ms/步 | Flow 的一小部分 |
| ❌ | 认领原子争用 | §11.2(b)：每-tile 成本不随 W 变差 | — | — |
| ❌ | 每-job 地板 1.22 µs / 框架簿记 2.5 µs | 07 §7(l6)/(l7) | — | — |
| **≠** | **Build / Integrate 的赤字**（+2.3 / +1.4 ms） | 它们的 item 数只有 15,625–31,250 ⇒ 每-item 税**最多解释 ~0.2 ms** | — | **不能**：属**同档下的体**（原子 RMW + 散列写 / 内核），与 JobSystem 的 per-item 路径无关 |

⇒ **对齐档下 EntJoy 落后 Unity 的地方有两处、性质不同**：
① **"Unity 选了 1"的那些 job**（Melee / MarkDead / Flow 的薄 pass）⇒ **每工作项路径**（EntJoy ~7 ns vs Unity ~0.9 ns）
＋ **重体内核的每-call 前导**（~19 ns/call）；
② **两侧都选 64 的 job**（Build / Integrate）⇒ **同档下的体/原子**，item 数只有万级，**不是** per-item 问题。

**待办（把 #1 从"推断"变"隔离"）**：
在 `ScheduleParallelForBatch` 的 General 路径加一个"**不物化**"模式（`TileKind::GeneralRange` 且等宽时，
worker 用 `claimedTileIndex` 算术推导 `first/count`，跳过 `tileBuffer` 填充与 16 MB 内存流量），
对 batch=1 的同一 bench 与真跑 `mir1` 各测一次。**验收判据**：1e6 tiles 时 `ns/tile` 应从 7.3 降到 ~1.5–2
（=只剩并行份额），且真跑 `mir1` 的整步从 245.8 降到 ~195–205。

⚠ **这两条的量级依附于"item 数"**：本负载只有在对齐档（items ≈ 4.1e6/步）才值 ~29 ms；
items 降回万级时它们自动缩到亚毫秒。所以它们不是"收益项"，而是**"别人把细档强加给我们时的鲁棒性保险"**。

**还缺的一块（唯一）**：上面只量到 **EntJoy 侧**的每-item 斜率与每-call 前导；**Unity 侧的对应斜率没有量**
（Unity 每个调用点是写死的常数）。要彻底把每条差距拆成 `items×斜率 + 体`，需要 B 侧**批大小可配置**后扫
`k∈{1,8,64,512}`（Unity 侧常量改读 env + 一次 IL2CPP 重编），与本档表同协议配对。

### 11.5 ⭐⭐ 定点消融（env-only，n=4）：对齐档下**最大的单点缺陷 = 认领步长**

表不变（= `mir1`），**只动认领**（`ChunkLevScheduler.cpp:632-634` 的 `step = clamp(tileCount/workers, 1, claimCap)`）：

| arm | 整步中位 | vs baseline | **vs Unity(161.87)** | Melee | Flow | MarkDead | Build | Integrate |
|---|---|---|---|---|---|---|---|---|
| `mir1`（`step=4`） | 251.0 | — | **0.645** | 166.5 | 60.9 | 6.74 | 5.14 | 4.20 |
| `+ENTJOY_CLAIM_BATCH=64` | 206.2 | −44.8 | 0.785 | 147.0 | 43.7 | 3.96 | 4.22 | 3.13 |
| **`+ENTJOY_CLAIM_BATCH=1024`** | **190.2** | **−60.8** | **0.851** | **136.3** | **39.7** | **3.28** | 3.40 | 3.52 |
| `+ENTJOY_CLAIM_BLOCK=1` | 206.0 | −45.0 | 0.786 | 146.9 | 43.7 | 3.84 | 3.41 | 3.77 |

⇒ **只加一个 env，EntJoy 与 Unity 在同 batchSize 下的差距从 1.55× 收到 1.17×**（86.4 ms 赤字里去掉 **60.8 ms**）。
（隐式自校验：baseline 251.0/Melee 166.5 与 §6 的 `mir1` 248.3/164.8 同档 ⇒ 表确实生效。）

**这一项里"原子"和"局部性"各占多少（判别）**：每 1e6-tile job 少掉 249,023 次认领，按段折算"每次认领值多少钱"：

| 段 | Δ（step 4→1024） | **每次认领** | 该内核的访存特征 |
|---|---|---|---|
| MarkDead | −3.46 ms | **13.9 ns** | 顺序流式 ⇒ 局部性不敏感 ⇒ **接近纯原子成本** |
| Build | −1.74 ms（2×1e6 tiles） | **3.5 ns** | 07 已证"对粒度不敏感" |
| **Melee** | **−30.2 ms** | **121 ns** | 邻居表/cell 计数器 ⇒ **空间复用敏感**（07 §8：只换顺序就 +13.84 ms） |

⇒ 不是常数原子成本（3.5~121 ns 差 35×）⇒ **大头是"`step=4` + 8 worker 让每个 worker 走交错条纹、打碎重核的空间复用"**，
**纯认领原子只占 ~14 ns/次（≈3.6 ms/每 1e6-tile job）**。这与 §11.2(b) 的"~70% 不并行"一致：
cacheline 串行化 + 交错访问共同让加核无效。

**修正后的"EntJoy 问题"排序（全部在**对齐档**内实测）**

| # | 问题 | 位置/证据 | 对齐档可回收 |
|---|---|---|---|
| **1** | **认领步长只有 4**（`clamp(T/W,1,4)`）⇒ 8 个 worker 交错走 4-tile 条纹（打碎重核空间复用）+ 250k 次争用原子 | §11.5；机制以**局部性**为主（Melee 121 ns/claim vs MarkDead 13.9） | **−60.8 ms**（1.55×→1.17×） |
| **2** | **内核每-call 前导**（58 参数扁平封送，每次调用数十条栈重载） | §11.2(c)；修掉 #1 后 Melee 仍 **+14.4 ms**（136.3 vs 121.9） | ~14 ms（Melee） |
| **3** | **O(T) tile 物化在提交线程**（`JobSystem_Scheduler.cpp:874-883`，1e6 tiles = 16 MB，~6-7 ns/tile 串行） | §11.2(b) 拟合；**`CLAIM_BATCH` 不动它**（仍在 190 ms 里） | ~25 ms（**未隔离**） |
| **4** | **每-item 路径琐碎开销**：`TryExecuteOneTile` 每 tile 做 2 次全局 flag load + `firstTileAt` load + `PrefetchNextTileData`（`GeneralRange` 不命中，纯浪费）+ **2 次间接调用** + TLS 访问 | `JobSystem_Tiles.cpp:586-681`、`GeneralFunctionTiles:1148-1159` | 未单独分离 |
| **≠** | Build / Integrate 的 +2.3 / +1.4 ms | item 仅 15,625–31,250 ⇒ 非 per-item | 体/原子（07 §7ar(k)） |

⚠ **口径**：本消融**只在对齐档内**做（表不变、只动认领），**未与 EntJoy 默认档比较**。
`ENTJOY_CLAIM_BATCH` 保留了 `tileCount/workers` 下限（小 job 自动退回细粒度），但它**同时改变默认档 job 的访问顺序**
⇒ 若要转成默认，必须走仓库标准验收（同会话 ≥6 对 + 五段全看）。
**原始档**：`tools/gate-run/jobbatchtbl/ablate/`（16 次运行）。

## 12. 通解修复清单与验收协议（2026-10-01 起）

**口径**：问题只在**对齐档**里识别（用户指令：不与 EntJoy 默认档比较），但每一项修复都要过**两条**验收：
- **R1（对齐档收益）**：表 = Unity 确定档（`mir1`），`EntJoy+fix` vs Unity，**同会话配对** ⇒ 差距必须下降。
- **R2（默认档不回归）**：**同会话** `def+fix` vs `def`，**≥6 对 + 五段全看**（Melee/Flow/Build/Integrate/MarkDead）+ 整步。
  （R2 是"不回归"验收，**不是**与 Unity 比较。）

| # | 通解（对所有 job 一致适用，不做按 job 白名单） | 落点 | 验收判据 | 状态 |
|---|---|---|---|---|
| **F1** | **认领步长自适应**（`SPAN=1024` 门控在薄 tile 上） | `ChaseLevScheduler.cpp:630-650` + `g_claimSpanElems` | R1 **0.656→0.841**；R2 配对中位 **−0.77 ms** | ✅ **完成（§12.3）** |
| **F2** | **去掉 O(T) tile 物化**：等宽 `GeneralRange` 时由 worker 用 claimed tile index 算术推导 `first/count` | `JobSystem_Scheduler.cpp`（两条 General 路）+ `TryExecuteOneTile` + `AcquireBatchStorage` 复位 | bench 1e6 tiles：`ns/tile` 4.77→**1.72**（2.78×）；R1 **−9.40 ms（4/4）**；R2 中性 | ✅ **完成（§12.6；按设计 env 门控、不提默认）** |
| **F3** | **削减每-call 内核前导**：`_Batch` 58/96 形参扁平封送 → `__scalars` 单指针（已实现） | `CppJobGenerator.cs:1072-1129`（`ENTJOY_PACK_SCALARS`） | 对齐档 **−1.24 ms（3/4）**；默认档 −0.2 ms；既有默认粒度 1.0005 | ❌ **关闭为"非瓶颈"**（§12.7.1） |
| **F4** | **每-tile 零碎开销提到每批/每令牌**：① trace/timing 两个全局载入 → 批快照；② `firstTileAt` 每 tile 判 → 每令牌判；③ TLS 记账**明确不做** | `JobSystem_Tiles.cpp`（批快照 + 2 处读）+ `ChaseLevScheduler.cpp`（令牌开头） | 器械 **2.40 → 1.84 ns/tile（−23%）**、交错配对 −18.5%（2/3）；游戏 R1 **−2.14 ms（3/4）**、R2 中性；测试两态 9/9 | ✅ **完成（§12.8；env 门控，与 F2 同理不提默认）** |
| **F1b** | **F1×guided 交互缺陷**：guided 的收缩 clamp 用旧 `claimCap` ⇒ 抹掉 `capEff`（薄 tile 上限失效） | `ChaseLevScheduler.cpp` guided 分支（1 行 → clamp 到 `capEff`） | 修前 `mir+guided` **242**（≈F1 之前）→ 修后 **195.8**（= `mir` 同档） | ✅ **bug 已修（§12.9）**；guided 默认档增益两会话矛盾（−4.36/5-6 ↔ +1.45/1-4，合并 −1.39/6-10）⇒ **不提默认** |

**明确不修（已被证据排除 / 不在框架账上）**：认领原子本身（~14 ns/次，F1 顺带摊薄）、每-job 地板 1.22 µs、
派发地板（`Schedule` 内派发 vs `ScheduleBatchedJobs` 是语义差异；两次尝试已否证）、
Build/Integrate 的体与原子（07 §7ar(k)）、内核 SIMD 质量（07 §7ar(g)：A 已比 B 少 1.7–3.9× 指令）。

**每项完成时**写回：改了什么 / 验收命令 / 读数（R1+R2）/ 未验证项 / 是否提为默认。

### 12.1 F1 第一轮：`ENTJOY_CLAIM_BATCH=1024` + `ENTJOY_CLAIM_GUIDED=1` —— **R1 ✔ / R2 ✘**

同会话 4 臂轮转、n=4、两帧（对齐/默认）**同批跑**（`tools/gate-run/jobbatchtbl/fix1/`）：

| 臂 | 整步中位 | Melee | Flow | MarkDead | Build | Integrate | 判定 |
|---|---|---|---|---|---|---|---|
| `mir1`（对齐基线） | 247.0 | 164.9 | 59.4 | 6.96 | 5.03 | 4.11 | — |
| **`mir1+fix`** | **189.6** | 135.8 | 40.0 | 3.20 | 3.20 | 3.40 | **R1 ✔：`B/A` 0.655 → 0.853**（−57.4 ms） |
| `def`（默认基线） | 155.9 | 116.6 | 30.8 | 0.57 | 5.10 | 2.90 | — |
| **`def+fix`** | **183.9** | **142.6** | 33.0 | 0.62 | 3.40 | 3.90 | **R2 ✘：+28.0 ms（+18%）**，其中 **Melee +26**（其余段都变好） |

⇒ **同一条通解在两帧上符号相反**：它把对齐档的 Melee 从 166 压到 136，却把默认档的 Melee 从 117 抬到 143。

**1b 归因（同会话 n=4，两帧只切认领）** —— `tools/gate-run/jobbatchtbl/fix1b/`：

| 臂 | 整步中位 | Melee | 读法 |
|---|---|---|---|
| `def` | 163.0 | 122.6 | 默认基线 |
| `def + ENTJOY_CLAIM_BATCH=1024` | **186.3（+23.3）** | **145.4（+22.8）** | ⇒ **回归源是 `cap`** |
| `def + ENTJOY_CLAIM_GUIDED=1`（cap 仍 4） | **154.1（−8.9）** | 114.5（−8.1） | ⇒ **`guided` 单独反而略好**，不是它 |
| （对齐档）`mir1 + cap1024` | 190.8 | 136.6 | cap 在对齐档是收益 |

**机制**：默认档 Melee 的 `tileCount/workers` 已经 ≈64（厚 tile：1e6 元素 / 512 tiles ≈ **1953 元素/tile**），
抬 cap ⇒ 每个 worker 只领**一次大块** ⇒ 8 个 worker 在 index 空间上被推开 ⇒ 打碎 Melee 依赖的
邻居表 / cell 计数器**空间复用**（与 07 §8 的 stride/顺序结论同向）。对齐档相反：1 元素/tile ⇒
`tileCount/workers` = 125k ⇒ cap=4 意味着**25 万次争用认领** ⇒ 抬 cap 就是摊薄它。

### 12.2 F1-修订（已实现，待验收）：**按元素跨度认领** `ENTJOY_CLAIM_SPAN`

```text
itemsPerTile = totalElements / tileCount
capEff = (itemsPerTile <= 16) ? clamp(SPAN / itemsPerTile, cap, SPAN) : cap      // SPAN = 1024
step   = clamp(tileCount / workers, 1, capEff)                                   // 旧式子不变
```
**为什么这是通解（不是按 job 白名单）**：它把"每次认领的**元素跨度**"钉住 ——
- 薄 tile（1 元素/tile）⇒ `capEff = 1024` ⇒ 拿到 §11.5 的收益（对齐档）；
- 厚 tile（≥1953 元素/tile）⇒ `capEff = cap = 4` ⇒ **默认档逐位不变**，正是 1b 里"默认档不喜欢大 cap"的那一侧。
⇒ 一条件公式同时满足两个区间，且不需要知道"哪个 job 是谁"。

**实现**（env 门控，0/未设 = 关 ⇒ 逐位不变）：
`JobSystem.cpp` 的 `g_claimSpanElems`、`JobSystemInternal.h` 的 `kClaimSpanThinElems = 16`、
`ChaseLevScheduler.cpp:630-650` 的 `capEff`。
**待验收**：R1（`mir + ENTJOY_CLAIM_SPAN=1024` vs Unity）与 R2（`def + ENTJOY_CLAIM_SPAN=1024` vs `def`）
同会话 4 臂 × n=4 ⇒ `tools/gate-run/jobbatchtbl/fix1c/`。

**1c 验收结果（同会话 4 臂 × n=4，`fix1c/`）**

| 臂 | 整步中位 | Melee | Flow | MarkDead | Build | Integrate | vs Unity |
|---|---|---|---|---|---|---|---|
| `def` | 154.45 | 115.11 | 30.80 | 0.55 | 5.19 | 3.16 | — |
| `def + SPAN=1024` | 156.39 | 116.98 | 30.41 | 0.545 | 4.88 | 3.27 | — |
| `mir1` | 246.83 | 164.41 | 59.95 | 6.88 | 4.98 | 3.99 | 0.656 |
| **`mir1 + SPAN=1024`** | **192.57** | **137.18** | **40.39** | **3.22** | 4.55 | 3.75 | **0.841** |

- **R1 ✔（强）**：对齐档 **−54.3 ms**，`B/A` **0.656 → 0.841**（差距 1.53× → 1.19×）；逐段改善 Melee −27.2、Flow −19.6、MarkDead −3.7。
- **R2 ◻（n=4 判为中性）**：默认档配对 Δ 中位 **+0.51 ms**（2/4 同号；逐对 −5.27/+3.15/+1.14/−0.12）；整步中位 +1.94 ms，
  **但按仓库纪律（≥6 对）还需补测**才能提为默认 ⇒ 见 §12.3。
- **门控自证**：`def+SPAN` 与 `def` 几乎一致，说明 `itemsPerTile ≤ 16` 的门在原负载上基本不开
  （默认档 job 全是 ~1953 元素/厚 tile），只对少数小 batch（如 64 元素的 PrefixSum，itemsPerTile=16）开口。

### 12.3 F1 结论：**✅ 双验收通过，已提为内置默认**

**1d R2 补测（同会话 n=6，`tools/gate-run/jobbatchtbl/fix1r2/`）**

| 臂 | 整步中位 | Melee |
|---|---|---|
| `def`（旧行为） | 158.54 | 118.98 |
| `def + SPAN=1024` | **156.53** | 117.00 |

逐对 Δ = **−5.73 / −1.55 / −4.12 / +2.48 / +0.01 / −17.15** ⇒ **配对中位 −0.77 ms、4/6 更快**
（末对的 `def` 是 176.58 的离群；去掉它仍为 −1.55）。
⇒ **R2 ✔ 无回归（略好）**，可提为默认。

**F1 最终形态**

| 项 | 内容 |
|---|---|
| 改动 | `g_claimSpanElems` **内置默认 1024**（`ENTJOY_CLAIM_SPAN=0` 可关 = 复现旧行为）；规则：**仅当 `itemsPerTile ≤ kClaimSpanThinElems(=16)`** 时 `capEff = clamp(SPAN/itemsPerTile, cap, SPAN)`，否则 `capEff = cap` |
| 落点 | `ChaseLevScheduler.cpp:630-650`（`capEff`）、`JobSystem.cpp`（env/默认）、`JobSystemInternal.h`（常量/声明） |
| **R1（对齐档）** | **246.83 → 192.57 ms（−54.3）**，`B/A` **0.656 → 0.841**（差距 **1.53× → 1.19×**）；Melee −27.2、Flow −19.6、MarkDead −3.7 |
| **R2（默认档）** | **158.54 → 156.53 ms（−2.0）**，配对中位 **−0.77 ms（4/6 更快）** ⇒ 无回归 |
| 未验证 | ① `SPAN=1024` / `kClaimSpanThinElems=16` 是**选的两个端点值，未做二维扫描**；② `guided` 在默认档单独值 −8.9 ms（§12.1 1b），**未与 SPAN 组合测** ⇒ 记为 **F1b 候选** |

**1e 出厂默认复验（同会话 n=4，`fix1final/`）：用 `ENTJOY_CLAIM_SPAN=0` 复现旧行为做对照**

| 轴 | 出厂默认 | `SPAN=0`（旧） | 逐对 Δ | 判定 |
|---|---|---|---|---|
| 对齐档（表 = Unity 确定档） | **195.41** | 252.84 | **−53.74（4/4 更快）** | R1 ✔ 复现（会话内 `B/A` ≈ **0.828** vs Unity 161.87） |
| 默认档 | **162.58** | 166.18 | **−4.17（3/4 更快）** | R2 ✔ 复现（略好） |
| 对齐档 Melee | 139.06 | 166.64 | −27.6 | — |

⇒ **F1 完成**：出厂默认（薄 tile 按元素跨度认领）在同 batchSize 下把 EntJoy 与 Unity 的差距
由 **1.53× 收到 ~1.21×**（`B/A` 0.650→0.828，会话间口径），默认档不回归。
**原始档**：`fix1/`（1a）、`fix1b/`（归因）、`fix1c/`（验收）、`fix1r2/`（R2 n=6）、`fix1final/`（出厂复验）。

### 12.4 F1 修订 2：**把门限定到"被验收过的那条路"**（通用性漏洞修补）

**漏洞（自审发现）**：1c/1d/1e 的门只用 `itemsPerTile = totalElements/tileCount`，而

| 路径 | `totalElements` | 后果 |
|---|---|---|
| `ScheduleParallelFor` / `ScheduleParallelForBatch`（等宽 GeneralRange） | 正确置为 `length`（[JobSystem_Scheduler.cpp:615/874](../../src/NativeDll/JobSystem_Scheduler.cpp#L615)） | ✅ 这是被验收的那条 |
| **chunk / entity 路**（`ChunkCallbacks`/`ChunkRange`/`EntityBatchRange`，[:1083](../../src/NativeDll/JobSystem_Scheduler.cpp#L1083)） | **根本不设** ⇒ 继承被复用 `BatchStorage` 的**陈旧值** | ❌ 门可能误开（未验收路径） |
| **packed plain jobs**（[JobSystem_Tiles.cpp:1361](../../src/NativeDll/JobSystem_Tiles.cpp#L1361)） | 显式置 **0** ⇒ `max(1,0/tileCount)=1` | ❌ 门必然误开 |

⇒ 初版 F1 会**把未验收路径一起改掉**，违反"只改已验收行为"的纪律。

**修补（已实现）**：门加硬条件，非等宽 GeneralRange **构造上不可能命中**：

```cpp
const bool spanEligible =
    g_claimSpanElems > 0 && batch->tileCount > 0 && batch->tiles != nullptr &&
    batch->tiles[0].kind == TileKind::GeneralRange &&      // 只作用于被验收过的路径
    batch->totalElements >= batch->tileCount;              // 语义兜底（GeneralRange 下恒成立）
```

**验证（"适配各种情况"的证据）**

1. **原生测试套件 9/9 全过**（`build-nativeDll-tests\Release`，逐个运行）：`AffinityMask` / `AssistLifetime` /
   `ChaseLevIntegration` / `ImplicitBatch` / `JobSystem` / `MPMCInjector` / **`PackedBatch`** / `ShutdownFinalize` /
   `SparseTileDeque` ⇒ 覆盖 chunk/entity/packed/JCC/implicit/affinity/shutdown 各路径。
2. 重跑被验收两帧（出厂默认 vs `ENTJOY_CLAIM_SPAN=0`）⇒ `fix1final2/`：**R1 复现**（对齐档 248.16 → **195.85**，
   配对 **−51.65 ms，4/4**；逐对 −53.77/−49.52/−49.03/−62.71），**R2 默认档不可测**（158.44 vs 157.38，
   配对中位 +2.05 ms、3/4，与 §12.3 的 −4.17/−0.77 一起**跨零** ⇒ 门在厚 tile 上本来就不开）。

### 12.5 通解性自评（每一项修完都答这四个问题）

| 问题 | F1 现状 |
|---|---|
| **① 认不认 job？** | **不认**。决策变量只有两个**运行时形状量**：`itemsPerTile = totalElements/tileCount` 与 `tiles[0].kind`。没有 funcHash / job 名 / 白名单；对任何调用方、任何 job（含 samples / tests）一致适用 |
| **② 会不会漏到未验收路径？** | 修订 2 后**不会**（硬条件限定 `GeneralRange`）。代价：chunk/entity 路的同类收益**暂不拿**（需单独验收才开） |
| **③ 换机器/负载还对吗？** | **方向对、数值未证**：`SPAN=1024` / `thinElems=16` 是本机本负载选的**两个端点值**，未做二维扫描 |
| **④ 有没有"本该受益却被门挡住"的？** | **有**：厚 tile 但**局部性不敏感**的内核（Build 的原子 RMW，itemsPerTile=64）在 §11.5 里吃过 cap1024 的 +1.7 ms，却被形状门拒 ⇒ 记为 **F1b**：判据升级为"**实测每-tile 成本占比**"（JCC 已学到 `perTileNs`/`perElemNs`），那才是不依赖形状的通用判据 |

⇒ **F1 = "无 job 知识、按形状自适应"的通解，且已收窄到被验收路径；但它仍是形状代理，不是终极形态（F1b）。**

### 12.6 F2：去掉 O(T) tile 物化（`ENTJOY_TILES_UNIFORM`）

**做了什么**（等宽、非 guided 的 GeneralRange 批**不再填 `tileBuffer`**）

| 侧 | 改动 |
|---|---|
| 提交侧 | `AcquireBatchStorage(0)`（只要 batch 对象，不申请 T 个 tile）；置 `batch->uniformTileSize = cs`、`batch->tiles = nullptr` |
| 执行侧 | `TryExecuteOneTile`：`uniformTileSize != 0` 时用 `first = tileIndex*size`、`count = min(size, total-first)` **算术推导**（末块裁剪与填表口径逐位一致）；并**跳过** `PrefetchNextTileData`（GeneralRange 本就不命中，07 §7(l6)(c)） |
| 复位 | 集中在 `AcquireBatchStorage` 里 `storage->batch.uniformTileSize = 0`（storage 池化复用 ⇒ 防止陈旧值泄给 chunk/entity/packed 批） |
| 不动的路径 | guided / chunk / entity / packed **一律走旧路径**（`uniformTileSize` 保持 0） |
| 与 F1 的交互 | F1 的门原要求 `tiles[0].kind == GeneralRange`，而 F2 下 `tiles == nullptr` ⇒ 已改为 `uniformTileSize != 0 \|\| (tiles && tiles[0].kind == GeneralRange)`，否则两个修复互斥 |

**bench 判据（空体、N=1e6、2 job/帧、8 worker）**

| batch | tiles | UNIFORM=0 ns/job | ns/tile | UNIFORM=1 ns/job | ns/tile | 加速 |
|---|---|---|---|---|---|---|
| **1** | 1,000,000 | 4,767,025 | 4.77 | **1,717,800** | **1.72** | **2.78×** |
| 8 | 125,000 | 631,425 | 5.05 | 331,400 | 2.65 | 1.91× |
| 64 | 15,625 | 211,925 | 13.56 | 150,025 | 9.60 | 1.41× |
| 0（auto） | 32 | 20,925 | 653.9 | 12,200 | 381.3 | 1.72× |

⇒ **判据达标**：1e6 tiles 的 `ns/tile` 落到预测区间 **1.5–2（实测 1.72）**；
叠加 F1 后，**空体每-item 从 7.08 ms → 1.72 ms（4.1×）**（F1 单独 7.08→4.77，F2 再 4.77→1.72）。

**原生测试套件：关/开两态各 9/9 全过**（开态是关键：它才真正走到 F2 的推导路径，覆盖末块裁剪、非整除长度、
依赖链、implicit batch、packed、shutdown）。

**游戏内 R1/R2**（`tools/gate-run/jobbatchtbl/fix2/`，同会话 4 臂 × n=4）

| 臂 | 整步中位 | Melee | Flow | MarkDead | Build | Integrate |
|---|---|---|---|---|---|---|
| `def`（F1 默认，F2 关） | 159.01 | 116.8 | 31.3 | 0.53 | 5.17 | 3.06 |
| `def + UNIFORM` | 160.58 | 120.6 | 31.0 | 0.55 | 5.06 | 3.10 |
| `mir`（对齐档，F2 关） | 200.36 | 141.7 | 41.9 | 3.44 | 4.65 | 4.24 |
| **`mir + UNIFORM`** | **190.97** | 138.4 | **37.0** | **2.09** | 4.65 | 4.06 |

- **R1 ✔**：对齐档配对 **−9.40 ms（4/4 更快）**（逐对 −9.28/−13.58/−9.51/−9.68）；
  逐段 Melee −3.3、**Flow −4.9**、**MarkDead −1.35**、Integrate −0.2、Build 0。
  **与 bench 预测吻合**：对齐档 ~4.1e6 items × (4.77−1.72 ns) ≈ 12 ms（实测 9.4，因其中 64 档的 job 获益较少）。
- **R2 ◻**：默认档配对中位 **−0.34 ms（2/4）** ⇒ 中性；n=6 补测（`fix2r2/`）：默认档
  `def` 157.22 vs `def+UNIFORM` 157.54（整步中位 **+0.32 ms**），配对中位 **+1.66 ms（4/6，含 +10.36 离群）** ⇒
  与 n=4 合并后**在机器漂移带内中性**。

**F2 定案：✅ 缺陷已消除并复现（R1 ✔）；但按设计【不提为默认】，保持 env 门控。**

理由（可核对，不是含糊其辞）：
1. **F2 只在"细 tile"有量**：出厂默认走 JCC，tile 是粗的（~19k tiles/步）⇒ 填表只占总量的 ~19k×3 ns ≈ **0.06 ms/步（0.04%）**，
   **在默认档不可测**；而它对齐档（4.1e6 items/步）值 **−9.4 ms**。
2. 因此"改默认"带来的是**不可测的收益 + 一次全路径行为变更**（违反"默认档逐位不变"的保守纪律）。
3. **正确用法**：若将来把"对齐档 / 细粒度"作为受支持配置，则应 **F1+F2 一起打开**（F1 已在默认里，
   F2 用 `ENTJOY_TILES_UNIFORM=1`）；两者已修掉互斥问题（§12.6 的交互行）。
4. F2 的价值主要是**鲁棒性**：细粒度下不再有 O(T) 填表与 16 MB/1e6 tiles 的流量。



### 12.7 F3：削减每-call 内核前导 —— **方向已被实现过且证为中性；本轮在"对齐档"复测**

**① 前提更正（先纠本文自己的说法）**：F3 原表述是"58 参数扁平封送"。实测核准：
- 生成内核 `SharpNative_Job_CPUBattle_MeleeSimJob_Execute_Batch` 的**形参数 = 96**（不是 58；58 是 **adapter 的字段数**）；
- 该内核是**标量逐元素循环**（`for (int index = __startIndex; index < __startIndex + __count; ++index)`，
  `simd_mask`/`SimdValue` 出现 **0 次**）⇒ 细粒度下**没有"外层 SIMD 利用率"问题**，这点先排除。

**② 既有实现与结论（跨仓证据，2026-09-16）**：EntJoy 侧早有 `ENTJOY_PACK_SCALARS`（把纯值字段收进
`__scalars` 结构体、以单指针传参），实现已补齐（`PackScalarsEnabled/PackScalarsForJob`，
`CppJobGenerator.cs:1072-1129`），并在**真实 Melee 内核 + s60 配对协议**上端到端验收过：

| 项（游戏侧 §16.45(aj)，15 worker、8 轮逐轮交替） | 结果 |
|---|---|
| Melee `_Batch` 形参数 | **96 → 65**（32 个纯值字段收进 `__scalars`） |
| Melee 逐轮比值 | 1.003 / 1.110 / 0.973 / 0.985 / 0.998 / 1.044 / 0.998 / 1.005 ⇒ **中位 1.0005（4/8）** |
| 整步 / Flow / Build / Integrate | 1.0053 / 0.9782 / 0.9797 / 0.9751 |
| 裁定 | **形参数不是瓶颈**；热循环的瓶颈是**同时存活的 ~22 个值**（寄存器压力） |

⚠ **两条会让构建期开关的 A/B 静默失效的操作坑**（游戏侧 §16.45(aj)(b)，本轮照办）：
① 生成器**不覆盖已有生成物** ⇒ 必须删掉 `SharpNative_*` 全部（只删 stamp/hash 无效）；
② **MSBuild 复用节点会粘住环境变量** ⇒ 必须先 `dotnet build-server shutdown` 或加 `-p:UseSharedCompilation=false`。

**③ 本轮新信息：把 F3 放到"对齐档"口径下复测**（⚠ 那次验收是在**默认粒度**、~19k 次调用/步下做的；
对齐档的调用数是它的 50~100×，每-call 固定代价的权重完全不同 —— 与 F1 的"0.08% vs 60 ms"同型）。

**协议**：**同一下 NativeDll**（F1 已默认、F2 关），只换 `NativeTranspiled.dll` + 托管程序集；
两臂交替轮转、n=4、同会话；对齐档用 Unity 确定档表（`applied` 自证）。
**自证换过配置**：打包臂 `Melee _Batch = 65` 形参、`__scalars` 出现 **33** 次；基线臂 96 形参 / 0 个 `__scalars`。

⚠⚠ **本轮踩到的第三个方法坑（比结论更值得记）**：**打包重建移动了生成代码的 RVA ⇒ 沿用基线表键会静默不命中**。
第一次跑出来的"`mir_packed` ≈ 155 ms / Melee 115"就是这个假象（那其实是**默认粒度**的数），
而我在汇总里用的 `applied=15` **只统计了 dump 行数、并不证明键命中** —— 与 §1 已写的"表与构建绑定"是同一件事，我这次没照做。
**修法（两处一起做）**：① 每次重建后**重推表键**（`dumpbin /exports` 取 adapter RVA，两臂各自一张、且限定同一组 15 个逻辑 job）；
② 每轮**校验 `applied` 等于表值**（逐项比对 `key=.. applied=N`），命中数须 = **15/15** 否则该轮判废。
（两臂表：base `000051f0…` / packed `00005300…`，各 15 项。）

| 臂 | 整步中位 | Melee | Flow | MarkDead | Build | Integrate | 表命中 |
|---|---|---|---|---|---|---|---|
| `mir_base`（96 形参） | 193.51 | 137.38 | 40.35 | 3.29 | 4.51 | 3.90 | 15/15 |
| `mir_packed`（65 形参） | **191.95** | **136.21** | 40.39 | 3.26 | 4.42 | 3.77 | 15/15 |
| `def_base`（96） | 157.06 | 117.38 | 30.82 | 0.53 | 4.91 | 3.10 | — |
| `def_packed`（65） | **156.85** | **116.52** | 31.20 | 0.57 | 5.01 | 2.97 | — |

- **对齐档配对 Δ（base − packed）**：**+1.24 ms（3/4 打包更快，−0.6%）**，逐对 +2.78/+0.50/+1.97/−0.18；
  逐段几乎全落在 Melee（**−1.17 ms**）。
- **默认档配对 Δ**：+1.47/+1.69/−3.61 ⇒ 中位 **+1.47 ms**；整步中位 **157.06 → 156.85（−0.2 ms）** ⇒ 中性。

### 12.7.1 F3 定案：**✅ 关闭为"非瓶颈"（两口径一致）**

| 口径 | 形参 96 → 65 的收益 | 判定 |
|---|---|---|
| 默认粒度（游戏侧 §16.45(aj)，15 worker、8 轮） | 中位 1.0005（4/8） | 中性 |
| **默认档（本轮，8 worker、n=4）** | −0.2 ms（配对 +1.47，2/3） | 中性 |
| **对齐档（本轮，调用数 ×50~100）** | **−1.24 ms（−0.6%，3/4）** | 中性（噪声内） |

⇒ **"形参扁平封送"不是 EntJoy 在对齐档落后的原因**：把 96 个形参压到 65（32 个纯值字段进 `__scalars`）
在两个口径下都只值 ~1 ms。**对齐档剩下的 Melee +15.5 ms（137.4 vs Unity 121.9）≈ 15.5 ns/次调用，
主要不在 JobSystem 的参数通路里**，而在内核自身的每-call 工作集（既有反汇编已定位为"热循环同时存活的 ~22 个值"）。
⇒ **F3 作为"JobSystem 缺陷"不成立；要再往下走就必须动内核/生成器的每-call 工作集（风险与面都大得多，超出本 goal）。**


### 12.8 F4：把每-tile 固定开销提到每批/每令牌（`ENTJOY_TILE_FASTPATH`）

**做了什么**（三处里做两处，第三处**明确不做**并说明理由）

| # | 每-tile 开销（原） | F4 做法 |
|---|---|---|
| ① | `g_traceEnabled.load` + `g_timingDiagnosticsEnabled.load`（两次全局 atomic relaxed + 两个分支） | 批构造时快照进 `BatchState.traceOn/timingOn`（在 `AcquireBatchStorage` 里刷新，池化复用也每次覆盖） |
| ② | `batch->firstTileAt.load()==0`（每 tile 一次 load + 比较，命中才 CAS） | 改到 **`ExecuteClaimToken` 的令牌开头判一次**；`TryExecuteOneTile` 整段跳过 |
| ③ | `t_tileAcctGroupActive` / `t_tileAcctGroupCount` 的 **TLS 读写** | **不做**：删它必须把 **6 处** `TileAcctGroupBegin/Flush` 改成显式传计数（其中 4 处是 range 循环、没有现成计数）⇒ **记账正确性风险 > 其 <1 ns/tile 的收益** |

**实现**：`JobSystemInternal.h`（`BatchState.traceOn/timingOn/tileFast` + `g_tileFastPath`）、`JobSystem.cpp`（env）、
`JobSystem_Tiles.cpp`（批快照 + 两处读改批内字段 + firstTileAt 跳过）、`ChaseLevScheduler.cpp`（令牌开头判一次）。
默认关 ⇒ 逐位不变。**F4 不动 `tilesRemaining` 记账**（故无挂起风险）。

**器械判据（SchedTileBench，空体、N=1e6、8 worker、batch=1）**

| 组（同会话顺序四组） | ns/tile |
|---|---|
| `UNIFORM=0` / F4 关 | 4.87 |
| `UNIFORM=0` / F4 开 | 4.58 |
| `UNIFORM=1` / F4 关 | 2.40 |
| **`UNIFORM=1` / F4 开** | **1.84（−23%）** |

⇒ **叠加 F1+F2+F4**：空体每-item **7.08 ms（F1 前）→ 1.84 ms / 1e6 tiles ≈ 3.85×**。

⚠ 顺序四组有漂移嫌疑，故**交错 3 对复核**（同会话、交替 `FASTPATH`）：`=0` **2.37** ns/tile ↔ `=1` **1.93（−18.5%，2/3 同号）**，
逐对 Δ = −0.40 / +0.65 / −0.79 ms ⇒ **器械自身噪声 ±25%**，F4 真实量级 ≈ **0.3–0.4 ns/tile**。

**换算到游戏**：对齐档 ~4.1e6 items/步 × 0.35 ns ≈ **1.4 ms/步（0.6%）**；默认档（~19k tiles/步）≈ **0.007 ms/步**
⇒ **预期在游戏里落在噪声内**（这也是为什么本节以**器械**为主判据，而不是像 F1/F2 那样以游戏配对为主判据）。

**原生测试套件：F4 关 / 开 两态各 9/9 全过**（`build-nativeDll-tests\Release`）。

**游戏内 R1/R2**（`tools/gate-run/jobbatchtbl/fix4/`，同会话 4 臂 × n=4，含表命中自证）

| 臂 | 整步中位 | Melee | Flow | MarkDead | Build | Integrate | 表命中 |
|---|---|---|---|---|---|---|---|
| `mir_f4off` | 196.31 | 140.07 | 40.55 | 3.33 | 4.46 | 3.98 | 15/15 |
| **`mir_f4on`** | **195.09** | 139.29 | 40.43 | 3.28 | 4.48 | 3.75 | 15/15 |
| `def_f4off` | 155.34 | 115.78 | 30.44 | 0.55 | 5.11 | 3.05 | — |
| `def_f4on` | **155.09** | 115.65 | 30.52 | 0.59 | 5.13 | 2.94 | — |

- **R1（对齐档）**：配对 **−2.14 ms（3/4 更快）**，逐对 −1.18/−3.09/−4.74/+0.58 ⇒ 量级与器械预测（~1.4 ms）同向、略大
  （器械 −18.5% 的残差 × 4.1e6 items）。
- **R2（默认档）**：配对 **−0.38 ms（2/4）**，整步中位 −0.25 ms ⇒ **中性、无回归**。

**F4 定案：✅ 已实现并验收（R1 小幅正、R2 中性）；与 F2 同理【保持 env 门控、不提默认】**
—— 它的价值同样只在"细 tile"：默认档 ~19k tiles/步 ⇒ 只值 **≈0.007 ms**（不可测）；
对齐档 ~4.1e6 items/步 ⇒ ~1.4–2 ms。**正确用法**：细粒度/对齐档配置与 F2 一起开（`ENTJOY_TILE_FASTPATH=1`）。


### 12.9 F1b：F1 × guided 的**交互缺陷**（已修）——并复核 guided 在默认档的收益

**怎么发现的**：F1b 第一轮（`f1b/`，n=6 轮转 4 臂）里，`mir + ENTJOY_CLAIM_GUIDED=1` 的整步回到 **242 ms**
（Melee 163 / MarkDead 6.7 / Flow 56.6）——**约等于 F1 之前**（§5 的 `mir1` 245.8）；
而同轮的 `def + guided` 却**更快**（158.3 → **153.5，−4.7 ms**）。同一个开关在两条轴上差 46 ms，只能是交互问题。

**根因（代码级）**：`ExecuteClaimToken` 的 guided 分支把收缩后的 step **clamp 回旧 `claimCap`**（=4），
而我 F1 的薄-tile 上限是 `capEff`：

```cpp
const uint32_t claimCap = (g_claimBatchSize != 0) ? g_claimBatchSize : kClaimBatchSize;  // = 4
uint32_t capEff = claimCap;
if (spanEligible) capEff = clamp(SPAN/itemsPerTile, claimCap, SPAN);   // 薄 tile → 1024
uint32_t step = clamp(tileCount/workers, 1, capEff);                   // 首步 = 1024 ✓
...
if (g_claimGuidedEnabled)
    step = clamp(remaining/(workers*2), 1, claimCap);   // ❌ 覆盖成 ≤4 ⇒ 下一步起退化成"每 4 tile 一次认领"
```
⇒ guided 一开，F1 的收益**从第二次认领起就没了**（对齐档 250k 次争用认领回来了）。

**修法（1 行）**：guided 的收缩也 clamp 到 **`capEff`**（厚 tile 时 `capEff == claimCap` ⇒ 默认档行为不变）。
语义上这也才是原设计意图："批次肥时块大、接近尾部自动退化到 1"。

**修后复核**（`f1b2/`，同会话 4 臂 × n=6，两帧同批跑，含表命中自证）

| 臂 | 整步中位 | Melee | Flow | MarkDead | Build | Integrate | 表命中 |
|---|---|---|---|---|---|---|---|
| `def`（guided 关） | **156.40** | 116.5 | 30.9 | 0.56 | 5.06 | 3.06 | — |
| **`def_g`（guided 开）** | **152.80** | 113.8 | 30.8 | 0.58 | 5.01 | 2.90 | — |
| `mir`（guided 关） | 196.69 | 140.2 | 40.7 | 3.48 | 4.42 | 3.98 | 15/15 |
| **`mir_g`（guided 开）** | **195.82** | 139.7 | 40.4 | 3.44 | 4.38 | 3.90 | 15/15 |

- **R2（默认档）**：配对中位 **−4.36 ms（5/6 更快）**，逐对 +2.35/−1.51/−5.29/−3.43/−7.06/−5.58；
  整步中位 156.40 → 152.80（**−3.60 ms，−2.3%**）⇒ **有增益且无回归**。
- **R1（对齐档）**：配对中位 **−1.36 ms（4/6）**，整步中位 196.69 → 195.82 ⇒ **中性/略好**（guided 只细化尾部，
  这正是修完交互后的预期）。
- **交互修复的独立自证**：修前 `mir+guided` = **242.4 ms**（≈F1 之前），修后 **195.8 ms** ✓。

**F1b 定案：⚠ clamp 修复保留；`guided` 的默认档增益【不可复现】⇒ 回滚为默认关**

单会话看它像通过（会话 A：−4.36 ms、5/6），于是先提了默认；**再复验（会话 B，n=4，出厂默认 vs `ENTJOY_CLAIM_GUIDED=0`）没复现**：

| 会话 | 默认档整步中位 | 配对中位 | 同号 |
|---|---|---|---|
| A（`f1b2`，n=6） | 156.40 → 152.80 | **−4.36 ms** | **5/6 更快** |
| B（`f1b3`，n=4） | 154.19 → 155.80 | **+1.45 ms** | **1/4 更快** |
| **合并（10 对）** | — | **−1.39 ms** | **6/10** |

⇒ 落在机器漂移带内、**符号随会话翻转** ⇒ **按仓库纪律不改默认**（`g_claimGuidedEnabled` 回滚为 false；
`ENTJOY_CLAIM_GUIDED=1` 仍可开）。机制仍成立（几何收缩 ⇒ 尾部更细 ⇒ 异构 job 尾部均衡更好），
但量级在噪声边缘；**要重启这条线需要 ≥10 对同会话 + 五段全看，并在机器安静时**。
**不管开关如何，本次 clamp 修复必须保留**（否则开启 guided 会把 F1 的薄-tile 上限抹掉：修前 `mir+guided` 242 vs `mir` 196）。
**原生测试：guided 关 / 显式开 两态各 9/9 全过。**



### 12.10 下一步（本 goal 的四项已全部处置完毕）

| 项 | 结果 | 默认 |
|---|---|---|
| **F1** 认领步长自适应（元素跨度 + 薄-tile 门控） | 对齐档 **246.83 → 192.57**（`B/A` 0.656 → **0.841**）；默认档配对 −0.77 ms | ✅ **已提默认** |
| **F2** 去 O(T) tile 物化 | 对齐档 **−9.40 ms（4/4）**；器械 `ns/tile` 4.77 → 1.72 | env 门控（默认档值 ≈0） |
| **F3** 削减每-call 内核前导 | 对齐档 −1.24 ms（3/4）、默认档 −0.2 ms、既有默认粒度 1.0005 | ❌ 非瓶颈（enabled 可用） |
| **F4** 每-tile 零碎开销提到每批/令牌 | 器械 **−23%**（交错 −18.5%）；对齐档 **−2.14 ms（3/4）**；测试两态 9/9 | env 门控（默认档值 ≈0.007 ms） |
| **F1b** F1×guided 交互 bug | 修前 `mir+guided` 242 → 修后 195.8；guided 默认档增益不可复现 | ✅ bug 已修；guided **保持默认关** |

**叠加效果（出厂默认，F1 + F1b 修复）**：对齐档比 Unity 慢 **1.19×**（F1 前 1.53×）；
空体每-item 器械从 7.08 ms → **1.84 ms/1e6 tiles（F1+F2+F4）≈ 3.85×**。

**仍未做（可选 / 超出本 goal）**
1. **F1b 重启条件**：`guided` 的默认档增益需要 **≥10 对同会话 + 五段全看 + 机器安静** 才能定。
2. **F1 阈值二维扫描**：`SPAN` × `kClaimSpanThinElems`（当前 1024 / 16 是端点取值）。
3. **内核侧**：对齐档剩下的 Melee +15.5 ms 主要在内核每-call 工作集（~22 个同时存活值）⇒ 要动生成器/内核，面与风险大。
4. **chunk/entity 路的同类收益**（F1/F2 显式排除）需单独验收后才能开。

## 13. 现状态：**默认档 / 对齐档 vs Unity**（2026-10-01 收尾，可引用）

### 13.1 框架侧现在是什么状态

| 开关 | 现状 | 说明 |
|---|---|---|
| **F1 元素跨度认领**（`g_claimSpanElems`） | **默认 1024（已开）** | 仅当 `itemsPerTile ≤ 16`（薄 tile）时抬高认领上限；厚 tile 走 `cap=4` ⇒ 默认负载行为不变 |
| **F1b guided 的 clamp** | **修复已在内** | guided 的收缩改为 clamp 到 `capEff`（原来用 `claimCap` 会抹掉 F1 的薄-tile 上限：修前对齐档 242 vs 修后 196） |
| `ENTJOY_CLAIM_GUIDED`（guided） | **默认关** | 默认档增益两会话矛盾（−4.36/5-6 ↔ +1.45/1-4，合并 −1.39/6-10）⇒ 按纪律不改默认（§12.9） |
| **F2** `ENTJOY_TILES_UNIFORM` | 默认关（**可选**） | 只有细粒度/对齐档有量（默认档 ≈0.06 ms/步） |
| **F4** `ENTJOY_TILE_FASTPATH` | 默认关（**可选**） | 同上（默认档 ≈0.007 ms/步） |
| **F3** `ENTJOY_PACK_SCALARS` | 默认关 | 两口径中性 ⇒ 关闭为"非瓶颈"（§12.7.1） |
| **F5 连续等宽 tile 合并**（`g_tileRunEnabled`） | **默认开（已验收）** | §14：对齐档 **−18.66 ms（3/3）**、`B/A` 0.836→0.946；默认档 **−0.84 ms（4/6）**、五段无回归；`ENTJOY_TILE_RUN=0` 回退。**同一开关也覆盖 chunk/entity 路的"相接 tile"（F5b，§15.2）**；packed 路实测有副作用 ⇒ 排除 |
| **F6 按 job 认领几何**（`g_claimAdaptiveEnabled`） | **默认关（未达门槛）** | §15.4：Build **−1.90 ms（10/10）** 但整步配对中位 **+1.65 ms（4/10 更快）** ⇒ 机制成立、整步不复现 ⇒ 与 `guided` 同处置（`ENTJOY_CLAIM_ADAPT=1` 可手动开；对齐档天然 no-op） |

### 13.2 对比表

⚠ **口径**：EntJoy 的三行是**各修复的同会话配对**结果（`f1b3` / `fix1r2` / `fix1final` / `fix2` / `fix4`）；
**Unity 的 B 引自 §6 的同协议会话**（def 臂 B=166.27、mir 臂 B=161.87；该会话 B 自身漂 12.9%）
⇒ 比值是**量级/区间**，不是同会话精确值。

| 配置（EntJoy 侧） | EntJoy 整步中位 | Unity B | **`B/A`** | 谁快 |
|---|---|---|---|---|
| **出厂默认**（F1 开 + F1b 修复；guided/F2/F4 关） | **154.2 ms**（`f1b3`；同协议另两会话 156.5 / 157.1） | 166.27 | **≈1.078** | **EntJoy 快 ~7%** |
| **对齐档**（逐 job 镜像 Unity 确定档；F1 开） | **195.4 ms**（`f1b3`；`f1b2` 196.7） | 161.87 | **≈0.828** | **EntJoy 慢 ~21%** |
| 对齐档 **+ F2 + F4**（`ENTJOY_TILES_UNIFORM=1`、`ENTJOY_TILE_FASTPATH=1`） | **≈184 ms**（=195.4 − 9.4 − 2.1） | 161.87 | **≈0.881** | EntJoy 慢 ~13% |
| 对齐档 **F1 之前**（历史） | 248.3 ms | 161.87 | 0.650 | EntJoy 慢 ~54% |

**F1 前后（对齐档）**：**248.3 → 195.4 ms（−53 ms）**，`B/A` **0.650 → 0.828**（差距 **1.54× → 1.21×**）；
再开 F2+F4 ⇒ **~1.14×**。

### 13.3 读数注意（必须与数字一起带）

1. **Unity 的 B 是跨会话引用**（§6 会话：def 臂 B=166.27 / mir 臂 B=161.87）；要给精确同会话比值，
   需要**重跑一次 A(def + mir) vs B 的配对会话**（每臂各配一个相位对齐的 B，协议见 07b §1）。
2. EntJoy 三行来自不同会话（各自有同会话配对自证），跨会话差 ~1–3 ms。
3. **`applied=15/15` 是每轮必备自证**（表键与构建绑定，重建后必须重推；§9 器械坑 8）。
4. 对齐档 ≈4.1e6 items/步、默认档 ≈19k tiles/步 ⇒ **同一份框架改动在两档的价值可差 100×**
   （F1：默认 0.09% ↔ 对齐 21%）。

### 13.4 建议配置

- **保持出厂默认**（F1 已开 + F1b 修复）：比 Unity 快 **~7%**，且默认档验收/测试全过。
- 若把"对齐 Unity 粒度"当受支持模式：**F1（默认）+ F2 + F4 一起开** ⇒ 与 Unity 差距 ≈**1.14×**；
  剩余 ~13% 已归因到**内核侧**（Melee 每-call 工作集 ~22 个同时存活值，§12.7.1）与 Build/Integrate 的体/原子（与粒度无关）。
- **不要**把 `ENTJOY_PACK_SCALARS`（F3）或 `guided` 当性能手段打开（前者两口径中性、后者未复现）；两者作为可用能力保留。

### 13.5 ⭐ 加入 F5 之后的现状态（2026-10-01 收尾，**取代 §13.2 的"对齐档"行**）

出厂默认现在是 **F1（元素跨度认领）+ F5（连续等宽 tile 合并）**；F2/F4 仍为可选 env。

| 配置 | EntJoy 整步 | Unity B | **`B/A`** | 谁快 |
|---|---|---|---|---|
| **出厂默认**（F1+F5） | **152.3 ms**（`final/def_f5default` 单跑）／156.1（`run5r2` 中位，同会话） | ~161.6（跨会话） | **≈1.05–1.07** | EntJoy 快 ~5–7% |
| **对齐档**（F1+F5） | **168.8 ms**（`final/mir_f5default` 单跑）／**173.3**（`run5c` 中位） | **160.5**（同会话 B 中位） | **≈0.93–0.95** | EntJoy 慢 ~5–7% |
| 对齐档 **+F2+F4** | **165.5**（`run5c` 中位） | 160.5（同会话） | **≈0.97** | EntJoy 慢 ~3% |
| 对齐档（F1，**无** F5，历史） | 191.9 | 160.5 | 0.836 | 慢 ~20% |
| 对齐档（F1 之前，历史） | 248.3 | 161.9 | 0.650 | 慢 ~54% |

**对齐档赤字演化**：**1.54×（F1 前）→ 1.20×（F1）→ 1.06×（+F5）→ 1.02×（+F2+F4）**。
残余（~3%）已全部落在**体**上：Melee +4.6 ms、Build +1.7、Integrate +1.4（Flow −1.8、MarkDead −0.3 是 EntJoy 反超）。

## 14. F5：对齐档残余（~13%）的**根因 = 每-tile 一次内核调用**（2026-10-01 追加，可引用）

### 14.0 一句话

"**同样的 Job 算法内容，为什么执行效率不一样**"——把 batchSize 钉成同一张表之后，剩下的差异**不是算法、不是粒度、不是并行度**，
而是**调用协议**：档=1 时 EntJoy 变成"**每个元素重新建立一次内核入口**"，Unity 是"**一个 job 函数内部 for 循环遍历该 worker 领到的 range**"。
两边处理的元素一样多、体一样、档一样，差的只是"每元素要不要重付一次入口"。

### 14.1 同一份元素，两边各要做什么（代码级，非推断）

**Unity（IL2CPP/Burst）**：worker 领到一段 range（`BattleBenchM4Entry.cs:423` `melee.Schedule(n, 0)`），
在 `Execute(index)` 的循环里逐元素跑体；job 字段/组件指针通过 job 结构体 `this` 相对寻址 **⇒ 循环内不重建入口**。

**EntJoy（对齐档，1 元素/tile）** —— 每个元素要走完这条链：

| # | 步骤 | 位置 |
|---|---|---|
| 1 | `executor_(batch, t)`（**间接调用**） | `ChaseLevScheduler.cpp:755` |
| 2 | `TryExecuteOneTile`：每 tile 的 trace/timing 开关、`firstTileAt`、`PrefetchNextTileData`、`try/catch` | `JobSystem_Tiles.cpp:592` |
| 3 | `GeneralExecuteTile` → `batchFunc(ctx, start, 1)`（**第二次间接调用**） | `JobSystem_Tiles.cpp:1178` |
| 4 | **生成的适配器逐字段读 `context`**（Melee **94** 次读） | `SharpNative_Job_*_Execute_Adapter.cpp` |
| 5 | **把 ~58 个参数封送进 `_Batch`**（x64 只有 4/6 个参数寄存器 ⇒ 其余全落栈） | 同上 |
| 6 | `_Batch` 前导再把字段读回来，才进入体循环（**此处 `count=1`**） | `SharpNative_Job_*_Execute.cpp` |

### 14.2 静态证据：每次调用的"入口税"与**每次调用要拆的字段数**单调同向

| kernel | 适配器行数 | **每次调用读 context 字段数** | 实测每次调用边际 |
|---|---|---|---|
| **MeleeSimJob** | 152 | **94** | **≈11.3 ns**（档 1→8）／13.4 ns（1→64） |
| SpawnJob | 105 | 47 | 未测 |
| IntegrateJob | 113 | 56 | 未测 |
| PlaceCellsJob | 75 | 19 | 未测 |
| FlowGradJob | 74 | 17 | 未测 |
| **MarkDeadJob** | 72 | **15** | **≈3.2 ns**（档 1→8，4/4 一致） |
| CountCellsJob | 71 | 15 | 未测 |
| FlowClearJob | 61 | 4 | 未测 |

⇒ 线性拟合 ≈**0.13 ns/字段 + ~1.5 ns 固定**（固定 = 两次间接调用 + `_Batch` 前导）。

### 14.3 动态证据 A：`calls/` 会话（A-vs-A，n=4，表不变、**只把一个 job 的档抬高**）

| 臂 | Melee（4 轮） | MarkDead（4 轮） |
|---|---|---|
| `mir1`（全 1） | 142.33 / 142.09 / 146.36 / 138.20 | 3.33 / 3.91 / 3.73 / 3.68 |
| `melee8` | 131.30 / 130.91 / 143.55 / 129.41 | — |
| `melee64` | 128.92 / 128.67 / 130.35 / 142.11 | — |
| `md8` | — | **0.92 / 0.85 / 0.91 / 0.98** |

配对中位：Melee 档 1→8 **−9.9 ms**（3/4）、1→64 **−13.4 ms**（3/4）；MarkDead 档 1→8 **−2.76 ms（4/4）**。
每轮 `applied=15/15` 自证；`ENTJOY_TILE_RUN/CLAIM_SPAN/F2/F4` 全关（纯基线 DLL）。
（`md8` 的 4/4 一致性最好 ⇒ 它的"每元素固定税"量得最干净：**2.76 ms / 875k 次少掉的调用 = 3.15 ns/次**。）

### 14.4 动态证据 B：与 Unity 的**最小对照**（MarkDead：同 job、同元素数、同档、同 worker 数）

| | 元素数 | ms | ns/元素 |
|---|---|---|---|
| **Unity**（`Schedule(n, 0)`，同会话 B） | 997,986 | **0.85–0.86** | **0.85** |
| EntJoy `mir1`（档 1） | 1,000,000 | 3.26–3.34 | 3.3 |
| **EntJoy `md8`（档 8）** | 1,000,000 | **0.85–0.98** | **0.92** |
| EntJoy `mir1_run_f`（档 1 + 合并 + F2/F4） | 1,000,000 | **0.55–0.60** | **0.56** |

⇒ 该窗口内 0 个死单位 ⇒ 两侧"体"都只是"load + 分支"。**去掉每元素调用税后 EntJoy 与 Unity 同档（0.9 ns/元素）**；
对齐档下多付的 ~2.5 ms **100% 是每元素调用税**。（F5 后甚至更快，因为合并后连每元素的 tile 记账也省了。）

### 14.5 修复 F5（通解）：**连续等宽 tile 合并成一次内核调用**

| 面 | 内容 |
|---|---|
| 开关 | **`ENTJOY_TILE_RUN`，已验收 ⇒ 默认开**（`ENTJOY_TILE_RUN=0` 回退；§14.7 的 R2 通过后提默认） |
| 门控条件 | General 路 + 非 guided + 等宽（`fuseTileSize>0`）+ `tileStride==1` + **trace/timing 关** |
| 落点 | `BatchState.fuseTileSize`（`JobSystemInternal.h`）；两条 General 提交路径写入（`JobSystem_Scheduler.cpp`）；`AcquireBatchStorage` 复位；认领循环（`ChaseLevScheduler.cpp`）改走 `executorRun_`；新 trampoline `ChaseLevExecuteTileRun`（`JobSystem_Tiles.cpp`） |
| 语义 | 认领块 `[t0, t0+run)` ⇒ 合成 tile = `{t0*size, min(run*size, total−t0*size), GeneralRange}`，**一次** `executeTile`。与逐 tile **逐位等价**：① 生成的 `_Batch` 里 `__count` **只作循环上界**（全 15 个内核都已核对）；② 索引集合/顺序不变；③ 记账仍按 `run` 个 tile 计（`tilesRemaining` 总量不变，认领组口径不变） |
| **验证** | ① 原生测试 **9/9 全过**（F5 显式关 / 显式开两态；`tools/gate-run/run-native-tests.ps1`）；② `JobSystemStressTest` 全部子项（含 **`MassiveParallelFor (100K elements)`：每个元素恰好命中一次**）在 `ENTJOY_FORCE_INNER_BATCH=1` 下 F5 开/关**都过**；③ 新增单测 **`TestTileRunFusionCoverage`**（`tests/NativeDll.Tests/JobSystemTests.cpp`）：F5 关时"回调次数 == tile 数"、F5 开时"回调次数 ≤ tile 数/2"、两态都要求"`count>0` ∧ 区间落在 `[0,N)` ∧ **元素计数和恰好 == N**"（= 恰好覆盖一次） |
| ⚠ 既有单测的**契约代理**需要修 | `TestAutomaticBatchDensity` 原用"回调次数"当 tile 数代理 ⇒ F5 下必然失败（tile 布局没变，变的是回调次数）。已按它关 JCC 的同一手法：本用例内临时关 F5 后再量密度 |

**冒烟（同 DLL、同表、单跑，仅示量级）**：`mir1` ~192（Melee 136 / MarkDead 3.3 / Flow 40.6）→
**`mir1_run` 173.3（Melee 126.4 / MarkDead 1.84 / Flow 34.7）** → **`mir1_run_f` 165.5（Melee 126.3 / MarkDead 0.56 / Flow 30.3）**。

### 14.6 R1：**同会话** A/B（表 = Unity 确定档；n=3，每个 A 臂各配一个相位对齐的 B；`tools/gate-run/ab-tile-run.ps1`）

| 臂（A = EntJoy，全部带 `mir1` 表） | 整步中位 | Build | Flow | **Melee** | **MarkDead** | Integrate | **B/A 同会话（3 轮）** |
|---|---|---|---|---|---|---|---|
| `mir1`（现出厂默认 + F1） | 191.92 | 4.54 | 40.57 | 136.27 | 3.34 | 3.87 | **0.845 / 0.831 / 0.836** |
| **`mir1_run`（+F5）** | **173.26** | 4.60 | 34.71 | **126.44** | **1.84** | 3.92 | **0.938 / 0.946 / 0.953** |
| `mir1_run_f`（+F5+F2+F4） | **165.51** | 4.38 | 30.25 | **126.31** | **0.56** | 3.87 | **0.994 / 0.987 / 0.967** |
| `mir1_run_b`（+F5 + 每 worker 一整块） | 182.95 | 3.47 | 36.31 | 137.01 | 1.88 | 3.37 | 0.888 / 0.890 / 0.905 |
| （同会话 Unity B） | 160.52 | 2.7 | 32.05 | 121.70 | 0.85 | 2.5 | — |

**配对差（同一轮内，vs `mir1`）**：`mir1_run` **−18.66 ms（3/3）**，其中 Melee **−9.83**、Flow **−5.86**、MarkDead −1.42；
`mir1_run_f` **−26.83 ms（3/3）**，Melee −9.81、MarkDead −2.78。
**`B/A`：0.836 → 0.946（只加 F5）→ ≈0.986（再加 F2/F4）**。

⚠ **反例（诚实记录）**：`mir1_run_b`（合并 + **每 worker 一整块 125k**）**反而更差**（Melee 137.0 > 126.4）⇒
"把 run 拉长到整块"**不是**收益来源：8 个静态大块不可窃取 ⇒ 尾部均衡/空间模式都被吃掉。
这与 §11.5 的旧结论一致（`CLAIM_BLOCK` 失败剖面），**故 F5 只合并"已认领的那一块"（step=1024 tiles），不动认领几何**。

### 14.7 R2：**默认档**不回归验收（同会话 A-vs-A，`def` vs `def_run`，n=6，五段全看）

| 臂 | 整步中位 | Build | Flow | Melee | MarkDead | Integrate |
|---|---|---|---|---|---|---|
| `def`（F5 关） | **156.17** | 4.98 | 31.25 | 115.75 | 0.54 | 3.28 |
| `def_run`（+F5） | **156.14** | 5.20 | 31.35 | 116.00 | 0.55 | 3.10 |

逐对差（`def_run − def`）：−3.70 / −6.62 / −0.83 / −0.86 / **+5.19** / **+11.84** ⇒ **配对中位 −0.84 ms（4/6 更快）**。
两个正项是**整段同向漂移**（同轮里 Build/Flow/Integrate/Melee 全部抬高：+11.84 那轮的 Melee 就 +10.6 ms ——
而默认档 Melee 的 tile 数只有个位数、`step=1` ⇒ **F5 在该 job 上根本不生效**）⇒ 判为机器漂移，非回归。
**R2 判定：中性（配对中位 −0.84 ms、4/6），五段无系统回归** ⇒ 与 R1 一起满足提默认的条件。
**提为默认后复测（`final/`）**：默认档 **152.25 ms**（Build 4.99 / Flow 29.93 / Melee 113.58 / MarkDead 0.57 / Integrate 2.93），
对齐档 **168.78 ms**（Melee 123.53 / MarkDead 1.73 / Flow 33.73）——与上表同向。

### 14.8 修复后的**残余**（同会话，`mir1_run_f` vs Unity B）

| 段 | EntJoy | Unity | Δ | 说明 |
|---|---|---|---|---|
| Melee | 126.31 | 121.70 | **+4.6** | 体本身（1e6 元素 × ~4.6 ns/元素）；合并后已无每元素调用税 |
| Flow | 30.25 | 32.05 | **−1.8** | **EntJoy 反超**（thin tile + 合并后记账也省了） |
| MarkDead | 0.56 | 0.85 | **−0.29** | **EntJoy 反超** |
| Build | 4.38 | 2.70 | **+1.7** | 两侧同档 64、item 仅 3.1 万 ⇒ **体/原子**（与粒度无关，07 §7ar(k)） |
| Integrate | 3.87 | 2.50 | **+1.4** | 同上（item 仅 1.6 万） |
| **整步** | **165.5** | **~160.5** | **+5.0（+3.1%）** | **`B/A ≈ 0.99`** |

⇒ **对齐档的赤字从 F1 前的 1.54×、F1 后的 1.20×，收到 F5+F2+F4 的 1.01–1.03×**；
残余 (~3%) 已全部落到**体**上（Melee 的每元素工作 + Build/Integrate 的原子/散列），**不再有 JobSystem 侧的口子**。

### 14.9 仍未做（可选 / 超出本轮）

1. **体侧残余**：对齐档剩下的 **Melee +4.6 ms**（1e6 元素 × ~4.6 ns/元素）与 **Build +1.7 / Integrate +1.4 ms**
   （两侧同档 64、item 仅 1.6–3.1 万 ⇒ 与粒度无关）⇒ 要动内核/生成器或原子策略（例如 Build 的计数趟原子），面与风险都大。
2. **F2/F4 是否也提默认**：两者都只在细粒度有量（对齐档 −9.4 / −2.1 ms、默认档 ≈0）⇒ 目前仍 env 门控；
   若把"对齐 Unity 粒度"当**受支持模式**，建议 `F1+F5（默认）+ F2+F4` 一起开（同会话 `B/A ≈0.99`）。
3. **`ENTJOY_CLAIM_SPAN` 的 4096 上限**（`JobSystem.cpp` 里 `n > 4096 ⇒ 4096`）：做"更长跨度"实验前要先放宽，
   否则 `8192/65536` 与 `4096` 等价（本轮器械侧踩到）。F1 阈值二维扫描（`SPAN` × `kClaimSpanThinElems`，现 1024/16）仍未做。
4. **F5 只做在 General 路**：chunk/entity 路（每 chunk 回调）与 packed 路的同类"细粒度 → 每元素一次调用"没有量过，
   也没有合并；index 路（`ScheduleParallelFor`）合并的是 trampoline，托管 per-index 回调仍在。
5. **Unity 侧**：① 默认档对比里的 B 仍是**跨会话**引用（同会话要专门跑一轮 A(def) vs B）；
   ② "Unity 的 work 分配几何 = 大段连续 range"目前是**解释性假设**（我们只测到它的效果：把 run 拉长到整块反而更差）。

## 15. 同类问题的系统排查 + F5b / F6（2026-10-01 第二轮，可引用）

### 15.0 一句话

把 F5 的判据抽象出来：**"凡按工作项付的固定成本，只要工作项在回调下标空间里首尾相接、且共用一个回调，
就该合并成一次调用"**。按这条判据把框架里**所有** tile 路逐条过一遍（F5b），
再用器械复核"框架侧每工作项成本是否已归零"，最后处理**最后一处"同算法、不同效率"**：认领几何（F6）。

### 15.1 器械复核：合并 + 去物化之后，**框架的每工作项成本已归零**

`SchedTileBench`（空体、`len=10⁶` 元素、`batch=1` ⇒ **10⁶ tiles / 1 job**、8 worker、9 帧中位）：

| 配置 | ns/job | **ns/tile** |
|---|---|---|
| F1 关 + F5 关 | 7,555,100 | 7.56 |
| F5 开（F2/F4 关） | 2,498,250 | 2.50 |
| F5 关 + F2 + F4 | 1,936,550 | 1.94 |
| **F5 开 + F2 + F4** | **21,300** | **0.02** |

⇒ 10⁶ tiles 的整 job 只剩 **21 µs**（≈1030 次认领 × 每次 ~20 ns 的 `_Batch` 调用）⇒
**"每元素"的框架成本已经没有了**；对齐档剩下的全部是**体**（§14.8）。

### 15.2 F5b：把合并推广到**全部"相接 tile"的批**

| tile 路 | tile 是否首尾相接 | 处置 | 证据 |
|---|---|---|---|
| General（range/batch） | 等宽 + 相接（`{i*cs, …}`） | **F5**（算术推导；F2 时 `tiles==nullptr` 也可） | §14 |
| **chunk / entity**（`ScheduleChunks` / `ScheduleChunkRanges` / `ScheduleEntityBatches`） | **恒相接**（`tileBounds[i..i+1]`） | **F5b**：`fuseRuns`，从 `tiles[]` 取并集；**保留该路的"下一 tile 预取"**（下一段 chunk 的 `entityArray`／下一批的 `componentArrays`，那是真有用的） | 新单测 `TestChunkRunFusionCoverage` |
| packed（`SubmitPackedPlainJobs`） | 等差相接（`[i*sliceSize, …)`） | ❌ **排除**：合并后 `PackedBatchTests` 出现**批不退役**（Test1/Test3/Exports 共 6 项失败）；且收益本为零（per-tile 回调数 = O(workers×4)，与 job 数无关） | 实测（本节） |
| index（`ScheduleFor`/`ScheduleParallelFor` 的托管体） | — | 已无此问题：`NativeJobScheduler.ScheduleParallelFor` 走的是 `GetAutoParallelForCache<T>()`（**每 tile 一次托管进入、循环在托管侧**），不是每元素一次 | 源码核对 |

**验证**：① 新增 `TestChunkRunFusionCoverage`（chunk 路）——F5 关时"回调次数 == tile 数"、F5 开时"≤ tile 数/2"，
两态都要求"每个 chunk 恰好被回调一次、区间落在 `[0, chunkCount)`、计数和 == chunkCount"；
② 既有 `TestChunkRangeExactOnce` / `PackedBatchTests` / `ChunkShutdownRace` 等在两态都过；
③ 全套 **9/9 通过（`ENTJOY_TILE_RUN` 显式关 / 显式开）**。

### 15.3 最后一处"同算法、不同效率"：**认领几何**（共享 RMW 争用 ↔ 空间复用）

默认档 A-vs-A（同 DLL、只切 env、n=3，`geom/` 会话；`def` = 现状交错认领）：

| 臂 | 整步中位 | **Build** | Flow | **Melee** | Integrate |
|---|---|---|---|---|---|
| `def`（交错，现状） | 154.72 | 5.20 | 30.93 | 114.55 | 3.09 |
| `def_slc`（切片：每 worker 独占一段 + 空手才偷） | 155.79 | **3.07** | 29.79 | **118.96** | 2.99 |
| `def_blk`（每 worker 一整块，不可偷） | 178.16 | 3.31 | 32.51 | 137.52 | 3.67 |
| `def_std`（index 空间置换） | 168.52 | 5.89 | 30.90 | 128.26 | 3.17 |

⇒ **同一个静态几何对两个 job 是相反符号**：Build（计数趟 = 共享计数器 RMW 争用）**−2.13 ms**，
Melee（邻居表/cell 空间复用）**+4.41 ms**；两种"散开"（blk/std）都更差。
⇒ 与 07 §(l8) 同一条教训：**没有静态通解，几何也只能按 job 定**。

### 15.4 F6：**按 job 的认领几何**（`ENTJOY_CLAIM_ADAPT=1`，默认关 ⇒ 逐位不变）

**判据（不做探索，避免探索期自己造成损失）**：用 JCC 已有的**每元素执行成本** EWMA + 迟滞 ——
`cost < 8 ns/元素 ⇒ 切片`（时间由共享内存 RMW/每项固定成本主导 ⇒ 让同时刻的 worker 散开），
`cost > 12 ns/元素 ⇒ 交错`（由计算与空间复用主导 ⇒ 保持 worker 邻近），区间内保持现状；
**无样本**（`funcHash == 0`：对齐档表命中旁路 JCC、或 JCC 关）⇒ 回退全局 env ⇒ **对齐档天然 no-op**。

**v1（两臂 bandit + 奇偶探索）实测为负，已废弃**：探索期本身让默认档 Melee +7 ms；
且踩到一个**真 bug**：两个学习器共用 `JobCostCache::slotHash` 而键不同（JCC 用 FNV、F6 用 RVA）⇒ 互相覆盖 ⇒ 永远停在探索期
（§9 器械坑 11）。v2 改成"只读 JCC 的 perElem、不写入"，结构上不可能再撞。

**v2 实测（默认档 A-vs-A，两个会话）**：

| 会话 | 臂 | 整步中位 | Build | Flow | Melee | Integrate |
|---|---|---|---|---|---|---|
| `adapt2`（n=4） | `def` | 153.06 | 4.87 | 30.68 | 114.17 | 2.91 |
| `adapt2`（n=4） | `def_adapt` | 154.64 | **2.93** | 30.95 | 116.89 | 3.09 |
| `adapt3`（n=6） | `def` | 153.35 | 5.07 | 30.85 | 113.95 | 2.91 |
| `adapt3`（n=6） | `def_adapt` | 153.97 | **3.21** | 30.88 | 116.19 | 2.96 |
| **合并 n=10** | 配对差 | **+1.65 ms（4/10 更快）** | **−1.90（10/10）** | ≈0 | **+2.24（3/6 更高）** | +0.09 |

⇒ **Build 稳定 −1.90 ms（10/10 同号）**，但**整步层面不成立**：4/10 更快、配对中位 **+1.65 ms**
（Melee 一侧中位数被抬高 ~2.2 ms —— 疑为**跨 job 的 cache/带宽耦合**：被切片的那些低每元素成本 job
改变了整步的访问次序，而 Melee 依赖它的空间复用；本轮没有把这条耦合隔离出来）。
**判定：不给默认**（与 `guided` 同一条纪律：机制成立、整步收益不复现/为负 ⇒ 保持 env 门控）。
**故 F6 保持 `ENTJOY_CLAIM_ADAPT=1` 手动开**；要提默认需要先隔离"跨 job cache 耦合"，再做**多对同会话 + 五段全看**。

## 16. "**入口现在已经一样了**"——但要分三层说（2026-10-01 第三轮，可引用）

### 16.1 三层口径（哪些已经对齐、哪些还没有）

| 层 | 现在 | 证据 |
|---|---|---|
| **① 框架每工作项成本**（认领/物化/开关/记账） | **已归零**：空体 10⁶ tiles/1 job 的每-tile 成本 **7.56 → 0.02 ns**（F1+F5+F2+F4） | §15.1（器械） |
| **② 内核调用入口**（间接调用 + 适配器拆字段 + 58 参数封送） | **已从"每元素一次"降到"每认领一次"**：对齐档 Melee 1e6 元素 ⇒ 认领 1024 tiles/步 ⇒ **~977 次/步**（原来 10⁶ 次，−99.9%）；每次 ~11–20 ns ⇒ **整步 ~11–20 µs（占 Melee 121 ms 的 0.01%）** | F5（§14）+ 器械 21.3 µs/10⁶ tiles |
| **③ 体内每元素** | **仍不一样**：生成的 `_Batch` 体在 **for 循环体内**重新物化分量指针（循环不变量），见 §16.2 | 生成源码扫描 |

**最干净的对照**（纯入口主导的 job，同会话配对）：**MarkDead** 在 F5+F2+F4 下 **0.56 ns/元素**，
**比 Unity 的 0.85 ns/元素还快** ⇒ "入口"这一项已经不是赤字。

### 16.2 残差在"体内"：环内指针重物化 = **寄存器压力**

扫描本构建的全部 `SharpNative_Job_*_Execute.cpp`：**位于 `for (index…)` 环体内**的
`= ((T*)X_ptr)` 分量指针物化次数（循环不变量，本应常驻寄存器）：

| kernel | 环内指针物化 | 实测每元素（对齐档） | 说明 |
|---|---|---|---|
| **MeleeSimJob** | **29** | **126.3 ns vs Unity 121.7（+4.6 ns/元素 ≈ +4.6 ms）** | 29 个活值 > x64 的 16 个通用寄存器 ⇒ 每元素从入参栈块重载 |
| IntegrateJob | 18 | 3.87 vs 2.50 ms（**+1.4**） | 同上（item 数只有 1.6 万 ⇒ 影响体现在"每元素"） |
| SpawnJob | 16 | — | 同上 |
| **MarkDeadJob** | **7** | **0.56 vs 0.85 ns/元素（比 Unity 快）** | **7 个活值留得住 ⇒ 每元素几乎零代价** |
| CountCellsJob / PlaceCellsJob / FlowPresenceJob / ClearAllJob / … | **0** | Build 仍 +1.7 / Integrate… | **生成器本来就会把指针物化提到环外** ⇒ 说明"提到环外"是它已具备的能力 |

⇒ 同一条生成器对**不同 kernel 产出不同形状**：环内指针少的（MarkDead 7）已经不吃亏；
环内指针多的（Melee 29）吃 **+4.6 ms**，量级与"29 个循环不变量被溢出重载"一致
（4.6 ns/元素 ≈ 17 周期 @3.8 GHz；**这是量级吻合的推断，未用汇编逐条核对**）。

### 16.3 下一步（两条，均未做）

1. **生成器**：把环内的分量指针物化统一提到环外（照 CountCells/PlaceCells/Presence 那批的现成形状）。
   ⚠ 预期收益有限：提到环外并不能把 29 个活值塞进 16 个寄存器 ⇒ 大概率只是把"从入参栈块重载"换成"从自己的栈帧重载"。
   **真正的通解**是降低"每个内层循环的活值数"（把 `_Batch` 的 58 个参数做成**一个 params 结构体**，
   让寻址走 `p->xxx` / 基址+位移），或按相把 Melee 体拆成若干段。面大、要重推 RVA 表。
2. **Unity 侧口径补齐**：把"Unity 的 worker 拿到的是**大段连续 range**"从假设变成观测（我们只测到它的效果：
   `CLAIM_BLOCK` 把 run 拉长到整块反而更差；`CLAIM_SLICE` 对 Melee +4.41 ms）。

### 16.4 一句话回答"入口一样吗"

**"每个元素一次入口"这件事已经没有了**（框架每元素成本 0.02 ns/tile；纯入口主导的 MarkDead 已快于 Unity）；
**与 Unity 剩下的差别在"体内"**——我们的生成代码把 29（Melee）/18（Integrate）/7（MarkDead）个**循环不变的**
分量指针留在环内重新物化，其中 ≤7 个时编译器能常驻寄存器（MarkDead 无损失），29 个时不能（Melee +4.6 ms）。

> ⛔ **§16.2 / §16.3 的归因与建议已被 §17 的机器码复核推翻**（B 栈同样是环内重物化，且每元素指令数更多）。
> 本节只保留①（框架成本归零）与②（入口已按认领聚合）两条结论。

## 17. 机器码复核：**B 栈（Burst）也是环内重物化**，§16 的"提到环外"建议不成立（2026-10-01 第四轮，可引用）

### 17.0 一句话

把两条实现都反汇编之后：**Burst 的 Melee 热循环同样逐元素重新读 job 结构体字段、并把 342 条帧访存/163 条帧写塞进 1418 条循环体**，
且它**每元素执行的指令总数比我们更多**（因为它把 ORCA 全部外提成 6 次调用而不内联）。
⇒ "把环内指针提到环外""降低每个内层循环的活值数"**既不是 Unity 的做法，也不是本残差的成因**；**§16.3-1 撤销**。

### 17.1 器械：本机**其实有** Burst 机器码（此前一直记为"没有留存"）

| 步 | 做法 | 本机实测 |
|---|---|---|
| 1. 名字 ↔ hash | `Build\W0Player\TestProject_BurstDebugInformation_DoNotShip\Data\Plugins\x86_64\lib_burst_generated.txt`，行格式 `--method=<签名>--<hash>` | `Bb0M2MeleeJob` → `d939b190ba9267a19134ea0b50a85224`；`Bb0M0IntegrateJob` → `771ee5bf53a1419fe43e96c042a763fd`；`Bb0M2MarkDeadJob` → `47550e7aa1788eb7c60bd1faf948b5b3` |
| 2. hash ↔ 目标文件 | `Library\BurstCache\{JIT,Windows-Intel}\Hashes\Objects\*.obj`（**1509 + 832** 个）。⚠ **obj 里不含方法名**（COMDAT 符号就是 hash）⇒ 只能 `findstr /m /c:"<hash>" *.obj`（全量扫 ≈ 47 s） | 出货档（W0Player）= `Windows-Intel\...\77d0f51ef3e917b363b764a88b7f600b.obj`（09/16 22:10:10，与同批 `W0Player_Data\Plugins\x86_64\lib_burst_generated.dll` 22:10:58 一致）。`JIT\...\dab90e8a….obj` 只是 `burst.debug_query.<hash>` 存根（.text 仅 476 B） |
| 3. 出货 dll | `llvm-objdump -t` 在出货 dll 上**只有 3 行**（已 strip）⇒ **改走 obj** | 见上 |
| 4. 反汇编 | `llvm-objdump -d --demangle --no-show-raw-insn`；**必须再加 `-r`**，否则 `callq` 目标显示成"下一条指令地址"，看不出被调用者 | `callq 0x605` → `IMAGE_REL_AMD64_REL32 …BuildObstacleLines…` |
| 5. 热点/访存统计 | `tools/gate-run/analyze-loops.ps1`（回边⇒热循环 + 栈访存/调用/基址直方图）、`tools/gate-run/loop-loads.ps1`（非索引/索引访存拆分） | 见 §17.2 |

⚠ **器械坑（补 §9）**：① llvm-objdump 用 **TAB** 分隔列，`-replace '[^\x20-\x7E]','.'` 会把 TAB 变成 `.`（`je` → `.je.0x1d6a`），
所有跳转正则静默失效、`back_edges=0`；必须**先把 TAB 换成空格**再过滤不可打印字符。
② PS 里 `$Lo` 与 `$lo` 是**同一个变量**（大小写不敏感）⇒ 把 `$Lo` 数值化后再拼字符串会打出 `0x424` 而不是 `0x1a8`（本会话第四次踩同一类坑）。

### 17.2 两条实现的每元素热循环（同一算法内容；对齐档元素数 1e6）

| | **Unity** `Bb0M2MeleeJob`（hash d939b190…） | **EntJoy** `MeleeSimJob_Execute_Batch` |
|---|---|---|
| 函数内指令总数 | **1526** | 4554 |
| **每元素循环体指令数** | **1418**（0x1a8 → 回边 0x1cd1） | **3224**（0x160 → 回边 0x3f95） |
| 循环内**帧访存**指令 | **342**（`%rbp` 267 + `%rsp` 75）= **24.1%** | **531**（几乎全 `%rsp` 528）= **16.5%** |
| 循环内**帧写** | **163** | 87 |
| 循环内**非索引**访存（指针/表读） | 507（`%rbp` 267、**`%r10` 88**、`%rsp` 75、`%rip` 48） | 858（`%rsp` 528、`%rip` 237、`%rax` 67） |
| 循环内**索引**访存（数据读） | 46 | 119 |
| 循环内 `callq` | **6（不内联）** | **0（全内联）** |
| opcode 构成 | `movq 219 / movl 141 / movaps 123 / movss 103 / mulss 75` | `movq 286 / movl 245 / vmovss 215 / vucomiss 192 / vmulss 141` |

Unity 环内 6 次调用的目标（按 `IMAGE_REL_AMD64_REL32` 逐条解出）：
`BuildObstacleLines`、`FlowGoalDirection`×2、`SteerAroundWalls`、`ApplyWallRepulsion`、`Solve`
（`Solve` 内还调 `BuildSingleOrcaLine`、`RelaxLineConstraints`）。这些助手在本 obj 内的体量：
`ApplyWallRepulsion` 330 / `BuildObstacleLines` 324 / `BuildSingleOrcaLine` 495 / `FlowGoalDirection` 383 /
`RelaxLineConstraints` 325 / `Solve` 524 / `SteerAroundWalls` 299 条。
⇒ **Unity 每元素实际执行 ≈ 1418 + 最多约 2600 条，比 EntJoy 全内联的 3224 条只多不少。**

### 17.3 判定（三条，均以 §17.2 数据为据）

1. **"把不变量提到环外"不是 Unity 的做法。** Burst 的 job 结构体指针在帧里（0x177 `movq 0x258(%rbp), %r10`），
   字段以 `0x40(%r10)` / `0x10(%r10)` / `0x20(%r10)` 这类**基址+位移逐元素读取**（环内 88 条 `%r10` 非索引读），
   并把 xmm6–15 溢出到帧。**Burst 的活值压力比我们更大**，它照样快 3.6%（126.3 vs 121.7 ns/元素）。
2. **EntJoy 的机器码没有"漏做 LICM"。** 编译器**已经**把 8 个入参指针提到环外常驻寄存器
   （0xc7–0x107 setup：`%rax,%rsi,%rdx,%r8,%r10,%r14,%rdi,%r15`），并把 `+4/+8/+0xc` 的派生指针**预先算好存帧**
   （0x107/0x113/0x11f）；环内剩下的重载是**寄存器压力下的主动选择**（3224 条体的活值 > 15 个 GPR），
   而且按指令密度算**比 Unity 更低**（非索引访存/指令：EntJoy 0.27 vs Unity 0.36）。
3. ⇒ **§16.2 的"29 个环内指针物化 ⇒ +4.6 ms"不成立**（§16.2 末已自标"未用汇编逐条核对"，逐条核对后**推翻**）；
   §16.3 的两条建议（统一提到环外 / 降低每个内层循环活值数）**都没有依据**，其中"降低活值"还与我们刚测到的
   "Burst 活值更多却更快"相反。**剩下 ~1.4–5% 的同算法差异，目前没有任何机器码证据指向某条生成器缺陷。**

### 17.4 下一步（唯一有依据的一条）

**对称相位探针**：给 Unity 的 `Bb0M2MeleeJob` 与 EntJoy 的 Melee 体**对称地**打 rdtsc 相位计数
（障碍线构建 / 邻居网格扫描 / ORCA / 写回，四相各自 ns/元素），同会话对照，
把 +4.6 ms **落到具体相位**上。只有出现"**同一相位、我们的每元素机器码更差**"才动生成器；
否则就是算法内容/访存模式的差异，不该再用通解改造去追。

**明确不做**：① 逐 job 白名单式的形状改造（违反通解约束）；② "内联↔不内联"形状 A/B —— §17.2 给的是**否定性先验**
（Unity 外提了 6 次调用、执行指令更多，仍然更快 ⇒ 内联不是赤字来源），故列为**最低优先级**，不是禁止。

## 18. 终局状态：已做优化清单 / **同会话**最终对比 / 接下来的方向（2026-10-01 第五轮，可引用）

### 18.0 一句话

出厂默认（F1 + F5 + F5b）**同会话 3 对**：EntJoy **161.92 ms** vs Unity **165.29 ms ⇒ EntJoy 快 2.0%**
（Melee/Flow/MarkDead 三段反超，仅 Build +2.32 / Integrate +0.66 落后）；
对齐档（Unity 确定档 + F5 + F2 + F4）**168.31 vs 165.81 ⇒ 慢 1.5%（3/3）**。
**本次两块 Unity B 都是同会话、相位对齐的配对值**（取代 §13.2/§13.5 的跨会话引用）。

### 18.1 器械与前置核验（可复做）

- 驱动：`tools/gate-run/ab-tile-run.ps1 -FinalProbe -Reps 3 -OutDir tools\gate-run\jobbatchtbl\final3`
  （新臂对 **`def`** = 出厂默认：不发表、不设 `ENTJOY_TILE_RUN` ⇒ 走默认开；**`mir1_run_f`** = 表 + `ENTJOY_TILE_RUN=1` + `TILES_UNIFORM=1` + `TILE_FASTPATH=1`。
  每个 A 跑完**立刻**跑一个相位对齐的 Unity B：B 的 warmup 由该 A 的 `[M-1]` 窗口起点决定）。
- 原始档：`tools/gate-run/jobbatchtbl/final3/final3.csv`（每臂另有 `.stdout.txt` / `.log` / B 的 `.csv`）。
- **前置核验**：运行时 DLL `.godot\mono\temp\bin\Debug\NativeDll.dll` 与构建产物 `NativeTranspiler_Generated\build\Release\NativeDll.dll`
  **SHA256 同哈希**（`4EAF29ACBAAED0ED…`），且 `src/NativeDll` 全部源文件 mtime < 构建时间（19:33:39）⇒ 测的就是当前源码。

| rep | 臂（轮转顺序） | A 整步 | B 整步 | `B/A` | B−A |
|---|---|---|---|---|---|
| 1 | def → mir1_run_f | **172.99** / 168.31 | 163.91 / 165.81 | 0.948 / 0.985 | −9.08 / −2.50 |
| 2 | mir1_run_f → def | 168.59 / **161.92** | 166.83 / 165.29 | 0.990 / 1.021 | −1.76 / +3.37 |
| 3 | def → mir1_run_f | **157.89** / 167.14 | 166.93 / 164.23 | 1.057 / 0.983 | +9.04 / −2.91 |
| — | **配对中位** | — | — | — | def **+3.37**；mir1_run_f **−2.50** |

### 18.2 终局对比（臂中位 + 分段）

**（a）出厂默认 `def`（F1+F5+F5b，`applied=0` = 不用表，符合预期）**

| 段 | EntJoy（中位） | Unity（中位） | A−B | 谁快 |
|---|---|---|---|---|
| Melee | **121.51** | 125.67 | **−4.16** | **EntJoy 快** |
| Flow | 31.54 | 32.84 | −1.30 | EntJoy 快 |
| MarkDead | 0.58 | 0.87 | −0.29 | EntJoy 快 1.5× |
| **Build** | **5.14** | **2.82** | **+2.32** | **EntJoy 慢（最大赤字）** |
| Integrate | 3.25 | 2.59 | +0.66 | EntJoy 慢 |
| **整步** | **161.92** | **165.29** | **−3.37** | **EntJoy 快 2.0%** |

**（b）对齐档 + F2 + F4 `mir1_run_f`（表=Unity 确定档；`applied=15/15` 逐次自证）**

| 段 | EntJoy（中位） | Unity（中位） | A−B | 谁快 |
|---|---|---|---|---|
| Melee | 127.55 | 125.54 | **+2.01** | Unity 快 |
| Flow | 30.71 | 33.07 | −2.36 | EntJoy 快 |
| MarkDead | 0.56 | 0.85 | −0.29 | EntJoy 快 |
| Build | 4.46 | 2.90 | +1.56 | Unity 快 |
| Integrate | 3.93 | 2.56 | +1.37 | Unity 快 |
| **整步** | **168.31** | **165.81** | **+2.50** | **Unity 快 1.5%（3/3）** |

**⚠ 本次会话新读出的两条（比"比值"本身更重要）**
1. **"镜像 Unity 粒度"对 EntJoy 是净代价、对 Unity 无所谓**：同一个 Melee，EntJoy 在默认档 **121.51**、
   在镜像档 **127.55**（**+6.04 ms**）；而 Unity 两臂实测 125.67 / 125.54（同一二进制同一配置，差异只是会话漂移）。
   ⇒ **对齐档不是"公平的同等粒度竞技场"，是专门惩罚我们代码形状的保守档**（表是按 Unity 的 job 结构体访存形状定出来的）。
   它仍然是**有用的诊断档**，但不该被当成优化目标。
2. **稳定性差别**：对齐档 `B/A` 3/3 落在 0.983–0.990（窄）；默认档 0.948–1.057（宽，rep1 首跑 172.99 是冷启动离群）。
   ⇒ 只有**配对中位 + 同号计数**可引用；单跑值不可引用。

### 18.3 已做优化清单（当前二进制里到底有什么）

**A. 框架侧（JobSystem，通解）**

| 项 | 状态 | 量级 | 证据 |
|---|---|---|---|
| **F1 元素跨度认领**（`g_claimSpanElems=1024`，仅薄 tile `itemsPerTile ≤ 16` 抬高；厚 tile 仍 cap=4） | **默认开** | 对齐档 **248.3 → 195.4 ms（−53）**；默认档不变 | §12.3（双验收） |
| **F1b** guided 的收缩 clamp 改用 `capEff` | 修在内 | 修前 242 vs 修后 196（对齐档） | §12.9 |
| **F5 连续等宽 tile 合并**（General 路） | **默认开** | 对齐档 **−18.66 ms（3/3）**，`B/A` 0.836→0.946；默认档 −0.84（4/6）、五段无回归 | §14.5–14.7 |
| **F5b** 同判据推广到 chunk/entity 路（packed 路排除） | **默认开**（同 F5 开关） | 全套原生测试 9/9（F5 显式关/开两态） | §15.2 |
| F2 去 O(T) tile 物化（`ENTJOY_TILES_UNIFORM`） | env 门控（默认关） | 对齐档 **−9.4 ms**；默认档 ≈0.06 ms/步 | §12.6 |
| F4 每-tile 固定开销提到每批/令牌（`ENTJOY_TILE_FASTPATH`） | env 门控（默认关） | 对齐档 **−2.1 ms**；默认档 ≈0.007 ms/步 | §12.8 |
| F6 按 job 认领几何（`ENTJOY_CLAIM_ADAPT`） | env 门控（**未提默认**） | Build **−1.90 ms（10/10）** 但整步配对中位 **+1.65 ms**（4/10 更快） | §15.4 |
| F3 标量打包（`ENTJOY_PACK_SCALARS`） | 关（**定案：非瓶颈**） | 两口径中性 | §12.7.1 |
| guided 收缩（`ENTJOY_CLAIM_GUIDED`） | 默认关 | 两会话矛盾（−4.36/5-6 ↔ +1.45/1-4） | §12.9 |

**B. 调度/生成器（更早几轮，已进默认）**

| 项 | 量级 | 证据 |
|---|---|---|
| 执行默认粒度 `tpw` 4 → 64（并配套：空/超轻 job 专用粒度上限 4、`kMaxAdaptiveTpw = max(16, 配置 tpw)`） | A 自身 **−4.88 ms/步**；空 job 微基准回到旧值；公式完善 7/8 对 −1.63 ms | doc 07 §(k3) / §(l1) / §(l2) |
| 生成器**助手头内联**（`static inline` 进 `.h`） | A 自身 **−4.87 ms/步**；6 处 `callq` 归零；门禁 14/14 | doc 07 §7y |

**C. 已被证伪/撤销（不要再走一遍）**

| 方向 | 为什么撤销 |
|---|---|
| 认领块 + 块内可窃取 | 上限 **0.08%**（反汇编 + 显微测量 + 真实负载预算） | doc 07 §(j)/(k) |
| **把环内指针"提到环外" / 降低内层循环活值数** | §16.2 的源码级归因被 §17 机器码复核**推翻**（B 栈同样环内重物化、执行指令更多反而更快） |
| F6 v1（两臂 bandit + 奇偶探索） | 默认档 Melee **+7 ms**；且两学习器共用 `JobCostCache::slotHash` 互覆（§9 坑 11） |
| `const T&` 值绑定 / 无符号模拟等生成码改动 | 汇编级否证（无符号模拟零额外指令；值绑定更差） | doc 07 §7ao |

### 18.4 接下来的方向（按证据强度排序，含判据）

| # | 方向 | 为什么是它（证据） | 判据/卡点 |
|---|---|---|---|
| **1** | **Build 段**（默认档最大赤字 **+2.32 ms，3/3 稳定**） | doc 07 §(l3)/§7ar(m,n)：Build 是"短体 + 每元素原子/op 密集（**指令受限**）"，**对粒度不敏感**，剩下的量在**认领形态**（切片 −1.21 同粒度）；F6 的自动判据已稳定拿到 Build **−1.90（10/10）** | **卡点=整步复现不了**（疑跨 job cache/带宽耦合未隔离）⇒ 下一步是**隔离该耦合**（观察 Build 趟与 Melee 趟之间的 cache/时间线），不是再调 F6 阈值 |
| **2** | **对齐档残余**（Melee +2.01 / Build +1.56 / Integrate +1.37） | 唯一手段是 **§17.4 对称相位探针**（障碍线/网格扫描/ORCA/写回 四相 ns/元素） | 判据：出现"同一相位、我们每元素机器码更差"才动生成器；否则不动 |
| **3** | **F2/F4 提默认** | 纯"补验收"：默认档近零收益，价值只在"把对齐 Unity 粒度当**受支持模式**"（对齐档 −11.5 ms） | 判据：R2（默认档五段不回归）+ 保留 `=0` 回退 |
| **4** | 生成器形状（内联 ↔ 不内联） | 无正面证据（§17 否定性先验） | 最低优先级 |
| **5** | Unity 侧口径 | 本会话已补齐**同会话 B**；剩下"Unity worker 拿大段连续 range"仍是**假设**（只测到效果） | 需观测而非推断 |

**明确不做**：① 逐 job 白名单 / 按 job 硬编码档位（违反通解约束）；② 把 F3 或 guided 当性能手段；
③ 在拿到相位归属证据之前再改**生成形状**；④ 把对齐档当优化目标（§18.2 已证它是惩罚档）。

## 19. 相位归属实测：**对齐档 Melee +2.0 ms 落在"邻居表访存"两条路上**（2026-10-01 第六轮，可引用）

### 19.0 一句话

用**两侧同义**的位掩码消融（A `CPUBATTLE_AB_MELEEARM` 单臂固定 × B `M2_ARM`，相位钉死）跑当前二进制：
**骨架（含控制流）1.01×持平**，残差集中在 **位置载入+d² = 1.91×** 与 **K 插入体 = 1.44×**；
**建线反过来 A 便宜（0.66×）**。⇒ 剩余差距是**邻居表访存的形状**，不是控制流、也不是生成码指令数。

### 19.1 协议与对照自证

- 驱动：`tools/gate-run/ab-melee-arms3.ps1 -Frame mir`（**新写**；旧 `ab-melee-arms2.ps1` 是默认档 + 15 worker）。
  A = 对齐档（表 + `TILE_RUN` + `TILES_UNIFORM` + `TILE_FASTPATH`）+ `CPUBATTLE_AB_MELEEARM=<arm>`；
  B = `M2_ARM=<arm>`，warmup/steps **按该 A 那一臂自己的 window** 定（`M4_WARMUP = lastStart − 61`，`M4_STEPS = 该 window 步数`）。
- 原始档：`tools/gate-run/melee-arms3b/`（含每臂 A stdout/log 与 B csv/log）、汇总 `melee-arms3b-mir.csv`。
- ⚠ **器械坑（补 §9，本次第二次踩同类）**：`ab-tile-run.ps1` 里窗口步数是从**整行**解析的
  （`$t -split "`n" | Where-Object {...}`），而外层正则 `\[M-1\][^\r\n]*Integrate=[0-9.]+` 的匹配**止于 `Integrate=` 的数字**；
  照抄成 `$_.Value` 会得到 `lastCount=0 ⇒ M4_STEPS=0`，B 于是**跑在未对齐的窗口**上（首轮 A/B 差 +23 ms 的假象）。
  另：`$A` 与 `$a` 在 PS 里是**同一个变量**（本会话第五次踩大小写坑）。
- **对照自证**：`arm 0`（全量）测得 **A 128.13 / B 126.90（Δ +1.23）**，与 §18.2(b) 独立会话的 **+2.01** 同号同量级
  ⇒ 协议复现了头条残差；且 9 臂 `applied=15/15`。

### 19.2 臂值（Melee ms/步，对齐档，每臂各自相位对齐）

| arm | 0 全量 | 1 −ORCA | 3 +−建线 | 7 +−扫描 | 59 仅骨架 | 27 +位置d² | 211 +K门控 | 83 +K插入 | 67 +索敌 |
|---|---|---|---|---|---|---|---|---|---|
| **A** | 128.13 | 98.24 | 84.64 | 11.90 | 34.91 | 60.67 | 79.70 | 128.35 | 113.19 |
| **B** | 126.90 | 102.93 | 82.34 | 15.69 | 38.56 | 52.08 | 67.64 | 101.53 | 106.38 |
| A−B | +1.23 | −4.69 | +2.30 | −3.79 | −3.65 | +8.59 | +12.06 | +26.82 | +6.81 |

### 19.3 子相分解（相邻臂差）

| 子相（臂差） | A | B | A−B | **A/B** | 判读 |
|---|---|---|---|---|---|
| 骨架（含 81 格环扫控制流）59−7 | 23.01 | 22.87 | +0.14 | **1.01** | **持平 ⇒ 控制流/FSM 不是赤字的来源** |
| **位置载入 + d²** 27−59 | 25.76 | 13.52 | +12.24 | **1.91** | **A 贵近 2×（邻居表读）** |
| K 门控 211−27 | 19.03 | 15.56 | +3.47 | 1.22 | A 略贵 |
| **K 插入体** 83−211 | 48.65 | 33.89 | +14.76 | **1.44** | **A 贵 44%（邻居表写/移位）** |
| 索敌 67−83 | −15.16 | 4.85 | −20.01 | **不可用** | 负值 ⇒ 轨迹已分叉（§7x(h) 同一坑） |
| 邻居扫描总 3−7 | 72.74 | 66.65 | +6.09 | 1.09 | 总量接近 |
| ORCA 求解 0−1 | 29.89 | 23.97 | +5.92 | 1.25 | A 略贵 |
| **建线** 1−3 | 13.60 | 20.59 | −6.99 | **0.66** | **A 便宜 34%** |

⚠ **读数纪律**：① 子相**不可相加**（每个差都是"不同配置下的整轮差"，缓存/相位效应重叠；
8 个子相之和不等于 arm 0 的 +1.23）；② 索敌那格不可用；③ 每臂 A 的 window 步数 29–40 不等（重臂更少），
但 A/B **逐步对齐**（B_steps == A_steps，见 `melee-arms3b-mir.csv`）。

### 19.4 结论（本轮的"改哪个循环"）

1. **不要动控制流/骨架**（1.01×，已持平）；**不要动生成形状**（§17 已否证）。
2. 要动的是**邻居表的两条路**：位置载入+d²（1.91×）与 K 插入体（1.44×）——
   两者都是"按 `SortedIndex`/`CellStart` 间接取邻居位置"的访存形态。
3. 与 2026-09-27 的**同名子相**对照（当时：默认档、15 worker、旧二进制；`07 §7x(i)`）：
   那时 **K 插入体是持平的**（A 23.97 / B 23.70）。现在对齐档下是 **1.44×** ⇒ 需要"帧 vs 体"的判决实验，见 §19.5。

### 19.5 ⭐ **帧 vs 体**判决：同一套消融在**出厂默认档**重跑一次（`-Frame def`）

| 子相（臂差） | **对齐档** A/B | **默认档** A/B | 判决 |
|---|---|---|---|
| 骨架 59−7 | 1.01 | 0.96 | **两帧都持平** |
| **位置载入 + d²** 27−59 | **1.91** | **1.87** | ⭐ **与帧无关 ⇒ 真·体侧赤字（唯一一个既大又稳的）** |
| K 门控 211−27 | 1.22 | 1.31 | 与帧无关（A 贵 ~1.2–1.3×） |
| K 插入体 83−211 | 1.44 | 1.24 | 部分与帧有关 |
| 邻居扫描总 3−7 | 1.09 | 0.90 | **与帧有关（符号翻转）** |
| ORCA 求解 0−1 | 1.25 | 1.07 | 与帧有关 |
| 建线 1−3 | 0.66 | 0.81 | **两帧都 A 便宜** |
| **arm 0（全量）** | A 128.13 / B 126.90（**+1.23**） | A 115.75 / B 127.35（**−11.60**） | 默认档 A 更快（复现 §18.2a 方向） |

- 原始档：`tools/gate-run/melee-arms3-def/`（`applied=0` ⇒ 确认是无表默认档）。
- **判决（推翻 §19.4 里"更像来自帧"的猜想）**：`位置载入 + d²` 在**两帧都是 ~1.9×、绝对差都是 ~12.3 ms**，
  ⇒ **它是体侧的、与认领几何无关的赤字，是当前最大且最稳的一处**。
  `扫描总` 才是随帧翻符号的那一项（对齐档 1.09× / 默认档 0.90×）。
- **工等价已核（源码级，非推断）**：这一相两侧是**逐行同形**的
  ——A `CPUBattleCombat.cs:267-270` 与 B `BattleBenchM2.cs:1368-1371` 都是
  `q = pos[i]; dx = p.x−q.x; dy = p.y−q.y; d2 = dx*dx+dy*dy;`
  （B 侧 `Bb0Align.Buckets = false` 是**编译期常量**，Burst 会消除全部计数器 ⇒ **B 的相位测量是干净的**）。
  ⇒ **工作量相同、效率差 1.9×**，不是"两边算法不一样"。
- **尚未定位**：为什么"读 `pos[i]` + 4 次浮点"这 2 访存 4 flop 的序列 A 要 1.83×。
  候选方向（都未证）：① `SortedIndex`→`Positions` 两条依赖载入的**布局/步长**差异；
  ② 该内层循环的**边界检查/预取**代码形状（对齐档两侧循环体指令 3224 vs 1418，§17.2）；
  ③ B 的 `pos` 与其 chunk 布局的局部性。**下一步应只针对这一个内层循环做机器码对照**（两侧都已可反汇编）。

**⇒ 因此 §20 的问题要改口径**：**认领几何修不了这 1.9×**（它跨帧不变）；认领几何能修的是
"扫描总/ORCA/K 插入体"这些**对帧敏感**的项——也就是"帧"这一层。

### 19.6 体侧线收尾：两侧内层扫描体**机器码结构同形**（2026-10-01）

用新器械 `tools/gate-run/find-scan-loop.ps1`（按 `i = sorted[s]; q = pos[i]` 的**依赖索引载入对**定位）
在两侧机器码里找到同一个候选循环体：

| | A（`MeleeSimJob_Execute_Batch`，命中 @0x24d0） | Unity（`d939b190…_x64_sse2`，命中 @0xc26） |
|---|---|---|
| sorted→i | `movslq (%r10,%rdx,4), %rdx` | `movslq (%r8,%rax,4), %rax` |
| cfgId[i] | `movslq (%r14,%rdx,4), %r8` | `movslq (%r8,%rax,4), %r8` |
| 84 B 步长 | `imulq $0x54, %r8, %r8` | `imulq $0x54, %r8, %r8` |
| cfg 字段聚集 | `vmovss 0x30(%r11,%r8)` | `movss 0x30(%r12,%r8)` |
| pos 载入 | `vmovsd (%r9,%rdx,8)` ×2 | `movsd (%r15,%rax,8)` + `movsd (%r11,%rax,8)` |
| d² | `vsubps`×2 + `vmulps` + `vaddss` | `subps`×2 + `mulps` + `shufps` + `addps` |
| 循环体指令（该段） | **18 条** | **22 条** |

⇒ **两侧同形，A 的指令数还略少**（两侧都做了同样的"84 字节步长 cfg 聚集"投机提升——Burst 也把它提到了半径门之前）。
**⇒ 内层循环本身没有可指的代码质量差。** §19.5 的 1.9× 因此只能来自两处：
① 消融法的**二阶效应**（从热循环里拿走 4 行会改变寄存器分配与代码形状，差值不是"这 4 行的纯代价"）；
② **候选量（工）差异**——即 A 在该相扫到的候选数比 B 多。
要钉死只能靠**候选量计数**：B 侧已有现成锚（`Bb0Align.Buckets`，`const bool = false` 编译期关断，
`bc1`=格/`bc2`=载入+d²/`bc3`=邻近门/`bc4`=alpha 门），打开 + 重建玩家档即可导出；A 侧需要加同义计数器（**未做**）。

## 20. 认领几何能不能统一成通解？（2026-10-01 第六轮，可引用）

### 20.0 一句话

**"一个固定的统一几何"不行**（我们自己的 A-vs-A 探针就测出同一几何对两个 job **反号**）；
**Unity 也不是统一几何**——它是**统一机制（range 级 work stealing）+ 每个 job 一个常量 `batchSize`**（机器码与官方文档双向证实）；
而"按工作量自动给常量"这条规则**我们早就实现了**（F6），它在整步层面不兑现
⇒ 缺的不是"更聪明的几何公式"，而是**信号类型**：**身份/成本 → 运行时状态**。

### 20.1 Unity 的认领：机制 + 参数（机器码 + 官方文档，双向证实）

- **机器码**（§17.1 器械，`Bb0M2MeleeJob` = hash `d939b190…`）：range 循环（0x140–0x17e）**每条 range** 都
  `callq *JobsUtility::GetWorkStealingRange_Ptr`（prologue 0x105 从 GOT 取指针存 `0xd8(%rbp)`；
  两个出参是 `0x1f0/0x1ec(%rbp)`），range 内部是**逐元素顺序**循环。
  ⇒ **认领单位 = range，由原生调度器按 work stealing 发放**；我们此前"Unity 拿到大段连续 range"的**假设**，到此变成**机制已证**。
- **官方文档**（`Unity.Collections / IJobParallelForDefer.cs` 注释，2026-10-01 取）：
  - "Unity automatically splits the work into **chunks of no less than the provided batchSize**, and schedules an appropriate
    number of jobs based on the **number of worker threads, the length of the array and the batch size**."
  - "**IJobParallelFor performs work stealing using atomic operations.** Batch sizes can be small but they aren't free."
  - 选参规则："a simple job … a batch size of **32 to 128**; if the work performed is very expensive … **a batch size of 1**."
- **我们的实测**（§3/§4 确定档）：B 侧 15 个 job 里 **4 个 = 64、11 个 = 1**（`innerloopBatchCount == 0 ⇒ 1`）
  ⇒ Unity 的粒度是**"每个 job 由作者给一个常量"**，**不是**自适应、也不是全局统一值。

### 20.2 其他项目：通解都建在"运行时状态"上，没有一家建在"job 身份"上

| 系统 | 认领单位 | 粒度由什么决定 | 信号 |
|---|---|---|---|
| **Unity Jobs** | range（≥ batchSize），原子 work stealing | **job 作者给的常量** | 无（静态） |
| OpenMP `schedule(dynamic,chunk)` | chunk | 常量 | 无 |
| OpenMP `schedule(guided[,chunk])` | chunk | **随剩余量递减**（≈ 剩余/线程数） | **进度** |
| GSS（Polychronopoulos & Kuck, 1987） | chunk | 递减 chunk 的鼻祖 | 进度 |
| **TBB `auto_partitioner`** | subrange | 初始只切 S（∝线程数）份；**"Each of these subranges is not divided further unless it is stolen by an idle thread"** | **窃取事件** |
| Rayon | adaptive split | 按需细分（steal-driven） | 窃取事件 |
| SLAW（IPDPS'10） | chunk | **限制偷取** + locality-aware victim 选择 | 窃取 + **局部性** |
| Cost-Aware WS（ACM'24） | chunk | 均匀体用 cyclic，不均匀体按代价 | 代价 |

⇒ **没有一家按 job 名/job 类型选几何**；它们按 **进度 / 窃取 / 局部性 / 争用**选。
我们用"每元素成本"（F6）本质是**用身份代理**，这是与文献最关键的一处差别。

### 20.3 我们自己的实测：为什么固定几何不行、为什么 F6 不兑现

**(a) 固定几何不行**（§15.3，同 DLL 只切 env、默认档、n=3）：

| 臂 | 整步 | **Build** | **Melee** |
|---|---|---|---|
| `def`（交错，现状） | 154.72 | 5.20 | 114.55 |
| `def_slc`（切片：独占一段 + 空手才偷） | 155.79 | **3.07（−2.13）** | **118.96（+4.41）** |
| `def_blk`（整块不可偷） | 178.16 | 3.31 | **137.52（+23）** |
| `def_std`（index 空间置换） | 168.52 | 5.89 | 128.26 |

⇒ **同一几何对两个 job 反号**；两种"散开"都更差、完全不可偷更差。

**(b) 机制解释（都有实测支撑，不是推断）**：Build 的赤字主因是**共享 RMW 争用**
（doc 07：Count 2.19 ms 中 `lock xadd` 争用 **1.25**；去掉原子后 0.92 且**与粒度无关**）；
Melee 是**邻居网格的空间复用**（interleaved 让 8 个 worker 同时扫同一片区域 ⇒ 共享 cache line / L2 命中）。
这正是 SLAW 所说的"load balance vs locality"的经典冲突 —— **它不是"选错了一个几何"，是两种作业要相反方向**。

**(c) F6（自动版"按工作量给常量"）**：用 JCC 每元素成本分类（<8 ns ⇒ 切片，>12 ns ⇒ 交错）——
Build **−1.90 ms（10/10）**，但**整步配对中位 +1.65 ms（4/10 更快）**。
⇒ 机制生效、**目标函数错**：每元素成本不是整步墙钟的单调代理（跨 job 耦合未隔离，§15.4）。

### 20.4 结论：能统一的是**机制**，不能统一的是**几何**；通解的信号必须是运行时状态

三条**可测**的改写方向（都不含 job 白名单、都不用 job 身份）：

| # | 方向 | 为什么（依据） | 先验/风险 |
|---|---|---|---|
| **1** | **窃取驱动细分（TBB `auto_partitioner` 形状）**：每 worker 先拿一段**连续** range（低开销 + "同区同期"），**只有被偷时才二分** | TBB 官方 spec 就是这个语义；它天然同时逼近"低固定开销 + 负载均衡 + 局部性" | 需要"可偷"保留；`blk` 已证不可偷会崩（Melee +23） |
| **2** | **争用自适应**：在**认领点**观测 CAS 失败/原子重试率，高 ⇒ 缩小单元让 worker 散开，低 ⇒ 合回连续 | 直接对应我们测到的反号（Build = 争用、Melee = 局部性）；**不需要知道 job 是什么** | ⛔ **已被 §21 实测否定**：认领原子（fetch_add）**没有失败可数**，且其**代价 ≤0.5 ms/步**（§21.2）⇒ 无空间 |
| **3** | **限制偷取（SLAW）**：仅"本地空且对方有多段"时偷，避免 counter-productive steal | SLAW 的原始动机与我们测到的现象一致 | 同上：必须保留可偷 |

**判据（先测再改，与 §12 的纪律一致）**：三个方向各做一个 env 门控臂，**同一 DLL、同会话、≥6 对、五段全看**；
**"通解"的定义是"Build 与 Melee 同号不退化"**，不是"整步平均更快"。
已知反例底座：`CLAIM_BLOCK` / `CLAIM_SLICE` / `TILE_STRIDE` 均已单独测过（§15.3），可直接作为对照臂。

### 20.5 如果三条都不成立：那就照 Unity 做"显式参数 + 离线标定"

那就诚实承认 **"认领几何没有 job 无关的通解"**，走上 Unity 的路：
粒度做成**显式的 per-job 参数**（默认值由**离线标定**给出一次，之后固定），
而**不做每步在线自适应** —— F6 的失败正好说明：在线自适应的**耦合成本可以吃掉它的收益**。

### 20.6 本轮消融对"认领几何"问题的直接回答（两帧实测）

| 问题 | 答案 | 证据 |
|---|---|---|
| 能不能用**一个固定几何**通吃？ | **不能** | §15.3（同一几何对 Build/Melee 反号；`blk` 两败） |
| Unity 是"统一几何"吗？ | **不是**：统一**机制**（`GetWorkStealingRange`）+ **每个 job 一个常量** | §20.1（机器码 + 官方文档 + §4 实测 4×64 / 11×1） |
| 其他项目有"按 job 身份选几何"的通解吗？ | **没有**；全是状态驱动（进度 / 窃取 / 局部性） | §20.2 |
| 我们的"按成本自动选几何"（F6）为什么不行？ | 机制成立（Build −1.90，10/10），**目标函数错**（整步配对中位 +1.65） | §15.4 |
| **认领几何能修掉本轮那 1.9× 吗？** | **不能** —— `位置载入+d²` 在两帧都是 1.9× ⇒ **与帧无关** | §19.5 |
| 那认领几何该修什么？ | 修**对帧敏感**的项：`邻居扫描总`（1.09 → 0.90，符号翻转）、`ORCA`（1.25 → 1.07）、`K 插入体`（1.44 → 1.24） | §19.5 |

⇒ **两条线各自成立、不要混**：
1. **体侧线**（跨帧恒定 1.9×）：目标是 `位置载入 + d²` 这个内层循环的**机器码形状**；
   **不要**再用认领几何去追它（§19.5 已排除）。
2. **帧侧线**（随帧摆动）：目标是**认领几何**，用 §20.4 的三条**状态驱动**候选
   （窃取驱动细分 / 争用自适应 / 限制偷取），判据是"**Build 与 Melee 同号不退化**"。

## 21. 认领点探针（**已实现**）：认领原子代价**不是杠杆**（2026-10-01 第七轮，可引用）

### 21.0 一句话

实现了 `ENTJOY_CLAIM_STAT`（认领点 rdtsc 探针，默认关 ⇒ 逐位不变），实测一次认领 `fetch_add` 要
**58–98 周期（15–26 ns）**——确实是 cacheline 弹跳；**但聚合起来只占整步 0.15–0.50 ms（≈0.1–0.3%）**
⇒ **"争用自适应认领几何"（§20.4 候选 2）没有可赚的空间，撤销**；
认领几何对 Build/Melee 的影响**不是来自认领原子的代价**，而是来自"**同时刻碰哪些数据**"。

### 21.1 实现（4 个文件，全部默认关 ⇒ 零时间戳开销、逐位不变）

| 文件 | 改动 |
|---|---|
| `src/NativeDll/JobSystem.cpp` | `g_claimStatEnabled`（env **`ENTJOY_CLAIM_STAT=1`**）+ 三个累加器 `g_claimProbeN/Cycles/Max`；stats reset 里清零 |
| `src/NativeDll/JobSystemInternal.h` | 4 个 extern 声明（含动机注释） |
| `src/NativeDll/ChaseLevScheduler.cpp` | `#include <intrin.h>`；`ClaimProbeNow/Begin/End/Flush`（**per-thread 本地累加，每令牌 flush 一次** ⇒ 不把探针本身变成新的原子热路径）；**三个认领点**各包一对 rdtsc：General 共享游标 / 切片自有游标 / 切片窃取游标 |
| `src/NativeDll/JobSystem_State.cpp` | `[E1] claim probe: n=… total_us=… mean=…cyc max=…cyc`（搭在 `ENTJOY_DIAG_E1=1` 打印块里；探针关闭时该行不打印） |

**验证**：构建 **0 错误**（只有既有 `getenv deprecated` 警告）｜原生测试 **9/9 ×（`ENTJOY_TILE_RUN` 关/开）**｜
运行期自证 `[CLAIMSTAT] on`｜运行时 DLL 与构建产物同哈希（`301382042557DE35…`）。

### 21.2 实测（默认档、8 worker、`AUTOEXIT=30`，同会话三臂；`tools/gate-run/claimstat/`）

| 臂 | 认领尝试 **n** | **mean cyc/次** | 聚合（8 worker 求和） | 折算/步 | Build | Melee | 整步 |
|---|---|---|---|---|---|---|---|
| `def`（共享 `nextTile`，交错） | 1,399,382 | **94.4（24.8 ns）** | 132.0 M cyc = **34.8 ms** | ~0.19 ms | 5.36 | 110.39 | 152.04 |
| `def_slc`（每 worker 独占游标） | 6,040,282 | **58.4（15.4 ns）** | 352.8 M cyc = **92.8 ms** | ~0.50 ms | **3.17** | 118.98 | 157.64 |
| `def_blk`（整块不可偷） | 1,098,538 | 97.5（25.7 ns） | 107.1 M cyc = **28.2 ms** | ~0.15 ms | 3.60 | 132.65 | 175.73 |

⚠ 口径：`n` 含"游标已耗尽"的**失败探测**（切片路的窃取循环会对每条游标各试一次 ⇒ n 高 4.3×）；
"折算/步"= 聚合 ÷ 步数（≈185 步），是**8 个 worker 求和**后除以步数，**不等于墙钟增量**（墙钟是它的 1/8 量级）。

### 21.3 结论（三条，决策级）

1. **认领原子确实被打成 cacheline 弹跳**：58–98 cyc/次（15–26 ns），而"独占 cacheline 的 fetch_add"
   应在 ~5–20 cyc ⇒ §15.3 的"争用"在**认领点**这一层成立；切片把它降了 **1.6×**（94.4 → 58.4 cyc）。
2. **但它不是杠杆**：聚合 ≤0.5 ms/步（≈0.3%），而 Build 的赤字是 **+2.32 ms**、Melee 是 ±4–23 ms
   ⇒ **候选 2（争用自适应认领）撤销**——不是"判据不对"，是**没有可赚的量**。
3. **几何的收益来自别处**：来自**同时刻的数据访问集合**（§20.3b：Melee 要"同区同期"以共享 cache line；
   Build 的计数趟要的是**内核体内** `lock xadd` 的争用被分散，07 §(l3)：Count 2.19 ms 里争用 1.25）。
   ⇒ 认领几何只是**间接**在调这件事（改变 worker 推进次序），这正是它"同一几何对两个 job 反号"的根因。

### 21.4 据本轮结果收窄后的下一步

| 选项 | 依据 | 成本/判据 |
|---|---|---|
| **A. 索引空间映射**（谁和谁同时被访问） | §21.3-3 的唯一直接动机；旋钮已有 `ENTJOY_TILE_STRIDE` | 在**现配置**复测默认档 `def` vs `def_std`（§15.3 的旧值 15 worker/旧二进制，需重做）；判据：Build 与 Melee **同号**不退化 |
| **B. 照 Unity 走"显式 per-job 参数 + 离线标定"** | §20.1/§20.5：Unity 就是这么做的；F6 证明在线自适应会被耦合吃掉 | 不改行为，只改**配置来源**（标定脚本 + 参数），风险最低 |
| C. 体侧 1.9× 的"工等价"钉死 | §19.6：两侧机器码同形 ⇒ 先证候选量是否相同 | 需打开 B 侧 `Bb0Align.Buckets`（+重建玩家档）并在 A 侧加同义计数器 |
| ~~D. 争用自适应认领~~ | ⛔ **§21.3-2 实测撤销**（无空间） | — |

## 22. 为什么会"Build 快 ⇒ Melee 慢"？—— 用 E1 数据把它拆开（2026-10-01，可引用）

### 22.0 一句话

"反号"其实是**两条不同的因果被算在了一起**：
**① 切片（slc）= 真·每元素效率损失**（`busy_ratio` 甚至更高，Melee 仍慢 ⇒ 不是负载不均，是访存收益被拿走了）；
**② 整块不可偷（blk）= 纯粹丢并发**（`busy_ratio` 0.851 → 0.735、7-worker 批次 1311 → 12）——
**blk 的 Melee +22 ms 里约 16–18 ms 是"工人空转"，不是 Melee 变慢**。
而"Build 快"与"Melee 快"抢的是**同一个资源：共享 cache line**（Build 在它上面**冲突**，Melee 靠它**共享**）——
所以一个几何无法同时取悦两者；**Unity 不是没有这个物理，而是用"每 job 一个常量"把选择在离线做掉了。**

### 22.1 证据（同一会话三臂；`tools/gate-run/claimstat/`，`ENTJOY_DIAG_E1=1`）

| 臂 | 整步 | Build | Melee | **busy_ratio** | meanConc | 7-worker 批次数 | 8-worker 聚合 busy |
|---|---|---|---|---|---|---|---|
| `def` | 152.04 | 5.36 | 110.39 | **0.8509** | 6.47 | **1,311** | 192.79 s |
| `def_slc` | 157.64 | **3.17** | 118.98 | **0.8582** | 6.48 | **1,461** | 195.12 s |
| `def_blk` | 175.73 | 3.60 | **132.65** | **0.7353** | **5.29** | **12** | **166.79 s** |

**读数**：
1. **slc 没有丢并发**（busy_ratio 0.8582 > def 0.8509，7-worker 批次更多）⇒ 它的 **Melee +8.6 ms 是"每元素变慢"**，不是尾部/空闲。
   而且 slc 让**每个 worker 自己的跑段完全连续**（自身预取更好）却仍然更慢 ⇒ **`def` 的 Melee 收益只能来自"跨 worker 共享"**（8 个 worker 同时在同一小区域，`CellStart/SortedIndex/Positions/OrcaPeer` 这类结构被取进缓存一次、被 8 个 worker 共用），不是各扫各的。
2. **blk 是另一种病**：8 worker 聚合 busy 少 **26.0 s**（192.79 → 166.79，占总容量 227 s 的 **11.5%**），
   墙钟不变 ⇒ 摊到 ≈200 步上约 **+16 ms/步**，与它的 **Melee +22 ms** 同量级 ⇒ **blk 的 Melee 损失主要是"丢并发"**（因为 Melee 占整步 ~70%，尾部的空闲都记在 Melee 头上）。
   ⇒ **`blk` 这一半是纯框架可修的**（长跑段 + **被偷时二分**，见 §22.4-①）。

### 22.2 机制（一张图说清"同一资源、相反符号"）

| | Build / Count / Place | Melee |
|---|---|---|
| 每元素做什么 | `Interlocked.Increment(Counts[cell])`（**两侧源码逐行同形**：A `CPUBattleSpatialHash.cs:176/263` ↔ B `BattleBenchM1Flat.cs:50/84`） | 扫 81 格邻居，读 `CellStart/SortedIndex/Positions/OrcaPeer…` |
| 对"同时刻访问集合"的要求 | **越散越好**：worker 同时落在同一 cell 上 ⇒ 同一计数器 cacheline 跨核弹跳（doc 07：Count 原子代价 977 tiles **2.47 ms** ↔ 62 tiles **0.07 ms**，**35×**；打乱 index 序后各粒度都塌到 **0.26–0.28 ms**） | **越聚越好**：worker 同区域 ⇒ 邻居结构只取一次、被 8 个 worker 共享 |
| 冲突的资源 | **同一条 cache line** | **同一条 cache line** |

⇒ 这就是"反号"的根：**一个几何只能决定"谁和谁同时碰哪些行"，而这一件事同时决定了 Build 的损失与 Melee 的收益。**

### 22.3 那 Unity 为什么"正常"

1. **不是因为它没有争用**：它的计数/放置与 A **逐行同形**（都是每元素一次 `Interlocked` 到 `Counts[cell]`）。
2. **是因为它的粒度是"每 job 一个常量"，从不打算用一个值服务两个 job**（§4/§20.1 实测）：
   `FlowPresence / CountCells / PlaceCells / Integrate = 64`；**含 Melee 在内的其余 11 个 = 1**
   （M2/M4 入口就是 `melee.Schedule(sN, 0)` ⇒ 有效值 1）⇒ **计数器重的 job 拿长私有跑段、Melee 拿最短跑段**。
3. ⇒ "Unity 正常"= **同一个物理冲突，它在离线（作者调参）就一次性解决掉了**；运行期不存在"一个旋钮必须同时服务两个 job"的情形，所以**看不到权衡**。
   我们痛，是因为我们要么用**全局**旋钮（必然反号），要么用**在线**自适应（F6：学习期本身扰动，Melee +2.24 把它赚的 Build −1.90 吃掉）。

### 22.4 在"只改 JobSystem/框架、不按 job 特判"这个约束下，还剩什么

**框架能控制的只有一个自由度**：*把哪些索引同时交给哪些 worker*（tile 划分 / 认领单元 / 认领顺序 / 窃取策略）。
**框架控制不了**：内核里的每元素共享自增（Build 的争用源）。

| # | 框架侧动作 | 能修哪一半 | 判据 |
|---|---|---|---|
| **①** | **长跑段 + 被偷时二分**（TBB `auto_partitioner` 形状：初始给连续大段，**只有被偷才切半**） | **blk 的"丢并发"那一半**（§22.1-2，≈16 ms/步） | `blk/slc` 的 `busy_ratio` 回到 ≥0.85、7-worker 批次数回到 ~1300 ⇒ Melee 的 blk 惩罚从 **+22 → ~+5** |
| **②** | **转译器层面的"共享原子累加"通用变换**（把 `Interlocked.Increment(shared[i])` 自动改写成 per-worker 私有累加 + 归约） | **Build 的争用那一半**（上界：Count 争用 1.25 ms，doc 07） | 变换后 `Count` 的原子代价 → ~0.9 ms 且**与粒度无关**（doc 07 的先验），即 **Build 不再对几何敏感** |
| ③ | 继续找"单一几何" | ⛔ 由 §22.2 的机制判定：**一个自由度不可能同时满足相反需求** | — |
| ④ | per-job 常量表 | 能 | ⛔ 特定解（已撤回主推） |

⚠ **② 是"框架侧且 job 无关"的唯一路线**：它不是改 job 的内容（源码不动），而是**让生成器把一类共享写模式自动私有化**
（类似 OpenMP `reduction` / 循环私有化的编译器变换）。代价与风险：需要在转译器里识别"按计算下标写共享数组"的模式并重写 + 插入归约；
**收益有上界**（Count 1.25 + Place 1.13，且 Place 的 1.13 是"散列写"不是争用，未必同法可解）。

**⇒ 必须说清的死结**：若"不改内核内容"与"不按 job 特化"**同时**成立，那么 **Build↔Melee 的冲突在框架内无解** ——
因为冲突根源（每元素共享自增）在核内，而框架唯一能动的自由度又正好是 Melee 共享收益所依赖的同一变量。
② 之所以是例外，是因为它把变换搬到了**转译器**（生成器），既没改 job 源码、也没有按 job 特判。

## 23. 认领粒度扫描（旋转 n=3）+ ① 判定 + ② 可行性调研（2026-10-01 第八轮，可引用）

### 23.0 一句话

**认领粒度就是"同时刻 8 个 worker 在 index 空间上铺开的宽度"**——这一点用旋转 n=3 的扫描钉死了：
**Build 在 claim≈16 处饱和（5.19 → 3.28，−1.9 ms）**；而 **Melee 单调变差（108.12 → 125.57@16 → 132.62@64）**，其中 **2/3 是"丢并发"（busy 0.857 → 0.782 → 0.729）、1/3 是每元素代价**；
把平衡修好（`slc` = 长跑段 + 逐 tile 可偷，busy 0.853 ≈ def 0.857）后 **Melee 仍 +6.0 ms** ⇒ 那 6 ms 是**真·每元素代价**。
⇒ **① 的语义已经存在（= `slc`），它的天花板就是 `slc`，而 `slc` 净亏 +5.6 ms** ⇒ **① 不值得实现**。
⇒ 唯一能移动这条前沿的是 **②（转译器私有化）**：它把 Build 对"大 window"的依赖去掉 ⇒ 小 window 同时服务两者。

### 23.1 扫描协议与**控制组**（`tools/gate-run/window-sweep2/`）

- 臂：`def`（不设 env）/ `ENTJOY_CLAIM_BATCH=4` / `=16` / `=64` / `ENTJOY_CLAIM_SLICE=1`；**旋转**（`ord = arms[(i+rep-1)%n]`）、n=3、默认档、8 worker、`AUTOEXIT=28`、末窗口。
- ⚠ **控制组**：`def` 与 `=4` 在代码上**完全等价**（`claimCap = env?:kClaimBatchSize(=4)` ⇒ `step=clamp(tileCount/workers,1,4)`）。
  第一轮（**不旋转、n=1**）两者差 **16.4 ms**（151.60 vs 167.99）⇒ **该轮作废**；旋转后两者差 **1.3 ms**（147.48 vs 148.80）✔
  ⇒ **教训（补 §9）：粒度扫描必须旋转 + 带"同义臂"控制组；否则不可分辨"结构性效应"与"运行次序漂移"。**

| 臂（中位 n=3） | **Build** | **Melee** | **busy_ratio** | meanConc | 整步 |
|---|---|---|---|---|---|
| `def`（claim 4 tiles ≈212 元素） | 5.19 | **108.12** | 0.8571 | 6.48 | **147.48** |
| `CLAIM_BATCH=4`（同义控制） | 5.24 | 108.70 | 0.8586 | 6.49 | 148.80 |
| `=16`（≈848 元素） | **3.28** | 125.57 | **0.7817** | 5.80 | 165.32 |
| `=64`（≈3.4k 元素） | 3.37 | **132.62** | **0.7288** | 5.27 | 174.10 |
| `slc`（长跑段 + 逐 tile 可偷） | 3.56 | 114.16 | **0.8527** | 6.44 | 153.09 |

**读数**
1. **Build 的收益有硬上限**：5.19 → **3.28**（claim≈16 起饱和）⇒ 最多 **−1.9 ms**。
2. **Melee 的代价分两半**（用 busy 拆）：
   - **机制 A｜平衡**：长认领就是"粗平衡粒度" ⇒ busy 0.857 → 0.782(16) → 0.729(64)，
     少 0.6~1.0 个 worker ⇒ 折算墙钟 +12 ms 量级；**这部分是"空转"，不是 Melee 变慢**。
   - **机制 B｜拉开**：`slc` 把平衡修好（busy 0.8527 ≈ def 0.8571）后 **Melee 仍 +6.04 ms** ⇒ **真·每元素代价**（worker 从"同一区域"变成"8 个区域"，跨 worker 共享消失）。
3. **⇒ ①（长认领 + 被偷时二分）的天花板 = `slc`**：`slc` 本来就是"长跑段 + 可偷（逐 tile）"，把它换成"偷一半"最多再回收 **0.4%** 并发（0.8527 vs 0.8571）。
   ⇒ ① 的最好结果是 **Build −1.63 / Melee +6.04 / 整步 +5.61（净亏）**。**结论：不实现 ①。**
   （这与 §22.4-① 的估计一致但更悲观：§22.4 说能把 blk 的 +22 收到 ~+5 —— 对，但那个点就是 `slc`，而它相对 `def` 仍然是净亏。）

### 23.2 ② 的可行性调研（转译器层面的"共享原子累加私有化"）

**现状（代码级）**：`Interlocked.Increment(ref Counts[hash])` →
`StatementTranslator.TranslateInterlockedCall`（`src/NativeTranspiler/Analyzer/Ast/StatementTranslator.cs:1019`）→ C 宏
`INTERLOCKED_INCREMENT_AND_FETCH32(&((int*)Counts_ptr)[hash])`（`Common/CodeTemplates.cs:81`）→ `_InterlockedIncrement`。
`PlaceCellsJob` 用的是 `INTERLOCKED_ADD_AND_FETCH32(...,1) - 1`（**返回值被用**）。

| 子问题 | 结论 | 依据/风险 |
|---|---|---|
| 哪些原子可私有化 | **只私有化"返回值被丢弃"的累加**；`Add_And_Fetch - 1`（槽位分配）**不能**（必须全局唯一） | 代码里**已有**这个分析：`Ispc/IspcGenerator.cs:1540` 就在查"返回值是否被使用" ⇒ 可复用 |
| 私有缓冲放哪 | 需要**运行期大小**（`Counts` 长度 = 格数，编译期未知）⇒ 由框架给 per-worker scratch | 新增框架侧分配/回收路径 |
| 归约在哪做 | 必须在**每 job 一次**（batch retire）时合并；否则后续前缀和读到旧值 | 框架有 retire 路径可挂；**但要证明"本 kernel 不读同一数组"** |
| 语义/确定性 | 求和确定 ⇒ 轨迹逐位不变（我们的基准依赖此） | 原子本身也不保证到达顺序，只有和确定 ⇒ 安全 |
| 别名/逃逸分析 | 必须证明该数组**在本 kernel 内只写不读**、且不被并发 job 观察 | ⚠ **今天没有这个分析**，要新写（最大的未知） |
| 影响面 | `StatementTranslator` + 生成器发射面 + `CodeTemplates` + 框架归约钩子 + 测试 | **`emit-snapshot` 门（44 文件逐字快照）会变 ⇒ 需要有意重设基线**；81 个转译器单测 + fixture 门 + ecs-native 门 |
| 工作量 | **多日级**，且是"所有 kernel 共享"的高爆炸半径改动 | — |
| **收益上界** | **−1.9 ms / 步（Build 5.19 → ~3.3）≈ 整步 −1.3%** | 直接来自 §23.1（claim≥16 的"无争用平台"）；**注意 Place 的 1.13 ms 是散列写、不是争用 ⇒ ② 修不了它** |
| **可证伪的预测** | ② 之后：**Build(claim=4) 应 ≈ Build(claim=64) ≈ 3.3**，且 **Melee 保持 108.1** ⇒ 整步 ~145.6 | 与 doc 07 的先验一致（"去原子后 Count 0.71–0.92 ms 且**与粒度无关**"） |

**另外两条"轻量变体"（同属框架侧、仍需同一份知识）**：
- **分片计数器（striping）**：`Counts[cell]` → `Counts[cell*S + worker]` + 末尾线性归约。内存只涨 S 倍、无需 per-worker 大缓冲；但**数组布局变了 ⇒ 消费者（前缀和/查表）必须同步知道** ⇒ 变更是"写者+读者一起改"，比 ② 更侵入、更不透明。
- **只在框架侧换分配**（slice/block）——已被 §23.1 证否（就是前沿本身）。

### 23.3 现状小结（供决策）

- **产品档（出厂默认）**：EntJoy 快 2.0%（§18.2a）；剩余赤字只有 **Build +2.32 / Integrate +0.66**。
- **几何这条线**：§23.1 判定**净亏**，关闭（① 不实现）。
- **② 是唯一能让"小 window 同时服务两者"的路线**，但代价是**多日级转译器改动 + 高爆炸半径**，换 **≈1.3% 整步**。

## 24. ② 的一次性验证（spike）：**预测被推翻；② 不值得投产**（2026-10-01 第九轮，可引用）

### 24.0 一句话

**做了 spike（claim 级私有化，只改生成物、C# 不动）**：在 Melee-友好档 `cap=4` 下 **Build 5.28 → 4.68（−0.60 ms，3/3 逐 rep 一致）**，
**Melee 完全不变（108.41 → 108.40）**，整步无变化（148.59 → 148.97）。
⇒ 预测的 **−1.9 ms 只兑现了 1/3**（**没有**到达 claim=64 的无争用平台 3.59）⇒ **"Build 的粒度敏感性主要来自 Count 共享原子"只对约 35% 成立** ⇒ **② 投产的性价比不成立**。
**另外：Unity 不是这么实现的** —— 它的 Count/Place 与 A **逐行同形**（都是每元素一次 `Interlocked` 到 `Counts[hash]`），引擎也没有私有化变换；Unity 靠的是**每 job 一个常量**。

### 24.1 做法（可复做；`tools/gate-run/spike-priv/`）

- 备份 `SharpNative_Job_CPUBattle_CountCellsJob_Execute.cpp` → 改成
  **每线程私有直方图 + 本 claim 触过的格清单**，claim 末尾只对"触过的格"各做一次 `INTERLOCKED_ADD_AND_FETCH32`
  （语义等价：每格的和相同 ⇒ 空间哈希逐位一致）。这是 ② 会生成的东西的**弱化版**（② job 级归约，本 spike claim 级）。
- ⚠ **器械发现（重要）**：内核在**独立模块 `NativeTranspiled.dll`**，不在 `NativeDll.dll` 里 ⇒
  改生成物后必须 `--target NativeTranspiled` **并部署该 dll**（第一次只重链 `NativeDll`，hash 未变 = 根本没生效）。
- 对比协议：**同一会话内交替换 dll**（`u`=未改 / `s`=spike）× `cap∈{4,64}` × 3 rep（消除会话漂移）。

| dll | cap | Build（3 rep） | Melee（3 rep） | 整步 | busy |
|---|---|---|---|---|---|
| u | 4 | 5.28 / 5.39 / 5.13 → **5.28** | 108.29 / 108.93 / 108.41 → **108.41** | **148.59** | 0.859 |
| **s** | 4 | **4.68 / 4.70 / 4.58 → 4.68** | 108.40 / 112.64 / 107.82 → **108.40** | 148.97 | 0.862 |
| u | 64 | 3.87 / 3.37 / 3.59 → **3.59** | 134.44 / 130.20 / 130.54 → 130.54 | 171.86 | 0.730 |
| s | 64 | 3.86 / 3.69 / 4.02 → 3.86 | 132.29 / 133.20 / 131.09 → 132.29 | 173.90 | 0.732 |

**三条判定**
1. **预测被推翻**：`cap=4` 下私有化只把 Build 拉到 **4.68**，**没有**到 claim=64 的 **3.59**
   ⇒ Build 的粒度敏感性**不主要是** Count 的共享原子（最多 ~35%）；doc 07 的"去原子 0.71–0.92 ms"是**完全去原子**的消融上界，**不是私有化可得**。
2. **② 的天花板**：即便做到 job 级归约，最好也就是逼近 3.59（−1.7 ms），而本 spike 已实测 claim 级只能拿到 **−0.60 ms**。
   以"多日级 + 高爆炸半径"换 ≤0.6~1.7 ms（整步 ≤0.4~1.1%）⇒ **不建议投产**。
3. **Melee 完全不变** ✓（预测的这一半成立）。

### 24.2 Unity 是不是这么实现的？—— **不是**

| 对照 | 结论 |
|---|---|
| Unity 的 Count | `BattleBenchM1Flat.cs:41-53`：每元素一次 `Interlocked.Increment(ref Counts[hash])` —— **与 A 的 `CPUBattleSpatialHash.cs:166-178` 逐行同形** |
| Unity 的 Place | `:71-88`：`Interlocked.Increment(counts,hash)-1` 取槽 + 写 `SortedIndex` —— 与 A `:263` 同形 |
| Unity 引擎/Burst | **没有**私有化/归约变换；官方给的答案就是"**按 job 选 batchSize**"（simple 32–128；very expensive 用 1） |
| ⇒ | **Unity 解决同一个冲突用的正是我们判定为"特定解"的那条路**：每 job 一个常量。它的 Build 优势 ≈ 它给计数趟选了 **64**（我们 cap=64 也才 3.59 vs Unity 2.82，仍差 0.8 ms） |

### 24.3 附带修复（spike 的副作用，已收尾）

- 生成物已按备份还原（hash 与原文件一致 `16541A4EE5A8`），spike 备份已删。
- 但 `NativeTranspiled.dll` 被**重建**（原本是 11:48 的构建，与 19:33 的 `NativeDll.dll` 属**混合世代**）⇒ **RVA 移位 ⇒ batch 表失效（实测 `applied=1/15`）**。
  已按**导出名**重推（⚠ 移位**不匀**：ClearAllJob 位移 0、CountCells +0x1C0、其余 +0x1A0 ⇒ **不能加常数**），
  `ab-tile-run.ps1` / `ab-melee-arms3.ps1` 已更新，实测 **`applied=15/15`** ✓（mir 168.49 / Melee 120.71）。
- ⚠ **口径修正**：今夜此前所有运行是**混合世代**（NativeDll 19:33 + NativeTranspiled 11:48）。现已是同世代；
  §18/§21/§23 的结论取自混合世代，**若要严格，关键结论应在现世代复跑**（未做）。

## 25. 对齐档下 Build 为什么慢？——**不是 JobSystem 的仪式，是两个内核趟的数据通路**（2026-10-01 第十轮）

### 25.0 一句话

在**对齐档**里把认领步长从 4 扫到 64（旋转 n=2、同会话、表+`TILE_RUN`+F2+F4）：
**Build 只从 5.88 → 5.56（−0.32 ms）**，`busy_ratio` 0.877 全程不变 ⇒
**认领几何/每工作项仪式不是对齐档 Build 的原因**（与默认档**相反**：默认档 cap 4→64 能让 Build **−1.6 ms**）。
逐趟拆解（doc 07 §(f)，夹具+真实 dump+payloadHash 自检）显示差距几乎全在两项**内核内数据通路**：
**Count 2.19（含 `lock xadd` 争用 1.25）+ Place 2.74（含散列写 1.13）+ 小 pass 0.28 = 5.21**，
而 **1.25 + 1.13 = 2.38 ≈ Build 差距 2.59 的全部** ⇒ 缺的是**内核侧**，不是 JobSystem 侧。

### 25.1 证据一：对齐档 Build 对认领步长不敏感（`tools/gate-run/aligned-build/`）

| 臂（对齐档，8 worker，`AUTOEXIT=24`） | Build（2 rep） | Melee | 整步 | busy_ratio |
|---|---|---|---|---|
| `def`（step=4，内置） | 6.03 / 5.72 → **5.88** | 120.62 / 119.24 | 162 | **0.877** |
| `CLAIM_BATCH=16` | 6.01 / 5.72 → 5.87 | 119.03 / 119.14 | 160.4 | 0.878 |
| `CLAIM_BATCH=64` | 5.64 / 5.48 → **5.56** | 118.36 / 117.29 | 158.2 | **0.876** |

⇒ **−0.32 ms / 16× 步长**，且 `busy_ratio` 无损失 ⇒ 对齐档 Build 既不是"认领原子"，也不是"平衡"。
（对照 §21 的框架侧归零：空体 1e6 tiles 的整 job 只剩 **21 µs** ⇒ 每 15.6k-tile 的 job 约 **0.3 µs** 的框架成本 —— 与 5.6 ms 差 4 个数量级。）

### 25.2 证据二：逐趟拆解（doc 07 §(f)，2026-09-30）

| 趟 | A（对齐档） | 其中可分离的"放大项" |
|---|---|---|
| Count | **2.19** | `lock xadd` 争用 **1.25**（**去原子后 0.92 且与粒度无关**） |
| Place | **2.74** | 散列写 **1.13**（顺序写消融） |
| 小 pass（zero+prefix±+host） | 0.28 | 与粒度无关 |
| **合计** | **5.21** | **B 只有总数 2.62；B 的逐趟拆分从未做过**（doc 07 §(f) 自注"未做"） |

⇒ **1.25 + 1.13 = 2.38 ≈ 5.21 − 2.62 = 2.59**：赤字几乎全在这两项；
"认领顺序"只是**放大器**（打乱 index 序后原子项 **1.25 → 0.27**），**不是根源**。

### 25.3 因此（结论与缺口）

1. **"对齐档 Build 慢"看不出 JobSystem 性能问题**：它对认领步长、对平衡都不敏感，而框架的每工作项成本已被器械证到 ~0.02 ns/tile。
2. **缺的那一步测量**：**Unity 发布档的逐趟 Build 拆分**（doc 07 §(f) 已写明做法：给 player 加 M1F/逐趟打印挂进 M4 段，重编 IL2CPP，约一行 + 一次构建）。
   拿到它才能判定 0.8~1.6 ms 落在哪：
   - B 的 Count ≈0.9（无争用）⇒ 我们的**计数趟共享写**是主因（§24 的 spike 只值 0.6，且那是默认档测的，对齐档未测）；
   - B 的 Place ≈1.6（无散列写代价）⇒ 我们的**放置趟写模式**是主因；
   - B 的 Count+Place ≈2.4（跟我们差不多）⇒ 差在**别处**（小 pass / host 侧 / 段边界），**那时才轮到 JobSystem**。
3. ⚠ **口径**：Build 强烈依赖**窗口/相位**——同一配置 `AUTOEXIT=24` 得 5.88~6.03，而 `AUTOEXIT=45` 的 final3 得 4.46 ⇒
   **跨协议比 Build 无效**；§18.2b 的"对齐档 Build +1.56"只在同协议（45 s、同窗口）内成立。
   ⚠⛔ **本节"对齐档 Build 对认领步长不敏感"的结论已在 §26.3 被推翻**（那是相位假象；用 `[M-19]` 窗口量后 count 对步长**强响应**）。

## 26. Build 逐趟同协议对照：**赤字 77% 在 `count`（3.3×），机制 = 认领窗口造成的原子争用**（2026-10-01 第十一轮，可引用）

### 26.0 一句话

给**两侧**都装上逐趟计时（Unity **玩家档** 6 趟 `M4,build,*`；EntJoy 6 段 `[M-19]`，段名一一对齐），
在**同窗口**（A 步 128–159）、**工等价已核**（A 存活 998,292 / 1e6 slots ↔ B `active_count` 997,986）下 3 对中位：

| 趟 | A (ms) | B (ms) | A−B | A/B |
|---|---|---|---|---|
| zero | 0.0093 | 0.0881 | −0.079 | 0.11 |
| **count** | **2.7116** | **0.8242** | **+1.887** | **3.29** |
| prefixPartial | 0.0841 | 0.0456 | +0.039 | 1.84 |
| hostRewrite | 0.0024 | 0.0001 | +0.002 | — |
| prefixFinal | 0.1832 | 0.1325 | +0.051 | 1.38 |
| place | 2.1586 | 1.6667 | +0.492 | 1.30 |
| **Σ** | **5.1492** | **2.7571** | **+2.392** | 1.87 |

⇒ **赤字的 77% 是 `count`**（我们比 Unity 慢 **3.29×**），`place` 只占 21% 且我们**没有**劣势到 2×（1.30×）；
`zero/prefixPartial/hostRewrite` ≈ 0。**机制（已证）**：`count` 的代价**主要由"认领窗口"造成的跨核原子争用**——
把认领步长从 4 个 tile 放大到 64，`count` 就从 **2.71 → 1.28 ms（−1.43）**，此时我们只剩 +0.4 的残差。

### 26.1 器械（两侧，可复做）

| 侧 | 改动 | 自证 |
|---|---|---|
| **Unity 玩家档** | `CpuHashFlat.BuildTimed`（`BattleBenchM2.cs`，6 趟同序 + 逐趟 `Stopwatch`）；`BattleBenchM4Entry.cs` 换调并导出 `M4,build,{zero,count,prefixPartial,hostRewrite,prefixFinal,place}_ms`；`W0BuildTool.BuildWindowsIl2Cpp` 重编 IL2CPP（**Succeeded / 0 errors / 1:56**） | 6 趟之和（**除以 `warmup+steps`** 修正后）= **2.8151** ↔ 权威 `M4,seg,build_ms` = **2.8112**（差 0.14%）✓ 3 对各自复核（2.725/2.777/2.756 ↔ 2.698/2.698/2.762）✓ |
| **EntJoy** | `CPUBattleSpatialHash.Build` 内 6 段 `Stopwatch` + `CPUBATTLE_DIAG_BUILDPASS=1`（默认关）；按 **32 步窗口**打 `[M-19] Build 逐趟(窗口均/ms): … length=…` | Σ = **5.2554** ↔ `[M-1] Build` = **5.30**（差 0.8%）✓ |

⚠ **两个器械坑（都已踩并修，补 §9）**：
1. **B 侧在预热步也累加**：`m4BuildMs` 覆盖 `warmup+steps` 次调用，而导出用 `×1/steps` ⇒ 直接读会**虚高 (warmup+steps)/steps 倍**（本例 99/32 = 3.09×）。
   **判别/修正**：拿 6 趟之和与**未膨胀的** `M4,seg,build_ms` 对齐即可（本例修正后 2.8151 vs 2.8112）。
2. **A 侧窗口累加写错**：`_bpWin += _bpSum - _bpLast` 而 `_bpLast` 只在打印时更新 ⇒ 每步累加的是**累计量** ⇒ O(N²) 虚高（count 假报 47.8 ms）。
   改为**每次调用**更新 `_bpPrev`。**教训**：窗口统计必须用"每次调用取增量"，且**必须与同期的段计时交叉验证**。

### 26.2 工等价（必须先过，否则逐趟对照无意义）

| | A | B |
|---|---|---|
| 循环上界 | `length = 1,000,000` | `n = 1,000,000`（`Schedule(n,64)`，`ceil(N/64)` 批相符） |
| 每步处理单位 | 存活 **998,292**（`[M-17]`） | `active_count` **997,986**（CSV） |
| 过滤条件 | `Alive!=0 && State!=StateDeath` | 同（逐行同形） |

⇒ 每步两侧都处理 ~1e6 元素、过滤同义 ⇒ **工等价成立**，差值可归因为**效率**。

### 26.3 ⭐ 机制：`count` 的代价由**认领窗口**主导（两个 F2/F4 状态都复现）

**对齐档、只扫认领步长 `ENTJOY_CLAIM_BATCH`，读 `[M-19] count`（窗口均）**：

| step | count（F2/F4 **关**） | count（F2/F4 **开**） | place（关/开） |
|---|---|---|---|
| 4（内置） | **2.9433** | **2.7647** | 2.2583 / 2.1294 |
| 64 | **1.2527** | **1.2849** | 1.7614 / 2.0180 |
| 1024 | 1.3276 | 1.3656 | 1.8684 / 1.9424 |

⇒ **认领放大 16× ⇒ count −1.43~1.69 ms（两个状态一致）**，而 B 的 count 是 **0.85**：
我们**放大认领后只剩 +0.40**（3.29× → 1.47×）。这与 07 §(f) 的"去原子消融 1.25"、以及"打乱 index 序后原子项 1.25 → 0.27"完全同向。
⇒ **`count` 的赤字几乎是"8 个 worker 挤在同几格上反复弹同一条计数器 cacheline"**，而这是**认领几何（框架侧）**造出来的。

⛔ **同时推翻两处旧结论**：
- **§25 的"对齐档 Build 对认领步长不敏感（−0.32 ms）"作废**：那次读的是 `[M-1]` 的**末窗口**（相位敏感，AUTOEXIT=24），
  用 `[M-19]` 32 步窗口量后，`count` 对步长**强响应**（2.76 → 1.28），两个 F2/F4 状态都复现。
- **07 §(f) 的归因错了**：它写"Build 赤字 = 原子争用 + **散列写（Place 1.13）**"，但当时**没有 B 的逐趟**；
  现在两侧都有 ⇒ **赤字 77% 在 `count`**，`place` 我们只慢 1.30×（+0.49），**不是**主因。
  （07 §(f) 的"A 5.21 vs B 2.62 = 2.0×"在**同协议**下量到的是 **5.149 vs 2.757 = 1.87×** ⇒ 量级对、**归因错**。）

### 26.4 对症（按"能否通用"排序，均未落地）

| # | 动作 | 预期收益（实测支撑） | 代价/约束 |
|---|---|---|---|
| **A** | **给计数/放置类批用更大的认领**（= Unity 的 per-job 常量；或"批大就认领大"的静态规则） | `count` **−1.43**、Build 段 −1.6~2.1 | ⛔ 默认档下同一规则会让 **Melee +17~24 ms**（§23.1）⇒ 必须**按 job 分档** ⇒ 判为**特定解**（需你放宽纪律） |
| **B** | **只改"计数器访问相位"、不改"数据访问区域"**（例如同窗口内让各 worker 错开起步 / 按格而非按 index 分片） | 目标：保住 Melee 的同区共享，同时消掉同格碰撞 | 未证可行；需一个 env 臂 + 逐趟 `count` 验收（器械已就绪） |
| **C** | **转译器私有化**（§24 spike） | claim 级实测只有 **−0.60** | 多日级 + 高爆炸半径 ⇒ 已判定不值得 |
| **D** | **消除共享计数本身**（分片计数器 `Counts[cell*S+worker]` + 末尾归约） | 上界 = count 的争用部分（**−1.4**） | 数组布局变了 ⇒ **写者+读者同改**（内核侧） |

**判据（沿用通解纪律）**：任何候选都要**同会话配对 + 逐趟 `[M-19]`/`M4,build` 对表**，
且必须满足"**`count` 显著下降而 Melee 不退化**"——只看 Build 总数不算通过（§25 就是栽在这上面）。

### 26.5 ④ 对症的判定：**通解不存在；唯一有量的杠杆是"按批的认领大小"（= 特定解）**

**为什么通解不存在（三轮证据闭环）**
1. **争用源在"同时刻的格集合"**，而它 = 认领窗口决定的（§26.3：认领 ×16 ⇒ `count` −1.43~1.69）。
2. **窗口与"worker 之间的数据区域"是同一个旋钮**：共享游标下 `窗口 ≈ 8 × 认领`，要压 `count` 就得放宽窗口，
   而 Melee 的收益恰恰来自"8 个 worker 同处一个小区域"（§23.1：放宽窗口 ⇒ Melee +17~24 ms）。
3. ⇒ **没有任何静态几何/状态规则能同时满足两者**（§15.3 反号 + §23.1 前沿 + 本节逐趟机制）。
   JobSystem 侧能动的自由度只有"谁在什么时候碰哪些索引"，而它一次只能取一个值。

**唯一有量的杠杆（收益为"推导值"，来自两臂实测，非新测）**

| 口径 | 做法 | 实测臂 | 推导收益 |
|---|---|---|---|
| **默认档（产品档）** | 只给 **Count/Place 两趟**放大认领到 64，Melee 保持 4 | §23.1 旋转 n=3：`def`（Build **5.19** / Melee **108.12** / 整步 147.48）vs `CLAIM_BATCH=64`（Build **3.37** / Melee 132.62 / 整步 174.10） | **Build −1.82、Melee 不变 ⇒ 整步 ≈ 145.7（−1.8 ms，−1.2%）** |
| **对齐档** | 同上（Melee 的认领本就由 F1 span 钉在 1024 元素，不受该旋钮影响） | §26.3：认领 4→64 ⇒ `count` **2.76 → 1.28**、Build 段 5.24 → 3.67 | **Build 段 −1.57** |

⚠ 两条推导的**共同前提**是"该旋钮只作用于 Build 的两趟"——现有旋钮是**全局**的，所以必须做**按 job**的版本才能直接量。

**实现规格（下一轮一次做完；4 文件 6 处，全部沿用既有开关风格、默认关 ⇒ 逐位不变）**
1. `JobSystemInternal.h`：`struct JobBatchTableEntry { uint32_t key; uint32_t batch; };` → 加 `uint32_t claim;`；
   新增声明 `uint32_t LookupJobClaim(uint32_t key) noexcept;`（挨着 `NoteJobBatchTableHash`）。
2. `JobSystemInternal.h` 的 `BatchState`：加 `uint32_t claimCapOverride{ 0 };`（0 = 不覆盖）。
3. `JobSystem.cpp`：解析表项第三字段 `<key>:<n>[:<claim>]`（现在只解析两段）；并实现 `LookupJobClaim`（照抄 `LookupJobBatch`）。
4. `JobSystem_Tiles.cpp` 的 `AcquireBatchStorage`：**必须**一起把 `claimCapOverride` 清零
   （BatchStorage 会被复用 ⇒ 否则继承陈旧值，与 F1 当时踩的坑同款）。
5. `ChaseLevScheduler.cpp` 的 `claimCap` 计算处：
   `const uint32_t claimCap = batch->claimCapOverride ? batch->claimCapOverride : ((g_claimBatchSize != 0) ? g_claimBatchSize : kClaimBatchSize);`
   （用 `batch->claimCapOverride` 而**不是**在调度器里查表，可避免跨 TU 传键；`funcHash` 与表键同源时也可二选一。）
6. **验收判据（缺一不可）**：① 同会话旋转 ≥3 对；② `[M-19]` 显示 **`count` 显著下降**；
   ③ **Melee 段不退化**；④ 默认档五段全看、整步配对中位同号；⑤ 全套原生测试 9/9（表为空时逐位不变）。

**仍未解（需一次旋转 A/B）**：对齐档把 `ENTJOY_CLAIM_BATCH` 4→64 时，§26.3 那轮 **整步 145.26 → 165.86（+20.6）**，
但其 Melee 的认领被 F1 钉在 1024 元素、不该受影响 ⇒ **+20.6 只能来自另两个厚-tile 批（Integrate / FlowPresence）被放大**，
或是 `[M-1]` 末窗口的相位漂移。判据：**旋转 ≥3 对**（`def` vs `cap=64`）同时读五段 + `[M-19]` ⇒ 判定后再决定"按 job"表里要给哪些批。**

**⇒ 结论**：你要求的"对症下药"在**"只改 JobSystem、且不按 job 特判"**两个约束下**无解**；
唯一有实测量的药是 **按批声明认领大小**（收益 −1.2%~−1.6 ms/步），而那**正是 Unity 的做法**（每 job 一个常量），
也是我们此前判定为"特定解"的那条 —— **是否采信，是产品/纪律决策，不是技术决策**。

### 26.6 仪器已落地（本轮），但**测量被两件事卡住**（诚实记录）

**已落地**（`src/NativeDll`，5 文件 7 处，全部默认关 ⇒ 表为空时逐位不变；构建 0 错误，原生测试 **9/9 ×2 模式**）：
`JobBatchTableEntry` 加第三字段 `claim` + `LookupJobClaim`（`JobSystemInternal.h` / `JobSystem.cpp`：表格式扩展为 `<key>:<n>[:<claim>]`）＋
`BatchState.claimCapOverride`（并在 `AcquireBatchStorage` 一并清零）＋ `JobSystem_Scheduler.cpp` 两个 General 入口写入 ＋
`ChaseLevScheduler.cpp` 的 `claimCap` 取覆盖。运行时 DLL 哈希由 `301382042557` → **`3690AFC6B5FE`**（改动确已进二进制）✓

**卡点 1（已定位并修，但代价是 RVA 又移位）**：第一次测量三臂**完全无差别**（count 2.5–2.9 全档不变）。
根因：**`BatchState` 布局变了 ⇒ 必须同时重建 `NativeTranspiled.dll`**（它也含框架 TU）——
只重建 `NativeDll.dll` 时，写入端与读取端在不同模块、布局不一致 ⇒ 覆盖值读不到。
**这是 §24.3 那条"内核在独立模块"的第二次踩坑，升级为结构性守则**：
> ⭐ **凡改 `JobSystemInternal.h` 里被跨模块共享的结构（`BatchState` 等），必须 `--target NativeTranspiled` **和** `--target NativeDll` 两个都重建并同时部署；**且 RVA 表随之失效、必须按导出名重推**。**

**卡点 2（未修，属器械小 bug）**：重新推 RVA 时的 dumpbin 解析没取到函数名（`parsed=137` 但 15 个名字全缺）⇒
导出行的"末token=名字"假设不成立（`$tok[-1]` 不是名字）。**修法**：先把 `dumpbin /exports` 原文打几行看格式（或直接用 `Select-String '^\s*\d+\s+\w+\s+[0-9A-F]{8}\s+(\S+)'` 取捕获组），
再按名字重推 15 个键。**这一步只需 2 分钟，且不影响默认档**（表为空 ⇒ 逐位不变）。

⇒ **下一轮第一件事**：修好键的解析 → 用 `A0 / A+Count&Place:64 / A+Count&Place:1024` 三臂旋转 n=3 在**对齐档**量
`[M-19] count` + `Melee` ⇒ 直测"只给 Build 两趟放大认领"的收益（推导值 −1.57）。

### 26.7 关键/分片事实的复核（2026-10-01，第十一轮续）——**两处认知被实测修正**

**① RVA 键与 §2 的表一致（重建后已复位）**
空表 + `ENTJOY_JOB_BATCH_TABLE_DUMP=1` 跑一轮，框架自报的 15 个键**逐一对上 §2 的表**，且**首见顺序与 §2 的表序完全一致**
（FlowPresence `000051f0` → FlowClear `00003960` → FlowSeed `00005480` → FlowGrad `00005010` → ClearAll `00001720` →
Spawn `00011790` → **Count `00001950`** → PrefixFinal `00010da0` → PrefixPartial `00010b30` → **Place `00010910`** →
Melee `0000c310` → MarkDead `000069f0` → Integrate `00005e60` → FlowSeedInit `00005340` → BfsWave `00003660`＝`00003690`）
⇒ **§2 的键→job 映射被第二次独立确认**；也说明 §24.3 那次"RVA 移位"是那次构建状态特有的，**现在已复位**。

**② ⚠ "默认档 tile 大小"我此前估错了（实测纠正）**
dump 自报：N=1e6 的各 job **tiles=512**（⇒ **tile ≈ 1953 元素**）、N=351,232 的 =512、N=64 的 =4、N=18,836 的 =510。
⇒ 默认档 **单个 job 是 512 tiles、~1953 元素/tile**，`step = clamp(512/8=64, 1, capEff=4) = **4 tiles ≈ 7812 元素/认领**`。
**纠正**：§26.3/§23 里我按"~19k tiles ⇒ 53 元素/tile ⇒ 256 元素/认领"讲的窗口尺寸是**错的**（19k 是整步跨 job 的口径）；
默认档的实际认领 ≈ **7800 元素**（≈4× 于对齐档的 256 元素）。这不影响 §26.3 的**实测响应**（那是对齐档、直接量的），
但**影响对"窗口"的物理解释** —— 记在这里，后续引用窗口尺寸一律以本节的 dump 值为准。

**③ 本轮测量未完成（器械 bug，已定位）**：我的三臂构造用 `tiles ≥ 10000` 去筛"Build 两趟"，但**默认档最大 tiles 只有 512** ⇒
筛选为空 ⇒ 三臂退化成"全 15 个 key 都 `:64`" / "全 `:1`" / "全 `:1`"（跑出来的三组数不是我要的对照）。
**修法（下一轮一步到位）**：直接用 §2 的键硬编码 —— `A0` = 15 键按表（4 个 `:64`、11 个 `:1`）；
`A+claim{k}` = 同表但在 **`00001950`(Count) 与 `00010910`(Place)** 后追加 `:{k}`（k = 64 / 1024）；旋转 ≥3 对，读 `[M-19] count` + `Melee`。

### 26.8 直测仍未拿到：**帧没有真的切到对齐档**（第三轮试验，器械已定位到"核对方式不可靠"）

**做了什么**：按 §2 硬编码键跑 `A0 / A+64 / A+1024` 三臂旋转 ×3（9 次）。**三臂的表串都已核对为 15 项、格式正确**
（`Tbl ':64' ':64'` → `…,00001950:64:64,00010910:64:64,…`，len 174；`:1024` → len 178）✓ —— 所以**不是**串构造问题。

**失败模式（用"帧指纹"识别）**：`[M-1] Melee` 是对齐档的可靠指纹（对齐 ~120–127 / 默认 ~105–110）。
9 次里**只有 1 次**（A0-r1）落在对齐档（Melee 123.3、count **1.93**、Build 4.69）；
**其余 8 次都落在默认档**（Melee 105.4–107.4、count 2.73–2.90、Build 5.2–5.5）⇒ **那 8 次 `ENTJOY_JOB_BATCH_TABLE` 没有生效**，
所以三臂不可比，**结论不可引用**。

**两个器械教训（补 §9）**
1. **`[JOBBATCHTABLE] entries=N` 不是可靠自证**：`entries=0` 与 `entries=15` 会在**同一次运行的日志里同时出现**
   （`NativeDll.dll` 与 `NativeTranspiled.dll` **都含框架 TU、各自有一份 `g_jobBatchTableCount`**，而我只取了正则的**首个**匹配 ⇒ 顺序不定）。
    ⇒ **帧自证必须用"实测指纹"**：对齐档判据 = `[M-1] Melee ≥ 118`（或沿用可靠的 `key=… applied=15/15` 正则），
   并在脚本里**对每次运行做门控**：不满足就丢弃该臂并打印告警（本轮就是因为只看 `entries` 而放过了 8 次默认档运行）。
2. **表串必须逐臂落盘核对**（本轮只核对了模板函数，未在每次运行前记录实际传给进程的值）。

**⇒ 下一轮（收官）**：① 每次运行前把该臂的表串写入 `$out\<arm>.txt` 并 `$env:ENTJOY_JOB_BATCH_TABLE = (Get-Content -Raw)`；
② 每次运行后**门控** `Melee ≥ 118` 且 `applied=15/15`，否则该次作废重跑；
③ 再取 `A0 / A+64 / A+1024` 的 `[M-19] count` 中位 ⇒ 直测"只给 Build 两趟放大认领"的收益（推导值 −1.57 ms）。

### 26.9 ⭐ 直测成功（收官）：**只给 Build 两趟放大认领 ⇒ Build −0.94 / 整步 −1.7~2.1，Melee 不退化**

**器械修法（生效）**：**每个臂跑一次独立的 `powershell.exe` 子进程**，表串在该子进程内用字面量设进 env
（父 shell 逐轮改 env 的做法**只有第一轮有效**）；每次运行后**门控** `applied=15/15` **且** `Melee ≥ 115`，不合格即作废。
本轮 **9/9 全部通过门控**（`applied=15`，Melee 116.7–120.2）⇒ 数据可信。

**顺带第三次确认键→job**：`000051f0`(FlowPresence)/`00001950`(Count)/`00010910`(Place)/`00005e60`(Integrate) = **tiles 15,625 / applied 64**；
其余 11 个 = **tiles 1,000,000（或 351,232 / 64 / 18,836）/ applied 1** ⇒ 与 §2 表**逐项一致** ✓

| 臂（给 Count+Place 的认领覆盖） | count | place | **Build 段** | **Melee** | **整步** | n |
|---|---|---|---|---|---|---|
| `A0`（= 内置 4 tiles） | **1.839**（1.92/1.82/1.84） | 2.5044 | **4.50** | 118.93 | **165.10** | 3 |
| `A+64` | 1.5966（1.59/1.60/1.63） | 2.331 | **4.09** | **117.39** | **163.00** | 3 |
| `A+1024` | **1.2906**（1.29/1.35/1.29） | **1.8352** | **3.56** | 117.97 | 163.40 | 3 |

**逐段结论（3/3 单调、无例外）**
1. **`count`：1.839 → 1.597（claim 64，−0.24）→ 1.291（claim 1024，−0.55）**；
   `place` 同时受益：2.504 → 2.331 → **1.835（−0.67）** ⇒ **两趟都随认领放大而变快**（争用被压低）。
2. **Build 段：4.50 → 4.09 → 3.56（−0.94）**。
3. ⭐ **Melee 不退化**：118.93 → 117.39 / 117.97（在噪声内，甚至略好）⇒ **通解判据"`count` 降而 Melee 不退"成立** ✓
4. 整步：165.10 → **163.00 / 163.40（−1.7 ~ −2.1 ms）**。
5. 与 Unity 的残差（B：`count 0.824 / place 1.667 / Build 2.757`，§26.0）：本档 `A+1024` 后
   `count 1.29`（仍 1.57×）、`place 1.84`（已与 B 持平）⇒ **认领放大把 Build 赤字从 +2.39 收到 +0.80 左右**，
   剩下的 ~0.5 在 count、~0.2 在 place —— 即"共享计数器 RMW"的**不可再摊薄**部分。

**⇒ ④ 收官判定**：**"按批声明认领大小"是唯一有实测量、且满足"`count` 降 + Melee 不退"的杠杆**（本档 −0.94 Build / −1.7~2.1 整步）；
它**不碰内核、只改框架的认领上限**，但需要"**按 job 分档**"⇒ 与既有纪律（不做逐 job 硬编码档位）冲突 ⇒ **采信与否是产品/纪律决策**（已交付候选与数据，等用户定）。
器械与全部数据：`tools/gate-run/{buildpass,player-build,perjobclaim6,diag}/`。

## 27. A/A2：把"交付粒度"从全局策略改成**可按批声明**（2026-10-01 第十二轮）

### 27.0 一句话

表格式扩展为 `<key>:<batch>[:<claim>]`，**两段都可省略** ⇒ **`key::<claim>` = 只覆盖认领、不改内批**（A2）——
实测自证：`FlowPresence/Integrate` 仍 `tiles=15625 applied=64`（钉内批有效），
**`Count/Place` 变成 `tiles=512 applied=0`（内批未被覆盖，回落到默认 tiling）而认领已生效**（`count` 1.839 → **1.356**）✓
这解决了 §26 遗留的"默认档根本没法测按批认领"的结构性障碍。

### 27.1 实现（本轮新增/修正）

| 处 | 改动 |
|---|---|
| `JobSystem.cpp` 表解析 | 改为**段内解析** `<key>:<batch>[:<claim>]`，两段可空（`b>0 || c>0` 才建条目）；修正了"吞掉两个冒号 ⇒ 把 claim 当成 batch"的 bug |
| 其余 | 沿用 §26.6 的 `claim` 字段 / `LookupJobClaim` / `BatchState.claimCapOverride`（`AcquireBatchStorage` 清零）/ `ChaseLevScheduler` 取覆盖 |

**验证**：两个 target 均 **0 错误**；原生测试 **9/9 ×（TILE_RUN 关/开）**；部署 `NativeDll=E4345751E5A3` / `NativeTranspiled=44D19D42370E`。

### 27.2 A2 功能自证（对齐档：13 键钉住 + Count/Place 走 `::1024`）

```
key=000051f0 N=1000000 tiles=15625 applied=64      ← 钉内批仍有效
key=00005e60 N=1000000 tiles=15625 applied=64
key=00001950 N=1000000 tiles=512   applied=0       ← A2：内批**未**被覆盖（回落到默认 512 tiles）
key=00010910 N=1000000 tiles=512   applied=0
[M-19] zero=0.0133 count=1.3563 prefixPartial=0.0518 hostRewrite=0.0026 prefixFinal=0.1669 place=2.0097 Σ=3.6007
[M-1]  Build=3.64 Flow=35.06 Melee=122.17（对齐档指纹）MarkDead=1.84 Integrate=4.02 整步=168.13
```
对照 §26.9：`A0`（认领=4）`count 1.839 / Build 4.50`；`A+1024`（tile=64 元素、认领=1024 tiles）`count 1.291 / Build 3.56`。
⇒ **认领可在"不动 tiling"的前提下单独调**（本例 tiling=默认 512 tiles ⇒ `capEff=1024` ⇒ `step=clamp(64,1,1024)=64` 即块状认领），
`count` 拿到 **1.356（−0.48）**，且 **Melee 未受损**（122.17 属对齐档正常区间）。

### 27.3 本轮未解（下一轮）

**默认档的收益仍量不出来**：默认档的 tile 划分是**运行期自适应**的（同一臂 `D0` 三次运行 `count` = 2.94 / **1.35** / 2.90）
⇒ **默认档跨运行不可比**。要量就必须**先冻结 tiling**：两臂都用表把 Count/Place 的内批钉到同一值（例如 `:1953`，即默认态的 512 tiles），
再只差认领（4 vs 1024）⇒ 这就是"产品档口径"的干净对照。

### 27.4 冻结 tiling 的尝试**失败了，但暴露了一个更重要的问题**（第十三轮）

**做法**：把 §26.7 dump 出的默认档 tiling 逐 job 钉住（15 个键：1e6→1953、351232→686、64→16、18836→37），
只让 Count/Place 的**认领**在 `4 / 16 / 64` 之间变（3 臂 × 3 rep，**每臂独立子进程**，带 `E1` 读 busy）。

**结果一：钉住全部 15 个 job ⇒ 帧变了**（不是默认档）

| 帧 | Melee（指纹） |
|---|---|
| 默认档（自适应） | ~108 |
| **本轮的"冻结"帧** | **120.6 – 124.0**（= 对齐档量级） |
| 对齐档（表） | ~121 |

⇒ **钉住 15 个 job 会绕过 JCC，帧随之改变**（`[JOBBATCHTABLE] … JCC bypassed`）⇒ **不能当作产品档口径**。
（所有 9 次运行都因此**未过我的帧门控**，我没有把它们的数当结论。）

**结果二（仅作参考，帧已变）**：在该帧内，认领 4 / 16 / 64 的 `count` = **1.49 / 1.34~1.49 / 1.30~1.40**
⇒ **认领敏感度只剩 −0.1~−0.2**（对齐档是 −1.4）；`busy` 全程 0.848–0.854 ⇒ 长认领**没有**带来平衡损失。
说明：**`count` 对认领的敏感度本身依赖帧的 tiling**——认领只在该帧"tile 小、worker 挤同一片"时才主导。

**结果三（本轮最重要的发现）**：**默认档的 Build 本身就不可复现**
`D0`（同臂、同配置、不同运行）`count` = **2.94 / 1.35 / 2.90**（Melee 108.9 / 114.8 / 111.7）⇒ 因为 **tiling 由运行期自适应决定**（JCC/ResolveChunkSize 依赖历史）。
⇒ **任何"默认档 Build A/B"都带这个不可复现性**；要让产品档的按批认领可量、可验收，**前提是先让默认档的 tiling 可复现**
（例如：默认档固定 tpw 或给 JCC 一个确定性初值）。**这是一个独立、且挡在验收路径上的框架议题**（建议下一轮单列）。

### 27.5 复核：dump 的 `tiles` 是**首步（冷态）**值 —— 三处观测由此自洽

**关键**：`NoteJobBatchTableHash` 只在**首见**该键时打印 ⇒ §26.7 记录的 `tiles=512` 是**首步冷态**的 tiling，**不是稳态**。
据此三件事自洽：

| 观测 | 数值 | 解释 |
|---|---|---|
| 默认档（稳态）`count` | **2.90**（D0-r1/r3） | 稳态 tiling **细**（接近 64 元素/tile） |
| 对齐档（tile=64 元素、认领 4）`count` | **2.71**（§26.9 A0） | 与默认档吻合 ⇒ **默认档稳态 ≈ 对齐档 tiling** |
| 本轮"冻结"帧（tile=1953 元素 = 冷态值、认领 4）`count` | **1.49** | tiling 被我用**冷态值**钉**粗**了 ⇒ 认领变块状、争用自然降 |

⇒ **两条推论**（标注为推论）：
1. **默认档的稳态 tiling 与对齐档同量级（~64 元素/tile）**，所以 §26.9 的"按批认领 ⇒ Build −0.94 / 整步 −1.7~2.1，Melee 不退"**对产品档同样成立**（只是产品档当前不可复现，无法直接验收）。
2. 本轮"长认领让 Melee 变慢"（108→122）**不是**认领本身之罪，而是我把 tiling 钉粗了 ⇒ **tiling 与认领是两个旋钮，此前多轮把它们混用过**（这也解释了 §23.1 里"认领 4→64 ⇒ Melee +24.5"里夹杂的平衡与 tiling 效应）。

**⇒ 验收路径（下一轮起）**：① 先做**周期性 tiling dump**（每 N 步打一次 `key/tiles/applied`）把"稳态 tiling"变成**可观测量**，而不是靠推论；
② 再用"固定稳态 tiling + 只变认领"做产品档 A/B（帧门控：Melee 落回默认区间）；③ 然后才谈 A 的 API 形态与 C 的 `count` 归因。

### 27.6 tiling 已可观测：**它跨运行完全一致** ⇒ 不可复现的根因不是 tiling、也不是相位（第十四轮）

**新增器械**：`ENTJOY_JOB_TILE_TRACE=<K>` ⇒ 每个键**前 K 次**调度都打 `tiles`（默认 0 = 只打首见，行为不变）。
构建 0 错误、原生测试通过、部署 `NativeDll=95B19A8DE6FF` / `NativeTranspiled=E7138071F4F9`。

**两次默认档 trace（各 22 s，独立子进程）逐次 tiling**：

| 运行 | Count `00001950` tiles 序列（前 12 次 → 末次） | 唯一值 | Melee `0000c310` | count |
|---|---|---|---|---|
| T1 | `512,512,512,32,8,512,512,512,512,512,512,512 … 512` | {8,32,512} | `512,512,512,8,512,…` | 2.7691 |
| T2 | **与 T1 逐位相同** | {8,32,512} | **与 T1 逐位相同** | 2.9590 |

⇒ ① **tiling 完全可复现**（跨运行逐位相同），且能看到"头几步自适应抖动（512→32→8→回到 512）"⇒ **稳态 = 512 tiles**；
② 两次运行 `count` 仍差 0.19 ms ⇒ **差异不在 tiling**。

**相位也不是原因**（用 `[M-19]累计步` 与 `[M-1]` 窗口起点核对，`tools/gate-run/claimonly/D0-r*`）：

| 运行 | `[M-19]`累计步 | 末窗口起点 | 末窗口步数 | count | Build | Melee |
|---|---|---|---|---|---|---|
| D0-r2 | 128 | **94** | 33 | **1.3507** | 3.85 | **114.78** |
| D0-r3 | 128 | **94** | 32 | **2.9012** | 5.51 | 111.74 |

⇒ **同一步窗口（94 起、~32 步）下 `count` 相差 2.15×** ⇒ **不是相位**；而且那次 `count` 快的运行 **Melee 反而更慢**
⇒ **也不是频率/热漂移**（那会整体同向缩放）。

**⇒ 结论（假设，待证）**：两次运行**做了不同的"工"**⇒ **仿真状态不可逐位复现**。
最可能来源：`PlaceCellsJob` 用**共享 `InterlockedAdd` 分配 cell 内槽位**（`CPUBattleSpatialHash.cs:263`，Unity 侧同款）
⇒ `SortedIndex` 的**cell 内顺序是竞态产物**；而 Melee 的 K 最近邻用 `d2 < worstK2` 这类**平局判定**读该顺序 ⇒ 平局时选出的邻居集合不同 ⇒ 位置演化分叉 ⇒ `Counts` 的分布（进而 `count` 的争用）随运行而变。
**这与 CPU 侧 3 处统计量一致**（同一臂 `count` 2.94/1.35/2.90，`Build` 5.40/3.85/5.51）。

**⇒ 下一轮的决定性实验（便宜）**：在固定步号打印**顺序敏感**的状态指纹（例如 `Σ_i SortedIndex[i]*i`、`Σ_i Counts[i]`、存活数与位置和），跑两次比对：
- 指纹不同 ⇒ **仿真不可逐位复现被证实** ⇒ 这是**框架/基准的确定性问题**（且它挡在所有产品档 A/B 的验收路径上，优先级高于 A 的 API 与 C）；
- 指纹相同 ⇒ 回到计时侧继续查（那时才轮到"计时不可复现"的思路）。

### 27.7 ⭐ 决定性实验：**仿真本身不可逐位复现**（步 32 就分叉，连"不敏感"的和也不同）（第十五轮）

**器械**：`[M-20] 指纹 步=N SortedXi=Σ SortedIndex[i]*(i+1) SortedSum=Σ SortedIndex CountsSum=Σ Counts`（与 `DIAG_BUILDPASS` 同门控，静态 32 步打一次；C# 侧零行为改动）。
三次同配置运行（默认档，各 22 s，独立子进程）：

| 步 | F1 | F2 | F3 |
|---|---|---|---|
| 32 | SortedXi=**250209202579826839**  SortedSum=**499999925309**  Counts=**999997** | **250223736112337239**  **500000857609**  **999996** | **250207516072836477**  **499999923317**  **999998** |
| 64 | 251020881328321262 / 500856558876 / 997804 | 250939235312820472 / 500697947120 / 997767 | 251041834658430936 / 500853609904 / 997797 |
| 96 | 251538537943666293 / 501343987813 / 995908 | 251086090705185428 / 500865630091 / 995906 | 251558539196764063 / 501330667950 / 995920 |

**⇒ 三条判定**
1. **第一个快照（步 32）三次就已不同**，而且**连设计上"不敏感"的两个量和也不同**（`ΣSortedIndex`：499,999,925,309 / 500,000,857,609 / 499,999,923,317；
   `ΣCounts`＝已放置数：999,997 / 999,996 / 999,998）⇒ **不是"仅槽位顺序不同"，是整个仿真在 32 步内就分叉**。
2. ⇒ **§27.6 的假设被证实（且比假设更强）**：**本基准的仿真不可逐位复现**。（根因候选仍是
   `PlaceCellsJob` 的共享原子槽位分配（`CPUBattleSpatialHash.cs:263`，**Unity 侧同款**，故 A/B 仍公平）＋ Melee 的平局判定读该顺序 ⇒ 位置/存活演化分叉。）
3. ⇒ **产品档 Build 的小认领 A/B 天然不可复现**（`count`：2.8334 / 2.4746 / 2.5933；Build 5.62/5.28/5.61）：
   小认领下 `count` 由**争用**支配，而争用取决于数据 ⇒ 数据一分叉，`count` 就跳。
   **反过来解释了为什么 §26.9 的对齐档数据是稳的**：那里认领大（块状）⇒ 争用被压掉 ⇒ `count` 只由循环长度决定 ⇒ 对数据分叉不敏感（1.29/1.35/1.29）。

**⇒ 对 goal 的影响（重要）**：**"默认档直测按批认领收益"在这套基准上做不到** —— 不是 A 的机制问题，是**仿真不可复现**这一前置条件。
**两条出路**（都需要你定，前者动内核、后者动基准语义）：
- **P1｜确定性放置（内核侧）**：`PlaceCellsJob` 改用**前缀和基数 + 确定性偏移**（不再用共享 `InterlockedAdd` 分配槽位），消除槽位顺序竞态；
  这会把"不可复现"从根上拿掉，但**改变了与 Unity 的对等前提**（Unity 同款竞态）⇒ 只能两边同改，或只用于 A 侧内部 A/B。
- **P2｜接受并绕开**：只在**确定性帧**（表钉住 tiling、且认领足够大）里验收按批认领 —— 即 §26.9 的做法，并把产品档收益标注为"§26.9 + 推论"。

## 28. ⭐⭐ 机器码对比：`count` 的残差是**生成器问题**（环内 7 次不变量重载），**不是调度问题**（第十六轮）

### 28.0 一句话

把 A 侧 `CountCellsJob_Execute_Batch` 反汇编出来：**每元素都从内存重载 7 个循环不变量**
（`Length`/`InvCellSize`/`OriginX`/`CellsW`/`OriginY`/`CellsH`/`StateDeath`，各自沿参数指针解引用），
**因为这些标量在生成物里被绑成 `const T&` 引用，而环内又通过 `Counts_ptr` 写内存 ⇒ 编译器无法证明无别名、只能逐个重载**。
Unity（Burst）把它们留在寄存器里 ⇒ **认领几何这条路已饱和后剩下的 1.29 → 0.824 缺口，主要就是这 7 次重载**（量级吻合，见 §28.2）。
⇒ **这是转译器（EntJoy 框架）里的生成问题，不是在 JobSystem 调度里**。

### 28.1 证据：A 侧 Count 内层循环（`llvm-objdump -d`，`NativeTranspiled.dir/Release/unity_0_cxx.obj`）

```
88: movslq (%rbx), %r9          ; ← Length      （`index < Length` 冗余守卫的操作数）
8b: cmpq   %r9, %rcx
8e: jge    0x80                 ; ← 每元素一次 load+cmp+jge（Unity 无此守卫）
90: cmpb   $0x0, (%r12,%rcx)    ; Alive[index]
95: je     0x80
97: movl   (%r15,%rcx,4), %r9d  ; State[index]
9b: cmpl   (%rax), %r9d         ; ← StateDeath 从内存读（Unity 里是寄存器/立即数）
9e: je     0x80
a0: vmovss (%r8,%rcx,8), %xmm1  ; p.x
a6: vmovss (%rdi), %xmm0        ; ← InvCellSize 重载
aa: movq   0xb8(%rsp), %r9      ; ← OriginX 指针重载
b2: vfmadd213ss (%r9), %xmm0, %xmm1
b7: vcvttss2si %xmm1, %r9d
bb: movl   (%rsi), %r13d        ; ← CellsW 重载
be: leal   -0x1(%r13), %ebp
c2: cmpl   %r9d, %ebp
c5: jl     0xca
c7: movl   %r9d, %ebp
ca: vmovss 0x4(%r8,%rcx,8), %xmm1
d1: movq   0xc0(%rsp), %r9      ; ← OriginY 指针重载
d9: vfmadd213ss (%r9), %xmm0, %xmm1
de: vcvttss2si %xmm1, %r10d
e2: movl   (%r11), %r9d         ; ← CellsH 重载
e5: decl   %r9d
e8: cmpl   %r10d, %r9d
eb: jl     0x58
58: movl   %ebp, %r10d          ; max(0,·) 的 clamp 序列
5b: sarl   $0x1f, %r10d
5f: andnl  %ebp, %r10d, %r10d
64: movl   %r9d, %ebp
67: sarl   $0x1f, %ebp
6a: andnl  %r9d, %ebp, %r9d
6f: imull  %r13d, %r9d          ; cy * CellsW
73: addl   %r10d, %r9d
76: movslq %r9d, %r9
79: lock incl (%r14,%r9,4)      ; 原子自增（这是必须的）
80: incq   %rcx
83: cmpq   %rdx, %rcx
86: jge    0xf9
```

**成因（代码级）**：生成物把标量参数绑成**引用**（`const int& Length = *Length_ptr;` 等，见 `SharpNative_Job_CPUBattle_CountCellsJob_Execute.cpp`），
而环内每个元素都写 `INTERLOCKED_INCREMENT(&((int*)Counts_ptr)[hash])` ⇒ **任何一次外部写都可能"改掉"这些引用指向的值**
（无 `__restrict` 可依赖）⇒ 编译器只能**每元素重载**。

### 28.2 量级核对（为什么这就是那 0.47 ms）

- 每元素多出：**7 次不变量重载 + 1 组冗余守卫（`load/cmp/jge`）**，而热路径本体约 20 条 ⇒ **≈ +40% 指令**。
- 我们最好（认领 1024、`count` = **1.291 ms**）↔ Unity（**0.8242 ms**）：差 0.47 ms；1e6 元素 × ~7 次 L1 载入
  ≈ 7M 次载入 ÷（~3 载入/周期 @3.8 GHz）≈ **0.6 ms** 量级 ⇒ **与缺口同量级**（标注：量级核对，非逐条插桩）。

### 28.3 修法与判据（转译器侧，通用、非按 job 特判）

| 方案 | 做法 | 预期 |
|---|---|---|
| **S1（首选）** | 生成器对**标量参数**（非数组）发射**值局部量**：`const int Length = *Length_ptr;`（读一次），而不是 `const int&` | 环内那 7 次重载消失；`count` 向 0.82 逼近；对 Melee 这类大核需**单独复核**（寄存器压力可能反转——§7ao 的"值绑定更差"就是在大核上测的，**本条把结论限定回小核**） |
| **S2** | 或把 `_Batch` 的标量参数**打包成一个按值传入的 `params` 结构体**（基址+位移寻址） | 与 S1 同效，且顺带减少参数封送 |
| **S3** | 给这些指针加 `__restrict`（若生成器能证明不别名） | 让编译器自己有自由度做 LICM；但**不改绑定形态**时收益不确定 |

**验收判据**（沿用纪律）：
① `llvm-objdump` 数**环内**的不变量重载数（应从 7 → 0~1）；
② 对齐档（表 + 门控 `applied=15/15`、帧指纹）`[M-19] count` 从 **1.291 → 逼近 0.824**；
③ **Melee 不退化**（大核的寄存器压力复核，必要时 S1 只对"小核"或按"环内活值数"自适应）；
④ 默认档五段全看 + 原生测试 9/9。

**顺带纠正**：§7ao 的"`const T&` 值绑定更差"是在 **Melee 大核**上测的，本条在 **Count 小核**上得到**相反**结论 ⇒ 该结论需**分核复核**，不能一概而论。

## 29. §28 的根因**仓里早已知道**，且已有两个现成旋钮 —— 但本轮的尝试是**空操作**（第十七轮）

### 29.1 仓里已有的认识与旋钮（`src/NativeTranspiler/Analyzer/Cpp/CppJobGenerator.cs:337-397`）

源码注释把 §28 的机制写得比我的复述更准（含**真实 obj 证据**：`ZeroCellsJob` 引用绑定下"8 条标量指令、0 向量指令、**循环内每轮 `movslq (%r8)`**"；按值绑定后一次载入 + `memset` 尾调用、每元素 1 次 AVX2）：

| 旋钮 | 取值 | 语义 |
|---|---|---|
| `ENTJOY_VALUE_BIND` | `0` / `1` / `2` / **默认 3** | 0=关（引用，历史基线）；1=仅单 IJob 路径；2=**全路径按值**（历史实测**退化**：整 TU 指令 +9%、Integrate +9.8%、FlowClear +53.6%、真实负载整步 **+2.81 ms**）；**3=判据：只对"参与循环行程数"的字段按值绑定** |
| `ENTJOY_SCALAR_RESTRICT` | `2` | 对"只在**循环体内**出现"的字段改用**形参 `__restrict`**（零拷贝；注释称"实测可解锁向量化"） |

### 29.2 为什么默认判据**没覆盖我们的 Count**

默认判据是"**字段出现在 `for` 初值/条件/步进、或 `while`/`do` 条件**里"。
而我们的循环是框架的 `for (index = __startIndex; index < __startIndex + __count; ++index)` ⇒ **行程数来自形参 `__startIndex/__count`**；
`Length` 只出现在**循环体内**的 `if (index < Length && …)` ⇒ **不判为行程数** ⇒ 仍是引用绑定 ⇒ 环内 7 次重载（§28.1 的 asm 与生成物 `const int& Length = *Length_ptr;` 一致）。
⇒ 按注释自己的说法，Count 这批属于**"循环体内"**，应当交给 **`ENTJOY_SCALAR_RESTRICT=2`**（形参 `__restrict`，零拷贝）或 `ENTJOY_VALUE_BIND=2`。

### 29.3 ⚠ 本轮的两个旋钮实验是**空操作**（器械发现，值得记）

`ENTJOY_VALUE_BIND=2` 与 `ENTJOY_SCALAR_RESTRICT=2` 两轮试跑后，**生成物 mtime 始终是 `11:48:01`、绑定形式与 asm 逐字不变（仍是 7 次重载）**
—— 即 **`dotnet build`（含 `-t:Rebuild`）并没有重新生成 `NativeTranspiler_Generated/*.cpp`**。
根因：生成是由 **MSBuild 自定义任务**做的 —— `ComputeShaderBattleSimulation.csproj:68-70`：
`<Target Name="CompileCppWithCustomTask" AfterTargets="CoreCompile" …><NativeCompileTask NativeCodeGenDir="…NativeTranspiler_Generated\" …/></Target>`，
该任务**自带增量判据**（按输入/Hash 决定是否重新发射）⇒ **改 env 而不动输入 ⇒ 任务判定"无需重发"** ⇒ 旋钮不生效。
（这也解释了 07 文档里那批臂为什么都写"**游戏重编（`--no-incremental`）**"。）

**⇒ 下一轮第一步（必须先做）**：读 `NativeCompileTask` 的增量判据/有无强制开关（或"touch 一个 C# 输入"绕过），
**先让生成物真的重发**，再分别用 `SCALAR_RESTRICT=2` / `VALUE_BIND=2` 复核：① objdump 环内重载数（目标 7 → 0~1）；
② 对齐档 `[M-19] count`（目标 1.291 → 逼近 0.824）；③ **Melee 不退化**；④ 最后把默认值定为**通用规则**（而非按 job 特判）。

## 30. 重发器械打通 + `SCALAR_RESTRICT` 为何无效 + 一个**从未被测过**的机会（第十八轮）

### 30.1 ✅ 让生成物真的重发（此前 3 轮空操作的根因找到了）

**缺的那一步 = `dotnet build-server shutdown`**（07 §(l9) 就有，我漏了）：
```
dotnet build-server shutdown
dotnet build ComputeShaderBattleSimulation.csproj -c Debug --no-incremental    # 游戏仓 ProjectReference 直连本仓 ⇒ Roslyn 生成器
cmake --build … --target NativeTranspiled
```
**自证**：`ENTJOY_SCALAR_RESTRICT=1` 下生成物的 `CountCellsJob_Execute_Batch` 签名**出现 `__restrict`**（genMtime 从 11:48:01 → 23:29:01）✓；
清空 env 重发后**恢复无 `__restrict`** ✓。整轮 29–31 s ⇒ **旋钮试验的循环很便宜**。
（此前失败的机理：Roslyn **build server 缓存**把"输入内容未变"的编译整段跳过，而 **env 不在其缓存键里** ⇒ 改 env 不重发；删 `obj/build/*.hash` 也不够。）

### 30.2 ⚠ 但 `SCALAR_RESTRICT=1` 对 Count **零效果**：`RESTRICT` 宏是**空定义**

| 臂 | 生成物 | `Count` 环内不变量重载 |
|---|---|---|
| baseline | 无 `__restrict` | **7** |
| `SCALAR_RESTRICT=1` | **有 `__restrict`** | **7（不变）** |
| restored | 无 | 7 |

原因（07 §(l9) 表格自注）：**`RESTRICT` 宏维持"空"定义**（因为"改它会让 44 个 emit-snapshot 基线全部变文本"）⇒
**`__restrict` 只出现在文本里，不生效** ⇒ 该旋钮在当前配置下是**惰性的**。
⇒ **要让它生效只需一行**：在 C++ 侧把 `RESTRICT` 定义为 `__restrict`（`Common/CodeTemplates.cs`），代价是**有意重设 emit-snapshot 基线**（44 文件）。

### 30.3 ⭐ 机会：`Count` **从来没被这批旋钮测过**

07 §(l9) 那批臂（`VALUE_BIND=1/2`、`SCALAR_RESTRICT=1`）的观测内核是
**ZeroCells / grad / Bfs / Melee / Integrate / FlowClear —— 里面没有 `CountCellsJob`**；
而当时否证结论建立在"**受益的内核不是热点**"（ZeroCells 30% 收益但不在关键路径）与"别的内核退化（Integrate +9.8%、FlowClear +53.6%）"上。
**现在 `Count` 是 Build 赤字的 77%（§26）** ⇒ 这批评判的**前提已经变了**：同样的旋钮在 **Count 这种小核**上收益可能很大（§28：环内 7 次重载 ≈ 该核的 36% 指令）。

**⇒ 下一轮（收官这批）**：① 先把 `RESTRICT` 宏在 C++ 侧定义成 `__restrict`（一行 + 重设快照基线），objdump 复核 **Count 环内重载 7 → ?**；
② 同时试 `VALUE_BIND=2`（全路径按值）——它对 **Count 小核**的效果从未测过；③ 两条都按"对齐档 + `[M-19] count` + Melee/Integrate/FlowClear 观察名单"验收；
④ 若小核受益、大核退化 ⇒ 落**通用判据**（按"环内不变量重载数/环内活值数"在发射期决定绑定形态），**不按 job 特判**。

## 31. `VALUE_BIND=2` 实测：**源码形态变了，但环内重载没消失**（第十九轮）

**做法**（用 §30.1 打通的重发器械：`dotnet build-server shutdown` + `--no-incremental`）：`ENTJOY_VALUE_BIND=2` 重发 → objdump → 跑一次对齐档 → **随后还原**。

| 项 | 结果 |
|---|---|
| 生成物 | **`const int Length = *Length_ptr;`（按值生效）** ✓ |
| Count objdump | 行数 78→**80**、**"不变量重载"仍是 7**、栈访存 **10** |
| Integrate objdump（观察名单） | 行数 531、不变量重载 21、栈访存 139 |
| 对齐档读数 | ❌ **无效**：`appliedKeys=0`（表未生效）⇒ 该次 `count=2.8649` 不能用作对照 |

**判断**：按值绑定把"**穿过指针的每元素重载**"换成了"**从自己栈帧的每元素重载**"（值跨整个循环存活 ⇒ 溢出到栈）
⇒ **重载代价没有被消除**（与 07 §(l9) 的"asm 收益不落地"一致）。
⇒ **两个现成旋钮都被排除**：`SCALAR_RESTRICT=1` 是**惰性**（`RESTRICT` 宏空定义，§30.2）；`VALUE_BIND=2` **不消除重载**（本节）。
⇒ **只剩一条干净路线**：**把 `RESTRICT` 宏在 C++ 侧真正定义成 `__restrict`** ⇒ 编译器可以**把指针解引用本身提到环外**（真 LICM：既没有值拷贝、也没有栈溢出）
＋ 有意重设 44 个 emit-snapshot 基线。**这条从未测过**，是 C 的下一步。

**状态**：已**还原为规范生成**（gen hash `16541A4EE5A8`、"引用绑定"、Count asm 78 行/7 次重载）✓；两个模块重建并部署（`NativeDll=D189C1384E2F` / `NativeTranspiled=E5580D94C808`）。

## 32. ⭐ 指令级对齐**做到了**，但它**不是** `count` 的瓶颈：Unity 侧同样每元素一次 `lock incl`（第二十轮）

### 32.1 成果一：`VALUE_BIND=2 + SCALAR_RESTRICT=1`（**必须两个一起**）把 7 次不变量重载全部提到环外

真重发（`dotnet build-server shutdown` + `--no-incremental`，59 s / 0 错误）后，Count 内层循环：

| | 序言 0x5c–0x76 | 循环体 0xb8→0xb6（回边） |
|---|---|---|
| 改前（引用绑定） | 只取指针 | **7 次不变量重载**（`movslq (%rbx)`/`cmpl (%rax)`/`vmovss (%rdi)`/`vfmadd213ss (%r9)`/`movl (%rsi)`/`movl (%r11)`…） |
| **改后（VB2+R1）** | **7 次加载各做一次**（InvCellSize/CellsW/OriginX/OriginY/StateDeath/Length/CellsH）+ 预算 `W-1`/`H-1` | **0 次不变量重载** —— 全寄存器：`cmpq %rdi,%rcx`、`cmpl %esi,(%r9,%rcx,4)`、`vfmadd132ss %xmm0,%xmm1,%xmm3`、`imull %r11d,%r15d`、`lock incl (%rax,%r14,4)` |

⇒ **循环形状与 Unity 同构**（每元素 = 2 过滤载入 + 1 位置载入 + 2 FMA + 2 cvt + clamp + 1 原子）。
⚠ 单独用任一个都不行：`VALUE_BIND=2` 单独用只是把重载**从指针搬到栈**（§31）；`SCALAR_RESTRICT=1` 单独用**不改变**（§30.2）。

### 32.2 ⭐ 但它**几乎不改变 `count`**（对齐档，`applied=15/15` 门控 ✓）

| 趟 | VB2+R1 | §26.9（引用绑定） | Δ |
|---|---|---|---|
| **count** | **1.2754** | **1.291** | **−0.016（≈ 平）** |
| **place** | **1.6736** | **1.835** | **−0.161** ✓ |
| Σ | **3.1916** | ~3.60 | **−0.41** |
| Melee（帧指纹） | 119.40 | 117.97 | 对齐档 ✓ |

⇒ **那 7 次重载不是瓶颈**（L1 热载入藏在循环延迟阴影里）⇒ **§28.2 的"≈0.6 ms 量级核对"被实测否掉**
（教训：**量级推算不能替代实测**；"指令多了 ⇒ 一定慢"在本例不成立）。

### 32.3 ⭐ Unity 侧反汇编：**同样每元素一次 `lock incl`** ⇒ 工等价确认

`Bb0M1FlatCountJob`（hash `df2f83b6931c44aac336f5977eb2e4de_avx2`，`Windows-Intel` AOT obj，主函数 91 行）：
```
16b: lock
16c: incl (%rdx,%r8,4)      ← 每元素一次共享原子自增（全函数仅此 1 处 lock）
```
⇒ **B 的 0.824 ms 与我们的 1.28 ms 是同一个作业、同样每元素一次原子** ⇒ **目标公平，残差不在"谁少了原子"**。

### 32.4 由此得到的判定（C 的当前结论）

| 假设 | 状态 |
|---|---|
| 指令数（7 次不变量重载） | ⛔ **已对齐，且实测无效**（§32.1/§32.2） |
| "Unity 没有每元素原子" | ⛔ **否证**（§32.3） |
| 每元素原子有一处，两边都有 | ✅ 已证 |
| **剩下的是原子/访存的"有效代价"**（coherence/带宽/频率） | ⬜ **唯一未否证的候选**：我们 ≈4.9 周期/元素、Unity ≈3.1–4 ⇒ 我们的原子更贵或访存更差 |
| 频率/前端（整体缩放） | ⚠ 弱证据：`zero` 趟我们**更快**（0.009 vs 0.088）、`prefixPartial/Final` 我们慢 1.4–1.8× ⇒ **不是统一频率差**，而是**带原子的趟变慢、纯写趟不慢** |

**⇒ 下一步（C 收尾）**：① 把 `Count` 的"几何→代价"曲线补全（对齐档 4/16/64/1024 已饱和在 ~64 附近）；② 检查我们的 `Counts` 访问是否仍有一次**跨核冲突**（例如同一步内 Count 与 Place 都在写同一数组 ⇒ 但它们是不同 job，不相交）；③ 内核侧候选只剩"分片计数器/换布局"（需你同意动内核）；④ 否则如实结论：**在"不改内核"约束下 `count` 追到 ~1.28 ms（Unity 0.82）为止，差 1.55×，成因是原子有效代价，不是指令、不是调度。**

### 32.5 器械教训（本轮踩到，必须记住）
1. **逐字字符串里写双引号会让 NativeTranspiler 编译失败**（我写的注释 `"宏为空 ⇒ 无效"` 提前终止了 `@"..."` 模板 ⇒ 7 个错误）。
2. **`dotnet build` 必须看"错误数"**：那两次"6 秒构建"其实是**失败**，不是"已是最新"；真重发 ≥30 s。
3. 重发后 **RVA 键必变** ⇒ 表要按导出名重推（本轮：Count=第 7 键、Place=第 10 键，`applied=15/15` 自证）。
4. 已恢复规范态：gen `16541A4EE5A8`、`const int& Length`、count asm 78 行/7 次重载；部署 `NativeDll=D842C6CE0705` / `NativeTranspiled=9094398E3FA8`。

### 32.6 用**已有测量**把 `count` 残差拆成两块（不再新增实验）

| 量 | 值 | 来源 |
|---|---|---|
| 我们对齐档 Count（认领 1024）**总代价** | **1.28 ms** | §26.9 / §32.2 |
| 我们**去掉原子**后的 Count（同档） | **≈0.92 ms** | 07 §(f) 消融（"去原子后 0.92 **且与粒度无关**"） |
| ⇒ **我们的原子开销** | **≈0.36 ms** | 相减 |
| Unity Count **总代价**（含每元素 `lock incl`，§32.3） | **0.824 ms** | §26.0 |
| ⇒ Unity 的**非原子部分必然 < 0.824** | — | 逻辑推论 |

**⇒ 两块**：
- **原子开销 ≈0.36 ms**（我们，已从 F1/认领几何里压过大半：2.19→1.28）；这一块**只剩"原子有效代价"**（coherence/带宽/频率），不改内核难再压。
- **非原子部分 ≈0.92 ms vs Unity ≤0.824** ⇒ **~0.1–0.2 ms 的差在"非原子"侧**：我们每元素是 **3 次过滤载入**（`index < Length` + `Alive` + `State`），Unity 是 **2 次**（无 `index < Length`）——而这个 `index < Length` 守卫在"调度长度 ≥ 数组长度"的调用里**恒真**。

**⇒ 下一步（需要你点头，因为它动内核）**：
> **实验 K1**：把 `CountCellsJob.Execute` 的 `if (index < Length && Alive[index] != 0 && State[index] != StateDeath)` 在**能证明 `Length ≥ 调度长度`** 时省掉 `index < Length`（或改为"调度长度取 min(length, Length)"的框架侧钳制）。判据：环内过滤载入 3→2、`[M-19] count` 由 1.28 → ~1.1，**且 Melee 不退化**、默认档五段全看。
> 若 K1 也不动 ⇒ 结论收敛为：**在"不改内核数据结构"的约束下，`count` 的 1.55× 全部来自"每元素共享原子"的有效代价，指令/调度都已不是原因**——那时要么接受，要么上"分片计数器/换布局"（更大的内核改动）。

### 32.7 实验 K1（撤 `index < Length` 守卫）：**asm 达标但破坏正确性 ⇒ 守卫是承重的；通解形态是框架侧 K1′**

**做了什么**：把 `CountCellsJob.Execute` 的 `if (index < Length && …)` 改成 `if (Alive[index] != 0 && State[index] != StateDeath)`（对齐 Unity 的 2 次过滤载入）。
**asm 目标达成** ✓：Count 函数 **78 → 74 行**，环内过滤载入 **3 → 2**（只剩 `cmpb (%r15,%rcx)` + `cmpl (%rax),%r9d`），循环形状与 Unity 只剩这一处差别已消除。

**但实测发现它破坏正确性**（对齐档，`applied=15/15`）：

| 量 | K1 | K1 前（认领 1024） |
|---|---|---|
| count | **1.5844** | 1.2754 |
| Melee（帧指纹） | **131.43**（**越出对齐档 118–127 区间**） | 119.40 |
| 整步 | **183.85** | 165.31 |

⇒ **不是"K1 变慢了"，而是这一帧在做不同的工** ⇒ **仿真分叉**。根因（代码级）：
**框架的分片 tiling 在最后一块会越过 `length`**：`tiles × innerBatch > length`
（例：`length=1e6`、`innerBatch=1024` ⇒ `977 × 1024 = 1,000,448`）⇒ 撤掉守卫后内核会**处理越界元素**。
⇒ **判定：这道守卫是承重的；不能只在内核里省掉它。**

**⇒ 通解的正确形态 = K1′（框架侧）**：在分片/合并路径上**把最后一块钳到 `length`**
（`count = min(tileEnd, length) − start`，让"调度长度 ≤ 数组长度"成为**框架保证**）；
**然后**所有同类守卫（`Count`、`Place`、以及其它带 `index < Length` 的内核）才可以撤。
收益是**通解**（不按 job 特判）：**每个**这类内核每元素少一次载入；并且顺带消除"越界 tile"这一潜在 UB。
**判据**：① 新增框架自检"每步 `Σ count == length`"（防越界复发）；② 撤守卫后 `[M-19] count` 1.2754 → ?（预期 −0.1~0.2）；
③ Melee 落回 118–127、默认档五段不退化；④ 原生 9/9 + 全套门禁。

**状态**：K1 **已回退**（守卫 2 处恢复、生成物规范 `16541A4EE5A8`、Count asm 78 行）；两个模块重建并部署
（`NativeDll=D842C6CE0705` / `NativeTranspiled=B48E870F7A72`）。














