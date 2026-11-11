using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;

namespace EntJoy.ECS
{
    public unsafe partial class EntityManager : IDisposable
    {
        // 记账标记：1 = 可能还有 Job 记录（保守），0 = 确定全空。结构变更快路径只读它一个。
        private int _jobTrackingDirty;

        /// <summary>记账是否已确定全空。</summary>
        internal bool JobTrackingIsIdle => Volatile.Read(ref _jobTrackingDirty) == 0;

        internal void CompleteActiveJobs()
        {
            EnsureStructuralChangeAllowed();
            EnsureNoJobExecuting();

            JobHandle[] jobs;
            lock (_activeJobLock)
            {
                if (_activeJobs.Count == 0)
                {
                    ClearJobTrackingNoLock();
                    ReleaseLastWritersAndClear();
                    DrainPendingNativeEvents();
                    return;
                }
                jobs = _activeJobs.ToArray();
                _activeJobs.Clear();
                // 下面会 Complete 全部 Job，记账表随之整体失效。
                ClearJobTrackingNoLock();
            }

            CompleteAll(jobs);

            // Complete 消费过的依赖句柄已 detach，Release 是空操作；未完成的留给句柄自身回收。
            ReleaseLastWritersAndClear();
            // 顺序要求：Job 全部完成后才能 drain 事件 buffer，否则 worker 仍在写时主线程已释放。
            DrainPendingNativeEvents();
        }

        // 结构变更禁止发生在组件拷贝钩子、组件析构、观察者回调内部：前两者会破坏正在复制的数据，
        // 后者持有指向块内组件列的指针。需要时改用 DeferredCommandBuffer。
        private static void EnsureStructuralChangeAllowed()
        {
            if (ComponentTypeManager.IsCopyingComponent)
                throw new InvalidOperationException("ECS changes are not allowed inside component copy hooks.");
            if (ComponentTypeManager.IsDisposingComponent)
                throw new InvalidOperationException("ECS changes are not allowed inside component Dispose. Use DeferredCommandBuffer.");
            if (s_observerDepth > 0)
                throw new InvalidOperationException(
                    "Structural changes are not allowed inside an observer callback. Use DeferredCommandBuffer to defer structural changes.");
        }

        private static void EnsureNoJobExecuting()
        {
            if (NativeJobScheduler.IsExecutingJob)
                throw new InvalidOperationException(
                    "Structural changes are not allowed while a scheduled job is executing. Complete the job before modifying entities or components.");
        }

        // 一个 Job 抛异常也必须完成其余 Job：它们已从记账表移除，仍在后台写内存。
        private static void CompleteAll(JobHandle[] jobs)
        {
            ExceptionDispatchInfo? pending = null;
            for (int i = 0; i < jobs.Length; i++)
            {
                try
                {
                    jobs[i].Complete();
                }
                catch (Exception ex)
                {
                    pending ??= ExceptionDispatchInfo.Capture(ex);
                }
            }
            pending?.Throw();
        }

        /// <summary>drain 本 World 待处理的 Native SendEvent buffers（Job 完成后事件才可读）。</summary>
        private void DrainPendingNativeEvents()
        {
            // 标志只在持锁入队后置 1、持锁清空时置 0，所以为空时可以不取锁。
            if (Volatile.Read(ref _pendingNativeEventsPresent) == 0) return;

            List<(IntPtr contextPtr, Type jobType, World world)> pending;
            lock (_pendingNativeEventsLock)
            {
                pending = new List<(IntPtr contextPtr, Type jobType, World world)>(_pendingNativeEvents);
                _pendingNativeEvents.Clear();
                Volatile.Write(ref _pendingNativeEventsPresent, 0);
            }

            Exception first = null;
            foreach (var (contextPtr, jobType, world) in pending)
            {
                try { ChunkJobScheduler.DrainAndFreeEventBuffers(contextPtr, world, jobType); }
                catch (Exception ex) { first ??= ex; }
            }
            if (first != null) throw first;
        }

        // 系统间依赖：组件类型 → 最后写/读它的 Job。写表覆盖，读表合并（多个读系统可同时在飞）。

        /// <summary>记录组件类型的最后写入 Job；空句柄表示清除。</summary>
        internal void SetLastWriter(ComponentType componentType, JobHandle handle)
        {
            if (handle.IsNull)
            {
                _lastWritePerComponent.Remove(componentType.Id);
                return;
            }
            if (_lastWritePerComponent.TryGetValue(componentType.Id, out var old) &&
                old._nativeHandle.Handle != handle._nativeHandle.Handle)
            {
                ReleaseCompletedHandle(old);
            }
            Volatile.Write(ref _jobTrackingDirty, 1);
            _lastWritePerComponent[componentType.Id] = handle;
        }

