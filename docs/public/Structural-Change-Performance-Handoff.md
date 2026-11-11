# 结构变更性能：现状与续接说明

## 当前数字

10 万实体、25 轮、逐操作交错、每轮取最小、同一构建内成对比较（`tools/EcsComparison --struct 100000 25`）：

| 操作 | EntJoy | Arch | 比值 |
| --- | --- | --- | --- |
| 创建实体 | 36.9 ns | 32.1 ns | 1.15 |
| 添加组件 | 85.0 ns | 80.2 ns | 1.06 |
| 移除组件 | 71.8 ns | 81.4 ns | 0.88 |
| 销毁实体 | 29.7 ns | 27.4 ns | 1.08 |

口径见 `tools/EcsComparison/obj/struct-amend.txt`（当前实现最近一次）。同提交前的 `struct-final.txt` 为 1.08 / 1.05 / 0.85 / 1.03；两次差落在同目录记录的比值漂移（0.05–0.1）内，引用时只用一份并把文件名写出来。

测量输出落盘在 `tools/EcsComparison/obj/`，文件名以 `struct-` 开头。数值随实现变化，引用前重新测量。

## 入口层与工作的分界

`--ab-pair absent-destroy` 对已销毁实体重复调用销毁，走完整入口后核心早退，测得入口固定开销约 14–15 ns；销毁全程 36 ns，因此搬移工作约 21 ns，比 Arch 的全程 26 ns 更少。四项相对 Arch 的差距全部来自入口层，不是搬移。

入口层的构成（逐项实测）：结构锁约 3.7 ns，`JobBookkeepingIdle` 的十项判定约 1.3 ns，其余分散在 `CheckDisposed`、关系判空、存活校验与核心前置。

## 已生效的结构改动

- 组件槽位清零：块创建时整块 memset 组件区，块内记高水位，追加只清复用过的槽位；关系列仍无条件写 -1（全零代表实体 0/v0）。
- 回收栈入栈内联，扩容隔离到 `NoInlining` 的慢路径。
- `InvalidateStructuralQueries` 与 `QueryBorrowVersion.Invalidate` 内联；令牌失效先读后换，无人借阅时不做带栅栏交换。
- 原型查找：与顺序无关、只混入 Id 的哈希；映射为「集合哈希 → 原型」单值字典，碰撞时线性兜底。
- 块元数据记录是否存在启用位图；追加与移除实体在无启用组件时跳过那趟空转遍历。
- 结构变更的空闲判定读一个记账标记：记账写入处置 1，全空路径重算，偏向保守。
- 实体位置打包成 `chunkIndex << 16 | slot`，`EntityIndexInWorld` 从 24 字节降到 16 字节。
- 迁移边表按 `ComponentType.Id` 索引的数组，取代 `Dictionary<ComponentType, Archetype>`。
- 无 job、无观察者、无关系时跳过 job 完成记账、销毁通知捕获、关系清理与关系令牌失效。
- 空 Chunk 延迟回收，销毁不再搬移 Chunk 与重写位置索引；内存由 `CompactChunks` 显式归还。

## 模块结构

`src/EntJoy.ECS` 现有 116 个源文件，最大的文件 483 行。三个原本超千行的类按职责拆成了 partial 文件：

- `ChunkJobScheduler`：主文件留共享状态；`Schedule`（调度入口与核心）、`Cache`（缓存查找构建）、`CacheTypes`（三种缓存的键与实例）、`EventBuffers`（事件元数据与 drain）、`Context`（上下文块、托管回退）。
- `EntityManager`：主文件留状态、构造与处置、原型表；`Creation`、`Destroy`、`Components`、`Compaction`、`Diagnostics`、`JobTracking`、`Location`、`SharedComponent`（含 `.Create`）、`Relation*` 系列、`MultiRelation*` 系列、`Observer`、`SnapshotValidation`、`QueryCache`、`DebugJson`。
- `Archetype`：主文件留块簿记、容量、移除与迁移边；`Matching`（构造与查询匹配）、`Chunks`（块创建与 Slab 分配）、`Storage`（槽位读写与原型间复制）、`Lifecycle`（版本、清空、释放）。

拆分只搬移代码，没有改动实现。

## 按 id 的组件元数据

`ComponentTypeManager` 在注册时同步维护按 id 的 `ComponentType` 数组，`GetComponentType(int id)` 一次数组索引即可。原先“id → Type → ComponentType”两次字典探测的调用点已全部改掉。需要 `Type` 对象本身的地方仍走 `GetTypeByComponentType`。

## 实测证伪的方向

