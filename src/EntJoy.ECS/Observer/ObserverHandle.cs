using System;

namespace EntJoy.ECS
{
    /// <summary>
    /// Observer 订阅句柄（不透明令牌，用于 RemoveObserver）。
    /// Id 从 1 递增分配；0 = Invalid。
    /// </summary>
    public readonly struct ObserverHandle : IEquatable<ObserverHandle>
    {
        public readonly int Id;

        public static readonly ObserverHandle Invalid = default;

        public ObserverHandle(int id) { Id = id; }

        public bool IsValid => Id != 0;

        public bool Equals(ObserverHandle other) => Id == other.Id;
        public override bool Equals(object? obj) => obj is ObserverHandle other && Equals(other);
        public override int GetHashCode() => Id;

        public static bool operator ==(ObserverHandle left, ObserverHandle right) => left.Id == right.Id;
        public static bool operator !=(ObserverHandle left, ObserverHandle right) => left.Id != right.Id;

        public override string ToString() => $"ObserverHandle({Id})";
    }
}
