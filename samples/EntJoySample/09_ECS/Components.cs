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
}