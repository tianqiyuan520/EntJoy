using System;

namespace EntJoy.ECS
{
    /// <summary>配置 IRelationComponent 的目标数量、层级约束和生命周期；默认单目标，目标删除时解除关系。</summary>
    [AttributeUsage(AttributeTargets.Struct)]
    public sealed class RelationshipComponentAttribute : Attribute
    {
        /// <summary>允许多个目标；默认使用主线程管理的目标列表。</summary>
        public bool Multiple { get; set; }

        /// <summary>固定目标容量，至少为 2；自动启用多目标，并生成 Job 可读的连续槽位。0 表示不指定固定容量。</summary>
        public int MaxTargets { get; set; }

        /// <summary>父子层级：每个实体至多一个父级，建立和重绑时拒绝成环。</summary>
        public bool Hierarchy { get; set; }

        /// <summary>同类型关系中，目标至多被一个来源持有；新来源接管时解除旧来源的对应关系。</summary>
        public bool ExclusiveTarget { get; set; }

        /// <summary>目标删除时一并删除来源；多目标关系中，任一目标删除都会触发。</summary>
        public bool DeleteWithTarget { get; set; }

        internal bool IsMultiple => Multiple || MaxTargets > 0;
    }
}
