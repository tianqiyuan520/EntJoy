using System;

namespace EntJoy
{
    public static partial class Utils
    {
        /// <summary>
        /// 检查原型是否匹配查询条件
        /// </summary>
        public static bool IsMatch(this Archetype archetype, QueryBuilder queryBuilder)  // 原型匹配扩展方法
        {

            if (queryBuilder.All != null && !archetype.HasAllOf(queryBuilder.All))
            {
                return false;  // 不满足All条件
            }


            if (queryBuilder.Any != null && !archetype.HasAnyOf(queryBuilder.Any))
            {
                return false;  // 不满足Any条件
            }


            if (queryBuilder.None != null && !archetype.HasNoneOf(queryBuilder.None))
            {
                return false;  // 不满足None条件
            }

            if (queryBuilder.AllEnabled != null && !archetype.HasAllOf(queryBuilder.AllEnabled.AsSpan())) // 必须包含这些组件
                return false;

            return true;  // 满足所有条件
        }
    }
}
