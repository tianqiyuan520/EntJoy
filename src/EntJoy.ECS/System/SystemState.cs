using EntJoy.JobSystem;

namespace EntJoy.ECS
{
    /// <summary>
    /// 系统状态（对齐 DOTS SystemState.Dependency）：每帧由 SystemRunner 重建并传入
    /// <see cref="ISystemWithState.OnUpdate"/>。Dependency 为系统累积的未完成 Job 句柄，
    /// 同步读取组件数据前应调用 <see cref="CompleteDependency"/> 等待本系统所有 Job 完成。
    /// </summary>
    public struct SystemState
    {
        public JobHandle Dependency;

        public void CompleteDependency()
        {
            Dependency.Complete();
            Dependency = default;
        }
    }

    /// <summary>
    /// 带状态系统的扩展接口：OnUpdate 接收 ref SystemState，可显式读写
    /// state.Dependency（调度/合并依赖）或调用 state.CompleteDependency()。
    /// 系统 struct 须同时实现 <see cref="ISystem"/>（注册约束）。
    /// </summary>
    public interface ISystemWithState
    {
        void OnUpdate(ref SystemState state);
    }
}
