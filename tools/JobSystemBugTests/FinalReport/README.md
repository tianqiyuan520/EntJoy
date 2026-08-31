# JobSystem 阶段 0～7 回归报告

日期：2026-08-31（squash 为单个 commit `03d134d "Jobsystem修复"`，自 `1e86d78` 起）

## 阶段结果

| 阶段 | 专门测试与实际覆盖 | 结果 |
|---|---|---|
| 0 基线 | C++ 单测、Stress、C# 构建；记录旧 DLL ABI 问题 | 已记录 |
| 1 C# Bridge | Managed For/Batch 依赖、Native 句柄副本（3 个测试） | 3/3 通过 |
| 1 补充 | `JobHandle.Complete()` 调用 Managed 句柄的异常/回收路径（1 行修复，尚无独立异常 fixture） | 已验证 |
| 2 Batch Lifetime | pending flush、shutdown residual、并发 flush/complete（3 个测试） | 3/3 通过 |
| 3 Scheduler Lifetime | 1000 次重初始化、并发 Complete/Shutdown、schedule gate（3 个测试） | 3/3 通过 |
| 4 ABI/Fallback | ABI fixture + Managed fallback；带 DLL 参数运行 | 2/2 通过 |
| 5 全量验收 | 现有测试、阶段测试、压力测试；仅报告，不代表 sanitizer 完成 | 部分通过/待 CI |
| 6 C++ 异常安全 | 原生 ScheduleFor、cleanup、multi-tile、并发 Complete、shutdown、backend reject、cleanup 顺序、batch-id 清理 | 9/9 通过 |
| 7a defer depth | 下溢后批量任务不滞留（修复前 Complete 永久阻塞） | 1/1 通过 |
| 7b Complete retain | 并发 Complete/Release 别名 + 等待期释放 | 2/2 通过 |
| 7c BatchStorage 退役 | storage 恰好回收一次 + 主线程 assist 并发（约 19 万批） | 2/2 通过 |
| 7d 隐式批 toggle | 并发调度 + 开关切换，flush 后 pending 必空 | 1/1 通过 |
| 7e 旧 ABI fixture | 缺 ABI export / 版本不匹配均安全 fallback 且 job 正常执行 | 2/2 通过 |

## 全量验证

本机实际执行的核心命令（均为 Release，C# stress 除外）：

```powershell
& .\tools\JobSystemBugTests\Stage1_CSharpBridge\bin\Release\net8.0\Stage1_CSharpBridge.exe
& .\tools\JobSystemBugTests\Stage2_BatchLifetime\build\Release\Stage2_BatchLifetime.exe
& .\tools\JobSystemBugTests\Stage3_SchedulerLifecycle\build\Release\Stage3_SchedulerLifecycle.exe
& .\tools\JobSystemBugTests\Stage6_CppExceptionSafety\build\Release\Stage6_CppExceptionSafety.exe
& .\tests\NativeDll.Tests\build\Release\JobSystemTests.exe
& .\tests\NativeDll.Tests\build\Release\ChaseLevIntegrationTests.exe
& .\tests\NativeDll.Tests\build\Release\AssistLifetimeTests.exe
& .\tools\JobSystemStressTest\build\Release\JobSystemStressTest.exe
dotnet run --project tools\JobSystemStressTest.CSharp\JobSystemStressTest.CSharp.csproj --no-restore
```

原始终端日志没有在阶段 0～5 中逐条归档；Stage6 的专项结果按 fresh Release
重建记录；以上结果以提交时的终端退出码和
输出摘要为依据。下一阶段应把每条命令输出写入按日期命名的日志文件。

- `JobSystemTests.exe`：fresh Release exit 0，全部测试通过（约 19.50s）。
- `ChaseLevIntegrationTests.exe`：fresh Release exit 0，5 项通过（约 265ms）。
- `AssistLifetimeTests.exe`：fresh Release exit 0（约 238ms）。
- `ImplicitBatchTests.exe`：fresh Release exit 0，全部测试通过（约 234ms）。
- `Stage6_CppExceptionSafety`：fresh Release exit 0，9/9 通过（约 218ms）。
- `JobSystemStressTest.exe`：fresh Release 20 项通过，3,868,400 jobs，约 26.46s。
  与阶段 0 的 22.48s/22.69s 记录不是同一次构建环境，只比较 wall time，
  未测完整 tasks/sec 与 P50/P95/P99，不能据此宣称性能门槛通过。
- `Stage1_CSharpBridge` Release：3/3 通过。
- `Stage2_BatchLifetime` Release：3/3 通过。
- `Stage3_SchedulerLifecycle` Release：3/3 通过。
- `Stage4_AbiCompatibility`：2/2 通过。
- `JobSystemStressTest.CSharp`：使用新 ABI DLL 后 Managed + Native 全部通过，总计约 67.51s。

实际使用 DLL：

```text
E:\GODOT\Project\EntJoy\tools\JobSystemStressTest.CSharp\bin\Debug\net8.0\NativeDll.dll
SHA256=092147225D41DC4774CAB499E1F25E3377A113722C54FB19DF5AC0B2B59DC578
```

阶段 4 的可复现实验命令：

