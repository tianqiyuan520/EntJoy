namespace EntJoy
{
    public partial class World
    {
        public void Query<T>(in QueryBuilder builder, ForeachWith<T> lambda)
            where T : struct
        {
            for (int i = 0; i < archetypeCnt; i++)
            {
                var arch = allArchetypes[i];
                if (!arch.IsMatch(builder))
                {
                    continue;
                }
                
                var archQuery = arch.GetQuery<T>();
                int entityIndex = 0;
                foreach (ref var t in archQuery)
                {
                    lambda(arch.GetEntity(entityIndex++), ref t);
                }
            }
        }
    }
}