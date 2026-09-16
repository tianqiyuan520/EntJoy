# NativeArray 索引安全检查：实测开销、两处并发缺陷与修复记录

本文记录 2026-09-12 对「Job 内 `NativeArray` 索引的并行读写安全检查」的实测、两处真实并发缺陷的定位与修复，以及 Span 快路径的收益。结论均已由测试与可复现基准支撑。

相关契约见 `Runtime-Contracts-and-Known-Limitations.md` 的「并行读写冲突检测」一节。

## 一、问题起点

`NativeArray<T>` / `NativeList<T>` 的索引器每次访问都会调用 `SafetyHandleManager.CheckReadAndThrow` / `CheckWriteAndThrow`。在 Job 内（`JobIdentity.CurrentContext != 0`）读访问会进入 `RegisterRead` 做读者登记。最初实测发现同线程对同一容器反复索引时，每次访问都要付出 `ConcurrentDictionary.GetOrAdd + Monitor` 的开销。

## 二、实测数据（Native 后端）

基准：`tools/SafetyLockOverheadBench`（1,048,576 实体 × 20 次，5 轮取中位；与 `samples/Godot/SpritesRandomMove` 的 `NativeMoveJob` 逐行同构）。

| 形态 | 优化前 | 优化后 | Span 参照（无检查） |
|---|---|---|---|
| 同容器反复索引（读） | 54.73 ns/访问 | **0.85 ns/访问** | 0.09 ns/访问 |
| NativeMoveJob 同构（每元素 2 读 2 写，4 容器） | 2.16x vs Span | **2.13x vs Span** | — |
| ↑ 其中每次索引的残余检查开销 | ~17.8 ns/访问 | **0.76 ns/访问** | — |

对照（修复前测得的基线，用于界定「检查开销」这一项）：

| 路径 | ns/次索引 |
|---|---|
| Job 内，关闭全部检查 | 0.47 |
| Job 内，豁免句柄（ECS chunk view 同构） | 1.82 |
| 主线程，普通句柄（`ctx==0`，不登记） | 2.75 |
| **Job 内，普通句柄（进入 `RegisterRead`）** | **19.64** |

机制微基准交叉验证：`ConcurrentDictionary.GetOrAdd + lock(HashSet) + Add` 单次 17.65 ns，与容器内实测 19.64 ns 吻合，确认瓶颈就是这条路径。

## 三、缺陷 1：写者声明按 tile 释放（Native 后端）

**症状**：`Complete()` 之后主线程访问容器抛 `"NativeContainer is being written by an active job; Complete() before accessing it from the main thread."`，且**持续失败**（后续访问恒被拦）。

**定位**：用「反射读取 `SafetyHandleManager` 私有状态」的手段在失败点快照（不改引擎源码）：`_ctxWrites=0`（字典条目已空）但 `slot1/slot2/slot3` 的 `_writerCtx` 全部残留同一个 ctx —— 说明释放循环从未覆盖这些 index，条目却已从字典移除。

**根因**：读写释放点不对称。`ReleaseReadsForContext` 在 **Job 完整结束**调用，而 `ReleaseWritesForContext` 在 **每个 tile 的回调 finally** 调用。同一 Job 的多个 tile 共享同一 ctx：

1. tile A 结束 → `TryRemove(ctx)` 移除条目并释放它看到的 index；
2. tile B 仍在运行 → 写容器时走 `TryGetValue` + `GetOrAdd`，拿到（或新建）一份**随后被丢弃**的 list，其 index 从此无人释放；
3. `_writerCtx[index]` 永久残留 → 主线程访问被永久误拦。

**修复**：
- 写声明释放移到 Job 完成点（`NativeJobCore.Cleanup` / `ManagedCleanup`；C++ 侧 `RunBatchCleanup` 只认领一次，在所有 tile 之后执行），删除 4 个 tile 回调里的逐 tile 写释放；
- `TryAcquireWriteContext` 改为单次 `GetOrAdd`（不再 `TryGetValue` + `GetOrAdd` 两步）；
- `ReleaseWritesForContext` 改为**保留条目只清空**，使「同一 ctx 只有一份 list」恒成立（条目随 ctx 存活，ctx 数量有限，不会无界增长）。

