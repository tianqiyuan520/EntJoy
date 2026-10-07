# 17 · 独立复核（2026-10-07）：HEAD 构建的两档判据 + L0–L5 机理逐层核验

> **触发（用户指令）**："①重新用最新数据更新 docs；②重新核实这些 layer。docs 里的可能不可信。"
>
> **口径**：本文**不引用** doc14–doc16 的读数作为结论，一律在 **HEAD**（`89e135a`）上重测；
> 机理一律用**自己的证据**（生成码普查 / 部署件反汇编 / 两侧源码 / 同源微基准 / Unity 空体器械）。
>
> **被测构建**（游戏侧部署件，即真正跑起来的二进制）：
> `NativeTranspiled.dll` = **132,096 B**、md5 **`FC4C19909A…`**；`NativeDll.dll` = 1,089,536 B。
> 与 §46.3 记录的 `132,096 B` 一致 ⇒ **部署件就是 HEAD 的构建**（`build/` 里的 `bin/NativeTranspiled.dll`
> 是 2026-10-03 的旧件，**不能**用来核对）。
>
> **仪器**：`qq-judge2.ps1`（相位匹配：A 跑 5 s 窗，B 从该窗**起点**的状态跑同样步数）+ 两个派生变体
> `qq-judge2-def.ps1`（默认档，去掉批表与 `JOB_COST_CACHE=0`）、`qq-judge2-assist0.ps1`（+`ENTJOY_ASSIST=0`）。
> ⚠ 机器非静默：测量期间有 ~17% 背景负载（浏览器/本 GUI）⇒ 本文只信**同会话、逐轮配对**的比值。

---

## 0. 结论速览（先看这张表）

| 层 / 判据 | doc14–doc16 的说法 | 本次独立复核 | 判定 |
|---|---|---|---|
| **两档判据** | 对齐档 0.976 / 0.989（§44.3/§44.7）、1.069（§44.19） | 对齐档 **1.021（6/6）**；默认档 **1.016（4/6）** | **更新** |
| **L0-a 粒度不是 Build 赤字来源** | 真对齐档把赤字放大 | 两档 Build 比值几乎相同（**0.831 / 0.829**） | ✅ **确认**（新证据） |
| **L0-b Unity 靠少调度赢→否** | floor 0.21–0.95 µs/job、0.335 ns/batch | 复测 **1.20 µs** 单 IJob、**0.216 µs**/job、1M/64 = **54.8 µs**、边际 **0.304 ns** | ✅ **确认** |
| **L0-c 工作量等价** | "逐行镜像 ⇒ 差异只能是 codegen" | **Integrate 段不可比**（Unity 侧源码自证 + 输入每步清零） | 🔴 **更正** |
| **L0-d 原子争用不是主因** | §43.3"`plain` ≈ `base`" | `plain`（去原子）**−10.6%（3/3）** | 🔴 **更正** |
| **L0-e 工具链/代码体积轴** | 全空（LTO/`/Ob3`/`/Os`/clang-cl/`/GS-`/`/Qpar-`） | **未重测**（本轮不碰） | ⏸ 未复核 |
| **L1 ABI 两帧** | 调度器调 3 参 Adapter，内部再 call 26 参入口 | 内联**已落地**：37 个 Adapter 中 **31 个 `callq=0`**，热内核全部 0 | ✅ **确认（且已修）** |
| **L2 cs=1 每元素一帧** | 大内核 ≈1.9 ns/项、MarkDead 0.18 ns/项 | MarkDead 复测 **0.230 ns/工作项**（1M 项省 0.230 ms） | ✅ **确认** |
| **L3 环内不变量重载税 −7.5%** | count −12.2% / place −6.1% / Σ −7.5% | 重载**存在**（反汇编逐条吻合），但**税不复现**（hoist 仅 +1.3%、2/3），且消融变体**结构上无效** | 🔴 **更正** |
| **L4 Integrate 赤字 = 访存双缓冲** | F8：A 的 In/Out 工作集 2× | 该支柱随 L0-c 一起撤掉 | 🔴 **更正** |
| **L5 每元素固定开销被便宜内核放大** | +1% @90 ns/单位 vs +20% @1 ns/元素 | Build+Flow 仍支撑；**Integrate 那根支柱撤掉** | ⚠ **部分成立** |

