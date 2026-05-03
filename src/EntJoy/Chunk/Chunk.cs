using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EntJoy
{
    public sealed unsafe class Chunk : IDisposable
    {
        public Archetype Archetype { get; internal set; }

        private nint _memoryBlock;
        private readonly int _entityCapacity;
        // 组件数据在块内的偏移和大小，索引与 Archetype.Types 一一对应
        private readonly int[] _componentOffsets;
        private readonly int[] _componentSizes;
        private readonly int _totalSize;
        private int _entityCount;

        // enableable 位图信息，索引与 Archetype.Types 对应，-1 表示不可 enable
        private readonly int[] _enableBitOffsets;
        private readonly int[] _enableStrideBytes;

        private const int ENTITY_ARRAY_OFFSET = 0;     // Entity 数组始终在内存块起始处

        public int EntityCount => _entityCount;
        public int Capacity => _entityCapacity;
        public int TotalSize => _totalSize;
        public int ComponentCount => _componentOffsets.Length;
        public nint MemoryBlock => _memoryBlock;

        public Chunk(int entityCapacity, ComponentType[] componentTypes, Archetype archetype)
        {
            Archetype = archetype;
            _entityCapacity = entityCapacity;
            _componentOffsets = new int[componentTypes.Length];
            _componentSizes = new int[componentTypes.Length];
            _enableBitOffsets = new int[componentTypes.Length];
            _enableStrideBytes = new int[componentTypes.Length];

            for (int i = 0; i < _enableBitOffsets.Length; i++)
                _enableBitOffsets[i] = -1;

            _totalSize = CalculateMemoryLayout(componentTypes, entityCapacity);
            _memoryBlock = Marshal.AllocHGlobal(_totalSize);
            Unsafe.InitBlock((byte*)_memoryBlock, 0, (uint)_totalSize);
        }

        private int CalculateMemoryLayout(ComponentType[] componentTypes, int capacity)
        {
            const int cacheLineSize = 64;

            // Entity 数组（置于偏移 0）
            int entityArraySize = capacity * Marshal.SizeOf<Entity>();
            int offset = entityArraySize;

            // 组件数组，每个都缓存行对齐
            for (int i = 0; i < componentTypes.Length; i++)
            {
                int componentSize = componentTypes[i].Size;

                // 对齐
                offset = offset + cacheLineSize - 1 & ~(cacheLineSize - 1);

                _componentOffsets[i] = offset;
                _componentSizes[i] = componentSize;
                offset += capacity * componentSize;

                // enableable 位图
                if (componentTypes[i].IsEnableable)
                {
                    int ulongCount = (capacity + 63) / 64;
                    int bitMapBytes = ulongCount * 8;
                    _enableBitOffsets[i] = offset;
                    _enableStrideBytes[i] = bitMapBytes;
                    offset += bitMapBytes;
                }
            }

            return offset;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddEntity(Entity entity)
        {
            if (_entityCount >= _entityCapacity)
                throw new InvalidOperationException("Chunk is full");

            // 将实体写入 Entity 数组
            ((Entity*)((byte*)_memoryBlock + ENTITY_ARRAY_OFFSET))[_entityCount] = entity;

            // 初始化所有 enableable 位为“启用”
            for (int i = 0; i < _enableBitOffsets.Length; i++)
            {
                if (_enableBitOffsets[i] != -1)
                {
                    ulong* bitMapPtr = (ulong*)((byte*)_memoryBlock + _enableBitOffsets[i]);
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
                Entity* entityArray = (Entity*)((byte*)_memoryBlock + ENTITY_ARRAY_OFFSET);
                entityArray[index] = entityArray[lastIndex];

                // 复制所有组件数据
                for (int i = 0; i < _componentOffsets.Length; i++)
                {
                    int compSize = _componentSizes[i];
                    byte* src = (byte*)_memoryBlock + _componentOffsets[i] + lastIndex * compSize;
                    byte* dst = (byte*)_memoryBlock + _componentOffsets[i] + index * compSize;
                    Unsafe.CopyBlock(dst, src, (uint)compSize);
                }

                // 复制 enableable 位，并清除最后实体的位
                for (int i = 0; i < _enableBitOffsets.Length; i++)
                {
                    if (_enableBitOffsets[i] == -1) continue;
                    ulong* bitMapPtr = (ulong*)((byte*)_memoryBlock + _enableBitOffsets[i]);

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
                for (int i = 0; i < _enableBitOffsets.Length; i++)
                {
                    if (_enableBitOffsets[i] == -1) continue;
                    ulong* bitMapPtr = (ulong*)((byte*)_memoryBlock + _enableBitOffsets[i]);
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
            return ref Unsafe.AsRef<T>(
                (byte*)_memoryBlock + _componentOffsets[componentIndex] + entityIndex * _componentSizes[componentIndex]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref Entity GetEntity(int entityIndex)
        {
            return ref ((Entity*)((byte*)_memoryBlock + ENTITY_ARRAY_OFFSET))[entityIndex];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public nint GetEntityPointer()
        {
            return _memoryBlock + ENTITY_ARRAY_OFFSET;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public nint GetComponentArrayPointer(int componentIndex)
        {
            return _memoryBlock + _componentOffsets[componentIndex];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetComponentOffset(int componentIndex)
        {
            return _componentOffsets[componentIndex];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong* GetEnableBitMapPointer(int componentIndex)
        {
            if (_enableBitOffsets[componentIndex] == -1) return null;
            return (ulong*)((byte*)_memoryBlock + _enableBitOffsets[componentIndex]);
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

        public void Dispose()
        {
            if (_memoryBlock != nint.Zero)
            {
                Marshal.FreeHGlobal(_memoryBlock);
                _memoryBlock = nint.Zero;
            }
        }

        ~Chunk()
        {
            Dispose();
        }
    }
}