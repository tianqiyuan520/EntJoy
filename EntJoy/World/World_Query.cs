using Godot;

namespace EntJoy  // EntJoy命名空间
{
    public partial class World  // World类部分定义
    {
        /// <summary>
        /// 添加SIMD查询方法
        /// </summary>
        public unsafe void QuerySIMD<T1, T2>(in QueryBuilder builder, in IForeachWithSIMD<T1, T2> simdAction)
            where T1 : struct
            where T2 : struct
        {
            unchecked
            {
                for (int i = 0; i < archetypeCount; i++)
                {
                    var arch = allArchetypes[i];
                    if (arch == null || !arch.IsMatch(builder)) continue;

                    // 获取组件数组指针
                    var posArray = arch.GetComponentArrayPointer<T1>();
                    var velArray = arch.GetComponentArrayPointer<T2>();
                    int count = arch.EntityCount;
                    //GD.Print(posArray," ", velArray);
                    // 执行SIMD操作
                    simdAction.Execute(ref posArray, ref velArray, count);
                }
            }
        }


        public unsafe void Query<T0>(in QueryBuilder builder, in ISystem<T0> lambda)
            where T0 : struct
        {
            unchecked
            {
                for (int i = 0; i < archetypeCount; i++)  // 遍历所有原型
                {
                    var arch = allArchetypes[i];
                    if (arch == null || !arch.IsMatch(builder)) continue;

                    Entity* EntityQuery = (Entity*)arch.GetEntityArrayAddress().ToPointer();
                    T0* archQuery0 = (T0*)arch.GetComponentArrayPointer<T0>().ToPointer();

                    int len = arch.EntityCount;
                    lambda._execute(ref EntityQuery, ref archQuery0, len);  // 执行回调
                }
            }
        }
        public unsafe void Query<T0, T1>(in QueryBuilder builder, in ISystem<T0, T1> lambda)
            where T0 : struct
            where T1 : struct
        {
            unchecked
            {
                for (int i = 0; i < archetypeCount; i++)  // 遍历所有原型
                {
                    var arch = allArchetypes[i];
                    if (arch == null || !arch.IsMatch(builder)) continue;

                    Entity* EntityQuery = (Entity*)arch.GetEntityArrayAddress().ToPointer();
                    T0* archQuery0 = (T0*)arch.GetComponentArrayPointer<T0>().ToPointer();
                    T1* archQuery1 = (T1*)arch.GetComponentArrayPointer<T1>().ToPointer();

                    // int entityIndex = 0;  // 实体索引
                    int len = arch.EntityCount;
                    // int limitCount = builder.LimitCount;

                    //ref var t0 = ref EntityQuery[0];
                    lambda._execute(ref EntityQuery, ref archQuery0, ref archQuery1, len);  

                    //for (int j = 0; j < len; j++)
                    //{
                    //    if (limitCount != -1 && entityIndex > limitCount) break;
                    //    {
                    //        ref var t0 = ref EntityQuery[j];
                    //        ref var t1 = ref archQuery0[j];
                    //        ref var t2 = ref archQuery1[j];

                    //        //var t2 = *(archQuery1 + j);
                    //        lambda.Execute(ref t0, ref t1, ref t2);  // 执行回调
                    //        entityIndex++;
                    //    }
                    //}
                }
            }

        }



        public void Query<T0>(in QueryBuilder builder)
            where T0 : struct
        {
            unchecked
            {
                for (int i = 0; i < archetypeCount; i++)  // 遍历所有原型
                {
                    var arch = allArchetypes[i];
                    if (arch == null || !arch.IsMatch(builder)) continue;

                    var EntityQuery = arch.GetEntities();
                    var archQuery0 = arch.GetQueryStructArray<T0>().GetAllData();
                    int len = arch.EntityCount;
                    int limitCount = builder.LimitCount;
                    
                }
            }

        }
    }
}