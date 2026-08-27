using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace EntJoy.ECS
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
        private int _version;        // 组件数据修改次数（变更追踪）
        private int _enableVersion;  // 实体增删/启用状态变化次数（位图缓存失效依据）

        private const int ENTITY_ARRAY_OFFSET = 0;

        public Archetype Archetype => Meta.Archetype;
        public int EntityCount => _entityCount;
        public int Capacity => Meta.EntityCapacity;
        public int TotalSize => Meta.TotalSize;
        public int ComponentCount => Meta.ComponentCount;

        /// <summary>组件数据修改版本号，用于变更追踪的 Chunk 级快速过滤。</summary>
        public int Version => _version;

        /// <summary>位图版本号：实体增删、启用状态变化时递增，用于组合位图缓存失效判断。</summary>
        public int EnableVersion => _enableVersion;

        /// <summary>本 Chunk 是否在指定版本号之后有组件数据变更。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HasChangesSince(int version)
        {
            return _version > version;
        }

        /// <summary>递增组件数据修改版本号。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void IncrementVersion()
        {
            Interlocked.Increment(ref _version);
        }

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
            // 延迟清零：AddEntity 逐 slot 初始化组件与 enableable 位，未使用 slot 不被访问。
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
            _enableVersion++;  // 位图布局变化，递增使缓存失效
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
            _enableVersion++;  // swap-pop 使位图内容变化，递增使缓存失效
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
            _enableVersion++;
        }

        // ======================== Shared values 区 ========================
        // blittable shared 内联存值；managed shared 槽位只存 int 索引（指向 EntityManager 哈希桶数组）。
        // 同一 Chunk 所有实体共享相同的 shared 值组合（不变式）。

        /// <summary>Shared values 区是否启用（该 Archetype 含 shared 组件）。</summary>
        public bool HasSharedValues => Meta.SharedValuesOffset != -1;

        /// <summary>Shared values 区起始字节指针（-1 时返回 null）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte* GetSharedValuesPointer()
        {
            if (Meta.SharedValuesOffset == -1) return null;
            return (byte*)MemoryBlock + Meta.SharedValuesOffset;
        }

        /// <summary>读取 blittable shared 值（chunk 内存块内联）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T GetSharedValue<T>(int componentIndex) where T : struct
        {
            if (Meta.SharedValueOffsets[componentIndex] == -1)
                throw new InvalidOperationException($"Component index {componentIndex} is not a shared component.");
            return Unsafe.AsRef<T>((byte*)MemoryBlock + Meta.SharedValueOffsets[componentIndex]);
        }

        /// <summary>写入 blittable shared 值（chunk 内存块内联）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetSharedValue<T>(int componentIndex, in T value) where T : struct
        {
            if (Meta.SharedValueOffsets[componentIndex] == -1)
                throw new InvalidOperationException($"Component index {componentIndex} is not a shared component.");
            Unsafe.AsRef<T>((byte*)MemoryBlock + Meta.SharedValueOffsets[componentIndex]) = value;
        }

        /// <summary>读取 managed shared 索引（指向 EntityManager 哈希桶值数组）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetSharedValueIndex(int componentIndex)
        {
            if (Meta.SharedValueOffsets[componentIndex] == -1)
                throw new InvalidOperationException($"Component index {componentIndex} is not a shared component.");
            return *(int*)((byte*)MemoryBlock + Meta.SharedValueOffsets[componentIndex]);
        }

        /// <summary>写入 managed shared 索引（指向 EntityManager 哈希桶值数组）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetSharedValueIndex(int componentIndex, int index)
        {
            if (Meta.SharedValueOffsets[componentIndex] == -1)
                throw new InvalidOperationException($"Component index {componentIndex} is not a shared component.");
            *(int*)((byte*)MemoryBlock + Meta.SharedValueOffsets[componentIndex]) = index;
        }

        /// <summary>shared 组件在 chunk 内的槽位指针（NativeTranspiler ABI 用：blittable 或 int 索引）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public nint GetSharedValuePointer(int componentIndex)
        {
            if (Meta.SharedValueOffsets[componentIndex] == -1) return nint.Zero;
            return MemoryBlock + Meta.SharedValueOffsets[componentIndex];
        }

        // ======================== 变更追踪 ========================
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong* GetChangedBitMaskPointer()
        {
            if (Meta.ChangedBitMaskOffset == -1) return null;
            return (ulong*)((byte*)MemoryBlock + Meta.ChangedBitMaskOffset);
        }

        /// <summary>检查指定实体是否在本帧被修改。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsEntityChanged(int entityIndex)
        {
            ulong* bitMapPtr = GetChangedBitMaskPointer();
            if (bitMapPtr == null) return false;
            int ulongIndex = entityIndex >> 6;
            int bitOffset = entityIndex & 63;
            return (bitMapPtr[ulongIndex] & 1UL << bitOffset) != 0;
        }

        /// <summary>标记指定实体为已修改。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MarkEntityChanged(int entityIndex)
        {
            ulong* bitMapPtr = GetChangedBitMaskPointer();
            if (bitMapPtr == null) return;
            int ulongIndex = entityIndex >> 6;
            int bitOffset = entityIndex & 63;
            bitMapPtr[ulongIndex] |= 1UL << bitOffset;
        }

        /// <summary>清除所有实体的变更标记（帧末调用）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClearChangedBitMask()
        {
            ulong* bitMapPtr = GetChangedBitMaskPointer();
            if (bitMapPtr == null) return;
            int ulongCount = (Meta.EntityCapacity + 63) / 64;
            for (int i = 0; i < ulongCount; i++)
                bitMapPtr[i] = 0;
        }

        /// <summary>Chunk 中是否有任何实体被修改过。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HasAnyEntityChanged()
        {
            ulong* bitMapPtr = GetChangedBitMaskPointer();
            if (bitMapPtr == null) return false;
            int ulongCount = (_entityCount + 63) / 64;
            for (int i = 0; i < ulongCount; i++)
            {
                if (bitMapPtr[i] != 0) return true;
            }
            return false;
        }
    }
}
