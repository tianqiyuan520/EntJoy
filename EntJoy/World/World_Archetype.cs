namespace EntJoy
{
    public partial class World  // World类部分定义
    {
        private readonly Dictionary<int, Archetype> archetypeMap;  // 原型映射表
        private Archetype[] allArchetypes;  // 所有原型数组
        private int archetypeCount;  // 原型计数器

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
    }
}
