namespace EntJoy
{
    
    public delegate void ForeachWith<T0>(Entity entity, ref T0 t0);

    public delegate void ForeachWith<T0, T1>(Entity entity, ref T0 t0, ref T1 t1); 
}
