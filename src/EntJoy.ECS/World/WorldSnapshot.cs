namespace EntJoy.ECS
{
    /// <summary>保存组件值字节及布局标识的 World 快照。</summary>
    public sealed class WorldSnapshot
    {
        internal readonly byte[] Data;
        internal WorldSnapshot(byte[] data) => Data = data;
    }
}