        /// <summary>记录组件类型的读取依赖；空句柄表示本次没有新增读依赖，不影响已有记录。</summary>
        internal void SetLastReader(ComponentType componentType, JobHandle handle)
        {
            if (handle.IsNull) return;
            Volatile.Write(ref _jobTrackingDirty, 1);

            if (!_lastReadPerComponent.TryGetValue(componentType.Id, out var old))
            {
                _lastReadPerComponent[componentType.Id] = handle;
                return;
            }
            if (old._nativeHandle.Handle == handle._nativeHandle.Handle) return;
            // 旧读已完成或句柄已 detach ， 不必再等它。
            bool oldDone = !old._nativeHandle.IsValid || old._nativeHandle.IsCompletedFast;
            _lastReadPerComponent[componentType.Id] = oldDone ? handle : JobHandle.CombineDependencies(old, handle);
        }

        internal JobHandle GetLastReader(ComponentType componentType)
            => _lastReadPerComponent.TryGetValue(componentType.Id, out var handle) ? handle : default;

        internal JobHandle GetLastWriter(ComponentType componentType)
            => _lastWritePerComponent.TryGetValue(componentType.Id, out var handle) ? handle : default;

        /// <summary>清除组件类型的读依赖：这次写已经等过所有读。</summary>
        internal void ClearLastReader(ComponentType componentType)
            => _lastReadPerComponent.Remove(componentType.Id);

        /// <summary>释放依赖表中已完成条目的原生句柄并清表（全量等待后、结构变更前调用）。</summary>
        private void ReleaseLastWritersAndClear()
        {
            ReleaseCompletedHandles(_lastWritePerComponent);
            ReleaseCompletedHandles(_lastReadPerComponent);
        }

        private static void ReleaseCompletedHandles(Dictionary<int, JobHandle> table)
        {
            if (table.Count == 0) return;
            foreach (var entry in table)
                ReleaseCompletedHandle(entry.Value);
            table.Clear();
        }

        // 只有已完成的句柄可以当场释放原生引用；待抛 Job 异常时不动，否则异常会随句柄一起丢。
        private static void ReleaseCompletedHandle(JobHandle handle)
        {
            if (NativeJobCore.HasPendingJobExceptions) return;
            if (!handle._nativeHandle.IsCompletedFast) return;
            NativeJobScheduler.Release(handle._nativeHandle);
        }

        /// <summary>
        /// 结构变更前等待访问该实体所在原型（以及组件迁移目标原型）的 Job。
        /// 实体已销毁或越界时退化为全量等待。
        /// </summary>
        private void CompleteEntityJobs(Entity entity, Archetype? extra = null)
        {
            EnsureStructuralChangeAllowed();

            if ((uint)entity.Id >= (uint)entities.Length)
            {
                CompleteActiveJobs();
                return;
            }
            ref var info = ref GetEntityInfoRef(entity.Id);
            if (info.Archetype == null)
            {
                CompleteActiveJobs();
                return;
            }
            if (extra == null || extra == info.Archetype)
            {
                // 单元素走栈上 span，避免每个实体一次数组分配。
                Archetype single = info.Archetype;
                CompleteArchetypeJobs(MemoryMarshal.CreateReadOnlySpan(ref single, 1));
            }
            else
            {
                CompleteArchetypeJobs(new[] { info.Archetype, extra });
            }
        }

        /// <summary>从原型移除实体槽位，并修正 swap-pop 搬移实体的位置索引。</summary>
        private void RemoveAndFixup(Archetype arch, int chunkIndex, int slotInChunk,
            bool continueOnDisposeError = false, bool skipDisposal = false)
        {
            var result = arch.Remove(chunkIndex, slotInChunk, continueOnDisposeError, skipDisposal);
            if (result.MovedEntityId >= 0)
                MoveSlotWithinChunk(result.MovedEntityId, result.MovedEntitySlot);
        }

        /// <summary>swap-pop 只在同一块内挪槽位：原型、块基址、列偏移都不变。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void MoveSlotWithinChunk(int entityId, int slotInChunk)
        {
            entities[entityId].SlotInChunk = slotInChunk;
            if ((uint)entityId < (uint)_locateBCapacity) _locateB[entityId].SlotInChunk = slotInChunk;
        }

        /// <summary>重算记账是否全空。调用者必须已保证没有在飞 Job。</summary>
        private void RefreshJobTrackingDirty()
        {
            // 只数真的持有 Job 的条目：_archetypeJobs 的键登记后不删除，它的 Count 不能当判据。
            int any = _activeJobs.Count | _unscopedJobs.Count
                    | _jobWrittenComponents.Count | _lastWritePerComponent.Count | _lastReadPerComponent.Count;
            foreach (var entry in _archetypeJobs)
                any |= entry.Value.Count;
            Volatile.Write(ref _jobTrackingDirty, any == 0 ? 0 : 1);
        }

