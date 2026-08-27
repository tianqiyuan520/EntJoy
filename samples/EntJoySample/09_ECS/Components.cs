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

    /// <summary>碰撞事件（Event Channel 测试用，blittable）。</summary>
    public struct CollisionEvent
    {
        public Entity A;
        public Entity B;
        public float Force;
    }

    /// <summary>伤害事件（Event Channel 测试用，blittable）。</summary>
    public struct DamageEvent
    {
        public Entity Target;
        public int Amount;
    }

    /// <summary>可启用组件，用于测试 EnabledComponent 过滤。</summary>
    public struct ActiveComponent : IComponentData, IEnableableComponent
    {
        public bool IsActive;
    }

    // Shared Component 类型已移至 SharedComponentDemo.cs（独立声明避免跨文件类型冲突）
}
