using System;
using System.Threading;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    // ─── #11 RunWhen 接线 ───
    [Order(0)]
    public struct EventEmitterSystem : ISystem
    {
        public void OnUpdate() => World.DefaultWorld.SendEvent(new TestDamageEvent());
    }

    [RunWhen(typeof(TestDamageEvent))]
    [Order(1)]
    public struct DamageReactSystem : ISystem
    {
        public static int Ran;
        public void OnUpdate() => Ran++;
    }

    // ─── #5 ScheduleGraph 传递顺序（First→Middle→Last，First/Last 写同一组件）───
    [OrderBefore(typeof(MiddleOrderSystem))]
    [Write(typeof(Position))]
    public struct FirstOrderSystem : ISystem { public void OnUpdate() { } }

    [OrderBefore(typeof(LastOrderSystem))]
    public struct MiddleOrderSystem : ISystem { public void OnUpdate() { } }

    [Write(typeof(Position))]
    public struct LastOrderSystem : ISystem { public void OnUpdate() { } }

    // ─── #10 嵌套 WithEnabled：外层位图不被内层覆盖 ───
    public struct EnableA : IComponentData, IEnableableComponent { }
    public struct EnableB : IComponentData, IEnableableComponent { }

    public class BugRegressionTests
    {
        // #11：SendEvent 后 RunWhen 系统应被触发（一帧事件延迟，DOTS 事件语义）
        [Fact]
        public void RunWhen_SystemRunsAfterEventSent()
        {
            var world = new World("RW");
            var runner = new SystemRunner(world);
            runner.RegisterSystem<EventEmitterSystem>();
            runner.RegisterSystem<DamageReactSystem>();

            DamageReactSystem.Ran = 0;
            runner.Update();  // 帧 1：发事件，RunWhen 检查上一帧计数=0 → 跳过
            Assert.Equal(0, DamageReactSystem.Ran);

            runner.Update();  // 帧 2：上一帧事件计数=1 → 运行
            Assert.Equal(1, DamageReactSystem.Ran);

            world.Dispose();
        }

        // #3：事件容量溢出可观测（返回 false + OverflowCount）
        [Fact]
        public void EventStream_Overflow_IsObservable()
        {
            var world = new World("EV");
            world.RegisterEvent<TestDamageEvent>();
            var stream = world.GetEventStream<TestDamageEvent>();

            int sent = 0, dropped = 0;
            for (int i = 0; i < stream.Capacity + 100; i++)
            {
                if (world.SendEvent(new TestDamageEvent())) sent++;
                else dropped++;
            }

            Assert.Equal(stream.Capacity, sent);
            Assert.Equal(100, dropped);
            Assert.Equal(100, stream.OverflowCount);

            world.Dispose();
        }

        // #4：ECB 多线程并发写不丢失
        [Fact]
        public void ECB_ConcurrentWrites_NoLoss()
        {
            var ecb = new DeferredCommandBuffer();
            const int threadCount = 8, perThread = 400;

            var threads = new Thread[threadCount];
            for (int t = 0; t < threadCount; t++)
            {
                threads[t] = new Thread(() =>
                {
                    for (int i = 0; i < perThread; i++)
                        ecb.DestroyEntity(new Entity { Id = i, Version = 0 });
                });
                threads[t].Start();
            }
            foreach (var t in threads) t.Join();

            Assert.Equal(threadCount * perThread, ecb.DestroyedEntityCount);
            // `CommandCount` 数的是**命令**，而连续的 destroy 合并成一个连续段 = 一条命令（P0-4b）
            // ⇒ 本用例里只写 destroy、中间无其它命令，故恰好 1 条。
            Assert.Equal(1, ecb.CommandCount);
            ecb.Dispose();
        }

        // #5：合法传递顺序（First→Middle→Last）不应被误判循环
        [Fact]
        public void ScheduleGraph_TransitiveOrder_NoFalseCycle()
        {
            var graph = new ScheduleGraph();
            graph.RegisterSystem<FirstOrderSystem>();
            graph.RegisterSystem<MiddleOrderSystem>();
            graph.RegisterSystem<LastOrderSystem>();

            var layers = graph.GetLayers();
            Assert.NotEmpty(layers);  // 不抛 cyclic dependency 即通过
        }

        // #13：稀疏高 ID 快照恢复到新 World 不越界崩溃
        [Fact]
        public void Restore_SparseHighId_DoesNotCrash()
        {
            var worldA = new World("A");
            var emA = worldA.EntityManager;
            var first = emA.NewEntity(typeof(Position));
            for (int i = 0; i < 2000; i++)
            {
                var tmp = emA.NewEntity(typeof(Position));
                emA.DestroyEntity(tmp);
            }
            var live = emA.NewEntity(typeof(Position));
            var snapshot = worldA.TakeSnapshot();

            var worldB = new World("B");
            worldB.Restore(snapshot);

            // 恢复后高 ID 活实体可读（修复前 entities 只翻倍扩容 → 越界抛异常）
            _ = worldB.EntityManager.GetComponent<Position>(live);
            _ = worldB.EntityManager.GetComponent<Position>(first);

            worldA.Dispose();
            worldB.Dispose();
        }

        // #12：Restore 后关系反向索引重建，级联删除仍生效
        [Fact]
        public void Restore_RebuildsCascadeIndex()
        {
            var worldA = new World("RelA");
            var emA = worldA.EntityManager;
            var parent = emA.NewEntity(typeof(Position));
            var child = emA.NewEntity(typeof(Position));
            emA.AddRelationship<CascadeChildOf>(child, parent);

            var snapshot = worldA.TakeSnapshot();
            worldA.Dispose();

            var worldB = new World("RelB");
            worldB.Restore(snapshot);

            // 反向索引重建后，销毁 parent 应级联销毁 child（依赖 target→sources 索引）
            worldB.EntityManager.DestroyEntity(parent);
            Assert.Null(worldB.EntityManager.GetEntityInfoRef(child.Id).Archetype);

            worldB.Dispose();
        }

        // #10：嵌套 WithEnabled 查询，外层组合位图不应被内层覆盖
        [Fact]
        public void NestedWithEnabledQueries_OuterMaskNotCorrupted()
        {
            var world = new World("Nest");
            var em = world.EntityManager;
            var e1 = em.NewEntity(typeof(Position), typeof(EnableA), typeof(EnableB));
            em.NewEntity(typeof(Position), typeof(EnableA), typeof(EnableB));
            em.SetComponentEnabled<EnableB>(e1, false);  // e1 的 EnableB 禁用

            int outerCount = 0;
            foreach (var a in world.Query<Position>().WithEnabled<EnableA>())
            {
                outerCount++;
                foreach (var b in world.Query<Position>().WithEnabled<EnableB>())
                {
                    // 内层只匹配 EnableB 启用的实体
                }
            }

            // 外层 EnableA 两个实体都启用，应遍历 2 个（修复前外层位图被内层 EnableB 覆盖 → 只遍历 1 个）
            Assert.Equal(2, outerCount);

            world.Dispose();
        }

        // #14：查询迭代期间结构变更应抛异常，而非静默读死槽/重复处理
        [Fact]
        public void QueryIteration_StructuralChange_Throws()
        {
            var world = new World("Iter");
            var em = world.EntityManager;
            em.NewEntity(typeof(Position), typeof(Velocity));
            var e2 = em.NewEntity(typeof(Position), typeof(Velocity));

            Assert.Throws<InvalidOperationException>(() =>
            {
                foreach (var r in world.Query<Position, Velocity>())
                {
                    em.DestroyEntity(e2);  // 迭代中 swap-pop 结构变更
                }
            });

            world.Dispose();
        }
    }
}
