using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EntJoy.ECS
{
    /// <summary>
    /// 非泛型派发接口：挂钩点（AddComponentRaw 等）只认识它，不感知具体组件类型。
    /// 每个方法的 valuePtr 指向一个 T 实例（快照或槽位内存）。
    /// </summary>
    internal unsafe interface IComponentObserver
    {
        ObserverHandle Handle { get; }
        void DispatchAdded(Entity entity, void* valuePtr);
        void DispatchRemoved(Entity entity, void* oldValuePtr);
        void DispatchSet(Entity entity, void* newValuePtr);
    }

    /// <summary>
    /// 泛型实现：注册时由 AddObserver&lt;T&gt; 实例化，内部把裸指针转成强类型事件并调用用户回调。
    /// 无反射、无装箱。
    /// </summary>
    internal sealed unsafe class ComponentObserver<T> : IComponentObserver where T : unmanaged
    {
        private readonly Action<ComponentEvent<T>> _callback;
        public ObserverHandle Handle { get; }
        public readonly ObserverEvents Events;

        public ComponentObserver(ObserverHandle handle, ObserverEvents events, Action<ComponentEvent<T>> callback)
        {
            Handle = handle;
            Events = events;
            _callback = callback;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DispatchAdded(Entity entity, void* valuePtr)
        {
            if ((Events & ObserverEvents.Added) == 0) return;
            _callback(new ComponentEvent<T>(entity, Unsafe.AsRef<T>(valuePtr), default, ObserverEvents.Added));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DispatchRemoved(Entity entity, void* oldValuePtr)
        {
            if ((Events & ObserverEvents.Removed) == 0 && (Events & ObserverEvents.Destroyed) == 0) return;
            _callback(new ComponentEvent<T>(entity, default, Unsafe.AsRef<T>(oldValuePtr), ObserverEvents.Removed));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DispatchSet(Entity entity, void* newValuePtr)
        {
            if ((Events & ObserverEvents.Set) == 0) return;
            _callback(new ComponentEvent<T>(entity, Unsafe.AsRef<T>(newValuePtr), default, ObserverEvents.Set));
        }
    }

    /// <summary>
    /// 每个组件类型的注册表：按事件位分桶。同一组件类型的多个注册共享同一 T。
    /// </summary>
    internal sealed class ObserverRegistry
    {
        public List<IComponentObserver> Added = new();
        public List<IComponentObserver> Removed = new();
        public List<IComponentObserver> Set = new();
    }
}
