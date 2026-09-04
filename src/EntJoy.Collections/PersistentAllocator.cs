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
    /// 目标：同尺寸 dispose→realloc 拿回同一块内存 → 地址/物理页稳定 → 消除冷路径方差。
    /// 线程安全（ConcurrentStack），可被 worker 线程 job 内分配/释放。
    /// 块布局（HeaderSize=16，payload 16 字节对齐，与 AllocHGlobal 在 x64 的 16 对齐一致）：
    ///   [0..3]   int classIndex   (0..30 可池化；-1 = 直通 OS；每次分配必写)
    ///   [4..7]   int payloadSize   (payload 字节数)
    ///   [8..15]  pad
    ///   [16..]   payload          返回给调用者
    ///
    /// 外来块安全护栏：存活表 s_live 记录所有本分配器发出的块基址。Free 时先查表——
    ///   命中   → 本分配器块，按 header 回收/释放；
    ///   未命中 → 外来块（原生 UnsafeList 用 CRT malloc 扩容后交给 C# 释放的块，无 header），
    ///            直接按原始指针 FreeHGlobal（LocalAlloc 基址→正确释放；CRT 块→静默泄漏，与改动前行为一致）。
    /// 这杜绝了"对内部指针减 HeaderSize 再释放"导致的堆损坏（STATUS_HEAP_CORRUPTION 0xc0000374）。
    /// </summary>
    public static unsafe class PersistentAllocator
    {
        private const int HeaderSize = 16;
        private const int MaxClassIndex = 30;      // 2^30 = 1GB；超过直通 OS
        private const int MaxPerClass = 64;        // 每类保留块数上限，防内存无限增长

        private static readonly ConcurrentStack<IntPtr>[] _classes = new ConcurrentStack<IntPtr>[MaxClassIndex + 1];
        private static readonly ConcurrentDictionary<IntPtr, byte> s_live = new ConcurrentDictionary<IntPtr, byte>();

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
                IntPtr basePtr = Marshal.AllocHGlobal(size + HeaderSize);
                WriteHeader(basePtr, -1, size);
                s_live.TryAdd(basePtr, 0);
                Interlocked.Increment(ref s_allocs);
                Interlocked.Increment(ref s_misses);
                return (void*)(basePtr + HeaderSize);
            }

            var cls = _classes[idx];
            if (cls != null && cls.TryPop(out IntPtr ptr))
            {
                WriteHeader(ptr, idx, 1 << idx);
                s_live.TryAdd(ptr, 0);
                Interlocked.Increment(ref s_allocs);
                Interlocked.Increment(ref s_hits);
                return (void*)(ptr + HeaderSize);
            }

            IntPtr newPtr = Marshal.AllocHGlobal((1 << idx) + HeaderSize);
            WriteHeader(newPtr, idx, 1 << idx);
            s_live.TryAdd(newPtr, 0);
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
            if (!s_live.TryRemove(basePtr, out _))
            {
                // 未登记指针不属于本分配器，不能猜测其 allocator 并释放。
                Interlocked.Increment(ref s_foreign);
                return;
            }

            int idx = *(int*)basePtr;
            int payloadSize = *(int*)(basePtr + 4);

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteHeader(IntPtr basePtr, int idx, int payloadSize)
        {
            *(int*)basePtr = idx;
            *(int*)(basePtr + 4) = payloadSize;
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
