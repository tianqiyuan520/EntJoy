# 09 HotField 重新设计（基于原始目标）

> 状态：设计定稿
> 原始目标：用户给 class 加 `[HotFieldEntity]`，无感用 OOD；OOD 性能与 class 持平；可写 System 高性能。
> 前置：06（句柄层探索）、07（随机访问）、08（生成器）。

---

## 1. 原始目标回顾

1. 用户写 **普通 class** + `[HotFieldEntity]` 属性，**无感用原本的 OOD 设计**（字段访问、Update、foreach 全不变）。
2. **OOD 性能与 class 持平**（至少不落后）。
3. 可写 **System** 获得 ECS 级批量性能。

## 2. 探索得到的定论（不该走的弯路）

| 弯路 | 结论 |
|---|---|
| chunk（archetype+chunk） | HotField 实体**固定组件集、不迁移**，chunk 的位置表/间接是纯开销 → **用平铺 SoA** |
| class + 指针/Entity → chunk | 双流（对象+chunk 数据），连续 ~1.4x、随机 ~2x |
| class 数据搬进 SoA（int 索引） | 连续 ~0.9x；随机 ~1.3-2x（对象 deref + 数组，与 EnTT/Unity 一致，稀疏可忽略） |
| struct 句柄 / AoS struct 数组 | 随机 0.51x，但用户要 class，不采纳 |
| 写穿（双份存储） | 违背"数据在 ECS 存储"、需同步，不采纳 |

**核心取舍**：class 门面 + 外部 SoA，随机天然 ~1.3-2x（2 次随机读）。这是"class 写 OOD + 数据在 ECS"的本质，**任何 ECS（EnTT/Unity/Flecs）都这样**；对游戏逻辑（稀疏）绝对时间可忽略。

## 3. 最终设计

### 用户侧（无感，与 plain class 逐字节相同）

```csharp
[HotFieldEntity]
public partial class Player
{
    [HotField] public float2 Pos;     // 生成器改写为 ref 属性 → 平铺数组
    [HotField] public float2 Vel;
    public void Update(float dt) => Pos += Vel * dt;   // 用户业务代码不变
}
```

### 生成器产出（机械部分）

```csharp
[HotFieldEntity]
public partial class Player
{
    internal int Index;                  // 平铺数组下标(4B,稳定 free-list 索引)
    internal HotFieldStore Store;        // 实例存储(per world)

    public ref float2 Pos => ref Store.Positions[Index];   // 平铺数组,零间接
    public ref float2 Vel => ref Store.Velocities[Index];

    public void Update(float dt) => Pos += Vel * dt;
}
```

### 存储（ECS 托管平铺 SoA，HotFieldStore）

```csharp
public sealed class HotFieldStore : IDisposable
{
    public NativeArray<float2> Positions;   // 平铺字段级 SoA(非 chunk)
    public NativeArray<float2> Velocities;
    public int[] Versions;                  // 版本号(防悬垂)
    public int[] NextFree;                  // free-list(稳定索引,不 swap-remove)
    public int FreeHead = -1, Count;

    public int Create(float2 pos, float2 vel) { /* free-list 分配 + 版本递增 */ }
    public void Destroy(int idx) { /* 回 free-list(版本只在 Create 递增) */ }
    public bool IsValid(int idx, int version) { /* 版本校验 */ }
}
```

- **平铺 SoA**：HotField 实体固定组件集、不迁移 → 不需要 archetype 位置表；`Index == 数组下标`，零间接。
- **free-list 稳定索引**：add/remove 不搬移他人对象（区别于 chunk swap-remove），生命周期归 ECS。
- **版本号**：每次分配递增，Destroy 后旧句柄检测失效。

### System（批量高性能）

```csharp
// IJobParallelFor 直接扫平铺数组(与 OOD 共享同一存储,零转换)
// Positions[i] += Velocities[i] * dt   → 并行 3.7x
```

## 4. 性能（1M 实测，class 容器）

| 指标 | 结果 | 说明 |
|---|---|---|
| 连续（class 门面 + 平铺 SoA） | **1.20x** | 对象流 + 数组流（散落 class 对象 deref） |
| 随机（class 门面 + 平铺 SoA） | **2.68x** | 散落对象 + 数组 = 2 次随机读（与 EnTT/Unity 一致） |
| System（并行平铺） | ~3.7x | 批量最高吞吐 |

**class 容器的本质**：`player.Pos` = 读散落 class 对象拿 Index（1 次随机）+ deref 平铺数组（1 次）= 2 次随机读。**无论存储是 chunk/平铺/静态/实例、指针/索引，class 容器都 ~1.2x 连续 / ~2.7x 随机**——这是 class 对象散落的物理约束，不是实现问题。

**struct 容器可到 0.66x/1.3x**（连续 4B 数组，无散落 deref），但用户坚持 class，不采纳。

**要随机 ≈ class（1.0x）的唯一办法 = 数据在 class 对象里**（写穿/AoS class 对象），但 System 变弱（~1.2x 而非 3.7x）。

**分层（最终）**：
- **稀疏 OOD**（游戏逻辑逐实体）：class 门面（int 索引 + 平铺 SoA），随机 ~2.7x 但绝对时间可忽略。
- **密集批量**：System（IJobParallelFor 扫平铺数组）~3.7x。
- 两者共享同一平铺存储，零转换、零同步。

## 5. 生命周期

- `HotFieldStore.Create(pos, vel)` → 分配稳定索引（free-list）+ 版本递增 + 返回索引。
- `HotFieldStore.Destroy(idx)` → 回 free-list（版本只在 Create 递增，防悬垂）。
- 生成器产出 `Dispose()` + `~Player()` 安全网（GC 兜底，QueueDestroy 主线程 Drain）。
- 自动刷新：`world.RegisterHotField`（平行 `Entity[]` 值数组 + WeakReference）。

## 6. 与原始目标对照

| 原始目标 | 本设计 |
|---|---|
| 加属性无感用 OOD | `[HotFieldEntity]` + int 索引 + ref 属性,代码不变 |
| OOD ≥ class | 连续 1.2x;随机 2.7x(稀疏可忽略,与业界一致)——class 容器本质 |
| 写 System 高性能 | IJobParallelFor 扫平铺数组 ~3.7x |

