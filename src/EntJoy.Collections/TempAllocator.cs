using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace EntJoy.Collections
{
    /// <summary>
    /// 临时内存分配器：帧内 Temp 内存。大块（≥kPoolThreshold）走 free-list 分桶池，
    /// 帧末 Reset 归还池而非直通 OS——避免每帧 new + 首次触摸缺页（GridSearch Query
    /// 的 100k int results 每帧 AllocHGlobal 400KB + 首触 100 页 = Query tail 根因）。
    /// 小块直通（碎块无收益）。对齐 PersistentAllocator 的 free-list 范式。
    ///
    /// 分配/释放快路径**不再取全局锁**——
    /// 存活登记从全局 ConcurrentDictionary + `_resetLock`（Alloc/Free/Reset 全抢同一把锁）
    /// 改为 **per-thread pending 列表**（每线程一把私有无争用 gate；帧末 Reset 依次收集）。
    /// 全局 `_resetLock` 仅由 Reset 与跨线程 Free 慢路径获取。
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

        // ---- 每线程存活登记（v3 Phase 1.1a） ----
        // 每个托管线程（主线程 / 原生 worker 的托管入口）一条登记表：
        // gate 只被 owner（分配/释放快路径）与帧末 Reset（慢路径收集）接触，
        // owner 单线程访问 → gate 无争用，分配/释放快路径不触碰任何跨线程共享锁。
        private sealed class ThreadEntry
        {
            public readonly object gate = new();
            public readonly List<Pending> items = new();
        }

        private readonly struct Pending
        {
            public readonly IntPtr Payload;   // 返回给调用者的指针
            public readonly int SafetyHandleIndex;
            public readonly byte Released;    // 1 = 已被用户手动 Free（Reset 跳过）

            public Pending(IntPtr payload, int safetyHandleIndex, byte released)
            {
                Payload = payload;
                SafetyHandleIndex = safetyHandleIndex;
                Released = released;
            }
        }

        [ThreadStatic]
        private static ThreadEntry? tls;
        private static readonly ConcurrentBag<ThreadEntry> s_entries = new();

        // Reset 期间的协调锁：只被 Reset 与跨线程 Free 慢路径获取（分配/释放快路径无锁）。
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

        private static ThreadEntry GetOrCreateThreadEntry()
        {
            var e = tls;
            if (e != null) return e;
            e = new ThreadEntry();
            s_entries.Add(e); // 注册供 Reset 收集；线程退出残留的实例为空表（可忽略）
            tls = e;
            return e;
        }

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
                    // 按 class 上界对齐分配（1 << idx），保证池复用返回的块容量恒 ≥ 该 class 内
                    // 任意请求，避免同 class 更大请求 pop 出更小块后越界写。
                    basePtr = Marshal.AllocHGlobal((1 << idx) + HeaderSize);
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

            // 存活登记：本线程私有列表（owner 无锁追加；Reset 持 gate 收集）。
            var entry = GetOrCreateThreadEntry();
            lock (entry.gate)
                entry.items.Add(new Pending(payload, safetyHandleIndex, 0));
            return payload;
        }

        /// <summary>释放临时内存（用户手动调用 Dispose 时调用），同时移除映射。</summary>
        public static void Free(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return;

            // 快路径：本线程登记表（通常即分配线程；表很小，从尾扫热区）。
            var entry = tls;
            if (entry != null)
            {
                if (TryReleaseIn(entry, ptr, out bool found))
                {
                    if (found) return;
                }
            }

            // 慢路径：跨线程释放（罕见）或本线程表为空——持 Reset 锁遍历全部登记表，
            // 与 Reset 互斥保证"恰一次"释放（未在任一表找到 → 已释放过/未知，幂等忽略）。
            lock (_resetLock)
            {
                foreach (var e in s_entries)
                {
                    if (TryReleaseIn(e, ptr, out bool found2))
                    {
                        if (found2) return;
                    }
                }
            }
        }

        /// <summary>在单张登记表中查找并释放；返回是否命中（found）。</summary>
        private static bool TryReleaseIn(ThreadEntry e, IntPtr ptr, out bool found)
        {
            found = false;
            lock (e.gate)
            {
                var items = e.items;
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    var p = items[i];
                    if (p.Payload == ptr)
                    {
                        found = true;
                        if (p.Released == 0)
                        {
                            items[i] = new Pending(p.Payload, p.SafetyHandleIndex, 1);
                            FreeImpl(ptr);
                        }
                        return true;
                    }
                }
            }
            return false;
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

                // ② 逐线程释放（锁内执行，无并发干扰）。
                //    即使 job 异常也必须执行，否则 Temp 内存 + 安全句柄泄漏，
                //    且活跃 job 跨帧运行会让主线程读未完成输出（数据竞态）。
                foreach (var e in s_entries)
                {
                    List<Pending> taken;
                    lock (e.gate)
                    {
                        // List 是引用类型：直接 taken = e.items 会让 Clear 清空同一实例，遍历空转。
                        // 必须快照拷贝，否则帧末回收永不执行 → 每帧泄漏 Temp 内存 + 安全句柄。
                        taken = new List<Pending>(e.items);
                        e.items.Clear();
                    }
                    foreach (var p in taken)
                    {
                        if (p.Released != 0) continue; // 用户已手动 Free
                        SafetyHandleManager.MarkReleased(p.SafetyHandleIndex);
                        FreeImpl(p.Payload);
                    }
                }

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