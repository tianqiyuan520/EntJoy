using System.Threading;
using EntJoy.Collections;
using EntJoy.JobSystem;

namespace EntJoy.ECS
{
    public partial class World : System.IDisposable
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

        public void Dispose()
        {
            _entityManager?.Dispose();
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
