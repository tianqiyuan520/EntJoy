using EntJoy.Debugger;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace EntJoy
{
    // Archetype 主要
    public sealed partial class Archetype
    {
        private ComponentType[] types;
        public ReadOnlySpan<ComponentType> Types => types;
        private Dictionary<ComponentType, int> componentTypeRecorder;
        public int ComponentCount { get; private set; }
        public int EntityCount { get; private set; }

        public Archetype(ComponentType[] ts)
        {
            types = ts;
            ComponentCount = ts.Length;
            componentTypeRecorder = new Dictionary<ComponentType, int>(ts.Length);
            for (int i = 0; i < ComponentCount; i++)
            {
                componentTypeRecorder.Add(types[i], i);
            }
            _chunkCapacity = CalculateOptimalChunkCapacity(types);
        }

        /// <summary>
        /// 查询匹配检查：是否符合 QueryBuilder 条件
        /// </summary>
        public bool IsMatch(QueryBuilder builder)
        {
            if (builder.All != null && builder.All.Length > 0)
            {
                if (!HasAllOf(builder.All.AsSpan()))
                    return false;
            }
            if (builder.Any != null && builder.Any.Length > 0)
            {
                if (!HasAnyOf(builder.Any.AsSpan()))
                    return false;
            }
            if (builder.None != null && builder.None.Length > 0)
            {
                if (!HasNoneOf(builder.None.AsSpan()))
                    return false;
            }
            if (builder.AllEnabled != null)
            {
                foreach (var ct in builder.AllEnabled)
                {
                    if (!componentTypeRecorder.ContainsKey(ct))
                        return false;
                }
            }
            return true;
        }
    }

    // 组件类型判定
    public sealed partial class Archetype
    {
        public bool HasAllOf(Span<ComponentType> spanTypes)
        {
            int len = spanTypes.Length;
            for (int i = 0; i < len; i++)
            {
                if (!componentTypeRecorder.ContainsKey(spanTypes[i]))
                    return false;
            }
            return true;
        }

        public bool HasAnyOf(Span<ComponentType> spanTypes)
        {
            int len = spanTypes.Length;
            for (int i = 0; i < len; i++)
            {
                if (componentTypeRecorder.ContainsKey(spanTypes[i]))
                    return true;
            }
            return false;
        }

        public bool HasNoneOf(Span<ComponentType> spanTypes)
        {
            int len = spanTypes.Length;
            for (int i = 0; i < len; i++)
            {
                if (componentTypeRecorder.ContainsKey(spanTypes[i]))
                    return false;
            }
            return true;
        }

        public bool Has(Type type)
        {
            for (int i = 0; i < types.Length; i++)
            {
                if (types[i] == type)
                    return true;
            }
            return false;
        }
    }

    // Archetype Chunk 管理
    public sealed partial class Archetype : IDisposable
    {
        private readonly List<Chunk> _chunkList = new();
        private readonly int _chunkCapacity;
        private const int _chunkHeaderSize = 64;

        public int ChunkCount => _chunkList.Count;
        public ref readonly List<Chunk> ChunkList => ref _chunkList;

        private static int CalculateOptimalChunkCapacity(ComponentType[] types)
        {
            const int targetChunkSize = _chunkHeaderSize * 1024;
            int totalComponentSize = 0;
            foreach (var type in types)
            {
                totalComponentSize += type.Size;
            }
            totalComponentSize += Marshal.SizeOf<Entity>();
            int capacity = Math.Max(_chunkHeaderSize, targetChunkSize / totalComponentSize);
            return (capacity + (_chunkHeaderSize - 1)) & ~(_chunkHeaderSize - 1);
        }

        public void AddEntity(Entity entity, out int chunkIndex, out int slotInChunk)
        {
            Chunk targetChunk = null;
            if (ChunkCount > 0)
            {
                var chunk = _chunkList[^1];
                if (chunk != null && chunk.EntityCount < chunk.Capacity)
                {
                    targetChunk = chunk;
                }
            }

            if (targetChunk == null)
            {
                targetChunk = new Chunk(_chunkCapacity, types, this);
                _chunkList.Add(targetChunk);
            }

            slotInChunk = targetChunk.EntityCount;
            targetChunk.AddEntity(entity);
            chunkIndex = _chunkList.IndexOf(targetChunk);
            EntityCount++;
        }

        public void Remove(int chunkIndex, int slotInChunk, out int movedEntityId, out int movedEntitySlot, out int compactedChunkIndex)
        {
            compactedChunkIndex = -1;
            var chunk = _chunkList[chunkIndex];

            if (chunk.EntityCount == 1)
            {
                movedEntityId = -1;
                movedEntitySlot = -1;
                chunk.RemoveEntity(slotInChunk);
                if (chunk.EntityCount == 0)
                {
                    int lastChunkIndex = _chunkList.Count - 1;
                    if (chunkIndex != lastChunkIndex)
                    {
                        _chunkList[chunkIndex] = _chunkList[lastChunkIndex];
                        compactedChunkIndex = chunkIndex;
                    }
                    _chunkList.RemoveAt(lastChunkIndex);
                    chunk.Dispose();
                }
            }
            else
            {
                int lastEntitySlot = chunk.EntityCount - 1;
                if (slotInChunk == lastEntitySlot)
                {
                    movedEntityId = -1;
                    movedEntitySlot = -1;
                }
                else
                {
                    movedEntityId = chunk.GetEntity(lastEntitySlot).Id;
                    movedEntitySlot = slotInChunk;
                }
                chunk.RemoveEntity(slotInChunk);
            }

            EntityCount--;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T GetComponent<T>(int chunkIndex, int slotInChunk) where T : struct
        {
            var componentIndex = componentTypeRecorder[typeof(T)];
            return ref _chunkList[chunkIndex].GetComponent<T>(slotInChunk, componentIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set<T>(int chunkIndex, int slotInChunk, T value) where T : struct
        {
            var componentIndex = componentTypeRecorder[typeof(T)];
            _chunkList[chunkIndex].GetComponent<T>(slotInChunk, componentIndex) = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void CopyComponentsTo(int sourceChunkIndex, int sourceSlot, Archetype target, int targetChunkIndex, int targetSlot)
        {
            var sourceChunk = _chunkList[sourceChunkIndex];
            var targetChunk = target._chunkList[targetChunkIndex];

            foreach (var type in types)
            {
                if (target.componentTypeRecorder.TryGetValue(type, out int targetComponentIndex))
                {
                    int sourceComponentIndex = componentTypeRecorder[type];
                    var sourcePtr = (byte*)sourceChunk.GetComponentArrayPointer(sourceComponentIndex) + sourceSlot * type.Size;
                    var targetPtr = (byte*)targetChunk.GetComponentArrayPointer(targetComponentIndex) + targetSlot * type.Size;
                    Unsafe.CopyBlock(targetPtr, sourcePtr, (uint)type.Size);
                }
            }
        }

        public List<Chunk> GetChunks()
        {
            return _chunkList;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetComponentTypeIndex<T>()
        {
            return componentTypeRecorder[typeof(T)];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetComponentTypeIndex(ComponentType componentType)
        {
            return componentTypeRecorder[componentType];
        }

        public void Dispose()
        {
            foreach (var chunk in _chunkList)
            {
                chunk.Dispose();
            }
            _chunkList.Clear();
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

    // debug 部分
    public partial class Archetype
    {
        private IntPtr _cachedAddress;

        public IntPtr GetAddress()
        {
            if (_cachedAddress == IntPtr.Zero)
            {
                _cachedAddress = MemoryAddress.GetCachedAddress(this);
            }
            return _cachedAddress;
        }

        public unsafe string GetMemoryLayoutInfo()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== Archetype 内存布局 ===");
            sb.AppendLine($"Archetype Address: {GetAddress().ToInt64():D}");
            sb.AppendLine($"实体数: {EntityCount}, 组件数: {ComponentCount}");

            int chunkCounter = 0;
            foreach (var chunk in _chunkList)
            {
                if (chunk == null) continue;
                chunkCounter++;
                sb.AppendLine($"Chunk: {chunkCounter}/{ChunkCount}");
                sb.AppendLine($"实体数: {chunk.EntityCount}, 组件数: {ComponentCount}");
                var entityArray = (Entity*)chunk.MemoryBlock;
                sb.AppendLine($"  Entity Array: {(long)entityArray:D} 每个size:{Marshal.SizeOf<Entity>()} (Type: {typeof(Entity).Name})");
                for (int i = 0; i < ComponentCount; i++)
                {
                    var componentType = types[i].Type;
                    string typeName = componentType.Name;
                    IntPtr componentArrayPtr = chunk.MemoryBlock + chunk.GetComponentOffset(i);
                    sb.AppendLine($"  Component {i} 地址: {componentArrayPtr.ToInt64():D} 每个size:{types[i].Size} (Type: {typeName})");
                }
            }
            return sb.ToString();
        }
    }
}