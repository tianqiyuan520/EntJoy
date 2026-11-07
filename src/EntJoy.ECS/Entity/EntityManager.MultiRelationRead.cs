using System;
using EntJoy.Collections;

namespace EntJoy.ECS
{
    public unsafe partial class EntityManager
    {
        /// <summary>复制有效目标到调用方缓冲；返回所需总长度，缓冲不足时写入可容纳的前缀。</summary>
        public int GetRelationships<TRel>(Entity source, Span<Entity> destination) where TRel : struct, IRelationComponent
        {
            CheckDisposed();
            var type = ComponentTypeManager.GetComponentType(typeof(TRel));
            RequireMultiRelation(type);
            if (!IsAlive(source)) return 0;
            int count = 0;
            if (type.MultiRelationMaxSlots == 0)
            {
                var targets = _relationListStore.TryGetTargets(type.Id, source);
                if (targets != null) foreach (var slot in targets)
                    if (IsAlive(slot.ToEntity()))
                    {
                        if (count < destination.Length) destination[count] = slot.ToEntity();
                        count++;
                    }
            }
            else if (GetEntityInfoRef(source.Id).Archetype.Has(type.Type))
            {
                var slots = (RelationSlot*)GetRelationValue(source, type);
                for (int i = 0; i < type.MultiRelationMaxSlots; i++)
                    if (slots[i].IsValid && IsAlive(slots[i].ToEntity()))
                    {
                        if (count < destination.Length) destination[count] = slots[i].ToEntity();
                        count++;
                    }
            }
            return count;
        }

        /// <summary>正向目标快照归调用方所有，可通过只读 NativeArray 交给 Managed/C++/ISPC Job。</summary>
        public void CopyRelationships<TRel>(Entity source, NativeList<Entity> destination) where TRel : struct, IRelationComponent
        {
            CheckDisposed();
            if (destination.GetListData() == null) throw new ArgumentException("Destination NativeList must be initialized.");
            int count = GetRelationships<TRel>(source, Span<Entity>.Empty);
            destination.Resize(count, NativeArrayOptions.UninitializedMemory);
            GetRelationships<TRel>(source, new Span<Entity>(destination.GetUnsafePtr(), count));
        }
    }
}
