using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EntJoy.ECS
{
    // Archetype Chunk 管理
    public unsafe sealed partial class Archetype : IDisposable
    {
        public void AddEntity(Entity entity, out int chunkIndex, out int slotInChunk)
        {
            var span = CollectionsMarshal.AsSpan(_chunkList);
            // Shared 的无指定值入口沿用末块分组；指定值入口由值匹配器选择。
            int candidate = HasSharedColumns ? Math.Max(0, span.Length - 1) : _firstChunkWithRoom;
            for (; candidate < span.Length; candidate++)
            {
                ref var chunk = ref span[candidate];
                if (chunk.EntityCount < chunk.Capacity)
                {
                    slotInChunk = chunk.EntityCount;
                    chunk.AddEntity(entity);
                    chunkIndex = candidate;
                    if (!HasSharedColumns) _firstChunkWithRoom = candidate;
                    EntityCount++;
                    return;
                }
            }
            if (!HasSharedColumns) _firstChunkWithRoom = span.Length;

            // 需要新建 chunk
            nint chunkMem = AllocateFromSlab();
            var newChunk = new Chunk(_sharedMetadata, chunkMem);
            newChunk.AddEntity(entity);
            _chunkList.Add(newChunk);
            slotInChunk = newChunk.EntityCount - 1;
            chunkIndex = _chunkList.Count - 1;
            EntityCount++;
        }

        // 压缩会直接移动实体和块；重新扫描，使游标覆盖交换后的空位。
        internal void ResetChunkRoomSearch() => _firstChunkWithRoom = 0;

        // Shared values 支持：managed shared 值在 World 生命周期内不释放，由 World.Dispose 统一清空。

        /// <summary>创建新 chunk（不添加实体），返回其索引。Shared values 区由调用方写入。</summary>
        public int CreateChunk()
        {
            nint chunkMem = AllocateFromSlab();
            var newChunk = new Chunk(_sharedMetadata, chunkMem);
            _chunkList.Add(newChunk);
            return _chunkList.Count - 1;
        }

        /// <summary>将实体添加到指定 chunk（SharedComponent 路径：目标 chunk 已按 shared 值选定）。</summary>
        public void AddEntityToChunk(Entity entity, int chunkIndex)
        {
            if ((uint)chunkIndex >= (uint)_chunkList.Count)
                throw new IndexOutOfRangeException($"Chunk index {chunkIndex} out of range (count={_chunkList.Count}).");
            ref var chunk = ref CollectionsMarshal.AsSpan(_chunkList)[chunkIndex];
            if (chunk.EntityCount >= chunk.Capacity)
                throw new InvalidOperationException("Chunk is full");
            chunk.AddEntity(entity);
            EntityCount++;
        }

        private int ComputeChunkStride()
        {
            // stride 覆盖完整布局并按缓存行对齐，使 slab 中每个 Chunk 的底地址保持对齐。
            int alignment = _cacheLayout.LineSize;
            // 每块多留一个缓存行：相邻 Chunk 的组号轮转，避免所有块落进同一组。
            int aligned = (_sharedMetadata.TotalSize + alignment - 1) & ~(alignment - 1);
            return aligned + alignment;
        }

        private nint AllocateFromSlab()
        {
            // 块号只有 16 位：越界后块号会与块内槽位互相污染。
            if (_chunkList.Count >= EntityIndexInWorld.PackedChunkLimit)
                throw new InvalidOperationException(
                    $"原型块数达到位置表块号上限 {EntityIndexInWorld.PackedChunkLimit}；先 CompactChunks 或按组件拆原型。");
            if (_freeChunks.Count > 0) return ReuseFreeChunk();
            if (_chunkStride == 0) _chunkStride = ComputeChunkStride();

            int slabBytes = ResolveSlabBytes();
            EnsureCurrentSlab(slabBytes);

            nint chunkMem = _currentSlab.AlignedPtr + _currentSlabOffset;
            _currentSlabOffset += _chunkStride;
            _currentSlab.ChunkCount++;
            return chunkMem;
        }

        // 复用已移除 Chunk 的空洞，避免 slab 无限增长。复用即视为该块重新存活，必须把它的
        // 「已释放」还回去，否则 slab 账本会提前认定整片已释放。
        private nint ReuseFreeChunk()
        {
            int last = _freeChunks.Count - 1;
            nint reused = _freeChunks[last];
            _freeChunks.RemoveAt(last);
            // 能进 _freeChunks 的空洞必属存活 slab：归还 slab 时已清空其空洞。
            var slab = FindSlab(reused.ToInt64());
            if (slab != null && slab.ReleasedCount > 0) slab.ReleasedCount--;
            return reused;
        }

        // 每个 slab 的可用字节数：常规 64 KiB；stride 超过 64 KiB（超大容量 Chunk）时按 stride 给，
        // 使该块独占一个 slab。
        private int ResolveSlabBytes()
        {
            int slabBytes = _chunkStride > SLAB_SIZE ? _chunkStride : SLAB_SIZE;
            // 不变式：slab 必须容得下一个 chunk。「放不下就换新 slab」只解决剩余空间不足，
            // 而 stride 超过 slab 时换多少片都放不下，所以在这里直接失败。
            if (slabBytes < _chunkStride)
                throw new InvalidOperationException(
                    $"Chunk stride {_chunkStride} 超过 slab {slabBytes}：容量/组件布局把 chunk 撑过了 slab。"
                    + "请调小 ChunkCapacityOverride 或增大 slab。");
            return slabBytes;
        }

        // ChunkMemoryPool.Allocate 返回 申请字节 + kOverAlloc（64 KiB），下面的对齐保证满足 SLAB_ALIGNMENT。
        private void EnsureCurrentSlab(int slabBytes)
        {
            if (_currentSlab != null && _currentSlabOffset + _chunkStride <= _currentSlab.Bytes) return;
            nint raw = ChunkMemoryPool.Allocate(slabBytes);
            long aligned = (raw.ToInt64() + SLAB_ALIGNMENT - 1) & ~(SLAB_ALIGNMENT - 1);
            _currentSlab = new SlabInfo { RawPtr = raw, AlignedPtr = new nint(aligned), Bytes = slabBytes };
            _slabs.Add(_currentSlab);
            _currentSlabOffset = 0;
        }

        /// <summary>
        /// 释放 Chunk 内存（复用或压缩）。Chunk 从列表移除时调用。
        /// 空洞进入复用列表；当某个 slab 的所有 Chunk 都释放时，整个 slab 归还（压缩回收）。
        /// </summary>
        internal void ReleaseChunkMemory(nint chunkMem)
        {
            SlabInfo slab = FindSlab(chunkMem.ToInt64());
            if (slab == null)
                return;

            slab.ReleasedCount++;
            if (slab.ReleasedCount == slab.ChunkCount)
            {
                ChunkMemoryPool.Free(slab.RawPtr, slab.Bytes);
                _slabs.Remove(slab);
                // _currentSlab 不能再指向已归还的内存，否则后续切片会 use-after-free。
                if (_currentSlab == slab) { _currentSlab = null; _currentSlabOffset = 0; }
                // 该 slab 的空洞随整片归还，不能再进复用列表。
                long start = slab.AlignedPtr.ToInt64();
                long end = start + slab.Bytes;
                for (int i = _freeChunks.Count - 1; i >= 0; i--)
                {
                    long address = _freeChunks[i].ToInt64();
                    if (address >= start && address < end) _freeChunks.RemoveAt(i);
                }
            }
            else
            {
                _freeChunks.Add(chunkMem);
            }
        }

        /// <summary>实体移除结果：swap-pop 搬移的实体与空 chunk 压缩信息（-1 = 无）。</summary>
        public readonly struct RemovalResult
        {
            public readonly int MovedEntityId;
            public readonly int MovedEntitySlot;

            public RemovalResult(int movedEntityId, int movedEntitySlot)
            {
                MovedEntityId = movedEntityId;
                MovedEntitySlot = movedEntitySlot;
            }
        }
    }
}
