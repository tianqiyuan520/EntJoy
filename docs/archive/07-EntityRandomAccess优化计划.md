# 优化 EntJoy ECS 实体随机访问开销（07_EntityRandomAccess）

> 状态：**已实现**（Part A1/A2/B 全部落地 + 07 基准量化）
> 关联：[06-HotFieldHandle设计.md](06-HotFieldHandle设计.md)

## Context

批量路径（IJobEntity / IJobChunk / Query）已经是「chunk 序提升」模式，性能最优（HotField 基准 0.21–0.26x vs Class）。但 **稀疏 Entity 随机访问**（`GetComponent<T>(Entity)` / `Set` / enableable 开关）每次调用都走完整解析链：Monitor 锁 → 位置表 → Dictionary 类型索引 → `_chunkList` → Chunk 对象 → 偏移数组 → 内存块。实测逐实体解析 1.13–1.32x 劣于 Class。

对比 Unity DOTS / EnTT / Flecs / Bevy：它们的随机访问都基于「位置表（按实体 Id 直接索引）+ 2–3 次相关加载 + 读路径 lock-free + 类型→列索引预存/缓存」。我们的差异是：**① 读路径带 Monitor 锁；② 类型→索引每次 Dictionary 查找；③ 解析链多一层托管 List→Chunk 对象**。本计划对齐这些做法，把随机访问压到接近硬件下限，同时保住正确性纪律。

**目标**：零风险优化（读路径去锁 + enableable 去拷贝）+ 新增 `ComponentLookup<T>`（对齐 Unity `ComponentLookup`），并用基准实证。

---

## Part A — 零风险纯优化

### A1. 读路径去 `_structuralLock`（[EntityManager.cs:664-677](src/EntJoy/Entity/EntityManager.cs#L664-L677)）

**安全性已验证**：全局搜索确认 `GetComponent<T>(Entity)` 唯一活跃调用方在主线程（样例循环）；job 体内拿到的是 `ArchetypeChunk`/原始指针，结构性 API（NewEntity/DestroyEntity/AddComponent/RemoveComponent/Set）都先调 `CompleteActiveJobs()`，该方法在 `IsExecutingJob`（ThreadStatic）时抛异常——**结构性变更不可能与读取并发**。锁从未保护过"读写并发"，只序列化了"读 vs 读"，纯开销。

