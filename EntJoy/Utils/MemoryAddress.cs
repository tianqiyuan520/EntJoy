using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;

namespace EntJoy.Debugger
{
    public static class MemoryAddress
    {
        private static Dictionary<object, GCHandle> _pinnedObjects = new Dictionary<object, GCHandle>();

        private static Dictionary<object, IntPtr> _addressCache = new Dictionary<object, IntPtr>();

        /// <summary>
        /// 获取对象的缓存地址(可能变化)
        /// </summary>
        public static IntPtr GetCachedAddress(object obj)
        {
            if (obj == null) return IntPtr.Zero;

            lock(_addressCache)
            {
                if (!_addressCache.TryGetValue(obj, out var address))
                {
                    // 使用Weak GCHandle获取当前地址
                    var handle = GCHandle.Alloc(obj, GCHandleType.Weak);
                    address = GCHandle.ToIntPtr(handle);
                    handle.Free();
                    
                    _addressCache[obj] = address;
                }
                return address;
            }
        }

        /// <summary>
        /// 清除地址缓存
        /// </summary>
        public static void ClearAddressCache(object obj)
        {
            if (obj == null) return;

            lock(_addressCache)
            {
                _addressCache.Remove(obj);
            }
        }

        /// <summary>
        /// 获取对象的内存地址(临时)
        /// </summary>
        public static IntPtr GetAddress(object obj)
        {
            if (obj == null) return IntPtr.Zero;

            GCHandle handle = GCHandle.Alloc(obj, GCHandleType.Pinned);
            IntPtr address = handle.AddrOfPinnedObject();
            handle.Free();
            return address;
        }

        ///<summary>
        ///获取值类型的内存地址
        ///</summary>
        public static IntPtr GetAddress<T>(ref T value) where T : struct
        {
            GCHandle handle = GCHandle.Alloc(value, GCHandleType.Pinned);
            IntPtr address = handle.AddrOfPinnedObject();
            handle.Free();
            return address;
        }

        /// <summary>
        /// 获取数组的首元素地址
        /// </summary>
        public static IntPtr GetArrayAddress(Array array)
        {
            if (array == null || array.Length == 0)
                return IntPtr.Zero;

            GCHandle handle = GCHandle.Alloc(array, GCHandleType.Pinned);
            IntPtr address = handle.AddrOfPinnedObject();
            handle.Free();
            return address;
        }
    }
}