- 去掉结构锁对创建没有影响（旧结论），但用入口探针隔离后，锁在销毁入口占 3.7 ns。以探针结果为准。
- `JobBookkeepingIdle` 的十项判定只值 1.3 ns，合并成单一标志收益有限。
- 去掉 `UpdateEntityLocation` 的 `Chunk` 结构体拷贝、`CopyComponentsTo` 的源结构体拷贝，均无收益。
- 给入口方法加 `AggressiveInlining` 无收益，JIT 已内联。
- 把 `GetComponentType(typeof(T))` 换成静态泛型缓存会改变异常语义：非法组件类型首次失败会被固化进静态构造，后续抛 `TypeInitializationException` 而非每次校验后抛 `InvalidOperationException`。凡接收用户类型的公开 API 都不能这样换。

## 追平 Arch 的边界

四项里只有移除组件低于 Arch（0.88），其余三项在 1.06–1.15，与上面表格同源。差值全部落在入口层：销毁的入口固定开销约 14 ns，而 Arch 的入口只有两三次调用。实测可压缩的部分——结构锁 3.7 ns、空闲判定 1.3 ns、存活校验 0.7 ns——合计约 5.7 ns；再往下就必须去掉「结构变更内部线程安全」「job 记账」「两张位置表」中的某一项，那是能力取舍，不是代码优化。

两处不可压缩的成本：结构变更写完新组件值的第一次触碰是冷缓存缺页（约 6.8 ns，改数据布局才能动）；`Buffer.MemoryCopy` 对小固定尺寸已是 JIT 内建，手写尺寸分派反而更慢（实测 add 93→121 ns）。

## 与 Arch 逐函数对照的结论

| 环节 | Arch | 本仓 | 差额归属 |
| --- | --- | --- | --- |
| 创建 | `RecycledIds.TryDequeue` → `GetOrCreate` → `Archetype.Add` → `EntityInfo.Add`（一次结构体写）→ `Size++` | 同序，外加两张位置表、版本表、查询失效 | 0.95：镜像写是唯一剩余差额 |
| 块追加 | `Entities[Size]=entity; Size++` | 同，外加首次使用槽位的清零 | 已改：块创建整块 memset + 高水位 |
| 迁移 | `World.Move`：`destination.Add` → 复制 → `source.Remove` → 两处 `EntityInfo.Move` | 同序，外加原生镜像与查询失效 | 添加 1.03 |
| 移除 | `Chunk.Transfer` 搬末位 + `EntityInfo.Move`（只改一个字段） | 同，swap-pop 已改为只改槽位 | 0.89 |
| 销毁 | `Archetype.Remove` + `EntityInfo.Remove` + `RecycledIds.Enqueue` | 同序，外加失效与镜像清零 | 1.08 |

**本仓比 Arch 多出来的、改不掉的部分**：

- `EntityLocateB` 原生镜像每实体 24 字节（`ChunkMemory`/`ChunkOffsets`/`SlotInChunk`/`Version`），实测占创建 4.4 ns。`TryGetComponentPointer`、`WriteComponentRange`、快照克隆、原生查找四处都读它，改成派发前整表重建要同时改四处，收益与风险不成比例。
- 结构变更的查询失效（Arch 无查询令牌机制）。
- 结构锁与 job 记账（Arch 没有，代价已在入口层，入口探针实测 3.7 + 1.3 ns）。

**接口粒度是线索**：Arch 的 `EntityInfo` 把「只改槽位」与「改原型加槽位」拆成两个重载，本仓原来只有全量写一个入口，于是同 chunk 搬移也重写了原型与列偏移。补一个只改槽位的入口，移除与销毁各降 3–4 ns。

## 非结构路径的当前状态

`--ab-pair`（9 轮，`obj/` 下）：`query` 对 `arch-query` 为 **0.51**（三组件）/ 0.88（两组件），`chunk` 对 `arch-chunk` 为 1.03，`query-movenext` 为 1.09。结构变更的改动没有拖慢遍历。

## 查询快照的分配

`QueryTraversalPlan` 的 Chunk 快照按两代缓冲复用（`src/EntJoy.ECS/Query/QueryTraversalCursor.cs` 的 `SnapshotGenerations`）：结构变更后的首次查询改写上一代，稳态不再分配数组。跨两代仍存活的游标早已因借用令牌失效，会在下一个 Chunk 边界抛出；数组只增不减，改写期间读到的是旧值而不是越界地址。

`--query-snapshot 100000 300`（10 万实体、双 int 组件、`DOTNET_TieredCompilation=0`）成对测量：结构变更后首次查询 57 B/次、紧随其后 0 B/次，重建段比复用段多 0.84–1.69 μs（三次 1.69 / 0.84 / 1.13）。改动前每次重建按「块数 × 列数」新建 3 个数组；同一台机器、双 float3 布局的探针在改动前为 16 328 B/次、重建段多 9.2 μs。批量结构变更的 `--struct` 不覆盖这项成本，逐帧结构变更的世界每查询每帧各付一次。