        /// <summary>清空 Job 记账结构。调用者必须已保证没有在飞 Job。</summary>
        private void ClearJobTrackingNoLock()
        {
            _unscopedJobs.Clear();
            _archetypeJobs.Clear();
            _jobWrittenComponents.Clear();
            RefreshJobTrackingDirty();
        }

        /// <summary>把已完成句柄从各记账结构剪掉，并决定性释放其原生引用。</summary>
        private void PruneCompletedJobsNoLock()
        {
            var completed = _completedJobs;
            completed.Clear();

            for (int i = _activeJobs.Count - 1; i >= 0; i--)
            {
                if (!_activeJobs[i].IsCompleted) continue;
                completed.Add(_activeJobs[i]);
                _activeJobs.RemoveAt(i);
            }
            foreach (var entry in _archetypeJobs)
                RemoveCompleted(entry.Value);
            RemoveCompleted(_unscopedJobs);

            if (completed.Count != 0)
            {
                for (int i = 0; i < completed.Count; i++)
                    _jobWrittenComponents.Remove(completed[i]);
                if (!NativeJobCore.HasPendingJobExceptions)
                {
                    for (int i = 0; i < completed.Count; i++)
                        NativeJobScheduler.Release(completed[i]._nativeHandle);
                }
                completed.Clear();
            }

            RefreshJobTrackingDirty();
        }

        private static void RemoveCompleted(List<JobHandle> list)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].IsCompleted) list.RemoveAt(i);
            }
        }

        /// <summary>登记 Job：全局列表 + 按原型的列表；没有原型归属的记入保守等待集合。</summary>
        internal void TrackEntityJob(JobHandle handle, Archetype[]? matchingArchetypes, ComponentType[]? writtenComponents = null)
        {
            if (!handle._nativeHandle.IsValid && handle._managedHandle.Completion == null) return;

            lock (_activeJobLock)
            {
                PruneCompletedJobsNoLock();
                Volatile.Write(ref _jobTrackingDirty, 1);
                _activeJobs.Add(handle);

                if (writtenComponents != null && writtenComponents.Length > 0)
                    _jobWrittenComponents[handle] = writtenComponents;

                if (matchingArchetypes != null && matchingArchetypes.Length > 0)
                {
                    for (int i = 0; i < matchingArchetypes.Length; i++)
                    {
                        int id = matchingArchetypes[i].GetHashCode();
                        if (!_archetypeJobs.TryGetValue(id, out var list))
                        {
                            list = new List<JobHandle>();
                            _archetypeJobs[id] = list;
                        }
                        list.Add(handle);
                    }
                }
                else
                {
                    // 归属未知 ， 选择性等待看不见它，只能由保守等待兜底。
                    _unscopedJobs.Add(handle);
                }
            }
        }

        /// <summary>
        /// 只等待访问受影响原型的 Job。affectedComponentTypes 非空时按写入组件过滤，
        /// 没有写入信息的 Job 保守等待。归属未知的 Job 一律参与等待。
        /// </summary>
        internal void CompleteArchetypeJobs(ReadOnlySpan<Archetype> affectedArchetypes, ComponentType[]? affectedComponentTypes = null)
        {
            EnsureNoJobExecuting();

            JobHandle[]? jobs;
            lock (_activeJobLock)
            {
                if (_activeJobs.Count == 0) return;

                var affected = new HashSet<JobHandle>();
                for (int i = 0; i < affectedArchetypes.Length; i++)
                {
                    if (!_archetypeJobs.TryGetValue(affectedArchetypes[i].GetHashCode(), out var list)) continue;
                    for (int j = 0; j < list.Count; j++)
                    {
                        var handle = list[j];
                        if (affectedComponentTypes != null && affectedComponentTypes.Length > 0 &&
                            !WritesAny(handle, affectedComponentTypes)) continue;
                        affected.Add(handle);
                    }
                }

                for (int i = 0; i < _unscopedJobs.Count; i++)
                    affected.Add(_unscopedJobs[i]);

                if (affected.Count == 0) return;
                jobs = affected.ToArray();
            }

            CompleteAll(jobs);

            lock (_activeJobLock)
            {
                PruneCompletedJobsNoLock();
            }
        }

        // 没有写入组件信息的 Job 视为写入一切。
        private bool WritesAny(JobHandle handle, ComponentType[] componentTypes)
        {
            if (!_jobWrittenComponents.TryGetValue(handle, out var written)) return true;
            for (int w = 0; w < written.Length; w++)
            {
                for (int a = 0; a < componentTypes.Length; a++)
                {
                    if (written[w] == componentTypes[a]) return true;
                }
            }
            return false;
        }
    }
}
