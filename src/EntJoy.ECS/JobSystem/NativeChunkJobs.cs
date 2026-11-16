using EntJoy.JobSystem;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace EntJoy.ECS.JobSystem
{
    /// <summary>关系位图为调度快照，启用位图在依赖完成后读取。缓冲由调度器持有到 Job 完成。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct EntityFilterData
    {
        public ulong* relationshipBits;
        public void** enabledBitMaps;
        public int enabledCount;
    }

    // Chunk 任务数据结构（与 C++ 一一对应）
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
        public void** requiredEnableBitMaps;    // 与 requiredComponentArrays 同序的逐组件 enable 位图（元素可为 null）
        public EntityFilterData filter;
    }

    /// <summary>
    /// NativeTranspile 轻量 Chunk 数据结构（与 C++ ChunkData 一一对应）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ChunkData
    {
        public void* entityArray;           // Entity 数组首地址（IJobEntity 的 Entity 参数需要；无该参数时为 null）
        public void** componentArrays;      // 组件数组指针 [requiredCount]
        public int entityCount;             // 实体数量
        public int requiredComponentCount;  // 组件数组数量
        public void** enableBitMaps;        // 与 componentArrays 同序的逐组件启用位图，元素可为 null
        public int enableBitmapCount;       // enableBitMaps 项数
        public void** sharedValuePtrs;      // SharedComponent blittable 值指针 [sharedValueCount]
        public int sharedValueCount;        // sharedValuePtrs 数量，0 = 无 shared 组件
        public EntityFilterData filter;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct EntityBatchData
    {
        public void** componentArrays;
        public void** enableBitMaps;
        public int entityCount;
        public int enableBitmapCount;
        public EntityFilterData filter;
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
        // Event Buffer
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
        public QueryBuilder Query;
    }

    // 回调委托签名
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal unsafe delegate void ChunkRangeJobFuncDelegate(IntPtr context, ChunkJobData* chunks, int startIndex, int count);

    /// <summary>
    /// Native 调用层：ECS JobSystem 与 C++ 调度器的唯一桥。
    /// 只承担 ABI 职责——数据结构定义、P/Invoke 函数指针加载、5 个提交入口、
    /// 托管调用层（ChunkJobScheduler）与 NativeTranspiler 生成代码（NativeExports）都经它提交。
    /// </summary>
    public static unsafe class NativeChunkJobs
    {
        // Chunk P/Invoke 函数指针
        internal static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, ChunkJobData*, int, IntPtr, int, int, int, uint, IntPtr> _jobSystem_ScheduleChunkJobEx;
        internal static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, ChunkJobData*, int, IntPtr, int, int, int, uint, IntPtr> _jobSystem_ScheduleChunkRangeJobEx;
        internal static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, EntityBatchData*, int, IntPtr, int, int, int, int, uint, IntPtr> _jobSystem_ScheduleEntityBatchJobEx;
        internal static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, EntityBatchData*, int, IntPtr, int, int, int, int, uint, IntPtr> _jobSystem_ScheduleAndCompleteEntityBatchJobEx;
        private static delegate* unmanaged[Cdecl]<int> _getChunkContextHeaderSize;

        private static readonly object _chunkPointerLoadLock = new();
        private static int _chunkPointersLoaded;
        private static int _reloadHookRegistered;

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
            ValidateChunkContextHeaderLayout();
        }

        /// <summary>布局防御：原生侧报告 chunk 上下文头的字节数与字段指纹，与托管 <see cref="ChunkContextHeader"/> 比对。
        /// 两边字段必须同步（原生定义在 NativeGeneratedSupport.h）。</summary>
        private static void ValidateChunkContextHeaderLayout()
        {
            if (!NativeJobCore.TryGetNativeTranspiledExport("NativeTranspiled_GetChunkContextHeaderSize", out var export))
                return;
            _getChunkContextHeaderSize = (delegate* unmanaged[Cdecl]<int>)export;
            int nativeSize = _getChunkContextHeaderSize();
            int managedSize = Unsafe.SizeOf<ChunkContextHeader>();
            if (nativeSize != managedSize)
                throw new InvalidOperationException(
                    $"ChunkContextHeader 布局不匹配：C++={nativeSize}B C#={managedSize}B。请同步 NativeGeneratedSupport.h 与 NativeChunkJobs.cs 字段。");

            // 总大小相同、字段换序或换名时，只有指纹能发现。
            if (!NativeJobCore.TryGetNativeTranspiledExport("NativeTranspiled_GetChunkContextHeaderLayoutFingerprint", out var fingerprintExport))
            {
                Console.Error.WriteLine(
                    "[NativeTranspiled] 未找到 ChunkContextHeader 布局指纹导出，本次只校验了字节数（模块版本旧于 ABI 5）。");
                return;
            }
            var getFingerprint = (delegate* unmanaged[Cdecl]<ulong>)fingerprintExport;
            ulong nativeFingerprint = getFingerprint();
            ulong managedFingerprint = ComputeChunkContextHeaderFingerprint();
            if (nativeFingerprint != managedFingerprint)
                throw new InvalidOperationException(
                    $"ChunkContextHeader 字段布局不匹配：C++=0x{nativeFingerprint:X16} C#=0x{managedFingerprint:X16}。" +
                    $"托管侧字段（名:偏移:宽度）={DescribeChunkContextHeaderFields()}。请同步 NativeGeneratedSupport.h 与 NativeChunkJobs.cs 字段。");
        }

        /// <summary>托管侧字段清单，供指纹失配时定位到具体字段。</summary>
        private static string DescribeChunkContextHeaderFields()
        {
            var type = typeof(ChunkContextHeader);
            var text = new StringBuilder();
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (text.Length > 0) text.Append(", ");
                text.Append(field.Name).Append(':')
                    .Append(Marshal.OffsetOf(type, field.Name)).Append(':')
                    .Append(Marshal.SizeOf(field.FieldType));
            }
            return text.ToString();
        }

        /// <summary>字段指纹：名字 + 类型名（都忽略大小写）+ 偏移 + 宽度，FNV-1a 后按加法合成，与 C++ 侧同值。</summary>
        internal static ulong ComputeChunkContextHeaderFingerprint()
        {
            ulong sum = 0;
            var type = typeof(ChunkContextHeader);
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                ulong hash = LayoutFnvBasis;
                foreach (char c in field.Name)
                {
                    hash ^= (byte)char.ToLowerInvariant(c);
                    hash *= LayoutFnvPrime;
                }
                // 名字与类型之间夹一次空推进，与 C++ 侧一致。
                hash *= LayoutFnvPrime;
                foreach (char c in TypeToken(field.FieldType))
                {
                    if (c == ' ') continue;
                    hash ^= (byte)c;
                    hash *= LayoutFnvPrime;
                }
                hash = (hash ^ (ulong)Marshal.OffsetOf(type, field.Name)) * LayoutFnvPrime;
                hash = (hash ^ (ulong)Marshal.SizeOf(field.FieldType)) * LayoutFnvPrime;
                sum += hash;
            }
            return sum + (LayoutSalt ^ (ulong)Marshal.SizeOf(type));
        }

        /// <summary>字段类型在指纹里的拼写：与 C++ 侧宏列表逐字一致（托管类型名是 Int32/UInt32，不能直接用）。</summary>
        private static string TypeToken(Type type) => type switch
        {
            _ when type == typeof(IntPtr) => "void*",
            _ when type == typeof(int) => "int",
            _ when type == typeof(uint) => "unsigned int",
            _ when type == typeof(float) => "float",
            _ when type == typeof(long) => "long long",
            _ => type.Name.ToLowerInvariant()
        };

        private const ulong LayoutFnvBasis = 1469598103934665603UL;
        private const ulong LayoutFnvPrime = 1099511628211UL;
        private const ulong LayoutSalt = 0x9e3779b97f4a7c15UL;

        /// <summary>热重载换了 NativeTranspiled 句柄后，重新校验 chunk 上下文头布局（导出指针取自 JobSystem 模块，不需要重取）。</summary>
        private static void EnsureReloadHook()
        {
            if (Interlocked.Exchange(ref _reloadHookRegistered, 1) != 0) return;
            NativeJobCore.RegisterReloadCallback(ValidateChunkContextHeaderLayout);
        }
        internal static void EnsureChunkPointersLoaded()
        {
            if (Volatile.Read(ref _chunkPointersLoaded) != 0) return;
            EnsureReloadHook();
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

        // 共享状态（chunk 表保活 / 上下文租赁 / 清理回调）
        internal static readonly ConcurrentDictionary<IntPtr, GCHandle> ChunkContextLeases = new();
        internal static readonly object ChunkGCHandlesLock = new();
        internal static readonly List<GCHandle> ChunkGCHandles = new();

        private static readonly NativeJobCore.CleanupFunc _chunkCleanup = ChunkCleanup;
        internal static readonly IntPtr ChunkCleanupPtr = Marshal.GetFunctionPointerForDelegate(_chunkCleanup);

        // 上下文块清理

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
                ReleaseEventBuffers(header);
                ReleaseChunkGCHandles(gcHandleStartIndex, chunkCount);
                if (ownsChunkData)
                {
                    for (int i = 0; i < chunkCount; i++)
                    {
                        if (chunksPtr != null) ChunkJobScheduler.FreeChunkJobData(chunksPtr[i]);
                    }
                    if (chunksPtr != null) Marshal.FreeHGlobal((IntPtr)chunksPtr);
                }
            }
            finally
            {
                ChunkJobScheduler.ChunkArrayTable.TryRemove(contextBlock, out _);
                ReleaseJobBoxHandle(header, contextBlock);
                ReleaseLease(contextBlock);
                Interlocked.Exchange(ref header->cleanupInProgress, 0);
                ReturnContextBlock(contextBlock);
            }
        }

        // 把缓冲里的事件 drain 进 World.EventStream，再释放缓冲头指针数组与 World 的 GCHandle。
        private static unsafe void ReleaseEventBuffers(ChunkContextHeader* header)
        {
            if (header->eventBufferCount <= 0 || header->eventWorldHandle == IntPtr.Zero) return;

            var world = (World)GCHandle.FromIntPtr(header->eventWorldHandle).Target!;
            ChunkJobScheduler.DrainEventBuffersFromCleanup((IntPtr)header, world);

            if (header->eventBufferHeaders != IntPtr.Zero)
            {
                var ptrArr = (IntPtr*)header->eventBufferHeaders;
                for (int ei = 0; ei < header->eventBufferCount; ei++)
                {
                    if (ptrArr[ei] != IntPtr.Zero) Marshal.FreeHGlobal(ptrArr[ei]);
                }
                Marshal.FreeHGlobal(header->eventBufferHeaders);
            }
            GCHandle.FromIntPtr(header->eventWorldHandle).Free();
        }

        // 释放本批占用的 Chunk 保活句柄，并把表尾的空洞回收掉。
        private static void ReleaseChunkGCHandles(int startIndex, int count)
        {
            if (startIndex < 0) return;
            lock (ChunkGCHandlesLock)
            {
                for (int i = 0; i < count && (startIndex + i) < ChunkGCHandles.Count; i++)
                {
                    int index = startIndex + i;
                    if (!ChunkGCHandles[index].IsAllocated) continue;
                    ChunkGCHandles[index].Free();
                    ChunkGCHandles[index] = default;
                }
                while (ChunkGCHandles.Count > 0 && !ChunkGCHandles[ChunkGCHandles.Count - 1].IsAllocated)
                    ChunkGCHandles.RemoveAt(ChunkGCHandles.Count - 1);
                if (ChunkGCHandles.Capacity > 8192 && ChunkGCHandles.Capacity > ChunkGCHandles.Count * 4)
                    ChunkGCHandles.TrimExcess();
            }
        }

        // 托管 job 以 box 形式存放在上下文尾部：跳过类型 id 数据后取出释放。
        private static unsafe void ReleaseJobBoxHandle(ChunkContextHeader* header, IntPtr contextBlock)
        {
            if (header->jobIsBoxed == 0) return;
            try
            {
                int typesSize = header->allEnabledCount * sizeof(int);
                int requiredSize = header->requiredComponentTypeIdCount * sizeof(int);
                byte* jobPtr = (byte*)contextBlock + sizeof(ChunkContextHeader) + typesSize + requiredSize;
                var handle = GCHandle.FromIntPtr(*(IntPtr*)jobPtr);
                if (handle.IsAllocated) handle.Free();
            }
            catch { }
        }

        private static void ReleaseLease(IntPtr contextBlock)
        {
            if (!ChunkContextLeases.TryRemove(contextBlock, out var leaseHandle)) return;
            try
            {
                if (leaseHandle.Target is IDisposable lease) lease.Dispose();
            }
            catch { }
            try { leaseHandle.Free(); } catch { }
        }

        // 上下文块前有一个 int 记录池化尺寸，归还时一并带回。
        private static unsafe void ReturnContextBlock(IntPtr contextBlock)
        {
            try
            {
                var pooledBlock = contextBlock - IntPtr.Size;
                int pooledSize = *(int*)pooledBlock;
                NativeJobCore.ContextPool.Return(pooledBlock, pooledSize);
            }
            catch { }
        }
    }
}
