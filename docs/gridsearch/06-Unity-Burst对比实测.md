# 06 — Unity Burst 对比实测：NativeTranspiler（C++）vs Unity DOTS/Burst

> 日期：2026-09-14。对照组：**Unity 6000.3.2f1 + Burst 1.8.28 + Entities 1.3.15**（IL2CPP 发布档）。
> 被测组：**EntJoy + NativeTranspiler 生成的原生 C++**（即带 `[NativeTranspile(Target = BackendTarget.Cpp)]` 的 job）。
> 原始证据与完整口径见下游项目：`ComputeShaderBattleSimulation/docs/CPU-百万同屏-UnityDOTS对比计划.md` §14 / §14.7
> （探针源码在游戏仓 `CPUBattle/Scripts/CPUBattleNativeProbe*.cs`）。
>
> ⚠ **本表一律不使用 C# 孪生/托管实现作对照值**。EntJoy 侧刻意不加 `[NativeTranspile]` 的 C# 版
> （`MeleeSimJobCs`、`FlattenJob`、`samples/.../IJobChunkScheduleOverheadTest`、`tools/EcsShapeCostProbe`）
> **都是托管档**，与 Burst 比会得出完全不同的（且错误的）结论。

> ⚠ **后续更新（2026-09-14）**：本报告里"每 job 调度"与"`[CPHS]` 诊断扰动"两项已在
> [`07-框架完善与托管-vs-原生对比.md`](07-框架完善与托管-vs-原生对比.md) 里做了**实测收口**：
> 批上下文池（6 轮交替配对 −6%～−8%）、诊断不再扰动被测（含窗口=4 的最坏情况）、
> 每 job 调度成本的"托管侧 0.10 µs / P/Invoke 0.05 µs / 原生提交 0.95 µs"拆分，
> 以及**托管 C# vs NativeTranspiler C++/ISPC** 的同 job 同形状对比
> （Light 1.24～1.36×、Heavy 1.19～1.27×、ISPC 6.9～7.2×、Sleep 1.05～1.09×）。

## 1. 结论摘要

| 轴 | EntJoy native | Unity Burst（发布档） | 谁快 |
|---|---|---|---|
| chunk 容量 / chunk 数（16 组件、1,015,808 实体） | 192/chunk、5,291 chunk、**≈85 B/实体** | 128/chunk、7,936 chunk、**128 B/实体** | **EntJoy 省 1.5× 内存** |
| 定位表 / `ComponentLookup` 随机访问（扣地板） | **3.79～4.30 ns/访问** | 6.99 ns/访问 | **EntJoy 快 1.6～1.8×** |
| 分布原子认领（每次命中） | **1.73～1.85 ns** | 3.36 ns | **EntJoy 快 1.8～1.9×** |
| 存活标记维护（1M/步） | **0.019～0.027 ms**（按字整写） | 0.533 ms（`EnabledMask` 逐位） | **EntJoy 快 20～28×** |
| **chunk 列就地写**（含数据访问） | **0.210～0.337 ns/实体** | 0.958 ns/实体 | **EntJoy 快 2.6～5.0×** |
| 扁平写（含数据访问） | ~~0.358 ns/实体~~ | ~~0.219 ns/实体~~ | ⚠ **已撤回 → §2.4b**（3 轮复测区间重叠，不可区分） |
| **每 chunk 调度开销**（空 chunk job，3 轮交替配对） | 3.9 ns/chunk（3.0～8.4） | 2.89 ns/chunk（1.42～3.53） | **区间重叠 ⇒ 不可区分** |
| **每 job 调度**（空 `IJobParallelFor`，连发 1000） | 6.66 µs（auto）／**9.27 µs（等口径 7936×64）** | **4.71 µs** | **Unity 快 1.41×／1.97×（§3.7）** |
| 扁平裸指针随机读 | 0.34～0.42 ns | 0.237 ns | 不可区分 |
| 扁平索引器随机读 | 0.32～0.36 ns | 0.264 ns | 不可区分 |
| **地板**（只算下标+写回，不访存） | 0.18～0.21 ns | 0.182 ns | **相等 ⇒ 两套 harness 口径可比** |

**读法**：**没有可辨的落后项**。可比项 10 项里 EntJoy 领先 5 项、Unity 领先 1 项、不可区分 4 项
（**2026-09-14 修订**：扁平写由"Unity 领先"移入"不可区分"，见 §2.4b）；
且**双方各在一类形态上占优**——EntJoy 强在**组件列就地写**，Unity 强在**每 job 调度**
（该轴 EntJoy 侧**已有通解**：批提交通道，**§3.8**）。
会进预算的量级差异只有"间接访问"与"存活标记"两项，**都是 EntJoy 占优**；
Unity 占优的调度项换算后 **0.78 ms/步（0.78%）**。
"地板"一栏两边几乎相等，说明两个 harness 的测量口径是校准过的，不是拿不同尺子量出来的。