**验证**：同一配置（N=262144 / batch=16384 / 16 tile）修复前第 14 次尝试触发，修复后 **40/40 无触发**，`_ctxWrites` 无增长。回归护栏：`MultiTileWriteJob_AfterComplete_MainThreadNotBlocked`（对旧行为在 attempt 5/job 3 失败，对修复通过）。

## 四、缺陷 2：读者登记配对的校验顺序（两后端共有）

**根因**：`RegisterRead` 原实现是「先校验条目仍现役，再 `set.Add(index)`」。两步之间条目可能被并发 `ReleaseReadsForContext` 移除，此时：

- `set.Add` 在已丢弃的集合上返回 true 并执行 `+1`；
- 释放侧只递减「它移除时看到的那份集合」的成员；

→ 该 `+1` 永不配对，`_readerCount` 永久为正，主线程访问被永久误拦。

**修复**：改为「先 `Add`，再校验条目仍是现役；不现役则撤销自己的登记并重新登记」。`Add` 返回 true 即代表本次是该集合内该 index 的首次登记，撤销自身登记是安全的。

**证据**：Managed 后端失败点快照为 `ctxReads=0` 而槽位读者计数为正；递增调用栈为 `NativeArray.get_Item ← WriteJob.Execute ← ParallelCache.Run`。

> 注意：不能用「每槽位归属单个读者 ctx」的数组替代「ctx → index 集合」—— 多个 Job 可以**并发读同一容器**（读-读不冲突），单槽位归属无法表达多读者，会把「已由别的 ctx 持有」误判成幂等，破坏读者计数语义（该方案实测引入新失败，已放弃）。

## 五、Span 快路径

热循环里对同一容器反复索引是绝大多数 Job 的形态。`RegisterRead` 增加 thread-static 快路径，键为 `(ctx, 容器 index, 句柄代际)`，命中即返回，跳过 `GetOrAdd` 与 `Monitor`。命中即安全的依据：

1. 只有登记成功（`+1`）才写入缓存，而 `+1` 与释放侧 `-1` 成对；
2. `ctx` 来自 `JobIdentity`（`[ThreadStatic]`），Job 退出会还原/换值，故命中时必然仍处于写入缓存的那个 Job 生命周期内；
3. **句柄代际参与比较**：index 释放后复用必然递增代际 → 缓存自动失效并重新登记，不削弱 Use-After-Free 检测；
4. 豁免句柄在首个访问就提前返回，永不写缓存。

## 六、调用方建议（SPI）

- 热循环内直接持有 `NativeArray` 成员并逐元素索引的 Job（如 `IJobParallelFor`），在循环外取一次 `Span<T>`：`var positions = Positions.AsSpan();`。`AsSpan()` 只做一次检查并登记读者，循环内零检查。`SpritesRandomMove.NativeMoveJob` 即按此改造，实测 **2.1~2.2x**。
- `IJobEntity` 生成的代码本身就用 `GetComponentDataSpan`（零 per-access 检查）；`ArchetypeChunk.GetComponentDataNativeArray` 走共享豁免句柄，检查开销约 1.35 ns/访问。
- 注意 `NativeArray` 的 `implicit operator ReadOnlySpan<T>` **不做任何检查**（不登记读者），需要安全检查时请用 `AsSpan()` 而不是该隐式转换；测试 `MainThreadAccess_WhileSpanJobReads_Throws` 专门守护这一点。

## 七、Managed 回退后端：通解已落地（2026-09-16）

Managed 回退后端（`JobScheduler` 在 NativeDll 加载失败时的 fallback）与 §§三、四 属同一根因族。2026-09-16 按
「完成发布顺序 + 声明释放点 + ctx 唯一性」三条一次修完，此前记录的「复现器挂死在 `Complete()`」与
「xUnit 全量在 Managed 下不稳定」两个现象都不再复现。

### 三处改动（`src/EntJoy.Jobs/Managed/`）

1. **完成发布晚于声明释放**（原成因 1）。`ManagedJobHandle.Signal` 里 `Remaining` 归零发生在释放**之前**，而
   `IsCompleted`/`Wait()` 只认 `Remaining == 0` ⇒ 主线程在「计数已归零、声明未释放」的窗口内 `Complete()` 立即返回，
   紧接着访问容器被误拦。修法：`ManagedCompletion` 增加 `_declReleased` 门；`Signal` 中**先释放写/读声明、再置位、
   最后 `_done.Set()`**；`ManagedCompletion.IsCompleted` 与 `ManagedJobHandle.IsCompleted` 都要求
   `Remaining == 0 && _declReleased == 1`；`Reset()` 清零。等待、依赖判定、`Complete()` 共用这一处定义。
