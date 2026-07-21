# JobSystem 下一步优化实施计划

> **执行要求：** 按任务顺序实施，每个任务独立完成 Red → Green → 回归验证。禁止同时修改两个性能变量。

**目标：** 在保留已修复 Assist 生命周期的前提下，把 C#、C++、ISPC 的 IJobChunk/IJobEntity 统一迁移到可诊断的连续本地分区和半量窃取路径，并最终以单一原生 WorkerPool 替换 Taskflow。

**架构依据：** [Unity风格JobSystem架构优化方案B.md](./Unity风格JobSystem架构优化方案B.md)

**技术栈：** C++20、Taskflow（迁移期旧执行器）、CMake/MSVC Native Tests、C#/.NET 9、Native Transpiler、Windows semaphore/atomic wait。

## 全局约束

- Sleep 必须保持在 Heavy 后执行。
- 性能判断只看用户机器结果；自动执行 EXE 只验证退出码、校验结果和诊断信息。
- 当前 Assist 修复是基线，不得恢复 reader 归零直接删除 `BatchState` 的行为。
- `workerCap=8` 时，参与任务、初始 ticket 和 permit 均不得超过 8。
- `IJobChunk.Execute()` 对一个物理 Chunk 只能调用一次。
- 不允许通过关闭 Trace、写死统计为零或永久自旋获得性能数字。
- 第一阶段不修改 ECS 物理 Chunk，不做硬 affinity，不引入第二个同时运行的线程池。
- 每个任务完成后必须先通过正确性和诊断门槛，再进行性能 A/B。
- 当前工作树有用户/Claude 未提交改动；提交时只暂存任务明确列出的文件。

## 当前基线

### 已完成

- Assist/Worker 并发释放已修复。
- `AssistLifetimeTests` 修复前稳定触发 `0xC0000005`，修复后通过。
- 默认 100 帧 Light、Heavy、Sleep 完整运行退出码为 0，结果校验为 OK。

### 尚未完成

- 普通 C#/C++ IJobChunk 仍走全局 `nextRange`。
- IJobEntity 仍走全局 `nextRange`。
- `workerCap=8` 时普通 C++ IJobChunk 每批仍提交 15 个 Taskflow task。
- Tile 路径目前主要只覆盖 ISPC IJobChunk。
- Trace 只有 Publish：`events=1`、`result=INCOMPLETE`。
- `workersPeak=0`、`workerClaims=0` 与 `workerRanges=3100` 矛盾。
- `assistPct` 可能超过 100%，统计口径错误。
- 完整 Native Tests 停在 `FAIL execution began before publication`。

---

## Task 1：恢复可信测试与 Trace 生命周期

**目的：** 在继续优化前，让测试和日志能够证明 Publish、Claim、Execute、Finalize、Complete 的真实顺序。

**文件：**

- 修改：`src/NativeDll/JobSystem.cpp`
- 修改：`src/NativeDll/JobSystem.h`
- 修改：`src/NativeDll.Tests/JobSystemTests.cpp`
- 保留：`src/NativeDll.Tests/AssistLifetimeTests.cpp`

**接口：**

```cpp
void PushTraceEvent(
    TraceEventType type,
    uint64_t batchId,
    int rangeIndex,
    int start,
    int count) noexcept;
```

- [ ] **1.1 先运行现有 Trace 测试，记录 Red**

```powershell
cmake -S src/NativeDll.Tests -B src/NativeDll.Tests/build
cmake --build src/NativeDll.Tests/build --config Release --target JobSystemTests
& src/NativeDll.Tests/build/Release/JobSystemTests.exe
```

预期当前失败：

```text
FAIL execution began before publication
```

- [ ] **1.2 在测试中明确生命周期约束**

每个有 `batchId` 的批次必须满足：

```text
Publish < Claim
Claim < ExecuteBegin
ExecuteBegin < ExecuteEnd
最后一个 ExecuteEnd < Finalize
Finalize <= Complete
```

测试不得仅检查事件存在，还必须按同一 `batchId` 比较时间戳顺序。

- [ ] **1.3 在真实热路径补齐事件**

旧 Range 和新 Tile 路径都必须发送：

