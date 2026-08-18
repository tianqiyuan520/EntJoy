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
- 每条 Worker 一条泳道（末条 `M` = 主/调用线程），Job/直调用彩色横条沿时间轴铺（start→end）。
- 数据来源：共享时间线历史 `g_debugSegments`，原生在 Job/直调 `start/end` 事件发生时记录完整窗口（`DebugBeginExec`/`DebugEndExec`），GUI 只读渲染，不采样。
- 直调方法（transpiler 直跑，如 `Fill`）标记 `[D]`（蓝青色），与调度式 Job 区分。
- 泳道画布**横向可滚**：画布以最早事件为原点横向铺开，底部横向滚动条 / 左键拖拽平移时间，竖向滚动条仅在泳道过多时出现。泳道区右/底部预留滚动条宽度，**拖拽判定不抢滚动条事件**。
- **ISPC MT 直调**：每个实际参与的 worker 会渲染出任务条，泳道标签 `T#`（ISPC ConcRT 任务线程，独立泳道，与 JobSystem 的 W 泳道区分）。
- **性能零开销**：`DebugBeginExec/DebugEndExec`、`DebugIspcTaskBegin/End`、直调上报等在面板未开启（`g_nativeActivityCaptureEnabled==false`）时**第一行直接短路**，不影响正常调度热路径与 ISPC 任务执行。

**交互**：
- **Ctrl+滚轮**：缩放时间窗（200ms~120s），以光标位置的时间为锚点，缩放后保持锚点相对位置（灵敏）
- **左键拖拽**：平移时间轴（改变可视时间窗右界），超过 3px 自动进入暂停态
- **底部横向滑块**：在历史时间内拖动平移窗口（保留横向滚时间轴能力）
- **泳道竖向滚动条**：泳道过多（含 ISPC W\* 泳道）时上下滚动
- **单击彩条**：选中并显示详情（Name、Where、Batch、Duration、Tiles 或直调并行度、Range）
- **Pause / Resume**：手动切换实时跟随；暂停时冻结时间窗右界（now 不推进也不漂移）
- **Live**：一键回到实时跟随，重置窗长 8s
- **窗长下拉**：快捷切换 0.5s~120s

**时间窗模型**：一窗 `[winLeft, winRight]` 映射到固定可视宽，所有交互（拖拽/缩放/滑块）统一走 `winRight` 与 `span`，不再依赖 ImGui 像素滚动定位，故始终灵敏。

### Activity（执行窗口 + 发布事件）
- **执行窗口**：逐条列出每个 Job/直调执行（`Wxx: JobName  耗时  tiles  workers`，直调前缀 `[D]`），来源同 Timeline（事件驱动，最多显示最近 2048 条）。
- **发布事件**：原生发布事件日志（`RecordPublishedJob` / 直调），保留全部历史（最多 4096 条，动态 vector 不覆盖）。

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

统一口径：**发布 = 计数 + 记 Activity**。`g_publishedJobs` 与 Activity 事件一一对应（面板开启后的增量部分）。

#### 1. 调度器发布（`SubmitBatch` / inline / 快速路径 / 同步路径）
- `JobSystem_Tiles.cpp` `SubmitBatch()`：`g_publishedJobs++` → `RecordPublishedJob(batchId, tiles)`
- `JobSystem_Scheduler.cpp` inline / 同步阈值 / `ScheduleFastPath`（`Schedule`、`ScheduleFor`、rc≤1 的 `ParallelFor`/`ParallelForBatch`）与 `ScheduleFor`>64 的 `ScheduleWithDependency` 路径：全部 `g_publishedJobs++` → `RecordPublishedJob(id, 1)`，保证每个 Job 都会被发布并计入

#### 2. 执行窗口事件记录（Timeline 段）——不按帧采样
- 每个执行窗口（`ExecuteBatchSlot` 的 tile 循环、`FastPath`/`ScheduleFor`>64 的 pool 执行、`RunSyncJob` 的 inline 同步执行）在 **start 瞬间** 由 `DebugBeginExec` 记录开始时间戳（压栈），在 **end 瞬间** 由 `DebugEndExec` 把完整窗口 `{lane, batchId, startMs, endMs, tiles}` 直接追加进共享时间线历史 `g_debugSegments`（base 模块数组，容量 16384）
- GUI 每帧只**渲染**共享历史，不再采样 worker 迁移——微秒级 Job（两帧之间跑完）也能在结束瞬间被完整记录，无采样丢失
- 泳道归属：
  - pool worker 线程由 `WorkerLoop` 预分配 worker 索引 → 上报到对应 `W#` 泳道
  - 主/调用线程（无索引）→ 上报到预留的 `M` 泳道（index == worker 数）
- 因此 **Timeline 能看到所有 Job**：tile 批次在 `W#`，快速路径/大 `ScheduleFor` 在 worker 泳道，inline/同步在 `M` 泳道
- 泳道区为可滚动 child：垂直滚动条 + 普通滚轮滚动泳道，Ctrl+滚轮缩放时间轴

#### 3. 直调方法（ISPC-MT 等不经调度器）
- C# `RecordDirectCall(string jobName, uint tiles)` → native `JobSystem_RecordDirectCall` → `g_publishedJobs++` → `RecordPublishedJob(id, tiles)`（直调也计入 published，与 Activity 一致；只以 Activity 形式呈现，不映射到泳道）
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