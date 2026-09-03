using System;
using EntJoy.Collections;
using EntJoy.ECS;

namespace EntJoySample.ECS
{
    /// <summary>
    /// SharedBlob 示例：不可变共享数据块 + 引用计数。组件「持有」需显式 Clone()（AddRef），
    /// SpawnFrom 复制含 SharedBlob 的组件时通过 OnCopy 自动 refcount++（实例与 prefab 共享同一 blob）。
    /// </summary>
    public static class SharedBlobDemo
    {
        public struct GridData
        {
            public int Width;
            public int Height;
        }

        public struct GridComp : IComponentData, IDisposable, ICopyable<GridComp>
        {
            public SharedBlob<GridData> Data;
            public void OnCopy(in GridComp src, ref GridComp dst) => dst.Data = src.Data.Clone();
            public void Dispose() => Data.Dispose();
        }

        public static void Run()
        {
            Console.WriteLine("=== SharedBlob 示例 ===\n");

            // 1. 创建不可变 blob
            var blob = SharedBlobBuilder.Create(new GridData { Width = 100, Height = 50 });
            Console.WriteLine($"blob 创建: Width={blob.Value.Width}, Height={blob.Value.Height}, refCount={blob.RefCount} (期望 1)");

            // 2. 组件持有 blob：显式 Clone() 递增引用计数（共享语义，位拷贝不会 AddRef）
            var world = new World("SharedBlobDemo");
            var prefab = world.EntityManager.NewEntity(typeof(GridComp), typeof(Prefab));
            world.EntityManager.GetComponent<GridComp>(prefab).Data = blob.Clone();
            Console.WriteLine($"prefab 持有后 refCount={blob.RefCount} (期望 2)");

            // 3. SpawnFrom 复制：OnCopy 自动 AddRef，实例与 prefab 共享同一 blob
            var instances = world.SpawnFrom(prefab, 3);
            Console.WriteLine($"SpawnFrom 3 实例后 refCount={blob.RefCount} (期望 5 = 2 + 3 实例)");

            ref var instData = ref world.EntityManager.GetComponent<GridComp>(instances[0]).Data.Value;
            Console.WriteLine($"实例读取: Width={instData.Width}, Height={instData.Height} (共享同一 blob)");

            // 4. 销毁递减引用计数，最后一个释放
            for (int i = 0; i < instances.Length; i++)
                world.EntityManager.DestroyEntity(instances[i]);
            Console.WriteLine($"销毁 3 实例后 refCount={blob.RefCount} (期望 2)");

            world.EntityManager.DestroyEntity(prefab);
            Console.WriteLine($"销毁 prefab 后 refCount={blob.RefCount} (期望 1)");

            blob.Dispose();
            Console.WriteLine($"释放局部 blob 后 IsCreated={blob.IsCreated} (期望 False)");

            world.Dispose();
            Console.WriteLine("\n=== SharedBlob 示例 Complete ===\n");
        }
    }
}
