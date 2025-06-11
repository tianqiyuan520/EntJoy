namespace EntJoy  // EntJoy命名空间
{
    public partial class World  // World类部分定义
    {
        // 支持任意数量组件的通用查询方法
        //public void Query(in QueryBuilder builder, ForeachWithComponents lambda, params Type[] componentTypes)
        //{
        //    if (componentTypes.Length == 0)
        //    {
        //        GD.PrintErr("Query requires at least one component type");
        //        return;
        //    }

        //    for (int i = 0; i < archetypeCount; i++)
        //    {
        //        var arch = allArchetypes[i];
        //        if (arch == null || !arch.IsMatch(builder))
        //            continue;

        //        // 获取所有需要的组件数组
        //        object[] componentArrays = new object[componentTypes.Length];
        //        for (int j = 0; j < componentTypes.Length; j++)
        //        {
        //            // 使用反射获取查询序列
        //            var compType = ComponentTypeManager.GetComponentType(componentTypes[j]);
        //            GD.Print(typeof(Archetype).GetMethod("GetQuery").GetGenericArguments());
        //            GD.Print(typeof(Archetype).GetMethod("GetQuery").GetGenericMethodDefinition());
        //            var method = typeof(Archetype).GetMethod("GetQuery").MakeGenericMethod(componentTypes[j]);
        //            componentArrays[j] = method.Invoke(arch, null);
        //        }

        //        // 遍历所有实体
        //        for (int entityIndex = 0; entityIndex < arch.EntityCount; entityIndex++)
        //        {
        //            // 获取实体引用
        //            ref var entity = ref arch.GetEntity(entityIndex);

        //            // 获取所有组件值
        //            IComponent[] componentValues = new IComponent[componentTypes.Length];
        //            for (int j = 0; j < componentTypes.Length; j++)
        //            {
        //                // 从组件数组中获取值
        //                var arrayType = componentArrays[j].GetType();
        //                var getRefMethod = arrayType.GetMethod("GetRef");
        //                componentValues[j] = (IComponent)getRefMethod.Invoke(componentArrays[j], new object[] { entityIndex });
        //            }

        //            // 调用委托
        //            lambda(ref entity, componentValues);
        //        }
        //    }
        //}

        /// <summary>
        /// 添加SIMD查询方法
        /// </summary>
        public unsafe void QuerySIMD<T1, T2>(in QueryBuilder builder,in IForeachWithSIMD<T1,T2> simdAction)
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
                    simdAction.Execute(ref posArray, ref velArray,count);
                }
            }
        }


        public void Query<T0>(in QueryBuilder builder, ForeachWith<T0> lambda)
            where T0 : struct
        {
            unchecked
            {
                for (int i = 0; i < archetypeCount; i++)
                {
                    var arch = allArchetypes[i];
                    if (arch == null || !arch.IsMatch(builder))
                    {
                        continue;
                    }
                    int entityIndex = 0;  // 实体索引
                    var EntityQuery = arch.GetEntities();
                    var archQuery0 = arch.GetQueryStructArray<T0>().GetAllData();
                    int len = arch.EntityCount;
                    int limitCount = builder.LimitCount;
                    for (int j = 0; j < len; j++)
                    {
                        if (limitCount != -1 && entityIndex > limitCount) break;
                        ref var t0 = ref EntityQuery[j];
                        ref var t1 = ref archQuery0[j];
                        lambda(ref t0, ref t1);  // 执行回调
                        entityIndex++;
                    }
                }
            }
        }

        public void Query<T0>(in QueryBuilder builder, in ISystem<T0> lambda)
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
                    int entityIndex = 0;  // 实体索引
                    int len = arch.EntityCount;
                    int limitCount = builder.LimitCount;
                    for (int j = 0; j < len; j++)
                    {
                        if (limitCount != -1 && entityIndex > limitCount) break;
                        {
                            ref var t0 = ref EntityQuery[j];
                            ref var t1 = ref archQuery0[j];
                            lambda.Execute(ref t0, ref t1);  // 执行回调
                            entityIndex++;
                        }
                    }
                }
            }

        }
        public void Query<T0, T1>(in QueryBuilder builder,in ISystem<T0, T1> lambda)
            where T0 : struct
            where T1 : struct
        {
            unchecked
            {
                for (int i = 0; i < archetypeCount; i++)  // 遍历所有原型
                {
                    var arch = allArchetypes[i];
                    if (arch == null || !arch.IsMatch(builder)) continue;

                    var EntityQuery = arch.GetEntities();
                    var archQuery0 = arch.GetQueryStructArray<T0>().GetAllData();
                    var archQuery1 = arch.GetQueryStructArray<T1>().GetAllData();
                    int entityIndex = 0;  // 实体索引
                    int len = arch.EntityCount;
                    int limitCount = builder.LimitCount;
                    for (int j = 0; j < len; j++)
                    {
                        if (limitCount != -1 && entityIndex > limitCount) break;
                        {
                            ref var t0 = ref EntityQuery[j];
                            ref var t1 = ref archQuery0[j];
                            ref var t2 = ref archQuery1[j];
                            lambda.Execute(ref t0, ref t1, ref t2);  // 执行回调
                            entityIndex++;
                        }
                    }
                }
            }

        }
    }
}