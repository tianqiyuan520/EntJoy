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
        // Event Buffer 元数据缓存（Job 类型 → 查询结果）
        public struct EventBufferMeta
        {
            public int Count;                  // 事件类型数
            public Type[] EventTypes;          // 事件类型数组（与 adapter index 一一对应）
        }
        public static readonly ConcurrentDictionary<Type, EventBufferMeta> EventMetaCache = new();

        // 活跃 EventBuffer 实例（context 指针 → buffer 列表，Complete 后 drain + free）
        internal static readonly ConcurrentDictionary<IntPtr, List<EventBufferHeader>> LiveEventBuffers = new();
        // 事件类型数组（context 指针 → 该 job 的事件类型列表，drain 时需要按类型写回 EventStream）
        internal static readonly ConcurrentDictionary<IntPtr, Type[]> LiveEventBufferTypes = new();

        // Job 名登记 / 实体跟踪
        /// <summary>从 C++ 返回的 IntPtr 构造 JobHandle。</summary>
        private static JobHandle FromNative(IntPtr handle) => new JobHandle(new NativeJobHandle(handle));

        private static NativeJobHandle TrackEntityJob(EntityManager entityManager, JobHandle handle, Archetype[]? matchingArchetypes = null, ComponentType[]? writtenComponents = null)
        {
            entityManager?.TrackEntityJob(handle, matchingArchetypes, writtenComponents);
            return handle._nativeHandle;
        }

        // Chunk 调度缓存（归属 ECS，与 engine 解耦）
        internal static readonly object _rawChunkScheduleCacheLock = new();
        internal static readonly Dictionary<RawChunkScheduleCacheKey, RawChunkScheduleCache> _rawChunkScheduleCaches = new();
        internal static readonly Dictionary<RawChunkScheduleCacheKey, EntityBatchScheduleCache> _entityBatchScheduleCaches = new();

        // 托管回调层的 Chunk[] 引用表：context block 指针 → Chunk[]（无 GCHandle，无泄漏）
        internal static readonly ConcurrentDictionary<IntPtr, Chunk[]> ChunkArrayTable = new();

        public static void ClearRawChunkScheduleCaches(EntityManager entityManager)
        {
            if (entityManager == null) return;

            lock (_rawChunkScheduleCacheLock)
            {
                var keysToRemove = new List<RawChunkScheduleCacheKey>();
                foreach (var pair in _rawChunkScheduleCaches)
                {
                    if (pair.Key.Matches(entityManager))
                    {
                        pair.Value.Dispose();
                        keysToRemove.Add(pair.Key);
                    }
                }

                for (int i = 0; i < keysToRemove.Count; i++)
                {
                    _rawChunkScheduleCaches.Remove(keysToRemove[i]);
                }

                keysToRemove.Clear();
                foreach (var pair in _entityBatchScheduleCaches)
                {
                    if (pair.Key.Matches(entityManager))
                    {
                        pair.Value.Dispose();
                        keysToRemove.Add(pair.Key);
                    }
                }

                for (int i = 0; i < keysToRemove.Count; i++)
                {
                    _entityBatchScheduleCaches.Remove(keysToRemove[i]);
                }
            }
        }

        // IJobChunk 调度
    }
}
