using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EntJoy.ECS
{
    /// <summary>
    /// Observer 注册表 + 主线程结构变更挂钩（single funnel）。
    /// 零订阅者 fast path：<see cref="_observerCount"/> 为 0 时所有挂钩点零额外分支。
    /// 回调统一在主线程执行（立即或 ECB Playback 派发）；回调内结构变更请走 ECB（<see cref="s_observerDepth"/> 检测）。
    /// 批量语义：同一结构变更 API 调用内对同一 observer 的连续触发 → 一次回调（ReadOnlySpan）。
    /// </summary>
    public unsafe partial class EntityManager
    {
        private Dictionary<int, ObserverRegistry> _observers;
        private int _observerCount;
        private int _nextObserverId;

        // 重入保护：派发回调期间结构变更 API 应提示走 ECB（对齐现有 Job 内禁令风格）
        [ThreadStatic] private static int s_observerDepth;

        internal static int ObserverDepth => s_observerDepth;

        // ======================== 注册 API ========================

        /// <summary>
        /// 注册组件生命周期 observer。回调在主线程执行（立即或 ECB Playback 派发）。
        /// 批量签名：一次回调拿整批实体 + 组件值（零拷贝 span）。事件位由注册桶决定。
        /// </summary>
        public ObserverHandle AddObserver<TComponent>(ObserverEvents events, ObserverCallback<TComponent> callback)
            where TComponent : unmanaged
        {
            CheckDisposed();
            _observers ??= new Dictionary<int, ObserverRegistry>();
            var compType = ComponentTypeManager.GetComponentType(typeof(TComponent));
            int typeId = compType.Id;

            int handleId = ++_nextObserverId;
            if (handleId <= 0) handleId = 1; // 防止 int 溢出回绕到 0（Invalid）
            var handle = new ObserverHandle(handleId);
            var entry = new ComponentObserver<TComponent>(handle, events, callback);

            if (!_observers.TryGetValue(typeId, out var reg))
            {
                reg = new ObserverRegistry();
                _observers[typeId] = reg;
            }
            if ((events & ObserverEvents.Added) != 0) reg.Added.Add(entry);
            if ((events & ObserverEvents.Removed) != 0 || (events & ObserverEvents.Destroyed) != 0) reg.Removed.Add(entry);
            if ((events & ObserverEvents.Set) != 0) reg.Set.Add(entry);

            _observerCount++;
            return handle;
        }

        /// <summary>移除指定句柄的 observer。</summary>
        public void RemoveObserver<TComponent>(ObserverHandle handle) where TComponent : unmanaged
        {
            if (!handle.IsValid) return;
            if (_observers == null) return;
            CheckDisposed();
            var compType = ComponentTypeManager.GetComponentType(typeof(TComponent));
            if (!_observers.TryGetValue(compType.Id, out var reg)) return;

            bool removed = RemoveFromList(reg.Added, handle) | RemoveFromList(reg.Removed, handle) | RemoveFromList(reg.Set, handle);
            if (removed) _observerCount--;
        }

        /// <summary>清空某组件类型的所有 observer。</summary>
        public void ClearObservers<TComponent>() where TComponent : unmanaged
        {
            if (_observers == null) return;
            CheckDisposed();
            var compType = ComponentTypeManager.GetComponentType(typeof(TComponent));
            if (!_observers.TryGetValue(compType.Id, out var reg)) return;

            _observerCount -= reg.Added.Count + reg.Removed.Count + reg.Set.Count;
            _observers.Remove(compType.Id);
        }

        private static bool RemoveFromList(List<IComponentObserver> list, ObserverHandle handle)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Handle.Equals(handle))
                {
                    list.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        // ======================== 派发工具（批量，锁外调用） ========================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool HasObservers(int typeId)
        {
            if (_observerCount == 0) return false;
            return _observers != null && _observers.ContainsKey(typeId);
        }

        /// <summary>
        /// 批量派发 Added/Set（新值语义）：对注册表快照逐 observer 调用，值指针零拷贝转 span。
        /// 调用方保证在主线程、锁外（回调内结构变更需走 ECB）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void DispatchAdded(List<IComponentObserver> list, Entity* entities, void* valuesPtr, int count)
        {
            var snapshot = list.ToArray();
            s_observerDepth++;
            try
            {
                for (int i = 0; i < snapshot.Length; i++)
                {
                    try { snapshot[i].DispatchAdded(entities, valuesPtr, count); }
                    catch (Exception ex) { RecordObserverException(ex); }
                }
            }
            finally
            {
                s_observerDepth--;
            }
        }

        /// <summary>
        /// 批量派发 Removed（旧值快照语义）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void DispatchRemoved(List<IComponentObserver> list, Entity* entities, void* oldValuesPtr, int count)
        {
            var snapshot = list.ToArray();
            s_observerDepth++;
            try
            {
                for (int i = 0; i < snapshot.Length; i++)
                {
                    try { snapshot[i].DispatchRemoved(entities, oldValuesPtr, count); }
                    catch (Exception ex) { RecordObserverException(ex); }
                }
            }
            finally
            {
                s_observerDepth--;
            }
        }

        private void RecordObserverException(Exception ex)
        {
            // 对齐 NativeJobCore.RecordJobException 风格：记录但不中断。
            System.Console.Error.WriteLine($"[Observer] callback threw: {ex}");
        }
    }
}
