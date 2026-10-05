# 09 — `count` 的几何根因：Unity 是「静态大块 + 尾部窃取」，我们是「共享游标发相邻窗口」；以及**每 job 槽索引碰撞**这个框架级缺陷（2026-10-02）

> ⚠ **2026-10-05 追记**：F5（`ENTJOY_TILE_RUN`）**已整体删除**（§2 门控表、§50.3、§59 等处把它当"默认开/已验收"的读法均为**当时状态**）；
> `ENTJOY_CLAIM_SLICE` / `ENTJOY_TILE_STRIDE` 也已删除（**切片机制本身保留，F6 在用**）。当前门控权威表：`docs/public/Gates-and-Flags.md`。
> **本文的根因分析（认领几何 → 共享计数器争用）仍是新会话的起点**，见 `docs/gridsearch/14-接续-Build与Integrate为何输给Unity.md`。

> 承接 `08b-交接-Build逐趟与count对齐.md`。本轮做了三件事：① 用 `tools/BuildPassBench` 把 `count` 的
> 几何依赖做成可复现的扫描（1 格 ≈ 1 s，取代 22 s 的游戏内单点）；② 用子代理在 Unity 侧**实测**了
> `IJobParallelFor` 的线程分配几何；③ 顺藤摸到并修掉了框架「每 job 反馈回路」的三个代码级缺陷。
> **结论先给**：在**同数据、同内核、同 batch（=Unity 的 64）**下，我们只要换成 Unity 的认领几何，
> `count` = **0.86 ms**（Unity 播放档 **0.8242**）⇒ **内核与原子都不是瓶颈**；剩下的 1.55× 全部是
> **认领几何 + 进程内并行效率**，而几何之所以一直只能"按 job 加常量/白名单"打补丁，根因是槽索引碰撞。

---

## 0. 一句话答案（回应"为什么 Unity 就做到 0.82、Melee 还稳"）

**因为 Unity 的两个 job 根本不在同一个工作单元模型里**：

| | Unity（B 栈，实测） | EntJoy（A 栈） |
|---|---|---|
| `count`/`place`/`presence` | 扁平 `IJobParallelFor`，`Schedule(n,64)` | 扁平 `IJobParallelFor` |
| **认领几何** | **调度时把批空间静态切成 `NumJobs = W+1 = 9` 个连续大块**，每线程从自己块头**顺序**向前走、每次领 1 个 batch；自己块跑完才去**别的块的尾部**窃取 | 共享游标 `fetch_add` 发**相邻**窗口（`step = clamp(tileCount/workers,1,cap)`） |
| **Melee** | **`IJobChunk`（7,936 chunk × 128 实体）** ——工作单元本身就是"块"，块间天然不重叠 | **扁平 `IJobParallelFor`**，每元素扫 81 格邻居 ⇒ **worker 在 index 空间越邻近，邻居表/cache 复用越高** |

⇒ Unity 的"贵 job"是 chunk 调度的，**大块切分对它免费**；它的扁平 job 恰好都是"便宜 + 共享计数器 RMW"，
正是大块切分**有利**的那一类。而我们的两种 job 共用一套扁平模型：`count` 要"散"（避开同一条计数器 cacheline）、
`Melee` 要"聚"（共享邻居数据）。同一条静态几何对二者反号（08 §15.3/§23.1 早已实测），于是只能按 job 定 ——
**这就是"为了 Melee 放弃全局平衡"的由来，它不是拍脑袋，而是 119 ms 的 Melee 对 4.5 ms 的 Build 的局部最优**。
真正该做的不是牺牲 Melee，而是把**按 job 的通用反馈回路修好**（下面 §3），或把 Melee 也做成 chunk 调度。

---

## 1. 器械：把几何实验从"游戏内单点"换成"夹具扫描"

| 器械 | 用途 |
|---|---|
| `tools/BuildPassBench`（既有，本轮重新构建） | 同源代码 6 趟 Build，**固定输入**（可 `BENCH_INPUT=<dump>`），`shape=pass` 逐趟独立秒表；**一格 ≈ 1 s**；3 轮 × 30 步中位 |
| `tools/gate-run/bench-count.ps1`（新） | 驱动夹具做 `(claim cap × batch × 变体)` 扫描，输出 `SUMMARY` 一行一格 |
| `tools/gate-run/derive-jobkeys.ps1`（新） | **按导出名重推表键**：空表 + `ENTJOY_JOB_BATCH_TABLE_DUMP=1` 跑一次，按首见顺序写 `jobkeys.txt`（重建后 RVA 必变，手改必翻车——本轮翻车两次） |
| `tools/gate-run/count-probe.ps1`（新） | 游戏内对齐档 `[M-19]` 探针：多臂 × 多 rep、每臂独立 `powershell.exe` 子进程、超时强杀、门控 `applied=15/15` |
| `E:\...\logs_dump\astate_live_s96.bin`（新，由 A 栈 `CPUBATTLE_DUMP_STATE` + `CPUBATTLE_DUMP_AT=96` 导出） | **被测窗口那一步的真状态**（payloadHash `0xCAA85BE4234C09BC`），消除"夹具用 step60、游戏量 step96"的口径错配 |

⚠ **两个器械坑（本轮实测踩到，记下）**：
1. **重建后 RVA 必变** ⇒ 表键必须重推。`NativeTranspiled.dll` 大小不变、MD5 变了也会移位。
2. **泄漏的 Godot 实例会持项目锁** ⇒ 下一次运行"秒退 / 挂到 100–265 s 且 stdout 为空"。
   现在探针每臂开跑前 `Stop-Process Godot*` 并对子进程加 75 s 硬超时。

---

## 2. 证据：同数据、同内核、同 batch，只换几何

**夹具**（`astate_live_s96.bin`，n=1e6，784×448=351232 格，8 worker，`BENCH_BATCHES=64` = Unity 的 `innerloopBatchCount`，3 轮 × 30 步中位）：

| 认领几何 | `tiles_count` | **count (ms)** | place (ms) | zero (ms) | 备注 |
|---|---|---|---|---|---|
| 交错，cap=4（内置默认） | 15625 | **1.4820** | 2.4366 | 0.0583 | 现状 |
| 交错，cap=1024（08 §26.5 的 A+1024） | 15625 | **0.8437** | 1.6076 | 0.0456 | |
| **切片（`CLAIM_SLICE`，= Unity 的"独占一段+空手才偷"）** | 15625 | **0.8629 / 0.8511** | 1.6833 / 1.6744 | | cap=4 / cap=256 |
| 块（`CLAIM_BLOCK`=ceil(tiles/workers)） | 15625 | **0.8837** | 1.6877 | | |
| **交错 cap=1024 + 去掉内核原子**（`BENCH_COUNT=plain` 消融） | 15625 | **0.7853** | 1.6482 | | **原子只值 ≈0.06–0.10 ms** |
| **Unity 播放档**（08 §26.0，游戏内六趟之一） | — | **0.8242** | 1.6667 | 0.0881 | 参照 |

**读法**：
- 换成 Unity 的几何后 **0.86 ↔ 0.824 = 1.05×**，**place 1.68 ↔ 1.667 持平**。
- **"每元素一次 `lock incl`"不是瓶颈**：把它整个删掉只值 0.06–0.10 ms。
- 与 08 §32.6 的"残差 0.36 在原子"**不一致** —— 那个 0.36 是"交错几何下的争用"，不是原子本身的成本。

**worker 扩展曲线**（同夹具，batch=64，cap=1024）：w1 **3.7792** → w2 2.2904 → w4 1.3931 → w8 **0.8840**
（每次翻倍 1.65/1.64/1.58×）。游戏内同一配置（旧口径）：w1 **3.8354**（与夹具**一致**）→ w2 2.4349 → w4 1.8395 → w8 1.4484。
⇒ **单线程成本两侧相同，游戏内只输在并行效率**（进程里多了 Godot/引擎线程 + 每 job 唤醒）。

---

## 3. 框架级根因：**每 job 槽索引碰撞**（本轮修，全绿 9/9）

`JobCostCache` 用 `funcHash & (kJobCostSlots-1)`（256 槽）当索引。但**内核键是 16 字节对齐的 RVA ⇒ 低 4 位恒 0**，
`& 255` 实际只用位 4..7。实测当前 15 个内核**只落进 4 个槽**：

```
slot  0 = PrefixFinal + Integrate + FlowClear
slot 16 = MarkDead + Count + PrefixPartial + FlowPresence
slot 32 = ClearAll + FlowSeedInit + FlowSeed
slot 48 = Spawn + Place + BfsWave + Melee + FlowGrad
```

后果（全部实测）：
1. `slotHash[slot] == funcHash` 校验让"被踩"的 job 读到 **0 = 无样本** ⇒ **JCC 永远学不到它的每元素成本**，
   退回 `tpw=4` 兜底；`slotMode` 分类也被踩。
2. F6（`ENTJOY_CLAIM_ADAPT`）的状态表**原本没有键校验**，直接读/写**别人的**决定 ⇒ 实测 `Place` 永不切片、
   `BfsWave` 每秒翻转上百次（`[CLAIMGEOM]` 刷屏），而 `Count` 侥幸切片。
3. 于是"每 job 的通用反馈回路"从来没真正生效过 ⇒ 只能靠**逐个 job 加常量 / 白名单**（span、claim cap、
   批表、per-job 特判）打补丁 —— **这就是"框架为了特例牺牲全局"的代码级根因**。

### 3.1 本轮改动（`src/NativeDll/`）
| 文件 | 改动 | 理由 |
|---|---|---|
| `JobCostCache.h` | 新增 `SlotOf(h) = (h*2654435761u)>>24`，**11 处槽索引全部改用它** | 散列后 15 个 job 落进 **14 个不同槽**（仅 Integrate/BfsWave 撞 1 次，且有键校验兜底） |
| `JobCostCache.h` | F6 状态改**独立表**（128 槽，乘法散列 + `claimGeomKey` 键校验）+ `Init()` 清表 | 碰撞退化为"偶尔重置"，绝不会读到别的 job 的决定 |
| `JobCostCache.h` | `ClaimSlicedWanted`：成本**细样本优先、粗样本兜底** | 表/强制档下 `jccFine=false` ⇒ 只写 `perElemCoarseNs`，旧实现只读细 EWMA ⇒ cost 恒 0 ⇒ 该档认领几何永不按 job 变 |
| `JobSystem_Scheduler.cpp` | 表/强制档（内批被钉住）时**保留学习键**（= 内核 RVA），且仅在 `ENTJOY_CLAIM_ADAPT=1` 时取 | 让每元素成本在**对齐档**下也有样本；**内批仍由表决定（`cs=forced`）** ⇒ "只镜像 Unity 粒度"的契约不破 |

**门禁**：`run-native-tests.ps1` **9/9 × 两态（`TILE_RUN` 关/开）全过**。

### 3.2 顺带修掉的口径 bug（**会让 build 归因整体偏移**）
游戏内 `CPUBattleSpatialHash.Build` 里 `BpStop(0)` 挂在 `Schedule(zero)` **之后**（不是 `Complete` 之后）⇒
`zero` 只量到**提交**（0.01–0.03 ms），而 `zero` 的**执行**被算进下一趟 `count` 的窗口。
Unity 侧六趟各自 `Complete` ⇒ **两侧不可比**。诊断档下补一次 `h.Complete()` 后实测：

| 口径 | zero | count | place | Build | Melee |
|---|---|---|---|---|---|
| 旧（`BpStop(0)` 在 Schedule 后） | 0.012 | **1.4175** | 2.1230 | — | — |
| 新（诊断档补 Complete） | **0.2613** | **1.1650** | 2.6074 | 4.46 | 125.94 |

⇒ **`zero` 实际是 0.26 ms（≈Unity 的 0.088 的 3×，且是冷 cache 的串行 1.34 MB 写），不是 0.01**；
"Build 赤字 77% 在 count"这个归因有一部分是**把 zero 的执行记到了 count 头上**。

---

## 4. 现状与剩余差距（诚实结论）

| 项 | 值 |
|---|---|
| **夹具（同数据/同内核/同 batch，Unity 几何）** | **count 0.86** ↔ Unity 0.8242 ⇒ **1.05×，持平** |
| 夹具 `place` | 1.68 ↔ 1.667 ⇒ 持平 |
| 夹具原子成本（消融） | **0.06–0.10 ms**（不是 0.36） |
| 游戏内对齐档（诊断档新口径，cap=1024） | count **1.07–1.21**、place 1.9–2.5、zero 0.26–0.31、Melee 119–127 |
| 游戏内 `slice`（全局大块） | count **1.03–1.07**、place **1.69–1.95**、Build **3.4–3.9**，**Melee +3.5～+11**（跨会话不稳） |
| **未闭合的差** | 游戏内比夹具**多 0.2–0.4 ms**，且**与几何无关**（同一数据、同一 batch、同一 cap）⇒ 落在**进程内**：Godot/引擎线程争 CPU + 每 job 唤醒/停靠 |

**未做/未证**：
- F6 的端到端收益**没能在游戏内稳定量出来**：修好槽碰撞后 `[CLAIMGEOM]` 正确切片（Count 0.42ns / Place 2.45ns / Melee 120ns），
  但游戏内 `count/place` 读数被**上述进程内噪声 + 本轮环境不稳定**（偶发挂起、run-to-run 漂移 1.13↔2.03）淹没。
- 本轮未能给出"游戏内 count ≤0.86"的可复现读数；**夹具侧已达到**，游戏内差在**并行效率**而非内核/原子/几何。
- `F6` 的阈值（8/12 ns）与迟滞仍会被**采样式噪声**掀翻（`BfsWave` 每步翻转）；需要"只允许单向 + 冷却 N 批"。

---

## 7. ⚠ 更正："Melee 没对齐"是错的 —— M4 权威档两侧都是扁平 `IJobParallelFor`

§0/§5 曾把 `BattleBenchM2.cs` 头注释里的 *"B 栈是 `IJobChunk`（7,936 chunk × 128）"* 与
`BattleBenchM4Entry.cs` 的 *"chunk 粒度 B=128/7,936 vs A=192/5,291"* 当成**计时档**的差异。逐行核对源码后推翻：

| 证据 | 内容 |
|---|---|
| `BattleBenchM4Entry.cs:388,425` | Melee 段 = `var melee = new Bb0M2MeleeJob{…}; melee.Schedule(n, 0).Complete();` ⇒ **扁平 `IJobParallelFor`，`innerloopBatchCount = 0 ⇒ 1`** |
| `BattleBenchM2.cs:1185` | `Bb0M2MeleeJob : IJobParallelFor`（**不是** `IJobChunk`） |
| `BattleBenchM4.cs:10` | *"B 侧 M1/M2/M3 的跑法都走**扁平工作区**（`CpuHashFlat`/`CpuFlowFlat`/`Bb0M2MeleeJob`），而唯一的 Integrate 实现 `Bb0M0IntegrateJob` 是 `IJobChunk`…故补这一份"* ⇒ 专门造了 `Bb0M4IntegrateFlatJob` 把两侧都变扁平，才能放进同一步循环计时 |
| 上文 "128/7,936" | 描述 **M0 ECS 世界**（另一条、不计时的对比线），不是 M4 权威档 |

⇒ **Melee 在"工作单元 + batch"这一轴上本来就是对齐的**；把它改成 chunk 调度只会**去对齐**。
（真正没对齐过的只有：③ Unity 的**扁平 job 认领几何**是"静态大块 + 尾部窃取"，我们是"共享游标发相邻窗口"。）

---

## 8. 按修正后的协议重测（同会话 A/B，3 对）

`tools/gate-run/ab-aligned.ps1`（新）：A 关 assist（框架默认就是关的，只有 CSBS 把它打开）+ 对齐表 + 逐趟诊断；
B = `W0Player.exe`（M4 权威档，逐趟 `M4,build,*` 已导出）；B 相位 = `A_lastStart − 61`，40 步；每臂带重试（见 §9）。

| 对 | A zero | A count | A place | A Build | A Melee | A 整步 | B zero | B count | B place | B Build | B Melee | B 整步 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 0.535 | 2.869 | 2.304 | 7.88 | 128.80 | 185.4 | 0.096 | 0.850 | 1.817 | 2.761 | 112.15 | 152.3 |
| 2 | 0.594 | 2.862 | 2.516 | 8.49 | 133.85 | 196.5 | 0.110 | 0.894 | 1.918 | 2.885 | 115.23 | 155.9 |
| 3 | 0.520 | 2.783 | 2.258 | 7.83 | 126.45 | 182.0 | 0.109 | 0.928 | 1.973 | 2.937 | 116.47 | 157.4 |

⇒ `count` **3.2×**、`place` 1.26×、`Build` 2.85×、`Melee` 1.15×、整步 1.22×。
**注意 A 侧绝对值明显高于文档上一轮**（Build 7.8–8.5 vs 5.15、zero 0.52–0.59 vs 0.26）⇒ **本会话 A 侧整体退化**，
跨会话比值不可用；B 侧稳定（count 0.85–0.93、Melee 112–116）。

### 8.1 assist 的受控对照（claim=4×3rep、claim=1024×3rep，交错旋转）

| 几何 | assist ON（中位） | assist OFF（中位） |
|---|---|---|
| claim=4 | **2.781** | 2.872 |
| claim=1024 | 2.157 | 2.132（2/3 运行挂在启动期） |

⇒ **关 assist 不带来性能**（claim=4 反而略差 0.09，噪声内）；但「夹具与 Unity 都是无 assist、只有 CSBS 打开」
⇒ **协议上仍然统一为 OFF**（已写进 `ab-aligned.ps1`），只是不要指望它省钱。

---

## 9. 本轮"同类问题"修复清单

| # | 问题 | 处置 |
|---|---|---|
| 1 | 每 job 槽索引用低位 ⇒ 15 个内核只落 4 槽，JCC/F6 互相踩 | ✅ 乘法散列（14/15 不同槽）+ F6 独立键校验表 |
| 2 | 表/强制档只写**粗**样本，F6 只读**细** EWMA ⇒ 该档永不生效 | ✅ 细优先、粗兜底 |
| 3 | 表/强制档把学习键置 0 ⇒ 对齐档无任何成本样本 | ✅ 保留学习键（内批仍由表决定） |
| 4 | `[M-19]` 的 `zero` 只量提交、执行被记进 `count` | ✅ 诊断档补 `Complete()`（`zero` 0.01→0.26） |
| 5 | 重建后 RVA 移位数表手改必错（本轮踩 2 次） | ✅ `derive-jobkeys.ps1` 自动重推 + `jobkeys.txt` |
| 6 | 泄漏 Godot 持锁 ⇒ 下一轮"秒退/挂到 75 s、stdout 全空" | ✅ 器械每臂前清理 + 75 s 硬超时 + 3 次重试 |
| 7 | 部署可能混两版二进制（崩溃现场出现过 22:18 / 09:04 两版） | ⏳ 已核对一致；建议加"跑前比对 build↔deploy 哈希"门 |
| 8 | `_mcp_game_helper` autoload 在**每个**游戏实例里注册 MCP，疑似启动挂起根源（已泄漏 15 个 0-CPU Godot 进程，`Stop-Process` 被拒） | ⏳ **未动**（属用户的 AI 工具链）：测量档建议临时注释 `project.godot` 的 `[autoload] _mcp_game_helper` 并在测后恢复 |

**结论**：§5 的下一步 ①②③ 不变（① 按调用点声明认领几何 ② 查进程内 0.2–0.4 ms ③ 可选统一模型），
但 ③ 已降级为**不需要**（Melee 本来就对齐）；**当务之急是 §9 的 #6/#8：把测量宿主稳定下来**，
否则 A 侧 1.7↔2.9 的漂移会把任何几何/策略的收益淹掉。

---

## 10. ⭐ 两个真 bug（2026-10-02 定位并修）+ 四个被否证的猜想

### 10.1 BUG-1：**静默回退到旧二进制** —— 这才是"同一 DLL 同一 env 却双峰"的元凶

`NativeJobScheduler` 在 `NativeDll.dll` 加载失败时会**回退**到 `.godot\mono\temp\bin\Release\`。
实测那个目录里躺着**两个不同构建**的旧 DLL：

```
Release\NativeDll.dll         1081344  2026-10-01 22:18:39  MD5 3DD0C12B0A8A
Release\NativeTranspiled.dll   100352  2026-10-01 21:23:30  MD5 C62FD0B32A1D   ← 连配对都不一致
Debug\  (当前)                1082368  2026-10-02 09:04:24  MD5 755C54BEF7F2
Debug\  (当前)                 99328   2026-10-02 09:04:26  MD5 E7FFBA5C70A0
```

失败现场（手动跑一次抓到的 stderr）：

```
[NativeJobScheduler] Failed to load ...\Debug\NativeDll.dll: 动态链接库(DLL)初始化例程失败。(0x8007045A)
[NativeJobScheduler] Loaded NativeDll: ...\.godot\mono\temp\bin\Release\NativeDll.dll (UTC: 2026-10-01T14:18:39)
```

⇒ 那些"`applied=0`""count 1.1↔3.2""Build 4.4↔9.3"的运行**根本跑的是另一份旧框架**（没有批表/F1 span/F5），
而**Melee 恰好对它不敏感** ⇒ 正是 `b40efbf` 登记的"未解释的 ~10 ms 双峰"。**不是噪声，是两个二进制。**

**处置**：① 探针加**硬门**：解析 `Loaded NativeDll:`，非 `\Debug\` 或有 `Failed to load` ⇒ 该 run 判废并重试；
② 把 Release 回退目录**同步成与 Debug 同一份**（MD5 已核对 SYNCED）⇒ 回退也不再是"另一个程序"。

### 10.2 BUG-2：**宿主泄漏引擎进程**（探针自己的 bug）

`Godot_v4.7-stable_mono_win64_console.exe` 只是**包装器**，真正的引擎是它启动的子进程
`Godot_..._mono_win64.exe`。旧探针只 `$proc.Kill()` 包装器 ⇒ 引擎被孤儿化，实测累积 **18 个**
`Threads=0 / WorkingSet=0`、`Stop-Process`/`taskkill /F /PID` 均被拒（Access denied）的僵尸壳。

**处置**：超时改为 `taskkill /F /T /PID`（杀树）；并在每臂开跑前清理。
（⚠ 直接启动 GUI 引擎 exe 反而 **4/4 挂到 75 s** ⇒ 仍用 console 包装器 + 杀树。）

### 10.3 被否证的四个猜想（都有数据）

| 猜想 | 实验 | 结论 |
|---|---|---|
| 挂起 = `_mcp_game_helper` autoload | 注释掉 `project.godot` 的 autoload 后 4 次 | ⛔ 仍 1/4 挂（`project.godot` 已恢复） |
| 挂起/崩溃 = F5 tile-run 融合 | `ENTJOY_TILE_RUN` 开 6 次 / 关 6 次 | ⛔ 失败率同为 ~40%（2/6 vs 3/6） |
| 挂起 = 批表路径 | 不带表的 `def` 臂 | ⛔ 同样挂（2/4） |
| "慢模式" = CPU 降频（笔记本） | `% Processor Performance` 采样 | ⛔ ≈90%（88–91%），无 2× 降频 |

### 10.4 剩余阻塞（需要你动手，我做不了）

挂起点固定在 `[CPUBattleEcs] JobSystem assist=开` 之后、`步进线程已启动` 之前；随着僵尸壳累积，
挂起率从 ~40% 升到现在 ~90–100%。**手动跑成功的那一次证明游戏本身是健康的**
（Build 5.84–6.02 / Melee 82–116 / 整步 128–158 ms）。⇒ 清掉那 18 个僵尸壳（需管理员权限或重启）后，
用已修好的探针（杀树 + 回退硬门 + 回退路径同步）复测即可。

---

## 11. ⭐⭐ BUG-3（真凶）：`JobCostCache::Init` **越界写** —— 挂起 + `Internal CLR error` 的根因

§10.4 里"需要重启才能清掉僵尸壳"的结论**被推翻**：挂起不是环境，是 **9ba7ba2 里我自己引入的越界写**。

**根因（代码级）**：F6 的两张独立表
```cpp
static constexpr uint32_t kClaimGeomSlots = 128;      // ← 128
std::atomic<uint32_t> claimGeomKey[kClaimGeomSlots];
std::atomic<uint8_t>  claimGeomState[kClaimGeomSlots];
```
被误写进了**既有的** 256 次初始化循环：
```cpp
for (int i = 0; i < kJobCostSlots /* =256 */; ++i) {
    ...
    claimGeomState[i].store(0);   // ⚠ i ∈ [128,256) 越界写！
    claimGeomKey[i].store(0);
}
```
⇒ 每次进程启动就踩坏 `JobCostCache` 相邻成员/相邻全局量（成员数组之间没有边界检查，编译器不报）。

**对照实验（同机、同 env、同命令，各 3 次）**：

| DLL | 结果 |
|---|---|
| 旧版（本修复之前那版） | **3/3 正常退出 23 s、有 `[M-19]`** |
| 含越界写那版 | **0/3：全部挂起 → 最终 `Fatal error. Internal CLR error. (0x80131506)`** |

且失败现场**每次都发生在回退到 `Release\` 之后**——因为 Debug 路径那份 DLL 的 `DllMain` 加载失败
（`0x8007045A`，**与本 bug 无关，是另一个独立问题**），于是真正执行的是 Release 那份；两份是同一个 bug，
所以"换目录"救不了。

**修复**：把两张表的清零**只**放在 `i < kClaimGeomSlots` 的独立循环里，并从 256 次循环中删除；
注释写明"128 < 256 ⇒ 绝不能并进上面那个循环"。（`5bdc50a`）

**验收（对齐档，`applied=15/15`、`countTiles=15625`、`countApplied=64`）**：

- **连续 8/8 A 侧运行全部正常退出（22.7±0.1 s）并产出 `[M-19]`，零挂起** ✓（目标达成）
- 对齐档读数（新口径）：`claim=4` → count 中位 **1.648**；`claim=1024` → **1.151**；
  place ≈2.47、zero ≈0.25、Build ≈4.4–5.0、**Melee ≈119–122**（落在帧门 118–127 内）

**教训（写进纪律）**：给"按 job 的缓存"加**第二张表**时，索引上界必须与那张表自己的常量绑定；
共用常量（`kJobCostSlots`）只属于原来那张表。本轮 `kClaimGeomSlots(128) < kJobCostSlots(256)`
正好让越界落在同一对象内部 ⇒ 不崩在写的那一行，而是几十秒后崩在别处。

---

## 12. 宿主稳定后的同会话 A/B（3 对，双侧 ±1%）—— Build 赤字 **76% 在 count+place**

`tools/gate-run/ab-aligned.ps1 -Reps 3`（A 关 assist + 对齐表 + `applied=15/15`；B = W0Player M4 权威档，
相位对齐 `A_lastStart−61`，40 步）。3 对中位：

| 段/趟 | A（EntJoy） | B（Unity） | A/B | Δ(A−B) |
|---|---|---|---|---|
| zero | 0.235 | 0.092 | 2.55× | +0.143 |
| **count** | **1.637** | **0.830** | **1.97×** | **+0.807** |
| prefixPartial | 0.048 | 0.046 | 1.04× | +0.002 |
| prefixFinal | 0.188 | 0.135 | 1.39× | +0.053 |
| **place** | **2.445** | **1.676** | **1.46×** | **+0.769** |
| **Build（段，权威）** | **4.86** | **2.78** | **1.75×** | **+2.08** |
| Flow | 35.05 | 32.92 | 1.06× | +2.13 |
| **Melee** | **119.35** | **115.09** | **1.04×** | +4.26 |
| Integrate | 4.07 | 2.58 | 1.58× | +1.49 |
| 整步 | — | 155.13 | — | — |

Σ逐趟Δ = 1.774、Build Δ = 2.08 ⇒ 残差 +0.31（hostRewrite/记账）。
**赤字归属：count 39%、place 37%、zero 7%、prefixFinal 2.5%、残差 15%。**

与 08 §26 对照：那轮 count 3.29× 且占 77%；本轮 **count 1.97×、count+place 占 76%** ——
count 变好（F1 span/F5 之后），而 **place 的 0.77 ms 此前被 count 的大赤字遮住了**。

### 12.1 ⚠ 本轮踩到并修掉的口径 bug（曾导致我得出错误结论，已作废）

**B 侧六行 `M4,build,*` 的分子是 `warmup+steps` 次迭代的累加，分母却是 `per = 1/steps`**
（`BattleBenchM4Entry.cs:590` 与 `680-685`），而 `M4,seg,build_ms` 的 `sBuild` **只累加正式步**
⇒ 六行被放大 **(warmup+steps)/steps**（本轮 warm=23~26 ⇒ 1.58~1.68×）。
08 §4.3 早已写明"除以 (warmup+steps) 才与 seg 对齐（自证 0.14%）"，**我的器械漏了这步**。

- 未修正时：B count 读作 1.334、place 2.674 ⇒ 我一度写出"**A 每步重算、B 早停 ⇒ 工作量差、
  按次重算已持平**"的结论 —— **该结论作废**（`EarlyStop` 是 Melee 内核的环序早停，
  与哈希重建无关；M4 每步无条件调 `BuildTimed`，A 的 `SpatialHashSystem` 也是每步调 `Build` ⇒ **工作量等价**）。
- 修正后自证：六行之和 **2.656 ↔ `seg,build_ms` 2.65（0.2%）** ✓
  （修复方式：`scale = steps/(warmup+steps)`，见 `ab-aligned.ps1`）

**次要坑（已记）**：`ab-aligned.ps1` 里插了中文注释后 `$scale` 变空 —— **PS 5.1 用 `-File` 按 ANSI/GBK
读脚本，非 ASCII 字节会吞掉下一条语句** ⇒ 该目录脚本必须保持 ASCII-only（文件头已有此约定）。

### 12.2 下一步（由修正后的数据确定）

1. **count + place = Build 赤字的 76%**，而夹具已证：换成 Unity 的认领几何后
   `count 0.86`（Unity 0.824）、`place 1.68`（Unity 1.667）—— 两个都到齐。
   ⇒ **落地"按调用点声明的认领几何"（`ClaimPolicy{Spread|Adjacent}`）就是下一步**，并在夹具验收。
2. `zero`（2.55×，+0.14）与 `Integrate`（1.58×，+1.49，但不在 Build 段）是第二梯队。
3. 仍待查：Debug 路径 `DllMain` 失败（`0x8007045A`）—— 两路径同码 ⇒ 当前无害，但应查清。

---

## 13. ⭐ 落地：**按调用点声明的认领几何**（`ClaimPolicy`），Build 赤字收回 66%

### 13.1 机制（框架侧）

表项加**第四字段 = 认领几何**（键即调用点）：

```
ENTJOY_JOB_BATCH_TABLE="<key>:<batch>[:<claim>][:s|a],..."
    s = Spread    ：每 worker 独占一段连续 tile，空手才去别段尾部窃取（= Unity 的静态大块 + 尾部窃取）
    a = Adjacent  ：共享游标发相邻窗口（现状；Melee 靠它吃邻居表/空间复用）
    缺省 = Auto   ：走全局 env 与 F6 学习  ⇒ **表里不写几何时逐位不变**
```

实现（`src/NativeDll`，本轮提交）：
| 位置 | 内容 |
|---|---|
| `JobSystemInternal.h` | `kClaimGeom{Auto,Spread,Adjacent}`；`JobBatchTableEntry` 加 `geom`；`LookupJobGeom`；BatchState 加 `claimGeomOverride` |
| `JobSystem.cpp` | 解析第四字段（`s/S`、`a/A`，可空）；`LookupJobGeom`；`[JOBBATCHTBL]` dump 行加 `geom=` 自证 |
| `JobSystem_Scheduler.cpp` | **优先级解析器** `ResolveClaimSliced(batch, allowAdaptive)`：① 调用点声明 > ② F6 按 job 学习（仅 General 路）> ③ 全局 env > ④ 交错；三个 `InitSliceCursors` 调用点全部改走它 |
| `JobSystem_Tiles.cpp` | `AcquireBatchStorage` 里复位 `claimGeomOverride`（防陈旧值，与 `claimCapOverride` 同款坑） |

**这是 docs 08 §26.5/§27 设计的"调用点声明 + 全局默认不变 + 缺声明回退"的落地**；产品 API 形态
就是把它从 env 表提升为 `ScheduleParallelFor(..., ClaimPolicy)` 参数（键 → 显式传参），
**F6 降级为"未声明时的自动调参"**（而不是唯一机制）。

### 13.2 实测（游戏内对齐档，`applied=15/15`，3 rep 中位）

| 臂 | count | place | **Build** | Melee |
|---|---|---|---|---|
| 现状（Auto，claim 4） | 1.636 | 2.482 | **4.91** | 120.1 |
| **声明 Spread（claim 4）** | **1.065** | **1.859** | **3.59** | **120.1** |
| 声明 Spread（claim 1024） | 1.094 | 1.999 | 3.96 | 118.7 |
| 只声明 Place（Spread） | 1.153 | 1.999 | 3.83 | 119.5 |

⇒ **只需声明几何**（不必再叠 claim 覆盖）：`count 1.636→1.065`、`place 2.482→1.859`、
**`Build 4.91→3.59`（−27%）**，而 **Melee 不动** ⇒ 判据"count/place 降、Melee 不退"成立 ✓

### 13.3 同会话 A/B（3 对，`ab-aligned.ps1 -DeclareSpread`）

| 趟 | A（声明 Spread） | B（Unity） | A/B | 声明前 A/B |
|---|---|---|---|---|
| zero | 0.269 | 0.095 | 2.83× | 2.55× |
| **count** | **1.096** | **0.843** | **1.30×** | 1.97× |
| prefixPartial | 0.041 | 0.045 | 0.91× | 1.04× |
| prefixFinal | 0.193 | 0.136 | 1.42× | 1.39× |
| **place** | **1.895** | **1.711** | **1.11×** | 1.46× |
| **Build（段）** | **3.52** | **2.82** | **1.25×** | **1.75×** |
| Flow | 35.85 | 32.99 | 1.09× | 1.06× |
| **Melee** | **118.70** | **116.71** | **1.02×** | 1.04× |
| Integrate | 4.00 | 2.60 | 1.54× | 1.58× |

⇒ **Build 赤字 +2.08 → +0.70 ms（收回 66%）**，`count` 1.97×→1.30×、`place` 1.46×→1.11×，
**Melee 1.04×→1.02×（不退）**。剩余赤字里 `zero`(+0.17) 与 `prefixFinal`(+0.06) 占大头，`count`(+0.25) 仍在。

### 13.4 顺带修掉的器械坑（第三次同类）

`gate-run` 脚本被 `edit` 工具重写后会**丢掉 BOM**，而 PS 5.1 用 `-File` 按 ANSI/GBK 读 ⇒
非 ASCII 注释会**吞掉后面的语句**（本轮表现为"新臂被判为 unknown arm"；上一轮表现为 `$scale` 变空）。
**处置：这 5 个脚本里的非 ASCII 全部清除（只存在于注释），从此不依赖 BOM。**







---

## 14. 收口二：`Debug` 路径 `DllMain` 失败（`0x8007045A`）—— 取证、加载语义修复、复测

本轮回答三件事：① `ClaimPolicy` 是什么（§13，此处不重复）；② `0x8007045A` 怎么处理（本节）；
③ 修完之后的性能对比（§14.5）。

### 14.1 取证：四条证据（**其中第 2 条决定了修法**）

1. **报错原文**（`tools/gate-run/**/[A-*].stdout.txt.err.txt`）：
```
[NativeJobScheduler] Failed to load ...\.godot\mono\temp\bin\Debug\NativeDll.dll:
    Unable to load DLL '...': 动态链接库(DLL)初始化例程失败。 (0x8007045A)
[NativeJobScheduler] Loaded NativeDll: ...\Release\NativeDll.dll (UTC: 2026-10-01T14:18:39.2531214Z)
```
2. ⭐ **失败那次的 stderr 里 `[SIMD]`/`[JOBBATCHTABLE]` 已经打出来了**（完整样本见下）。这两行来自 DLL 的
   **全局动态初始化器**：`Exports.cpp` 的 `struct SimdInfo { SimdInfo(){...} } g_simdInfo;` 与
   `JobSystem.cpp` 的 `g_jobBatchTableCount = []() -> uint32_t {...}()` —— 它们都在 **`_CRT_INIT`（= DllMain 期）**
   里跑。⇒ **失败发生在"加载期静态初始化已经跑过之后"**，是"整个模块载不进来"，不是"某行日志没打出来"。
```
[SIMD] AVX2 8-wide                              <- 失败那次的静态初始化输出
[JOBBATCHTABLE] entries=15 dump=1 (...)
[NativeJobScheduler] Failed to load ...\Debug\NativeDll.dll: ... (0x8007045A)
[SIMD] AVX2 8-wide                              <- 紧接着改载 Release：同一份字节，成功
[JOBBATCHTABLE] entries=15 dump=1 (...)
[NativeJobScheduler] Loaded NativeDll: ...\Release\NativeDll.dll (UTC: 2026-10-02T01:04:24.1916223Z)
```
3. **隔离实验**（新增器械 `tools/gate-run/loadrace.ps1`）：
   - `-Mode loader`：同一份 `NativeDll.dll` 用 `LoadLibraryExW`+`FreeLibrary` 反复载卸 **300×2 = 600 次**，
     Debug / Release 两个路径各 300 ⇒ **600/600 成功、0 失败** ⇒ 不是文件内容问题；
   - `-Mode writer`：让另一个进程在这段时间里反复重写同一文件 ⇒ 写侧拿到 **`ERROR_SHARING_VIOLATION`**
     （映像被映射时 Windows 不允许写）⇒ **"载入读到写了一半的文件"这条不成立**。
   （另用 `tools/gate-run/pe-deps.ps1` 读了 PE：`NativeDll.dll` 的 19 个依赖全是系统 DLL（d3d11 / D3DCOMPILER_47 /
   MSVCP140 / VCRUNTIME140 / api-ms-win-crt-*），没有"我们自己的相邻 DLL"，所以也不是依赖缺失/错版。）
4. **历史统计**：`tools/gate-run` 下 **131 个 A 侧 stderr 文件里 50 个含 `Failed to load`**；按二进制分组，
   失败**全部**落在 2026-10-02 11:44 之前构建的 DLL 上（09-16 / 10-01T14:18 / 10-02T01:04），而 11:44（`5bdc50a`）
   与 12:10（`52ceca8`）两版共 **62 次运行 0 次**；今天修复后的新二进制（12:46）**21 次运行 0 次 `Failed to load`、
   0 次挂起**（其中 1 次 ~5 s 早退、无 `[M-19]`，见 §14.6-2 ⇒ 有效 20/21）。

⇒ 「**加载期瞬态失败**」这一条是**实测**；「上游是哪一步返回 FALSE」**未定位**（见 §14.6 的诚实标注）。

### 14.2 真正的危害不在"这次载入失败"，而在框架的**静默回退**

`0x8007045A` 的后果本来只是"这一次载入没成功"——同一份字节紧接着就能载入（证据 2）。但旧代码在首选路径
失败后会**静默去载另一个目录的那份 DLL**，而当时 `.godot\mono\temp\bin\Debug` 与 `...\Release` 里是
**22:18 / 21:23 两份不同的二进制** ⇒ 一次测量变成"两台不同机器"，还伴随 `applied=0` 与假的 ~10 ms 双峰（§10.1）。
所以本轮修复的重点是**让回退不可能静默、让瞬态失败不再需要回退**。

### 14.3 本轮改动

| 侧 | 文件 | 改动 |
|---|---|---|
| C#（框架） | `src/EntJoy.Jobs/Native/NativeJobCore.cs` | ① 首选（意图）路径失败 ⇒ **同一路径重试 3 次**（60/120 ms 退避），每次打印 `Trying …` 与 **Win32 错误码**；② 只有首选彻底失败才看别的目录，且**必须长度 + SHA256 字节等价**；③ 字节不等价**默认拒绝**（要恢复旧行为须显式 `ENTJOY_NATIVE_ALLOW_MISMATCHED_FALLBACK=1`）；④ 新增 `dll self-proof:` 自证行（intended / attempts / fallback / size / sha256）⇒ **任何回退都会留在日志里** |
| native | `src/NativeDll/{JobSystemInternal.h,JobSystem.cpp,Exports.cpp}` | 加载期 banner（`[SIMD]` + `JobSystem.cpp` 里 15 处 `[]{...}()` 初始化器里的 `std::fprintf(stderr,…)`）改为**写内存缓冲** `LoadBannerAppend`，由 `JobSystem_Initialize()` 一次性 flush。理由：`_CRT_INIT` 持有 loader lock，**DllMain 期不应做 I/O**（stderr 可能是父进程管道；且加载期失败会整体表现为"整个 DLL 载不进来"）。**文本逐字节不变**，只有时机变了（实现放 `JobSystem.cpp` 是因为 `tests/NativeDll.Tests` 的 vcxproj 只编 8 个核心 .cpp、不含 `Exports.cpp`） |
| 测试 | `tests/NativeDll.Tests/JobSystemTests.cpp` | **两个"槽碰撞"用例写的是旧索引**（`h2 = h1 + kJobCostSlots` + `h1 & (slots-1)` 断言）；10-02 的 `SlotOf` 改成 Knuth 乘法散列后，它们测的是**根本不存在的碰撞**（假失败/假绿）。改为用框架自己的 `SlotOf` 构造真碰撞；**新增** `TestJobCostCacheSlotIndexNotLowBits`（16 字节对齐键族的散列度必须 ≥48；旧实现只有 4） |
| 器械（本地，`/tools/*` 不入库） | `gate-run/{loadrace,pe-deps}.ps1`、`count-probe.ps1`、`ab-aligned.ps1` | 载入竞态探针 / PE 依赖·TLS 读取器；**两道门**：① `count-probe` 一次运行若没有 `[M-19]`（早退/挂起）→ 重试，并在 CSV 末尾加 `attempts` 列（旧行为会把这种运行记成全 0，是**静默丢数据**）；② `count-probe`/`ab-aligned` 都要求"**必须**载入 `…\Debug\NativeDll.dll`"——没有 `Loaded NativeDll:` 行（= 静默退回托管后端）或载了别的目录 → 判为坏样本重试（否则会静默读成"A 很慢"） |

### 14.4 验收

- **原生测试（本轮重新编译，不是 10-01 的旧 exe）**：`ENTJOY_TILE_RUN` off/on 两态 **9/9 rc=0**；
  `JobSystemTests` 里 `JobCostCacheSlotIndexNotLowBits (distinct=52)`、`JobCostCacheCollisionReuse`、
  `JccCollisionSlotConcurrent` 全过。
  ⚠ **顺带发现上一轮的一个口径漏洞**：`5bdc50a` 那条"全绿 9/9"是在 **10-01 19:34 编译的测试 exe** 上跑的 ——
  那个 exe 里还是**旧 `SlotOf`**，所以"槽索引改成乘法散列"这一步**从未被测试覆盖**；今天重新编译才暴露
  （旧用例 + 新实现 = 假失败）。修完用例后 9/9 才是真的 9/9。
- **加载路径自证**（真实一次运行，`Debug` 路径、零回退、零重试）：
```
[NativeJobScheduler] Trying NativeDll: ...\Debug\NativeDll.dll (attempt 1/3)
[NativeJobScheduler] Loaded NativeDll: ...\Debug\NativeDll.dll (UTC: 2026-10-02T04:46:58.8477216Z)
[NativeJobScheduler] dll self-proof: intended=...\Debug\NativeDll.dll attempts=1 fallback=none size=1082368 sha256=BC09C7252C0F80BC
[SIMD] AVX2 8-wide                    <- 现在由 JobSystem_Initialize flush（顺序变了、文本没变）
[JOBBATCHTABLE] entries=15 dump=1 (auto-batch: table hit wins over ENTJOY_FORCE_INNER_BATCH; JCC bypassed)
```
- **稳定性（8 连跑，最终二进制，对齐档 + 声明 Spread + claim 4）**：`applied=15/15`、`attempts=1`（**零重试**）、
  WALL 22.6–22.9 s、**8/8 正常退出且都产出 `[M-19]`** ⇒ 挂起 / 早退 / 回退三类各 0 次。8 rep 中位：
  zero 0.269 / count **1.166** / place **2.039** / Build **4.09** / Melee **123.1**
  —— 与前一节 3 rep 那轮（0.269 / 1.110 / 2.074 / 4.11 / 124.9）一致 ⇒ 该档读数可复现。
- **`ClaimPolicy` 受控复测**（最终二进制，同会话，`count-probe` 3 rep，两臂都声明 claim=4 ⇒ 只差几何）：

| 臂（claim=4，同会话） | count | place | **Build** | Melee |
|---|---|---|---|---|
| Auto（不声明几何） | 1.79（1.8285 / 早退作废 / 1.7463） | 2.62 | **5.31** | 127.2 |
| **声明 Spread** | **1.1095**（1.2100/1.0535/1.1095） | **2.0741** | **4.11**（4.11/4.12/3.94） | **124.9** |

⇒ 方向与量级与 §13.2 一致（count −38%、place −21%、Build −23%、**Melee 不退**），确认 ClaimPolicy 在最终
二进制上仍成立。⚠ 其中 Auto 臂 rep2 **早退**（WALL_SEC 5.1 s、stderr 干净、stdout 停在"步进线程已启动"之后、
无 `[M-19]`）= §14.6 记录的那个宿主偶发；该 rep 已作废，也正是上面那道新门的由来。

### 14.5 复测：对齐档 A/B（`ab-aligned.ps1 -DeclareSpread`，3 对/轮，A=EntJoy、B=Unity `W0Player`）

三轮同会话 A/B（中位）。**跨会话绝对值不可比**（本机在这三小时里整体漂移约 5–10%，两侧同向），
可比的只有**同轮内的 A/B 比**：

| 趟 | S1 修复前<br>A / B (比) | S2 修复后①<br>A / B (比) | S3 修复后②<br>A / B (比) | A/B 比的三轮区间 |
|---|---|---|---|---|
| zero | 0.278 / 0.109 (**2.55×**) | 0.309 / 0.124 (2.49×) | 0.328 / 0.125 (**2.62×**) | 2.49–2.62× |
| **count** | 1.106 / 0.959 (**1.15×**) | 1.239 / 0.992 (1.25×) | 1.268 / 0.989 (**1.28×**) | 1.15–1.28× |
| prefixPartial | 0.044 / 0.062 (0.71×) | 0.064 / 0.066 (0.97×) | 0.065 / 0.059 (1.10×) | 0.71–1.10× |
| prefixFinal | 0.187 / 0.152 (1.23×) | 0.217 / 0.156 (1.39×) | 0.204 / 0.166 (1.23×) | 1.23–1.39× |
| **place** | 1.958 / 2.045 (**0.96×**) | 2.300 / 2.167 (1.06×) | 2.287 / 2.145 (**1.07×**) | 0.96–1.07× |
| **Build（段）** | 3.87 / 3.11 (**1.24×**) | 4.73 / 3.26 (1.45×) | 4.74 / 3.24 (**1.46×**) | 1.24–1.46× |
| Flow | 39.12 / 33.72 (1.16×) | 41.77 / 33.72 (1.24×) | 42.76 / 34.16 (1.25×) | 1.16–1.25× |
| **Melee** | 124.48 / 117.29 (**1.06×**) | 131.34 / 120.77 (1.09×) | 130.10 / 120.73 (**1.08×**) | 1.06–1.09× |
| Integrate | 4.27 / 2.80 (1.53×) | 4.88 / 2.99 (1.63×) | 5.20 / 2.92 (**1.78×**) | 1.53–1.78× |

注：S1 = 修复前的 `52ceca8` 二进制（12:10）；S2/S3 = 修复后的二进制（12:46），S2 的 A 侧 rep1 `Build=15.09`
是一次干扰尖峰（同轮 flow/Melee 也抬高、B 侧同样抬高，中位不受影响）。另有 **S4 = 1 对相位对齐复跑**
（`warm=24`、`scale=0.625`，同时用来验证改完器械门后 A 侧仍必须是 `Debug` DLL、`applied=15/15`）：
zero 2.42×、count 1.33×、**place 1.10×**、**Build 1.37×**、Flow 1.13×、**Melee 1.03×**、Integrate 1.70×
⇒ 全部落在上表区间内。

**当前结论（对齐档、已声明 Spread）**：

1. **`place` 已到/接近平价**（0.96–1.07×，其中一轮 A 更快）——这是 §13 里最大的单项反转。
2. **`count` 仍慢 1.15–1.28×（+0.15~0.28 ms）**；夹具上 count 在 Unity 的几何下是 0.84–0.86（= 平价），
   所以这 0.15–0.28 是**进程内每批固定开销**，不是内核或几何（§12.2 第 2 条那条线仍未解）。
3. **`Melee` 1.06–1.09×** ⇒ 从 §12 的 1.02× 看是"持平但略偏慢"，量级在跨会话漂移带内 ⇒ **判据"count/place 降、Melee 不退"仍成立**。
4. **剩余赤字（以 S3 为例，Build 段 +1.50 ms）**：`zero +0.20`（2.62×，最大单项比值）、`count +0.28`、
   `place +0.14`、`prefixFinal +0.04`、`prefixPartial ±0`，其余 `~+0.8` 是 **A 侧诊断器械自带的每趟 `Complete()` +
   窗口记账**（`CPUBATTLE_DIAG_BUILDPASS=1`，B 侧没有对应器械）——要更干净的段口径得先让器械两侧对称。
5. **Build 赤字从 §12 的 1.75× 收到 1.24–1.46×**；`Flow`（1.16–1.25×）与 **`Integrate`（1.53–1.78×）**
   是新的第二梯队，`zero` 是比值最大的一项。

### 14.6 未决（诚实标注）

1. **`0x8007045A` 的上游根因未定位**：本轮做到了"证据充分（失败发生在加载期静态初始化之后）+ 复现不了
   （600/600 隔离载入成功、62+13 次运行 0 失败）"，因此**无法做对照实验判定**是 Windows loader 的哪一步
   （CRT 的 process-attach、TLS 回调、还是并发加载竞争）返回 FALSE。可行取证路线（未做）：`gflags +sls`
   （loader snaps）或 ETW `Microsoft-Windows-Kernel-Loader`，并在**复现窗口内**抓。
   本轮给的是"**失败可归因 + 不可能静默换二进制 + 同路径重试**"，不是"根因已修"。
2. **宿主偶发早退**：1/6 运行在 ~5 s 干净退出（stderr 无错、stdout 停在步进线程启动之后、无 `[M-19]`）。
   已加器械门（重试 + `attempts` 列）以免把这种运行记成全 0；根因未查（与 §1 记载的宿主偶发同类）。
3. `zero`（2.5–2.6×，+0.2 ms）与 `Integrate`（1.5–1.8×，+1.5–2.3 ms）**完全没动**：前者是冷 cache 串行
   1.34 MB（§5 第 4 条），后者不在 Build 段、但在整步里。

---

## 15. ⭐ 通解：几何"**按调用点声明**"（对齐档）+ "**按 job 学习**"（默认档）—— "一条几何套所有 job"被数据否证

### 15.1 否证：统一几何在**两个档**里都对 Melee 反号

新增"几何专用表"器械 `<key>:::<s|a>`（只声明几何、**不碰内批** ⇒ 默认档仍由 JCC 选粒度）。同会话 3 rep 中位：

**对齐档**（批表镜像 Unity 的 innerloopBatchCount）：

| 臂 | count | place | **Build** | Melee |
|---|---|---|---|---|
| Auto（不声明） | 1.70 | 2.53 | 5.16 | 123.6 |
| Spread（只声明 count+place） | 1.13 | 1.87 | 3.80 | 123.7 |
| **Spread（全 15 个 kernel）** | 1.14 | 1.83 | 3.99 | **135.8（+10%）** |

**默认档**（无批表 ⇒ JCC 自选内批，thick tile）：

| 臂 | count | place | **Build** | Melee |
|---|---|---|---|---|
| 全 Adjacent（= 改动前的默认） | 2.539 | 2.201 | 5.47 | 111.9 |
| **全 Spread** | **0.936（−63%）** | **1.926（−12%）** | **3.68（−33%）** | **124.5（+11%）** |

⇒ Spread 的**收益与代价在两个档里完全一致**：对"元素便宜 + 共享原子计数器"的 `count`/`place` 是大赢，
对邻居扫描 `Melee` 是 +10~11% 的输。**"一条几何套所有 job"不通**，通解必须是"**按调用点定几何**"，且要在**两个档都生效**。

### 15.2 落地的两条腿

**① 声明**（对齐档用；该档要求"无自适应"）：
`ClaimPolicy{Auto,Spread,Adjacent}` + `Schedule(…, ClaimPolicy)` 贯穿
`JobExtensions → JobScheduler → NativeJobScheduler → NativeJobCore → 新导出 JobSystem_ScheduleParallelForBatchEx`
（旧导出保留 ⇒ 老 NativeDll 自动降级、**无 ABI 破坏**；`Auto` 仍走原导出 ⇒ 零回归）。
native 侧优先级：**批表第四字段（诊断覆盖，可做同会话 A/B）> API 声明 > F6 学习 > 全局 env > Adjacent**。
自证：`[NativeJobScheduler] ClaimPolicy API: JobSystem_ScheduleParallelForBatchEx present (per-call-site claim geometry)`。

**② 学习**（默认档/产品档用；无 per-job 特判、无 env）：F6 按**每个 job 学到的每元素成本**选几何。
本轮把它的内置默认**从"关"改成"开"**（证据如下），`ENTJOY_CLAIM_ADAPT=0` 可复现旧行为：

| 默认档，同会话 3 rep 中位 | count | place | **Build** | Melee |
|---|---|---|---|---|
| F6 关（旧默认） | 2.298 | 2.380 | 5.37 | 117.8 |
| **F6 开（新默认）** | **1.094（−52%）** | 2.418（≈持平） | **4.35（−19%）** | 114.1（不退） |

（对齐档不受影响：器械显式 `ENTJOY_CLAIM_ADAPT=0`，且批表声明优先级更高。）

### 15.3 ⚠ 踩到并记录的 transpiler 约束：**不能给 `Schedule()` 加实参**

我最初把声明写成了调用点实参 `countJob.Schedule(length, 0, h, ClaimPolicy.Spread)` —— **这会掉出原生内核**：
transpiler 对 `job.Schedule(len, batch[, dep])` 的调用点改写**只认这个形状**，多一个实参就不再匹配 ⇒ 该 job 回落到
**托管回调 thunk**（`Marshal.GetFunctionPointerForDelegate`）。三条实测证据：

1. `[JOBBATCHTBL]` 里 Count/Place/Melee 的 key 从**模块内 RVA**（`0x1950 / 0x10910 / 0xc310`）变成**堆地址**
   （`0x39a99140 / …170 / …1a0`，三者相差 0x30）—— key 是"派发入口的 RVA"，堆地址 ⇒ 已不在任何原生模块里；
2. `applied` 由 15 掉到 **12**（批表的 15 条有 3 条再也匹配不上）；
3. 默认档 place 2.2 → **5.3 ms**、Melee 108 → **140 ms**（托管转换的每 tile ~3 µs 开销，见 `BindingsGenerator.cs:353` 的注释）。

⇒ **"声明进代码"的正确形态是 job 结构体上的属性**（`[ClaimGeometry(ClaimPolicy.Spread)]`，由 transpiler 读并在**生成的
`Schedule_{Job}` 体内**透传 geom），**不是**改调用形状。当前 CSBS 已把这两处**回退**（声明仍由批表第四字段表达），
属性路线 = 下一步第一件事（实现点：`NativeTranspiler/BindingsGenerator.cs` 的 `Schedule_{Job}` 生成段）。

### 15.4 三档性能对比（**默认 / 对齐 / Unity**）

协议：同会话 A/B（B = Unity `W0Player` M4 权威臂，相位对齐，`M4_STEPS=40` + 逐步口径修正）；A 侧 `ENTJOY_JOB_WORKERS=8`、
`ENTJOY_ASSIST=0`（框架默认就是 off，协议口径见 §8.1）；**默认档 = 出厂默认（无批表、JCC 自适应内批、F6 默认开）**，
**对齐档 = 逐 job 镜像 Unity 内批 + 关自适应（JCC 被批表旁路、`ENTJOY_CLAIM_ADAPT=0`）+ count/place 声明 Spread**。
中位（3 rep；`[M-19]` 逐趟 / `[M-1]` 分段 / 步均总计）：

| 指标 (ms) | **EntJoy 默认** | **EntJoy 对齐**（同 BatchSize、无自适应） | **Unity** | 默认/Unity | 对齐/Unity |
|---|---|---|---|---|---|
| zero | 0.229 | 0.278 | 0.103 | 2.22× | 2.70× |
| **count** | 0.898 | 1.176 | 0.855 | **1.05×** | 1.41× |
| prefixPartial | 0.040 | 0.050 | 0.049 | 0.82× | 1.04× |
| prefixFinal | 0.186 | 0.209 | 0.138 | 1.35× | 1.56× |
| **place** | 1.902 | 1.949 | 1.726 | **1.12×** | 1.13× |
| **Build 段** | **3.57** | **3.87** | **2.85** | **1.27×** | 1.36× |
| Flow | 32.28 | 36.57 | 33.11 | **0.97×** | 1.11× |
| **Melee** | **108.22** | **119.93** | **117.25** | **0.91×（A 更快）** | **1.02×（持平）** |
| MarkDead | ~0.85 | ~0.88 | 0.85/0.88 | ~1.0× | ~1.0× |
| Integrate | 3.09 | 4.18 | 2.73 | **1.13×** | 1.53× |
| **整步（步均总计）** | **148.63** | **167.96** | **159.04 / 158.50** | **0.93×（快 6.5%）** | **1.06×（慢 6%）** |

读法（三句话说清）：

1. **默认档（产品档）现在整体不慢于 Unity**：整步 **148.6 vs 159.0 ms（0.93×）**；`Melee 0.91×`、`Flow 0.97×`、
   `count 1.05×`、`place 1.12×` —— 只剩下 `zero`（2.22×）与 `Integrate`（1.13×）两处小项赤字。
   （这一档的领先主要来自**更粗的工作单元** —— Melee 1953 元素/tile，而 Unity 是 64 元素粒度。）
2. **对齐档（同样的工作单元 + 关掉自适应）暴露的是"同粒度下的内核/调度差"**：Build 段 1.36×、count 1.41×、
   而且 `Melee 1.02×`、`place 1.13×` 已经在噪声带内 ⇒ 赤字集中在 `count`/`prefixFinal`/`Integrate` 与 `zero`。
3. **两档之间的 Delta 就是"工作单元粒度"的收益**：Build 3.87→3.57、Melee 119.9→108.2、Flow 36.6→32.3、整步 168.0→148.6。

### 15.5 未决（下一步按性价比）

1. **`[ClaimGeometry]` 属性 + transpiler 透传**（§15.3）—— 这样"默认档也用显式声明"就不必依赖 F6 的学习期；
   顺带把 F6 降为"未声明时的兜底"。
2. **`zero`（2.2–2.7×，+0.13 ms）**：冷 cache 串行 1.34 MB，Unity 0.10 vs 我们 0.23 — 独立小项，仍未动。
3. **`Integrate`（1.13× 默认档 / 1.53× 对齐档）**：整步里 +0.4~1.5 ms，唯一"两个档都明显偏慢"的**计算**内核。

---

## 5. 下一步（按性价比）

1. **把 Melee 也变成 chunk 调度**（对齐 Unity 的工作单元模型）⇒ 就能只用**一条统一几何（大块+尾部窃取）**，
   一次性同时拿到 `count 0.86` 与 `Melee` 不退。这是唯一能"既追平 Unity、又不用 per-job 特判"的路。
   （08 §15.3 已证：静态几何对两种扁平 job 反号；解只有"换模型"或"按 job 学"。）
2. **查游戏内那 0.2–0.4 ms 的进程内开销**：`ENTJOY_WORKER_AFFINITY`/`SCHED_PRIO`/`SPIN_HOT_US` 本轮都试过，**都没效**
   （aff 1.39 / prio 1.27 / noassist 1.41 vs 基线 1.17，n=1）；需要 `busy_ratio` 级别的证据（08 §22 的器械）
   才能判定是"被引擎线程抢占"还是"每 job 唤醒"。
3. **F6 加单向 + 冷却**，再在**夹具**上验收（夹具 1 s/格、可复现），最后才回到游戏内。
4. **`zero` 的 0.26 ms**：Unity 0.088。这是一个**独立于 count 的真实赤字**（冷 cache 串行 1.34 MB），
   值得单独做（并行清零 / 与前缀和合并 / 用 count 直接产出基线）。

## 6. 附：本轮新增/修改的文件

**EntJoy**：`src/NativeDll/JobCostCache.h`、`src/NativeDll/JobSystem_Scheduler.cpp`（框架修复）；
`tools/gate-run/{count-probe,bench-count,derive-jobkeys}.ps1`、`tools/gate-run/jobkeys.txt`（器械）。
**CSBS（测量宿主）**：`CPUBattle/Scripts/CPUBattleSpatialHash.cs`（诊断档 `zero/count` 口径订正，仅 `DiagBuildPass` 分支）。
**Unity（子代理实测，未改内核）**：`Assets/Scripts/BattleBench/BattleBenchM1Flat.cs`（加 `M1_COUNT_BATCH` 默认 64 = 逐位不变 + 分区探针），
产物 `Logs/sched-struct/REPORT.md`。

---

## 16. ⭐ 对齐档"落后清单"与**粒度税**实验（2026-10-02，3 臂×3 rep，同会话配对 `ab-aligned.ps1`）

**动机**：回答"对齐状态下哪些部分落后 Unity、能否追上"。器械：给 `ab-aligned.ps1` 加 `-TableOverride`（整表替换，几何第 4 字段手写），
其余协议**逐字不变**（`ENTJOY_ASSIST=0`、`ENTJOY_CLAIM_ADAPT=0`、8 workers、count/place 声明 Spread、配对 Unity `W0Player`）。
表键来自 `derive-jobkeys.ps1`（RVA 稳定）；`applied=15/15` 三门全过。原始证据：`tools/gate-run/batch-relax/{m1,m64,m512}/`。

### 16.1 三臂定义（唯一的自变量 = 每 tile 元素数）

| 臂 | 表 | 含义 |
|---|---|---|
| **m1** | 4 键 `:64` + 11 键 `:1`（= Unity `innerloopBatchCount` 镜像，即当前"对齐档"） | 11 个内核 = **逐元素 1 tile** |
| **m64** | 全 15 键 `:64` | 64 元素/tile |
| **m512** | 全 15 键 ≈512 元素/tile（1e6→512、351232→686、18836→37、N=64 两个键→64） | 512 元素/tile |

`[JOBBATCHTBL]`（m1）实测 15 键长度/内批：1e6 长有 6 个键（4 键 `:64`：FlowPresence/**Count**/**Place**/**Integrate**；4 键 `:1`：ClearAll/Spawn/**Melee**/**MarkDead**）；
351232 长 3 键 `:1`（FlowClear/Seed/Grad）；18836 长 2 键 `:1`（FlowSeedInit/BfsWave）；64 长 2 键 `:1`（PrefixPartial/Final）。键↔名字对照见 08b §4.1。

### 16.2 A 侧分项（同机同会话，3 rep 中位，ms）

| 分项 | **m1（逐元素）** | m64 | m512 | m1→m512 |
|---|---|---|---|---|
| zero | 0.307 | 0.258 | 0.246 | −0.06 |
| **count**（:64 不变几何） | 1.234 | 1.024 | 0.999 | −0.24 |
| prefixPartial | 0.060 | 0.064 | 0.069 | +0.01 |
| prefixFinal | 0.205 | 0.399\* | 0.405\* | （\*器械副作用，见 16.4） |
| place | 2.186 | 2.021 | 2.295 | +0.11 |
| Build 段 | 4.50 | 4.15 | 4.11 | −0.39 |
| **Flow** | **39.50** | 34.58 | **33.14** | **−6.36** |
| **Melee** | **131.31** | 125.16 | **120.44** | **−10.87** |
| **MarkDead** | **2.48** | **0.84** | **0.66** | **−1.82** |
| **Integrate**（:64 不变） | 4.88 | 4.41 | **3.21** | −1.67 |
| 残差（Spawn/ClearAll 等未分项） | 1.96 | 0.36 | 0.28 | −1.68 |
| **整步** | **184.47** | 169.61 | **162.54** | **−21.93（−12%）** |

配对 Unity（同臂各自的 `W0Player`，3 rep 中位）：Flow **33.02–33.54**、Melee **113.11–121.82**、MarkDead **0.87–0.94**、
Integrate **2.71–2.84**、count **0.853–0.916**、place **1.833–1.921**、zero **0.106–0.116**、Build **2.88–3.16**、整步 **153.9–163.0**。

### 16.3 结论

1. **对齐档的整步赤字（本会话 1.16×）主要是"逐元素 tile 的每 tile 常数税"，不是内核质量**。
   决定性证据：`MarkDead`（1e6 个 batch-1 tile，内核体极轻）A **2.48** vs B **0.88**；把粒度放到 64/512 后 A 变成 **0.84/0.66**，
   **正好落到 Unity 水平**；同一臂里"残差"（Spawn/ClearAll 同样是 1e6 batch-1 tile）1.96→0.28 同步消失。
   ⇒ 每 tile 常数差 ≈ 1.6 ms/1e6 tile ≈ **1.6 ns/tile**。
2. **把粒度放宽到 512 元素/tile（其余不变）即可追平**：整步 A **162.54** vs 同臂 B（中位 162.98，另一口径 159.6）⇒ **0.997–1.02×**；
   分项上 **Flow 33.14 vs 33.54、Melee 120.44 vs 121.82、MarkDead 0.66 vs 0.94 已反超**。
3. **与粒度无关的真赤字（做完 16.3.2 之后仍然存在）**：`count 1.11×`、`place 1.19×`、`Integrate 1.14×`、`zero 2.1×`、
   `prefixFinal`（器械副作用排除后仍偏慢）。其中 `Integrate` 在 m1/m64 都已是 64 元素粒度 ⇒ **它是内核/内存差，不是调度差**，
   是最值得单独立项的一项。
4. **"对齐"的定义决定答案**：若"对齐"= **逐元素同粒度**（照抄 `innerloopBatchCount`）⇒ 在框架把每 tile 常数降到 Unity 水平之前**追不上**；
   若"对齐"= **同 workload / 同几何声明 / 无自适应**，则**已实测追平并可反超**（这正是默认档整步 0.93–0.94× 的来源）。

### 16.4 口径与诚实标注

- \*`prefixFinal/prefixPartial` 的 N=64（只有 64 个 chunk），m64/m512 臂把内批也放到 64 ⇒ **只剩 1 个 tile ⇒ 掉回串行**（0.20→0.40 ms）。
  这是器械副作用，不是真实回归。若要干净的 Build 对比，两个 prefix 键必须留在 `:1`。
- **本会话 A 侧绝对时间比上一会话（§15.4）整体高 5–8%（A 184.5 vs 167.0），B 侧不变（159.6 vs 158.5）**
  ⇒ 同一会话内的**臂间差**可信（A 只在表上不同），**A/B 绝对比值精度约 ±5%**；跨会话比值不可用（08b §2.1 同款警告）。
- **Melee 131→120（−8.7%）超出"1.6 ns/tile × 1e6"（≈1.6 ms）的解释能力**，机制**未定位**（疑与 tile 连续块的空间局部性有关）。
- **⚠ 更正上一轮（§15.4 表）**：`MarkDead` 当时写成"~0.85 / ~0.88（≈1.0×）"，把 B 的值当成了 A 的 —— 真值是 **A 1.9–2.5 vs B 0.85–0.89（2.2–2.8×）**。
  根因：`ab-aligned.ps1` 的 A 行 CSV **markdead 槽位为空**（B 才有），A 的值只在 `[M-1]` 行里。本节的 A 值全部取自 `[M-1]`。
- `zero`（2.1–2.9×）与 08b §6 的结论一致，仍是"独立小项"：串行 `IJob` 清 1.4 MB（`prefixPartial` 是并行 64 tile、只 0.06 ms，
  可作对标）⇒ **低风险改法 = 改 `IJobParallelFor` 分块清零**，预期 0.31→0.05（**反超** Unity 的 0.106）。
  更进一步：`Place` 用 `InterlockedAdd(_counts+hash,+1)-1` 取槽 ⇒ 每步结束 `_counts` 被写回人口、必须靠下一趟 zero 归零。
  若把 `Place` 改成**消耗式**（`InterlockedAdd(...,-1)` 递减取槽，`dest = CellStart+返回值`），则 `_counts` 用尽即**自然归零**
  ⇒ **整趟 zero 可删、`PrefixSumFinalJob` 里的写零也可删**（每步省 1 趟 + 1 次 barrier + 2×1.4 MB 流量）。
  代价：cell 内槽位顺序反转 ⇒ 仿真轨迹变化（§27.7 指纹会变），必须重测 Melee/整步。
- **不要重走**（08b §3 已证否）：单一静态几何通吃、认领点原子自适应（≤0.5 ms/步）、环内不变量重载（对齐后仅 −1.2%）、
  `SCALAR_RESTRICT=1` 单用（生成器发字面量，不过宏）、`VALUE_BIND=2` 单用（只把重载搬到栈）、撤 `index < Length` 守卫（破坏正确性）。

---

## 17. ⭐ 框架侧取证（2026-10-02）：逐 tile 税的定量上界、`zero` 的真相、以及"框架能拿回多少"

**约束（用户指定）**：**不改测量宿主（CSBS）**、**不改 batch/几何/自适应（保持与 Unity 对齐）**，只优化 EntJoy 框架（`src/NativeDll`、`src/NativeTranspiler*`、`src/EntJoy.*`）。
器械：`llvm-objdump`（clang-cl 工具链，`D:\...\VC\Tools\Llvm\x64\bin`）反汇编**当前部署**的 `NativeTranspiler_Generated\build\Release\NativeTranspiled.dll`（99328 B，10-02 13:52 = 与 `.godot\mono\temp\bin\Debug` 同批）；全量产物 `tools/gate-run/disasm-current.txt`。

### 17.1 `zero` 的 2.9× **不是代码生成问题**

`SharpNative_Job_CPUBattle_ZeroCellsJob_Execute` 的反汇编只有 6 条有效指令：

```
movsxd r8, dword ptr [r8]      ; Length
test r8, r8 / jle ret
shl    r8, 2                   ; byte count
xor    edx, edx                ; fill value = 0
jmp    <memset>                ; ← 尾调用 memset
```

⇒ **它已经是 `memset` 尾调用**。所以 0.307 ms 全部是"**1.4 MB 冷 cache 串行 memset 的 RFO 带宽**"：
1.4 MB 读(RFO) + 1.4 MB 写 = 2.8 MB / 0.28 ms ≈ **10 GB/s**。Unity 的 0.106 ms ⇒ **≈26 GB/s**（等价于避开 RFO：NT store 或并行清零）。
⇒ 框架侧只剩一条路：**给运行时的整块填零加 NT-store 实现**（`_mm256_stream_si256` + `sfence`，≥阈值才用），由转译器识别"填零循环"发射。
预期 0.31 → **~0.14**（不是 0.05）；且 RFO 会转移到紧随其后的 `Count`（8 线程并行付，便宜）。**反超 Unity 的 0.106 需要宿主不再每步清整表，那要动宿主 —— 已排除。**

### 17.2 逐 tile 税的**定量标尺**：1.64 ms / 1e6 tile（≈1.6 ns/tile）

用 §16.2 的 m1→m64 差分，取**内核体极轻、存活数几乎不变**的 `MarkDead`（1e6 个 batch-1 tile）作标尺：

| 内核（1e6 个 batch-1 tile） | m1 | m64 | 差 | 解读 |
|---|---|---|---|---|
| `MarkDead`（体极轻） | 2.48 | 0.84 | **−1.64** | = 纯派发税（落到 Unity 的 0.85–0.88） |
| 残差（`Spawn`/`ClearAll`，同为 1e6 tile） | 1.96 | 0.36 | **−1.60** | 同上，独立复核 |
| `Melee`（同 1e6 tile，体极重） | 131.31 | 125.16 | −6.15 | **派发税只占 1.64**，其余 ~4.5 ms 是"tile 变大后邻居扫描局部性变好" |

⇒ **框架侧（对齐口径）能回收的上界** = 派发税 × 受影响的 tile 数 = 6 个 1e6-tile 内核（Melee/MarkDead/Spawn/ClearAll/…）+ Flow 的 ~1.09M batch-1 tile ≈ **8.4 ms**；
其中 Melee 的那部分**本来就只有 1.64 ms 归派发**，其余 ~4.5 ms 属于"粒度"本身（不在框架手里）。**实际把每 tile 常数减半 ≈ 4 ms**。
（夹具侧历史：`sched-tile/acct-*` → `final-default` 已把 per-tile 边际从 **40.68 → 17.17 → 5.19 ns** 压过一轮，`tools/SchedTileBench` 的 `per_tile_marginal_ns` 就是该指标。）

### 17.3 转译器发射面：**别在"环内重载"上再花时间**

`CountCellsJob` 的 tile 入口（RVA `0x1950` = 批表里的 COUNT 键）反汇编显示：每元素 ~25 条指令，其中**每个 job 字段仍是逐轮从 ctx 重载**
（`Length`/`StateDeath`/`InvCellSize`/`OriginX`/`CellsW`/`CellsH` 走 `[rcx+…]`，`vfmadd213ss` 甚至直接吃内存操作数）——这正是 NT16 记录的"引用绑定"。
但 08b §2.2/§3 已实测 `VALUE_BIND=2 + SCALAR_RESTRICT=1` 合用时 count 只 **−1.2%** ⇒ **不是杠杆**。
另：原子已经是 `lock inc`（无取回），**原子侧无浪费**；`IntegrateJob` 485 条/元素、`MeleeSimJob` 4569 条，全是标量（`vucomiss`/`vmovss`）——**不是向量化候选**（原子/分支/数据相关）。

### 17.4 由此得到的框架侧方案与诚实上界

| 编号 | 框架改动（不碰宿主/不改 batch 几何） | 预期（对齐档） | 风险 |
|---|---|---|---|
| **F-1** | 运行时加 NT-store 整块填零 + 转译器识别填零循环发射 | `zero` 0.31→~0.14（Build −0.17） | RFO 转移到 Count；NT 对"写完立刻读"的缓冲可能反而更慢（需 A/B） |
| **F-2** | 调度器给"扁平 `IJobParallelFor`、无依赖"加**单原子 tile 计数器快速通道**（对标 Unity 的 `fetch_add` 派发） | 目标每 tile 常数减半 ⇒ **−4 ms**（上界 8.4） | 动调度器核心；先用 `tools/SchedTileBench` 夹具（秒级、可复现）验收 |
| **F-3** | 转译器把 tile 循环的 ctx 标量按值提到环外（`__restrict` 全参 + 值绑定）**与 F-2 联动** | 单独无效（−1.2%），联动后才有意义 | 低优先 |
| **F-4** | tiny `IJob` 在队列空闲时由提交线程内联执行 | µs 级（不是收益项，只为一致性） | 最后考虑 |

**诚实结论**：在"**不改宿主 + 保持逐元素同粒度对齐**"下，框架侧能拿回 **≈4–6 ms（整步 1.16× → ~1.12×）**，**不可能追平**；
剩下的大头（Melee 局部性 4.5 ms、`Integrate` 内核 1.5–2 ms、`count/place` 内核 0.7 ms）在框架与对齐口径之外。
追平只来自两处：**放宽粒度**（§16 已实测，但那是改口径）或**受体层不再每步清整表**（要改宿主）。

---

## 18. ⭐⭐ 通解落地（2026-10-02）：JobSystem 的"细粒度路径"收成一条判据、并提为默认

> §17 的结论（"框架侧上界 ~4–6 ms、追不平"）**在本节被推翻**：那个上界是**我自己**按"每 tile 派发开销"估的，
> 而真正的开销是 **tileBuffer 的物化流量**（16 MB 写 + 16 MB 读 / 1e6 tiles），它**整块可去**。
> 改进不是新算法：**四条已分别验收过的机制**（F1/F5/F2/F4）此前只有两条提了默认，另外两条**被刻意留在 env 门控后面**——
> 而 08 §12.6 的结论原文已经写明触发条件：**"若将来把'对齐档 / 细粒度'作为受支持配置，则应 F1+F2 一起打开"**。

### 18.1 通解 = 一条判据覆盖所有 job（判据是**每 tile 元素数**，不是 job 名、不是 batch 值）

| 机制 | 内容 | 门控判据 | 本轮状态 |
|---|---|---|---|
| **F1** `ENTJOY_CLAIM_SPAN` | 认领**元素跨度**恒定（SPAN=1024）：薄 tile 时把认领上限 4 → 1024 | `itemsPerTile ≤ kClaimSpanThinElems(16)` | 已是默认开 |
| **F5** `ENTJOY_TILE_RUN` | 一次认领的**连续等宽 tile 合并成一次内核调用** | 等宽 GeneralRange + 非 guided + trace/timing 关 | 已是默认开 |
| **F2** `ENTJOY_TILES_UNIFORM` | 等宽 GeneralRange **不物化 tileBuffer**（消 O(T) 填表/读回） | 本开关 && **`cs ≤ 16`**（本轮新加门） | **本轮提默认** |
| **F4** `ENTJOY_TILE_FASTPATH` | 每-tile 的 trace/timing/firstTileAt 固定开销提到每批/每令牌 | 本开关 && **`cs ≤ 16`**（本轮新加门） | **本轮提默认** |

- **语义不变**：batch 声明不变（对齐档仍是 Unity 镜像）、宿主不改、不按 job 分类、不改认领几何；只去掉"每个 work-item 一个 tile"的记账成本。
- **厚 tile（默认/产品档）逐位走旧路径**：判据 `cs > 16` ⇒ 两个开关都判 false ⇒ 产品档无行为变更、无风险。
- 回退：`ENTJOY_TILES_UNIFORM=0` / `ENTJOY_TILE_FASTPATH=0` 逐位复现旧行为（启动时打 `[TILESUNIFORM]/[TILEFASTPATH]` 横幅自证）。

### 18.2 取证：那 1.6 ns/tile 是 **32 B 的 tileBuffer 流量**，不是"调度开销"

- 反汇编（`tools/gate-run/disasm-current.txt`，clang-cl）：`ZeroCellsJob_Execute` **已是 `memset` 尾调用**（6 条指令）；
  `CountCellsJob` 的 tile 入口每元素 ~25 条、字段逐轮重载（08b 已证非杠杆）；原子已是 `lock inc`（无取回浪费）。
- 定量标尺（`MarkDead`：1e6 个 batch-1 tile、体内存活数几乎不变、体极轻）：
  **F2/F4 关 2.61 ms → 开 0.66 ms**；"残差"（`Spawn`/`ClearAll` 同为 1e6 batch-1 tile）**2.27 → 0.26**。
  ⇒ 每 tile ≈ **1.6 ns ≈ 32 B @ 20 GB/s** = `ExecutionTile`(16 B) 的**写 + 读回**。§16 里被我读成"派发税"的 1.6 ms/1e6 tile，就是这个。

### 18.3 实测（同会话 A/B，3 rep 中位，对齐档 = Unity batch 镜像 + claim=Spread + `ENTJOY_CLAIM_ADAPT=0`）

| 分项 (ms) | F2/F4 关 | **默认（通解）** | Δ | 同臂 Unity | 判定 |
|---|---|---|---|---|---|
| **整步** | 177.23 | **163.31** | **−13.90（3/3）** | 158.89 | 1.14× → **1.03×** |
| **Flow** | 39.04 | **32.25** | −6.79 | 33.17 | **反超** |
| **MarkDead** | 2.61 | **0.66** | −1.95 | 0.87 | **反超** |
| **残差**（Spawn/ClearAll） | 2.27 | **0.26** | −2.01 | ~1.3 | **反超** |
| Melee | 124.28 | 121.27 | −3.01 | 118.45 | 1.02× |
| Build 段 | 4.67 | 4.07 | −0.60 | 2.99 | 1.36× |
| Integrate | 4.98 | 4.68 | −0.30 | 2.73 | **1.71×（最大的真内核差）** |
| place | 2.260 | 2.122 | −0.14 | 1.768 | 1.20× |
| count | 1.215 | 1.101 | −0.11 | 0.881 | 1.25× |
| zero | 0.301 | 0.249 | −0.05 | 0.105 | 2.37× |

自证：`[TILESUNIFORM] on (built-in default…)`、`[TILEFASTPATH] on (…)`；OFF 臂为 `explicit …=0`。
（注：2026-10-04 起内置默认横幅不再带日期，见 doc13 §5.6。）
原生测试：**默认态 9/9 rc=0**（`TILE_RUN` 关/开两轮）**+ 显式关两开关 9/9 rc=0**（新旧路径都过）。

### 18.4 ⚠ 本轮踩到的坑（第一版被自己否掉）：**不能无条件提默认**

第一版把 F2 无条件提默认 ⇒ **默认档（厚 tile）整步 150.77 → 154.81（+4.04 ms 中位，2/3）**，
与 08 §12.6 记的"+0.32 中位、漂移带内"同向但更大 ⇒ 按纪律**不能**作为产品档默认。
根因：厚 tile 下物化表**是对的**——~19k tiles/步只值 ~0.06 ms，而 `tiles[]` 还**带下一 tile 预取**（`PrefetchNextTileData`），
免物化反而把这部分丢了。⇒ 收进 **`cs ≤ kClaimSpanThinElems`** 这条**与 F1 完全相同**的判据：
厚 tile（默认档）逐位不变、薄 tile（对齐档）吃满收益。第二版默认档 A/B：149.77 ↔ 152.77（配对 −7.62/+6.06/+14.21/−0.05，n=4）
⇒ **落在机器漂移带内**，且**代码路径由构造保证与旧版相同**。

### 18.5 落地物

- `src/NativeDll/JobSystem.cpp`：F2/F4 两个开关改为**内置默认开 + `=0` 回退 + 双向横幅**（同 F6 的模式）。
- `src/NativeDll/JobSystem_Scheduler.cpp`：两条 General 提交路径加 `thinTiles = !guided && cs ≤ kClaimSpanThinElems`，
  `uniformTiles = g_uniformTilesEnabled && thinTiles`、`batch->tileFast = g_tileFastPath && thinTiles`。
- 器械：`tools/gate-run/ab-aligned.ps1` 增 `-TableOverride`（§16）；证据目录 `tools/gate-run/{f24,f24def,f24def2,f24final}/`、`f24-smoke/`。

### 18.6 复验（收进薄-tile 判据后，同会话 2 rep，配对 Unity）

| | A | B(Unity) | A/B |
|---|---|---|---|
| 整步 | **155.67 / 160.18（中位 157.9）** | 157.28 / 156.98（中位 157.1） | **1.005×** |
| Flow | 30.85 / 31.34 | 33.21 / 32.87 | **0.94×（反超）** |
| MarkDead | 0.61 / 0.71 | 0.87 / 0.84 | **0.77×（反超）** |
| Melee | 116.84 / 119.93 | 117.06 / 116.95 | 1.01× |
| place | 1.727 / 1.736 | 1.661 / 1.727 | 1.02× |
| Build 段 | 3.37 / 3.43 | 2.72 / 2.86 | 1.22× |
| count | 0.930 / 1.004 | 0.797 / 0.832 | 1.19× |
| zero | 0.225 / 0.239 | 0.091 / 0.093 | 2.51× |
| Integrate | 3.77 / 4.54 | 2.50 / 2.54 | **1.65×** |

⇒ **对齐档（逐元素同粒度 + 声明几何 + 无自适应）整步已与 Unity 打平（1.005×）**，且 `Flow`/`MarkDead` 反超。
本轮改动自证：`[TILESUNIFORM] on (built-in default…)`、`[TILEFASTPATH] on (…)`、`[CLAIMADAPT] off (explicit …)`；`applied=15/15`。

### 18.7 通解之后的账（对齐档）

整步 **≈0.5%**，且**已无框架记账成分**，剩余赤字全部落在 4 个内核：

| 项 | 赤字 | 性质 |
|---|---|---|
| `Integrate` | **+1.6（1.65×）** | 内核体/内存（batch=64 同粒度下测得）—— **下一优先级** |
| `zero` | +0.14（2.5×） | 冷 cache 1.4 MB memset 的 RFO（已是 memset；框架侧只能靠 NT store，收益 ~0.1） |
| `count` + `place` | +0.24 | 共享原子 + 过滤载入（08b §2.3 已分解） |
| `Melee` | ~0 | 已打平（§16 里那 4.5 ms 的"局部性"项被 F2 的连续性一并吃掉了） |

⇒ **JobSystem 这一层到此为止**：它不再是"细粒度配置下的弱点"（Flow/MarkDead 已反超 Unity、整步打平），
再往前必须动内核（`Integrate` 为首），那已经不是 JobSystem 的事。

---

## 19. 通解续：`ENTJOY_CLAIM_SPAN` 量程解锁后的全量程重扫（**否证式收尾**）

**动机**：08 §1039 记录 `ENTJOY_CLAIM_SPAN` 有硬编码 **4096 上限**，"想用更长认领跨度做实验，必须先放宽这个 clamp"（当时 8192 与 65536 读数几乎一样 = 被 clamp 吃掉）。
而 §18 提默认 F2/F4 后**代价结构已变**（tile 记账不再随 tile 数增长）⇒ 薄 tile 下的最优跨度必须在**新结构**上重扫一遍。

**改动**：`JobSystemInternal.h` 增 `kClaimSpanElemsMax = 16384`（旧硬编码 4096），`JobSystem.cpp` 引用之。
**只放宽实验/回退用的 env 量程；内置默认仍是 F1 已验收的 1024**（`=0` 仍可关）。

**重扫（对齐档，同一 DLL、仅换 env，2 rep/点）**：

| `ENTJOY_CLAIM_SPAN` | A 整步（ms） | 中位 |
|---|---|---|
| 512 | 158.63 / 156.94 | 158.63 |
| **1024（内置默认）** | **155.67 / 160.18** | **157.90** |
| 2048 | 157.16 / 155.88 | **157.16** |
| 4096 | 159.63 / 157.35 | 159.63 |
| 8192 | 161.57 / 162.15 | 162.15 |
| 16384 | 165.61 / 163.07 | 165.61 |

（同会话 B(Unity) 非常稳：155.86–157.83，故 A 的差可直接比。）

**结论（否证）**：
1. **最优平台在 512–2048，默认 1024 已在平台上**；2048 只快 0.7 ms（2 rep 内不可辨），**不值得改默认**。
2. **4096 以上单调变差**（16384 比 2048 差 **8.5 ms**）⇒ 与 08 §12.1 的机制一致：认领跨度太大 = 块太大 ⇒
   尾部均衡与"8 个 worker 在 index 空间上的邻近"被破坏（厚的方向没有免费午餐）。
3. ⇒ **F2/F4 提默认并没有把最优跨度推向更粗**；§18 的默认配置（SPAN=1024）就是新结构下的最优端。
   本轮"继续"的净结果是一条**否证**（原本可能存在的"更粗跨度还能再赚"假设被数据排除），加上量程解锁留给后续。

### 19.1 夹具复核（`tools/SchedTileBench`，N=1e6、2 job/帧、8 worker、空体内核）

重建夹具（`dotnet build tools/SchedTileBench -c Release`，它会把当前 `NativeDll.dll` 拷进 `bin\Release`）后扫 batch `1 / 64 / 1024`：

| batch | tiles | 每 job 中位 | 每 tile |
|---|---|---|---|
| **1** | 1,000,000 | **28,050 ns** | 0.028 ns |
| 64 | 15,625 | 233,200 ns | 14.9 ns |
| 1024 | 977 | 23,150 ns | 23.7 ns |

⇒ **旧病理性"batch 越细越慢"已经反转**：现在 batch=1（1e6 tiles）反而是**最快**的一档，因为 SPAN=1024 把它收成 ~977 次认领。
⚠ 口径警告：空体内核的循环会被编译器消掉，此时读数反映的是**每次认领**的固定开销（977 × ~29 ns ≈ 28 µs；3907 × ~60 ns ≈ 234 µs），
不是每元素成本 ⇒ 只作**结构性**证据（"认领次数而非 tile 数决定成本"），绝对值不可与游戏内比较。

### 19.2 由构造判死的一个杠杆：`ENTJOY_CLAIM_GUIDED`（引导式收缩）在新结构下已无价值

- 旧世界：认领数 ≈ tile 数（1e6 级），尾部均衡是真问题 ⇒ 才有 guided（`remaining/(2W)` 几何收缩，尾部退回 1）。
- 新世界：认领数 = `ceil(tileCount/step)`，对齐档 = **977 次认领**；977/8 ≈ 122 次/worker ⇒ **尾部不平衡上界 = 1 次认领
  = 1024 元素 ≈ 整步 4.1e6 元素的 0.025%** ⇒ 可回收量在**噪声以下**。
- 且 guided 与 F2/F5 **互斥**（`guided` ⇒ `uniformTiles=false`、`fuseTileSize=0`，即放弃免物化与合并）⇒ 开 guided 会**丢掉 §18 的 −13.9 ms**。
- ⇒ **不再重测 guided**（08 §12.9 的"两会话矛盾"结论在新结构下已无关紧要）。这是"通解"带来的**副作用**：它顺手消掉了一个历史悬案。

---

## 20. ⭐⭐ "为什么对齐档还剩 `Integrate`/`zero`/`count`/`place` 的差距"——**逐条对齐两侧内核源码**（2026-10-02）

**问题**：这三个（四趟）的差距，是"代码不一样"还是"代码一样但调度不一样"？

**方法**：把 A（`CPUBattleSpatialHash.cs` / `CPUBattleCombat.cs`）与 B（Unity `BattleBenchM1.cs` / `BattleBenchM1Flat.cs` / `BattleBenchM4.cs`，
即 M4 权威档实际调用的 `CpuHashFlat.BuildTimed` + `Bb0M4IntegrateFlatJob`）的**每个内核体逐行对齐**。

### 20.1 逐内核对齐结果

| 内核 | A（EntJoy，转译 C++） | B（Unity，Burst） | 代码是否一样 | 差距归因 |
|---|---|---|---|---|
| **zero** | `for (int i = 0; i < Length; i++) ptr[i] = 0;`（`IJob`，串行） | `for (int i = 0; i <= CellCount; i++) p[i] = 0;`（`IJob`，串行）——**同一个循环，同一长度（cellCount+1）** | **一样** | **全是调度侧**：A 已是 `memset` 尾调用但**单线程 RFO 冷 cache**；B 更快 ⇒ 见 §20.2 |
| **count** | `if (index<Length && Alive!=0 && State!=Death) { …clamp…; Interlocked.Increment(counts[hash]); }` | `if (Alive[i]!=0 && State[i]!=StateDeath) { …clamp…; Interlocked.Increment(counts[hash]); }` | **差 1 个谓词**（A 多 `index<Length` 守卫） | 08b §2.3 已分解：守卫 ≈0.1–0.2 ms；**其余是几何/原子** |
| **place** | `…; inCell = Interlocked.Add(counts[hash],1)-1; SortedIndex[CellStart[hash]+inCell] = index;` | `…; cursor = Interlocked.Increment(counts[hash])-1; sorted[CellStart[hash]+cursor] = Slot[i];` | **一样**（只差写入值是 `index` vs `Slot[i]`） | **几何**（§20.3 新数据） |
| **prefixPartial / prefixFinal** | 分块求和 / 落地+清零 | 同 | **一样** | 无实质差距（§16 v 里已在噪声带） |
| **Integrate** | 见 §20.4 | 见 §20.4 | **不一样**（守卫结构不同，见下） | **内核体 + 调度各占一部分** |

### 20.2 `zero`：代码一样（都是串行 `IJob` + 同一个 for），差在**内存带宽**

- A 反汇编：`shl r8,2; xor edx,edx; jmp memset` ⇒ **已经是 `memset` 尾调用**，0.307 ms ≈ 1.4 MB 冷 cache 的 RFO
  （读 1.4 MB + 写 1.4 MB = 2.8 MB / 0.28 ms ≈ **10 GB/s**）。
- B：0.11 ms ⇒ 等效 **26 GB/s**。**同样一个 for 循环、同样长度**，差 2.5× 只能是：
  ① Burst 把 `p[i]=0` 编译成**向量化 NT store**（绕开 RFO）；② 或 B 侧该缓冲在 L2 里更热。
  ⇒ **这一项属于"库/生成器质量"，不属于 JobSystem**：要追必须让 A 的生成物也走 NT store（框架侧 `MemZeroNT`），
  预估 −0.1~0.15 ms（上限就是 Unity 的 0.10），且**有反噬**（紧接着的 `Count` 要读这块内存，NT 把它写回 DRAM 反而更慢）。
- **判定：代码一样 ⇒ 差距是内存/生成质量，不是调度。**

### 20.3 `count` + `place`：差距是**内存/别名行为**，几何**已经调到最优**（不是几何问题）

同会话、同一 DLL、只改批表第四字段（`s`=Spread / `a`=Adjacent），2 rep：

| 组合 | count (ms) | place (ms) | count+place | Build 段 |
|---|---|---|---|---|
| **count=Spread, place=Spread（当前对齐档）** | **0.946 / 0.981** | **1.749 / 1.930** | **2.70 / 2.91** | 3.56 / 3.97 |
| count=Spread, place=Adjacent | 1.061 / 0.978 | 2.525 / 2.582 | 3.59 / 3.56 | 4.59 / 4.21 |
| count=Adjacent, place=Spread | 1.484 / 1.477 | 1.643 / 1.795 | 3.13 / 3.27 | 3.74 / 4.24 |
| count=Adjacent, place=Adjacent | 1.530 / 1.563 | 2.405 / 2.449 | 3.94 / 4.01 | 4.81 / 4.69 |

读法（**同向，不是反号**）：
1. **count 强烈要求 Spread**：0.95–0.98 → 1.48–1.56（Adjacent 慢 **~55%**）。机制：count 只对同一批格子做无返回值的原子 ++
   （写密集、几乎不读），静态连续切片让每个 worker 独占自己的 cache line；相邻窗口则制造跨核 cache line 弹跳。
2. **place 也偏好 Spread**：1.75–1.93 → 2.41–2.58（Adjacent 慢 **~35%**）。理由：place 要 *读* `CellStart[hash]`、
   *写* `SortedIndex[CellStart+cursor]`、再对 `Counts[hash]` 做原子 RMW，三个数组的访问都随 hash 跳 ——
   相邻窗口并不能带来净读侧收益，反而叠加了原子争用。
3. ⇒ **`Spread+Spread`（当前对齐档）就是四组里的最优**（count+place 2.70/2.91 vs 次优 3.13/3.27、最差 3.94/4.01）。
   **几何这一维已经调到端点，没有剩余收益**。
   （⚠ 我第一遍读表时曾误判为"两者要求相反几何"——那是把 `place` 的行错配到了另一列 `count`；此处为更正后的读法。）
4. **即便如此，对 Unity 仍有差距**：count 0.95 vs B 0.83、place 1.84 vs B 1.69（B 也是 `Schedule(n, 64)` ⇒ **同粒度**）。
   两侧代码对照（§20.1）显示只差一个 `index<Length` 守卫；08b §2.3 已把 count 的差距分解为
   **原子代价 ≈0.36 ms + 过滤载入 ≈0.1–0.2 ms**，并实测"把环内重载提到序言"（VB2+R1）只值 **−1.2%**。
   ⇒ **判定：代码几乎一样、粒度一样、几何已最优 ⇒ 剩余差距来自"每种访存的 cache 行为/别名信息"
   （Burst 的 `[ReadOnly]` 能让它证明 `Alive/State/Pos` 与 `Counts` 不别名，从而合并载入），
   属于编译器/生成器质量，不是 JobSystem 调度问题。**

### 20.4 `Integrate`：代码**几乎一样**，差在**发射面**（不是调度，也不是守卫结构）

| # | 差异 | A | B |
|---|---|---|---|
| ① | **活体守卫** | **没有** `alive` 早退：直接按 `State==Death?` 分支；由于 transpiler 不支持 `Execute(int)` 内 `return`（CSBS 注释明写），代码改成 if/else 嵌套 | `if (alivePtr[i]!=0) { …全部逻辑… }` —— 外层先挡一次 |
| ② | **cfg 载入位置** | `CpuUnitConfigData cfg = cfgPtr[cfgIdPtr[index]];` 在**分支之前**逐元素执行 | **同一行、同一位置**（`BattleBenchM4.cs:83`）⇒ **这一项两侧相同，不是差异** |
| ③ | 额外工作 | — | B 多 `BatchTouched[index>>6]=1`（每 64 元素一次写）⇒ **B 做的工作更多** |
| ④ | **编译产物** | transpiled C++：96 个形参 + 17 个数组基址全部溢出/从栈取回（`rsp+0x140/0x168/0x178/0x1c8/0x250/0x260`），字段无别名证明 | Burst：`[ReadOnly]` 标注 + 无别名假设 ⇒ 载入可合并、寄存器分配更自由 |

⚠ **先排除一个误判**：A 的 `imul 0x54` = **84 B/单位的 `CpuUnitConfigData` 拷贝**看起来很像浪费，但
B 的 `Bb0UnitConfigData` 是**逐字段镜像的同样 21 个字段**（`BattleBenchM0Data.cs:36-63`）⇒ **两侧都拷 84 B**，
**不是差异**。存活率 99.5%+ ⇒ 差异①的 `alive` 守卫在 B 侧也几乎不省事。⇒ 真正的差异只剩 **④ 编译产物**。

- 实测（对齐档，同会话）：A **4.68 / 3.77 / 4.54** vs B **2.73 / 2.50 / 2.54** ⇒ **1.65×**。
  两者 batch 都是 64（B 用 `Schedule(n, 64)`；A 批表第 13 键 `00005e60:64`）⇒ **同粒度、同工作量、甚至 B 多写一个 `BatchTouched`**。
- 反汇编：A 的 `_Batch` **485 条指令/元素**，`cfg` 的 84 B 拷贝溢出到栈（`rsp+0x230` 一带），
  循环里数组基址靠 `rsp+0x140/0x168/0x178/0x1c8/0x250/0x260` 反复取回。
- ⇒ **判定：不是调度问题**（同粒度 64 下仍 1.65×）。差异是**发射面/寄存器分配**：A 侧 96 个形参的作业把
  17 个基址与多个标量全打进栈槽，循环体每元素都要 `mov reg,[rsp+off]` 取回；B 的 Burst 靠 `[ReadOnly]` +
  别名假设把它们留在寄存器里。**B 还多做了 `BatchTouched` 写** ⇒ 差距只会更大，不是"B 少干活"。
- **可动范围（框架侧）**：把环外不变量提到函数序言 = 08b 已证"只值 −1.2%"（VB2+R1）⇒ **不必重走**。
  真正的差在"96 形参 / 无别名证明"这个**转译器调用约定**上：
  ⇒ 若要动，应当走 **`ENTJOY_PACK_SCALARS`（08 §12.7 已实现：纯值字段收进 `__scalars` 结构体、单指针传参，
  Melee `_Batch` 形参 96→65，实测中位比值 1.0005 = 中性）** 的延伸：把那 17 个**指针**也收进一个 `__arrays` 结构体，
  减少溢出与重取。**但 08 §12.7 的实测是中性 ⇒ 优先级低、且不保证有效**，需先做夹具判据。
- ⇒ **诚实结论**：`Integrate` 的 1.65× 是**发射面（形参数/别名）**问题，**不是调度**；
  在"不改宿主"约束下，唯一合法路径是转译器调用约定（`__scalars`/`__arrays` 打包），
  而现有证据（08 §12.7：96→65 形参 = 中性）说明**期望值不高**。

### 20.5 三句话总结

1. **`zero`：代码一样**（都是串行 `IJob` + 同一个 `for`，A 甚至已是 `memset` 尾调用）⇒ 差在**内存带宽/NT store**，不是调度。
2. **`count`/`place`：代码几乎一样**（A 只多一个 `index<Length` 守卫）、**粒度一样**（两侧都 64）、**几何已调到最优**
   （四组几何对照里 `Spread+Spread` 最好）⇒ 剩余差距是**别名信息/载入合并**这类生成器质量，调度只占小头。
3. **`Integrate`：源码几乎一样**（连 84 B 的 `cfg` 拷贝都一样；B 还多写 `BatchTouched`），
   **粒度也一样（都 64）** ⇒ 差距在**发射面**（A 的 96 形参数把 17 个基址打进栈槽、每元素取回；Burst 靠别名假设留在寄存器）
   ⇒ **不是调度**；框架侧唯一合法路径是转译器调用约定（`__scalars`/`__arrays`），而 08 §12.7 已证 96→65 形参为**中性**，期望值不高。

### 20.6 由此得到的结论（对"是调度问题还是代码问题"的直接回答）

| 项 | 代码是否一样 | 粒度是否一样 | 判定 |
|---|---|---|---|
| `zero` | **一样**（都是串行 `IJob` + 同一个 `for`；A 甚至已是 `memset` 尾调用） | 一样（都是单任务串行） | **既不是调度也不是代码**：是**内存带宽**（A 10 GB/s vs B 26 GB/s 的 RFO 差异）⇒ 生成器/库质量 |
| `count` | **几乎一样**（A 多 `index<Length` 守卫） | **一样**（都 64） | **不是调度**：是**别名信息**（Burst `[ReadOnly]` 可合并载入）+ 守卫 |
| `place` | **一样**（只差写入值 `index` vs `Slot[i]`） | **一样**（都 64） | **不是调度**：几何已最优（§20.3），剩别名/载入 |
| `Integrate` | **几乎一样** | **一样**（都 64） | **不是调度**：**发射面**（96 形参造成的栈流量） |

⇒ **四项全部不是 JobSystem 调度问题**（§18 的通解已把调度这一维做到 1.005×）。剩下的差距分两类：
**① 内存带宽/别名信息（zero/count/place）**、**② 转译器调用约定（Integrate）**。
两者都只能靠**框架侧生成器**改，而现有实测（08b VB2+R1 −1.2%、08 §12.7 形参打包中性）说明**期望值都不高**。

---

## 21. ⭐ "Spread/Adjacent 之外还有没有别的认领方案？为什么 Unity 更好？"（2026-10-02）

### 21.1 认领方案的**完备清单**与实测（对齐档、同一 DLL、仅换 env、2 rep、`NoB`）

| 方案 | 机制 | zero | count | place | Flow | Melee | Integrate | 判定 |
|---|---|---|---|---|---|---|---|---|
| **基线：`Spread`（静态段 + 空手窃取）+ `CLAIM_SPAN=1024`** | 每 worker 独占一段游标，段内按 1024 元素认领 | 0.251 | 0.992 | 2.048 | 33.02 | **122.83** | 4.19 | ✅ **当前最优** |
| `ENTJOY_CLAIM_GUIDED=1` | 认领预算按 `remaining/(2W)` 几何收缩（尾部自动退回 1） | 0.259 | 1.168 | 2.196 | 33.04 | 123.80 | 4.82 | ❌ 更差（尾部均衡本已不是问题，见 §19.2） |
| `ENTJOY_CLAIM_SLICE=1` | 段游标 + **空手才窃取**（与 Spread 的差别：窃取点/顺序不同） | 0.238 | 1.039 | 2.303 | 33.23 | **142.62** | 3.43 | ❌ Melee **+20 ms** |
| `ENTJOY_TILE_STRIDE=1` ⚠ | 把 tile 下标按 `(t*stride) mod T` 置换，让 W 个 worker 分散在 index 空间 | 0.539 | 1.221 | 3.011 | **58.46** | **473.15** | 5.42 | ❌❌ **灾难**（Melee 3.9×、整步 1.8×） |
| `ENTJOY_CLAIM_BLOCK=1` | 每 worker 一次领走 `≈tileCount/W` 的连续大块（**不可窃取**） | — | — | — | — | — | — | ⛔ 08 §(l8) 已证否：Build −0.69 但 Melee **+3.97**、整步 **+6.85（0/10）** |

**为什么这些全局方案都不行（机制，不是巧合）**：
1. **Melee 是"空间局部性绑定"的 job**：它每次都要按 cell 扫邻居表/空间哈希。相邻窗口（或大块连续）能让 W 个 worker
   **同时命中同一批 cache line**；一旦把它们在 index 空间上推开（`TILE_STRIDE`、`CLAIM_SLICE` 的窃取顺序），
   每个 worker 各自去拉不同的 cell 区间 ⇒ **L2/L3 命中崩塌**。`TILE_STRIDE` 直接 +350 ms 就是这么来的。
2. **`count`/`place` 是"写争用绑定"的 job**（每元素一次 `lock incl`）⇒ 它们**要**把 worker 推开（独占 cache line）。
3. ⇒ **同一趟里两类需求同时存在**（`count` 要推开、`Melee` 要靠近），所以**任何单一全局认领方案都是在这两者之间重新分配**。
   这与 08 §(l8)/§架 的结论一致：`tpw` / `CLAIM_BLOCK` / `CLAIM_SLICE` 三种全局策略各自证否。
4. **我们已有的解法正是"不全局"**：**按调用点声明几何**（§13/§15 的 `ClaimPolicy`：count/place=Spread、Melee=Adjacent）。
   本节这四个全局开关**没有一个能超过它** ⇒ **"均衡方案"不存在；现有的 per-call-site 组合就是端点**（§20.3 又证明它在 count/place 内部也是四组最优）。

### 21.2 「为什么 Unity 比我们更好」——按项给出机制，不是笼统的"它更快"

| 项 | A vs B | **根因（已有证据）** |
|---|---|---|
| `zero` | 0.25 vs **0.10** | 代码**完全一样**（都是串行 `IJob` + 同一个 `for`）⇒ 差在**内存/cache**：游戏内那块 1.4 MB 在两次 Build 之间被 Melee/Flow 冲出 cache（冷态等式 ≈4–5 GB/s）；热态下**我们的 `for`→`memset` 能跑 57–63 GB/s**（§22.2 实测）。⚠ **22.2 已修正本节旧表述**：不要再归因于"Burst 发 NT store、我们不发"（那是推断，且 NT store 对本场景有害）。另：`IJob` 的绑定当前退化为托管 thunk（§22.1），是可修点但量级只 1.3 µs/次。 |
| `count` | 0.95 vs **0.83** | 代码**几乎一样**（A 多一个承重的 `index<Length` 守卫）、**粒度一样**（都 64）、**几何已最优**。U 侧反汇编证实**它也是每元素一次 `lock incl`**（08 §32.3）⇒ **不是"它没有原子"**。差在 **Burst 的 `[ReadOnly]` 别名证明**：`Alive/State/Pos` 与 `Counts` 不别名 ⇒ 可合并载入/提前 clamp；A 侧字段无别名证明，只能逐元素重载（08b §2.2 实测"把重载提到序言"仅 −1.2%，说明剩下的差在**载入合并**而非重载次数）。 |
| `place` | 1.84 vs **1.69** | 同上（代码逐字对应，只差写入 `index` vs `Slot[i]`）；差 ≈ 0.15 ms，同样归到别名/载入。 |
| `Integrate` | 4.2 vs **2.5** | 源码几乎一样（连 84 B `cfg` 拷贝都一样，**B 还多写 `BatchTouched`**）、**粒度一样（都 64）** ⇒ 差在**发射面**：A 的 `_Batch` **96 个形参**把 17 个数组基址压进栈槽（`rsp+0x140/0x168/…`）**每元素取回**、485 指令/元素；Burst 把这些留在寄存器。**这是转译器调用约定问题**（08 §12.7：光把纯值字段打包 96→65 形参 = **中性**，所以修它需要把**指针**也打包，期望值待验证）。 |
| `Flow`/`MarkDead`/`Spawn` | **已反超** | §18 的通解（不物化 tileBuffer + 每批快照）吃掉了逐 tile 记账 ⇒ 现在比 Unity 快 6%/23%。 |
| `Melee` | **打平** | 两侧都是扁平 `IJobParallelFor` + 同粒度；A 的发射面无 call、无多余重载（§17.3）。 |

**一句话**：差距不在"调度不如 Unity"（通解落地后对齐档整步已 1.005×，Flow/MarkDead 反超），而在**两侧编译器对同一段代码的处理质量**：
① `count`/`place` 的别名/只读标注换来载入合并；② `Integrate` 的寄存器分配不被 96 形参的调用约定拖累；
③ `zero` 则是**冷 cache**（§22.2 已修正本节先前"NT store"的推断）。
**唯一确属"路径"的问题是 `IJob` 绑定退化为托管 thunk（§22.1），量级 1.3 µs/次。**

> ⚠ **本节（§21.2）的 `zero` 行已被 §22.2 的三臂探针修正**：热态下我们的填零不慢（57–63 GB/s），
> 游戏内慢是**冷 cache**；`IJob` 走托管 thunk 是**真问题但量级小**。以 §22 为准。

### 21.3 结论（可执行口径）

- **没有"更均衡"的认领方案**：四个全局替代（guided / slice / stride / block）**全部更差**，其中 `TILE_STRIDE` 是灾难。
- **要赢 Unity，只能在这三条里选**（都在框架侧，且都不改宿主）：
  1. **转译器把指针也打包进单指针传参**（`__scalars` → `__scalars`+`__arrays`），目标 = `Integrate` 的栈流量。**须先建夹具判据**（08 §12.7 的中性结论是前车之鉴）。
  2. **NT-store 填零**（`zero`，−0.1~0.15 ms，有反噬需 A/B）。
  3. **给生成物加别名/只读信息**（`__restrict` 或 `const` 指针）以解锁载入合并 —— 但 08 §3 已证 `SCALAR_RESTRICT=1` 单独用**无效**
     （生成器发字面量、不过宏）⇒ 要做得在**发射面**做，是 1 的一部分。
- **不能修的**：`zero` 的 RFO（除非 NT store）、`count/place` 的原子与 alias（除非发射面），
  `Integrate` 的内核写法（在宿主里，被"不改测试端"约束挡住）。

---

## 22. ⭐⭐ "这些 job 都走原生路径吗？"——**分发路径取证**（2026-10-02，回答用户质疑）

**用户的质疑（正确的部分）**："同样是 `IJob`，Unity 更快 ⇒ EntJoy 的 JobSystem 路径上有性能瓶颈；其他项也是类似道理。"
本节先把"是否走原生"钉死，再用一条**新增的三臂探针**把"调度路径成本"与"内核/内存成本"分开。

### 22.1 取证结论：**不是所有 job 都走原生路径**——`IJob` 例外

发射面在 `src/NativeTranspiler/Analyzer/Common/BindingsGenerator.cs`：

| job 形态 | 绑定 | 是否原生 |
|---|---|---|
| `IJobParallelFor`（**非 MT**，即默认） | `s_X_BatchFuncPtr = Get_X_Execute_AdapterPtr();`（第 355 行） | ✅ **原生→原生**（调度器直接调 C++ adapter） |
| `IJobParallelFor`（**`UseISPC_MT`**） | `Marshal.GetFunctionPointerForDelegate(BatchFunc)`（第 348 行） | ⚠ **托管 thunk**（注释自承"每 tile ~3 µs 开销"） |
| `IJobChunk` / `IJobEntity` | `Get_X_Chunk(EntityBatch)AdapterPtr()`（282-294 行） | ✅ 原生 |
| **`IJob`（单任务）** | `Marshal.GetFunctionPointerForDelegate(JobFunc)`（第 **360-366** 行，`else` 分支） | ❌ **走托管 thunk**：`native → managed JobFunc → native X_Execute` |

⇒ **CSBS 的 `zero`（`ZeroCellsJob : IJob`）走的是托管 thunk 分支**，而 Unity 的 `Bb0M1ZeroJob` 是 **Burst 编译的原生 `IJob`**。
这与你说的"同样是 IJob，Unity 更快"**方向一致**——但要害是**量级**，见 22.2。
（注：`NativeTranspiled.dll` 里**确实也存在** `ZeroCellsJob_Execute` 与 `ZeroCellsJob_Execute_Adapter`（16 条指令、尾调 `memset`），
那是生成器为"原生可用"准备的产物；**当前绑定没有用它**——这正是可修点，见 22.3。）

### 22.2 量级判定：新增三臂探针（`tools/HotSpotMicro/dispatch_cost.cpp`）

同一个函数体（`for (i < n) p[i] = 0;`，**逐字抄 CSBS `ZeroCellsJob`**）走三条路，`dlopen` 真实 `NativeDll.dll`：

| 臂 | 含义 | 1.4 MB 第 1 轮 | 1.4 MB 第 2 轮 | 4 MB 第 1 轮 | 4 MB 第 2 轮 |
|---|---|---|---|---|---|
| **NOOP** | 空体，作为单任务 `IJob` 派发 | 0.0013 | 0.0011 | 0.0011 | 0.0011 ms |
| **DIRECT** | 提交线程直接调用（不经调度器） | 0.0225（62.6 GB/s） | 0.0159（88.5） | 0.0693 | 0.0561（71.3） |
| **JOB** | 同一函数体作为单任务 `IJob` 派发 | 0.0244（57.6） | 0.0273（51.4） | 0.0664 | 0.0882（45.4） |
| **JOB − DIRECT** | 调度路径净成本 | **+0.0019** | **+0.0115** | **−0.0029** | **+0.0321** |
| NOOP 占该差值 | | 65% | 10% | 0% | 3% |

（两轮同参数重跑，**说明这一档的自噪声大到 ±0.01 ms**；`JOB−DIRECT` 的符号都会翻。故只取**两轮都成立**的结论。）

**三个结论（只保留两轮都支持的）**：
1. **调度路径确有成本：`Schedule+Complete` 往返 = NOOP = 1.1–1.3 µs/次**（两轮一致、±0.2 µs）。
   ⇒ **"EntJoy 派发不是免费的"成立**。游戏内每步约十几次单任务派发 ⇒ 合计 ~15 µs/步 ≈ 整步 **0.01%**
   （**当前不值一修，但它是"小 job 密集"场景的预算基准**）。
2. **但它解释不了游戏内的 `zero` 差距**：即使取最差的 `JOB`=0.0273 ms（1.4 MB），
   也比游戏内实测 **0.25–0.31 ms 小一个数量级**；而 `DIRECT` 更低到 0.0159 ms。
   ⇒ 游戏内那 ~90% 既不是这段代码、也不是调度，而是**冷 cache**（游戏内 `_counts` 在两次 Build 之间被 Melee/Flow 的数百 MB 流量冲出 cache；
   本探针的缓冲在 300 次重复中一直热）。**热态填零 45–88 GB/s，游戏内等效只有 4–5 GB/s。**
3. ⚠ **据此修正 §21.2 的一条**：那里说 `zero` 是"Burst 发 NT store、我们走 CRT memset"——那是**推断**。
   本节证明**热态下我们的 `for`→`memset` 本身不慢**（45–88 GB/s）；真差异是**冷 cache**。
   ⇒ **`zero` 的正解是"别每步清 1.4 MB"（要改宿主），不是换 memset/NT store/原生绑定。**

### 22.3 由此得到的第一条**可落地框架改进**：`IJob` 也走原生 adapter

- **现状**：`IJob` 绑定用 `Marshal.GetFunctionPointerForDelegate`（22.1 表）⇒ 每次派发多一次
  **native→managed→native 双重转换**。生成器**已经**为 `IJob` 产出了 `X_Execute_Adapter`（16 条指令、尾调 `memset`），**只是没接上**。
- **改法（框架侧、不动宿主、不动 batch）**：在 `BindingsGenerator` 的 `IJob` 分支，与 `IJobParallelFor` 非 MT 一样改用
  `Get_{Name}_Execute_AdapterPtr()`；adapter 由 `CppJobGenerator` 已生成（同一套字段偏移解析）。
- **预期收益（必须实测，不能推断）**：按 NOOP=1.3 µs 的量级，**单任务派发最多省 ~1 µs/次**（0.01% 整步）；
  但若托管转换成本随**参数个数**增长（`IJob` 的 adapter 要解字段偏移），则大 job 上会更明显。
  **判据**：用 22.2 的探针加一臂"JOB-managed"（当前绑定）vs "JOB-native"（新绑定），同一函数体、同一 DLL，先看夹具差值再进游戏。
- **风险**：`IJob` 的 adapter 是否覆盖全部字段形态（`UnsafeList` 写回、bool 变体、`NativeArray` 长度校验）需与 `IJobParallelFor` 同等回归
  ⇒ 原生测试 **9/9 ×（新旧绑定）** 是硬门禁。

### 22.4 对"其他性能问题也是类似道理"的回应：判定方法已固化

对任一项都可套 **NOOP / DIRECT / JOB 三臂**，把"调度路径成本"与"内核成本"分离（本轮已实现为可复用器械）：

| 项 | 锚点（已知） | 判定 |
|---|---|---|
| `zero` | NOOP=1.3 µs；游戏 0.25–0.31 ms | ✅ 已判：**冷 cache（内存）**，调度只占 ~0.5%（1.3 µs / 250 µs） |
| `count`/`place` | 08b §2.3：原子 ≈0.36 + 守卫 0.1–0.2；几何已四组最优（§20.3） | ✅ 已判：**内核/内存（原子+载入）** |
| `Integrate` | 同粒度 64 下 1.65×；96 形参/485 指令/元素 | ✅ 已判：**发射面（栈流量）**，非调度 |
| `Flow`/`MarkDead` | §18 通解后**已反超 Unity** | ✅ 调度维已被吃掉 |
| **`IJob` 类（zero 等单任务）** | **本轮新发现：绑定退化为托管 thunk** | ⚠ **未量化**——22.3 的臂待做 |

⇒ **诚实的合并结论**：用户"路径上有开销"的判断**在 `IJob` 这一类上成立**（且找到了确切代码位置与修法），
但对**当前四项赤字**不成立（`zero` 冷 cache、`count/place` 原子+载入、`Integrate` 发射面、`Flow/MarkDead` 已反超）。

**原始证据（本地、`/tools/*` 按设计不入库）**：
- 探针源码（可复跑，三臂）：`tools/HotSpotMicro/dispatch_cost.cpp`
- 两轮原始输出：`tools/gate-run/dispatchcost/{dispatch_cost.cpp,results.txt}`
- 复跑命令：`cmake --build tools/HotSpotMicro/build --config Release --target DispatchCost`，
  再把 `NativeDll.dll` 拷进 `build\Release\` 后 `DispatchCost.exe NativeDll.dll 351233 300 8`

### 22.5 追问："`IJob` 是不是没加 `[NativeTranspile]`？没加为何还生成 CPP？"

**事实（三段代码对齐）**：
1. **`ZeroCellsJob` 有特性**：CSBS `CPUBattleSpatialHash.cs:214` 就是
   `[NativeTranspile(Target = BackendTarget.Cpp)] public unsafe struct ZeroCellsJob : IJob`。
   ⇒ **不是"漏了特性"**。
2. **生成器为 `IJob` 确实产出三件套**（都已存在于 `NativeTranspiler_Generated/`）：
   - `SharpNative_Job_CPUBattle_ZeroCellsJob_Execute.cpp` —— 内核（19 行，`for` + 尾调 `memset`）
   - `SharpNative_Job_CPUBattle_ZeroCellsJob_Execute_Adapter.cpp` —— **原生 adapter + getter**：
     ```cpp
     GENERATED_API void ..._Execute_Adapter(void* context) {
         auto* Counts_ptr = *(int**)((char*)context + 0);
         int   Counts_length = *(int*)((char*)context + 8);
         auto* Length_ptr = (int*)((char*)context + 32);
         SharpNative_Job_CPUBattle_ZeroCellsJob_Execute(Counts_ptr, Counts_length, Length_ptr);
     }
     GENERATED_API void* ... Get_..._Execute_AdapterPtr() { return (void*)..._Execute_Adapter; }
     ```
   - `[DllImport]` 声明 `..._Execute(...)`（`BindingsGenerator.cs:473-474`）
3. **断层在绑定层**：`BindingsGenerator.cs` 生成调度入口时，`IJobParallelFor` 走
   `s_X_BatchFuncPtr = Get_X_Execute_AdapterPtr();`（L355，**原生→原生**），
   而 `IJob` 走 **`else` 分支**（L358-366）：
   ```csharp
   s_X_JobFunc = (IntPtr context) => { var jobPtr = (T*)context; X_Execute(...); };   // 托管 lambda
   s_X_JobFuncPtr = Marshal.GetFunctionPointerForDelegate(s_X_JobFunc);              // -> 托管 thunk
   ```
   ⇒ **adapter 与 getter 都生成了，但 `IJob` 的绑定没有引用它**，反而现场造了一个**托管 lambda + P/Invoke 反向 thunk**。

**所以"为何生成 CPP"的答案是**：生成器对 `IJob` 的设计就是"生成原生内核 + 提供 adapter/getter 供调度器直调"，
`IJobParallelFor` 已按此接上；**`IJob` 分支只完成了一半**（adapter 生成了、没接线）——这是一个**不完整实现**，
不是"没有特性所以不该生成"。

**为什么这仍然只值 ~1 µs/次（量级来自 §22.2）**：
- 托管 lambda 的每次派发多一次 native→managed→native 转换 + `GCHandle`/delegate 间接调用；
  但 `NOOP` 臂测得**整个 `Schedule+Complete` 往返也才 1.1–1.3 µs**，故这一项**上界就是 1 µs 量级**。
- ⇒ **值得修（改动小、语义等价），但不要期待它解决游戏内 0.25 ms 的 `zero` 差距**（那是冷 cache，§22.2）。

**衍生问题（本轮顺带发现，记账用）**：`IJob` 的 `UseISPC_MT` 分支（L299-348）**故意**保留托管 delegate
（注释："MT（task 内部自己调度）仍用托管 delegate：原生 Adapter 只生成非 MT 变体"）⇒ 那条是**有意的**，
与 `IJob` 的"半成品"性质不同，修的时候不要一起动。

**运行时侧的第二重证据（同一条链，独立复核）**：`EntJoy.Jobs/Native/NativeJobScheduler.cs:296-304`
```csharp
public static NativeJobHandle Schedule<T>(ref T job, NativeJobHandle? dependsOn = null) where T : struct, IJob
{
    ...
    var cache = NativeJobCore.JobDelegateCacheFor<T>.Cache;          // 托管 delegate
    NativeJobCore.ScheduleRaw(cache.FuncPtr, ...);                   // FuncPtr = GetFunctionPointerForDelegate
}
```
⇒ **`IJob` 的托管侧根本没有"取原生 getter"的路由**（对比 `IJobParallelFor` 走 `ScheduleParallelForBatchRaw`，
其 `cache.FuncPtr` 来自生成的 `s_X_BatchFuncPtr = Get_X_Execute_AdapterPtr()`）。
**两处必须一起改**才算接通：① 发射面 `BindingsGenerator` 的 `IJob` 分支改用 adapter getter；
② 运行时 `NativeJobScheduler.Schedule<T>` / `JobDelegateCacheFor<T>` 增加原生指针来源（或按"有 adapter 就用原生"选择）。
**这也是为什么改动虽小、却要动两处**（发射面 + 运行时），验收必须覆盖两条路径。

**第四重独立验证（本节的"无中生有"检查）**：全树搜 `ZeroCellsJob` 只命中
`CPUBattleSpatialHash.cs`（内核源码）与已编译的 `ComputeShaderBattleSimulation.dll` / `NativeTranspiled.dll`
⇒ **不存在生成的 `Schedule_ZeroCellsJob` 绑定文件**（`IJobParallelFor` 会生成）
⇒ 与"`IJob` 分支现场造托管 lambda"的读法一致，不是"有绑定但被切走"。

### 22.6 定性：**这是 EntJoy 的缺陷（漏接线 / 半成品），不是有意设计**

**历史取证（三条命令，可复核）**：
1. `git log -L 358,366:.../BindingsGenerator.cs` ⇒ `IJob` 分支的这 9 行**自首次提交 `c382dbd`（"将 JobSystem 改为 C++ 版"）就存在，此后从未被改动**。
2. `git log -S '原生→托管→原生' -- BindingsGenerator.cs` ⇒ 那句"消除托管 delegate 双重转换（每 tile ~3 µs）"的注释
   来自 **`2eed4ad`（2026-08-20，一次 refactor）**；该提交**同时**把
   `IJobChunk`/`IJobEntity` 改成 `Get_X_Chunk(EntityBatch)AdapterPtr()`、把 `IJobParallelFor`(非 MT) 改成 `Get_X_Execute_AdapterPtr()`，
   **却把 `IJob` 留在 `Marshal.GetFunctionPointerForDelegate`**（同一次 diff 里两者并存，`s_X_JobFuncPtr` 行未被改）。
   ⇒ **同一次重构里只接了三分之二**：chunk 系 ✅、批处理 ✅、**单任务 `IJob` ❌**。
3. `git show 55f7001`（"perf(native): ZeroCellsJob 转译（对齐 Unity）"）⇒ 那次是**专门**给 `ZeroCellsJob` 加特性的，
   提交信息写"**由托管标量变原生内核**（B 侧 `Bb0M1ZeroJob` 是 Burst AVX2）"，但**只改了 `CppJobGenerator`（生成内核）**，
   **没有改 `BindingsGenerator`（绑定）** ⇒ 内核转译了、调用路径仍是托管的。
   提交信息里的实测是"整步 −0.73(4/8)、Build −0.04(6/8)"——**这是"托管标量 → 原生内核"的收益；
   而"原生内核 → 原生直调"这一步的收益从未被测量过**（因为没接线）。

**⇒ 定性结论**：`IJob` 走的不是原生直调，是 **EntJoy 转译器绑定层的漏接线**：
- 生成器已产出 `X_Execute`（内核）+ `X_Execute_Adapter`（适配器）+ `Get_X_Execute_AdapterPtr`（getter），
  `CppJobGenerator` 侧对 `IJob` 一视同仁地生成了；
- `BindingsGenerator` 侧只有 `IJob` 没引用 getter；**运行时侧 `NativeJobScheduler.Schedule<T>` 也根本没有原生的路由**（§22.5）。
- **两处都在 EntJoy**（`src/NativeTranspiler`、`src/EntJoy.Jobs`），**与 CSBS 无关** ⇒ 按"不改测试端"的约束**可以修**。

**量级仍需实测（不要重复 `55f7001` 的乐观）**：
- ⚠ **2026-10-02 实测更正**：本节初稿写"上界 = NOOP 1.1–1.3 µs/次"——**这个预测是错的**（见 §23）。
  真实量级大两个数量级：`JobScheduler.Schedule(ref job)` 这条运行时路径调的是
  `CreateJobCallback<T>` 里的 **`job.Execute()` = 托管 C#**（原生内核是**死代码**），
  所以修复不是"省一次 thunk"，而是"**整个内核从托管 JIT 变成原生 C++**"。
  实测 `zero` **0.28 → 0.14 ms/步（−50%）**。
- `55f7001` 之谜就此解开：它给 `ZeroCellsJob` 加了特性却只测到 −0.04 ms(Build)/−0.73(整步, 4/8)，
  **因为宿主走的是运行时路径，而那条路径从来不看 `[NativeTranspile]`**。

**修法（两处，语义等价）**：
1. **发射面** `BindingsGenerator`：`IJob` 分支改用 `s_X_JobFuncPtr = Get_X_Execute_AdapterPtr();`（与 L355 同形），
   并同步生成 `[DllImport] Get_X_Execute_AdapterPtr()` 声明（当前只在批处理分支生成，`IJob` 分支没有）。
2. **运行时** `NativeJobScheduler.Schedule<T>`（`NativeJobCore.JobDelegateCacheFor<T>`）：为 `IJob` 增加
   "若该 job 存在原生 adapter getter，则取原生指针"的路径（否则回退托管 delegate，保证未转译 job 不受影响）。
3. **硬门禁**：原生测试 **9/9 ×（新旧绑定）** + 转译器单测（含 emit-snapshot：默认档发射面逐字不变是 `55f7001` 立的规矩）。

---

## 23. ⭐⭐ 修复落地：`IJob` 原生直调（2026-10-02，提交 `a80bd17`）

### 23.1 真正的缺陷比 §22.6 判断的**更严重**：原生内核是**死代码**

修复前有**两条**调度入口，**行为完全不同**：

| 入口 | 修复前实际执行的内核 | 说明 |
|---|---|---|
| 生成扩展 `job.Schedule()` → `NativeExports.Schedule_X` | 托管 thunk → **P/Invoke 原生内核** `X_Execute(...)` | 生成代码里 lambda 体是 `X_Execute(args)`（DllImport），内核**是原生的**，只多一层 thunk |
| **`JobScheduler.Schedule(ref job)`**（**宿主实际用的这条**） | 托管 thunk → **`job.Execute()` = 托管 C#** | `NativeJobCore.CreateJobCallback<T>` 直接调托管 `Execute()`；**原生内核是死代码** |

⇒ **宿主（CSBS）里的 `ZeroCellsJob` 及其它单任务 `IJob`，其 `[NativeTranspile]` 从未生效**；
`55f7001`"给 ZeroCellsJob 加特性"之所以只测到 −0.04 ms(Build)，正是因为它走的运行时路径不看这个特性。
⇒ §22.6 里"上界 = NOOP 1.1–1.3 µs/次"的预测**错了两个数量级**：省掉的不是一次 thunk，而是**整个托管 JIT 内核**。

### 23.2 修复内容（全在 EntJoy，**宿主源码零改动**）

| 位置 | 改动 |
|---|---|
| `BindingsGenerator`（发射面） | `IJob` 分支：`s_X_JobFuncPtr = Get_X_Execute_AdapterPtr()`（原为托管 lambda）；补 `[DllImport] Get_X_Execute_AdapterPtr()` 声明；删掉随之变死的 `JobFuncDelegate` 字段（消 CS0169）；静态构造里 `RegisterNativeJobAdapter(typeof(T), ptr, totalSize)` |
| `NativeJobScheduler`（运行时） | 新增 adapter + ctxSize 注册表；`Schedule<T>` **优先原生**：`开关 && 无非托管引用 && 有 adapter && 有字段写入器`；不满足/失败 ⇒ **原样回退托管**（未转译 job 行为不变） |
| `NativeJobCore` | 新增专用 `HGlobalCleanupPtr`。⚠ **不能复用 `Cleanup`**：那条做 `dataPtr - sizeof(int)` 再 `ContextPool.Return`，而 HGlobal 块没有那 4 字节前缀 ⇒ 越界读 + 池污染 |
| 开关 | `ENTJOY_NATIVE_SINGLE_JOB=0` 回退托管（A/B 对照臂 + 安全阀） |

**布局安全的关键**：原生 adapter 按 C++ 偏移读 ctx，而 Debug 下 `NativeArray` 带 DisposeSentinel ⇒ 裸拷贝不可靠。
故**必须**用生成代码的逐字段写入器 `WriteJobFields_X`，ctx 尺寸取其 `totalSize`（不能用 `Marshal.SizeOf<T>()`，含泛型 NativeArray 会抛）。
生成物核对（`tools/gate-run/genbind/`）：`WriteJobFields_ZeroCellsJob` 与 `Schedule_ZeroCellsJob` 的写入偏移**逐字节一致**（0/8/32，size=36），与 C++ adapter 的读取偏移一致。

### 23.3 运行时自证（不可伪造）

```
[NATIVEJOB] single-job native direct-dispatch: on (default 2026-10-02; =0 falls back to managed thunk)
[NATIVEJOB] IJob native direct-dispatch wired: LerpUploadJob (ctx=480B)
[NATIVEJOB] IJob native direct-dispatch wired: ZeroCellsJob (ctx=36B)
[NATIVEJOB] IJob native direct-dispatch wired: YSortScanRangeJob (ctx=112B)
[NATIVEJOB] IJob native direct-dispatch wired: YSortKeyRangeJob (ctx=120B)
[NATIVEJOB] IJob native direct-dispatch wired: YSortRangeJob (ctx=168B)
[NATIVEJOB] IJob native direct-dispatch wired: YSortScatterRangeJob (ctx=232B)
[NATIVEJOB] first native direct-dispatch: ZeroCellsJob           ← 运行时真的走了原生
```
`ENTJOY_NATIVE_SINGLE_JOB=0` 时：只有 `wired` 行、**没有** `first native direct-dispatch` ⇒ 确实回退了。

### 23.4 实测（对齐档，同会话 A/B，`-DeclareSpread`，各 4 rep）

**`zero`（与仿真密度无关的固定 1.4 MB 填零，唯一无混杂的指标）**：

| 臂 | win1 | win2 | win3 |
|---|---|---|---|
| managed（`=0`） | 0.54 / 0.54 / 0.53 / 0.48 | 0.28 / 0.33 / 0.32 / 0.29 | 0.24 / 0.27 / 0.30 / 0.23 |
| **native（默认）** | **0.33 / 0.26 / 0.27 / 0.23** | **0.12 / 0.13 / 0.16 / 0.15** | **0.12 / 0.15 / 0.16 / 0.12** |

⇒ **12 个窗口零重叠**，**0.28 → 0.14 ms/步（−50%）**。这是本次修复的直接收益（**且与密度/相位无关**）。

**整步（⚠ 不可用）**：managed 中位 **165.29** vs native 中位 **182.91** ms——但两臂**轨迹发散了**
（每臂内 `存活` 波动 ±8%~30%、Melee/Flow 同步偏高），且本项目已多次记录"**越快越显慢**"的相位陷阱
（同一 wall-clock 窗口内步数更多 ⇒ 密度更高 ⇒ Melee 更大）。
⇒ **整步差不能归因于本次修复**；要判定必须**相位对齐**（`ab-aligned.ps1` 的 A-vs-B 会做，A-vs-A 不会）。
**待办**：给 A-vs-A 也加相位对齐（按步序号取窗口），或只认 `zero`/YSort 这类**与密度无关的分项**。

### 23.5 影响面（比 `zero` 一条更大）

本项目有 **6 个单任务 `IJob`** 在这条路径上：`ZeroCellsJob` + `LerpUploadJob` + **4 个 `YSort*RangeJob`**。
`YSort*` 在**渲染路径开启**（本 A/B 用 `NO_RENDER_PATH=1` 把它们跳过了）时每步 4 次派发、作用在 1e6 元素上
⇒ **之前也全是托管 JIT 在跑**。**待办**：在渲染路径开启的配置下复测 YSort 四项（预期是本修复的更大头）。

### 23.6 门禁（全过）

| 门禁 | 结果 |
|---|---|
| 转译器单测 | **101/101 通过** |
| 原生测试 | **9/9 rc=0 ×（`TILE_RUN` 关/开）** |
| jobs-only 耦合守卫（`check.ps1`） | **PASS**（bindings 14920 B，零 ECS 耦合） |
| 生成物核对 | `WriteJobFields_X` 与 `Schedule_X` 偏移逐字节一致（0/8/32, 36B） |
| 游戏内 | 跑通、**0 异常**、自证横幅齐全 |
| 回退 | `ENTJOY_NATIVE_SINGLE_JOB=0` 两态自证正确 |

---

## 24. 同类修复 + 修复后性能对比（2026-10-02，提交 `558daf4`）

### 24.1 同类问题的边界（**先取证，不扩散**）

宿主里所有静态调用点只有 **6 处**，**全部是 `IJob`**（已由 §23 修复）：
`CPUBattleSpatialHash.cs:141`(zero)、`CPUBattleEcs.Lerp.cs:184`(LerpUpload)、`CPUBattleYSort.cs:165/189/223/275`(YSort×4)。
其余 **72 处走 `job.Schedule(...)` 扩展** ⇒ 命中的是**生成的 per-job 绑定**（早已原生）。
⇒ **同类问题在宿主里已闭环**；剩下的同类点在 **EntJoy 运行时的静态 API**。

### 24.2 ABI 核对（决定"能不能直换"）

| 形态 | 原生 typedef | 生成的 adapter | 结论 |
|---|---|---|---|
| `IJob` | `void(void*)` | `void(void* context)` | ✅ 一致（§23 已修） |
| `IJobParallelFor` / `IJobParallelForBatch` | `BatchJobFunc = void(void*, int start, int count)` | `void(void* ctx, int __startIndex, int __count)` | ✅ **逐字一致** ⇒ 可直换 |
| `IJobFor` | `IndexJobFunc = void(void*, int index)`（`ScheduleFor`） | 生成的是 **Batch 形** adapter | ⚠ **形状不同** ⇒ 直换等于改调度语义，**不动**（已在代码注释写明，与 IJob 的"漏接线"性质不同） |

### 24.3 改动（提交 `558daf4`）

- `NativeJobScheduler`：新增 `TryScheduleBatchWithNativeAdapter`（复用同一 adapter 注册表 + 字段写入器 + `HGlobalCleanupPtr`）；
  `ScheduleParallelFor` / `ScheduleParallelForBatch` **优先原生**，否则回退托管。
- `BindingsGenerator`：为 `IsParallelForJob || IsParallelForBatchJob` 且**非 MT** 且 `explicitOk` 的类型注册
  `s_X_BatchFuncPtr`（MT 变体的托管 delegate 是**有意保留**的 ⇒ 不注册）。
- 自证：`[NATIVEJOB] native direct-dispatch wired: <类型> (ctx=NB)` —— 本工程共 **35 个类型**接线；
  `first native direct-dispatch: ZeroCellsJob`。
- 门禁：转译器 **101/101**；jobs-only 守卫 **PASS**（15224 B、零 ECS 耦合）；
  对齐档 `[M-19] count=0.9798 place=1.7300`（仍是 batch=64 + Spread）；**0 异常**。

### 24.4 ⭐ 修复后性能对比（同会话配对 `W0Player`，3 rep 中位，ms）

**对齐档**（逐 job 镜像 Unity batch + count/place 声明 Spread + `ENTJOY_CLAIM_ADAPT=0`）：

| 分项 | EntJoy 对齐 | Unity | A/B | 修复前 A/B |
|---|---|---|---|---|
| **zero** | 0.118 | 0.095 | **1.24×** | **≈2.5×** ⇒ 赤字腰斩 |
| count | 1.022 | 0.854 | 1.20× | 1.19–1.25× |
| prefixPartial | 0.040 | 0.048 | 0.83× | 0.83× |
| prefixFinal | 0.197 | 0.139 | 1.42× | 1.42× |
| place | 1.928 | 1.836 | 1.05× | 1.05–1.19× |
| Build 段 | 3.73 | 3.04 | 1.23× | 1.23–1.33× |
| **Flow** | 32.57 | 34.03 | **0.96×（反超）** | 0.96–0.97× |
| Melee | 124.06 | 121.96 | 1.02× | 1.01–1.02× |
| **MarkDead** | 0.64 | 0.87 | **0.74×（反超）** | 0.77× |
| **Integrate** | 4.41 | 2.70 | **1.63×** | 1.65× |
| **整步** | **165.55** | **163.56** | **1.012×** | 1.005–1.03× |

**默认档**（出厂默认：无批表 + F6 自适应开）：

| 分项 | EntJoy 默认 | Unity | A/B |
|---|---|---|---|
| **zero** | 0.121 | 0.103 | **1.17×** |
| count | 0.993 | 0.878 | 1.13× |
| place | 1.993 | 1.796 | 1.11× |
| Build 段 | 4.03 | 2.91 | 1.39× |
| Flow | 32.43 | 33.41 | **0.97×（反超）** |
| Melee | 111.40 | 120.80 | **0.92×（反超）** |
| MarkDead | 0.56 | 0.86 | **0.65×（反超）** |
| Integrate | 3.10 | 2.61 | 1.19× |
| **整步** | **151.43** | **161.38** | **0.938×（快 6.2%）** |

### 24.5 诚实标注

- **`zero` 是本修复的干净证据**：它与仿真密度无关（固定 1.4 MB 填零），A/B 从 ≈2.5× 降到 **1.24×**
  （对齐档）/ **1.17×**（默认档）——这是"内核从托管 JIT 变原生 C++"的直接结果。
- **整步比值仍在噪声带内**（对齐档 1.005→1.012、默认档 0.93-0.94）：本修复对整步的贡献是 ~0.1 ms 量级（0.06%），
  远小于跨会话漂移（本次 B 侧整步 156.7–168.6）。**不要用整步变化声称本修复的收益**，用 `zero` 分项。
- **未做**（诚实）：① A-vs-A 的**相位对齐**（`ab-aligned.ps1` 的 A-vs-B 有相位对齐，A-vs-A 没有）；
  ② **渲染路径开启**下复测 `YSort*` 四项（本 A/B 用 `NO_RENDER_PATH=1` 把它们跳过了）——
  那才是本修复可能的更大头（每步 4 次 × 1e6 元素，此前全是托管 JIT）。

---

## 25. 覆盖补齐：`IJobFor` 原生直调 + 注册时机 + 泄漏审计（2026-10-02）

### 25.1 覆盖审计：EntJoy 只有 4 个 job 接口，`IJobFor` 是漏的那个

| job 接口 | 生成扩展路径 | 运行时静态 API 路径 | 状态 |
|---|---|---|---|
| `IJob` | ✅ 原生（§23 修） | ✅ 原生（§23 修） | 完整 |
| `IJobParallelFor` | ✅ 原生（原有） | ✅ 原生（§24 修） | 完整 |
| `IJobParallelForBatch` | ✅ 原生（原有） | ✅ 原生（§24 修） | 完整 |
| **`IJobFor`** | ✅ 原生（原有，走 Batch adapter） | ❌ **托管** ⇒ 本节修 | **本节补齐** |

（`IJobChunk` / `IJobEntity` 在 `EntJoy.ECS`，走 `ChunkJobScheduler` + `Get_X_Chunk(EntityBatch)AdapterPtr()`，**一直是原生**。）

### 25.2 为什么 `IJobFor` 不能简单换指针，以及正确解法

- 原生 `Scheduler::ScheduleFor` 的语义是**单线程串行**：`for (i<length) func(ctx,i)`，注释"异步单任务 Job：单线程执行"，
  其 typedef 是 `IndexJobFunc(void*, int index)`。
- 而 `CppJobGenerator` 为 `IJobFor` 生成的 adapter 是 **Batch 形** `(void* ctx, int start, int count)`
  ⇒ 把它交给 `ScheduleFor` 会造成**签名错位**（第二个参数被当成 start，count 读到垃圾）。
- **正确解法**：走**批量入口**并显式 `batchSize = length`
  ⇒ `cs = length` ⇒ `rc = CeilDiv(length,length) = 1`
  ⇒ 命中批量入口的 `rc<=1` 快路径 `func(context, 0, length)`（**提交线程单线程**执行）
  ⇒ 与"单线程串行"**逐字对应**（adapter 内部按序循环 `0..length-1`）。
  且与**生成扩展**一致：`job.Schedule(len, batch)` 本来就经 `ScheduleParallelForBatchRaw`。
- `ScheduleFastPath` 已核对：**尊重依赖**（无依赖/已完成 ⇒ 立即；未完成 ⇒ 挂 continuation），异常路径也调用 cleanup。

### 25.3 ⚠ 注册时机（本节踩到的真坑）：注册必须钉在**程序集加载**时

**现象**：验证台第一次跑，`PASS` 通过但 `first native direct-dispatch` **不出现** ⇒ 说明没走原生。
**根因**：原生 adapter / 字段写入器的注册发生在生成类 `NativeExports` 的**静态构造**里，
而它只在**该类首次被触碰**时执行。若调用方在触碰它之前就走运行时静态 API，那一刻注册表为空 ⇒ **静默回退托管**。
**修法**：生成的 `NativeExports` 里新增
```csharp
[global::System.Runtime.CompilerServices.ModuleInitializer]
internal static void __EntJoyInitNativeJobRegistrations() { _ = typeof(NativeExports); }
```
把注册钉在**程序集加载**时，消除对外部调用顺序的依赖。
**教训（方法层面）**：验证"是否走原生"**不能只看功能正确**——回退路径也正确；必须看
`first native direct-dispatch` 自证，并且**验证用的调度要跑在注册之后**（本节的探针现在显式
`RuntimeHelpers.RunClassConstructor(typeof(NativeExports).TypeHandle)`，使检查自包含）。

### 25.4 运行时验证（`tools/BuildPassBench`，Exe + 原生编译，正反两态）

新增 `BenchForProbeJob : IJobFor`（`Values[index] += 1`）+ 覆盖检查（走 `JobScheduler.ScheduleFor`）：

| 态 | 覆盖检查 | 自证 |
|---|---|---|
| **native（默认）** | `PASS,IJobFor_native_coverage,n=1000,each_index_exactly_once=1` | `first native direct-dispatch: BenchForProbeJob` ⇒ **确实走原生** |
| managed（`ENTJOY_NATIVE_SINGLE_JOB=0`） | 同上 PASS | 无 first-use ⇒ **回退路径也正确** |

判据是"**每个 index 恰好执行一次**"（漏跑/重跑都会失败）⇒ 同时验证了"分批执行不丢不重"。

### 25.5 指针 / 内存泄漏审计（回答"当前没有纯指针的'存储'变量吧"）

**新增的堆分配只有一处**：每次原生派发 `Marshal.AllocHGlobal(ctxSize)`（ctxSize = 生成代码的 `totalSize`，如 `ZeroCellsJob` = 36 B）。
配套释放路径（**逐条核对**）：

| 情形 | 释放 |
|---|---|
| 正常完成 | 原生 batch 退役 → `cleanup(ctx)` = `NativeJobCore.HGlobalCleanupPtr` → `Marshal.FreeHGlobal` |
| `ScheduleRaw` 返回无效句柄 | 调用方就地 `Marshal.FreeHGlobal(ctx)` 后回退 |
| 字段写入器抛异常 | `catch` 内 `Marshal.FreeHGlobal(ctx)` 后回退 |
| **不能复用 `Cleanup`** | 那条做 `dataPtr - sizeof(int)` 再 `ContextPool.Return`（只有 `AllocContext` 的块带 4 B 前缀）⇒ HGlobal 块会**越界读 + 池污染**。故专门新增 `HGlobalCleanupPtr`（§23.2） |

**静态注册表里存的是"代码地址"，不是我们拥有的内存**：
`s_nativeJobAdapterPtrs`(IntPtr=模块内函数地址)、`s_nativeJobCtxSizes`(int)、`s_jobFieldWriters`(Delegate)、
`s_nativeFirstUseLogged`(HashSet\<Type\>)。它们进程级存活、数量 = job 类型数（本工程 35），**不随派发增长**。
⚠ 但**必须保持委托存活**（`Marshal.GetFunctionPointerForDelegate` 的经典坑）：`_hglobalCleanup` / `_cleanup` /
`_managedCleanup` 都是 `static readonly` 字段 ⇒ 被静态根引用，不会被 GC 回收。

**实证（硬判据）**：密集派发 **200,000 次**后进程私有内存
`priv_before=78.3 MB → priv_after=78.3 MB，delta=0 KB`（若每拍漏 32 B ⇒ 应 +6.4 MB）
⇒ `PASS,native_dispatch_leak_soak,iters=200000,delta_kb=0`。

### 25.6 门禁（本节全过）

转译器单测 **101/101**；jobs-only 守卫 **PASS**（15593 B、零 ECS 耦合，含新的 `[ModuleInitializer]` 成员）；
原生测试 **9/9 rc=0 ×（`TILE_RUN` 关/开）**；`IJobFor` 覆盖检查正反两态 PASS；泄漏 soak PASS（0 KB）。

---

## 26. 通解化：`IJobFor` 的**真·原生单线程串行** adapter + 复用抽离 + ctx 池化（2026-10-02）

### 26.1 问题：§25 的 `batchSize = length` 是"等效绕法"，不是"翻译支持"

§25 用"批量入口 + `batchSize = length` ⇒ `rc=1` ⇒ 快路径串行"实现了 `IJobFor` 的原生直调，
语义**等价**（都是单线程按序跑完所有 index），但**形态不对**：它借道批形 adapter
`(void*, int start, int count)`，而 `IJobFor` 的语义形态是 `IndexJobFunc(void*, int index)`。
⇒ 本轮把**转译器对 `IJobFor` 的翻译**补正：为它发射**专用的 index 形 adapter**，
运行时 `ScheduleFor` 用原生 `JobScheduler_ScheduleFor` 直调（不再借道批量入口）。

### 26.2 转译器改动（含"可复用部分抽离"）

| 改动 | 内容 |
|---|---|
| **新增 index 形 adapter** | 为 `IsForJob` 的 job 额外发射 `X_Execute_Adapter_Index(void* context, int __index)` + `Get_X_Execute_Adapter_IndexPtr()`。实现 = 解包同一份字段后调 `X_Execute_Batch(__index, 1, …)`（内核本就按 `[start, start+count)` 循环 ⇒ `count=1` 即"只跑该 index"；两者同处一个 unity TU ⇒ 可内联） |
| **⭐ 抽离复用** | 把 adapter 的"字段解包"收敛为 `BuildAdapterFieldAccess(jobStruct, fieldReads, callArgs)`：批形与 index 形**字段解包逐字相同，只有前缀实参不同**（`__startIndex, __count` ↔ `__index, 1`）。原来那段 40 行内联代码被两处共用 |
| **命名规则统一** | `CppJobNames.GetIndexAdapterFunctionName()` / `GetIndexAdapterPtrGetterName()`（复用批形的 `<base>_Adapter` 规则再加 `_Index`），C++ 导出名与 C# DllImport 名由**同一函数**派生 ⇒ 不会两边拼错 |

### 26.3 运行时改动（ctx 池化，回答"要不要用智能指针"）

| 改动 | 内容 |
|---|---|
| **ctx 改用框架自带 `ContextPool`** | `NativeJobCore.RentMarshalledContext(size)`：与既有 `AllocContext` **同一套 4 字节长度前缀**租块 ⇒ 释放**复用同一个 `CleanupPtr`**（回池 + 释放读写声明）。**删掉 per-dispatch 的 `Marshal.AllocHGlobal` / `FreeHGlobal`**（上一版为此专门加的 `HGlobalCleanupPtr` 也随之不再需要） |
| **三种形态共用一步** | `TryRentMarshalledContext`（租块 + 逐字段写入）被 单任务 / index / 批 三个 `TrySchedule*WithNativeAdapter` 共用，不再三处重复 |
| **`IJobFor` 走 index 形** | `ScheduleFor<T>` 查 `s_nativeForAdapterPtrs`（与批形**分开**存，避免 ABI 混用）⇒ `ScheduleForRaw(indexAdapter, …)` = 真·原生单线程串行 |
| 自证 | `[NATIVEJOB] IJobFor index-shaped native adapter wired: <T> (ctx=NB)` + `first native direct-dispatch: <T>` |

**⚠ 为什么不用智能指针（`unique_ptr` / `shared_ptr`）**：

1. **跨 C ABI 边界**：ctx 由**托管侧**租、由**原生调度器**在 batch 退役时经 `cleanup` 回调释放 —— 这是一条
   `void* + 回调` 的 C 接口（`Exports.h` 的导出签名）。`unique_ptr`/`shared_ptr` 是 C++ 概念，
   **无法作为 ABI 类型跨越这条边界**（跨 DLL、跨语言，且各 TU 的 ABI 不保证一致）。
2. **所有权语义已经明确**：`Schedule(func, ctx, cleanup, dep)` 的契约就是"**cleanup 恰好一次**"，
   由原生 `ScheduleFastPath` / batch 退役路径保证（本次已逐路径核对：正常完成、依赖未满足、异常都调用）。
   `shared_ptr` 的原子引用计数在这里**只增加成本、不增加正确性**（独占有主，无共享所有权）。
3. **`shared_ptr` 会伤害热路径**：每派发一次原子 RMW 引用计数，正是我们在 §18 里花大力气消掉的那类开销。
4. **真正该做的（已做）**：把每派发的 `malloc/free` 换成**框架自有池**（`ContextPool`）——
   这才是"更好的所有权模型"在这个场景下的形态：**arena/pool + 显式单次释放契约**，而不是智能指针。
5. 唯一适合 RAII 的地方是 **C++ 适配器内部的临时量**（异常安全），而生成代码里那些都是栈上/平凡类型，
   不需要智能指针。

### 26.4 验证（`tools/BuildPassBench`：覆盖 + 泄漏 soak，正反两态）

| 态 | 覆盖检查（`ScheduleFor`，n=1000） | 泄漏 soak（20 万次派发） | 自证 |
|---|---|---|---|
| native（默认） | `PASS,each_index_exactly_once=1` | `PASS,delta_kb=0` | `IJobFor index-shaped native adapter wired: BenchForProbeJob` + `first native direct-dispatch: BenchForProbeJob` |
| managed（`=0`） | 同上 PASS | 同上 PASS | 有 `wired`、**无** first-use ⇒ 回退正确 |

门禁：转译器 **101/101**、jobs-only 守卫 **PASS**、原生 **9/9 × `TILE_RUN` 两态**、宿主重建 **0 错误**。

### 26.5 门控清单：**该默认开的已经全开了**

| 门控 | 现状 | 依据 |
|---|---|---|
| `ENTJOY_TILE_RUN`（F5 tile-run 合并） | **默认开** | §18/08 §14：对齐档 −18.66 ms（3/3） |
| `ENTJOY_CLAIM_SPAN`（F1 元素跨度认领，内置 1024） | **默认开** | §18/08 §12.3：R1 对齐档 −54 ms、R2 默认档无回归 |
| `ENTJOY_TILES_UNIFORM`（F2 不物化 tileBuffer） | **默认开（薄 tile 门控）** | §18：对齐档 −9.4 ms；**无条件开会伤默认档 +4.0 ms** ⇒ 加 `cs≤16` 门 |
| `ENTJOY_TILE_FASTPATH`（F4 每批快照） | **默认开（同门）** | §18：对齐档 −2.1 ms、默认档中性 |
| `ENTJOY_CLAIM_ADAPT`（F6 按 job 学几何） | **默认开** | 通解落地：默认档 count −52%、Build −19% |
| `ENTJOY_NATIVE_SINGLE_JOB`（原生直调） | **默认开** | §23–§26：内核从托管 JIT 变原生（`zero` −50%）；`=0` 回退 |
| `ENTJOY_CLAIM_GUIDED` | **保持关** | §19.2：新结构下尾部不平衡上界 ≈0.025%；且**与 F2/F5 互斥**，开它会丢掉 −13.9 ms |
| `ENTJOY_CLAIM_SLICE` / `ENTJOY_TILE_STRIDE` / `ENTJOY_CLAIM_BLOCK` | **保持关** | §21.1：三个全局方案**各自更差**（`TILE_STRIDE` 是灾难：Melee 122→473 ms） |
| `ENTJOY_VALUE_BIND` / `ENTJOY_SCALAR_RESTRICT` | **保持关** | 08 §30–§32：单独用无效/仅 −1.2% |
| `ENTJOY_JOB_BATCH_TABLE` | 不设（= 默认档） | 它是"对齐档"的**测量控制量**，不是产品门控 |

**判据（我把这个当纪律）**：一个门控要提默认，必须同时满足 ① 同会话 A/B 有可辨收益（3/3 或 ≥4/6 且方向一致）；
② **产品档（默认档）无回归**；③ 保留 `=0` 回退与启动自证横幅。上面"默认开"的六项都过了这三条；
"保持关"的六项各自有否证数据。**⇒ 没有"再打开就能更快"的门控了。**

---

## 27. 8 worker vs 15 worker：EntJoy（默认/对齐）对 Unity（8/15）

**机器**：`logical=16 physical=8` ⇒ **15 worker = SMT 超订**（16 逻辑线程跑在 8 物理核上）。
器械：`ab-aligned.ps1` 新增 `-Workers N`（同时设 `ENTJOY_JOB_WORKERS` 与 `M4_WORKERS`）；3 rep，同会话配对。

### 27.1 EntJoy 侧（A，整步步均中位，ms）

| 档 | 8 worker | 15 worker | 8→15 |
|---|---|---|---|
| **默认档** | **153.81** | **113.31** | **−26.3%** |
| **对齐档** | **161.30** | **116.84** | **−27.6%** |

（15-worker 的 rep1 是冷起异常值：148.91 / 113.31；取 reps2–3 均值 115.20 / 113.89，结论不变。）

**⇒ EntJoy 在 15 worker 上收益巨大（−26~28%）**，因为 8 worker 时只用了 8 个逻辑线程、另一半物理核的 SMT 兄弟闲置。

### 27.2 Unity 侧（B）⚠ **15-worker 数据不稳定**

| 档 | 8 worker（整步中位） | 15 worker（三次原始值） | 15 worker 中位 |
|---|---|---|---|
| 默认档 | 164.48 | 119.20 / **188.52** / 153.51 | 153.51 |
| 对齐档 | 159.87 | **206.50** / 120.68 / 120.71 | 120.71 |

⇒ **B 的 15-worker 三次里必有一次严重异常**（206.5 与 188.5），而同批的 `warm` 已按相位对齐（warm=50/50/51）。
**这是测量问题，不是结论**：Unity 播放器在 16 逻辑线程（15 job worker + 主线程）下的相位/预热行为与 A 侧不可比，
需要专门协议（更多 rep + 固定 warm + 更长的统计窗）才能给单点比值。

### 27.3 能下的结论（只用可辩护的部分）

| 对比 | 结论 |
|---|---|
| A：8→15 worker | **−26~28%（稳，3 rep 方向一致）** |
| B：8→15 worker | **不确定**（B 15-worker 原始值跨 120–206 ms） |
| A(15) vs B(15) | **只能给区间**：用 reps2–3，对齐档 A 115.2 vs B ~120.7 ⇒ **A 快约 5%**；默认档 A 113.9 vs B 133.7–153.5 ⇒ **A 快 15–26%** |
| A(8) vs B(8) | 对齐档 **1.009×**、默认档 **0.935×**（§24.4，稳定） |

⚠ **不要**用 §27.2 的中位数对外声称"15 worker 下 A 大胜"——那是 B 侧异常值造成的。要坐实必须重跑协议。

### 27.4 当前仍差的项（对齐档，§24.4 + §27 的稳定口径）

| 项 | A/B | 绝对差 | 性质 |
|---|---|---|---|
| **Integrate** | **1.50×** | +1.36 ms | 内核发射面（96 形参 → 栈流量）；**唯一的大项** |
| prefixFinal | 1.60× | +0.08 ms | 绝对值小 |
| count | 1.20× | +0.17 ms | 原子 + `index<Length` 守卫 |
| Build 段 | 1.21× | +0.61 ms | 上面几项之和 |
| zero | 1.11× | +0.011 ms | 冷 cache memset |
| place / pp / Melee / Flow / MarkDead | 0.67–1.12× | — | **打平或反超** |

### 27.5 §26 改动后的 8-worker 复核（诚实标注）

§26（IJobFor index adapter + ctx 改池化）之后，8-worker 的 A 侧独立复测（**`-NoB`，无配对**）：

| 档 | §26 之前（配对基线） | §26 之后（A-only） | 差 |
|---|---|---|---|
| 对齐档 | 161.30 | **163.19**（162.26/163.19/171.09） | +1.2% |
| 默认档 | 153.81 | **159.91**（159.59/167.05/159.91） | +4.0% |

**读法（不夸大）**：
- 这是**无配对**复测，跨会话漂移带是 ±5%（本项目多次记录）⇒ **不能据此判定回归**，也不能据此判定收益；
- §26 两处改动对**宿主这条路径**的影响是：① `IJobFor` index adapter —— 宿主**没有 IJobFor job**（§25.1 已核），**零影响**；
  ② ctx 由 `Marshal.AllocHGlobal/FreeHGlobal` 改为框架 `ContextPool` —— **严格更少的每派发工作**
  （去掉一对 malloc/free；池本就是托管路径在用的同一套），且 soak 显示 20 万次 **0 KB 增长**。
- ⇒ 若要坐实，需要一次**同会话配对**（`ENTJOY_*` 单开关切回 HGlobal 路径做对照臂）——列为遗留项。

---

## 28. 注册机制（自动 / 手动）+ AOT + 冗余防御清理（2026-10-02）

### 28.1 两种注册机制（都已实测）

| 方式 | 触发点 | 实现 | 实测证据 |
|---|---|---|---|
| **自动** | **程序集加载** | 生成代码里的 `[ModuleInitializer] public static void EnsureNativeJobRegistrations()` | 探针**不调用任何东西**（`INFO,for_probe_init_mode=auto`）时仍出现 `first native direct-dispatch: BenchForProbeJob` ⇒ 自动路径确实生效 |
| **手动** | 调用方显式调用 | 同一个 `EnsureNativeJobRegistrations()`（**public**、幂等） | `ENTJOY_PROBE_MANUAL_INIT=1` 走显式调用 ⇒ 同样出现 first-use（PASS） |
| 回退 | — | `ENTJOY_NATIVE_SINGLE_JOB=0` | 只有 `wired`、**无** first-use ⇒ 回退正确 |

**⚠ 本轮修正的一个隐患**：上一版发射的方法体是 `_ = typeof(NativeExports);`，注释说"触碰本类即可触发静态构造"——
**`typeof(X)` 并不保证触发 cctor**（它是 `ldtoken`，不访问静态成员）。真正让它生效的是
"**调用该方法**本身"（`NativeExports` 有显式静态构造 ⇒ 非 beforefieldinit ⇒ 调用其静态方法前必然先跑 cctor）。
现在把方法体写成**有意留空**并把这条写进注释，不再用一个"看起来像触发、其实不是"的表达式表达意图。

**探针改法（方法层面教训延续 §25.3）**：旧探针用 `RunClassConstructor` 替框架把注册做了 ⇒ 会让"自动"结论失真。
现在默认**什么都不做**（验证自动），`=1` 时走公开的手动入口（验证手动入口本身可用）。

### 28.2 AOT / IL2CPP 友好性（为什么这套设计成立）

| 关注点 | 我们的做法 |
|---|---|
| 反射 | **无**。注册走 `typeof(T)` 静态泛型缓存 + 字典查表（`TryGetNativeJobAdapter`/`TryGetJobFieldWriter`），不用 `MakeGenericType` / `Activator` / `Assembly.GetTypes` |
| 泛型实例化 | 只在**已编译**的泛型形参上查表，不动态实例化 |
| delegate 生根 | 交给原生的函数指针来自 `static readonly` 字段（`Marshal.GetFunctionPointerForDelegate` 的经典坑 = 委托被 GC ⇒ 这里被静态根引用） |
| `[ModuleInitializer]` | **可选增强**：转译器先 `GetTypeByMetadataName` 探测，目标框架没有该类型就不发射；**手动入口始终存在** |
| 裁剪 / trimming | 手动入口给宿主一个确定性调用点；README/指南已写明 |
| 堆分配 | ctx 用框架自带 `ContextPool`（复用 `CleanupPtr` 释放）⇒ **无逐派发 `AllocHGlobal/FreeHGlobal`**；20 万次 soak **0 KB** |

> 与项目既有约定一致：`EntJoy.ECS` 的 `ComponentMetaSourceGenerator` 早就用 `[ModuleInitializer]` 自动注册
> 并标注"AOT 安全无反射"——本节只是把这套约定对齐到 job 注册上。

### 28.3 删除的冗余防御（可读性/简洁性/可维护性）

| 删除项 | 理由 |
|---|---|
| `NativeJobCore.HGlobalCleanupPtr` / `_hglobalCleanup` / `HGlobalCleanup()` | ctx 改池化后**完全不再使用**（死代码）；其存在只为 HGlobal 路径 |
| `NativeJobScheduler.TryGetNativeJobAdapter(Type, IntPtr)`（只 `out _` 的那个重载） | 无调用者 |
| `Register*Adapter` 里的 `type == null` 判断 | `typeof(T)` **不可能**为 null |
| `Register*Adapter` 里的 `ctxSize <= 0` 判断 | 与 `TryRentMarshalledContext` 的判据**重复**；收敛到**唯一一处**（无字段的 job 生成器本来就不注册） |
| `TryRentMarshalledContext` 的 `adapterPtr == IntPtr.Zero` / `ctx == IntPtr.Zero` 判断 | 前者的指针已由调用方查表得到（getter 失败会抛而非返回 0）；后者由 `ContextPool.Rent` 契约保证 |
| `TryRentMarshalledContext` 的 try/catch | 字段写入器是生成代码的**纯指针写**、不会抛；吞异常会**掩盖 bug**。改为**冒泡（fail-fast）**，并把"不做静默回退"写进注释 |
| `TryScheduleFor/BatchWithNativeAdapter` 的 `length <= 0` 判断 | 调用方（`ScheduleFor` / `ScheduleParallelFor*`）**已经**提前 `return default` |

净效果：三个 `TrySchedule*WithNativeAdapter` 现在**同构**（`TryRentMarshalledContext` → 原生调度 → 无效句柄则回池返回 false），
不可用判据只剩**一处**；文件更短、每条判断都能说出"谁会违反它"。

### 28.4 三态验证（`tools/BuildPassBench`）

| 态 | 覆盖检查（`ScheduleFor` n=1000） | 泄漏 soak（20 万次） | first-use 自证 |
|---|---|---|---|
| auto（默认，不调用任何东西） | PASS `each_index_exactly_once=1` | PASS `delta_kb=0` | **有** ⇒ 自动注册生效 |
| manual（`ENTJOY_PROBE_MANUAL_INIT=1`） | PASS | PASS `delta_kb=0` | **有** ⇒ 手动入口可用 |
| managed（`ENTJOY_NATIVE_SINGLE_JOB=0`） | PASS | PASS `delta_kb=0` | **无** ⇒ 回退正确 |

### 28.5 用户文档更新

- [`docs/public/Native-Jobs-Guide.md`](../public/Native-Jobs-Guide.md) 新增 **§3.6 原生直调与"注册时机"（自动 / 手动）——AOT 友好**，
  并在 §4 排错速查加了"标了 `[NativeTranspile]` 但性能像托管"的诊断行（判据 = 看有没有 `first native direct-dispatch`）。
- [`src/EntJoy.Jobs/README.md`](../../src/EntJoy.Jobs/README.md) 中英双语各加一节（同样的三行表格 + 自证横幅 + AOT 设计要点）。

---

## 29. `zero`："同是原生 C++ 为什么慢"——**否证 store 策略**，落到"窗口结构 + 内存状态"（2026-10-02，回应"你还没分析到重点"）

### 29.1 ⚠ 先作废我自己的第一版器械（方法层面的教训）

第一版 `tools/HotSpotMicro/zero_cost.cpp` 的结论**无效**：那个 EXE **没有符号表**，而 clang 把每个 arm 都**内联**了 ——
我测的**根本不是** A 的真实路径（`jmp → IAT → VCRUNTIME140!memset → rep stosb`）。
**重写版** `tools/HotSpotMicro/zero_codegen.cpp`：全部 arm 用 `extern "C"` + `__declspec(noinline)`（符号可见、可反汇编核对），
`memset` 走 **volatile 函数指针**（不可内联），并核对导入表确有 `VCRUNTIME140.dll!memset`。

> **教训（写进纪律）**：微基准的每个 arm 必须能被反汇编证明"它真的是它"。凡"结论依赖某个 arm 的 codegen"，
> 先 `llvm-objdump` 核对，再读数。无符号表的 EXE 上做 codegen 结论 = 自欺。

### 29.2 实测（1,404,932 B = 351,233 int = A 的 `_counts` 精确尺寸，60 rep，中位/最小 µs）

| 策略 | 暖（背靠背） | **冷（128 MB 冲刷）** | 冷态 GB/s |
|---|---|---|---|
| **CRT `memset`（A 的真实路径，1.4 MB 走 `rep stosb`）** | 19.90 / 16.00 | **97.60 / 72.50** | 14.39 |
| `rep stosb`（ERMS）显式 | 18.60 / 15.80 | 98.60 / 85.40 | 14.25 |
| `rep stosd`（ERMS） | 17.20 / 15.60 | 100.30 / 81.50 | 14.01 |
| 标量循环 | 17.10 / 15.70 | 101.00 / 84.90 | 13.91 |
| **AVX2 4×32B（B 的精确形状）** | 23.80 / 19.30 | **100.90 / 84.90** | 13.92 |
| AVX2 8×32B（CRT 中段形状） | 18.50 / 16.40 | 96.50 / 80.50 | 14.56 |
| **非临时（NT）** | 47.50 / 42.30 | **47.40 / 36.60** | **29.64** |

**冷态"清零 + 跟随读一遍"配对**：memset 141.0、rep stosb 133.2、标量 135.9、AVX2 4×32B 133.6、AVX2 8×32B 147.9、NT 143.6 µs。

### 29.3 判据与结论

1. **除 NT 外，全部策略落在 96.5–101 µs 的 4% 带内** ⇒ **store 策略不是原因**。
2. **A 的真实路径（CRT memset/`rep stosb`）比 B 的精确形状（AVX2 4×32B）快 3%** ⇒
   **⛔ 撤回** §27/上一轮"B 的存储策略更好"的推断（该推断建立在 29.1 那个无效器械上）。
3. NT 能把冷态砍到 47.4 µs（2.1×），但**配对测量里后继读没退化**（143.6 vs 141.0，噪声内）；
   它仍是**宿主内核的语义选择**，框架侧没有任何 NT/memzero 助手（`src/` 全树 grep `MemZero|NonTemporal|_mmstream|movnt` = 0 命中）。

### 29.4 那 A 的 +19.4 µs 在哪（A 117 vs 冷 memset 97.6）

同一窗口里除 memset 外只有 `_bpSw.Restart() → Schedule(ref zero) → Complete() → BpStop(0)`：

| 组成 | A | B |
|---|---|---|
| 冷态 1.4 MB 清零（A 的真实代码路径；同机同工具链） | 97.6 µs | ~90–93（缓冲略暖） |
| **窗口内的游戏内单发派发+计时** | **≈7 µs**（项目自己在游戏内测的"单发地板 6–8 µs"，07 §7(l6)/(l7)） | <1 µs（IL2CPP Release） |
| 游戏内存状态 vs 我"128 MB 顺序冲刷"的差 | **≈12 µs（未闭合）** | — |
| 实测（§24.4/§27 干净会话） | **117 µs** | **93 µs** |

- 顺带否掉一个我自己的旧假设：**诊断仪器不背这个锅** —— `CPUBATTLE_DIAG_BUILDPASS=0` 与 `=1` 的 Build 段基本一致
  （3.41 vs 3.41/3.43/3.88，跨会话漂移内；探针 `tools/gate-run/q5diagoff/`）。
- **⇒ `zero` 封存**：内核不是瓶颈，唯一杠杆（NT）不在框架侧，stake **0.024 ms/步（整步 1.5%）**。

### 29.5 器械

`tools/HotSpotMicro/zero_codegen.cpp`（+ CMake target `ZeroCodegen`；`ZeroCodegen.exe [thrashMB]`）；
第一版 `zero_cost.cpp` 保留但**其结论作废**（文件头已标注）。夹具侧新增 `BENCH_SHAPES=zeroprobe`（`BENCH_ZERO_LEN` 可扫长度）
用于把"框架地板"与"memset 本身"分开。

---

## 30. 别名 `__restrict`：**能解、但不是瓶颈；通解只能划在 ABI 边界**（2026-10-02）

### 30.1 A vs B 环内机器码（同一数法，每元素 1 次迭代、都未展开、都 128-bit）

| 指标（每元素） | A（clang-cl） | B（Burst AVX2） |
|---|---|---|
| 循环体指令数 | 413 | 397–398 |
| **带内存操作数的指令** | **177** | **126** |
| 其中栈帧（rsp/rbp）访问 | **75**（58 = **重载传入实参槽**，34 个不同槽位；其余真溢出） | **39** ← 由我实测（子代理初版报的"0"是错的） |
| 提升进寄存器的数组基址 | 3 / 36 | 字段按 `off(%rdi)` 直接寻址，一次 `callq`/range |

**两者总指令数几乎相同**（413 vs 397）⇒ **不是"指令多"，是"每元素有效访存多 40%"**。

### 30.2 三层结论

1. **标量字段指针的 `__restrict`：可以加，本来就是 sound 的**（它们指向框架自有 ctx 缓冲，与任何 `NativeArray` 数据不可能同址）。
   但当前判据是 `ScalarRestrictEnabled = loopUse.InLoop.Contains(field)`，而 `InLoop` 来自 **C# 源码里的循环** ——
   `IJobParallelFor`/`Batch`/`For` 的元素循环是**转译器合成**的 ⇒ `InLoop` 恒空 ⇒ **最热的那批 job 一个 restrict 都没拿到**。
   **这是真 bug；但实测上限只有 2–4%**（夹具 `hoist` 消融，08 §12）。
2. **分量/数组指针的 `__restrict`：默认不 sound**（两个字段可指向同一 buffer；08 §30 已记 NativeList 护栏）。
   要做 sound 只能是"**checked promise**"：`[NoAlias]` 显式声明 + 派发时校验所有被封送指针区间两两不相交（按指针集合哈希缓存）
   + 违规回退托管路径。**但已实测收益为负**（全网加：净 −2.1% 指令、某内核 +56.5%），且**带 restrict 时环内重载依然存在**。
3. **真因是寄存器压力**：20 个数组基址 + 16 个标量指针 = **36 个需同时存活的值**，而 x64 只有 ~13 个可用 GPR ⇒
   放不下是**算术事实**。编译器选择"用到就从调用者帧重取"。

**决定性证据（随字段数单调；同框架/同转译器/同编译器/同认领几何）**：

| 内核 | 字段 | 实参 | 环内指令 | 带访存指令/元素 | 栈访问/元素 |
|---|---|---|---|---|---|
| `CountCellsJob` | 13 | 17 | 43 | **15** | **2** |
| `IntegrateJob` | 38 | 58 | 413 | **177** | **75** |
| `MeleeSimJob` | ~90 | 96 | 3224 | ~1120 | **530**※ |

（※ MeleeSim 后两列取自子代理报告；其函数体指令数我独立复核为 4569 vs 其 4554。）

### 30.3 ⛔ 撤回：删 20 个死参**不会**有收益

生成的 `_Execute_Batch` 里 191/600 = 32% 形参是"声明了但内核体从不读"的死参（Integrate 58→20 死）。
但反汇编核对：**clang 已把它们 DCE 掉**（54 个栈槽只写了 35 个，`PositionIn_length` 连寄存器都没设）⇒
**只影响符号/调试面，不影响热路径**。上一轮"删死参能省 0.2 ms"的推断**作废**。

### 30.4 通解边界（回应"改成一个结构体指针，通解，适合任何情况"）

| 位置 | 形态 | 判据 |
|---|---|---|
| **跨 ABI 边界（不可内联）的入口**：job 入口、导出函数、跨 DLL/跨语言 | ✅ **单一指针**（ctx / params 结构体） | 省掉"摆 N 个实参 + 保 callee-saved"的税 |
| **同一 TU 内可内联的助手 / 设备函数** | ❌ **不要**改成结构体指针 | 调用点要先物化结构体（N store + lea），而被内联时编译器本来就能把这些值留在寄存器里；若该助手在**每元素循环**里，等于把"一次寄存器传参"变成"每元素一次结构体物化" |
| 例外：不可内联且形参 > ~8 的助手 | 可合成 params 结构体，但**物化必须提到调用方循环之外** | 需要"调用点是否在循环里"的判据（`FieldLoopUse` 已有同类语法分析基础，可复用） |

---

## 31. `Integrate` 的两个杠杆（实测）+ **空体内核控制实验证明"框架仪式不背锅"**

### 31.1 两个独立轴都塌缩到"**每次内核调用的元素数**"

**轴一：改声明内批（claim cap 固定 4 tile）** —— `integ` ms（A-only，同会话交替，2 rep 均值）

| 声明内批 | 32 | 64 | 128 | 256 | 512 | 1024 | 2048 |
|---|---|---|---|---|---|---|---|
| 每次调用元素数 = 4×cs | 128 | 256 | 512 | **1024** | 2048 | 4096 | 8192 |
| ms | 4.25 | 4.02 | 3.83 | **2.99** | 3.09 | 3.18 | 3.13 |

**轴二：只改认领跨度（声明恒 `:64`，改批表第 3 字段 = 每次认领几个 tile）** —— 同一会话

| cap（tile） | 4（内置默认） | 8 | 16 | 32 |
|---|---|---|---|---|
| 每次调用元素数 | 256 | 512 | **1024** | 2048 |
| ms | **4.91** | 3.98 | **3.41** | 3.25 |

⇒ **两轴塌缩到同一自变量；最优 ≈1024–2048 元素/调用；`cs=64` + 默认 cap=4 只有 256 元素/调用 ⇒ 白付 +1.0…1.5 ms。**
代码挂点：[`ChaseLevScheduler.cpp:696-709`](../../src/NativeDll/ChaseLevScheduler.cpp) 的 `itemsPerTile <= kClaimSpanThinElems(16)`
把"按元素的认领跨度"锁在薄 tile 上；厚 tile 退回 `capEff = claimCap`（=4 个 tile）。F5（tile-run 合并，[`JobSystem_Scheduler.cpp:949`](../../src/NativeDll/JobSystem_Scheduler.cpp)）
使**每次认领 = 一次内核调用**。

### 31.2 ⭐ 空体内核控制实验：cs=64 下我们的框架仪式**与 Unity 平手**

同一框架、同一 cs、**零内核体**（`BENCH_SHAPES=emptyjob` vs Unity 新臂 `batch_sweep`），1e6 元素，中位/最小 µs：

| 声明内批 | EntJoy | Unity（干净重测） | EntJoy 每工作项 | Unity 每工作项 |
|---|---|---|---|---|
| cs=1（薄 tile，F1/F5 合并） | **20.0 / 17.6** | 603–621 / 505–514 | **0.02 ns** | 0.60 ns |
| **cs=64** | 101.3 / 70.1 | 102–109 / 81–84 | **6.5 ns** | **7.0 ns** |
| cs=1024 | **9.4 / 8.0** | 62.5–74.9 / 54.8–56.7 | 9.6 ns | **64 ns** |

⇒ **cs=64 时框架仪式 6.5 vs 7.0 ns/工作项 = 平手**（15,625 项 ⇒ 各 ~0.10 ms）。
**所以 Integrate 在对齐档的 1.8 ms 赤字不在框架仪式里**，也不在内存/store 策略里（§29）——
只剩**内核调用层**（adapter + 58 实参 + XMM 保存恢复）与**认领跨度决定的"调用/run 次数"**。

### 31.3 每次调用的机器码代价（反汇编）

| 项 | 数值 |
|---|---|
| `_Adapter` 调用前指令数 | **127**（8 push + `sub rsp,0x288` = **648 B 帧** + 43 load + 58 store；23 个值经第二暂存区中转） |
| adapter 访存次数 | **101** |
| 内核 prologue / epilogue | 40 / 20 条，含 **20×16 B `vmovaps` 保存+恢复**（320 B 栈流量） |
| 内核帧 | 8 push + `sub rsp,0xd8` = 280 B |
| 对照：B | 无封送层；字段 `off(%rdi)`；每 range 一次 `callq`；帧 424 B（**比我们更大**） |

### 31.4 未闭合项（诚实标注）

cap 4→16 少了 2,929 次调用却省 1.5 ms ⇒ **≈512 ns/次**，比 asm 能解释的 ~114 ns 大 ~4.5×。
**机制类别已确定（每次调用），倍率未闭合** —— **不再往"框架仪式"归因**（§31.2 已否）。
闭合手段：换成单入口后重测，或给每次内核调用加计数器。

---

## 32. 删 `_Adapter` / 内核单入口化：改动清单与预期（**未实施，设计已定**）

### 32.1 现状结构

| 导出 | 签名 | 谁调 |
|---|---|---|
| `X_Execute_Batch` | `(int start, int count, 20 数组指针 + 16 标量指针 + 20 个 `_length` 死参) = 58 实参` | 适配器（每工作 run）、`Run()` 便捷 API |
| `X_Execute_Adapter` | `(void* context, int start, int count)` | **框架**（每工作 run 的函数指针） |

生成点：[`CppJobGenerator.cs:1210-1271`](../../src/NativeTranspiler/Analyzer/Cpp/CppJobGenerator.cs)（扁平行参表）、
[:2149-2204](../../src/NativeTranspiler/Analyzer/Cpp/CppJobGenerator.cs)（adapter）、
[`BindingsGenerator.cs:1315`](../../src/NativeTranspiler/Analyzer/Common/BindingsGenerator.cs)（`Run()` 的 P/Invoke）。
**讽刺点**：`Count`/`Zero` 的 adapter 里 **0 个 `callq`**（clang 把 84 条内核内联进去了），只有 `IntegrateJob`(485 条)/`MeleeSimJob` 是**两层**
⇒ "两层税"是**大小相关的 codegen 结果**，不是设计。

### 32.2 改动清单

1. `CppJobGenerator`：内核签名 → `(const void* context, int __startIndex, int __count)`；函数体开头插入**同一份** `BuildAdapterFieldAccess`
   的字段解包（局部变量名与今天的形参名**逐字相同** ⇒ 内核体一行都不用改）。
2. adapter → 1 行转发（或删除、`Get_..._AdapterPtr()` 指回内核）。
3. `BindingsGenerator`：`Run()` 与 MT/`_true/_false` 变体走薄包装或走 ctx；`Schedule_X` 扩展本身不必动。
4. 基线：所有 emit 快照变文本、44 个基线重发；`AddAdapterFieldAccess` 只需改一处（§26 抽离的收益）。

### 32.3 预期（用 §31.3 的数字推）

| 档 | 调用次数 | 预期收益 |
|---|---|---|
| 对齐档 cs=64 | 15,625 | **0.2–0.35 ms** |
| 产品档（≈1953 元素/tile，~512 tiles） | ~512 | ~0.01 ms |

**两个"但是"**：① **不保证**环内 58 处实参槽重载下降（那是压力）；② 同类思路的标量版 `__scalars` 打包实测**中性**。
⇒ **先夹具（`NativeTranspilerFixture` 的 `BenchParams96Job`）量"环内重载是否下降"，再决定铺开。**

---

## 33. 派发地板：EntJoy vs Unity **同形状同窗口实测**（Unity 侧新臂，2026-10-02）

### 33.1 Unity 侧器械（新增，不改任何既有测量）

`Assets/Scripts/BattleBench/BattleBenchDispatchFloor.cs`（新文件；门控 `M4_DISP=1`，独立入口、跑完自退，正常战斗不跑）；
CSV 组 `M4DISP`；三个 `[BurstCompile(CompileSynchronously=true)]` 空 job；**工作证明** = 每 job 写标记位，
`proof_fail_reps=0`、遍历戳计数 == 声明项数（逐配置逐 rep）。IL2CPP 重编 `W0BuildTool.BuildWindowsIl2Cpp`：
`result=Succeeded platform=StandaloneWindows64 errors=0`（含污染时 337.6 s）。
原始档 `tools/gate-run/q5unitydisp/`（`REPORT.md`、`disp-{w8-q1,w8-q2,w1-q1}.csv` = 干净重测、
`disp-{w8-r1..r3,w1-r1..r2}.csv` = 污染集、`load-*.txt` 负载快照、`build.log`、`run-disp.ps1`）。

### 33.2 干净重测（同窗口先杀全部残留 Godot；8 worker，µs，中位/最小）

| 形状 | EntJoy（.NET8 夹具） | Unity（IL2CPP 播放器） | 判定 |
|---|---|---|---|
| **空串行 `IJob` 往返** | **1.08 / 0.99** | 2.90–5.55 / 0.70–0.80 | ✅ **A 中位快 2.7–5.1×**，尾巴更稳（Unity max 曾 43.9 µs）；**地板值 Unity 略低** |
| 空并行 job ×100（各 1 元素） | 0.87 / 0.72（每 job） | **0.66 / 0.41–0.45** | A 慢 1.3×（中位）/ 1.6–1.8×（最小） |
| **空并行 job ×1000** | 0.84 / 0.65（每 job） | **0.32 / 0.29** | ❌ **A 慢 2.6×**（唯一明确落后的派发轴） |
| 同上（1 worker） | 0.55–0.83 | 0.33–0.37 | 方向不变 |
| 单个空并行 job（1 元素） | — | 6.2–7.85 / 2.8–5.1 | Unity 的"第一个 job"远贵于边际 |

### 33.3 读法

1. **单发一个 job：追上了，中位更快**（1.08 vs 2.90–5.55 µs）；**只有地板值（min）Unity 略优**。
2. **大量小 job 连发：慢 2.6×**（每 job 多 ~0.52 µs）。机制：每次 `Schedule` 仍有 **ctx 租用+写字段 / batch storage 获取 /
   state 创建 / 退役**；且我们 1 worker（0.55–0.83）好于 8 worker（0.84–0.87）⇒ **池/状态创建的跨核争用**。
   折算本工程 ~400 次派发/步 ⇒ **≈0.21 ms/步（0.13%）**，与旧结论（≤0.4 ms/步）一致 ⇒ **不建议为它重构**。
3. **粒度轴两极我们大胜**：cs=1 快 **30×**（F1/F5 合并，Unity 按工作项付费）、cs=1024 快 **6.7–7.9×**
   （Unity 的每 range 成本 64 ns vs 我们 9.6 ns）；**中段 cs=64 平手**。

### 33.4 ⛔ 撤回：污染版说"cs=64 我们慢 1.4–1.5×"

污染集（我的 `dotnet build` + 夹具基准同时跑，见 §34.1）读作 EntJoy 143.0 vs Unity 96–103 µs；
**干净重测**为 EntJoy 101.3 vs Unity 102–109 µs ⇒ **平手**。上一条"cs=64 慢 1.4×"**作废**。
（相对地，`每声明批 0.54 ns`、`连发 2.6×`、`cs=1 快 30×` 在污染/干净两版里同向、稳定。）

### 33.5 地板分解（夹具 `BENCH_SHAPES=zeroprobe`，`Length=0`，200 步，中位/最小 µs）

| 量 | 8 worker | 1 worker |
|---|---|---|
| 计时器本身（`Restart/Stop`） | **0.00** | 0.00 |
| `Schedule()` 提交（Complete 在计时外） | **0.60 / 0.40** | 0.50 / 0.30 |
| 已完成后**再次** `Complete()`（retain 句柄） | **0.00** | 0.00 |
| 静态 API `JobScheduler.Schedule(ref j)` + Complete | 1.20 / 0.80 | 1.20 / 0.90 |
| 生成的扩展 `j.Schedule()` + Complete（游戏走这条） | **1.10 / 0.90** | **0.80 / 0.70** |

⇒ **与 worker 数几乎无关（1w 0.80 vs 8w 1.10）⇒ 这条路径上没有"唤醒 worker"**（单工作项在提交线程跑完），
**1.1 µs = 提交段 ~0.5 µs + 等待段 ~0.4 µs 的纯簿记**（ctx 池租 + 逐字段写、batch storage 获取、state 创建、原生提交、退役）。
retain 句柄的二次 `Complete` = **0**。

---

## 34. ⚠ 测量纪律（本轮新增两条，**先于任何新测量执行**）

### 34.1 并发污染（我自己踩的）

Unity 侧首轮（`disp-w8-r1..r3`）运行时，我同时在 `dotnet build` 夹具并跑微基准。子代理的负载快照点名：
`VBCSCompiler 244%`（我的构建）、`BCUT.exe 150–243%`、`Verifier.exe 150–220%`、`msedge ×4 150–200%`、
一个仍在烧 **316%** 的 Godot、`powershell 180–200%` —— 全程 **~1074–1462% of one core（16 逻辑核里 11–15 忙）**。
**后果**：中位/最大被抬高、run 间漂移；**最小值最可信**（§33.4 就是靠它纠正了一条结论）。

> **纪律**：① 跑计时期间**不得**有本会话的编译/基准并发；② 每轮前后打**负载快照**并写进证据目录；
> ③ 主判据优先用 **min**，中位只作参考；④ 跨会话绝对值一律不可比（本项目旧约定）。

### 34.2 🐞 Godot 进程泄漏（器械 bug，影响面已确认）

清理时发现 **30+ 个仍存活的 `Godot_v4.7-stable_mono_win64`**。根因：`tools/gate-run/ab-aligned.ps1` 的 `RunA`
**只在每次启动前** `Stop-Process Godot*`，且 `$p.Kill()` 只杀启动器 ⇒ **每批参数的最后一个 A 运行必然留下一个**。
多数残留空转（CPU≈0），但至少一个仍在烧 316%。

- **影响面**：所有基于该 harness 的**绝对值**都可能被抬高（对同会话 A/B 的**比值**伤害较小）；
  本次已实测到一条被它/我被放大的结论（§33.4）。
- **修法（待做）**：`RunA` 在**每次 rep 结束后**也 kill；并在 `[done]` 前做一次收尾清理 + 打印残留计数。

---

## 35. 下一步探索方向（按"收益 ÷ 风险/成本"排，2026-10-02）

**当前账面**：对齐档整步 **1.010×**（+1.59 ms，其中 `Integrate` 一项 +1.83）、默认档 **0.937×（已快 6.3%）**。
⇒ **整个对齐档的剩余工作实际上是"一个内核 × 一个轴"**。

| 序 | 方向 | 依据（本轮实测） | 预期 | 风险/前置 |
|---|---|---|---|---|
| **D0** | **修测量协议**：每次 rep 后 kill Godot、计时期禁止并发、每轮负载快照 + 主判据用 min | §34.1/§34.2（已实测两条被放大的结论） | 0 收益，但**决定后续测量是否可信** | 无（改 `ab-aligned.ps1`） |
| **D1** | **认领跨度按"元素"定**：把 F1 的规则从 `cs ≤ 16` 门里放出来（GeneralRange 一律 `capEff = clamp(SPAN/cs, 1, SPAN)`，SPAN≈1024–2048） | §31.1（同会话 **4.91 → 3.41，−31%**；声明仍是 `:64`） | **−1.0…1.5 ms**（对齐档） | 整步验收 **Melee/Flow 不退化**（F1 当年收窄正是为 Melee 的 index 邻近性） |
| **D2** | **内核单入口化**（ABI 边界单一指针） | §31.3（127 指令 + 101 访存 + 648 B 帧 + 20×16 B XMM / 次调用）；§31.2 已排除"框架仪式" | **0.2–0.35 ms**（对齐档）/ ~0.01（产品档） | ABI 大改（44 基线重发）；**先夹具量环内重载是否下降** |
| **D3** | **环内每元素访存 177 → 126**（唯一与几何/窗口无关的差：平台期 1.18–1.20×） | §30.1/§30.2（36 活指针 vs ~13 GPR；`Count` 2 次栈访问是控制组） | 上限 **0.2–0.5 ms** | 历史两次中性/否证；先 `CodegenAsmProbe` 验 asm 再谈墙钟 |
| **D4** | **每-job 派发成本**（连发慢 2.6×） | §33.2/§33.3（0.84 vs 0.32 µs/job；1w 优于 8w ⇒ 争用） | **≈0.21 ms/步（0.13%）** | 收益小但**通用**（ECS 侧同样吃）；低优先 |
| **D5** | 未闭合两项：`zero` 的 ~12 µs 内存状态差；`Integrate` 每次调用 512 ns vs asm 114 ns 的 4.5× 缺口 | §29.4 / §31.4 | 记账 | 闭合需插桩 B 或给内核调用加计数器 |
| **D6** | 结构性：让 Melee 也走 chunk 模型 ⇒ 统一几何；把"调用点声明几何"与 D1 合并成一条通用契约 | §0 + §31.1 | 未知 | 大改；z 顺序在 D1 之后 |
| ❌ | `zero` 改 NT / 内联 AVX2 | §29.3（A 已比 B 的形状快 3%） | ≤0.024 ms | 不做 |
| ❌ | 删 20 个死参 | §30.3（已被 DCE） | 0 | 撤回 |
| ❌ | 调度地板 | §33.5（纯簿记、与 worker 数无关） | ≤0.25% | 不做 |

**策略层的一点判断**：原目标"追到 Unity 的 0.86 标准"在**两档都已达成**（对齐 1.010×、默认 0.937×）。
剩余对齐档 1% 里 **`Integrate` 占 115%**，且它的两个杠杆（D1/D2）都在**我们自己的框架/转译器**里、
不动宿主也不破坏"对齐"语义。所以下一步不是"再找性能点"，而是：
**先 D0 把尺子修准 → 再做 D1（已实测）→ 用夹具判 D2/D3 是否值得 → D4/D5/D6 记账。**

---

## 36. D0 落地：六条协议缺陷，一条否证（2026-10-02 第二段）

本节全部是**对仪器本身的实测**，不是对框架的实测。做这件事的理由很直接：§35 的 D1 预期值（−1.0…1.5 ms）
来自 §31.1 的一次同会话读数，而那条读数事后被证明建立在一个有缺陷的尺子上。修完之后，D1 的真实量级
只有原来的 **1/8**，而 §34 里两条"污染结论"的方向也说反了。

### 36.1 否证：「Godot 进程泄漏偷核」（撤回 §34.2 的机理）

残留的 **28 个** `Godot_v4.7-stable_mono_win64` 全部是**尸体**，不是活进程：

| 判据 | 读数 |
|---|---|
| `Threads.Count` / `HandleCount` | **0 / 0**（28 个全部） |
| 合计 `WorkingSet64` | **0.9 MB** |
| `Win32_Process.CommandLine` | **空**（活进程不会空） |
| `GetOwner` | **rc=2**（access denied） |
| `Stop-Process -Force` / `taskkill /F` | 均 `Access is denied`（已终止者无可终止） |

⇒ 它们**不耗 CPU**，也不可能"各自保留 worker 池"。§34.2 的机理判断作废。
`RunA` 仍然按名 reap（句柄/内存卫生 + 让计数可读），但 harness 现在**区分 live 与 corpse**并把两者都打进日志，
避免把尸体当泄漏、也避免把真泄漏当尸体。§34.2 里"至少一个仍在烧 316%"那句：那个 316% 的观察对象其实是
**本轮 harness 自己**（见 36.2）。

### 36.2 负载仪器在测自己：`.CPU` 每次访问 ~10.5 ms

第一版 `Get-Load` 用"两次读 `$p.CPU`、除以 0.6 s 睡眠窗"算每进程核数。两个独立错误叠加：

| 项 | 实测 |
|---|---|
| `$p.CPU` 单次访问 | **10.5 ms/进程**（381 进程 ⇒ 单次枚举 ~4 s） |
| `$p.TotalProcessorTime` 单次访问 | **12.6 ms/进程**（更慢 ⇒ 不是属性解析问题，是每次都要开进程句柄） |
| ⇒ 单次快照（两遍枚举） | **~9 s 墙钟**，而分母只有 **0.6 s** ⇒ 每进程核数虚高 **≈14×** |
| ⇒ §34.1 的"13–40 核" | 实际 **1–3 核**（`Verifier/5.08` 真值 ≈0.36 核） |

（错误之二：`& script.ps1` 是**同进程**执行 ⇒ driver 就是 harness，快照把自己算进了负载，这也是
`powershell/<driver>` 长期位居榜首的原因。）

修法：① 总量改用**单次系统计数器**（`Win32_PerfFormattedData_PerfOS_Processor` 的 `_Total`，~0.3 s，
输出形如 `system=9% = 1.44 cores of 16`）；② 排除 `$PID`；③ 每进程明细降级为 `Get-LoadDeep`
（**逐进程时间窗**，只在每轮 `Prepare` 里跑一次），它才是当初**点名 VBCSCompiler** 的那个工具，保留。
**结论：这台机器其实是安静的**（0.6–5.1 核波动），§34.1 的"机器从不安静/11–15 核被占"读数作废。

### 36.3 诊断行污染返回值 ⇒ `count` 读成数组长度

`RunA` 内部的 `Write-Output` 诊断行与它的返回对象**走同一条输出流** ⇒ 调用方 `$a = RunA ...` 变成
`[诊断字符串, 对象]` 数组。成员枚举让除 `count` 外的每个属性都取到正确值，而 **`$a.count` 解析成数组自身的
`Count` = 2** ⇒ 第一轮 sweep 每一臂都报 `count=2.000`（真值 0.98–1.26）。
修法：新增 `Diag()`，走 console 流 + 自己的 `_diag.txt`，永不上管道。

### 36.4 `Prepare()` 从未被调用 ⇒ build-server 清理是死代码

写好了 reap build server 的函数，但主循环里没调用它。接上后第一次执行就清掉
**1 × VBCSCompiler + 9 × MSBuild + 6 × dotnet**（都是我刚跑完的构建留下的）。这是当初被误认为
"Godot 泄漏"的那个 4–6 核（**测出来是 0.36 核**，见 36.2）之外唯一真实的自家污染源，现在每臂前都会清。

### 36.5 否证：旧启动器是否只污染 A 侧

历史 A/B 里 A 走 `Start-Process -RedirectStandardOutput/-RedirectStandardError`，B 走无重定向的
`-Wait`。若重定向路径让 harness 在**只有 A 的样本里**烧 CPU（8 物理核），则**整个历史数据集都偏向 Unity**、
必须重标定。同产物同 env 交替实测（`tools/gate-run/d0-launcher-ab.ps1`）：

| round | 模式 | 自身 CPU（秒） | 自身核数 | wall | integ |
|---|---|---|---|---|---|
| 1 | old | 0.14 | 0.01 | 22.8 s | 3.95 |
| 1 | new | 0.02 | 0 | 22.5 s | 4.12 |
| 2 | old | 0.11 | 0 | 22.7 s | 4.17 |
| 2 | new | 0 | 0 | 22.6 s | 4.49 |
| 3 | old | 0.09 | 0 | 22.7 s | 3.91 |
| 3 | new | 0.02 | 0 | 22.5 s | 3.86 |

⇒ **否证**：两条路径自身 CPU 都 ≤0.14 CPU 秒 / 22.6 s（≤0.01 核），integ 中位 3.95 vs 4.12（无系统性方向）。
**历史 A/B 不需要重标定。** 启动器仍然换成 `cmd /c` + OS 级重定向：去掉管道写满阻塞的风险，并让
每次运行都打印 `self-cores=`，使这类缺陷无法再隐藏。`Diag`/CSV 里现在都有这个字段。

### 36.6 最大的一条：B 的窗口与 A 的窗口**在步号上不相交** ⇒ 造出假的 Melee 赤字

`lastStart` 的语义此前是错的。实测一条真实 stdout：M-1 窗口宽度 `[26,32,32,31]`、总和 121 ⇒
`lastStart = 121 - 31 = 90` 是**最后一个窗口的起始步**，即 A 报告的窗口是 **[90,121)**。
而 `RunB` 做的是 `warmEff = lastStart - 61` 且 `M4_STEPS = 40` ⇒ B 测 **[29,69)**：与 A **完全不相交**。

为什么这能造出赤字——**Melee 的代价是步号的函数**，其它段不是（`tools/gate-run/d0-phase-sweep.ps1`，
只扫 Unity 的 warmup，其余全固定）：

| warm（秒…步） | whole | build | flow | **melee** | markdead | integ | count | place | zero |
|---|---|---|---|---|---|---|---|---|---|
| 0 | 147.37 | 2.916 | 31.519 | **108.648** | 0.848 | 2.574 | 0.932 | 1.936 | 0.107 |
| 30 | 153.67 | 2.840 | 31.621 | **114.833** | 0.871 | 2.605 | 0.847 | 1.696 | 0.090 |
| 60 | 167.45 | 3.114 | 33.961 | **125.913** | 0.851 | 2.684 | 0.846 | 1.795 | 0.095 |
| 90 | 159.86 | 2.833 | 33.166 | **119.559** | 0.855 | 2.549 | 0.825 | 1.758 | 0.088 |
| 120 | 159.50 | 2.826 | 31.043 | **121.283** | 0.849 | 2.582 | 0.844 | 1.699 | 0.089 |
| 160 | 164.73 | 2.893 | 31.595 | **125.950** | 0.830 | 2.551 | 0.832 | 1.725 | 0.092 |

**只有 Melee 摆动 ±8%（108.6→126.0），其余段全平**。Melee 又占整步 ~76% ⇒ 拿 A 的"后期窗口"去比
B 的"早期窗口"，等于把一处**不存在的 Melee 赤字**做出来（§33 的 Melee +1.26、B/A 1.01 里那一项就是它）。

- **修法**：`M4_WARMUP = lastStart`、`M4_STEPS = A 自己的窗口宽度`（二者都从 A 的 stdout 解析）。
  `-WarmOffset 61` 保留旧行为，便于在同一会话里对照。
- **顺带验证**了 B 那六行 `M4,build,*` 的 scale 假设：raw/seg 实测 **1.732 @warm=30**、**3.263 @warm=90**，
  对上预测 `(w+40)/40 = 1.75 / 3.25` ⇒ 累计确实跨 `warmup+steps`，`scale = steps/(steps+warmup)` 成立。

### 36.7 A 是**秒级**限时 ⇒ 运行步数不可控（新增 `steps` 列）

`CPUBATTLE_AUTOEXIT` 与 `CPUBATTLE_STATS_WINDOW` 的单位都是**秒**（`BattleSimulation.cs:299`、
`CPUBattleEcs.cs:231-238`），所以 A 每次跑满 22 秒、覆盖"22 秒内能塞下的步数"，报告的又是**最后一个
5 秒窗口**的步均。实测同一轮 sweep 内运行步数从 **84 到 126**，且**与臂相关**（见 §37.3）。
⇒ harness 现在同时记录 A 的 `steps`（窗口宽度之和）与整步 `总计`（顺手让 A 也有了可比 B `whole` 的整步量）。
配对 A 采样前必须先看 `steps` 是否可比，否则整步差不可信。

---

## 37. D1 落地：认领跨度成为**调用点声明**（机制成立、量级 0.1%）

### 37.1 框架改动（4 文件，**已编译 + 已用 dump 验证**）

| 文件 | 改动 |
|---|---|
| `JobSystemInternal.h` | `JobBatchTableEntry` 增 `span`；`BatchState` 增 `claimSpanOverride`（元素）；`LookupJobSpan` 声明；`kClaimSpanDeclaredMax = 1<<20` |
| `JobSystem.cpp` | 表第三字段新增 **`e<N>` 形态**（元素跨度，与 `<claim>`(tile 数) 互斥）；`LookupJobSpan`；dump 增 `span=` |
| `JobSystem_Scheduler.cpp` | 读 `tableSpan` → `batch->claimSpanOverride` |
| `JobSystem_Tiles.cpp` | `AcquireBatchStorage` 里清零（池化复用 ⇒ 否则继承别的调用点的声明） |
| `ChaseLevScheduler.cpp` | 把"**几何合格**"（等宽 GeneralRange）与"是否走 F1 全局规则"拆开；**声明腿** `capEff = clamp(span/itemsPerTile, 1, tileCount)`，**与 tile 厚薄无关**、且可**缩小** cap（无隐藏下限）；未声明 ⇒ 走 F1 旧规则 ⇒ 逐位不变 |

**通解性**：单位是**元素**（`itemsPerTile` 变了语义不变）、键是**调用点**（不按 job 名特判）、不改内批镜像。
产品侧形态（C# `ClaimPolicy` 携带 span）尚未接——见 §39 的优先级说明。

**验证（两条独立证据，不靠墙钟反推）**：
1. dump 字段：base 臂 `key=00005e60 … span=0`，声明臂 `key=00005e60 … span=1024`；`key=00001950 … span=0`
   （未声明者不受影响）。⇒ 解析 → 表项 → 执行器读的那个字段，链路通。
2. 产物确实换过：部署的 `NativeDll.dll` sha256 `42E3144D…` → **`62BE3A83…`**（体积仍 1082880，巧合）；
   native 测试 **9/9 PASS × 2 组（TILE_RUN on/off）**，其中依赖改动头文件的 5 个用例都是**新编**的二进制。

### 37.2 度量：A-only 4 rep 旋转 + 逐 rep 配对（`d1span2`）

对齐档，内批固定 `:64`（= Unity innerloopBatchCount 镜像，工作分解不动），只改 Integrate 的认领上限
= 每次内核调用的元素数 `cap × 64`：

| 臂 | 元素/调用 | Integrate 配对中位 Δ | 符号 | Count 配对中位 Δ | 符号 |
|---|---|---|---|---|---|
| i16 | 1024 | **−0.165 ms（−5.3%）** | **4/4** | +0.036 | 0/4 |
| i32 | 2048 | −0.060 ms | 3/4 | +0.011 | 0/4 |
| ci16 | 1024（含 Count） | −0.115 ms | 3/4 | +0.071 | 1/4 |
| ci32 | 2048（含 Count） | −0.065 ms | 3/4 | +0.022 | 1/4 |

对照控制：**未被触碰的 pass 符号混乱**（melee 2/4、build 1/4、flow 1/4、place 2/4），说明配对统计量在起
作用；按 canary 归一后 i16 的 Integrate 收益（−5.3%）仍远大于同臂漂移（≈±0.5%）。
更早一轮 `cap=1`（= 严格镜像，64 元素/次）读数 **6.20 vs cap=4 的 4.63 ms（+34%）** ⇒ 该旋钮确实有效、
且**膝点在 cap≈4**。

### 37.3 整步：**没有效应**，且带 B 的验收轮被 A 的运行长度伪影污染

- **A-only 整步配对**（`d1span2`，i16 vs base，逐 rep）：**−3.56 / +4.08 / −0.99 / +0.45 ms**
  ⇒ 2/4、中位 **−0.27 ms**，落在 A 侧整步噪声（±2.5%）之内 ⇒ **无整步效应**。
- **带 B 的验收轮**（`d1accept2`，修正相位后）：i16 看似 3/3 快 1.8–3.8%，但同一 22 秒里
  **base 只跑了 84/91/118 步，i16 跑了 122/125/123 步**（§36.7）⇒ 该轮的整步对比**不可用**；
  它同时也是"配对前必须查 `steps`"这条纪律的现场教材。

### 37.4 判定与一条新推论

- **判定**：机制成立、可复现（Integrate 自身 −5.3%、4/4），但量级 **0.165 ms / 159 ms ≈ 0.1%**
  ⇒ **不足以作为对齐档的主杠杆**。保留为**能力**（`e<N>` 声明 + `claimSpanOverride`），**默认不改任何 job 的 cap**，
  也不去动 F1 的薄-tile 门（除非 §39 的默认档 cap 轴给出安全证据）。
- **新推论（下调 D2）**：把 Integrate 的调用数从 **3906 降到 977**（4×）只买到 **0.165 ms**
  ⇒ 边际**每次内核调用**代价 ≈ `0.165 ms / 2929 calls` ≈ **56 ns**，
  而不是 §31.4 从微基准反推的 ~400–512 ns。⇒ **D2（单入口 ABI）在 Integrate 上的天花板 ≈0.22 ms**
  （3906 × 56 ns），比 §35 的 0.2–0.35 ms 估计更靠下沿，且这条推论是**原位测得的**而非外推。

---

## 38. 重新基线：对齐档赤字 ≈1.6%，且 **Melee 不再是赤字项**

修正相位后，用 **base 臂**（= 出厂对齐表）的逐 rep 配对（`tools/gate-run/d1accept2/`）：

| rep | A 整步 | B whole | B/A |
|---|---|---|---|
| 1 | 163.11 | 159.15 | 0.976 |
| 2 | 162.89 | 161.70 | 0.993 |
| 3 | 160.17 | 157.66 | 0.984 |

⇒ 中位 **0.984**，即 **EntJoy 慢 ≈1.6%**（区间 0.7–2.4%）。历史记录的 **1.010**（慢 1.0%）落在同一带宽内
⇒ **跨会话比值本身有 ~3% 的不可复现带宽**，"1.0%" 级结论不能跨会话断言。
A 侧复现良好（158.02 / 160.99 / 157.84 vs 历史 158.96），差异主要来自 B 侧。

**修正相位后 A/B 的 Melee 互相对齐**（A 122.94 / 121.57 / 119.92 vs B 120.06 / 122.54 / 118.39）
⇒ **Melee 从"赤字主项（§33 记 +1.26，占整步 76%）"回到打平（B/A 0.988）**。
现赤字构成（base 臂中位比，B/A < 1 表示 Unity 更快）：

| 项 | A | B | B/A | 折合整步 |
|---|---|---|---|---|
| zero | 0.122 | 0.092 | 0.754 | +0.030 ms |
| count | 1.041 | 0.844 | 0.810 | +0.197 |
| prefixPartial | 0.066 | 0.046 | 0.703 | +0.020 |
| prefixFinal | 0.190 | 0.140 | 0.735 | +0.050 |
| place | 1.985 | 1.732 | 0.873 | +0.253 |
| **Build 段** | 3.660 | 2.796 | **0.764** | **+0.864** |
| flow | 32.330 | 31.933 | 0.988 | −0.397 |
| melee | 121.570 | 120.062 | 0.988 | −1.508 |
| **Integrate** | 3.330 | 2.578 | **0.774** | **+0.752** |

⇒ **对齐档剩余的 ~1.6% 大致均分给两块**：**① Build 段（五个原子密集短体内核，合计 ≈+0.86 ms）**、
**② Integrate（+0.75 ms）**；Flow/Melee 合计已经反超 −1.9 ms。

---

## 39. 方向重排（2026-10-02 第二段结论）

| 序 | 方向 | 依据 | 预期 | 判定 |
|---|---|---|---|---|
| **N1** | **Build 段的每元素成本**（zero/count/pf/place/Integrate 合计 ≈1.7 ms ≈ 整步 1.1%） | §38（五项 B/A 0.70–0.87）；§30 的环内访存 177 vs 126 正落在这些"字段多、体短"的内核上 | 未知，量级**足够**（唯一 ≥1% 的方向） | **新第一优先** |
| **N2** | 协议剩余项：默认 `WarmOffset 0`、配对前校验 `steps`、A 侧"按步数停"的口径（宿主只支持秒 ⇒ 不改宿主就靠 `steps` 过滤） | §36.6/§36.7 | 0 收益，但**保护所有后续结论** | 立即执行 |
| **D1′** | 元素跨度声明：**能力已落地并验证**（`e<N>` + `claimSpanOverride`），但默认不改 cap | §37.2–37.4（Integrate −5.3% 4/4，但整步 0.1%） | +0.17 ms | **降级为能力**，不做主杠杆 |
| **D2′** | 内核单入口化（ABI 单指针） | §37.4 原位标定 ⇒ 每次调用 ≈**56 ns**，全步调用开销 ≈0.22 ms | **≤0.22 ms** | 从 0.2–0.35 下调，**降级** |
| **D4** | 每-job 派发成本（连发慢 2.6×） | §33.2/§33.3 | ≈0.21 ms（0.13%） | 低优先（但通用） |
| **D5** | 未闭合：`zero` 的 ~12 µs 内存状态差；调用前导的 512 ns vs 114 ns | §29.4/§31.4 | 记账 | 待 D2 重测后判断是否自动消失 |
| **D6** | Melee 统一到 chunk 模型 / 把声明几何与跨度合成一条通用契约 | §0 + §31.1 | 未知 | 大改；Melee 已打平 ⇒ 优先级下调 |
| ❌ | 修正相位前的"Melee 赤字" | §36.6 | — | **撤回**（窗口不相交造成的伪影） |
| ❌ | `zero` NT/AVX2、删死参、调度地板 | §29.3/§30.3/§33.5 | ≤0.05 ms | 不做 |

**方法层沉淀（比本轮任何数字都重要）**：本轮六条缺陷里有 **四条**是"仪器在测自己"（负载仪器读自身、
诊断行混进返回值、清理函数没被调用、相位窗口不相交），且**每一条都表现成"框架有性能问题"**。
⇒ 本项目任何新仪器的第一条验收应该是：**先证明它能测出一个已知的 0**，再拿它去测未知的差异。

---

## 40. N1 判定：赤字在**每元素体代价**，不在调用/认领前导；D2 夹具否证

### 40.1 匹配调用粒度实验（`tools/gate-run/n1-granularity-sweep.ps1`）

判别逻辑：Unity 的 `innerloopBatchCount` 是 64，即 **Unity 每 64 个元素调用一次内核**；EntJoy 对齐档的 tile
正好是 64 元素，所以 **`cap=1` 就是与 Unity 完全相同的调用粒度**（一次调用 = 64 元素）。于是：

- `cap=1` 下仍存在的赤字 = **每元素体代价**（调用次数与 Unity 相同）；
- 基线 `cap=4`（256 元素/次）相对 `cap=1` 补回的部分 = **调用/认领前导**。

三臂都改全部 4 个 K64 内核（FlowPresence/Count/Place/Integrate），**每臂各配一次相位对齐的 Unity 运行**，
3 rep 旋转（本轮机器安静：系统负载 0.5–6 核，A 的 melee 117.5–120.1 稳定）：

| pass | c4 = 256 elem/call | **c1 = 64 elem/call（= Unity 粒度）** | c16 = 1024 elem/call |
|---|---|---|---|
| zero | 0.782 | 0.839 | 0.788 |
| count | 0.807 | 0.787 | 0.861 |
| pf | 0.724 | 0.676 | 0.730 |
| place | 0.885 | 0.880 | 0.940 |
| **Build 段** | 0.758 | **0.728** | 0.796 |
| **Integrate** | 0.807 | **0.812** | 0.828 |
| flow | 1.004 | 1.023 | 1.013 |
| melee | 1.013 | 1.006 | 1.010 |
| **整步** | **1.008** | **1.005** | **1.008** |
| steps-check | — | max\|dSteps\|=4 ⇒ 整步不可用 | max\|dSteps\|=7 ⇒ 整步不可用 |

**判定**：
1. **在完全匹配的调用粒度下（c1），Build 段仍慢 27%、Integrate 仍慢 19%**；把调用**粗化 4×**（c1→c16）
   只把 Build 从 0.728 补到 0.796（≈7 个点）、Integrate 从 0.812 到 0.828（≈2 个点）。
   ⇒ **每元素体代价是主项，调用/认领前导是少数项。** 这与 §30 的"环内访存 177 vs 126"指向同一处。
2. 顺带把 §37.4 的 56 ns/调用从"边际"提升为**有上界的结论**：即使把前导全部消掉（c1→c16 的极限），
   Build 段还剩 ~20 个点的赤字。
3. **整步在三个臂里都是打平（A 快 0.5–0.9%）** —— 与 §38 的"慢 1.6%"同属那条 ±3% 的会话带宽，
   本轮三次独立复现（1.005/1.008/1.008）说明**对齐档的真实状态是"打平"**。
4. c1/c16 的 `steps-check` 都报了 SUSPECT（A 的运行步数因限时口径而变），所以整步数字只作旁证，
   逐 pass 数字（count/place/Integrate 对相位不敏感，见 §36.6 的 warmup 扫描）才是本节的依据。

### 40.2 D2 夹具否证：**单结构体指针的 ABI 没有收益**

夹具里原本就有这个对照臂（`tools/NativeTranspilerFixture`）：同一个内核体、同一份输入，只改**参数形状**：
`BenchParams96Job` = 96 个扁平形参（32 pad 数组 + 32 pad 标量 + 8 实数组 + 6 标量），
`BenchParams96AllJob` = 把 40 个输入字段装进 **单个结构体字段** `In`。

先确认两臂**真的是不同 ABI**（不是被生成器摊平的空转）：

```
BenchParams96AllJob_Execute_Batch(int start, int count,
    BenchParams96In* __restrict In_ptr,          // 40 个输入字段 = 1 个指针
    float* KD2_ptr, int KD2_length, int* KPeer_ptr, int KPeer_length,
    int* Counters_ptr, int Counters_length)       // 共 9 个形参
```
对照扁平版：`..._BenchParams96Job_Execute_Batch(int,int,` 后面是 96 个扁平形参 + 各自的 `_length`。

实测（`dotnet run --project tools\NativeTranspilerFixture -c Release -- bench`，同会话交错 5 对，ns/元素）：

| pair | p125（96 扁平形参） | p9（单结构体指针） | p9/p125 |
|---|---|---|---|
| 1 | 1416.8 | 1422.7 | **1.004** |
| 2 | 1416.5 | 1422.3 | **1.004** |
| 3 | 1413.2 | 1423.0 | **1.007** |
| 4 | 1416.8 | 1419.8 | **1.002** |
| 5 | 1416.8 | 1423.5 | **1.005** |

校验和两臂完全一致（`cand = 428168300`）⇒ 语义等价；**5/5 都更慢 0.2–0.7%**。

**判定：D2（内核单入口化 / 单结构体指针）否证，不铺开。** 而且它给出了机理：
把标量放进结构体后，环内访问变成"透过指针取标量"，比扁平标量（可留寄存器）**多一层间接**；
而扁平版的 36 个指针本来就超出 ~13 个可用 GPR，两者都落到栈上被反复重载 ⇒ **换 ABI 形状不改变重载**。
这与 08 §32 的旧观察一致：`SCALAR_RESTRICT=1` 真的带上 `__restrict` 后 **环内重载次数仍是 7 次**
⇒ **那些重载不是别名驱动的，是寄存器压力驱动的**。

> ⚠ **2026-10-02 更正（读生成的签名之后）**：上面那句"扁平标量（可留寄存器）"**是错的**。
> 生成代码把**标量也按指针传**：p125 的签名是
> `... int* PadScalar0_ptr, ... int* __restrict CellsW_ptr, int* CellsH_ptr, float* InvCellSize_ptr,
> float* OriginX_ptr, float* OriginY_ptr, float* __restrict MyOrcaRadiusSq_ptr, ...` —— 也就是说
> 读一个标量要"**先取指针、再解引用**"（两次依赖载入），而数组是 `ptr + length` 两参。
> p9 则是**一个 `BenchParams96In* __restrict In_ptr` + 立即数偏移**（`offset(%In_ptr)`，一次载入）。
> **于是朴素预测是 p9 更快（寄存器更省、少一次间接），而实测是 p9 慢 0.2–0.7% —— 方向相反。**
> ⇒ **那 0.4% 目前无法解释**（已列入"未定位"）。候选原因，按可测性排序：
> ① **夹具自身不同构**：p9 臂的反射循环只填 `job96`，**从未给 `In.PadArr0..31` 赋值**
> （那 32 个字段是"只为撑开形参表"的哑字段，按理不被读，但两臂的输入并非逐位相同）；
> ② **只有 p9 有 `__restrict`**（`In_ptr` 上），restrict 既可能帮忙也可能改变寄存器分配/代码布局；
> ③ **工作集驻留**：p125 的参数被**拷进被调方的原生栈帧**（热 L1），p9 的 1352 B 结构体是**就地读**
> （托管侧的缓冲，跨 22 条 cacheline），标量/指针字段反复读它；
> ④ 代码对齐 / I-cache 布局。
> **决定性实验**（未做）：做一个**纯 C++** 的对形状微基准（`tools/HotSpotMicro`），
> 只比"标量按指针传 vs 按结构体偏移取"，把 C# / 转译器 / 托管缓冲全部排除；
> 若差距消失，则 ③ 是原因（即 ABI 形状本身无关，是缓冲驻留），若仍在，再查 ②。
> **对结论的影响：没有** —— 无论 1.004 还是 0.996，ABI 形状都值不到 1%，且实测方向说"扁平略好"。

### 40.3 D3 的重新表述（唯一剩下的杠杆）

| 旧表述（§30/D3） | 新表述（本节实测后） |
|---|---|
| "36 个扁平形参 ⇒ 环内重载 177 vs 126 ⇒ 单入口化可解" | 前因**错**：ABI 形状无关（40.2）。真因是**每元素同时活跃的指针值数 > GPR 数 ⇒ 溢出重载**，与参数怎么传无关 |
| 杠杆 = 改 ABI | 杠杆 = **降低每元素同时活跃的值数**（循环分裂 / 分块处理多字段 elementwise 内核），或提高单次循环内的复用 |
| 先验手段 = asm 指令普查 | 不变（先把环内 `mov/lea` 计数降下来，再看墙钟）；但**必须在夹具里先做出一个环比 <1 的臂**，否则不该碰生成器 |

**记账**：D2 ❌ 否证（40.2）；D1 ✅ 能力已落地、量级 0.1%（§37）；D0 ✅（§36）；D4/D5/D6 未动。
对齐档当前状态：**整步打平（1.005–1.008，三次独立复现）**，赤字集中在 Build 段与 Integrate 的**每元素体**。

---

## 41. D3 追到底：环形状几乎相同，且 B 的 Integrate **由 B 自己声明不可比**

### 41.1 "73 次栈访存"是**分类**差异，不是代价差异

上一轮 disasm 报告（`tools/gate-run/q5disasm/REPORT.md`）把 A 的 73 次 `rsp` 基址访存当成"headline number"。
把两边的环放在一张表里看，结论相反：

| 指标（Integrate 每元素环） | A (clang-cl) | B (Burst AVX2) |
|---|---|---|
| 环内指令 | 420 | 399 |
| 环内字节 | 1982 | 1808 |
| 每迭代元素数 | 1（未展开） | 1（未展开） |
| **访存总数 / 元素** | **164**（129 载 + 35 存） | **160**（119 载 + 41 存） |
| 其中 `rsp/rbp` 基址 | 73 | **0** |
| 纯数据数组访存 | 91 | 160 |
| 环内 `call` | 0 | 0（每个 range 一次 `callq`，不在元素环内） |
| 帧大小 | 280 B | 424 B（**B 更大**） |

**两者访存总量几乎相同（164 vs 160），只是"字段从哪儿取"不同**：B 的字段在一个已解包的**结构体指针**（`%rdi`）
里，按固定位移取（`movl 0x3c(%rbp), %edx`），被归为"数据访存"；A 的字段在**入参帧镜像**里取
（`movq 0x168(%rsp), %rax`），被归为"栈访存"。**两者都是 L1 固定位移 load，成本相同。**

这个"分类不等于代价"的判断**已被独立证实**：§40.2 的夹具实验正是"把 40 个输入字段从扁平入参帧搬进一个结构体"
—— 结果 **5/5 更慢 0.2–0.7%（1.004）**。⇒ **73 这个数字没有代价含义，上一轮把它当瓶颈是错的。**

（同时，`§1(f)` 的"84 字节 cfg 结构体"也不是问题：A 只载入实际用到的 5 个 4 字节字段 = 20 B/元素，
与 B 的"只载入用到的字段"同策略。`§1(e)` 的 "pressure verdict" 仍然成立且有用：33 个活跃指针 vs ~9 个空闲 GPR
⇒ **环是寄存器压力受限，不是别名受限**；这也解释了为什么 08 §32 打开 `__restrict` 后环内重载次数不变。）

### 41.2 两边的"同名段"是否做同样多的活：工作证明

B 侧 CSV 带工作证明。三轮实测（`tools/gate-run/n1gran/B-r{1,2,3}.csv`）：

| 证明 | 实测 | 期望 | 判定 |
|---|---|---|---|
| `integrate_touched` | 500000 | 500000 | ✅ `ceil(1e6/64)=15625` 批 × 32 步 ⇒ **B 的 Integrate 覆盖全部 1e6 元素** |
| `recycle_first` | 224 | 224 | ✅ |
| `melee_work` | **0** | 31935552 | ⚠ **设计如此**：单位口径被 `Bb0Align.Buckets=false` 编译期关断，仅 `M4_BUCKETS=1` 才计（`BattleBenchM4Entry.cs:24` 与 `:606` 明写）⇒ **不是失败** |

### 41.3 B 的 Integrate 由 **B 自己的源码头**声明不可比

`E:\UnityProject\...\BattleBenchM4.cs` 文件头（§13.9）原文：

> ⚠ 必须随结论披露的两点：① dump v2 只有 position/alive/state/team/hp ⇒ **B 的 Integrate 拿到 vel=0、knock=0、
> af=0、stuck=0** ⇒ 只有"存活 + 位移"这一条主分支可达（A 的活战场还有击退/受伤硬直/尸体滞留分支）
> ⇒ **Integrate 段仍不可比**，只作"整步结构完整"用。

另外 `BattleBenchM4Entry.cs:462` 的注释也确认粒度确实对齐（`Schedule(n, 64)`，且"照抄 A 的 0 会更慢：
Integrate 2.88→4.32ms"），`WallProjOn`/`ObstacleCount` 由 `wallsOn` 环境开关驱动（A 侧 `ObstacleCount=40` 会投影）。

### 41.4 D3 的最终判定

把三轮证据叠起来：

| 候选机理 | 判据 | 判定 |
|---|---|---|
| ABI/参数形状（扁平 96 形参 ⇒ 重载） | 夹具 5/5：单结构体 = **1.004**（更慢） | ❌ 否证 |
| 调用/认领前导 | 匹配粒度（c1 = 64 元素/次）仍慢 27%/19%；粗化 4× 只补 7/2 个点 | ❌ 不是主项 |
| 环内机器码（指令数/访存数） | 420 vs 399 指令、164 vs 160 访存；"73 栈访存"无代价含义 | ❌ 不是主项 |
| 别名（`__restrict`） | 08 §32：打开后环内重载次数不变；本文件 §1(e) 亦判"pressure bound" | ❌ 不成立 |
| **注册压力**（33 活跃指针 vs ~9 GPR） | §1(e) 逐寄存器点名；属**真实**约束 | ✅ 成立，**但框架无法降低它**（活跃指针数由宿主的 job 字段决定） |
| **Integrate 段工作量不等价** | B 源码头 §13.9 自述"不可比" | ✅ 成立 ⇒ 该段差额**不可归因** |

⇒ **对齐档剩下的 per-pass 差额（Build 段 + Integrate）不能由 EntJoy 的框架侧解释**：机器码形状几乎相同、
ABI 形状无收益、调用前导是少数项、别名不成立；唯一成立的"注册压力"由宿主的字段数决定；
而 Integrate 那一段被 B 臂自己声明为**工作量不等价**（要修就得改测试端，超出本轮约束）。

**因此 D3 的结论是"无可测量的框架侧余量"，而不是"没找到"** —— 这是本轮方法学上最有价值的一条：
把一个被反复怀疑的方向（环内访存/ABI）用**夹具 + 匹配粒度 + 静态码形状 + 工作证明**四条独立证据钉死。
对齐档的真实状态就是 §40.1 的 **整步打平**（1.005–1.008）。

---

## 42. D4 判定：派发成本已定位到机制、上界 0.07–0.13%，**不动**

### 42.1 基线（同会话，`tools/BuildPassBench`，`BENCH_SHAPES=emptyjob`，min of 6 rounds，µs/job）

空 job、`len=1` —— 注意 `length ≤ 64` 会走 `ScheduleFastPath`，所以**这不是"调度器开销"，是"最小派发往返"**：

| 配置 | 8 worker | 1 worker | 8w−1w |
|---|---|---|---|
| parallel 1000 job | **0.631** | **0.346** | +0.285 |
| parallel 100 job | 0.664 | 0.480 | +0.184 |
| serial 1000 job | 0.940 | 0.418 | +0.522 |

### 42.2 三个假设，两个被否证

| 假设 | 实验 | 结果 |
|---|---|---|
| 诊断 RMW（每次派发 4 个 + `RecordPublishedJob`） | `ENTJOY_STATS=0` | ❌ **无影响**：8w 0.631→0.632、1w 0.346→0.340（且 `RecordPublishedJob` 默认早退、`AssignStateDiagnosticId` 只有一次 relaxed fetch_add） |
| worker 自旋抢 cacheline（共享 `wakeEpoch`：派发方 `fetch_add`，8 个 worker 紧循环 `load`） | `ENTJOY_SPIN_BUSY` = def/0/64/1024；`ENTJOY_SPIN_HOT_US=200` | ❌ **无影响**：0.615/0.635/0.638/0.650/0.588（全在噪声内；`SPIN_HOT_US` 默认本来就是 0） |
| 状态池分配/争用 | 读码：`CreateState` 已是**线程本地缓存 + 一次性批量补池**（`JobSystem_State.cpp:613-664`，`kStateCacheCap`/`g_statePoolMutex`） | ❌ 不是主项 |
| **每次派发一次"投递→唤醒→worker 领走→跑→回收"往返** | 读码：`FastPath` **不内联**，它 `SubmitBackendAsync(...)`（`JobSystem_Scheduler.cpp:186-215`）；该路径的历史与两级池化见 `JobSystem_State.cpp:879-894`（当时实测 **800–900 ns/job**，堆分配项已修） | ✅ **这就是机制** |

**对照 Unity**：`JobHandle.Complete()` 对一个尚未被 worker 取走的极小 job，Unity 会在**调用线程上内联执行**（其 job 系统空闲时的既有行为）⇒ 单线程下 EntJoy 0.346 µs/job 与 Unity 0.29–0.41 本来相当，
**2.6× 全部来自"8 个 worker 在场"这件事本身**（多一个唤醒/争抢往返）。

> ⚠ **2026-10-02 更正（§44）**：上面这句"Unity 会在调用线程上内联执行"是**会话外的知识被当成了实测**。
> 补测之后：方向得到支持（Unity 的 1000-派发成本对 worker 数**不敏感**），但**"内联"这个机制本身没有被测到**，
> 只能说到"它不付这次跨核交接的代价"。详见 §44，那里也给出了"小 job 内联"为什么不安全（含本会话自己的反例）。

### 42.3 为什么不修

1. **上界**：真实战场实测 `[JOBWAKE] notify_all skipped=49280` / 128 步 ≈ **385 次提交/步**
   ⇒ 0.285 µs × 385 ≈ **110 µs/步 ≈ 0.07%**（就算整块消掉）。这个量级远小于 §40.1 的 ±3% 会话带宽。
2. **风险**：修法只有一条 —— 让"极小 job 在调用线程内联执行"。但这条路的**执行语义**被显式占用：
   `FastPath` 的注释说明它承载"异常按本 job 归属 + 调试面板泳道上报"；同文件（`ChaseLevScheduler.h:126-137`）
   还记录了上一次"改参与者/准入"的尝试被 **ECS 读写序断言当场抓住**并回滚。
3. **纪律**：收益 0.07%、换执行语义 ⇒ 按本项目"收益 ÷ 风险"的取舍，**记账不动**。

---

## 43. D5/D6 处置（本轮收口）

| 项 | 状态 | 依据 |
|---|---|---|
| D5① `Integrate` "每次调用 512 ns vs asm 114 ns 的 4.5× 缺口" | ✅ **已闭合（且方向修正）** | §37.4 原位标定：4× 少调用只买到 0.165 ms ⇒ 边际 ≈**56 ns/调用**，不是 512 ns。那个 512 ns 是从微基准外推的，属"未定位"被误当"已定位" |
| D5② `zero` 的 ~12 µs 内存状态差 | ⏳ **仍未闭合** | §29.4。闭合需要给 B 侧加插桩（改测试端，超出约束）或在 A 侧复现整步脏行量 ⇒ 记账 |
| D6 Melee 统一到 chunk 模型 / 声明几何+跨度合成一条通用契约 | ❌ **不做（负期望）** | §40.1/§41：Melee 是 EntJoy **已经赢**的段（B/A 1.006–1.022，占整步 76%）；把已赢的段按对手模型重构，是风险换未知收益。而"声明几何+跨度"的**能力**已随 D1 落地（§37.1），无需为它改 Melee |

### 43.1 目标收口：对齐档的真实状态

| 档 | 状态 | 证据 |
|---|---|---|
| **对齐档** | **打平**（A 快 0.5–0.9%，三次独立复现：1.005 / 1.008 / 1.008） | §40.1 |
| **默认（产品）档** | **A 快 6.3%**（0.937×） | §38 及历史（未变） |

对齐档剩下的 per-pass 差额被 §41.4 的四条独立证据判定为**不可归因到 EntJoy 框架**（环形状几乎相同、
ABI 形状无收益、调用前导是少数项、Integrate 段 B 自己声明工作量不等价）。
⇒ **"追到 Unity 的 0.86 标准"这一目标在可测量的范围内已经达成并留有余量；本轮之后没有量级 ≥1% 的已知杠杆。**

---

## 44. 更正与补测：派发差额**不是**"调用方在干等"，而"小 job 内联"是错的路线

### 44.1 更正 §42.2

§42.2 里"Unity 会在调用线程上内联执行"**是会话外的知识，不是本会话的实测** —— 按本项目"实测与未定位严格分标"
的纪律，它必须降级为推断。补测之后：

**Unity 侧（`tools/gate-run/q5unitydisp/`，n=100 轮，us/job）** — 这张表本该在 §42 就拉出来：

| 空 job 臂 | Unity 8w（min / med，两次干净复跑） | Unity 1w（min / med） | 对 worker 数 |
|---|---|---|---|
| `empty_parallelfor_1000` | 0.292 / 0.322 与 0.293 / 0.336 | 0.326 / 0.371 | **不敏感**（8w 甚至略好） |
| `empty_parallelfor_100` | 0.411 / 0.662 与 0.445 / 0.669 | 0.439 / 0.507 | 基本不敏感 |
| `empty_serial_ijob`（单发） | 0.700 / 5.550 与 0.800 / 2.900 | **0.700 / 0.800** | **中位强烈敏感（3.6–7×）**，min 不敏感 |
| `empty_parallelfor_1`（单发） | 4.00 / 7.85 与 5.10 / 7.80 | 2.80 / 4.40 | 敏感 |

**EntJoy 侧（本会话，min of 6 轮 / best-med）**：

| 空 job 臂 | 8w | 1w | 对 worker 数 |
|---|---|---|---|
| parallel 1000 | 0.631 / 0.719（assist A/B 那轮 0.759 / 0.898） | 0.346 / 0.421 | **敏感 +0.285（+82%）** |
| parallel 100 | 0.664 / — | 0.480 / — | 敏感 |
| serial 1000 | 0.940 / — | 0.418 / — | 敏感 |

⇒ **结论修正**：Unity 的**突发派发成本对 worker 数不敏感**（0.32 @8w vs 0.37 @1w），EntJoy 的敏感。
这**支持**"Unity 不让这次派发变成一次跨核交接"这个方向，但**"内联执行"这个具体机制本会话没有测到**
⇒ 状态是 **未定位**，不是已定位。（另外两边单发的 **min** 都是 ~0.70 µs ⇒ 地板相同，差的是中位/抖动。）

### 44.2 为什么"按 length 判小 ⇒ 内联执行"是错的路线

**`length` 不约束每个元素的代价。本会话自己的数据就是反例**：

| 内核 | `N`（length） | 实测 ms | 每元素 |
|---|---|---|---|
| `prefixFinal`（`key=00010da0`） | **64** | 0.183–0.190 | **≈2.9 µs/元素** |
| `count`（`key=00001950`） | 1 000 000 | 0.98–1.04 | ≈1.0 ns/元素 |

⇒ `prefixFinal` 的 length 只有 64（会命中任何"小 job"判据），但每元素代价是 elementwise 内核的 **~3000×**。
一条 `length ≤ 64 ⇒ 在调用线程跑完` 的规则会把它整个丢给主线程；而一个 `length = 1`、内部做 10 ms 串行归约的 job
会直接把主线程冻 10 ms。**用户的担心成立，这条路线不能走。**

### 44.3 安全的形式是"阻塞时帮忙"，而它**已经在框架里**，且实测只值 ~10%

`JobSystem_SetMainThreadAssist(int)`（`Exports.cpp:788`）⇒ C# `NativeJobScheduler.SetMainThreadAssistEnabled`
（`NativeJobScheduler.cs:554`）。语义是**"先 assist 再 wait；每轮最多 assist 16 次"**
（`JobSystem_State.cpp:1543-1549`，原文注释：防止链条级联时主线程无限 assist 不回查 completed）。

- 它**不是**"内联执行某个 job"，而是"调用方从 worker 同一条注入器里领活干"⇒ 不增加任何阻塞（调用方本来就在等），
  且 job 对 worker 始终可见、重 job 由多方分担。
- 开关归属：**框架默认 `false`**（`JobSystem.cpp:647`，"纯 worker 模式"）；**宿主 CSBS 默认开**
  （`CPUBattleEcs.cs:625`：`ENTJOY_ASSIST != "0"`）；对齐档 harness 显式 pin 成 `0`，让两边都是 8 个执行者。

**实测（本轮给 bench 加了 `BENCH_EMPTY_ASSIST` 钩子，`tools/BuildPassBench/Program.cs`）**：

| 臂 | assist=0（min / best-med） | assist=1 | 变化 |
|---|---|---|---|
| 8 worker · parallel 1000 | 0.759 / 0.898 | **0.679 / 0.847** | −0.080（收回 gap 的 ~10%） |
| 1 worker · parallel 1000 | 0.344 / 0.421 | 0.365 / 0.460 | 略差（没人可帮） |
| 8 worker · serial 1000 | 0.934 / 1.048 | 0.980 / 1.070 | 略差 |

⇒ **assist 只收回约 10%，所以那 +0.285 µs/job 的主体不是"调用方在干等"。**
剩下的部分更可能是**"8 个 worker 同时轮询同一条共享注入器"的吞吐代价**（本次已否证 `ENTJOY_SPIN_BUSY=0`
与 `ENTJOY_STATS=0` 两条解释，见 §42.2），属于调度器结构问题；量级仍是 **≈0.07%/步** ⇒ 记账不动。

### 44.4 对"要不要学 Unity 内联"的回答（收口）

| 做法 | 安全性 | 实测收益 | 判定 |
|---|---|---|---|
| 按 `length`/调度期估计"小" ⇒ 调用线程内联跑完 | ❌ `length` 不约束元素代价；反例见 44.2（`prefixFinal` 64 元素 / 2.9 µs per 元素） | 未测（不建议测，语义风险） | **不做** |
| 调用线程在 `Complete()` 里 assist（= 领活干） | ✅ 只是把"本来就要等"变成有用功；job 仍对 worker 可见 | **~10% of 0.285 µs** | **已有能力，框架默认关、宿主默认开**；对齐档 pin 关以保 8v8 |
| 降低共享注入器的消费者争用 | ✅ 无执行语义变化 | 未测（剩余 ~90% 的候选） | 记账；≈0.07%/步，不值得动 |

---

## 45. 对齐档落后项的**框架侧候选**：调用线程 assist（`ENTJOY_ASSIST`）

### 45.1 线索来自"A 的 Build 段比它自己的六趟之和大 0.31 ms"

把每次 A 运行的 `[M-1] Build 段` 与 `[M-19]` 的六趟和（含 hostRewrite）对比，**A 稳定多出 ~0.3 ms**，
而 B 侧没有这个差（它的六行之和 ≈ 它的 `M4,seg,build_ms`）：

| 会话 | `Build 段 − 六趟和` | 均值 |
|---|---|---|
| `d1accept2`（3 rep） | 0.138 / 0.306 / 0.297 | **0.26 ms** |
| `n1gran`（3 rep） | 0.437 / 0.143 / 0.297 | **0.38 ms** |
| `d1span2`（4 rep） | 0.145 / 0.022 / 0.335 / 0.309 | **0.35 ms** |

⇒ 对齐档 Build 赤字（+0.86 ms）里 **35–45% 不属于任何命名内核**，而是"段内、趟外"的时间。
最自然的归属是每趟的**爬坡/尾部/同步**，而框架对此的机制正是 `ENTJOY_ASSIST`
（`JobSystem_SetMainThreadAssist` ⇒ 等待 `Complete()` 的线程从**同一条注入器**里领活干，
`JobSystem_State.cpp:1543-1549`：先 assist 再 wait，每轮最多 16 次）。

**而这个开关从未在对齐档测过**：harness 一直把它 pin 成 `0`，理由是"两边都只有 8 个执行者"。
那个前提本身未经验证，而且很可能是反的 —— Unity 的 `Complete()` 会在调用线程上处理 job，
这与 §44.1 唯一的独立实测一致（Unity 派发成本对 worker 数**不敏感**：0.322 µs/job @8w vs 0.371 @1w；
EntJoy 敏感 +82%）。**若如此，pin 掉 assist 比较的是"8 个 EntJoy 执行者"对"8 个 Unity worker + Unity 的调用线程"。**

### 45.2 实测（对齐档，A-only，交错旋转，逐 rep 配对；两轮池化）

`n3`（4 rep，负载 1.4–4.8 核）与 `n4`（3 rep，负载 1.8–8.2 核）：

| pass | n3 符号 / 中位 Δ | n4 符号 / 中位 Δ | 池化 | 判定 |
|---|---|---|---|---|
| **flow** | **4/4** / **−1.010** | **3/3** / **−1.640** | **7/7** | ✅ **成立**（−3…−5%） |
| melee | 3/4 / −0.675 | 1/3 / +1.670 | 4/7 | ✗ 不成立 |
| place | 4/4 / −0.080 | 1/3 / +0.007 | 5/7 | 弱 |
| count | 2/4 / +0.006 | 2/3 / −0.071 | 4/7 | ✗ |
| integ | 2/4 / −0.115 | 2/3 / −0.280 | 4/7 | ✗ |
| **build 段** | 2/4 / **0.000** | 1/3 / +0.010 | — | **未改善 ⇒ assist 不修 45.1 的残差** |

- 收益随机器负载增大而增大（n3 的 flow：r1 −0.32 → r4 −1.88），正是"填空闲槽位"该有的形状。
- 机制自洽：**Flow 是 11 个内核 × 1e6 tile 的段，爬坡/尾部最重**，所以 assist 收益最大；
  Build 段（6 趟、粗 tile）几乎无爬坡可填，所以不动。
- **重要**：`n4` 的**带 B 验收不可用** —— A 的 melee 在本轮是 124–130（平常 117–120，系统负载到 8 核），
  且 `a1` 臂的 B 自己慢了 1.7%，把比值差异从 A 的 2.6% 放大到 5.9%。**只能采信 A-only 配对。**

### 45.3 结论与下一步（对齐档）

1. **候选 1（最强，7/7 已复现）**：对齐档开 `ENTJOY_ASSIST=1` ⇒ `flow` −1.0…−1.6 ms
   ⇒ 整步 ≈ **−0.65…−1.0%**，并把 Flow 从"打平（0.988）"推到"我们更快（1.005–1.011）"。
   它**不破坏对齐语义**（工作分解镜像不变，只是让等待的调用线程也参与认领）。
   **待办**：一次干净的 3 rep 带 B 验收（B 侧必须与 A 侧分别在臂内配对，看各自绝对值是否漂移）。
2. **候选 2（框架参数）**：assist 目前**每轮最多 16 次**（防止主线程无限 assist 不回查 `completed`）。
   Flow 的收益可能正被这个上限削掉 ⇒ 调大上限、或改成"assist 直到 `completed`"的循环，是**明确可测的框架参数**。
3. **候选 3**：45.1 的 0.31 ms 残差 —— assist 已排除它；剩余归属（宿主 C# glue vs 框架 sync）待分段。

---

## 46. 连发小 job 的 worker 数敏感性：曲线形状、五个被否证的候选、以及 Unity 为何不同

### 46.1 曲线（`tools/gate-run/n5-dispatch-scaling.ps1`，1000 个空 job，min of 6，assist=0）

| W | 1 | 2 | 3 | 4 | 6 | 8 | 12 | 16 |
|---|---|---|---|---|---|---|---|---|
| **parallel** µs/job（先全 Schedule 再全 Complete） | 0.343 | 0.426 | 0.503 | 0.509 | 0.594 | 0.666 | 0.717 | 0.803 |
| **serial** µs/job（逐个 Schedule+Complete） | 0.426 | 0.653 | 0.724 | 0.814 | 0.899 | 0.917 | 0.980 | 1.020 |

形状判定：两条曲线都**次线性增长**、且**在 W=8（物理核数）之后继续涨**（8→16：+0.137 / +0.103），
所以既不是"线性于 W 的每 worker 流量"，也**不是干净地在物理核数处饱和**（首版猜测被这轮否掉）。
最大的单步是 **1→2**（serial +0.227）⇒ 含"第二方出现"的成分。

**Unity 侧（`tools/gate-run/q5unitydisp/`，同样形状，n=100 轮）**：

| 臂 | Unity 8w（min / med，两次干净复跑） | Unity 1w（min / med） | 对 worker 数 |
|---|---|---|---|
| `empty_parallelfor_1000` | 0.292 / 0.322 与 0.293 / 0.336 | 0.326 / 0.371 | **不敏感** ✓ |
| `empty_serial_ijob`（**单发**） | 0.700 / 5.550 与 0.800 / 2.900 | 0.700 / 0.800 | **中位敏感（3.6–7×）**，min 不敏感 |

⇒ "Unity 不敏感"**只在连发形状上成立**（正是本问题的形状）；单发的中位反而比 EntJoy 差。
控制已核实：`M4_WORKERS` 真写进 `JobsUtility.JobWorkerCount` 并回读
（`BattleBenchDispatchFloor.cs:237` + `BattleBenchUtil.cs:72-83`）。

### 46.2 五个被否证的候选（全部同会话配对实测）

| # | 候选 | 实验 | 结果 |
|---|---|---|---|
| 1 | 诊断 RMW（每派发 4 个） | `ENTJOY_STATS=0` | ❌ 无影响（8w 0.631→0.632） |
| 2 | worker 自旋抢 cacheline | `ENTJOY_SPIN_BUSY`=0/64/1024、`SPIN_HOT_US=200` | ❌ 无影响（0.615/0.635/0.638/0.650/0.588） |
| 3 | 调用方干等 | `BENCH_EMPTY_ASSIST=1` | ❌ 只收回 ~10%（0.759→0.679） |
| 4 | `g_liveHandleStates`（**唯一未被 STATS 门控**的每 job 跨核 RMW：提交线程 +1 / 完成 worker −1） | 实现按线程分片（每片独占 cacheline、读侧求和），同会话**只换 DLL** 配对 6 往返 | ❌ **中性到更慢**：W=1/2/4/8/16 ratio = 0.917/0.983/0.940/0.988/0.952。W=1 无跨核流量 ⇒ 分片只增成本，−8.3% 正是自洽性检查；由"W=8 处 +0.031 固定成本 / −0.023 跨核节省"反推 ⇒ **这条线只值 ~0.023 µs/job ≈ W 依赖总量的 7%**。**已回退** |
| 5 | 共享池 mutex（`g_statePoolMutex` / `g_asyncCtxPoolMutex`；原 caps 64/8/16 ⇒ ~0.14 次取锁/job） | 把三个 TLS cap 临时放大到 1024（取锁降到 ~1/64），同样换 DLL 配对 6 往返 | ❌ **完全无影响**：ratio = 1.036/0.977/1.004/**1.000**/**0.999**。**已回退**（且 cap=8 本就是被旧实测选定的值） |

**顺带得到的校准（本轮最有用的副产物）**：第一次跑 4/5 时因备份文件名格式不符，`Copy-Item` **静默失败**
⇒ 两边其实是**同一份 DLL**，于是得到一次**空对照 A/A**：ratio = 1.046/1.021/1.015/**0.964**/1.002
⇒ **这套方法（3 往返）的噪声底是 ±4%**，小于它的效应测不出来。据此加到 6 往返，并给换 DLL 加了
**hash 守卫**（要换的 hash 不在位就 abort）——正是这个守卫抓出了那次静默 no-op。

### 46.3 为什么 Unity 没有这个问题（**机理是推断，未实测**）

两条结构性差异，各有代码/反汇编依据：

| | Unity | EntJoy |
|---|---|---|
| 一个 job 的队列代价 | **一条 job 记录**，worker **自行切 index 区间**（反汇编里 `callq *[rip+..]` 是**每 range 一次**的区间迭代器，**不在元素环内**，环内步长 1） | **往共享 MPMC 注入器塞一个闭包**（`FastPath` → `SubmitBackendAsync`），W 个 worker 都观察/争抢同一条注入器索引线 |
| 每 job 是否跨核交接 | 连发形状下**不付**（实测对 W 不敏感） | **每条派发都付**（注入器与 `wakeEpoch` 都是 worker 在读/在等的线） |

⇒ **"通解"不是某个开关，而是一条设计性质**：**"派发一个 job 不得写任何所有 worker 都持有的 cacheline"**。
要拿到它只有三条结构路，各有代价/风险：

1. **入口分片**：每 worker 一条注入器，或让生产者推自己那条 + 空手才窃取。
   EntJoy 已有 per-worker Chase-Lev deque，**共享的只是"入口"这一层** ⇒ 改动面比想象小。
   风险：本项目记录过两次同族回滚（`MPMCInjector::PopMany` 在 W=15 一律更差；"减少参与者/准入"
   被 ECS 读写序断言当场抓住）。
2. **批量提交**：一次推 N 个 job，跨核交接从 N 次降到 1 次。框架**已有** `JobSystem_ScheduleBatch`
   （含"≥4 个描述符才打包"的快路径，`Exports.cpp:229-249`）。真实战场每步 ≈ **385 次提交**
   （由 `[JOBWAKE] notify_all skipped=49280 / 128 步` 折算）正是它的用武之地 —— **但需要调用方改用它**，
   在"不修改测试端"的约束下不能由框架单方面完成。
3. **让提交线程跑掉自己的 job（内联）**：**不安全** —— `length` 不约束元素代价，本会话自己的反例是
   `prefixFinal`（`N=64` 却 **2.9 µs/元素**，是 elementwise 内核的 ~3000×）⇒ 会冻主线程（§44.2）。

### 46.4 一句话记账

**这条线：机制族已定位（随参与核数增长的一致性代价）、五个候选已否证、第三个仍未点名
（首要嫌疑是共享注入器入口）；Unity 的平性是架构性质而非参数。**
**收益上界：真实战场 ≈ 385 次提交/步 × ~0.3 µs ≈ 110 µs/步 ≈ 0.07%（对齐档整步）**
⇒ 它是**通用性/健壮性**修复，不是基准修复；对"8+ worker、大量小 job 的 ECS"有价值，
对本仓库对齐档的整步**无可观测收益**（§40.1/§41 已证明对齐档赤字在每元素侧、与配置无关）。

---

## 47. **找到了**：连发小 job 的 W 依赖 = 每条派发一次广播（`SubmitWork` 的 `wakeEpoch` + `notify_all`）

### 47.1 来源：读码 + 该项目自己的旧实测注释

`ChaseLevScheduler::SubmitWork`（`ChaseLevScheduler.cpp:924-978`）在每次派发、且提交窗口深度 ≤0 时：

```cpp
if (g_submitDeferDepth.load(relaxed) <= 0) {
  if (DeferWakeEnabled()) { g_pendingDeferredWake.store(1); }
  else { wakeEpoch.fetch_add(1, release); wakeEpoch.notify_all(); }   // ← 每条派发一次广播
}
```

它上方那段注释**就是同一现象的旧实测**：
> 此前这里**无条件** bump+notify_all … 实测：15 个 parked worker 上一次广播约 **37～39 µs**；
> 被最坏情况放大为 **~600 ns/job**（`probe defer`：IJob x100 hot sched 832 ns/job → parked **1439** ns/job，
> **+607 ns/job**）

而 harness 日志确认 `[JOBPHYS] deferWake=OFF` ⇒ 走的就是广播分支。**这是已有的开关，从没对着"W 轴"测过。**

### 47.2 实测（`tools/gate-run/n8-deferwake-ab.ps1`，1000 空 job，min of 6，同会话交替臂）

| kind | W | hot（默认，广播） | **`ENTJOY_DEFER_WAKE=1`** | ratio |
|---|---|---|---|---|
| **parallel**（先全 Schedule 再全 Complete） | 1 | 0.349 | 0.304 | 1.15 |
| | 2 | 0.460 | 0.303 | 1.52 |
| | 4 | 0.522 | 0.314 | 1.66 |
| | **8** | **0.640** | **0.322** | **1.99** |
| | 16 | 0.792 | 0.392 | 2.02 |
| **serial**（逐个 Schedule+Complete） | 1 | 0.425 | 0.427 | 0.995 |
| | 2 | 0.557 | 0.594 | 0.938 |
| | 4 | 0.785 | 0.787 | 0.997 |
| | 8 | 0.973 | 0.941 | 1.034 |
| | 16 | 1.012 | 1.004 | 1.008 |

**两条结论，且互为自洽性检查**：
1. **连发形状下 W 依赖几乎消失**：defer 后 0.304→0.392（W=1→16，×1.29），而原来 0.349→0.792（×2.27）；
   W=8 处 **0.640 → 0.322 = 恰好落在 Unity 的 0.322/0.326**。
2. **serial 形状完全无效应**（0.94–1.03，全在噪声带内）—— 正如机制所预言："逐个 Schedule+Complete 时
   每次 `Complete()` 都必须补一次广播，所以推迟不掉"。**这个对照把因果钉住了**（若是别的效应，serial 也该动）。

### 47.3 但游戏内**没有成立**，而且原因被同一次测量解释了

`tools/gate-run/n9-defer-ingame.ps1`（对齐档，A-only，3 rep 旋转，逐 rep 配对）：

- **门控 ✓**：`d0` = `deferWake=OFF flushes=0`；`d1` = `deferWake=ON flushes=52679/53903/51856`。
- **关键反证**：`flushes ≈ 5.2 万 / ~120 步 ≈ **430 次/步**`，而提交数 ≈ **385 次/步**
  ⇒ **宿主基本是"一次 Schedule 紧跟一次 Complete"**，先把广播推迟、紧接着被 `Complete()` 冲刷掉
  ⇒ 与 n8 的 **serial 臂同形**，本来就不该有收益。
- 仍然测到的方向（flow −0.67、melee −2.61、whole −4.41，均 2/3）**不成立**：只有 2/3，且
  `steps-check` 报 `max|dSteps|=36 ⇒ SUSPECT`（整步不可用）。

**顺带发现一个会影响后续所有"工作证明"的坑**：`[M-20] 指纹`（`SortedXi/SortedSum/CountsSum`）
**不能当工作证明** —— 同一臂内三次运行的 `SortedXi` 互不相同（步数相同的两次也不同），
`CountsSum` 也有 **±50 ppm** 的漂移 ⇒ **这个仿真的状态逐位不可复现**。
它能给的只是"语义没有粗变"（≈5×10⁻⁵ 一致），**不能**证等价；要证等价得用别的证明（如逐位 dump 或计数器）。

### 47.4 回答"这是通解吗 / 整步是否都优化了"

**通解性**：
- ✅ **原理是通解**：**"一次派发不得广播给所有 worker"**。这就是 §46.3 那条设计性质的具体形态，
  而且现在有了实测支撑（广播成本随 parked worker 数增长，正是 W 依赖的来源）。
- ❌ **开关不是通解**：`ENTJOY_DEFER_WAKE=1` 只在"**先连发多个 Schedule、再 Complete**"的调用形态下生效
  （连发 2×、W 平；逐个 round-trip 无效）。要变成通解需要框架**不依赖调用方批量**也能合并唤醒，例如：
  ① **时间窗合并**（N µs 内最多广播一次）；② **把"有活"做成 worker 已经在轮询的标志位**（生产者每批写一次，
  而不是每个 job 一次）；③ 或在 worker 侧保留"入 park 前复查"（已有）下把广播从"每 job"降为"每批"。

**整步是否都优化了**：**没有。**
- **本轮没有任何改动被采用**：`g_liveHandleStates` 分片（§46.2 #4）与 TLS 缓存放大（#5）都实测无效并**已回退**；
  `ENTJOY_DEFER_WAKE` 只是**被测量**，**默认仍是 OFF**。
- 这条线**只优化"很多小 job 的连发"这一个轴**，与 §40.1/§41 证明的**每元素侧**赤字（Build 段 0.76、
  Integrate 0.77）**无关**；且本游戏宿主的调用形态（每步 ~385 次提交 ≈ ~430 次冲刷）恰好落在这条线的
  **无效区**。收益上界仍是 §46.4 的 **≈0.07%/步**。

---

## 48. 按"Unity 的设计"抄：**两次尝试、两次否证**，机理反而更清楚了

§47 找到"每条派发一次广播"这个靶子后，本轮把 **Unity 那条设计（signal-one 而不是 wake-all）**
真的实现出来测了 —— **结论是它更差**。两次尝试的代码都已回退（`git diff` 只剩 D1 的 5 个文件）。

### 48.1 尝试 A：`ENTJOY_WAKE_ONE` —— 每 worker 一条独占 cacheline 的唤醒槽，生产者 round-robin poke 一个

实现：`alignas(64) std::atomic<uint32_t> wakeSeq[kMaxTrackedWorkers]`；生产者只写被选中 worker 的槽
（`fetch_add(release) + notify_one()`）；worker 自旋与停靠都多看自己那条槽（照旧"先登记 parked 再复查"
防丢失唤醒）；所有真广播路径（SubmitBatch/WakePending/Stop/启动回滚）统一走 `WakeAll()`。
**两种开关状态下 native 测试 9/9 全过**（含 `ChaseLevIntegrationTests`/`ShutdownFinalizeTests`）。

实测（`tools/gate-run/n10-wakeone-ab.ps1`，1000 空 job，min of 6，6 往返，同会话交替臂）：

| kind | W=1 | 2 | 4 | **8** | **16** |
|---|---|---|---|---|---|
| parallel：`off/one` ratio | 0.951 | 0.919 | 0.839 | **0.803** | **0.696** |
| serial：`off/one` ratio | 0.952 | 1.049 | 0.947 | 0.977 | 0.942 |

⇒ **更慢，且随 W 单调恶化**（W=8 −20%、W=16 −30%）。**预测（"poke-one 应连 serial 也帮"）被否证。**

### 48.2 尝试 B：`ENTJOY_WAKE_SKIP_AWAKE` —— 把 §7ah 的守卫也搬到 `SubmitWork`（`parkedWorkers>0` 才广播）

实现：`SubmitWork` 的每派发分支加 `parkedWorkers.load() <= 0 ⇒ 跳过广播`（worker 已在自旋，会自己从注入器
看到 token）。**两种状态下 native 9/9 全过。**

实测（`tools/gate-run/n11-skipawake-ab.ps1`，同形状）：

| kind | W=1 | 2 | 4 | 8 | 16 |
|---|---|---|---|---|---|
| parallel：`off/skip` ratio | 0.997 | 1.007 | 1.010 | 1.011 | 0.990 |
| serial：`off/skip` ratio | 1.000 | 0.927 | 1.003 | 0.992 | 1.008 |

⇒ **无效应**（serial 的 0.927 在噪声带内）。**原因自洽且可解释**：两次派发之间约 0.3–0.6 µs 的空隙里，
worker 已走完退火预算（`kSpinMin=64` pause ≈ 0.6–2 µs）并登记停靠 ⇒ **`parkedWorkers` 在派发时刻
几乎总是 > 0**，守卫几乎不触发。

### 48.3 机理修正（三条实测互锁，且与最初的假设**不同**）

| 观测 | 解释 |
|---|---|
| `ENTJOY_DEFER_WAKE=1`（**完全不写**被轮询的线，只置一个没人轮询的 pending 标志）⇒ W=8 **0.640 → 0.322**（= Unity），W 依赖 ×2.27 → ×1.29 | 成本主项是**生产者对"被轮询的那条线"的写** |
| `ENTJOY_WAKE_ONE`（**换一条线写**，轮流写 W 条）⇒ **更慢 + 随 W 恶化** | 换线不解决：总流量同级，还赔上"被 poke 的 worker 可能在忙 ⇒ 任务没人取"的延迟 |
| serial 形状对 `defer` **无效应**（0.94–1.03） | 因果确实在这条广播路径上（否则 serial 也会动） |

⇒ **正确表述**：成本不是"一次广播 = W 次 futex"，而是 **"每条派发都要写一条 W 个 worker 正在轮询的
cacheline"** —— 每次写都要把它从 W 个共享者手里抢回独占。所以**"少写"的唯一有效形态是"不写"**
（defer），而**"换条线写"（poke-one）与"条件写"（skip-awake）都无效**。

### 48.4 这条轴的结论：**没有免费午餐**

调用方要等某个 job 时，**就必须叫醒某个 worker**（除非它自己把活干了 = assist）。能省的只有
"本来不必叫醒却还在写那条线"。而在 EntJoy 的结构里，"不必叫醒"只发生在**调用方先连发多个 Schedule
再 Complete**的形态下 —— 那正是已有的 `ENTJOY_DEFER_WAKE`。所以：

- ✅ **唯一实测有效的形态 = `ENTJOY_DEFER_WAKE=1`**（已有开关，默认关）；
- ❌ 抄 Unity 的 signal-one（poke-one）：**更差**，已回退；
- ❌ 条件跳过（skip-awake）：**无效**，已回退；
- ❌ 让 worker 永久自旋不 park（≈"always-poll"）：本项目已实测会抢 SMT（`ENTJOY_SPIN_NEEDS_WORK`
  就是为此而加），且 `SPIN_BUSY=0` 与默认**实测相同** ⇒ 两条路互相抵消；
- **收益上界仍是 ≈0.07%/步**（§46.4），且与对齐档赤字（每元素侧）**无关**。

**记账**：本轮"连发小 job"这条轴共否证 7 个候选（STATS、自旋、assist、live-handle 计数分片、
池 mutex 放大、poke-one、skip-awake），**只留下一个已有的、有前提的开关**（defer）。
**本轮没有任何改动被采用**；源码回到只含 D1 元素跨度声明的状态（native 9/9 全过）。


## 49. **N12**：把提交侧唤醒改成"醒着的人不够才写"（rayon 的 posted-without-storing + 两次 seq-cst fence）
### 结论先行：**微基准连发/逐个 round-trip 两种形状全胜且曲线变平（W=8 1.73× / 2.16×，W=1 无回归），
### 游戏内对齐档整步 −1.51 ms / 159.6 ms（3/3 趟，melee −1.39 ms、place 3/3），已提为默认开**
### —— 但**第一版实现让整机 2.2× 变慢**，原因与修法是本节最值钱的部分（§49.3–§49.5）。

### 49.1 起点：§48 的两条否证不是"这条轴没救"，而是"判据选错了"

§48 的机理结论是对的（成本 = 生产者写一条 W 个 worker 正在轮询的 cacheline，而不是"一次广播 = W 次
futex"），但**它否证的两个候选用的判据都不对**：

| §48 的尝试 | 判据 | 为什么必然无效 |
|---|---|---|
| `ENTJOY_WAKE_ONE` | 换一条线写（poke 一个） | 也是"写"，而且轮换 W 条线 ⇒ 总流量同级 + 被 poke 的人可能在忙 |
| `ENTJOY_WAKE_SKIP_AWAKE` | `parkedWorkers > 0`（**有没有人睡**） | 派发时刻 `parkedWorkers` **几乎恒 > 0** ⇒ 守卫几乎不触发 |

真正要问的不是"有没有人睡"，而是 **"有没有人正在轮询注入器、自己就能领到"**。两者是不同的量：
- `parkedWorkers` = 已登记停靠（含 futex 等待）的人数；
- `wakeIdlePollers` = **登记在"搜索区"**（每轮都读注入器）的人数。

**外部文献对照**（rayon-core `sleep` 模块，读的是 main 分支源码与它的 README）：
README 明说"每 post 一次就自增计数器 [turns out to be too expensive]"[^rayon746]，它的解法不是
"条件自增"，而是：**当存在 idle 线程时，post 侧根本不存**（`increment` 只在"有人在睡"时才写）；
并且给出**两处 seq-cst fence** 的免死锁证明骨架（提交侧 `PushFence` / 停靠侧 `SleepFence`）。
Taskflow 的"当有活跃 worker 存在时，保持至少一个 worker 在做窃取尝试"是同一件事的另一种写法；
Go 的"自旋的 M 不超过忙碌 P 的一半"是它的 CPU 政策侧。

[^rayon746]: <https://github.com/rayon-rs/rayon/pull/746#issuecomment-624802747>

### 49.2 协议（三处不变量 + 与 rayon 的一处**有意偏离**）

| # | 提交侧（生产者） | 停靠侧（worker） |
|---|---|---|
| I1/I2 | token 入注入器（release）→ **`fence(seq_cst)`** → 读 `idle` → 读 `sleepers` | 退搜索区登记 → 登记 sleepers（seq_cst）→ **`fence(seq_cst)`** → **最后一次读注入器/deque** → `atomic::wait` |
| I3 | — | 搜索区登记必须**配平**（粘性登记，只在"进停靠协议"与"退出主循环"两处减） |

**决策**（`idle >= need` 才允许不写；`need` 见 §49.4）：

| 读到 | 动作 | 为什么安全 |
|---|---|---|
| `idle >= need` | **一个字节都不写** | 该 worker 退出登记（--idle）必在本次读之后 ⇒ 它的"最后一次读注入器"也在读之后 ⇒ 必然看到 token |
| `idle < need`，`sleepers > 0` | `bump epoch + notify_all` | 真有人在 futex 上等，没人替它读 |
| `idle < need`，`sleepers == 0` | 不写 | 其余 worker 正在执行；执行完回主循环必读注入器 |

**安全性骨架**（rayon README "Using seq-cst fences to prevent deadlock" 的 proof sketch，逐条落到我们的对象上）：
两处 seq-cst fence 必有一处在全序中靠前 —— `PushFence` 靠前 ⇒ push 对停靠侧的"最后读"可见（它不睡，
自己会领到）；`SleepFence` 靠前 ⇒ 登记 sleepers 对提交侧的读可见（提交侧看到 `sleepers>0` ⇒ 广播）。

**与 rayon 的一处有意偏离**：rayon 把 `[sleeping, inactive, JEC]` **打包进同一个字**（`AtomicCounters`）。
我们不能把 JEC（= 我们的 `wakeEpoch`）和被频繁写的线程计数同放一字 —— 我们的 futex 原语是
`std::atomic::wait(wakeEpoch, stamp)`，它**按值比较**：线程计数的任何写入都会让全部等待者立刻"被唤醒"
（虚假唤醒风暴）。所以这里只把"搜索区人数"独立成一条 cacheline，并固定读序（先 idle、后 sleepers）。
代价：x86 上 PushFence 是一条 `mfence`，而注入器的 push 本身是 locked CAS（已是全屏障）⇒ 这条 mfence
在 x86 上是冗余的**语义保险**，只为把协议写成模型要求的样子。

### 49.3 第一次实现（两个入口用**同一个**谓词"有 1 个登记中的人就跳过"）⇒ 游戏内 **2.2× 变慢**

`tools/gate-run/n13-wakepoll-ingame.ps1 -Reps 3`（对齐档，A-only，逐趟配对，同一会话）：

| 臂 | 整步 med | melee med | 22 s 内步数 | `parkWake` | 判定 |
|---|---|---|---|---|---|
| `p0` = `ENTJOY_WAKE_POLL=0` | 161.5 ms | 122.5 ms | 118 / 119 / — | 20 478–22 530 | 基线 |
| `p1` = `=1`（第一版谓词） | **434.3 ms** | **367.2 ms** | **55 / 55 / 54** | **1 392–1 790** | 2.2× 变慢，3/3 一致 |

`steps-check max|dSteps|=64 -> SUSPECT`，指纹窗口也不同（`步=96` vs `步=32`）。**这不是噪声，是稳定复现
的整机回归**，而且同一份代码在微基准上是**赢**的（并行 W=8 1.40×）。⇒ **微基准的"每次派发只需 1 个
worker"形状，不覆盖"一次派发需要 8 个 worker"的形态。**

### 49.4 根因：谓词必须带**需求**，而两个入口不是同一件事（分入口计数自证）

给 `[JOBWAKEPOLL]` 加分入口计数后，真实宿主一次 22 s 跑（对齐档）的分布是：

| 入口 | 决策数 | 说明 |
|---|---|---|
| `batch skips=37,638 wakes=12,048` | 49 686 | **游戏负载几乎全在 `SubmitBatch`** |
| `work skips=107 wakes=12` | 119 | `SubmitWork`（小 job 快路径）在游戏里几乎不用 |

一次**真并行趟**要 `batch->workerCount` 个 worker 才跑得动；第一版谓词"有 1 个登记中的人就跳过"
⇒ 一整趟只被 1~2 个 worker 拖着跑 ⇒ melee 120 → 315 ms。**修法**：`idle >= need` 才允许不写，

- 小 job（`SubmitWork`）：`need = 1`（一次派发只需 1 个 worker 推进）；
- 真并行趟（`SubmitBatch`）：`need = batch->workerCount`（与既有 §7ah 守卫同一个口径）。

这个谓词**比现有的 §7ah 守卫更保守**（跳过集是它的子集：`idle <= awake = W - parked`，
故 `idle >= need ⇒ awake >= need`）⇒ 唤醒只会更多、不会更少。修后单趟探针：
`steps-check ok`（88 vs 85 步）、整步 164.3 → 158.4 ms，且 `parkWake` 回到 19 637 vs 基线 19 439
（worker 群体行为恢复正常）。

### 49.5 第二次实现的关键细节：登记必须**粘性**（否则一半收益被自己的计数吃掉）

第一版在"每次进出搜索区"各写一次计数。连发形状下"领一个 job → 执行 → 回搜索区"每个 job 就要进出一次
⇒ **每个 job 两次 `lock xadd` 打在同一条共享行上**，实测 W=8 吃掉 0.11 µs/job（0.449 vs defer 0.335），
把收益从 1.88× 压到 1.40×。改成**粘性登记**（只在"进入停靠协议"与"退出主循环"两处减）后：

| 版本 | 并行 W=8 `on/off` | 串行 W=8 `on/off` |
|---|---|---|
| 非粘性登记 | 1.401× | 0.979×（无效应） |
| **粘性登记** | **1.816×** | **2.157×** |

⚠ 一个必须写对的细节：`drain_quit` 标签**每轮都会经过**（正常"没找到活"也落到那里），所以解锁登记
必须写在 `if (quit_)` **分支里**；写在外面会每轮清一次 ⇒ 退化成"每 job 一次 RMW"。

### 49.6 实测：微基准（`tools/gate-run/n12-wakepoll-ab.ps1`，1000 空 job，6 趟配对，median of round-trips）

| kind | W | `off` | **`on`(N12)** | `defer` | `on/off` | `defer/off` |
|---|---|---|---|---|---|---|
| parallel | 1 | 0.372 | **0.368** | 0.306 | 1.011 | 1.216 |
| parallel | 2 | 0.474 | **0.392** | 0.314 | 1.209 | 1.510 |
| parallel | 4 | 0.513 | **0.380** | 0.318 | 1.350 | 1.613 |
| parallel | 8 | 0.596 | **0.345** | 0.330 | **1.728** | 1.806 |
| parallel | 16 | 0.730 | **0.361** | 0.393 | **2.022** | 1.858 |
| serial | 1 | 0.417 | **0.416** | 0.423 | 1.002 | 0.986 |
| serial | 2 | 0.525 | **0.425** | 0.546 | 1.235 | 0.962 |
| serial | 4 | 0.795 | **0.419** | 0.784 | 1.897 | 1.014 |
| serial | 8 | 0.904 | **0.419** | 0.897 | **2.158** | 1.008 |
| serial | 16 | 1.012 | **0.426** | 1.020 | **2.376** | 0.992 |

三条读法：
1. **`on` 的曲线在 W=1..16 上基本是平的**（并行 0.372 → 0.361，串行 0.417 → 0.426）—— 这正是这条轴
   从一开始想要的性质（Unity 同形状也是平的：0.322 @8w / 0.371 @1w）；
2. `off` 与 `defer` 都随 W 单调恶化，**W=16 处 `on` 已超过 `defer`**（并行 0.361 vs 0.393）；
3. `defer` 在**逐个 round-trip** 形状上没有收益（1.008），`on` 有 2.158× ⇒ **`on` 严格更通用**：
   它不要求调用方"先连发多个 Schedule 再 Complete"。

**生效证据**（`wakePollSkipsWakes`，每次进程 6 个探针样本）：并行 W=8 `3 316 966 / 3 035` = **99.91%
的派发一个字节都没写**；串行 W=8 `3 319 912 / 89` = 99.997%。

### 49.7 实测：游戏内对齐档（3 趟配对，A-only，`tools/gate-run/n13-wakepoll-ingame.ps1`）

`steps-check max|dSteps|=3 -> ok`；6 次运行的 `[M-20]` 指纹窗口**都落在同一步**（`步=96`），
`CountsSum` 995 864–995 915（±50 ppm，本项目已知的不可比特复现），⇒ 两臂做的是同一段模拟。

| pass | `p0` 中位 | `p1` 中位 | 配对中位 Δ | 臂更快趟数 |
|---|---|---|---|---|
| zero | 0.128 | 0.122 | −0.001 | 2/3 |
| count | 0.995 | 1.000 | +0.006 | 1/3 |
| pp | 0.039 | 0.040 | +0.001 | 1/3 |
| pf | 0.193 | 0.184 | −0.008 | 2/3 |
| place | 1.886 | 1.826 | −0.057 | **3/3** |
| build 段 | 3.550 | 3.490 | +0.030 | 1/3 |
| flow | 30.850 | 30.770 | −0.250 | 2/3 |
| **melee** | **121.260** | **119.830** | **−1.390** | **3/3** |
| integ | 3.220 | 3.120 | −0.120 | 2/3 |
| **整步** | **159.640** | **158.130** | **−1.510** | **3/3** |

⇒ 对齐档整步 **−1.51 ms / 159.6 ms ≈ −0.95%**，三个 pass 是 3/3 稳定更快（整步、melee、place），
其余在噪声带内；**系统负载读数没有上升**（`p1` 的 `parkWake` 19 890–21 939 vs `p0` 20 516–20 610，
`load` 读数 1.76–3.04 vs 2.40–3.20 cores）。

⚠ 注意：这里的收益**不是** §46.4 那个"≈0.07%/步"的上界所能覆盖的。那个上界只算了
`SubmitWork` 的**每条派发一次写**；N12 在游戏里真正省掉的是 `SubmitBatch` 侧的 76% 广播
（`batch skips=39 365 / wakes=12 367`），其中被省掉的每一次原本都要把 `wakeEpoch` 那条 cacheline
从 W 个共享者手里抢回来 —— 生产者自己一次、以及 W 个 worker 自旋轮询各一次失效。

### 49.8 实测：游戏内对齐档 vs Unity（同一会话，每个臂各自一趟相位对齐的 Unity 跑）

`tools/gate-run/n13-wakepoll-ingame.ps1 -Reps 3 -WithB`（B = Unity W0Player，`M4_WARMUP/M4_STEPS`
对齐到该臂 A 的同一窗口）。比值 = B/A，**>1 表示 EntJoy 更快**：

| pass | `p0`（基线）B/A | `p1`（N12）B/A | 方向 |
|---|---|---|---|
| zero | 0.843 | 0.755 | A 更拉开 |
| count | 0.908 | 0.859 | 更拉开 |
| pp | 1.296 | 1.324 | 更拉开 |
| pf | 0.777 | 0.718 | 更拉开 |
| place | 0.934 | 0.970 | 更拉开 |
| build 段 | 0.816 | 0.844 | 更拉开 |
| flow | 1.037 | 1.054 | 更拉开 |
| **melee** | 1.012 | **1.034** | 更拉开 |
| integ | 0.827 | 0.838 | 更拉开 |
| **整步** | **1.015** | **1.036** | **更拉开** |

- A 的整步中位数：**156.67 → 153.93 ms（−2.74 ms）**；B 的两臂中位数几乎相同（159.05 / 159.49 ms，
  差 0.3%）⇒ 比值的改善基本全部来自 A 自己变快，不是 B 漂移。
- **9/9 个 pass 的方向一致**（没有一个反向）。
- ⚠ 但这次带 B 的会话 **`steps-check max|dSteps|=37 -> SUSPECT`**（`p0-r1` 只跑了 90 步，被第三方负载
  拖住），按本项目纪律**整步的配对差值不能用**；可用的读法是"每个臂自己的 B/A 比值 + 9/9 方向"。
  **主证据仍是 §49.7 那张 A-only 表（`steps-check ok`，整步 3/3 更快 −1.51 ms）**，本次带 B 的会话
  只作方向性佐证。

### 49.9 结论、代价与未做

- **已提为默认开**（`ENTJOY_WAKE_POLL=0` 关闭；与 F1/F2/F4/F5/F6 同一惯例），理由：微基准两种形状
  全胜且变平、游戏内对齐档 3/3 更快、负载不升、native 9/9 两个状态各 3 遍全过、基准自带的
  20 万次 `Schedule+Complete` 存活/泄漏判据也过。
- **代价 / 残余风险**：谓词要求"登记人数 ≥ 本次派发需要的 worker 数"。若将来有入口**低报**需求，
  后果是**少唤醒**（延迟问题，不是丢任务：登记者的下一次读注入器必然在 push 之后）。正确补丁是
  rayon 的级联（`work_found → wake_any_threads(min(sleeping,2))`，即"找到活的人顺手叫醒 1~2 个"），
  **本轮没做** —— 需求感知谓词已覆盖当前两个入口，但那是低报需求时的下一步。
- **移植性**：协议要求两处 `fence(seq_cst)` 与计数用的 seq-cst RMW 严格配对。x86 上 fence 与注入器的
  locked CAS 冗余；换 ARM 时应保留 fence 并复核 `injector_.IsEmpty()`（relaxed 读）与 fence 的相对顺序。
- **记账**：这条轴此前共否证 7 个候选、只留下一个有前提的 defer（§48.4）。N12 是**第一个既不需要
  调用方配合、又同时赢下"连发"与"逐个 round-trip"两种形状**的形态；代价是两处 fence 与一条新的
  计数不变量（I3），以及 §49.3 那次 2.2× 回归所换来的教训：**"同一条唤醒策略套到所有入口"是错的**。

### 49.10 落地后的两处自我审计（都改在 N12 内部，另行提交）

**① 诊断计数器本身违反了本改动的前提（结构性缺陷）。** 第一版 `WakePollDecideAfterPush` 在**每条派发**上
做 2~6 次 `g_wakePoll*.fetch_add(relaxed)` —— 那是 `lock xadd` 打在一个**全局**行上，多个生产者线程
（主线程 + 嵌套派发的 worker）还会互相争。这正是"生产者不要再写共享行"要消除的东西，只是换了个名字。
改法与项目既有的认领探针一致：**thread_local 累加，每 1024 次合并一次**；worker 退出主循环时 flush；
读取侧（`JobSystem_GetWakePollCounters` / `[JOBWAKEPOLL]` 打印）先 flush 本线程尾巴。
（说明：这是**结构性**修正，不声称提速 —— 它的量级低于本方法的噪声底 ±4%。）

**② 唤醒需求的口径从"上限"改为"真实令牌数"。** 第一版 `need = batch->workerCount`，那只是**上限**；
而 `SubmitBatch` 里的 `tokenCount = min(workerCap, workerCount_, tileCount)` 才是这一趟**真正会发布的
令牌数**，也就是它真正需要的并行度（一趟只有 k 个 tile 时，唤醒超过 k 个 worker 是纯浪费）。
改成 `need = tokenCount` 后，谓词**比既有 §7ah 守卫更准确**，且仍然保守（`idle <= awake` ⇒ 跳过集更小）。
同一会话配对 A/B（`n13 -WithCapArm`，臂 p2 = `ENTJOY_WAKE_POLL_NEED=cap`）：

| 读法 | 结果 |
|---|---|
| `step-check` | p2 干净（max\|dSteps\|=1），**p1 被 r1 的位置效应污染**（max\|dSteps\|=30，p0-r1 只跑了 92 步）⇒ p1 的配对差值不可用 |
| 分入口计数（真正可用的读数） | p1 `batch skips 37.3k/38.7k/37.9k, wakes 13.8k/13.5k/13.3k` vs p2 `37.6k/38.1k/39.5k, wakes 13.6k/14.2k/12.7k` ⇒ **两臂无系统差异** |
| p2 的 A-only 配对 | zero/count/pf/place **3/3 更快**，整步 −0.64 ms（2/3） |

⇒ **结论：这个口径修正对本宿主是 no-op**，原因是几何：对齐档里 K64 的 4 个内核 tileCount ≈ 长度/64，
K1 的 11 个内核 tileCount ≈ 长度 ⇒ 两者都远大于 8，`tokenCount` 恒等于 `workerCount`。
保留它的理由是通用性（小数组 / 大内批时 `tileCount < 8`，此时"唤醒超过 tile 数的 worker"确实纯属浪费）
与"更准确且仍保守"这两个性质，而不是本宿主的分数。`WakeLivenessTests` 的形态 B 也复现了这一点
（`batch skips=0 wakes=54`：该形态刻意先等全体停靠，`need=8` 必然走慢路径）。

### 49.11 新增回归测试：丢唤醒必须以**失败**而不是**挂住**结束

`tests/NativeDll.Tests/WakeLivenessTests.cpp`（已进 CMake 与 `run-native-tests.ps1`，共 10 套件）。
动机：本协议的失效模式不是崩溃而是**丢唤醒**（令牌进了注入器、提交侧却判定"有人会自己领到"而
一个字节都没写）—— 表现为 `Complete()` 永久阻塞。现有 9 套件都在"worker 已经醒着"的连续形态里跑，
**恰好绕过了唯一必须走慢路径的时刻**。该测试用 `RunWithTimeout`（超时 → 打印 outstanding/计数器 →
`DumpState` → `abort`）把死锁变成 rc≠0，并制造五个形态：

| 形态 | 内容 | 判据 |
|---|---|---|
| A | 每轮先 `WaitAllParked(8)`（确认 `parked==8` 且 `idlePollers==0`）再派发单个小 job | 该趟**必须**走慢路径（`wakes` 必须增加）；12/12 轮通过 |
| B | 全体停靠 + `ScheduleParallelForBatch(4096, 64)` | 4096 个 index **恰好各执行一次**；6/6 轮通过 |
| C | 先把池子叫热再连发 3×300 | `skips>0`（证明快路径真的被走到，否则测试会假通过） |
| D | 8 个长 job 占满 worker 后再发一个小 job（"登记中的人都在执行"） | 必须在超时前完成（不得搁浅） |
| E | 150 次随机形状（连发/round-trip/批）+ 随机空隙 0–3 ms | 每次批的 index 恰好一次 |

两个开关状态都 PASS（`skips=1597 wakes=176` / OFF 时走基线路径同样全过），单次 ~1.0 s。
**没有发现丢唤醒**；同时把 `DumpState` 补上了 `idlePollers=`/`parked=`/`wakePoll=` 三个字段 ——
丢唤醒的诊断全在这两个数上（此前 dump 里没有）。

### 49.12 还没做 / 已知冗余（留给下一轮，避免被当成"已优化"）

1. **PushFence 在 x86 上是冗余的**：注入器的 push 本身是 locked CAS（已是全屏障），所以那条 `mfence`
   只为把协议写成模型要求的样子。去掉它可省 ~10 ns/派发（约连发形状 per-job 成本的 3%），但会把
   正确性押在 `MPMCInjector::Push` 的内存序实现细节上 —— **未做，也未测**（要动代码 + 重建 + 重测）。
2. **rayon 的级联**（`work_found → wake_any_threads(min(sleeping,2))`）：需求感知谓词已覆盖当前两个入口；
   只有在"某入口低报 need"或"长尾延迟"被实测到时才需要它。
3. **`SubmitWork` 的 `need` 恒为 1** 是本轮的有意简化：该路径的 `RangeTask` `batch==nullptr`，由**一个**
   worker 跑 `RunWorkTask`，语义上不可能并行 ⇒ 1 是精确值，不是低报。

## 50. 缺陷专项：按"声明了但在实际配置下从不触发"这一类去查（2026-10-03）

### 50.1 方法（照抄本项目历史上真正抓到 bug 的那条路）

本项目此前抓到的**真缺陷**几乎全是同一个类：**路径声明了、在测试里也被当成"已生效"，但在产品配置下
从不触发，且因为没有计数而长期没人发现**。已记录的先例：

| 先例 | 形态 | 出处 |
|---|---|---|
| JCC 槽位碰撞 | 15 个内核只落 4 槽 ⇒ 学习表被互相踩，`slotHash` 校验读到 0 = 无样本 | §?/08 §(c) |
| F6 只读细 EWMA | 表/强制档只写**粗**样本 ⇒ `cost` 恒 0 ⇒ 该档认领几何永不按 job 变 | §? |
| `IJob` 绑定漏接线 | 走托管 thunk ⇒ **原生内核是死代码** | §22.6/§23 |
| `Prepare()` 从未被调用 | build-server 清理是死代码 | §36.4 |
| JCC 自选一个大 chunk | 融合永不发生 ⇒ 测试必须用 `ENTJOY_FORCE_INNER_BATCH=1` 绕过 | 测试脚本注释 |

所以本轮不做"我觉得哪里可能有问题"，而是**机械地查证据**：
1. 枚举全部 55 个 `ENTJOY_*` 开关与默认开的优化；
2. 对每个找"生效证据计数"，检查**在产品路径上是否为 0**；
3. 带全套诊断跑一次真实宿主，逐条读计数（`[E1]`/`[JOB*]`/`[JCC-DIAG]`/`[JOBBATCHTBL]`）；
4. 对"重复的两条 Schedule 路径"做**字段奇偶校验**（同一个配置字段是否两条路都写）。

### 50.2 修掉的两个（都属"诊断撒谎 ⇒ 结论无效"这一类）

**① `[JOBPHYS] workerThreads=` 恒为 0。** `Shutdown()` 在**打印之前**执行 `g_numThreads.store(0)`
（`JobSystem_Scheduler.cpp:428`），而那一行打印（:471）读的就是它 ⇒ 无论 8 个还是 16 个 worker，
这一行永远写 `workerThreads=0`。讽刺的是这一行**存在的意义**就是"便于判定 A/B 臂是否真的生效"
（注释原文），却带一个恒假字段。修复：关停前取快照再打印。
**实测前后**：`workerThreads=0` → **`workerThreads=8`**。
顺带查了它是否有**功能性**后果：`SubmitBatch` 用的是 Schedule 时刻已经写进批里的 `workerCount`，
关停期的 `FlushPendingSubmits` **不重新解析** worker 目标 ⇒ 只影响诊断，不影响关停期的行为（已核实）。

**② `[JOBWAKE] notify_all skipped=0` 在默认配置下是"死计数"。** `ENTJOY_WAKE_POLL` 默认开之后，
§7ah 旧守卫整段不再执行 ⇒ 这个计数恒为 0，而真正的跳过数（同一趟 4 万级）在下一行 `[JOBWAKEPOLL]`。
不标注的话会被读成"跳过守卫从不触发"，与事实相反。修复：在该行直接标注"legacy 路径专用，wakePoll=ON
时无效"。

### 50.3 补上的一个关键生效证据：**F5 融合此前没有任何计数**

`ENTJOY_TILE_RUN`（F5，**默认开**）把连续等宽 tile 块合并成一次内核调用，可是**全代码库没有任何计数
证明它真的发生过** —— "融合有没有生效"只能从墙钟反推，这正是上表那一类的温床。已补三个计数
（`g_tileRunFusedRuns/Tiles/MaxRun`）并在关停时打印 `[JOBTILERUN]`。**实测（同一台机器、8 worker）：**

| 档 | `fusedRuns` | `avgRun`（tile/次） | `maxRun` | 折算元素/次 |
|---|---|---|---|---|
| **默认档**（无批表，JCC 自选 ≈1953 元素/tile，capEff=4） | 201 561 | **3.82** | 6 | ≈ 7 460 |
| **对齐档**（表 64/1，薄 tile 走 F1 ⇒ span 1024） | 2 786 503 | **249.47** | 1024 | `:1` 内核 1024 元素/次；`:64` 内核 4×64 = 256 |

⇒ **F5 在两档都是活的**（不再是假设），而且融合宽度现在可观测：`avgRun` 恒为 1 就说明融合退化成 no-op，
将来任何一次静默失效都会被这一行抓住。（对齐档的 249 是"tile 数"平均，被 11 个 `cs=1` 内核拉高；
按**元素**看它是 1024，正好落在 §31 测出的最优区 1024–2048 内 —— 所以这不是过融合。）

**F6（`ENTJOY_CLAIM_ADAPT`，默认开）同样此前没有计数**，而它的判据链有**三处可以静默退化**的关口：
键为 0（表/强制档）、**没学到成本样本**（← 这正是本项目栽过的那个坑：JCC 槽位碰撞 / 只写粗样本而只读细
EWMA ⇒ `cost` 恒 0 ⇒ 几何永不按 job 变）、迟滞永不翻状态。已补 5 个计数并打印 `[JOBF6]`。**实测
（默认档，8 worker）**：

| 计数 | 值 | 读法 |
|---|---|---|
| `nokey` | **0** | 每次调用都有真实的 per-job 键（没有被表/强制档旁路） |
| `nosample` | **15** | 占有效调用 15/55 426 = **0.03%** ⇒ **"成本恒 0"的失效模式没有发生**（若是那个坑，这里会≈全部） |
| `sliced` / `interleaved` | 1 847 / 53 564 | 两种决策都在发生（默认以交错为主） |
| `flips` | **149** | 自适应**真的动过** 149 次 |

⇒ F6 在产品配置下**确实是活的**，而且"静默无样本"这一失效模式现在可量化、可告警（`nosample ≈ n` 即为回归）。

**F2（`ENTJOY_TILES_UNIFORM`）/ F4（`ENTJOY_TILE_FASTPATH`）同样没有分支计数**，而它们承载着**本项目
单次最大的一笔已记录收益**（§18：F2+F4 提默认后对齐档整步 −13.90 ms）。两者都受 `thinTiles`
（`cs ≤ 16`）门控，而按 §18.4 的措辞很容易**误以为**"默认档 = 厚 tile ⇒ F2/F4 恒不生效"。补上计数后
**实测直接把这条信念推翻了**：

| 档 | `uniformTiles applied` | `tileFastPath applied` | 占该趟批数 | 说明 |
|---|---|---|---|---|
| **默认档** | **4 037** | **4 037** | ≈ 7%（56 637 批） | **不是 0** —— 承载"默认档恒不生效"这个信念的推断被推翻 |
| 对齐档 | 45 556 | 45 556 | ≈ 80% | 11 个 `:1` 内核都是薄 tile |

⇒ 两个开关在**两档都生效**，只差占比。⚠ 但这 4 037 批的**来源尚未定位**：候选是首见 dump 里
`N=64 tiles=4 ⇒ cs=16` 的那类小内核，**未验证**（`[JCC-DIAG] chunk[2^k]` 数的是 **tile 数**而不是 `cs`，
不能用它反推 —— 我第一版就是这么写错的，已改）。这条"我以为它该是 0、计数告诉我不是"的过程本身就是
§50.1 那个方法论的正当性证明；同时它修正了一处**写在注释里的错误信念**（已同步改掉
`JobSystemInternal.h` 与 `JobSystem_Scheduler.cpp` 的四处措辞）。判据留下：薄 tile 占比骤降为 0 即为回归。

### 50.4 查了但**没有**问题的（负结果也要记账，避免下一轮重复劳动）

| 审计对象 | 结论 |
|---|---|
| **`BatchStorage` 复用的陈旧字段**（代码注释自己警告过这一类） | **安全**：`ReleaseBatchStorage` 做 `destroy_at` + placement-new 整对象重建，且 `AcquireBatchStorage` 命中缓存后**仍**复位 6 个标记（`uniformTileSize/fuseTileSize/fuseRuns/claimCapOverride/claimGeomOverride/claimSpanOverride`）⇒ 两条路都不会继承陈旧值 |
| **`JobSystemStatsNative` 的 ABI 布局防御**（不同步 ⇒ C# 越界写 = 堆损坏） | **已强制**：`ValidateStatsLayout()` 在 `Initialize` 里比对 `GetStatsSize()` 与 `Marshal.SizeOf`，不等就抛；运行中的游戏即证明当前两边一致 |
| **重复的两条 Schedule 路径的字段奇偶** | 7 个共享配置字段（`funcHash/jccFine/totalElements/tileFast/uniformTileSize/fuseTileSize`）在两条路上**都**被赋值；`fuseRuns` 只在 chunk/entity 路（与 `ChaseLevExecuteTileRun` 的两个分支对应，设计如此） |
| **`claimGeom` API 参数是否被丢掉**（"声明了但不生效"） | **没丢**：`Exports.cpp` 参数 → `ScheduleParallelForBatch` 的 `declareGeom` → `batch->claimGeomOverride` → `ShouldUseSpreadGeometry`。批表第 4/5 字段同理（且游戏全部 15 个内核都走 Batch 路 ⇒ 与 `ab-aligned` 的 `:s` 声明一致） |
| `hit=` 字段（`JOBBATCHTBL` 首次出现打印 `hit=1`） | **不是 bug 但是命名陷阱**：它是"第几次见到这个 key"，不是"批表命中"。已核对了全部 11 个解析该行的脚本：**都只用 `applied=`**（诚实的那个字段）⇒ 无实际风险，未改 |

### 50.5 仍开着的（量化了，但不是缺陷）

`[E1] busy_ratio=0.84`（默认档）：约 **16% 的 worker 时间不在 tile 执行里**，而退役链只占 0.06%
（1.67 µs/批 × 56 637）⇒ 差额主要是**自旋等待下一批**（`hotSpin=423 121`）。这是本项目已量过的
自旋策略权衡（`ENTJOY_SPIN_NEEDS_WORK` 就是为此而加），而且**同一份自旋正是 N12 能跳过唤醒的前提**；
§42/§46 已实测"调自旋旋钮"对整步无正收益 ⇒ 记为"已知权衡"，不作为待修缺陷。


## 51. 对齐档赤字结账（2026-10-03）：**落后的只有 2 个段，且都是宿主侧**；整步已经反超

### 51.1 逐趟账（对齐档，N12 已开；`n13 -WithB` 各臂各自对齐一趟 Unity）

比值 = B/A，**>1 = EntJoy 更快**；min 是本项目的主统计量：

| pass | A min | B min | B/A | A−B (ms) | 判定 |
|---|---|---|---|---|---|
| zero | 0.108 | 0.089 | 0.827 | **+0.019** | A 落后 |
| count | 0.964 | 0.823 | 0.854 | **+0.141** | A 落后 |
| prefixPartial | 0.036 | 0.047 | 1.297 | −0.011 | A 领先 |
| prefixFinal | 0.185 | 0.133 | **0.715** | **+0.052** | A 落后（**相对差最大**） |
| place | 1.658 | 1.624 | 0.980 | **+0.034** | 近平 |
| **Build 段**（= 上 5 项 + hostRewrite + 残差） | 3.190 | 2.704 | **0.848** | **+0.486** | **A 落后** |
| **Flow** | 30.170 | 31.880 | 1.057 | **−1.710** | A 领先 |
| **Melee** | 116.370 | 119.582 | 1.028 | **−3.212** | **A 领先（主项）** |
| MarkDead | (A 计入段内 0.5) | 0.868 | — | −0.34 | A 领先 |
| **Integrate** | 2.970 | 2.495 | **0.840** | **+0.475** | **A 落后** |
| **整步** | **153.660** | **159.220** | **1.036** | **−5.560** | **A 领先 3.6%** |

**分解闭合性（必须说清，否则容易把不同 rep 的 min 相加）**：同一 rep（r2）的段级配对是
`Build +0.55 / Flow −1.60 / Melee −3.99 / MarkDead −0.34 / Integrate +0.53`；A 的五个段加起来与
whole 只差 **0.21 ms**（0.14%），而 **B 差 1.69 ms**（1.06%，B 的宿主在段外还有活）。
⇒ 逐趟**比值**用 min（主统计量），而**差额归因**要用同一 rep 的配对，两者不可混用。

### 51.2 为何落后：三层，且**没有一层落在"调度器"上**

| 层 | 段 | 证据（本项目已做过的实验） |
|---|---|---|
| ① **每元素体代价（宿主决定）** | **Integrate** +0.475、**Build 段** +0.486 | §31 **空内核对照**证明框架本身 **6.5 vs Unity 7.0 ns/工作项 = 打平** ⇒ 差价在工作、不在调度；§40.1 在**匹配调用粒度**（`cap=1` = 64 元素/次 = Unity 的 `innerloopBatchCount`）下 Build 仍慢 27%、Integrate 仍慢 19%，粗化 4× 只补回 ~7 / ~2 个点 ⇒ **每元素是主项，调用前导是少数项**；§41 D3：Integrate 环 A 420 指令/164 访存 vs B 399/160 **几乎相同**，唯一成立的约束是**寄存器压力**（33 活跃指针 vs ~9 GPR），而它由**宿主字段数**决定 |
| ② **不可比的段** | Integrate 的一部分 | B 的 `BattleBenchM4.cs` 文件头**自己声明该段不可比**（dump 只给 position/alive/state/team/hp，缺 vel/knock/af/stuck）⇒ 那部分差额**不可归因** |
| ③ **小 pass 的"每次调用前导"**（唯一还可能有框架侧空间的） | prefixFinal +0.052、place +0.034、zero +0.019、count +0.141 | count 的有效代价在 08b §2.2 已分解为"共享原子 ≈0.36 + 非原子尾 ≈0.1–0.2"（两侧同形，Unity 的 asm 也是 `lock incl`）；**但 prefixFinal 只有 0.185 ms 却排到相对差最大的 0.715** —— 它的元素数极少（`N=64` 级），暗示**它的成本由"每次内核调用/每批的固定开销"主导**，而那正是框架侧的东西（⚠ 这是**推断，尚未测**：见 §51.3 第 1 步） |

### 51.3 优化方案（按"证据强度 × 可动性 × 上限"排序）

**第 1 步（先做测量，不改代码）：给每个 pass 加"调用数 / 元素数"计数。**
现在的 `[M-19]` 只有 ms，没有分母 ⇒ 无法区分"每次调用贵"与"每元素贵"。有了分母就能一次性裁决：
- `prefixFinal` / `zero` / `place` 若**每次调用 ~2–3 µs、元素数极少** ⇒ 是固定开销主导 ⇒ **框架侧有空间**
  （手段：把这类小 pass 合并成一次调用、或用已有的 `claimSpanOverride` 声明更大元素跨度以减调用数）；
- 若是**每元素 ns 级** ⇒ 与 Integrate 同层（宿主侧），直接关闭，不再花时间。
上限：这 4 个小 pass 合计 **0.246 ms/步（0.16%）**；乐观回收一半 ≈ **0.12 ms/步（0.08%）**。

**第 2 步：Build 段那笔"没有名字"的开销（~0.1–0.6 ms，中位 ~0.2–0.35）先做得可测。**
现状：`[M-1] Build` 与 `[M-19]` 六趟之和的差在 15 次运行里散布 0.04–0.82，**散度 ≈ 均值** ⇒ 用现在
两行分别平均的量根本量不准（§50 已记）。要动它，先把 pass 边界做成**同窗口逐步配对计时**。
上限 0.2–0.35 ms/步（0.15–0.23%）。

**第 3 步：定位 50.4/§50.3 里那批"来源未定位"的薄 tile 批（默认档 4 037 批）。**
若发现**大内核**因 JCC 学习到的 cs 漂移而落进薄 tile 路，那才是真问题（会同时改变 F2/F4/F5 的行为）；
一次跑就能定位（按 key 分桶）。**这是目前唯一"已知异常但机理未定"的线索。**
> ⚠ **2026-10-04 已结案（§58）**：机理 = **门与 JCC 的硬编码下限撞值** —— `kClaimSpanThinElems = 16` 恰好等于
> `ResolveChunkSize` 五处 `std::max(16, …)` 的下限 ⇒ `cs <= 16` 实际是"**`cs` 撞在下限上**"，**不是某个小内核**；
> 撞下限的最短路 = 空体/短 body 被分类成 **mem-bound** ⇒ `tpwChunk = max(16, ceil(len/(W·tpw)))` ⇒ **`len ≤ 16·W·tpw`（默认 8192）时恒 16**。
> ⚠ 且"进不进那个 regime"**本身随进程翻**：同一形状同一 `length`，会话内既见 6/6 恒薄、也见 2/3 不薄（§58.2b）。
> ⇒ 原判据"薄 tile 占比骤降为 0 即为回归"**作废**（它既不是薄厚判据，也不是 `length` 的稳定函数）。

**第 4 步（可选）：x86 上删掉 PushFence**（≈10 ns/派发 ≈ 派发受限形状 per-job 的 3%），
但它把正确性押在 `MPMCInjector::Push` 的内存序实现细节上 ⇒ 只在愿意接受该耦合时才做。

**已关闭、不要再开**：把 Integrate/Build 的每元素代价当框架问题（§31/§40/§41 + 你的"不改测试端"约束）；
靠调自旋旋钮（§42/§46）；靠 poke-one / skip-awake / claim 切片 / 值绑定 / 标量限制（§46/§48）。

### 51.4 为什么"还是没追上 Unity"这个说法需要更正

**对齐档整步已经反超：B/A = 1.036（A 快 3.6%），默认档 1.109–1.110（A 快 ~11%）。**
"落后"只发生在**两个段**上（Build 段 +0.486、Integrate +0.475，合计 **0.96 ms = 0.6% 的步**），
而 A 在 Melee（−3.21）与 Flow（−1.71）上合计领先 **4.92 ms**。也就是说：

- 用户体感"某个 pass 比 Unity 慢"是**真的**（6 个 pass 的比值 < 1）；
- 但它们**加起来只有 0.96 ms**，而两个大段的领先是它的 5 倍；
- 且这 0.96 ms 里，Integrate 的一部分被 B 自己声明为**不可比**、其余被 §40/§41 归因到**宿主字段数决定的
  寄存器压力**（框架改不了，且改动测试端是你明确排除的）。

⇒ 结论：**对齐档这一轮的活已经干完了**（N12 把最后一条调度轴也收了，贡献 +2.1 个点）；
剩下的是"能不能把宿主侧内核也写得比 Unity 快"的问题，而那不在框架的职责边界内。
再做，收益上限是 **~0.3 ms/步（0.2%）**，全部来自 §51.3 的第 1/2 步。


## 52. 取证：把"每趟的宿主 ms"拆成**内核真干活**与**框架开销**（2026-10-03）

### 52.1 补齐缺失的分母（§51.3 第 1 步，**放在框架侧**，不动测试端）

新增按 **job 键**（`JobFuncKey` = 内核模块内 RVA，与批表/`jobkeys.txt` **同一键空间**）的计数：
`batches / elems / tiles`（Schedule 侧，提交线程）+ `calls / elemsCalled`（**内核调用**侧，worker）
+ `kernelNs`（**抽样 1/32** 次调用的内核自计时，把探针扰动压到 ~0.1%）。关停打印 `[JOBPERKEY]`。
调度开关：`ENTJOY_JOB_BATCH_TABLE_DUMP=1`（与 `[JOBBATCHTBL]` 同一个）⇒ 产品路径零开销。

⚠ **第一版仪器自己踩了本项目的经典坑**：键用了 `funcHash`，而它在"表/强制档 + `ENTJOY_CLAIM_ADAPT=0`"
（**正是对齐档**）时被**有意置 0** ⇒ 仪器在对齐档里静默失效（打印 0 行）。改用 `JobFuncKey` 后才生效。
—— 这本身就是"新加的诊断必须先在目标配置里证明自己非零"的又一例证。

**一致性判据（顺带是正确性证据）**：15 个内核全部 `mismatch = elemsCalled − elems` **= 0**
⇒ 认领 + F5 融合**没有漏执行/重复执行任何元素**（15/15）。

### 52.2 对齐档的完整成本模型（同一次 22 s 跑，8 worker，128 步）

| pass | 元素/步 | 调用/步 | 元素/调用 | 内核 ticks/元素 | 聚合内核 ms/步 | 宿主 ms/步 | 对账 |
|---|---|---|---|---|---|---|---|
| Melee | 1 203 125 | 1 175 | 1023.5 | 2688 | 850.6 | 94.5 | /8 = 106 ✔ |
| BfsWave | 820 372 | 4 410 | 186 | 417 | 90.0 | (含在 Flow) | — |
| FlowSeed | 845 152 | 825 | 1024 | 203.7 | 45.3 | — | — |
| **Integrate** | 1 203 125 | 4 701 | **256** | **105.6** | 33.4 | 3.66 | /8 = 4.2 ✔ |
| FlowGrad | 845 152 | 825 | 1024 | 115.8 | 25.8 | — | — |
| **Place** | 1 203 125 | 4 701 | **256** | **56.7** | 17.9 | 1.88 | /8 = 2.2 ✔ |
| **Count** | 1 203 125 | 4 701 | **256** | **37.4** | 11.8 | 1.23 | /8 = 1.5 ✔ |
| FlowPresence | 1 203 125 | 4 701 | **256** | 33.9 | 10.7 | — | — |
| Spawn / MarkDead | 1 203 125 | 1 175 | 1023.5 | 17.7 / 15.2 | 5.6 / 4.8 | 0.61(MD) | /8 = 0.6 ✔ |
| PrefixPartial / Final | **77** | 10 | **8** | 46 748 / 11 872 | 0.95 / 0.24 | 0.033 / 0.142 | ⚠ 样本仅 27/35 |
| ClearAll | 7 812 | 8 | 1023.5 | 117.8 | 0.24 | 0.104（"zero"） | ⚠ 见 52.5 |

**模型自洽**：聚合内核 ms/步 ÷ 有效并行度(≈8) 与宿主 `[M-1]`/`[M-19]` 的 ms/步吻合在 10–20% 以内
（Melee 106 vs 94.5；Integrate 4.2 vs 3.66；Place 2.2 vs 1.88；Count 1.5 vs 1.23；MarkDead 0.60 vs 0.61）
⇒ **这些 pass 的成本基本就是它们自己的内核**，框架的每趟开销是小项（不是 §51.2 层③ 猜想的那样）。

### 52.3 因果实验：元素跨度（span）vs 认领几何（geometry）

**span 剂量-反应**（`n16-span-aligned.ps1`，256 → 1024 → 2048 元素/次，3 趟配对，`steps-check ok`）：

| 指标 | base(256) | e1024 | e2048 |
|---|---|---|---|
| Count 宿主 ms（min） | 0.981 | **0.881** | 0.914 |
| Count 内核 ticks/元素 | 25.27 | 25.24 | 24.35 |
| Place / Integrate ticks/元素 | 48.81 / 84.77 | 49.71 / 84.70 | 47.55 / 81.94 |
| Count 配对 Δ（臂更快趟数） | — | **−0.058, 3/3** | −0.050, 3/3 |

⇒ **span 让宿主 ms 略降（count 3/3，≈−6%），但内核 ticks/元素几乎不动** ⇒ 省下的是**框架每次调用的固定
开销**（调用数 4 701 → 1 175，相当于每次调用省 ~16 ns），**不是**每元素代价。上限因此很小。

**geometry 剂量-反应**（`n17-geom-aligned.ps1`，Count/Place 声明 `:s`=Spread，3 趟配对，两臂 `steps-check ok`）：

| 指标（中位） | adj（共享游标） | spr（Spread） | spr_span（Spread+span） |
|---|---|---|---|
| **Count 内核 ticks/元素** | 25.00 | 24.09 | **23.91（−4.4%）** |
| **Place 内核 ticks/元素** | 49.03 | 45.78 | **46.35（−5.5%）** |
| **Integrate 内核 ticks/元素** | 83.17 | 78.60 | **79.94（−3.9%）** |
| Count 宿主 ms（min） | 0.882 | 0.914 | **0.846（−4%）** |
| Place 宿主 ms（min） | 1.619 | 1.590（−1.8%） | **1.476（−8.8%）** |
| Integrate 宿主 ms（min） | 2.970 | 2.910 | **2.800（−5.7%）** |
| Build 段配对 Δ | — | −0.080（2/3） | **−0.290（3/3）** |
| 整步配对 Δ | — | −1.000（3/3） | **−1.070（2/3）** |

⇒ **因果结论：认领几何能改变每元素代价（−4…−5.5%），而元素跨度不能。** 这修正了 §41 的一个隐含前提
（"每元素代价由宿主字段数决定、框架改不了"）：**"元素→worker 的分配方式"是框架可控的，且在
count/place/integrate 上值 4–5.5%** —— 与宿主源码自己的注释一致（`CPUBattleSpatialHash.cs`：
"Count/Place 每元素都对同一批计数器做原子 RMW ⇒ 共享游标的 cacheline 争用是主因，几何应为 Spread"）。

**但它不足以追平**：Count 23.9 vs Unity ≈21 ticks/元素（由 B 的 0.823 ms/步反推）、Place/Integrate 同理
⇒ 差额仍在**内核体**里。收益记账：geometry+span 合计回收 **~0.29–0.36 ms/步 ≈ 0.25%**，
Build 段赤字 +0.486 → ≈+0.20、Integrate +0.475 → ≈+0.31，**两段仍落后**。

### 52.4 结论：zero / prefixFinal / prefixPartial 与框架无关（用户的直觉是对的）

- **zero**：宿主"M-19 zero"那趟**不是** `ClearAll` 内核 —— `[JOBPERKEY]` 显示 ClearAll 整轮**只跑了 1 批**
  （1 000 000 元素，1 次），而宿主每步报 0.10–0.12 ms ⇒ 那是**宿主自己的清零**（memset 类），
  与 JobSystem 无关。§29 已证 A 的 memset 形态不比 B 的 AVX2 差。
- **prefixFinal / prefixPartial**：元素数极少（77/步）而调用 10 次/步、8 元素/次；它们的宿主 ms
  **两侧几乎相同**（A 0.142/0.033 vs B 0.133/0.028）⇒ 那是**两栈共用的宿主侧工作**，不是 A 的框架开销。
  （内核自计时在这两个键上只有 27/35 个样本，均值不可用 —— 需要改成 min/中位才有意义，已记入待办。）

### 52.5 缺陷（**EntJoy 侧，本轮未修**）：`ClaimPolicy` 在调用点**用不了**，且失败是静默的

宿主源码注释（`CPUBattleSpatialHash.cs`，紧挨 Count/Place 派发）写明：
> `声明**暂时**只能从批表走（<key>:64:4:s），**不能**在这里加 ClaimPolicy 实参：transpiler 的调用点改写
> 只认 job.Schedule(len, batch[, dep]) 这个形状，多一个实参就掉回**托管回调** —— 实测 key 从"模块内 RVA"
> 变成堆地址、applied 15→12、place 2.2→5.3 ms。正确形态 = job 结构体上的 [ClaimGeometry(ClaimPolicy.Spread)]
> 属性由 transpiler 读并透传（下一步）。`

**代码层复核（我在 EntJoy 侧确认）**：`src/NativeTranspiler/Analyzer/Common/BindingsGenerator.cs` 为
`IJobParallelForBatch` 生成的原生重载集合是
`Schedule(this T job, int arrayLength, int innerBatchCount = 0, JobHandle dependsOn = default)`；
对 `claimGeom|ClaimPolicy|ClaimGeometry` 的 grep **零命中** —— 也就是说**生成的重载里没有几何形参**。
于是传了几何的调用点在 C# 重载解析里**绑不到原生重载**，静默落到托管 `JobExtensions.Schedule<T>`：
键从 RVA 变成堆地址（`[JOBPERKEY]`/批表全部对不上）、`applied` 15→12、该 pass 慢 2.4×。

⇒ 这是**本类缺陷的又一例**（公开 API 存在、原生导出存在、几何机制存在，但**调用点够不到**，且症状是
静默降级而非报错）。**修法**（供下一轮）：按生成器已有的"多形参重载"模式（`ScheduleWithWorkerCap` /
`ScheduleWithWorkerCapAndRangeSize` 就是这么生成的）补一个带几何形参、转调
`NativeJobScheduler.ScheduleParallelForBatchRaw(..., claim)` 的重载；更干净但更重的是宿主建议的
`[ClaimGeometry]` 属性 + 分析器透传。**本轮未改**（改了也无法在"不改测试端"的前提下端到端验证）。

### 52.6 缺陷已修 + 端到端验收（同轮完成）

**改动**（`BindingsGenerator.cs` 两处，最小侵入）：给 `isParallelFor` 的生成签名**末尾**加一个
`ClaimPolicy claim = ClaimPolicy.Auto`，并透传到 `ScheduleParallelForBatchRaw(..., dependsOn, claim)`；
扩展方法（调用点直接用的那个）同形。⚠ 两处参数顺序必须一致（第一版一侧加在 `dependsOn` 之前、
另一侧加在之后 ⇒ 生成物 CS1503：`JobHandle` 无法转 `ClaimPolicy`）。带默认值 ⇒ 既有调用点逐位不变。

**同时补上这条轴的生效证据**（此前**完全没有**：`[JOBBATCHTBL] geom=` 打的是**批表**的值，不是调用点
声明的值 ⇒ 用 API 传几何时根本无从证明它到了原生层）：
- 原生新计数 `g_claimGeomDecl{Spread,Adjacent,Auto}` + `[JOBGEOM] declared spread=/adjacent=/auto=`；
- 新导出 `JobSystem_GetClaimGeomCounters`（+ 托管 `TryGetClaimGeomCounters`，用 `TryGetExport`，老 DLL 安全）；
- 预置 bench 臂 `BENCH_CLAIM_GEOM=auto|spread|adjacent`，`EMPTYPROBE` 行带 `claimGeom=`。

**端到端验收**（`tools/BuildPassBench`，160 步并行形状）：**只有原生路径能写这三个计数** ⇒ 它同时证明
"调用点绑到了生成的原生重载"**和**"几何真的到了认领层"：

| `BENCH_CLAIM_GEOM` | `claimGeom=spread/adjacent/auto` | 判定 |
|---|---|---|
| `auto` | **0 / 0 / 14 000** | 未声明（默认） |
| `spread` | **14 000 / 0 / 0** | ✅ **`ClaimPolicy.Spread` 到达原生** |
| `adjacent` | **0 / 14 000 / 0** | ✅ |

且三臂 `PASS,IJobFor_native_coverage,each_index_exactly_once=1` 与 `native=True` 都在 ⇒ **没有**掉回托管回调
（那正是缺陷的症状）。回归门：`dotnet build EntJoy.sln`（CI 口径，`-p:ENTJOY_STARTUP=…`）**0 错误**；
CI 自带的 jobs-only 守门 `tests/JobsOnlyTranspilerCheck/check.ps1` **PASS**（生成物仍 16 402 B、
零 ECS 耦合）；native **10/10 套件 × 2 状态全过**。

**意义**：几何这条轴（在 count/place/integrate 上实测值 4–5.5% 每元素代价，见 §52.3）此前**只有用批表的人
拿得到**；现在普通调用点 `job.Schedule(len, batch, dep, ClaimPolicy.Spread)` 就能拿到，而且失败不再静默
（`[JOBGEOM]` 直接可验）。


## 53. 通用机制叠加之后：落后**没有**解决，但边界被钉死了（2026-10-03）

### 53.1 叠加通用机制的效果（同一会话 3 趟配对，宿主实测）

叠加 = N12 唤醒（默认开）+ 声明 span `:e1024` + 几何 `:s`=Spread + `ENTJOY_ASSIST=1`：

| 指标 | 配对 Δ（stack − strict） | 趟数 |
|---|---|---|
| **Integrate** | **−0.180 ms** | **3/3** |
| prefixFinal | −0.022 ms | 3/3 |
| count | −0.034 ms | 2/3 |
| Build 段 | −0.100 ms | 2/3 |
| place / melee / 整步 | +0.108 / +2.87 / +2.30 | 1/3（整步 `steps-check SUSPECT`） |

**但 B/A 仍然 < 1**：integ 0.797 → **0.897**、Build 段 0.763 → **0.800**、count 0.792 → 0.820
⇒ **回落了约 1/3 的赤字，落后仍未解决**，且 `steps-check SUSPECT`（max\|dSteps\|=5）使整步不可用。

### 53.2 本轮最重要的发现：**对齐档的整步结论随机器状态翻转**

| 会话 | A melee | B melee | 整步 B/A |
|---|---|---|---|
| 上一轮（n13vsB p1） | 116.4 ms | 119.6–120.4 ms | **1.036（A 领先 3.6%）** |
| **本轮（n19 两个臂）** | 93.5–96.6 ms | **90.8–92.5 ms** | **0.978 / 0.949（A 落后）** |

**两栈同时快了约 25%** ⇒ 差别在**环境**（机器状态/热/背景负载），不在代码。⇒ 结论：
**"A 打平/领先"与"A 落后 2–5%"都是真的，取决于跑在什么状态的机器上**；单会话的整步 B/A 不能当
跨会话结论用（这正是本项目 §34 立的口径纪律）。**跨会话稳定的只有逐趟比值**：

- **稳定落后**：`count 0.79–0.86`、`Integrate 0.80–0.84`、`prefixFinal 0.62–0.72`、`zero 0.62–0.83`
  （都属"每元素体 / 宿主侧"，且 `zero` 那趟是宿主自己的 memset）。
- **稳定领先**：`melee 0.97–1.03`、`flow 0.94–1.06`、`prefixPartial 0.95–1.32`。

### 53.3 找到了"按值绑定"对最重要 job 类型**恒不生效**的根因（已修，默认不变）

`CppJobGenerator` 的按值绑定判据是"字段是否参与**循环行程数**"，而它**在 C# `Execute` 源码里找循环**。
但 `IJobParallelFor`/`IJob` 的 C# 是**逐元素**形态（`Execute(int index)`）——**源码里没有循环**，
循环是 transpiler 合成的 ⇒ `TripCount` 恒空 ⇒ 按值绑定（与 `SCALAR_RESTRICT` 的 InLoop 判据）
对宿主**最重要的 job 类型完全失效**。宿主生成物实测：`CountCellsJob_Execute.cpp` 六个标量全是
`const T& X = *X_ptr;`，而 `Counts_ptr[hash]` 的原子写让编译器**每元素重载**这些不变量。

修法：源码里没有循环 ⇒ 整个 body 视为循环体；新增 `ENTJOY_VALUE_BIND=4`（判据 = 行程数 **或**
**循环内条件字段** —— 后者必须占寄存器做比较，按值绑定不增加寄存器压力，而"只在循环体出现一次的
数据操作数"按值绑定反而要求它跨循环存活，历史实测会把 FlowClear/Integrate 拖坏）。
**验证**：生成管线确实按规则改输出（jobs-only 工程 mode 2 后 `const int& Delta` → `const int Delta`）；
**默认输出与 mode 3 逐位相同**（unset/3/4 三态对比）；回归门全绿。
**未验证**：mode 4 对宿主真实内核的收益 —— 宿主源生成器被 **Roslyn 增量机制跳过**（C# 输入未变 ⇒
管线不重跑，`NativeTranspiler_Generated/*.cpp` 时间戳不变；mode 2 同样无变化，故不是规则没生效）。
下一步：强制宿主重新生成（删 `NativeTranspiler_Generated/*.cpp` 或 `.godot/mono/temp` 后重建），
再用 `[JOBPERKEY]` 的**每键 ticks/元素**读 count/place/integrate 的前后变化。

### 53.35 mode 4 的验证结果：**对宿主内核无任何改变 ⇒ 这条杠杆关闭**（2026-10-03，实测）

强制宿主重新生成（该目录被 gitignore ⇒ 删掉 38 个 `*_Execute.cpp` 后重建，确认会重新生成）后三态对比：

| `ENTJOY_VALUE_BIND` | `CountCellsJob_Execute.cpp` 的标量绑定 |
|---|---|
| 未设（默认 = mode 3） | `const int& Length`、`const int& CellsW`、`const int& StateDeath` … |
| `=4`（新判据） | **完全相同**（仍然全部 by reference） |

⇒ **mode 4 在宿主真实内核上不产生任何代码变化**，因此**没有收益可测**。原因是我的假设错了：
生成物里的 `if (index < Length && …)` 那个 `Length` 守卫**是 transpiler 为逐元素 job 合成的**
（`IJobParallelFor` 的 batch 适配器加的边界检查），**C# 源码的 `Execute(int index)` 里并没有这个条件**；
C# 自己那些条件引用的是**局部变量**（如先取 `StateDeath`/`State` 到局部再比较），所以 `TakeConditions`
收集不到任何 job 字段 ⇒ 判据为空 ⇒ 不改变绑定。

> ⚠ **2026-10-03 更正：本节的两条归因都错了，mode 4 的结论作废（详见 §54.1/§54.2）**
> ① `if (index < Length && …)` **不是** transpiler 合成的 —— 它就写在**宿主 C#** 里
>    （`CPUBattleSpatialHash.cs:251` Count / `:338` Place；transpiler 合成的循环 `CppJobGenerator.cs:652`
>    是 `for (index = __startIndex; index < __startIndex + __count; ++index)`，**不含任何 length 守卫**）。
> ② mode 4 之所以"输出与 mode 3 逐位相同"，是 `ValueBindMode()` **根本没有解析 `4`**
>    （`v[0]=='4'` 落进 `return ValueBindDefault(3)` ⇒ `case 4:` 是**死代码**）⇒ **这条臂从未存在过**。
> 已修 + 已自证：临时条件字段 job 在 mode 3 下是 `const int& Limit`、在 mode 4 下是 **`const int Limit`**。
> ⇒ **"codegen 标量绑定形式"这条杠杆并未关闭**；宿主上的收益仍是**未测**状态。

**记账（这条要写清楚，避免下一次重复投入）**：
- §53.3 的**根因判断成立**（"在源码里找循环 ⇒ per-index job 恒空" 已由读码 + 生成物证实），
  修正**默认输出逐位不变**、也不会被误触发 ⇒ 作为基础设施保留；
- 但**"给条件字段按值绑定"在宿主内核上收益 = 0**（实测），因为那些字段根本不出现在源码条件里；
- 与之对照的历史实测（mode 2 = 全部按值）是**整步退化 +2.81 ms**（Integrate +9.8%、FlowClear +53.6%，
  栈引用 +34%）⇒ **按值绑定这条路在宿主内核上两头都不通**：
  该绑的字段不存在，全绑则退化。**结论：codegen 的"标量绑定形式"杠杆到此关闭。**

⇒ **框架侧可控的杠杆已全部度量完毕，且都已到边界。** 剩下的 2/3 赤字在宿主内核的**每元素数据访问
形态**（`count` 的散列原子 RMW、`Integrate` 的 ~20 数组访问 / 33 活跃指针），而"不改测试端"这一约束下
这一层不可动。

### 53.4 结论：落后的边界
1. **调度/派发/唤醒/粒度/几何/参数形状/绑定形式**这些**框架可控**的轴，已被逐条度量：能拿的都拿了
   （N12 1.6–2.4×、几何 −4–5.5% 每元素、span −6% 每调用开销、assist 见 §45），**合计约回收 Build/
   Integrate 赤字的 1/3**。
2. 剩下的 2/3 是**每元素体**：`count` 每元素一次**散列寻址的原子 RMW**（`INTERLOCKED_INCREMENT_AND_FETCH32
   (&Counts[cy*CellsW+cx])`）、`Integrate` 每元素 ~20 个数组访问 + 33 个活跃指针 ⇒ **内存延迟/寄存器压力**，
   而同一份 C# 在 IL2CPP 下每元素快 ~15–20%（指令数几乎相同，§41：420 vs 399 ⇒ 差在**动态**：
   停顿/ILP，不是指令数）。
3. ~~**要真正解决，必须动内核体**（数据布局/散列/分块），而那在宿主里——**你的约束"不改测试端"下不可行**；~~
   ⚠ **2026-10-03 更正（见 §54）**：这句话把三件事混成了一件。**数据布局/散列**确实在宿主；
   **"分块/循环形貌"与那道承重的 `index < Length` 谓词在框架侧**（transpiler 自己合成逐元素循环）⇒
   不动宿主也能解（形态 **K1′ + G**，§54.4）；且 §53.3 的 codegen 判据此前被判"收益为零"是**仪器缺陷**（§54.1/§54.2）。
4. **归因修正**：余下赤字按"每元素体"记没错，但**计数趟的对齐档赤字不是"每元素原子"**——
   隔离夹具同几何下 A 0.8437 vs Unity 0.8242（**1.05×**）、单线程成本两侧相同（§2）；
   Unity 的 count 与宿主**逐行同形**（都每元素一次 `lock incl`，08 §32.3），它快在
   **①没有 bounds 谓词 ②Burst `[ReadOnly]` 别名证明带来的载入合并**，不在计数算法（§54.6）。

---

## 54. 更正与重新划界：「合并字段 / 分块」哪些其实在框架侧（2026-10-03，代码级核实）

### 54.1 两处文档错误（均已代码级证实）

| # | 原说法 | 出处 | 读码/实测 | 判定 |
|---|---|---|---|---|
| ① | `if (index < Length && …)` 的守卫**是 transpiler 合成的**（"C# 源码的 `Execute(int index)` 里并没有这个条件"） | 09 §53.35 | 宿主 `CPUBattleSpatialHash.cs:251`（Count）/ `:338`（Place）**就写着它**；transpiler 合成的批循环是 `for (index = __startIndex; index < __startIndex + __count; ++index)`（`CppJobGenerator.cs:652`），**不含任何 length 守卫** | ❌ 原说法**错** |
| ② | `ENTJOY_VALUE_BIND=4` 对宿主无任何改变 ⇒ codegen"标量绑定形式"杠杆**关闭** | 09 §53.35 / §53.4 | `ValueBindMode()` 只解析 `0/1/2`；`v[0]=='4'` 落进 `return ValueBindDefault(3)` ⇒ `ValueBindAllowed` 的 `case 4:` 是**死代码**，该臂**从未存在过** | ❌ 原结论**作废**（已修） |

### 54.2 修复与自证

- 修：`ValueBindMode()` 补 `if (v[0] == '4') return 4;`。默认路径**逐位不变**（jobs-only 门 PASS、生成绑定位仍 16 402 B；
  未设/默认下 `const int& Delta = *Delta_ptr;` 不变）。
- 自证（`tests/JobsOnlyTranspilerCheck` 临时加一个条件字段 job，验证后**已还原**）：

| 臂 | 生成物 |
|---|---|
| mode 3（未设/默认） | `const int& Delta = *Delta_ptr;` / `const int& Limit = *Limit_ptr;` |
| **mode 4** | `const int& Delta = *Delta_ptr;` / **`const int Limit = *Limit_ptr;`** |

⇒ 条件字段按值、纯数据操作数仍按引用（判据正确）⇒ **mode 4 是一条真实存在的臂**；
宿主上的收益**已测**（见 §55：为零，Integrate 反而 −11%）。

### 54.3 重新划界：那道 `index < Length` 到底在谁手里

| 环节 | 归属 | 证据 |
|---|---|---|
| 谓词的**文字** | 宿主 C#（**不可动**） | `CPUBattleSpatialHash.cs:251/338`，宿主全文仅此 2 处 |
| 它**为什么承重** | **框架** | 调度器把最后一块切得越过 `length`（977×1024 = 1 000 448 > 1 000 000）；08 §32.7 的 K1 实验撤掉守卫后**仿真分叉**（count 1.2754→1.5844、Melee 131.43 越出 127 上界） |
| 它**每元素值多少、能否消掉** | **框架**（纯 codegen 决策） | transpiler 自己合成逐元素循环（`CppJobGenerator.cs:652`），宿主源码里没有循环 |
| Unity 为什么没有这道守卫 | 引擎契约 | `BattleBenchM1Flat.cs:43-55`：`if (Alive[i] != 0 && State[i] != StateDeath)` = **2 次过滤载入，无 `i < length`** |

⇒ **它不是"宿主数据布局"，而是"框架没给出 Unity 那条契约"** ⇒ 属于框架侧，**不动宿主也能解**。

### 54.4 通解方案（两条，可叠加；判据已预注册）

> **2026-10-04 状态更新（12 §6）**：① **K1′ 经逐路径核实"早已实装"** —— 物化 tile 表（`JobSystem_Scheduler.cpp:768–773`，`edf611e`/2026-08-15）、
> guided（`JobSystem_Tiles.cpp:180–190`，同提交）、uniform 算术推导（`:616–624`，`9ba7ba2`/2026-10-02）、F5 融合 run（`:1611–1613`）
> **四处都钳到 `totalElements`** ⇒ "调度长度 ≤ 数组长度"今天已是框架保证，本节"从未实现"与 §54.3 的"框架没给出契约"是**陈旧记录**；
> 宿主 `CPUBattleSpatialHash.cs:247–250` 的"最后一块越过 length"注释同样陈旧。
> ② **G 已实装**（`CppJobGenerator.cs`，构建期开关 `ENTJOY_GUARD_FOLD`）：生成物里 `index < Length` 消失、上界变 `std::min(__startIndex + __count, Length)`，
> 判据 ①/②/④ 达标（同源两臂 codegen 对照 + `elemsCalled==elems` + 转译器 101/101 + 原生十套件全绿）；
> **但性能收益本次不可判定**（未受影响的 Melee 在两构建间 3/3 变慢 ≥1.1 ms/步 ⇒ 布局噪声 > 预期效应 0.03–0.15 ms）。
>
> **2026-10-04 后续（§56，单独重测那一条）**：G 的性能收益已按"同一源码两次构建 + 位置平衡配对 + 每臂 discovery"重测
> ⇒ **实测无可测收益**：Count/Place 环比 **1.027 / 1.023**、逐 rep 符号 **1/3、1/3**（中位 +0.68 / +0.77 ns/elem），
> 而 **G 根本碰不到的** ClearAll / Integrate 在同一个 16 跑里漂移 **+38% / −6.8%** ⇒ 效应比噪声小一个数量级。
> ⇒ 判据③未通过：G 保留在树里（等价变换 + 消越界 UB、零回归）但**不得引用为性能改进**；本条判据③的期望区间"0.03–0.15 ms/步"作废。

**K1′（调度器钳制）** —— 08 §32.7 已定形态但**从未实现**：在分片/合并路径把最后一块钳到 `length`
（`count = min(tileEnd, length) − start`），让"**调度长度 ≤ 数组长度**"成为框架保证；配套框架自检 `Σ count == length`。

**G（守卫折叠，新）** —— 若 `Execute` 体是一个**无 `else`** 的 `if (c0 && c1 …) { … }`，且首个合取项形如
`<indexParam> < <job 字段>`：
1. 把该合取项从**体内条件**里去掉；
2. 把合成循环的上界从 `__startIndex + __count` 改成 **`min(__startIndex + __count, <字段>)`**（前置一次载入）。

- **语义等价**：短路求值下 `index ≥ Length` 时整个体本就不执行 ⇒ 换成循环上界不改变可观察行为
  （⇒ G **不依赖** K1′ 的正确性；K1′ 是它"顺带消掉越界 tile UB"的搭档）。
- **通解性**：按**语法形状**识别（任意字段名、任意 job），不按 job 特判；宿主全文只命中 `Count`/`Place`。
- **收益预期（诚实区间）**：删掉"每元素一次比较+分支"，并把 `Length` 的载入从每元素一次降到每批一次
  （后者单独已实测 −1.2%，08b §32.2）⇒ 量级 **count/place 各 ~0.03–0.15 ms/步**
  （对齐档 count 基线 0.98 ms、对 Unity 赤字 0.14 ms）。⚠ 08b §2.3 曾写的"守卫 0.1–0.2 ms"是**残差推断**，
  不是受控 A/B（K1 那次 A/B 因越界分叉作废）——G 的价值必须自己测出来。
- **判据**：① 生成的 `*_Execute.cpp` 里 `index < Length` 消失、`for` 上界为 `min(…)`；
  ② `[JOBPERKEY]` count/place **ticks/元素**下降且 `elemsCalled == elems` 不变（无跳/重）；
  ③ 对齐档 Melee 落回 118–127、整步不退化；④ 原生 10/10 × 2 状态、jobs-only PASS、`dotnet build EntJoy.sln` 0 错误。

### 54.5 仍然确实在宿主侧的部分（口径不放宽）

- **数组物理合并**（`Alive[]/State[]/Pos[]` → 打包布局）：transpiler 只拿到 `T* + length`，**无法合并它没分配的缓冲**。
  其 ABI 代理（40 字段装进单结构体形参）已实测**更慢 5/5（0.2–0.7%）**（§40.2）。
- **散列寻址/数组布局**（`cy*CellsW+cx`、`Counts/CellStart` 语义）：宿主数据结构。
- **33 个活跃指针**：机理是**寄存器压力**而非别名（`SCALAR_RESTRICT` 后环内重载仍是 7 次，§41.1），
  框架侧只剩"降低同时活跃值数"（**循环分裂/分块**）——但 §40.3 已预注册门槛：
  **必须先在夹具里做出一个环比 <1 的臂**才允许碰生成器；目前**没有**这样的臂，故不铺开。
  ⚠ **2026-10-04 后续（§57）**：臂**做出来了**（`BenchTwoPassJob.cs`，入库），夹具 8 对交错 + 校验和逐位相同 ⇒
  **3/3 复跑都更慢（环比 1.03 / 1.06 / 1.16）** ⇒ 门槛**未通过**，这条轴按预注册规则**关闭**。

### 54.6 「shared-nothing 计数」这条教科书答案为什么在这里不成立

| 论点 | 证据 |
|---|---|
| Unity **不是**这么做的 | `BattleBenchM1Flat.cs:43-55` ↔ 宿主 `CPUBattleSpatialHash.cs:243-256` **逐行同形**：都是每元素一次 `Interlocked.Increment(Counts[cy*CellsW+cx])`；Unity 侧反汇编也是 `lock incl`（08 §32.3） |
| 我们已经做过私有化 spike | 08 §24：claim 级"每线程私有直方图 + 触过的格清单 + claim 末归并"⇒ **Build −0.60 ms**（5.28→4.68，3/3），Melee 不变；预测的 −1.9 只兑现 1/3 ⇒ 判定**不投产** |
| 对齐档里原子的**上界**很小 | §2 消融：对齐几何下**整个删掉内核原子**只值 **0.06–0.10 ms**（0.8437→0.7853） |
| 隔离夹具里 count 本来就打平 | §2：同数据/同 batch=64/cap=1024 ⇒ **A 0.8437 vs Unity 0.8242 = 1.05×**，place 1.68 vs 1.667 **持平**；**单线程成本两侧相同**（3.7792 vs 3.8354） |
| 语义障碍 | `Counts` 不是纯累加器：`Place` 用 `InterlockedAdd(counts[hash],+1)-1` 当**槽位分配器**（返回值被用 ⇒ 不可私有化）；且 `prefixPartial/place` 必须在本 job 结束后读到全局值 ⇒ 私有化需"每 job 一次归约" + **S×内存**（351 232 格 × 8 worker × 4 B ≈ 11 MB） |

---

## 55. mode 4（`ENTJOY_VALUE_BIND=4`）的第一次真实测量：**为零，且 Integrate 一致退化 −11%**（2026-10-03）

### 55.0 一句话

修好 `ValueBindMode()` 的解析缺陷后（§54.1②），mode 4 **第一次真正存在**（14/15 个内核的发射件改变）；
用**位置平衡**的同会话 DLL 交换 A/B（对齐档、每臂用**自己** discovery 出的批表、16 跑中 14 跑通过全部门控）测：
**count / place 的每元素成本无变化**（每元素对差值 median **+0.62 / +1.42 ns**，符号 1/3、2/2），
而 **Integrate 一致退化**（**+11.18 ns/elem，4/4 同号**）。
⇒ **"标量绑定形式"这条 codegen 杠杆关闭 —— 这一次是"测出来的零"，不是仪器故障。**

### 55.1 生效证据（缺一不可，全部通过）

| 检查 | 结果 |
|---|---|
| 生成物真的不同吗 | **是**：`vbind/B1` vs `vbind/E1` 逐文件 diff ⇒ **14 个** `*_Execute.cpp` 改变（Count/Place 各 4 行、Integrate 14 行、其余 2 行） |
| Count 的具体改变 | `const int& Length/StateDeath` → **`const int Length/StateDeath`**（正是"循环内条件字段"这一条判据） |
| 批表生效吗 | `applied=15/15`（每臂用**各自**的 discovery 表；见 55.3） |
| 门控 | 16 跑中 **14 通过**（2 跑 stdout 缺 `[JOBPERKEY]` 转储 ⇒ 整跑剔除，不是"跳过门控"）；`mismatch=0`；DLL 全部从 `\Debug\` 载入 |
| 统计量 | 每 rep 内"B 均值 − A 均值"（位置平衡）+ 每臂 min（本机档案口径） |

### 55.2 结果（对齐档：镜像批表 + `ENTJOY_CLAIM_ADAPT=0`，4 rep）

**每元素内核成本（`[JOBPERKEY]` kernelNs/elem，1/32 采样自耗时；min per arm）**

| 角色 | B1 min | E1 min | ratio_min | 每 rep Δ（B−A） | 符号(负/正) | Δ median |
|---|---|---|---|---|---|---|
| **Count** | 35.88 | 35.83 | **0.999** | +1.00 +1.33 +0.24 −1.98 | 1/3 | **+0.62** |
| **Place** | 54.13 | 53.99 | **0.997** | −0.02 +5.79 +2.84 −1.62 | 2/2 | **+1.42** |
| **Integrate** | 102.64 | 115.70 | **1.127** | +12.86 +15.52 +9.51 +4.67 | **0/4** | **+11.18** |
| Melee | 2673.80 | 2723.63 | 1.019 | +30.97 +245.57 +134.99 −63.36 | 1/3 | +82.98 |
| MarkDead | 14.69 | 14.37 | 0.978 | −1.11 +1.50 +0.22 +0.31 | 1/3 | +0.26 |
| FlowPresence | 32.45 | 32.50 | 1.002 | −1.11 +4.36 +1.20 −0.21 | 2/2 | +0.49 |
| BfsWave | 409.91 | 414.24 | 1.011 | +4.16 +12.58 −0.63 −8.10 | 2/2 | +1.77 |

**宿主 ms（`[M-19]` 逐趟 / `[M-1]` 整步）**

| 趟 | B1 min / med | E1 min / med | ratio_min | 每 rep Δ（ms） | 符号 |
|---|---|---|---|---|---|
| count | 1.278 / 1.388 | 1.248 / 1.369 | 0.976 | +0.066 +0.255 −0.157 −0.154 | 2/2 |
| place | 1.844 / 2.021 | 1.817 / 1.937 | 0.986 | −0.030 +0.660 −0.212 −0.294 | 2/2 |
| Integrate | 3.61 / 3.81 | 3.92 / 4.01 | **1.086** | +0.37 +0.53 −0.03 −0.14 | 3/4 正 |
| Build 段 | 3.76 / 3.91 | 3.72 / 3.76 | 0.989 | +0.04 +0.37 −0.36 −0.44 | 2/2 |
| 整步 | 126.63 / 129.69 | 124.83 / 129.10 | 0.986 | +2.55 **+15.43** −3.97 −8.47 | 2/2 |

（r2 的 +15.43 由该 rep 内一跑 Melee=144 ms 的离群样本贡献；`dMelee=+12.10` ⇒ 不是臂效应。）

### 55.3 三个仪器坑（本轮当场踩到，都会**静默**造假结论）

| 坑 | 现象 | 后果 | 修法 |
|---|---|---|---|
| **两臂的 key 空间不同** | 内核 key = 被派发入口在 `NativeTranspiled.dll` 里的 **RVA** ⇒ **两个构建的 key 完全不同**（B1 `Count=000019a0` vs E1 `00001990`；`Place` `00010aa0` vs `000108f0`）；重发后 `jobkeys.txt` 立刻过期 | 用旧表建表 ⇒ `applied=0`，`[JOBPERKEY]` 与角色对不上 ⇒ 每元素列全 NaN | 每臂**各自** discovery 一次（空表 + `DUMP=1`，**首见序 = 派发序**）⇒ 按**角色**建表、按**角色**比较（`tools/gate-run/n20-vbind4-aligned.ps1`） |
| **`A,B,B,A` 不是位置平衡的** | 第一轮（未交替，3 rep）里"每 rep 第 4 跑"三跑全偏慢（A2 的 Count `ns/call` +13%，而 A1/B1/B2 一致）⇒ 用"配对差"算出的 **±5% 全内核提速是假的**（Count 3/0 负 vs 平衡后 1/3 正） | 假阳性 | 按 rep 奇偶交替 `A,B,B,A` / `B,A,A,B`；统计量 = 每 rep 内 B 均值 − A 均值 |
| **`Write-Output` 落在函数输出流** | `$map = Discover-Arm ...` 把函数内的日志文本一起收进返回值 ⇒ `$map` 变数组 ⇒ `$map['Count']` 为 `$null` ⇒ 批表渲染成 `:64,:1,…`（**没有 key**） | 静默跑了一整轮 `applied=0` 的"对齐档" | 取值函数里的日志一律 `Write-Host`（脚本内已加注） |

> ⚠ **回溯性提醒**：本仓历史脚本多用 `A,B,B,A` + "第一对配对差"（如 `ab-vbindfix-pair.ps1`）。
> 在本机这种"第三方负载永久存在 + 位置效应"的盒子上，那套设计对**位置 4**不设防；引用那些结论时应带此警告。

### 55.4 结论与方向

1. **by-value 绑定不是路（已实测）**：count/place 的两个条件标量按值后每元素成本**不变** ⇒ §41.1 的
   "73 次栈重载"确实是**寄存器压力**的产物，**不是**"条件标量每轮重载"的产物；与 §32.2（把 7 次不变量
   重载提到环外只值 −1.2%）自洽。**"把标量绑定形式改一改"这条轴到此关闭。**
2. **Integrate 一致退化 +11%（0/4）**：复现了历史 mode 2 的"Integrate +9.8%"方向（§53 注释块）⇒
   mode 4 的判据仍会命中"按值绑定只有代价"的那批字段（Integrate 有 7 个）。
3. ⇒ 框架侧与"每元素谓词"有关的**结构**杠杆只剩一条：**G（把 `index < Length` 折叠进合成循环上界）
   + K1′（调度器把最后一块钳到 `length`）**——§54.3/§54.4 已预注册判据。
   注意它与 mode 4 **不是**同一件事：mode 4 只把 `Length` 的**载入**变便宜，G 把**比较与分支整个去掉**。

> 📌 **接续**：三条轴（整步·默认/对齐档 vs Unity、Job 调度内部、Job S+C 跨栈）的**当前状态、落后清单与
> 改进方向**已收口到 [`10-三轴终局对比-默认档对齐档与Job调度.md`](10-三轴终局对比-默认档对齐档与Job调度.md)（2026-10-03）。
> 其中 **Job S+C（同参空 job、8 worker = 物理核）实测 8.72 µs/job vs Unity 2.43 = 3.60×**，
> 且分解显示**慢在 join 侧**（`c` only 4.01 µs）而**提交侧反而更快**（`s` only 1.72 µs < Unity 地板 3.00 µs）。
> ⚠ **2026-10-04 更正（12 §6.7/§5.5）**：这条 **3.60× 不成立** —— 8.72 µs 是**托管路径**的离机探针（job 未转译）且两侧不同形；
> 真同形（`[M-15]` vs Unity `W0_SHAPE`，8 worker，空体）= A 3.66 vs B 2.67 µs ⇒ **median 1.369× / min 1.327×**。本节下面的三条轴结论不受影响（它们用的是各自的同会话配对）。

---

## 56. G（守卫折叠）的**每元素收益实测**：判为"无可测收益"；顺带量出宿主机 per-element 仪器的噪声底（2026-10-04）

**背景**：§54.4 给 G 预注册了四条判据，其中①（生成物对照）②（`elemsCalled==elems`）④（回归门）当时已达标，
只有**性能收益"本次不可判定"**（跨构建布局噪声 > 预期效应）。本轮把**性能那一条**单独拉出来测。

### 56.1 器械（新增，未入库：`tools/gate-run/gfold-build.ps1`）

- **两臂 = 同一源码、两次构建**（这是 §54.4 自己立的纪律）：`GF0` = `ENTJOY_GUARD_FOLD=0`（不折叠）、`GF1` = `=1`（折叠，默认档）。
- 两个**必须**的构建细节（否则静默拿到同一个 DLL）：`GuardFoldEnabled` 是 `CppJobGenerator` 的 **`static readonly`**（每生成器进程只读一次）
  ⇒ 先 `dotnet build-server shutdown`；且**增量构建不会因 env 变化重跑生成器** ⇒ 必须 `-t:Rebuild`（12 §6.5 踩过：两臂都得到 `07CC2B03`）。
- 测量复用 `n20-vbind4-aligned.ps1`（§55 已验证的协议）：**每臂各自 discovery**（key = 模块内 RVA，两构建的 key 空间不同）、
  按角色建表、**位置平衡**（每 rep 按奇偶交替 `A,B,B,A` / `B,A,A,B`）、每 rep 配对差、min 为主统计量。

### 56.2 判据①（生成物逐行对照）—— **精确达成**

| | `SharpNative_Job_CPUBattle_CountCellsJob_Execute.cpp` |
|---|---|
| **GF0**（不折叠） | `for (int index = __startIndex; index < __startIndex + __count; ++index)`<br>`if (index < Length && Alive_ptr[index] != 0 && State_ptr[index] != StateDeath)` |
| **GF1**（折叠） | `for (int index = __startIndex; index < std::min(__startIndex + __count, Length); ++index)`<br>`if (Alive_ptr[index] != 0 && State_ptr[index] != StateDeath)` |

**影响面 = 2/80** 个生成文件（`CountCellsJob` / `PlaceCellsJob`），与 §54.4 "宿主全文只命中 Count/Place 两处"一致。

### 56.3 判据②/④ —— 通过

16 跑（4 rep × 4 跑）**全部过门**：`applied=15/15`、DLL 全部从 `\Debug\` 载入、`[JOBPERKEY] mismatch != 0` 的行 **0**（无跳/重）。
⇒ 折叠不改变"每个元素恰好执行一次"。

### 56.4 性能：**不可判定，且符号偏"更慢"**

| 角色 | GF0 min | GF1 min | ratio_min | 每 rep Δ(GF1−GF0) | 符号 | 中位 |
|---|---|---|---|---|---|---|
| **Count**（G 触及） | 34.85 | 35.79 | **1.027** | −0.28 +1.65 +0.64 +0.73 | 1/3 | **+0.68 ns/elem** |
| **Place**（G 触及） | 53.34 | 54.55 | **1.023** | −0.19 +1.71 +0.49 +1.04 | 1/3 | **+0.77 ns/elem** |
| 宿主 ms `count` | 1.243 | 1.268 | 1.020 | −0.086 +0.117 +0.026 +0.057 | 1/3 | +0.04 ms |
| 宿主 ms `place` | 1.833 | 1.860 | 1.014 | −0.103 +0.074 −0.075 −0.037 | 2/2 | −0.02 ms |

**决定性的是"G 不可能影响的内核"给出的噪声带**：

| 未触及内核 | ratio_min | 每 rep Δ 中位 | 说明 |
|---|---|---|---|
| **ClearAll** | **1.342** | **+29.90 ns/elem（+38%）** | 0/4，G 完全碰不到它 |
| **Integrate** | 0.983 | **−7.12 ns/elem（−6.8%）** | 3/1，G 完全碰不到它 |
| PrefixFinal | 1.354 | +649.89 ns/elem | 1/3 |
| FlowPresence | 1.064 | +0.23 ns/elem | 1/3 |

⇒ **未触及内核的会话内漂移是 5%–38%，而 G 的效应上限只有 ~2%（+0.7 ns/elem / 36 ns/elem）**。
所以 Count/Place 上那 +0.7 ns 既**不能**判为收益，也**不能**判为退化 —— 它是"**打不到**"。

### 56.5 判定与记账（可执行口径）

1. **G 不作为优化记账**：判据③（§54.4 写的"`[JOBPERKEY]` count/place ticks/元素下降"）**未通过**；
   实测为"不可判定"，且在被触及的两个内核上符号偏向"更慢"（1/3、1/3）。
2. **G 保留在树里**（默认开）：它是**语义等价**的生成期变换（判据①②④达标、零回归），且与 K1′ 一起消掉越界 tile 的 UB；
   但**不得**被引用为性能改进。§54.4 的"性能收益本次不可判定"应读作"**实测无可测收益**"。
3. **方法学（本轮最有复用价值的一条）**：宿主机 `[JOBPERKEY]` per-element 口径在本机**打不到 2%**
   —— 因为它没有任何"未受处理的对照"能定出噪声带。**推论**：以后凡是预期 <3% 的每元素结论，
   必须走**夹具**（进程内、确定性输入、交错配对、n≥6/点），或者**自带对照臂**（本例就是 ClearAll/Integrate 这两个"G 碰不到"的角色）。
   ⚠ 注意与 §55 的区别：§55 的"+0.62/+1.42 ns = 零"是**测出来的零**（mode 4 在 Integrate 上 4/4 反向、方向自洽）；
   本节的 Count/Place 是**打不到**（未触及内核漂移比效应大一个数量级）。两者不能混为一谈。

---

## 57. §54.5 预注册门槛的**第一次执行**：两遍切分（live-set 缩小）**3/3 更慢 ⇒ 该轴关闭**（2026-10-04）

§54.5 给"降活跃值/循环分裂/分块"这条轴立了硬门槛：
> **必须先在夹具里做出一个环比 <1 的臂，才允许碰生成器；目前没有这样的臂，故不铺开。**

本轮把那个臂做出来了（`tools/NativeTranspilerFixture/BenchTwoPassJob.cs`，**入库**）：

- **形状**：`BenchMeleeScanTwoPassJob` —— 与 `BenchMeleeScanPackJob` 逐元素等价，把两个互不相关的累加拆成两遍
  （① orca top-8；② 3×3 中心块最近敌人），两遍各自都不需要对方那批值 ⇒ **同时活跃值数严格下降**。
  等价性由夹具的 checksum 断言把关（`same KD2 output` / `same KPeer output`），不是靠注释。
- **代价**：两遍都要重放 81 格遍历 ⇒ `CellStart/SortedIndex/Positions` 的 gather 流量翻倍。

### 57.1 结果（8 对交错，n=6 次/点；同一二进制复跑 3 次；比值 <1 才算门槛通过）

| 复跑 | `melee_pack` 中位 | `melee_twopass` 中位 | 环比 | 逐对符号 |
|---|---|---|---|---|
| #1 | 149.4 | 154.1 | **1.03** | 5 对更慢 / 3 对更快 |
| #2 | 147.8 | 157.4 | **1.06** | — |
| #3 | 141.3 | 163.9 | **1.16** | — |

**校验和 3/3 全 PASS**（等价性成立）⇒ 差异纯粹来自结构。

### 57.2 判定

1. **门槛未通过、且方向一致**：两遍切分 **3/3 更慢（+3%…+16%）** ⇒ 按 §54.5 预注册规则，
   **"循环分裂/分块以降活跃值"这条轴关闭**：Melee 形状是**访存/延迟受限**，"省下的活跃值"换不回"翻倍的 gather"。
2. **对 §41 的判读要收窄**：§41 的"环是**寄存器压力**受限、33 活跃指针 vs ~14 GPR"解释的是"为什么编译器要重载/重算地址"
   （即**为什么指令数下不来**），**不等于**"减少同时活跃值能提速"。本轮给出了后者的直接否证：
   减少活跃值的同时如果增加了访存，净效果为负。⇒ **框架侧在这条轴上没有可摘的果子**（这正是 §54.5 门槛要防的事）。
3. **顺带校正一条旧结论**：同一夹具里 `melee_offsets`（§9.2c：静态指令 −4.8% 但"墙钟无变化"、对级比值中位 ≈0.99）
   本轮 **3/3 复跑都是 offsets 更快**：中位 `pack/offsets` = 151.4/143.9（−5.0%）、153.3/148.1（−3.4%）、148.9/146.5（−1.6%）。
   ⇒ 旧结论"墙钟没动"应收窄为"**幅度明显小于静态指令降幅**（1.6–5% vs 4.8%），方向一致但小得多"。
   ⚠ 夹具自身的重测散布很大（同一臂 5 连测 130.4–148.7，±12%）⇒ 这条只是"同向、小幅、3/3"，**不足以**作为新杠杆启动；对整步量级 ~0.1%，继续按 §54.4 的口径**不投产**。

---

## 58. §50.3 那条"来源未定位"的薄 tile 批：**已定位 —— 门与 JCC 的硬编码下限撞值**（2026-10-04）

### 58.1 结论

**这不是"薄 tile"判据，而是"`cs` 撞在 JCC 硬编码下限上"判据。** 三条事实相乘即可闭合：

| 事实 | 位置 |
|---|---|
| 门控 = `thinTiles = !guided && cs <= kClaimSpanThinElems` | `JobSystem_Scheduler.cpp:711`（与 `:1043` 同形） |
| `kClaimSpanThinElems = 16`（**同一个常量**也被 F1 认领跨度那条轴用） | `JobSystemInternal.h:201` |
| `ResolveChunkSize` 的**每一条返回路径**都用 `std::max(16, …)` 封底 | `JobSystem_State.cpp:1183 / 1203 / 1259 / 1281 / 1342` |

⇒ **`cs <= 16` ⟺ JCC 顶在硬编码下限上**，与"tile 厚薄"、与"是哪个内核"都无关。
对空体/短 `length` 的 job，撞下限走的是**最短路**：被分类成 mem-bound 后每个决策都命中 `:1219` 的
`return JccDiagNote(2, tpwChunk)`，而 `tpwChunk = max(16, ceil(length/(W·tpw)))`
⇒ **只要 `length ≤ 16·W·tpw`（默认 W=8、tpw=64 ⇒ 8192），`cs` 就恒等于 16 ⇒ 门恒真**。
这就是 §50.3 那句"默认档 = 厚 tile ⇒ F2/F4 恒不生效"被推翻的**真正原因**：不是存在某个 `cs=16` 的小内核。

### 58.2 实测（独立夹具 `tools/BuildPassBench`，不需要 Godot）

**器械（本轮 A 项新增，入库）**：给 `[JOBPERKEY]` 加了**每键薄批计数** `thin=`（`JobSystem.cpp` + `JobSystemInternal.h` +
`JobSystem_Scheduler.cpp` 两处），与既有 per-key 计数共用同一零开销门控（`JobPerKeyResolve` 在 dump 开关关闭时恒返回 −1）。
**自洽性判据**：14 行扫描里每行都满足 `[JOBF2F4] applied == Σ per-key thin`（例 `59412 = 59400+6+6`、`12 = 0+6+6`）⇒ 归因口径可信。
另用 `ENTJOY_JCC_VERBOSE=1` 把**分支名**一起取下来 —— 这一步是定位的关键：光看 `chunk` 值看不出是谁给的。

**形状**：空体并行 job、`BENCH_EMPTY_LEN=1024`、`BENCH_EMPTY_BATCH=0`（框架自选）、W=8、每跑约 59 400 次派发。

| 观测量 | "薄" regime（6/6 rep） | "不薄" regime（3 rep 里 2 rep） |
|---|---|---|
| 空 job `thin / batches` | **99.98–99.99%** | **0.06–0.16%** |
| `cs`（= `elemsCalled / tiles`） | **16** | 32 |
| 分支打印（59 400 次决策） | **`MEM-BOUND → tpw chunk` ≈ 59 300**；`MEM-BOUND PROBE` **0**；SCHED-DOMINATED 3–11 | **`SCHED-DOMINATED chunk=32` ≈ 59 300**；TWO-FACTOR ~20 |
| 机制 | `tpwChunk = max(16, ceil(1024/512)) = 16` ⇒ 撞下限 | 执行主导分支 ⇒ 32 |

⇒ **机理链闭合**：`TryClassify ⇒ kModeMemBound ⇒ :1219 tpwChunk ⇒ cs = 16（撞下限）⇒ cs<=16 恒真 ⇒ F2/F4 开`。
`tpw=1` 的臂同源：`tpwChunk = max(16, ceil(len/8))` ⇒ 只有 `len ≤ 128` 才撞下限（`len=512` 时实测 `cs=64`、`len=8192` 时 `cs=1024`，
与公式一致）。

### 58.2b ⚠ 自我更正：本文件上一版的"塌点 512 / 128"**不成立**（陈旧二进制）

提交 `6b1d2b5` 写下的定量结论是"塌点 = `16·W·min(4,tpw)`（tpw=64 ⇒ **512**、tpw=1 ⇒ **128**），且随 tpw 移动"。
**那次扫描用 `dotnet run --no-build` 跑的，用的是 `tools/BuildPassBench/bin/Release/` 里那份旧的 `NativeDll.dll`**
（本轮 rebuild 后它是 `18:38:01 / md5 C2ED4DC6`）：旧二进制里同一形状 **59 311/59 335** 次决策走 `SCHED-DOMINATED chunk=32`，
而当前源码的同形决策**绝大多数走 `MEM-BOUND → tpw chunk`** ⇒ 两份数据描述的是**两个 JCC**，不可混用。

用当前二进制复测（14 行扫描 + 定点多 rep）后的**真实图像**：

| tpw（W=8） | len | 空 job `thin` 占比 | cs | 备注 |
|---|---|---|---|---|
| 64 | 512 | **100%** | 16 | 撞下限 |
| 64 | **1024** | 0.09% / **99.98%** / 0.16%（3 rep）；另 6 rep **6/6 ≈100%** | 32 或 16 | **regime 会翻** |
| 64 | **4096** | 99.95%；另 3 rep 见 0.1 / **99.99** / 0.06% | 16 或 127 | **regime 会翻** |
| 64 | 8192 | 0.12% | 251.6 | — |
| 64 | 8193 / 16384 / 32768 | **0%** | 17 / 507 / 1014 | 越过 8192 ⇒ 不再撞下限 |
| 1 | 512 … 32768 | **0%** | `len/8` | 该臂阈值 = 128 |

⇒ 正确表述是两条 —— **第 1 条可辩护、第 2 条才是本轮真发现**：

1. **撞下限 ⟺ `cs == 16` ⟺ `length ≤ 16·W·tpw`**（在该 job 落进 mem-bound / tpw 兜底那类分支时；默认阈值 **8192**）；
2. **但"这个 job 会不会落进那类分支"本身不确定**：同一形状、同一 `length`、同一二进制，会话内既观测到 **6/6 恒薄**，
   也观测到 **2/3 不薄**（分类成 mem-bound 与否随进程/机器状态翻）⇒ **这个门既不是薄厚判据，也不是 `length` 的稳定函数**，
   它跟着 JCC 的分类 regime 走。

### 58.2c 器械纪律（本轮新踩，值得单列）

**`dotnet run --no-build` 会静默使用 `bin/` 里那份可能很旧的 `NativeDll.dll`** ⇒ 测出来的 JCC/调度行为可能与当前源码
**完全不同**（与 09 §10.1 BUG-1"静默回退到旧二进制"同类）。凡是用夹具量框架行为，**先核对 `bin/NativeDll.dll` 的
mtime/哈希与本轮构建一致**，或干脆用会重建的方式跑。本轮就是靠"两份数据的 `cs` 相差 2×（32 vs 16）"追下去才发现二进制不同。

### 58.3 §50.3 那 4 037/4 711 批的归因：**器械跑了，结果把问题推到了更前面**（2026-10-04）

- 逐键器械（本轮 A 项 `thin=` + `ENTJOY_JOB_BATCH_TABLE_DUMP=1`）在 CSBS 默认档跑了两趟（`n35attrib/`，8 worker）：
  `[JOBF2F4] applied = ` **4 572 / 4 612**，而**逐键 `thin` 全为 0**（15 个键全 0 —— 连平均 `cs` 只有
  **8.05 / 15.86** 的 `PrefixPartial` / `PrefixFinal` 也是 0）。
- ⚠ 同一计数器在 `tools/BuildPassBench` 里 **15/15 次**满足 `applied == Σ thin`（如 `59403 == 59403`）。
  ⇒ 差异只能来自：**那 4 572 个薄批的调用点在 per-key 表里没有身份**（`perKeyIndex = −1`
  = `JobFuncKey` 解不出模块内 RVA 的那一类，即 §52.5 记的"静默降级到托管回调/堆上 thunk"面）。
- ⇒ **归因结论**：这 4 711 批**不是**由那 15 个已身份化的内核产生的；要闭合，需要补一个**全局**计数
  （`unkeyed`：`perKeyIndex < 0` 的批数，及其中的 `thinTiles` 子集）再跑一趟 —— 这是下一轮的第一件事。
  另一个待查点：`PrefixFinal`/`PrefixPartial` 的平均 `cs ≤ 16` 却 `thin=0` ⇒ 它们的批要么也在未标识面里，
  要么逐批落在另一个 regime（§58.2b）——两者都指向"**平均 `cs` 不能当判据**"这条已有纪律。
- ✅ **零代码就把来源钉住了**（`n36verbose/`，`ENTJOY_JCC_VERBOSE=1` 打印**每一次**决策）：把
  `[JCC] R length=… <分支> chunk=…` 按 (length, 分支, chunk) 分桶，`chunk ≤ 16` 的**全部**落在
  **`length ∈ [444, 477]`**（`len=448` 占 3 198 次、449 占 763，其余是 444–447/472–477 的长尾），
  分支一律 `SCHED-DOMINATED`，而该分支的 `chunk = max(16, ceil(len/(W·min(4,tpw)))) = max(16, ceil(448/32)) = 16`
  —— **又一次撞上下限**。这批桶之和 ≈ `applied=4 621` ⇒ **全局数被它们完全解释**。
  **对照 15 个已身份化角色的每步长度**：`1 000 000`（8 个角色）、`351 232`（3 个）、`64`（`PrefixFinal`/`PrefixPartial`）、
  `129 113`（`FlowSeedInit`）、`1 734`（`BfsWave`，均值）—— **没有一个落在 444–477**。
  ⇒ 结论：这些批来自**per-key 表无法给出身份**的调用点（`perKeyIndex = −1`），即 §52.5 记的"静默降级/堆上 thunk"面：
  **它们才是默认档 `[JOBF2F4] applied` 的全部来源**（约 20 次/步、长度 ~448 且随数据变化）。
  ⇒ §50.3 的问题到此**定性闭合**（来源 = 短长度 + 未标识调用点，不是"小内核"、也不是那 15 个角色）；
  下一步若要**指名**那个调用点，需要给它补一个可打印的名字/尺寸（或看托管侧哪个 Schedule 走了 thunk）。

### 58.4 影响与处置

- **良性**：短 `length` 的 `tileCount = ceil(len/cs)` 本来就小 ⇒ F2 省的"物化表 O(tileCount)"与 F4 省的"每 tile 固定开销"
  本来也小，开着的代价同样小。故**不改行为**。
- **但两处信念/判据必须改**：
  1. `JobSystemInternal.h` 的 `kClaimSpanThinElems` 注释、`JobSystem_Scheduler.cpp` 两处 `thinTiles` 注释、
     `[JOBF2F4]` 打印处注释 ⇒ 一律写明"**这是与 JCC 下限撞值，不是薄厚判据**"，并附机理（mem-bound ⇒ `tpwChunk`）
     与阈值（`len ≤ 16·W·tpw`，默认 8192；**且 regime 会翻**）。⚠ 这三处注释在本轮被**改了两遍**：
     第一遍附的是"塌点 512/128"，自查更正后才换成现在的口径（§58.2b）。
  2. §50.3 留的回归判据"**薄 tile 占比骤降为 0 即为回归**"**作废** —— 它既不是薄厚判据、也不是 `length` 的稳定函数
     （regime 会翻）。若要保留回归价值，门必须换成**相对**口径（如把 `cs` 与 `length/W` 比较，或用 `rc`）；
     但那会改变"短 length 大内核"上 F2/F4 的开关状态，收益量级未知（本来就小）⇒ **按纪律先不动**，
     记为"已知名不副实的判据，当前良性"。
- **代码改动记账（本轮 A 项）**：`src/NativeDll` 有**一处真代码改动** —— 新增每键薄批计数 `g_perKeyThin`
  （定义 + 复位 + `[JOBPERKEY] thin=` 打印 + 两处 `if (thinTiles) …fetch_add`）。**产品路径（dump 关）零开销**：
  自增在 `if (batch->perKeyIndex >= 0)` 内，而 `JobPerKeyResolve` 在 dump 关时恒返回 −1（`:632-635`）。
  其余为本轮与上一轮的注释改动。
- **注意**：`kClaimSpanThinElems` 同时被 F1（认领元素跨度）与 F2/F4（厚薄门）两条轴使用，改它会**同时**移动两者阈值，
  不是局部改动（已写进注释）。

### 58.5 环境阻塞与器械缺口（如实记录）

1. ⚠ **更正：CSBS 从来没有被"占锁"**（前两轮我把启动崩溃归因于 `Get-Process Godot*` 里那 3 个 PID，**是错的**）。
  逐项二分（`n33envbisect/` + `n34logconfirm/`）给出真正的原因：**`--log-file` 传相对路径必崩**（2/2，
  退出码 `-1073741819` = `0xC0000005` 访问违例、stdout 0、日志文件都没建），**绝对路径正常**（2/2，日志已建），
  不带也正常（2/2；其余 8 个 env arm 全部正常）。而 `n20`/`thin-tile-census` 这类驱动把 `--log-file` 与
  调用者给的 `-OutDir` 拼在一起 ⇒ **我用相对 `-OutDir` 时就崩，用默认的绝对 `-OutDir` 时就正常**
   （18:16 成功那次没传 `-OutDir`）。已修：`thin-tile-census.ps1` 干脆不带 `--log-file`。
   ⇒ 那 3 个进程（pid 3528/32228/33844，11:29/11:33/18:21 创建、owner 查不到、`whoami /priv` 只有
  `SeShutdownPrivilege` ⇒ 非管理员）**与本次崩溃无关**，只是"我无权结束"这一条仍成立（对照组：我自己起的
  Godot 子进程可以随便 kill）。⚠ `tools/gate-run/n20-vbind4-aligned.ps1` 仍带 `--log-file`，同潜在坑。
2. ✅ **`tools/BuildPassBench` 报不出 JobSystem 计数 —— 本轮 B 项已修**：根因是它**从不显式关停**，只靠
   `NativeJobCore` 注册的 `ProcessExit += SafeShutdown` 兜底，而该回调跑在运行时线程上 ⇒ 原生侧判"非主线程"直接拒绝
   （`[JobSystem] Shutdown() called from non-main thread — rejected`）⇒ 关停统计整段一行不打印。
   修法 = 在 `Main` 的主线程上显式 `JobScheduler.Shutdown()`。**已验**：`[JOBGEN]`/`[JOBPERKEY]`/`[JOBF2F4]` 全部恢复打印，
   且逐键 `thin` 之和与 `[JOBF2F4] applied` 精确相等（14/14 行）。教训：**诊断缺席会被读成"计数为 0"**。
3. `.ps1` 里用 `[IO.File]::ReadAllText('相对路径')` 会按**进程 CWD** 解析，而不是 PowerShell 的 `Set-Location`
   （本轮浪费一次读数）⇒ 证据脚本一律用绝对路径。

---

## 59. ✅ JCC 的每 job chunk 决策**跨进程摆动**（真 job）—— 现象与 `JCC_ROBUST` 的缓解**均已用等负载对照证实**；机理未逐行证明（2026-10-04）

### 59.1 观测（两次 CSBS 默认档普查：同一 DLL、同一会话、相隔 31 s、`ENTJOY_JOB_BATCH_TABLE_DUMP=1`）

| 键（角色） | batches / elems（两次是否相同） | `tiles` | `calls` | `elemPerCall`（= F5 融合宽度） | `cs` |
|---|---|---|---|---|---|
| `00006bd0`（MarkDead） | **225 / 225 000 000 —— 完全相同** | **3 336 → 114 216**（34×） | 2 160 → 28 560 | **104 166.7 → 7 878.2** | **67 446 → 1 970** |
| `00003a80`（FlowClear） | **450 / 158 054 400 —— 完全相同** | **229 416 → 5 136**（45×） | 57 360 → 3 960 | 2 755.5 → 39 912.7 | **688.9 → 30 773.8** |
| `000053b0`（FlowPresence） | 225 / 225 000 000 | 114 216 → 114 216 | 28 560 → 28 560 | 7 878.2 → 7 878.2 | 1 970 → 1 970（**逐字段相同**） |
| 其余 | 同 | 小差 | — | — | BfsWave 77.8→71.6、PrefixFinal 16→15.9、PrefixPartial 15.9→8.1、Melee 1953.1→1961.7 |

⇒ 同一 job（**同一个键 = 同一个模块内 RVA**、同样的 `batches`/`elems` ⇒ 同样的 `length`）在两个进程里拿到了
**34–45× 不同的 chunk**；而 FlowPresence 逐字段相同 ⇒ **不是 per-key 归属错位**（§52.1 那类仪器坑），是决策本身变了。

### 59.2 ⚠ 受控复现**失败** ⇒ 不能归因于"JCC 天生不稳"（本条的诚实边界）

| 受控配置（`tools/BuildPassBench`，W=8，dump 开） | rep 数 | 观测 | 摆动 |
|---|---|---|---|
| `BENCH_SHAPES=pass`（单一 workload） | 4 | 两个大键 `cs` = 281.5 / 287.9 | **≤2.3%** |
| 默认 `game,pass`（混合 workload） | 3 | 281.4 / 288（小键 8–8.4） | **≤2.3%** |
| `emptyjob` × `BENCH_EMPTY_LEN=1e6`（**极便宜 job × 1e6 长度 = MarkDead 的形状条件**） | 6 | `cs` = **30 940 – 31 126**（0.6%），全部走 `SCHED-DOMINATED`，`thin=0` | **0.6%** |

⇒ 13 个受控 rep 里**没有**出现 >2.3% 的摆动，而 CSBS 观测到 34–45×。
⇒ **不能**写成"JCC 的 chunk 决策不可复现"。能被三类受控配置**排除**的诱因：单一 workload、混合 workload、
"极便宜 job + 1e6 长度"本身。剩下唯一没被排除、也唯一没被验证的诱因是 **CSBS 那种"15 个 job 逐步交织"的真实 workload**
（它同时让 15 个键竞争成本缓存与探测/分类）。⇒ **机理归属仍未定**，需要在 CSBS 上带 `ENTJOY_JCC_VERBOSE=1` 复跑
（把同一个 key 的 `[JCC]` 分支名与 `cs` 一起取下来）——该跑现在被环境阻塞（§58.5 第 1 条）。

**§59.2b 已知同族机制（与 §58 同源，可解释两端）**：分类双稳 —— 被判 **mem-bound** 时 `cs = tpwChunk
= max(16, ceil(len/(W·tpw)))`（len=1e6 ⇒ **1 954**，正好是观测到的 1 970 那一端）；走 **two-factor** 时
`tileSize = (150 µs − C_fixed)/C_elem`、`cs ≈ len/ceil(len/tileSize)`，对 MarkDead 这种极便宜 job
（`C_elem` 只学出 ~2 ns/元素）⇒ `cs ≈ 6.7e4`，正好是另一端。**两端都落在观测值上**，中间没有别的分支能给这两个数。

### 59.2b ✅ 现象与缓解均已用**同等完整负载**的对照证实（2026-10-04；首版被独立复查推翻后重做）

> **首版写"定案"但证据混淆**：当时 `ENTJOY_JCC_ROBUST=1` 会**中断** `JobSystemTests`（47/57、rc=1），
> 于是两臂跑的不是同一负载（robust 臂 60 批 vs 默认 810 批），"40×→1.03×"是拿**截断运行比完整运行**。
> 经独立复查指出后，我分两步重做：**① 先修掉那条中断 → ② 再做等负载干净对照**。以下为重做结果。

**① 修掉中断（真缺陷，已修）**：根因是用例断言 `GetPerElemCost(h) > 0`（**细**通道），而 robust 把 job
判为 **mem-bound** 后只写**粗**通道（`GetCoarseCost`）——细通道保持 0 是**该档的设计行为**，不是学习失败。
⇒ 用例把"档位不同"误报成失败。已把判据改为"**任一通道学到正成本**"（保留原不变量：4 个异构 job
各自独立学到自己的成本、互不串扰）。**修后两臂都 57/57 rc=0**（此前 robust 固定 47/57）。

**② 干净对照（8 rep × 2 臂，键 `000053e0`，两臂均 810 批 / 21 000 000 元素）**：

| 臂 | `tiles` 跨 rep | 摆动 |
|---|---|---|
| 默认 | 68 610 … 768 358（**两条带**：~68–71k / ~765–768k） | **11.2×** |
| `ENTJOY_JCC_ROBUST=1` | 408 960 … 410 033 | **1.0001×** |

⇒ **默认档的 `cs` 确实双带、跨进程不稳；`JCC_ROBUST` 把它压成稳定值** —— 这一条**成立**，
且是在**两臂同工作量**的前提下测到的（首版被推翻，正是缺这个前提）。
（更早 16 rep 抽样给过 39.90×/40.75× ⇒ 带间距离随抽样变化，但"双带 + 不稳"这一性质一致。）

**仍未证实（不要把本节读成"机理已逐行证明"）**：
- "**逐批重判**"这一机理**方向**与"双带"结构一致，但未逐行证明（复查未能复现首版引的分支配比，
  且其 6 次 `JCC_VERBOSE=1` 里 `UNKNOWN` 恒为 0）。
- robust 稳定到的那个值**是否更快**未测（早前 `BuildPassBench` 配对整步 A/B 显示性能中性）。
- 稳定性是否对**其它负载 / 二进制布局**也成立未测。

**⇒ 提默认的整步验收已跑完（2026-10-04）：结论是"不提"**。按规定"6 对同会话 / 逐对同号 / 五段全看"
（同一 DLL、`BENCH_SHAPES=game,pass`、臂 `base,64`、6 对顺序平衡、min-of-windows）：
**两个口径符号相反**：median +0.054 ms（2/6 同号）vs **min（本仓硬化口径）−0.081 ms（4/6）** ⇒ 都未达"逐对同号" ⇒ 性能中性无净收益。
（两臂共有的 ~2× 偶发停顿是 doc12 表 16 已定性的 GC/OS 停顿，**不是**本开关的缺点；换 min 口径后最坏情况基本持平。）唯一稳定收益是 `count` 段（5/6、−0.06 ms）。
⇒ `JCC_ROBUST` 定位为**可选的可复现性阀**（要"同配置可复现"时可显式开），或改用**钉住 `cs`**
（`ENTJOY_JOB_BATCH_TABLE` / `ENTJOY_TILES_PER_WORKER`）这条更便宜的路径。详见 doc13 §5.9b。

下面保留首版原始记录，供追溯。

> 本节推翻上面 §59.2 的"受控复现失败"边界：不是机理不可复现，而是**当时的受控配置不对**。用
`JobSystemTests`（原生十套件之一）本身就带着能落在双稳边界上的 job 形状，**同一二进制、同一工作负载**、
只重复跑即可复现：

**复现（6 rep，`JOB_BATCH_TABLE_DUMP=1`）**：两个键的 `batches`/`elems` 逐 rep 完全相同，而

| 键 | batches / elems | `tiles` 跨 rep | 摆动 |
|---|---|---|---|
| `000053e0` | 810 / 21 000 000 | 18 048 … 768 313 | **42.6×** |
| `00005440` | 810 / 21 000 000 | 18 761 … 718 509 | **38.3×** |
| 其余 9 个键 | 同 | 比值 1.00 … 1.69 | 稳定 |

与 CSBS 观测到的 34×/45× **同量级、同形状** ⇒ §59 就是同一个现象。

**机理（`JCC_VERBOSE=1`，按 `[JCC]` 分支名计数）**：摆动的是**分支选择的配比**，不是单个分支的算术：

| rep | `tiles`(53e0) | `MEM-BOUND → tpw chunk` | `TWO-FACTOR` |
|---|---|---|---|
| 1 | 716 678 | 1 515 | 139 |
| 2 | 68 610 | 897 | 805 |
| 3 | 69 998 | 181 | 1 507 |
| 4 | 69 285 | 132 | 1 500 |
| 5 | 68 610 | 199 | 1 505 |

两个分支**同时活着**，且配比在 rep 间大范围移动（`memBoundTpw` 132↔1515 = **11×**、`twoFactor` 139↔1507 = **11×**）。
⇒ 分类器是**逐批拿当前成本估计重判**的，估计在边界附近抖动 ⇒ 每批各自投硬币 ⇒ 整批的 `cs`/`tiles` 落到哪一端不确定。
这与 §58 的"regime 随进程翻"**是同一个根因**（§59.2b 的推测被实测确认）。

**缓解措施已验证（同一实验，8 rep × 2 臂）**：

| 臂 | key `53e0` | 比值 | key `5440` | 比值 |
|---|---|---|---|---|
| 默认（`JCC_ROBUST` 关） | 18 896 … 768 313 | **40.7×** | 17 823 … 766 347 | **43×** |
| `ENTJOY_JCC_ROBUST=1` | 29 085 … 30 068 | **1.03×** | 29 040 … 30 916 | **1.06×** |

⇒ `JCC_ROBUST`（环形窗**中位数** + 最小样本 + 冷却 + 双向迟滞）把 40× 摆动压到 **3–6%**。
它的路径特征是：不再直接采信 mem-bound，而是走 UNKNOWN 的**粗/细交错**（`unkCoarse=122`、`unkFine=120`），
用成对样本判 mode ⇒ 判据不再逐批翻。

**结论与建议**：
1. §59 的"34–45× 跨进程摆动"**不是调度器 bug**，也不是 per-key 归属错位（§59.1 的 FlowPresence 逐字段相同已排除），
   而是**默认分类器逐批重判 + 成本估计在边界抖动**。
2. **默认档的交叉实验结论因此不可复现**（§59.3 第 2 条成立）：跑默认档 A/B 必须**钉住 `cs`**
   （用 `JOB_BATCH_TABLE` 或 `ENTJOY_TILES_PER_WORKER`），否则两端会随机落点。
3. **可执行的改进**：`ENTJOY_JCC_ROBUST` 目前默认关（因为它是"健壮分类"实验臂）；本实验证明它能把
   默认档的可复现性从 40× 提到 ~1.05×。⇒ **是否把它提为默认，是一条独立可验收的决策**
   （需按本仓纪律跑整步 A/B：≥6 对 + 五段全看，确认不引入回归）。

**复现命令**（原生即可，不需要 CSBS）：
```powershell
$env:ENTJOY_JOB_BATCH_TABLE_DUMP='1'; $env:ENTJOY_JCC_VERBOSE='1'
1..8 | % { .\build-nativeDll-tests\Release\JobSystemTests.exe 2>&1 |
  Select-String 'key=000053e0|MEM-BOUND → tpw|TWO-FACTOR' }
# 对照臂：$env:ENTJOY_JCC_ROBUST='1'
```

### 59.3 影响（不依赖机理归属，这两条都成立）

1. **F2/F4 的薄 tile 门、F5 的融合宽度、认领几何全都建在 `cs`/`tileCount` 上** ⇒ 它们随该决策一起动。
   `elemPerCall` 在两次观测里差 **13×**（104 166.7 vs 7 878.2）就是 F5 融合宽度的直接体现。
2. ⇒ **按趟/按元素的 A/B 应当同时报出（或干脆钉住）每键 `tiles`/`cs`**。对齐档用批表把内批钉死，
   这正是**对齐档数字更可复现**、而默认档整步结论会"随机器状态翻转"（§53.2）的同源解释之一。
   复现/定性的最小命令（解锁后一次即可）：
   `ENTJOY_JOB_BATCH_TABLE_DUMP=1` + `ENTJOY_JCC_VERBOSE=1` 跑默认档，按 key 对比 `tiles`/`cs` 与 `[JCC]` 分支名。

### 59.4 与 §58 的关系

§58 证明"**薄 tile 门 = `cs` 撞下限**"；§59 提供"**`cs` 本身可能跨进程变**"的（未定因）观测。
两者相加的保守结论：**默认档下 F2/F4 的开关状态既不是薄厚判据，也不保证可复现** ⇒
要对它做对照实验，必须先把 `cs` 钉住（批表或 `ENTJOY_TILES_PER_WORKER`）。













