using EntJoy.JobSystem;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace EntJoy.ECS.JobSystem
{

    // ======================== Chunk 任务数据结构（与 C++ 一一对应） ========================
    // 命名空间级（非嵌套）：调度层/回调层/transpiler 生成代码均可裸用。

    /// <summary>
    /// 跨语言共享的 Chunk 任务数据结构（与 C++ ChunkJobData 一一对应）
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ChunkJobData
    {
        public void* entityArray;           // Entity 数组首地址
        public int entityCount;             // 实体数量
        public int componentCount;          // 组件种类数
        public void** componentArrays;      // 每个组件数组首地址（长度为 componentCount）
        public int* componentSizes;         // 每个组件大小（字节，长度为 componentCount）
        public void** enableBitMaps;        // 每个 enableable 组件位图指针（可为 null，长度为 componentCount）
        public int* componentTypeIndices;   // 组件类型索引数组
        public IntPtr chunkHandle;          // GCHandle IntPtr，用于在回调中恢复 Chunk 对象
        public void** requiredComponentArrays; // NativeTranspile IJobChunk 所需组件数组指针
        public int requiredComponentCount;     // requiredComponentArrays 数量
        public void** sharedValuePtrs;          // SharedComponent blittable 值指针 [sharedValueCount]
        public int sharedValueCount;            // sharedValuePtrs 数量，0 = 无 shared 组件
    }

    /// <summary>
    /// NativeTranspile 轻量 Chunk 数据结构（与 C++ ChunkData 一一对应）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ChunkData
    {
        public void** componentArrays;      // 组件数组指针 [requiredCount]
        public int entityCount;             // 实体数量
        public int requiredComponentCount;  // 组件数组数量
        public void** enableBitMaps;        // enable 位图 [enableCount]，无过滤时为 null（预留）
        public int enableBitmapCount;       // enable 位图数量，0 表示无过滤（预留）
        public void** sharedValuePtrs;      // SharedComponent blittable 值指针 [sharedValueCount]
        public int sharedValueCount;        // sharedValuePtrs 数量，0 = 无 shared 组件
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct EntityBatchData
    {
        public void** componentArrays;
        public void** enableBitMaps;
        public int entityCount;
        public int enableBitmapCount;
    }

    /// <summary>
    /// Chunk 上下文包的内存布局（非托管），必须 Sequential 以确保布局与指针访问一致。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct ChunkContextHeader
    {
        public int chunkCount;               // Chunk 数量
        public int hasEnabledFilter;         // 是否有 enable 过滤
        public IntPtr queryAllEnabledTypes;  // int[]（类型哈希数组）指针
        public int allEnabledCount;          // AllEnabled 数组长度
        public int gcHandleStartIndex;       // GCHandle 列表起始索引（-1 = 无 GCHandle）
        public IntPtr chunksPtr;             // ChunkJobData 数组指针（用于 cleanup 回收）
        public int cleanupInProgress;        // 防止重复清理的标志
        public int ownsChunkData;            // 该 context 是否负责释放 chunksPtr + 每 chunk 缓冲区
        public IntPtr requiredComponentTypeIds; // NativeTranspiler IJobChunk 所需组件类型 ID 数组
        public int requiredComponentTypeIdCount; // 所需组件类型 ID 数量
        public int jobIsBoxed;               // job 区域存的是 GCHandle(ManagedJobBox) 而非裸字节（托管引用 job）
        public IntPtr chunkArrayHandle;      // 单 GCHandle 保活收集期 Chunk[]（托管回调路径按 chunkId 索引）
        // ─── Event Buffer ───
        public int eventBufferCount;         // 事件类型数（0 = 无事件）
        public IntPtr eventBufferHeaders;    // EventBufferHeader[] 指针（每个元素 = 一个事件类型的 buffer 描述）
        public IntPtr eventWorldHandle;      // GCHandle → World（cleanup 时自动 drain 到正确的 EventStream）
    }

    internal enum ChunkScheduleMode
    {
        PublishAssist = 1,
        ImmediateNative = 3
    }

    internal enum NativeEcsJobKind
    {
        Chunk = 0,
        Entity = 1
    }

    /// <summary>
    /// 托管引用 job 的 GCHandle 盒（仅 jobBoxed 路径使用）。
    /// </summary>
    internal sealed class ManagedJobBox<T> where T : struct
    {
        public T Job;
    }

    // ======================== 回调委托签名 ========================
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal unsafe delegate void ChunkRangeJobFuncDelegate(IntPtr context, ChunkJobData* chunks, int startIndex, int count);

    /// <summary>
    /// Native 调用层：ECS JobSystem 与 C++ 调度器的唯一桥。
    /// 只承担 ABI 职责——数据结构定义、P/Invoke 函数指针加载、5 个提交入口、
    /// context/chunk 列表清理回调。无调度编排、无业务逻辑。
    /// 托管调用层（ChunkJobScheduler）与 NativeTranspiler 生成代码（NativeExports）都经它提交。
    /// </summary>
    public static unsafe class NativeChunkJobs
    {
        // ======================== Chunk P/Invoke 函数指针 ========================
        internal static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, ChunkJobData*, int, IntPtr, int, int, int, uint, IntPtr> _jobSystem_ScheduleChunkJobEx;
        internal static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, ChunkJobData*, int, IntPtr, int, int, int, uint, IntPtr> _jobSystem_ScheduleChunkRangeJobEx;
        internal static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, EntityBatchData*, int, IntPtr, int, int, int, int, uint, IntPtr> _jobSystem_ScheduleEntityBatchJobEx;
        internal static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, EntityBatchData*, int, IntPtr, int, int, int, int, uint, IntPtr> _jobSystem_ScheduleAndCompleteEntityBatchJobEx;

        private static readonly object _chunkPointerLoadLock = new();
        private static int _chunkPointersLoaded;

        /// <summary>
        /// 从 <see cref="NativeJobCore.NativeDllHandle"/> 加载 chunk 专属导出。
        /// 首次 chunk 调度时幂等调用。
        /// </summary>
        internal static void LoadNativeChunkPointers(IntPtr dllHandle)
        {
            _jobSystem_ScheduleChunkJobEx = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, ChunkJobData*, int, IntPtr, int, int, int, uint, IntPtr>)
                NativeLibrary.GetExport(dllHandle, "JobSystem_ScheduleChunkJobEx");
            _jobSystem_ScheduleChunkRangeJobEx = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, ChunkJobData*, int, IntPtr, int, int, int, uint, IntPtr>)
                NativeLibrary.GetExport(dllHandle, "JobSystem_ScheduleChunkRangeJobEx");
            _jobSystem_ScheduleEntityBatchJobEx = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, EntityBatchData*, int, IntPtr, int, int, int, int, uint, IntPtr>)
                NativeLibrary.GetExport(dllHandle, "JobSystem_ScheduleEntityBatchJobEx");
            _jobSystem_ScheduleAndCompleteEntityBatchJobEx = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, EntityBatchData*, int, IntPtr, int, int, int, int, uint, IntPtr>)
                NativeLibrary.GetExport(dllHandle, "JobSystem_ScheduleAndCompleteEntityBatchJobEx");
        }

        internal static void EnsureChunkPointersLoaded()
        {
            if (Volatile.Read(ref _chunkPointersLoaded) != 0) return;
            lock (_chunkPointerLoadLock)
            {
                if (_chunkPointersLoaded != 0) return;
                LoadNativeChunkPointers(NativeJobCore.NativeDllHandle);
                Interlocked.Exchange(ref _chunkPointersLoaded, 1);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static IntPtr JobSystem_ScheduleChunkJobEx(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, ChunkJobData* chunks, int chunkCount, IntPtr dependency, ChunkScheduleMode mode, int workerCap = 0, int rangeSize = 0, uint unitGeneration = 0)
        {
            NativeJobCore.EnsureNativeLoaded();
            EnsureChunkPointersLoaded();
            return _jobSystem_ScheduleChunkJobEx(funcPtr, context, cleanupPtr, chunks, chunkCount, dependency, (int)mode, workerCap, rangeSize, unitGeneration);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static IntPtr JobSystem_ScheduleChunkRangeJobEx(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, ChunkJobData* chunks, int chunkCount, IntPtr dependency, ChunkScheduleMode mode, int workerCap = 0, int rangeSize = 0, uint unitGeneration = 0)
        {
            NativeJobCore.EnsureNativeLoaded();
            EnsureChunkPointersLoaded();
            return _jobSystem_ScheduleChunkRangeJobEx(funcPtr, context, cleanupPtr, chunks, chunkCount, dependency, (int)mode, workerCap, rangeSize, unitGeneration);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static IntPtr JobSystem_ScheduleEntityBatchJobEx(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, EntityBatchData* batches, int batchCount, IntPtr dependency, ChunkScheduleMode mode, int workerCap = 0, int rangeSize = 0, NativeEcsJobKind jobKind = NativeEcsJobKind.Entity, uint unitGeneration = 0)
        {
            NativeJobCore.EnsureNativeLoaded();
            EnsureChunkPointersLoaded();
            return _jobSystem_ScheduleEntityBatchJobEx(funcPtr, context, cleanupPtr, batches, batchCount, dependency, (int)mode, workerCap, rangeSize, (int)jobKind, unitGeneration);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static IntPtr JobSystem_ScheduleAndCompleteEntityBatchJobEx(IntPtr funcPtr, IntPtr context, IntPtr cleanupPtr, EntityBatchData* batches, int batchCount, IntPtr dependency, ChunkScheduleMode mode = ChunkScheduleMode.PublishAssist, int workerCap = 0, int rangeSize = 0, NativeEcsJobKind jobKind = NativeEcsJobKind.Entity, uint unitGeneration = 0)
        {
            NativeJobCore.EnsureNativeLoaded();
            EnsureChunkPointersLoaded();
            return _jobSystem_ScheduleAndCompleteEntityBatchJobEx(funcPtr, context, cleanupPtr, batches, batchCount, dependency, (int)mode, workerCap, rangeSize, (int)jobKind, unitGeneration);
        }

        // ======================== 共享状态（chunk 表保活 / 上下文租赁 / 清理回调） ========================
        internal static readonly ConcurrentDictionary<IntPtr, GCHandle> ChunkContextLeases = new();
        internal static readonly object ChunkGCHandlesLock = new();
        internal static readonly List<GCHandle> ChunkGCHandles = new();

        private static readonly NativeJobCore.CleanupFunc _chunkCleanup = ChunkCleanup;
        internal static readonly IntPtr ChunkCleanupPtr = Marshal.GetFunctionPointerForDelegate(_chunkCleanup);

        // ======================== 上下文块清理 ========================

        /// <summary>
        /// 释放 chunk 调度上下文：chunk 表保活句柄、GCHandle 列表占用、HGlobal 分配的 chunk 缓冲区、上下文池。
        /// </summary>
        internal unsafe static void ChunkCleanup(IntPtr contextBlock)
        {
            if (contextBlock == IntPtr.Zero) return;
            var header = (ChunkContextHeader*)contextBlock;
            if (Interlocked.CompareExchange(ref header->cleanupInProgress, 1, 0) != 0) return;
            int chunkCount = header->chunkCount;
            int gcHandleStartIndex = header->gcHandleStartIndex;
            var chunksPtr = (ChunkJobData*)header->chunksPtr;
            bool ownsChunkData = header->ownsChunkData != 0;

            try
            {
                // ─── Event Buffer: 自动 drain 到 World.EventStream ───
                if (header->eventBufferCount > 0 && header->eventWorldHandle != IntPtr.Zero)
                {
                    var world = (World)GCHandle.FromIntPtr(header->eventWorldHandle).Target!;
                    ChunkJobScheduler.DrainEventBuffersFromCleanup(contextBlock, world);
                    // 释放 eventBufferHeaders 指针数组
                    if (header->eventBufferHeaders != IntPtr.Zero)
                    {
                        var ptrArr = (IntPtr*)header->eventBufferHeaders;
                        for (int ei = 0; ei < header->eventBufferCount; ei++)
                            if (ptrArr[ei] != IntPtr.Zero) Marshal.FreeHGlobal(ptrArr[ei]);
                        Marshal.FreeHGlobal(header->eventBufferHeaders);
                    }
                    // 释放 World GCHandle
                    GCHandle.FromIntPtr(header->eventWorldHandle).Free();
                }

                if (chunksPtr != null && gcHandleStartIndex >= 0)
                {
                    lock (ChunkGCHandlesLock)
                    {
                        for (int i = 0; i < chunkCount && (gcHandleStartIndex + i) < ChunkGCHandles.Count; i++)
                        {
                            int index = gcHandleStartIndex + i;
                            if (ChunkGCHandles[index].IsAllocated) { ChunkGCHandles[index].Free(); ChunkGCHandles[index] = default; }
                        }
                        while (ChunkGCHandles.Count > 0 && !ChunkGCHandles[ChunkGCHandles.Count - 1].IsAllocated)
                            ChunkGCHandles.RemoveAt(ChunkGCHandles.Count - 1);
                        if (ChunkGCHandles.Capacity > 8192 && ChunkGCHandles.Capacity > ChunkGCHandles.Count * 4)
                            ChunkGCHandles.TrimExcess();
                    }
                }

                if (ownsChunkData)
                {
                    for (int i = 0; i < chunkCount; i++)
                    {
                        if (chunksPtr != null)
                        {
                            var cd = chunksPtr[i];
                            if (cd.componentArrays != null) Marshal.FreeHGlobal((IntPtr)cd.componentArrays);
                            if (cd.componentSizes != null) Marshal.FreeHGlobal((IntPtr)cd.componentSizes);
                            if (cd.enableBitMaps != null) Marshal.FreeHGlobal((IntPtr)cd.enableBitMaps);
                            if (cd.componentTypeIndices != null) Marshal.FreeHGlobal((IntPtr)cd.componentTypeIndices);
                            if (cd.requiredComponentArrays != null) Marshal.FreeHGlobal((IntPtr)cd.requiredComponentArrays);
                            if (cd.sharedValuePtrs != null) Marshal.FreeHGlobal((IntPtr)cd.sharedValuePtrs);
                        }
                    }
                }

                if (chunksPtr != null && ownsChunkData) Marshal.FreeHGlobal((IntPtr)chunksPtr);
            }
            finally
            {
                // 托管回调路径：移除 Chunk[] 引用（由 ChunkArrayTable 管理，无 GCHandle）
                ChunkJobScheduler.ChunkArrayTable.TryRemove(contextBlock, out _);

                // 托管引用 job：释放 job 区域的 GCHandle（box）
                if (header->jobIsBoxed != 0)
                {
                    try
                    {
                        int jobTypesDataSize = header->allEnabledCount * sizeof(int);
                        int jobRequiredTypesDataSize = header->requiredComponentTypeIdCount * sizeof(int);
                        byte* jobPtr = (byte*)contextBlock + Unsafe.SizeOf<ChunkContextHeader>() + jobTypesDataSize + jobRequiredTypesDataSize;
                        var jobBoxHandle = GCHandle.FromIntPtr(*(IntPtr*)jobPtr);
                        if (jobBoxHandle.IsAllocated) jobBoxHandle.Free();
                    }
                    catch { }
                }

                if (ChunkContextLeases.TryRemove(contextBlock, out var leaseHandle))
                {
                    try
                    {
                        if (leaseHandle.Target is IDisposable lease)
                            lease.Dispose();
                    }
                    catch { }
                    try { leaseHandle.Free(); } catch { }
                }

                try
                {
                    var pooledBlock = contextBlock - IntPtr.Size;
                    int pooledSize = *(int*)pooledBlock;
                    NativeJobCore.ContextPool.Return(pooledBlock, pooledSize);
                }
                catch { }

                Interlocked.Exchange(ref header->cleanupInProgress, 0);
            }
        }
    }
}