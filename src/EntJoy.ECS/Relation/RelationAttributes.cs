using System;

namespace EntJoy.ECS
{
    /// <summary>
    /// 独占关系（真 1:1）：标记的关系类型在 AddRelationship 时，若 target 已被其他 source 指向，
    /// 自动解绑旧 source。出边单值（列存储），入边唯一。
    /// 与 <see cref="MultiRelationAttribute"/> 互斥。
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct)]
    public sealed class ExclusiveRelationAttribute : Attribute
    {
    }

    /// <summary>单目标关系必须无环；建立或重绑前校验同类型的目标链。</summary>
    [AttributeUsage(AttributeTargets.Struct)]
    public sealed class AcyclicRelationAttribute : Attribute
    {
    }

    /// <summary>
    /// 多值关系（出边 1:N / M:N）：source 可持有多个 target，两种存储模式按是否指定 <see cref="MaxSlots"/> 路由。
    /// <c>MaxSlots = 0</c>（默认）走主线程托管列表；<c>MaxSlots = N</c> 走定长多槽列，有界且 Job 可读。
    /// 两种模式共用反向索引（RelationIndex，target → sources O(1)）。
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct)]
    public sealed class MultiRelationAttribute : Attribute
    {
        /// <summary>定长槽位数量；0 = 托管列表模式（默认）。≥2 走定长多槽列。</summary>
        public int MaxSlots { get; set; }
    }

    /// <summary>
    /// 入边唯一约束（可叠加在 [MultiRelation] 上）：target 至多被一个 source 指向，
    /// 新 Add 自动解绑旧 source 的该 target 条目（背包场景：物品唯一持有者）。
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct)]
    public sealed class ExclusiveTargetAttribute : Attribute
    {
    }

    /// <summary>
    /// 声明式级联：标记的关系类型在 target 被销毁时自动级联销毁所有指向它的 source
    /// （Flecs OnDeleteTarget=EcsDelete 语义）。作用于单值列关系与多值关系。
    /// 未声明级联时只解除存活 source 的对应边。
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct)]
    public sealed class OnTargetDeletedAttribute : Attribute
    {
        public bool Cascade { get; set; }
    }
}
