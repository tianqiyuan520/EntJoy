using System.Buffers; 

namespace EntJoy  // EntJoy命名空间
{
    /// <summary>
    /// 世界实体的定位和更新
    /// </summary>
    /// <summary>
    /// 世界实体管理器，负责实体的生命周期管理
    /// </summary>
    /// <remarks>
    /// 主要功能：
    /// 1. 实体创建与销毁
    /// 2. 实体ID回收管理
    /// 3. 实体与组件的关联维护
    /// 设计特点：
    /// 1. 使用对象池技术回收实体ID
    /// 2. 数组存储保证内存连续性
    /// 3. 版本控制防止悬垂引用
    /// </remarks>
    public partial class World  // World类部分定义
    {
        /// <summary>世界名称</summary>
        // public string Name { get; private set; }  // 世界名称属性

        /// <summary>实体回收队列（对象池）</summary>
        private Queue<Entity> recycleEntities;  // 实体回收队列

        /// <summary>实体索引数组（直接索引访问）</summary>
        private EntityIndexInWorld[] entities;  // 实体索引数组

        /// <summary>当前已创建的实体总数</summary>
        private int entityCount;  // 实体计数器

        public World(string worldName = "Default")  // 构造函数
        {
            // Name = worldName;  // 设置世界名称
            recycleEntities = new Queue<Entity>();  // 初始化回收队列
            entities = ArrayPool<EntityIndexInWorld>.Shared.Rent(32);  // 从内存池租用数组
            archetypeMap = new Dictionary<int, Archetype>();  // 初始化原型映射
            allArchetypes = ArrayPool<Archetype>.Shared.Rent(8);  // 从内存池租用原型数组
        }

        /// <summary>
        /// 给定index，返回实体引用
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        public ref EntityIndexInWorld GetEntityInfoRef(int index)
        {
            return ref entities[index];
        }

        /// <summary>
        /// 创建新实体（基于组件类型）
        /// </summary>
        public Entity NewEntity(params Type[] componentTypes)
        {
            var componentSpan = new ComponentType[componentTypes.Length];  // 创建组件类型数组
            for (int i = 0; i < componentTypes.Length; i++)  // 遍历输入的组件类型
            {
                componentSpan[i] = ComponentTypeManager.GetComponentType(componentTypes[i]);  // 获取组件类型
            }
            return NewEntity(componentSpan.AsSpan());  // 调用核心实现
        }

        /// <summary>
        /// 创建新实体（基于ComponentType数组）
        /// </summary>
        public Entity NewEntity(params ComponentType[] types)  // 基于ComponentType创建实体
        {
            return NewEntity(types.AsSpan());  // 调用核心实现
        }

        /// <summary>
        /// 创建新实体核心实现
        /// </summary>
        public Entity NewEntity(Span<ComponentType> types)  // 创建实体核心方法
        {
            var newEntity = new Entity();  // 创建新实体
            bool isRecycled = recycleEntities.TryDequeue(out var recycledEnt);  // 尝试从回收队列获取

            if (isRecycled)  // 使用回收的实体
            {
                newEntity.Id = recycledEnt.Id;  // 复用ID
                newEntity.Version = recycledEnt.Version + 1;  // 版本号递增
            }
            else  // 无可复用的实体，则创建新实体
            {
                newEntity.Id = entityCount++;  // 分配新ID
                if (newEntity.Id >= entities.Length)  // 检查数组容量
                {
                    Array.Resize(ref entities, entities.Length * 2);  // 扩容数组
                }
            }

            var targetArch = GetOrCreateArchetype(types);
            targetArch.AddEntity(newEntity, out var index);  // 在该实体对应的原型中添加实体

            // 更新该实体索引
            entities[newEntity.Id] = new EntityIndexInWorld()
            {
                Archetype = targetArch,  // 设置原型
                Entity = newEntity,  // 设置实体
                SlotInArchetype = index,  // 设置槽位
            };

            //GD.Print("new Entity"," ", newEntity.Id," ", allArchetypes.Count()," ", allArchetypes[0]?.EntityCount," ", allArchetypes[1]?.EntityCount, allArchetypes[2]?.EntityCount);

            return newEntity;  // 返回新实体
        }

        /// <summary>
        /// 销毁实体
        /// </summary>
        public void KillEntity(Entity entity)  // 销毁实体方法
        {
            recycleEntities.Enqueue(entity);  // 加入回收队列
        }
    }
}
