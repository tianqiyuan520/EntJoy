using System;

namespace EntJoy
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
