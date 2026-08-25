using System.Runtime.InteropServices;

namespace EntJoy.ECS
{
    /// <summary>
    /// Chunk 元数据：每个 Archetype 构造时创建一份，所有该 Archetype 的 Chunk 共享。
    /// 包含组件布局信息（偏移、大小、enableable 位图位置），这些在 Chunk 生命周期内不变。
    /// </summary>
    public sealed class ChunkMetadata
    {
        public readonly Archetype Archetype;
        public readonly int EntityCapacity;
        public readonly int TotalSize;
        public readonly int ComponentCount;

        // 组件数据在块内的偏移和大小，索引与 Archetype.Types 一一对应
        public readonly int[] ComponentOffsets;
        public readonly int[] ComponentSizes;

        // enableable 位图信息，索引与 Archetype.Types 对应，-1 表示不可 enable
        public readonly int[] EnableBitOffsets;
        public readonly int[] EnableStrideBytes;

        // 实体级变更位掩码在块内的偏移与字节数（-1/0 表示不启用变更追踪）
        public readonly int ChangedBitMaskOffset;
        public readonly int ChangedBitMaskSize;

        public ChunkMetadata(
            Archetype archetype,
            int entityCapacity,
            int totalSize,
            int[] componentOffsets,
            int[] componentSizes,
            int[] enableBitOffsets,
            int[] enableStrideBytes,
            int changedBitMaskOffset = -1,
            int changedBitMaskSize = 0)
        {
            Archetype = archetype;
            EntityCapacity = entityCapacity;
            TotalSize = totalSize;
            ComponentCount = componentOffsets.Length;
            ComponentOffsets = componentOffsets;
            ComponentSizes = componentSizes;
            EnableBitOffsets = enableBitOffsets;
            EnableStrideBytes = enableStrideBytes;
            ChangedBitMaskOffset = changedBitMaskOffset;
            ChangedBitMaskSize = changedBitMaskSize;
        }

        /// <summary>
        /// 计算 Chunk 内存布局并创建元数据。每个 Archetype 只调用一次。
        /// </summary>
        public static ChunkMetadata Create(Archetype archetype, int entityCapacity, ComponentType[] componentTypes, bool enableChangeTracking = true)
        {
            const int cacheLineSize = 64;

            int componentCount = componentTypes.Length;
            var componentOffsets = new int[componentCount];
            var componentSizes = new int[componentCount];
            var enableBitOffsets = new int[componentCount];
            var enableStrideBytes = new int[componentCount];

            for (int i = 0; i < componentCount; i++)
                enableBitOffsets[i] = -1;

            // Entity 数组（置于偏移 0）
            int entityArraySize = entityCapacity * Marshal.SizeOf<Entity>();
            int offset = entityArraySize;

            // 组件数组，每个都缓存行对齐
            for (int i = 0; i < componentCount; i++)
            {
                int componentSize = componentTypes[i].Size;

                // 对齐
                offset = offset + cacheLineSize - 1 & ~(cacheLineSize - 1);

                componentOffsets[i] = offset;
                componentSizes[i] = componentSize;
                offset += entityCapacity * componentSize;

                // enableable 位图
                if (componentTypes[i].IsEnableable)
                {
                    int ulongCount = (entityCapacity + 63) / 64;
                    int bitMapBytes = ulongCount * 8;
                    enableBitOffsets[i] = offset;
                    enableStrideBytes[i] = bitMapBytes;
                    offset += bitMapBytes;
                }
            }

            // 实体级变更位掩码（每实体 1 bit）
            int changedBitMaskOffset = -1;
            int changedBitMaskSize = 0;
            if (enableChangeTracking)
            {
                offset = (offset + cacheLineSize - 1) & ~(cacheLineSize - 1);
                changedBitMaskOffset = offset;
                int ulongCount = (entityCapacity + 63) / 64;
                changedBitMaskSize = ulongCount * 8;
                offset += changedBitMaskSize;
            }

            return new ChunkMetadata(
                archetype,
                entityCapacity,
                offset,
                componentOffsets,
                componentSizes,
                enableBitOffsets,
                enableStrideBytes,
                changedBitMaskOffset,
                changedBitMaskSize);
        }
    }
}
