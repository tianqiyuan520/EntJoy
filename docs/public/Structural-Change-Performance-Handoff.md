# 结构变更性能与不变量

## 当前位置

10 万实体、四项逐实体结构变更，同一进程内两侧逐轮交替、按轮取最小。下面是同一构建两次重复：

| 操作 | EntJoy | Arch | 比值 |
| --- | --- | --- | --- |
| 创建实体 | 23.0–23.3 ns | 22.8–23.4 ns | 0.99–1.02 |
| 添加组件 | 61.5–63.8 ns | 66.6–69.2 ns | 0.92 |
| 移除组件 | 63.7–64.3 ns | 75.4–75.8 ns | 0.84–0.85 |
| 销毁实体 | 23.3 ns | 26.2–26.5 ns | 0.88–0.89 |

读数：`tools/EcsComparison/results/final-20261116-b.log`（2026-11-16 13:10）。该目录在 `.gitignore` 内，是本机记录，不入库。含 Flecs、EnTT、Unity 的横向读数与场景位置见 [28 跨框架 ECS 性能对比](../gridsearch/28-跨框架ECS性能对比.md)。

口径：.NET 8 Release JIT；两侧都用非线程安全 World（EntJoy `new World(name, threadSafe: false)`，Arch 本来没有结构锁）；每项单次计时、按轮取最小；预建与复位都在计时之外；首轮是预热轮、不计入。绝对时间只在同一进程内可比，跨会话、跨构建、跨 World 配置都不可相减。

添加臂两侧都写入组件初始值：EntJoy 只有带值的入口 `AddComponent<T>(entity, value)`，对应 Arch 的 `Add<T>(entity, value)`。拿 Arch 不带值的 `Add<T>(entity)` 对照会把一侧写值、另一侧不写值算成实现差距（实测 Arch 这两条入口相差 7–9 ns）。

## 一次迁移由什么构成

四条路径共用一次原型迁移：目标原型按组件 id 查边表得到，实体追加进目标 Chunk，逐列搬运组件值，旧原型 swap-pop 收回槽位，最后写位置表并失效查询。

- **列序不查表**：`Archetype.CopyComponentsTo` 按源列序搬运。列序等于 `Archetype.types` 顺序（构造时按同一顺序建 `_componentColumns`），源列下标就是循环下标。shared 列不随实体迁移；可启用位只在两侧都有该列时随值搬移。
- **非析构组件走定长搬运**：`BufferCopy.Copy` 按 1/2/4/8/12/16 字节选定长指令，其余长度交给 `Unsafe.CopyBlock`。析构组件仍走 `ComponentTypeManager.MoveComponentValue`（位拷贝后清空源，转移所有权）。
- **swap-pop 与清槽同源**：`Chunk.MoveSlot` 用 `BufferCopy.Copy`，追加到复用过的槽位用 `BufferCopy.Zero`；只有高水位以下的槽位需要清。
- **新组件值最后单独写列**：`WriteColumnBytes` 同样走 `BufferCopy.Copy`。

`BufferCopy` 存在的理由：`Unsafe.CopyBlock` 的长度是运行时值，对任意长度都落到 memmove 调用。逐实体搬列时一个 4 字节组件也付一次调用。改为定长搬运后，同会话配对测得添加 −5.5 ns、移除 −5 ns（Arch 侧读数不变），轮间波动也从 ±6% 收到 ±1%。

## 比 Arch 多付的三项

都是能力取舍，不是可以抹掉的实现开销。三项合起来只让创建落在 0.99–1.02，其余三项仍是本篇更快：

1. **结构锁**：`EnterStructural` 只在 `threadSafe: true` 的 World 上取 Monitor，非线程安全 World 整句跳过。Arch 没有等价机制。上表的读数不含这项。
2. **Job 记账**：`JobBookkeepingIdle` 读一个记账标记加五项状态判定（观察者深度、两个拷贝/析构钩子标记、Job 执行中、原生事件待 drain）；标记为 0 时跳过完成与清理。有在飞 Job 时必须先等到完成。Arch 没有 Job 系统，无从对应。
3. **原生镜像位置表**：每实体 24 字节的 `EntityLocateB`（`ChunkMemory` / `ChunkOffsets` / `SlotInChunk` / `Version`），供 `CreateNativeLookup`、`CreateNativeEntityLookup` 与原生内核按实体 id 随机访问。托管侧只写不读，但原生侧读；改成派发前整表重建等于每次派发重扫全部实体。

`--ab-pair` 可单独量入口固定开销（对已销毁实体重复销毁，走完整入口后核心早退），用于判断某项差额落在入口层还是搬移层。

## 不变量

改这些位置前先确认不变量仍成立。

**Chunk 是值类型，取块一律经 ref**
- `Chunk` 24 字节：`ChunkMetadata` 是 class，结构体里只有它的引用，加上 `MemoryBlock` 指针与两个计数。
- 经 `ChunkList[i]` 或 `foreach` 取到的是副本；改托管字段的成员（`AddEntity`、`RemoveEntity`、`InitializeSlot`）是 `internal`，只能由 `Archetype` 经 `ref` 调用。热路径一律用 `CollectionsMarshal.AsSpan(_chunkList)[i]` 取 `ref`。
- 副本也必须生效的状态都在非托管内存里（组件列、启用位图、变更位图）：副本共享同一块内存，在副本上读写同样生效；`_entityCount`、`_highWater` 不做跨副本递增。
- 用例：`SwapPopLocationConsistencyTests`。

