using EntJoy.Debugger;
using System;
using System.Runtime.InteropServices;

namespace EntJoy
{
    public abstract class StructArray  // 结构数组基类
    {
        public int Capacity { get; set; }  // 数组容量
        public int Length { get; set; } // 容器的有效长度

        public StructArray(int defaultCapacity) { }
        public abstract void EnsureCapacity(int newCapacity);
        public abstract void Move(int from, int to);
        public abstract void SetDefault(int index);
        public abstract void CopyTo(int sourceIndex, StructArray target, int targetIndex);
        public abstract void ClearData();
        public abstract Type GetStructType();
        public abstract IntPtr GetAddress();
        public abstract int GetMemorySize();
        public abstract string GetDebugValue(int index);
        public abstract object GetValue(int index);
        public abstract IntPtr GetElementAddress(int entityIndex);
    }

    /// <summary>
    /// 泛型结构数组具体实现，管理特定类型组件的存储
    /// </summary>
    public sealed class StructArray<T> : StructArray  // 泛型结构数组
        where T : struct
    {
        public T[] Data;  // 该特定类型组件的数据数组

        public StructArray(int defaultCapacity) : base(defaultCapacity)
        {
            Data = new T[defaultCapacity];
            Capacity = defaultCapacity;
            Length = 0;
        }
        /// <summary>
        /// 检测并保证容量大小
        /// </summary>
        public override void EnsureCapacity(int newCapacity)
        {
            if (Capacity < newCapacity)
            {
                //扩容
                Array.Resize(ref Data, newCapacity * 2);
                Capacity = newCapacity * 2;
            }
        }

        /// <summary>
        /// 添加该组件数据
        /// </summary>
        public void Add(T item)
        {
            EnsureCapacity(Length + 1);  // 确保容量足够
            unchecked  // 禁用溢出检查
            {
                Data[Length] = item;
                Length++;
            }
        }

        /// <summary>
        /// 获取该组件数据的引用
        /// </summary>
        public ref T GetRef(int index)
        {
            if (index < 0 || index >= Length)
                throw new IndexOutOfRangeException($"在获取组件数据的引用时出现异常，索引{index}超出有效范围[0, {Length})");

            return ref Data[index];  // 返回引用
        }
        /// <summary>
        /// 获取所有组件数据
        /// </summary>
        public ref T[] GetAllData()
        {
            return ref Data; 
        }

        /// <summary>
        /// 设置对应位置组件数据的值
        /// </summary>
        public void SetValue(int index, T value)  // 设置元素值
        {
            EnsureCapacity(index + 1);  // 确保容量足够

            unchecked  // 禁用溢出检查
            {
                Data[index] = value;  // 设置值
                if (index >= Length)  // 更新长度
                {
                    Length = index + 1;
                }
            }
        }

        public override void Move(int from, int to)  // 移动元素
        {
            Data[to] = Data[from];
            Data[from] = default;
            Length--;  // 减少长度
        }

        public override void SetDefault(int index)  // 设置默认值
        {
            Data[index] = default;  // 重置为默认值
        }

        /// <summary>
        /// 拷贝该实体数据到另一个 <see cref="StructArray"/> 里去
        /// </summary>
        public override void CopyTo(int sourceIndex, StructArray target, int targetIndex)
        {
            var genericTarget = (StructArray<T>)target;  // 类型转换
            genericTarget.SetValue(targetIndex, Data[sourceIndex]);  // 设置目标值
        }

        public override Type GetStructType()
        {
            return typeof(T);
        }

        public override void ClearData()
        {
            Data = new T[Capacity];
            Length = 0;
        }


        public override IntPtr GetAddress()
        {
            return MemoryAddress.GetArrayAddress(Data);
        }

        public override int GetMemorySize()
        {
            if (Data == null) return 0;

            try
            {
                // 计算实际内存大小
                return Data.Length * Marshal.SizeOf(typeof(T));
            }
            catch
            {
                // 对于复杂类型，返回近似值
                return Data.Length * IntPtr.Size;
            }
        }

        // 获取元素地址
        public override IntPtr GetElementAddress(int index)
        {
            if (Data == null || index < 0 || index >= Data.Length)
                return IntPtr.Zero;

            // 创建临时引用以获取地址
            T element = Data[index];
            return MemoryAddress.GetAddress(ref element);
        }

        // 获取调试值
        public override string GetDebugValue(int index)
        {
            if (index < 0 || index >= Data.Length)
                return "索引溢出";

            return Data[index].ToString();
        }
        // 获取值
        public override object GetValue(int index)
        {
            if (index < 0 || index >= Data.Length)
                return "索引溢出";

            return Data[index];
        }

        public unsafe IntPtr GetDataPointer()
        {
            if (Data == null || Data.Length == 0)
                return IntPtr.Zero;

            fixed (T* ptr = &Data[0])
            {
                return (IntPtr)ptr;
            }
        }
    }
}