2. **写声明在完成点释放，不再按 tile 释放**。Native 侧按 §三 早已如此，Managed 侧却仍在 `ExecuteTileTask` 的 finally 里
   逐 tile 释放 ⇒ 与仍在运行的同 ctx tile 竞争：登记可能落进一份已被 `TryRemove` 的 list，从此无人清理 ⇒
   `_writerCtx[index]` 残留 ⇒ `Complete()` 后主线程被误拦。该条是修完第 1 条后才暴露的（失败变体由全部
   `being read` 转为全部 `being written`）。修法：Managed 侧写释放移到 `ManagedJobHandle.Signal` 的完成点
   （与读释放、发布点同处），删除 tile 级调用。
3. **每次调度唯一 ctx**（原成因 2）。托管 ctx 原为 `RuntimeHelpers.GetHashCode(box)`，而 box 由
   `SingleCache<T>`/`ParallelCache<T>` 池化复用 ⇒ 相邻两次 Job 可能拿到同一 ctx，幂等写登记与 `_readMark` 快路径跨 job 串味。
   修法：新增 `ManagedJobScheduler.NextCtx()`（`2^40 + 递增计数`；基准取 2^40 以避开 32 位 ctx 值域，且低 32 位在
   `_readMark` 打包下仍唯一）；tile 侧改为从 `task.Completion.HostCtx` 取 ctx（同一 Job 的所有 tile 天然共享）；
   `ManagedCompletion.Reset()` 同时清 `HostCtx`，槽位复用不继承上一个 Job 的 ctx。

### 验收（本机 16 核；Managed = 临时改名 `bin/NativeDll.dll`，且必须在 `EntJoy` 目录下运行）

| 项 | 修前 | 修后 |
|---|---|---|
| `ParallelReadWriteContainmentTests`（Managed） | 4/4 红（全 `being read`） | **6/6 绿** |
| xUnit 全量 191 条（Managed，连跑 5 遍，对齐 CI 的 5 遍循环） | 首遍即红（CI 现场 attempt 73/job 3） | **5/5 绿** |
| xUnit 全量 + containment 类（Native） | 绿 | **绿（2/2 + 1/1，零影响）** |
| `tools/SafetyInterceptProbe`（Managed，两轮 ctx 复用） | §九 记录的第 2 轮不再拦截 | **两轮均拦截；`Complete()` 后首个访问即可读（重试=0）** |
| `repro`（Managed，默认 `40 262144 16384`） | 曾**卡死在 `Complete()`** | **6 s 跑完；`触发=0 _ctxWrites max=0 _ctxReads max=0`** |

CI 现场对应关系：`framework-test` 用 `-p:EnableNativeCompile=false` 构建、且该 job 没有 NativeDll ⇒ 那 191 条测试
**必然**跑在 Managed 上；2026-09-16 的 run 第 1 遍死在 `MultiTileWriteJob_AfterComplete_MainThreadNotBlocked`，正是成因 1。

> 测量陷阱（踩过）：这些工具按 CWD 搜索 NativeDll。若在**游戏仓目录**下运行 EntJoy 的工具，会静默加载
> `ComputeShaderBattleSimulation/.godot/mono/temp/bin/Debug/NativeDll.dll`（游戏侧的另一份构建），于是「Managed 验收」
> 实际跑成了 Native。上表数据全部在 `EntJoy` 目录下取得，并以日志中的 `falling back` / `Loaded NativeDll` 行确认后端。

> 历史：此前尝试过「只调释放顺序」「只加每次调用唯一 ctx」「两者都加」「完成阶段状态机（Pending/Finalizing/Done）+ 唯一 ctx」
> 四种组合，均因未同时满足「复现器不挂起 + Managed 下全量稳定全绿」而全部回退（未提交）。本次三条同批落地，两个验收项均通过。