```cpp
PushTraceEvent(TraceEventType::Claim, batch->diagnosticId,
    logicalIndex, begin, count);
PushTraceEvent(TraceEventType::ExecuteBegin, batch->diagnosticId,
    logicalIndex, begin, count);
// callback
PushTraceEvent(TraceEventType::ExecuteEnd, batch->diagnosticId,
    logicalIndex, begin, count);
```

`TryFinalizeCompletedBatch()` 在 cleanup 前后发送 Finalize/Complete；Publish 必须在任务对 Worker 可见之前发送。

- [ ] **1.4 验证 Trace 和 Assist 测试**

```powershell
cmake --build src/NativeDll.Tests/build --config Release --target JobSystemTests AssistLifetimeTests
& src/NativeDll.Tests/build/Release/AssistLifetimeTests.exe
& src/NativeDll.Tests/build/Release/JobSystemTests.exe
```

通过门槛：

```text
PASS AssistLifetime
PASS TraceLifecycleOrder
所有 Native Tests 退出码 0
```

---

## Task 2：修正统计定义，禁止“假诊断”

**目的：** 让每个数字具有唯一口径，并能从总量守恒关系发现调度错误。

**文件：**

- 修改：`src/NativeDll/JobSystem.h`
- 修改：`src/NativeDll/JobSystem.cpp`
- 修改：`src/NativeDll/Exports.h`
- 修改：`src/NativeDll/Exports.cpp`
- 修改：`src/EntJoy/JobSystem/NativeJobScheduler.cs`
- 修改：`src/EntJoySample/02_IJobChunkECS/IJobChunkMoveCompareTest/IJobChunkMoveCompareSample.cs`
- 修改：`src/NativeDll.Tests/JobSystemTests.cpp`

**统一统计字段：**

```cpp
uint64_t batchesPublished;
uint64_t participantTasksSubmitted;
uint64_t workerTargetTotal;
uint64_t localTiles;
uint64_t stolenTiles;
uint64_t assistTiles;
uint64_t stealAttempts;
uint64_t stealSuccesses;
uint64_t permitsReleased;
uint64_t parkWakes;
uint64_t activeWorkersPeak;
```

- [ ] **2.1 编写统计守恒失败测试**

测试 31 Tile、`workerCap=8`、100 批次，断言：

```cpp
Require(stats.batchesPublished == 100, "wrong batch count");
Require(stats.participantTasksSubmitted == 800, "worker cap ignored");
Require(stats.localTiles + stats.stolenTiles + stats.assistTiles == 3100,
    "tile accounting does not reconcile");
Require(stats.activeWorkersPeak <= 8, "too many active workers");
```

- [ ] **2.2 删除写死为零的运行时字段**

如果当前 Taskflow 无法提供 `parkWakes` 或 `permitsReleased`，字段必须显式标记为“不适用于 Taskflow”，而不是伪装成真实的 0。日志使用：

```text
parkWakes=N/A
permitsReleased=N/A
```

- [ ] **2.3 修正 Assist 百分比**

当前 `assistExecuted / assistAttempts` 不是百分比，因为一次 Attempt 可执行多个 Tile。改为：

```text
assistTilePct = assistTiles * 100 / totalTiles
```

数值必须位于 `[0, 100]`。

- [ ] **2.4 验证统计 ABI**

C++ `Exports.h` 和 C# `NativeJobSystemStats` 字段顺序、大小完全一致；新增字段只追加到结构尾部，并增加结构大小断言。

通过门槛：

```text
localTiles + stolenTiles + assistTiles == totalTiles
assistTilePct <= 100%
日志不再同时出现 workerRanges>0 与所有 Worker 指标为 0
```

---

## Task 3：统一 C#、C++、ISPC 的 Tile 执行路径

**目的：** 消除“只有 ISPC IJobChunk 走 Tile”的分裂，使三种回调适配器共享一个调度协议。

**文件：**

- 修改：`src/NativeDll/JobSystem.cpp`
- 修改：`src/NativeDll.Tests/JobSystemTests.cpp`
- 检查：`src/EntJoy/JobSystem/NativeJobScheduler.cs`
- 检查：`src/NativeDll/Exports.cpp`

