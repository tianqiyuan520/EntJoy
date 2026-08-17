# JobSystem Debugger GUI

## 概述

JobSystem Debugger GUI 是一个基于 Dear ImGui（Win32 + D3D11）的实时调试面板，以独立窗口运行。它提供 JobSystem 的实时 Worker 状态监控、Timeline 可视化和统计信息，帮助开发者观察 job 调度与执行情况。

## 启用方式

面板默认不启动，需要显式调用：

```csharp
NativeJobScheduler.Initialize(Environment.ProcessorCount);
NativeJobScheduler.LaunchDebuggerGUI();  // 启动面板并开始监听
```

不再需要设置 `ENTJOY_DEBUG=1` 环境变量。

## 面板页签

### Stats（统计）
- 显示 JobSystem 全局统计：已发布 Jobs、Tiles 总数、Local/Stolen/Assist 分布、Steal/Park 统计、Wake/Exec EWMA 延迟等。
- 所有统计值均通过 `GetStatsSnapshot()` 快照读取，面板打开即开始计数。

### Timeline（时间线 — Unity 风格 Gantt 图）
- 每条 Worker 一条泳道，Job 用彩色横条沿时间轴从左往右铺（start→end）。
- 数据来源：GUI 每帧检测 Worker 的 start/end 迁移，用 `steady_clock` 记录时间戳，产生 `JobSegment{worker, batchId, startMs, endMs, tiles}`。

**交互**：
- **Ctrl+滚轮**：缩放时间窗（200ms~120s），以鼠标位置为锚点居中缩放
- **左键拖拽**：平移时间轴，超过 3px 自动进入暂停态（脱离实时跟随）
- **单击彩条**：选中并显示详情（Job 名、Worker、Duration、Tiles、Range 相对程序启动 + 调度路径开销 EWMA）
- **Pause / Resume**：手动切换实时跟随
- **Live**：一键回到实时跟随，重置窗长 8s
- **窗长下拉**：快捷切换 0.5s~120s

### Activity（活动日志）
- 记录每个已发布的 batch（来自 `RecordPublishedJob` / `RecordDirectCall`）。
- 原生调度器每次 `SubmitBatch` 或 inline 执行时写入一条发布事件，Activity 完整保留所有记录（最多 4096 条，动态 vector 不覆盖）。

## 架构

### 原生侧（NativeDll）

```
JobDebuggerGUI.h / .cpp
```

- `JobDebuggerGUI::Launch()` — 强制启动调试窗口（线程安全、幂等）
- `JobDebuggerGUI::TryLaunch()` — 检查 `ENTJOY_DEBUG` 环境变量后启动（旧路径）
- `GuiThreadMain()` — 后台线程：创建 Win32 窗口 + D3D11 设备 + ImGui 上下文，每帧渲染
- `DrawGuiFrame()` — 渲染所有 UI 页面
- `DrawTimeline()` — 渲染 Gantt 时间线，处理交互（缩放/平移/点选）
- `RecordActivity()` — 每帧检测 Worker 状态迁移，记录 Timeline 段
- `DrainNativeActivity()` — 消费原生发布事件到 Activity 日志

### 数据采集

#### 1. 调度器发布（`SubmitBatch` / inline 路径）
- `JobSystem_Tiles.cpp` `SubmitBatch()`：`g_publishedJobs++` → `RecordPublishedJob(batchId, tiles)`
- `JobSystem_Scheduler.cpp` `ScheduleParallelForBatch` inline 路径：`g_publishedJobs++` → `RecordPublishedJob(id, 1)`
- `JobSystem_Scheduler.cpp` `ScheduleChunkBatchCore` inline 路径同上

#### 2. Worker 快照（Timeline 段）
- `CollectWorkerRows()`：每帧读取 `g_workerCurrentBatchId`、`g_workerCurrentTile`、`g_workerBatchTileCount`、`g_workerIsActive`
- `RecordActivity()`：比较上一帧状态，检测 start/end 迁移 → `StartSegment()` / `EndSegment()`

#### 3. 直调方法（ISPC-MT 等不经调度器）
- C# `RecordDirectCall(string jobName, uint tiles)` → native `JobSystem_RecordDirectCall` → `RecordPublishedJob(id, tiles)`（不增 `g_publishedJobs`，但加入 Activity）
- transpiler `GenerateMethodWrapper` 自动为每个 `[NativeTranspile]` 直调方法插入 `RecordDirectCall`

### 名字解析

#### 1. 调度式 Job
- C# 调度入口（`Schedule<T>`、`ScheduleParallelFor<T>`、`ScheduleChunkCore` 等）在调度后调用 `RegisterScheduledJobName(handle, typeof(T).Name)`
- 读取 `JobSystem_GetDiagnosticBatchId(handle)` 获取 batchId，写入 `ConcurrentDictionary<ulong, string>`
- 仅调试面板开启时（`_debugNameCaptureEnabled = true`）才记录，不影响性能

#### 2. 直调方法（transpiler 生成）
- `BindingsGenerator.cs` `GenerateMethodWrapper` 生成的 `Fill` 等方法体内插入 `RecordDirectCall("方法名", numTasks)`
- native 侧 `RecordDirectCall` 分配自增 id 并维护 `id→名字` 映射

#### 3. 原生名表兜底
- `ResolveNativeJobName(batchId, buf, len)` 优先查 native 侧名字表，再查 C# resolver

### 面板关闭清理
- GUI 线程退出时：`JobSystem_ClearNameResolver()` 清空 C# 字典
- `ClearPublishedJobs()` 清空 native 活动事件
- `g_nativeActivityCaptureEnabled = false` 关闭采集

## 涉及的 C# 侧改动

### `NativeJobScheduler.cs`
- `LaunchDebuggerGUI()` — 启动面板 + 开启名字采集
- `RecordDirectCall(string, uint)` — 直调方法上报 P/Invoke
- `RegisterScheduledJob(IntPtr, string)` — 供 transpiler 生成代码注册 job 名
- `ResolveBatchJobName(batchId, buf, len)` — `[UnmanagedCallersOnly]` 回调供 GUI 查询名字
- `ClearBatchJobNames()` — `[UnmanagedCallersOnly]` 回调供 GUI 关闭时清理
- `_debugNameCaptureEnabled` — 面板关闭时零开销

### `BindingsGenerator.cs`（transpiler）
- `GenerateMethodWrapper`：直调方法插入 `RecordDirectCall`
- `GenerateJobScheduleMethod`：`ScheduleParallelForBatchRaw` / `ScheduleRaw` 后插入 `RegisterScheduledJob`

## 涉及的原生文件

| 文件 | 用途 |
|------|------|
| `JobDebuggerGUI.h` | 类声明（`TryLaunch` / `Launch` / `Shutdown`） |
| `JobDebuggerGUI.cpp` | 全部 GUI 实现（~970 行） |
| `JobSystemInternal.h` | `NativeActivityEvent`、`RecordPublishedJob` 声明 |
| `JobSystem.cpp` | 原生活动事件环形、`RecordDirectCall`、名字表 |
| `JobSystem_Scheduler.cpp` | inline 路径发布计数的补全 |
| `JobSystem_Tiles.cpp` | `SubmitBatch` 发布计数 |
| `Exports.h / Exports.cpp` | `JobSystem_RegisterNameResolver`、`JobSystem_RecordDirectCall` 等 C ABI 导出 |
| `thirdParty/imgui/` | Dear ImGui 库（Win32 + D3D11 后端） |
| `NativeTranspilerGenerator.cs` | CMake 生成：ImGui 源文件集成到 NativeDll 构建 |