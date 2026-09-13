using System;
using System.Runtime.CompilerServices;
using EntJoy.Collections;

namespace EntJoy.ECS
{
    public struct ArchetypeChunk
    {
        private readonly Chunk _chunk;

        internal ArchetypeChunk(Chunk chunk) => _chunk = chunk;

        public int Count => _chunk.MemoryBlock != nint.Zero ? _chunk.EntityCount : 0;

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
        }

        // ======================== Span 访问（原有） ========================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe Span<T> GetComponentDataSpan<T>() where T : struct
        {
            ThrowIfNull();
            int idx = _chunk.Archetype.GetComponentTypeIndex<T>();
            T* ptr = (T*)((byte*)_chunk.MemoryBlock + _chunk.GetComponentOffset(idx));
            return new Span<T>(ptr, Count);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe NativeArray<T> GetComponentDataNativeArray<T>() where T : unmanaged
        {
            ThrowIfNull();
            int idx = _chunk.Archetype.GetComponentTypeIndex<T>();
            void* ptr = (void*)_chunk.GetComponentArrayPointer(idx);
            return NativeArray<T>.CreateView(ptr, Count, s_chunkViewSafety, Allocator.None);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe T* GetComponentDataPtr<T>() where T : struct
        {
            ThrowIfNull();
            int idx = _chunk.Archetype.GetComponentTypeIndex<T>();
            return (T*)((byte*)_chunk.MemoryBlock + _chunk.GetComponentOffset(idx));
        }

        // ======================== Entity 访问 ========================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe Span<Entity> GetEntitySpan()
        {
            ThrowIfNull();
            Entity* ptr = (Entity*)_chunk.MemoryBlock;
            return new Span<Entity>(ptr, Count);
        }

        // ======================== Shared Component 访问 ========================

        /// <summary>
        /// 读取该 chunk 的 blittable SharedComponent 值（per-chunk 共享，非 per-entity）。
        /// 翻译器（CppChunkStatementTranslator）会将此调用转换为 C++ 的
        ///   reinterpret_cast&lt;T*&gt;(__chunkData->sharedValuePtrs[sharedIdx])
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

        // ======================== Enableable 访问 ========================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe BitMask GetEnabledMask<T>() where T : struct, IEnableableComponent
        {
            ThrowIfNull();
            int idx = _chunk.Archetype.GetComponentTypeIndex<T>();
            ulong* ptr = _chunk.GetEnableBitMapPointer(idx);
            if (ptr == null)
                throw new InvalidOperationException($"Component {typeof(T).Name} is not enableable.");
            return new BitMask(ptr, Count);
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
        /// 该 chunk 内组件 T 的 **逐实体 enable 位图指针**（每实体 1 bit，64 位字；位 i 对应 chunk 内第 i 个实体）。
        /// 组件非 enableable 时返回 null。
        ///
        /// 原生路径（P1-6/P1-7）：转译器把本调用翻成
        /// <c>reinterpret_cast&lt;unsigned long long*&gt;(__chunkData-&gt;requiredEnableBitMaps[requiredIdx])</c>，
        /// **requiredIdx 按 `GetComponentDataNativeArray&lt;T&gt;()` 的 required 序号对齐** ⇒ 原生 job 内
        /// 必须同时访问该组件的组件列（否则该类型不在 required 列表里，生成期直接报错）。
        /// 并发纪律：位图是 chunk 内存的一部分，同一 chunk 的位图只应由一个 worker 写。
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
