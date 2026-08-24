# Phase 优先级分析与实施路线（2026-08-24）

> **目的**：将 v3 进化方案的 Phase 计划、设计决策遗留项、AutoSIMD 修复、以及工程改进
> 统一按优先级排序，给出明确的实施顺序建议。
> **前置文档**：`ecs-evolution-plan-v3.md`、`项目现状总览.md`、`20260822-设计决策记录-AI聊天讨论沉淀.md`

---

## 一、推荐实施顺序

```
                        ┌──────────────────────────────────────┐
                        │  当前位置（Phase 1 ✅ 已完成）        │
                        └──────────────┬───────────────────────┘
                                       │
                    ┌──────────────────▼──────────────────┐
            Step 0  │  Chunk struct 化（Phase 1 遗留）     │  ← 3-5 天
                    └──────────────────┬──────────────────┘
                                       │
                    ┌──────────────────▼──────────────────┐
            Step 1  │  Phase 3：Selective Wait + ECB       │  ← 2-3 周
                    └──────────────────┬──────────────────┘
                                       │
                    ┌──────────────────▼──────────────────┐
            Step 2  │  Phase 4：System 框架                │  ← 2-3 周
                    └──────────────────┬──────────────────┘
                                       │
              ┌────────────────────────┼────────────────────────┐
              │                        │                        │
    ┌─────────▼─────────┐  ┌──────────▼──────────┐  ┌─────────▼─────────┐
    │  Phase 2：Edges   │  │  AutoSIMD 修复      │  │  Phase 5：易用性  │
    │  + Shared Comp    │  │  E1/E9/E7/E5        │  │  Events/OneFrame  │
    │  1-2 周           │  │  1-2 周              │  │  2-3 周           │
    └─────────┬─────────┘  └──────────┬──────────┘  └─────────┬─────────┘
              │                        │                        │
              └────────────────────────┼────────────────────────┘
                                       │
                    ┌──────────────────▼──────────────────┐
                    │  Phase 6 → 7 → 8 → 9               │  ← 后续轨道
                    └─────────────────────────────────────┘
```

**核心原则**：Phase 1（基础设施）→ Phase 3（选择性等待）→ Phase 4（System 框架）是性能核心三连，Chunk struct 化是这三者的共同前置优化。

---

## 二、全部待办项（按优先级排序）

### ⭐⭐⭐ 最高优先级（立即可做）

| # | 项 | 所属 Phase | 预估工时 | 依赖 | 说明 |
|---|---|-----------|---------|------|------|
| **S0** | **Chunk struct 化** | Phase 1 遗留 | 3-5 天 | ChunkMemoryPool ✅ | 元数据连续化（`List<Chunk>` → `NativeList<Chunk>`），消除对象头/GC 根/双跳转；300×40B=12KB 进 L1；Phase 3/4 的遍历基础设施 |
| **S1** | **Per-Archetype Job Tracking** | Phase 3 | 2-3 天 | 无 | TrackEntityJob 传入 matchingArchetypes，为 Selective Wait 提供数据基础 |
| **S2** | **Selective Wait** | Phase 3 | 3-5 天 | S1 | CompleteArchetypeJobs(affectedArchetypes)，Set\<A\> 不再等 B |
| **S3** | **DeferredCommandBuffer** | Phase 3 | 5-7 天 | S2 | Job 内结构变更写 staging，帧末 Playback |
| **S4** | **AutoSIMD E1 修复** | AutoSIMD | 3-5 天 | 无 | int/float 混合比较类型推断，高频触发 |
| **S5** | **AutoSIMD E9 修复** | AutoSIMD | 1 周 | 无 | while 循环路径编译期阻塞 |

### ⭐⭐ 高优先级（1-4 周内）

