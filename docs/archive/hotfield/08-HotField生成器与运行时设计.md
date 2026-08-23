# 08 HotField 生成器与运行时设计

> 状态：设计定稿（runtime 支持）
> 前置：06-HotFieldHandle设计.md（组件指针 + 阶段边界）、07-EntityRandomAccess优化计划.md（ComponentLookup）

---

## 1. 生成风格（用户标注 → 机械部分自动产出）

用户写（源码 class 不变，编译类型是 struct）：

```csharp
[HotFieldEntity]
public partial class Player
{
    [HotField] public float2 Pos;
    [HotField] public float2 Vel;

    public void Update(float dt) => Pos += Vel * dt;
}
```

生成器产出：

```csharp
// ① 组件
public struct Position : IComponentData { public float2 Value; }
public struct Velocity : IComponentData { public float2 Value; }

// ② 实体:class → struct(容器值类型 → 0.96x 持平;_entity 不进对象,由平行数组管)
public unsafe partial struct Player : IDisposable
{
    internal float2* PosPtr;          // 缓存组件指针(零解析链)
    internal float2* VelPtr;
    private World __world;            // 生成器注入:世界上下文(Dispose 用)
    private bool __bound;

    public ref float2 Pos => ref *PosPtr;     // 属性改写
    public ref float2 Vel => ref *VelPtr;

    public void Update(float dt) => Pos += Vel * dt;   // 用户方法原样

    // Bind:阶段边界重解析缓存指针
    public void Bind(World world, Entity __entity)
    {
        __world = world;
        var em = world.EntityManager;
        ref var info = ref em.GetEntityInfoRef(__entity.Id);
        var chunk = info.Archetype.ChunkList[info.ChunkIndex];
        int slot = info.SlotInChunk;
        PosPtr = (float2*)chunk.GetComponentArrayPointer(info.Archetype.GetComponentTypeIndex<Position>()) + slot;
        VelPtr = (float2*)chunk.GetComponentArrayPointer(info.Archetype.GetComponentTypeIndex<Velocity>()) + slot;
        __bound = true;
    }

    // Dispose:显式解绑 + 删 entity(不用 finalizer,见 §3)
    partial void OnDispose();
    public void Dispose()
    {
        if (__bound) { __world.EntityManager.DestroyEntity(__entity); __bound = false; }
        OnDispose();
    }
}

// ③ 框架:平行 Entity[] __entities(值数组,索引对齐,GC 友好)
```

---

## 2. 阶段边界重解析（World 不存 class，GC 友好）

**问题**：缓存指针在结构变更（实体迁移 chunk / swap-remove）后失效，需重解析；但 World 不能持有 class 对象（GC 扫描压力）。

**解法**：平行的值数组 `Entity[] __entities`（8B/实体，纯 struct，无对象引用）：

```
用户容器:  Player[] players      ← 用户持有,World 不碰
平行值数组: Entity[] __entities  ← 框架维护,索引对齐 players
```

阶段边界刷新（O(n) 一次，迭代窗口零检查）：

```csharp
public static void RefreshHandles(World world, Player[] players, Entity[] __entities)
{
    for (int i = 0; i < players.Length; i++)
        players[i].Bind(world, __entities[i]);
}
```

- `players[i] ↔ __entities[i]` 索引对齐稳定（实体在 chunk 内迁移不改用户容器顺序）。
- 细粒度优化：`EntityManager` 在结构变更时记录**移动的实体**（`Archetype.Remove` 返回 movedEntityId；Add/Remove 组件返回迁移实体），只刷新受影响 subset。
- **GC**：World/EntityManager 只存 `EntityIndexInWorld[]`（struct 位置表）与平行 `Entity[]`，都是值数组；class 对象由用户容器持有，不额外产生 GC 根。

---

## 3. 析构：GC 兜底 + 显式 Dispose（用户可全权交给 GC）

**ECS 正确模式是显式生命周期，但迁移用户很可能不调 `Dispose`——生成器提供 finalizer 安全网，且不在 GC 线程直接销毁。**

