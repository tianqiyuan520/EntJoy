using System;
using System.Runtime.InteropServices;

namespace EntJoy.Debugger
{
    public static class MemoryAddress
    {
        /// <summary>
        /// 获取对象的内存地址
        /// </summary>
        public static IntPtr GetAddress(object obj)
        {
            if (obj == null) return IntPtr.Zero;

            GCHandle handle = GCHandle.Alloc(obj, GCHandleType.Weak);
            IntPtr address = GCHandle.ToIntPtr(handle);
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
