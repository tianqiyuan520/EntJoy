using EntJoy.Debugger;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EntJoy.ECS
{
    public unsafe sealed partial class Archetype : IDisposable
    {
        /// <summary>检查当前 Archetype 是否包含指定组件。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HasComponent(ComponentType componentType)
        {
            return TryGetComponentTypeIndex(componentType, out _);
        }

        /// <summary>清除所有 Chunk 的实体变更标记（帧末调用）。</summary>
        public void ClearAllChangedBitMasks()
        {
            foreach (var chunk in _chunkList)
            {
                chunk.ClearChangedBitMask();
            }
        }

        /// <summary>
        /// 清空全部实体与 Chunk（配合 <c>EntityManager.DestroyAllInArchetype</c>）：不做逐实体移除，
        /// 只析构组件值后整批释放 slab，代价 O(Chunk 数)。
        /// 含关系列的 Archetype 不能用本方法，反向索引需要逐实体清理。
        /// </summary>
        public void ClearAllEntities()
        {
            if (_chunkList.Count == 0)
            {
                EntityCount = 0;
                return;
            }
            // 组件析构必须早于 slab 释放：析构钩子会读 chunk 内存。
            DestroyAllEntityComponents(null);
            foreach (var slab in _slabs)
            {
                try { ChunkMemoryPool.Free(slab.RawPtr, slab.Bytes); }
                catch { /* ClearAll 语义下不因单个 slab 释放失败中断 */ }
            }
            _slabs.Clear();
            _freeChunks.Clear();
            _chunkList.Clear();
            ResetChunkRoomSearch();
            _currentSlab = null;
            _currentSlabOffset = 0;
            EntityCount = 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var errors = new List<Exception>();
            DestroyAllEntityComponents(errors);
            foreach (var slab in _slabs)
            {
                try { ChunkMemoryPool.Free(slab.RawPtr, slab.Bytes); }
                catch (Exception ex) { (errors ??= new List<Exception>()).Add(ex); }
            }
            _slabs.Clear();
            _freeChunks.Clear();
            _chunkList.Clear();
            ResetChunkRoomSearch();
            if (_chunkOffsetsNative != null)
            {
                NativeLocateStorage.Free(_chunkOffsetsNative);
                _chunkOffsetsNative = null;
            }
            GC.SuppressFinalize(this);
            if (errors.Count > 0) throw new AggregateException("Archetype disposal failed.", errors);
        }

        /// <summary>销毁所有存活实体的生命周期组件（Dispose 前调用，无 hook 组件时 no-op）。</summary>
        private unsafe void DestroyAllEntityComponents(List<Exception> errors = null)
        {
            var types = Types;
            foreach (var chunk in _chunkList)
            {
                int count = chunk.EntityCount;
                for (int slot = 0; slot < count; slot++)
                {
                    for (int i = 0; i < ComponentCount; i++)
                    {
                        byte* ptr = (byte*)chunk.GetComponentArrayPointer(i) + slot * types[i].Size;
                        try { ComponentTypeManager.DestroyComponentValue(types[i], ptr); }
                        catch (Exception ex) { (errors ??= new List<Exception>()).Add(ex); }
                    }
                }
            }
        }

        ~Archetype()
        {
            try { Dispose(); }
            catch { }
        }

        private IntPtr _cachedAddress;

        public IntPtr GetAddress()
        {
            if (_cachedAddress == IntPtr.Zero)
            {
                _cachedAddress = MemoryAddress.GetAddress(this);
            }
            return _cachedAddress;
        }
    }
}
