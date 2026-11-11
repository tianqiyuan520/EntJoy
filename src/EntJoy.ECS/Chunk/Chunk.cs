using System;
using System.Runtime.CompilerServices;

namespace EntJoy.ECS
{
    /// <summary>
    /// 一个数据块：持有指向非托管内存的指针与共享元数据，组件按 SoA 排布在其中。
    /// 布局信息（列偏移、列大小、启用位图、变更位图、shared 区）都在 <see cref="ChunkMetadata"/> 里，同原型各块共享。
    /// </summary>
    /// <remarks>
    /// 值类型：经 <c>ChunkList[i]</c> 或 <c>foreach</c> 取到的是副本。改托管字段的成员（<see cref="AddEntity"/>、
    /// <see cref="RemoveEntity"/>）是 internal，只能由 <see cref="Archetype"/> 经 <c>ref</c> 调用；
    /// 位图与组件数据都在非托管内存里，副本指向同一块内存，读写都生效。
    /// </remarks>
    public unsafe struct Chunk
    {
        private const int EntityArrayOffset = 0;

        public readonly ChunkMetadata Meta;
        public readonly nint MemoryBlock;

        private int _entityCount;
        // 用过的最大槽位数 + 1。低于它的槽位可能残留旧实体的数据，追加时要再清一次。
        private int _highWater;

        public Archetype Archetype => Meta.Archetype;
        public readonly int EntityCount => _entityCount;
        public int Capacity => Meta.EntityCapacity;
        public int TotalSize => Meta.TotalSize;
        public int ComponentCount => Meta.ComponentCount;
        public bool HasSharedValues => Meta.SharedValuesOffset != -1;

        /// <summary>在 Archetype 的 slab 内存上构造块：清零位图与非托管数据区，使新块呈空状态。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Chunk(ChunkMetadata meta, nint memoryBlock)
        {
            Meta = meta;
            MemoryBlock = memoryBlock;
            _entityCount = 0;
            _highWater = 0;
            ClearBitMaps(meta, memoryBlock);
            ClearComponentData(meta, memoryBlock);
            ClearSharedValues(meta, memoryBlock);
        }

        // slab 内存来自池、跨 World 复用，位图必须清零：残留位会让启用状态与 WithChanged 过滤出假阳性。
        private static void ClearBitMaps(ChunkMetadata meta, nint memoryBlock)
        {
            for (int i = 0; i < meta.ComponentCount; i++)
            {
                if (meta.EnableBitOffsets[i] != -1 && meta.EnableStrideBytes[i] > 0)
                    Unsafe.InitBlock((byte*)memoryBlock + meta.EnableBitOffsets[i], 0, (uint)meta.EnableStrideBytes[i]);
            }
            if (meta.ChangedBitMaskOffset != -1 && meta.ChangedBitMaskSize > 0)
                Unsafe.InitBlock((byte*)memoryBlock + meta.ChangedBitMaskOffset, 0, (uint)meta.ChangedBitMaskSize);
        }

        // 未使用槽位不被读取，整块清一次即可；追加时只补清复用过的槽位。
        private static void ClearComponentData(ChunkMetadata meta, nint memoryBlock)
        {
            for (int i = 0; i < meta.ComponentCount; i++)
            {
                if (meta.ComponentSizes[i] > 0)
                    Unsafe.InitBlock((byte*)memoryBlock + meta.ComponentOffsets[i], 0,
                        (uint)(meta.EntityCapacity * meta.ComponentSizes[i]));
            }
        }