**一句话**：整步上 EntJoy 在 HEAD 上**持平或略优**（对齐档 1.021、默认档 1.016、关掉 assist 后 1.024），
但**撑起整步的仍然只有 Melee 一段**；Build / Integrate / Flow 仍 0/6 落后。账本里**三条**关键抓手
（§45.6 的判据、§44.19 的 1.069、§41/§42 对 Integrate 的归因）与**当前代码/产物不符**，逐条见 §2。

---

## 1. 当前数据（HEAD，相位匹配，同会话，6 轮）

比值口径 **B/A，>1 = EntJoy 更快**。`>1` 列 = 同号轮数。

### 1.1 对齐档（JCC 真关 + 按 job 名批表；11 处 cs=1、4 处 cs=64）

| 段 | **B/A 中位** | 同号 | 逐轮 |
|---|---|---|---|
| **Build** | **0.831** | 0/6 | 0.796 0.849 0.896 0.891 0.810 0.813 |
| Flow | **0.907** | 0/6 | 0.904 0.909 0.932 0.918 0.905 0.905 |
| **Melee** | **1.067** | **6/6** | 1.076 1.058 1.150 1.087 1.049 1.045 |
| MarkDead | **0.755** | 0/6 | 0.732 0.746 0.829 0.811 0.705 0.765 |
| **Integrate** | **0.826** | 0/6 | 0.789 0.839 0.835 0.826 0.826 0.806 |
| **整步** | **1.021** | **6/6** | 1.025 1.016 1.091 1.041 1.007 1.004 |

绝对值中位：`Flow 25.645 / 23.301`、`Melee 95.720 / 102.303`、`整步 128.290 / 131.343`（A / B，ms）。

### 1.2 对齐档 + **assist=0**（工作等价口径）

| 段 | B/A 中位 | 同号 |
|---|---|---|
| Build | 0.828 | 0/6 |
| Flow | 0.879 | 0/6 |
| **Melee** | **1.080** | **6/6** |
| MarkDead | 0.755 | 0/6 |
| Integrate | 0.830 | 0/6 |
| **整步** | **1.024** | **6/6** |

绝对值中位：`Flow 26.290 / 23.183`、`Melee 93.240 / 100.431`、`整步 126.325 / 129.134`。

⇒ **关掉 assist 反而更好**（A 的 Melee 95.72 → 93.24 ms）⇒ 整步的领先**不依赖** assist。
（这一点同时更正了 Unity 侧 `[M4-DISCLOSE]①`"assist 给 A −9.49 ms/步"的**方向**：本协议下它是**成本**。）

### 1.3 默认档（出厂默认：JCC 自适应 + 无批表）

| 段 | **B/A 中位** | 同号 | 逐轮 |
|---|---|---|---|
| **Build** | **0.829** | 0/6 | 0.823 0.769 0.871 0.915 0.720 0.834 |
| Flow | 0.991 | 2/6 | 0.963 0.984 1.000 1.052 0.959 0.998 |
| Melee | 1.029 | 5/6 | 1.040 1.001 1.024 1.131 0.971 1.033 |
| **MarkDead** | **1.291** | **6/6** | 1.297 1.305 1.169 1.377 1.184 1.286 |
| **Integrate** | **0.892** | 0/6 | 0.944 0.781 0.933 0.971 0.796 0.852 |
| **整步** | **1.016** | 4/6 | 1.018 0.987 1.014 1.106 0.958 1.018 |

绝对值中位：`Flow 23.875 / 23.717`、`Melee 88.015 / 89.970`、`整步 118.630 / 119.413`。

### 1.4 与历史同仪器的对照（**只能看量级，不能看绝对差**）

