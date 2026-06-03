using EntJoy.Collections;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EntJoy
{
    public sealed unsafe class EntityQuery
    {
        private readonly World _world;
        private readonly QueryBuilder _builder;
        private readonly List<Archetype> _matchingArchetypes = new();
        private readonly List<Chunk> _chunks = new();
        private int _cachedStructuralVersion = -1;

        public EntityQuery(World world, QueryBuilder builder)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _builder = builder;
            Refresh();
        }

        public int StructuralVersion
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                EnsureUpToDate();
                return _cachedStructuralVersion;
            }
        }

        public IReadOnlyList<Archetype> MatchingArchetypes
        {
            get
            {
                EnsureUpToDate();
                return _matchingArchetypes;
            }
        }

        public IReadOnlyList<Chunk> Chunks
        {
            get
            {
                EnsureUpToDate();
                return _chunks;
            }
        }

        public void Refresh()
        {
            _matchingArchetypes.Clear();
            _chunks.Clear();

            var entityManager = _world.EntityManager;
            for (int archetypeIndex = 0; archetypeIndex < entityManager.ArchetypeCount; archetypeIndex++)
            {
                var archetype = entityManager.Archetypes[archetypeIndex];
                if (archetype == null || !archetype.IsMatch(_builder))
                {
                    continue;
                }

                _matchingArchetypes.Add(archetype);
                foreach (var chunk in archetype.GetChunks())
                {
                    if (chunk.EntityCount > 0)
                    {
                        _chunks.Add(chunk);
                    }
                }
            }

            _cachedStructuralVersion = entityManager.StructuralVersion;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureUpToDate()
        {
            if (_cachedStructuralVersion != _world.EntityManager.StructuralVersion)
            {
                Refresh();
            }
        }

        public int CalculateEntityCount()
        {
            EnsureUpToDate();
            int total = 0;
            for (int i = 0; i < _chunks.Count; i++)
            {
                total += _chunks[i].EntityCount;
            }

            return total;
        }

        public NativeArray<T> ToComponentDataArray<T>(Allocator allocator = Allocator.Persistent) where T : unmanaged
        {
            EnsureUpToDate();
            int total = CalculateEntityCount();
            var result = new NativeArray<T>(total, allocator);
            int dstIndex = 0;
            int elementSize = Unsafe.SizeOf<T>();

            for (int archetypeIndex = 0; archetypeIndex < _matchingArchetypes.Count; archetypeIndex++)
            {
                var archetype = _matchingArchetypes[archetypeIndex];
                if (!TryGetComponentTypeIndex<T>(archetype, out int componentIndex))
                {
                    continue;
                }

                foreach (var chunk in archetype.GetChunks())
                {
                    int count = chunk.EntityCount;
                    if (count == 0)
                    {
                        continue;
                    }

                    var srcPtr = (byte*)chunk.GetComponentArrayPointer(componentIndex);
                    var dstPtr = (byte*)result.GetUnsafePtr() + dstIndex * elementSize;
                    Unsafe.CopyBlock(dstPtr, srcPtr, (uint)(count * elementSize));
                    dstIndex += count;
                }
            }

            return result;
        }

        private static bool TryGetComponentTypeIndex<T>(Archetype archetype, out int componentIndex) where T : unmanaged
        {
            var componentType = ComponentTypeManager.GetComponentType(typeof(T));
            var types = archetype.Types;
            for (int i = 0; i < types.Length; i++)
            {
                if (types[i].Id == componentType.Id)
                {
                    componentIndex = i;
                    return true;
                }
            }

            componentIndex = -1;
            return false;
        }
    }
}