> ⚠ **一处已撤回的早期结论**：本报告首版曾写"每 chunk 调度 Unity 快约 3×（1.47 vs 4.3）"，
> 那是**单轮读数**。补 3 轮交替配对后为 **2.89 vs 3.9 ns，两臂区间重叠**（Unity 1.42～3.53、EntJoy 3.0～8.4）
> ⇒ **该轴不可区分**，该结论已撤回。

## 2. 明细与口径

### 2.1 每 chunk 调度开销（同量纲对照：双方都是**空 `Execute`** 的 chunk job）

口径：只测 `schedule + complete` 摊到每 chunk 的**派发/调用**固定开销，**不含任何数据访问**（数据访问见 §2.4）。

| 实现 | 档 | ns/chunk（中位数） | 范围（轮数） | 执行证据 |
|---|---|---|---|---|
| Unity Burst | IL2CPP 发布档 | **2.89** | 1.42～3.53（3 轮） | 遍历到 7,936 **OK** |
| EntJoy native | EntityBatch 路径 | **3.9** | 3.0～8.4（9 轮） | 遍历到 5,291 **OK（9/9 轮）** |
| Unity Burst | Editor 档 | 5.33 | （1 轮） | 7,936 **OK** |
| EntJoy **托管** C#（无 `[NativeTranspile]`） | — | **22.4** | 20.9～28.2（9 轮） | 5,291 **OK** |

- **native vs 发布档：2.89 vs 3.9 ns，区间重叠 ⇒ 不可区分**（早期"Unity 快 3×"的单轮读数已撤回，见 §1 注）。
- EntJoy 的 native 相对自身**托管**路径快约 **5.7×**（3.9 vs 22.4 ns）—— 这一条很稳（两侧区间不重叠）⇒ 原生绑定确实生效。
- ⚠ **空 job 必须带执行证据**：否则"0.015 ms"与"查询匹配 0 个 chunk"无法区分。两侧都用计数 job 验证过。
- 两栈在这里的绝对量都是**纳秒级**：5,291 / 7,936 chunk 的全遍历只有 **23～42 µs**，
  对下游项目的 125 ms/步预算**无实质影响** ⇒ **该轴不值得优化**（无论谁快）。

#### 2.1b 3.9 ns/chunk 的拆解与"可优化性"（2026-09-14 追补）

用**同长度（5,291）、同为 1 次派发**的空 `IJobParallelFor` 作对照，可把"每 chunk 调度开销"拆成两段：

| 项 | 3 轮实测 | 含义 |
|---|---|---|
| 空 chunk job 总计 | **3.61 / 4.20 / 3.86 ns/chunk** | 与 Unity 侧 2.89 同量级（区间重叠） |
| 固定派发地板（空 parallel-for，与 chunk 数无关） | **8.2 / 8.9 / 7.7 µs** | 每次 schedule 的固定成本 |
| **逐 chunk 增量** | **2.06 / 2.51 / 2.40 ns/chunk** | 占 57～62% ⇒ **这部分是真账** |

**⇒ 2.40 ns/chunk ≈ 8 cycles/chunk**，即"读 16 字节 `EntityBatchData` 描述符 + 一次空内核调用"。
EntityBatch 是**每个 batch 一个 chunk**，内核签名里的 `__count`（`CppGenerator` 的
`(void* context, const EntityBatchData* __batches, int __startIndex, int __count)`）只摊掉外层调用，
**摊不掉每个 batch 的这次读+调用** ⇒ **已在下限附近，没有可观的优化空间**。
唯一能再降的路径是"多个 chunk 共用一份描述符"（框架数据布局改造），且下列两条使其不划算：

1. 下游项目的**热路径完全不走 chunk 路径**（Melee / Flow / BFS 波 / 搬运全是 `IJobParallelFor`）
   ⇒ 即使把 chunk 派发优化到 0，**收益也是 0**；
2. chunk 路径本身的占比：5,291 × 3.9 ns ≈ **20 µs/次 schedule**，相对 125 ms/步的预算是 0.02%。

#### 2.1c ⚠ 两个 EntJoy 侧的框架发现（本轮副产物，比上面这个数更值得处理）

