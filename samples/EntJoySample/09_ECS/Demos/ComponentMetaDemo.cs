using System;
using System.Runtime.CompilerServices;
using System.Text;
using EntJoy.ECS;

namespace EntJoySample.ECS
{
    /// <summary>
    /// 组件元数据示例：验证 ComponentMetaRegistry 的字段元数据（含嵌套 struct 递归展开），
    /// 并用元数据（非反射）打印组件字段值。
    /// </summary>
    public static class ComponentMetaDemo
    {
        public struct Vec2 { public float X; public float Y; }

        public struct DemoComponent : IComponentData
        {
            public Vec2 Position;   // 嵌套 struct → 展开为 Position.X / Position.Y
            public int Health;
            public float Speed;
            public bool Active;
        }

        public static void Run()
        {
            Console.WriteLine("=== 组件元数据 Demo ===\n");

            var meta = ComponentMetaRegistry.Get<DemoComponent>();
            Console.WriteLine($"组件: {meta.TypeName}, 大小={meta.Size}, 字段数={meta.Fields.Length}");
            foreach (var f in meta.Fields)
                Console.WriteLine($"  {f.Name}: offset={f.Offset}, size={f.Size}, kind={f.Kind}");

            bool metaOk = meta.TypeName == nameof(DemoComponent)
                && meta.Fields.Length == 5
                && meta.Fields[0].Name == "Position.X"
                && meta.Fields[1].Name == "Position.Y"
                && meta.Fields[2].Name == "Health"
                && meta.Fields[3].Name == "Speed"
                && meta.Fields[4].Name == "Active";
            Console.WriteLine($"元数据正确: {metaOk}");

            // 用元数据打印字段值（非反射）
            var world = new World("MetaDemo");
            var e = world.EntityManager.NewEntity(typeof(DemoComponent));
            world.EntityManager.GetComponent<DemoComponent>(e) = new DemoComponent
            {
                Position = new Vec2 { X = 1.5f, Y = 2.5f },
                Health = 100,
                Speed = 3.25f,
                Active = true,
            };
            ref var comp = ref world.EntityManager.GetComponent<DemoComponent>(e);
            string dumped = DumpComponent(ref comp, meta);
            Console.WriteLine($"Dump: {dumped}");
            Console.WriteLine($"Dump 正确: {dumped.Contains("Position.X=1.5") && dumped.Contains("Health=100") && dumped.Contains("Active=True")}");

            world.Dispose();
            Console.WriteLine("\n=== 组件元数据 Demo Complete ===\n");
        }

        private static unsafe string DumpComponent<T>(ref T value, ComponentMeta meta) where T : struct
        {
            var sb = new StringBuilder();
            byte* basePtr = (byte*)Unsafe.AsPointer(ref value);
            foreach (var f in meta.Fields)
            {
                void* fieldPtr = basePtr + f.Offset;
                string v = f.Kind switch
                {
                    FieldKind.Int32 => (*(int*)fieldPtr).ToString(),
                    FieldKind.Float32 => (*(float*)fieldPtr).ToString(),
                    FieldKind.Float64 => (*(double*)fieldPtr).ToString(),
                    FieldKind.Bool => (*(bool*)fieldPtr).ToString(),
                    FieldKind.Int64 => (*(long*)fieldPtr).ToString(),
                    _ => "?",
                };
                sb.Append($"{f.Name}={v} ");
            }
            return sb.ToString().TrimEnd();
        }
    }
}
