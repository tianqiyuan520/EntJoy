//using System.Collections.Generic;
//using System.Linq;
//using System.Runtime.CompilerServices;

//namespace EntJoy
//{
//    public class EntityQuery
//    {
//        private readonly World _world;
//        private readonly QueryBuilder _builder;
//        private List<Archetype> _matchingArchetypes;
//        private List<Chunk> _allChunks; // 可选的扁平化 Chunk 列表

//        public EntityQuery(World world, QueryBuilder builder)
//        {
//            _world = world;
//            _builder = builder;
//            Refresh();
//        }

//        // 重新计算匹配（当 World 结构变化时调用）
//        public void Refresh()
//        {
//            _matchingArchetypes = new List<Archetype>();
//            for (int i = 0; i < _world.ArchetypeCount; i++)
//            {
//                var arch = _world.GetArchetype(i);
//                if (arch != null && arch.IsMatch(_builder))
//                    _matchingArchetypes.Add(arch);
//            }
//            // 可同时缓存所有 Chunk
//            _allChunks = _matchingArchetypes.SelectMany(a => a.GetChunks()).ToList();
//        }

//        public IReadOnlyList<Archetype> MatchingArchetypes => _matchingArchetypes;
//        public IReadOnlyList<Chunk> GetAllChunks() => _allChunks;

//        public int CalculateEntityCount()
//        {
//            int total = 0;
//            foreach (var arch in _matchingArchetypes)
//                foreach (var chunk in arch.GetChunks())
//                    total += chunk.EntityCount;
//            return total;
//        }

//        public NativeArray<T> ToComponentDataArray<T>(Allocator allocator) where T : struct
//        {
//            int total = CalculateEntityCount();
//            var result = new NativeArray<T>(total, allocator);
//            int dstIndex = 0;
//            foreach (var arch in _matchingArchetypes)
//            {
//                int compIdx = arch.GetComponentTypeIndex<T>();
//                foreach (var chunk in arch.GetChunks())
//                {
//                    int count = chunk.EntityCount;
//                    if (count == 0) continue;
//                    var srcPtr = (byte*)chunk.GetComponentArrayPointer(compIdx).ToPointer();
//                    var dstPtr = (byte*)result.GetUnsafePtr() + dstIndex * Unsafe.SizeOf<T>();
//                    Unsafe.CopyBlock(dstPtr, srcPtr, (uint)(count * Unsafe.SizeOf<T>()));
//                    dstIndex += count;
//                }
//            }
//            return result;
//        }
//    }
//}
