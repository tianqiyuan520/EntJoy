using System; 

namespace EntJoy 
{
    public static partial class Utils
    {
        /// <summary>
        /// 计算组件类型集合的哈希值
        /// <br>这样我们对于同一类组件集合可以确定唯一的原型</br>
        /// </summary>
        /// <param name="isNeedSort">判断是否需要对给定的 <paramref name="types"/> 排序</param>
        /// <returns></returns>
        public static int CalculateHash(Span<ComponentType> types, bool isNeedSort = true) 
        {
            if (isNeedSort)
            {
                SortByInsert(types);  // 调用插入排序
            }
            HashCode hashCode = new();  // 创建哈希计算器
            for (int i = 0; i < types.Length; i++)  // 遍历组件类型
            {
                hashCode.Add(types[i].Id);  // 添加组件ID到哈希
            }

            return hashCode.ToHashCode();  // 返回最终哈希值
        }

        /// <summary>
        /// 插入排序 升序
        /// </summary>
        public static void SortByInsert(Span<ComponentType> span) 
        {
            int length = span.Length;
            for (int i = 1; i < length; i++)
            {
                ComponentType cur = span[i];
                int j = i - 1;
                while (j >= 0 && span[j].Id > cur.Id)  // 若前者的ID大于后者，则 前移指针
                {
                    span[j + 1] = span[j]; // 元素后移
                    j--;
                }
                span[j + 1] = cur;  // 将当前元素放到正确位置
            }
        }
    }
}
