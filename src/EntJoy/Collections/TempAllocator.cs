using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

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
                // ① 先检查并抛出 Job 异常
                NativeJobScheduler.FlushRecordedExceptions();

                // ② 完成所有活跃异步 Job，确保没有 C++ Worker 线程还在读写 Temp 内存
                if (World.DefaultWorld != null)
                {
                    var entityManager = World.DefaultWorld._entityManager;
                    entityManager.CompleteActiveJobs();
                }

                // ③ 再释放内存（锁内执行，无并发干扰，无需快照）
                foreach (var kvp in _active)
                {
                    SafetyHandleManager.MarkReleased(kvp.Value);
                    Marshal.FreeHGlobal(kvp.Key);
                }
                _active.Clear();
            }
        }
    }
}