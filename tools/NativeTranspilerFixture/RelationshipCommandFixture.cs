using EntJoy.ECS;
using EntJoy.ECS.JobSystem;

namespace NativeTranspilerFixture;

public struct RecordRelationshipTargets : IJobEntity
{
    public ParallelWriter Writer;
    public Entity Destination;
    public void Execute(Entity source, in ReadLink link)
    {
        Writer.SetRelationshipTarget<ReadLink>(source.Id, source, Destination);
    }
}

public static class RelationshipCommandFixture
{
    public static void Run()
    {
        int previous = Archetype.ChunkCapacityOverride;
        Archetype.ChunkCapacityOverride = 32;
        try
        {
            using var world = new World("RelationshipCommandConsumer");
            using var ecb = new DeferredCommandBuffer();
            var em = world.EntityManager;
            var a = em.NewEntity(Array.Empty<ComponentType>());
            var b = em.NewEntity(Array.Empty<ComponentType>());
            var sources = new Entity[129];
            for (int i = 0; i < sources.Length; i++)
            {
                sources[i] = em.NewEntity(typeof(ReadLink), typeof(ReadLinkOutput));
                em.SetComponent(sources[i], new ReadLink { Target = RelationSlot.From(i % 3 == 0 ? b : a), Weight = i * 2 });
            }
            ecb.EnsureParallelWriters(1);
            var writer = ecb.CreateSharedParallelWriter(0, sources.Length * 32, 1);
            var query = new QueryBuilder().WithAll<ReadLink>().WithRelationship<ReadLink>(a);
            new RecordRelationshipTargets { Writer = writer, Destination = b }.Schedule(query, world).Complete();
            if (em.GetRelationsOf<ReadLink>(a).Length != 86) throw new Exception("Recording mutated the relationship graph.");
            ecb.PlaybackParallel(em);
            if (em.GetRelationsOf<ReadLink>(b).Length != sources.Length || em.GetRelationsOf<ReadLink>(a).Length != 0)
                throw new Exception("ECB retarget produced the wrong reverse index.");
            for (int i = 0; i < sources.Length; i++)
            {
                var link = em.GetComponentReadOnly<ReadLink>(sources[i]);
                if (!link.Target.ToEntity().Equals(b) || link.Weight != i * 2) throw new Exception("ECB retarget did not preserve its payload.");
                if (i % 2 == 0) writer.RemoveRelationship<ReadLink>(sources.Length - i, sources[i]);
            }
            ecb.PlaybackParallel(em);
            if (em.GetRelationsOf<ReadLink>(b).Length != 64) throw new Exception("ECB unlink produced the wrong edge set.");
            var stale = em.NewEntity(Array.Empty<ComponentType>());
            ecb.SetRelationshipTarget<ReadLink>(sources[1], a);
            ecb.SetRelationshipTarget<ReadLink>(sources[3], stale);
            ecb.SetRelationshipTarget<ReadLink>(sources[5], a);
            em.DestroyEntity(stale);
            try { ecb.Playback(em); throw new Exception("Expected target validation to fail."); }
            catch (CommandPlaybackException failure)
            {
                if (failure.CommandIndex != 1 || failure.UnexecutedCommandCount != 1) throw;
            }
            if (!em.GetRelationship<ReadLink>(sources[1]).Equals(a) || !em.GetRelationship<ReadLink>(sources[5]).Equals(b))
                throw new Exception("ECB failure did not stop at its failing command.");
            em.RemoveRelationship<ReadLink>(sources[1]);
            ecb.Playback(em);
            if (em.HasRelationship<ReadLink>(sources[1])) throw new Exception("ECB replayed an already committed prefix.");
            Console.WriteLine("PASS: Job-recorded ECB target-set/unlink, 129 preserved payloads, reverse index, and failure consumption");
        }
        finally { Archetype.ChunkCapacityOverride = previous; }
    }
}
