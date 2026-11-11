using System.Runtime.CompilerServices;

namespace EntJoy.ECS
{
    /// <summary>
    /// Chunk 元数据：每个 Archetype 构造时创建一份，所有该 Archetype 的 Chunk 共享。
    /// 包含组件布局信息（偏移、大小、enableable 位图位置）、Shared values 区布局，
    /// 这些在 Chunk 生命周期内不变。
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

        /// <summary>是否存在 enableable 列：为假时追加/移除实体无需遍历位图。</summary>
        public readonly bool HasEnableables;

        // 实体级变更位掩码在块内的偏移与字节数（-1/0 表示不启用变更追踪）
        public readonly int ChangedBitMaskOffset;
        public readonly int ChangedBitMaskSize;

        // Shared values 区

        /// <summary>
        /// Shared values 区起点（chunk 内存块内偏移；-1 表示无 shared 组件）。
        /// blittable shared 内联存值；managed shared 只存 int 索引（指向 EntityManager 哈希桶数组）。
        /// </summary>
        public readonly int SharedValuesOffset;

        /// <summary>每个 shared 类型在 Shared values 区内的偏移（索引与 Archetype.Types 对应，非 shared 为 -1）。</summary>
        public readonly int[] SharedValueOffsets;

        /// <summary>每个 shared 类型的大小：blittable = 类型大小；managed = sizeof(int)。</summary>
        public readonly int[] SharedValueSizes;

        /// <summary>managed shared 类型个数（chunk 槽位只存索引，值在 EntityManager）。</summary>
        public readonly int ManagedSharedCount;

        public ChunkMetadata(
            Archetype archetype,
            int entityCapacity,
            int totalSize,
            int[] componentOffsets,
            int[] componentSizes,
            int[] enableBitOffsets,
            int[] enableStrideBytes,
            int changedBitMaskOffset = -1,
            int changedBitMaskSize = 0,
            int sharedValuesOffset = -1,
            int[] sharedValueOffsets = null,
            int[] sharedValueSizes = null,
            int managedSharedCount = 0)
        {
            Archetype = archetype;
            EntityCapacity = entityCapacity;
            TotalSize = totalSize;
            ComponentCount = componentOffsets.Length;
            ComponentOffsets = componentOffsets;
            ComponentSizes = componentSizes;
            EnableBitOffsets = enableBitOffsets;
            EnableStrideBytes = enableStrideBytes;
            HasEnableables = System.Array.IndexOf(enableBitOffsets, -1) != enableBitOffsets.Length;
            ChangedBitMaskOffset = changedBitMaskOffset;
            ChangedBitMaskSize = changedBitMaskSize;
            SharedValuesOffset = sharedValuesOffset;
            SharedValueOffsets = sharedValueOffsets;
            SharedValueSizes = sharedValueSizes;
            ManagedSharedCount = managedSharedCount;
        }

        /// <summary>
        /// 计算指定容量的完整布局。Archetype 保存最终元数据并供其所有 Chunk 共享。
        /// </summary>
        public static ChunkMetadata Create(Archetype archetype, int entityCapacity, ComponentType[] componentTypes, bool enableChangeTracking = true)
            => Create(archetype, entityCapacity, componentTypes, enableChangeTracking, ChunkCacheLayout.Current);