**swap-pop 只改槽位**
- 同块末位实体搬到空洞时，原型、块基址、列偏移都不变，位置更新只写槽位（`MoveSlotWithinChunk`）。搬移槽位取自 `RemovalResult.MovedEntitySlot`，不依赖调用点入参。

**块复用后的清槽**
- 块构造时整块 memset 组件区，块内记 `_highWater`。追加到从未用过的槽位跳过清零，复用过的槽位逐列清。
- 块内存复用时一定走 `new Chunk(...)`，高水位与清零状态不会跨复用残留。

**记账标记只数真实条目**
- 重算只数真的持有 Job 的集合：`_archetypeJobs` 的键登记后不删除，拿它的 `Count` 当判据会让标记永远停在 1。`PruneCompletedJobsNoLock` 与 `ClearJobTrackingNoLock` 结束时都重算。
- 用例：`JobTrackingIdleFlagTests`。

**位置表的打包上限**
- 槽位占低 16 位（`PackedSlotLimit` = 65536，Chunk 容量不得超过），块号占高 16 位且必须落在正数区（`PackedChunkLimit` = 32768，单原型块数不得超过）。`ChunkIndex` 用算术右移读出，再高会读成负数；`0xFFFF/0xFFFF` 正好等于清空哨兵 -1。
- 两处校验都在建立边界的入口：`Archetype` 构造校验容量，`AllocateFromSlab` 校验块数。逐实体路径上没有分支，越界也没有兜底——`SetLocation` 会静默截断到低 16 位。
- 用例：`PackedLocationLimitTests`。

**原型查找的哈希**
- 与顺序无关，每项先过 splitmix32 终混再累加。直接累加 Id 会让 id 之和相等的集合恒碰撞（`{1,4}` 与 `{2,3}`）。
- 碰撞走线性兜底，且单个哈希只缓存一个原型：碰撞的那个原型此后每次都重新比对，所以哈希质量决定兜底频率，不只影响一次查找。

**释放先清完再抛错**
- 原型释放失败不中断整体清理：逐个收集异常，照常归还 slab、定位表与回收栈，末尾抛 `AggregateException`。`EntityManager.Dispose` 与 `Archetype.Dispose` 同一套做法。

## 复现与判读

```powershell
dotnet build tools/EcsComparison -c Release
dotnet run -c Release --project tools/EcsComparison --no-build -- --struct 100000 5
```

- **必须先单独构建 `tools/EcsComparison`**：`dotnet build EntJoy.sln` 不包含它，只建 solution 就会让 `--no-build` 用上一次的工具与库，测出来的还是旧版本。
- 四项逐轮交错两侧，比值只在同一进程内成立。
- 轮数 5 够用：同一构建重复三次，添加项的比值波动在 ±0.07 内。
- 首轮是预热轮，必须保留。没有它时最小值会在「冷 JIT + 全新 Chunk（槽位无需清槽）」与「热 JIT + 复用 Chunk」之间跳，读数不可复现。
- 单个操作的读数会被一侧的偶发慢轮带偏（某一轮 Arch 添加跑到 86 ns 而非 59 ns，比值会从 1.05 翻成 0.79）。出现这种离群就重跑，不要拿它下结论。
- `tools/EcsComparison/obj/struct-*.txt` 是历史逐次测量记录，各自协议不同，不互相比较，也不与本表混用。

## 边界

- 创建与 Arch 持平（0.99–1.02），差额在入口层：去掉结构锁或 Job 记账都是对外契约变更，不是代码优化。
- 原生镜像的 24 字节写是入口层的第三项，去掉需要改原生 ABI。
- 四条路径的核心搬移（迁移列、swap-pop 搬槽、写新组件值）与 Arch 同序同量，没有可再压的独立环节。

## 未决项

- 两条写入入口语义重叠：`Set<T>` 1616–1648 µs，`SetComponent<T>` 3404–3455 µs（同 10 万次按句柄写同一列）。后者经定位表取指针，但每次按 `Type` 查类型表，且完成作业前不判空闲、每次取一次 `_activeJobLock`。两者应收敛到同一实现。
- 按句柄直读取组件 599–633 µs，句柄索引入口 394–402 µs。差额是直取每次都从实体表重新解析定位，并多付处置、版本与关系列校验。
- 延迟命令回放没有对照读数，`Change` 一侧还缺这一项基准。
- 逐实体 `foreach` 与 Arch `InlineQuery` 的差随组件数反向：2 组件 95 对 46 µs，3 组件 77 对 131 µs。2 组件的差额来自逐访问校验与结果构造，属语法形态成本。

读数与口径见 [遍历与延迟设计](ECS-Iteration-And-Deferred-Design.md)。
