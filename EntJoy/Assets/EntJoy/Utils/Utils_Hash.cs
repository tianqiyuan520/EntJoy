using System;

namespace EntJoy
{
    public partial class Utils
    {
        /// <summary>
        /// 计算组件集合的哈希值
        /// 这样我们对于同一类组件集合可以确定唯一的原型
        /// </summary>
        public static int CalculateHash(Span<ComponentType> types, bool isNeedSort = true)
        {
            if (isNeedSort)
            {
                SortByInsert(types);
            }
            
            HashCode hashCode = new HashCode();
            for (int i = 0; i < types.Length; i++)
            {
                hashCode.Add(types[i].Id);
            }
            
            return hashCode.ToHashCode();
        }
        
        /// <summary>
        /// 插入排序 升序
        /// </summary>
        public static void SortByInsert(Span<ComponentType> span)
        {
            int length = span.Length;
            for (int i = 1; i < length; i++)
            {
                ComponentType key = span[i];
                int j = i - 1;
                while (j >= 0 && span[j].Id > key.Id)
                {
                    span[j + 1] = span[j];
                    j--;
                }
                span[j + 1] = key;
            }
        }
    }
}