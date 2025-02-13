namespace EntJoy
{
    public struct QueryBuilder
    {
        public ComponentType[] All;
        public ComponentType[] Any;
        public ComponentType[] None;

        public QueryBuilder WithAll<T>() where T : struct
        {
            All = ComponentTypes<T>.Share;
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