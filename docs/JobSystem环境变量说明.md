# JobSystem 环境变量说明 / Environment Variables

本文说明 EntJoy JobSystem 和性能样例当前实际读取的环境变量。环境变量应在
`NativeJobScheduler.Initialize()` 之前设置；JobSystem 初始化后再修改不会切换已创建的
Worker Pool。

This document lists the environment variables currently read by the EntJoy
JobSystem and benchmark sample. Set them before `NativeJobScheduler.Initialize()`;
changing them after initialization does not reconfigure an existing worker pool.

## `ENTJOY_JOB_BACKEND`

选择 JobSystem 执行后端。未设置时默认使用生产后端 `native`。

Selects the JobSystem execution backend. When unset, the production `native`
backend is used by default.

| 值 / Value | 说明 / Description |
|---|---|
| 未设置、`native` | 使用持久化 NativeWorkerPool。/ Use the persistent NativeWorkerPool. |
| `taskflow` | 仅用于兼容性和 A/B 对比。/ Compatibility and A/B comparison only. |
| 其他值 | 记录一次无效选择并回退到 `native`。/ Record an invalid selection and fall back to `native`. |

PowerShell：

```powershell
$env:ENTJOY_JOB_BACKEND = 'native'
.\bin\EntJoySample.exe

$env:ENTJOY_JOB_BACKEND = 'taskflow'
.\bin\EntJoySample.exe

Remove-Item Env:ENTJOY_JOB_BACKEND -ErrorAction SilentlyContinue
```

## `ENTJOY_WORKER_AFFINITY`

控制是否把持久化 Worker 绑定到逻辑处理器。默认关闭。开启值为 `1`、`true` 或
`on`，大小写不敏感；其他值均视为关闭。

Controls whether persistent workers are bound to logical processors. It is off
by default. Case-insensitive enabled values are `1`, `true`, and `on`; every
other value disables affinity.

固定亲和性不保证更快。它可能减少迁核，也可能干扰 Windows 调度、ISPC/ConcRT 或其他
线程池。应使用相同场景进行 A/B 测试后再决定是否开启。

Affinity is not guaranteed to improve performance. It may reduce migration, but
it can also interfere with Windows scheduling, ISPC/ConcRT, or other thread
pools. Enable it only after an A/B test of the target workload.

```powershell
$env:ENTJOY_WORKER_AFFINITY = '1'
.\bin\EntJoySample.exe

$env:ENTJOY_WORKER_AFFINITY = '0'
.\bin\EntJoySample.exe

Remove-Item Env:ENTJOY_WORKER_AFFINITY -ErrorAction SilentlyContinue
```

## 性能样例变量 / Benchmark Variables

以下变量只影响 `IJobChunkMoveCompareTest` 样例，不改变 JobSystem 架构。

The following variables affect only the `IJobChunkMoveCompareTest` sample; they
do not change JobSystem behavior.

| 变量 / Variable | 默认值 / Default | 说明 / Description |
|---|---:|---|
| `ENTJOY_BENCH_WARMUP` | `5` | 每项预热次数，只接受正整数。/ Warm-up iterations per case; positive integers only. |
| `ENTJOY_BENCH_FRAMES` | `100` | 每项测量次数，只接受正整数。/ Measured iterations per case; positive integers only. |

```powershell
$env:ENTJOY_BENCH_WARMUP = '5'
$env:ENTJOY_BENCH_FRAMES = '100'
.\bin\EntJoySample.exe

Remove-Item Env:ENTJOY_BENCH_WARMUP -ErrorAction SilentlyContinue
Remove-Item Env:ENTJOY_BENCH_FRAMES -ErrorAction SilentlyContinue
```

## 关于 `ENTJOY_JOB_DIAGNOSTICS` / About `ENTJOY_JOB_DIAGNOSTICS`

当前实现没有读取 `ENTJOY_JOB_DIAGNOSTICS`，设置它不会开启或关闭原生诊断。当前样例的
诊断输出由样例代码直接控制，原生逐 Tile 计时则通过
`NativeJobScheduler.SetTimingDiagnosticsEnabled(...)` 控制。

The current implementation does not read `ENTJOY_JOB_DIAGNOSTICS`; setting it
does not enable or disable native diagnostics. The sample controls its diagnostic
output directly, while native per-tile timing is controlled through
`NativeJobScheduler.SetTimingDiagnosticsEnabled(...)`.

