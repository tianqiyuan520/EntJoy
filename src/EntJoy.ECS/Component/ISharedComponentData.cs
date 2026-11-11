namespace EntJoy.ECS
{
    /// <summary>
    /// 共享组件标记接口（per-chunk 存储）：同一块的所有实体共享相同的值组合。
    /// blittable 类型的值内联存在块的 Shared values 区；managed 类型走引用计数，原生翻译器不处理。
    /// managed 类型应实现 <see cref="System.IEquatable{T}"/> 与 GetHashCode 以支持去重。
    /// </summary>
    public interface ISharedComponentData
    {
    }
}