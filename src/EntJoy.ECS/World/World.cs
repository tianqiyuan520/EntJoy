using EntJoy.Collections;
using EntJoy.JobSystem;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace EntJoy.ECS
{
    public partial class World : IDisposable
    {
        private static readonly object _defaultLock = new();
        [ThreadStatic] private static World _defaultWorld;   // 每线程独立：双 SystemRunner 并行不互踩
        public static World DefaultWorld
        {
            get => _defaultWorld;
            set => _defaultWorld = value;
        }

        public string Name { get; private set; }
        public long CurrentFrame { get; set; }
        public EntityManager _entityManager;
        public ref EntityManager EntityManager => ref _entityManager;

        private bool _disposed;

        // Event Channel
        private readonly ConcurrentDictionary<Type, object> _eventStreams = new();

        public World(string worldName = "Default")
        {
            Name = worldName;
            _entityManager = new EntityManager();

            // 线程安全地设置 DefaultWorld：只有第一个 World 会成为默认
            lock (_defaultLock)
            {
                if (DefaultWorld == null)
                {
                    DefaultWorld = this;

                    // 注册 TempAllocator 回调：Collections 项目不引用 ECS/Jobs，
                    // 通过静态回调实现依赖反转。
                    TempAllocator.OnBeforeReset = () =>
                    {
                        DefaultWorld?._entityManager.CompleteActiveJobs();
                    };
                    TempAllocator.OnAfterReset = () =>
                    {
                        NativeJobScheduler.FlushRecordedExceptions();
                    };
                }
            }
        }

        public EntityQuery CreateEntityQuery(QueryBuilder builder) => new EntityQuery(this, builder);

        /// <summary>获取（或创建）共享查询：相同规则返回同一实例，跨调用复用匹配集合。
        /// 刷新是惰性的（访问时做版本检查），实例由 World 持有并在 Dispose 时释放。</summary>
        public EntityQuery GetOrCreateEntityQuery(QueryBuilder builder)
            => _entityManager.GetOrCreateEntityQuery(builder);

        /// <summary>当前注册的共享查询数量（诊断用）。</summary>
        public int QueryCacheCount => _entityManager.QueryCacheCount;

        /// <summary>批量创建：一次原型查找、一次等待在飞 Job、一次返回。</summary>
        public Entity[] CreateEntities(int count, params ComponentType[] types)
            => _entityManager.CreateEntities(count, types);

        /// <summary>生成内存分析报告（原生分配/释放/泄漏、Chunk 数、碎片率、slab 占用）。</summary>
        public MemoryReport GetMemoryReport(float thinThresholdPercent = 30f)
            => _entityManager.GetMemoryReport(thinThresholdPercent);

        /// <summary>从 Prefab 模板实体复制创建实例（对齐 Unity Instantiate）。</summary>
        public Entity[] SpawnFrom(Entity prefabEntity, int count)
            => _entityManager.SpawnFrom(prefabEntity, count);

        /// <summary>复制指定实体集合，并重映射集合内部的关系 Target。</summary>
        public Entity[] CloneEntities(ReadOnlySpan<Entity> sources) => _entityManager.CloneEntities(sources);

        /// <summary>打印单个实体的所有组件字段值（用组件元数据，非反射）。</summary>
        public string DumpEntity(Entity entity) => _entityManager.DumpEntity(entity);

        /// <summary>打印指定组件签名 Archetype 的所有实体。</summary>
        public string DumpArchetype(params ComponentType[] types) => _entityManager.DumpArchetype(types);

        /// <summary>打印所有 Archetype 概览。</summary>
        public string DumpWorld() => _entityManager.DumpWorld();

        /// <summary>等待已登记作业后保存支持的组件值及布局标识。</summary>
        public WorldSnapshot TakeSnapshot() => _entityManager.TakeSnapshot();

        /// <summary>在临时状态装载并校验完整快照，再切换当前存储。</summary>
        public void Restore(WorldSnapshot snapshot) => _entityManager.Restore(snapshot);

        /// <summary>实体级查询选择器（双组件），支持链式附加过滤条件（WithRelationship/WithEnabled）。</summary>
        /// <example>
        /// <code>
        /// foreach (var r in world.Query&lt;Position, Velocity&gt;().WithRelationship&lt;ChildOf&gt;(parent))
        /// {
        /// ref var pos = ref r.Comp0; // Position
        /// }
        /// </code>
        /// </example>
        public QuerySelection<T0, T1> Query<T0, T1>() where T0 : struct where T1 : struct
            => new QuerySelection<T0, T1>(_entityManager, new QueryBuilder().WithAll<T0, T1>());

        /// <summary>实体级查询（复用已构建的 <see cref="QueryBuilder"/>，避免热路径分配）。</summary>
        public QuerySelection<T0, T1> Query<T0, T1>(QueryBuilder builder) where T0 : struct where T1 : struct
            => new QuerySelection<T0, T1>(_entityManager, builder);

        /// <summary>单组件查询选择器，支持链式附加过滤条件。</summary>
        /// <example>
        /// <code>
        /// foreach (var result in world.Query&lt;Position&gt;().WithEnabled&lt;ActiveComponent&gt;())
        /// {
        /// // 只处理启用 ActiveComponent 的实体
        /// }
        /// </code>
        /// </example>
        public QuerySelection<T0> Query<T0>() where T0 : struct
            => new QuerySelection<T0>(_entityManager);

        /// <summary>Chunk 级查询（chunk 遍历面，等价原 SystemAPI.QueryChunks）。</summary>
        public ChunkEnumerable<T0, T1> QueryChunks<T0, T1>() where T0 : struct where T1 : struct
            => new ChunkEnumerable<T0, T1>(_entityManager, new QueryBuilder().WithAll<T0, T1>());

        // Event Channel API

        /// <summary>
        /// 注册事件类型。World 初始化时调用，每种事件类型调用一次。
        /// </summary>
        public void RegisterEvent<T>() where T : unmanaged
        {
            if (_disposed) throw new ObjectDisposedException(nameof(World));
            _eventStreams.GetOrAdd(typeof(T), _ => new EventStream<T>());
        }

        /// <summary>生产者：发送事件（零结构变更，写入双缓冲）。返回 false 表示事件流已满、事件被丢弃，
        /// 累计丢弃数见 <see cref="EventStream{T}.OverflowCount"/>。</summary>
        public bool SendEvent<T>(in T evt) where T : unmanaged
        {
            if (_disposed) throw new ObjectDisposedException(nameof(World));
            if (!_eventStreams.TryGetValue(typeof(T), out var obj))
            {
                RegisterEvent<T>();
                obj = _eventStreams[typeof(T)];
            }
            return ((EventStream<T>)obj).SendEvent(evt);
        }

        /// <summary>
        /// 消费者：获取事件流，调用 ReadBuffer() 读取上一帧的所有事件。
        /// </summary>
        public EventStream<T> GetEventStream<T>() where T : unmanaged
        {
            if (_disposed) throw new ObjectDisposedException(nameof(World));
            if (!_eventStreams.TryGetValue(typeof(T), out var obj))
            {
                RegisterEvent<T>();
                obj = _eventStreams[typeof(T)];
            }
            return (EventStream<T>)obj;
        }

        /// <summary>非泛型版本：通过 Type 获取事件流（供 drain 路径使用）。</summary>
        internal IEventStream? GetEventStream(Type eventType)
        {
            if (_eventStreams.TryGetValue(eventType, out var obj))
                return (IEventStream)obj;
            return null;
        }

        /// <summary>
        /// 帧末：交换所有事件流的双缓冲区（SystemRunner.Update 调用）。
        /// </summary>
        public void NextFrameEvents()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(World));
            foreach (var kv in _eventStreams)
                ((IEventStream)kv.Value).NextFrame();
        }

        /// <summary>把上一帧各事件流的 ReadCount 累加进计数器（SystemRunner 帧末调用，供 RunWhen 门控）。</summary>
        internal void RefreshEventCounter(EventCounter counter)
        {
            foreach (var kv in _eventStreams)
                counter.Add(kv.Key, ((IEventStream)kv.Value).ReadCount);
        }

        /// <summary>完成所有在飞的 Native 事件 buffer：等价于 CompleteActiveJobs。
        /// 异步 Schedule 之后必须调用一次，否则事件不会写进 EventStream。</summary>
        public void CompletePendingNativeEvents()
        {
            _entityManager.CompleteActiveJobs();
        }

        public void DrainNativeEvents(Type jobType, IntPtr contextPtr)
        {
            JobSystem.ChunkJobScheduler.DrainAndFreeEventBuffers(contextPtr, this, jobType);
        }

        // Observer 门面（注册表在 EntityManager，见 EntityManager.Observer.cs）

        /// <summary>注册组件生命周期 observer，回调在主线程执行；同一结构变更内自动按类型合并成一次批量回调。</summary>
        public ObserverHandle AddObserver<TComponent>(ObserverEvents events, ObserverCallback<TComponent> callback)
            where TComponent : unmanaged
            => _entityManager.AddObserver(events, callback);

        public void RemoveObserver<TComponent>(ObserverHandle handle) where TComponent : unmanaged
            => _entityManager.RemoveObserver<TComponent>(handle);

        /// <summary>清空某组件类型的所有 observer。</summary>
        public void ClearObservers<TComponent>() where TComponent : unmanaged
            => _entityManager.ClearObservers<TComponent>();

        /// <summary>当前是否正在派发 observer 回调（供结构变更 API 做 reentrancy 检测）。</summary>
        internal bool IsInObserverCallback => EntityManager.ObserverDepth > 0;

        public void Dispose()
        {
            if (_disposed) return;
            _entityManager?.CompleteActiveJobs();
            _disposed = true;
            // 先解除 DefaultWorld：避免 TempAllocator.Reset 的 OnBeforeReset 回调访问已释放的 World
            lock (_defaultLock)
            {
                if (ReferenceEquals(DefaultWorld, this))
                    DefaultWorld = null;
            }
            foreach (var kv in _eventStreams)
            {
                if (kv.Value is IDisposable d) d.Dispose();
            }
            _eventStreams.Clear();
            _entityManager?.Dispose();
        }


    }
}
