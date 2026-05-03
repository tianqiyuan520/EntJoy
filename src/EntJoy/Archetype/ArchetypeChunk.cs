using System;
using System.Runtime.CompilerServices;

namespace EntJoy
{
    public struct ArchetypeChunk
    {
        private readonly Chunk _chunk;

        internal ArchetypeChunk(Chunk chunk) => _chunk = chunk;

        public int Count => _chunk.EntityCount;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe Span<T> GetComponentDataSpan<T>() where T : struct
        {
            int idx = _chunk.Archetype.GetComponentTypeIndex<T>();
            T* ptr = (T*)((byte*)_chunk.MemoryBlock + _chunk.GetComponentOffset(idx));
            return new Span<T>(ptr, Count);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe T* GetComponentDataPtr<T>() where T : struct
        {
            int idx = _chunk.Archetype.GetComponentTypeIndex<T>();
            return (T*)((byte*)_chunk.MemoryBlock + _chunk.GetComponentOffset(idx));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe Span<Entity> GetEntitySpan()
        {
            Entity* ptr = (Entity*)_chunk.MemoryBlock;
            return new Span<Entity>(ptr, Count);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe BitMask GetEnabledMask<T>() where T : struct, IEnableableComponent
        {
            int idx = _chunk.Archetype.GetComponentTypeIndex<T>();
            ulong* ptr = _chunk.GetEnableBitMapPointer(idx);
            if (ptr == null)
                throw new InvalidOperationException($"Component {typeof(T).Name} is not enableable.");
            return new BitMask(ptr, Count);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsComponentEnabled<T>(int entityIndex) where T : struct, IEnableableComponent
        {
            int idx = _chunk.Archetype.GetComponentTypeIndex<T>();
            return _chunk.GetComponentEnabled(idx, entityIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetComponentEnabled<T>(int entityIndex, bool enabled) where T : struct, IEnableableComponent
        {
            int idx = _chunk.Archetype.GetComponentTypeIndex<T>();
            _chunk.SetComponentEnabled(idx, entityIndex, enabled);
        }
    }
}