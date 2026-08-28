using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EntJoy.ECS
{
    /// <summary>
    /// Observer 批量回调：一次回调拿整批实体 + 组件值（零拷贝 span）。
    /// 事件位由注册桶决定（Added 桶 = Added 事件），values 即对应语义的值。
    /// 用自定义委托而非 <see cref="Action{T1,T2}"/>：ReadOnlySpan 是 ref struct，
    /// 不能作为 Action 的泛型类型参数（CS9244），但可直接作委托签名参数。
    /// </summary>
    public delegate void ObserverCallback<TComponent>(ReadOnlySpan<Entity> entities, ReadOnlySpan<TComponent> values) where TComponent : unmanaged;

    /// <summary>
    /// 非泛型派发接口：挂钩点（AddComponentRaw 等）只认识它，不感知具体组件类型。
    /// 批量语义：一次派发携带实体数组 + 组件值指针 + count，泛型实现内部零拷贝转 span 调回调。
    /// </summary>
    internal unsafe interface IComponentObserver
    {
        ObserverHandle Handle { get; }
        /// <summary>Added / Set 事件：entities 指向 count 个连续 Entity，valuesPtr 指向 count 个连续 T（新值）。</summary>
        void DispatchAdded(Entity* entities, void* valuesPtr, int count);
        /// <summary>Removed 事件：entities + oldValuesPtr（旧值快照）。</summary>
        void DispatchRemoved(Entity* entities, void* oldValuesPtr, int count);
    }

    /// <summary>
    /// 泛型实现：注册时由 AddObserver&lt;T&gt; 实例化。
    /// </summary>
    internal sealed unsafe class ComponentObserver<T> : IComponentObserver where T : unmanaged
    {
        private readonly ObserverCallback<T> _callback;
        public ObserverHandle Handle { get; }
        public readonly ObserverEvents Events;

        public ComponentObserver(ObserverHandle handle, ObserverEvents events, ObserverCallback<T> callback)
        {
            Handle = handle;
            Events = events;
            _callback = callback;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DispatchAdded(Entity* entities, void* valuesPtr, int count)
        {
            // Added 桶与 Set 桶共用此方法（新值语义）；注册时按事件位入桶
            if ((Events & (ObserverEvents.Added | ObserverEvents.Set)) == 0 || count <= 0) return;
            _callback(new ReadOnlySpan<Entity>(entities, count), new ReadOnlySpan<T>(valuesPtr, count));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DispatchRemoved(Entity* entities, void* oldValuesPtr, int count)
        {
            if ((Events & ObserverEvents.Removed) == 0 && (Events & ObserverEvents.Destroyed) == 0) return;
            if (count <= 0) return;
            _callback(new ReadOnlySpan<Entity>(entities, count), new ReadOnlySpan<T>(oldValuesPtr, count));
        }
    }

    /// <summary>
    /// 每个组件类型的注册表：按事件位分桶。同一组件类型的多个注册共享同一 T。
    /// </summary>
    internal sealed class ObserverRegistry
    {
        public List<IComponentObserver> Added = new();
        public List<IComponentObserver> Removed = new();
        public List<IComponentObserver> Set = new();  // Set 桶复用 DispatchAdded（新值语义）
    }
}
