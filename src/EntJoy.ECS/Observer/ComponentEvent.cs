using System;

namespace EntJoy.ECS
{
    /// <summary>
    /// Observer 事件载荷（struct，零装箱）。
    /// <list type="bullet">
    /// <item>Added / Set：<see cref="NewValue"/> 有效。</item>
    /// <item>Removed / Destroyed：<see cref="OldValue"/> 有效（记录命令时或迁移前的快照）。</item>
    /// </list>
    /// </summary>
    public readonly struct ComponentEvent<T> where T : unmanaged
    {
        public readonly Entity Entity;
        public readonly T NewValue;
        public readonly T OldValue;
        public readonly ObserverEvents Flags;

        public ComponentEvent(Entity entity, T newValue, T oldValue, ObserverEvents flags)
        {
            Entity = entity;
            NewValue = newValue;
            OldValue = oldValue;
            Flags = flags;
        }

        public override string ToString()
            => $"ComponentEvent<{typeof(T).Name}> {Flags} Entity={Entity} New={NewValue} Old={OldValue}";
    }
}