1. **`ENTJOY_DIAG_CSHARP_PHASE=1` 会把 chunk 派发实测值放大约 10×**：
   同一天、同一二进制，**关**诊断时 3.61/4.20/3.86 ns/chunk，**开**诊断时 **38.6 ns/chunk**。
   原因：`ChunkJobScheduler.cs:483` 的 4 次 `Stopwatch.GetTimestamp()` 与 **`Console.WriteLine` 落在
   被计时区之内**，而短探针的 7 次调度（2 预热 + 5 轮）**全落在前 24 次窗口内** ⇒ **每一轮都被污染，
   取中位数也挡不住**。⇒ **这是诊断仪自身的缺陷**（"观测改变被测量"），
   建议：计时/打印移出被测区，或改为"攒够样本后一次性打印"，并在文档里标注该开关会扰动短调度序列。
2. **C# 侧调度成本在暖机后接近于 0**：`[CPHS]` 实测首次调度 7.1 µs
   （`cache+hash` 3.3 ＋ `contextBlock` 2.5 ＋ `PInvoke` 0.3 ＋ `track+return` 1.0），
   **之后基本为 0.1～0.5 µs**，偶发 `cache+hash` 2.4 µs 尖峰。
   ⇒ **那 8.2 µs 的派发地板不在 C# 侧，而在原生调度器（线程唤醒/完成）**。
   注意：该结论**只在 chunk 路径上成立**（`[CPHS]` 只插在这条路径上），下游项目热路径走的是
   `IJobParallelFor`，未插桩 —— 若要判断后者的 C# 侧成本，需在 `JobScheduler.ScheduleParallelFor` 上补同款仪器。


### 2.2 间接访问（EntJoy 定位表 vs Unity `ComponentLookup`）

`EntityLocateB`（24 B/实体：`ChunkMemory` + `ChunkOffsets` + `SlotInChunk` + `Version`，见 `src/EntJoy.ECS/Storage/NativeEntityLocate.cs`）
每访问是"一次表读 + 地址算术"；Unity 的 `ComponentLookup<T>.Get` 多做一层 chunk 头 / type handle 校验。

换算成下游项目的口径（8 次邻居 × 1M 实体/步）：**EntJoy ≈30 ms/步，Unity ≈56 ms/步**。

### 2.3 存活标记维护（1M/步）—— **EntJoy 的设计在这里明显更优，但不可直接移植**

| 实现 | 机制 | ms |
|---|---|---|
| **EntJoy native** | **按字整写**：每 lane 读 32 个 `UnitAliveFlag` 字节 → 写 1 个 uint（**无原子**）= `AliveBitJob` | **0.019～0.027** |
| EntJoy native | 逐实体写 1 字节 | 0.107～0.203 |
| Unity 发布档 | `EnabledMask` **逐位** + 每 bit 一次 `Interlocked.Add`（chunk 的 `ChunkDisabledCount`） | 0.533 |
| Unity 发布档 | 逐实体写 1 字节 | 0.141 |

- `AliveBitJob` 注释里写的"**按字整写而不是逐位改**：并行 job 里同一个 ulong 的 64 位由同一 lane 负责 ⇒ 无竞争、
  也不必读旧位图"——本次实测把这个取舍量化了：**比 Unity 的逐位路径快 20～28×**。
- ⚠ **不是同一种实现的两个移植**：Unity 的 `EnabledMask` setter 必须每 bit 维护 `ChunkDisabledCount`
  （查询过滤依赖它），**不能**照搬按字整写。所以这条的正确用法是
  "**若不需要 enableable 查询过滤，就自建数组按字整写**"。

### 2.4 组件列就地写 vs 扁平写 —— **栈内比值 + 跨栈每臂（后者才是关键）**

口径：同一份数学（`pos += vel*dt`，8Hz 步长），1,015,808 实体，**3 轮交替配对**。

**栈内比值**

| 实现 | 列就地写（ns/实体） | 扁平写（ns/实体） | 扁平/列 |
|---|---|---|---|
| EntJoy native | 0.234 / 0.807 / 0.374 | 0.157 / 0.482 / 0.358 | **0.672× / 0.597× / 0.957×** |
| Unity 发布档 | 1.055 / 0.870 / 0.958 | 0.167 / 0.288 / 0.219 | 0.158× / 0.331× / 0.229× |

⇒ **栈内看：EntJoy 两条路径不可区分（0.6～0.96×）；Unity 明显偏好扁平（0.16～0.33×）。**

**跨栈每臂（容易漏看，但信息量更大）**

