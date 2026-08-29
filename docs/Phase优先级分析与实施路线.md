# Phase 优先级分析与实施路线（2026-08 更新）

> 2026-08-29 增量 7：**S22 安全检查宏分层完成** —— `ENTJOY_SAFETY`（Debug 默认：句柄+边界）与
> `ENTJOY_SAFETY_BOUNDS`（Release 默认：仅边界，裁剪句柄原子读 ~1-2ns/次）双档；
> 句柄检查的 `#if` 下沉到 `SafetyHandleManager.Check*` 方法体内（一处裁剪，NativeArray/NativeList
> ~35 处调用点自动生效）；边界检查在 3 处索引器分层；全关 `-p:DefineConstants=` 覆盖。
> Debug/Release/全关三档编译通过，Debug + Release 全量测试 116/116。
>
> 2026-08-29 增量 6：**Phase 8 完成，里程碑 C 达成** —— `[ECSComponent]` 组件生成（partial struct
> 免写 IComponentData，生成器自动补接口 + EJ2001/2002/2003 诊断：非 partial/非 blittable/泛型）、
> System 自动收集（`SystemRegistry.RegisterAll` 一行注册同程序集所有 ISystem + `[DisableAutoCreation]`
> opt-out 对齐 Unity DOTS）、Reactive 处理器（`[Reactive(ObserverEvents)]` + 静态
> `Execute(in ReadOnlySpan&lt;Entity&gt;, in ReadOnlySpan&lt;T&gt;)` 签名推导组件类型 →
> `ReactiveSystemRegistry.RegisterAll` 自动注册 Observer，EJ2011/2012 诊断）。
> 详见 `docs/20260829-Phase8-SourceGenerator设计.md`。
>
> 2026-08-29 增量 5：**N 元组补齐 + 警告清理** —— `QueryBuilder.WithAll<T0..Tn>()`（新生成器，
> 替代 3+ 组件链式调用）、`World.QueryChunks<T0..Tn>()`（chunk 级 N 元组遍历，chunk 三件套 +
> 扩展方法）、QueryTuple 生成器重构为按 arity 泛型模板（修复固定类名 CS0101 冲突）、
> 构建警告清理（NoWarn 8500/8632/8981/CA2014/CA2255、删不可达代码、字段可空化）。
> 详见 `docs/20260828-查询缓存与N元组生成.md` §八。
>
> 2026-08-28 增量 4：**查询缓存共享 + N 元组查询生成器** —— EntityQuery 共享注册表（QueryKey 指纹 + 排序归一 + 增量刷新）、
> Entity Group 反向索引（Entity→匹配查询集合，惰性构建）、`world.Query<T0..Tn>()` N≥3 强类型枚举器按需生成（SourceGenerator），
> **SystemAPI 移除**（Query/QueryChunks 并入 World）。S20（Entity Index/Group）查询缓存部分完成。
> 详见 `docs/20260828-查询缓存与N元组生成.md`。
>
> 2026-08-28 增量：**Observer（S8-new）完成**——组件生命周期事件 push 回调（Added/Removed/Set/Destroyed），
> 主线程立即 + ECB Playback 自然触发 + 批量 span 合并（CreateEntities 10000→1 回调）。**Phase 4 全部完成，里程碑 A（高性能核心）达成**。
> 详见 `docs/20260828-Observer设计.md`。Job 内 Set 事件（per-comp 位图 + adapter 置位）延后，记录于设计文档 §11。
>
> 2026-08-27 增量：Event Channel 完整实现（双缓冲 EventStream + Managed/NativeTranspile SendEvent +
> C++ EventBuffer 自动翻译 + 多 World + 异步自动 drain）——详见 `docs/20260827-EventChannel实现记录.md`。
> 事件总线（S18）完成并移入 Phase 4；One-Frame Component（S19）废弃，由 Event Channel 替代。
>
> 2026-08-27 增量 2：**ISPC 后端 SendEvent 支持** —— NativeTranspile ISPC Job 内 SendEvent
> （SIMD foreach_tiled + fetch-add 原子槽分配 + AoS 布局写）+ **AutoSIMD=Enabled fallback 支持 SendEvent** +
> **C#→ISPC 原子返回值语义修复**（fetch-add 补回增量 = C# add-fetch 新值）——详见 `docs/20260827-ISpcEvent-实现记录.md`。
>
> 2026-08-27 增量 3：**Lambda 易用路径已移除**（用户明确不需要），AutoSIMD P2 循环展开已跳过（Clang/MSVC `-funroll-loops` 已自动处理）。

> 2026-08-25 增量：Change Tracking 核心实现、EnabledComponent 过滤优化、Run 直执/ImmediateNative、
> SourceGenerator 统一（IJobEntity/IJobChunk）——详见 `docs/20260825-ChangeTracking与Enabled优化记录.md`。

> **目的**：将 v3 进化方案的 Phase 计划、设计决策遗留项、AutoSIMD 修复、以及工程改进
> 统一按优先级排序，给出明确的实施顺序建议。
> **前置文档**：`ecs-evolution-plan-v3.md`、`项目现状总览.md`、`20260822-设计决策记录-AI聊天讨论沉淀.md`

---

## 一、当前进度（2026-08 最新）

```
Phase 1 (基础设施优化)     ✅ 已完成
  └─ Chunk struct 化       ✅ — C# IJobChunk -38%，Sleep 路径 -9%
  └─ ENTJOY_SAFETY 开关    ✅ — 宏分层（S22）：Debug 完整检查 / Release 仅边界 / 全关可覆盖
  └─ 空 chunk 延迟移除      ✅ — 避免边界抖动

Phase 2 (Archetype Edges)  ✅ 已完成
  └─ 2.1 Archetype Edges   ✅ — Add/Remove 走 edge 快路径
  └─ 2.3 Chunk lazy zero   ✅ — 批量创建加速 1.79x

Phase 3 (Selective Wait)   ✅ 基础完成
  └─ Per-Archetype Tracking ✅
  └─ Selective Wait         ✅ — 只等相关 Archetype 的 Job
  └─ Batch CreateEntities   ✅ — 100 万实体 = 69.5 ms
  └─ DeferredCommandBuffer  ✅ — 基础框架

Phase 4 (System 调度与开发体验增强) ✅ 已完成（2026-08-28，Observer 收尾）
  └─ QueryEnumerator       ✅ — foreach 遍历已实现
  └─ Schedule Graph        ✅ — DAG 拓扑排序 + PrintSchedule 输出
  └─ OrderBefore/OrderAfter ✅ — 手动指定 System 执行顺序
  └─ Entity Builder        ✅ — 实体构造器，使用 SetRaw 无反射
  └─ Change Tracking       ✅ — WithChanged<T> 查询过滤 + Set 自动标记 + ClearAllChangedBitMasks + MatchChangedFilter 接入 ChunkJobCollector
  └─ RunWhen               ✅ — 条件执行，空闲跳过
  └─ 非泛型方法             ✅ — SetRaw/AddComponentRaw/RemoveComponentRaw 无反射
  └─ 命名空间重构           ✅ — EntJoy → EntJoy.ECS
  └─ EnabledComponent 过滤   ✅ — 链式 `Query<T0>().WithEnabled<T1>()`，SIMD 位图 + enableVersion 缓存
  └─ Run 直执 / ImmediateNative ✅ — ECS 侧直接执行；Native 版 Run 零 worker 唤醒（2026-08-25）
  └─ Event Channel         ✅ — 双缓冲事件流 + Managed/Native SendEvent（2026-08-27，详见 docs/20260827-EventChannel实现记录.md）
  └─ ISPC SendEvent        ✅ — ISPC 后端 SendEvent + AutoSIMD fallback 支持 + C#→ISPC 原子语义修复（2026-08-27，详见 docs/20260827-ISpcEvent-实现记录.md）
  └─ Observer (S8-new)     ✅ — 组件生命周期事件 push 回调：主线程立即 + ECB Playback 自然触发 + 批量 span 合并（2026-08-28，详见 docs/20260828-Observer设计.md）

Phase 5 (易用性)            🔲 仅剩低优先级项（S28 Context / S29 DI）
  └─ 关系型状态机            ❌ — 已决策不做（2026-08-29 后复核：业务逻辑由用户在引擎层实现，符合 §22.6 核心原则）
  └─ ~~事件总线~~             ✅ — Event Channel 已实现（S18 World Events 完成，移到 Phase 4）
  └─ ~~One-Frame Component~~ ❌ — 已废弃：Event Channel 替代（零结构变更）

Phase 6 (实体关系)          ✅ 全部完成（2026-08-29）
  └─ Non-Fragmenting 关系   ✅ — S23 RelationSlot 8B 列，不拆 Archetype
  └─ 级联删除              ✅ — S24 RelationIndex + DestroyEntityCascade 递归防环
  └─ 关系查询              ✅ — WithRelationship 过滤 + GetRelationsOf 反向查询（O(1)）
  └─ Job/原生访问          ✅ — IJobChunk/IJobEntity/NativeTranspiler(C++/ISPC) 六路径验证
  └─ 关系遍历 API          ✅ — S23c GetAncestors/GetDescendants/GetSiblings（借鉴 Bevy/Flecs）

Phase 7 (共享组件)          ✅ S25 收口（b/c ✅ 2026-08-29；a ⏸ 已评估暂不实现）
  └─ Shared Component 落地  ✅ — S25：WithShared 查询 ✅ + Change Tracking ✅ + 排序分组 ⏸ 已评估暂不实现（决策见 SharedComponent 设计 §5.1）

Phase 8 (Source Generator)  ✅ 已完成（2026-08-29，里程碑 C 达成）
  └─ Component 存取生成    ✅ — [ECSComponent] partial struct 免写 IComponentData（生成器补接口 + EJ 诊断）
  └─ System 注册生成       ✅ — SystemRegistry.RegisterAll 自动收集 + [DisableAutoCreation] opt-out
  └─ Reactive System 生成  ✅ — [Reactive] 声明式 Observer 订阅（Execute 签名推导组件类型）

Phase 9 (托管类型)          🔲 未开始
  └─ GCHandle + 指针数组
  └─ 字符串驻留
  └─ NativeTranspiler 突破
  └─ AOT 兼容修复          ✅ 已完成（2026-08-26，详见 §十）

工具链                       🔲 未开始
  └─ Godot 场景桥接
  └─ 热重载支持（Native System）
  └─ 内存分析器
  └─ 性能分析器

AutoSIMD 修复                ✅ E1-E11 全部修复 + EdgeCase 44/50 → 最终 48/50+
```
    │  Phase 2：遗留     │  │  AutoSIMD 修复 ✅    │  │  Phase 5：易用性  │
    │  + Shared Comp    │  │  E1-E11 已完成      │  │  Events/Group    │
    │  1-2 周           │  │  EdgeCase 基本完成   │  │  2-3 周           │
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
| **S0** | **Chunk struct 化** | Phase 1 遗留 | ✅ 已完成 | ChunkMemoryPool ✅ | 元数据连续化（`List<Chunk>` → `NativeList<Chunk>`），消除对象头/GC 根/双跳转 |
| **S1** | **Per-Archetype Job Tracking** | Phase 3 | ✅ 已完成 | — | TrackEntityJob 传入 matchingArchetypes |
| **S2** | **Selective Wait** | Phase 3 | ✅ 已完成 | S1 | CompleteArchetypeJobs(affectedArchetypes) |
| **S3** | **DeferredCommandBuffer** | Phase 3 | ✅ 已完成 | S2 | Job 内结构变更写 staging，帧末 Playback |
| **S4** | **AutoSIMD E1 修复** | AutoSIMD | 3-5 天 | 无 | ✅ 已完成（SemanticModel 类型回退 + float store cast） |
| **S5** | **AutoSIMD E9 修复** | AutoSIMD | 1 周 | 无 | ✅ 已用 for 覆盖 |

### ⭐⭐ 高优先级（1-4 周内）

| # | 项 | 所属 Phase | 预估工时 | 依赖 | 说明 |
|---|---|-----------|---------|------|------|
| **S6** | QueryEnumerator 实现 | Phase 4 | 3-5 天 | S3 | ✅ 已完成 |
| **S7-new** | **Schedule Graph** — System 自动并行 | Phase 4 | 1-2 周 | 无 | ✅ 已完成 — DAG 拓扑排序 + `[Read]`/`[Write]`/`[Order]` 属性 + `PrintSchedule` |
| **S7b** | **ISPC SendEvent** | Phase 4 | ✅ 已完成（2026-08-27） | — | ISPC 后端 SIMD + fetch-add 原子事件写入 + AutoSIMD fallback + C#→ISPC 原子语义修复（详见 `docs/20260827-ISpcEvent-实现记录.md`） |
| **S8-new** | **Observer** — 组件变化触发 | Phase 4 | ✅ 已完成（2026-08-28） | 无 | **Push-based**回调（区别于 Change Tracking 的 pull-based）。组件增/删/改自动触发注册回调。主线程立即 + ECB Playback 自然触发 + 批量 span 合并（详见 `docs/20260828-Observer设计.md`） |
| **S9-new** | **One-Frame Component** — 帧事件 | Phase 4 | 3-5 天 | 无 | ❌ 已废弃：由 Event Channel（S18）替代 |
| **S10** | AutoSIMD E7 修复 | AutoSIMD | 1 周 | 无 | ✅ 已完成（returnedMask + post_mask + int→float cast） |
| **S11** | AutoSIMD E5 修复 | AutoSIMD | 3-5 天 | 无 | ✅ 已完成（NativeTranspiled 移除 /fp:fast） |
| **S12** | Batch Structural Changes | Phase 3 | 3-5 天 | S3 | ✅ 已完成 |
| **S13** | Auto-Defer | Phase 3 | ✅ 已完成 | S3 | 检测 Job 执行上下文自动 defer |
| **S14** | Archetype Edges | Phase 2 | 3-5 天 | 无 | ✅ 已完成 |

~~S7 (SystemBase)~~、~~S8 (Processor)~~、~~S9 (SystemGraph)~~ — 已砍掉，与 IJobEntity 重复，合并到 S7-new (Schedule Graph)。

