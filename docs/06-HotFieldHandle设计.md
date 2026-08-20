# 06 HotField 句柄层设计(OOP 访问面:普通 class + Attribute → 缓存组件指针访问 ECS 内核)

> 本文档记录样例 `src/EntJoySample/06_HotFieldHandle/` 的设计与验证结论。它把 HotField 研究项目（`E:\Code\HotField`）的核心命题——「保留 OOP 编程风格、获得 ECS 级性能」——搬进 EntJoy：**用户写与 plain class 逐字节相同的 OOP 代码（class + `Update(dt)`），`[HotField]` 字段被视作组件存进 ECS chunk，生成器给 class 注入「缓存组件指针」（指向 chunk SoA 元素），实体生命周期交给 ECS 管理，System（`IJobChunk`）并行消费同一 chunk。**

---

## 1. 架构决策（最终）

**单一存储 = chunk，HotField 直接访问 ECS 内核。** 不做平铺 SoA 双存储（那需要同步点 + 自己管理实体增删）：

| 决策 | 结论 |
|---|---|
| 存储 | 只有 chunk（archetype + 字段级 SoA），无第二份存储、无同步点 |
| `[HotField]` 字段 | 视作**组件**（自动组件生成：`Pos`→`Position`、`Vel`→`Velocity`） |
| 实体生命周期 | 交给 ECS（`CreateEntity`/`DestroyEntity`/版本号） |
| OOD 访问 | class 持有**缓存组件指针**（指向 chunk 元素），`Bind` 阶段边界重解析，零解析链 |
| 密集遍历 | class 容器 1.4x（对象流税）；struct 句柄 0.96x（连续值数组）；System 3.4x（并行） |

- **不双存储**：OOD 面与 System 共享同一个 chunk，零转换、零同步（OOD↔DOD 一致 PASS）。
- **不自己管实体增删**：ECS 的 `Entity` + 位置表 + 版本号已具备，缓存指针失效由 `Bind` + `StructuralVersion` 处理。

---

## 2. 性能（1M，实测）+ 4 路拆解

| 密集 OOD 形态 | avg | 相对 Class |
|---|---|---|
| ClassEntity（纯 OOP 基线，数据在对象，单流） | 2.71 ms | 1.00x |
| StructEntity（AoS 值数组） | ~1.2 ms | ~0.45x |
| **ChunkLoop（纯 chunk 数据流，无对象）** | 1.72 ms | **0.63x** |
| **PlayerObjOnly（纯对象流，只读指针）** | 1.99 ms | **0.74x** |
| **Player（class + 缓存组件指针，对象流+chunk数据流）** | 3.68 ms | **1.36x** |
| HotMoveChunkJob（System，并行） | 0.79 ms | 3.42x 快于 Class |

**4 路拆解验证**：ObjOnly(1.99) + ChunkLoop(1.72) = 3.71ms ≈ Player 实测 3.68ms ✅。

**关键修正：chunk 数据流根本不贵**（ChunkLoop 0.63x，顺序数组、预取充分，比 ClassEntity 还快）。1.36x 的真相是：

```
Player      = 对象流(读指针) 1.99ms + chunk数据流(读写数据) 1.72ms = 3.71ms(双流)
ClassEntity = 对象流(数据在对象) 2.71ms(单流)
```

**class 容器 = 双流税**：读对象拿句柄 + 读/写 chunk 拿数据，两条内存流；ClassEntity 只读一条（对象即数据）。这是 class 容器访问 chunk 的**物理地板**，不是"chunk 加载贵"。

- **纯 chunk 路径（System/ChunkLoop）是快的**（0.63x 串行 / System 并行 3.42x）——密集就该走这里。
- **class OOP 面 1.36x** 只适合稀疏（每帧几~几千实体，绝对时间可忽略）。

**最终方案（分层）**：
- **稀疏 OOD**：class + 缓存组件指针（`Bind` 阶段边界刷新），无感、绝对时间可忽略。
- **密集批量**：System（`IJobChunk`）走同一 chunk 直读，0.63x 串行 / 3.42x 并行。
- 两者共享同一 chunk、零转换零同步；`Update(dt)` 业务体逐字节相同。

---

## 3. 用户模型：普通 class + Attribute

```csharp
[HotFieldEntity]                        // ← 用户加一个 Attribute
public class Player
{
    // ── 生成器注入的机械部分(示例手写)──
    public float2* PosPtr;              // 缓存 Position 组件元素指针(零解析链)
    public float2* VelPtr;              // 缓存 Velocity 组件元素指针

    public ref float2 Pos => ref *PosPtr;
    public ref float2 Vel => ref *VelPtr;

    // ── 用户写的普通业务逻辑(与 ClassEntity.Update 逐字节同构)──
    public void Update(float dt) => Pos += Vel * dt;
}
```