| 臂 | EntJoy native | Unity 发布档 | 判读 |
|---|---|---|---|
| **chunk 列就地写** | **0.210～0.337 ns/实体** | 0.958 | **EntJoy 快 2.6～5.0×** |
| 扁平写 | ~~0.358~~ | ~~0.219~~ | ⚠ **已撤回 → §2.4b** |

换算成每 chunk（EntJoy 192 实体/chunk、Unity 128）：**71.8 vs 122.6 ns/chunk**，
而空派发只有 3.9 vs 2.89 ns/chunk ⇒ **差异来自"每 chunk 的数据访问建立"（`GetComponentDataNativeArray` /
`chunk.GetNativeArray` 等），不是派发**。

⇒ **每栈都在"自己原生的形态"上更快**：EntJoy 强在组件列就地写（EntityBatch 描述符几乎零建立成本），
Unity 在扁平写上原本被记为更快——⚠ **但这一半已被 §2.4b 的 3 轮复测撤回**。
**整块都在 0.2～1.0 ns/实体（1M 合计 <1 ms），对预算无实质影响 ⇒ 不值得优化。**

#### 2.4b ⚠ 「扁平写 Unity 快 1.6×」撤回（2026-09-14 追加，同批 3 轮）

原表两臂口径不等（EntJoy 用 `Schedule(n, 0)` = auto tile，Unity 用 `Schedule(n, 64)` = 显式内批 64），
且原 EntJoy 值只取了单批运行的高位样本。3 轮复测（1M 实体，与 §2.4 同一探针）：

| 实现 | 轮1 / 轮2 / 轮3（ns/实体） | 中位 |
|---|---|---|
| EntJoy native 扁平写 | 0.0916 / 0.2147 / 0.1497 | **0.150** |
| Unity 发布档 扁平写 | 0.167 / 0.288 / 0.219 | 0.219 |

EntJoy 区间 **0.092～0.215** 与 Unity **0.167～0.288** **重叠，EntJoy 中位反而更快 1.5×**，
且 EntJoy 自身跨轮散布 **2.3×** ⇒ 该轴判为**不可区分**。
同一批的**组件列就地写**则复现并加强：EntJoy 0.2102 / 0.2235 / 0.3365 vs Unity 0.870 / 0.958 / 1.055
（**2.6～5.0×**）⇒ 结论：**EntJoy 的"chunk 列就地写"优势是稳的，"扁平写劣势"不存在。**

## 3. 对 EntJoy 自身的三条可执行结论

1. **凡"某种形态不可用"的结论，必须先排除 `NativeArray` 索引器 / 安全检查口径。**
   下游项目 §5.5 曾据"0.27 ms/chunk × 5,291 = 1,438 ms"判定"`IJobChunk` 在本项目规模下不可用"，
   并据此放弃了 chunk 就地写路线。本次实测：**每 chunk 调度只有 22.6 ns（托管档）**，原值高约 **1.2×10⁴ 倍**；
   那 1,438 ms 的真因是 **15 列 × 1M ≈ 15M 次 `NativeArray` 索引器访问**
   （`1,438 ms ÷ 15M ≈ 96 ns/访问`，与 [public/NativeArray-Index-Safety-Overhead-and-Fixes.md](../public/NativeArray-Index-Safety-Overhead-and-Fixes.md)
   记录的 167.7 ns/访问同量级）。⇒ 建议把"调度开销 vs 索引器开销"这一对区分写进该 public 文档的诊断清单。
2. **调度层与 Unity Burst：无可辨差距（早期结论已撤回）。** 3 轮交替配对后
   **2.89（Unity）vs 3.9（EntJoy）ns/chunk，区间重叠**；早期"Unity 快 3×"出自单轮读数。
   ⚠ 但要注意 **EntJoy native 相对自身托管路径快 5.7×**（3.9 vs 22.4 ns）—— 这是 native 绑定生效的直接证据，
   也是"凡涉及性能的判断必须用 native 档"这条纪律的又一次量化。
3. **领先项应固化为"设计资产"**：定位表紧凑布局（1.6～1.8×）、分布原子认领（1.8～1.9×）、
   **按字整写位图（20～28×）**、**组件列就地写（2.6～5.0×，得益于 EntityBatch 的零建立成本）**、
   小 chunk 内存占用（1.5×）。其中"按字整写位图"与"EntityBatch 就地写"两项建议补进 `public/` 供移植方参考。

## 3.5 由本报告产生的硬纪律（"小量级 × 大数量"必须双条件）

**这类数字要进结论正文，必须同时满足：① 同轮交替配对（≥3 轮）；② 先算它占总预算的比例。**
比例 **<1%** 的项一律只作"机制事实"记录，**不得作为路线选择依据**。

