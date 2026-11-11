using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace EntJoy.Collections
{
    /// <summary>
    /// Allocator.Persistent 的 free-list 分配器（参考 Unity AllocatorManager / ContextPool 范式）。
    /// 线程安全（ConcurrentStack），可被 worker 线程 job 内分配/释放。
    /// 存活表按返回指针记录分桶索引；未知指针不会被释放。
    /// </summary>
    public static unsafe class PersistentAllocator
    {
        // payload 始终从块基址偏移 HeaderSize 开始：这段前缀保留给分配器内部使用，
        // 记账改由 s_live 承担，因此不得缩小偏移——缩小会让既有调用方的 payload 落点整体前移。
        private const int HeaderSize = 16;
        private const int MaxClassIndex = 30;      // 2^30 = 1GB；超过直通 OS
        private const int MaxPerClass = 64;        // 每类保留块数上限，防内存无限增长

        private static readonly ConcurrentStack<IntPtr>[] _classes = new ConcurrentStack<IntPtr>[MaxClassIndex + 1];
        private static readonly ConcurrentDictionary<IntPtr, int> s_live = new ConcurrentDictionary<IntPtr, int>();

        // 统计（Interlocked）
        private static int s_allocs;
        private static int s_frees;
        private static int s_hits;
        private static int s_misses;
        private static int s_toOS;
        private static int s_foreign;

        public static void* Alloc(int size)
        {
            if (size <= 0) size = 1;

            int idx = SizeToClass(size);
            if (idx < 0 || idx > MaxClassIndex)
            {
                // 超大块直通 OS
                IntPtr basePtr = Marshal.AllocHGlobal((nint)size + HeaderSize);
                s_live.TryAdd(basePtr + HeaderSize, -1);
                Interlocked.Increment(ref s_allocs);
                Interlocked.Increment(ref s_misses);
                return (void*)(basePtr + HeaderSize);
            }

            var cls = _classes[idx];
            if (cls != null && cls.TryPop(out IntPtr ptr))
            {
                s_live.TryAdd(ptr + HeaderSize, idx);
                Interlocked.Increment(ref s_allocs);
                Interlocked.Increment(ref s_hits);
                return (void*)(ptr + HeaderSize);
            }

            IntPtr newPtr = Marshal.AllocHGlobal((1 << idx) + HeaderSize);
            s_live.TryAdd(newPtr + HeaderSize, idx);
            Interlocked.Increment(ref s_allocs);
            Interlocked.Increment(ref s_misses);
            return (void*)(newPtr + HeaderSize);
        }

        public static void Free(void* payload)
        {
            if (payload == null) return;
            IntPtr basePtr = (IntPtr)((byte*)payload - HeaderSize);
            Interlocked.Increment(ref s_frees);

            // 存活表：区分本分配器块与外来块，杜绝内部指针释放
            if (!s_live.TryRemove((IntPtr)payload, out int idx))
            {
                // 未登记指针不属于本分配器，不能猜测其 allocator 并释放。
                Interlocked.Increment(ref s_foreign);
                return;
            }

            if (idx < 0 || idx > MaxClassIndex)
            {
                Marshal.FreeHGlobal(basePtr);
                Interlocked.Increment(ref s_toOS);
                return;
            }

            var cls = GetOrCreateClass(idx);
            if (cls.Count < MaxPerClass)
            {
                cls.Push(basePtr);
            }
            else
            {
                Marshal.FreeHGlobal(basePtr);
                Interlocked.Increment(ref s_toOS);
            }
        }

        /// <summary>ceil(log2(size)) → 2 的幂 size-class 索引。</summary>
        private static int SizeToClass(int size)
        {
            if (size <= 1) return 0;
            return (int)BitOperations.Log2((uint)(size - 1)) + 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ConcurrentStack<IntPtr> GetOrCreateClass(int idx)
        {
            var cls = _classes[idx];
            if (cls != null) return cls;
            var fresh = new ConcurrentStack<IntPtr>();
            return Interlocked.CompareExchange(ref _classes[idx], fresh, null) ?? fresh;
        }

        public struct Stats
        {
            public int Allocs;
            public int Frees;
            public int Hits;
            public int Misses;
            public int ToOS;
            public int Foreign;
        }

        public static Stats GetStats()
        {
            return new Stats
            {
                Allocs = Volatile.Read(ref s_allocs),
                Frees = Volatile.Read(ref s_frees),
                Hits = Volatile.Read(ref s_hits),
                Misses = Volatile.Read(ref s_misses),
                ToOS = Volatile.Read(ref s_toOS),
                Foreign = Volatile.Read(ref s_foreign),
            };
        }
    }
}
