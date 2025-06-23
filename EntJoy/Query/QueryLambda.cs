namespace EntJoy
{
    // 使用委托数组支持任意数量组件
    //public delegate void ForeachWithComponents(Entity entity, params object[] components);
    //public delegate void ForeachWithComponents(ref Entity entity, params IComponent[] components);

    //public delegate void ForeachWith<T0>(ref Entity entity, ref T0 t0);
    //public delegate void ForeachWith<T0, T1>(ref Entity entity, ref T0 t0, ref T1 t1);
    //public delegate void ForeachWith<T0, T1, T2>(ref Entity entity, ref T0 t0, ref T1 t1, ref T2 t2);
    //public delegate void ForeachWith<T0, T1, T2, T3>(ref Entity entity, ref T0 t0, ref T1 t1, ref T2 t2, ref T3 t3);


    public interface IForeachWithSIMD<T0, T1>
    where T0 : struct
    where T1 : struct
    {
        void Execute(ref IntPtr t0, ref IntPtr t1, int count);
    }

    public partial interface ISystem<T0> 
        where T0 : struct
    {
        unsafe void _execute(ref Entity* _entity, ref T0* t0,int Count) { }
        void Execute(ref Entity entity, ref T0 t0);
    }
    public partial interface ISystem<T0, T1>
        where T0 : struct
        where T1 : struct
    {
        unsafe void _execute(ref Entity* _entity, ref T0* t0, ref T1* t1, int Count) { }
        void Execute(ref Entity entity, ref T0 t0, ref T1 t1);
    }

}