| # | 项 | 所属 Phase | 预估工时 | 依赖 | 说明 |
|---|---|-----------|---------|------|------|
| **S6** | QueryEnumerator 实现 | Phase 4 | 3-5 天 | S3 | `foreach (var (pos, vel) in state.Query<T0,T1>())` |
| **S7** | SystemBase 基类 | Phase 4 | 5-7 天 | S6 | OnUpdate/OnCreate/OnDestroy 生命周期 |
| **S8** | Processor 基类（深度路径） | Phase 4 | 3-5 天 | S7 | ConfigureQueries + IJobChunk 调度 |
| **S9** | SystemGraph + Phase | Phase 4 | 5-7 天 | S7 | 注册、排序、Phase 执行 + sync point |
| **S10** | AutoSIMD E7 修复 | AutoSIMD | 1 周 | 无 | 嵌套循环 return 标号恢复 |
| **S11** | AutoSIMD E5 修复 | AutoSIMD | 3-5 天 | 无 | NaN 载荷分支掩码处理 |
| **S12** | Batch Structural Changes | Phase 3 | 3-5 天 | S3 | World.CreateEntities(count, types) 批量创建 |
| **S13** | Auto-Defer | Phase 3 | 3-5 天 | S3 | 检测 Job 执行上下文自动 defer |
| **S14** | Archetype Edges | Phase 2 | 3-5 天 | 无 | add/remove edge 缓存 |
| **S15** | Lambda 易用路径 | Phase 4 | 3-5 天 | S7 | SourceGenerator 展开为 struct loop |

### ⭐ 中优先级（1-3 月）

| # | 项 | 所属 Phase | 预估工时 | 依赖 | 说明 |
|---|---|-----------|---------|------|------|
| **S16** | Shared Component 语义修正 | Phase 2 | 3-5 天 | S14 | 值不同则拆 Archetype（v3 修正 v2） |
| **S17** | Chunk lazy zero | Phase 2 | 1-2 天 | 无 | 移除构造时整体 InitBlock |
| **S18** | World Events | Phase 5 | 3-5 天 | S7 | typed event channel，struct 避免装箱 |
| **S19** | One-Frame Components | Phase 5 | 3-5 天 | S18 | 帧末批量清理 |
| **S20** | Entity Index / Group | Phase 5 | 5-7 天 | S7 | delta 更新，O(1) 索引查询 |
| **S21** | AutoSIMD P2 循环展开 | AutoSIMD | 1-2 周 | — | 预期 5-10% 性能提升 |
| **S22** | 安全检查宏分层 | Phase 1 遗留 | 2-3 天 | — | ENTJOY_SAFETY 裁剪 Release 开销 |
| **S23** | Relation SoA 编码 | Phase 6 | 1 周 | S7 | 含 target version/epoch 防 ID 回收 |
| **S24** | 级联删除 + target index | Phase 6 | 1 周 | S23 | 索引加速 |
| **S25** | Shared Component 落地 | Phase 7 | 1 周 | S16 | 改变 shared value 执行结构移动 |
| **S26** | Component 存取生成 | Phase 8 | 3-5 天 | S7 | 自动生成 Get/Set 访问器 |
| **S27** | System 注册生成 | Phase 8 | 3-5 天 | S9 | 自动收集 system + 注入 |

### ⭐ 低优先级（3 月+ / 独立轨道）

| # | 项 | 所属 Phase | 预估工时 | 依赖 | 说明 |
|---|---|-----------|---------|------|------|
| **S28** | Context（多 World 隔离） | Phase 5 | 2-3 天 | — | 命名仅用于诊断 |
| **S29** | DI | Phase 5 | 3-5 天 | — | 启动期反射，运行期零反射 |
| **S30** | Prefab / IsA | Phase 6 | 1-2 周 | S24 | 可选能力 |
| **S31** | Subsystem Query | Phase 7 | 3-5 天 | S7 | 系统依赖注入 |
| **S32** | Reactive System 生成 | Phase 8 | 1 周 | S26 | [Reactive(EventType.Added)] |
| **S33** | Chunk 合并/碎片整理 | Phase 1 遗留 | 1 周 | S0 | 瘦 Chunk 合并（利用率 <30%） |
| **S34** | AutoSIMD E2/E3/E4/E6/E8 | AutoSIMD | 1-2 周 | — | 其余 edge case |
| **S35** | 组件 copy/move/destroy hooks | Phase 9 | 2-3 周 | — | **Phase 9 前置条件** |
| **S36** | ManagedComponentStore | Phase 9 | 1-2 周 | S35 | 字典存储 managed 字段 |
| **S37** | NativeProjection | Phase 9 | 2-3 周 | S35 | NativeString/NativeDictionary |
| **S38** | AutoSIMD SIMD 宽度多版本 | AutoSIMD | 3-4 周 | — | AVX-512 支持（99% 用户 AVX2） |

---

## 三、Chunk struct 化详细分析

### 3.1 为什么排在 Phase 3 之前