| 情形（同仪器） | Build | Flow | Melee | MarkDead | Integrate | 整步 |
|---|---|---|---|---|---|---|
| 对齐档 **本轮（HEAD）** | 0.831 | 0.907 | 1.067 | 0.755 | 0.826 | **1.021** |
| 对齐档 §44.7（2026-10-06，白名单 ON） | 0.892 | 0.890 | 1.028 | 0.752 | 0.831 | 0.989 |
| 对齐档 §44.3（同上） | 0.841 | 0.879 | 1.013 | 0.777 | 0.803 | 0.976 |
| 对齐档 §44.19（白名单 ON + 内联 ON） | 0.849 | 0.953 | 1.115 | 0.763 | 0.815 | 1.069 |
| 默认档 **本轮（HEAD）** | 0.829 | 0.991 | 1.029 | 1.291 | 0.892 | **1.016** |
| 默认档 doc14 §1（2026-10-05） | 0.900 | 1.004 | 1.008 | 1.286 | 0.925 | 1.000 |

**三条可读的**：
1. **Build 从 ~0.89–0.90 掉到 ~0.83**，而 §43 的对比里值绑定白名单在 HEAD 已被删除（§2.1）⇒ 与"那份收益不在树里"一致；
   但跨会话 Build 漂移可达 18%（§43.9b）⇒ **不能**把 7% 全归给它，只能说"方向一致、幅度未隔离"。
2. **MarkDead 的 1.291 vs 0.755** 不是矛盾：默认档它是粗 tile（JCC 给 ~1M/tile），对齐档被钉在 **cs=1** ⇒ 每一段都付 0.23 ns/项。
   这是"对齐档把 EntJoy 的理性补偿拆掉"的最干净例子。
3. **整步 1.0 上下翻转完全由 Melee 决定**（占整步 73–75%）：Melee 从 1.008（doc14）到 1.115（§44.19）摆动 ±5%，
   整步就跟着摆 ±4%。

---

## 2. 与账本不符的四处（**先看这节**）

### 2.1 §45.6 的"判据 0.976 / 0.989"是"白名单还开着"的树

- §45.6 把 `整步 0.976 / 0.989` 记为"判据"，这两个数来自 §44.3 / §44.7 —— 而那两轮**晚于** §43.6 把
  `DefaultValueBindBodyJobs` 落成默认 ⇒ 它们**含**值绑定给 Build 的那一份。
- HEAD 的 diff（`89e135a`）**删除**了 `DefaultValueBindBodyJobs` / `ValueBindBodyJobs` /
  `ParseValueBindJobs` / `ValueBindAllowed` / `ENTJOY_VALUEBIND_JOBS` / `ValueBindWideTypeAllowed`，
  规则只剩 `tripCount && typeOk`（`CppJobGenerator.cs:299-304`）。
- 而 `tripCount` 只从 **C# 源码循环**的初值/条件/步进收集（`:426-442`），`if (!sawLoop) Take(body, use.InLoop)`
  写的是 **InLoop 而不是 TripCount**（`:462-465`）⇒ **元素形 `IJobParallelFor` 的 TripCount 恒空**。
- **实测普查（游戏侧 `NativeTranspiler_Generated`，80 个内核，2026-10-07 09:16 生成、09:17 编译）**：
  ``byValue=17 / byRef=178``；`CountCellsJob 0/7`、`PlaceCellsJob 0/7`、`FlowPresenceJob 0/6`、
  `IntegrateJob 0/16`、`MeleeSimJob 1/31`，只有 `ZeroCellsJob 1/0` 全按值。
  生成码形如 `const int& Length = *Length_ptr;` + 转译器合成的 `for (int index = __startIndex; …)`。
- **同源微基准重建后同样按引用**（`tools/BuildPassBench` 于 10:17 重新生成）：7 个标量**全部** `const T&`。
  （此前 `NativeTranspiler_Generated`（08:09）里它们**是按值**的 —— 那是"宽按值默认"那一臂的遗留物，
  **不可**用来核对 HEAD。）

⇒ **§43 的 Build +21.5% 不在当前树里**；本文 §1 的 Build 0.83 才是 HEAD 的读数。

### 2.2 §44.19 的 1.069 不等于 HEAD

1.069 出自"白名单 ON + 内联 ON"的树。HEAD（内联 ON、白名单 OFF）实测 **1.021**（assist 开）/ **1.024**（assist 关）。
§45.6 直接沿用 0.976/0.989 作为"判据"而没有在删白名单后重测 —— 这是**账本的缺口**，本文补上。

### 2.3 assist 的"协议口径"与实际不符（**新发现**）

