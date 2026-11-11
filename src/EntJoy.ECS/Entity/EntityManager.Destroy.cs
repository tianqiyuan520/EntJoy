using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using EntJoy.Collections;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;

namespace EntJoy.ECS
{
    public unsafe partial class EntityManager : IDisposable
    {
        /// <summary>
        /// 批量销毁：一次 <c>CompleteActiveJobs</c> + 一次锁处理整批，
        /// 避免"逐实体 DestroyEntity"时每条命令都等一遍在飞 job。
        /// 无效/已销毁/版本不匹配的项被静默跳过（返回实际销毁数）。
        /// </summary>
        public unsafe int DestroyEntities(Entity* targets, int count)
        {
            CheckDisposed();
            if (targets == null || count <= 0) return 0;
            CompleteActiveJobs();
            using var changes = BeginRelationChanges();
            using (EnterStructural()) return DestroyRelationGraph(new ReadOnlySpan<Entity>(targets, count));
        }

        /// <summary>清空某 Archetype 的全部实体：代价与 Chunk 数成正比而非实体数——
        /// 逐实体只清两表并回池，Chunk 与 slab 整批释放。返回数含级联销毁。</summary>
        public unsafe long DestroyAllInArchetype(Archetype archetype)
        {
            CheckDisposed();
            if (archetype == null) return 0;
            CompleteActiveJobs();
            using var changes = BeginRelationChanges();
            using (EnterStructural())
            {
                // 有关系列、观察者或可释放组件时，必须逐实体走完整的销毁路径。
                if (NeedsLifecyclePass(archetype)) return DestroyRelationGraph(CollectArchetypeEntities(archetype));

                long destroyed = 0;
                var chunks = archetype.ChunkList;
                for (int c = 0; c < chunks.Count; c++)
                {
                    var chunk = chunks[c];
                    int ec = chunk.EntityCount;
                    for (int slot = 0; slot < ec; slot++)
                    {
                        if (RetireEntityRow(chunk.GetEntity(slot))) destroyed++;
                    }
                }
                archetype.ClearAllEntities();
                InvalidateStructuralQueries();
                return destroyed;
            }
        }

        private bool NeedsLifecyclePass(Archetype archetype)
        {
            bool needsLifecycle = !_relationIndex.IsEmpty || _observerCount > 0;
            foreach (var type in archetype.Types) needsLifecycle |= type.IsRelation || type.IsDisposable;
            return needsLifecycle;
        }

        private Entity[] CollectArchetypeEntities(Archetype archetype)
        {
            var targets = new Entity[checked((int)archetype.EntityCount)];
            int index = 0;
            foreach (var chunk in archetype.ChunkList)
                for (int slot = 0; slot < chunk.EntityCount; slot++) targets[index++] = chunk.GetEntity(slot);
            return targets;
        }

        // 快路径的单实体退役：清位置、清反向索引条目、id 回池。Version 自增避免悬垂引用。
        private bool RetireEntityRow(Entity e)
        {
            if ((uint)e.Id >= (uint)entities.Length) return false;
            ref var info = ref entities[e.Id];
            info.Archetype = null;
            info.ClearLocation();
            ClearLocateB(e.Id);
            // 本原型无关系列，实体自身没有出边，但可能被别人指向；不清会永久残留死 id 的条目。
            _relationIndex.ClearTarget(e);
            PushRecycled(e);
            return true;
        }

        public void DestroyEntity(Entity entity)
        {
            CheckDisposed();
            bool idle = JobBookkeepingIdle();
            if (idle && _relationIndex.IsEmpty)
            {
                // 无 job、无关系：关系图遍历与重复校验都无事可做，核心自己做一次性校验。
                using (EnterStructural())
                {
                    DestroyEntityCore(entity, observersAbsent: _observerCount == 0);
                }
                return;
            }
            // 无 job 在册时不必走完成与记账清理，直接进结构锁。
            if (!idle)
            {
                if (!_relationIndex.IsEmpty && _relationIndex.TryGetSources(entity, out _)) CompleteActiveJobs();
                else CompleteEntityJobs(entity);
            }
            using var changes = BeginRelationChanges();
            using (EnterStructural())
            {
                ValidateEntityForDestruction(entity);
                if (!IsAlive(entity)) return;
                DestroyRelationGraph(System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref entity, 1));
            }
        }