**Tile 描述：**

```cpp
enum class TileKind : uint8_t
{
    ChunkCallbacks,
    ChunkRange,
    EntityBatchRange
};

struct ExecutionTile
{
    uint32_t firstItem;
    uint32_t itemCount;
    TileKind kind;
};
```

**统一执行入口：**

```cpp
static bool ExecuteTile(void* raw, const ExecutionTile& tile) noexcept
{
    auto& context = *static_cast<ChunkBatchContext*>(raw);
    switch (tile.kind)
    {
    case TileKind::ChunkCallbacks:
        for (uint32_t i = 0; i < tile.itemCount; ++i)
            context.func(context.originalContext,
                &context.chunks[tile.firstItem + i]);
        return true;
    case TileKind::ChunkRange:
        context.rangeFunc(context.originalContext, context.chunks,
            static_cast<int>(tile.firstItem), static_cast<int>(tile.itemCount));
        return true;
    case TileKind::EntityBatchRange:
        context.entityRangeFunc(context.originalContext, context.entityBatches,
            static_cast<int>(tile.firstItem), static_cast<int>(tile.itemCount));
        return true;
    }
    return false;
}
```

- [ ] **3.1 为三个入口编写 exact-once 失败测试**

分别调用：

```cpp
Scheduler::ScheduleChunks(...);
Scheduler::ScheduleChunkRanges(...);
Scheduler::ScheduleEntityBatches(...);
```

对每个 Chunk/Batch 使用原子命中计数，断言每项恰好执行一次、cleanup 恰好一次。

- [ ] **3.2 让三个入口都构建 `ExecutionTile[]`**

删除以下分支含义：

```cpp
if (execMode == 0 && func) usePartitions();
else useGlobalNextRange();
```

改为所有 Chunk/Entity Batch 调度都设置：

```cpp
batch->executeTile = &ExecuteTile;
batch->tiles = tiles;
batch->tileCount = tileCount;
batch->partitions = partitions;
```

- [ ] **3.3 保持 IJobChunk 语义**

物理 Chunk 不得拆成多个用户可观察的 `IJobChunk.Execute()` 调用。Tile 可以组合相邻 Chunk，但适配器内部仍逐 Chunk 调用一次。

- [ ] **3.4 暂时保留旧路径用于 A/B**

使用内部枚举而不是环境参数散落在代码中：

```cpp
enum class BatchSchedulerPath : uint8_t
{
    LegacyGlobalCursor,
    LocalPartitions
};
```

测试可以选择路径；公开 API 默认仍在该任务完成后切换到 `LocalPartitions`。

通过门槛：

```text
C# IJobChunk、C++ IJobChunk、ISPC IJobChunk 都报告 local/stolen/assist Tile
IJobEntity 同样不再使用 nextRange
所有 Verify = OK
```

---

## Task 4：严格落实 `workerTarget` 和 `workerCap`

**目的：** `workerCap=8` 时绝不再提交 15 个参与任务。

**文件：**

- 修改：`src/NativeDll/JobSystem.cpp`
- 修改：`src/NativeDll.Tests/JobSystemTests.cpp`

**统一解析函数：**

```cpp
static int ResolveWorkerTarget(int workerCap, int tileCount) noexcept
{
    const int cap = workerCap > 0 ? workerCap : g_numThreads;
    return std::max(1, std::min({ cap, g_numThreads, tileCount }));
}
```

- [ ] **4.1 编写 WorkerCap 参数化测试**

测试 `workerCap = 1, 2, 8, 15`，以及 `tileCount < workerCap`。断言：

```cpp
participantTasksSubmitted == min(workerCap, workerCount, tileCount)
partitionCount == participantTasksSubmitted
activeWorkersPeak <= participantTasksSubmitted
```

- [ ] **4.2 `SubmitBatch()` 只读取 Batch 中已解析的 target**

禁止再次使用：

```cpp
std::min(g_numThreads, batch->rangeCount)
```

改为：

```cpp
const int participantCount = static_cast<int>(batch->partitionCount);
for (int slot = 0; slot < participantCount; ++slot)
    taskflow->emplace([batch, slot] { WorkerPartitionLoop(batch, slot); });
```