对比 plain class：

```csharp
public class ClassEntity
{
    public float2 Pos;
    public float2 Vel;
    public void Update(float dt) => Pos += Vel * dt;
}
```

游戏循环零差异：`foreach (var p in players) p.Update(dt)`。属性把 OOP 访问重定向到 chunk SoA 元素。

> **`__entity` 不进对象**：把 ECS entity 桥放进 class 会让对象涨到 32B（流体积 40MB+8MB），实测 1.5x；由框架在平行的 `Entity[]` 中维护，结构操作走它。

---

## 4. 阶段边界刷新（缓存指针如何保持有效）

缓存指针在**结构变更**（add/remove 组件、destroy → 实体迁移 chunk / swap-remove）后失效。框架在**阶段边界**统一重解析：

```
迭代窗口(帧内): 指针有效,零检查,零解析  ← foreach (var p in players) p.Update(dt)
        ↓
结构变更:  NewEntity / DestroyEntity / AddComponent / RemoveComponent
        ↓  (实体可能在 chunk 间迁移,指针悬垂)
阶段边界:  player.Bind(world, entity)  ← 重解析一次
        ↓
下一个迭代窗口
```

`Bind` 的实现（手写示例，生成器自动产出）：

```csharp
public void Bind(World world, Entity entity)
{
    var em = world.EntityManager;
    ref var info = ref em.GetEntityInfoRef(entity.Id);   // 位置表 → (archetype, chunk, slot)
    var arch = info.Archetype;
    var chunk = arch.ChunkList[info.ChunkIndex];
    int slot = info.SlotInChunk;
    PosPtr = (float2*)chunk.GetComponentArrayPointer(arch.GetComponentTypeIndex<Position>()) + slot;
    VelPtr = (float2*)chunk.GetComponentArrayPointer(arch.GetComponentTypeIndex<Velocity>()) + slot;
}
```

- 阶段边界成本 = **O(受影响实体)** 一次，不是逐访问。
- 样例 `RunPhaseBoundaryRefresh` 实证：destroy 一个实体 → swap-remove 移动末位实体 → 重 Bind → 缓存指针 == 权威 `GetComponent`，PASS（maxAbsDiff=0）。
- 与 EntJoy 既有纪律一致：`EntityIndexInWorld` 位置表 + `StructuralVersion` 版本号（07 文档）。

---

## 5. System 支持

```csharp
public struct HotMoveChunkJob : IJobChunk
{
    public float Dt;
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var pos = chunk.GetComponentDataSpan<Position>();
        var vel = chunk.GetComponentDataSpan<Velocity>();
        for (int i = 0; i < chunk.Count; i++)
            pos[i].Value += vel[i].Value * Dt;
    }
}
// job.Schedule(builder).Complete();
```

- 直接消费与 OOD 面同一 chunk，零转换/零同步，结果逐元素一致（OOD↔DOD PASS）。
- 批量更新走原生 worker 池，3.41x。

---

## 6. 正确性

- **Class == Player（组件指针）**：60 帧 `Update` 逐元素 maxAbsDiff=0 PASS。
- **阶段边界刷新**：destroy + swap-remove + 重 Bind 后，缓存指针 == 权威 GetComponent PASS。
- **OOD↔DOD**：Player 缓存指针循环 vs `HotMoveChunkJob` 共享 chunk 逐元素 maxAbsDiff=0 PASS。

---

## 7. 测量方法

- 轮转采样：Class / Struct / Player / PlayerHandle / System 在同一稳态相位内背靠背采样（warmup + measure 帧），`Stopwatch.GetTimestamp`，输出 p50/p95/p99 + `BENCH|case=HotField-{variant}` 行。
- 实体数默认 100 万（`ENTJOY_HF_ENTITIES` 可调），`ENTJOY_BENCH_WARMUP / FRAMES` 复用全局约定。
- 机器热降频影响绝对数值，但**同一 run 内相对比值是验收依据**。

---

## 8. 与 HotField 设计要点的对照

