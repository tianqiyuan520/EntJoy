using System;

namespace EntJoy
{
    /// <summary>
    /// 组件类型
    /// </summary>
    public struct ComponentType : IEquatable<ComponentType>  //  使用IEquatable实现内容对比，而非地址对比相等
    {
        public readonly int Id;  // 记录该 组件类型的ID

        public Type Type => ComponentTypeManager.GetTypeByComponentType(Id);  // 通过查询获取组件类型

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

        public bool Equals(ComponentType other)
        {
            return Id == other.Id;
        }
        // 如果为object型，需要判断该obj是否可转ComponentType
        public override bool Equals(object obj)
        {
            return obj is ComponentType other && Equals(other);
        }

        public static bool operator ==(ComponentType left, ComponentType right)  // 相等运算符
        {
            return left.Equals(right);
        }

        public static bool operator !=(ComponentType left, ComponentType right)  // 不等运算符
        {
            return !left.Equals(right);
        }

        #endregion
    }
}
