using System;
using System.Linq;
using System.Threading;
using EntJoy.Collections;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public struct RelationReadEnabled : IComponentData, IEnableableComponent { }

    public struct RelationValueCopyJob : IJobEntity
    {
        public void Execute(ref Position position, LifecycleRelation relationship)
        {
            relationship.Target = RelationSlot.Default;
            position.X = relationship.Target.TargetId;
        }
    }

    public struct RelationReadOnlyParamJob : IJobEntity
    {
        public void Execute(ref Position position, in LifecycleRelation relationship)
        {
            position.X = relationship.Target.TargetId;
        }
    }

    public unsafe class RelationReadTests
    {
        private sealed class Fixture : IDisposable
        {
            internal readonly World World = new("RelationRead" + Guid.NewGuid().ToString("N"));
            internal EntityManager Em => World.EntityManager;
            internal readonly Entity Source, A, B;
            internal Fixture()
            {
                A = Em.NewEntity(typeof(Position));
                B = Em.NewEntity(typeof(Position));
                Source = Em.NewEntity(typeof(Position), typeof(Velocity), typeof(LifecycleRelation));
                Em.AddRelationship<LifecycleRelation>(Source, A);
            }
            public void Dispose() => World.Dispose();
        }

        [Fact]
        public void WritableReferences_RejectRelationshipColumnsIncludingPrefixReinterpretation()
        {
            using var f = new Fixture();
            Assert.Throws<InvalidOperationException>(() => { _ = f.Em.GetComponent<LifecycleRelation>(f.Source); });
            var lookup = f.Em.GetComponentLookup<LifecycleRelation>();
            Assert.Throws<InvalidOperationException>(() => { _ = lookup[f.Source]; });
            Assert.Throws<InvalidOperationException>(() => { _ = lookup.UnsafeRef(f.Source); });
            var info = f.Em.GetEntityInfoRef(f.Source.Id);
            var arch = info.Archetype;
            var chunk = arch.ChunkList[info.ChunkIndex];
            int column = arch.GetComponentTypeIndex(typeof(LifecycleRelation));
            Assert.Throws<InvalidOperationException>(() => { _ = chunk.GetComponent<LifecycleRelation>(info.SlotInChunk, column); });
            Assert.Throws<InvalidOperationException>(() => { _ = chunk.GetComponent<RelationSlot>(info.SlotInChunk, column); });
            Assert.Throws<InvalidOperationException>(() => { _ = arch.GetComponent<LifecycleRelation>(info.ChunkIndex, info.SlotInChunk); });
            Assert.Throws<InvalidOperationException>(() => arch.Set(info.ChunkIndex, info.SlotInChunk,
                new LifecycleRelation { Target = RelationSlot.From(f.B) }));
            Assert.Throws<InvalidOperationException>(() => arch.SetRaw(info.ChunkIndex, info.SlotInChunk,
                typeof(LifecycleRelation), new LifecycleRelation { Target = RelationSlot.From(f.B) }));
            Assert.Equal(f.A, f.Em.GetRelationship<LifecycleRelation>(f.Source));
        }

        [Fact]
        public void ReadOnlyValues_AreCopiesAndNormalComponentsStayWritable()
        {
            using var f = new Fixture();
            var copy = f.Em.GetComponentReadOnly<LifecycleRelation>(f.Source);
            copy.Target = RelationSlot.From(f.B);
            Assert.Equal(f.A, f.Em.GetRelationship<LifecycleRelation>(f.Source));
            var position = f.Em.GetComponentLookup<Position>();
            position[f.Source].X = 81;
            Assert.Equal(81f, f.Em.GetComponentReadOnly<Position>(f.Source).X);
            Assert.Equal(f.A.Id, f.Em.GetReadOnlyComponentLookup<LifecycleRelation>()[f.Source].Target.TargetId);
        }

        [Theory]
        [InlineData("retarget")]
        [InlineData("structure")]
        [InlineData("restore")]
        [InlineData("dispose")]
        public void ReadOnlyLookup_RejectsExpiredBorrow(string change)
        {
            using var f = new Fixture();
            var lookup = f.Em.GetReadOnlyComponentLookup<LifecycleRelation>();
            if (change == "retarget") f.Em.AddRelationship<LifecycleRelation>(f.Source, f.B);
            else if (change == "structure") f.Em.NewEntity(typeof(Position));
            else if (change == "restore") f.Em.Restore(f.Em.TakeSnapshot());
            else f.World.Dispose();
            Assert.ThrowsAny<InvalidOperationException>(() => { _ = lookup[f.Source]; });
        }

        [Fact]
        public void HeldReadOnlyQueryResult_RejectsWorldDisposalAtMoveNext()
        {
            using var f = new Fixture();
            var cursor = f.World.Query<Position>().WithRelationship<LifecycleRelation>(f.A).GetEnumerator();
            Assert.True(cursor.MoveNext());
            var result = cursor.Current;
            Assert.Equal(f.A.Id, result.ReadOnlyComp1.Target.TargetId);
            f.World.Dispose();
            bool rejected = false;
            try { cursor.MoveNext(); }
            catch (ObjectDisposedException) { rejected = true; }
            catch (InvalidOperationException) { rejected = true; }
            Assert.True(rejected);
            var empty = default(ReadOnlyComponentLookup<LifecycleRelation>);
            Assert.Throws<InvalidOperationException>(() => { _ = empty[f.Source]; });
        }

        [Fact]
        public void ReadOnlyLookup_ValidatesTheSourceGenerationAndAllowsEmptyColumns()
        {
            using var f = new Fixture();
            var old = f.Em.NewEntity(typeof(Position), typeof(LifecycleRelation));
            f.Em.DestroyEntity(old);
            var fresh = f.Em.NewEntity(typeof(Position), typeof(LifecycleRelation));
            Assert.Equal(old.Id, fresh.Id);
            var lookup = f.Em.GetReadOnlyComponentLookup<LifecycleRelation>();
            Assert.Throws<InvalidOperationException>(() => { _ = lookup[old]; });
            Assert.Equal(-1, lookup[fresh].Target.TargetId);
        }

        [Fact]
        public void ChunkReadOnlyAccess_RejectsMutableViewsAndChecksItsBorrowVersion()
        {
            using var f = new Fixture();
            var info = f.Em.GetEntityInfoRef(f.Source.Id);
            var chunk = new ArchetypeChunk(info.Archetype.ChunkList[info.ChunkIndex]);
            Assert.Throws<InvalidOperationException>(() => { chunk.GetComponentDataSpan<LifecycleRelation>(); });
            Assert.Throws<InvalidOperationException>(() => { chunk.GetComponentDataNativeArray<LifecycleRelation>(); });
            Assert.Equal(f.A.Id, chunk.GetComponentReadOnlyDataSpan<LifecycleRelation>()[info.SlotInChunk].Target.TargetId);
            Assert.Equal(f.A.Id, chunk.GetComponentReadOnlyDataNativeArray<LifecycleRelation>()[info.SlotInChunk].Target.TargetId);
            chunk.GetComponentDataSpan<Position>()[info.SlotInChunk].X = 21;
            f.Em.AddRelationship<LifecycleRelation>(f.Source, f.B);
            Assert.Throws<InvalidOperationException>(() => { chunk.GetComponentReadOnlyDataSpan<LifecycleRelation>(); });
        }

        [Fact]
        public void EntityQueryResult_ProvidesReadOnlyRelationshipsAndKeepsOtherColumnsWritable()
        {
            using var f = new Fixture();
            var cursor = f.World.Query<Position>().WithRelationship<LifecycleRelation>(f.A).GetEnumerator();
            Assert.True(cursor.MoveNext());
            var result = cursor.Current;
            Assert.Equal(f.A.Id, result.ReadOnlyComp1.Target.TargetId);
            Assert.Throws<InvalidOperationException>(() => { _ = result.Comp1.Target; });
            result.Comp0.X = 71;
            Assert.Equal(71f, f.Em.GetComponentReadOnly<Position>(f.Source).X);
            f.Em.AddRelationship<LifecycleRelation>(f.Source, f.B);
            bool invalidated = false;
            try { cursor.MoveNext(); } catch (InvalidOperationException) { invalidated = true; }
            Assert.True(invalidated);
        }

        [Fact]
        public void GeneratedTripleQuery_GuardsItsRelationshipColumnAndHeldResult()
        {
            using var f = new Fixture();
            var cursor = f.World.Query<Position, Velocity, LifecycleRelation>().GetEnumerator();
            Assert.True(cursor.MoveNext());
            var result = cursor.Current;
            Assert.Equal(f.A.Id, result.ReadOnlyComp2.Target.TargetId);
            Assert.Throws<InvalidOperationException>(() => { _ = result.Comp2.Target; });
            f.Em.AddRelationship<LifecycleRelation>(f.Source, f.B);
            bool invalidated = false;
            try { cursor.MoveNext(); } catch (InvalidOperationException) { invalidated = true; }
            Assert.True(invalidated);
        }

        [Fact]
        public void ChunkQueries_ExposeReadOnlySpansForTwoAndGeneratedThreeColumns()
        {
            using var f = new Fixture();
            var pair = f.World.QueryChunks<Position, LifecycleRelation>().GetEnumerator();
            Assert.True(pair.MoveNext());
            var pairResult = pair.Current;
            Assert.Equal(f.A.Id, pairResult.GetReadOnlySpan1()[0].Target.TargetId);
            bool rejected = false;
            try { pairResult.GetSpan1(); } catch (InvalidOperationException) { rejected = true; }
            Assert.True(rejected);
            var triple = f.World.QueryChunks<Position, Velocity, LifecycleRelation>().GetEnumerator();
            Assert.True(triple.MoveNext());
            var tripleResult = triple.Current;
            Assert.Equal(f.A.Id, tripleResult.GetReadOnlySpan2()[0].Target.TargetId);
            rejected = false;
            try { tripleResult.GetSpan2(); } catch (InvalidOperationException) { rejected = true; }
            Assert.True(rejected);
            f.Em.AddRelationship<LifecycleRelation>(f.Source, f.B);
            bool invalidated = false;
            try { _ = tripleResult.Length; } catch (InvalidOperationException) { invalidated = true; }
            Assert.True(invalidated);
        }

        [Fact]
        public void OrdinaryQueryAndLookup_DoNotExpireOnUnrelatedValueOnlyRelationshipChanges()
        {
            using var f = new Fixture();
            var lookup = f.Em.GetReadOnlyComponentLookup<Position>();
            var cursor = f.World.Query<Position, Velocity>().GetEnumerator();
            Assert.True(cursor.MoveNext());
            var result = cursor.Current;
            f.Em.AddRelationship<LifecycleRelation>(f.Source, f.B);
            result.Comp0.X = 9;
            Assert.Equal(9f, lookup[f.Source].X);
            Assert.False(cursor.MoveNext());
        }

        [Fact]
        public void CachedCountsAndArrays_ApplyRelationshipAndEnabledFiltersAcrossChunks()
        {
            using var world = new World("RelationshipArrayFilters");
            var em = world.EntityManager;
            var a = em.NewEntity(typeof(Position));
            var b = em.NewEntity(typeof(Position));
            var entities = new Entity[300];
            for (int i = 0; i < entities.Length; i++)
            {
                var source = entities[i] = em.NewEntity(typeof(Position), typeof(LifecycleRelation), typeof(RelationReadEnabled));
                em.SetComponent(source, new Position { X = i });
                em.AddRelationship<LifecycleRelation>(source, i % 3 == 0 ? a : b);
                em.SetComponentEnabled<RelationReadEnabled>(source, i % 5 != 0);
            }
            var builder = new QueryBuilder().WithAll<Position>().WithRelationship<LifecycleRelation>(a).WithEnabled<RelationReadEnabled>();
            var query = world.GetOrCreateEntityQuery(builder);
            Assert.Equal(300, query.Chunks.Sum(chunk => chunk.EntityCount));
            Assert.Equal(80, query.CalculateEntityCount());
            using (var values = query.ToComponentDataArray<Position>())
                Assert.Equal(Enumerable.Range(0, 300).Where(i => i % 3 == 0 && i % 5 != 0).Select(i => (float)i),
                    values.ToArray().Select(value => value.X).OrderBy(value => value));
            int structuralVersion = em.StructuralVersion;
            em.AddRelationship<LifecycleRelation>(entities[1], a);
            Assert.Equal(structuralVersion, em.StructuralVersion);
            Assert.Equal(81, query.CalculateEntityCount());
            em.SetComponentEnabled<RelationReadEnabled>(entities[1], false);
            Assert.Equal(80, query.CalculateEntityCount());
        }

        [Fact]
        public void RecycledTarget_DoesNotMatchAnOldQueryIdentity()
        {
            using var f = new Fixture();
            var query = f.World.GetOrCreateEntityQuery(new QueryBuilder().WithAll<Position>().WithRelationship<LifecycleRelation>(f.A));
            Assert.Equal(1, query.CalculateEntityCount());
            f.Em.DestroyEntity(f.A);
            var replacement = f.Em.NewEntity(typeof(Position));
            Assert.Equal(f.A.Id, replacement.Id);
            f.Em.AddRelationship<LifecycleRelation>(f.Source, replacement);
            Assert.Equal(0, query.CalculateEntityCount());
            using var values = query.ToComponentDataArray<Position>();
            Assert.Equal(0, values.Length);
            var cursor = f.World.Query<Position>().WithRelationship<LifecycleRelation>(f.A).GetEnumerator();
            Assert.False(cursor.MoveNext());
        }

        [Fact]
        public void ReverseCopies_DeduplicateAcrossTypesAndSupportShortOrZeroCapacityBuffers()
        {
            using var f = new Fixture();
            var other = f.Em.NewEntity(typeof(Position));
            f.Em.AddRelationship<LifecycleRelation>(other, f.A);
            f.Em.AddRelationship<ChildOf>(f.Source, f.A);
            Assert.Equal(2, f.Em.GetRelationsOfAll(f.A).Length);
            Span<Entity> shortBuffer = stackalloc Entity[1];
            Assert.Equal(2, f.World.GetRelationsOf<LifecycleRelation>(f.A, shortBuffer));
            Assert.Contains(shortBuffer[0], new[] { f.Source, other });
            Span<Entity> enough = stackalloc Entity[2];
            Assert.Equal(2, f.Em.GetRelationsOf<LifecycleRelation>(f.A, enough));
            using var native = new NativeList<Entity>(0);
            f.World.CopyRelationsOf<LifecycleRelation>(f.A, native);
            Assert.Equal(2, native.Length);
            f.Em.RemoveRelationship<LifecycleRelation>(other);
            Assert.Equal(2, native.Length);
            f.Em.CopyRelationsOf<LifecycleRelation>(f.B, native);
            Assert.Equal(0, native.Length);
        }

        [Fact]
        public void WarmSpanCopies_AllocateNoManagedMemory()
        {
            using var f = new Fixture();
            Span<Entity> buffer = stackalloc Entity[4];
            for (int i = 0; i < 20; i++) f.Em.GetRelationsOf<LifecycleRelation>(f.A, buffer);
            long before = GC.GetAllocatedBytesForCurrentThread();
            int total = 0;
            for (int i = 0; i < 100; i++) total += f.Em.GetRelationsOf<LifecycleRelation>(f.A, buffer);
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(100, total);
            Assert.Equal(before, after);
        }

        [Fact]
        public void ManagedEntityJob_ReadOnlyAndValueParametersDoNotExposeRelationWrites()
        {
            using var f = new Fixture();
            var query = new QueryBuilder().WithAll<Position, LifecycleRelation>();
            new RelationValueCopyJob().Run(query, f.World);
            Assert.Equal(-1f, f.Em.GetComponentReadOnly<Position>(f.Source).X);
            Assert.Equal(f.A, f.Em.GetRelationship<LifecycleRelation>(f.Source));
            new RelationReadOnlyParamJob().Run(query, f.World);
            Assert.Equal((float)f.A.Id, f.Em.GetComponentReadOnly<Position>(f.Source).X);
        }

        [Fact]
        public void RegisteredJobChunk_IsNotExpiredByAnUnrelatedStructuralChange()
        {
            TestBackend.EnsureInitialized(2);
            using var world = new World("UnrelatedStructuralChange");
            var em = world.EntityManager;
            em.NewEntity(typeof(Velocity));
            var unrelated = em.NewEntity(typeof(Health));
            UnrelatedRelationReaderJob.Started.Reset();
            UnrelatedRelationReaderJob.Release.Reset();
            UnrelatedRelationReaderJob.Finished = 0;
            UnrelatedRelationReaderJob.ObservedCount = 0;
            var handle = new UnrelatedRelationReaderJob().Schedule(new QueryBuilder().WithAll<Velocity>(), world);
            try
            {
                Assert.True(UnrelatedRelationReaderJob.Started.Wait(TimeSpan.FromSeconds(3)));
                em.DestroyEntity(unrelated);
                Assert.Equal(0, Volatile.Read(ref UnrelatedRelationReaderJob.Finished));
            }
            finally { UnrelatedRelationReaderJob.Release.Set(); handle.Complete(); }
            Assert.Equal(1, UnrelatedRelationReaderJob.ObservedCount);
        }
    }
}
