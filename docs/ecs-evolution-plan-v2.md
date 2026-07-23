# EntJoy ECS 全面进化方案 v2

> 整合 14 个 ECS 项目的设计经验，建立双轨制：高性能深度路径 + 易用快速路径
>
> 参考: Unity DOTS、Bevy、Flecs、EnTT、Arch(C#)、Svelto.ECS、LeoECS、Morpeh、Unreal Mass、Entitas、Arche(Go)、FrifloEngine ECS、fennecs、DefaultEcs

---

## 设计哲学：双轨制

```
用户可以选择：
                        易用路径（SystemBase + CodeGen）
    ┌─────────────────────────────────────────────────────┐
    │ Reactive systems · Group/index · World events       │
    │ Auto-defer · Lambda queries · One-frame components  │
    │ DI · Contexts · Trait bundles                       │
    └─────────────────────────────────────────────────────┘
                             ⬇
                         中间层（JobHandle 串联）
                         自动 per-archetype tracking
                             ⬇
    ┌─────────────────────────────────────────────────────┐
    │ Hardcore 路径（IJobChunk + NativeTranspiler）       │
    │ Struct queries · Direct pointer · ISPC/C++ 编译    │
    │ 零 reactive 开销 · 零 event 生成                    │
    └─────────────────────────────────────────────────────┘
```

两条路径共享底层：同样 per-archetype 存储、同一套 JobSystem、同一个 temp allocator。

---

## 项目核心优势

当前项目有一个独特的优势：**Slab/ChunkPool 内存 + 指针稳定性。**

| 系统 | 指针安全机制 | 运行时开销 |
|------|------------|-----------|
| Unity DOTS | AtomicSafetyHandle（每访问检查） | ✅ 有 |
| Bevy | Rust 借用检查器（编译期） | 无 |
| Flecs | staging + defer（运行时） | ✅ 有 |
| **本项目** | **New → 全局 ChunkPool，remove 回池** | **接近 0（已在 ChunkPool 层解决指针问题）** |

---

## 借鉴来源总表

| 项目 | 借鉴点 | 用于 |
|------|--------|------|
| **Unity DOTS** | JobSystem + Burst 模型 | 已有等价 |
| **Flecs** | Pipeline Phase、auto-defer、pair relation、Observer | Phase 4 调度 + Phase 6 关系 |
| **Bevy** | non-fragmenting 关系、system-as-function、Commands | Phase 6 关系 + Phase 4 函数式 system |
| **EnTT** | **Pay per use** 哲学、storage customization、Direct pointer T** | **核心设计理念** |
| **Arch (C#)** | Struct query > lambda query、archetype edges、batch command buffer | Phase 2 edges + Phase 3 ECB + query API |
| **Svelto.ECS** | Static archetype、Groups/Filters、design over speed | static entity descriptor |
| **LeoECS** | World events、one-frame components、零依赖 | Phase 5 易用路径 |
| **Unreal Mass** | **Processor** 模式、shared fragment、subsystem query、Trait | Phase 4 Processor + Phase 7 共享组件 |
| **Entitas** | **Code generation**、Reactive system、Group/index | **Phase 8 代码生成** |
| **Arche (Go)** | 最小化 ECS API、relation per archetype | Phase 6 关系 |
| **FrifloEngine ECS** | SIMD 加速、struct query | struct query 方向验证 |

---

## 核心组件关系

```
                    ┌──────────────┐
                    │   Context    │  ← 多个隔离 world（Game/UI/Input）
                    └──────┬───────┘
                           │
                    ┌──────▼───────┐
                    │    World     │
                    │  EntityManager│
                    │  SystemGraph  │
                    └──────┬───────┘
                           │
               ┌───────────┼───────────┐
               │           │           │
        ┌──────▼────┐ ┌───▼────┐ ┌────▼────┐
        │ Archetype │ │ Chunk  │ │ Edges  │
        │ + ChunkPool│ │ + SoA  │ │ (Cache)│
        └──────┬────┘ └───┬────┘ └─────────┘
               │          │
               │   per-chunk 列:
               │   Entity | Comp0 | Comp1 | ... | Relation0
               │                         ↑ SoA
               │
        ┌──────▼──────────────────────┐
        │  Entity (int ID + version)  │
        │  → Archetype + ChunkIdx + Slot  O(1)
        └─────────────────────────────┘

存储层之上 ─────────────────────────────

        ┌──────────────────────────────┐
        │   Query / Group / Index      │
        │   EntityQuery  |  Group      │
        │   (遍历式)      | (索引式)    │
        └───────┬──────────────────────┘
                │
        ┌───────▼──────────────────────┐
        │   System / Processor          │
        │   SystemBase  |  SystemFn    │  ← 双轨
        │   Processor  |  IJobChunk    │
        └───────┬──────────────────────┘
                │
        ┌───────▼──────────────────────┐
        │   Pipeline / Phase           │
        │   PreUpdate → OnUpdate → ...  │
        │   per-phase sync points       │
        └─────────────────────────────┘
```

---

## 跨平台策略（关键纠正）

**之前错误：** 说 iOS 不能跑 C++、WebGL 不能跑 C++。

**纠正：** C++ 可以编译到所有平台。限制的是 **ISPC（仅 x86_64）** 和 **动态加载方式**。

- **iOS** 禁止 `dlopen`，但静态链接 `.a` 完全没问题。你的 C++ 调度器可编译成 `.a` 静态链接进去。
- **WebGL** 不能跑 `.dll`，但 Emscripten 把 C++ 编译成 WASM，在浏览器里跑。
- **Android** 原生 `.so` 是标准做法，NDK 交叉编译即可。

### 全平台覆盖表

| 平台 | 编译方式 | C++ 调度器 | SIMD | 加载方式 |
|------|---------|-----------|------|---------|
| **Windows** | MSVC | ✅ | ✅ ISPC | `NativeLibrary.Load` .dll |
| **Linux x64** | GCC/Clang | ✅ | ✅ ISPC | `NativeLibrary.Load` .so |
| **macOS x64** | Apple Clang | ✅ | ✅ ISPC | `NativeLibrary.Load` .dylib |
| **macOS ARM** | Apple Clang | ✅ | ⚠️ NEON intrinsics | `NativeLibrary.Load` .dylib |
| **Android ARM64** | NDK Clang | ✅ | ⚠️ NEON intrinsics | `NativeLibrary.Load` .so |
| **iOS ARM64** | Apple Clang | ✅ **静态链接** | ⚠️ NEON intrinsics | `[DllImport("__Internal")]` .a |
| **WebGL** | Emscripten | ✅ **WASM** | ❌ 无 SIMD | Emscripten 导出 .wasm |

### iOS 静态链接示例

```csharp
#if IOS
    [DllImport("__Internal")]
    private static extern void JobSystem_Initialize(int numThreads);
    // 与其他平台一样的完整 C++ 调度器（assist, work stealing, spinning 全部在）
#else
    var handle = NativeLibrary.Load(GetPlatformLibName());
#endif
```

### ISPC 降级策略

每个 ISPC 函数配 C++ 标量 + NEON 等效实现，编译时选择：

```cpp
#ifdef __ISPC_ENABLED__
    ispc::MoveJob(positions, velocities, count, dt);
#elif defined(__ARM_NEON)
    // NEON intrinsics 手写
    for (; count >= 4; count -= 4) {
        float32x4_t vx = vld1q_f32(&positions->x);
        vx = vmlaq_f32(vx, vld1q_f32(&velocities->x), dt_vec);
        vst1q_f32(&positions->x, vx);
        positions += 4; velocities += 4;
    }
    // 剩余标量
#else
    // 纯 C++ 标量（WebGL 等）
    for (int i = 0; i < count; i++)
        positions[i].x += velocities[i].x * dt;
#endif
```

### iOS/Android 编译器说明

**iOS 用 Apple Clang，Android 用 NDK Clang——都是 LLVM。** LLVM 的 auto-vectorization 比 MSVC 强得多。简单的 `pos += vel * dt` 循环 Clang 会自动生成 NEON 指令。所以没 ISPC 在 ARM 上的损失比在 Windows 上小。

### iOS/Android 竞争力分析

| 维度 | Unity DOTS | Bevy | Flecs | EnTT | **本项目** |
|------|-----------|------|-------|------|-----------|
| **ARM SIMD** | ✅ Burst 自动 | ⚠️ auto-vec | ⚠️ auto-vec | ⚠️ 手写 | ⚠️ NEON + auto-vec |
| **调度器能力** | ✅（无 assist） | ❌ system 级 | ❌ | ❌ | **✅ assist + work stealing** |
| **SoA 迭代** | ✅ | ✅ | ✅ | ✅ | ✅ |
| **C# 开发效率** | ❌ Burst 限制 | — | — | — | **✅** |

综合竞争力：**本项目 ≥ Unity > Bevy > Flecs ≈ EnTT**

---

## Phase 0: 已有优势（不修改）

| 组件 | 对标 | 状态 |
|------|------|------|
| JobSystem | Unity DOTS | ✅ 等价甚至超越 |
| ISPC/C++ 编译 | Unity Burst + C++ Job | ✅ 碾压（x86） |
| Archetype + SoA + 64B 对齐 | Bevy / Flecs | ✅ 到位 |
| Entity free list + version | 全行业一致 | ✅ O(1) |
| NativeContainer / SafetyHandle | Unity | ✅ 够好 |

---

## Phase 1: 基础设施优化（3 天）

### 1.1 TempAllocator → Per-Thread Stack

**当前：** `ConcurrentDictionary<IntPtr, int>` + 全局锁 + 帧末 O(n) FreeHGlobal
**改为：** 每线程 1MB 连续 buffer + 原子游标。帧末 O(1) Reset 归零。
**收益：** 分配无锁，释放 O(n)→O(1)，热路径零 GC。

**文件：** `src/EntJoy/Collections/TempAllocator.cs`（重写）

### 1.2 ChunkPool（全局 chunk 内存池）

**当前：** slab 分配器（64KB slab 从 heap 分配，slab 永不归还）
**改为：** 全局 `ChunkPool`，按 `(chunkSize, archetypeTypeHash)` 分池

```csharp
internal sealed class ChunkPool {
    private readonly ConcurrentDictionary<(int size, int typeHash), ConcurrentStack<nint>> _freeChunks = new();

    public nint Rent(int chunkSize, int archetypeTypeHash) {
        var key = (chunkSize, archetypeTypeHash);
        if (_freeChunks.TryGetValue(key, out var stack) && stack.TryPop(out var mem))
            return mem;
        return Marshal.AllocHGlobal(chunkSize);
    }

    public void Return(nint mem, int chunkSize, int archetypeTypeHash) {
        var key = (chunkSize, archetypeTypeHash);
        var stack = _freeChunks.GetOrAdd(key, _ => new ConcurrentStack<nint>());
        stack.Push(mem);
    }

    // 场景切换时释放所有空闲 chunk
    public void Trim() {
        foreach (var kv in _freeChunks)
            while (kv.Value.TryPop(out var mem))
                Marshal.FreeHGlobal(mem);
        _freeChunks.Clear();
    }
}
```

**Archetype.AddEntity** 走 ChunkPool，O(1) 不 IndexOf。

**Archetype.Remove** 空 chunk 回池而非 null Dispose。

**指针安全保证：** selective wait 确保结构性变更前所有 job 已完成。

> **之前说 slab "0 开销"是错的。** slab 永不释放的代价是内存只涨不缩。ChunkPool 回收解决了这个问题——空 chunk 立即回池复用。

**文件：**
- 新建 `src/EntJoy/Archetype/ChunkPool.cs`
- `src/EntJoy/Archetype/Archetype.cs`（大改：移除 slab，走 ChunkPool，IndexOf→O(1)）
- `src/EntJoy/Chunk/Chunk.cs`（小改：Dispose 只 null 指针）

### 1.3 AddEntity 去 O(n)

**当前：** `_chunkList.IndexOf(targetChunk)` 线性扫描
**改为：** `chunkIndex = _chunkList.Count - 1`（O(1)）

### 1.4 GetChunks 零分配

**当前：** 每次 `new List<Chunk>(_chunkList)`
**改为：** `internal IReadOnlyList<Chunk> Chunks => _chunkList`

### 1.5 QueryBuilder 零分配

**当前：** `WithAll<T>()` 每次 `new List<ComponentType>` + `new ComponentType[]`
**改为：** 像 `WithAny<T>()` 一样用静态缓存数组 `ComponentTypes<T>.Share`

**文件：** `src/EntJoy/Query/QueryBuilder.cs`

---

## Phase 2: Archetype Edges + 存储层（2 天）

### 2.1 Archetype Edges（Arch/Flecs 启发）

```csharp
class Archetype {
    private Dictionary<int, Archetype> _addEdges;   // typeId → archetype
    private Dictionary<int, Archetype> _removeEdges;

    public Archetype GetAddEdge(int typeId) { ... }  // 缓存查找结果
}
```

`EntityManager.AddComponent` / `RemoveComponent` 走 edge 路径，消除反复 `GetOrCreateArchetype`。

**文件：** `src/EntJoy/Archetype/Archetype.cs`、`src/EntJoy/Entity/EntityManager.cs`

### 2.2 Shared Components（Unreal Mass 启发）

```csharp
[SharedComponent]
struct TeamData { public int TeamId; }

foreach (var (pos, team) in query.Query<Position, Shared<TeamData>>()) {
    // team 是引用，不在 chunk 遍历时拷贝
}
```

**实现：** Archetype 增加 `Dictionary<int, object>`。同 archetype 的 entity 共享。

### 2.3 Chunk lazy zero

**当前：** `Unsafe.InitBlock` 零初始化整个 chunk
**改为：** `EnsureSlotZero(int slot)` 只零化该 slot

---

## Phase 3: 选择性等待 + Auto-Defer + CommandBuffer（4 天）

### 3.1 Per-Archetype Job Tracking

**当前：** `_activeJobs`（List）— 全部等
**改为：** `Dictionary<Archetype, List<JobHandle>>` — 只等相关 archetype

`NativeJobScheduler.ScheduleChunkCore` 通过 `TrackEntityJob(entityManager, handle, matchingArchetypes)` 传入。

### 3.2 Selective Wait

```csharp
CompleteArchetypeJobs(HashSet<Archetype> affected) {
    foreach (var arch in affected)
        foreach (var j in _archJobs[arch]) j.Complete();
}
```

每个结构性操作（NewEntity/DestroyEntity/AddComponent/RemoveComponent）只等目标 archetype。

### 3.3 DeferredCommandBuffer + Auto-Defer

**底层：** `DeferredCommandBuffer`（Phase 1 per-thread stack 分配）
**命令格式：** `[OpCode(4)] [payload...]`

**易用路径（auto-defer）：**
```csharp
EntityManager.NewEntity(types);  // IsDeferMode → 自动 defer
```

**深度路径（显式 ECB）：**
```csharp
var ecb = World.CreateCommandBuffer();
ecb.CreateEntity(types);
ecb.PlayBack();  // 用户控制 flush 时机
```

**Flush 路径：** `FlushDeferredCommands()` → `CompleteArchetypeJobs(affected)` → PlayBack

### 3.4 Batch Structural Changes

```csharp
World.CreateEntities(1000, typeof(Position), typeof(Velocity));
// 内部：1.锁定 archetype → 2.一次性 slab 分配 → 3.批量零初始化 → 4.返回 Entity[]
```

**文件：** `EntityManager.cs`（大改）、`NativeJobScheduler.cs`（中改）
- 新建 `DeferredCommandBuffer.cs`
- 新建 `EntityCommandBuffer.cs`
- 新建 `BatchOperations.cs`

---

## Phase 4: Processor/System 框架（4 天）

### 4.1 双轨 System API

**深度路径（高性能）：**
```csharp
struct MovementJob : IJobChunk { public void Execute(ArchetypeChunk chunk, ...); }

class MovementProcessor : Processor {
    public override void ConfigureQueries() {
        Require<Position>(Access.ReadWrite);
        Require<Velocity>(Access.Read);
    }
}
```

**易用路径（SystemBase）：**
```csharp
class MovementSystem : SystemBase {
    protected override void OnUpdate() {
        Entities.ForEach((ref Position pos, in Velocity vel) => {
            pos.Value += vel.Value * DeltaTime;
        }).Schedule();
    }
}
```

**最易路径（function-as-system，Bevy 风格）：**
```csharp
[UpdateInPhase(Phase.OnUpdate)]
static void Movement(SystemState state) {
    foreach (var (pos, vel) in state.Query<Position, Velocity>())
        pos.Value += vel.Value * state.DeltaTime;
}
```

### 4.2 查询 API 分层

```csharp
// Level 1: Struct query（最快）
var q = World.Query<Position, Velocity>();
foreach (var (pos, vel) in q.Chunks()) { }

// Level 2: Lambda callback（Arch 等价）
World.Query((ref Position pos, ref Velocity vel) => { });

// Level 3: Entity-aware（Entitas Group 等价）
World.Query((Entity e, ref Position pos, ref Velocity vel) => { });
```

### 4.3 Pipeline Phases（Flecs 风格）

```csharp
enum Phase { PreUpdate, OnUpdate, PostUpdate, FrameEnd }

// Phase 内：read-write 冲突分析 → 分层并行
// Phase 间：严格串行
```

### 4.4 执行 Flags（Unreal Mass）

```csharp
[ExecuteIn(ExecuteIn.Editor | ExecuteIn.Player)]
[RequireGameThread]
class DebugRenderSystem : SystemBase { ... }
```

**文件（新建）：** `SystemBase.cs`、`SystemFn.cs`、`Processor.cs`、`SystemGraph.cs`、`Phase.cs`、`Query.cs`、`SystemState.cs`
**修改：** `World.cs`（大改：AddSystem + Update）

---

## Phase 5: 易用性基础设施（3 天）

### 5.1 World Events（LeoECS + Entitas 启发）

```csharp
World.Publish(new DamageEvent { Target = entity, Amount = 10 });
World.Subscribe<DamageEvent>((in DamageEvent e) => { });
```

### 5.2 One-Frame Components（LeoECS）

```csharp
entity.AddOneFrame(new JustSpawned { Time = Time.time });
struct JustSpawned : IOneFrameComponent { public float Time; }
```

### 5.3 Entity Index / Group（Entitas）

```csharp
var group = World.GetGroup<TeamId>(team => team.Value == Team.Player);
// 自动维护，O(1) 索引查询
```

### 5.4 Context（Entitas）

```csharp
var gameWorld = new World("Game");
var uiWorld = new World("UI");
var inputWorld = new World("Input");
```

### 5.5 DI（LeoECS + Svelto）

```csharp
class PlayerSystem : SystemBase {
    [Inject] EntityManager _entityManager;
    [Inject("Player")] Entity _player;
}
```

**文件（新建）：** `EventBus.cs`、`IReactiveSystem.cs`、`IOneFrameComponent.cs`、`Group.cs`、`InjectAttribute.cs`
**修改：** `EntityManager.cs`、`World.cs`

---

## Phase 6: 实体关系（5 天）

### 6.1 存储模型：Non-Fragmenting（Bevy 0.15）

```
关系列在 Chunk 上扩展 SoA：
[Entity array] [Comp0] [Comp1] [Relationship0: u64] [Relationship1: u64]

每个关系 = (RelationTypeID << 32) | TargetEntityID
```

Non-fragmenting：`(ChildOf, Player1)` 和 `(ChildOf, Player2)` 在同一 archetype 同一列中。

### 6.2 级联删除

```csharp
DestroyEntity(Player1) → 遍历所有 ChildOf 列 → 找到 Target==Player1 → 递归删除
```

### 6.3 Prefab / IsA（Flecs 独有优势）

```csharp
Entity prefab = World.CreateEntity(typeof(Position), typeof(Health));
Entity instance = World.CreateEntity(typeof(IsA));
SetComponent(instance, new IsA { Prefab = prefab });
// 继承组件在 prefab 上只存一份
```

**文件（新建）：** `IRelationship.cs`
**修改：** `Archetype.cs`（关系列）、`Chunk.cs`（关系存取）、`EntityManager.cs`（级联删除）

---

## Phase 7: 共享组件 / 子系统（Unreal Mass，2 天）

**SharedComponent：** 同 archetype entity 共享数据，不拷贝到每个实体。
**Subsystem Query：** `RequireSubsystem<PhysicsWorld>()`。

---

## Phase 8: Source Generator（Entitas，远期 3-5 天）

### 8.1 Component 存取生成

```csharp
// 用户写：
partial struct Health : IComponent { public int Value; }

// 生成器生成：
partial struct Health {
    public static ref Health Get(Entity e) => ref World.EntityManager.GetComponent<Health>(e);
}
```

### 8.2 System 注册生成

自动收集所有 system + 自动注入 EntityManager/DeltaTime + 自动分析组件读写。

### 8.3 Reactive System 生成

```csharp
[Reactive(EventType.Added)]
partial class DebugLogSystem : SystemBase {
    void OnUpdate(Entity entity, ref Health health) { ... }
}
```

**文件（新建）：** `src/EntJoy.SourceGenerator/ComponentGenerator.cs`、`SystemGenerator.cs`、`ReactiveGenerator.cs`

---

## Phase 9: 组件 Managed 类型与原生内存投影

### 核心问题：组件能不能放 string/List/Dictionary？

**结论：** `IComponentData` 允许任何 struct（不强制 unmanaged）。但自动分拆存储：

```csharp
struct Player : IComponentData {
    public string Name;                        // managed → ManagedStore
    public int Health;                         // unmanaged → SoA
    public NativeList<Entity> Allies;          // 纯数据 16B struct → SoA
}
```

框架自动分拆（对用户透明）：

| 字段类型 | 存储位置 | C++/ISPC 可读？ |
|---------|---------|----------------|
| `int`, `float`, `Entity` 等纯数据 | chunk SoA | ✅ |
| `NativeString`（16B struct） | chunk SoA | ✅ |
| `NativeList<T>`（16B struct） | chunk SoA | ✅ |
| `NativeArray<T>`（16B struct） | chunk SoA | ✅ |
| `string`, `List<T>`（托管引用） | **ManagedStore**（字典） | ❌ 仅主线程 C# |

### ManagedStore 实现

```csharp
sealed class ManagedComponentStore {
    // 每个 managed 组件类型一个 sparse set
    private readonly Dictionary<Type, object> _stores = new();

    sealed class Store<T> {
        private GCHandle[] _handles = new GCHandle[32];
        public void Set(Entity entity, T value);
        public ref T Get(Entity entity);
        public void Remove(Entity entity);  // swap-pop + GCHandle.Free
    }
}
```

### 原生内存投影（NativeProjection）

**为什么不能直接 GCHandle pin + C++ 读 .NET string？** 因为 `GCHandleType.Pinned` 阻止 GC 压缩，长时间导致堆碎片化，且 `.NET string` 布局是运行时实现细节。

**替代方案：所有 in-hot-path 数据用原生容器表示：**

| C# 类型 | 原生投影 | 结构 | 放 SoA | C++ 读 |
|---------|---------|------|--------|--------|
| `string` | `NativeString` | `char* + length + capacity` | ✅ 16B | ✅ |
| `List<T>` (T unmanaged) | `NativeList<T>` | `T* + length + capacity` | ✅ 16B | ✅ |
| `T[]` (T unmanaged) | `NativeArray<T>` | `T* + length + allocator` | ✅ 16B | ✅ |
| `Dictionary<K,V>` (K,V unmanaged) | `NativeDictionary<K,V>` | 原生内存哈希表 | ✅ 16B | ✅ |
| `HashSet<T>` (T unmanaged) | `NativeHashSet<T>` | 同上，无 value | ✅ 16B | ✅ |
| `Queue<T>` / `Stack<T>` | `NativeQueue<T>` / `NativeStack<T>` | 原生内存 | ✅ | ✅ |
| `object` | ❌ | — | ❌ | ❌ |

**NativeString 跨语言定义：**

```cpp
// C++
struct NativeString {
    char* buffer;      // 原生内存缓冲区
    int32_t length;
    int32_t capacity;
};
```

```csharp
// C# — 二进制布局一致
[StructLayout(LayoutKind.Sequential)]
unsafe struct NativeString : IComponentData {
    private char* _buffer;
    private int _length;
    private int _capacity;

    public ReadOnlySpan<char> AsSpan() => new(_buffer, _length);
    public override string ToString() => new(_buffer, 0, _length);
    public void FromString(string s) { /* 确保容量 → memcpy */ }
}
```

### 为什么 NativeDictionary 可以但 .NET Dictionary 不行

.NET Dictionary 内部全是 GC 对象（`int[] buckets`, `Entry[] entries`, `IEqualityComparer`）——哈希表的存储就是数组+整数。**这些都可以在原生内存里实现。**

```cpp
// C++ NativeDictionary（ChunkNativeArray.h）
template<typename K, typename V>
struct NativeDictionary {
    int* buckets;          // 原生内存哈希槽
    Entry<K,V>* entries;   // 原生内存键值对
    int count;
    int capacity;
};
```

**好处：**
- 放在 SoA 列里（16B struct），C++ 直读
- K 和 V 必须 unmanaged（K=V=`NativeString` 可以）
- 小数据（< 32 对）用 `NativeList<(K,V)>` + 线性搜索，比哈希快

**跟所有框架的对比：**

| 项目 | string 存储 | C++ 可读？ | 用户感知 |
|------|-----------|-----------|--------|
| **Unity DOTS** | `FixedString64` | ✅ | 必须用 FixedString，不能写 string |
| **Bevy** | `String`（Rust 原生） | ✅ | 自然 |
| **本项目** | `NativeString` 原生 + C# `string` 视图 | ✅ | **写 string，框架自动转** |

---

## 实施路线图

```
Phase 1: 基础设施优化（3 天）
  TempAllocator per-thread stack
  ChunkPool（全局池化回收）
  AddEntity O(1)
  GetChunks 零分配
  QueryBuilder 零分配

Phase 2: 存储层增强（2 天）
  Archetype Edges
  Shared Component
  Chunk lazy zero

Phase 3: 选择性等待 + Auto-Defer + ECB（4 天）
  Per-Archetype Job Tracking
  Selective Wait
  DeferredCommandBuffer
  ECB API
  BatchOperations

Phase 4: System/Processor 框架（4 天）
  双轨 System API
  Query 分层
  Pipeline Phases
  Execute Flags

Phase 5: 易用性基础设施（3 天）
  World Events
  One-Frame Components
  Entity Index / Group
  Context
  DI

Phase 6: 实体关系（5 天）
  Non-Fragmenting 关系列
  级联删除
  关系查询 + Index 加速
  Prefab IsA（可选）

Phase 7: 共享组件 / 子系统（2 天）

Phase 8: Source Generator（远期，3-5 天）
  Component 存取生成
  System 注册生成
  Reactive System 生成

Phase 9: 原生内存投影（持续）
  NativeString, NativeDictionary
  ManagedComponentStore
  Auto-Generate 分拆
```

---

## 基准测试方案

新增 `EntJoySample/06_ArchBenchmark/`，对比所有后端：

| # | 后端 | Schedule | Execute | 期望 |
|---|------|---------|---------|------|
| 1 | C# Task.Run | `Task.Run` | C# | 最慢（对照） |
| 2 | C# ZeroAlloc | 现有 C# fallback | C# | |
| 3 | C# IJobChunk | `NativeJobScheduler` | C# callback | ~3-5ms（1M 实体） |
| 4 | C++ IJobChunk | NativeTranspiler | C++ 标量 | ~1-2ms |
| 5 | ISPC IJobChunk | NativeTranspiler | C++ SIMD | ~0.3-0.8ms |

测试负载（与 Arch 一致）：`pos += vel * dt`，1M 实体，`Position(3 floats) + Velocity(3 floats)`。

---

## 文件修改总表

| Phase | 文件 | 操作 |
|-------|------|------|
| 1 | `TempAllocator.cs` | 重写 per-thread stack |
| 1 | **新建** `ChunkPool.cs` | 全局 chunk 内存池 |
| 1 | `Archetype.cs` | 大改：移 slab，走 ChunkPool |
| 1 | `Chunk.cs` | Dispose 只 null |
| 1 | `QueryBuilder.cs` | WithAll 零分配 |
| 2 | `Archetype.cs` | 新增 edges + SharedComponent |
| 2 | `Chunk.cs` | SharedComponent + lazy zero |
| 2 | `EntityManager.cs` | AddComponent/Remove 走 edge |
| 3 | `EntityManager.cs` | 大改：archJobs + selective wait + auto-defer |
| 3 | `NativeJobScheduler.cs` | TrackEntityJob 签名 |
| 3 | **新建** `DeferredCommandBuffer.cs` | ECB 核心 |
| 3 | **新建** `EntityCommandBuffer.cs` | ECB API |
| 3 | **新建** `BatchOperations.cs` | 批量创建 |
| 4 | **新建** `System/`（7 文件） | 双轨 system 框架 |
| 4 | `World.cs` | 大改：AddSystem + Update |
| 5 | **新建** `EventBus.cs` 等 | 易用性设施 |
| 5 | `EntityManager.cs` | 中改：group + one-frame |
| 6 | **新建** `IRelationship.cs` | 关系接口 |
| 6 | `Archetype.cs` | 关系列支持 |
| 6 | `Chunk.cs` | 关系存取 |
| 6 | `EntityManager.cs` | 级联删除 |
| 7 | `Archetype.cs` | SharedComponent（revisit） |
| 8 | **新建** SourceGenerator 项目 | 3 个 generator |
| 9 | **新建** `NativeString.cs` | 原生字符串 |
| 9 | **新建** `NativeDictionary.cs` | 原生哈希表 |
| 9 | **新建** `ManagedComponentStore.cs` | managed 存储 |

---

## 验证方案

```
每个 Phase 独立可测：

Phase 1: 基准 10000 次 AddEntity O(n²)→O(n)
         ChunkPool：空 chunk 回池复用 vs slab 永不释放内存对比
Phase 2: 基准 10000 次 AddComponent 有/无 edge 对比
Phase 3: 正确性 + wait 次数 N→1
Phase 4: 多 system 按序执行 + 并行
Phase 5: Event/OneFrame/Group 正确性
Phase 6: 级联删除 + 关系查询
Phase 7: SharedComponent 共享
Phase 8: Generator 编译输出正确
Phase 9: NativeString C++ 直读正确性
```