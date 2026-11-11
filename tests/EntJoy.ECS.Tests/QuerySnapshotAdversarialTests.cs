using System;
using System.Collections.Generic;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// 对抗用例：尽量让「查询快照两代复用」「无序哈希」「空块延迟回收」按最不利的方式被使用，
    /// 记录每条的结论。全部用例不修改全局静态，可并行跑。
    /// </summary>
    public class QuerySnapshotAdversarialTests
    {
        private static ComponentType[] ColumnsOf(params Type[] types)
        {
            var columns = new ComponentType[types.Length];
            for (int i = 0; i < types.Length; i++) columns[i] = ComponentTypeManager.GetComponentType(types[i]);
            return columns;
        }

        private static int CountChunks(ref QueryTraversalCursor cursor)
        {
            int count = 0;
            while (cursor.MoveNextChunk(out _)) count++;
            return count;
        }

        [Fact]
        public void StaleCursor_AcrossEightPublishes_TerminatesWithoutOutOfRange()
        {
            // 最不利用法：先构造游标（此时发布一代），再让同一个 plan 反复发布改写缓冲，
            // 最后拿旧游标继续推进。数组只增不减 ⇒ 允许读到新值，但不得越界，且必须终止。
            using var world = new World("cursor-stale");
            var em = world.EntityManager;
            for (int i = 0; i < 1000; i++) em.NewEntity(typeof(Position), typeof(Velocity));

            var columns = ColumnsOf(typeof(Position), typeof(Velocity));
            var builder = new QueryBuilder { All = columns };
            var stale = new QueryTraversalCursor(em, builder, columns);

            int publishes = 0;
            for (int i = 0; i < 8; i++)
            {
                var entity = em.NewEntity(typeof(Position), typeof(Velocity));
                em.DestroyEntity(entity);
                _ = new QueryTraversalCursor(em, builder, columns);   // 构造即刷新 ⇒ 发布新一代
                publishes++;
            }

            Assert.True(publishes > 2, "至少跨过两代缓冲");
            int chunks = CountChunks(ref stale);
            Assert.True(chunks > 0 && chunks <= 1000, $"旧游标推进得到 {chunks} 个块");
        }

        [Fact]
        public void EnabledRangeFilter_AcrossRepeatedPublishes_KeepsMaskExact()
        {
            // 启用过滤走「位图字合并 + 连续区间推进」，是快照复用期间最容易读到错位数据的一条。
            // 800 个实体跨多个块与多个 64 位字，每轮结构变更都让快照重建，计数必须一次不差。
            using var world = new World("enabled-ring");
            var em = world.EntityManager;
            var entities = new Entity[800];
            for (int i = 0; i < entities.Length; i++)
            {
                entities[i] = em.NewEntity(typeof(Position), typeof(EnableA));
                em.SetComponentEnabled<EnableA>(entities[i], i % 3 != 0);
            }

            int Iterate()
            {
                int count = 0;
                foreach (var _ in world.Query<Position>().WithEnabled<EnableA>()) count++;
                return count;
            }

            const int expected = 800 - 267;   // i % 3 == 0 的 267 个被禁用
            Assert.Equal(expected, Iterate());

            for (int round = 0; round < 20; round++)
            {
                var entity = em.NewEntity(typeof(Position), typeof(EnableA));
                em.SetComponentEnabled<EnableA>(entity, false);
                em.DestroyEntity(entity);   // 结构变更 ⇒ 本轮快照失效，下面这次枚举触发重建
                Assert.Equal(expected, Iterate());
            }
        }

        [Fact]
        public void BatchResults_AreRefStructs_SoChunkReferencesCannotOutliveTheIteration()
        {
            // 快照数组会被下一代发布改写；批量层结果若能把 Chunk 引用带出去，就会读到被改写的块。
            // 现在结果为 ref struct（不能存字段、不能进闭包），且每个访问器都先校验借用令牌。
            Assert.True(typeof(ChunkResult<Position, Velocity>).IsByRefLike);
            Assert.True(typeof(ChunkEnumerator<Position, Velocity>).IsByRefLike);
            Assert.True(typeof(QueryEnumerator<Position, Velocity>).IsByRefLike);
        }

        [Fact]
        public void HashTable_AllSixteenComponentCombinations_ResolveToDistinctStableArchetypes()
        {
            // 两种错误各一半：不同集合被并成同一原型（哈希碰撞后比较失效），
            // 或同一集合分裂成两个原型（哈希不稳定）。16 种组合逐个核对实例身份。
            using var world = new World("hash-combos");
            var em = world.EntityManager;
            var all = new[] { typeof(Position), typeof(Velocity), typeof(Health), typeof(Armor) };
            var first = new Entity[16];

            for (int mask = 0; mask < 16; mask++)
            {
                var types = new List<Type>();
                for (int bit = 0; bit < 4; bit++)
                    if ((mask & (1 << bit)) != 0) types.Add(all[bit]);
                first[mask] = em.NewEntity(types.ToArray());
                em.NewEntity(types.ToArray());   // 同集合第二个实体
            }

            var archetypes = new Archetype[16];
            for (int mask = 0; mask < 16; mask++)
            {
                archetypes[mask] = em.GetEntityInfoRef(first[mask].Id).Archetype;
                Assert.NotNull(archetypes[mask]);
            }
            for (int a = 0; a < 16; a++)
            {
                Assert.Equal(2, archetypes[a].EntityCount);
                for (int b = a + 1; b < 16; b++)
                    Assert.NotSame(archetypes[a], archetypes[b]);
            }
            Assert.Equal(16, em.ArchetypeCount);
        }

        [Fact]
        public void EmptyChunksAfterMassDestroy_QuerySeesZeroUntilCompaction()
        {
            // 空块延迟回收后 ChunkList 里留下空块：查询必须看 0，压缩后仍看 0，
            // 再建实体必须落到可用块（不能落进已归还的块）。
            using var world = new World("empty-chunks");
            var em = world.EntityManager;
            var created = new Entity[5000];
            for (int i = 0; i < created.Length; i++) created[i] = em.NewEntity(typeof(Position), typeof(Velocity));

            var archetype = em.GetEntityInfoRef(created[0].Id).Archetype;
            int chunksBefore = archetype.ChunkCount;
            Assert.True(chunksBefore > 1, $"默认容量下应多于一块，实测 {chunksBefore}");

            foreach (var entity in created) em.DestroyEntity(entity);

            int Count()
            {
                int n = 0;
                foreach (var _ in world.Query<Position, Velocity>()) n++;
                return n;
            }
            int CountChunksOnly()
            {
                int n = 0;
                foreach (var _ in world.QueryChunks<Position, Velocity>()) n++;
                return n;
            }

            Assert.Equal(0, Count());
            Assert.Equal(0, CountChunksOnly());
            // 空块留在原地（销毁路径不即时回收），压缩后只留最后一块。
            Assert.Equal(chunksBefore, archetype.ChunkCount);

            em.CompactChunks();
            Assert.Equal(0, Count());
            Assert.Equal(1, archetype.ChunkCount);

            var again = em.NewEntity(typeof(Position), typeof(Velocity));
            Assert.Same(archetype, em.GetEntityInfoRef(again.Id).Archetype);
            Assert.Equal(1, Count());
        }
    }
}
