using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EntJoy
{
    public unsafe partial class EntityManager : IDisposable
    {
        private readonly Dictionary<int, Archetype> archetypeMap;  // 原型映射表
        private Archetype[] allArchetypes;  // 所有原型数组

        private int archetypeCount;
        public int ArchetypeCount
        {
            get { return archetypeCount; }
            set { archetypeCount = value; }
        }
        public ref readonly Archetype[] Archetypes => ref allArchetypes;

        /// <summary>实体回收队列（对象池）</summary>
        private Queue<Entity> recycleEntities;  // 实体回收队列

        /// <summary>实体索引数组（直接索引访问）</summary>
        private EntityIndexInWorld[] entities;  // 实体索引数组

        /// <summary>当前已创建的实体总数</summary>
        private int entityCount;  // 实体计数器
        public int EntityCount => entityCount;

        private bool _disposed;


        public EntityManager()
        {
            recycleEntities = new Queue<Entity>();  // 初始化回收队列
            entities = ArrayPool<EntityIndexInWorld>.Shared.Rent(32);  // 从内存池租用数组
            archetypeMap = new Dictionary<int, Archetype>();  // 初始化原型映射
            allArchetypes = ArrayPool<Archetype>.Shared.Rent(8);  // 从内存池租用原型数组
        }

        /// <summary>
        /// 根据给定的 <see cref="ComponentType"/> 数组 <paramref name="types"/> 获取或创建对应的 <see cref="Archetype"/>
        /// </summary>
        private Archetype GetOrCreateArchetype(Span<ComponentType> types)  // 获取或创建原型方法
        {
            var hash = Utils.CalculateHash(types);
            if (archetypeMap.TryGetValue(hash, out Archetype archetype))  // 根据哈希值检查是否已存在
            {
                return archetype;  // 返回已有原型
            }
            // 不存在，则创建新原型
            archetype = new Archetype(types.ToArray());
            archetypeMap.Add(hash, archetype);
            //检查原型数组容量
            if (archetypeCount >= allArchetypes.Length)
            {
                Array.Resize(ref allArchetypes, allArchetypes.Length * 2);
            }
            // Todo: 如果有移除archetype的操作,空白的数组需要被填充
            allArchetypes[archetypeCount] = archetype;
            archetypeCount++;
            return archetype;
        }

        /// <summary>
        /// 获取所有原型数组
        /// </summary>
        public Archetype[] GetAllArchetypes()
        {
            return allArchetypes;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            for (int i = 0; i < archetypeCount; i++)
            {
                allArchetypes[i]?.Dispose();
                allArchetypes[i] = null;
            }

            archetypeMap.Clear();
            recycleEntities.Clear();
            entities = Array.Empty<EntityIndexInWorld>();
            allArchetypes = Array.Empty<Archetype>();
            archetypeCount = 0;
            entityCount = 0;
        }

    }
    // Entity
    public unsafe partial class EntityManager
    {
        /// <summary>
        /// 给定index，返回实体引用
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        public ref EntityIndexInWorld GetEntityInfoRef(int index)
        {
            return ref entities[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateEntityLocation(int entityId, Archetype archetype, int chunkIndex, int slotInChunk)
        {
            ref var entityInfoRef = ref GetEntityInfoRef(entityId);
            entityInfoRef.Archetype = archetype;
            entityInfoRef.ChunkIndex = chunkIndex;
            entityInfoRef.SlotInChunk = slotInChunk;
        }

        private void RefreshChunkEntityIndices(Archetype archetype, int chunkIndex)
        {
            var chunk = archetype.ChunkList[chunkIndex];
            for (int slot = 0; slot < chunk.EntityCount; slot++)
            {
                UpdateEntityLocation(chunk.GetEntity(slot).Id, archetype, chunkIndex, slot);
            }
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
        public unsafe Entity NewEntity(Span<ComponentType> types)  // 创建实体核心方法
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
            targetArch.AddEntity(newEntity, out var chunkIndex, out var slotInChunk);  // 在该实体对应的原型中添加实体

            // 更新该实体索引
            UpdateEntityLocation(newEntity.Id, targetArch, chunkIndex, slotInChunk);

            //GD.Print("new Entity"," ", newEntity.Id," ", allArchetypes.Count()," ", allArchetypes[0]?.entityCount," ", allArchetypes[1]?.entityCount, allArchetypes[2]?.entityCount);

            return newEntity;  // 返回新实体
        }

    }

    // Query
    //public unsafe partial class EntityManager
    //{
    //    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    //    public unsafe void Query<T0>(QueryBuilder builder, ISystem<T0> system)
    //        where T0 : struct
    //    {
    //        int entityCounter = 0; // 记录查询到的实体数量
    //        int limitCount = builder.LimitCount;

    //        for (int i = 0; i < archetypeCount; i++)
    //        {
    //            var archetype = allArchetypes[i];
    //            if (archetype != null && archetype.IsMatch(builder))
    //            {
    //                int t0Index = archetype.GetComponentTypeIndex<T0>();
    //                var chunks = archetype.GetChunks();
    //                var ArchtypeIndex = i;
    //                system.InArchetype(ArchtypeIndex);
    //                for (int j = 0; j < chunks.Count; j++)
    //                {
    //                    var chunk = chunks[j];
    //                    int count = chunk.EntityCount;
    //                    if (count == 0) continue;
    //                    var ChunkIndex = j;
    //                    system.InChunk(ArchtypeIndex, ChunkIndex);
    //                    Entity* entities = (Entity*)chunk.GetEntityPointer().ToPointer();
    //                    T0* components = (T0*)chunk.GetComponentArrayPointer(t0Index).ToPointer();
    //                    {
    //                        system._execute(entities, components, count, limitCount - entityCounter, ArchtypeIndex, ChunkIndex);
    //                    }
    //                    entityCounter += count;
    //                    if (limitCount != -1 && entityCounter >= limitCount) break;
    //                }
    //            }
    //        }
    //    }

    //    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    //    public unsafe void Query<T0, T1>(QueryBuilder builder, ISystem<T0, T1> system)
    //        where T0 : struct
    //        where T1 : struct
    //    {
    //        int entityCounter = 0; // 记录查询到的实体数量
    //        int limitCount = builder.LimitCount;
    //        unchecked
    //        {

    //            for (int i = 0; i < archetypeCount; i++)
    //            {
    //                var archetype = allArchetypes[i];
    //                if (archetype != null && archetype.IsMatch(builder))
    //                {

    //                    int t0Index = archetype.GetComponentTypeIndex<T0>();
    //                    int t1Index = archetype.GetComponentTypeIndex<T1>();
    //                    var chunks = archetype.GetChunks();
    //                    var ArchtypeIndex = i;
    //                    system.InArchetype(ArchtypeIndex);
    //                    for (int j = 0; j < chunks.Count; j++)
    //                    {
    //                        var chunk = chunks[j];
    //                        int count = chunk.EntityCount;
    //                        if (count == 0) continue;
    //                        var ChunkIndex = j;
    //                        system.InChunk(ArchtypeIndex, ChunkIndex);
    //                        Entity* entities = (Entity*)chunk.GetEntityPointer().ToPointer();
    //                        T0* components0 = (T0*)chunk.GetComponentArrayPointer(t0Index).ToPointer();
    //                        T1* components1 = (T1*)chunk.GetComponentArrayPointer(t1Index).ToPointer();
    //                        {
    //                            system._execute(entities, components0, components1, count, limitCount - entityCounter, ArchtypeIndex, ChunkIndex);
    //                        }

    //                        entityCounter += count;
    //                        if (limitCount != -1 && entityCounter >= limitCount) break;
    //                    }
    //                }
    //            }

    //            //allArchetypes[0].Query(system);

    //        }

    //    }



    //    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    //    public unsafe void MultiQuery<T0>(QueryBuilder builder, ISystem<T0> system)
    //        where T0 : struct
    //    {
    //        static void RunSystem(Chunk chunk, int t0Index, ISystem<T0> system, int LimitCount, int ArchetypeIndex, int ChunkIndex)
    //        {
    //            int count = chunk.EntityCount;
    //            Entity* entities = (Entity*)chunk.GetEntityPointer().ToPointer();
    //            T0* components0 = (T0*)chunk.GetComponentArrayPointer(t0Index).ToPointer();
    //            system._execute(entities, components0, count, LimitCount, ArchetypeIndex, ChunkIndex);
    //        }

    //        unchecked
    //        {
    //            int entityCounter = 0;
    //            int limitCount = builder.LimitCount;

    //            for (int i = 0; i < archetypeCount; i++)
    //            {
    //                var archetype = allArchetypes[i];
    //                if (archetype != null && archetype.IsMatch(builder))
    //                {
    //                    int t0Index = archetype.GetComponentTypeIndex<T0>();
    //                    List<Task> tasks = new();

    //                    var ArchtypeIndex = i;
    //                    system.InArchetype(ArchtypeIndex);

    //                    var chunks = archetype.GetChunks();
    //                    for (int j = 0; j < chunks.Count; j++)
    //                    {
    //                        var chunk = chunks[j];
    //                        int count = chunk.EntityCount;
    //                        if (count == 0) continue;
    //                        int spareCount = limitCount - entityCounter;
    //                        int ChunkIndex = j;

    //                        system.InChunk(ArchtypeIndex, ChunkIndex);
    //                        Task task = Task.Run(() =>
    //                        {
    //                            RunSystem(chunk, t0Index, system, spareCount, ArchtypeIndex, ChunkIndex);
    //                        }
    //                        );

    //                        tasks.Add(task);
    //                        entityCounter += count;
    //                        if (limitCount != -1 && entityCounter >= limitCount) break;

    //                    }
    //                    Task.WaitAll(tasks.ToArray());
    //                }
    //            }
    //        }
    //    }
    //    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    //    public unsafe void MultiQuery<T0, T1>(QueryBuilder builder, ISystem<T0, T1> system)
    //        where T0 : struct
    //        where T1 : struct
    //    {
    //        void RunSystem(Chunk chunk, int t0Index, int t1Index, ISystem<T0, T1> system, int LimitCount, int ArchetypeCount, int ChunkIndex)
    //        {
    //            int count = chunk.EntityCount;


    //            Entity* entities = (Entity*)chunk.GetEntityPointer().ToPointer();
    //            T0* components0 = (T0*)chunk.GetComponentArrayPointer(t0Index).ToPointer();
    //            T1* components1 = (T1*)chunk.GetComponentArrayPointer(t1Index).ToPointer();
    //            system._execute(entities, components0, components1, count, LimitCount, ArchetypeCount, ChunkIndex);
    //        }

    //        unchecked
    //        {
    //            int entityCounter = 0;
    //            int limitCount = builder.LimitCount;

    //            for (int i = 0; i < archetypeCount; i++)
    //            {
    //                var archetype = allArchetypes[i];
    //                if (archetype != null && archetype.IsMatch(builder))
    //                {
    //                    int t0Index = archetype.GetComponentTypeIndex<T0>();
    //                    int t1Index = archetype.GetComponentTypeIndex<T1>();

    //                    List<Task> tasks = new();

    //                    var ArchtypeIndex = i;
    //                    system.InArchetype(ArchtypeIndex);

    //                    var chunks = archetype.GetChunks();
    //                    for (int j = 0; j < chunks.Count; j++)
    //                    {
    //                        var chunk = chunks[j];
    //                        int count = chunk.EntityCount;
    //                        if (count == 0) continue;
    //                        int spareCount = limitCount - entityCounter;

    //                        var ChunkIndex = j;

    //                        system.InChunk(ArchtypeIndex, ChunkIndex);

    //                        Task task = Task.Run(() =>
    //                        {
    //                            RunSystem(chunk, t0Index, t1Index, system, spareCount, ArchtypeIndex, ChunkIndex);
    //                        }
    //                        );

    //                        tasks.Add(task);
    //                        //task.Start();

    //                        entityCounter += count;
    //                        if (limitCount != -1 && entityCounter >= limitCount) break;

    //                    }
    //                    Task.WaitAll(tasks.ToArray());
    //                    //Task.WhenAll(tasks.Select(t => t.AsTask()));
    //                }
    //            }
    //        }
    //    }

    //    //[MethodImpl(MethodImplOptions.AggressiveInlining)]
    //    //private IEnumerable<Archetype> GetMatchingArchetypes(QueryBuilder builder)
    //    //{
    //    //    for (int i = 0; i < archetypeCount; i++)
    //    //    {
    //    //        var arch = allArchetypes[i];
    //    //        if (arch != null && arch.IsMatch(builder))
    //    //        {
    //    //            yield return arch;
    //    //        }
    //    //    }
    //    //}
    //}

    // Component
    public unsafe partial class EntityManager
    {
        /// <summary>
        /// 添加组件
        /// </summary>
        public void AddComponent<T0>(Entity entity, T0 t0) where T0 : struct
        {
            ref var entityInfoRef = ref GetEntityInfoRef(entity.Id);
            var oldArch = entityInfoRef.Archetype;
            if (oldArch.Has(typeof(T0)))
            {
                oldArch.Set(entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk, t0);
                return;
            }

            // 创建新组件类型数组
            Span<ComponentType> targetComponents = stackalloc ComponentType[oldArch.ComponentCount + 1];
            oldArch.Types.CopyTo(targetComponents);
            targetComponents[^1] = ComponentTypeManager.GetComponentType(typeof(T0));

            var targetArch = GetOrCreateArchetype(targetComponents);
            targetArch.AddEntity(entity, out var chunkIndex, out var slotInChunk);

            // 复制组件数据
            oldArch.CopyComponentsTo(entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk, targetArch, chunkIndex, slotInChunk);

            // 从旧原型移除
            oldArch.Remove(entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk, out var movedEntityID, out var movedEntitySlotInChunk, out var compactedChunkIndex);

            if (movedEntityID >= 0)
            {
                UpdateEntityLocation(movedEntityID, oldArch, entityInfoRef.ChunkIndex, movedEntitySlotInChunk);
            }

            if (compactedChunkIndex >= 0)
            {
                RefreshChunkEntityIndices(oldArch, compactedChunkIndex);
            }

            // 刷新索引（关键修复）
            UpdateEntityLocation(entity.Id, targetArch, chunkIndex, slotInChunk);

            // 设置新组件值
            targetArch.Set(chunkIndex, slotInChunk, t0);
        }


        public void RemoveComponent<T0>(Entity entity) where T0 : struct
        {
            ref var entityInfoRef = ref GetEntityInfoRef(entity.Id);
            var oldArch = entityInfoRef.Archetype;
            //若旧原型中无该类型，则直接返回
            if (!oldArch.Has(typeof(T0)))
            {
                return;
            }

            //生成 目标"组件类型"
            Span<ComponentType> targetComponents = stackalloc ComponentType[oldArch.ComponentCount - 1];  // 创建新组件数组
            int spanIndex = 0;
            for (int i = 0; i < oldArch.Types.Length; i++)  // 遍历组件类型
            {
                var comType = oldArch.Types[i];  // 获取组件类型
                if (comType.Type == typeof(T0))  // 跳过要移除的组件
                {
                    continue;
                }

                targetComponents[spanIndex++] = comType;  // 添加保留的组件
            }
            // 获取或创建新原型
            var targetArch = GetOrCreateArchetype(targetComponents);
            targetArch.AddEntity(entity, out var chunkIndex, out var slotInChunk);  // 添加实体到新原型

            oldArch.CopyComponentsTo(entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk, targetArch, chunkIndex, slotInChunk);  //复制组件数据
            oldArch.Remove(entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk, out var movedEntityID, out var movedEntitySlotInChunk, out var compactedChunkIndex);  // 从旧原型移除
            if (movedEntityID >= 0)  // 处理被移动的实体
            {
                UpdateEntityLocation(movedEntityID, oldArch, entityInfoRef.ChunkIndex, movedEntitySlotInChunk);
            }

            if (compactedChunkIndex >= 0)
            {
                RefreshChunkEntityIndices(oldArch, compactedChunkIndex);
            }
            //刷新索引
            UpdateEntityLocation(entity.Id, targetArch, chunkIndex, slotInChunk);
        }

        /// <summary>
        /// 设置组件值
        /// </summary>
        public void Set<T>(Entity entity, T t) where T : struct, IComponentData
        {
            ref var entityInfoRef = ref GetEntityInfoRef(entity.Id);
            var arch = entityInfoRef.Archetype;  // 获取对应的原型
            arch.Set(entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk, t);
        }
    }

    public unsafe partial class EntityManager
    {
        #region Enableable Components

        /// <summary>
        /// 设置指定实体上 enableable 组件的启用状态。
        /// </summary>
        /// <typeparam name="T">实现了 IEnableableComponent 的组件类型</typeparam>
        /// <param name="entity">目标实体</param>
        /// <param name="enabled">true 为启用，false 为禁用</param>
        /// <exception cref="InvalidOperationException">如果实体不包含该组件，或组件不可 enable</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetComponentEnabled<T>(Entity entity, bool enabled) where T : struct, IEnableableComponent
        {
            ref var info = ref GetEntityInfoRef(entity.Id);
            var archetype = info.Archetype;

            // 检查实体是否拥有该组件
            if (!archetype.Has(typeof(T)))
                throw new InvalidOperationException($"Entity {entity} does not have component {typeof(T).Name}.");

            int compIdx = archetype.GetComponentTypeIndex<T>();
            var chunks = archetype.GetChunks();
            var chunk = chunks[info.ChunkIndex];

            chunk.SetComponentEnabled(compIdx, info.SlotInChunk, enabled);
        }

        /// <summary>
        /// 获取指定实体上 enableable 组件的当前启用状态。
        /// </summary>
        /// <typeparam name="T">实现了 IEnableableComponent 的组件类型</typeparam>
        /// <param name="entity">目标实体</param>
        /// <returns>true 表示启用，false 表示禁用</returns>
        /// <exception cref="InvalidOperationException">如果实体不包含该组件，或组件不可 enable</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsComponentEnabled<T>(Entity entity) where T : struct, IEnableableComponent
        {
            ref var info = ref GetEntityInfoRef(entity.Id);
            var archetype = info.Archetype;

            if (!archetype.Has(typeof(T)))
                throw new InvalidOperationException($"Entity {entity} does not have component {typeof(T).Name}.");

            int compIdx = archetype.GetComponentTypeIndex<T>();
            var chunks = archetype.GetChunks();
            var chunk = chunks[info.ChunkIndex];

            return chunk.GetComponentEnabled(compIdx, info.SlotInChunk);
        }

        #endregion
    }






}
