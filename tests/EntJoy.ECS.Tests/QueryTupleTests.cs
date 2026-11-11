using System;
using System.Collections.Generic;
using System.Threading;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// N 元组查询生成器：验证 world.Query&lt;T0, T1, T2&gt;() 生成的重载可用且正确。
    /// 触发 QueryTupleSourceGenerator 生成 QueryEnumerable/Enumerator/Result 三组件版本。
    /// </summary>
    public class QueryTupleTests
    {
        private struct SparseColumn<T> : IComponentData { public int Value; }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void ChunkViews_RejectEveryAccessAfterStorageChanges(int operation)
        {
            TestBackend.EnsureInitialized();
            using var world = new World("ChunkViewInvalidation");
            var snapshot = world.TakeSnapshot();
            world.EntityManager.NewEntity(typeof(Position), typeof(Velocity), typeof(RelActiveComponent));
            var pairs = world.QueryChunks<Position, RelActiveComponent>().GetEnumerator();
            var triples = world.QueryChunks<Position, Velocity, RelActiveComponent>().GetEnumerator();
            Assert.True(pairs.MoveNext());
            Assert.True(triples.MoveNext());
            var pair = pairs.Current;
            var triple = triples.Current;
            if (operation == 0) world.EntityManager.NewEntity(typeof(Position), typeof(Velocity), typeof(RelActiveComponent));
            else if (operation == 1) world.Restore(snapshot);
            else world.Dispose();
            if (operation != 2)
            {
                foreach (var chunk in world.QueryChunks<Position, RelActiveComponent>()) Assert.True(chunk.Length > 0);
                foreach (var chunk in world.QueryChunks<Position, Velocity, RelActiveComponent>()) Assert.True(chunk.Length > 0);
            }
            var expected = operation == 2 ? typeof(ObjectDisposedException) : typeof(InvalidOperationException);
            for (int access = 0; access < 6; access++)
            {
                Exception pairError = null, tripleError = null;
                try
                {
                    switch (access)
                    {
                        case 0: _ = pair.Length; break;
                        case 1: _ = pair.GetReadOnlySpan0(); break;
                        case 2: _ = pair.GetSpan0(); break;
                        case 3: _ = pair.GetEnabledMask<RelActiveComponent>(); break;
                        case 4: _ = pair.IsComponentEnabled<RelActiveComponent>(0); break;
                        default: pair.SetComponentEnabled<RelActiveComponent>(0, false); break;
                    }
                }
                catch (Exception error) { pairError = error; }
                try
                {
                    switch (access)
                    {
                        case 0: _ = triple.Length; break;
                        case 1: _ = triple.GetReadOnlySpan0(); break;
                        case 2: _ = triple.GetSpan0(); break;
                        case 3: _ = triple.GetEnabledMask<RelActiveComponent>(); break;
                        case 4: _ = triple.IsComponentEnabled<RelActiveComponent>(0); break;
                        default: triple.SetComponentEnabled<RelActiveComponent>(0, false); break;
                    }
                }
                catch (Exception error) { tripleError = error; }
                Assert.IsType(expected, pairError);
                Assert.IsType(expected, tripleError);
            }
        }

        [Fact]
        public void QueryBorrow_ConcurrentCaptureInvalidatesEveryViewAndKeepsDisposeState()
        {
            using var world = new World("QueryBorrowConcurrentCapture");
            var states = new QueryReadState[64];
            System.Threading.Tasks.Parallel.For(0, states.Length,
                i => states[i] = new QueryReadState(world.EntityManager, trackRelationships: false));
            world.EntityManager.NewEntity(typeof(Position));
            foreach (var state in states) Assert.Throws<InvalidOperationException>(() => state.Validate());
            var fresh = new QueryReadState(world.EntityManager, trackRelationships: false);
            fresh.Validate();
            world.Dispose();
            Assert.Throws<ObjectDisposedException>(() => fresh.Validate());
            foreach (var state in states) Assert.Throws<ObjectDisposedException>(() => state.Validate());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void QueryRanges_PreserveSlotOrderAcrossChunksAndFilters(bool filtered)
        {
            using var world = new World("QueryRangeSlotOrder");
            var em = world.EntityManager;
            var target = em.NewEntity(typeof(Position));
            var other = em.NewEntity(typeof(Position));
            var expected = new List<int>();
            for (int i = 0; i < 260; i++)
            {
                var entity = em.NewEntity(typeof(Position), typeof(Velocity), typeof(Health), typeof(RelActiveComponent));
                em.Set(entity, new Position { X = i });
                em.AddRelationship<ChildOf>(entity, i % 3 == 0 ? target : other);
                bool enabled = i % 128 >= 60 && i % 128 < 70;
                em.SetComponentEnabled<RelActiveComponent>(entity, enabled);
                if (!filtered || (enabled && i % 3 == 0)) expected.Add(i);
            }
            var builder = new QueryBuilder().WithAll<Position, Velocity>().WithAll<Health>();
            if (filtered) builder = builder.WithEnabled<RelActiveComponent>().WithRelationship<ChildOf>(target);
            var pairs = new List<int>();
            var triples = new List<int>();
            foreach (var row in new QueryEnumerable<Position, Velocity>(em, builder)) pairs.Add((int)row.ReadOnlyComp0.X);
            foreach (var row in new QueryEnumerable<Position, Velocity, Health>(em, builder)) triples.Add((int)row.ReadOnlyComp0.X);
            Assert.Equal(expected, pairs);
            Assert.Equal(expected, triples);
        }

        [Fact]
        public void QueryPlans_PreserveRequestedColumnOrder()
        {
            using var world = new World("QueryPlanColumnOrder");
            var em = world.EntityManager;
            var entity = em.NewEntity(typeof(Position), typeof(Velocity), typeof(Health));
            em.Set(entity, new Position { X = 11 });
            em.Set(entity, new Velocity { X = 22 });
            em.Set(entity, new Health { Current = 33 });

            foreach (var row in world.Query<Position, Velocity>()) Assert.Equal(11f, row.ReadOnlyComp0.X);
            foreach (var row in world.Query<Velocity, Position>()) Assert.Equal(22f, row.ReadOnlyComp0.X);
            foreach (var row in world.Query<Position, Velocity, Health>()) Assert.Equal(33, row.ReadOnlyComp2.Current);
            foreach (var row in world.Query<Health, Position, Velocity>()) Assert.Equal(33, row.ReadOnlyComp0.Current);
            foreach (var chunk in world.QueryChunks<Velocity, Position>()) Assert.Equal(11f, chunk.GetReadOnlySpan1()[0].X);
            foreach (var chunk in world.QueryChunks<Health, Velocity, Position>()) Assert.Equal(11f, chunk.GetReadOnlySpan2()[0].X);
            Assert.Equal(2, world.QueryCacheCount);
        }

        [Fact]
        public void QueryPlans_FollowChunkGrowthNewArchetypesAndRestore()
        {
            TestBackend.EnsureInitialized();
            using var world = new World("QueryPlanStorageChanges");
            var em = world.EntityManager;
            void Create(int count, bool extra = false)
            {
                for (int i = 0; i < count; i++)
                {
                    var entity = extra
                        ? em.NewEntity(typeof(Position), typeof(Velocity), typeof(Health), typeof(RelActiveComponent))
                        : em.NewEntity(typeof(Position), typeof(Velocity), typeof(Health));
                    em.Set(entity, new Position { X = 1 });
                }
            }
            void Verify(int expected)
            {
                int pairs = 0, triples = 0, pairChunks = 0, tripleChunks = 0;
                foreach (var row in world.Query<Position, Velocity>()) pairs += (int)row.ReadOnlyComp0.X;
                foreach (var row in world.Query<Position, Velocity, Health>()) triples += (int)row.ReadOnlyComp0.X;
                foreach (var chunk in world.QueryChunks<Position, Velocity>()) pairChunks += chunk.Length;
                foreach (var chunk in world.QueryChunks<Position, Velocity, Health>()) tripleChunks += chunk.Length;
                Assert.Equal(expected, pairs);
                Assert.Equal(expected, triples);
                Assert.Equal(expected, pairChunks);
                Assert.Equal(expected, tripleChunks);
            }
            Create(129);
            Verify(129);
            var snapshot = world.TakeSnapshot();
            Create(260);
            Verify(389);
            Create(2, extra: true);
            Verify(391);
            world.Restore(snapshot);
            Verify(129);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void QueryPlans_RelationshipFilterRemainsExhausted(bool matching)
        {
            using var world = new World("QueryPlanRelationshipEnd");
            var em = world.EntityManager;
            var target = em.NewEntity(typeof(Position));
            var other = em.NewEntity(typeof(Position));
            var source = em.NewEntity(typeof(Position), typeof(Velocity), typeof(Health));
            em.AddRelationship<ChildOf>(source, matching ? target : other);
            var pair = world.Query<Position, Velocity>().WithRelationship<ChildOf>(target).GetEnumerator();
            var builder = new QueryBuilder().WithAll<Position, Velocity>().WithAll<Health>().WithRelationship<ChildOf>(target);
            var triple = new QueryEnumerable<Position, Velocity, Health>(em, builder).GetEnumerator();
            Assert.Equal(matching, pair.MoveNext());
            Assert.Equal(matching, triple.MoveNext());
            for (int i = 0; i < 3; i++)
            {
                Assert.False(pair.MoveNext());
                Assert.False(triple.MoveNext());
            }
        }

        [Fact]
        public void Query_MoveNext_ValidatesBorrowWithinChunk()
        {
            using var world = new World("QueryBorrowPerChunk");
            var em = world.EntityManager;
            for (int i = 0; i < 4; i++) em.NewEntity(typeof(Position), typeof(Velocity));
            var pairs = world.Query<Position, Velocity>().GetEnumerator();
            Assert.True(pairs.MoveNext());

            em.NewEntity(typeof(Position), typeof(Velocity));

            bool rejected = false;
            try { pairs.MoveNext(); }
            catch (InvalidOperationException) { rejected = true; }
            Assert.True(rejected);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Query_MoveNext_RejectsInvalidatedBorrow(bool dispose)
        {
            using var world = new World("QueryInvalidatedBorrow");
            world.EntityManager.NewEntity(typeof(Position), typeof(Velocity), typeof(Health));
            var pairs = world.Query<Position, Velocity>().GetEnumerator();
            var triples = world.Query<Position, Velocity, Health>().GetEnumerator();
            Assert.True(pairs.MoveNext());
            Assert.True(triples.MoveNext());
            var pair = pairs.Current;
            var triple = triples.Current;
            Assert.Equal(0f, pair.ReadOnlyComp0.X);
            Assert.Equal(0f, triple.ReadOnlyComp0.X);

            if (dispose) world.Dispose();
            else world.EntityManager.NewEntity(typeof(Position));

            var expectedException = dispose ? typeof(ObjectDisposedException) : typeof(InvalidOperationException);
            bool pairRejected = false;
            bool tripleRejected = false;
            try { pairs.MoveNext(); }
            catch (Exception e) { pairRejected = e.GetType() == expectedException; }
            try { triples.MoveNext(); }
            catch (Exception e) { tripleRejected = e.GetType() == expectedException; }
            Assert.True(pairRejected);
            Assert.True(tripleRejected);
        }

        [Fact]
        public void QueryResult_CrossThreadCopyScope_PreservesCallingThreadWriteGuard()
        {
            using var world = new World("QueryCrossThreadCopyScope");
            world.EntityManager.NewEntity(typeof(Position), typeof(Velocity), typeof(Health));
            var pairs = world.Query<Position, Velocity>().GetEnumerator();
            var triples = world.Query<Position, Velocity, Health>().GetEnumerator();
            Assert.True(pairs.MoveNext());
            Assert.True(triples.MoveNext());
            var pair = pairs.Current;
            var triple = triples.Current;
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            Exception failure = null;
            var worker = new Thread(() =>
            {
                try
                {
                    using (ComponentTypeManager.BeginCopyPreparation())
                    {
                        Assert.Throws<InvalidOperationException>(() => pair.Comp0.X = 1);
                        Assert.Throws<InvalidOperationException>(() => triple.Comp0.X = 1);
                        entered.Set();
                        Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                    }
                    pair.Comp0.X = 11;
                    triple.Comp0.X += 1;
                }
                catch (Exception error) { failure = error; entered.Set(); }
            });
            worker.Start();
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
                Assert.Null(failure);
                pair.Comp0.X = 7;
                triple.Comp0.X += 1;
            }
            finally
            {
                release.Set();
                Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
            }
            Assert.Null(failure);
            Assert.Equal(12f, pair.Comp0.X);
        }

        [Fact]
        public void QueryWriteGuard_NestedCopyFailure_PreservesOuterScopeAndRecovers()
        {
            using (ComponentTypeManager.BeginCopyPreparation())
            {
                Assert.Throws<InvalidOperationException>((Action)(() =>
                {
                    using var inner = ComponentTypeManager.BeginCopyPreparation();
                    throw new InvalidOperationException("Copy failed.");
                }));
                Assert.Throws<InvalidOperationException>(() => ComponentTypeManager.CheckWritableAccess<Position>());
            }
            ComponentTypeManager.CheckWritableAccess<Position>();
        }

        [Fact]
        public void QueryWriteGuard_OverlappingThreads_KeepRemainingScopeProtected()
        {
            Exception failure = null;
            using (ComponentTypeManager.BeginCopyPreparation())
            {
                var worker = new Thread(() =>
                {
                    try
                    {
                        using (ComponentTypeManager.BeginCopyPreparation())
                            Assert.Throws<InvalidOperationException>(() => ComponentTypeManager.CheckWritableAccess<Position>());
                        ComponentTypeManager.CheckWritableAccess<Position>();
                    }
                    catch (Exception error) { failure = error; }
                });
                worker.Start();
                Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
                Assert.Null(failure);
                Assert.Throws<InvalidOperationException>(() => ComponentTypeManager.CheckWritableAccess<Position>());
            }
            ComponentTypeManager.CheckWritableAccess<Position>();
        }

        [Fact]
        public void ArchetypeColumnLookup_HighTypeId_UsesDictionaryFallback()
        {
            var marker = typeof(int);
            ComponentType registered;
            do
            {
                marker = marker.MakeArrayType();
                registered = ComponentTypeManager.GetComponentType(typeof(SparseColumn<>).MakeGenericType(marker));
            } while (registered.Id < 64);
            ComponentType high = typeof(SparseColumn<int>);
            Assert.True(high.Id >= 64);
            using var archetype = new Archetype(new ComponentType[] { typeof(Position), high });
            Assert.Equal(0, archetype.GetComponentTypeIndex<Position>());
            Assert.Equal(1, archetype.GetComponentTypeIndex(high));
            Assert.Equal(1, archetype.GetComponentTypeIndex<SparseColumn<int>>());
            Assert.Throws<System.Collections.Generic.KeyNotFoundException>(() => archetype.GetComponentTypeIndex<Velocity>());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(65)]
        [InlineData(129)]
        [InlineData(300)]
        public void Query_AcrossArchetypes_RemainsExhausted(int entityCount)
        {
            using var world = new World("QueryExhaustion");
            var em = world.EntityManager;
            for (int i = 0; i < entityCount; i++)
            {
                var entity = i % 2 == 0
                    ? em.NewEntity(typeof(Position), typeof(Velocity), typeof(Health))
                    : em.NewEntity(typeof(Position), typeof(Velocity), typeof(Health), typeof(RelActiveComponent));
                em.Set(entity, new Position { X = i });
            }

            var pair = world.Query<Position, Velocity>().GetEnumerator();
            int pairCount = 0;
            long pairSum = 0;
            while (pair.MoveNext())
            {
                pairCount++;
                pairSum += (long)pair.Current.Comp0.X;
            }
            var triple = world.Query<Position, Velocity, Health>().GetEnumerator();
            int tripleCount = 0;
            long tripleSum = 0;
            while (triple.MoveNext())
            {
                tripleCount++;
                tripleSum += (long)triple.Current.Comp0.X;
            }

            Assert.Equal(entityCount, pairCount);
            Assert.Equal(entityCount, tripleCount);
            Assert.Equal((long)entityCount * (entityCount - 1) / 2, pairSum);
            Assert.Equal(pairSum, tripleSum);
            for (int i = 0; i < 3; i++)
            {
                Assert.False(pair.MoveNext());
                Assert.False(triple.MoveNext());
            }
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(65, 0)]
        [InlineData(65, 1)]
        [InlineData(129, 1)]
        [InlineData(300, 0)]
        [InlineData(300, 1)]
        [InlineData(300, 2)]
        [InlineData(300, 3)]
        [InlineData(300, 4)]
        public void Query_EnabledFilter_RemainsExhausted(int entityCount, int pattern)
        {
            using var world = new World("QueryFilteredExhaustion");
            var em = world.EntityManager;
            int expectedCount = 0;
            long expectedSum = 0;
            for (int i = 0; i < entityCount; i++)
            {
                var entity = em.NewEntity(typeof(Position), typeof(Velocity), typeof(Health), typeof(RelActiveComponent));
                em.Set(entity, new Position { X = i });
                bool enabled = pattern switch
                {
                    1 => i % 2 == 0,
                    2 => i < 128,
                    3 => i % 128 >= 60 && i % 128 < 70,
                    4 => i % 128 != 64,
                    _ => false
                };
                em.SetComponentEnabled<RelActiveComponent>(entity, enabled);
                if (enabled)
                {
                    expectedCount++;
                    expectedSum += i;
                }
            }

            var builder = new QueryBuilder().WithAll<Position, Velocity>().WithAll<Health>().WithEnabled<RelActiveComponent>();
            var pair = new QueryEnumerable<Position, Velocity>(em, builder).GetEnumerator();
            int pairCount = 0;
            long pairSum = 0;
            while (pair.MoveNext())
            {
                pairCount++;
                pairSum += (long)pair.Current.Comp0.X;
            }
            var triple = new QueryEnumerable<Position, Velocity, Health>(em, builder).GetEnumerator();
            int tripleCount = 0;
            long tripleSum = 0;
            while (triple.MoveNext())
            {
                tripleCount++;
                tripleSum += (long)triple.Current.Comp0.X;
            }

            Assert.Equal(expectedCount, pairCount);
            Assert.Equal(expectedCount, tripleCount);
            Assert.Equal(expectedSum, pairSum);
            Assert.Equal(expectedSum, tripleSum);
            for (int i = 0; i < 3; i++)
            {
                Assert.False(pair.MoveNext());
                Assert.False(triple.MoveNext());
            }
        }

        [Fact]
        public void Query_EnabledFilter_NestedEnumeratorsHaveIndependentStateAndNoWarmAllocation()
        {
            using var world = new World("QueryNestedEnabled");
            var em = world.EntityManager;
            for (int i = 0; i < 300; i++)
            {
                var entity = em.NewEntity(typeof(Position), typeof(Velocity), typeof(Health), typeof(RelActiveComponent));
                em.SetComponentEnabled<RelActiveComponent>(entity, i % 128 >= 60 && i % 128 < 70);
            }
            var builder = new QueryBuilder().WithAll<Position, Velocity>().WithAll<Health>().WithEnabled<RelActiveComponent>();
            int Visit()
            {
                int count = 0;
                foreach (var pair in new QueryEnumerable<Position, Velocity>(em, builder))
                    foreach (var triple in new QueryEnumerable<Position, Velocity, Health>(em, builder))
                        count++;
                return count;
            }
            Assert.Equal(400, Visit());
            long before = GC.GetAllocatedBytesForCurrentThread();
            int actual = Visit();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(400, actual);
            Assert.Equal(0, allocated);
        }

        [Fact]
        public void Query_SharedValues_ReuseArchetypeCacheAndKeepFiltersIndependent()
        {
            using var world = new World("QuerySharedCache");
            var em = world.EntityManager;
            for (int material = 0; material < 20; material++)
            {
                var entity = em.NewEntity(new ComponentType[] { typeof(Position), typeof(Velocity), typeof(Health), typeof(Material) },
                    (typeof(Material), (object)new Material(material)));
                em.Set(entity, new Position { X = material });
            }
            for (int material = 0; material < 20; material++)
            {
                int pairs = 0, triples = 0;
                foreach (var result in world.Query<Position, Velocity>().WithShared(new Material(material)))
                {
                    Assert.Equal(material, result.ReadOnlyComp0.X);
                    pairs++;
                }
                var builder = new QueryBuilder().WithAll<Position, Velocity>().WithAll<Health>()
                    .WithShared(new Material(material));
                foreach (var result in new QueryEnumerable<Position, Velocity, Health>(em, builder))
                {
                    Assert.Equal(material, result.ReadOnlyComp0.X);
                    triples++;
                }
                Assert.Equal(1, pairs);
                Assert.Equal(1, triples);
            }
            Assert.Equal(2, world.QueryCacheCount);
        }

        [Fact]
        public void Query_RelationshipTargets_ReuseArchetypeCacheAndKeepFiltersIndependent()
        {
            using var world = new World("QueryRelationshipCache");
            var em = world.EntityManager;
            var targets = new Entity[20];
            for (int i = 0; i < targets.Length; i++)
            {
                targets[i] = em.NewEntity(typeof(Position));
                var source = em.NewEntity(typeof(Position), typeof(Velocity), typeof(Health));
                em.Set(source, new Position { X = i });
                em.AddRelationship<ChildOf>(source, targets[i]);
            }
            for (int i = 0; i < targets.Length; i++)
            {
                int pairs = 0, triples = 0;
                foreach (var result in world.Query<Position, Velocity>().WithRelationship<ChildOf>(targets[i]))
                {
                    Assert.Equal((float)i, result.ReadOnlyComp0.X);
                    pairs++;
                }
                var builder = new QueryBuilder().WithAll<Position, Velocity>().WithAll<Health>()
                    .WithRelationship<ChildOf>(targets[i]);
                foreach (var result in new QueryEnumerable<Position, Velocity, Health>(em, builder))
                {
                    Assert.Equal((float)i, result.ReadOnlyComp0.X);
                    triples++;
                }
                Assert.Equal(1, pairs);
                Assert.Equal(1, triples);
            }
            Assert.Equal(2, world.QueryCacheCount);
        }

        [Fact]
        public void Query_Triple_CombinesSharedAndChangedChunkFilters()
        {
            using var world = new World("QueryTripleChunkFilters");
            var em = world.EntityManager;
            Entity matching = default;
            for (int material = 7; material <= 8; material++)
            {
                var entity = em.NewEntity(new ComponentType[] { typeof(Position), typeof(Velocity), typeof(Health), typeof(Material) },
                    (typeof(Material), (object)new Material(material)));
                if (material == 7) matching = entity;
            }
            var builder = new QueryBuilder().WithAll<Position, Velocity>().WithAll<Health>()
                .WithShared(new Material(7)).WithChanged<Position>();
            int Count()
            {
                int count = 0;
                foreach (var result in new QueryEnumerable<Position, Velocity, Health>(em, builder)) count++;
                return count;
            }
            em.ClearAllChangedBitMasks();
            Assert.Equal(0, Count());
            em.Set(matching, new Position { X = 42 });
            Assert.Equal(1, Count());
            em.ClearAllChangedBitMasks();
            Assert.Equal(0, Count());
        }

        [Fact]
        public void Query_MultipleChunks_VisitsEachEntity()
        {
            using var world = new World("QueryMultipleChunks");
            var em = world.EntityManager;
            for (int i = 0; i < 300; i++)
            {
                var entity = em.NewEntity(typeof(Position), typeof(Velocity), typeof(Health));
                em.Set(entity, new Position { X = i });
            }

            int pairCount = 0;
            long pairSum = 0;
            foreach (var result in world.Query<Position, Velocity>())
            {
                pairCount++;
                pairSum += (long)result.Comp0.X;
            }
            int tripleCount = 0;
            long tripleSum = 0;
            foreach (var result in world.Query<Position, Velocity, Health>())
            {
                tripleCount++;
                tripleSum += (long)result.Comp0.X;
            }

            Assert.Equal(300, pairCount);
            Assert.Equal(300, tripleCount);
            Assert.Equal(44850, pairSum);
            Assert.Equal(pairSum, tripleSum);
        }

        [Fact]
        public void Query_Triple_IteratesMatchingEntities()
        {
            using var world = new World("QueryTuple1");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            // 3 个匹配实体（P+V+H）
            for (int i = 0; i < 3; i++)
            {
                var e = em.NewEntity(typeof(Position), typeof(Velocity), typeof(Health));
                em.Set(e, new Position { X = i, Y = 0 });
                em.Set(e, new Velocity { X = 1, Y = 1 });
                em.Set(e, new Health { Current = 100 });
            }
            // 1 个不匹配实体（只有 P+V）
            em.NewEntity(typeof(Position), typeof(Velocity));

            int count = 0;
            long sumX = 0;
            foreach (var r in world.Query<Position, Velocity, Health>())
            {
                sumX += (long)r.Comp0.X;
                Assert.Equal(1, (int)r.Comp1.X);
                Assert.Equal(100, (int)r.Comp2.Current);
                count++;
            }

            Assert.Equal(3, count);
            Assert.Equal(0 + 1 + 2, sumX); // X = 0,1,2
        }

        [Fact]
        public void Query_Triple_EmptyWorld_NoIteration()
        {
            using var world = new World("QueryTuple2");
            World.DefaultWorld = world;

            int count = 0;
            foreach (var r in world.Query<Position, Velocity, Health>())
            {
                count++;
            }
            Assert.Equal(0, count);
        }
    }
}
