

using System.Collections.Generic;
using System.Linq;

namespace EntJoy
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
            var list = AllEnabled?.ToList() ?? new List<ComponentType>();
            list.AddRange(ComponentTypes<T>.Share);
            AllEnabled = list.ToArray();
            return this;
        }

        public QueryBuilder WithAll<T>()
            where T : struct
        {
            var preCompTypes = All == null ? new List<ComponentType>() : All.ToList();
            preCompTypes.AddRange(ComponentTypes<T>.Share);
            All = preCompTypes.ToArray();
            return this;
        }
        public QueryBuilder WithAll<T, T2>()
            where T : struct
            where T2 : struct
        {
            var preCompTypes = All == null ? new List<ComponentType>() : All.ToList();
            preCompTypes.AddRange(ComponentTypes<T, T2>.Share);
            All = preCompTypes.ToArray();
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
    }

}
