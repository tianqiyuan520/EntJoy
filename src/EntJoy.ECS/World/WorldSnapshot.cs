namespace EntJoy.ECS
{
    /// <summary>World 状态快照（字节级序列化，组件值零拷贝）。</summary>
    public sealed class WorldSnapshot
    {
        internal readonly byte[] Data;
        internal WorldSnapshot(byte[] data) => Data = data;
    }
}
