using System;
using System.Collections.Generic;
using System.Linq;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public struct ModelValue : IComponentData { public int Id, Value; }
    public struct ModelExtra : IComponentData { public int Value; }

    public struct ModelIncrementJob : IJobChunk
    {
        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
        {
            var values = chunk.GetComponentDataSpan<ModelValue>();
            for (int i = 0; i < values.Length; i++) values[i].Value++;
        }
    }

    public class EcsReferenceModelTests
    {
        [Theory]
        [InlineData(64, 3)]
        [InlineData(65, 17)]
        [InlineData(127, 101)]
        [InlineData(128, 4099)]
        [InlineData(129, 65537)]
        [InlineData(192, 7919)]
        [InlineData(257, 104729)]
        public void MixedOperations_MatchIndependentManagedModel(int capacity, int seed)
        {
            TestBackend.EnsureInitialized(2);
            int previousCapacity = Archetype.ChunkCapacityOverride;
            Archetype.ChunkCapacityOverride = capacity;
            try
            {
                using var world = new World("ReferenceModel");
                World.DefaultWorld = world;
                var em = world.EntityManager;
                var random = new Random(seed);
                var handles = new Dictionary<int, Entity>();
                var expected = new Dictionary<int, (int Value, int? Extra)>();
                var query = world.GetOrCreateEntityQuery(new QueryBuilder().WithAll<ModelValue>());
                var extraQuery = world.GetOrCreateEntityQuery(new QueryBuilder().WithAll<ModelValue, ModelExtra>());
                int nextId = 0;

                // Start with several chunks before applying seeded structural changes.
                for (int i = 0; i < capacity * 2 + 3; i++) Create();
                for (int step = 0; step < 600; step++)
                {
                    int operation = random.Next(8);
                    int id = expected.Count == 0 ? -1 : expected.Keys.ElementAt(random.Next(expected.Count));
                    if (id < 0 || operation == 0) Create();
                    else if (operation == 1)
                    {
                        em.DestroyEntity(handles[id]);
                        handles.Remove(id);
                        expected.Remove(id);
                    }
                    else if (operation == 2)
                    {
                        int value = random.Next(-10000, 10000);
                        em.SetComponent(handles[id], new ModelValue { Id = id, Value = value });
                        expected[id] = (value, expected[id].Extra);
                    }
                    else if (operation == 3)
                    {
                        int value = random.Next();
                        em.AddComponent(handles[id], new ModelExtra { Value = value });
                        expected[id] = (expected[id].Value, value);
                    }
                    else if (operation == 4 && expected[id].Extra.HasValue)
                    {
                        em.RemoveComponent<ModelExtra>(handles[id]);
                        expected[id] = (expected[id].Value, null);
                    }
                    else if (operation == 5)
                    {
                        using var buffer = new DeferredCommandBuffer();
                        buffer.EnsureParallelWriters(1);
                        buffer.CreateParallelWriter(0).SetComponent(handles[id], new ModelValue { Id = id, Value = step });
                        buffer.PlaybackParallel(em);
                        expected[id] = (step, expected[id].Extra);
                    }
                    else if (operation == 6) em.CompactChunks();
                    else if (operation == 7)
                    {
                        var job = new ModelIncrementJob();
                        var rule = new QueryBuilder().WithAll<ModelValue>();
                        if ((step & 1) == 0) job.Run(rule); else job.Schedule(rule).Complete();
                        foreach (int key in expected.Keys.ToArray())
                            expected[key] = (expected[key].Value + 1, expected[key].Extra);
                    }

                    Verify();
                    if (step % 100 == 99)
                    {
                        var snapshot = world.TakeSnapshot();
                        world.Restore(snapshot);
                        Verify();
                    }
                }

                void Create()
                {
                    int id = nextId++;
                    int value = random.Next(-10000, 10000);
                    var entity = em.NewEntity(typeof(ModelValue));
                    em.SetComponent(entity, new ModelValue { Id = id, Value = value });
                    handles.Add(id, entity);
                    expected.Add(id, (value, null));
                }

                void Verify()
                {
                    Assert.Equal(expected.Count, query.CalculateEntityCount());
                    Assert.Equal(expected.Values.Count(value => value.Extra.HasValue), extraQuery.CalculateEntityCount());
                    foreach (var pair in expected)
                    {
                        var actual = em.GetComponent<ModelValue>(handles[pair.Key]);
                        Assert.Equal(pair.Key, actual.Id);
                        Assert.Equal(pair.Value.Value, actual.Value);
                        if (pair.Value.Extra.HasValue)
                            Assert.Equal(pair.Value.Extra.Value, em.GetComponent<ModelExtra>(handles[pair.Key]).Value);
                    }
                    foreach (var archetype in em.GetAllArchetypes())
                        Assert.Equal(0, archetype.VerifySlabInvariants());
                }
            }
            finally { Archetype.ChunkCapacityOverride = previousCapacity; }
        }
    }
}
