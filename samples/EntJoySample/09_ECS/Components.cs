using EntJoy.ECS;

namespace EntJoySample.ECS
{
    public struct Position : IComponentData
    {
        public float X;
        public float Y;
    }

    public struct Velocity : IComponentData
    {
        public float X;
        public float Y;
    }

    public struct Health : IComponentData
    {
        public float Current;
        public float Max;
    }

    public struct DamageEvent : IComponentData { }

    /// <summary>可启用组件，用于测试 EnabledComponent 过滤。</summary>
    public struct ActiveComponent : IComponentData, IEnableableComponent
    {
        public bool IsActive;
    }

    // Shared Component 类型已移至 SharedComponentDemo.cs（独立声明避免跨文件类型冲突）
}
