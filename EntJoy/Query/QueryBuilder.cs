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

        public QueryBuilder()
        {
            LimitCount = -1;
        }

        public QueryBuilder SetLimit(int count)
        {
            LimitCount = count;
            return this;
        }

        // 使用可变参数支持多组件
        public QueryBuilder WithAll(params Type[] types)
        {
            int index = All.Length;
            All = new ComponentType[All.Length + types.Length];
            for (int i = index; i < types.Length; i++)
            {
                All[i] = ComponentTypeManager.GetComponentType(types[i]);
            }
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
        public QueryBuilder WithAny(params Type[] types)
        {
            Any = new ComponentType[types.Length];
            for (int i = 0; i < types.Length; i++)
            {
                Any[i] = ComponentTypeManager.GetComponentType(types[i]);
            }
            return this;
        }

        public QueryBuilder WithNone<T>() where T : struct
        {
            None = ComponentTypes<T>.Share;
            return this;
        }
        public QueryBuilder WithNone(params Type[] types)
        {
            None = new ComponentType[types.Length];
            for (int i = 0; i < types.Length; i++)
            {
                None[i] = ComponentTypeManager.GetComponentType(types[i]);
            }
            return this;
        }
    }

    public struct QueryBuilderMemory
    {
    }
}
