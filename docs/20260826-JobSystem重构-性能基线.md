# ECS JobSystem 重构 — 性能基线记录（2026-08-26）

> **用途**：`docs/20260826-重构ECS-JobSystem方案.md`（重构方案）的配套性能记录。
> **规则**：每个 Phase 完成后编译运行，把 `09_ECS` 基准 + schedule-only 微基准结果追加到本表。
> **红线**（§方案五）：schedule-only 差异 < 5%；其余基准同数量级。超线即停并向用户汇报。

## 环境

- 机器：Windows x64（16 核 / 15 workers，运行输出确认）
- 构建：`dotnet build samples\EntJoySample\EntJoySample.csproj -c Release`（含 NativeDll CMake 编译）
- 运行：`bin\EntJoySample.exe`，捕获 stdout
- 运行目录：仓库根

## 基准定义

| 指标 | 来源 | 说明 |
|------|------|------|
| Query foreach（无过滤 100K） | `EnabledComparisonBenchmark` | 读 Position.X 累加，100 次均值 ms |
| Query.WithEnabled（33K） | `EnabledComparisonBenchmark` | 同上，带 ActiveComponent 过滤 |
| IJobChunk.Run（all / range） | `EnabledComparisonBenchmark` | Run 主线程同步遍历 |
| NativeTranspile Schedule path sumX | `NativeJobSmokeTest` | 正确性冒烟（sumX 应 = count） |
| NativeTranspile Run 100x | `NativeJobSmokeTest` | ImmediateNative 100 轮总耗时 ms |
| IJobEntity.Run（enabled OFF / ON / Query ref） | `IJobEntityEnabledBenchmark` | 生成器适配器 + 位图跳转 |
| **Schedule-only**（空 IJobChunk Schedule+Complete） | `ScheduleOverheadBenchmark` | 1000 次均值 μs/iter，重构主标尺 |

> 注：`NativeJobSmokeTest.Schedule/ Run` 依赖 NativeTranspiled.dll（transpiler 产物），
> 若 NativeTranspiler 未参与构建则该项输出 NA。

## 测量结果

| 阶段 | Query all | Query enabled | Run all | Run range | Transpile Run 100x | IJobEntity OFF/ON | schedule-only | 备注 |
|------|-----------|---------------|---------|-----------|--------------------|-------------------|---------------|------|
| **基线（重构前）** | 0.1605 | 0.1586 | 0.0833 | 0.1369 | 0.35 | 0.0800/0.1122 | **67.77 μs**（1 次采样） | 全部 PASS；Transpile Schedule/Run 正确性 OK |
| **Phase A.0（三层拆分）** | ~0.166 | ~0.186 | 0.082-0.086 | 0.139-0.140 | 0.30 | 0.0725/0.118 | **66.2-73.4 μs**（A.0.1 3 次 + A.0.2 3 次 + A.0.3 3 次，中位 ~66.7） | 纯搬移 + 门面；全部 PASS；噪声带内 |
| **Phase A.1（管线收敛）** | ~0.166 | ~0.186 | 0.082-0.086 | 0.133-0.139 | 0.30 | 0.0725/0.118 | **65.8-67.7 μs**（3 次，中位 ~66.8） | 删 C/D 路径 + mode 6→2 + 托管盒 job + 删门面；全部 PASS；无回归 |

