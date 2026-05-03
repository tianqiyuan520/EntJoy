namespace EntJoy
{
    public static class SystemAPI
    {
        public static World CurrentWorld => World.DefaultWorld;


        public static QueryEnumerable<T0, T1> Query<T0, T1>() where T0 : struct where T1 : struct
            => new QueryEnumerable<T0, T1>();


        public static ChunkEnumerable<T0, T1> QueryChunks<T0, T1>()
       where T0 : struct where T1 : struct
        {
            return new ChunkEnumerable<T0, T1>(CurrentWorld.EntityManager, new QueryBuilder().WithAll<T0, T1>());
        }
    }
}
