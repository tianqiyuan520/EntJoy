namespace EntJoy.ECS
{
    /// <summary>
    /// World 级关系便捷入口，转发到 EntityManager 的关系操作。
    /// </summary>
    public partial class World
    {
        /// <summary>建立关系：entity --TRel--> target（首次结构变更，已有则覆盖写 8B）。</summary>
        public void AddRelationship<TRel>(Entity entity, Entity target)
            where TRel : struct, IRelationComponent
            => _entityManager.AddRelationship<TRel>(entity, target);

        /// <summary>移除关系（无则 no-op）。</summary>
        public void RemoveRelationship<TRel>(Entity entity)
            where TRel : struct, IRelationComponent
            => _entityManager.RemoveRelationship<TRel>(entity);

        /// <summary>读取关系 target（无关系/target 失效 → default(Entity)）。</summary>
        public Entity GetRelationship<TRel>(Entity entity)
            where TRel : struct, IRelationComponent
            => _entityManager.GetRelationship<TRel>(entity);

        /// <summary>是否持有有效关系（列存在 + 槽位有效 + target 存活版本匹配）。</summary>
        public bool HasRelationship<TRel>(Entity entity)
            where TRel : struct, IRelationComponent
            => _entityManager.HasRelationship<TRel>(entity);

        /// <summary>
        /// 级联销毁：销毁 entity 及所有关系指向它的实体（递归整棵子树，防环）。
        /// 走反向索引 O(1)，不扫描所有关系列。
        /// </summary>
        public void DestroyEntityCascade(Entity entity)
            => _entityManager.DestroyEntityCascade(entity);

        /// <summary>获取所有与 target 建立 TRel 关系的 source 实体（O(1) 索引查表）。</summary>
        public Entity[] GetRelationsOf<TRel>(Entity target)
            where TRel : struct, IRelationComponent
            => _entityManager.GetRelationsOf<TRel>(target);

        /// <summary>获取所有指向 target 的关系源（跨所有关系类型，O(1)）。</summary>
        public Entity[] GetRelationsOfAll(Entity target)
            => _entityManager.GetRelationsOfAll(target);

        /// <summary>获取 entity 的全部祖先（沿 TRel 链向上，最近祖先在前，防环）。</summary>
        public Entity[] GetAncestors<TRel>(Entity entity)
            where TRel : struct, IRelationComponent
            => _entityManager.GetAncestors<TRel>(entity);

        /// <summary>获取 entity 的全部后代（沿 TRel 链向下 BFS，直接子在前，防环，不含自身）。</summary>
        public Entity[] GetDescendants<TRel>(Entity entity)
            where TRel : struct, IRelationComponent
            => _entityManager.GetDescendants<TRel>(entity);

        /// <summary>获取 entity 的全部兄弟（与 entity 共享同一 TRel target 的其他实体，不含自身）。</summary>
        public Entity[] GetSiblings<TRel>(Entity entity)
            where TRel : struct, IRelationComponent
            => _entityManager.GetSiblings<TRel>(entity);
    }
}
