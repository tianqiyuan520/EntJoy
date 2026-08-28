using EntJoy.Collections;
using EntJoy.JobSystem;
using System;
using System.Collections.Generic;

namespace EntJoy.ECS
{
    public partial class World : IDisposable
    {
        private static readonly object _defaultLock = new();
        private static volatile World _defaultWorld;
        public static World DefaultWorld
        {
            get => _defaultWorld;
            set => _defaultWorld = value;
        }

        public string Name { get; private set; }
        public long CurrentFrame { get; set; }
        public EntityManager _entityManager;
        public ref EntityManager EntityManager => ref _entityManager;

        // ─── Event Channel ───
        private readonly Dictionary<Type, object> _eventStreams = new();


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

        public EntityQuery CreateEntityQuery(QueryBuilder builder)
        {
            return new EntityQuery(this, builder);
        }

        /// <summary>
        /// 批量创建实体。一次 Archetype 查找、一次 CompleteActiveJobs、一次返回。
        /// 比逐个 NewEntity 快 N 倍。
        /// </summary>
        public Entity[] CreateEntities(int count, params ComponentType[] types)
        {
            return _entityManager.CreateEntities(count, types);
        }

        /// <summary>实体级查询（chunk 序 struct query，密集 OOD 访问面）。</summary>
        public QueryEnumerable<T0, T1> Query<T0, T1>() where T0 : struct where T1 : struct
            => new QueryEnumerable<T0, T1>(_entityManager, new QueryBuilder().WithAll<T0, T1>());

        /// <summary>实体级查询（复用已构建的 <see cref="QueryBuilder"/>，避免热路径分配）。</summary>
        public QueryEnumerable<T0, T1> Query<T0, T1>(QueryBuilder builder) where T0 : struct where T1 : struct
            => new QueryEnumerable<T0, T1>(_entityManager, builder);

        /// <summary>单组件查询选择器，支持链式附加过滤条件。</summary>
        /// <example>
        /// <code>
        /// foreach (var result in world.Query&lt;Position&gt;().WithEnabled&lt;ActiveComponent&gt;())
        /// {
        ///     // 只处理启用 ActiveComponent 的实体
        /// }
        /// </code>
        /// </example>
        public QuerySelection<T0> Query<T0>() where T0 : struct
            => new QuerySelection<T0>(_entityManager);

        // ─── Event Channel API ───

        /// <summary>
        /// 注册事件类型。World 初始化时调用，每种事件类型调用一次。
        /// </summary>
        public void RegisterEvent<T>() where T : unmanaged
        {
            var type = typeof(T);
            if (!_eventStreams.ContainsKey(type))
                _eventStreams[type] = new EventStream<T>();
        }

        /// <summary>
        /// 生产者：发送事件（零结构变更，写入双缓冲）。
        /// </summary>
        public void SendEvent<T>(in T evt) where T : unmanaged
        {
            if (!_eventStreams.TryGetValue(typeof(T), out var obj))
            {
                RegisterEvent<T>();
                obj = _eventStreams[typeof(T)];
            }
            ((EventStream<T>)obj).SendEvent(evt);
        }

        /// <summary>
        /// 消费者：获取事件流，调用 ReadBuffer() 读取上一帧的所有事件。
        /// </summary>
        public EventStream<T> GetEventStream<T>() where T : unmanaged
        {
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
            foreach (var kv in _eventStreams)
                ((IEventStream)kv.Value).NextFrame();
        }

        /// <summary>
        /// Drain Native EventBuffer → EventStream（Run/Complete 后调用）。
        /// </summary>
        /// <summary>
        /// 完成所有 pending 的 Native 事件 buffer drain（异步 Schedule 后必须调用）。
        /// 等价于内部的 EntityManager.CompleteActiveJobs()。
        /// </summary>
        public void CompletePendingNativeEvents()
        {
            _entityManager.CompleteActiveJobs();
        }

        public void DrainNativeEvents(Type jobType, IntPtr contextPtr)
        {
            JobSystem.ChunkJobScheduler.DrainAndFreeEventBuffers(contextPtr, this, jobType);
        }

        // ─── Observer 门面（注册表在 EntityManager，见 EntityManager.Observer.cs） ───

        /// <summary>注册组件生命周期 observer。回调在主线程执行（立即或 ECB Playback 派发）。</summary>
        public ObserverHandle AddObserver<TComponent>(ObserverEvents events, Action<ComponentEvent<TComponent>> callback)
            where TComponent : unmanaged
            => _entityManager.AddObserver(events, callback);

        /// <summary>移除指定句柄的 observer。</summary>
        public void RemoveObserver<TComponent>(ObserverHandle handle) where TComponent : unmanaged
            => _entityManager.RemoveObserver<TComponent>(handle);

        /// <summary>清空某组件类型的所有 observer。</summary>
        public void ClearObservers<TComponent>() where TComponent : unmanaged
            => _entityManager.ClearObservers<TComponent>();

        /// <summary>当前是否正在派发 observer 回调（供结构变更 API 做 reentrancy 检测）。</summary>
        internal bool IsInObserverCallback => EntityManager.ObserverDepth > 0;

        public void Dispose()
        {
            foreach (var kv in _eventStreams)
            {
                if (kv.Value is IDisposable d) d.Dispose();
            }
            _eventStreams.Clear();
            _entityManager?.Dispose();   // Dispose 内清空 observer 注册表
            lock (_defaultLock)
            {
                if (ReferenceEquals(DefaultWorld, this))
                {
                    DefaultWorld = null;
                }
            }
        }


    }
}