| 误判 | 当时声称 | 实测 | 错因 | 后果 |
|---|---|---|---|---|
| §5.5 | 逐 chunk 调度 0.27 ms/chunk ⇒ 1,438 ms | **22.6 ns/chunk**（托管档） | 把 15M 次 `NativeArray` 索引器访问算成了调度 | **放弃 chunk 就地写路线** |
| §4.1 E0 | 定位表 29.6 ns/访问 ⇒ "E3 是最大风险" | **native 4.0 ns/访问** | 托管档当 native 用 | **放弃 chunk-aware 热路径** |
| 本报告首版 | 每 chunk 调度 Unity 快约 3× | 3.9 vs 2.89 ns，区间重叠 | 单轮读数；且占预算 0.02% | 已撤回 |
| §2.4 / §3.6 | 扁平写 Unity 快 1.6×；每 job 调度 Unity 快 1.40× | 3 轮复测：扁平写区间重叠；**等口径调度 9.27 vs 4.71 = 1.97×** | **跨栈对照口径不等**（长度 + 内批粒度两变量混用；扁平写取了单批高位样本） | **§2.4b / §3.7 已修订**；"1.40×"低估了同参差距 |

⇒ 前两次都**改变了路线决策**，第三次只是噪声。**代价不对称，所以纪律必须是硬的。**

**关于"能否把调度层追平"的直接回答**：存在可调旋钮
（`ChunkJobScheduler.ScheduleChunkEntityBatchRawWithWorkerCapAndRangeSize(..., workerCap, rangeSize, ...)` 的 `rangeSize`，
生成的 `Schedule(query)` 扩展当前传 `0, 0` = 默认；另可换 `ScheduleChunkRangeRaw` 路径），
**但本报告未测这两个旋钮**，所以只能说"有旋钮"。而收益上限已被算术封死：整块 ~20～30 µs/步 = 预算的 **0.02%**，
即使压到 0 也测不出来。⇒ **不要为此投入。**

### 3.6 框架调度层吞吐 + 「单纯框架上能否追平 Unity DOTS」的结论

口径：空 `IJobParallelFor`（**完全不做事**），逐次 `schedule + complete`；EntJoy 长度 5,291（= chunk 数）、
Unity 7,936（= chunk 数）；各 3 轮取中位数。
⚠ **本表两臂口径不等**（EntJoy `batchSize=0` 自动 tile、Unity 显式内批 64，长度也不同）
⇒ 等口径复测见 **§3.7**：同参下为 **1.97×**；本表的 1.40× 应读作"**EntJoy 在 auto 档、下游项目实际形状**"的值。

| 每 job 调度成本 | **EntJoy + NativeTranspiler (C++)** | **Unity + Burst（IL2CPP 发布档）** | 比值 |
|---|---|---|---|
| 单次固定地板 | 7.4 / 8.0 / 11.6 µs → **8.0 µs** | 4.40 / 4.90 / 4.30 µs → **4.40 µs** | **Unity 快 1.82×** |
| **连发 1000 个** | 7.70 / 6.00 / 6.59 → **6.59 µs/job** | 4.98 / 4.71 / 4.56 → **4.71 µs/job** | **Unity 快 1.40×** |

**换算到下游项目**（每步 ~7 系统 + ~390 BFS 波 ≈ **400 次调度**）：EntJoy 6.66×400 ≈ **2.66 ms/步**；
Unity 同规模 4.71×400 ≈ **1.88 ms/步**；**差 ≈0.78 ms/步（0.78%）**（控制臂 3 轮复现：5.97/6.66/6.81）。

⇒ **这是 EntJoy 在框架层唯一"落后且量大到可换算"的项。** 且 §5.28.3 已把"波循环进内核"的上限量到
**1.4 ms/步**并证伪 ⇒ **这 2.66 ms 省不掉"调度次数"，只能靠降低单次调度成本**；
而 C# 侧≈0（§2.1c）⇒ 要动的是**原生调度器的提交/固定仪式/唤醒-完成路径**（旋钮清单见 §3.7）。

#### 9 项可比框架机制的总账

| 结果 | 项数 | 明细 |
|---|---|---|
| **EntJoy 领先** | **5** | 内存布局 1.5×、间接访问 1.6～1.8×、分布原子 1.8～1.9×、存活标记 **20～28×**、chunk 列就地写 2.6～5.0× |
| **Unity 领先** | **1** | 每 job 调度 **1.41×**（auto 档）／**1.97×**（等口径 7936×64，§3.7） |
| **不可区分** | **3** | 空 chunk 派发（区间重叠）、扁平裸指针/索引器随机读、**扁平写（§2.4b 已撤回）**；"地板"项两侧持平（口径校准） |

