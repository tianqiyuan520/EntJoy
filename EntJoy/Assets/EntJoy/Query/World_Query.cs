namespace EntJoy
{
    /// <summary>
    /// 查询相关
    /// </summary>
    public partial class World
    {
        public void Lookup<T0>(in QueryDesc desc, ForeachWith<T0> lambda) 
            where T0 : struct, IComponent
        {
            
            
            
        }
        
        public void Lookup<T0, T1>(in QueryDesc desc, ForeachWith<T0, T1> lambda) 
            where T0 : struct, IComponent
            where T1 : struct, IComponent
        {
            
            
            
        }
    }
}