namespace EntJoy.ECS
{
    /// <summary>
    /// 关系组件标记接口：声明"此组件类型用于实体间关系"。
    /// 关系类型是空 struct（如 <c>struct ChildOf : IRelationComponent</c>），
    /// 列值存放在 <see cref="RelationSlot"/>（target + version）。
    /// 与普通组件同路径注册（ComponentTypeManager），一实体每关系类型最多一个 target（单实例语义）。
    /// </summary>
    public interface IRelationComponent
    {
    }

    /// <summary>
    /// 关系槽位值：指向 target 实体（含版本防 ID 回收）。8B blittable。
    /// 实体无关系时槽位为 <see cref="Default"/>（TargetId = -1），不触发结构变更。
    /// 查询/读取时校验 <see cref="Matches"/>（Id 相等且 Version 相等），
    /// 实体销毁后 Id 复用（version+1）时旧关系自动失效。
    /// </summary>
    public unsafe struct RelationSlot
    {
        public int TargetId;       // 目标实体 ID（-1 = 无关系）
        public int TargetVersion;  // 目标实体版本（回收校验：Id 匹配且 Version 匹配才有效）

        public static readonly RelationSlot Default = new() { TargetId = -1, TargetVersion = -1 };

        /// <summary>是否持有有效关系（槽位已写入，非默认）。</summary>
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
