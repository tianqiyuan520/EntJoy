# JobSystem 深度审查与压力测试报告（2026-08-24）

> 日期：2026-08-24
> 提交：`ad8e236` `54271e4`
> 范围：C++/C# 全代码审查 + UAF Bug 修复 + 三套压力测试项目

---

## 一、审查范围

逐行阅读了以下全部核心源文件：

### C++ 层（NativeDll）

| 文件 | 职责 |
|------|------|
| `ChaseLevScheduler.h/.cpp` | Chase-Lev 调度器：WorkerLoop、SubmitBatch、StealAndExecute |
| `SparseTileDeque.h` | 无锁双端队列：PushBottom/PopBottom/StealTop |
| `MPMCInjector.h` | Vyukov MPMC 无锁环形队列 |
| `RangeTaskPool.h` | Treiber 无锁空闲栈任务池 |
| `JobSystem.h` | HandleState、JobHandle、Scheduler 接口 |
| `JobSystemInternal.h` | BatchState、BatchStorage、ExecutionTile、跨模块类型 |
| `JobSystem.cpp` | 全局状态、统计快照、诊断助手 |
| `JobSystem_State.cpp` | HandleState 生命周期、依赖链、CombineDependencies |
| `JobSystem_Tiles.cpp` | Batch 退役、双条件检查、tile 执行循环 |
| `JobSystem_Scheduler.cpp` | Schedule 系列入口、依赖挂接、SubmitBatch |
| `Exports.cpp` | P/Invoke 导出函数 |
| `JobCostCache.h` | per-job 每元素成本 EWMA 缓存 |
| `JobProfiler.cpp` | 性能分析采集 |

### C# 层（EntJoy.Jobs）

| 文件 | 职责 |
|------|------|
| `ManagedJobScheduler.cs` | 纯 C# Chase-Lev 调度器 |
| `ManagedWorkStealingDeque.cs` | C# 无锁双端队列 |
| `ManagedTileTaskPool.cs` | C# Treiber 任务池 |
| `ManagedJobHandle.cs` | ManagedCompletion + ManagedJobHandle |
| `NativeJobScheduler.cs` | C# → C++ P/Invoke 门面 |
| `NativeJobCore.cs` | DLL 加载、委托缓存、上下文池、异常传播 |
| `NativeJobHandle.cs` | 原生句柄包装 |
| `JobInterface.cs` | IJob/IJobParallelFor/IJobFor/IJobParallelForBatch |

---

## 二、Bug 修复

### 2.1 Use-After-Free in `StealAndExecute`

**文件**：`src/NativeDll/ChaseLevScheduler.cpp` 第 130-134 行

**问题**：`ExecuteAndRelease(task)` 将 `RangeTask` 释放回池后，继续读取 `task->tileCount`：

```cpp
ExecuteAndRelease(task, workerIndex);           // task 释放回池
g_assistTiles.fetch_add(task->tileCount, ...);  // ⚠️ 悬垂指针
```

**修复**：释放前保存到本地变量：

```cpp
const uint32_t stolenTileCount = task->tileCount;
ExecuteAndRelease(task, workerIndex);
g_assistTiles.fetch_add(stolenTileCount, ...);
```

**影响**：诊断计数器 `g_assistTiles` 统计不准。不崩溃不泄漏不误调度，但属于 UB。

---

## 三、审查结论

### 3.1 确认正确的部分

| 组件 | 评估 |
|------|------|
| Chase-Lev 算法（SparseTileDeque） | ✅ PopBottom seq_cst fence 正确阻断 x86 store→load 重排 |
| MPMCInjector | ✅ 标准 Vyukov 算法，序列号免 ABA |
| RangeTaskPool | ✅ 64-bit tag 防 ABA，堆分配兜底完整 |
| 双条件退役（tilesRemaining + pendingTasks） | ✅ finalized.exchange 保证单线程执行退役块 |
| C++ 异常传播 | ✅ try-catch → exceptionRecorded CAS → rethrow |
| C# 代际守卫（Generation） | ✅ 有效防止跨链 ABA |
| C# ManagedCompletion 自动归还 | ✅ Interlocked.Exchange 幂等防 double-return |
| Shutdown 路径（C++ / C#） | ✅ drain_quit 排空 + join 等待 |
| HandleState 引用计数 | ✅ Acquire/Release 配对，RecycleState 归还池 |

### 3.2 已知限制（设计取舍）

| 限制 | 说明 |
|------|------|
| 循环依赖不检测 | 文档化契约：依赖图必须是无环 DAG，Complete() 不做环检测 |
| JobCostCache 哈希碰撞 | 256 槽位，>256 种 Job 类型时碰撞，仅影响 tile 数计算，不影响正确性 |
| StealTop 有限重试 | 4 次重试，极高并发窃取时可能降低成功率 |
| `_batchIdToJobName` 无上限 | 调试面板开启时持续增长，关闭时 Clear 清空 |

---

## 四、压力测试项目

### 4.1 C++ 压力测试

**路径**：`tools/JobSystemStressTest/`