## 规模无关性

同一构建在 1 千、1 万、100 万实体下各跑 7 轮（`obj/` 下对应 `--struct` 输出；与上面 25 轮表格不是同一次测量，只读同一行内的规模趋势）：创建 0.89 / 0.94 / 0.99，添加 1.08 / 1.07 / 1.10，移除 0.96 / 0.94 / 1.01，销毁 1.15 / 1.14 / 1.13。块创建整块 memset 的代价在小世界与百万级都成立，不需要按规模换策略。

## 稳定性不变量

以下每条都有对应的代码位置与（除注明外）用例；改这些地方前先确认不变量还成立。

**块是值类型，只读视图可持副本**
- `Chunk` 经 `ChunkList[i]` 或 `foreach` 取到的是副本。改其托管字段的成员（`AddEntity`、`RemoveEntity`、`InitializeSlot`）是 `internal`，只能由 `Archetype` 经 `ref` 调用。
- 副本也必须生效的状态都在非托管内存里（组件列、启用位图、变更位图）：副本共享同一块内存，读写在副本上同样生效；`_entityCount`、`_highWater` 这类托管字段不做跨副本递增。
- 用例：`SwapPopLocationConsistencyTests`（搬移后两表槽位一致、块基址不变）。

**swap-pop 只改槽位**
- 同块末位实体搬到空洞时原型、块基址、列偏移都不变，位置更新只写槽位（`MoveSlotWithinChunk`）。搬移槽位取自 `RemovalResult.MovedEntitySlot`，不依赖调用点的入参。

**块复用后的清零**
- 组件数据区在块构造时整块 memset，块内记 `_highWater`；追加到从未用过的槽位直接跳过清零，复用过的槽位逐列清零。块内存复用时一定走构造（`new Chunk(...)`），所以高水位与清零状态不会跨复用残留。

**记账标记只数真实条目**
- 结构变更快路径读 `_jobTrackingDirty` 一个标记。重算时只数真的持有 Job 的集合：`_archetypeJobs` 的键登记后不删除，用它的 `Count` 当判据会让标记永远停在 1。
- `PruneCompletedJobsNoLock` 与 `ClearJobTrackingNoLock` 结束时都会重算。
- 用例：`JobTrackingIdleFlagTests`（调度→完成后标记回到空闲）。

**位置表的打包上限**
- 槽位占低 16 位（`PackedSlotLimit` = 65536，容量不得超过它）；块号占高 16 位且必须落在正数区（`PackedChunkLimit` = 32768，块数不得超过它），因为 `ChunkIndex` 用算术右移读出，再高会读成负数，`0xFFFF/0xFFFF` 正好等于清空哨兵 -1。
- `Archetype` 构造时校验容量，分配新块前校验块数；两处都在建立边界的入口，逐实体路径上没有分支。
- 越界没有兜底：`SetLocation` 会静默截断到低 16 位，读写落到别的实体上。`ClearLocation` 写 `Address = -1` 后块号读 -1、槽位读 65535，存活判定只看 `Archetype`。
- 用例：`PackedLocationLimitTests`（边界值、越界读数、清空读数）。

**原型查找的哈希**
- 与顺序无关，每项先过 splitmix32 终混再累加；直接累加 Id 会让 id 之和相等的集合恒碰撞。
- 哈希碰撞时走线性兜底，且单个哈希只缓存一个原型：碰撞的那个原型此后每次都重新比对，所以哈希质量决定兜底频率，不是只影响一次查找。

**查询快照两代复用**
- 快照数组只增不减：结构变更后首次查询改写上一代，稳态不分配。改写期间另一个游标读到的是旧值（借用令牌已失效，下一个 Chunk 边界抛出），不会越界。
- 用例：`QueryTupleTests`、`EntityQueryCacheTests` 覆盖结构变更后的查询结果；分配与耗时用 `--query-snapshot` 复核。

**释放先清完再抛错**
- 原型释放失败不中断整体清理：逐个收集异常，照常归还 slab、定位表与回收栈，末尾抛 `AggregateException`。`EntityManager.Dispose` 与 `Archetype.Dispose` 同一套做法。

## 续接时可做的

