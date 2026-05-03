namespace EntJoy.Collections
{
    /// <summary>
    /// 内存分配器类型，与 Unity 保持一致。
    /// </summary>
    public enum Allocator
    {
        Invalid = 0,
        None = 1,
        Temp = 2,         
        TempJob = 3,       
        Persistent = 4,    
    }
}