- doc09 §8.1 / doc16 §616 的口径写着 `ENTJOY_ASSIST=0`（"框架默认就是 off"）。
- **框架**默认确实是 off（`JobSystem.cpp:750 g_mainThreadAssistEnabled{false}`），
  但**游戏自己把它默认打开**：`CPUBattleEcs.cs:625 JobAssistOn = env("ENTJOY_ASSIST") != "0"`，
  并打印 `[CPUBattleEcs] JobSystem assist=开（…）` —— 本轮每个 A 日志里都有这一行。
- **脚本之间不一致**（本轮逐个查了 `tools/gate-run/*.ps1`）：
  - **doc09/doc10 时代**的 `ab-aligned.ps1` / `ab-cross-stack-a.ps1` / `ab-coldwarm.ps1` / `ab-workerscan.ps1` /
    `ab-workscaled.ps1` / `ab-step-workers.ps1` / `d0-launcher-ab.ps1` / `count-probe.ps1` **显式 `ENTJOY_ASSIST=0`**
    ⇒ **doc10 §2 的三轴对比是"assist=关"的**（与 doc10 §1 表里写的口径一致）；
  - **2026-10-05 之后的战役脚本**（`qq-judge2.ps1`、`cs1-cost.ps1`、`ab-3arm-2curve.ps1`、`frozen-pairs2/3.ps1`、
    `perpass-*.ps1`、`scan34-ab.ps1`、`melee-phases-ab.ps1`、`claimspan-interleaved.ps1`、`claimfilter-eq.ps1`、
    `k-sweep.ps1` 等）**第一步清空全部 `ENTJOY_*`/`CPUBATTLE_*`，然后只设自己那几个** ⇒ **assist 回落到游戏默认的"开"**；
  - 另有一批（`n10/n11/n12/n5/n6/n8-*.ps1`、`ab-step-workers-2s.ps1`）**只 `Remove-Item Env:\ENTJOY_ASSIST`**
    ⇒ 同样是"开"。
  ⇒ **doc15–doc17 的读数含 assist，doc10 的读数不含**；doc09 里"对齐档 harness 显式 pin 成 0"那句
  **只对 `ab-aligned.ps1` 成立**，对后来的战役脚本不成立。而 Unity 侧无对应物（B 侧 `[M4-DISCLOSE]①` 自己标了这一点）。
- **本轮做了对照**（§1.2）：关掉 assist 后整步 **1.024（6/6）**、Melee **1.080（6/6）**
  ⇒ **结论稳健**；但"协议口径 assist=0"这句话**与脚本实际行为不符**，应更正。

### 2.4 doc16 §41/§42 对 Integrate 的归因不成立

见 §3。B 侧源码与运行日志**两处自证**该段不可比，而 doc16 §42.3 写的是"两侧差异只有 F4/F7/F8"
⇒ "Integrate 的 20% 是访存（F8 双缓冲）"（§44.8）**没有证据支撑**。

---

## 3. L0-c / L4：Integrate 的工作量**不等价**（更正）

### 3.1 B 侧源码两处自证"不可比"

| 出处 | 原文 |
|---|---|
| `BattleBenchM4.cs:16-19` | dump v2 只有 position/alive/state/team/hp ⇒ B 的 Integrate 拿到 **vel=0、knock=0、af=0、stuck=0** ⇒ 只有"存活+位移"一条主分支可达（A 的活战场还有击退/受伤硬直/尸体滞留分支）⇒ **Integrate 段仍不可比** |
| `BattleBenchM4Entry.cs:31, :120` | ④ **Integrate 段不可比**（dump v2 无 velocity/knock/af ⇒ 退化分支混合） |

### 3.2 运行日志每次都打印这句

```
[M4-DISCLOSE] … ④**Integrate 段不可比**（dump v2 无 velocity/knock/af ⇒ 退化分支混合）；…
[M4-PROOF-5] 分支混合（输入档）：alive&state==DEATH=242（本档 af 全 0 ⇒ 首个执行步即全部回收）…
```

### 3.3 机制（本轮读代码确认，非推断）

- `BattleBenchM2Entry.cs:372-377` 的 `RestoreInput` 方向是 **影子数组 → 活数组**：
  `inVel→sVel`、`inAf→sAf`、`inStuck→sStuck`、`inKnock→sKnock`，并把 `sFlash` 清零。