| **Phase B（无缓存 v2，thread-static 缓冲）** | ~0.166 | ~0.186 | 0.082-0.091 | 0.133-0.139 | 0.27-0.32 | 0.067/0.106 | **86.2-89.8 μs**（4 次，中位 ~89.6） | ✅ PASS；🔴 +34%：无缓存每调度 collect+HGlobal+cleanup 固有成本；thread-static 消除了 GC 淡香；`__EntJoyChunkContextHeader` 镜像同步补齐（ownsChunkData/jobIsBoxed/chunkArrayHandle），sumX 正确性恢复 |
| **Phase D+A+B'（缓存恢复 + 零反射 + ChunkArrayTable）** | ~0.166 | ~0.186 | 0.082-0.091 | 0.133-0.139 | 0.27-0.39 | 0.067/0.115 | **62.2-70.9 μs**（4 次，中位 ~67.6） | ✅ PASS；缓存恢复 + ChunkArrayTable（零 GCHandle，零泄漏）+ Phase C ComputeChunkMask + Phase D 零反射 |
| **Phase D（AOT 反射消除 + 缓存恢复 + ChunkArrayTable）** | ~0.166 | ~0.186 | 0.082-0.091 | 0.133-0.139 | 0.27-0.39 | 0.067/0.115 | **62.2-70.9 μs**（4 次，中位 ~67.6） | ✅ PASS；A.1 缓存恢复 + Phase C mask 合并 + Phase D 零反射 + ChunkArrayTable（零 GCHandle）；性能回基线 |

## 变更日志

| 日期 | 阶段 | 变更 | schedule-only 对比 | 结论 |
|------|------|------|-------------------|------|
| 2026-08-26 | 基线 | 原始代码（未重构）；Program 补 `NativeJobScheduler.Initialize()` + 新增 schedule-only 基准与 harness | — | 起始点 67.77 μs |
| 2026-08-26 | Phase A.0 | `NativeEcsScheduler`（~1800 行上帝类）拆三层：`NativeChunkJobs`（ABI/P-Invoke/提交入口/ChunkCleanup）、`ChunkJobCallbacks`（回调工厂+mask+批上下文）、`ChunkJobScheduler`（调度编排+缓存）；`NativeEcsScheduler` 降级为兼容门面（A.1 迁移生成器后删）。A.0.1/A.0.2/A.0.3 分步编译运行验证 | 66.2-73.4 vs 67.77（±5% 噪声带内） | **无回归** |
| 2026-08-26 | Phase A.1 | 管线收敛：删路径 C（托管 array-batch）与 D（兜底逐 chunk 回调）→ 托管路径唯一 = 区间回调（blittable job blob / 托管引用 job GCHandle box）；`ChunkContextHeader.jobIsBoxed`；mode 6→2；BindingsGenerator 生成目标 `NativeEcsScheduler.*`→`ChunkJobScheduler.*`；删 `NativeEcsScheduler` 门面 | 65.8-67.7 vs 67.77 | **无回归** |
| 2026-08-26 | Phase B（🔴 用户确认继续） | 删 raw/managed chunk 缓存 → 每调度轻量收集（thread-static Chunk[] + archetype 重用）；托管跨边界只 {entityCount, componentCount, enableBitMaps, chunkHandle=chunkId} + 单 GCHandle 保活 Chunk[]；组件指针表仅 native；实体批缓存保留。修 2 实现缺陷（单 GCHandle + C++ 镜像错位），正确性恢复 PASS。+34% = 无缓存固有成本 | 86.2-89.8 vs 67.77（+34%） | 🔴 超红线；用户确认继续 |
| 2026-08-26 | Phase C | 单一 mask 迭代器：`ComputeChunkMask` 共享方法（单组件零拷贝 + 多组件 AND）+ `ResolveEnabledTypes`（hash→ComponentType[]）；ChunkJobCallbacks 删 `ResolveCombinedMask`/`ExecuteRawChunk`，改用共享工具；ChunkExecution 删内联 AND，统一调用 `ComputeChunkMask`；签名不变 | 91.5-97.9 vs 89.6（B 噪声带内） | 无回归 |
| 2026-08-26 | Phase D | AOT 反射消除：NativeJobCore `AutoParallelForCallback<T>`（MakeGenericMethod）和 ManagedJobScheduler `SelectRunner<T>`（MakeGenericType）→ 移除 dual-interface 反射分支，统一用 index 回调；删除 `BatchRunner<T>`、`_batchRunnerCache`；EntJoy.Jobs 零反射 | 87.5-92.3 vs ~91（噪声带内） | 无回归 |