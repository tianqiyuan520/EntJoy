namespace EntJoy
{
    /// <summary>
    /// 组件类型容器（单组件）
    /// </summary>
    internal sealed class ComponentTypes<T0>
        where T0 : struct
    {
        /// <summary>共享的组件类型数组</summary>
        public static ComponentType[] Share = new ComponentType[] 
        {
            typeof(T0) 
        };
    }

    /// <summary>
    /// 组件类型容器（双组件）
    /// </summary>
    internal sealed class ComponentTypes<T0, T1>
        where T0 : struct
        where T1 : struct
    {
        /// <summary>共享的组件类型数组</summary>
        public static ComponentType[] Share = new ComponentType[] 
        {
            typeof(T0),
            typeof(T1)
        };
    }
}
