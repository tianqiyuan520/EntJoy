using System;

namespace EntJoy
{
    /// <summary>
    /// 组件类型
    /// </summary>
    public struct ComponentType : IEquatable<ComponentType>
    {
        public readonly int Id;  // 记录该组件类型的ID
        public int Size;

        public Type Type => ComponentTypeManager.GetTypeByComponentType(Id);  // 通过查询获取组件类型
        public bool IsEnableable => ComponentTypeManager.GetIsEnableable(Id);


        public ComponentType(int id)
        {
            Id = id;
        }

        // 将该类型转 组件类型
        public static implicit operator ComponentType(Type type)
        {
            return ComponentTypeManager.GetComponentType(type);  // 通过查询获取对应的组件类型
        }

        /// <summary>
        /// 获取哈希码
        /// </summary>
        public override int GetHashCode()
        {
            return Id;  // 返回ID作为哈希码
        }



        #region Equals

        public bool Equals(ComponentType other) => Type == other.Type;

        public override bool Equals(object obj) => obj is ComponentType ct && Equals(ct);

        public static bool operator ==(ComponentType left, ComponentType right) => left.Equals(right);
        public static bool operator !=(ComponentType left, ComponentType right) => !left.Equals(right);


        #endregion
    }
}