换算到本项目（1,015,808 实体、125 ms 预算）：

| 方向 | 项 | ms/步 | 占步均 |
|---|---|---|---|
| **Unity 领先合计** | 每 job 调度（6.66 µs，auto 档） | **≈0.78** | **≈0.78%** |
| **EntJoy 领先合计** | 间接访问 ≈26.0 ＋ chunk 就地写 0.59 ＋ 存活标记 0.51 | **≈27.1** | **≈27%** |

**结论（限于框架层、机制级）**

1. **"能否追平"这个提法方向是反的：EntJoy 并不落后。** 9 项里领先 5、不可区分 3、Unity 领先 1；
   **Unity 那一项换算后只有 ≈0.78 ms/步（0.78%）**，而 **EntJoy 领先的大块合计 ≈27 ms/步**。
2. **框架层唯一有数据支撑的 EntJoy 改进点是"每 job 调度 6.66 → 4.71 µs"**（等口径 9.27 → 4.71，§3.7），但它只值 **0.78 ms/步**
   —— 按下游项目 §15.6 的硬纪律（占预算 <1% 不进路线决策），**它不该驱动路线**。
   ⚠ 且该项**框架侧已提供通解**（**§3.8**：批提交通道，`Schedule()` 零改动，实测每 job 提交成本 **−48%**，
   外推后 6.66 → **3.3~3.5 µs 量级**，不再落后于 Unity 的 4.71）⇒ **它不是"待改进的框架缺口"，
   而是"调用方是否启用批"的选择题**。
3. ⚠ **边界**：以上全是**机制级**。框架调度成本只有**乘以算法的调度次数**才有意义（这里 ~400 次/步）；
   **"同一套算法在两栈各跑一步谁快"仍无数据**（需 B 栈 M1～M4）。
   框架层能判定的只有一句：**在框架提供的每一项机制上，EntJoy 都不构成劣势**——唯一一项劣势换算后不足 1%。

### 3.7 等口径复测（NP-4e，3 轮）：每 job 调度的差距是 **1.97×**，主变量是 **tile 数**

**为什么补测**：§3.6 的两臂长度与内批粒度都不同（5,291/auto vs 7,936/64），两个变量混在一起。
补三条臂（同进程、同一次运行、各 1000 连发、3 轮中位）：

| 臂（空 `IJobParallelFor`，连发 1000） | 3 轮 | 中位 | vs Unity 4.71 µs |
|---|---|---|---|
| **等口径 = (7936, 64)** | 10.12 / 8.10 / 9.27 | **9.27** | **Unity 快 1.97×** |
| 同长度 (7936)、auto tile | 8.06 / 6.77 / 6.04 | 6.77 | 1.44× |
| 同批次 (64)、本项目长度 (5291) | 7.26 / 7.19 / 7.06 | 7.19 | 1.53× |
| 控制臂 = (5291, auto)（= §3.6 原臂） | 5.97 / 6.66 / 6.81 | **6.66** | 1.41× |

**变量分离（中位）**

| 变化 | 代价 | tile 数变化 |
|---|---|---|
| 同长度下 auto → 显式 64 | **+2.50 µs** | ≈62 → 124 |
| 同批 64 下 长度 5291 → 7936 | **+2.08 µs** | 83 → 124 |

⇒ **A 栈原生侧每 tile 约 50 ns**（`ScheduleParallelForBatch` 的 tile 数组填充 + `SubmitOrPending` 提交；
`JobSystem_Scheduler.cpp:609-706`）⇒ **每 job 成本的主变量是 tile 数，不是长度**。
配合 §2.1c 的结论（C# 侧≈0.1～0.5 µs、固定地板 8.2 µs）可定位为：
**固定仪式 ~8.2 µs（存储/状态/令牌/退役链 + 唤醒）＋ ~50 ns/tile**。

**对 EntJoy 自身的读法**

1. **该轴是真差距，且比 §3.6 记的更大**（同参 1.97×）；但**下游项目实际走 auto 档**（全仓 `Schedule(len, 0)`），
   所以它要付的仍是 6.66 µs 那一档 ⇒ 每步影响 **0.78 ms（0.78%）**。
2. **可动的旋钮已明确**：① **tile 数** —— auto 档的 tile 粒度（`ResolveChunkSize`）直接决定每 job 成本，
   下游项目若要压低应传显式更大的 `innerBatchCount`（但 auto 已接近更优，见上表"同长度 auto 6.77 < 显式 64 9.27"）；
   ② **~8.2 µs 固定仪式** —— 这是 `tokens + 存储 + 退役链 + 唤醒/完成` 的总和。
