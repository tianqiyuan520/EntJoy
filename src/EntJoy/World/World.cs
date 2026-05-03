namespace EntJoy
{
    public partial class World : System.IDisposable
    {
        public static World DefaultWorld = null;

        public string Name { get; private set; }
        public EntityManager _entityManager;
        public ref EntityManager EntityManager => ref _entityManager;


        public World(string worldName = "Default")
        {
            Name = worldName;
            _entityManager = new EntityManager();

            if (DefaultWorld == null)
            {
                DefaultWorld = this;
            }
        }

        public void Dispose()
        {
            _entityManager?.Dispose();
            if (ReferenceEquals(DefaultWorld, this))
            {
                DefaultWorld = null;
            }
        }


    }
}
