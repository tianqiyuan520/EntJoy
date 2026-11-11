using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace EntJoy.ECS
{
    /// <summary>
    /// 稀疏随机访问句柄：缓存上次原型的列下标与块基址，<c>lookup[entity]</c> 稳态退化为一次位置表加载加指针比较。
    /// 持有 EntityManager/Archetype/Chunk 强引用，Dispose 后失效；仅限主线程。
    /// 结构变更推进 StructuralVersion 使缓存失效，Set/SetEnabled 不推进。
    /// </summary>
    public unsafe struct ComponentLookup<T> where T : struct
    {
        private readonly EntityManager _em;
        private Archetype _archetype;
        private int _componentIndex;
        private int _stride;
        private Chunk _chunk;
        private byte* _base;
        private int _version;

        internal ComponentLookup(EntityManager em)
        {
            _em = em;
            _archetype = null;
            _componentIndex = -1;
            _stride = Unsafe.SizeOf<T>();
            _chunk = default;
            _base = null;
            _version = int.MinValue;
        }

        /// <summary>把缓存解析到给定 archetype/chunk（列索引 + 基址），结构版本变更时失效。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureResolved(Archetype arch, int chunkIndex)
        {
            if (_version != _em.StructuralVersion)
            {
                _archetype = null;
                _chunk = default;
                _version = _em.StructuralVersion;
            }

            if (arch != _archetype)
            {
                _archetype = arch;
                _componentIndex = arch.GetComponentTypeIndex<T>();
                Debug.Assert(_stride == arch.Types[_componentIndex].Size, "ComponentLookup<T> stride mismatch");
            }

            var chunk = arch.ChunkList[chunkIndex];
            if (chunk.MemoryBlock != _chunk.MemoryBlock)
            {
                _chunk = chunk;
                _base = (byte*)chunk.GetComponentArrayPointer(_componentIndex);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ref T Resolve(Entity entity, bool validate)
        {
            if (_em == null) throw new InvalidOperationException("Component lookup is not initialized.");
            if (validate) _em.ValidateEntity(entity);
            ref var info = ref _em.GetEntityInfoRef(entity.Id);

            EnsureResolved(info.Archetype, info.ChunkIndex);
            return ref Unsafe.AsRef<T>(_base + info.SlotInChunk * _stride);
        }

        public ref T this[Entity entity]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                ComponentTypeManager.CheckWritableAccess<T>();
                return ref Resolve(entity, validate: true);
            }
        }

        /// <summary>跳过 null/version 校验的快速变体（调用方保证句柄有效）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T UnsafeRef(Entity entity)
        {
            ComponentTypeManager.CheckWritableAccess<T>();
            return ref Resolve(entity, validate: false);
        }

        internal ref readonly T ReadOnly(Entity entity) => ref Resolve(entity, validate: true);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsEnabled(Entity entity)
        {
            _em.ValidateEntity(entity);
            ref var info = ref _em.GetEntityInfoRef(entity.Id);
            EnsureResolved(info.Archetype, info.ChunkIndex);
            return _chunk.GetComponentEnabled(_componentIndex, info.SlotInChunk);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetEnabled(Entity entity, bool enabled)
        {
            _em.ValidateEntity(entity);
            ref var info = ref _em.GetEntityInfoRef(entity.Id);
            EnsureResolved(info.Archetype, info.ChunkIndex);
            _chunk.SetComponentEnabled(_componentIndex, info.SlotInChunk, enabled);
        }
    }
}