3. **框架侧已有通解**：见 **§3.8** —— 批提交通道（显式 `BatchScope` / 隐式 `ImplicitBatch` + deferNotify 单次唤醒）
   把每 job 提交成本**减半**（同形状实测 8K parFor 5.0 → 2.6 µs/job），**`Schedule()` 零改动即可启用**。
   ⇒ 该轴不是"框架缺陷"，而是"调用方是否启用批"。下游项目每步 ~400 次里绝大多数是 BFS 波
   （第 N 波依赖第 N−1 波），**依赖未完成的 job 不进 pending** ⇒ 其帧内聚合收不到收益（选型结果，非框架缺口）。

### 3.8 ⭐ 框架侧通解：批提交通道（**"每 job 调度"这一缺点已由框架自身解决**）

> 依据：`src/EntJoy.Jobs/BatchScope.cs`、`src/EntJoy.Jobs/ImplicitBatch.cs`、`src/EntJoy.Jobs/Native/NativeJobScheduler.cs`、
> `src/NativeDll/JobSystem_Tiles.cpp:667-736`（`SubmitOrPending` / `FlushPendingSubmits`）、
> `src/NativeDll/Exports.h:99,103,105,112,113`、`src/NativeDll/JobSystem.cpp:66,70`；
> 实测见 `docs/archive/jobsystem/20260826-*` §§14~20 与 `docs/archive/jobsystem/20260830-NativeImplicitBatch-实现与基准.md`。

**机制（四条腿，§20.4 已收敛为统一实现）**

| 腿 | 实现 | 关键点 |
|---|---|---|
| **显式批** | `BatchScope`：`Add<T>`（IJob）/ `AddFor<T>`（IJobFor）/ `AddParallelFor<T>`（IJobParallelFor，含内批）×N → `Submit()` → `CompleteAll()` | 入队=快照拷贝、零 P/Invoke；`Submit()` 走**单次** `JobSystem_ScheduleBatch(descs[], count, outHandles)`；`Add after Submit` 显式报错 |
| **隐式批** | `NativeJobScheduler.SetImplicitBatchEnabled(true)` + 帧末 `EndFrame()` | **`job.Schedule()` 零改动**；只收 SubmitBatch 路径 job（ParallelFor / ParallelForBatch / Chunk / Entity）；`Complete()`/`IsCompleted()` 自动 flush（防死等） |
| **单次唤醒** | flush 时进入 `g_submitDeferDepth` 窗口 → 逐个 `SubmitBatch` → **一次** `WakePending()` | `JobSystem_Tiles.cpp:709-736`；`SubmitBatch` 内 `if (deferDepth<=0) bump+notify_all`（`ChaseLevScheduler.cpp:491`）⇒ 一个批只一次广播 |
| **依赖与安全** | 依赖**未完成**的 job **不进 pending**，走 continuation 立即提交 ⇒ 依赖顺序天然保持；入队 `AcquireState` / flush `ReleaseState` 防 handle 被 GC 后 batch 悬垂；关闭开关与 `Shutdown()` 都先排空 pending | `JobSystem_Tiles.cpp:667-704`；`ImplicitBatchTests` 5/5 |

**切换与默认**：`SetImplicitBatchEnabled` 是**互斥切换**——Native 收集（DLL 不可用时回退 C# `ImplicitBatch` 层）/ C# 层；
**默认关闭**（`g_implicitBatchEnabled{false}`，`JobSystem.cpp:70`；§20.1 因"Schedule 后不 Complete 不执行"的语义变化而显式选择 opt-in）。
启用成本 = **一行 API + 帧末一行 `EndFrame()`**；双后端（Native / 纯托管回退）语义一致。

**实测收益（EntJoy 自己的受控基准，同机 15 workers；空体 job 排除执行成本）**

| 形状 | 逐 job | 批路径 | 收益 |
|---|---|---|---|
| `IJobParallelFor`·8K ×100 | 5.0 µs/job | **2.6 µs/job**（BatchScope） | **−48%** |
| Mixed(100 IJob + 50 IJobFor + 50 parFor) ×200 | ~1.3 ms/帧 | **0.58 / 0.68 / 0.70 ms/帧**（显式 / C# 隐式 / Native 隐式） | **−45~−55%** |
| 帧内唤醒次数 | `waitFallbacks = 126` | **2~4** | 单次提交/唤醒 |
| Schedule 阶段每 job（隐式，仅挂 pending） | — | **0.6~1.7 µs**（IJob 0.75 / IJobFor 0.64 / parFor 8K 1.66） | 与逐 job 提交同量级，无额外预切分/唤醒 |