        /// <summary>无 observer、无在册 job、无原生事件时为真：结构变更可跳过 job 完成与记账清理。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool JobBookkeepingIdle()
        {
            return s_observerDepth == 0
                && !ComponentTypeManager.IsCopyingComponent
                && !ComponentTypeManager.IsDisposingComponent
                && !NativeJobScheduler.IsExecutingJob
                && System.Threading.Volatile.Read(ref _jobTrackingDirty) == 0
                && System.Threading.Volatile.Read(ref _pendingNativeEventsPresent) == 0;
        }
        /// <summary>取实体项并校验仍存活、版本一致；不满足时抛出。返回的引用可直接继续写位置。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ref EntityIndexInWorld GetAliveInfoRef(Entity entity)
        {
            ref var info = ref GetEntityInfoRef(entity.Id);
            if (info.Archetype == null)
                throw new InvalidOperationException($"Entity {entity} has been destroyed.");
            if (info.Version != entity.Version)
                throw new InvalidOperationException($"Entity {entity} is a stale reference (version mismatch).");
            return ref info;
        }
        private void ValidateEntityForDestruction(Entity entity)
        {
            if ((uint)entity.Id >= (uint)entities.Length || GetEntityInfoRef(entity.Id).Version != entity.Version)
                throw new InvalidOperationException($"Entity {entity} has an invalid ID or stale version.");
        }

        /// <summary>校验 source 是否通过指定关系类型指向 target。</summary>
        private bool StillPointsToRelation(Entity source, Entity target, int relTypeId)
        {
            if ((uint)source.Id >= (uint)entities.Length) return false;
            ref var info = ref GetEntityInfoRef(source.Id);
            if (info.Archetype == null || info.Version != source.Version) return false;

            var type = ComponentTypeManager.GetTypeByComponentType(relTypeId);
            var compType = ComponentTypeManager.GetComponentType(type);

            // 定长多槽列（[MultiRelation(MaxSlots=N)]）：逐槽校验
            if (compType.MultiRelationMaxSlots >= 2)
            {
                if (!info.Archetype.Has(type)) return false;
                int compIdx = info.Archetype.GetComponentTypeIndex(compType);
                var chunk = info.Archetype.ChunkList[info.ChunkIndex];
                var basePtr = (byte*)chunk.GetComponentArrayPointer(compIdx) + info.SlotInChunk * compType.Size;
                for (int i = 0; i < compType.MultiRelationMaxSlots; i++)
                {
                    var slot = ((RelationSlot*)(basePtr + i * 8))[0];
                    if (slot.TargetId == target.Id && slot.TargetVersion == target.Version)
                        return true;
                }
                return false;
            }

            // 托管列表模式（[MultiRelation] 无 MaxSlots）：查正向列表
            if (compType.IsMultiRelation)
                return _relationListStore.Has(relTypeId, source, RelationSlot.From(target));

            // 单值列关系：校验列槽位
            if (!info.Archetype.Has(type)) return false;
            int compIdx1 = info.Archetype.GetComponentTypeIndex(compType);
            var chunk1 = info.Archetype.ChunkList[info.ChunkIndex];
            var slot1 = chunk1.GetComponentReadOnly<RelationSlot>(info.SlotInChunk, compIdx1);
            return slot1.TargetId == target.Id && slot1.TargetVersion == target.Version;
        }

        /// <summary>
        /// 销毁实体核心（调用方必须已持有 _structuralLock）。含 Observer Destroyed 派发。
        /// </summary>
        private unsafe void DestroyEntityCore(Entity entity, List<RelationNotification> preparedNotifications = null, bool notificationsPrepared = false, bool alreadyValidated = false, bool observersAbsent = false)
        {
            if (!alreadyValidated && (uint)entity.Id >= (uint)entities.Length)
                throw new InvalidOperationException($"Entity {entity} has an invalid ID.");
            ref var entityInfoRef = ref GetEntityInfoRef(entity.Id);
            if (!alreadyValidated && entityInfoRef.Version != entity.Version)
                throw new InvalidOperationException($"Entity {entity} is a stale reference (version mismatch).");
            var archetype = entityInfoRef.Archetype;
            if (archetype == null) return;
            int oldChunkIndex = entityInfoRef.ChunkIndex;
            var notifications = notificationsPrepared ? preparedNotifications : (observersAbsent ? null : CaptureDestroyedNotifications(entity));
            // 三项关系清理各自先判空：无关系的 World 里它们是纯开销。
            if (archetype.HasRelationships) CleanupSourceRelations(entity, entityInfoRef, archetype);
            CleanupMultiSourceRelations(entity);   // 内部在无多值关系数据时直接返回
            if (!_relationIndex.IsEmpty) _relationIndex.ClearTarget(entity);

            RemoveAndFixup(archetype, entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk, continueOnDisposeError: true);

            entityInfoRef.ClearLocation();   // 原型置空与打包地址清零一次写完
            ClearLocateB(entity.Id);
            PushRecycled(entity);
            InvalidateStructuralQueries();
            if (!_relationIndex.IsEmpty || archetype.HasRelationships) InvalidateRelationshipQueries();
            if (notifications != null)
                foreach (var notification in notifications) CommitRelationNotification(notification);
        }

    }
}
