namespace EntJoy.ECS
{
    /// <summary>非泛型 marker（供 ComponentTypeManager 用 IsAssignableFrom 检测，AOT 安全）。</summary>
    public interface ICopyable
    {
    }

    /// <summary>
    /// 组件复制钩子：复制组件值时调用（区别于位拷贝）。
    /// 典型用途：含 SharedBlob 的组件在复制时递增引用计数（OnCopy = dst.Data = src.Data.Clone()）。
    /// 由生成器自动注册（复用 IDisposable 注册模式）。
    /// </summary>
    public interface ICopyable<T> : ICopyable
        where T : unmanaged, ICopyable<T>
    {
        /// <summary>把 src 复制到 dst（dst 约定为已销毁/空状态）。</summary>
        void OnCopy(in T src, ref T dst);
    }
}
