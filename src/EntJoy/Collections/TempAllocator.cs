using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using EntJoy.JobSystem;

namespace EntJoy.Collections
{
    public static class TempAllocator
    {
        // 使用字典记录所有活跃的 Temp 内存指针 -> 对应的安全句柄索引
        private static readonly ConcurrentDictionary<IntPtr, int> _active = new ConcurrentDictionary<IntPtr, int>();

        // Reset 期间阻止并发分配，防止快照遗漏 + use-after-free
        private static readonly object _resetLock = new();

        /// <summary>分配临时内存，并关联安全句柄索引。</summary>
        public static IntPtr Alloc(int size, int safetyHandleIndex)
        {
            var ptr = Marshal.AllocHGlobal(size);
            lock (_resetLock) _active.TryAdd(ptr, safetyHandleIndex);
            return ptr;
        }

        /// <summary>释放临时内存（用户手动调用 Dispose 时调用），同时移除映射。</summary>
        public static void Free(IntPtr ptr)
        {
            lock (_resetLock)
            {
                if (_active.TryRemove(ptr, out _))
                    Marshal.FreeHGlobal(ptr);
            }
        }

        /// <summary>在帧末调用，释放所有未被手动释放的 Temp 内存，并标记对应的安全句柄为已释放。</summary>
        public static void Reset()
        {
            lock (_resetLock)
            {
                // ① 先完成所有活跃异步 Job，确保没有 C++ Worker 线程还在读写 Temp 内存。
                // 若 job 抛异常也须继续——下面的内存释放不能跳过。
                ExceptionDispatchInfo? pending = null;
                if (World.DefaultWorld != null)
                {
                    try
                    {
                        World.DefaultWorld._entityManager.CompleteActiveJobs();
                    }
                    catch (Exception ex)
                    {
                        pending = ExceptionDispatchInfo.Capture(ex);
                    }
                }

                // ② 释放内存（锁内执行，无并发干扰，无需快照）。
                // 即使 job 异常也必须执行，否则 Temp 内存 + 安全句柄泄漏，
                // 且活跃 job 跨帧运行会让主线程读未完成输出（数据竞态）。
                foreach (var kvp in _active)
                {
                    SafetyHandleManager.MarkReleased(kvp.Value);
                    Marshal.FreeHGlobal(kvp.Key);
                }
                _active.Clear();

                // ③ 最后抛异常（job 异常 + 帧内未归属异常）。
                pending?.Throw();
                NativeJobScheduler.FlushRecordedExceptions();
            }
        }
    }
}