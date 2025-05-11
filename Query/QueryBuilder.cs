namespace EntJoy 
{
    /// <summary>
    /// 查询构建器
    /// </summary>
    public struct QueryBuilder  
    {
        public ComponentType[] All;  
        public ComponentType[] Any;  
        public ComponentType[] None; 

        public QueryBuilder WithAll<T>() where T : struct 
        {
            All = ComponentTypes<T>.Share;  // 设置All条件
            return this; 
        }
        public QueryBuilder WithAll<T, T2>() where T : struct
        {
            All = ComponentTypes<T, T2>.Share; 
            return this; 
        }

        public QueryBuilder WithAny<T>() where T : struct
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
