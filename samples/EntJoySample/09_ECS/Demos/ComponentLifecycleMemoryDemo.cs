using System;
using EntJoy.Collections;
using EntJoy.ECS;

namespace EntJoySample.ECS
{
    /// <summary>
    /// 组件持有 NativeCollection（NativeArray）时，结构变更（DestroyEntity / RemoveComponent / World.Dispose）
    /// 的内存行为对比：无 hook（纯位拷贝）vs 有 hook（IDisposable + move 转移所有权）。
    ///
    /// 用 PersistentAllocator.GetStats() 的 Allocs/Frees/Foreign 差量作为证据：
    ///   - 无 hook：Allocs > Frees（泄漏），旧副本指针被 swap-pop 覆盖丢弃、无人释放；
    ///   - 有 hook：Allocs == Frees（平衡），move 零分配（指针转移）+ Dispose 正确释放。
    /// </summary>
    public static class ComponentLifecycleMemoryDemo
    {
        /// <summary>无 hook：普通 blittable 组件（纯位拷贝，与 Position/Health 同一处理路径）。</summary>
        public struct NoHookBuffer : IComponentData
        {
            public NativeArray<int> Data;
        }

        /// <summary>有 hook：实现 IDisposable，销毁/移除时 ECS 自动调 Dispose；move 由 ECS 自动（指针转移，零分配）。</summary>
        public struct HookedBuffer : IComponentData, IDisposable
        {
            public NativeArray<int> Data;

            public void Dispose() => Data.Dispose();
        }

        private const int EntityCount = 1000;
        private const int BufferLength = 64;

        public static void Run()
        {
            Console.WriteLine("=== Component Lifecycle Memory Demo（无 hook vs 有 hook）===\n");
            // Disposable 组件由生成器在模块加载时自动注册（[ModuleInitializer]），无需手动调用

            RunNoHookScenario();
            RunHookedScenario();

            Console.WriteLine("\n=== 对比完成 ===\n");
        }

        private static void RunNoHookScenario()
        {
            Console.WriteLine("--- A. 无 hook（NoHookBuffer，纯位拷贝）---");
            var world = new World("NoHook");
            var before = PersistentAllocator.GetStats();

            // 场景 1：创建 + 销毁
            var e1 = world.CreateEntities(EntityCount, typeof(NoHookBuffer));
            for (int i = 0; i < EntityCount; i++)
            {
                ref var c = ref world.EntityManager.GetComponent<NoHookBuffer>(e1[i]);
                c.Data = new NativeArray<int>(BufferLength, Allocator.Persistent);
                c.Data[0] = i;
            }
            for (int i = 0; i < EntityCount; i++)
                world.EntityManager.DestroyEntity(e1[i]);

            // 场景 2：创建 + RemoveComponent（swap-pop）
            var e2 = world.CreateEntities(EntityCount, typeof(NoHookBuffer), typeof(Position));
            for (int i = 0; i < EntityCount; i++)
            {
                ref var c = ref world.EntityManager.GetComponent<NoHookBuffer>(e2[i]);
                c.Data = new NativeArray<int>(BufferLength, Allocator.Persistent);
                c.Data[0] = i;
            }
            for (int i = 0; i < EntityCount; i++)
                world.EntityManager.RemoveComponent<NoHookBuffer>(e2[i]);

            world.Dispose();
            var after = PersistentAllocator.GetStats();
            PrintDelta("无 hook", before, after);
        }

        private static void RunHookedScenario()
        {
            Console.WriteLine("--- B. 有 hook（HookedBuffer，move 转移 + Dispose）---");
            var world = new World("Hooked");
            var before = PersistentAllocator.GetStats();

            // 场景 1：创建 + 销毁
            var e1 = world.CreateEntities(EntityCount, typeof(HookedBuffer));
            for (int i = 0; i < EntityCount; i++)
            {
                ref var c = ref world.EntityManager.GetComponent<HookedBuffer>(e1[i]);
                c.Data = new NativeArray<int>(BufferLength, Allocator.Persistent);
                c.Data[0] = i;
            }
            for (int i = 0; i < EntityCount; i++)
                world.EntityManager.DestroyEntity(e1[i]);

            // 场景 2：创建 + RemoveComponent（swap-pop）
            var e2 = world.CreateEntities(EntityCount, typeof(HookedBuffer), typeof(Position));
            for (int i = 0; i < EntityCount; i++)
            {
                ref var c = ref world.EntityManager.GetComponent<HookedBuffer>(e2[i]);
                c.Data = new NativeArray<int>(BufferLength, Allocator.Persistent);
                c.Data[0] = i;
            }
            for (int i = 0; i < EntityCount; i++)
                world.EntityManager.RemoveComponent<HookedBuffer>(e2[i]);

            world.Dispose();
            var after = PersistentAllocator.GetStats();
            PrintDelta("有 hook", before, after);
        }

        private static void PrintDelta(string label, PersistentAllocator.Stats before, PersistentAllocator.Stats after)
        {
            int da = after.Allocs - before.Allocs;
            int df = after.Frees - before.Frees;
            int dfo = after.Foreign - before.Foreign;
            Console.WriteLine($"  {label}:  Allocs +{da,-5}  Frees +{df,-5}  Foreign +{dfo}");
            if (da == df)
                Console.WriteLine($"    => 平衡（Allocs == Frees），无泄漏");
            else
                Console.WriteLine($"    => 泄漏 {da - df} 块原生缓冲（Allocs > Frees）");
        }
    }
}