| 测试 | 内容 | 规模 |
|------|------|------|
| 1. MassiveScheduleComplete | 海量 IJob Schedule/Complete | 1M 次 |
| 2. DeepDependencyChain | 深层串行依赖链 | 2000 层 |
| 3. ConcurrentScheduleComplete | 多线程并发调度 | 16 线程 |
| 4. MassiveParallelFor | 大数组 ParallelFor | 100K 元素 |
| 5. ExceptionPropagation | 50% Job 抛异常 | 1000 Job |
| 6. ParallelForWithDependency | ParallelFor + 依赖链 | 100 轮 |
| 7. PoolStability | 池化稳定性 | 10 轮 × 100K |
| 8. DiamondDependency | 菱形依赖图 | 500 轮 |
| 9. FanOutFanIn | 扇出扇入 | 16×10 层 |
| 10. ParallelForBatchSizes | 各种 batch 大小 | 48 组合 |
| 11. ShutdownRace | Init/Shutdown 竞争 | 20 轮 |
| 12. ChunkScheduling | Chunk 调度 | 1000 chunk |
| 13. MixedJobTypes | 混合 Job 类型风暴 | 100K 混合 |
| 15. MainThreadAssist | 主线程 assist 模式 | 500K 元素 |
| 16. BatchStoragePool | BatchStorage 池压力 | 1000 batch × 100K |
| 17. ZeroLengthParallelFor | 零长度边界 | 10K 迭代 |
| 18. ConcurrentDifferentSizes | 并发不同长度 | 8 线程 |
| 19. ConcurrentCombineDependencies | 并发依赖合并 | 200 轮 |
| 20. TinyJobStorm | 极小 Job 风暴 | 2M 次 |
| 21. LongRunningStability | 长时间稳定性 | 30 秒持续负载 |

### 4.2 C# 压力测试

**路径**：`tools/JobSystemStressTest.CSharp/`

**Part A — ManagedJobScheduler（纯 C#，无 NativeDll）**

| 测试 | 内容 | 规模 |
|------|------|------|
| M1. MassiveScheduleComplete | 海量 IJob | 100K |
| M2. DeepDependencyChain | 深层依赖链 | 1000 层 |
| M3. ConcurrentSchedule | 多线程并发 | 8 线程 |
| M4. ParallelFor | 大数组并行 | 100K 元素 |
| M5. ParallelForBatch | 批处理并行 | 100K 元素 |
| M6. ExceptionPropagation | 异常传播 | 50% 抛异常 |
| M7. DiamondDependency | 菱形依赖 | 200 轮 |
| M8. FanOutFanIn | 扇出扇入 | 8×10 层 |
| M9. ZeroLength | 零长度边界 | 5K 迭代 |
| M10. TinyJobStorm | 极小 Job 风暴 | 500K |
| M11. MixedJobTypes | 混合类型 | 50K |
| M12. HeavyParallelFor | 重计算并行 | 50K 元素 |
| M13. CombineDependencies | 依赖合并 | 200 轮 |

**Part B — NativeJobScheduler（C# → C++ P/Invoke）**

| 测试 | 内容 | 规模 |
|------|------|------|
| N1-N13 | 同 Part A 对应测试 | 对应规模 |

---

## 五、测试结果汇总

### 5.1 C++ 单元测试

```
JobSystemTests:        42/42 PASS
ChaseLevIntegration:    5/5  PASS
AssistLifetimeTests:    1/1  PASS
SparseTileDequeTests:   6/6  PASS
MPMCInjectorTests:      6/6  PASS
```

### 5.2 C++ 压力测试

```
Workers: 15 | 耗时: 22.22s | 总 Job 数: 3,868,400
20/20 ALL PASS
```

### 5.3 C++ 长时间稳定性

```
Workers: 15 | 30 秒持续负载
9,487,290 轮 ParallelFor 调度
0 崩溃 | 0 内存异常
```

### 5.4 C++ ASAN 内存检测

```
JobSystemTests (ASAN):         42/42 PASS — 零内存错误
ChaseLevIntegration (ASAN):     5/5  PASS
AssistLifetimeTests (ASAN):     1/1  PASS
```

### 5.5 C# 压力测试

```
Part A (Managed):  13/13 PASS — 1.2s
Part B (Native):   13/13 PASS — 0.2s
```

---

## 六、创建的文件

| 文件 | 说明 |
|------|------|
| `src/NativeDll/ChaseLevScheduler.cpp` | UAF 修复（1 处） |
| `tools/JobSystemStressTest/CMakeLists.txt` | C++ 压力测试构建 |
| `tools/JobSystemStressTest/src/StressTest.cpp` | C++ 压力测试（20 项） |
| `tools/JobSystemStressTest.CSharp/*.csproj` | C# 压力测试构建 |
| `tools/JobSystemStressTest.CSharp/Program.cs` | C# 测试入口 |
| `tools/JobSystemStressTest.CSharp/ManagedStressTests.cs` | 纯 C# 路径测试 |
| `tools/JobSystemStressTest.CSharp/NativeStressTests.cs` | C#→C++ 路径测试 |

---

## 七、结论

该 Job System 经过深度审查和全面压力测试，**可以投入生产环境**。

- 核心算法（Chase-Lev / MPMC / 双条件退役 / 代际守卫）实现正确
- 发现并修复 1 个 UAF Bug（诊断计数器，非功能性）
- 72+ 项测试全部通过（单元 + 压力 + ASAN + 长时间）
- 三套测试项目覆盖 C++ / 纯 C# / C#→C++ 全路径