1. 结构锁是入口最大单项。去掉它等于把「结构变更内部线程安全」改为「调用方串行化」，属于对外契约变更，需单独决策（入口探针实测 3.7 ns，收益有限）。
2. 两张位置表里原生镜像占大头。纯托管世界不读它，但 `TryGetComponentPointer`、`WriteComponentRange`、原生 Job 派发都读，按需刷新不成立。
3. 添加组件比移除组件慢 13 ns，两者核心同构，差异是「写入新组件初始值」的冷缓存缺页。

## 环境注意

- 必须设 `DOTNET_TieredCompilation=0`，否则读数不稳定。
- 15 轮时同一构建的比值可漂 0.05–0.1，验收用 25 轮。
- 跨构建的绝对时间不可相减或相除；只比较同一批次内交替运行的结果。

## 代码重写进度

按「维护性与易读性优先」逐文件过 `src/EntJoy.ECS`：只改可读性与死代码，不改行为。每轮 580 用例双后端与遍历矩阵 20/20 全绿，`--struct 100000 25` 记录一次。

当前规模：116 个文件 / 14491 行，最长方法 44 行（起点 355）。以下扫描项均为 0：历史叙述注释、空 `<summary>`、空 `<param>`、写后不读的私有字段、零引用公开方法、`summary` 内散文超过三行的块。

已重写：`Chunk` 与 `Archetype` 全部五个分部、`ChunkMetadata`、`ArchetypeChunk`、`ChunkEnabledMask`、`ChunkCacheLayout`、`ChunkMemoryPool`、`EntityManager` 的创建/销毁/组件/关系/克隆/快照/压缩/诊断/任务记账/释放、`EntityQuery`、`QueryBuilder`、`QuerySelection`、`QueryTraversalCursor`、`QueryEnumerable`、`ComponentTypeManager`、`ComponentLookup`、`ScheduleGraph`、`ChunkJobScheduler`（缓存/上下文/调度/事件缓冲/回调）、`ChunkJobCollector`、`NativeChunkJobs`、`ChunkJobExtensions`、`DeferredCommandBuffer` 六个分部、`EventStream`、`World` 与 `SystemRunner`。

已删：`Chunk` 三个 shared 访问器与整套版本计数；`Archetype` 的全局/启用位版本计数与 `GetMemoryLayoutInfo`；排序版 `CalculateHash`；`NotifyComponentChangedPublic`；`ValidateRelationshipView`；只含注释的 `World_Reporter.cs`。

已合并：克隆主流程（355 → 45 行）、删除提交（162 → 18 行）、命令回放（153 → 25 行及 6 个分支方法）、上下文块组装（94 → 40 行）、块布局（118 → 30 行）、调度负载填充（66 → 8 行）、缓存租约基类、`FreeChunkJobData`、`GetSourcesOfType`、`WriteInt`、`Append`、`AllocateEntityId`、`FindSlab`、`TryReuseCached`、`SelectObservers`、`FlushDestroyRun`、`WriteInlineComponentCommand`、`ResolveAddTarget`、`ResolveRequiredColumns`、`ResetTraversalBuffers`、`ValidateEntity` 复用 `GetAliveInfoRef`。

刻意保留的重复：`ChunkJobScheduler.Cache.cs` 与 `Schedule.cs` 各有一份 required 列解析。两者输入形态不同（一边是原型 + 列指针，一边是类型 id 数组 + 列指针），合并任一侧都要先多建一个数组，热路径上不划算。

已回退（实测退化）：增删组件的目标解析抽取（add 78.3 → 89.3 ns）。

未做及原因：
- `ComponentTypeManager.idToTpyeMap` 拼写错误——改名是破坏性 API 变更，等确认。
- `unsafe` 与业务逻辑的全量分层——现有边界已按 `*Raw`/`*Core`/原生文件划分，进一步分层要把校验与指针运算拆成两层对象，改动面覆盖全部结构变更路径，收益只是形式上的。
- `ValidateQueryReadState`、`TryGetLocation`、`DrainNativeEvents` 三个成员当前源码零引用，但属公开 API 面（前者的失效校验曾被内联进查询热路径），保留。

## 关于 `unsafe` 与业务逻辑的边界

现状即是分层结果：指针运算只出现在三类位置——原生互操作文件（`NativeChunkJobs`、`ChunkJobScheduler.Schedule`、`NativeEntityLocate`）、`*Raw`/`*Core` 内部实现、以及本身就是 `unsafe` 的 blittable 结构（`Chunk`、`ChunkJobData`、`RelationSlot` 等）。公开入口与校验/通知/记账逻辑不在这些位置。

试过把 183 处方法级 `unsafe` 删掉（理由：类型上已声明），编译报 468 个 CS0214：`Archetype`、`QueryEnumerable`、`ComponentObserver<T>` 内层等类型并未在类型级声明。方法级标注正是「这一支真的碰指针」的表达，不能批删。
