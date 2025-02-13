namespace EntJoy
{
    internal sealed class ComponentTypes<T0>
    {
        public static ComponentType[] Share = new ComponentType[]
        {
            typeof(T0)
        };
    }
    
    internal class ComponentTypes<T0, T1>
    {
        public static ComponentType[] Share = new ComponentType[]
        {
            typeof(T0),
            typeof(T1),
        };
    }
    
    internal class ComponentTypes<T0, T1, T2>
    {
        public static ComponentType[] Share = new ComponentType[]
        {
            typeof(T0),
            typeof(T1),
            typeof(T2),
        };
    }
    
    internal class ComponentTypes<T0, T1, T2, T3>
    {
        public static ComponentType[] Share = new ComponentType[]
        {
            typeof(T0),
            typeof(T1),
            typeof(T2),
            typeof(T3),
        };
    }
}