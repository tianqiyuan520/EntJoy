

using System;
using System.Collections.Generic;

namespace EntJoy.ECS
{
    /// <summary>
    /// 查询构建器
    /// </summary>
    public partial struct QueryBuilder
    {
        public int LimitCount; //限制选择的数量
        public ComponentType[] All;
        public ComponentType[] Any;
        public ComponentType[] None;
        public ComponentType[] AllEnabled;   // 必须存在且启用的组件

        public QueryBuilder()
        {
            LimitCount = -1;
            AllEnabled = [];
        }

        public QueryBuilder SetLimit(int count)
        {
            LimitCount = count;
            return this;
        }

        public QueryBuilder WithEnabled<T>() where T : struct, IEnableableComponent
        {
            // AllEnabled 默认初始化为 []（空数组）；单组件首次调用直接引用静态 Share（零分配）。
            // 约定：All/AllEnabled 数组对外只读（调度/匹配仅读取，不得原地修改元素）。
            if (AllEnabled == null || AllEnabled.Length == 0)
            {
                AllEnabled = ComponentTypes<T>.Share;
                return this;
            }
            AllEnabled = Merge(AllEnabled, ComponentTypes<T>.Share);
            return this;
        }

        public QueryBuilder WithAll<T>()
            where T : struct
        {
            // 单组件首调：直接引用静态 Share，零分配（v3 Phase 1.5）。
            if (All == null)
            {
                All = ComponentTypes<T>.Share;
                return this;
            }
            All = Merge(All, ComponentTypes<T>.Share);
            return this;
        }
        public QueryBuilder WithAll<T, T2>()
            where T : struct
            where T2 : struct
        {
            if (All == null)
            {
                All = ComponentTypes<T, T2>.Share;
                return this;
            }
            All = Merge(All, ComponentTypes<T, T2>.Share);
            return this;
        }

        //TODO

        public QueryBuilder WithAny<T>()
            where T : struct
        {
            Any = ComponentTypes<T>.Share;
            return this;
        }
        public QueryBuilder WithNone<T>() where T : struct
        {
            None = ComponentTypes<T>.Share;
            return this;
        }

        /// <summary>
        /// 合并两个只读类型数组（链式条件追加）：单次数组分配，避免 List+ToArray 两次分配。
        /// </summary>
        private static ComponentType[] Merge(ComponentType[] a, ComponentType[] b)
        {
            var merged = new ComponentType[a.Length + b.Length];
            Array.Copy(a, 0, merged, 0, a.Length);
            Array.Copy(b, 0, merged, a.Length, b.Length);
            return merged;
        }
    }

}
