namespace EntJoy.Query
{
    public struct QueryBuilder
    {
        public ComponentType[] All;
        public ComponentType[] Any;
        public ComponentType[] None;

        public void WithAll<T>() where T : struct
        {
            All = ComponentTypes<T>.Share;
        }

        public void WithAny<T>() where T : struct
        {
            Any = ComponentTypes<T>.Share;
        }

        public void WithNone<T>() where T : struct
        {
            None = ComponentTypes<T>.Share;
        }
    }
}