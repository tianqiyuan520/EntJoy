namespace EntJoy.ECS
{
    /// <summary>单组件形态，语义同 <see cref="IQueryForeach{T0,T1}"/>。</summary>
    public interface IQueryForeach<T0>
        where T0 : struct
    {
        void Update(ref T0 component0);
    }

    /// <summary>
    /// 块循环内联回调：由 <see cref="QueryEnumerable{T0,T1}.ForEach{TAction}"/> 逐实体调用。
    /// 实现为 struct 时 JIT 可内联整个循环体，避免逐实体游标状态机。
    /// </summary>
    public interface IQueryForeach<T0, T1>
        where T0 : struct
        where T1 : struct
    {
        void Update(ref T0 component0, ref T1 component1);
    }

    /// <summary>三组件形态，语义同 <see cref="IQueryForeach{T0,T1}"/>。</summary>
    public interface IQueryForeach<T0, T1, T2>
        where T0 : struct
        where T1 : struct
        where T2 : struct
    {
        void Update(ref T0 component0, ref T1 component1, ref T2 component2);
    }

    /// <summary>四组件形态，语义同 <see cref="IQueryForeach{T0,T1}"/>。</summary>
    public interface IQueryForeach<T0, T1, T2, T3>
        where T0 : struct
        where T1 : struct
        where T2 : struct
        where T3 : struct
    {
        void Update(ref T0 component0, ref T1 component1, ref T2 component2, ref T3 component3);
    }
}