- `BattleBenchM4Entry.cs:152-158` 把这五个影子数组**建成全零**；`:287` 在**步循环的第一句**调用它
  ⇒ **每一步都把 sVel/sAf/sStuck/sKnock 清零、sFlash 清空**。
- 后果（逐条可推）：
  - **A 侧**：`MarkDeadJob` 首次死亡写 `af = FramesDeath + 1`（`CPUBattleCombat.cs:618`），
    `IntegrateJob` 的死亡分支按 `af-1` **滞留 FramesDeath 步**（`:712/727`），并做速度积分 + 击退（`1/max(mass,1)`）+ 受伤硬直。
  - **B 侧**：`af` 每步被清零 ⇒ `afd = af-1 ≤ 0` ⇒ **一步回收**；`knock=0` ⇒ 击退分支空转；`vel` 每步归零。
    只有**同步内**被 Melee / MarkDead 重写的字段才非零。
  - 群体也不同：B 的 alive = **997,554**（首步），A 的 alive ≈ **999,999**。
- ⇒ **A 在每个单位上做的事严格多于 B**，而 A 反而更慢（0.826）⇒ **这个比值不能用来衡量 codegen 或访存**。

### 3.4 处置

- 保留"Integrate 仍慢"这一**现象**（0/6、0.826–0.892）；
- **撤回**"它是 F8 双缓冲/访存造成的"这一**归因**（doc16 §44.8 的那一行）；
- doc16 §41.5 的第 2 条本来写着"待核（若 A 真的多做活 ⇒ 是工作量差异）"、§41.6 也把"逐条核"列为第 1 步 ——
  §42 越过了这一步直接下了"逐行镜像 ⇒ 只能是 codegen"的结论。**本文把它退回到"待核"。**

---

## 4. L3：不变量重载与"税"（更正）

### 4.1 重载**确实存在**（部署件反汇编，逐条吻合）

对**部署的** `NativeTranspiled.dll` 反汇编（`llvm-objdump -d`，120 个函数 / 26,068 条指令）：

| 内核 | 整函数 | 稳态每元素 | 其中循环不变量取数 |
|---|---|---|---|
| `CountCellsJob_Execute_Batch` | 77 | **38** | **6** —— `(%rdi)`InvCellSize、`(%r10)`OriginX、`(%r9)`OriginY（且**指针每元素从栈重载** `movq 0xc0(%rsp),%r9`）、`(%rax)`StateDeath、`(%rsi)`CellsW、`(%r11)`CellsH |
| `PlaceCellsJob_Execute_Batch` | 95 | — | **每元素 4 次栈指针重载**（`0xd8/0xe0/0xc8/0xd0(%rsp)`）+ 10 条内存操作数指令 |
| `ZeroCellsJob_Execute` | 7 | — | `movslq/testq/jle/shlq/xorl/jmp/retq` ⇒ **`memset` 尾调用**（§43.10 的 F2 更正**成立**） |

**38 / 6 这两个数与 §43.2 记的数逐位相同** ⇒ 该节的反汇编结论可复现（含"一次栈重载"那个细节）。
`lock` 计数：`FlowBfsWaveJobDual 93`、`SpawnJob 6`、`IntegrateJob 4`、`FlowSeedJob 2`、`Count/Place/Melee 各 1`。

### 4.2 但它的**消融变体结构上无效**（这是本轮的新发现）

`BenchCountCellsHoistJob` 的 C# 把标量抄进局部（`int len = Length; …`），看起来是"源码级 hoist"。
**但生成码把它放进了转译器合成的 `for` 循环体内**：

```cpp
const int& Length = *Length_ptr;                       // 仍是按引用
for (int index = __startIndex; index < __startIndex + __count; ++index)
{
    int len = Length;          // ← 提升发生在"每元素"体内，不是循环外
    float inv = InvCellSize;
    …
```

⇒ **它根本没有把标量提升出循环**，因此**不构成对"环内不变量重载"假设的有效检验**。

### 4.3 在 HEAD 上重建并重跑（`tools/BuildPassBench`，当前发射器，冻结 dump，batch=64，3 轮）