⇒ **每 job 提交成本减半，且不依赖具体项目、覆盖 tile 路径全部 job 族与依赖关系** ⇒ 这是**通解**。
把它接到 §3.7 的跨栈对照上：EntJoy 的每 job 成本从 6.66 µs 量级降到 **3.3~3.5 µs 量级**（按 −50% 外推），
即**不再落后**于 Unity 发布档的 4.71 µs（外推值，非同一 host 实测，标注为估计）。

**该通解**不**覆盖的三件事（引用时必须一并说明）**

1. **空 job + worker 常驻自旋**时"单次唤醒"收益**不可测**（增量 10 实测：`IJobParFor` 100 job 8.75 vs 8.25 ms；
   `PrewakeWorkers` 为 no-op、Chase-Lev worker 常驻自旋 ⇒ 唤醒本就近乎免费）。批省的是**提交侧仪式**，
   唤醒收益只在 worker 休眠场景显现。
2. **flush 时仍逐 job 建 tile 数组**：每 job 的 tile 数 × 提交侧成本没被摊掉
   （§3.7 测得 ~50 ns/tile，该常数随 host 变）。"共享批上下文 / 共享 storage 摊平仪式"只在 §18 留了
   `ScheduleBatch(descs)` 这个**入口**，**尚未实现**（§18 原文：后续共享 storage/仪式摊薄的入口已就位）。
3. **严格串行链**（每个 job 都等上一个完成）：依赖未完成的 job 不进 pending ⇒ 每个 job 仍各有一次提交+唤醒。
   这与 Unity 的 `JobHandle` 语义同构，属**调度语义固有**，不是框架缺口。

## 4. 本报告的局限（引用前必读）

1. **只测了机制，没有整步对比。** 上述都是微基准。下游项目 B 栈（Unity 侧）**尚无仿真逻辑**
   （M1～M4 未开工），A 栈纯逻辑步均 100.5 ms **没有对应的 Unity 整步数字可比**。
   因此**本表不能回答"同一套算法在两栈各跑一步谁快"**。
2. **Unity 侧数字分两档，混用会读反结论**：Editor 档恒开 `ENABLE_UNITY_COLLECTIONS_CHECKS`，
   会把间接访问读成 14.19 ns（发布档 7.17）、把"位图 vs 字节列"读成 0.98×（发布档 3.79×）。
   **本表只用发布档**。
3. **跨运行散布**：EntJoy 侧原子项 ±5% 内很稳；随机访问项跨轮 3.58～4.84 ns（±15%）。
   故表中给的是区间，不是单点值。
4. **A 栈侧探针要求"每步写回"档**（`CPUBATTLE_TRANSPORT_SCATTER=step`）——懒写回档下组件列与扁平工作区脱钩，
   等价性判据会失效。这是下游项目的配置约束，不影响本表数值。

## 5. 复现

- EntJoy 侧：`ComputeShaderBattleSimulation` 仓，`CPUBATTLE_NATIVE_PROBE=1` +
  `CPUBATTLE_TRANSPORT_SCATTER=step` + `CPUBATTLE_AUTODEPLOY=1` + `CPUBATTLE_AUTOEXIT=30`，
  跑 `res://CPUBattle/Scenes/CPUBattleEcs.tscn`；输出 `[NP-4]`/`[NP-4b]`/`[NP-4e]`/`[NP-5]` 与 `CPUBATTLE_NP_CSV`。
  探针源码：`CPUBattle/Scripts/CPUBattleNativeProbe.cs`、`CPUBattleNativeProbeChunk.cs`、`CPUBattleNativeProbeAlive.cs`。
  **等口径臂（§3.7）**：`CPUBattleNativeProbeChunk.Run` 末尾的 NP-4e 三条臂，CSV 前缀 `NP4e,`；
  本轮产物 `logs_np4e/run{1,2,3}.log` + `logs_np4e/np4e_{1,2,3}.csv`。
- Unity 侧：IL2CPP 发布档 player（`W0BuildTool.BuildWindowsIl2Cpp`）+ `W0_PLAYER_RUN=1`，输出 `[W0-P4]`/`[W0-P5]`。
- ⚠ **构建失败时旧 DLL/旧 player 仍能跑出"格式正常"的数字**——取数前必须核对
  `NativeTranspiled.dll` / `GameAssembly.dll` 的时间戳与大小（本次两侧各踩过一次）。