| 理由 | 说明 |
|------|------|
| **Phase 1 遗留项** | 设计决策记录 §5.1 明确标注"暂缓 ⏸"，当时优先级低于 TempAllocator/AddEntity，现在这两个已完成 |
| **Phase 3 的基础设施** | ECB staging 区、Per-Archetype Tracking 都需要高效遍历 Chunk 元数据；`NativeList<Chunk>` 连续遍历比 `List<Chunk>`（每元素一次指针跳转）快得多 |
| **收益明确无风险** | 300×40B=12KB 进 L1；消除对象头/GC 根扫描/双跳转；公共 API 暴露的是 `ArchetypeChunk`（已经是 struct），用户代码不受影响 |
| **不涉及数据块合并** | 64KB 独立 slab 保持不变，只改元数据句柄的存储方式 |
| **与 ChunkMemoryPool 联动** | Phase 1 已完成 ChunkMemoryPool，struct 化后 Chunk 持有 `IntPtr`，职责清晰 |

### 3.2 变更范围

```
变更前：
  Chunk : sealed unsafe class : IDisposable     ← 托管 class（GC 扫描）
  Archetype._chunkList : List<Chunk>             ← 每元素一次指针跳转
  Archetype._chunkList[i]                        ← 间接寻址

变更后：
  Chunk : struct                                 ← 值类型（GC 不扫描）
  Archetype._chunkList : NativeList<Chunk>       ← 连续内存遍历
  ref var chunk = ref _chunkList.ElementAt(i)    ← 直接索引
```

### 3.3 关键约束

| 约束 | 说明 |
|------|------|
| Chunk 不能包含托管引用 | 必须 blittable（`IntPtr` / `int` / `fixed` 数组） |
| 修改字段注意值拷贝 | 通过索引 + `ref` 访问，不能直接 `_chunkList[i].EntityCount++` |
| `ArchetypeChunk` 不变 | 对外句柄已经是 struct，用户 API 零影响 |
| Dispose 语义变更 | struct 无 Dispose，`ChunkMemoryPool` 负责 64KB 块回收 |

### 3.4 预估收益

| 场景 | 收益来源 | 预估提升 |
|------|----------|----------|
| Chunk 遍历（查询/调度） | 连续内存，无指针跳转 | 10-20% |
| GC 压力 | 消除 300 个 class 对象的根扫描 | 帧末 GC 暂停减少 |
| 内存开销 | 消除对象头（~16B/对象 × 300 = 4.8KB） | 微量 |
| C++ 互操作 | 连续数组可直接传指针 | 后续 Phase 9 受益 |

### 3.5 风险

| 风险 | 概率 | 缓解 |
|------|------|------|
| 内部代码修改 EntityCount 等字段时值拷贝 bug | 中 | 使用 `ref` 访问 + 单测覆盖 |
| `NativeList<Chunk>` 的 Capacity 扩容时指针失效 | 低 | 调度期间不扩容（快照语义） |
| `Chunk.Dispose` 语义丢失 | 低 | 改由 `ChunkMemoryPool.Return` 管理 |

---

## 四、Phase 依赖关系图

```
Phase 1 (基础设施) ✅
  ├─ TempAllocator ✅
  ├─ ChunkMemoryPool ✅
  ├─ AddEntity O(1) ✅
  ├─ GetChunks 零分配 ✅
  ├─ QueryBuilder 零分配 ✅
  └─ Chunk struct 化 ◄── S0（当前推荐下一步）
       │
Phase 3 (选择性等待) ◄── S1→S2→S3
  ├─ Per-Archetype Job Tracking (S1)
  ├─ Selective Wait (S2)
  ├─ DeferredCommandBuffer (S3)
  ├─ Auto-Defer (S13)
  └─ Batch Structural Changes (S12)
       │
Phase 4 (System 框架) ◄── S6→S7→S8→S9
  ├─ QueryEnumerator (S6)
  ├─ SystemBase (S7)
  ├─ Processor (S8)
  ├─ SystemGraph + Phase (S9)
  └─ Lambda 易用路径 (S15)
       │
       ├──────────────────┬──────────────────┐
       │                  │                  │
Phase 2 (存储增强)    AutoSIMD 修复      Phase 5 (易用性)
  ├─ Archetype Edges     ├─ E1 (S4)        ├─ Events (S18)
  ├─ Shared Comp (S16)   ├─ E9 (S5)        ├─ OneFrame (S19)
  └─ Chunk lazy zero     ├─ E7 (S10)       ├─ Group (S20)
                         └─ E5 (S11)       ├─ Context (S28)
                                           └─ DI (S29)
       │
Phase 6 (关系) → Phase 7 (共享组件) → Phase 8 (生成器)
       │
Phase 9 (Managed 类型，独立轨道)
  ├─ copy/move/destroy hooks (S35) ← 前置
  ├─ ManagedComponentStore (S36)
  └─ NativeProjection (S37)
```

