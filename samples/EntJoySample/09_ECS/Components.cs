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
}
