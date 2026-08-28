using System;
using EntJoy.ECS;

namespace EntJoySample.ECS
{
    /// <summary>
    /// Observer 测试：组件生命周期事件的 push-based 回调（批量 span 签名）。
    ///
    /// v1 覆盖：
    ///   - 主线程立即回调：AddComponent / Set / RemoveComponent / DestroyEntity
    ///   - NewEntity / CreateEntities 带组件 → Added
    ///   - ECB Playback（主线程手动）→ 内部调用主入口 → 事件照常触发
    ///   - 多 World 隔离
    ///   - RemoveObserver
    ///   - 批量合并：CreateEntities 大数量 → 一次回调（ReadOnlySpan）
    /// </summary>
    public static class ObserverDemo
    {
        private static World _world;

        public static void Run()
        {
            Console.WriteLine("=== Observer Demo ===\n");
            _world = new World("ObserverDemo");

            TestAddedOnAddComponent();
            TestSetOnValueChange();
            TestRemovedOnRemove();
            TestRemovedOnDestroy();
            TestAddedOnNewEntity();
            TestEcbPlaybackTriggersEvents();
            TestMultiWorldIsolation();
            TestRemoveObserver();
            TestBatchCreateMerge();

            _world.Dispose();
            Console.WriteLine("\n=== Observer Demo Complete ===\n");
        }

        /// <summary>测试 1：AddComponent → Added 立即回调。</summary>
        private static void TestAddedOnAddComponent()
        {
            Console.WriteLine("--- Test 1: AddComponent → Added ---");
            int addedCount = 0;
            Entity observed = default;
            float observedValue = 0;

            _world.AddObserver<Health>(ObserverEvents.Added, (entities, values) =>
            {
                addedCount += entities.Length;
                observed = entities[0];
                observedValue = values[0].Current;
            });

            var e = _world.CreateEntities(1, typeof(Position))[0]; // 先创建（无 Health，不触发 Added）
            // 用 AddComponent 触发 Added：先创建无 Health 的实体
            var e2 = _world.CreateEntities(1, typeof(Position))[0];
            _world.EntityManager.AddComponent(e2, new Health { Current = 50, Max = 100 });

            Console.WriteLine($"  Added count: {addedCount} (expected 1)");
            Console.WriteLine($"  Entity match: {observed.Id == e2.Id} (expected True)");
            Console.WriteLine($"  NewValue.Current: {observedValue} (expected 50)");
            bool ok = addedCount == 1 && observed.Id == e2.Id && observedValue == 50;
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}\n");

            _world.ClearObservers<Health>();
        }

        /// <summary>测试 2：Set → Set 立即回调（值正确）。</summary>
        private static void TestSetOnValueChange()
        {
            Console.WriteLine("--- Test 2: Set → Set ---");
            int setCount = 0;
            float lastValue = 0;

            var handle = _world.AddObserver<Health>(ObserverEvents.Set, (entities, values) =>
            {
                setCount += entities.Length;
                lastValue = values[0].Current;
            });
            Console.WriteLine($"  registered handle valid: {handle.IsValid}, id={handle.Id}");

            var e = _world.CreateEntities(1, typeof(Health))[0];
            _world.EntityManager.Set(e, new Health { Current = 75, Max = 100 });

            Console.WriteLine($"  Set count: {setCount} (expected 1)");
            Console.WriteLine($"  NewValue.Current: {lastValue} (expected 75)");
            bool ok = setCount == 1 && lastValue == 75;
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}\n");

