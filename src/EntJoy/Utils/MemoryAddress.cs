using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EntJoy.Debugger
{
    /// <summary>
    /// 仅用于 DEBUG 输出。注意：返回的 IntPtr 仅在持有 GCHandle 期间有效。
    /// 调用 GetAddress 后会保持对象的 pin 状态，请在不再需要时调用 ReleasePinned 释放。
    /// </summary>
    public static class MemoryAddress
    {
        // 已 pin 的对象列表：保留 GCHandle 使返回的地址有效
        private static readonly Dictionary<object, GCHandle> _pinned = new();

        /// <summary>
        /// 获取对象的内存地址（会 pin 住对象直到 ReleasePinned 调用）
        /// </summary>
        public static IntPtr GetAddress(object obj)
        {
            if (obj == null) return IntPtr.Zero;

            var handle = GCHandle.Alloc(obj, GCHandleType.Pinned);
            IntPtr address = handle.AddrOfPinnedObject();
            lock (_pinned) _pinned[obj] = handle;
            return address;
        }

        /// <summary>
        /// 获取值类型引用在栈上的地址（不安全，地址仅在调用者的栈帧内有效）
        /// </summary>
        public unsafe static IntPtr GetAddress<T>(ref T value) where T : struct
        {
            return (IntPtr)Unsafe.AsPointer(ref value);
        }

        /// <summary>
        /// 获取数组的首元素地址（会 pin 住数组直到 ReleasePinned 调用）
        /// </summary>
        public static IntPtr GetArrayAddress(Array array)
        {
            if (array == null || array.Length == 0)
                return IntPtr.Zero;

            var handle = GCHandle.Alloc(array, GCHandleType.Pinned);
            IntPtr address = handle.AddrOfPinnedObject();
            lock (_pinned) _pinned[array] = handle;
            return address;
        }

        /// <summary>释放所有已 pin 的对象</summary>
        public static void ReleaseAll()
        {
            lock (_pinned)
            {
                foreach (var kvp in _pinned)
                    if (kvp.Value.IsAllocated) kvp.Value.Free();
                _pinned.Clear();
            }
        }

        // ---------- 已废弃：以下方法仅返回无效地址，保留仅为编译兼容 ----------

        [Obsolete("Use GetAddress(object) instead — this method returned a dangling pointer.")]
        public static IntPtr GetCachedAddress(object obj) => IntPtr.Zero;

        [Obsolete("No longer needed.")]
        public static void ClearAddressCache(object _) { }
    }
}