### ⭐ 中优先级（1-3 月）

| # | 项 | 所属 Phase | 预估工时 | 依赖 | 说明 |
|---|---|-----------|---------|------|------|
| **S16** | Shared Component 存储（per-chunk） | Phase 2 | ✅ 已完成（2026-08-27） | S14 | per-chunk 双类型：blittable 内联 chunk 内存块 + managed 扁平数组索引 + 43/43 测试 + 6/6 Demo 通过 |
| **S16b** | NativeTranspiler 支持 blittable SharedComponent | Phase 2 | ✅ 已完成（2026-08-27） | S16 | IJobChunk `GetSharedComponent<T>` → C++ 指针解引用 + ABI 扩展 + NT014 拦截 managed + EntityBatchAdapter 跳过 |
| **S17** | Chunk lazy zero | Phase 2 | 0-0.5 天 | 无 | ✅ 已验证完成：chunk 构造无整体 InitBlock，AddEntity 逐 slot 清零 |
| **S18** | World Events | Phase 4 | ✅ 已完成（2026-08-27） | — | Event Channel：双缓冲 EventStream + Managed/Native SendEvent，详见 20260827-EventChannel实现记录.md |
| ~~S19~~ | ~~One-Frame Components~~ | ~~Phase 5~~ | ❌ 废弃 | — | 由 Event Channel 替代（零结构变更，非每帧 add/remove 组件） |
| **S20** | Entity Index / Group | Phase 5 | ✅ 已完成（2026-08-28） | S7 | 查询缓存共享注册表（QueryKey 指纹 + 排序归一 + 增量刷新）+ Entity Group 反向索引（Entity→匹配查询集合，惰性构建）。基准：共享 4x+ 提速，GetGroupsOf 0.08us。详见 20260828-查询缓存与N元组生成.md |
| **S21** | ~~AutoSIMD P2 循环展开~~ | ~~AutoSIMD~~ | 已跳过 | — | 🔲 已跳过：常量界小循环（≤64次）已由 SimdLoopGenerator 全展开；非常量界中等循环 Clang/MSVC `-funroll-loops` 已自动处理，手动额外半展开收益不确定且增加生成代码体积 |
| **S22** | 安全检查宏分层 | Phase 1 遗留 | ✅ 已完成（2026-08-29） | — | ENTJOY_SAFETY（Debug：句柄+边界）/ ENTJOY_SAFETY_BOUNDS（Release：仅边界）双档 + 句柄检查 #if 下沉 Check* 方法体（~35 调用点一处裁剪）+ 索引器边界分层。全关 -p:DefineConstants= 覆盖。Debug/Release/全关三档编译通过 + 测试 116/116 |
| **S23** | Relation SoA 编码 | Phase 6 | ✅ 已完成（2026-08-29） | S7 | RelationSlot 8B 列（target+version 防 ID 回收）+ IRelationComponent + Add/Remove/Get/HasRelationship + WithRelationship 查询过滤（QueryKey 指纹化）。11/11 测试 + 全量 77/77。详见 docs/20260829-RelationSoA设计.md |
| **S24** | 级联删除 + target index | Phase 6 | ✅ 已完成（2026-08-29） | S23 | RelationIndex 反向索引（target→HashSet，O(1)）+ DestroyEntityCascade 递归防环 + 索引一致性（Add/Remove/标准 Destroy 同步）。7/7 级联测试 + 全量 84/84 |
| **S23b** | 关系查询进阶 + Job 访问 | Phase 6 | ✅ 已完成（2026-08-29） | S23 | GetRelationsOf<TRel>/GetRelationsOfAll（O(1) 反向查询）+ QuerySelection<T0,T1> 链式 WithRelationship + IJobChunk/IJobEntity/NativeTranspiler(C++/ISPC) 六路径访问验证。修复 3 个 NativeTranspiler 通用 bug（嵌套 include + ISPC 类型映射） |
| **S23c** | 关系遍历 API | Phase 6 | ✅ 已完成（2026-08-29） | S23/S24 | GetAncestors/GetDescendants(BFS)/GetSiblings（借鉴 Bevy iter_*，利用 RelationIndex O(1)，visited 防环含起始实体）。12/12 测试 + 全量 96/97（唯一 FAIL = S25 缺口判据）。基准：深链 10000 祖先 4.46ms、宽树 10000 后代 2.21ms。修复 default(Entity) Id=0 陷阱（新增 TryGetRelationshipTarget） |
| **S25** | Shared Component 落地扩展 | Phase 7 | ✅ 收口（2026-08-29：b/c ✅，a 已评估暂不实现；2026-08-29 后落地 per-value 最近使用缓存） | S16 | 三项：a) 按共享值排序/分组 chunk ⏸ 已评估暂不实现——现状已保证同值实体入同值 chunk（例外：SetSharedComponent 单实体就地改值不合并，同值可多 chunk，详见 SharedComponent 设计 §5.2）+ 空 chunk 回收，缺口仅为物理相邻；跨值排序不可行（boxed 无全序）、换位需同步 EntityInfo.ChunkIndex、Remove swap-pop 打乱分组、cap 768 下收益存疑（决策详见 SharedComponent 设计 §5.1/5.2）；2026-08-29 后：`FindChunkWith*` 全量扫描改 per-value 最近使用缓存（方案 B，O(1) 期望 + lazy 验证，4 测试，全量 108/108，详见 §5.2）；b) `WithShared` 查询修复 ✅——修复链见 SharedComponent 设计 §5.1（MatchesSharedFilter 统一三路径 + QueryKey 指纹补 value + IsMatch 共享列校验 + **chunk stride 布局 bug**：stride 漏算位掩码/共享值区致下一 chunk Entity 数组压在本 chunk 位掩码上 + 新 chunk 位掩码清零 + WithChanged 查询每次访问重评；SharedQueryTests 8/8）；c) Change Tracking 联动 ✅ 验证为既有能力（SetSharedComponent 就地/移动路径均已有 MarkEntityChanged），Job 安全由 CompleteActiveJobs 保证 |
| **S26** | Component 存取生成 | Phase 8 | ✅ 已完成（2026-08-29） | S7 ✅ | [ECSComponent] partial struct → 生成器自动补齐 IComponentData（免写接口标记）+ EJ2001/2002/2003 诊断（非 partial/非 blittable/泛型）。详见 docs/20260829-Phase8-SourceGenerator设计.md |
| **S27** | System 注册生成 | Phase 8 | ✅ 已完成（2026-08-29） | 标注 S9 已砍，实为 S7-new ✅ | SystemRegistry.RegisterAll 自动收集同程序集所有 struct : ISystem 一行注册（按全名排序）+ [DisableAutoCreation] opt-out（对齐 Unity DOTS）。详见 docs/20260829-Phase8-SourceGenerator设计.md |

### ⭐ 低优先级（3 月+ / 独立轨道）

| # | 项 | 所属 Phase | 预估工时 | 依赖 | 说明 |
|---|---|-----------|---------|------|------|
| **S28** | Context（多 World 隔离） | Phase 5 | 2-3 天 | — | 命名仅用于诊断 |
| **S29** | DI | Phase 5 | 3-5 天 | — | 启动期反射，运行期零反射 |
| **S30** | Prefab / IsA | Phase 6 | 1-2 周 | S24 | 可选能力 |
| **S31** | Subsystem Query | Phase 7 | 3-5 天 | S7 | 系统依赖注入 |
| **S32** | Reactive System 生成 | Phase 8 | ✅ 已完成（2026-08-29） | S26 | [Reactive(ObserverEvents)] 声明式 Observer 订阅：静态 Execute(in ReadOnlySpan&lt;Entity&gt;, in ReadOnlySpan&lt;T&gt;) 签名推导组件类型 → ReactiveSystemRegistry.RegisterAll 自动注册（事件位组合支持）+ EJ2011/2012 诊断。详见 docs/20260829-Phase8-SourceGenerator设计.md |
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
  └─ Chunk struct 化 ✅
       │
Phase 3 (选择性等待) ✅
  ├─ Per-Archetype Job Tracking ✅
  ├─ Selective Wait ✅
  ├─ DeferredCommandBuffer ✅
  ├─ Batch Structural Changes ✅
  └─ Auto-Defer (S13) ◄── 未完成
       │
Phase 4 (System 调度与能力增强) ✅ 已完成（2026-08-28）
  ├─ QueryEnumerator (S6) ✅
  ├─ Schedule Graph (S7-new) ✅ — 自动并行，参考 Bevy Schedule
   ├─ Observer (S8-new) ✅ — 组件变化触发，参考 Flecs Observer（2026-08-28，批量 span 合并）
   ├─ Event Channel (S18) ✅ — 双缓冲事件流 + Managed/Native SendEvent
   ├─ ISPC SendEvent (S7b) ✅ — ISPC 后端 + AutoSIMD fallback
   └─ One-Frame Component (S9-new) ❌ — 已废弃：由 Event Channel（S18）替代

       │
       ├──────────────────┬──────────────────┐
       │                  │                  │
Phase 2 (存储增强)    AutoSIMD 修复      Phase 5 (易用性)
   ├─ Archetype Edges ✅     ├─ E1 (S4)             ├─ Events (S18)
   ├─ Shared Comp (S16)      ├─ E9 (S5)             └─ Group (S20)
   └─ Chunk lazy zero        ├─ E7 (S10)          ├─ Context (S28)
                            └─ E5 (S11)          └─ DI (S29)
       │                                          └─ ~~关系型状态机~~ ❌ 已决策不做
       │
Phase 6 (关系) ✅ 主体完成 → Phase 7 (共享组件) → Phase 8 (生成器)
   ├─ S23 RelationSlot 列 ✅
   ├─ S24 级联删除 ✅
   ├─ S23b 查询进阶 + Job 访问 ✅
   └─ S23c 关系遍历 API 🔲 ← 下一步
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
| **里程碑 A：高性能核心** | Phase 1 ✅ → Phase 2 ✅ → Phase 3 ✅ → Phase 4 ✅（含 Observer 收尾 2026-08-28） | ✅ **已完成**（2026-08-28） | ~4-6 周 |
| **里程碑 B：存储与易用性** | Phase 7（Shared per-chunk）✅ + Phase 5 + Phase 6 | **主体完成**（Phase 6 ✅ 2026-08-29；Phase 7 ✅ S25 收口 2026-08-29；Phase 5 关系型状态机 ❌ 已决策不做，剩余仅 S28/S29 低优先级） | ~8-12 周 |
| **里程碑 C：生成器扩展** | Phase 8 | ✅ **已完成**（2026-08-29：S26/S27/S32） | ~2-3 周 |
| **里程碑 D：Managed 类型** | Phase 9（独立轨道） | 未开始 | ~5-8 周 |
| **AutoSIMD 修复** | 与里程碑并行 | 进行中 | ~2-3 周 |

---

## 六、决策记录

### 6.0 Phase 4 重设计：砍掉 Processor/SystemBase，聚焦真正新增能力（2026-08）

**问题**：旧 Phase 4 的 Processor（Unreal Mass）本质是 IJobEntity 换名，SystemBase 只是加了生命周期钩子（ISystem 已有），SystemGraph 如果只做注册+排序用户手动管理也够用。

**决策**：砍掉 Processor、SystemBase 包装，聚焦三大真正缺失的能力：

| 新编号 | 内容 | 来源 | 为什么有价值 |
|--------|------|------|------------|
| S7-new | Schedule Graph | Bevy Schedule | 自动并行化，用户不再手动 Schedule/Complete |
| S8-new | Observer | Flecs Observer | push-based 变化触发，替代 poll-based 遍历 |
| S9-new | One-Frame Component | LeoECS | 帧事件自动清理，合并到 Phase 4 而非 Phase 5 |

**影响**：
- S7 (SystemBase)、S8 (Processor)、S9 (SystemGraph) 合并为 S7-new (Schedule Graph)
- S9-new (One-Frame) 从 Phase 5 移入 Phase 4，因为它是 System 调度的自然配套
- Phase 5 保留 Events/Group/Context/DI，不再包含 One-Frame

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

### 6.4 Shared Component 为何从"拆 Archetype"改为 per-chunk（2026-08-26）

**问题**：v3 原方案"值不同则拆 Archetype"在共享值种类多时 Archetype 爆炸，且高频改值会频繁触发结构迁移（换 Archetype = 全量结构变更）。

**决策**：改为 **per-chunk 存储**（对齐 Unity DOTS）：同一 Archetype + 同一共享值 → 同一 Chunk；改值 → Archetype **内部** Chunk 间移动（无 Archetype 变更）。

**双类型策略**：
- **blittable**（struct 无引用）→ chunk 内存块 Shared values 区内联 → NativeTranspiler 可读（IJobChunk `GetSharedComponent<T>` / IJobEntity job 字段）
- **managed**（string/class）→ EntityManager 扁平值数组 + `Dictionary<object,int>` per type（去重查找 O(1)）+ chunk 槽位存 int 索引；值只增不减，World.Dispose 清空；NativeTranspiler 不处理（validator 编译期拦截）

