using EntJoy;
using Godot;
using System;

namespace EntJoy  // EntJoy命名空间
{
    public partial class World  // World类部分定义
    {
        public void Query<T>(in QueryBuilder builder, ForeachWith<T> lambda)
            where T : struct
        {
            for (int i = 0; i < archetypeCount; i++) 
            {
                GD.Print(allArchetypes[i]?.GetComponentTypeString());
                var arch = allArchetypes[i];
                if (arch == null||!arch.IsMatch(builder))
                {
                    continue;
                }
                GD.Print("该原型符合");
                //该原型通过,获取该原型的对应组件的查询序列
                var archQuery = arch.GetQuery<T>();
                int entityIndex = 0;  // 实体索引
                foreach (ref var t in archQuery)  // 遍历查询结果
                {
                    lambda(arch.GetEntity(entityIndex++), ref t);  // 执行回调
                }
            }
        }

        public void Query<T0, T1>(in QueryBuilder builder, ForeachWith<T0, T1> lambda)
            where T0 : struct
            where T1 : struct
        {
            for (int i = 0; i < archetypeCount; i++)  // 遍历所有原型
            {
                var arch = allArchetypes[i];
                if (!arch.IsMatch(builder)) continue;

                var archQuery0 = arch.GetQuery<T0>();
                var archQuery1 = arch.GetQuery<T1>();
                int entityIndex = 0;  // 实体索引
                for (int j = 0; j < archQuery0.Count; j++)
                {
                    ref var t1 = ref archQuery0._array.GetRef(j);
                    ref var t2 = ref archQuery1._array.GetRef(j);

                    lambda(arch.GetEntity(entityIndex++), ref t1, ref t2);  // 执行回调
                }
            }
        }
    }
}