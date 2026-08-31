# Stage 6 C++ exception-safety baseline

在异常实现修改前（2026-08-31）运行同一测试：

```text
Stage6_CppExceptionSafety.exe
BASELINE_FAILURE ScheduleFor Complete timed out
exit=1 (parent; child timeout exit=2)

Stage6_CppExceptionSafety.exe --cleanup
exit=-1073740791 (0xC0000409 STATUS_STACK_BUFFER_OVERRUN/terminate)
```

继续补充的基线（同一工作树、修复前无法安全地强制回退 scheduler）还覆盖了
backend reject、cleanup/依赖顺序和 batch-id 清理三个回归断言；这些断言在修复后
必须保持通过，若回退到修复前实现则分别会暴露 context 泄漏、依赖过早启动或线程
局部 batch-id 残留。

复现含义：

- `ScheduleFor` 回调异常从异步 lambda 逃逸，`CompleteState/ReleaseState` 未执行，
  句柄等待超时；
- General batch cleanup 异常穿过 `noexcept` 执行器，进程 terminate；
- 异步 backend 提交失败没有统一 cleanup/terminal 状态；
- batch cleanup 与依赖 continuation 的顺序及 worker 线程 batch-id 清理没有专门
  回归保护。

修复后同一测试输出 `PASS Stage6: 9/9`，并连续复跑 20 次无失败。该测试仍需在
ASAN/TSAN CI 中重复运行。