改动：
- `GetComponent<T>(Entity)`：去掉 `lock(_structuralLock)`，其余校验（CheckDisposed + Archetype null + Version）原样保留。
- 同样处理只读的 enableable 访问 `IsComponentEnabled<T>(Entity)`（[:727-749](src/EntJoy/Entity/EntityManager.cs#L727-L749)）。
- 用 `#if ENTJOY_SAFE_ENTITY_READS` 门控（默认关），文档注明 main-thread only 纪律，给多线程用户一个安全逃生口。

### A2. enableable 访问去防御拷贝

`SetComponentEnabled`（[:712-713](src/EntJoy/Entity/EntityManager.cs#L712-L713)）和 `IsComponentEnabled`（[:744-745](src/EntJoy/Entity/EntityManager.cs#L744-L745)）调用 `archetype.GetChunks()` 返回 `new List<Chunk>(_chunkList)`（每次拷贝+分配），只为取一个 chunk。`Archetype` 已有 `ref readonly List<Chunk> ChunkList`（[Archetype.cs:127](src/EntJoy/Archetype/Archetype.cs#L127)）：

```csharp
// 改前
var chunks = archetype.GetChunks();
var chunk = chunks[info.ChunkIndex];
// 改后
var chunk = archetype.ChunkList[info.ChunkIndex];
```

两处替换即可，Archetype.cs 无需改。`GetChunks()` 留给外部调用者，加注释提示优先 `ChunkList`。

**写路径 `Set<T>` 保持不动**：`CompleteActiveJobs()` 是正确性必需（挂起 job 持有写入 chunk 的原始指针）；稳态下近零开销（ThreadStatic 读 + 空列表早退）。基准的稀疏路径是读主导，Set 不在热循环。可选后续 `SetUnsafe` 另行评估。

---

## Part B — `ComponentLookup<T>`（新文件 `src/EntJoy/Entity/ComponentLookup.cs`）

镜像 Unity `ComponentLookup<T>` / `EntityStorageInfoLookup`：缓存上次 archetype 的组件列索引（消掉 Dictionary）+ 上次 chunk 的组件基址，`lookup[entity]` 稳态退化为「位置表 1 次加载 + 指针比较 + `base[slot]`」，Flecs/EnTT 级。

```csharp
public unsafe struct ComponentLookup<T> where T : struct
{
    private readonly EntityManager _em;
    private Archetype _archetype;   // 上次解析的 archetype
    private int _componentIndex;    // T 在该 archetype 的列索引（Dictionary 一次搞定）
    private int _stride;            // Unsafe.SizeOf<T>()，JIT 折叠为常量
    private Chunk _chunk;           // 上次解析的 chunk
    private byte* _base;            // chunk.GetComponentArrayPointer(_componentIndex)
    private int _version;           // _em.StructuralVersion 缓存，初值 int.MinValue

    public ref T this[Entity entity] { get { /* 见下 */ } }
    public ref T UnsafeRef(Entity entity);   // 跳过 null/version 校验的快速变体
    public bool IsEnabled(Entity entity);    // 复用缓存列索引，走 GetComponentEnabled
    public void SetEnabled(Entity entity, bool enabled);
}
```

**访问算法**（`this[Entity]`）：
1. `_em.GetEntityInfoRef(entity.Id)` —— 位置表 1 次加载
2. null/version 校验（与 `GetComponent` 同语义）
3. `if (_version != _em.StructuralVersion)` 则失效缓存（`_archetype = _chunk = null`）——结构变更（NewEntity/DestroyEntity/Add/Remove）都在 bump 版本，覆盖 chunk 被 swap-pop 释放导致的悬垂基址；`Set`/`SetEnabled` 不 bump（正确：它们不移 chunk）
4. archetype 缓存未命中 → `_archetype.GetComponentTypeIndex<T>()` + `Unsafe.SizeOf<T>()`，一次 `Debug.Assert(_stride == _archetype.Types[_componentIndex].Size)` 兜 exotic 布局
5. `_archetype.ChunkList[info.ChunkIndex]` —— 1 次 List 索引
6. chunk 缓存未命中 → `_base = (byte*)chunk.GetComponentArrayPointer(_componentIndex)`
7. `return ref Unsafe.AsRef<T>(_base + info.SlotInChunk * _stride);`

稳态：~4 次相关加载 + 全是可预测的指针比较（热循环中永不命中分支）。slot 每次都从位置表现读，swap-pop 后仍正确（位置表是权威）。

**工厂**：EntityManager `// Component` partial 加 `public unsafe ComponentLookup<T> GetComponentLookup<T>() where T : struct`。lookup 是普通 struct（非 ref struct），可作系统字段。文档注明：持有 EntityManager/Archetype/Chunk 强引用，Dispose 后失效；main-thread only。

**enableable**：约束用 `where T : struct`（`IEnableableComponent` 不继承 `IComponentData`），一个 lookup 同时服务两类。`IsEnabled`/`SetEnabled` 复用缓存列索引+chunk，走 `Chunk.GetComponentEnabled/SetComponentEnabled`（非 enableable 类型本就抛"not enableable"，语义与现状一致）。

---

## 基准验证

新增 `src/EntJoySample/07_EntityRandomAccess/`（RandomAccess.cs + Program.cs），照抄 06 约定（`ReadPositiveEnvironmentInt` / `Percentile` / `PrintSummary` / `BENCH|` 行 / warmup=5, measure=100）。

**注意**：EntJoySample.csproj 同一时刻只编译一个活跃 `Program.cs`（其余 Main 注释掉，仓库既有约定）。实现时需注释 06 的 Main 激活 07，跑完恢复。若不想动 Main，退路是把基准作为 06 内的第二个场景类从 06 既有 Main 调用。

**场景**（对预先打乱的实体索引排列，打破位置表缓存局部性；单 archetype `Position+Velocity`，N=1M 默认 `ENTJOY_RA_ENTITIES`）：
1. `ClassArray` —— AoS `ClassEntity[]` 乱序 `+=`，1.0x 基线（镜像 06）
2. `GetComponentLocked` —— 现状 `em.GetComponent<Position>(e)`（抓 A1 前的数字）
3. `GetComponentLockFree` —— A1 之后（隔离锁移除收益）
4. `ComponentLookup` —— `lookup[e]`
5. `ComponentLookupUnsafe` —— `lookup.UnsafeRef(e)`
6. `QueryDense` —— chunk 序循环（锚定 0.26x 参考点）
7. `SetWrite`（可选）—— 论证 §写路径决策

**正确性门**：K 帧后逐元素断言 `lookup[e] == em.GetComponent<Position>(e) == classArr[i].Pos`；enableable 微检查（`SetComponentEnabled`/`IsComponentEnabled` 开关一致 + 无每访问 GC 分配）。

**预期结论**：`GetComponentLockFree` 低于现状；`ComponentLookup` 逼近 `GetComponentLockFree`（但**不会**逼近 `QueryDense`——随机 vs 顺序是两类访问模式，见下实测）。

---

## 实测结果（本机 1M，乱序随机访问，warmup=5，measure=40）

> `07_EntityRandomAccess/RandomAccess.cs`。实体索引预先 Fisher-Yates 打乱，打破位置表/缓存局部性，逼近真实随机访问。

| 变体 | 访问路径 | avg (ms) | 相对 ClassArray |
|---|---|---|---|
| ClassArray | AoS class 对象乱序 `+=` | 67.6 | 1.00x |
| StructArray | AoS 值类型数组乱序 `+=` | 35.2 | 0.52x |
| GetComponent(lock-free) | 位置表→Archetype→Dictionary→chunk→基址 | 231.6 | 3.43x |
| ComponentLookup | 缓存列索引+chunk 基址，位置表+指针比较+base[slot] | 156.1 | 2.31x |
| ComponentLookupUnsafe | 跳过 null/version 校验 | 153.9 | 2.28x |
| QueryDense | chunk 序顺序循环 | 1.40 | 0.02x |

**结论（如实记录，纠正计划里的两条旧预期）**：

1. **A1（读路径去锁）有效**：`GetComponent` 从带锁 ~16x（06 顺序场景实测）降到 lock-free 3.43x（乱序场景）；锁移除 + 顺序访问共同收益。
2. **B（ComponentLookup）有效但幅度小于预期**：3.43x → 2.31x，消掉了「Dictionary 类型索引 + List→Chunk」两层；**残余 2.31x 是位置表（24MB）随机访问的 DRAM 缓存未命中地板**，任何 Entity-keyed 随机访问都躲不掉。
3. **「ComponentLookup 逼近 QueryDense」是错的**：随机访问（位置表 cache miss ~100ns/次）与顺序访问（QueryDense 1.4ms）差两个量级，本质是两类负载。ComponentLookup 优化的是随机访问，不与顺序下界比。
4. **StructArray 0.52x**：AoS 值类型数组随机访问比散落 class 快 2x（无对象头、连续内存），是 OOD 随机访问应对比的公平基线。

**正确性门**：`lookup[e] == UnsafeRef(e) == GetComponent<Position>(e) == classArr[i].Pos` 逐元素 PASS。

---

## 实施顺序与风险

**顺序**（每步编译 + 过正确性门再进下一步）：
1. **A2** —— enableable 去拷贝（2 行，最低风险）
2. **A1** —— 读路径去锁 + `#if ENTJOY_SAFE_ENTITY_READS` + 文档
3. **B** —— `ComponentLookup.cs` + 工厂
4. **07 基准** —— 量化 1→2→3 相对 `ClassArray`/`QueryDense` 的 delta

**风险**：
- **A1** 仅在单线程纪律下安全；多线程滥用会与 `Array.Resize` 竞态。用 XML 文档 + `#if` 开关缓解；写路径锁保留以维持部分保护。
- **ComponentLookup 悬垂基址**：`StructuralVersion` 比较消除（chunk Dispose 只发生在版本 bump 下）。
- **stride 不匹配**（exotic 打包布局）：一次性 `Debug.Assert` 兜底；现状已假设 `sizeof(T) == 注册大小`。
- **`GetComponentTypeIndex<T>()` KeyNotFound**（实体缺 T）：与现状 `GetComponent` 一致（Dictionary 抛），保留不吞。
- **新增样例的 `Main()` 冲突（CS0017）**：按仓库注释切换约定处理。

## 关键文件
- [EntityManager.cs](src/EntJoy/Entity/EntityManager.cs) —— A1 读路径去锁、A2 enableable、B 工厂
- [Archetype.cs](src/EntJoy/Archetype/Archetype.cs) —— 复用已有 `ChunkList`，不改
- [Chunk.cs](src/EntJoy/Chunk/Chunk.cs) —— 复用 `GetComponentArrayPointer/GetComponentEnabled/SetComponentEnabled`，不改
- `src/EntJoy/Entity/ComponentLookup.cs` —— 新增
- `src/EntJoySample/07_EntityRandomAccess/` —— 新增基准