using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace EntJoy.Collections
{
    public static class TempAllocator
    {
        // 使用字典记录所有活跃的 Temp 内存指针 -> 对应的安全句柄索引
        private static readonly ConcurrentDictionary<IntPtr, int> _active = new ConcurrentDictionary<IntPtr, int>();

        /// <summary>分配临时内存，并关联安全句柄索引。</summary>
        public static IntPtr Alloc(int size, int safetyHandleIndex)
        {
            var ptr = Marshal.AllocHGlobal(size);
            _active.TryAdd(ptr, safetyHandleIndex);
            return ptr;
        }

        /// <summary>释放临时内存（用户手动调用 Dispose 时调用），同时移除映射。</summary>
        public static void Free(IntPtr ptr)
        {
            if (_active.TryRemove(ptr, out _))
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        /// <summary>在帧末调用，释放所有未被手动释放的 Temp 内存，并标记对应的安全句柄为已释放。</summary>
        public static void Reset()
        {
            foreach (var kvp in _active)
            {
                // 先标记安全句柄为已释放（使所有持有该句柄的容器访问时抛出异常）
                SafetyHandleManager.MarkReleased(kvp.Value);
                Marshal.FreeHGlobal(kvp.Key);
            }
            _active.Clear();
        }
    }
}