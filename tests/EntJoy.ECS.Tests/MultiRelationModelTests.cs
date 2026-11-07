using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public class MultiRelationModelTests
    {
        [Theory]
        [InlineData(false, 731)] [InlineData(true, 731)]
        [InlineData(false, 1039)] [InlineData(true, 1039)]
        [InlineData(false, 42069)] [InlineData(true, 42069)]
        public void RandomOperations_MatchIndependentEdgesAndEvents(bool bounded, int seed)
        {
            if (bounded) Run<BoundedLinks>(seed, 2); else Run<ListLinks>(seed, int.MaxValue);
        }

        private static void Run<T>(int seed, int capacity) where T : unmanaged, IRelationComponent
        {
            using var world = new World("MultiReferenceModel");
            var em = world.EntityManager;
            var random = new Random(seed);
            var graph = new Dictionary<Entity, HashSet<Entity>>();
            var expected = new List<string>(); var actual = new List<string>();
            bool coherent = true;
            string Event(string kind, Entity source, Entity target) => $"{kind}:{source.Id}/{source.Version}->{target.Id}/{target.Version}";
            void Observe(string kind, ReadOnlySpan<Entity> entities, ReadOnlySpan<T> values)
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    var value = values[i]; var target = Unsafe.As<T, RelationSlot>(ref value).ToEntity();
                    coherent &= em.HasRelationship<T>(entities[i], target) == (kind == "Added");
                    actual.Add(Event(kind, entities[i], target));
                }
            }
            em.AddObserver<T>(ObserverEvents.Added, (entities, values) => Observe("Added", entities, values));
            em.AddObserver<T>(ObserverEvents.Removed, (entities, values) => Observe("Removed", entities, values));
            em.AddObserver<T>(ObserverEvents.Destroyed, (entities, values) => Observe("Destroyed", entities, values));
            Entity New()
            {
                var entity = em.NewEntity(typeof(Position)); graph.Add(entity, new()); return entity;
            }
            void Add(Entity source, Entity target)
            {
                if (!graph[source].Contains(target) && graph[source].Count == capacity)
                {
                    Assert.Throws<InvalidOperationException>(() => em.AddRelationship<T>(source, target)); return;
                }
                if (graph[source].Add(target)) expected.Add(Event("Added", source, target));
                em.AddRelationship<T>(source, target);
            }
            void Remove(Entity source, Entity target)
            {
                if (graph[source].Remove(target)) expected.Add(Event("Removed", source, target));
                em.RemoveRelationship<T>(source, target);
            }
            void Clear(Entity source)
            {
                foreach (var target in graph[source]) expected.Add(Event("Removed", source, target));
                graph[source].Clear(); em.ClearRelationships<T>(source);
            }
            void Delete(Entity source)
            {
                foreach (var target in graph[source]) expected.Add(Event("Destroyed", source, target));
                graph.Remove(source);
                foreach (var pair in graph)
                    if (pair.Value.Remove(source)) expected.Add(Event("Removed", pair.Key, source));
                em.DestroyEntity(source);
            }
            for (int i = 0; i < 24; i++) New();
            for (int step = 0; step < 10000; step++)
            {
                try
                {
                    var entities = graph.Keys.ToArray();
                    var source = entities[random.Next(entities.Length)]; var target = entities[random.Next(entities.Length)];
                    switch (random.Next(8))
                    {
                        case 0: case 1: Add(source, target); break;
                        case 2: Remove(source, target); break;
                        case 3: Clear(source); break;
                        case 4: Delete(source); New(); break;
                        case 5:
                        {
                            var snapshot = em.TakeSnapshot();
                            var saved = graph.ToDictionary(pair => pair.Key, pair => new HashSet<Entity>(pair.Value));
                            Clear(source); em.Restore(snapshot); graph = saved;
                            break;
                        }
                        case 6:
                        {
                            var inputs = source.Equals(target) ? new[] { source } : new[] { source, target };
                            var copies = em.CloneEntities(inputs);
                            var map = inputs.Select((entity, i) => (entity, copies[i])).ToDictionary(pair => pair.entity, pair => pair.Item2);
                            foreach (var input in inputs)
                            {
                                var edges = graph[input].Select(edge => map.TryGetValue(edge, out var mapped) ? mapped : edge).ToHashSet();
                                graph.Add(map[input], edges);
                                foreach (var edge in edges) expected.Add(Event("Added", map[input], edge));
                            }
                            while (graph.Count > 24) Delete(graph.Keys.First());
                            break;
                        }
                        case 7:
                        {
                            int count = world.GetOrCreateEntityQuery(new QueryBuilder().WithAll<Position>().WithRelationship<T>(target)).CalculateEntityCount();
                            Assert.Equal(graph.Count(pair => pair.Value.Contains(target)), count);
                            break;
                        }
                    }
                    foreach (var pair in graph)
                    {
                        Assert.True(pair.Value.SetEquals(em.GetRelationships<T>(pair.Key)));
                        Assert.True(graph.Where(other => other.Value.Contains(pair.Key)).Select(other => other.Key).ToHashSet()
                            .SetEquals(em.GetRelationsOf<T>(pair.Key)));
                    }
                    Assert.True(coherent);
                    Assert.Equal(expected.OrderBy(value => value), actual.OrderBy(value => value));
                    expected.Clear(); actual.Clear();
                    Assert.Equal(0, em.VerifyLocateTable(out _));
                }
                catch (Exception error) { throw new Exception($"Multi-reference model failed: seed={seed}, step={step}, bounded={capacity != int.MaxValue}", error); }
            }
        }
    }
}
