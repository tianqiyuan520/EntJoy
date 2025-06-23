using EntJoy.Debugger;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace EntJoy
{
    public sealed class Archetype
    {
        // private const int BaseChunkSize = 16 * 1024; // 16KB基础块大小
        // private readonly List<Chunk> _chunks = new();
        private ComponentType[] types;  // 该原型对应的组件类型数组
        // private int _chunkCapacity; // 每个Chunk能容纳的实体数
        // private void CalculateChunkCapacity()
        // {
        //     // 计算单个组件占用的空间
        //     int componentSize = 0;
        //     foreach (var type in types)
        //     {
        //         componentSize += Marshal.SizeOf(type.Type);
        //     }

        //     // 计算适合缓存行的实体数量
        //     _chunkCapacity = Math.Max(16, BaseChunkSize / componentSize);

        //     // 对齐到16的倍数
        //     _chunkCapacity = (_chunkCapacity + 15) & ~15;
        // }
        public ReadOnlySpan<ComponentType> Types => types;  // 该原型对应的组件类型数组[只读]
        private StructArray[] structArrays;  // 组件数据数组
        private StructArray<Entity> entities;  // 实体数组
        private Dictionary<ComponentType, int> componentTypeRecorder;  // 组件类型与索引映射(用来查找保存的组件类型)
        public int ComponentCount { get; private set; }  // 组件数量
        public int EntityCount { get; private set; }  // 实体数量

        internal Archetype(ComponentType[] ts)
        {
            types = ts;
            ComponentCount = ts.Length;
            structArrays = new StructArray[ts.Length];
            entities = new StructArray<Entity>(8);  // 初始化实体数组
            componentTypeRecorder = new Dictionary<ComponentType, int>(ts.Length);
            for (int i = 0; i < ComponentCount; i++)
            {
                componentTypeRecorder.Add(types[i], i);  // 添加类型到索引映射
                var genericType = typeof(StructArray<>).MakeGenericType(types[i].Type);  //创建泛型数组类型
                var instance = Activator.CreateInstance(genericType, [8]);  // 实例化对应组件类型的数组默认值
                var array = (StructArray?)instance;  // 转换为基类
                structArrays[i] = array;  // 存储数组
            }
        }

        /// <summary>
        /// 检查是否包含所有指定组件
        /// </summary>
        public bool HasAllOf(Span<ComponentType> spanTypes)
        {
            int len = spanTypes.Length;
            for (int i = 0; i < len; i++)
            {
                if (!componentTypeRecorder.ContainsKey(spanTypes[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 检查是否包含任意指定组件
        /// </summary>
        public bool HasAnyOf(Span<ComponentType> spanTypes)
        {
            int len = spanTypes.Length;
            for (int i = 0; i < len; i++)
            {
                if (componentTypeRecorder.ContainsKey(spanTypes[i]))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 检查是否不包含所有指定组件
        /// </summary>
        public bool HasNoneOf(Span<ComponentType> spanTypes)
        {
            int len = spanTypes.Length;
            for (int i = 0; i < len; i++)
            {
                if (componentTypeRecorder.ContainsKey(spanTypes[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 检查是否包含指定类型组件
        /// </summary>
        /// <param name="type"></param>
        public bool Has(Type type)
        {
            for (int i = 0; i < types.Length; i++)
            {
                if (types[i] == type)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 添加实体
        /// </summary>
        public void AddEntity(Entity entity, out int slotInArchetype)
        {
            entities.Add(entity);
            slotInArchetype = EntityCount;  // 返回该实体再该原型里的索引
            EntityCount++;
        }

        /// <summary>
        /// 删除实体和对应组件
        /// </summary>
        public void Remove(int index, out int bePushEntityId, out int bePushEntityNewIndexInArchetype)  // 移除实体方法
        {
            //GD.Print("触发实体与组件删除 ", "实体id: ", index, " 当前实体数", EntityCount, " ");
            // 用最后一个有效实体去覆盖被移除的位置
            if (EntityCount >= 2)
            {
                int LastEntityIndex = EntityCount - 1;  // 获取最后一个实体索引
                bePushEntityId = entities.GetRef(LastEntityIndex).Id;  // 获取被移动实体ID
                bePushEntityNewIndexInArchetype = index;  // 设置新索引
                entities.Move(LastEntityIndex, index);  // 移动实体
                for (int i = 0; i < ComponentCount; i++)  // 遍历所有组件
                {
                    var array = structArrays[i];  // 获取组件数组
                    array.Move(LastEntityIndex, index);  // 移动组件数据
                }
                EntityCount--;
            }
            // 如果这是最后一个实体了, 那就只要设置为默认 
            else
            {
                EntityCount = 0; //[BugFix]
                bePushEntityId = -1;  // 设置无效ID
                bePushEntityNewIndexInArchetype = -1;  // 设置无效索引
                entities.SetDefault(index);  // 重置实体数据
                for (int i = 0; i < ComponentCount; i++)  // 遍历所有组件
                {
                    var array = structArrays[i];  // 获取组件数组
                    //array.SetDefault(index);  // 重置组件数据
                    array.ClearData();
                }
            }
        }

        /// <summary>
        /// 设置指定位置的组件值
        /// </summary>
        public void Set<T>(int index, T value) where T : struct
        {
            var arrayIndex = componentTypeRecorder[typeof(T)];
            var array = (StructArray<T>)structArrays[arrayIndex];
            array.SetValue(index, value);
        }

        /// <summary>
        /// 复制当前实体
        /// </summary>
        public void CopyComponentsTo(int sourceIndex, Archetype target, int destinationIndex)
        {
            for (int i = 0; i < ComponentCount; i++)  // 遍历所有组件
            {
                // 当前要复制的组件类型
                var array = structArrays[i];  // 获取源组件数组
                var arrayType = array.GetStructType();  // 获取组件类型

                //GD.Print("组件拷贝: ", arrayType);

                // 计算目标原型中,是否包含此组件类型
                if (!target.Has(arrayType))  // 检查目标是否包含
                {
                    continue;  // 不包含则跳过
                }
                // 找到目标原型中, 该类型组件是第几行第几列
                // 然后把组件的值复制过去
                var targetCow = target.componentTypeRecorder[arrayType];  // 获取目标数组索引
                var targetColumn = destinationIndex;  // 获取目标列索引
                var targetArray = target.structArrays[targetCow];  // 获取目标数组
                array.CopyTo(sourceIndex, targetArray, targetColumn);  // 复制数据
            }
        }

        /// <summary>
        /// 给定索引，获取对应实体
        /// </summary>
        public ref Entity GetEntity(int index)
        {
            return ref entities.GetRef(index);
        }
        /// <summary>
        /// 给定索引，获取所有实体
        /// </summary>
        public ref Entity[] GetEntities()
        {
            return ref entities.GetAllData();
        }

        public ArchetypeQuery<T> GetQuery<T>() where T : struct  // 获取该组件的查询
        {
            var indexOfT = componentTypeRecorder[typeof(T)];  // 获取组件索引
            var array = structArrays[indexOfT];  //获取组件数组
            return new ArchetypeQuery<T>((StructArray<T>)array);  // 创建并返回查询
        }

        public StructArray<T> GetQueryStructArray<T>() where T : struct  // 获取该组件的查询
        {
            var indexOfT = componentTypeRecorder[typeof(T)];  // 获取组件索引
            var array = structArrays[indexOfT];  //获取组件数组
            return (StructArray<T>)array;  // 创建并返回查询
        }


        private IntPtr _cachedAddress;

        // 获取原型自身地址(缓存)
        public IntPtr GetAddress()
        {
            if (_cachedAddress == IntPtr.Zero)
            {
                _cachedAddress = MemoryAddress.GetCachedAddress(this);
            }
            return _cachedAddress;
        }

        ~Archetype()
        {
            if (_cachedAddress != IntPtr.Zero)
            {
                MemoryAddress.ClearAddressCache(this);
            }
        }

        // 获取实体数组地址
        public IntPtr GetEntityArrayAddress()
        {
            return entities.GetAddress();
        }

        // 获取组件数组地址
        public IntPtr GetComponentArrayAddress(int index)
        {
            if (index < 0 || index >= structArrays.Length)
                return IntPtr.Zero;

            return structArrays[index].GetAddress();
        }
        // 获取组件数组指针
        public unsafe IntPtr GetComponentArrayPointer<T>() where T : struct
        {
            if (!componentTypeRecorder.TryGetValue(typeof(T), out int index))
            {
                return IntPtr.Zero;
            }

            var structArray = structArrays[index] as StructArray<T>;
            return structArray?.GetDataPointer() ?? IntPtr.Zero;
        }

        // 获取内存布局信息
        public unsafe string GetMemoryLayoutInfo()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== Archetype 内存布局 ===");
            sb.AppendLine($"Archetype Address: {GetAddress().ToInt64():D}");
            sb.AppendLine($"实体数: {EntityCount}, 组件数: {ComponentCount}");

            // 输出实体数组信息
            var entityArray = entities as StructArray<Entity>;
            IntPtr entityArrayAddr = entityArray?.GetDataPointer() ?? IntPtr.Zero;
            sb.AppendLine($"  Entity Array: {entityArrayAddr.ToInt64():D}");

            // 输出组件数组信息
            IntPtr prevAddress = IntPtr.Zero;
            int preSize = 0;
            for (int i = 0; i < ComponentCount; i++)
            {
                var array = structArrays[i];
                IntPtr addr = IntPtr.Zero;
                int size = array.GetMemorySize();

                // 使用反射调用GetDataPointer方法
                var method = array.GetType().GetMethod("GetDataPointer");
                if (method != null)
                {
                    addr = (IntPtr)method.Invoke(array, null);
                }

                sb.AppendLine($"  Component {i} ({types[i].Type.Name}):");
                sb.AppendLine($"    Array Address: {addr.ToInt64():D}");
                sb.AppendLine($"    Memory Size: {size} bytes");

                // 计算与前一个组件的地址差
                if (prevAddress != IntPtr.Zero && addr != IntPtr.Zero)
                {
                    long gap = (long)addr - (long)prevAddress;
                    sb.AppendLine($"  与前一个组件的地址差: {preSize}+{gap} bytes");
                }

                prevAddress = (addr != IntPtr.Zero) ? addr + size : IntPtr.Zero;
                preSize = size;
            }

            return sb.ToString();
        }
    }
}
