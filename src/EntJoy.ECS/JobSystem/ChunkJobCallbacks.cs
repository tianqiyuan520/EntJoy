using EntJoy.JobSystem;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EntJoy.ECS.JobSystem
{
    /// <summary>
    /// 托管回调层：C++ worker 反向 P/Invoke 进入托管侧的入口。
    /// 每 job 类型 T 缓存一个区间回调 thunk；执行期解析 job 与 chunk 并调用用户 Execute。
    /// Mask 计算委托给共享工具 <see cref="ChunkJobScheduler.ComputeChunkMask"/>（单一真值来源）。
    /// </summary>
    public static unsafe class ChunkJobCallbacks
    {
        [SkipLocalsInit]
        internal unsafe static ChunkRangeJobFuncDelegate CreateChunkRangeCallback<T>() where T : struct, IJobChunk
        {
            return (IntPtr ctx, ChunkJobData* chunks, int startIndex, int count) =>
            {
                NativeJobCore.EnterJobExecution();
                NativeJobCore.RegisterCurrentBatchJobName(typeof(T).Name);
                try
                {
                    var header = (ChunkContextHeader*)ctx;
                    ref var job = ref ResolveJob<T>(ctx, header);
                    // 单 GCHandle 保活收集期 Chunk[]（ChunkId 索引 O(1)）
                    Chunk[]? chunkTable = ChunkJobScheduler.ChunkArrayTable.TryGetValue(ctx, out var _t) ? _t : null;

                    int end = startIndex + count;
                    for (int index = startIndex; index < end; index++)
                    {
                        var cd = chunks + index;
                        if (chunkTable == null) continue;
                        var chunk = chunkTable[cd->chunkHandle.ToInt32()];
                        if (chunk.MemoryBlock == nint.Zero) continue;
                        // Phase C：mask 委托给共享工具（ResolveCombinedMask 已删）
                        ChunkEnabledMask mask;
                        if (header->hasEnabledFilter != 0 && header->allEnabledCount > 0)
                        {
                            var enabledTypes = ChunkJobScheduler.ResolveEnabledTypes(header, chunk);
                            mask = ChunkJobScheduler.ComputeChunkMask(chunk, enabledTypes);
                        }
                        else mask = default;
                        job.Execute(new ArchetypeChunk(chunk), mask);
                    }
                }
                catch (Exception exception)
                {
                    NativeJobCore.RecordJobException(NativeJobCore.CurrentBatchId, exception);
                }
                finally
                {
                    NativeJobCore.ExitJobExecution();
                }
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe static ref T ResolveJob<T>(IntPtr ctx, ChunkContextHeader* header) where T : struct
        {
            if (header->jobIsBoxed != 0)
                return ref GetBoxedJobFromContext<T>(ctx, header);
            return ref ResolveJobFromContext<T>(ctx, out _);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe static ref T GetBoxedJobFromContext<T>(IntPtr ctx, ChunkContextHeader* header) where T : struct
        {
            int typesDataSize = header->allEnabledCount * sizeof(int);
            int requiredTypesDataSize = header->requiredComponentTypeIdCount * sizeof(int);
            byte* jobPtr = (byte*)ctx + Unsafe.SizeOf<ChunkContextHeader>() + typesDataSize + requiredTypesDataSize;
            var box = (ManagedJobBox<T>)GCHandle.FromIntPtr(*(IntPtr*)jobPtr).Target;
            return ref box.Job;
        }

        /// <summary>从 context block 解析裸字节 job 引用。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe static ref T ResolveJobFromContext<T>(IntPtr ctx, out ChunkContextHeader* header) where T : struct
        {
            header = (ChunkContextHeader*)ctx;
            int typesDataSize = header->allEnabledCount * sizeof(int);
            int requiredTypesDataSize = header->requiredComponentTypeIdCount * sizeof(int);
            byte* jobPtr = (byte*)ctx + Unsafe.SizeOf<ChunkContextHeader>() + typesDataSize + requiredTypesDataSize;
            return ref Unsafe.AsRef<T>(jobPtr);
        }
    }
}