# JobSystem Bug-Fix Baseline

日期：2026-08-30

## 当前基线

| 项目 | 结果 | 用时 |
|---|---:|---:|
| C++ JobSystemTests Release | PASS | 21.781 s |
| C++ ChaseLevIntegrationTests Release | PASS | 0.051 s |
| C++ AssistLifetimeTests Release | PASS | 0.043 s |
| C++ JobSystemStressTest Release | PASS | 22.483 s |
| EntJoy.Jobs.csproj | PASS，6 个既有 CS0649 warning / 0 error | 1.41 s |
| C# JobSystemStressTest | FAIL：NativeDll 缺少 JobSystem_ScheduleBatch | 9.0 s |

## C# stress 失败摘要

```text
EntryPointNotFoundException:
Unable to find an entry point named 'JobSystem_ScheduleBatch' in DLL.
at EntJoy.JobSystem.NativeJobCore.LoadNativeDll() line 332
```

当前 C# stress 使用的 DLL：

```text
tools/JobSystemStressTest.CSharp/bin/Debug/net8.0/NativeDll.dll
```

该文件时间戳为 2026-08-26，缺少当前 C# 代码要求的批量提交导出。

## 说明

- 本基线没有修改源代码；
- C++ 常规测试通过不能证明 Shutdown、异常和窄窗口竞态不存在；
- C# Native stress 的加载失败将作为阶段 4 的专门回归问题处理；
- 后续每阶段必须与本基线比较吞吐、延迟和压力测试结果。

> 注：基线记录时使用了 quiet 输出，未把编译器警告完整列出；后续以
> “0 error、6 个既有 CS0649 warning”为准。该警告不属于本轮改动。