            _world.ClearObservers<Health>();
        }

        /// <summary>测试 3：RemoveComponent → Removed 立即回调（OldValue 为迁移前快照）。</summary>
        private static void TestRemovedOnRemove()
        {
            Console.WriteLine("--- Test 3: RemoveComponent → Removed ---");
            int removedCount = 0;
            float oldValue = 0;
            bool entityResolvable = false;

            _world.AddObserver<Health>(ObserverEvents.Removed, (entities, values) =>
            {
                removedCount += entities.Length;
                oldValue = values[0].Current;
                // 回调内实体应仍可解析（已迁移到目标 archetype，无 Health 但实体有效）
                entityResolvable = _world.EntityManager.GetEntityInfoRef(entities[0].Id).Archetype != null;
            });

            var e = _world.CreateEntities(1, typeof(Position), typeof(Health))[0];
            _world.EntityManager.Set(e, new Health { Current = 30, Max = 100 });
            _world.EntityManager.RemoveComponent<Health>(e);

            Console.WriteLine($"  Removed count: {removedCount} (expected 1)");
            Console.WriteLine($"  OldValue.Current: {oldValue} (expected 30)");
            Console.WriteLine($"  Entity resolvable: {entityResolvable} (expected True)");
            bool ok = removedCount == 1 && oldValue == 30 && entityResolvable;
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}\n");

            _world.ClearObservers<Health>();
        }

        /// <summary>测试 4：DestroyEntity → Removed(=Destroyed) 回调。</summary>
        private static void TestRemovedOnDestroy()
        {
            Console.WriteLine("--- Test 4: DestroyEntity → Removed ---");
            int destroyedCount = 0;
            float oldValue = 0;

            _world.AddObserver<Health>(ObserverEvents.Removed, (entities, values) =>
            {
                destroyedCount += entities.Length;
                oldValue = values[0].Current;
            });

            var e = _world.CreateEntities(1, typeof(Position), typeof(Health))[0];
            _world.EntityManager.Set(e, new Health { Current = 80, Max = 100 });
            _world.EntityManager.DestroyEntity(e);

            Console.WriteLine($"  Removed count (on destroy): {destroyedCount} (expected 1)");
            Console.WriteLine($"  OldValue.Current: {oldValue} (expected 80)");
            bool ok = destroyedCount == 1 && oldValue == 80;
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}\n");

            _world.ClearObservers<Health>();
        }

        /// <summary>测试 5：NewEntity 带组件 → Added 回调。</summary>
        private static void TestAddedOnNewEntity()
        {
            Console.WriteLine("--- Test 5: NewEntity → Added ---");
            int addedCount = 0;

            _world.AddObserver<Health>(ObserverEvents.Added, (entities, _) => addedCount += entities.Length);

            var e = _world.EntityManager.NewEntity(typeof(Health));
            var e2 = _world.EntityManager.NewEntity(typeof(Position), typeof(Health));

            Console.WriteLine($"  Added count: {addedCount} (expected 2)");
            bool ok = addedCount == 2;
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}\n");

            _world.ClearObservers<Health>();
        }

        /// <summary>测试 6：ECB Playback（主线程手动）→ 内部走主入口 → 事件照常触发。</summary>
        private static void TestEcbPlaybackTriggersEvents()
        {
            Console.WriteLine("--- Test 6: ECB Playback → Events ---");
            int addedCount = 0;
            int removedCount = 0;

            _world.AddObserver<Health>(ObserverEvents.Added, (entities, _) => addedCount += entities.Length);
            _world.AddObserver<Health>(ObserverEvents.Removed, (entities, _) => removedCount += entities.Length);

            var e = _world.CreateEntities(1, typeof(Position))[0];

            var ecb = new DeferredCommandBuffer();
            ecb.AddComponent(e, new Health { Current = 10, Max = 100 });
            ecb.RemoveComponent<Health>(e);
            ecb.Playback(_world.EntityManager);   // Playback 内部调 AddComponentRaw/RemoveComponentRaw → 主线程立即派发
            ecb.Dispose();

            Console.WriteLine($"  Added via ECB: {addedCount} (expected 1)");
            Console.WriteLine($"  Removed via ECB: {removedCount} (expected 1)");
            bool ok = addedCount == 1 && removedCount == 1;
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}\n");

            _world.ClearObservers<Health>();
        }

        /// <summary>测试 7：多 World 隔离。</summary>
        private static void TestMultiWorldIsolation()
        {
            Console.WriteLine("--- Test 7: Multi-World Isolation ---");
            int world1Count = 0;
            int world2Count = 0;

            using (var w1 = new World("W1"))
            using (var w2 = new World("W2"))
            {
                w1.AddObserver<Health>(ObserverEvents.Added, (entities, _) => world1Count += entities.Length);
                w2.AddObserver<Health>(ObserverEvents.Added, (entities, _) => world2Count += entities.Length);

                w1.EntityManager.NewEntity(typeof(Health));
                w1.EntityManager.NewEntity(typeof(Health));
                w2.EntityManager.NewEntity(typeof(Health));

                Console.WriteLine($"  world1 events: {world1Count} (expected 2)");
                Console.WriteLine($"  world2 events: {world2Count} (expected 1)");
            }

            bool ok = world1Count == 2 && world2Count == 1;
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}\n");
        }

        /// <summary>测试 8：RemoveObserver 后不再回调（独立 World 隔离 Test 7 影响）。</summary>
        private static void TestRemoveObserver()
        {
            Console.WriteLine("--- Test 8: RemoveObserver ---");
            int addedCount = 0;

            using var w = new World("T8");
            var handle = w.AddObserver<Health>(ObserverEvents.Added, (entities, _) => addedCount += entities.Length);
            Console.WriteLine($"  handle valid: {handle.IsValid}, id={handle.Id}");
            w.EntityManager.NewEntity(typeof(Health));
            Console.WriteLine($"  after first NewEntity: {addedCount} (expected 1)");
            w.RemoveObserver<Health>(handle);
            w.EntityManager.NewEntity(typeof(Health));

            Console.WriteLine($"  Added count after remove: {addedCount} (expected 1)");
            bool ok = addedCount == 1;
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}\n");
        }

        /// <summary>测试 9：批量合并——CreateEntities(10000) → 1 次回调（Span 长度 10000）。</summary>
        private static void TestBatchCreateMerge()
        {
            Console.WriteLine("--- Test 9: Batch Create Merge ---");
            int callbackCount = 0;
            int totalEntities = 0;
            float firstValue = 0;
            float lastValue = 0;

            _world.AddObserver<Health>(ObserverEvents.Added, (entities, values) =>
            {
                callbackCount++;
                totalEntities += entities.Length;
                if (entities.Length > 0) { firstValue = values[0].Current; lastValue = values[entities.Length - 1].Current; }
            });

            var created = _world.CreateEntities(10000, typeof(Health));

            Console.WriteLine($"  callback count: {callbackCount} (expected 1)");
            Console.WriteLine($"  total entities: {totalEntities} (expected 10000)");
            Console.WriteLine($"  span length: {totalEntities} (expected 10000)");
            Console.WriteLine($"  first/last value: {firstValue}/{lastValue} (expected 0/0, 新组件零值)");
            bool ok = callbackCount == 1 && totalEntities == 10000 && created.Length == 10000;
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}\n");

            _world.ClearObservers<Health>();
        }
    }
}
