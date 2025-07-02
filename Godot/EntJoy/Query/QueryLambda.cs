namespace EntJoy
{
    // 使用委托数组支持任意数量组件
    //public delegate void ForeachWithComponents(Entity entity, params object[] components);
    //public delegate void ForeachWithComponents(ref Entity entity, params IComponent[] components);

    //public delegate void ForeachWith<T0>(ref Entity entity, ref T0 t0);
    //public delegate void ForeachWith<T0, T1>(ref Entity entity, ref T0 t0, ref T1 t1);
    //public delegate void ForeachWith<T0, T1, T2>(ref Entity entity, ref T0 t0, ref T1 t1, ref T2 t2);
    //public delegate void ForeachWith<T0, T1, T2, T3>(ref Entity entity, ref T0 t0, ref T1 t1, ref T2 t2, ref T3 t3);



    //public interface IForeachWithSIMD<T0, T1>
    //    where T0 : struct
    //    where T1 : struct
    //{
    //    unsafe void Execute(ref IntPtr t0Ptr, ref IntPtr t1Ptr, int Count);
    //}

    public interface ISystem<T0> where T0 : struct
    {
        virtual unsafe void _execute(Entity* entities, T0* components, int Count) { }
        void Execute(ref Entity entity, ref T0 component);
    }

    public interface ISystem<T0, T1>
        where T0 : struct
        where T1 : struct
    {
        virtual unsafe void _execute(Entity* entities, T0* components0, T1* components1, int Count) { }
        void Execute(ref Entity entity, ref T0 component0, ref T1 component1);
    }


}