---

## 五、里程碑对照

| 里程碑 | 包含 Phase | 状态 | 预估总工时 |
|--------|-----------|------|-----------|
| **里程碑 A：高性能核心** | Phase 1 ✅ → Chunk struct 化 → Phase 3 → Phase 4 | 进行中 | ~6-10 周 |
| **里程碑 B：存储与易用性** | Phase 2 + Phase 5 + Phase 6 + Phase 7 | 未开始 | ~8-12 周 |
| **里程碑 C：生成器扩展** | Phase 8 | 未开始 | ~2-3 周 |
| **里程碑 D：Managed 类型** | Phase 9（独立轨道） | 未开始 | ~5-8 周 |
| **AutoSIMD 修复** | 与里程碑并行 | 进行中 | ~2-3 周 |

---

## 六、决策记录

### 6.1 为什么 Chunk struct 化排在 Phase 3 之前而非 Phase 2 之后

原 v3 计划顺序是 `Phase 1→3→4→2→5→6→7→8→9`，Phase 2（Archetype Edges）排在 Phase 4 之后。

但 Chunk struct 化是 Phase 1 的遗留项（非 Phase 2 的 Archetype Edges），且它是 Phase 3/4 遍历 Chunk 的**共同前置优化**：
- Phase 3 的 ECB staging 区需要遍历受影响 Archetype 的 Chunk 列表
- Phase 4 的 QueryEnumerator 需要遍历所有匹配 Chunk
- 这些遍历在 `List<Chunk>` 下是 O(n) 指针跳转，在 `NativeList<Chunk>` 下是连续内存

因此 Chunk struct 化应该在 Phase 3 之前完成，作为 Phase 1 的真正收尾。

### 6.2 为什么 AutoSIMD 修复与 ECS Phase 并行

AutoSIMD 是**深度路径的执行引擎**，与 ECS Phase 无直接依赖：
- E1（int/float 混合比较）影响所有 AutoSIMD 用户，独立于 ECS 进度
- E9（while 循环）编译期阻塞，修复后可扩展 AutoSIMD 使用范围
- 两者可在 Phase 3 实施期间并行修复（不同模块、不同人/时间）

### 6.3 Phase 9 为什么是独立轨道

Phase 9（Managed 类型）的前置条件是组件 copy/move/destroy hooks，这是一个独立的协议设计：
- 不依赖 Phase 1-8 的任何完成状态
- 但 Phase 1-8 的完成会影响 Phase 9 的 API 设计（如 System 框架决定组件生命周期边界）
- 因此排在最后，先完成 hooks 协议，再引入非 blittable 组件

---

## 七、工时估算汇总

| Step | 内容 | 工时 | 累计 |
|------|------|------|------|
| S0 | Chunk struct 化 | 3-5 天 | 3-5 天 |
| S1-S3 | Phase 3 全部 | 2-3 周 | ~3-4 周 |
| S6-S9, S15 | Phase 4 全部 | 2-3 周 | ~5-7 周 |
| S4-S5, S10-S11 | AutoSIMD 关键修复 | 2-3 周 | 并行 |
| S14-S17 | Phase 2 全部 | 1-2 周 | ~7-9 周 |
| S18-S20 | Phase 5 核心 | 2-3 周 | ~9-12 周 |
| S23-S24 | Phase 6 核心 | 2 周 | ~11-14 周 |
| S25 | Phase 7 | 1 周 | ~12-15 周 |
| S26-S27 | Phase 8 核心 | 1-2 周 | ~13-17 周 |
| S35-S37 | Phase 9 | 5-8 周 | 独立轨道 |

**里程碑 A（高性能核心）预估**：6-10 周
**里程碑 B（存储与易用性）预估**：8-12 周
**全部完成预估**：15-20 周（不含 Phase 9 独立轨道）
