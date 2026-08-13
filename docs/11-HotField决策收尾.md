# 11 HotField 决策收尾（结论：不再实现 class 门面 + SoA 路线）

> 状态：**决策定稿（放弃）**。本文档记录 HotField 方案的去留决定、依据（`E:\Code\HotField` 2026-08-13 基准）与最终落点，供后续不再重新踩坑。
> 前置：06（句柄层）、07（随机访问）、08（生成器）、09（重新设计）——本系列设计均已**定稿但不再进入自动生成器实现阶段**。

---

## 1. 决策

**放弃** HotField 的「普通 class 门面 + `[HotFieldEntity]` 属性 + 外部 SoA/平铺存储」这条路线。即：**不实现** 08 的 source generator（自动组件生成、`ref` 属性改写、`Bind`/`Dispose`/finalizer）与 09 的 `HotFieldStore` 实例版 + `world.RegisterHotField` 自动刷新。

**决策依据（用户）**：实际用法以**稀疏随机逐实体**为主（每帧几千~几万实体的逐实体逻辑），而 class 门面 + SoA 存储的随机访问恰是物理短板（2.5~3.5x）；SoA 的批量 System 优势在此用法下用不上，为它付出 generator + store + 生命周期管理的复杂度不值得。

---

## 2. 数据依据（`20260813.22.17.13.txt`，AMD 8845H，.NET 8.0.19）

三套基准：2‑Vector2（连/随）、50‑floats、200‑floats（全/子集）。基线=对应 `Class`（数据在对象），Ratio 越小越快。

### 2.1 连续遍历（UpdateAll）
| 形态 | 2 字段 | 50 全 | 200 全 | 200 Top10 |
|---|---|---|---|---|
| Class（基线） | 1.00 | 1.00 | 1.00 | 0.09 |
| Struct（AoS 值数组） | 0.86 | 0.48 | 0.57 | 0.04 |
| HotEntity_SoA_Ptr | **1.01** | 0.69 | — | — |
| HotEntity_BasePtr | — | 0.55 | 0.67 | 0.05 |
| **System_Unsafe（并行裸指针）** | **0.57** | **0.48** | **0.57** | **0.04** |

- **字段越多 SoA 优势越大；子集访问（Top10）优势最大**（快的部分更快，且只需读少数字段）。
- System 并行始终最快（0.04~0.57x）。

### 2.2 随机遍历（稀疏）
| 形态 | 2 字段 |
|---|---|
| Class（数据在对象） | 1.00 |
| Struct | 0.55 |
| HotEntity_Index | **3.50** |
| HotEntity_PtrMethod | 3.07 |
| HotEntity_ChunkPtrMethod | 2.81 |
| HotEntitySoA_Ptr | 2.50 |
| HotEntity_RefMethod | 2.38 |
| HotEntityStruct_Index | 2.31 |

- **class 门面 + 随机 ≈ 2.3~3.5x 负优化**。根源：读散落对象拿句柄 + 二次 deref SoA 数组 = 2 次随机读；这是「class 容器 + 数据外置」的物理地板，非实现缺陷（与 EnTT/Unity/Flecs 一致）。

---

## 3. 为什么是「物理短板」，不是工程问题

- **连续**：SoA 预取充分，class 门面可追平（SoA_Ptr 1.01x）。
- **随机**：数据在对象 = 1 次对象读（≈class）；数据在 SoA = 对象读 + 数组读（2 次随机读）。**随机 ≥ class 的唯一办法 = 数据在对象**（AoS class 对象），但那会让 System 丢失 SoA 批量优势。
- 这是**不可兼得的单一取舍轴**：稀疏随机 ↔ 数据在对象（AoS）；密集批量 ↔ 数据在 SoA。不存在同时两全。

---

## 4. 最终落点（替代方案，按用法选）

HotField 系列**不再作为产品特性实现**。稀疏随机场景的正式替代：

| 用法 | 落点 | 随机 | System |
|---|---|---|---|
| **稀疏随机逐实体（本决策场景）** | **普通 class，数据在对象**（无需 ECS 管 HotField 生命周期）；若确需 ECS 生命周期管理 → `class 数据在对象 + ECS free-list 稳定索引 + 版本号`（docs/06 §现场 `HotFieldClassStore`：连续 1.24x / **随机 1.21x** ≈class） | **≈1.2x** ✅ | ~1.2x（弱） |
| 密集批量 | 直接写 **ECS IJobChunk/System** 走 chunk SoA（0.5~0.6x 串行，并行 0.04~0.57x） | — | 最强 ✅ |

- **不要用** `class + int 索引 + 平铺 SoA` 做稀疏随机（本决策已否决）。
- EntJoy 既有 `IHotFieldEntity` 接口与 `06_HotFieldHandle/` 样例保留为**可行性原型/测量记录**，不推进到生产级 generator。

---

## 5. 相关代码与后续

- **保留**：`src/EntJoy/Entity/IHotFieldEntity.cs`、`src/EntJoySample/06_HotFieldHandle/*`（原型与基准，标注为"已定稿并冻结，不进生产"）。
- **不再实现**：08 的 source generator、09 的 `HotFieldStore` 实例 + `RegisterHotField` 自动刷新。
- **后续路径**：稀疏随机 → 普通 class（如需 ECS 生命周期再上 class-in-object + free-list）；密集批量 → ECS System。两者无需 HotField 门面。
