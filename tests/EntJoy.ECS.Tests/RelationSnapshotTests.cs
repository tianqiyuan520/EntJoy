using System;
using System.IO;
using System.Linq;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public unsafe class RelationSnapshotTests
    {
        private static WorldSnapshot ValidSnapshot(out Entity a, out Entity b, out Entity c, out Entity d)
        {
            using var world = new World("SnapshotSource");
            var em = world.EntityManager;
            Entity Create() => em.NewEntity(typeof(Position), typeof(LifecycleRelation), typeof(LifecycleParent), typeof(OccupiedBy));
            a = Create(); b = Create(); c = Create(); d = Create();
            em.SetComponent(a, new Position { X = 17 });
            em.SetComponent(a, new LifecycleRelation { Target = RelationSlot.From(b), Data = 37 });
            em.AddRelationship<LifecycleParent>(a, b); em.AddRelationship<LifecycleParent>(b, c);
            em.AddRelationship<OccupiedBy>(a, b); em.AddRelationship<OccupiedBy>(c, d);
            return em.TakeSnapshot();
        }

        private static WorldSnapshot Rewrite<TRel>(WorldSnapshot snapshot, Entity source, RelationSlot target) where TRel : struct, IRelationComponent
        {
            var data = (byte[])snapshot.Data.Clone();
            using var stream = new MemoryStream(data, writable: true);
            using var reader = new BinaryReader(stream);
            reader.ReadChars(8); reader.ReadInt32();
            var signatures = new (int Id, int Size)[reader.ReadInt32()][];
            for (int a = 0; a < signatures.Length; a++)
            {
                signatures[a] = new (int, int)[reader.ReadInt32()];
                for (int c = 0; c < signatures[a].Length; c++)
                {
                    signatures[a][c] = (reader.ReadInt32(), reader.ReadInt32());
                    reader.ReadString(); reader.ReadBytes(16);
                }
            }
            int count = reader.ReadInt32(), typeId = ComponentTypeManager.GetComponentType(typeof(TRel)).Id;
            for (int i = 0; i < count; i++)
            {
                int id = reader.ReadInt32(), version = reader.ReadInt32(), arch = reader.ReadInt32();
                foreach (var type in signatures[arch])
                {
                    if (id == source.Id && version == source.Version && type.Id == typeId)
                    {
                        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
                        writer.Write(target.TargetId); writer.Write(target.TargetVersion);
                        return new WorldSnapshot(data);
                    }
                    stream.Position += type.Size;
                }
            }
            throw new Exception("Relationship payload was not found in the snapshot.");
        }

        [Theory]
        [InlineData("missing")]
        [InlineData("version")]
        [InlineData("empty-id")]
        [InlineData("empty-version")]
        [InlineData("self-cycle")]
        [InlineData("cycle")]
        [InlineData("exclusive")]
        [InlineData("truncated")]
        [InlineData("trailing")]
        public void InvalidSnapshot_PreservesTheExistingGraphResourcesAndViews(string corruption)
        {
            var valid = ValidSnapshot(out var a, out var b, out var c, out _);
            var bad = corruption switch
            {
                "missing" => Rewrite<LifecycleRelation>(valid, a, new RelationSlot { TargetId = 9999, TargetVersion = 0 }),
                "version" => Rewrite<LifecycleRelation>(valid, a, RelationSlot.From(new Entity { Id = b.Id, Version = b.Version + 1 })),
                "empty-id" => Rewrite<LifecycleRelation>(valid, c, new RelationSlot { TargetId = -2, TargetVersion = -1 }),
                "empty-version" => Rewrite<LifecycleRelation>(valid, c, new RelationSlot { TargetId = -1, TargetVersion = 0 }),
                "self-cycle" => Rewrite<LifecycleParent>(valid, a, RelationSlot.From(a)),
                "cycle" => Rewrite<LifecycleParent>(valid, c, RelationSlot.From(a)),
                "exclusive" => Rewrite<OccupiedBy>(valid, c, RelationSlot.From(b)),
                "truncated" => new WorldSnapshot(valid.Data[..^1]),
                "trailing" => new WorldSnapshot(valid.Data.Concat(new byte[] { 1 }).ToArray()),
                _ => throw new Exception("Unknown corruption")
            };
            CloneOwned.Reset();
            using (var world = new World("SnapshotAtomicity"))
            {
                var em = world.EntityManager;
                var source = em.NewEntity(typeof(Position), typeof(CloneOwned));
                var target = em.NewEntity(typeof(Position));
                em.SetComponent(source, new Position { X = 99 });
                em.SetComponent(source, CloneOwned.Create(81));
                em.AddRelationship<LifecycleRelation>(source, target);
                var lookup = em.GetReadOnlyComponentLookup<Position>();
                int version = em.StructuralVersion, count = em.EntityCount;
                long relationships = em.RelationshipVersion;
                Assert.Throws<InvalidDataException>(() => em.Restore(bad));
                Assert.Equal(version, em.StructuralVersion); Assert.Equal(relationships, em.RelationshipVersion);
                Assert.Equal(count, em.EntityCount); Assert.Equal(99f, lookup[source].X);
                Assert.Equal(target, em.GetRelationship<LifecycleRelation>(source));
                Assert.Equal(new[] { source }, em.GetRelationsOf<LifecycleRelation>(target));
                Assert.Equal(1, CloneOwned.Live); Assert.Equal(0, CloneOwned.Disposals);
                Assert.Equal(81, *(int*)em.GetComponentReadOnly<CloneOwned>(source).Pointer);
                em.Restore(valid);
                Assert.Equal(0, CloneOwned.Live); Assert.Equal(1, CloneOwned.Disposals);
                Assert.Equal(b, em.GetRelationship<LifecycleRelation>(a));
                Assert.Equal(37, em.GetComponentReadOnly<LifecycleRelation>(a).Data);
                Assert.Equal(0, em.VerifyLocateTable(out _));
            }
            Assert.Equal(0, CloneOwned.Live);
        }

        [Fact]
        public void Restore_PublishesACompleteIndexWithoutObserverReplayAndInvalidatesViews()
        {
            var snapshot = ValidSnapshot(out var a, out var b, out var c, out var d);
            using var world = new World("SnapshotPublish");
            var em = world.EntityManager;
            var old = em.NewEntity(typeof(Position));
            int notifications = 0;
            em.AddObserver<Position>(ObserverEvents.Added | ObserverEvents.Destroyed, (_, _) => notifications++);
            em.AddObserver<LifecycleRelation>(ObserverEvents.Added, (_, _) => notifications++);
            var lookup = em.GetReadOnlyComponentLookup<Position>();
            var query = world.GetOrCreateEntityQuery(new QueryBuilder().WithAll<Position>());
            Assert.Equal(1, query.CalculateEntityCount());
            em.Restore(snapshot);
            Assert.Equal(0, notifications);
            Assert.Equal(4, query.CalculateEntityCount());
            Assert.Throws<InvalidOperationException>(() => { _ = lookup[old]; });
            Assert.Equal(new[] { a }, em.GetRelationsOf<LifecycleRelation>(b));
            Assert.Equal(new[] { a }, em.GetRelationsOf<OccupiedBy>(b));
            Assert.Equal(new[] { c }, em.GetRelationsOf<OccupiedBy>(d));
            Assert.Equal(new[] { a }, em.GetRelationsOf<LifecycleParent>(b));
            Assert.Equal(new[] { b }, em.GetRelationsOf<LifecycleParent>(c));
            em.DestroyEntity(b);
            Assert.False(em.HasRelationship<LifecycleRelation>(a));
            Assert.False(em.HasRelationship<OccupiedBy>(a));
            Assert.False(em.HasRelationship<LifecycleParent>(a));
            Assert.Equal(0, em.VerifyLocateTable(out _));
        }

        [Fact]
        public void OrdinaryCycles_AreValidAndKeepTheirTopology()
        {
            using var world = new World("OrdinaryCycleSnapshot");
            var em = world.EntityManager;
            var a = em.NewEntity(typeof(Position)); var b = em.NewEntity(typeof(Position));
            em.AddRelationship<LifecycleRelation>(a, b); em.AddRelationship<LifecycleRelation>(b, a);
            var snapshot = em.TakeSnapshot();
            var copies = em.CloneEntities(new[] { a, b });
            Assert.Equal(copies[1], em.GetRelationship<LifecycleRelation>(copies[0]));
            Assert.Equal(copies[0], em.GetRelationship<LifecycleRelation>(copies[1]));
            em.Restore(snapshot);
            Assert.Equal(b, em.GetRelationship<LifecycleRelation>(a));
            Assert.Equal(a, em.GetRelationship<LifecycleRelation>(b));
        }

        [Fact]
        public void TakeSnapshot_RejectsInvalidForwardDataRatherThanSerializingIt()
        {
            using var world = new World("SnapshotForwardValidation");
            var em = world.EntityManager;
            var source = em.NewEntity(typeof(Position), typeof(LifecycleRelation));
            var info = em.GetEntityInfoRef(source.Id);
            var chunk = info.Archetype.ChunkList[info.ChunkIndex];
            var relation = (LifecycleRelation*)chunk.GetComponentArrayPointer(info.Archetype.GetComponentTypeIndex<LifecycleRelation>());
            relation[info.SlotInChunk].Target = new RelationSlot { TargetId = -1, TargetVersion = 0 };
            Assert.Throws<InvalidDataException>(() => em.TakeSnapshot());
            relation[info.SlotInChunk].Target = RelationSlot.Default;
            Assert.NotNull(em.TakeSnapshot());
        }

        [Fact]
        public void OldResourceDisposalFailure_DoesNotDamageThePublishedSnapshot()
        {
            var snapshot = ValidSnapshot(out var a, out var b, out _, out _);
            CloneOwned.Reset();
            using (var world = new World("SnapshotOldResourceDisposal"))
            {
                var em = world.EntityManager;
                var old = em.NewEntity(typeof(CloneOwned));
                em.SetComponent(old, CloneOwned.Create(9));
                CloneOwned.ThrowOnDispose = true;
                CloneOwned.DuringDispose = () => world.Dispose();
                try { em.Restore(snapshot); }
                finally { CloneOwned.ThrowOnDispose = false; CloneOwned.DuringDispose = null; }
                Assert.Equal(0, CloneOwned.Live); Assert.Equal(1, CloneOwned.Disposals);
                Assert.Equal(b, em.GetRelationship<LifecycleRelation>(a));
                var parent = em.GetRelationship<LifecycleParent>(b);
                var clone = Assert.Single(em.CloneEntities(new[] { b }));
                Assert.Equal(parent, em.GetRelationship<LifecycleParent>(clone));
            }
        }
    }
}
