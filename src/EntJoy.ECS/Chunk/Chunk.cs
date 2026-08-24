using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EntJoy
{
    /// <summary>
    /// 轻量级 Chunk 句柄（struct）：持有指向 64KB 非托管内存块的指针和共享元数据引用。
    /// 实际的组件布局信息（偏移、大小、enableable 位图）存储在 <see cref="ChunkMetadata"/> 中，
    /// 由同一 Archetype 的所有 Chunk 共享。
    /// </summary>
    public unsafe struct Chunk
    {
        public readonly ChunkMetadata Meta;
        public readonly nint MemoryBlock;
        private int _entityCount;

        private const int ENTITY_ARRAY_OFFSET = 0;

        public Archetype Archetype => Meta.Archetype;
        public int EntityCount => _entityCount;
        public int Capacity => Meta.EntityCapacity;
        public int TotalSize => Meta.TotalSize;
        public int ComponentCount => Meta.ComponentCount;

        /// <summary>
        /// Construct a chunk using pre-computed shared metadata and a pre-allocated slab pointer.
        /// The memory is owned by Archetype (contiguous slab), not by this Chunk.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Chunk(ChunkMetadata meta, nint memoryBlock)
        {
            Meta = meta;
            MemoryBlock = memoryBlock;
            _entityCount = 0;
            Unsafe.InitBlock((byte*)memoryBlock, 0, (uint)meta.TotalSize);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddEntity(Entity entity)
        {
            if (_entityCount >= Meta.EntityCapacity)
                throw new InvalidOperationException("Chunk is full");

            // 将实体写入 Entity 数组
            ((Entity*)((byte*)MemoryBlock + ENTITY_ARRAY_OFFSET))[_entityCount] = entity;

            // 清零该实体的所有组件数据（防止 swap-pop 后读到脏数据）
            for (int i = 0; i < Meta.ComponentCount; i++)
            {
                int compSize = Meta.ComponentSizes[i];
                byte* compPtr = (byte*)MemoryBlock + Meta.ComponentOffsets[i] + _entityCount * compSize;
                Unsafe.InitBlock(compPtr, 0, (uint)compSize);
            }

            // 初始化所有 enableable 位为"启用"
            for (int i = 0; i < Meta.ComponentCount; i++)
            {
                if (Meta.EnableBitOffsets[i] != -1)
                {
                    ulong* bitMapPtr = (ulong*)((byte*)MemoryBlock + Meta.EnableBitOffsets[i]);
                    int ulongIndex = _entityCount >> 6;
                    int bitOffset = _entityCount & 63;
                    bitMapPtr[ulongIndex] |= 1UL << bitOffset;
                }
            }

            _entityCount++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveEntity(int index)
        {
            if (index < 0 || index >= _entityCount)
                throw new IndexOutOfRangeException();

            int lastIndex = _entityCount - 1;

            if (lastIndex > index)
            {
                // 复制最后一个实体到被移除的位置
                Entity* entityArray = (Entity*)((byte*)MemoryBlock + ENTITY_ARRAY_OFFSET);
                entityArray[index] = entityArray[lastIndex];

                // 复制所有组件数据
                for (int i = 0; i < Meta.ComponentCount; i++)
                {
                    int compSize = Meta.ComponentSizes[i];
                    byte* src = (byte*)MemoryBlock + Meta.ComponentOffsets[i] + lastIndex * compSize;
                    byte* dst = (byte*)MemoryBlock + Meta.ComponentOffsets[i] + index * compSize;
                    Unsafe.CopyBlock(dst, src, (uint)compSize);
                }

                // 复制 enableable 位，并清除最后实体的位
                for (int i = 0; i < Meta.ComponentCount; i++)
                {
                    if (Meta.EnableBitOffsets[i] == -1) continue;
                    ulong* bitMapPtr = (ulong*)((byte*)MemoryBlock + Meta.EnableBitOffsets[i]);

                    int lastUlongIdx = lastIndex >> 6;
                    int lastBitOffset = lastIndex & 63;
                    bool lastEnabled = (bitMapPtr[lastUlongIdx] & 1UL << lastBitOffset) != 0;

                    int targetUlongIdx = index >> 6;
                    int targetBitOffset = index & 63;
                    if (lastEnabled)
                        bitMapPtr[targetUlongIdx] |= 1UL << targetBitOffset;
                    else
                        bitMapPtr[targetUlongIdx] &= ~(1UL << targetBitOffset);

                    // 清除原来的最后一位
                    bitMapPtr[lastUlongIdx] &= ~(1UL << lastBitOffset);
                }
            }
            else
            {
                // 被移除的就是最后一个，只需清除 enableable 位
                for (int i = 0; i < Meta.ComponentCount; i++)
                {
                    if (Meta.EnableBitOffsets[i] == -1) continue;
                    ulong* bitMapPtr = (ulong*)((byte*)MemoryBlock + Meta.EnableBitOffsets[i]);
                    int lastUlongIdx = lastIndex >> 6;
                    int lastBitOffset = lastIndex & 63;
                    bitMapPtr[lastUlongIdx] &= ~(1UL << lastBitOffset);
                }
            }

            _entityCount--;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T GetComponent<T>(int entityIndex, int componentIndex) where T : struct
        {
            if ((uint)entityIndex >= (uint)_entityCount)
                throw new IndexOutOfRangeException($"Entity index {entityIndex} out of range (count={_entityCount}).");
            if ((uint)componentIndex >= (uint)Meta.ComponentCount)
                throw new IndexOutOfRangeException($"Component index {componentIndex} out of range (count={Meta.ComponentCount}).");
            return ref Unsafe.AsRef<T>(
                (byte*)MemoryBlock + Meta.ComponentOffsets[componentIndex] + entityIndex * Meta.ComponentSizes[componentIndex]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref Entity GetEntity(int entityIndex)
        {
            return ref ((Entity*)((byte*)MemoryBlock + ENTITY_ARRAY_OFFSET))[entityIndex];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public nint GetEntityPointer()
        {
            return MemoryBlock + ENTITY_ARRAY_OFFSET;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public nint GetComponentArrayPointer(int componentIndex)
        {
            return MemoryBlock + Meta.ComponentOffsets[componentIndex];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetComponentOffset(int componentIndex)
        {
            return Meta.ComponentOffsets[componentIndex];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong* GetEnableBitMapPointer(int componentIndex)
        {
            if (Meta.EnableBitOffsets[componentIndex] == -1) return null;
            return (ulong*)((byte*)MemoryBlock + Meta.EnableBitOffsets[componentIndex]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool GetComponentEnabled(int componentIndex, int entityIndex)
        {
            ulong* bitMapPtr = GetEnableBitMapPointer(componentIndex);
            if (bitMapPtr == null) throw new InvalidOperationException("Component is not enableable.");
            int ulongIndex = entityIndex >> 6;
            int bitOffset = entityIndex & 63;
            return (bitMapPtr[ulongIndex] & 1UL << bitOffset) != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetComponentEnabled(int componentIndex, int entityIndex, bool enabled)
        {
            ulong* bitMapPtr = GetEnableBitMapPointer(componentIndex);
            if (bitMapPtr == null) throw new InvalidOperationException("Component is not enableable.");
            int ulongIndex = entityIndex >> 6;
            int bitOffset = entityIndex & 63;
            if (enabled)
                bitMapPtr[ulongIndex] |= 1UL << bitOffset;
            else
                bitMapPtr[ulongIndex] &= ~(1UL << bitOffset);
        }
    }
}