| # | 困难 | HotField 解法 | EntJoy 本样例 |
|---|---|---|---|
| 1 | `ref` 属性带边界检查 | 下沉裸指针（ChunkPtr 快 15%） | 缓存组件指针 + ref 属性（零解析链） |
| 2 | "零成本"依赖 JIT 内联 | `AggressiveInlining` | 同（属性 getter + Update 全标注） |
| 4 | 指针依赖 GCHandle.Pinned | pin 纪律 / NativeMemory | **无此问题**：chunk Persistent 原生指针稳定 |
| 5 | 结构改变使指针悬垂 | 指针失效 + 阶段边界 | **阶段边界 `Bind` 重解析**（§4） |
| 6 | SoA swap-remove 写错对象 | ECB + 版本号 | **已有**：EntityManager + 版本号 |
| 7 | 实体复用 / stale id | free-list + 版本号 | **已有**：`Entity`（Id+Version） |
| 12 | 预存指针结构膨胀 | 不预存，用索引 | 缓存 2 个组件指针（24B 对象）vs 预存 N 字段指针（膨胀）——字段多时用单基址+偏移 |
| 13 | AoS 块单字段寻址成本高 | 字段级 SoA | chunk 字段级 SoA |
| 14 | 进程级单例 store 冲突 | 实例 store | **World 实例上下文** |

---

## 9. 为什么 class 形态是 1.40x、struct 句柄是 0.96x

- **class 对象**：`Player[]` 存 8B 引用 → 指向散落堆对象（24B，含对象头）。遍历 = 引用跳转 + 对象头 + 读指针 + 解引用 chunk。流体积 48MB（8MB 引用 + 24MB 对象 + 16MB chunk 数据）vs Class 的 32MB → **1.40x，对象流税，追不平**。
- **struct 句柄**：`PlayerHandle[]` 是 16B 连续值数组（无对象头、无跳转）。遍历 = 读连续元素 + 解引用 chunk。流体积 32MB → **0.96x，持平**。
- 二者**指针解析逻辑完全一样**（`*PosPtr += *VelPtr * dt`），差距纯粹是「散落 class 对象 vs 连续值数组」的容器税。
- **结论**：OOD 持平的落点是**值类型句柄**（struct），由生成器从 class 定义自动产出——`Update(dt)`/字段访问逐字节相同，用户无感。

---

## 10. 多字段扩展

`E:\Code\HotField` 的 2/50/200 字段套件已移植到 `HotFieldMultiField.cs`（同目录独立文件），含全字段 + Top10/Top50 子集测试，验证 shell 形态（Class/Struct/ChunkPtr/System）随字段数扩展的规律（字段越多带宽越主导；子集访问时 SoA 优势最明显）。

---

## 11. 后续方向 / 待探索：OOD（连续+随机）不落后于 class

**目标**：让 OOD 面在**连续和随机**访问上都 ≥ class，且保留 class 容器、ECS 管生命周期、单存储。

**4 路拆解的启示**：chunk 数据流 0.63x（便宜），class 对象流 0.74x（便宜），两者相加 1.36x——class + chunk 指针是**双流**地板。要打破双流，只有让「对象流」和「数据流」合一。

### 待探索方案 A：ECS 托管的稳定 dense 索引（HotStore 一等存储，但 ECS 管生命周期）—— 已实测

```
class PlayerIndexed { int Index; HotFieldStore Store; }   // 对象含 int(4B)+Store 引用(8B)+头 = 24B
ref float2 Pos => ref Store.Positions[Index];   // 平铺数组,一次下标
ECS 管:  free-list 分配/回收(不 swap-remove,索引稳定) + 版本号防悬垂
```

**实测（1M）**：

| 场景 | HotIndex | AoS包 | Static(小对象) |
|---|---|---|---|
| **连续** | 0.97x ✅ | ~0.6x ✅ | ~0.45x ✅ |
| **随机** | 3.20x ❌ | 3.07x ❌ | **2.58x** ❌ |

- **连续达标**：三变体都接近 class。
- **随机未达标（2.6~3.2x）**：瓶颈是**散落 class 对象 deref 本身**，不是数组个数（AoS 包把数组 3→2 次几乎无改善；Static 把对象 24B→16B 有改善但仍在 2.6x）。
- **随机 ≥ class 的唯一办法 = 数据在对象里**（AoS class 对象数组），但那样 System 失去 SoA 优势。

**根本权衡（不可兼得，全变体实测）**：

| 存储 | OOD 连续 | OOD 随机 | System |
|---|---|---|---|
| SoA 平铺 / chunk（当前） | ~0.9x | 2.4~3.0x（对象+数据双随机读） | 3.74x |
| **AoS class 对象数组（ECS 托管 free-list）** | ~0.5x | **~1.0x（数据在对象,1 次对象读）** | 2.12x（散落对象并行） |

