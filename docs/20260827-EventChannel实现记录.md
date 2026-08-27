# 20260827：Event Channel 实现记录（含 NativeTranspile SendEvent）

> **范围**：EntJoy.ECS 事件通道（Event Channel）完整实现 —— 双缓冲 EventStream、Managed/Native Job SendEvent、
> NativeTranspiler C++ EventBuffer 自动翻译、多 World 支持、异步自动 drain。
> **关联文档**：`Phase优先级分析与实施路线.md`（S18 World Events）、`20260825-ChangeTracking与Enabled优化记录.md`

---

## 一、设计目标

**零结构变更的系统间事件传递**，替代 One-Frame Component（每帧 add/remove 组件 = 2 次结构变更，1000 事件 ≈ 20ms）。

| 方案 | 结构变更 | 性能 | 说明 |
|------|:-------:|------|------|
| One-Frame Component | ✅ 每帧 2 次 | ⭐⭐ | 反模式（事件实体多时开销大） |
| **Event Channel（本实现）** | ❌ 无 | ⭐⭐⭐⭐⭐ | 纯内存双缓冲 |

---

## 二、核心设计

### 2.1 EventStream<T>（双缓冲）

```
帧 N：SendEvent 写 buffer[0]（writeBuffer），writeCount++
帧末：NextFrame swap，writeCount → readCount，writeCount = 0
帧 N+1：ReadBuffer 读 buffer[1]（readBuffer），取 readCount 条
```

- `SendEvent(in T)`：Interlocked 原子写（多线程安全）
- `ReadBuffer()`：返回 `ReadOnlySpan<T>`（上一帧事件）
- `NextFrame()`：swap + 计数转移 + generation++
- `DrainFromBuffer(void* dataPtr, int count)`：从 Native EventBuffer 批量拷贝

### 2.2 事件类型约束

```csharp
SendEvent<T>() where T : unmanaged  // 必须 blittable
```

- **Managed 事件**（string/class/Dictionary）在 NativeTranspile Job 中编译报错（NT015）
- 需要复杂数据 → 用 Entity 引用，或 C# 侧构造完整事件
- 典型事件：`struct CollisionEvent { Entity A; Entity B; float Force; }`

### 2.3 用户 API

```csharp
// 发送
world.SendEvent(new DamageEvent { Target = e, Amount = 10 });   // Managed 任意处
EventBus.SendEvent(new DeathSignal { ... });                     // NativeTranspile Job 内（using static）

// 读取（每帧一次，无需 reader 对象）
world.NextFrameEvents();
foreach (var evt in world.GetEventStream<DamageEvent>().ReadBuffer()) { ... }
```

---

## 三、NativeTranspile SendEvent 实现

### 3.1 翻译流程

```
[C# 用户代码]                        [NativeTranspiler 编译时]
  EventBus.SendEvent(new DeathSignal  → CppChunkStatementTranslator 检测 SendEvent<T>
    { Target = e, Amount = 10 })      → 生成 C++ EventBuffer 写入代码
                                       → 收集事件类型（CollectSendEventTypes）
                                       → BindingsGenerator 生成元数据注册
```

### 3.2 C++ 生成的代码

```cpp
// Execute 内 SendEvent<T> 翻译为：
{
    auto* buf = ((__EntJoyEventBuffer**)__header->eventBufferHeaders)[0];
    if (buf != nullptr) {
        int idx = INTERLOCKED_ADD_AND_FETCH32(buf->count, 1) - 1;
        if (idx < buf->capacity) {
            ((DeathSignal*)buf->data)[idx] = { entities_ptr[i], amount };
        }
    }
}
```

**关键点**：
- `INTERLOCKED_ADD_AND_FETCH32` 是跨编译器原子宏（非 `&buf->count`！后者会递增字段地址）
- `eventBufferHeaders` 是指针数组（`__EntJoyEventBuffer*[]`），C# 侧分配独立 header 内存
- 空指针保护（`__header`/`buf` 判空）

### 3.3 EventBuffer 内存布局

```
context header（ChunkContextHeader）
  ├─ eventBufferCount: int
  ├─ eventBufferHeaders: __EntJoyEventBuffer*[]（指针数组）
  │    └─ [0] → EventBufferHeader { dataPtr, countPtr, capacity, elementSize }
  └─ eventWorldHandle: GCHandle → World（cleanup 时自动 drain）
```

### 3.4 事件类型收集

- `CppJobGenerator.CollectSendEventTypes`：从 Execute AST 收集 SendEvent 泛型参数 / new 表达式类型
- `CollectJobStructIncludes`：为事件类型生成 include（`DeathSignal.h`）
- BindingsGenerator：内联注册 `ChunkJobScheduler.RegisterEventBufferMeta(jobType, types)`
- 事件类型头文件（嵌套类型也能生成：`EntJoySample_ECS_NativeEventJobTest_DeathSignal.h`）

---

## 四、Drain 时机（事件回读）

| 路径 | 时机 | 触发者 |
|------|------|--------|
| **Sync (Run)** | C++ 执行完立即 | `DrainAndFreeEventBuffers`（同步调用） |
| **Async (Schedule)** | C++ 执行完 | `ChunkCleanup` 回调自动 drain |

