using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace EntJoy
{
    /// <summary>
    /// 64KB 对齐块池化器：为 Archetype 的 chunk 数据块提供池化分配，
    /// 避免每帧 AllocHGlobal/FreeHGlobal 带来的堆碎片与缺页抖动。
    ///
    /// 分配：每个块超分配 <see cref="kOverAlloc"/> 字节以保证 64KB 对齐（与 Archetype SLAB_ALIGNMENT 一致）。
    /// 回池：每个块 free 时进入 per-size class free-list；池满时直通 OS。
    /// 帧末清理：无（块在 Archetype.Dispose 时回池；Prewarm 可预热）。
    ///
    /// 设计对齐 v3 Phase 1.2"ChunkPool：保留对齐的池化"目标。
    /// </summary>
    public static class ChunkMemoryPool
    {
        // 池化大小：64KB（与 Archetype SLAB_SIZE 对齐；所有 chunk stride 均 ≤ 64KB）
        private const int kBlockSize = 64 * 1024;
        // 超分配：+64KB 保证在任意 malloc 返回地址上都能找到 64KB 对齐位置
        private const int kOverAlloc = 64 * 1024;
        // 每 class 池上限（防止池无限增长）
        private const int kMaxPerClass = 64;

        private static readonly object _lock = new();
        private static readonly List<nint> _pool = new(256);
        private static int s_allocs, s_frees, s_hits, s_misses;

        /// <summary>
        /// 分配一个 64KB 对齐的块（≥ <see cref="kBlockSize"/> 可用空间）。
        /// 调用方通过块起始 + offset 访问 chunk 数据（与 Archetype._currentSlab 模式一致）。
        /// </summary>
        public static nint Allocate()
        {
            nint raw;
            lock (_lock)
            {
                if (_pool.Count > 0)
                {
                    raw = _pool[^1];
                    _pool.RemoveAt(_pool.Count - 1);
                    Interlocked.Increment(ref s_hits);
                    Interlocked.Increment(ref s_allocs);
                    return raw;
                }
            }
            // 池空：直通 OS
            Interlocked.Increment(ref s_misses);
            Interlocked.Increment(ref s_allocs);
            return Marshal.AllocHGlobal(kBlockSize + kOverAlloc);
        }

        /// <summary>
        /// 归还块（仅池化 ≤ <see cref="kBlockSize"/> 的标准块）。
        /// 非本池块（超大/未知来源）直通 OS 释放。
        /// </summary>
        public static void Free(nint raw)
        {
            if (raw == nint.Zero) return;
            Interlocked.Increment(ref s_frees);

            // 安全：若已超过池上限，直接 OS 释放
            lock (_lock)
            {
                if (_pool.Count < kMaxPerClass)
                {
                    _pool.Add(raw);
                    return;
                }
            }
            Marshal.FreeHGlobal(raw);
        }

        /// <summary>帧末/场景切换时释放所有池中空闲块（归还 OS）。</summary>
        public static void Trim()
        {
            List<nint> taken;
            lock (_lock)
            {
                taken = _pool;
                _pool.Clear();
            }
            foreach (var ptr in taken)
                Marshal.FreeHGlobal(ptr);
        }

        /// <summary>预热：向池中注入指定数量的空闲块。</summary>
        public static void Prewarm(int count)
        {
            for (int i = 0; i < count; i++)
                Free(Marshal.AllocHGlobal(kBlockSize + kOverAlloc));
        }

        public static (int allocs, int frees, int hits, int misses) GetStats()
        {
            return (Volatile.Read(ref s_allocs), Volatile.Read(ref s_frees),
                    Volatile.Read(ref s_hits), Volatile.Read(ref s_misses));
        }
    }
}
