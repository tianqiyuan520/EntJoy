using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Threading.Tasks;

namespace EntJoy  // EntJoy命名空间
{
    public partial class World  // World类部分定义
    {

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void Query<T0>(QueryBuilder builder, ISystem<T0> system)
            where T0 : struct
        {
            foreach (var archetype in GetMatchingArchetypes(builder))
            {
                foreach (var chunk in archetype.GetChunks())
                {
                    int count = chunk.EntityCount;
                    if (count == 0) continue;

                    fixed (Entity* entities = &chunk.GetEntity(0))
                    fixed (T0* components = &chunk.GetComponent<T0>(0, archetype.GetComponentTypeIndex<T0>()))
                    {
                        system._execute(entities, components, count);
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void Query<T0, T1>(QueryBuilder builder,ISystem<T0, T1> system)
            where T0 : struct
            where T1 : struct
        {
            unchecked
            {
                foreach (var archetype in GetMatchingArchetypes(builder))
                {
                    int t0Index = archetype.GetComponentTypeIndex<T0>();
                    int t1Index = archetype.GetComponentTypeIndex<T1>();

                    foreach (var chunk in archetype.GetChunks())
                    {
                        int count = chunk.EntityCount;
                        if (count == 0) continue;

                        fixed (Entity* entities = &chunk.GetEntity(0))
                        fixed (T0* components0 = &chunk.GetComponent<T0>(0, t0Index))
                        fixed (T1* components1 = &chunk.GetComponent<T1>(0, t1Index))
                        {
                            system._execute(entities, components0, components1, count);
                        }
                    }
                }
            }

        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        //public unsafe void QuerySIMD<T0, T1>(in QueryBuilder builder, in IForeachWithSIMD<T0, T1> system)
        //    where T0 : struct
        //    where T1 : struct
        //{
        //    foreach (var archetype in GetMatchingArchetypes(builder))
        //    {
        //        int t0Index = archetype.GetComponentTypeIndex<T0>();
        //        int t1Index = archetype.GetComponentTypeIndex<T1>();

        //        foreach (var chunk in archetype.GetChunks())
        //        {
        //            int count = chunk.EntityCount;
        //            if (count == 0) continue;

        //            IntPtr t0Ptr = chunk.GetComponentArrayPointer(t0Index);
        //            IntPtr t1Ptr = chunk.GetComponentArrayPointer(t1Index);
        //            system.Execute(ref t0Ptr, ref t1Ptr, count);
        //        }
        //    }
        //}
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private IEnumerable<Archetype> GetMatchingArchetypes(QueryBuilder builder)
        {
            for (int i = 0; i < archetypeCount; i++)
            {
                var arch = allArchetypes[i];
                if (arch != null && arch.IsMatch(builder))
                {
                    yield return arch;
                }
            }
        }
    }

}