| variant | Σ六趟（中位） | count | place |
|---|---|---|---|
| `base`（现状 = 按引用） | 2.1685 | 0.6007 | 1.3499 |
| `hoist`（源码级抄局部） | 2.1415 | 0.5923 | 1.3260 |
| `plain`（**去掉原子**） | **2.0314** | **0.5229** | 1.2913 |

逐轮配对（>1 = 前者更快）：

| 对 | r1 | r2 | r3 | 中位 | 同号 |
|---|---|---|---|---|---|
| `base/hoist` | 0.975 | 1.042 | 1.013 | **1.013** | 2/3 |
| `base/plain` | 1.166 | 1.106 | 1.067 | **1.106** | **3/3** |

### 4.4 判决

- **"hoist 税 = count −12.2% / place −6.1% / Σ −7.5%"（§43.3）在 HEAD 上不复现**：本轮 hoist 只有 +1.3%（2/3），
  落在噪声内；而**去掉原子**却稳定给出 **−10.6%（3/3）**。
- 这不是"§43.3 测错"，而是**两件事都变了**：① 树变了（白名单删除 ⇒ 基准臂的绑定形式不同）；
  ② 消融变体本身无效（§4.2）。**结论：账本 L3 的量化抓手在当前树/当前仪器下不成立。**
- ASM 里的那 6 条不变量取数**是真的**，但它们**不在关键路径上**（被同环内 `lock incl` 的延迟吸收），
  所以"删掉它们"换不来时间；真正能换来时间的是**减少原子/内存往返**（`plain` 的 −10.6% 里含真工作量差异，见 §9 局限）。

---

## 5. L2：cs=1 每元素一帧的定价（复测，确认）

`cs1-cost.ps1`（同 DLL、只改批表；**故意不对齐的诊断臂**），3 轮配对：

| 轮 | MarkDead cs=1 | MarkDead cs=64 | 省 |
|---|---|---|---|
| 1 | 0.910 | 0.720 | 0.190 |
| 2 | 0.890 | 0.660 | 0.230 |
| 3 | 0.830 | 0.650 | 0.180 |

⇒ 中位 **cs1 = 0.890 / cs64 = 0.660（ratio 1.348）**，MarkDead 覆盖 1,000,000 工作项
⇒ **0.230 ns / 工作项**（§44.10 记 0.18 ns/项；更早的注释记 0.85 ns/项）⇒ **同量级，确认**。
把它外推到全部 cs=1 调用点（Melee 1M + MarkDead 1M + Flow 系列 ≈1.4M）⇒ **≈0.7–0.8 ms/步 ≈ 整步的 0.6%**。

---

## 6. L0-b：Unity 调度 floor（复测，确认）

`M4_DISP=1`，W0Player，8 worker，60 rep / 20 warmup（本轮自跑）：

| 配置 | 中位 | 归一 |
|---|---|---|
| 1 个空 `IJob` 往返 | **1.20 µs** | （min 0.60） |
| 1 job × 1 项 | 2.65 µs | — |
| 100 job × 1 项 | 39.2 µs | **0.392 µs/job** |
| 1000 job × 1 项 | 215.9 µs | **0.216 µs/job** |
| **1 job × 1M 项，batch=1** | **354.4 µs** | 0.354 ns/项 |
| **1 job × 1M 项，batch=64** | **54.8 µs** | 0.055 ns/项 |
| **1 job × 1M 项，batch=1024** | **34.7 µs** | 0.035 ns/项 |
| 派生：每 declared batch（b1 vs b64 / b1024） | — | **0.304 / 0.320 ns** |

与 doc15 §4.1（0.95 µs、0.39/0.21 µs、382/52.1/35.9 µs、0.335 ns）**在 8% 内吻合** ⇒
"Build 六趟的调度总开销 ≈6 µs，可忽略；B 不靠少调度赢"**成立**。
而 EntJoy 侧每 tile 边际 13–52 ns（doc15 §4.5，本文未重测）对 Unity 的 0.304 ns/declared batch ⇒ **量级差仍在 ~40–150×**。

---

## 7. L1：ABI 与"内联"（复核，确认且已落地）

对部署件做全量函数普查（37 个 `*_Execute_Adapter`）：

