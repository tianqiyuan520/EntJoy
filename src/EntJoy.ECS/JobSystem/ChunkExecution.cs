namespace EntJoy.ECS.JobSystem
{
    /// <summary>
    /// ECS 侧 IJobChunk 同步执行器：直接在主线程遍历匹配 Chunk 并调用 Execute，
    /// 不经过 NativeJobCore/C++ 调度（Native 调度路径见 NativeEcsScheduler）。
    /// </summary>
    internal static class ChunkExecution
    {
        /// <summary>
        /// 在匹配 query 的所有 Chunk 上同步执行 job（主线程，无调度开销）。
        /// 单 AllEnabled 组件直接传原始位图（零拷贝）；多组件走 Archetype 组合位图缓存。
        /// </summary>
        internal static unsafe void ExecuteOnQuery<T>(ref T job, EntityManager entityManager, QueryBuilder query)
            where T : struct, IJobChunk
        {
            var allEnabledTypes = query.AllEnabled;
            bool hasFilter = allEnabledTypes != null && allEnabledTypes.Length > 0;

            for (int archIdx = 0; archIdx < entityManager.ArchetypeCount; archIdx++)
            {
                var archetype = entityManager.Archetypes[archIdx];
                if (archetype == null || !archetype.IsMatch(query))
                    continue;

                var chunks = archetype.ChunkSpan;
                for (int ci = 0; ci < chunks.Length; ci++)
                {
                    var chunk = chunks[ci];
                    if (chunk.EntityCount == 0)
                        continue;

                    if (!hasFilter)
                    {
                        job.Execute(new ArchetypeChunk(chunk), new ChunkEnabledMask(null, 0));
                        continue;
                    }

                    // 单组件零拷贝；多组件复用 Archetype 组合位图缓存
                    if (allEnabledTypes.Length == 1)
                    {
                        int compIdx = archetype.GetComponentTypeIndex(allEnabledTypes[0]);
                        if (compIdx < 0) continue;
                        ulong* bitmap = chunk.GetEnableBitMapPointer(compIdx);
                        if (bitmap == null) continue;
                        job.Execute(new ArchetypeChunk(chunk), new ChunkEnabledMask(bitmap, chunk.EntityCount));
                    }
                    else
                    {
                        ulong* combined = archetype.GetOrComputeCombinedMask(allEnabledTypes, ci, chunk);
                        job.Execute(new ArchetypeChunk(chunk),
                            combined != null ? new ChunkEnabledMask(combined, chunk.EntityCount) : new ChunkEnabledMask(null, 0));
                    }
                }
            }
        }
    }
}