**参考**：[Unity Entities Shared components](https://docs.unity3d.com/Packages/com.unity.entities@0.50/manual/shared_component_data.html)、[GetSharedComponentIndex](https://docs.unity.cn/Packages/com.unity.entities@1.1/api/Unity.Entities.ArchetypeChunk.GetSharedComponentIndex.html)、[SetSharedComponentManaged](https://docs.unity3d.com/Packages/com.unity.entities@1.2/api/Unity.Entities.EntityManager.SetSharedComponentManaged.html)

**影响**：
- S16 重新定义为"Shared Component 存储（per-chunk）"，S16b 新增"NativeTranspiler 支持 blittable SharedComponent"
- S25（落地扩展）依赖 S16 而非"结构移动"语义
- 完整设计见 `docs/20260826-SharedComponent-perChunk设计.md`

---

## 七、工时估算汇总

| Step | 内容 | 工时 | 累计 |
|------|------|------|------|
| S0-S3 | Phase 1-3 全部 | ✅ 已完成 | — |
| S6 | QueryEnumerator | ✅ 已完成 | — |
| S7-new | Schedule Graph（自动并行） | ✅ 已完成 | — |
| S8-new | Observer（变化触发） | ✅ 已完成（2026-08-28） | — |
| S7b | ISPC SendEvent | ✅ 已完成（2026-08-27） | — |
| S4-S5, S10-S11 | AutoSIMD 关键修复 | 2-3 周 | 并行 |
| S16 + S16b | Shared Component：per-chunk 存储 + NativeTranspiler blittable 支持 | S16 3-5 天 + S16b 3-5 天 | ~5-7 周 |
| S17 | Chunk lazy zero | ✅ 已完成（已验证） | — |
| S18-S20 | Phase 5 核心 | 2-3 周 | ~7-10 周 |
| S23-S24 | Phase 6 核心 | 2 周 | ~9-12 周 |
| S25 | Phase 7 | ✅ 已完成（2026-08-29 收口） | ~10-13 周 |
| S26-S27-S32 | Phase 8 | ✅ 已完成（2026-08-29，里程碑 C 达成） | ~11-15 周 |
| S35-S37 | Phase 9 | 5-8 周 | 独立轨道 |

**里程碑 A（高性能核心）✅ 已完成（2026-08-28）**：Phase 1-4 全部落地（含 Observer 收尾）。
**里程碑 C（生成器扩展）✅ 已完成（2026-08-29）**：Phase 8 全部落地（S26/S27/S32）。

---

## 八、创新功能库（2026-08 脑暴）

> 本节记录所有待选的创新功能点子，供后续 Phase 规划参考。

### 8.1 用户痛点调研（各框架）

| 框架 | 最大痛点 | 来源 |
|------|---------|------|
| **Unity DOTS** | API 冗长（大量 typeof/GetComponent 样板）、Burst 限制多（不能用 string/class）、SafetyHandle 运行时开销、从 MonoBehaviour 迁移困难 | [Unity Discussions](https://discussions.unity.com/t/dots-development-status-and-milestones-ecs-for-all-september-2024/1519286/80#1)、[Stack Review](https://discussions.unity.com/t/november-2025-ecs-stack-review/1694077#1) |
| **Bevy** | System 执行顺序不确定（需要手动声明 ordering）、Rust 学习曲线陡峭、热重载困难 | [Bevy Schedule 文档](https://raw.githubusercontent.com/bevyengine/bevy-website/d5676eee0b06e579328553f617cc8847ab589ebf/content/learn/book/game-logic/system-ordering/_index.md#1) |
| **Flecs** | C API 不友好、关系查询复杂、没有内置 SIMD | [Flecs Observer](https://deepwiki.com/SanderMertens/flecs/4.4-observers#1)、[Flecs Storage](https://deepwiki.com/SanderMertens/flecs/11.2-storage-implementation#1) |
| **通用** | 调试困难（数据分散在不同 Archetype）、热重载丢状态、样板代码多 | [Stack Overflow](https://stackoverflow.com/questions/45316989/avoid-cache-miss-related-to-1n-relationship-in-entity-component-system/50558979#50558979#1)、[ECS FAQ](https://github.com/friflo/ecs-faq) |

### 8.2 创新功能清单

#### 开发体验类

| 功能 | 解决什么痛点 | 创新度 | 实现难度 | EntJoy 优势 |
|------|------------|--------|---------|------------|
| **实体构造器** — `world.Spawn().With<Position>(...).With<Velocity>(...).Build()` | 消灭 CreateEntities + typeof 样板代码 | ⭐⭐⭐ | 低 | — |
| **声明式组件** — `[ECSComponent] partial struct Health { int Value; }` | 消灭 IComponentData + 手动注册 | ⭐⭐⭐ | 中 | SourceGenerator 已有基础 |
| **查询链式过滤** — `world.Query<Position>().Without<Dead>().Where(h => h.Health > 50)` | QueryBuilder 太啰嗦 | ⭐⭐⭐ | 低 | QueryEnumerable 已有基础 |
| **System 依赖声明** — `[DependsOn(typeof(MovementSystem))]` | 手动管理执行顺序容易出错 | ⭐⭐⭐⭐ | 低 | SourceGenerator 已有基础 |
| **实体模板/预制体** — `world.SpawnFrom(zombieTemplate, count: 1000)` | 重复创建实体的样板代码 | ⭐⭐⭐ | 低 | — |
| **ECS Inspector** — 运行时查看实体/Archetype 数据 | 调试困难，数据分散 | ⭐⭐⭐⭐ | 中 | 需要 Godot 面板集成 |

#### 高性能类

| 功能 | 解决什么痛点 | 创新度 | 实现难度 | EntJoy 优势 |
|------|------------|--------|---------|------------|
| **变更追踪** — `world.Query<Position>().ChangedThisFrame()` | 每帧全量遍历浪费 | ⭐⭐⭐⭐ | 中 | Chunk struct 化后 bitmask 高效 |
| **空闲跳过** — `[RunWhen(typeof(DamageEvent))]` | 空查询 System 白跑 | ⭐⭐⭐ | 低 | — |
| **分帧处理** — `world.AddSystem<Heavy>(batchSize: 10000)` | 大任务卡帧 | ⭐⭐⭐⭐ | 中 | — |
| **Auto-SIMD** — 用户写 C#，框架自动生成 SIMD kernel | 手动写 SIMD 太难 | ⭐⭐⭐⭐⭐ | 高 | **NativeTranspiler 已有基础，最强差异化** |
| **Archetype 合并** — 瘦 Chunk 合并减少遍历开销 | 碎片化导致遍历变慢 | ⭐⭐⭐ | 高 | ChunkPool 指针稳定性是基础 |

#### 架构类

| 功能 | 解决什么痛点 | 创新度 | 实现难度 | EntJoy 优势 |
|------|------------|--------|---------|------------|
| **非碎片化关系** — 关系作为 SoA 列，不拆 Archetype | 关系导致 Archetype 碎片化 | ⭐⭐⭐⭐ | 中 | ChunkPool 指针稳定性 |
| **Godot 场景树桥接** — `world.ImportNode<Sprite2D>()` | ECS 和场景树割裂 | ⭐⭐⭐⭐⭐ | 低 | **EntJoy 独有，Godot 集成** |
| **多 World 隔离** — 不同系统域的数据隔离 | 数据耦合 | ⭐⭐⭐ | 低 | World 已有 Name |
| **World 快照/回放** — 录像回放、调试、rollback netcode | 调试和网络同步 | ⭐⭐⭐⭐⭐ | 高 | ChunkPool 指针稳定性 |

### 8.3 关系查询 vs 缓存命中 — 技术分析

**核心问题**：关系（Parent/Child、Link）是否会导致 Archetype 碎片化？

```
❌ 糟糕的实现（碎片化）：
  关系类型成为 Archetype 签名的一部分
  Parent=A → Archetype [Position, Parent=A]  ← 100 entities
  Parent=B → Archetype [Position, Parent=B]  ← 50 entities
  → 同样的 Position 数据被拆到不同 Archetype → cache miss

✅ 好的实现（不碎片化）：Bevy 0.15 / Flecs 方式
  关系只是普通组件的 VALUE，不是 Archetype 签名
  Archetype [Position, ChildOf]  ← 150 entities
  ChildOf 列：[A, A, A, ..., B, B, B, ...]
  → Position 连续存储，ChildOf 也是连续的 → cache 命中
```

| 实现方式 | 碎片化 | Cache 命中 | 代表框架 |
|---------|--------|-----------|---------|
| 关系类型 → Archetype 签名 | ❌ 严重 | 差 | 早期 Unity DOTS |
| 关系 → 普通组件 VALUE | ✅ 无 | 好 | Bevy 0.15、Flecs |
| 关系 → 独立列 (SoA) | ✅ 无 | 好 | **推荐方案** |

**EntJoy 推荐方案**：关系作为 SoA 列存储在 Chunk 上，与 Position/Velocity 等组件一样连续排列。查询时通过 Archetype 匹配获得所有关系实体，遍历时关系列与组件列一样 cache-friendly。

```
Chunk 内存布局（推荐方案）：
[Entity array] [Position column] [Velocity column] [ChildOf column]
                  ↑ 连续            ↑ 连续           ↑ 连续
                  全部 cache-friendly
```

**关键约束**：
- 关系列的 target entity 必须包含 version/epoch，防止 ID 回收后指向错误实体
- 级联删除需要 target index（targetEntity → relation list），不能扫描所有关系列
- 关系查询不能 O(N) 遍历所有实体，需要按 Archetype 分组 + 索引加速

### 8.4 Phase 4 最终推荐方案（2026-08 修订）

砍掉与 IJobEntity 重复的 Processor/SystemBase，聚焦真正新增能力：

| 编号 | 内容 | 来源 | 价值 | 工时 |
|------|------|------|------|------|
| **S7-new** | **Schedule Graph** — 自动并行 | Bevy Schedule | ⭐⭐⭐⭐⭐ | 1-2 周 |
| **S8-new** | **Entity Builder** — 实体构造器 | 通用痛点 | ⭐⭐⭐⭐ | 2-3 天 |
| **S9-new** | **Declarative Components** — 声明式组件 | 通用痛点 | ⭐⭐⭐⭐ | 3-5 天 |
| **S10-new** | **变更追踪** — ChangedThisFrame | 高性能刚需 | ⭐⭐⭐⭐ | 3-5 天 |
| **S11-new** | **空闲跳过** — RunWhen | 简单有效 | ⭐⭐⭐ | 1-2 天 |

**砍掉的**：Processor（与 IJobEntity 重复）、SystemBase（ISystem 已有）

**推迟的**：Observer（push-based 回调，与 Change Tracking pull-based 互补，详见 §八 Phase 4 推荐方案）、One-Frame Component（Event Channel S18 替代，已废弃）、System 依赖声明（Schedule Graph 自动冲突分析覆盖大部分场景）

---

## 九、托管类型支持分析（2026-08 脑暴）

> 本节分析如何在 ECS + NativeTranspiler 中支持托管类型（string/List<T>/class），同时保持 C++ 性能路径。

### 9.1 当前 NativeTranspiler 对 managed 类型的处理

| 组件 | 行为 | 说明 |
|------|------|------|
| **Validator (NT001-NT009)** | 完全禁止 managed 类型 | 返回值/参数/局部变量/Job 字段必须 unmanaged |
| **MapCSharpTypeToCpp** | `if (type.IsReferenceType) return "void*"` | 引用类型映射为 void* 8B 槽位 |
| **ComputeJobFieldMarshal** | 引用字段 → 8B 零填充 | C++ 侧为 void*，Execute 不应访问 |
| **NativeArray/NativeList** | 提取内部 native 指针 | 特殊处理，传递 NativeArrayInfo 结构 |

**当前限制**：managed 字段在 Job struct 中存在但零填充，C++ Execute 不能访问。

### 9.2 GCHandle.Pinned 方案分析

```
方案：临时 Pin managed 字段，获取固定地址传给 C++

单个 Pin+Unpin：~200ns
100K 实体 × 200ns = 20ms ❌ 不可接受

堆碎片化风险：
  同时 Pin 100K 个对象 → 堆严重碎片化
  GC 无法压缩 pinned 区域 → 长期内存泄漏
```

**结论**：GCHandle.Pinned 仅适用于单个对象调试/检查，不适用于批量执行。

### 9.3 托管回调方案分析

```
单次回调（CLR 过渡）：~120ns
100K 实体 × 120ns = 12ms ❌ 太慢

批量回调（一次过渡，处理所有）：~120ns
100K 实体 × 0.12ns = 0.012ms ✅ 可接受
```

**推荐：批量回调 + 延迟执行**

```
执行流程：
1. C++ 处理 unmanaged 字段（SIMD）
2. C++ 标记需要哪些 managed 数据（批量）
3. C# 一次性获取所有 managed 数据
4. C++ 使用 managed 数据
→ 仅 2 次 CLR 过渡 = 240ns（可忽略）
```

### 9.4 分层执行 vs JobSystem

```
问题：JobSystem 调度 Job 到 Worker Thread
  如果拆分 Job 为 C++ 和 C# 两部分：
    C++ 部分：Worker Thread 执行
    C# 部分：在哪执行？
      - 主线程：阻塞主线程 ❌
      - Worker Thread：managed 代码在 worker 上（可行但复杂）
      - 额外线程：开销大 ❌

结论：不要拆分 Job，让 Job 自己处理 managed 和 unmanaged 部分
```

### 9.5 推荐方案：混合 Job + Managed Accessor

```csharp
// 用户写：
[NativeTranspile]
struct PlayerJob : IJobEntity {
    public float dt;
    public NativeArray<float> health;   // unmanaged → C++ SIMD
    public NativeArray<float> speed;    // unmanaged → C++ SIMD
    public IntPtr managedAccessor;      // 托管数据访问器
}

// C++ 生成：
extern "C" void Execute(JobData* data, int count, ManagedAccessor accessor) {
    for (int i = 0; i < count; i++) {
        // unmanaged：直接 SIMD
        data->health[i] += data->speed[i] * dt;
        
        // managed：调用回调
        char name[64];
        accessor.GetName(i, name, 64);
        if (strcmp(name, "Boss") == 0) {
            data->health[i] -= 100;
        }
    }
}
```

### 9.6 推荐实现路径

| 阶段 | 内容 | 工时 | 价值 |
|------|------|------|------|
| **Phase 9.1** | GCHandle + 延迟反序列化（批量回调） | 1-2 周 | ⭐⭐⭐⭐⭐ |
| **Phase 9.2** | 字符串驻留（[Interned] → uint32_t） | 3-5 天 | ⭐⭐⭐⭐⭐ |
| **Phase 9.3** | 混合内核（NativeTranspiler 自动生成 accessor） | 1-2 周 | ⭐⭐⭐⭐ |
| **Phase 9.4** | 列压缩（字典 + 索引） | 1 周 | ⭐⭐⭐ |

### 9.7 EnTT hashed_string 模式

```csharp
// EnTT：字符串在编译期转为 uint32_t
constexpr auto name = entt::hashed_string{"Player"};
// 比较：整数比较，O(1)

// EntJoy 可以这样用：
[Interned("Player")]
struct Player : IComponentData {
    [Interned("Name")] public string Name;  // 编译时转为 uint
    public int Health;
}

// NativeTranspiler 生成 C++：
struct Player {
    uint32_t nameId;    // 编译期哈希
    int32_t health;
};
```

### 9.8 核心原则

```
1. 不要替用户做决定：允许 managed 类型，但明确告知性能代价
2. 批量处理：回调要批量，不要逐个
3. 不拆分 Job：让 Job 自己处理 managed 和 unmanaged
4. 渐进优化：从 managed 开始，逐步迁移到 unmanaged
5. 字符串驻留：编译期哈希，零运行时开销
6. 分层 Pin：只 Pin 被访问的字段，减少碎片化
7. GCHandlePool：限制并发 Pin 数量，批次间允许 GC 压缩
```

### 9.9 托管字段偏移量分析（2026-08 更新）

**问题**：NativeTranspiler 能否编译时知道托管字段偏移？

| 类型 | 编译时可知？ | 原因 | 解决方案 |
|------|------------|------|---------|
| `string` | ✅ 可以 | 布局固定：[header 8B][length 4B][firstChar] | 硬编码偏移 |
| `List<T>` | ✅ 可以 | 内部结构固定：[_items ref][\_size][\_version] | 提取 _items 数组 |
| `float[]`, `int[]` | ✅ 可以 | 数组布局固定：[header 8B][length 4B][elements] | 硬编码偏移 |
| `NativeArray<T>` | ✅ 已有 | 已支持 NativeArrayInfo 结构 | 已实现 |
| **自定义 class** | ❌ 不行 | CLR 可能重排字段，编译时无法确定 | 要求 `[StructLayout(Sequential)]` |

**自定义 class 解决方案**：

```csharp
// 方案 A：要求 [StructLayout(LayoutKind.Sequential)]
[StructLayout(LayoutKind.Sequential)]
class PlayerData {
    public string Name;      // offset 0
    public int Health;       // offset 8
    public float Speed;      // offset 12
}
// 强制顺序后，NativeTranspiler 可以编译时计算偏移

// 方案 B：运行时反射填充偏移
struct PlayerDataDescriptor {
    public IntPtr NameOffset;
    public IntPtr HealthOffset;
}
// 运行时：
desc.NameOffset = Marshal.OffsetOf<PlayerData>("Name");
```

### 9.10 性能分析（2026-08 更新）

**GCHandle 遍历开销**：

```
遍历指针数组：~1ns/元素（数组访问）
100K 实体：~0.1ms（可忽略）

C++ 处理：
  unmanaged 字段：SIMD 加速，~0.3ms/100K
  managed 字段：指针访问，~0.1ms/100K
  → C++ 依然快
```

**批次固定 + 解除耗时**：

| 批次大小 | Pin 耗时 | Unpin 耗时 | 总耗时 |
|---------|---------|-----------|--------|
| 100K | 10ms | 5ms | 15ms |
| 1K | 0.1ms | 0.05ms | 0.15ms |
| 100 | 0.01ms | 0.005ms | 0.015ms |

**关键**：分层 Pin 后，只有被访问的字段才 Pin，数量大幅减少。

### 9.11 GCHandlePool 解决碎片化（2026-08 更新）

**碎片化原因**：同时 Pin 大量对象 → GC 无法压缩 → 堆碎片

**GCHandlePool 解决方案**：

```csharp
class GCHandlePool {
    private readonly int _maxPinned;      // 最大并发 Pin 数（如 1024）
    private int _currentPinned;           // 当前已 Pin 数量
    
    public GCHandle Pin(object obj) {
        if (_currentPinned >= _maxPinned) {
            // 达到上限，等待 GC 压缩
            GC.Collect(0, GCCollectionMode.Forced, false);
            _currentPinned = 0;
        }
        var handle = GCHandle.Alloc(obj, GCHandleType.Pinned);
        _currentPinned++;
        return handle;
    }
    
    public void Unpin(GCHandle handle) {
        handle.Free();
        _currentPinned--;
    }
}
```

**分批处理策略**：

```
总数据：100K 实体
批次大小：1024

批次 1：Pin 1024 → 处理 → Unpin → GC 压缩
批次 2：Pin 1024 → 处理 → Unpin → GC 压缩
...
批次 99：Pin 剩余 512 → 处理 → Unpin

总耗时：~15ms（与不分批相同）
但关键是：堆稳定，无碎片化
```

**并行处理**：

```csharp
// 并行处理，每批独立 Pin/Unpin
Parallel.For(0, batchCount, batchIndex => {
    int start = batchIndex * batchSize;
    int end = Math.Min(start + batchSize, totalCount);
    
    // 本批次 Pin
    var handles = new GCHandle[end - start];
    for (int i = start; i < end; i++) {
        handles[i - start] = GCHandle.Alloc(data[i], GCHandleType.Pinned);
    }
    
    // C++ 处理本批次
    NativeBindings.ProcessBatch(start, end);
    
    // 本批次 Unpin
    foreach (var h in handles) h.Free();
});

// 每批最多 1024 个 Pin，GC 可以在批次间压缩
```

### 9.12 综合推荐（2026-08 更新）

```
NativeTranspiler 编译时：
  1. 分析 Execute 访问了哪些 managed 字段
  2. 对已知类型（string/List/array）计算偏移
  3. 对自定义 class 要求 [StructLayout(Sequential)]
  4. 生成 Pin/Unpin 代码

运行时：
  1. GCHandlePool 管理 Pin（最多 1024 并发）
  2. 分批处理，批次间 GC 可压缩
  3. 返回指针数组给 C++
  4. C++ 直接访问，零拷贝
```

---

## 十、AOT 兼容性（2026-08 审计 → ✅ 已完成）

> 2026-08 审计发现 3 处 AOT 不兼容反射用法，**2026-08-26 已全部修复**。
> 修复记录：`docs/20260826-重构ECS-JobSystem方案.md`（Phase D）+ `docs/20260826-JobSystem重构-性能基线.md`（Phase D 无回归）。
> 2026-08-29 复核：`src/` 全库 grep `MakeGenericMethod|MakeGenericType|.GetMethod(|.GetField(|Activator.` **零命中**。

### 10.1 问题概述（已解决）

2026-08 审计发现 **3 处严重 AOT 不兼容反射用法**（动态泛型实例化 + 反射调用），会导致 iOS/主机/Godot AOT 编译失败。全部已在 2026-08-26 的 JobSystem 重构中消除，不需要 SourceGenerator 介入（改用非泛型指针路径 + 静态泛型委托缓存）。

### 10.2 修复明细（问题 → 修复 → 当前代码）

#### 问题 1：NativeJobCore.cs `CreateParallelForBatchCallback` 反射调用 → ✅ 已修复

```csharp
// 修复前（AOT 不兼容）：
var create = typeof(NativeJobCore)
    .GetMethod(nameof(CreateParallelForBatchCallback), BindingFlags.Static | BindingFlags.NonPublic)
    .MakeGenericMethod(typeof(T))  // ❌ AOT 不兼容
    .Invoke(null, null);           // ❌ AOT 不兼容

// 修复后：静态泛型委托缓存（零字典查找、零反射），当前 NativeJobCore.cs:798-811
internal static class ParallelForBatchDelegateCacheFor<T> where T : struct, IJobParallelForBatch
{
    public static readonly DelegateCache Cache = new(CreateParallelForBatchCallback<T>());
}
// 调度点 NativeJobScheduler.cs:376 直接取 Cache.FuncPtr，AOT 安全
```

#### 问题 2：NativeJobScheduler.cs `BatchRunner<T>` MakeGenericType → ✅ 已修复

```csharp
// 修复前（AOT 不兼容）：
return _batchRunnerCache.GetOrAdd(typeof(T), t =>
    var f = typeof(BatchRunner<>)
        .MakeGenericType(t)  // ❌ AOT 不兼容
        .GetField("Runner"); // ❌ AOT 不兼容

// 修复后：BatchRunner<T> / _batchRunnerCache 已删除（bc6509c 死代码清理），
// 统一走 NativeJobCore 静态泛型缓存 + index 回调（AutoParallelForCallback<T>），
// 当前 NativeJobScheduler.cs 零反射
```

#### 问题 3：EntityManager.cs `AddComponent` MakeGenericMethod → ✅ 已修复

```csharp
// 修复前（AOT 不兼容）：
return typeof(EntityManager)
    .GetMethod(nameof(AddComponent))!
    .MakeGenericMethod(componentType)  // ❌ AOT 不兼容
    .Invoke(this, new object[] { entity, boxedValue });  // ❌ AOT 不兼容

// 修复后：非泛型指针路径（零反射、零 GCHandle 装箱拷贝），
// 当前 EntityManager.cs:882 AddComponentRaw(entity, Type, object) + SetRaw 同构
// 泛型版本 AddComponent<T> 直接转发，ECB Playback 走 AddComponentRaw
```

### 10.3 修复结果

| 问题 | 修复方式 | 落地提交 | 状态 |
|------|---------|---------|------|
| `MakeGenericMethod` (NativeJobCore) | 静态泛型委托缓存 `ParallelForBatchDelegateCacheFor<T>` | 91a3875 + dc3e63d | ✅ 已修复 |
| `MakeGenericType` (NativeJobScheduler) | 删除 `BatchRunner<T>`/`_batchRunnerCache`，统一 index 回调 | bc6509c + 91a3875 | ✅ 已修复 |
| `MakeGenericMethod` (EntityManager) | 非泛型 `AddComponentRaw`/`SetRaw`/`RemoveComponentRaw` 指针路径 | cec84b2 + 01f162e | ✅ 已修复 |

**复核**（2026-08-29）：`src/` 全库 `grep MakeGenericMethod|MakeGenericType|.GetMethod(|.GetField(|Activator.` = **零命中**；`EntJoy.Jobs` 仅剩 `Assembly.Location`（读取 DLL 路径，AOT 安全）。

### 10.4 残留的反射用法（AOT 安全）

| 用法 | 位置 | 说明 |
|------|------|------|
| `GetCustomAttribute` | SystemRunner.cs / ScheduleGraph.cs | 读取特性元数据（编译期已知），AOT 安全 |
| `typeof(T).Name` / `typeof(T).Assembly.Location` | 多处 | 字符串/路径，不生成代码 |
| `RuntimeHelpers.IsReferenceOrContainsReferences<T>()` | NativeJobScheduler.cs | 静态泛型检查，AOT 安全 |

### 10.5 影响范围

```
原受影响平台：iOS / 主机（PS5/Xbox/Switch）/ Godot .NET AOT
修复后：全部消除动态反射，AOT 部署路径安全
（20260823-Unity集成基准.md 已记录：IL2CPP 兼容修复完成，待 Windows Standalone IL2CPP 构建最终验证）
```

### 10.6 后续原则（长期）

```
1. 新代码禁止引入 MakeGenericMethod/MakeGenericType 动态反射（评审门禁）
2. 组件存取一律走非泛型指针路径（AddComponentRaw/SetRaw）或静态泛型缓存
3. 反射仅限编译期可确定的元数据读取（特性、类型名、类型检查）
```

---

## 十一、关系型状态机设计（2026-08 脑暴）

> **决策（2026-08-29 后复核）**：本节方案 **已决策不实施**。理由：状态机属于业务逻辑（§22.6 核心原则明确"业务逻辑由用户在引擎层实现"）；
> 所需基础设施（关系 SoA 列 + 反向索引 + 级联删除 + 遍历 API）已全部就绪，用户可在引擎层用现有 API 自行构建。

> 本节分析如何用 ECS 关系实现高性能状态机，避免 Add/Remove Component 的结构变更开销。

### 11.1 传统状态机的问题

```
传统做法：
  Entity 获得 WalkingState 组件 → AddComponent（结构变更）
  Entity 移除 IdleState 组件 → RemoveComponent（结构变更）

每次结构变更：
  1. Archetype 迁移（移动数据到新 chunk）~10μs
  2. Selective Wait（等待运行中的 Job）~100μs
  3. 新旧 Archetype 查找 ~1μs
  4. Chunk 分配/回收 ~1μs

100K 实体状态转换：
  100K × 112μs = 11.2s ❌ 完全不可接受
```

### 11.2 解决方案对比

| 方案 | 性能 | 复杂度 | 查询能力 | 推荐度 |
|------|------|--------|---------|--------|
| **Enable/Disable** | ⭐⭐⭐⭐⭐ 10ns | 低 | ✅ 按状态查询 | ⭐⭐⭐⭐⭐ |
| **枚举值** | ⭐⭐⭐⭐⭐ 1ns | 低 | ❌ 需要 Where 过滤 | ⭐⭐⭐⭐ |
| **关系型状态机** | ⭐⭐⭐⭐ 100ns | 中 | ✅ 关系查询 | ⭐⭐⭐⭐⭐ |
| **Chunk 元数据** | ⭐⭐⭐⭐ 10μs/chunk | 中 | ✅ 按 Chunk 查询 | ⭐⭐⭐ |
| **事件驱动** | ⭐⭐⭐⭐ 10ns | 中 | ✅ 事件查询 | ⭐⭐⭐⭐ |

### 11.3 关系型状态机设计

**核心思想**：状态是独立的实体，实体与状态的关系 = 当前状态

```csharp
// 1. 定义状态实体
var idleState = world.Spawn()
    .With(new StateData { Name = "Idle", Duration = 0 })
    .With(new AnimationClip { Name = "idle_anim" })
    .Build();

var walkingState = world.Spawn()
    .With(new StateData { Name = "Walking", Speed = 2.0f })
    .With(new AnimationClip { Name = "walk_anim" })
    .Build();

// 2. 实体关联到状态
world.AddRelationship(playerEntity, idleState, new InState());

// 3. 状态转换（零结构变更）
world.RemoveRelationship(playerEntity, idleState);
world.AddRelationship(playerEntity, walkingState, new InState());

// 4. 查询：获取所有在 Idle 状态的实体
foreach (var (e, stateData) in world.Query<StateData>()
    .WithRelationship<InState>(idleState)) {
    // e 是所有在 Idle 状态的实体
}
```

### 11.4 关系型状态机的优势

| 优势 | 说明 |
|------|------|
| **零结构变更** | 状态转换只是修改关系，不触发 Archetype 迁移 |
| **状态可携带数据** | 状态是实体，可以有自己的 Component |
| **共享状态数据** | 多个实体可以共享同一个状态实例 |
| **状态历史** | 通过关系链记录状态转换历史 |
| **状态继承** | 状态可以继承自父状态 |
| **并行状态** | 实体可以同时处于多个状态（Movement + Animation） |
| **状态图** | 可以定义状态转换规则 |

### 11.5 高级特性

#### 状态历史

```csharp
[Relationship]
public struct PreviousState : IRelationshipData {
    public Entity StateEntity;
    public float Timestamp;
}

// 转换时记录历史
world.AddRelationship(entity, currentState, new PreviousState {
    StateEntity = currentState,
    Timestamp = Time.time
});

// 查询状态历史
foreach var (e, prev) in world.Query<PreviousState>()
    .WithRelationshipTo<PreviousState>(entity)) {
    // 遍历所有历史状态
}
```

#### 状态继承

```csharp
// 状态可以继承
var baseIdle = world.Spawn()
    .With(new StateData { Name = "BaseIdle" })
    .Build();

var playerIdle = world.Spawn()
    .With(new StateData { Name = "PlayerIdle" })
    .WithRelationship<InheritsFrom>(baseIdle)
    .Build();

// 查询：获取所有继承自 BaseIdle 的状态
foreach var (e, stateData) in world.Query<StateData>()
    .WithRelationship<InheritsFrom>(baseIdle)) {
    // 包括 PlayerIdle 和其他继承的状态
}
```

#### 并行状态

```csharp
// 实体可以同时处于多个状态
world.AddRelationship(playerEntity, walkingState, new MovementState());
world.AddRelationship(playerEntity, walkAnimState, new AnimationState());

// 独立转换
sm.TransitionTo<MovementState>(runningState);  // 只改变 Movement
sm.TransitionTo<AnimationState>(runAnimState);  // 只改变 Animation
```

### 11.6 性能分析

```
传统状态机（Add/Remove Component）：
  状态转换：~112μs（结构变更）
  100K 实体：~11.2s ❌

关系型状态机（修改关系）：
  状态转换：~100ns（修改关系）
  100K 实体：~10ms ✅

性能提升：1000x
```

### 11.7 与其他框架对比

| 框架 | 状态机实现 | 性能 |
|------|-----------|------|
| Unity DOTS | Add/Remove Component | ❌ 慢 |
| Flecs | 关系 + 状态实体 | ✅ 快 |
| Bevy | State Entity + 资源 | ✅ 快 |
| EnTT | 手动管理 | ⚠️ 取决于实现 |
| **EntJoy** | **关系型状态机** | ✅ **快** |

### 11.8 实施建议

```
Phase 6（关系）完成后的扩展：
  1. 实现 InState 关系类型
  2. 提供 StateMachine 辅助类
  3. 支持状态历史、继承、并行状态
  4. 与 Phase 5（Events）集成，支持状态事件

性能目标：
  状态转换：< 100ns/实体
  100K 实体状态转换：< 10ms
```

---

## 十二、引擎集成与创新功能（2026-08 脑暴）

> 本节记录利用 EntJoy 独特优势（NativeTranspiler、ChunkPool、Godot 集成）的创新功能。

### 12.1 Godot 同步方案对比

| 方案 | 模式 | 性能 | 适用场景 |
|------|------|------|---------|
| **Unity Entities.Graphics** | 编译时烘焙 | ⭐⭐⭐⭐⭐ | 静态场景 |
| **EntJoy 单向同步** | 运行时导入 | ⭐⭐⭐⭐⭐ | 静态场景导入 |
| **EntJoy 双向同步** | 运行时桥接 | ⭐⭐⭐ | 动态场景 |

**推荐**：单向同步（Godot Node → ECS Entity），只在启动时同步一次，零运行时开销。

### 12.2 ECS 代码生成器

**可行性分析**：

| 功能 | 可行性 | 难度 | 价值 |
|------|--------|------|------|
| **Component 生成** | ✅ 高 | 低 | 高 |
| **System 生成** | ✅ 高 | 中 | 高 |
| **查询过滤器** | ✅ 高 | 低 | 中 |
| **序列化代码** | ✅ 高 | 中 | 高 |
| **调试显示** | ✅ 高 | 低 | 中 |
| **实体工厂** | ✅ 高 | 低 | 高 |
| **事件处理** | ✅ 高 | 中 | 中 |
| **状态机** | ⚠️ 中 | 高 | 高 |

### 12.3 ECS 性能优化器

**可行性分析**：

| 功能 | 可行性 | 难度 | 价值 |
|------|--------|------|------|
| **查询模式分析** | ✅ 高 | 中 | 高 |
| **内存访问分析** | ⚠️ 中 | 高 | 高 |
| **并发分析** | ⚠️ 中 | 高 | 中 |
| **SIMD 分析** | ✅ 高 | 中 | 高 |
| **运行时性能收集** | ✅ 高 | 低 | 高 |
| **优化建议生成** | ✅ 高 | 中 | 高 |
| **自动优化应用** | ⚠️ 中 | 高 | 高 |

### 12.4 EntJoy 独特优势的创新

| 功能 | 利用的优势 | 可行性 | 价值 |
|------|-----------|--------|------|
| **Auto-SIMD 动画** | NativeTranspiler | ✅ 高 | ⭐⭐⭐⭐⭐ |
| **编译时物理优化** | NativeTranspiler | ✅ 高 | ⭐⭐⭐⭐⭐ |
| **零拷贝序列化** | ChunkPool | ✅ 高 | ⭐⭐⭐⭐⭐ |
| **实体快照/回放** | ChunkPool | ✅ 高 | ⭐⭐⭐⭐⭐ |
| **内存分析器** | ChunkPool | ✅ 高 | ⭐⭐⭐⭐ |
| **Godot 场景导入** | Godot 集成 | ✅ 高 | ⭐⭐⭐⭐⭐ |

---

## 十三、现代化 ECS 开发与设计（2026-08 探索）

> 本节探索如何让 ECS 开发更现代化、更易用、更高效。

### 13.1 现代化 ECS 设计原则

```
传统 ECS：
  - 命令式编程（告诉计算机怎么做）
  - 手动管理状态
  - 手动优化性能

现代化 ECS：
  - 声明式编程（告诉计算机想要什么）
  - 自动管理状态
  - 自动优化性能
```

### 13.2 声明式编程

```csharp
// 传统：命令式
public class MovementSystem : ISystem {
    public void OnUpdate() {
        foreach var (e, pos, vel) in world.Query<Position, Velocity>()) {
            pos.X += vel.X * dt;
            pos.Y += vel.Y * dt;
        }
    }
}

// 现代化：声明式
[UpdateEveryFrame]
[Query<Position, Velocity>]
public void Move(ref Position pos, in Velocity vel) {
    pos.X += vel.X * dt;
    pos.Y += vel.Y * dt;
}

// 框架自动生成：
// 1. System 类
// 2. 查询过滤器
// 3. 调度逻辑
// 4. 性能优化
```

### 13.3 响应式编程

```csharp
// 传统：轮询
public class HealthSystem : ISystem {
    public void OnUpdate() {
        foreach var (e, health) in world.Query<Health>()) {
            if (health.Value <= 0) {
                world.DestroyEntity(e);
            }
        }
    }
}

// 现代化：响应式
world.Query<Health>()
    .Where(h => h.Value <= 0)
    .OnMatch((entity, health) => {
        world.DestroyEntity(entity);
    });

// 或者：
world.Watch<Health>()
    .Where(h => h.Value <= 0)
    .Subscribe((entity, oldVal, newVal) => {
        Console.WriteLine($"Entity {entity} died: {oldVal.Value} -> {newVal.Value}");
    });
```

### 13.4 组合式系统

```csharp
// 传统：每个系统独立
public class MovementSystem : ISystem { ... }
public class AttackSystem : ISystem { ... }
public class AnimationSystem : ISystem { ... }

// 现代化：组合式
var player = world.Spawn()
    .With<MovementBehavior>(speed: 5f)
    .With<AttackBehavior>(damage: 10f, range: 2f)
    .With<AnimationBehavior>(clips: animClips)
    .Build();

// 框架自动：
// 1. 检测有哪些 Behavior
// 2. 自动调度对应的 System
// 3. 处理行为之间的优先级
// 4. 生成最优执行计划
```

### 13.5 自动优化

```csharp
// 传统：手动优化
public class MovementSystem : IJobChunk {
    public void Execute(ArchetypeChunk chunk, int chunkIndex) {
        var positions = chunk.GetNativeArray<Position>();
        var velocities = chunk.GetNativeArray<Velocity>();
        
        // 手动 SIMD 优化
        for (int i = 0; i < chunk.Count; i += 8) {
            var pos = positions.GetSubArray(i, 8);
            var vel = velocities.GetSubArray(i, 8);
            SIMD.Add(pos, vel, dt);
        }
    }
}

// 现代化：自动优化
[AutoSIMD]
public void Move(ref Position pos, in Velocity vel) {
    pos.X += vel.X * dt;
    pos.Y += vel.Y * dt;
}

// NativeTranspiler 自动：
// 1. 分析数据布局
// 2. 生成 SIMD 内核
// 3. 处理边界情况
// 4. 优化内存访问
```

---

## 十四、更多用户痛点（2026-08 探索）

### 14.1 代码组织痛点

```
用户痛点：
  "我不知道怎么组织 ECS 代码"
  "System 太多了，不知道放哪里"
  "Component 太多了，不知道怎么分类"

解决方案：模块化 + 命名空间

// 按功能模块组织
namespace EntJoy.Modules.Physics {
    public struct Position : IComponentData { ... }
    public struct Velocity : IComponentData { ... }
    public struct Collider : IComponentData { ... }
    
    public class MovementSystem : ISystem { ... }
    public class CollisionSystem : ISystem { ... }
}

namespace EntJoy.Modules.Combat {
    public struct Health : IComponentData { ... }
    public struct Damage : IComponentData { ... }
    
    public class DamageSystem : ISystem { ... }
    public class DeathSystem : ISystem { ... }
}

// 用户按需加载模块
world.LoadModule<PhysicsModule>();
world.LoadModule<CombatModule>();
```

### 14.2 数据导航痛点

```
用户痛点：
  "我找不到我的数据"
  "Entity 在哪里？Component 在哪里？"
  "我想查看某个 Archetype 的所有 Entity"

解决方案：数据导航工具

// 查询编辑器
var query = world.QueryEditor()
    .With<Position>()
    .With<Velocity>()
    .Where<Health>(h => h.Value > 50)
    .Build();

// 结果：
// Query Results:
//   Archetype 1: [Position, Velocity, Health]
//     Entity #1: Position(1.5, 2.3), Velocity(0.1, 0.0), Health(75)
//     Entity #2: Position(3.0, 1.0), Velocity(0.0, 0.1), Health(100)
//   Archetype 2: [Position, Velocity, Health, Name]
//     Entity #3: Position(5.0, 5.0), Velocity(0.2, 0.2), Health(60)
//   
//   Total: 3 entities in 2 archetypes
```

### 14.3 可观测性痛点

```
用户痛点：
  "我不知道系统在做什么"
  "我不知道性能瓶颈在哪里"
  "我不知道数据怎么流动的"

解决方案：可观测性系统

// 执行追踪
world.EnableTracing();

// 输出：
// Execution Trace:
//   Frame 1234:
//     1. InputSystem (0.1ms)
//        - Read: InputState (singleton)
//        - Write: PlayerInput (entity #42)
//     
//     2. MovementSystem (1.2ms) [PARALLEL with PhysicsSystem]
//        - Read: Position, Velocity (1,000,000 entities)
//        - Write: Position
//        - Side effects: None
//     
//     3. PhysicsSystem (0.8ms) [PARALLEL with MovementSystem]
//        - Read: Position, Collider (1,000,000 entities)
//        - Write: Position, Velocity
//        - Side effects: CollisionEvent (123 events)
//     
//     4. RenderSystem (0.5ms) [AFTER MovementSystem]
//        - Read: Position, Sprite (500,000 entities)
//        - Write: None
//   
//   Total: 2.6ms (60% parallel, 40% serial)
```

### 14.4 协作痛点

```
用户痛点：
  "多人开发时代码冲突"
  "不知道谁修改了什么"
  "无法合并不同人的工作"

解决方案：协作工具

// 模块所有权
[ModuleOwner("PhysicsTeam")]
public class PhysicsModule { ... }

[ModuleOwner("CombatTeam")]
public class CombatModule { ... }

// 变更追踪
var changes = world.GetChanges();
// Changes:
//   PhysicsModule:
//     - Added: PhysicsSystem
//     - Modified: MovementSystem (by Alice)
//   CombatModule:
//     - Added: DamageSystem (by Bob)
//     - Modified: HealthSystem (by Charlie)

// 冲突检测
var conflicts = world.DetectConflicts();
// Conflicts:
//   - Position component modified by both PhysicsModule and CombatModule
//     Suggestion: Use separate PositionPhysics and PositionCombat components
```

### 14.5 可维护性痛点

```
用户痛点：
  "代码太乱，不知道怎么维护"
  "重构很困难，容易出错"
  "测试很难写"

解决方案：可维护性工具

// 依赖分析
var deps = world.AnalyzeDependencies();
// Dependencies:
//   MovementSystem depends on: Position, Velocity
//   CollisionSystem depends on: Position, Collider
//   DamageSystem depends on: Health, Damage
//   
//   Circular dependencies: None ✅
//   Unused components: None ✅
//   Missing components: None ✅

// 重构工具
var refactor = world.Refactor();
// Suggestions:
//   1. Split "Position" into "Position2D" and "Position3D"
//   2. Merge "DamageSystem" and "HealthSystem" into "CombatSystem"
//   3. Extract "MovementBehavior" from "MovementSystem"

// 测试生成
var tests = world.GenerateTests();
// Generated:
//   - MovementSystemTests.cs
//   - CollisionSystemTests.cs
//   - DamageSystemTests.cs
```

### 14.6 可扩展性痛点

```
用户痛点：
  "我想扩展框架，但不知道怎么做"
  "我想添加自定义 Component，但不知道规范"
  "我想添加自定义 System，但不知道怎么集成"

解决方案：扩展点

// 扩展接口
public interface IComponentExtension {
    void OnComponentAdded(Entity entity, IComponentData component);
    void OnComponentRemoved(Entity entity, IComponentData component);
    void OnComponentChanged(Entity entity, IComponentData component);
}

public interface ISystemExtension {
    void OnSystemRegistered(ISystem system);
    void OnSystemExecuted(ISystem system, TimeSpan elapsed);
    void OnSystemError(ISystem system, Exception error);
}

// 插件系统
[Plugin]
public class DebugPlugin : IPlugin {
    public void Initialize(World world) {
        world.AddExtension<DebugComponentExtension>();
        world.AddExtension<DebugSystemExtension>();
    }
}

// 用户扩展
world.AddPlugin<DebugPlugin>();
world.AddPlugin<NetworkingPlugin>();
world.AddPlugin<SaveLoadPlugin>();
```

### 14.7 性能可预测性痛点

```
用户痛点：
  "我不知道性能会不会下降"
  "我不知道新功能会不会影响性能"
  "我不知道优化是否有效"

解决方案：性能基准测试

// 性能基准
[PerformanceBenchmark]
public class MovementBenchmark {
    [Benchmark(1000)]
    public void Movement_1M_Entities() {
        // ...
    }
    
    [Benchmark(1000)]
    public void Movement_10M_Entities() {
        // ...
    }
}

// CI/CD 集成
// 每次提交自动运行基准测试
// 如果性能下降 > 10%，阻止合并

// 性能报告
// Benchmark Results:
//   Movement_1M_Entities: 1.2ms ± 0.1ms (✅ PASS)
//   Movement_10M_Entities: 12ms ± 1ms (✅ PASS)
//   
//   Comparison with previous commit:
//     Movement_1M_Entities: -5% (✅ IMPROVED)
//     Movement_10M_Entities: +2% (✅ STABLE)
```

---

## 十五、现代化 ECS 设计总结

| 设计原则 | 传统 ECS | 现代化 ECS | EntJoy 优势 |
|---------|---------|-----------|------------|
| **编程范式** | 命令式 | 声明式 | SourceGenerator |
| **状态管理** | 手动 | 自动 | ChunkPool |
| **性能优化** | 手动 | 自动 | NativeTranspiler |
| **代码组织** | 扁平 | 模块化 | 命名空间 |
| **数据导航** | 手动查询 | 可视化编辑 | Godot 集成 |
| **可观测性** | 无 | 完整追踪 | 运行时分析 |
| **协作支持** | 无 | 模块所有权 | 工具链 |
| **可维护性** | 困难 | 自动分析 | SourceGenerator |
| **可扩展性** | 困难 | 插件系统 | 模块化 |
| **性能可预测** | 不确定 | 基准测试 | CI/CD 集成 |

### EntJoy 现代化 ECS 路线图

```
Phase 1-3（已完成）：
  ✅ 基础设施优化
  ✅ Archetype Edges
  ✅ Selective Wait

Phase 4（设计完成）：
  🔲 Schedule Graph（自动并行）
  🔲 Entity Builder（实体构造器）
  🔲 Declarative Components（声明式组件）
  🔲 变更追踪
  🔲 空闲跳过

Phase 5-6（设计完成）：
  ~~🔲 关系型状态机~~ ❌ 已决策不做（2026-08-29 后复核）
  🔲 事件总线
  🔲 World Events

Phase 7-8（设计完成）：
  🔲 SourceGenerator 扩展
  🔲 ECS 代码生成器
  🔲 ECS 性能优化器

Phase 9（设计完成）：
  🔲 托管类型支持
  🔲 GCHandlePool
  🔲 NativeTranspiler 突破

工具链（设计完成）：
  🔲 Godot 场景导入
  🔲 内存分析器
  🔲 性能分析器
  🔲 数据导航工具
```

---

## 十六、引擎集成与创新功能（2026-08 脑暴）

### 16.1 Godot 同步方案对比

| 方案 | 模式 | 性能 | 适用场景 |
|------|------|------|---------|
| **Unity Entities.Graphics** | 编译时烘焙 | ⭐⭐⭐⭐⭐ | 静态场景 |
| **EntJoy 单向同步** | 运行时导入 | ⭐⭐⭐⭐⭐ | 静态场景导入 |
| **EntJoy 双向同步** | 运行时桥接 | ⭐⭐⭐ | 动态场景 |

**推荐**：单向同步（Godot Node → ECS Entity），只在启动时同步一次，零运行时开销。

### 16.2 ECS 代码生成器

**可行性分析**：

| 功能 | 可行性 | 难度 | 价值 |
|------|--------|------|------|
| **Component 生成** | ✅ 高 | 低 | 高 |
| **System 生成** | ✅ 高 | 中 | 高 |
| **查询过滤器** | ✅ 高 | 低 | 中 |
| **序列化代码** | ✅ 高 | 中 | 高 |
| **调试显示** | ✅ 高 | 低 | 中 |
| **实体工厂** | ✅ 高 | 低 | 高 |
| **事件处理** | ✅ 高 | 中 | 中 |
| **状态机** | ⚠️ 中 | 高 | 高 |

### 16.3 ECS 性能优化器

**可行性分析**：

| 功能 | 可行性 | 难度 | 价值 |
|------|--------|------|------|
| **查询模式分析** | ✅ 高 | 中 | 高 |
| **内存访问分析** | ⚠️ 中 | 高 | 高 |
| **并发分析** | ⚠️ 中 | 高 | 中 |
| **SIMD 分析** | ✅ 高 | 中 | 高 |
| **运行时性能收集** | ✅ 高 | 低 | 高 |
| **优化建议生成** | ✅ 高 | 中 | 高 |
| **自动优化应用** | ⚠️ 中 | 高 | 高 |

### 16.4 EntJoy 独特优势的创新

| 功能 | 利用的优势 | 可行性 | 价值 |
|------|-----------|--------|------|
| **Auto-SIMD 动画** | NativeTranspiler | ✅ 高 | ⭐⭐⭐⭐⭐ |
| **编译时物理优化** | NativeTranspiler | ✅ 高 | ⭐⭐⭐⭐⭐ |
| **零拷贝序列化** | ChunkPool | ✅ 高 | ⭐⭐⭐⭐⭐ |
| **实体快照/回放** | ChunkPool | ✅ 高 | ⭐⭐⭐⭐⭐ |
| **内存分析器** | ChunkPool | ✅ 高 | ⭐⭐⭐⭐ |
| **Godot 场景导入** | Godot 集成 | ✅ 高 | ⭐⭐⭐⭐⭐ |

---

## 十七、现代化 ECS 开发与设计（2026-08 探索）

### 17.1 现代化 ECS 设计原则

```
传统 ECS：
  - 命令式编程（告诉计算机怎么做）
  - 手动管理状态
  - 手动优化性能

现代化 ECS：
  - 声明式编程（告诉计算机想要什么）
  - 自动管理状态
  - 自动优化性能
```

### 17.2 声明式编程

```csharp
// 传统：命令式
public class MovementSystem : ISystem {
    public void OnUpdate() {
        foreach var (e, pos, vel) in world.Query<Position, Velocity>()) {
            pos.X += vel.X * dt;
            pos.Y += vel.Y * dt;
        }
    }
}

// 现代化：声明式
[UpdateEveryFrame]
[Query<Position, Velocity>]
public void Move(ref Position pos, in Velocity vel) {
    pos.X += vel.X * dt;
    pos.Y += vel.Y * dt;
}

// 框架自动生成：
// 1. System 类
// 2. 查询过滤器
// 3. 调度逻辑
// 4. 性能优化
```

### 17.3 响应式编程

```csharp
// 传统：轮询
public class HealthSystem : ISystem {
    public void OnUpdate() {
        foreach var (e, health) in world.Query<Health>()) {
            if (health.Value <= 0) {
                world.DestroyEntity(e);
            }
        }
    }
}

// 现代化：响应式
world.Query<Health>()
    .Where(h => h.Value <= 0)
    .OnMatch((entity, health) => {
        world.DestroyEntity(entity);
    });
```

---

## 十八、零样板代码分析（2026-08 分析）

### 18.1 零样板代码对比

| 操作 | Arch | Flecs.NET | EntJoy |
|------|------|-----------|--------|
| **定义 Component** | 1 行 | 3 行 | 3 行 |
| **定义 System** | 3 行 | 5 行 | 7 行 |
| **调用 System** | 1 行 | 5 行 | 3 行 |
| **总计** | **5 行** | **13 行** | **13 行** |

### 18.2 核心差异：Struct vs Delegate

```
Arch/Flecs.NET：委托/lambda
  ✅ 零样板（一行定义 System）
  ❌ 有分配（delegate 是托管对象）
  ❌ 不友好 SIMD（无法内联）

EntJoy：Struct
  ✅ 零分配（热路径无 GC）
  ✅ SIMD 友好（可内联到 C++）
  ❌ 样板代码多（需要定义 struct）
```

### 18.3 EntJoy 的优势

**Struct 方案是正确的选择**：

```
性能对比：
  Delegate：~100ns/调用（分配 + 间接调用）
  Struct：~1ns/调用（内联 + SIMD）

100K 实体：
  Delegate：100K × 100ns = 10ms
  Struct：100K × 1ns = 0.1ms

100x 性能差距！
```

### 18.4 解决方案：SourceGenerator 减少样板

**保留 Struct 方案，用 SourceGenerator 自动生成样板代码**：

```csharp
// 用户写简洁定义
[Job]
static void Move(ref Position pos, in Velocity vel) {
    pos.X += vel.X;
    pos.Y += vel.Y;
}

// SourceGenerator 自动生成：
public struct MoveJob : IJobEntity {
    public void Execute(ref Position pos, in Velocity vel) {
        pos.X += vel.X;
        pos.Y += vel.Y;
    }
}

// 用户一行调用
World.Schedule<MoveJob>();
```

---

## 十九、更多用户痛点（2026-08 探索）

### 19.1 代码组织痛点

```
用户痛点：
  "我不知道怎么组织 ECS 代码"
  "System 太多了，不知道放哪里"
  "Component 太多了，不知道怎么分类"

解决方案：模块化 + 命名空间

// 按功能模块组织
namespace EntJoy.Modules.Physics {
    public struct Position : IComponentData { ... }
    public struct Velocity : IComponentData { ... }
    public struct Collider : IComponentData { ... }
    
    public class MovementSystem : ISystem { ... }
    public class CollisionSystem : ISystem { ... }
}
```

### 19.2 数据导航痛点

```
用户痛点：
  "我找不到我的数据"
  "Entity 在哪里？Component 在哪里？"
  "我想查看某个 Archetype 的所有 Entity"

解决方案：数据导航工具

// 查询编辑器
var query = world.QueryEditor()
    .With<Position>()
    .With<Velocity>()
    .Where<Health>(h => h.Value > 50)
    .Build();
```

### 19.3 可观测性痛点

```
用户痛点：
  "我不知道系统在做什么"
  "我不知道性能瓶颈在哪里"
  "我不知道数据怎么流动的"

解决方案：可观测性系统

// 执行追踪
world.EnableTracing();

// 输出：
// Execution Trace:
//   Frame 1234:
//     1. InputSystem (0.1ms)
//     2. MovementSystem (1.2ms) [PARALLEL]
//     3. PhysicsSystem (0.8ms) [PARALLEL]
//     4. RenderSystem (0.5ms) [AFTER]
//   
//   Total: 2.6ms (60% parallel)
```

### 19.4 协作痛点

```
用户痛点：
  "多人开发时代码冲突"
  "不知道谁修改了什么"
  "无法合并不同人的工作"

解决方案：协作工具

// 模块所有权
[ModuleOwner("PhysicsTeam")]
public class PhysicsModule { ... }

// 变更追踪
var changes = world.GetChanges();
// Changes:
//   PhysicsModule (by Alice):
//     - Added: PhysicsSystem
//   CombatModule (by Bob):
//     - Added: DamageSystem
```

### 19.5 可维护性痛点

```
用户痛点：
  "代码太乱，不知道怎么维护"
  "重构很困难，容易出错"
  "测试很难写"

解决方案：可维护性工具

// 依赖分析
var deps = world.AnalyzeDependencies();
// Dependencies:
//   MovementSystem depends on: Position, Velocity
//   CollisionSystem depends on: Position, Collider
//   
//   Circular dependencies: None ✅

// 测试生成
var tests = world.GenerateTests();
// Generated:
//   - MovementSystemTests.cs
//   - CollisionSystemTests.cs
```

### 19.6 性能可预测性痛点

```
用户痛点：
  "我不知道性能会不会下降"
  "我不知道新功能会不会影响性能"
  "我不知道优化是否有效"

解决方案：性能基准测试

// 性能基准
[PerformanceBenchmark]
public class MovementBenchmark {
    [Benchmark(1000)]
    public void Movement_1M_Entities() {
        // ...
    }
}

// CI/CD 集成
// 每次提交自动运行基准测试
// 如果性能下降 > 10%，阻止合并
```

---

## 二十、创新 ECS 方向（2026-08 探索）

### 20.1 ECS 领域特定语言（DSL）

```yaml
# ECS 脚本：movement.yecs
entity Player {
    component Position { x: 0, y: 0 }
    component Velocity { x: 0, y: 0 }
    component Health { value: 100 }
}

system Movement {
    query: Position, Velocity
    update: {
        position.x += velocity.x * delta_time
        position.y += velocity.y * delta_time
    }
}

system HealthCheck {
    query: Health
    when: health.value <= 0
    action: destroy(entity)
}
```

### 20.2 ECS 模板系统

```csharp
// 使用模板
var world = new World();
world.UseTemplate<PlatformerTemplate>();
world.UseTemplate<RTSTemplate>();
world.UseTemplate<FightingTemplate>();

// PlatformerTemplate 自动生成：
// - MovementSystem
// - GravitySystem
// - CollisionSystem
// - AnimationSystem
// - InputSystem
// - CameraSystem
```

### 20.3 热重载

```csharp
// 修改代码后自动热重载
[HotReload]
public class MovementSystem : ISystem {
    public void OnUpdate() {
        // 修改这里的代码
        // 立即生效，不需要重启
    }
}

// 热重载时：
// 1. 保留所有 Entity 和 Component 数据
// 2. 替换 System 实现
// 3. 继续执行
// 4. 零停机时间
```

### 20.4 运行时编辑

```csharp
// 运行时编辑
var editor = world.GetEditor();

// 修改 Component
editor.SetComponent<Position>(entity, new Position { X = 10, Y = 20 });

// 添加 Component
editor.AddComponent<Health>(entity, new Health { Value = 100 });

// 撤销/重做
editor.Undo();
editor.Redo();
```

### 20.5 多人协作

```csharp
// 协作编辑
var collab = world.GetCollaboration();

// 邀请协作者
collab.Invite("alice@example.com");

// 实时同步
collab.OnEntityChanged += (entity, changes) => {
    Console.WriteLine($"Entity {entity} changed by {changes.Author}");
};

// 锁定实体（避免冲突）
collab.LockEntity(entity, "alice");
```

### 20.6 AI 代码生成

```csharp
// AI 代码生成
var ai = world.GetAI();

// 描述需求
var code = ai.Generate(@"
  创建一个玩家角色：
  - 可以移动
  - 可以跳跃
  - 有生命值
  - 可以攻击敌人
");

// AI 生成：
// 1. Player 组件
// 2. MovementSystem
// 3. JumpSystem
// 4. HealthSystem
// 5. AttackSystem
```

### 20.7 自动重构

```csharp
// 自动重构
var refactor = world.GetRefactor();

// 检测代码问题
var issues = refactor.Analyze();
// Issues:
//   1. MovementSystem 和 PhysicsSystem 功能重复
//   2. Position 组件被太多 System 依赖

// 自动修复
refactor.Fix(issues);
```

### 20.8 运行时分析

```csharp
// 运行时数据分析
var analytics = world.GetAnalytics();

// 收集数据
analytics.Collect("entity_count");
analytics.Collect("system_performance");

// 生成报告
var report = analytics.GenerateReport();
// Report:
//   Entity Count: 1,234,567
//   System Performance:
//     - MovementSystem: 1.2ms (32%)
//     - CollisionSystem: 0.8ms (21%)
```

### 20.9 预测分析

```csharp
// 预测分析
var predictor = world.GetPredictor();

// 预测性能
var prediction = predictor.PredictPerformance(world);
// Prediction:
//   如果添加 100K 实体：
//     - MovementSystem: 1.2ms → 1.5ms (+25%)
//     - 总帧时间: 2.6ms → 3.2ms (+23%)

// 优化建议
var suggestions = predictor.SuggestOptimizations(world);
// Suggestions:
//   1. 使用对象池减少 GC
//   2. 使用批量处理减少系统调用
```

### 20.10 关卡编辑器

```csharp
// 关卡编辑器
var editor = world.GetLevelEditor();

// 创建关卡
var level = editor.CreateLevel("Level1");

// 添加实体
editor.AddEntity(level, new EntityConfig {
    Type = "Enemy",
    Position = new Vector2(10, 5),
    Properties = new Dictionary<string, object> {
        { "Health", 100 },
        { "Speed", 2.0f }
    }
});

// 保存关卡
editor.SaveLevel(level, "level1.ecs");

// 预览关卡
editor.Preview(level);
```

---

## 二十一、创新 ECS 方向总结

| 功能 | 创新度 | 实现难度 | 价值 | 优先级 |
|------|--------|---------|------|--------|
| **ECS DSL** | ⭐⭐⭐⭐⭐ | 高 | ⭐⭐⭐⭐⭐ | ⭐⭐⭐ |
| **ECS 模板** | ⭐⭐⭐⭐ | 中 | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| **热重载** | ⭐⭐⭐⭐ | 高 | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ |
| **运行时编辑** | ⭐⭐⭐⭐ | 中 | ⭐⭐⭐⭐ | ⭐⭐⭐⭐ |
| **多人协作** | ⭐⭐⭐⭐⭐ | 高 | ⭐⭐⭐⭐ | ⭐⭐⭐ |
| **AI 代码生成** | ⭐⭐⭐⭐⭐ | 高 | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ |
| **自动重构** | ⭐⭐⭐⭐ | 高 | ⭐⭐⭐⭐ | ⭐⭐⭐ |
| **运行时分析** | ⭐⭐⭐⭐ | 中 | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| **预测分析** | ⭐⭐⭐⭐⭐ | 高 | ⭐⭐⭐⭐ | ⭐⭐⭐ |
| **关卡编辑器** | ⭐⭐⭐⭐ | 中 | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ |

## EntJoy 独特优势的创新

| 功能 | 利用的优势 | 创新度 | 可行性 |
|------|-----------|--------|--------|
| **Auto-SIMD 代码生成** | NativeTranspiler | ⭐⭐⭐⭐⭐ | ✅ 高 |
| **编译时优化建议** | NativeTranspiler | ⭐⭐⭐⭐⭐ | ✅ 高 |
| **指针稳定热重载** | ChunkPool | ⭐⭐⭐⭐ | ✅ 高 |
| **零拷贝运行时编辑** | ChunkPool | ⭐⭐⭐⭐ | ✅ 高 |
| **Godot 场景桥接** | Godot 集成 | ⭐⭐⭐⭐ | ✅ 高 |

---

## 二十二、EntJoy 定位与聚焦（2026-08 修正）

> 本节明确 EntJoy 的核心定位，聚焦于高价值、符合定位的创新方向。

### 22.1 EntJoy 核心定位

```
EntJoy = 高性能无头 ECS 框架
  - 不是游戏引擎
  - 不是可视化工具
  - 是提供给游戏引擎和 .NET 生态的高性能 ECS 核心

核心价值：
  1. 高性能 ECS 核心（Archetype、Chunk、Query）
  2. NativeTranspiler（C# → C++/ISPC 自动编译）
  3. ChunkPool（内存管理、指针稳定性）
  4. JobSystem（多线程调度）
```

### 22.2 游戏引擎开发者真正需要的

| 需求 | 优先级 | EntJoy 能提供 |
|------|--------|--------------|
| **高性能 ECS 核心** | ⭐⭐⭐⭐⭐ | ✅ 已有 |
| **零分配遍历** | ⭐⭐⭐⭐⭐ | ✅ QueryEnumerable |
| **自动并行** | ⭐⭐⭐⭐⭐ | 🔲 Schedule Graph |
| **SIMD 优化** | ⭐⭐⭐⭐⭐ | ✅ NativeTranspiler |
| **内存效率** | ⭐⭐⭐⭐⭐ | ✅ ChunkPool |
| **易用 API** | ⭐⭐⭐⭐ | 🔲 SourceGenerator |
| **Godot 集成** | ⭐⭐⭐⭐ | 🔲 场景桥接 |
| **.NET 兼容** | ⭐⭐⭐⭐⭐ | ✅ 已有 |
| **AOT 兼容** | ⭐⭐⭐⭐⭐ | ✅ 已完成（2026-08-26，动态反射全消除） |

### 22.3 不需要的（偏离定位）

| 功能 | 原因 |
|------|------|
| ❌ ECS DSL | 太重，用户可以直接用 C# |
| ❌ 可视化编辑器 | 游戏引擎自己有 |
| ❌ 多人协作 | 不是框架的职责 |
| ❌ AI 代码生成 | 太超前，不实用 |
| ❌ 关卡编辑器 | 游戏引擎自己有 |
| ❌ 粒子编辑器 | 游戏引擎自己有 |

### 22.4 聚焦的创新方向

#### Phase 4（必须做）

```csharp
// 自动并行
world.AddSystem<MovementSystem>(
    reads: typeof(Velocity),
    writes: typeof(Position)
);
world.Update();  // 自动分析冲突、并行执行

// 实体构造器
var entity = world.Spawn()
    .With(new Position { X = 1, Y = 2 })
    .With(new Velocity { X = 0, Y = 1 })
    .Build();

// 变更追踪
foreach var (e, pos) in world.Query<Position>().ChangedThisFrame()) {
    // 只处理变化的实体
}
```

#### NativeTranspiler 增强（核心价值）

```csharp
// Auto-SIMD（已有基础）
[NativeTranspile]
struct MovementJob : IJobEntity {
    public void Execute(ref Position pos, in Velocity vel) {
        pos.X += vel.X * dt;
        pos.Y += vel.Y * dt;
    }
}
// 自动生成 SIMD 内核

// 托管类型支持（Phase 9）
[NativeTranspile]
struct PlayerJob : IJobEntity {
    [ManagedField] public string Name;  // 托管字段
    public int Health;                  // 非托管字段
}
// 托管字段通过指针数组传递给 C++
```

#### Godot 集成（参考实现）

```csharp
// 场景导入（单向同步）
[SyncFromNode(typeof(Sprite2D))]
public struct SpriteData : IComponentData {
    public int TextureId;
    public float X, Y;
}

// 启动时导入一次，零运行时开销
```

### 22.5 符合定位的创新总结

| 功能 | 创新度 | 实现难度 | 价值 | 符合定位 |
|------|--------|---------|------|---------|
| **Schedule Graph** | ⭐⭐⭐⭐ | 中 | ⭐⭐⭐⭐⭐ | ✅ |
| **Entity Builder** | ⭐⭐⭐ | 低 | ⭐⭐⭐⭐ | ✅ |
| **变更追踪** | ⭐⭐⭐⭐ | 中 | ⭐⭐⭐⭐⭐ | ✅ |
| **Auto-SIMD 增强** | ⭐⭐⭐⭐⭐ | 中 | ⭐⭐⭐⭐⭐ | ✅ |
| **托管类型支持** | ⭐⭐⭐⭐⭐ | 高 | ⭐⭐⭐⭐⭐ | ✅ |
| **Godot 场景桥接** | ⭐⭐⭐ | 低 | ⭐⭐⭐⭐ | ✅ |
| **AOT 兼容** | ⭐⭐⭐ | 中 | ⭐⭐⭐⭐⭐ | ✅ |
| **关系型状态机** | ⭐⭐⭐⭐ | 中 | ⭐⭐⭐⭐ | ✅ |
| **事件总线** | ⭐⭐⭐ | 低 | ⭐⭐⭐⭐ | ✅ |

### 22.6 核心原则

```
EntJoy 的核心价值：
  1. 高性能（C++/ISPC 编译）
  2. 易用（C# API）
  3. 兼容（.NET 生态）
  4. 无头（不绑定特定引擎）

不做：
  1. 游戏引擎功能（编辑器、可视化）
  2. 开发工具（协作、AI）
  3. 业务逻辑（状态机、事件系统）← 这些由用户在引擎层实现
```

---

## 二十三、实用创新功能（2026-08 聚焦）

> 本节聚焦于可行性和实用性最高的创新功能，解决其他框架的历史包袱。

### 23.1 核心痛点（必须解决）

#### 痛点 1：Component 定义太啰嗦

```csharp
// Unity DOTS：每个都要写接口
public struct Position : IComponentData { public float X, Y; }

// EntJoy：直接用 struct
public struct Position { public float X, Y; }
// SourceGenerator 自动生成注册代码
```

**可行性**：✅ 高（SourceGenerator 已有基础）
**实用性**：⭐⭐⭐⭐⭐（每个 Component 都受益）

#### 痛点 2：查询太啰嗦

```csharp
// 当前：
var query = new QueryBuilder()
    .WithAll<Position, Velocity>()
    .WithNone<Dead>()
    .Build();

// 改进：
var query = world.Query<Position, Velocity>()
    .Without<Dead>();
```

**可行性**：✅ 高（扩展 QueryEnumerable）
**实用性**：⭐⭐⭐⭐⭐（每次查询都受益）

#### 痛点 3：手动 Schedule 太麻烦

```csharp
// 当前：
var job = new MovementJob { dt = 0.016f };
var handle = world.Schedule(job);
handle.Complete();

// 改进：
world.Schedule<MovementJob>(new { dt = 0.016f });
// 自动等待完成
```

**可行性**：✅ 高（Phase 4 Schedule Graph）
**实用性**：⭐⭐⭐⭐⭐（每次调度都受益）

### 23.2 性能痛点（必须解决）

#### 痛点 4：手动 SIMD

```csharp
// 其他框架：手写 SIMD
for (int i = 0; i < count; i += 8) {
    var pos = positions.GetSubArray(i, 8);
    var vel = velocities.GetSubArray(i, 8);
    SIMD.Add(pos, vel, dt);
}

// EntJoy：自动 SIMD
[AutoSIMD]
public void Move(ref Position pos, in Velocity vel) {
    pos.X += vel.X * dt;
    pos.Y += vel.Y * dt;
}
// NativeTranspiler 自动生成 SIMD 代码
```

**可行性**：✅ 高（NativeTranspiler 已有基础）
**实用性**：⭐⭐⭐⭐⭐（性能提升 4-8x）

#### 痛点 5：手动并发控制

```csharp
// 其他框架：手动声明依赖
[UpdateAfter(typeof(MovementSystem))]
public class CollisionSystem : ISystem { ... }

// EntJoy：自动分析
world.AddSystem<MovementSystem>(writes: typeof(Position));
world.AddSystem<CollisionSystem>(reads: typeof(Position));
// 框架自动分析冲突，自动安排执行顺序
```

**可行性**：✅ 高（Phase 4 Schedule Graph）
**实用性**：⭐⭐⭐⭐⭐（自动并行，无需手动管理）

#### 痛点 6：每帧遍历所有实体

```csharp
// 其他框架：每帧遍历
foreach var (pos) in world.Query<Position>()) {
    // 即使没变化也遍历
}

// EntJoy：只处理变化的
foreach var (pos) in world.Query<Position>().Changed()) {
    // 只处理这帧变化过的
}
```

**可行性**：✅ 高（Chunk 级 bitmask）
**实用性**：⭐⭐⭐⭐⭐（减少无用遍历）

### 23.3 开发痛点（应该解决）

#### 痛点 7：实体创建太啰嗦

```csharp
// 当前：
var entity = world.CreateEntity();
world.AddComponent(entity, new Position { X = 1, Y = 2 });
world.AddComponent(entity, new Velocity { X = 0, Y = 1 });

// 改进：
var entity = world.Spawn()
    .With(new Position { X = 1, Y = 2 })
    .With(new Velocity { X = 0, Y = 1 })
    .Build();
```

**可行性**：✅ 高（Entity Builder）
**实用性**：⭐⭐⭐⭐（减少样板代码）

#### 痛点 8：Component 访问不安全

```csharp
// 当前：运行时可能失败
var pos = world.GetComponent<Position>(entity);

// 改进：安全访问
var pos = world.GetComponentOrDefault<Position>(entity, new Position());
// 不会失败，返回默认值
```

**可行性**：✅ 高（扩展 API）
**实用性**：⭐⭐⭐⭐（减少运行时错误）

#### 痛点 9：批量操作太慢

```csharp
// 当前：逐个操作
for (int i = 0; i < 1000; i++) {
    world.AddComponent(entities[i], new Health { Value = 100 });
}

// 改进：批量操作
world.AddComponents<Health>(entities, e => new Health { Value = 100 });
```

**可行性**：✅ 高（扩展 EntityManager）
**实用性**：⭐⭐⭐⭐（减少调用次数）

#### 痛点 10：调试困难

```csharp
// 当前：信息不足
"Entity does not have component Position"

// 改进：详细信息
"Entity #42 does not have component Position
 Archetype: [Health, Name]
 已创建 1,234,567 个 Entity"
```

**可行性**：✅ 高（增强错误信息）
**实用性**：⭐⭐⭐⭐（减少调试时间）

### 23.4 集成痛点（应该解决）

#### 痛点 11：AOT 不兼容 ✅ 已解决（2026-08-26）

```csharp
// 现状：动态反射已全消除
// 旧问题：typeof(T).MakeGenericMethod(...);  // ❌ AOT 不兼容
// 方案：非泛型指针路径（AddComponentRaw/SetRaw）+ 静态泛型委托缓存（ParallelForBatchDelegateCacheFor<T>）
// 复核（2026-08-29）：src/ 全库动态反射零命中
```

**可行性**：✅ 已完成（非泛型指针路径 + 静态泛型缓存，未用 SourceGenerator）
**实用性**：⭐⭐⭐⭐⭐（iOS/主机/Godot AOT 路径安全）

#### 痛点 12：后端切换困难

```csharp
// 当前：固定后端

// 改进：运行时切换
world.SetBackend(Backend.Cpp);
// 或
world.SetBackend(Backend.Ispc);
// 自动降级
```

**可行性**：✅ 高（已有多后端）
**实用性**：⭐⭐⭐⭐（跨平台灵活）

#### 痛点 13：迁移困难

```csharp
// 改进：自动迁移工具
[MigrationFrom("Unity.Entities")]
public struct Position : IComponentData {
    public float3 Value;
}

// 自动生成 EntJoy 等价代码
```

**可行性**：✅ 中（SourceGenerator 转换）
**实用性**：⭐⭐⭐⭐⭐（降低迁移成本）

### 23.5 实用功能总结

| 功能 | 可行性 | 实用性 | 价值 | 优先级 |
|------|--------|--------|------|--------|
| **零接口 Component** | ✅ 高 | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| **查询缓存** | ✅ 高 | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| **自动调度** | ✅ 高 | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| **自动 SIMD** | ✅ 高 | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| **自动并发** | ✅ 高 | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| **变更追踪** | ✅ 高 | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| **Entity Builder** | ✅ 高 | ⭐⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐⭐ |
| **安全访问** | ✅ 高 | ⭐⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐⭐ |
| **批量操作** | ✅ 高 | ⭐⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐⭐ |
| **增强错误** | ✅ 高 | ⭐⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐⭐ |
| **AOT 兼容** | ✅ 高 | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| **后端切换** | ✅ 高 | ⭐⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐⭐ |
| **迁移工具** | ✅ 中 | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ |

### 23.6 不实用的（放弃）

| 功能 | 原因 |
|------|------|
| ❌ IDE 功能 | 这是 IDE 的职责 |
| ❌ 交互式文档 | 不是框架核心 |
| ❌ 代码模板 | 编辑器功能 |
| ❌ 重构工具 | 编辑器功能 |
| ❌ 自动文档 | Nice to have，不是必须 |

---

## 二十四、热重载分析（2026-08 深入）

> 本节分析热重载的可行性、限制和实现方式。

### 24.1 热重载的本质

```
热重载 = 运行时替换代码，保留数据

关键区分：
  - 代码（System）→ 可以热重载
  - 数据（Component）→ 不能热重载（会破坏数据）
```

### 24.2 两种 System 的对比

| 特性 | C# System | C++ System (NativeTranspiler) |
|------|-----------|------------------------------|
| **性能** | 一般 | 快 3-10x |
| **SIMD** | 手动 | 自动 |
| **热重载** | ❌ 不支持 | ✅ 支持 |
| **调试** | 简单 | 较难 |
| **AOT 兼容** | 部分 | 完全 |

### 24.3 为什么 C# System 不能热重载

```
C# 编译流程：
  C# → IL → JIT/AOT → 机器码

  IL 加载到 CLR 后：
    - CLR 管理代码执行
    - 不能运行时替换 IL
    - AOT 模式更不可能

C++ 编译流程：
  C# → NativeTranspiler → C++ → DLL → 机器码

  DLL 加载后：
    - 可以卸载旧 DLL
    - 可以加载新 DLL
    - 更新函数指针即可
```

### 24.4 热重载工作流

```
用户使用热重载的前提：
  1. System 必须用 [NativeTranspile] 标记
  2. 框架自动编译成 C++
  3. 运行时可以重载 C++ DLL

用户不需要手动做什么：
  - 写 C# 代码
  - 标记 [NativeTranspile]
  - 框架自动处理热重载
```

### 24.5 热重载的限制

```
可以热重载：
  ✅ System 逻辑（Execute 方法）
  ✅ System 参数（Job 字段）
  ✅ 添加新 System
  ✅ 删除 System

不能热重载：
  ❌ Component 结构（会破坏数据布局）
  ❌ 添加新 Component 类型（会破坏 Archetype）
  ❌ 修改 Component 字段顺序（会破坏内存布局）
```

### 24.6 热重载的价值

```
游戏开发迭代速度：
  传统：修改代码 → 重新编译 → 重启游戏 → 加载数据 → 继续测试
         10-30 秒

  热重载：修改代码 → 自动重载 → 继续测试
         1-3 秒

  提升：10x 迭代速度
```

### 24.7 热重载总结

| 功能 | 可行性 | 实用性 | 价值 |
|------|--------|--------|------|
| **System 热重载** | ✅ 高 | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| **Component 热重载** | ❌ 不可能 | N/A | N/A |
| **自动检测变化** | ✅ 高 | ⭐⭐⭐⭐ | ⭐⭐⭐⭐ |
| **版本化 Component** | ✅ 中 | ⭐⭐⭐⭐ | ⭐⭐⭐⭐ |

**核心洞察**：
- System 是代码 → 可以热重载
- Component 是数据 → 不能热重载
- EntJoy 的 NativeTranspiler 使 System 热重载成为可能

---

## 二十五、突破 Blittable 瓶颈：最终架构（2026-08 深入）

> 本节记录如何让用户在 Component 中使用 string/List<T>/class，同时保持 C++ 性能。

### 25.1 各框架如何处理托管类型（参考依据）

| 阵营 | 框架 | 方式 | 代价 |
|------|------|------|------|
| **纯托管 C#** | Arch、Morpeh、Friflo class 组件 | string/List/class 直接进组件 | 整个 ECS 无 C++/Burst 加速 |
| **Rust** | Bevy | 任意类型（String/Vec/HashMap）直接进 archetype 列 | 零 GC，C# 物理做不到 |
| **混合** | Unity DOTS | managed 组件只能主线程，Job 不能碰 | 用户手写 FixedString/NativeList |

**参考来源**：
- Unity 官方：[AddComponentObject](https://docs.unity.cn/Packages/com.unity.entities%401.0/api/Unity.Entities.EntityManager.AddComponentObject.html) — managed 对象可作组件，但只主线程
- Unity 社区：[Managed components in parallel jobs](https://discussions.unity.com/t/managed-components-in-parallel-jobs/903590/3) — 并行 Job 不能碰托管组件
- Friflo：[component types](https://github.com/friflo/Friflo.Engine.ECS) — struct 组件（blittable）+ class 组件（托管）分开存
- Arch：[components](https://deepwiki.com/genaray/Arch/2.2-components) — struct 组件在托管数组，非 blittable 也能用
- Morpeh：[core architecture](https://deepwiki.com/scellecs/morpeh/2-core-ecs-architecture) — 纯 C#，托管友好
- Bevy：[component.rs](https://docs.rs/bevy_ecs/0.16.0-rc.1/x86_64-apple-darwin/src/bevy_ecs/component.rs.html) — 任意类型直接存 archetype 列
- 对比表：[CSharpECSComparison](https://github.com/Chillu1/CSharpECSComparison#1)

### 25.2 关键约束（物理边界）

| 场景 | 结论 | 依据 |
|------|------|------|
| 并行 Job 内访问托管 | ❌ 不允许/极危险 | Unity DOTS 行业验证 |
| 托管数据在热路径 | ❌ 速度上限 = C# 本身速度 | GC 堆 + 引用图客观事实 |
| string 在热路径比较 | ✅ 用 Interned ID（int） | 编译期哈希，零 GC |
| List<blittable> 在 Job 内 | ✅ Pin backing array，直接指针 | 原生内存不移动 |
| Dictionary/嵌套 class 图 | ✅ 回调法（同步点/主线程 C#） | C# 内置结构，只能 C# 访问 |

### 25.3 有界 Pin 的 STW 风险

```
大规模 Pin（100K 对象）：
  → 阻止 GC 压缩 → 堆碎片化
  → Gen0 对象被 Pin → 强制晋升 Gen1/Gen2 → STW 累积

GCHandlePool 有界 Pin（≤128 活跃）+ 池化 buffer（复用 Gen2 对象）：
  → 减少压缩阻力 + 避免晋升问题
  → STW 风险可控（参考 .NET [PinnedHeap 设计](https://raw.githubusercontent.com/dotnet/coreclr/master/Documentation/design-docs/PinnedHeap.md#1)）
```

### 25.4 分层字段路由架构

用户 class 任意定义，SourceGenerator 逐字段分类：

```csharp
class PlayerData {                  // 用户写（一行不改）
    public float Health;                    // L0 → Blittable SoA / 直接指针
    public string Name;                     // L1 → Interned ID（int）
    public List<int> Items;                 // L2 → backing array 指针（有界 Pin）
    public Dictionary<string,float> Stats;  // L3 → 批量回调（主线程 C#）
    public OtherData Ref;                   // L3 → 批量回调（主线程 C#）
}
```

| 层 | 字段类型 | Job 内直接操作？ | 方式 |
|----|---------|----------------|------|
| L0 | float/int/Vector3 等 Blittable | ✅ 1.7ns | SoA / 指针 |
| L1 | string | ✅ 只读 int 比较 | Interned ID |
| L2 | List<blittable> 元素 | ✅ 直接读写 | Pin backing array（有界） |
| L3 | Dictionary/嵌套 class 图 | ❌ 同步点 C# | 回调法 / 主线程批量 |

### 25.5 "继续 Job"的适用范围

```
✅ 能拆（托管依赖可提升为数据）：
  C++ Job 记录请求 → Complete() → 主线程 C# 批量处理 → 原生结果数组 → 继续 Job B
  例：if (dict.Get("crit") > 0.3) → 请求 + 回填 + 重评估

❌ 不能拆（依赖链交错深嵌套）：
  托管访问在 if 里且内部还有更多托管访问 → 拆不开
  → 低频：内联回调（~12ns/次，串行防竞态）
  → 或：这段逻辑留在 C# 不转译（托管逻辑的天然归宿）
```

### 25.6 最终架构

```
Job 内（并行 C++，纯指针）：
  Blittable + interned int → SIMD
  List backing array → 直接指针（有界 Pin）
  字符串比较 → int 比较（零 Pin）
  
需要 Dictionary / 嵌套图？
  → 写原生请求缓冲区（并行安全）或低频内联回调

Complete()（同步点）← Unity DOTS 行业验证的标准模式

主线程 C#（托管访问的物理上限）：
  Dictionary / 嵌套 class / string 修改
  → 纯 C# 速度（~30-80ns/次）

可选再调度 C++ Job 用结果
```

### 25.7 与 Unity DOTS 的差异

| 维度 | Unity DOTS | EntJoy |
|------|-----------|--------|
| 托管组件访问 | 用户手写 `.AsNativeArray()` 等 | SourceGenerator 自动路由 |
| List<int> 在 Job 内 | 必须迁移到 NativeList | 直接用 backing array 指针 |
| string | FixedString（手写） | Interned ID（自动生成） |
| Dictionary | 只能主线程 | 同（批量化 + 自动封装） |
| 用户感知 | 明确知道哪些快哪些慢 | 自动分层，无感 |
