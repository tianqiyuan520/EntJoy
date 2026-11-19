namespace EntJoy.ECS
{
    /// <summary>
    /// 逐实体的回调形态：把 ref 直接交给调用方，不产出逐实体结果结构体。
    /// 用结构体实现（JIT 按具体类型内联），循环体在块循环里原地展开，吞吐与块遍历同档。
    /// </summary>
    public interface IEntityForEach<T0, T1>
        where T0 : struct
        where T1 : struct
    {
        void Execute(ref T0 c0, ref T1 c1);
    }
}
