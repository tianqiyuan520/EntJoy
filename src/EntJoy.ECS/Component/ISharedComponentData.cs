namespace EntJoy.ECS
{
    /// <summary>
    /// 共享组件标记接口（per-chunk 存储，对齐 Unity DOTS）。
    /// 同一 Chunk 的所有实体共享相同的 SharedComponent 值组合。
    ///
    /// 双类型策略：
    /// - blittable（无引用字段的 struct）：值内联存储于 Chunk 内存块 Shared values 区，NativeTranspiler 可读。
    /// - managed（含引用字段的 struct 或 class）：值存于 EntityManager 哈希桶值数组，Chunk 槽位只存 int 索引；
    ///   refCount 归零自动销毁；NativeTranspiler 不处理（validator 编译期拦截）。
    ///
    /// managed 类型建议实现 <see cref="System.IEquatable{T}"/> 与 GetHashCode，以支持值去重。
    /// </summary>
    public interface ISharedComponentData
    {
    }
}