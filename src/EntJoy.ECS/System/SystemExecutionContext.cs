using System;
using EntJoy.JobSystem;

namespace EntJoy.ECS
{
    /// <summary>
    /// 系统执行上下文（线程静态）：SystemRunner 执行系统时写入，系统内调度的 Job
    /// 自动继承并回写 Dependency。OnUpdate 同步执行，ThreadStatic 足够；不支持嵌套 Update。
    /// </summary>
    internal static class SystemExecutionContext
    {
        [ThreadStatic] internal static bool IsActive;
        [ThreadStatic] internal static JobHandle Dependency;
    }
}