        // shared 区残留旧值会让按值分组查询把不同组的实体混进同一块。
        private static void ClearSharedValues(ChunkMetadata meta, nint memoryBlock)
        {
            if (meta.SharedValuesOffset != -1)
                Unsafe.InitBlock((byte*)memoryBlock + meta.SharedValuesOffset, 0,
                    (uint)(meta.TotalSize - meta.SharedValuesOffset));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void AddEntity(Entity entity)
        {
            if (_entityCount >= Meta.EntityCapacity)
                throw new InvalidOperationException("Chunk is full");

            int slot = _entityCount;
            ((Entity*)((byte*)MemoryBlock + EntityArrayOffset))[slot] = entity;
            InitializeSlot(slot);
            _entityCount = slot + 1;
        }

        // 首次使用的槽位由构造时的整块清零保证为零；复用过的槽位要逐列再清。
        // 关系列的空槽必须是 -1（全零代表真实实体 0/v0），无论是否复用都要写。
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void InitializeSlot(int slot)
        {
            bool reused = slot < _highWater;
            if (!reused) _highWater = slot + 1;

            bool relations = Meta.Archetype.HasRelationships;
            if (reused || relations)
            {
                for (int i = 0; i < Meta.ComponentCount; i++)
                {
                    int size = Meta.ComponentSizes[i];
                    byte* column = (byte*)MemoryBlock + Meta.ComponentOffsets[i] + slot * size;
                    if (reused) Unsafe.InitBlock(column, 0, (uint)size);
                    if (relations) InitializeRelationSlots(i, column);
                }
            }

            if (!Meta.HasEnableables) return;
            for (int i = 0; i < Meta.ComponentCount; i++)
            {
                if (Meta.EnableBitOffsets[i] != -1)
                    *EnableWordAt(i, slot) |= 1UL << (slot & 63);
            }
        }

        private void InitializeRelationSlots(int componentIndex, byte* column)
        {
            var type = Meta.Archetype.Types[componentIndex];
            if (!type.IsRelation) return;
            int slotCount = Math.Max(1, type.MultiRelationMaxSlots);
            for (int s = 0; s < slotCount; s++)
            {
                // RelationSlot 是 8 字节：TargetId 与 TargetVersion，空槽两个都写 -1。
                ((int*)column)[s * 2] = -1;
                ((int*)column)[s * 2 + 1] = -1;
            }
        }

        /// <summary>
        /// 移除指定槽位：析构其组件、把末位实体搬进空洞。调用者负责后续的位置索引修正。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RemoveEntity(int index, bool continueOnDisposeError, bool skipDisposal = false)
        {
            if (index < 0 || index >= _entityCount)
                throw new IndexOutOfRangeException();

            int last = _entityCount - 1;
            if (!skipDisposal) DisposeSlotComponents(index, continueOnDisposeError);

            if (last > index)
                MoveSlot(last, index);
            else
                ClearEnableBits(index);

            _entityCount = last;
        }

        private void DisposeSlotComponents(int slot, bool continueOnDisposeError)
        {
            if (!Meta.Archetype.HasDisposableComponents) return;
            var types = Meta.Archetype.Types;
            for (int i = 0; i < Meta.ComponentCount; i++)
            {
                byte* value = (byte*)MemoryBlock + Meta.ComponentOffsets[i] + slot * Meta.ComponentSizes[i];
                try
                {
                    ComponentTypeManager.DestroyComponentValue(types[i], value);
                }
                catch (Exception exception) when (continueOnDisposeError)
                {
                    Console.Error.WriteLine($"[ECS] Dispose failed for '{types[i].Type}': {exception}");
                }
            }
        }

        // 纯位拷贝：持有原生内存的组件转移所有权即可，不做析构。
        private void MoveSlot(int from, int to)
        {
            Entity* entities = (Entity*)((byte*)MemoryBlock + EntityArrayOffset);
            entities[to] = entities[from];

            for (int i = 0; i < Meta.ComponentCount; i++)
            {
                int size = Meta.ComponentSizes[i];
                byte* src = (byte*)MemoryBlock + Meta.ComponentOffsets[i] + from * size;
                byte* dst = (byte*)MemoryBlock + Meta.ComponentOffsets[i] + to * size;
                Unsafe.CopyBlock(dst, src, (uint)size);
            }

            if (!Meta.HasEnableables) return;
            for (int i = 0; i < Meta.ComponentCount; i++)
            {
                if (Meta.EnableBitOffsets[i] == -1) continue;
                bool enabled = (*EnableWordAt(i, from) & (1UL << (from & 63))) != 0;
                SetEnableBit(i, to, enabled);
                SetEnableBit(i, from, false);
            }
        }

        private void ClearEnableBits(int slot)
        {
            if (!Meta.HasEnableables) return;
            for (int i = 0; i < Meta.ComponentCount; i++)
            {
                if (Meta.EnableBitOffsets[i] != -1) SetEnableBit(i, slot, false);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T GetComponent<T>(int entityIndex, int componentIndex) where T : struct
        {
            ref var value = ref GetComponentStorage<T>(entityIndex, componentIndex);
            if (Meta.Archetype.Types[componentIndex].IsRelation)
                throw new InvalidOperationException(
                    "Relationship columns require GetComponentReadOnly; change values through EntityManager or DeferredCommandBuffer.");
            return ref value;
        }

        public ref readonly T GetComponentReadOnly<T>(int entityIndex, int componentIndex) where T : struct
            => ref GetComponentStorage<T>(entityIndex, componentIndex);

        internal ref T GetComponentStorage<T>(int entityIndex, int componentIndex) where T : struct
        {
            Meta.Archetype.CheckReadable();
            if ((uint)entityIndex >= (uint)_entityCount)
                throw new IndexOutOfRangeException($"Entity index {entityIndex} out of range (count={_entityCount}).");
            if ((uint)componentIndex >= (uint)Meta.ComponentCount)
                throw new IndexOutOfRangeException($"Component index {componentIndex} out of range (count={Meta.ComponentCount}).");
            return ref Unsafe.AsRef<T>(
                (byte*)MemoryBlock + Meta.ComponentOffsets[componentIndex] + entityIndex * Meta.ComponentSizes[componentIndex]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref Entity GetEntity(int entityIndex)
            => ref ((Entity*)((byte*)MemoryBlock + EntityArrayOffset))[entityIndex];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public nint GetEntityPointer() => MemoryBlock + EntityArrayOffset;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly nint GetComponentArrayPointer(int componentIndex)
            => MemoryBlock + Meta.ComponentOffsets[componentIndex];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetComponentOffset(int componentIndex) => Meta.ComponentOffsets[componentIndex];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong* GetEnableBitMapPointer(int componentIndex)
            => Meta.EnableBitOffsets[componentIndex] == -1
                ? null
                : (ulong*)((byte*)MemoryBlock + Meta.EnableBitOffsets[componentIndex]);

        // 不经实体数校验的位图字指针：追加时槽位下标等于当前实体数，两种校验在此都不成立。
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ulong* EnableWordAt(int componentIndex, int slot)
            => (ulong*)((byte*)MemoryBlock + Meta.EnableBitOffsets[componentIndex]) + (slot >> 6);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetEnableBit(int componentIndex, int slot, bool enabled)
        {
            ulong* word = EnableWordAt(componentIndex, slot);
            ulong mask = 1UL << (slot & 63);
            if (enabled) *word |= mask;
            else *word &= ~mask;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool GetComponentEnabled(int componentIndex, int entityIndex)
        {
            CheckEnableableEntity(componentIndex, entityIndex, out ulong* word, out ulong mask);
            return (*word & mask) != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetComponentEnabled(int componentIndex, int entityIndex, bool enabled)
        {
            CheckEnableableEntity(componentIndex, entityIndex, out ulong* word, out ulong mask);
            if (enabled) *word |= mask;
            else *word &= ~mask;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CheckEnableableEntity(int componentIndex, int entityIndex, out ulong* word, out ulong mask)
        {
            if ((uint)entityIndex >= (uint)_entityCount)
                throw new IndexOutOfRangeException($"Entity index {entityIndex} out of range (count={_entityCount}).");
            if (GetEnableBitMapPointer(componentIndex) == null)
                throw new InvalidOperationException("Component is not enableable.");
            word = EnableWordAt(componentIndex, entityIndex);
            mask = 1UL << (entityIndex & 63);
        }

        // shared 区：blittable 类型内联存值，managed 类型只存哈希桶索引。同块内所有实体共享同一组值。

        /// <summary>读取 managed shared 索引（指向 EntityManager 的哈希桶值数组）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetSharedValueIndex(int componentIndex)
            => *(int*)((byte*)MemoryBlock + RequireSharedOffset(componentIndex));

        /// <summary>写入 managed shared 索引（指向 EntityManager 的哈希桶值数组）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetSharedValueIndex(int componentIndex, int index)
            => *(int*)((byte*)MemoryBlock + RequireSharedOffset(componentIndex)) = index;

        /// <summary>shared 组件在块内的槽位指针（NativeTranspiler ABI：blittable 值或 int 索引）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public nint GetSharedValuePointer(int componentIndex)
        {
            int offset = Meta.SharedValueOffsets[componentIndex];
            return offset == -1 ? nint.Zero : MemoryBlock + offset;
        }

        private int RequireSharedOffset(int componentIndex)
        {
            int offset = Meta.SharedValueOffsets[componentIndex];
            if (offset == -1)
                throw new InvalidOperationException($"Component index {componentIndex} is not a shared component.");
            return offset;
        }

        // 变更追踪：每实体一位，标记本帧被写过的实体，供 WithChanged 过滤。

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ulong* ChangedBitMask()
            => Meta.ChangedBitMaskOffset == -1 ? null : (ulong*)((byte*)MemoryBlock + Meta.ChangedBitMaskOffset);

        /// <summary>检查指定实体是否在本帧被修改过。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsEntityChanged(int entityIndex)
        {
            ulong* bitMap = ChangedBitMask();
            if (bitMap == null) return false;
            return (bitMap[entityIndex >> 6] & (1UL << (entityIndex & 63))) != 0;
        }

        /// <summary>标记指定实体已修改。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MarkEntityChanged(int entityIndex)
        {
            ulong* bitMap = ChangedBitMask();
            if (bitMap == null) return;
            bitMap[entityIndex >> 6] |= 1UL << (entityIndex & 63);
        }

        /// <summary>清除本块所有变更标记（帧末调用）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClearChangedBitMask()
        {
            ulong* bitMap = ChangedBitMask();
            if (bitMap == null) return;
            for (int i = 0; i < (Meta.EntityCapacity + 63) / 64; i++) bitMap[i] = 0;
        }

        /// <summary>本块是否有任何实体被修改过。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HasAnyEntityChanged()
        {
            ulong* bitMap = ChangedBitMask();
            if (bitMap == null) return false;
            for (int i = 0; i < (_entityCount + 63) / 64; i++)
            {
                if (bitMap[i] != 0) return true;
            }
            return false;
        }
    }
}
