using System;
using System.Runtime.CompilerServices;
using EntJoy.Collections;
using EntJoy.JobSystem;

namespace EntJoy.ECS
{
    public readonly struct ArchetypeChunk
    {
        private readonly Chunk _chunk;
        private readonly int _count;
        private readonly QueryReadState _readState;
        private readonly bool _trusted;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ArchetypeChunk(Chunk chunk)
        {
            _chunk = chunk;
            _count = chunk.MemoryBlock != 0 ? chunk.EntityCount : 0;
            if (NativeJobScheduler.IsExecutingJob)
            {
                // Registered Job dependencies protect this chunk; unrelated archetypes may change.
                _readState = default;
                return;
            }
            bool relationships = chunk.MemoryBlock != 0 && chunk.Archetype.HasRelationships;
            _trusted = false;
            _readState = new QueryReadState(chunk.MemoryBlock != 0 ? chunk.Archetype.Owner : null, relationships);
        }

        /// <summary>复用调用方已借用的读状态：同一批 Chunk 只借用一次，不再逐 Chunk 取令牌。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ArchetypeChunk(Chunk chunk, in QueryReadState readState)
        {
            _chunk = chunk;
            _count = chunk.MemoryBlock != 0 ? chunk.EntityCount : 0;
            _readState = readState;
            _trusted = true;
        }

        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (_chunk.MemoryBlock == nint.Zero) return 0;
                ThrowIfNull();
                return _count;
            }
        }

        // 安全句柄在应用域生命周期内持续有效，无需显式释放。
        // 共享句柄（多 chunk view 复用同一句柄）豁免并行写冲突检测——chunk 并行 tile 写不同 chunk 同列是合法场景。
        private static readonly AtomicSafetyHandle s_chunkViewSafety = CreateChunkViewSafety();

        private static AtomicSafetyHandle CreateChunkViewSafety()
        {
            var h = SafetyHandleManager.Allocate();
            SafetyHandleManager.ExemptWriteTracking(h.Index);
            return h;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowIfNull()
        {
            if (_chunk.MemoryBlock == nint.Zero)
                throw new InvalidOperationException("ArchetypeChunk is not initialized (default constructed or chunk was disposed).");
            if (!_trusted) _readState.Validate();
            _chunk.Archetype.CheckReadable();
        }

        // Span 访问（原有）

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe Span<T> GetComponentDataSpan<T>() where T : struct
        {
            ThrowIfNull();
            ComponentTypeManager.CheckWritableAccess<T>();
            int idx = _chunk.Archetype.GetComponentTypeIndex<T>();
            T* ptr = (T*)((byte*)_chunk.MemoryBlock + _chunk.GetComponentOffset(idx));
            return new Span<T>(ptr, _count);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe NativeArray<T> GetComponentDataNativeArray<T>() where T : unmanaged
        {
            ThrowIfNull();
            ComponentTypeManager.CheckWritableAccess<T>();
            int idx = _chunk.Archetype.GetComponentTypeIndex<T>();
            void* ptr = (void*)_chunk.GetComponentArrayPointer(idx);
            return NativeArray<T>.CreateView(ptr, _count, s_chunkViewSafety, Allocator.None);
        }

        /// <summary>只读借用；Job 完成或结构/关系改变后不得继续持有该 Span。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe ReadOnlySpan<T> GetComponentReadOnlyDataSpan<T>() where T : struct
        {
            ThrowIfNull();
            int index = _chunk.Archetype.GetComponentTypeIndex<T>();
            return new ReadOnlySpan<T>((void*)_chunk.GetComponentArrayPointer(index), _count);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe NativeArray<T>.ReadOnly GetComponentReadOnlyDataNativeArray<T>() where T : unmanaged
        {
            ThrowIfNull();
            int index = _chunk.Archetype.GetComponentTypeIndex<T>();
            return NativeArray<T>.CreateView((void*)_chunk.GetComponentArrayPointer(index), _count,
                s_chunkViewSafety, Allocator.None).AsReadOnly();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe T* GetComponentDataPtr<T>() where T : struct
        {
            ThrowIfNull();
            int idx = _chunk.Archetype.GetComponentTypeIndex<T>();
            return (T*)((byte*)_chunk.MemoryBlock + _chunk.GetComponentOffset(idx));
        }

        // Entity 访问

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe Span<Entity> GetEntitySpan()
        {
            ThrowIfNull();
            Entity* ptr = (Entity*)_chunk.MemoryBlock;
            return new Span<Entity>(ptr, _count);
        }

        // Shared Component 访问

        /// <summary>
        /// 读取该 chunk 的 blittable SharedComponent 值（per-chunk 共享，非 per-entity）。
        /// 转译器把本调用翻成 <c>reinterpret_cast&lt;T*&gt;(__chunkData-&gt;sharedValuePtrs[sharedIdx])</c>。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe T GetSharedComponent<T>() where T : unmanaged
        {
            ThrowIfNull();
            int compIdx = _chunk.Archetype.GetComponentTypeIndex<T>();
            var ct = _chunk.Archetype.Types[compIdx];
            if (!ct.IsShared)
                throw new InvalidOperationException($"Component {typeof(T).Name} is not a shared component.");
            if (ct.IsManagedShared)
                throw new InvalidOperationException(
                    $"Shared component {typeof(T).Name} is managed. " +
                    "Managed shared components cannot be read from ArchetypeChunk. " +
                    "Use EntityManager.GetSharedComponent<T>(entity) instead.");
            var ptr = _chunk.GetSharedValuePointer(compIdx);
            if (ptr == nint.Zero)
                throw new InvalidOperationException($"Shared values area not initialized for this chunk.");
            return Unsafe.AsRef<T>((void*)ptr);
        }

        // Enableable 访问

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe BitMask GetEnabledMask<T>() where T : struct, IEnableableComponent
        {
            ThrowIfNull();
            int idx = _chunk.Archetype.GetComponentTypeIndex<T>();
            ulong* ptr = _chunk.GetEnableBitMapPointer(idx);
            if (ptr == null)
                throw new InvalidOperationException($"Component {typeof(T).Name} is not enableable.");
            return new BitMask(ptr, _count);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsComponentEnabled<T>(int entityIndex) where T : struct, IEnableableComponent
        {
            ThrowIfNull();
            int idx = _chunk.Archetype.GetComponentTypeIndex<T>();
            return _chunk.GetComponentEnabled(idx, entityIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetComponentEnabled<T>(int entityIndex, bool enabled) where T : struct, IEnableableComponent
        {
            ThrowIfNull();
            int idx = _chunk.Archetype.GetComponentTypeIndex<T>();
            _chunk.SetComponentEnabled(idx, entityIndex, enabled);
        }

        /// <summary>
        /// 该 chunk 内组件 T 的逐实体启用位图指针（每实体 1 bit；位 i 对应 chunk 内第 i 个实体），
        /// 组件非 enableable 时返回 null。原生侧按 <c>GetComponentDataNativeArray&lt;T&gt;()</c> 的 required
        /// 序号取用，因此必须同时访问该组件列，否则生成期报错。位图属于 chunk 内存，同一 chunk 只应由一个 worker 写。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe ulong* GetEnableBitMapPtr<T>() where T : struct, IEnableableComponent
        {
            ThrowIfNull();
            int idx = _chunk.Archetype.GetComponentTypeIndex<T>();
            return _chunk.GetEnableBitMapPointer(idx);
        }
    }
}
