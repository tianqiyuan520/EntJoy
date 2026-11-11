using EntJoy.JobSystem;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace EntJoy.ECS.JobSystem
{
    public static unsafe partial class ChunkJobScheduler
    {
        internal readonly struct RawChunkScheduleCacheKey : IEquatable<RawChunkScheduleCacheKey>
        {
            private readonly EntityManager _entityManager;
            private readonly int _managerHash;
            private readonly int _queryHash;
            private readonly int _requiredHash;
            private readonly QueryBuilder _query;
            private readonly int[] _requiredTypes;
            private readonly int _mode;

            public RawChunkScheduleCacheKey(EntityManager entityManager, QueryBuilder query, int[] requiredTypes, int mode)
            {
                _entityManager = entityManager;
                _managerHash = RuntimeHelpers.GetHashCode(entityManager);
                _query = query;
                _requiredTypes = requiredTypes;
                _queryHash = GetQueryHash(query);
                _requiredHash = GetRequiredComponentHash(requiredTypes);
                _mode = mode;
            }

            public bool Equals(RawChunkScheduleCacheKey other)
                => ReferenceEquals(_entityManager, other._entityManager) &&
                   _queryHash == other._queryHash &&
                   _requiredHash == other._requiredHash &&
                   _mode == other._mode &&
                   SameTypes(_query.All, other._query.All) &&
                   SameTypes(_query.Any, other._query.Any) &&
                   SameTypes(_query.None, other._query.None) &&
                   SameTypes(_query.AllEnabled, other._query.AllEnabled) &&
                   SameTypes(_query.ChangedComponents, other._query.ChangedComponents) &&
                   _query.LimitCount == other._query.LimitCount &&
                   _query.MinChangedVersion == other._query.MinChangedVersion &&
                   _query.HasSharedFilter == other._query.HasSharedFilter &&
                   (!_query.HasSharedFilter || (_query.SharedFilterType.Id == other._query.SharedFilterType.Id &&
                       object.Equals(_query.SharedFilterValue, other._query.SharedFilterValue))) &&
                   _query.HasRelationshipFilter == other._query.HasRelationshipFilter &&
                   (!_query.HasRelationshipFilter || (_query.RelationshipFilterType.Id == other._query.RelationshipFilterType.Id &&
                       _query.RelationshipFilterTarget.TargetId == other._query.RelationshipFilterTarget.TargetId &&
                       _query.RelationshipFilterTarget.TargetVersion == other._query.RelationshipFilterTarget.TargetVersion)) &&
                   _requiredTypes.AsSpan().SequenceEqual(other._requiredTypes);

            private static bool SameTypes(ComponentType[] first, ComponentType[] second)
            {
                if (ReferenceEquals(first, second)) return true;
                int length = first?.Length ?? 0;
                if (length != (second?.Length ?? 0)) return false;
                for (int i = 0; i < length; i++)
                    if (first[i].Id != second[i].Id) return false;
                return true;
            }

            // Stored keys own their rule arrays. Lookup keys borrow them without
            // allocation; only a cache miss takes this snapshot.
            public RawChunkScheduleCacheKey Capture()
            {
                var query = _query;
                query.All = Clone(query.All);
                query.Any = Clone(query.Any);
                query.None = Clone(query.None);
                query.AllEnabled = Clone(query.AllEnabled);
                query.ChangedComponents = Clone(query.ChangedComponents);
                return new RawChunkScheduleCacheKey(_entityManager, query, Clone(_requiredTypes), _mode);
            }

            private static T[] Clone<T>(T[] values)
                => values == null || values.Length == 0 ? values : (T[])values.Clone();

            public bool Matches(EntityManager entityManager)
                => ReferenceEquals(_entityManager, entityManager);

            public override bool Equals(object obj)
                => obj is RawChunkScheduleCacheKey other && Equals(other);

            public override int GetHashCode()
                => HashCode.Combine(_managerHash, _queryHash, _requiredHash, _mode);
        }

        /// <summary>
        /// 调度缓存的共同生命周期：领取租约计数、延迟释放（最后一个租约归还后才释放原生内存）。
        /// 释放条件：已退役、无租约、且尚未释放过；三者任一不满足就留给下一次。
        /// </summary>
        internal abstract class ScheduleCacheLifetime : IDisposable
        {
            /// <summary>构建时记录的 EntityManager 结构版本：变了说明缓存已过期。</summary>
            public readonly int StructuralVersion;
            private int _leaseCount;
            private int _retired;
            private int _disposed;

            protected ScheduleCacheLifetime(int structuralVersion)
            {
                StructuralVersion = structuralVersion;
            }

            ~ScheduleCacheLifetime()
            {
                Dispose();
            }

            /// <summary>缓存是否有已分配的原生负载；没有的缓存领取租约直接失败并视为已释放。</summary>
            protected abstract bool HasPayload();

            /// <summary>负载条目数（块数或批次数）；为 0 表示没有可调度内容。</summary>
            public abstract int PayloadCount { get; }

            /// <summary>释放原生负载，只在租约归零且已退役时调用一次。</summary>
            protected abstract void ReleasePayload();

            protected abstract string CacheName { get; }

            public IDisposable RetainLease(bool synchronous = false)
            {
                if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(CacheName);

                Interlocked.Increment(ref _leaseCount);
                if (Volatile.Read(ref _disposed) != 0)
                {
                    ReleaseLease();
                    throw new ObjectDisposedException(CacheName);
                }

                return synchronous ? null : new CacheLease(this);
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _retired, 1);
                GC.SuppressFinalize(this);
                TryDisposeNow();
            }

            internal void ReleaseLease()
            {
                if (Interlocked.Decrement(ref _leaseCount) == 0) TryDisposeNow();
            }

            private void TryDisposeNow()
            {
                if (!HasPayload())
                {
                    Interlocked.Exchange(ref _disposed, 1);
                    return;
                }

                if (Volatile.Read(ref _retired) == 0 ||
                    Volatile.Read(ref _leaseCount) != 0 ||
                    Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                ReleasePayload();
            }

            internal sealed class CacheLease : IDisposable
            {
                private ScheduleCacheLifetime _owner;

                public CacheLease(ScheduleCacheLifetime owner)
                {
                    _owner = owner;
                }

                public void Dispose()
                {
                    var owner = Interlocked.Exchange(ref _owner, null);
                    owner?.ReleaseLease();
                }
            }
        }

        internal sealed class RawChunkScheduleCache : ScheduleCacheLifetime
        {
            public readonly ChunkJobData* ChunksPtr;
            public readonly int ChunkCount;
            public readonly bool OwnsChunkHandles;
            public readonly Archetype[]? MatchingArchetypes;
            internal Chunk[]? ManagedChunkArray;  // 托管路径：直接持有 Chunk[]（无 GCHandle）

            public RawChunkScheduleCache(int structuralVersion, ChunkJobData* chunksPtr, int chunkCount, bool ownsChunkHandles = false, Archetype[]? matchingArchetypes = null)
                : base(structuralVersion)
            {
                ChunksPtr = chunksPtr;
                ChunkCount = chunkCount;
                OwnsChunkHandles = ownsChunkHandles;
                MatchingArchetypes = matchingArchetypes;
            }

            protected override string CacheName => nameof(RawChunkScheduleCache);

            protected override bool HasPayload() => ChunksPtr != null;

            public override int PayloadCount => ChunkCount;

            protected override void ReleasePayload()
            {
                for (int i = 0; i < ChunkCount; i++)
                {
                    var chunkData = ChunksPtr[i];
                    FreeChunkJobData(chunkData);
                    if (OwnsChunkHandles && chunkData.chunkHandle != IntPtr.Zero)
                    {
                        var handle = GCHandle.FromIntPtr(chunkData.chunkHandle);
                        if (handle.IsAllocated) handle.Free();
                    }
                }

                // ManagedChunkArray 无 GCHandle，由引用计数 GC 管理。
                Marshal.FreeHGlobal((IntPtr)ChunksPtr);
            }
        }

        internal sealed class EntityBatchScheduleCache : ScheduleCacheLifetime
        {
            public readonly EntityBatchData* BatchesPtr;
            public readonly int BatchCount;
            private void* _componentArraysBlock;
            private void* _enableBitMapsBlock;

            public EntityBatchScheduleCache(int structuralVersion, EntityBatchData* batchesPtr, int batchCount, void* componentArraysBlock, void* enableBitMapsBlock)
                : base(structuralVersion)
            {
                BatchesPtr = batchesPtr;
                BatchCount = batchCount;
                _componentArraysBlock = componentArraysBlock;
                _enableBitMapsBlock = enableBitMapsBlock;
            }

            protected override string CacheName => nameof(EntityBatchScheduleCache);

            protected override bool HasPayload() => BatchesPtr != null;

            public override int PayloadCount => BatchCount;

            protected override void ReleasePayload()
            {
                if (_componentArraysBlock != null) Marshal.FreeHGlobal((IntPtr)_componentArraysBlock);
                if (_enableBitMapsBlock != null) Marshal.FreeHGlobal((IntPtr)_enableBitMapsBlock);
                for (int i = 0; i < BatchCount; i++) FreeNativeFilter(BatchesPtr[i].filter);
                Marshal.FreeHGlobal((IntPtr)BatchesPtr);
            }
        }
    }
}
