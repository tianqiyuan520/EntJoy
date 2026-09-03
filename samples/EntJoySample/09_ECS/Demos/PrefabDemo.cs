using System;
using EntJoy.Collections;
using EntJoy.ECS;

namespace EntJoySample.ECS
{
    /// <summary>
    /// Prefab 实例化示例：模板标记 + SpawnFrom 复制 + 默认排除 + 显式包含 + 独立副本验证。
    /// </summary>
    public static class PrefabDemo
    {
        public struct DemoNativeBuffer : IComponentData, IDisposable
        {
            public NativeArray<int> Data;
            public void Dispose() => Data.Dispose();
        }

        public static void Run()
        {
            Console.WriteLine("=== Prefab 实例化 Demo ===\n");
            var world = new World("PrefabDemo");

            // 1. 创建 prefab（Position + Velocity + Prefab 标记）
            var prefab = world.EntityManager.NewEntity(typeof(Position), typeof(Velocity), typeof(Prefab));
            world.EntityManager.Set(prefab, new Position { X = 1, Y = 2 });

            // 2. SpawnFrom 实例化 100 个
            var instances = world.SpawnFrom(prefab, 100);
            Console.WriteLine($"实例数: {instances.Length} (期望 100)");

            // 3. 实例值复制正确
            bool valuesOk = true;
            foreach (var e in instances)
            {
                ref var pos = ref world.EntityManager.GetComponent<Position>(e);
                if (pos.X != 1 || pos.Y != 2) { valuesOk = false; break; }
            }
            Console.WriteLine($"实例 Position 值复制正确: {valuesOk}");

            // 4. 默认排除：Query<Position,Velocity> 不含 prefab，含 100 实例
            int normalCount = 0;
            foreach (var _ in world.Query<Position, Velocity>()) normalCount++;
            Console.WriteLine($"Query<Position,Velocity> 匹配: {normalCount} (期望 100，prefab 被排除)");

            // 5. 显式包含：WithAll<Prefab> 匹配到 prefab
            int prefabCount = 0;
            var prefabQuery = world.Query<Position, Velocity>(
                new QueryBuilder().WithAll<Position, Velocity>().WithAll<Prefab>());
            foreach (var _ in prefabQuery) prefabCount++;
            Console.WriteLine($"Query<Position,Velocity>.WithAll<Prefab> 匹配: {prefabCount} (期望 1)");

            // 6. 独立副本：改实例不影响 prefab 和其他实例
            ref var inst0 = ref world.EntityManager.GetComponent<Position>(instances[0]);
            inst0.X = 99;
            ref var prefabPos = ref world.EntityManager.GetComponent<Position>(prefab);
            ref var inst1 = ref world.EntityManager.GetComponent<Position>(instances[1]);
            bool independent = prefabPos.X == 1 && inst1.X == 1 && inst0.X == 99;
            Console.WriteLine($"独立副本（改实例不影响 prefab/其他实例）: {independent}");

            // 7. Disposable 组件进 prefab → SpawnFrom 抛异常
            bool disposableRejected = false;
            var badPrefab = world.EntityManager.NewEntity(typeof(DemoNativeBuffer), typeof(Prefab));
            world.EntityManager.GetComponent<DemoNativeBuffer>(badPrefab).Data = new NativeArray<int>(4, Allocator.Persistent);
            try
            {
                world.SpawnFrom(badPrefab, 1);
            }
            catch (InvalidOperationException)
            {
                disposableRejected = true;
            }
            Console.WriteLine($"Disposable 组件进 prefab 被拒绝: {disposableRejected}");

            world.Dispose();
            Console.WriteLine("\n=== Prefab 实例化 Demo Complete ===\n");
        }
    }
}
