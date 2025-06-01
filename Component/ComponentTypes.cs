namespace EntJoy
{
    /// <summary>
    /// 组件类型容器（单组件）
    /// </summary>
    internal sealed class ComponentTypes<T0>  // 单组件类型容器
    {
        /// <summary>共享的组件类型数组</summary>
        public static ComponentType[] Share = new ComponentType[]  // 共享组件类型数组
        {
            typeof(T0)  // 组件类型0
        };
    }

    /// <summary>
    /// 组件类型容器（双组件）
    /// </summary>
    internal sealed class ComponentTypes<T0, T1>  // 双组件类型容器
    {
        /// <summary>共享的组件类型数组</summary>
        public static ComponentType[] Share = new ComponentType[]  // 共享组件类型数组
        {
            typeof(T0),  // 组件类型0
            typeof(T1),  // 组件类型1
        };
    }

    internal sealed class ComponentTypes<T0, T1, T2>  // 三组件类型容器
    {
        public static ComponentType[] Share = new ComponentType[]  // 共享组件类型数组
        {
            typeof(T0),  // 组件类型0
            typeof(T1),  // 组件类型1
            typeof(T2),  // 组件类型2
        };
    }

    internal sealed class ComponentTypes<T0, T1, T2, T3>  // 四组件类型容器
    {
        public static ComponentType[] Share = new ComponentType[]  // 共享组件类型数组
        {
            typeof(T0),  // 组件类型0
            typeof(T1),  // 组件类型1
            typeof(T2),  // 组件类型2
            typeof(T3),  // 组件类型3
        };
    }
}