**影响面**：`JobScheduler` 默认走 Native，本仓库测试集与 Godot 示例均运行在 Native 上，因此该缺陷不影响当前主路径；仅在 NativeDll 加载失败的回退场景下才会暴露。

## 八、复现与基准工具

- 性能基准：`dotnet run -c Debug --project tools/SafetyLockOverheadBench/SafetyLockOverheadBench.csproj`
- 残留/泄漏复现器：`dotnet run -c Debug --project tools/SafetyLockOverheadBench/repro/repro.csproj -- <attempts> <N> <batch>`（默认 `40 262144 16384`；隐藏 `bin/NativeDll.dll` 可切到 Managed 后端复现）
- 判定标准：`触发器: 触发=0  残留状态=-  _ctxWrites max=0 _ctxReads max=0`

## 九、快路径的语义负向用例（P1-10 回归）与两个构造陷阱

`RegisterRead` 的快路径（`_readMark`/`_readMarkEpoch`，见 `AtomicSafetyHandle.cs`）把 job 内跨容器交替访问
从 ~167 ns/访问 降到 ~2 ns/访问。**快路径只能省"重复登记"，绝不能漏"新登记"**，因此需要一条负向用例：

```
dotnet run --project tools/SafetyInterceptProbe/SafetyInterceptProbe.csproj -c Debug     # Release 同样有效
```
判据（全部 PASS）：无 job 时主线程正常 → **两轮** job 运行期主线程读/写均被拦截 → `Complete()` 后主线程可读可写；
两轮是刻意的：连续两个 job 会**复用同一个 ctx**（实测两轮体内 `ctx` 相同），这正是"快路径误判已登记"最容易出错的形态。

两个构造陷阱（都踩过，探针注释里也写了）：

1. **必须在 worker 上跑**：单 tile/小规模的托管 job 会**内联在调用线程执行**（实测体内 `JobIdentity.CurrentContext == 0`），
   此时任务已经结束、也没有 job 侧登记，负向用例天然不成立（曾据此得出过"未拦截 = 快路径吞掉了违规"的错误结论）。
   探针因此打印并断言 `体内 ctx != 0`、`读时 job 仍在运行`。
2. **Release 不等于"没有检查"**：`ENTJOY_SAFETY`（Debug）与 `ENTJOY_SAFETY_BOUNDS`（Release）**编译的是同一段**
   读/写持有追踪代码（`AtomicSafetyHandle.cs` 内所有 `#if` 都是 `||` 形式），两种配置都能验拦截。

### 顺带把 Managed 后端的缺陷复现成了确定现象（非本轮引入）

把本探针切成 Managed 回退后端（把 `JobScheduler.Initialize()` 换成 `ManagedJobScheduler.Initialize()`）后：

- **第 1 轮**：拦截正常；
- **第 2 轮**（同一 ctx 被复用）：主线程读/写**均不再被拦截**（`_readerCount` 没被加上）；
- 随后第 1 轮滞后的释放会对第 2 轮没加过的计数做递减 ⇒ `_readerCount` 变负 ⇒ **`Complete()` 之后主线程访问被永久拦截**
  （Release 配置下 3/3 稳定复现，表现为未捕获异常直接终止）。

这正是 §七 里已登记的两个成因（完成计数先归零、读声明后释放 + box 池化导致 ctx 复用）的叠加效果，
**与本轮快路径改动无关**（旧的 `_ctxReads[ctx]` 幂等集合在同样的组合下同样会跳过 `+1`）。
结论不变：`JobScheduler` 默认走 Native，主路径不受影响；探针固定用 Native 后端，故两种配置下均稳定全绿。

> 2026-09-16 更新：§七 的三处改动落地后，把 DLL 隐藏让 `JobScheduler.Initialize()` 自然落到 Managed 重跑本探针：
> 1/2 两轮均拦截、两轮 `Complete()` 后首个访问即可读（重试次数=0），上面那条"第 2 轮不再被拦截"的确定现象已消失。
> Managed 下两轮体内 ctx 实测为 `2^40+1` / `2^40+2`（每次调度唯一），Native 下两轮仍为同一 ctx，两条后端都通过。
> 注意复测时必须在 `EntJoy` 目录下运行：按 CWD 搜索会捡到游戏仓 `.godot/mono/temp/bin/...` 里的另一份 NativeDll。