- **「OOD 连续 & 随机都 ≥ class」的答案是 AoS class 对象数组（ECS 托管 free-list 生命周期）**：数据在对象 → 随机 = 1 次对象读（class 同速）、连续 = 对象单流。
- **代价**：System 并行扫散落对象 = 2.12x，弱于 SoA 的 3.74x。
- 反之要 System 强（3.74x）→ 必须 SoA → OOD 随机 2.4~3.0x。
- **这是"System 强"与"OOD 随机 ≥ class"的根本取舍**，由用户按应用偏好裁决：密集批量优先 → SoA；OOD 逐实体优先 → AoS class 对象。

### 待探索方案 B：类数组双流但对象流用「紧凑索引」降体积

- 已并入方案 A 的 Static 变体（对象只留 `int Index`），连续达标、随机 2.41x。

### 裸指针平铺 vs 原始 HotField ChunkPtr vs 原始 class（1M 实测，HotFieldPtrCompare.cs）

| 变体 | 连续 | 随机 | struct 尺寸 |
|---|---|---|---|
| Class（原始 class，数据在对象） | 1.00x | 1.00x | 24B 对象 |
| **FlatPtr（裸指针平铺：`int Index` + 静态 `float2*` 基址）** | **0.66x ✅** | 1.30x | **4B** |
| **ChunkPtr（原始 HotField：每实体持 2 基址 + 偏移）** | 1.01x | 1.45x | 24B |

- **连续：裸指针平铺 0.66x（快于 class 1.5 倍）**，原始 ChunkPtr 1.01x（持平）。验证用户记忆「指针→静态平铺数组开销很低」。
- FlatPtr 优于 ChunkPtr：**4B struct（共享静态基址）vs 24B struct（每实体存基址）**，对象流 4MB vs 24MB。
- **平铺快于 chunk 的本质**：对象小（int 索引 vs 2 指针）+ 数组全程连续（无 chunk 分段跳转）+ 裸指针无边界检查。

### AoS 组件指针 + chunk（HotFieldAoSChunk.cs，实测）

```
PlayerData{Pos,Vel} 一个组件(数据内联) → chunk 存 PlayerData[]
class PlayerAoS { PlayerData* _data; }   // 1 指针,Pos/Vel 同 cache line
```

| 形态 | 连续 | 随机 |
|---|---|---|
| AoS 组件指针 + chunk（class） | 1.44x | 2.06x |
| SoA 组件指针 + chunk（class） | 1.36x | ~2.3x |

**结论：class 容器 + chunk（无论组件 SoA 还是 AoS）都是"对象流 + chunk 数据流"双流**，AoS 组件（数据内联同 cache line）不帮 class 容器——class 仍要先读散落对象拿指针、再 deref chunk。

### class 数据在对象 + ECS 托管 free-list（HotFieldClassStore.cs，实测）

```
用户写 class Player { float2 Pos; float2 Vel; Update() }   // 数据在对象
ECS 管: free-list 稳定索引(不 swap-remove) + 版本号(防悬垂) + 平行数组
```

| 指标 | 结果 |
|---|---|
| **连续** HotClass/Class | **1.24x**（≈class） |
| **随机** HotClass/Class | **1.21x**（≈class） |
| System 并行 | 0.84x（~1.2x 快于 class） |

- **class 数据在对象 → OOD 连续/随机都 ≈ class，不再有 chunk/SoA 的 2~3x 负优化**。
- 多出的 ~0.2x 是生成器注入的 `__index`（4B 生命周期字段，28B 对象 vs ClassEntity 24B）；可挪平行数组消除（但 Destroy 要多一次查找）。
- **代价**：System 并行扫 class 数组只有 ~1.2x（弱于 SoA 3.7x）——因为数据在对象（AoS），不是字段级 SoA。

### 随机访问的最终答案（能否 ≥ class）

- **class 容器 + chunk/SoA = 随机 ~2~3x**（对象 deref + 字段数组/位置表双随机读），组件排成 AoS 也没用。
- **class 数据在对象（ECS 托管）= 随机 ~1.2x（≈class，不负优化）**，System 并行 ~1.2x。
- **要随机 ≈ class + 数据在对象，class 形态唯一解**；代价是 System 从 SoA 3.7x 降到 ~1.2x。
- 取舍：OOD 逐实体随机优先 → class 数据在对象（≈1.2x 随机，System 1.2x）；密集批量优先 → SoA（System 3.7x，随机 2~3x 稀疏可忽略）。

### 优先级

- 「OOD 随机 ≥ class」硬要求 → **class 数据在对象 + ECS free-list（1.2x 随机，System 1.2x）**。
- 「System 最强」优先 → SoA 平铺（System 3.7x），随机 ~2~3x（稀疏游戏逻辑绝对时间可忽略）。
- 建议按 06 分层：稀疏 OOD 用 class（数据在对象），密集批量用 System（SoA 或 AoS 并行）。