- **31 个 `callq=0`** ⇒ Adapter 内**没有**第二次调用；热内核全部 **0**：
  `wave Adapter 1415 / Batch 1378`、`Melee 4341 / 4569`、`Integrate 504 / 485`、`Grad 778 / 791`、
  `Count 75 / 77`、`Place 86 / 95`、`Presence 68 / 79`、`MarkDead 44 / 50`、`PrefixFinal 75 / 81`。
- 6 个仍含 `callq`：`YSortRange/YSortScatterRange/LerpUpload` 三个是 **`IJob` 形**（调 `_Execute`，本就不在
  内联属性覆盖内）；`FlattenBySlot/ScatterBySlot`（另有 `MoveJob`）把调用打进 Batch 体中部（`+0x280`）。
  **非热内核，不影响本条结论。**
- 独立 `_Execute_Batch` 那份**仍必须存在**（托管 `BINDINGS.g.cs` 按名 P/Invoke）⇒ §44.24"方案三做不到"**成立**。

⇒ **L1 的机理（Adapter 帧 + 26 实参搬迁）在 HEAD 上已经被"内联进 Adapter"消除**；
它现在只剩下**一次** Adapter 帧，而 cs=1 时每元素仍要付这一次（= §5 的 0.23 ns/项）。

---

## 8. 仍未复核的项（诚实清单）

| 项 | 为什么不复核 / 状态 |
|---|---|
| L0-e 工具链与代码体积轴（LTO、`/Ob3`、`/Os`、clang-cl、`/GS-`、`/Qpar-`） | 需要多次重建；本轮只确认"真机是 clang-cl"这一前提未变，**其余按未复核对待** |
| L0-d 的 `plain` 消融含义 | `plain` 同时去掉 `lock`、RMW 和 store ⇒ **它不等于"只去掉屏障"**，−10.6% 里含真工作量差；见 §9 |
| doc15 §4.5 的"13–52 ns/tile 边际" | **未重测**（需 A/A 几何扫）——本文 L0-b 只重测了 Unity 那一侧 |
| Melee 侧的"每候选 1.9 cycle / 访存受限"（§33） | **未重测**（需插桩/反汇编逐相） |
| `zero` 的 11.6 vs 17.1 GB/s | **未重测**；只确认 `ZeroCellsJob` 是 `memset` 尾调用 |
| `prefixPartial` vs `prefixFinal` 的访存放大 | **未重测**（引用 doc15 §5） |
| 正式 M4 的 `[M4-DISCLOSE]` 其余各条（chunk 1.5×、17 vs 16 组件、Scatter 写回 9–10 ms/步、ECS runner 开销） | 本轮**未逐条定价**；它们与 §3 一样属于"两侧不同构"的已披露项，引用任何跨栈整步数字时**必须同时引用** |

---

## 9. 局限

1. **机器非静默**：测量期间 ~17% 背景负载（浏览器 + 本 GUI）。因此本文只引用**同会话逐轮配对**的比值；
   §1.4 的跨会话对照只用于看量级。
2. **§4.3 的消融缺一个有效变体**：要真正测"环内不变量重载"，需要一个**批量形**（`IJobParallelForBatch`，
   自己写 `for` 循环、标量在循环外）的变体 —— 那样提升才真的在循环外。本轮**没有**加这个变体。
3. **assist 的两个臂是两次会话**（§1.1 vs §1.2），所以"assist 是成本"这一条含会话漂移；
   稳健的部分只有"两臂都 ≈1.02、都 6/6"。
4. **§5 的 0.230 ns/项是 MarkDead 一个内核**的价，外推到 Flow 的大内核（26 形参、1373 条指令）未重测。
5. **本文没有改任何产品代码**；新增的只是 `tools/gate-run/` 下三个仪器脚本（该目录不入库）与一次
   `tools/BuildPassBench` 的重建（生成物，未改其源码）。

---

## 10. 复现命令与产物

```powershell
# 对齐档（相位匹配，6 轮）——本文 §1.1
powershell -NoProfile -ExecutionPolicy Bypass -File tools\gate-run\qq-judge2.ps1 -Rounds 6 -CfArms 0 `
  -OutDir tools\gate-run\verify47\judge-align
