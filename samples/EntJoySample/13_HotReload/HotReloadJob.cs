using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace EntJoySample.HotReload
{
    /// <summary>
    /// 热重载测试案例的**原生内核**。整个案例里"手动修改"就只有下面 <c>Execute</c> 里的那一个常量。
    /// <para>
    /// 改完**不要重启**进程：执行 <c>Program.cs</c> 启动时打印的那条 build 命令，
    /// 宿主会在安全点自动换掉 NativeTranspiled 模块，下一帧打印的值就变了。
    /// </para>
    /// </summary>
    [NativeTranspile(Target = BackendTarget.Cpp)]
    public struct HotReloadAddJob : IJobParallelFor
    {
        public NativeArray<int> Values;
        public int Delta;

        public void Execute(int index)
        {
            Values[index] += Delta + 1;
        }
    }
}
