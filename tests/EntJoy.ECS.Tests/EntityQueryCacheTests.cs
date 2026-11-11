using System;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// 查询缓存：共享实例（规则指纹复用）+ 增量刷新 + 正确性。
    /// </summary>
    public class EntityQueryCacheTests
    {
        [Fact]
        public void QueryCache_WarmLookup_IsAllocationFreeAndOrderIndependent()
        {
            using var world = new World("WarmQueryCache");
            var rule = new QueryBuilder().WithAll<Position, Velocity>();
            var reordered = new QueryBuilder().WithAll<Velocity, Position>();
            var expected = world.GetOrCreateEntityQuery(rule);
            for (int i = 0; i < 100; i++) world.GetOrCreateEntityQuery(reordered);
            // 三窗取最小：偶发的一次性分配来自进程环境（同一循环在独立进程 300×1000 次实测 0 B），
            // 真实回归会让每个窗口都非零，取最小不会掩盖它。
            EntityQuery actual = null;
            long allocated = long.MaxValue;
            for (int window = 0; window < 3; window++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 1000; i++) actual = world.GetOrCreateEntityQuery(reordered);
                allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            Assert.Same(expected, actual);
            Assert.Equal(0, allocated);
            Assert.Equal(1, world.QueryCacheCount);
        }

        [Fact]
        public void QueryKey_SnapshotsRulesAndDistinguishesDuplicateCounts()
        {
            var position = ComponentTypeManager.GetComponentType(typeof(Position));
            var velocity = ComponentTypeManager.GetComponentType(typeof(Velocity));
            var health = ComponentTypeManager.GetComponentType(typeof(Health));
            var types = new[] { position, velocity };
            var key = new QueryKey(new QueryBuilder { All = types });
            types[0] = health;
            Assert.Equal(key, new QueryKey(new QueryBuilder { All = new[] { velocity, position } }));
            Assert.NotEqual(key, new QueryKey(new QueryBuilder { All = types }));
            var duplicate = new QueryKey(new QueryBuilder { All = new[] { position, position, velocity } });
            Assert.NotEqual(duplicate, new QueryKey(new QueryBuilder { All = new[] { position, velocity, velocity } }));
        }

        [Fact]
        public void QueryForeach_ReusesRegistryAndRefreshesAfterNewArchetype()
        {
            using var world = new World("CachedForeach");
            var query = world.GetOrCreateEntityQuery(new QueryBuilder().WithAll<Position, Velocity>());
            Assert.False(world.Query<Position, Velocity>().GetEnumerator().MoveNext());
            Assert.Equal(1, world.QueryCacheCount);
            world.EntityManager.NewEntity(typeof(Position), typeof(Velocity), typeof(Health));
            int count = 0;
            foreach (var result in world.Query<Position, Velocity>()) count++;
            Assert.Equal(1, count);
            Assert.Equal(1, query.CalculateEntityCount());
            Assert.Equal(1, world.QueryCacheCount);
        }

        [Fact]
        public void GetOrCreateEntityQuery_SameRule_ReturnsSameInstance()
        {
            using var world = new World("QueryCacheTest1");
            var b1 = new QueryBuilder().WithAll<Position, Velocity>();
            var b2 = new QueryBuilder().WithAll<Position, Velocity>();

            var q1 = world.GetOrCreateEntityQuery(b1);
            var q2 = world.GetOrCreateEntityQuery(b2);

            Assert.Same(q1, q2); // 相同规则 → 共享实例
        }

        [Fact]
        public void GetOrCreateEntityQuery_DifferentRule_DifferentInstance()
        {
            using var world = new World("QueryCacheTest2");
            var b1 = new QueryBuilder().WithAll<Position>();
            var b2 = new QueryBuilder().WithAll<Position, Velocity>();

            var q1 = world.GetOrCreateEntityQuery(b1);
            var q2 = world.GetOrCreateEntityQuery(b2);

            Assert.NotSame(q1, q2);
        }

        [Fact]
        public void GetOrCreateEntityQuery_RuleOrderInsensitive()
        {
            using var world = new World("QueryCacheTest3");
            // 条件顺序不同 → 指纹排序归一后应共享同一实例（匹配语义与顺序无关）
            var b1 = new QueryBuilder().WithAll<Position, Velocity>();
            var b2 = new QueryBuilder().WithAll<Velocity, Position>();

            var q1 = world.GetOrCreateEntityQuery(b1);
            var q2 = world.GetOrCreateEntityQuery(b2);

            Assert.Same(q1, q2);
        }

        [Fact]
        public void QueryCache_StructuralChange_ChunksStayAccurate()
        {
            using var world = new World("QueryCacheTest4");
            var query = world.GetOrCreateEntityQuery(new QueryBuilder().WithAll<Position>());

            Assert.Equal(0, query.CalculateEntityCount());

            // 创建实体后（结构变更），惰性刷新应反映新实体
            world.EntityManager.NewEntity(typeof(Position));
            Assert.Equal(1, query.CalculateEntityCount());

            world.EntityManager.NewEntity(typeof(Position));
            Assert.Equal(2, query.CalculateEntityCount());

            // DestroyEntity → 计数回落
            var entities = world.EntityManager.GetAllArchetypes();
            // 直接销毁：遍历所有实体
            for (int i = 0; i < entities.Length; i++)
            {
                var arch = entities[i];
                if (arch == null || !arch.IsMatch(new QueryBuilder().WithAll<Position>())) continue;
                for (int c = arch.ChunkCount - 1; c >= 0; c--)
                {
                    var chunk = arch.ChunkList[c];
                    for (int s = 0; s < chunk.EntityCount; s++)
                    {
                        var e = chunk.GetEntity(s);
                        world.EntityManager.DestroyEntity(e);
                    }
                }
            }
            Assert.Equal(0, query.CalculateEntityCount());
        }

        [Fact]
        public void QueryCache_NewArchetype_FullRefreshRecoversMatch()
        {
            using var world = new World("QueryCacheTest5");
            var query = world.GetOrCreateEntityQuery(new QueryBuilder().WithAll<Position>());

            Assert.Equal(0, query.CalculateEntityCount());

            // 创建带 Position + Velocity 的实体（新 Archetype）→ 也应匹配 Position 查询
            world.EntityManager.NewEntity(typeof(Position), typeof(Velocity));
            Assert.Equal(1, query.CalculateEntityCount());
        }

        [Fact]
        public void QueryCache_NonMatchingEntity_NotIncluded()
        {
            using var world = new World("QueryCacheTest6");
            var query = world.GetOrCreateEntityQuery(new QueryBuilder().WithAll<Position>());

            world.EntityManager.NewEntity(typeof(Health)); // 不匹配 Position 查询
            Assert.Equal(0, query.CalculateEntityCount());

            world.EntityManager.NewEntity(typeof(Position));
            Assert.Equal(1, query.CalculateEntityCount());
        }

        [Fact]
        public void QueryKey_Equality_MirrorsMatchingSemantics()
        {
            var b1 = new QueryBuilder().WithAll<Position>().WithNone<Health>();
            var b2 = new QueryBuilder().WithAll<Position>().WithNone<Health>();
            var b3 = new QueryBuilder().WithAll<Position>().WithNone<Velocity>();

            var k1 = new QueryKey(b1);
            var k2 = new QueryKey(b2);
            var k3 = new QueryKey(b3);

            Assert.Equal(k1, k2);
            Assert.NotEqual(k1, k3);
            Assert.Equal(k1.GetHashCode(), k2.GetHashCode());
        }

        [Fact]
        public void QueryCache_ToComponentDataArray_MatchesEntityCount()
        {
            using var world = new World("QueryCacheTest7");
            var query = world.GetOrCreateEntityQuery(new QueryBuilder().WithAll<Position>());

            world.EntityManager.NewEntity(typeof(Position));
            world.EntityManager.NewEntity(typeof(Position));
            world.EntityManager.NewEntity(typeof(Health));

            var arr = query.ToComponentDataArray<Position>();
            Assert.Equal(2, arr.Length);
        }

        [Fact]
        public void QueryForeach_AfterRepeatedStructuralChanges_DoesNotAllocateSnapshots()
        {
            // 快照缓冲两代轮转复用：结构变更后的重建只花借用令牌，不再按块数分配数组。
            // 阈值取两侧各留约 6 倍余量：重建若退回「每次新建 3 个数组」，50 轮会到 150 KB 量级。
            using var world = new World("SnapshotAllocation");
            var em = world.EntityManager;
            const int entities = 20000;
            for (int i = 0; i < entities; i++) em.NewEntity(typeof(Position), typeof(Velocity));

            int Iterate()
            {
                int count = 0;
                foreach (var _ in world.Query<Position, Velocity>()) count++;
                return count;
            }

            for (int i = 0; i < 8; i++) Iterate();   // 预热枚举器与快照

            long before = GC.GetAllocatedBytesForCurrentThread();
            long counted = 0;
            for (int i = 0; i < 50; i++)
            {
                var entity = em.NewEntity(typeof(Position), typeof(Velocity));
                em.DestroyEntity(entity);
                counted += Iterate();   // 断言放在计时之外：xUnit 的断言自身会分配
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal((long)entities * 50, counted);
            Assert.True(allocated < 50 * 512, $"快照重建应复用缓冲；50 轮实测分配 {allocated} B。");
        }

        [Fact]
        public void QueryForeach_AfterRepeatedStructuralChanges_SeesCurrentCount()
        {
            // 每次结构变更都会让查询重建 Chunk 快照；快照缓冲按两代轮转复用，这里走满多轮。
            using var world = new World("SnapshotGenerations");
            var em = world.EntityManager;
            var created = new Entity[500];
            for (int i = 0; i < created.Length; i++)
            {
                created[i] = em.NewEntity(typeof(Position), typeof(Velocity));
                int count = 0;
                foreach (var _ in world.Query<Position, Velocity>()) count++;
                Assert.Equal(i + 1, count);
            }
            foreach (var entity in created) em.DestroyEntity(entity);
            Assert.Equal(0, world.GetOrCreateEntityQuery(new QueryBuilder().WithAll<Position, Velocity>()).CalculateEntityCount());
        }
    }
}