**Async 自动 drain 流程**：
```
C++ worker 执行完
  └─ ChunkCleanup 回调（C++ → C#）
      ├─ 从 context 读 World（eventWorldHandle GCHandle）
      ├─ DrainEventBuffersFromCleanup → World.EventStream
      ├─ 释放 EventBuffer 内存（dataPtr/countPtr）
      ├─ 释放指针数组 + World GCHandle
      └─ 事件可读（无需手动 drain）
```

**防双释放**：sync 路径 drain 后清掉 `eventWorldHandle`/`eventBufferHeaders`（置零），ChunkCleanup 检测到清零跳过。

---

## 五、多 World 支持

| 路径 | 支持方式 |
|------|---------|
| **Managed** | 天然：`world1.SendEvent` / `world2.SendEvent` 写各自 `_eventStreams` |
| **Native** | `job.Run(query, world)` / `job.Schedule(query, world)` 显式传 World |

**World 参数贯穿全链路**：
```
job.Run(query, world) 或 job.Schedule(query, world)
  → 扩展方法（ECSSourceGenerator 生成）→ NativeExports.Schedule_X(..., world)
  → ChunkJobScheduler.ScheduleNativeEntityBatchRawCore(..., world)
  → CreateChunkContextBlock(..., world) → eventWorldHandle = GCHandle(world)
  → drain 时从 context 读 world → 写回正确 EventStream
```

所有 Schedule/Run 扩展方法签名：`(job, query, [workerCap, rangeSize,] world = null, [dependsOn])`，默认 `world ??= World.DefaultWorld`。

---

## 六、改动文件清单

**src/EntJoy.ECS**
- `Event/EventStream.cs`（新增）：双缓冲事件流
- `Event/EventBuffer.cs`（新增）：Native EventBuffer + POD header
- `Event/EventBus.cs`（新增，原 ECS.cs）：静态 SendEvent 入口（using static）
- `World/World.cs`：RegisterEvent/SendEvent/GetEventStream/NextFrameEvents/DrainNativeEvents
- `System/SystemRunner.cs`：Update() 末尾调用 NextFrameEvents()
- `JobSystem/ChunkJobScheduler.cs`：EventMetaCache/LiveEventBuffers/AllocateEventBuffers/Drain/自动 drain
- `JobSystem/NativeChunkJobs.cs`：ChunkContextHeader + eventBufferCount/Headers/WorldHandle + ChunkCleanup 自动 drain
- `Entity/EntityManager.cs`：pending native events（早期方案，后改为 cleanup 自动 drain）
- `Chunk/ChunkJobExtensions.cs`：Schedule/Run 加 World 参数

**src/NativeTranspiler**
- `Analyzer/Cpp/CppChunkStatementTranslator.cs`：SendEvent 检测 + C++ 生成（语法匹配 + 空指针保护）
- `Analyzer/Cpp/CppJobGenerator.cs`：EventBuffer POD + header struct + CollectSendEventTypes + 独立函数 __header 参数
- `Analyzer/Common/BindingsGenerator.cs`：元数据注册 + world 参数
- `Analyzer/Common/NativeTranspileValidator.cs`：SendEvent 特放 + NT015 托管事件报错
- `Analyzer/Common/Config.cs`：TypeEventBus/TypeWorld/TypeEntityManager/TypeArchetypeChunk 常量
- `Analyzer/NativeTranspilerGenerator.cs`：事件类型头文件收集

**src/EntJoy.ECS.SourceGenerator**
- `IJobEntitySourceGenerator.cs`：Schedule/Run 加 World 参数

**samples/EntJoySample/09_ECS**
- `EventChannelDemo.cs`（新增）：5 测试（单/多消费者/NextFrame/溢出/多帧）
- `EventChannelJobTest.cs`（新增）：Managed IJobChunk → SendEvent
- `NativeEventJobTest.cs`（新增）：Native 单事件/多事件/多 World/Async

---

## 七、测试结果（10/10 PASS）

```
EventChannelDemo:       5/5  PASS（双缓冲、多消费者、NextFrame 清空、容量溢出、多帧）
EventChannelJobTest:    1/1  PASS（Managed IJobChunk → SendEvent）
NativeEventJobTest:
  Test 1: 单事件端到端   PASS（5 events）
  Test 2: 多事件类型     PASS（3 Death + 4 Damage 分流）
  Test 3: 多 World       PASS（world1=5, world2=3 隔离）
  Test 4: Async 自动 drain PASS（Schedule + Complete 后自动回读 5 events）
```

---

## 八、注意事项 / 遗留项

### 8.1 已知限制

| 项 | 说明 |
|----|------|
| 事件实体数组读取 | Native 测试中 `Target.Id` 均为 0（C++ 读实体数组的第一个元素），count 正确，Target 字段待验证 |
| Async 自动 drain 的线程安全 | ChunkCleanup 在 C++ worker 线程触发，EventStream.DrainFromBuffer 用 Interlocked 保证写安全；主线程 ReadBuffer 需在 Complete 后 |
| EventStream 容量 | 默认 1024，溢出丢弃（返回 false） |
| 事件类型必须 unmanaged | managed 事件在 Native Job 编译报错（NT015） |

### 8.2 使用约定

- **Managed 代码**：`world.SendEvent(new T{...})`，任意线程（Interlocked）
- **NativeTranspile Job**：`EventBus.SendEvent(new T{...})`（需 `using static EntJoy.ECS.EventBus`）
- **读取**：每帧一次 `world.NextFrameEvents()` 后 `world.GetEventStream<T>().ReadBuffer()`
- **多 World**：`job.Run(query, world)` 显式指定
