namespace EntJoy
{
    // 使用委托数组支持任意数量组件
    //public delegate void ForeachWithComponents(Entity entity, params object[] components);
    public delegate void ForeachWithComponents(ref Entity entity, params IComponent[] components);

    public delegate void ForeachWith<T0>(ref Entity entity, ref T0 t0);
    public delegate void ForeachWith<T0, T1>(ref Entity entity, ref T0 t0, ref T1 t1);
    public delegate void ForeachWith<T0, T1, T2>(ref Entity entity, ref T0 t0, ref T1 t1, ref T2 t2);
    public delegate void ForeachWith<T0, T1, T2, T3>(ref Entity entity, ref T0 t0, ref T1 t1, ref T2 t2, ref T3 t3);


    public interface IForeachWithSIMD<T0, T1>
    where T0 : struct
    where T1 : struct
    {
        void Execute(ref IntPtr t0, ref IntPtr t1, int count);
    }

    public interface ISystem<T0> 
        where T0 : struct
    {
        void Execute(ref Entity entity, ref T0 t0);
    }
    public interface ISystem<T0, T1>
        where T0 : struct
        where T1 : struct
    {
        void Execute(ref Entity entity, ref T0 t0, ref T1 t1);
    }

    //public interface ISystem<T0, T1, T2>
    //    where T0 : struct
    //    where T1 : struct
    //    where T2 : struct
    //{
    //    void Execute(ref Entity entity, ref T0 t0, ref T1 t1, ref T2 t2);
    //}
    //public interface ISystem<T0, T1, T2, T3>
    //    where T0 : struct
    //    where T1 : struct
    //    where T2 : struct
    //    where T3 : struct
    //{
    //    void Execute(ref Entity entity, ref T0 t0, ref T1 t1, ref T2 t2, ref T3 t3);
    //}
    //public interface ISystem<T0, T1, T2, T3, T4>    
    //    where T0 : struct
    //    where T1 : struct
    //    where T2 : struct
    //    where T3 : struct
    //    where T4 : struct
    //{
    //    void Execute(ref Entity entity, ref T0 t0, ref T1 t1, ref T2 t2, ref T3 t3, ref T4 t4);
    //}   
    //public interface ISystem<T0, T1, T2, T3, T4, T5>
    //    where T0 : struct
    //    where T1 : struct
    //    where T2 : struct
    //    where T3 : struct
    //    where T4 : struct
    //    where T5 : struct
    //{
    //    void Execute(ref Entity entity, ref T0 t0, ref T1 t1, ref T2 t2, ref T3 t3, ref T4 t4, ref T5 t5);
    //}
    //public interface ISystem<T0, T1, T2, T3, T4, T5, T6>
    //    where T0 : struct
    //    where T1 : struct
    //    where T2 : struct
    //    where T3 : struct
    //    where T4 : struct
    //    where T5 : struct
    //    where T6 : struct
    //{
    //    void Execute(ref Entity entity, ref T0 t0, ref T1 t1, ref T2 t2, ref T3 t3, ref T4 t4, ref T5 t5, ref T6 t6);
    //}
    //public interface ISystem<T0, T1, T2, T3, T4, T5, T6, T7>
    //    where T0 : struct
    //    where T1 : struct
    //    where T2 : struct
    //    where T3 : struct
    //    where T4 : struct
    //    where T5 : struct
    //    where T6 : struct
    //    where T7 : struct
    //{
    //    void Execute(ref Entity entity, ref T0 t0, ref T1 t1, ref T2 t2, ref T3 t3, ref T4 t4, ref T5 t5, ref T6 t6, ref T7 t7);
    //}
    //public interface ISystem<T0, T1, T2, T3, T4, T5, T6, T7, T8>
    //    where T0 : struct
    //    where T1 : struct
    //    where T2 : struct
    //    where T3 : struct
    //    where T4 : struct
    //    where T5 : struct
    //    where T6 : struct
    //    where T7 : struct
    //    where T8 : struct
    //{
    //    void Execute(ref Entity entity, ref T0 t0, ref T1 t1, ref T2 t2, ref T3 t3, ref T4 t4, ref T5 t5, ref T6 t6, ref T7 t7, ref T8 t8);
    //}
    //public interface ISystem<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9>
    //    where T0 : struct
    //    where T1 : struct
    //    where T2 : struct
    //    where T3 : struct
    //    where T4 : struct
    //    where T5 : struct
    //    where T6 : struct
    //    where T7 : struct
    //    where T8 : struct
    //    where T9 : struct
    //{
    //    void Execute(ref Entity entity, ref T0 t0, ref T1 t1, ref T2 t2, ref T3 t3, ref T4 t4, ref T5 t5, ref T6 t6, ref T7 t7, ref T8 t8, ref T9 t9);
    //}
    //public interface ISystem<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>
    //    where T0 : struct
    //    where T1 : struct
    //    where T2 : struct
    //    where T3 : struct
    //    where T4 : struct
    //    where T5 : struct
    //    where T6 : struct
    //    where T7 : struct
    //    where T8 : struct
    //    where T9 : struct
    //    where T10 : struct
    //{
    //    void Execute(ref Entity entity, ref T0 t0, ref T1 t1, ref T2 t2, ref T3 t3, ref T4 t4, ref T5 t5, ref T6 t6, ref T7 t7, ref T8 t8, ref T9 t9, ref T10 t10);
    //}
    //public interface ISystem<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>
    //    where T0 : struct
    //    where T1 : struct
    //    where T2 : struct
    //    where T3 : struct
    //    where T4 : struct
    //    where T5 : struct
    //    where T6 : struct
    //    where T7 : struct
    //    where T8 : struct
    //    where T9 : struct
    //    where T10 : struct
    //    where T11 : struct
    //{
    //    void Execute(ref Entity entity, ref T0 t0, ref T1 t1, ref T2 t2, ref T3 t3, ref T4 t4, ref T5 t5, ref T6 t6, ref T7 t7, ref T8 t8, ref T9 t9, ref T10 t10, ref T11 t11);
    //}
    //public interface ISystem<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>
    //    where T0 : struct
    //    where T1 : struct
    //    where T2 : struct
    //    where T3 : struct
    //    where T4 : struct
    //    where T5 : struct
    //    where T6 : struct
    //    where T7 : struct
    //    where T8 : struct
    //    where T9 : struct
    //    where T10 : struct
    //    where T11 : struct
    //    where T12 : struct
    //{
    //    void Execute(ref Entity entity, ref T0 t0, ref T1 t1, ref T2 t2, ref T3 t3, ref T4 t4, ref T5 t5, ref T6 t6, ref T7 t7, ref T8 t8, ref T9 t9, ref T10 t10, ref T11 t11, ref T12 t12);
    //}


}
