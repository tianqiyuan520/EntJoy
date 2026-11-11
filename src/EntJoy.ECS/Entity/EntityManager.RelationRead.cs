using System;
using System.Collections.Generic;
using EntJoy.Collections;

namespace EntJoy.ECS
{
    public unsafe partial class EntityManager
    {
        public bool IsRelationshipTargetAlive(RelationSlot target)
        {
            CheckDisposed();
            return target.IsValid && IsAlive(target.ToEntity());
        }

        /// <summary>逐实体关系条件，供查询运行时与生成的枚举代码使用。</summary>
        public bool MatchesRelationshipFilter(QueryBuilder query, Chunk chunk, int entityIndex)
        {
            CheckDisposed();
            if (!query.HasRelationshipFilter) return true;
            if ((uint)entityIndex >= (uint)chunk.EntityCount) throw new ArgumentOutOfRangeException(nameof(entityIndex));
            chunk.Archetype.CheckReadable();
            if (!IsRelationshipTargetAlive(query.RelationshipFilterTarget)) return false;
            var type = ComponentTypeManager.GetComponentType(query.RelationshipFilterType.Type);
            if (!type.IsRelation) throw new ArgumentException("Relationship filter requires a relationship type.");
            if (type.IsMultiRelation && type.MultiRelationMaxSlots == 0)
                return _relationListStore.Has(type.Id, chunk.GetEntity(entityIndex), query.RelationshipFilterTarget);
            if (!chunk.Archetype.Has(type.Type)) return false;
            int column = chunk.Archetype.GetComponentTypeIndex(type);
            byte* value = (byte*)chunk.GetComponentArrayPointer(column) + entityIndex * type.Size;
            for (int slot = 0; slot < Math.Max(1, type.MultiRelationMaxSlots); slot++)
                if (((RelationSlot*)value)[slot].Matches(query.RelationshipFilterTarget)) return true;
            return false;
        }

        private HashSet<Entity> GetRelationSources<TRel>(Entity target) where TRel : struct, IRelationComponent
        {
            CheckDisposed();
            int typeId = ComponentTypeManager.GetComponentType(typeof(TRel)).Id;
            if (!IsAlive(target)) return null;
            return _relationIndex.GetSourcesOfType(target, typeId);
        }

        /// <summary>写入有效 source，返回所需总数；缓冲不足时仅写入可容纳的前缀。</summary>
        public int GetRelationsOf<TRel>(Entity target, Span<Entity> destination) where TRel : struct, IRelationComponent
        {
            var sources = GetRelationSources<TRel>(target);
            if (sources == null) return 0;
            int typeId = ComponentTypeManager.GetComponentType(typeof(TRel)).Id;
            int count = 0;
            foreach (var source in sources)
            {
                if (!StillPointsToRelation(source, target, typeId)) continue;
                if (count < destination.Length) destination[count] = source;
                count++;
            }
            return count;
        }

        /// <summary>复制到调用方拥有的原生列表；不借用内部索引，可作为 Job 输入快照。</summary>
        public void CopyRelationsOf<TRel>(Entity target, NativeList<Entity> destination) where TRel : struct, IRelationComponent
        {
            CheckDisposed();
            if (destination.GetListData() == null) throw new ArgumentException("Destination NativeList must be initialized.", nameof(destination));
            var sources = GetRelationSources<TRel>(target);
            destination.EnsureCapacity(sources?.Count ?? 0);
            destination.Clear();
            if (sources == null) return;
            int typeId = ComponentTypeManager.GetComponentType(typeof(TRel)).Id;
            foreach (var source in sources)
                if (StillPointsToRelation(source, target, typeId)) destination.Add(source);
        }
    }
}
