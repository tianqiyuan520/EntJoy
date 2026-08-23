# Chase-Lev Deque 重构计划（2026-08-21）

> 状态：**计划中**。将 NativeWorkerPool 的调度核心从 MPMC 环形队列 + 全局扫描 改为 Chase-Lev 无锁 Deque + 工作窃取。

---

## 一、为什么要做

### 1.1 当前架构的根本问题

```
当前: 全局 MPMC 队列 + nextTile.fetch_add(4) + 15 worker 竞争同一个原子
     ↓
每个 batch 的 240 tiles 都在同一个"战场"上竞争
     ↓
原子争用 = S5 方差的主要来源之一
```

### 1.2 MPMC vs Chase-Lev 的本质区别

| 维度 | MPMC 环形队列 | Chase-Lev Deque |
|---|---|---|
| **调度模型** | 推送式（集中竞争） | 拉取式（分布式） |
| **缓存局部性** | ❌ 全局队列跨核颠簸 | ✅ 本地队列核内操作 |
| **竞争频率** | 每次 Pop 都 CAS 竞争 | 仅窃取时 CAS（低频） |
| **可扩展性** | 随核心数增长而退化 | 随核心数增长而优势扩大 |
| **FIFO 限制** | 严格 FIFO | LIFO（本地）+ FIFO（窃取） |
| **容量** | 有界（满则阻塞） | 可动态扩容 |

### 1.3 Unity 的设计哲学

Unity Job System 采用工作窃取（Work Stealing）策略，核心思想：
- **本地优先**：Worker 先处理自己 Deque 里的任务
- **按需窃取**：本地空了才去偷别人的
- **全局队列仅作入口**：新任务先进全局队列，Worker 拉到本地后再执行

Chase-Lev Deque 是实现无锁工作窃取的经典算法（Rust crossbeam、Intel TBB 等均采用）。

---

## 二、设计决策

| 决策项 | 选择 | 原因 |
|---|---|---|
| **任务粒度** | 混合策略 | IJobParallelFor 走 Batch 级（粗粒度），IJobChunk/IJobEntity 走 Tile 级（细粒度） |
| **全局队列** | 保留 MPMC 作为入口 | 避免多生产者并发 PushBottom 的问题，改动最小 |
| **迁移策略** | `ENTJOY_USE_WORKSTEALING=1` 环境变量开关 | 保留旧路径可回退，灰度验证 |
| **对象池** | TaskPool 避免动态分配 | BatchTask 1024、TileTask 4096 |

---

## 三、架构概览

```
SubmitBatch:
  Task* → 全局 MPMC 队列（入口）
                ↓
WorkerLoop:
  1. PopBottom(myDeque)     ← 本地 Deque（无竞争）
  2. StealTop(otherDeque)   ← 窃取其他 Worker（CAS）
  3. TryPop(globalQueue)    ← 全局队列（后备）
  4. Park（spin → yield → semaphore）
```

---

## 四、实施步骤

### Phase 1：WorkStealingDeque 实现（新文件）

**文件**：`src/NativeDll/WorkStealingDeque.h`

```cpp
// Chase-Lev 无锁双端队列
// top: 原子，窃取者 CAS 修改
// bottom: 非原子，仅 Owner 修改
// buffer: 环形数组，可扩容
class WorkStealingDeque {
    std::atomic<size_t> top_;
    size_t bottom_;
    void** buffer_;
    size_t capacity_;
public:
    void PushBottom(void* task);   // Owner 调用（无竞争）
    void* PopBottom();             // Owner 调用（无竞争）
    void* StealTop();              // 窃取者调用（CAS）
    bool IsEmpty() const;
};
```

关键实现要点：
- 初始容量 1024（2 的幂），支持扩容
- `PushBottom`: 写 `buffer_[bottom_] = task`，然后 `bottom_++`（release store）
- `PopBottom`: 读 `buffer_[bottom_-1]`，CAS 处理与 Steal 的竞争
- `StealTop`: CAS 更新 `top_`，读 `buffer_[top_]`
- 扩容：分配新 buffer，迁移旧数据，CAS 替换指针

### Phase 2：Task 抽象（新文件）

**文件**：`src/NativeDll/SchedulerTask.h`

```cpp
// 任务基类
struct SchedulerTask {
    virtual void Execute() = 0;
    virtual ~SchedulerTask() = default;
};

// Batch 级任务（IJobParallelFor 均匀负载）
struct BatchTask : SchedulerTask {
    BatchState* batch;
    void Execute() override {
        // 复用现有 WorkerAtomicRangeLoop 逻辑
        WorkerAtomicRangeLoop(batch);
    }
};

// Tile 级任务（IJobChunk/IJobEntity 不规则负载）
struct TileTask : SchedulerTask {
    BatchState* batch;
    uint32_t tileIndex;
    void Execute() override {
        TryExecuteOneTile(batch, tileIndex);
    }
};
```

对象池：`TaskPool<BatchTask, 1024>` 和 `TaskPool<TileTask, 4096>`，避免动态分配。

### Phase 3：NativeWorkerPool 改造

**文件**：`src/NativeDll/NativeWorkerPool.h` + `NativeWorkerPool.cpp`

#### 3.1 WorkerState 新增字段

```cpp
struct WorkerState {
    // 现有字段保留（旧路径）
    MpmcRing<WorkItem, kLocalQueueCapacity> queue;
    
    // 新路径：Chase-Lev Deque
    WorkStealingDeque deque;
};
```

#### 3.2 Submit 接口扩展

