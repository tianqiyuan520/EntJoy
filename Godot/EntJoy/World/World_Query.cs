using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace EntJoy  // EntJoy命名空间
{
    public partial class World  // World类部分定义
    {

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void Query<T0>(QueryBuilder builder, ISystem<T0> system)
            where T0 : struct
        {
            int entityCounter = 0; // 记录查询到的实体数量
            int limitCount = builder.LimitCount;

            for (int i = 0; i < archetypeCount; i++)
            {
                var archetype = allArchetypes[i];
                if (archetype != null && archetype.IsMatch(builder))
                {
                    int t0Index = archetype.GetComponentTypeIndex<T0>();
                    var chunks = archetype.GetChunks();
                    for (int j = 0; j < chunks.Count; j++)
                    {
                        var chunk = chunks[j];
                        int count = chunk.EntityCount;
                        if (count == 0) continue;
                        system.GetArchetypeID(i);
                        system.GetChunkID(j);

                        Entity* entities = (Entity*)chunk.GetEntityPointer().ToPointer();
                        T0* components = (T0*)chunk.GetComponentArrayPointer(t0Index).ToPointer();
                        {
                            system._execute(entities, components, count, limitCount - entityCounter);
                        }
                        entityCounter += count;
                        if (limitCount != -1 && entityCounter >= limitCount) break;
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void Query<T0, T1>(QueryBuilder builder, ISystem<T0, T1> system)
            where T0 : struct
            where T1 : struct
        {
            int entityCounter = 0; // 记录查询到的实体数量
            int limitCount = builder.LimitCount;
            unchecked
            {

                for (int i = 0; i < archetypeCount; i++)
                {
                    var archetype = allArchetypes[i];
                    if (archetype != null && archetype.IsMatch(builder))
                    {

                        int t0Index = archetype.GetComponentTypeIndex<T0>();
                        int t1Index = archetype.GetComponentTypeIndex<T1>();
                        var chunks = archetype.GetChunks();
                        for (int j = 0; j < chunks.Count; j++)
                        {
                            var chunk = chunks[j];
                            int count = chunk.EntityCount;
                            if (count == 0) continue;

                            system.GetArchetypeID(i);
                            system.GetChunkID(j);

                            Entity* entities = (Entity*)chunk.GetEntityPointer().ToPointer();
                            T0* components0 = (T0*)chunk.GetComponentArrayPointer(t0Index).ToPointer();
                            T1* components1 = (T1*)chunk.GetComponentArrayPointer(t1Index).ToPointer();
                            {
                                system._execute(entities, components0, components1, count, limitCount - entityCounter);
                            }

                            entityCounter += count;
                            if (limitCount != -1 && entityCounter >= limitCount) break;
                        }
                    }
                }
            }

        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void MultiQuery<T0>(QueryBuilder builder, ISystem<T0> system)
            where T0 : struct
        {
            static void RunSystem(Chunk chunk, int t0Index, ISystem<T0> system, int LimitCount, int ArchID, int ChunkID)
            {
                int count = chunk.EntityCount;
                system.GetArchetypeID(ArchID);
                system.GetChunkID(ChunkID);
                Entity* entities = (Entity*)chunk.GetEntityPointer().ToPointer();
                T0* components0 = (T0*)chunk.GetComponentArrayPointer(t0Index).ToPointer();
                system._execute(entities, components0, count, LimitCount);
            }

            unchecked
            {
                int entityCounter = 0;
                int limitCount = builder.LimitCount;

                for (int i = 0; i < archetypeCount; i++)
                {
                    var archetype = allArchetypes[i];
                    if (archetype != null && archetype.IsMatch(builder))
                    {
                        int t0Index = archetype.GetComponentTypeIndex<T0>();
                        List<ValueTask> tasks = new();

                        var chunks = archetype.GetChunks();
                        for (int j = 0; j < chunks.Count; j++)
                        {
                            var chunk = chunks[j];
                            int count = chunk.EntityCount;
                            if (count == 0) continue;
                            int spareCount = limitCount - entityCounter;

                            int archetypeID = i;
                            int chunkID = j;
                            Task task = Task.Run(() =>
                            {
                                RunSystem(chunk, t0Index, system, spareCount, archetypeID, chunkID);
                            }
                            );

                            tasks.Add(new ValueTask(task));
                            entityCounter += count;
                            if (limitCount != -1 && entityCounter >= limitCount) break;

                        }
                        //Task.WaitAll(tasks.ToArray());
                        Task.WhenAll(tasks.Select(v => v.AsTask()));
                    }
                }
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void MultiQuery<T0, T1>(QueryBuilder builder, ISystem<T0, T1> system)
            where T0 : struct
            where T1 : struct
        {
            static void RunSystem(Chunk chunk, int t0Index, int t1Index, ISystem<T0, T1> system, int LimitCount, int ArchID, int ChunkID)
            {
                int count = chunk.EntityCount;
                system.GetArchetypeID(ArchID);
                system.GetChunkID(ChunkID);
                fixed (Entity* entities = &chunk.GetEntity(0))
                fixed (T0* components0 = &chunk.GetComponent<T0>(0, t0Index))
                fixed (T1* components1 = &chunk.GetComponent<T1>(0, t1Index))

                {
                    system._execute(entities, components0, components1, count, LimitCount);
                }
            }

            unchecked
            {
                int entityCounter = 0;
                int limitCount = builder.LimitCount;

                for (int i = 0; i < archetypeCount; i++)
                {
                    var archetype = allArchetypes[i];
                    if (archetype != null && archetype.IsMatch(builder))
                    {
                        int t0Index = archetype.GetComponentTypeIndex<T0>();
                        int t1Index = archetype.GetComponentTypeIndex<T1>();

                        List<ValueTask> tasks = new();

                        var chunks = archetype.GetChunks();
                        for (int j = 0; j < chunks.Count; j++)
                        {
                            var chunk = chunks[j];
                            int count = chunk.EntityCount;
                            if (count == 0) continue;
                            int spareCount = limitCount - entityCounter;

                            int archetypeID = i;
                            int chunkID = j;
                            Task task = Task.Run(() =>
                            {
                                RunSystem(chunk, t0Index, t1Index, system, spareCount, archetypeID, chunkID);
                            }
                            );

                            tasks.Add(new ValueTask(task));
                            entityCounter += count;
                            if (limitCount != -1 && entityCounter >= limitCount) break;

                        }
                        Task.WhenAll(tasks.Select(v => v.AsTask())).Wait();

                    }
                }
            }
        }




        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        //private IEnumerable<Archetype> GetMatchingArchetypes(QueryBuilder builder)
        //{
        //    for (int i = 0; i < archetypeCount; i++)
        //    {
        //        var arch = allArchetypes[i];
        //        if (arch != null && arch.IsMatch(builder))
        //        {
        //            yield return arch;
        //        }
        //    }
        //}
    }

}