# 对齐档 + assist=0 —— §1.2（脚本由 qq-judge2.ps1 派生，仅多一行 $env:ENTJOY_ASSIST='0'）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\gate-run\qq-judge2-assist0.ps1 -Rounds 6 -CfArms 0 `
  -OutDir tools\gate-run\verify47\judge-align-assist0
# 默认档（派生脚本：去掉批表与 JOB_COST_CACHE=0）—— §1.3
powershell -NoProfile -ExecutionPolicy Bypass -File tools\gate-run\qq-judge2-def.ps1 -Rounds 6 `
  -OutDir tools\gate-run\verify47\judge-default

# 部署件反汇编普查 —— §4.1 / §7
& 'D:\Microsoft Visual Studio\2022\Community\VC\Tools\Llvm\x64\bin\llvm-objdump.exe' -d --no-show-raw-insn --demangle `
  'E:\GODOT\Project\ComputeShaderBattleSimulation\.godot\mono\temp\bin\Debug\NativeTranspiled.dll' > tools\gate-run\verify47\asm.txt

# 同源消融（**必须先重建**，否则测的是 08:09 那一臂） —— §4.3
dotnet build tools\BuildPassBench\BuildPassBench.csproj -c Release
$env:BENCH_INPUT='tools\gate-run\frozen-pairs3-r31\A_s60_p1.bin'; $env:BENCH_VARIANTS='base,hoist,plain'
$env:BENCH_BATCHES='64'; $env:BENCH_ROUNDS='3'; $env:ENTJOY_JOB_WORKERS='8'; $env:ENTJOY_JOB_COST_CACHE='0'
& tools\BuildPassBench\bin\Release\BuildPassBench.exe

# cs=1 派发链定价 —— §5
powershell -NoProfile -ExecutionPolicy Bypass -File tools\gate-run\cs1-cost.ps1 -Reps 3 -OutDir tools\gate-run\verify47\cs1-markdead

# Unity 调度 floor —— §6（M4_DISP=1 门控，自带工作证明）
#   M4_DISP=1 M4_WORKERS=8 M4_DISP_REPS=60 M4_DISP_WARMUP=20 M4_DISP_ITER=0 M4_CSV=<csv>
```

| 产物 | 内容 |
|---|---|
| `tools/gate-run/verify47/judge-align/` | 对齐档：`rounds.csv` + 每个 `A-*.log` / `B-*.csv` |
| `tools/gate-run/verify47/judge-align-assist0/` | assist=0 臂（同上） |
| `tools/gate-run/verify47/judge-default/` | 默认档臂（同上） |
| `tools/gate-run/verify47/asm.txt` / `funcs.csv` | 部署件反汇编 + 每函数 `instrs/callq/lock/push` 普查 |
| `tools/gate-run/verify47/bench-ablation.out.txt` | 消融三臂的原始摘要行 |
| `tools/gate-run/verify47/cs1-markdead*` / `dispfloor.csv` | §5 / §6 的原始读数 |
| `tools/gate-run/qq-judge2-def.ps1`、`qq-judge2-assist0.ps1` | 本轮新增的两个派生仪器（**未入库**） |

---

## 11. 下一步（按"证据强度 ÷ 成本"排）

1. **补一个有效的 hoist 消融**（批量形变体、标量真在循环外）—— 这决定 L3 的税到底是 1% 还是 7%，
   也是"值绑定该不该按结构判据回来"的唯一判据（§4.3/§4.4）。
2. **给 Integrate 换一个可比口径**：把 A 的 Integrate 也喂退化输入（vel/knock/af 清零），
   或用带 velocity/knock/af 列的 dump v3 —— 否则该段的 0.826 永远无法归因（§3）。
3. **Build 的 7% 掉幅做同会话隔离**：在同一会话内用"env 门控的按值臂 vs 现状臂"复测（而非跨会话比 §44.7），
   才能判定它是不是白名单删除的代价。
4. 本文的 1.021 / 1.016 **不应**被当作"已追平"的判据引用：它含 §8 列出的多项两侧不同构，
   而 `五段无一退化` 仍然**不满足**（Build/Flow/MarkDead/Integrate 四段 0/6）。
