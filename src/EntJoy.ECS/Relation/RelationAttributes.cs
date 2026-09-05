using System;

namespace EntJoy.ECS
{
    /// <summary>
    /// 独占关系（真 1:1）：标记的关系类型在 AddRelationship 时，若 target 已被其他 source 指向，
    /// 自动解绑旧 source（Flecs EcsExclusive 语义）。出边单值（列存储），入边唯一。
    /// 与 <see cref="MultiRelationAttribute"/> 互斥。
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct)]
    public sealed class ExclusiveRelationAttribute : Attribute
    {
    }

    /// <summary>
    /// 多值关系（出边 1:N / M:N）：source 可持有多个 target。
    ///
    /// 两种存储模式，按是否指定 <see cref="MaxSlots"/> 路由：
    /// - <c>[MultiRelation]</c>（MaxSlots = 0，默认）：托管列表（RelationListStore），无界、主线程、不进 Job（NT017 拦截）。
    /// - <c>[MultiRelation(MaxSlots = N)]</c>（N ≥ 2）：定长多槽列（chunk 列，宽 N×8B），有界、Job 可读
    ///   （IJobEntity/IJobChunk 直接访问，同单值关系列）。要求类型是 partial struct 且首字段为 RelationSlot Target，
    ///   源生成器注入 Slot1..SlotN-1 字段。
    ///
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
    /// 不标记的类型保持现状：DestroyEntity 不级联（S24 决策），关系自动失效。
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct)]
    public sealed class OnTargetDeletedAttribute : Attribute
    {
        public bool Cascade { get; set; }
    }
}
