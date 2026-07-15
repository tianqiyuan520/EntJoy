using System.Threading;

namespace EntJoy
{
    public partial class World : System.IDisposable
    {
        private static readonly object _defaultLock = new();
        public static World DefaultWorld = null;

        public string Name { get; private set; }
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
                }
            }
        }

        public EntityQuery CreateEntityQuery(QueryBuilder builder)
        {
            return new EntityQuery(this, builder);
        }

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
