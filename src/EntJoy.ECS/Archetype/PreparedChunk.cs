namespace EntJoy.ECS
{
    /// <summary>
    /// 预备好的目标块。地址是块在 slab 里的稳定身份（块表 swap-pop 会改下标）；下标是预留那一刻
    /// 的位置，落盘时先用它命中，对不上才按地址回扫。
    /// </summary>
    internal readonly struct PreparedChunk
    {
        public static readonly PreparedChunk None = default;

        public readonly nint Pointer;
        public readonly int Index;

        public PreparedChunk(nint pointer, int index)
        {
            Pointer = pointer;
            Index = index;
        }

        public bool IsValid => Pointer != 0;
    }
}