把 `slot` 显式传入 Worker，删除 `nextPartitionSlot.fetch_add()`，避免参与任务先争抢第二个全局游标。

- [ ] **4.3 运行 100 帧诊断测试**

通过门槛：

```text
WorkerCap=8
batches=100
participantTasksSubmitted=800
workerTargetTotal=800
activeWorkersPeak<=8
```

注意：在 Taskflow 迁移期只能证明提交了 8 个参与任务，不能把它谎报为唤醒了 8 个唯一 Worker；唯一 Worker/permit 语义在 Task 6 完成。

---

## Task 5：完成真正的本地执行与半量窃取

**目的：** 正常阶段顺序执行本地连续 Tile，只在尾部不均衡时窃取 victim 一半剩余区间。

**文件：**

- 修改：`src/NativeDll/JobSystem.cpp`
- 修改：`src/NativeDll.Tests/JobSystemTests.cpp`

**接口：**

```cpp
bool TryTakeLocal(LocalPartition& partition, uint32_t& tileIndex) noexcept;
bool TryStealHalf(LocalPartition& victim, TileSpan& stolen) noexcept;
bool TryAssistTarget(BatchState& batch, TileSpan& stolen) noexcept;
```

- [ ] **5.1 编写 owner/thief 并发测试**

覆盖：

```text
奇数/偶数 Tile
1、2、8、15 个 Worker
多个 thief 同时选择一个 victim
Complete 与 Worker 同时领取尾部
慢 Tile 固定在头部、中部、尾部
```

每轮断言 Tile exact-once；至少循环 1000 轮。

- [ ] **5.2 Owner 只从 front 顺序领取**

Owner 正常路径不得扫描其他 partition，也不得访问全局 `nextRange`。

- [ ] **5.3 Thief 从 back 一次取得一半连续区间**

```cpp
const uint32_t remaining = back - front;
if (remaining <= 1) return false;
const uint32_t stealCount = remaining / 2;
const uint32_t newBack = back - stealCount;
```

CAS 成功后，thief 本地顺序执行 `[newBack, back)`；不得每执行一个 Tile 就重新扫描 victim。

- [ ] **5.4 Complete 使用同一 steal 协议**

Complete 只能作为特殊 thief 从 victim 尾部取得 span，不能成为第二个 owner，也不能恢复全局游标。

- [ ] **5.5 增加公平预算**

每个参与者处理固定 Tile 数预算后检查全局是否存在其他 Batch；只有存在其他 Batch 时才让出。第一版使用 Tile 数，不在热路径频繁读时钟。

通过门槛：

```text
localTiles / totalTiles >= 90%（均匀测试）
steal 只在尾部负载失衡测试明显发生
stolen span 平均长度 > 1 Tile
exact-once 压力测试 1000 轮通过
```

---

## Task 6：以单一原生 WorkerPool 替换 Taskflow

**目的：** 获得 Unity 风格的真实本地队列、精确 permit 唤醒和可测 park/wake；迁移完成后不保留双线程池。

**文件：**

- 新建：`src/NativeDll/WorkerPool.h`
- 新建：`src/NativeDll/WorkerPool.cpp`
- 修改：`src/NativeDll/NativeDll.vcxproj`
- 修改：`src/NativeDll/NativeDll.vcxproj.filters`
- 修改：`src/NativeDll/JobSystem.cpp`
- 修改：`src/NativeDll.Tests/CMakeLists.txt`
- 新建：`src/NativeDll.Tests/WorkerPoolTests.cpp`

**最小接口：**

```cpp
class WorkerPool
{
public:
    void Initialize(int workerCount);
    void Shutdown();
    void Publish(BatchState* batch, int participantCount);
    int WorkerCount() const noexcept;
};
```

- [ ] **6.1 先写精确唤醒失败测试**

发布 100 个 `participantCount=8` 的 Batch，断言：

```text
ticketsPublished=800
permitsReleased=800
workersPeak<=8
每个 Batch exact-once
```

- [ ] **6.2 实现参与 ticket 队列**