```csharp
// 生成器产出(class 稀疏句柄):
public partial class Player : IHotFieldEntity
{
    private World __world;
    private Entity __entity;
    private bool __bound;

    public void Bind(World world, Entity entity) { __world = world; __entity = entity; /* 解析 PosPtr/VelPtr */ __bound = true; }

    // 显式(用户可选):
    public void Dispose()
    {
        if (__bound) { __world.QueueDestroy(__entity); __bound = false; }   // 排队,主线程阶段边界 Drain
        OnDispose();
        GC.SuppressFinalize(this);                                          // 显式后免 finalizer 双跑
    }

    // 安全网:用户不 Dispose → GC 兜底
    ~Player() => Dispose();

    partial void OnDispose();   // 用户已有清理逻辑的合并点
}
```

**为什么不在 finalizer 直接 `DestroyEntity`**：finalizer 跑在 GC 线程，直接改 chunk 结构有竞态/World 生命周期风险。改为 `QueueDestroy`（入队），主线程阶段边界统一 Drain。

**代价**：finalizable 对象多活一个 GC cycle——"全权交给 GC"的必然代价；密集迭代不受影响（用 struct 句柄，无生命周期概念，是值视图）。

---

## 4. `IHotFieldEntity` 接口（方便写泛型函数）

内核新增 `EntJoy.IHotFieldEntity`（`src/EntJoy/Entity/IHotFieldEntity.cs`）：

```csharp
public interface IHotFieldEntity : IDisposable
{
    void Bind(World world, Entity entity);
}
```

生成器产出的实体（class 稀疏句柄 / struct 密集句柄）都实现它。**接口只声明实体契约（绑定/生命周期），不含业务方法（如 `Update` 只是测试场景，不属于契约）**，避免把测试产物塞进框架契约。

---

## 5. 自动 RefreshHandles（框架自动，用户不手动调）

**应该自动。** 用户注册一次，框架在结构变更后自动刷新：

```csharp
// 用户只需在创建后注册一次:
world.RegisterHotField(__entities, weakPlayers);

// 框架内部:
public void RegisterHotField<T>(Entity[] entities, WeakReference<T[]> weakHandles) where T : struct, IHotFieldEntity
{
    _hotFieldRegistries.Add(new HotFieldRegistry { Entities = entities, WeakHandles = weakHandles });
}

// 阶段边界(World.Update / FlushStructuralChanges 内):
void AutoRefreshHotFields()
{
    foreach (var reg in _hotFieldRegistries)
    {
        if (reg.WeakHandles.TryGetTarget(out var handles))
            for (int i = 0; i < handles.Length; i++) handles[i].Bind(this, reg.Entities[i]);
        else
            _hotFieldRegistries.Remove(reg);   // 对象已回收,移除注册
    }
}
```

- **GC 友好**：`Entities` 是值数组（8B/实体，无对象引用）；`WeakReference` 不阻止对象回收（配合 §3 的 GC 兜底，对象死了 entity 也随之清理）。
- **触发点**：`EntityManager` 结构变更（`StructuralVersion` bump）后的阶段边界自动调 `AutoRefreshHotFields()`。
- 细粒度：只刷新 `EntityManager.Remove/AddComponent` 记录的**移动实体** subset。

---

## 6. 与既有能力的关系

- `ComponentLookup<T>`（07）：稀疏随机访问（2.3x 位置表地板），供未绑定指针的临时访问。
- 本设计：缓存组件指针（密集 0.96x / 并行 0.80x / System 3.5x），绑定后零解析链。
- `EntityManager.DestroyEntity` / `StructuralVersion`：生命周期与阶段边界失效的基础，已具备。

---

## 7. 生成器要点清单

| 输入标注 | 产出 |
|---|---|
| `[HotFieldEntity]` | partial class → partial struct + `IHotFieldEntity : IDisposable` |
| `[HotField] float2 Pos` | 组件 `Position` + `float2* PosPtr` + `ref Pos => ref *PosPtr` |
| `Update(dt)` | 方法体原样（字段访问已重定向到 ref 属性）；业务方法不属于 `IHotFieldEntity` 契约 |
| — | `Bind(World, Entity)`：重解析缓存指针 |
| — | `Dispose()` + `partial void OnDispose()`：QueueDestroy + 解绑 |
| — | `~Player()` finalizer 安全网（用户不 Dispose 时 GC 兜底，主线程 Drain） |
| — | 框架 `Entity[] __entities` 平行值数组 + `world.RegisterHotField` 自动刷新 |

> 待实现（source generator，Phase 8 范围）。