```powershell
dotnet run --project tools\JobSystemBugTests\Stage4_AbiCompatibility\Stage4_AbiCompatibility.csproj --no-restore -- `
  "E:\GODOT\Project\EntJoy\tools\JobSystemBugTests\Stage4_AbiCompatibility\build\Release\NativeDll.dll"
```

不传 DLL 参数时，ABI fixture 会跳过，程序仍返回 0；因此不能把无参数运行
记作 2/2。

## 性能对比（阶段 0 vs 阶段 7，同环境 PerfBench）

工具 `tools/JobSystemPerfBench` 各跑 3 次取中位数（Workers=15）：

| 指标 | 阶段 0 基线 | 阶段 7 | 差异 |
|---|---:|---:|---:|
| Schedule+Complete | ~1.78 M ops/s | ~1.77 M ops/s | <1%（噪声） |
| Schedule 延迟 P50/P95 | 500 / 600 ns | 500 / 600 ns | 无差异 |
| ParallelFor | ~119 M elem/s | ~118 M elem/s | 噪声范围 |

StressTest wall-time：阶段 0 22.48s、阶段 7 25.54s。两者不是同一次构建环境
（阶段 7 额外包含阶段 1~6 的修复），不能据此宣称回退。

结论：阶段 7 的 4 项修复对 C++ 热路径零开销（defer `<=0`、隐式批快路径早退）
或略提升（退役单线程化去掉一次双条件检查）；C# Complete retain 仅增加 2 次
P/Invoke（纳秒级）。无显著回退，但完整「吞吐回退 <1%」签字仍待 CI 固定频率、
多轮中位数 + 分位数归档。

## ASAN / TSAN（CI 已通过）

`linux-sanitizers` job 已补跑并全绿：

- ASAN+UBSAN：`JobSystemTests` 0 个 UAF / double-free / 越界；
- TSAN：0 个 data race（修复了 `SparseTileDeque::bottom_` 非原子导致 owner 写 / thief 读的 C++ UB，及 `TestJccConcurrentHeterogeneous` 并发写共享数组的测试 race）。

## CI 覆盖与缺口

`.github/workflows/jobsystem-ci.yml` 当前覆盖：

- ✅ C++ 调度器：直接编译 7 个核心源文件跑 JobSystemTests / ChaseLevIntegrationTests / AssistLifetimeTests / ImplicitBatchTests / Stage6·7·9·10（Windows），以及 ASAN/TSAN（Linux）；
- ✅ C# 编译：`dotnet build EntJoy.Jobs.csproj`；
- ✅ C# 旧 ABI fallback：Stage11（缺 ABI export / 版本不匹配 → Managed fallback）。

缺口（尚未覆盖）：

- ❌ C# Managed JobSystem 运行（Stage1_CSharpBridge 的 Managed 测试未入 CI）；
- ❌ C# 调用 C++ Native 的 P/Invoke 完整路径（CI 未构建 NativeDll.dll，无法验证 Native 侧调度）。

## 当前未完成/不可宣称的门槛

1. 没有 Schedule/Complete/tile-claim 的独立 P50/P95/P99、tasks/sec 和
   分配次数数据，不能宣称满足“吞吐回退 <1% / tile claim <0.5%”门槛。
2. BatchStorage 退役竞态已通过「退役单线程化」关闭（阶段 7c），未采用
   intrusive refcount 方案；两者都满足「退役只有一个 finalizer」的不变量，
   但单线程化依赖「pendingTasks 归零 ⇒ tilesRemaining 归零」的模型不变量。
3. Initialize/Shutdown 已串行化；公开 API 与 Shutdown 并发的 guard 已评估为
   契约化（Shutdown 仅主线程、不与 Schedule/Complete 并发），未引入热路径锁。
4. 旧 DLL ABI fixture 已建立（Stage11）但未纳入 CI；Stage1～11 测试覆盖的是
   确定性回归集，不等于计划中列出的全部异常、ProcessExit/DomainUnload 和
   worker 自 shutdown 场景。
5. defer/toggle/退役竞态修复依赖代码审查 + 压力回归，ASAN/TSAN 证据需在
   Linux/Clang CI 补跑。

## 下一步（按优先级）

1. CI 已落地：`.github/workflows/jobsystem-ci.yml` 覆盖 Windows 功能回归 +
   旧 ABI fixture（Stage11）+ C# 构建，以及 Linux/Clang ASAN+UBSAN 与 TSAN。
   需推送到 GitHub 后验证 Linux 编译与 sanitizer 结果（本机为 Windows，
   尚未在真实 Linux 环境跑通）。
2. 旧 ABI fixture 剩余分支：「缺失核心导出」「初始化中途失败」。
3. 性能完整签字：CI 固定频率 + 多轮中位数 + 分位数归档（PerfBench 已建，
   tile-claim / tasks/sec 独立分位数仍缺）。
4. 门槛通过后才规划 Safety Layer、Cancel API 等扩展。

## 已知非阻塞项

1. `ManagedJobScheduler.cs` 仍有既有 CS0649 字段警告，不影响构建或运行。
2. C# 压力工具的输出目录可能残留旧 `NativeDll.dll`；ABI 校验会安全 fallback，但要验证 Native 部分必须部署与源码同版本 DLL。
3. 本轮未加入 Safety Layer、Cancel API、依赖图可视化或热路径优化。
