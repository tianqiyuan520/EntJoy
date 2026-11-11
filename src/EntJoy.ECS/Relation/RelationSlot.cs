namespace EntJoy.ECS
{
    /// <summary>
    /// 关系组件标记接口：声明"此组件类型用于实体间关系"。
    /// 关系类型以 RelationSlot Target 字段开头，可附带无托管引用的数据。
    /// 普通类型为单目标，多目标类型通过 MultiRelationAttribute 声明。
    /// </summary>
    public interface IRelationComponent
    {
    }

    /// <summary>
    /// 关系槽位值：指向 target 实体（含版本防 Id 回收），8B blittable；空槽为 <see cref="Default"/>。
    /// 查询时用 <see cref="Matches"/> 校验 Id 与 Version，实体销毁后 Id 复用（version+1）时旧关系自动失效。
    /// 增删关系列属于结构变更。
    /// </summary>
    public unsafe struct RelationSlot
    {
        public int TargetId;       // 目标实体 ID（-1 = 无关系）
        public int TargetVersion;  // 目标实体版本（回收校验：Id 匹配且 Version 匹配才有效）

        public static readonly RelationSlot Default = new() { TargetId = -1, TargetVersion = -1 };

        /// <summary>是否写入了目标 ID；存活与版本另由 EntityManager 检查。</summary>
        public readonly bool IsValid => TargetId >= 0;

        /// <summary>校验：槽位指向的实体就是 target（Id + Version 双匹配）。</summary>
        public readonly bool Matches(Entity target) => TargetId == target.Id && TargetVersion == target.Version;

        /// <summary>校验：两个槽位指向同一实体（Id + Version 双匹配）。</summary>
        public readonly bool Matches(RelationSlot other) => TargetId == other.TargetId && TargetVersion == other.TargetVersion;

        /// <summary>从 target 实体构建槽位值。</summary>
        public static RelationSlot From(Entity target) => new() { TargetId = target.Id, TargetVersion = target.Version };

        /// <summary>将槽位解码为 Entity（Id + Version；可能指向已销毁实体，调用方需二次校验）。</summary>
        public readonly Entity ToEntity() => new() { Id = TargetId, Version = TargetVersion };

        public override readonly string ToString() => IsValid ? $"->({TargetId}, v{TargetVersion})" : "(none)";
    }
}
