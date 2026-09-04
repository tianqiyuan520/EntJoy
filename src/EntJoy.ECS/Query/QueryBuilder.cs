

using System;
using System.Collections.Generic;

namespace EntJoy.ECS
{
    /// <summary>
    /// 查询构建器
    /// </summary>
    public partial struct QueryBuilder
    {
        public int LimitCount; //限制选择的数量
        public ComponentType[] All;
        public ComponentType[] Any;
        public ComponentType[] None;
        public ComponentType[] AllEnabled;   // 必须存在且启用的组件

        // 变更追踪过滤
        public ComponentType[] ChangedComponents;  // 需要检查变更的组件类型
        public int MinChangedVersion;              // 最小版本号（用于帧级过滤）

        // Shared Component 过滤
        public ComponentType SharedFilterType;     // 要过滤的 shared 组件类型（default = 无过滤）
        public object SharedFilterValue;           // 目标 shared 值（boxed）
        public bool HasSharedFilter;

        // Relation 过滤（WithRelationship<T>(target)）
        public ComponentType RelationshipFilterType;   // 关系组件类型（default = 无过滤）
        public RelationSlot RelationshipFilterTarget;  // 目标 RelationSlot（含 target.Id + target.Version）
        public bool HasRelationshipFilter;

        public QueryBuilder()
        {
            LimitCount = -1;
            AllEnabled = [];
            ChangedComponents = null;
            MinChangedVersion = -1;
            HasSharedFilter = false;
            HasRelationshipFilter = false;
        }

        public QueryBuilder SetLimit(int count)
        {
            LimitCount = count;
            return this;
        }

        public QueryBuilder WithEnabled<T>() where T : struct, IEnableableComponent
        {
            // AllEnabled 默认初始化为 []（空数组）；单组件首次调用直接引用静态 Share（零分配）。
            // 约定：All/AllEnabled 数组对外只读（调度/匹配仅读取，不得原地修改元素）。
            if (AllEnabled == null || AllEnabled.Length == 0)
            {
                AllEnabled = ComponentTypes<T>.Share;
                return this;
            }
            AllEnabled = Merge(AllEnabled, ComponentTypes<T>.Share);
            return this;
        }

        public QueryBuilder WithAll<T>()
            where T : struct
        {
            // 单组件首调：直接引用静态 Share，零分配（v3 Phase 1.5）。
            if (All == null)
            {
                All = ComponentTypes<T>.Share;
                return this;
            }
            All = Merge(All, ComponentTypes<T>.Share);
            return this;
        }
        public QueryBuilder WithAll<T, T2>()
            where T : struct
            where T2 : struct
        {
            if (All == null)
            {
                All = ComponentTypes<T, T2>.Share;
                return this;
            }
            All = Merge(All, ComponentTypes<T, T2>.Share);
            return this;
        }

        //TODO

        public QueryBuilder WithAny<T>()
            where T : struct
        {
            if (Any == null) { Any = ComponentTypes<T>.Share; return this; }
            Any = Merge(Any, ComponentTypes<T>.Share);
            return this;
        }
        public QueryBuilder WithNone<T>() where T : struct
        {
            if (None == null) { None = ComponentTypes<T>.Share; return this; }
            None = Merge(None, ComponentTypes<T>.Share);
            return this;
        }

        // ======================== 变更追踪过滤 ========================

        /// <summary>只返回指定组件被修改过的实体。</summary>
        public QueryBuilder WithChanged<T>() where T : struct
        {
            var compType = ComponentTypeManager.GetComponentType(typeof(T));
            if (ChangedComponents == null || ChangedComponents.Length == 0)
            {
                ChangedComponents = new ComponentType[] { compType };
            }
            else
            {
                ChangedComponents = Merge(ChangedComponents, new ComponentType[] { compType });
            }
            return this;
        }

        /// <summary>只返回指定版本号之后被修改过的实体。</summary>
        public QueryBuilder ChangedSince(int version)
        {
            MinChangedVersion = version;
            return this;
        }

        // ======================== Shared Component 过滤 ========================

        /// <summary>
        /// 只处理持有指定 SharedComponent 值的 Chunk（对齐 Unity WithSharedComponentFilter）。
        /// 过滤发生在 chunk 收集阶段（C# 侧），与 NativeTranspiler 无交互。
        /// </summary>
        public QueryBuilder WithShared<T>(T filterValue) where T : ISharedComponentData
        {
            SharedFilterType = ComponentTypeManager.GetComponentType(typeof(T));
            SharedFilterValue = filterValue;
            HasSharedFilter = true;
            return this;
        }

        // ======================== Relation 过滤 ========================

        /// <summary>
        /// 只处理持有 <typeparamref name="T"/> 关系且 target == <paramref name="target"/> 的实体。
        /// 关系过滤：Archetype 匹配要求拥有 TRel 列（不拆 Archetype），
        /// chunk 收集期逐槽校验 RelationSlot.Matches（Id + Version 双匹配）。
        /// </summary>
        public QueryBuilder WithRelationship<T>(Entity target) where T : struct, IRelationComponent
        {
            RelationshipFilterType = ComponentTypeManager.GetComponentType(typeof(T));
            RelationshipFilterTarget = RelationSlot.From(target);
            HasRelationshipFilter = true;
            return this;
        }

        /// <summary>
        /// 合并两个只读类型数组（链式条件追加）：单次数组分配，避免 List+ToArray 两次分配。
        /// </summary>
        private static ComponentType[] Merge(ComponentType[] a, ComponentType[] b)
        {
            var merged = new ComponentType[a.Length + b.Length];
            Array.Copy(a, 0, merged, 0, a.Length);
            Array.Copy(b, 0, merged, a.Length, b.Length);
            return merged;
        }
    }

}
