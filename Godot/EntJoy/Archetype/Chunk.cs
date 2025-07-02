using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EntJoy
{
    public sealed unsafe class Chunk : IDisposable
    {
        // 内存块指针
        private IntPtr _memoryBlock;
        private readonly int _entityCapacity;
        private readonly int[] _componentOffsets;
        private readonly int _totalSize;
        private int _entityCount;

        public int EntityCount => _entityCount;
        public int Capacity => _entityCapacity;
        public int TotalSize => _totalSize;
        public int ComponentCount => _componentOffsets.Length;

        public IntPtr MemoryBlock => _memoryBlock;
        public Chunk(int entityCapacity, ComponentType[] componentTypes)
        {
            _entityCapacity = entityCapacity;// 实体容量
            _componentOffsets = new int[componentTypes.Length]; // 每种组件的偏移量

            // 计算总内存大小和组件偏移量
            _totalSize = CalculateMemoryLayout(componentTypes, entityCapacity);

            // 分配连续内存块
            _memoryBlock = Marshal.AllocHGlobal(_totalSize);

            // 清空内存
            Unsafe.InitBlock((byte*)_memoryBlock, 0, (uint)_totalSize);
        }

        private int CalculateMemoryLayout(ComponentType[] componentTypes, int capacity)
        {
            int offset = 0;
            const int cacheLineSize = 64; // 64字节缓存行对齐

            // 实体数组放在最前面
            int entityArraySize = capacity * Marshal.SizeOf<Entity>();
            _componentOffsets[0] = 0;
            offset += entityArraySize; // 偏移量为实体数组大小

            // 组件数组按顺序排列，每个组件数组都缓存行对齐
            for (int i = 0; i < componentTypes.Length; i++)
            {
                int componentSize = Marshal.SizeOf(componentTypes[i].Type);
                int componentArraySize = capacity * componentSize;

                // 对齐到缓存行
                offset = (offset + cacheLineSize - 1) & ~(cacheLineSize - 1);
                _componentOffsets[i] = offset;

                offset += componentArraySize;
            }

            return offset;
        }

        /// <summary>
        /// 添加实体
        /// </summary>
        public void AddEntity(Entity entity)
        {
            if (_entityCount >= _entityCapacity)
                throw new InvalidOperationException("Chunk is full");

            // 写入实体ID
            ((Entity*)_memoryBlock)[_entityCount] = entity;
            _entityCount++;
        }

        public void RemoveEntity(int index)
        {
            if (index < 0 || index >= _entityCount)
                throw new IndexOutOfRangeException();

            // 用最后一个实体覆盖被移除的实体
            if (_entityCount > 1 && index != _entityCount - 1)
            {
                // 复制实体数据
                ((Entity*)_memoryBlock)[index] = ((Entity*)_memoryBlock)[_entityCount - 1];


                // 复制所有组件数据 指定实体的组件数据复制到另一个实体的位置
                for (int i = 0; i < _componentOffsets.Length; i++)
                {
                    int componentSize = Marshal.SizeOf(((Entity*)_memoryBlock)[index].GetType());
                    byte* source = (byte*)_memoryBlock + _componentOffsets[i] + (_entityCount - 1) * componentSize;
                    byte* dstination = (byte*)_memoryBlock + _componentOffsets[i] + index * componentSize;

                    Unsafe.CopyBlock(dstination, source, (uint)componentSize);
                }
            }

            _entityCount--;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T GetComponent<T>(int entityIndex, int componentIndex) where T : struct
        {
            if (entityIndex < 0 || entityIndex >= _entityCount)
                throw new IndexOutOfRangeException();
            return ref Unsafe.AsRef<T>((byte*)_memoryBlock + _componentOffsets[componentIndex] + entityIndex * Marshal.SizeOf<T>());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref Entity GetEntity(int entityIndex)
        {
            if (entityIndex < 0 || entityIndex >= _entityCount)
                throw new IndexOutOfRangeException();

            return ref ((Entity*)_memoryBlock)[entityIndex];
        }

        // 获取特定组件的数组指针
        public IntPtr GetComponentArrayPointer(int componentIndex)
        {
            return _memoryBlock + _componentOffsets[componentIndex];
        }
        // 获取特定组件的偏移量
        public int GetComponentOffset(int componentIndex)
        {
            return _componentOffsets[componentIndex];
        }

        public int GetComponentSize<T>()
        {
            return Marshal.SizeOf<T>();
        }
        public int GetComponentSize(ComponentType componentType)
        {
            return Marshal.SizeOf(componentType.Type);
        }
        public int GetEntitySize()
        {
            return Marshal.SizeOf<Entity>();
        }

        public void Dispose()
        {
            if (_memoryBlock != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_memoryBlock);
                _memoryBlock = IntPtr.Zero;
            }
        }

        ~Chunk()
        {
            Dispose();
        }
    }
}