```cpp
// 新接口：支持 Task 直接入队
bool SubmitTask(void* task, uint32_t targetWorker);

// 旧接口保留（ENTJOY_USE_WORKSTEALING=0 时使用）
bool Submit(void* context, uint32_t slotCount, RunSlotFn runSlot, CompletionFn completion);
```

#### 3.3 WorkerLoop 改造

```cpp
void WorkerLoop(uint32_t workerIndex, WorkerState* worker) {
    while (true) {
        SchedulerTask* task = nullptr;
        
        if (g_useWorkStealing) {
            // 新路径：Chase-Lev 工作窃取
            // 1. 本地 Deque
            task = (SchedulerTask*)worker->deque.PopBottom();
            if (!task) {
                // 2. 窃取其他 Worker
                for (uint32_t i = 1; i < workerCount; ++i) {
                    uint32_t victim = (workerIndex + i) % workerCount;
                    task = (SchedulerTask*)workers[victim]->deque.StealTop();
                    if (task) { ++g_stealCount; break; }
                }
            }
            if (!task) {
                // 3. 全局队列
                WorkItem item;
                if (globalQueue.TryPop(item)) {
                    task = CreateTaskFromWorkItem(item);
                }
            }
        } else {
            // 旧路径：MPMC 环 + 全局扫描（保持不变）
        }
        
        if (!task) {
            ParkIdle(worker);
            continue;
        }
        
        task->Execute();
        DeleteTask(task);
    }
}
```

### Phase 4：SubmitBatch 改造

**文件**：`src/NativeDll/JobSystem_Tiles.cpp`

```cpp
void SubmitBatch(BatchState* batch, int workerCap) {
    if (g_useWorkStealing) {
        // 新路径：创建 Task，推入全局队列
        if (batch->tileCount <= 16) {
            // 少 tile → Batch 级任务（粗粒度）
            auto* task = TaskPool<BatchTask>::Acquire();
            task->batch = batch;
            g_nativeWorkerPool->SubmitTask(task, RoundRobinTarget());
        } else {
            // 多 tile → Tile 级任务（细粒度）
            for (uint32_t i = 0; i < batch->tileCount; ++i) {
                auto* task = TaskPool<TileTask>::Acquire();
                task->batch = batch;
                task->tileIndex = i;
                g_nativeWorkerPool->SubmitTask(task, (i % workerCount));
            }
        }
    } else {
        // 旧路径：现有 SubmitBatch 逻辑（保持不变）
    }
}
```

### Phase 5：环境变量开关

**文件**：`src/NativeDll/JobSystem.cpp`

```cpp
bool g_useWorkStealing = false;

void JobSystem_Initialize(int numThreads) {
    const char* env = std::getenv("ENTJOY_USE_WORKSTEALING");
    if (env && std::atoi(env) == 1) {
        g_useWorkStealing = true;
    }
    // ...现有初始化...
}
```

---

## 五、测试与验证

### 5.1 正确性测试

- **单线程**：PushBottom/PopBottom 正确性
- **多线程压测**：16 worker 并发 Push/Pop/Steal
- **ABA 测试**：高频 push/pop 模拟
- **内存泄漏**：Valgrind / ASan 检测

### 5.2 性能测试

```powershell
# 旧路径（基线）
set ENTJOY_USE_WORKSTEALING=0
dotnet run -c Release

# 新路径
set ENTJOY_USE_WORKSTEALING=1
dotnet run -c Release

# 对比 S1-S5 + GridSearch
```

### 5.3 关注指标

| 指标 | 预期 |
|---|---|
| S5 Native 中位数 | ≤ 4.3ms（持平或略优）|
| S5 方差 spread% | ≤ ±40%（持平或略优）|
| S3 依赖链 | 持平 |
| GridSearch | 显著改善（窃取效率提升）|
| 内存泄漏 | 0 |

---

## 六、风险与缓解

| 风险 | 缓解措施 |
|---|---|
| ABA 问题 | 64 位 top 回绕需 2^64 次操作，物理不可能；仍加版本号防御 |
| 竞争最后一个元素 | Chase-Lev 标准处理：PopBottom 与 StealTop 的 CAS 仲裁 |
| 内存序错误 | 严格按论文实现 acquire/release 配对；x86 测试通过后加 ARM 测试 |
| 扩容期间竞态 | 扩容时暂停新 push（短临界区），或使用双 buffer 切换 |
| 调试面板错乱 | Task 记录 workerIndex，Execute 前设置 |

---

## 七、回退方案

- `ENTJOY_USE_WORKSTEALING=0`（默认）→ 旧 MPMC 路径
- `ENTJOY_USE_WORKSTEALING=1` → 新 Chase-Lev 路径
- 任何问题立即回退，零风险

---

## 八、实施顺序

1. Phase 1（WorkStealingDeque）→ 单元测试通过
2. Phase 2（Task 抽象）→ 对象池测试通过
3. Phase 3（NativeWorkerPool 改造）→ 编译通过
4. Phase 4（SubmitBatch 改造）→ 编译通过
5. Phase 5（环境变量开关）→ 可切换
6. Phase 6（测试验证）→ S1-S5 全通过
7. 性能对比 → 确认无回归

---

## 九、参考文献

- Chase-Lev Deque 原论文："Dynamic Work Stealing for Parallel Task Granularities"
- Intel TBB 实现：`tbb::concurrent_bounded_queue`
- Rust crossbeam：`crossbeam-deque`
- Unity Job System 文档：https://docs.unity3d.com/Manual/JobSystemOverview.html
