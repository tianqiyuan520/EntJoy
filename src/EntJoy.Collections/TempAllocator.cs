using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace EntJoy.Collections
{
    /// <summary>
    /// 临时内存分配器：帧内 Temp 内存。大块（≥kPoolThreshold）走 free-list 分桶池，
    /// 帧末 Reset 归还池而非直通 OS——避免每帧 new + 首次触摸缺页（GridSearch Query
    /// 的 100k int results 每帧 AllocHGlobal 400KB + 首触 100 页 = Query tail 根因）。
    /// 小块直通（碎块无收益）。对齐 PersistentAllocator 的 free-list 范式。
    /// </summary>
    public static class TempAllocator
    {
        // 池化阈值：≥64KB 才进池（覆盖 NativeArray 大缓冲）；MaxPerClass 防无限增长。
        private const int kPoolThreshold = 64 * 1024;
        private const int HeaderSize = 8;
        private const int MaxClassIndex = 19;      // 2^19 = 512KB 上限；更大直通 OS
        private const int MaxPerClass = 32;

        private const int kOsMarker = MaxClassIndex + 1; // 无符号池块标记（直通 OS）

        private static readonly ConcurrentStack<IntPtr>[] _classes = new ConcurrentStack<IntPtr>[MaxClassIndex + 2];
        private static int s_allocs, s_frees, s_hits, s_misses, s_toOS;

        // 使用字典记录所有活跃的 Temp 内存指针 -> 对应的安全句柄索引
        private static readonly ConcurrentDictionary<IntPtr, int> _active = new ConcurrentDictionary<IntPtr, int>();

        // Reset 期间阻止并发分配，防止快照遗漏 + use-after-free
        private static readonly object _resetLock = new();

        /// <summary>
        /// 由 ECS 层在 World 初始化时注册：在释放 Temp 内存前完成所有活跃 Job。
        /// 签名：无参数、可抛异常（异常会被 Reset 捕获并在内存释放后重新抛出）。
        /// </summary>
        public static Action? OnBeforeReset;

        /// <summary>
        /// 由 ECS 层在 World 初始化时注册：在 Temp 内存释放完成后刷新调度器异常记录。
        /// </summary>
        public static Action? OnAfterReset;

        private static int SizeToClass(int size)
        {
            if (size <= 1) return 0;
            return (int)(System.Numerics.BitOperations.Log2((uint)(size - 1)) + 1);
        }

        private static ConcurrentStack<IntPtr> GetOrCreateClass(int idx)
        {
            var cls = _classes[idx];
            if (cls != null) return cls;
            var fresh = new ConcurrentStack<IntPtr>();
            return Interlocked.CompareExchange(ref _classes[idx], fresh, null) ?? fresh;
        }

        /// <summary>分配临时内存，并关联安全句柄索引。</summary>
        public static IntPtr Alloc(int size, int safetyHandleIndex)
        {
            if (size <= 0) size = 1;
            IntPtr payload;
            int idx;
            IntPtr basePtr;
            if (size >= kPoolThreshold && size <= (1 << MaxClassIndex))
            {
                idx = SizeToClass(size);
                var cls = _classes[idx];
                if (cls != null && cls.TryPop(out IntPtr ptr))
                {
                    WriteHeader(ptr, idx);
                    Interlocked.Increment(ref s_allocs);
                    Interlocked.Increment(ref s_hits);
                    payload = ptr + HeaderSize;
                }
                else
                {
                    Interlocked.Increment(ref s_allocs);
                    Interlocked.Increment(ref s_misses);
                    // 直通块也带 header（Free 时按 header 判池归属）
                    basePtr = Marshal.AllocHGlobal(size + HeaderSize);
                    WriteHeader(basePtr, idx);
                    payload = basePtr + HeaderSize;
                }
            }
            else
            {
                // 小块直通：仍带 header（kOsMarker = 直接 OS 释放）
                Interlocked.Increment(ref s_allocs);
                Interlocked.Increment(ref s_toOS);
                idx = kOsMarker;
                basePtr = Marshal.AllocHGlobal(size + HeaderSize);
                WriteHeader(basePtr, idx);
                payload = basePtr + HeaderSize;
            }
            lock (_resetLock) _active.TryAdd(payload, safetyHandleIndex);
            return payload;
        }

        /// <summary>释放临时内存（用户手动调用 Dispose 时调用），同时移除映射。</summary>
        public static void Free(IntPtr ptr)
        {
            lock (_resetLock)
            {
                if (_active.TryRemove(ptr, out _))
                    FreeImpl(ptr);
            }
        }

        private static void FreeImpl(IntPtr ptr)
        {
            Interlocked.Increment(ref s_frees);
            if (ptr == IntPtr.Zero) return;
            // 所有块都带 header（class idx 或 kOsMarker=直通 OS）
            IntPtr basePtr = ptr - HeaderSize;
            int idx = ReadHeader(basePtr);
            if (idx < 0 || idx > MaxClassIndex)
            {
                // OS 直通块（含旧的无 header 块：idx 读成超界垃圾 → 按 OS 释放）
                Marshal.FreeHGlobal(basePtr);
                return;
            }
            var cls = GetOrCreateClass(idx);
            if (cls.Count < MaxPerClass)
                cls.Push(basePtr);
            else
            {
                Marshal.FreeHGlobal(basePtr);
                Interlocked.Increment(ref s_toOS);
            }
        }

        private static unsafe void WriteHeader(IntPtr basePtr, int idx)
        {
            *(int*)basePtr = idx;
        }

        private static unsafe int ReadHeader(IntPtr basePtr)
        {
            return *(int*)basePtr;
        }

        /// <summary>在帧末调用，释放所有未被手动释放的 Temp 内存，并标记对应的安全句柄为已释放。</summary>
        public static void Reset()
        {
            lock (_resetLock)
            {
                // ① 先完成所有活跃异步 Job，确保没有 C++ Worker 线程还在读写 Temp 内存。
                //    若 job 抛异常也须继续——下面的内存释放不能跳过。
                Exception? pending = null;
                try
                {
                    OnBeforeReset?.Invoke();
                }
                catch (Exception ex)
                {
                    pending = ex;
                }

                // ② 释放内存（锁内执行，无并发干扰，无需快照）。
                //    即使 job 异常也必须执行，否则 Temp 内存 + 安全句柄泄漏，
                //    且活跃 job 跨帧运行会让主线程读未完成输出（数据竞态）。
                foreach (var kvp in _active)
                {
                    SafetyHandleManager.MarkReleased(kvp.Value);
                    FreeImpl(kvp.Key);
                }
                _active.Clear();

                // ③ 最后抛异常（job 异常 + 帧内未归属异常）。
                //    FlushRecordedExceptions 由 ECS 层通过 OnAfterReset 注册。
                OnAfterReset?.Invoke();
                if (pending != null) throw pending;
            }
        }

        public static (int allocs, int hits, int misses, int toOS) GetStats()
        {
            return (Volatile.Read(ref s_allocs), Volatile.Read(ref s_hits),
                    Volatile.Read(ref s_misses), Volatile.Read(ref s_toOS));
        }
    }
}