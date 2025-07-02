using EntJoy.Debugger;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace EntJoy
{
    //Archetype 主要
    public sealed partial class Archetype
    {
        private ComponentType[] types;  // 该原型对应的组件类型数组

        public ReadOnlySpan<ComponentType> Types => types;  // 该原型对应的组件类型数组[只读]
        // private StructArray[] structArrays;  // 组件数据数组
        // private StructArray<Entity> entities;  // 实体数组
        private Dictionary<ComponentType, int> componentTypeRecorder;  // 组件类型与索引映射(用来查找保存的组件类型)
        public int ComponentCount { get; private set; }  // 组件数量
        public int EntityCount { get; private set; }  // 实体数量

        public Archetype(ComponentType[] ts)
        {
            types = ts;
            ComponentCount = ts.Length;
            componentTypeRecorder = new Dictionary<ComponentType, int>(ts.Length);
            for (int i = 0; i < ComponentCount; i++)
            {
                componentTypeRecorder.Add(types[i], i);  // 添加类型到索引映射
            }
            _chunkCapacity = CalculateOptimalChunkCapacity(types);
        }

        /// <summary>
        /// 获取组件类型对应的索引
        /// </summary>
        //public int GetComponentTypeIndex<T>()
        //{
        //    return componentTypeRecorder[typeof(T)];
        //}

        // 获取内存布局信息
        //public unsafe string GetMemoryLayoutInfo()
        //{
        //    var sb = new StringBuilder();
        //    sb.AppendLine($"=== Archetype 内存布局 ===");
        //    sb.AppendLine($"Archetype Address: {GetAddress().ToInt64():D}");
        //    sb.AppendLine($"实体数: {EntityCount}, 组件数: {ComponentCount}");

        //    int chunkCounter = 0;
        //    foreach (var chunk in ChunkList)
        //    {
        //        chunkCounter++;
        //        sb.AppendLine($"Chunk: {chunkCounter}/{ChunkCount}");
        //        sb.AppendLine($"实体数: {chunk.Entities.Length}, 组件数: {chunk.Components.Length}");
        //        // 输出实体数组信息
        //        ref var entityArray = ref chunk.Entities;
        //        IntPtr entityArrayAddr = entityArray?.GetDataPointer() ?? IntPtr.Zero;
        //        sb.AppendLine($"  Entity Array: {entityArrayAddr.ToInt64():D}");

        //        // 输出组件数组信息
        //        IntPtr prevAddress = IntPtr.Zero;
        //        int preSize = 0;
        //        for (int i = 0; i < ComponentCount; i++)
        //        {
        //            ref var array = ref chunk.Components[i];
        //            IntPtr addr = IntPtr.Zero;
        //            int size = array.GetMemorySize();

        //            // 使用反射调用GetDataPointer方法
        //            var method = array.GetType().GetMethod("GetDataPointer");
        //            if (method != null)
        //            {
        //                addr = (IntPtr)method.Invoke(array, null);
        //            }

        //            sb.AppendLine($"  Component {i} ({types[i].Type.Name}):");
        //            sb.AppendLine($"    Array Address: {addr.ToInt64():D}");
        //            sb.AppendLine($"    Memory Size: {size} bytes");

        //            // 计算与前一个组件的地址差
        //            if (prevAddress != IntPtr.Zero && addr != IntPtr.Zero)
        //            {
        //                long gap = (long)addr - (long)prevAddress;
        //                sb.AppendLine($"  与前一个组件的地址差: {preSize}+{gap} bytes");
        //            }

        //            prevAddress = (addr != IntPtr.Zero) ? addr + size : IntPtr.Zero;
        //            preSize = size;
        //        }
        //    }


        //    return sb.ToString();
        //}
    }

    //判断Archetype的组件类型
    public partial class Archetype
    {
        /// <summary>
        /// 检查是否包含所有指定组件
        /// </summary>
        public bool HasAllOf(Span<ComponentType> spanTypes)
        {
            int len = spanTypes.Length;
            for (int i = 0; i < len; i++)
            {
                if (!componentTypeRecorder.ContainsKey(spanTypes[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 检查是否包含任意指定组件
        /// </summary>
        public bool HasAnyOf(Span<ComponentType> spanTypes)
        {
            int len = spanTypes.Length;
            for (int i = 0; i < len; i++)
            {
                if (componentTypeRecorder.ContainsKey(spanTypes[i]))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 检查是否不包含所有指定组件
        /// </summary>
        public bool HasNoneOf(Span<ComponentType> spanTypes)
        {
            int len = spanTypes.Length;
            for (int i = 0; i < len; i++)
            {
                if (componentTypeRecorder.ContainsKey(spanTypes[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 检查是否包含指定类型组件
        /// </summary>
        /// <param name="type"></param>
        public bool Has(Type type)
        {
            for (int i = 0; i < types.Length; i++)
            {
                if (types[i] == type)
                {
                    return true;
                }
            }

            return false;
        }
    }

    //Archetype Chunk
    public sealed partial class Archetype : IDisposable
    {
        private readonly List<Chunk> ChunkList = new();
        private readonly int _chunkCapacity;
        public int ChunkCount => ChunkList.Count;

        // 计算最佳Chunk容量
        private static int CalculateOptimalChunkCapacity(ComponentType[] types)
        {
            const int targetChunkSize = 16 * 1024; // 目标16KB大小
            int totalComponentSize = 0;

            foreach (var type in types)
            {
                totalComponentSize += Marshal.SizeOf(type.Type);
            }

            // 加上实体ID的大小
            totalComponentSize += Marshal.SizeOf<Entity>();

            // 计算实体数量，对齐到16的倍数
            int capacity = Math.Max(16, targetChunkSize / totalComponentSize);
            return (capacity + 15) & ~15; // 取16的整数倍 (最少16个实体) 并按16字节对齐
        }
        // 增加实体
        public void AddEntity(Entity entity, out int chunkIndex, out int slotInChunk)
        {
            // 查找有空位的Chunk或创建新Chunk
            Chunk targetChunk = null;
            foreach (var chunk in ChunkList)
            {
                if (chunk.EntityCount < chunk.Capacity)
                {
                    targetChunk = chunk;
                    break;
                }
            }

            if (targetChunk == null)
            {
                targetChunk = new Chunk(_chunkCapacity, types);
                ChunkList.Add(targetChunk);
            }

            // 添加实体
            slotInChunk = targetChunk.EntityCount;
            targetChunk.AddEntity(entity);
            chunkIndex = ChunkList.IndexOf(targetChunk);
            EntityCount++;
        }

        public void Remove(int chunkIndex, int slotInChunk, out int movedEntityId, out int movedEntitySlot)
        {
            var chunk = ChunkList[chunkIndex];

            if (chunk.EntityCount == 1)
            {
                // 如果是最后一个实体，直接清理
                movedEntityId = -1;
                movedEntitySlot = -1;
                chunk.RemoveEntity(slotInChunk);

                // 如果Chunk空了，可以回收
                if (chunk.EntityCount == 0)
                {
                    ChunkList.RemoveAt(chunkIndex);
                    chunk.Dispose();
                }
            }
            else
            {
                // 记录被移动的实体ID
                movedEntityId = chunk.GetEntity(chunk.EntityCount - 1).Id;
                movedEntitySlot = slotInChunk;

                // 移除实体
                chunk.RemoveEntity(slotInChunk);
            }

            EntityCount--;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T GetComponent<T>(int chunkIndex, int slotInChunk) where T : struct
        {
            var componentIndex = componentTypeRecorder[typeof(T)];
            return ref ChunkList[chunkIndex].GetComponent<T>(slotInChunk, componentIndex);
        }

        public void Set<T>(int chunkIndex, int slotInChunk, T value) where T : struct
        {
            var componentIndex = componentTypeRecorder[typeof(T)];
            ChunkList[chunkIndex].GetComponent<T>(slotInChunk, componentIndex) = value;
        }

        public unsafe void CopyComponentsTo(int sourceChunkIndex, int sourceSlot, Archetype target, int targetChunkIndex, int targetSlot)
        {
            var sourceChunk = ChunkList[sourceChunkIndex];
            var targetChunk = target.ChunkList[targetChunkIndex];

            // 复制所有共有组件
            foreach (var type in types)
            {
                if (target.componentTypeRecorder.TryGetValue(type, out int targetComponentIndex))
                {
                    int sourceComponentIndex = componentTypeRecorder[type];
                    var sourcePtr = (byte*)sourceChunk.GetComponentArrayPointer(sourceComponentIndex) + sourceSlot * Marshal.SizeOf(type.Type);
                    var targetPtr = (byte*)targetChunk.GetComponentArrayPointer(targetComponentIndex) + targetSlot * Marshal.SizeOf(type.Type);

                    Unsafe.CopyBlock(targetPtr, sourcePtr, (uint)Marshal.SizeOf(type.Type));
                }
            }
        }

        public List<Chunk> GetChunks()
        {
            return ChunkList;
        }

        public int GetComponentTypeIndex<T>()
        {
            return componentTypeRecorder[typeof(T)];
        }

        public void Dispose()
        {
            foreach (var chunk in ChunkList)
            {
                chunk.Dispose();
            }
            ChunkList.Clear();
        }

        ~Archetype()
        {
            Dispose();

            if (_cachedAddress != IntPtr.Zero)
            {
                MemoryAddress.ClearAddressCache(this);
            }
        }
    }


    //Archetype 地址
    public partial class Archetype
    {
        private IntPtr _cachedAddress;

        // 获取原型自身地址(缓存)
        public IntPtr GetAddress()
        {
            if (_cachedAddress == IntPtr.Zero)
            {
                _cachedAddress = MemoryAddress.GetCachedAddress(this);
            }
            return _cachedAddress;
        }


        // 获取内存布局信息
        public unsafe string GetMemoryLayoutInfo()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== Archetype 内存布局 ===");
            sb.AppendLine($"Archetype Address: {GetAddress().ToInt64():D}");
            sb.AppendLine($"实体数: {EntityCount}, 组件数: {ComponentCount}");

            int chunkCounter = 0;
            foreach (var chunk in ChunkList)
            {
                if (chunk == null) continue;
                chunkCounter++;
                sb.AppendLine($"Chunk: {chunkCounter}/{ChunkCount}");
                sb.AppendLine($"实体数: {chunk.EntityCount}, 组件数: {ComponentCount}");
                // 输出实体数组信息
                var entityArray = (Entity*)chunk.MemoryBlock;
                sb.AppendLine($"  Entity Array: {(long)entityArray:D} 每个size:{Marshal.SizeOf<Entity>()} (Type: {typeof(Entity).Name})");

                // 输出组件数组信息
                for (int i = 0; i < ComponentCount; i++)
                {
                    // 获取当前组件的类型名称
                    var componentType = types[i].Type;
                    string typeName = componentType.Name;

                    // 计算组件数组的起始地址
                    IntPtr componentArrayPtr = chunk.MemoryBlock + chunk.GetComponentOffset(i);
                    sb.AppendLine($"  Component {i} 地址: {componentArrayPtr.ToInt64():D} 每个size:{Marshal.SizeOf(componentType)} (Type: {typeName})");
                }

            }


            return sb.ToString();
        }
    }

}
