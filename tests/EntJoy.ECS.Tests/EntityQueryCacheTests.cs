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
    }
}