        internal static ChunkMetadata Create(Archetype archetype, int entityCapacity, ComponentType[] componentTypes,
            bool enableChangeTracking, ChunkCacheLayout cacheLayout)
        {
            int lineSize = cacheLayout.LineSize;
            int componentCount = componentTypes.Length;
            var componentOffsets = new int[componentCount];
            var componentSizes = new int[componentCount];
            var enableBitOffsets = new int[componentCount];
            var enableStrideBytes = new int[componentCount];
            var sharedValueOffsets = new int[componentCount];
            var sharedValueSizes = new int[componentCount];
            for (int i = 0; i < componentCount; i++)
            {
                enableBitOffsets[i] = -1;
                sharedValueOffsets[i] = -1;
            }

            // Entity 数组固定在偏移 0，其后依次是组件列、各 enableable 位图、变更位图、shared 区。
            int offset = entityCapacity * Unsafe.SizeOf<Entity>();
            offset = LayoutColumns(componentTypes, entityCapacity, cacheLayout, componentOffsets, componentSizes,
                enableBitOffsets, enableStrideBytes, offset);
            LayoutChangedBitMask(entityCapacity, lineSize, enableChangeTracking, ref offset,
                out int changedBitMaskOffset, out int changedBitMaskSize);
            int sharedValuesOffset = LayoutSharedValues(componentTypes, lineSize, ref offset,
                sharedValueOffsets, sharedValueSizes, out int managedSharedCount);

            return new ChunkMetadata(
                archetype,
                entityCapacity,
                offset,
                componentOffsets,
                componentSizes,
                enableBitOffsets,
                enableStrideBytes,
                changedBitMaskOffset,
                changedBitMaskSize,
                sharedValuesOffset,
                sharedValueOffsets,
                sharedValueSizes,
                managedSharedCount);
        }

        // 每列按缓存行对齐；相邻列起点对缓存检测周期取余相同时再错开一行，分散组索引。返回新的游标。
        private static int LayoutColumns(ComponentType[] componentTypes, int entityCapacity, ChunkCacheLayout cacheLayout,
            int[] componentOffsets, int[] componentSizes, int[] enableBitOffsets, int[] enableStrideBytes, int offset)
        {
            int lineSize = cacheLayout.LineSize;
            int conflictPeriod = cacheLayout.ConflictPeriod;
            int previousColumnOffset = 0;
            for (int i = 0; i < componentTypes.Length; i++)
            {
                int componentSize = componentTypes[i].Size;
                offset = (offset + lineSize - 1) & ~(lineSize - 1);
                if (componentSize > 0)
                {
                    if (offset % conflictPeriod == previousColumnOffset % conflictPeriod)
                        offset += lineSize;
                    previousColumnOffset = offset;
                }

                componentOffsets[i] = offset;
                componentSizes[i] = componentSize;
                offset += entityCapacity * componentSize;

                if (componentTypes[i].IsEnableable)
                {
                    int bitMapBytes = (entityCapacity + 63) / 64 * 8;
                    enableBitOffsets[i] = offset;
                    enableStrideBytes[i] = bitMapBytes;
                    offset += bitMapBytes;
                }
            }
            return offset;
        }

        // 实体级变更位掩码占每实体 1 bit；不启用变更追踪时两个输出都是 -1 / 0。
        private static void LayoutChangedBitMask(int entityCapacity, int lineSize, bool enabled, ref int offset,
            out int maskOffset, out int maskSize)
        {
            if (!enabled)
            {
                maskOffset = -1;
                maskSize = 0;
                return;
            }
            offset = (offset + lineSize - 1) & ~(lineSize - 1);
            maskOffset = offset;
            maskSize = (entityCapacity + 63) / 64 * 8;
            offset += maskSize;
        }

        // blittable shared 内联存值；managed shared 只存 int 索引（值在 EntityManager 哈希桶数组）。
        private static int LayoutSharedValues(ComponentType[] componentTypes, int lineSize, ref int offset,
            int[] sharedValueOffsets, int[] sharedValueSizes, out int managedSharedCount)
        {
            managedSharedCount = 0;
            bool hasShared = false;
            for (int i = 0; i < componentTypes.Length; i++)
            {
                if (!componentTypes[i].IsShared) continue;
                hasShared = true;
                if (componentTypes[i].IsManagedShared) managedSharedCount++;
            }
            if (!hasShared) return -1;

            offset = (offset + lineSize - 1) & ~(lineSize - 1);
            int sharedValuesOffset = offset;
            for (int i = 0; i < componentTypes.Length; i++)
            {
                if (!componentTypes[i].IsShared) continue;
                sharedValueOffsets[i] = offset;
                sharedValueSizes[i] = componentTypes[i].IsManagedShared ? sizeof(int) : componentTypes[i].Size;
                offset += sharedValueSizes[i];
            }
            return sharedValuesOffset;
        }
    }
}