全局队列只保存 Batch 参与权，不保存每个 Tile：

```cpp
struct ParticipationTicket
{
    BatchState* batch;
    uint16_t partitionSlot;
};
```

每个 ticket 持有 Batch 生命周期引用，领取、取消和 Shutdown 时对称释放。

- [ ] **6.3 实现自适应等待**

```text
Hot：20~100μs _mm_pause
Warm：最多 2 次 yield
Cold：semaphore/atomic_wait park
```

禁止无边界永久自旋，普通发布禁止 `notify_all`。

- [ ] **6.4 接入 JobSystem，新旧执行器 A/B**

迁移期间一次 Batch 只能选择一个执行器：

```cpp
enum class ExecutorKind : uint8_t
{
    TaskflowLegacy,
    NativeWorkerPool
};
```

禁止同一 Batch 同时发布到两个线程池。

- [ ] **6.5 验收后删除 Taskflow 执行路径**

只有以下条件全部满足才删除：

```text
Native Tests 全通过
AssistLifetime 通过
Shutdown/依赖/异常压力测试通过
100 帧 Sample 退出码 0、Verify 全 OK
p50 不劣于可靠旧基线 5%
p95 至少改善 15% 或达到 <=1.20ms
```

---

## Task 7：单独验证 ECS 物理 Chunk 字节预算

**前置条件：** Task 1~6 全部通过后才能开始。不得与 WorkerPool 重构同时实施。

**文件：**

- 修改：`src/EntJoy/Archetype/Archetype.cs`
- 修改：ECS Chunk 池、Query、结构变化和序列化相关测试
- 修改：`src/EntJoySample/02_IJobChunkECS/IJobChunkMoveCompareTest/IJobChunkMoveCompareSample.cs`

- [ ] **7.1 固定调度器，只改变 Chunk 字节预算**

依次测试：

```text
64KiB
32KiB
16KiB
```

- [ ] **7.2 保持 API 语义**

- `IJobChunk.Execute()` 每个物理 Chunk 一次。
- Query 不遗漏、不重复。
- 结构变化和序列化测试通过。

- [ ] **7.3 用户机器 A/B**

每个值至少运行 5 个独立进程，保存 p50/p95/max、Chunk 数量、Query 枚举成本和内存占用。选择跨轮稳定值，而不是单轮最小值。

---

## 暂时不要做

- 不要继续优化或恢复全局 `nextRange`。
- 不要先做 `SetThreadAffinityMask` 硬绑核。
- 不要通过永久 `_mm_pause()` 保持所有核心活跃。
- 不要把每个 Tile 放进全局 MPMC 队列。
- 不要同时修改 Tile 大小、Worker 数量、Assist 策略和 Chunk 大小。
- 不要根据 `events=1/result=INCOMPLETE` 的日志判断优化成功。
- 不要只看 avg；必须报告 p50、p95、max。

## 每阶段统一验证命令

```powershell
cmake -S src/NativeDll.Tests -B src/NativeDll.Tests/build
cmake --build src/NativeDll.Tests/build --config Release
& src/NativeDll.Tests/build/Release/AssistLifetimeTests.exe
& src/NativeDll.Tests/build/Release/JobSystemTests.exe
```

构建 Sample 生成版 DLL：

```powershell
& 'D:\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe' `
  src\EntJoySample\NativeTranspiler_Generated\build\NativeDll.sln `
  /m /p:Configuration=Release /p:Platform=x64
```

运行前复制生成版 DLL，不能复制缺少 Transpiler exports 的基础 DLL：

```powershell
Copy-Item `
  src\EntJoySample\NativeTranspiler_Generated\build\Release\NativeDll.dll `
  bin\NativeDll.dll -Force
& bin\EntJoySample.exe
```

最终正确性门槛：

```text
进程退出码 0
Light Verify = OK
Heavy Verify = OK
Sleep Verify = OK
Trace result = COMPLETE
统计总量完全守恒
```

最终性能门槛（用户机器）：

```text
C++ IJobChunk p50 <= 0.90ms
C++ IJobChunk p95 <= 1.20ms
或相对可靠旧路径：p50 不退化超过 5%，p95 改善至少 15%
```
