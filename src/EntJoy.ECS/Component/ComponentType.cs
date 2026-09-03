using System;

namespace EntJoy.ECS
{
    /// <summary>
    /// 组件类型
    /// </summary>
    public struct ComponentType : IEquatable<ComponentType>
    {
        public readonly int Id;  // 记录该组件类型的ID
        public readonly int Size;  // 组件大小（blittable size）

        public Type Type => ComponentTypeManager.GetTypeByComponentType(Id);  // 通过查询获取组件类型
        public bool IsEnableable => ComponentTypeManager.GetIsEnableable(Id);
        public bool IsShared => ComponentTypeManager.GetIsShared(Id);
        public bool IsRelation => ComponentTypeManager.GetIsRelation(Id);
        public bool IsDisposable => ComponentTypeManager.GetIsDisposable(Id);
        public bool IsCopyable => ComponentTypeManager.GetIsCopyable(Id);

        /// <summary>
        /// 是否为 managed shared 组件（ISharedComponentData 且含引用字段/class）。
        /// managed shared 不内联存于 chunk 内存块，chunk 槽位只存 int 索引；
        /// NativeTranspiler 不处理，validator 编译期拦截。
        /// </summary>
        public bool IsManagedShared => IsShared && !ComponentTypeManager.IsBlittable(Type);


        public ComponentType(int id, int size = 0)
        {
            Id = id;
            Size = size;
        }

        // 将该类型转 组件类型
        public static implicit operator ComponentType(Type type)
        {
            return ComponentTypeManager.GetComponentType(type);  // 通过查询获取对应的组件类型
        }

        /// <summary>
        /// 获取哈希码（基于 Id，与 Equals 保持一致）
        /// </summary>
        public override int GetHashCode()
        {
            return Id;
        }



        #region Equals

        /// <summary>
        /// 基于 Id 比较（Id 在 ComponentTypeManager 中全局唯一映射到 Type）
        /// </summary>
        public bool Equals(ComponentType other) => Id == other.Id;

        public override bool Equals(object obj) => obj is ComponentType ct && Equals(ct);

        public static bool operator ==(ComponentType left, ComponentType right) => left.Equals(right);
        public static bool operator !=(ComponentType left, ComponentType right) => !left.Equals(right);


        #endregion
    }
}
