namespace EntJoy
{
    public delegate void ForeachWith<T0>(ref T0 t0) 
        where T0 : struct, IComponent;
    
    public delegate void ForeachWith<T0, T1>(ref T0 t0, ref T1 t1)
        where T0 : struct, IComponent
        where T1 : struct, IComponent;
}