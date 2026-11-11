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
        // Event Buffer 注册 / 分配 / Drain

        /// <summary>
        /// 注册 NativeTranspile Job 的事件类型元数据（由 BindingsGenerator 在启动时调用）。
        /// </summary>
        public static void RegisterEventBufferMeta(Type jobType, Type[] eventTypes)
        {
            EventMetaCache[jobType] = new EventBufferMeta { Count = eventTypes.Length, EventTypes = eventTypes };
        }

        /// <summary>
        /// 为指定 Job 类型分配 EventBuffer 并返回 headers 列表。
        /// </summary>
        internal static List<EventBufferHeader> AllocateEventBuffers(Type jobType)
        {
            var headers = new List<EventBufferHeader>();
            if (!EventMetaCache.TryGetValue(jobType, out var meta) || meta.Count == 0)
                return headers;

            // 前置校验所有事件类型 4 字节对齐，避免分配中途抛异常泄漏已分配的 dataPtr/countPtr
            for (int i = 0; i < meta.Count; i++)
            {
                int elemSize = ManagedValueLayout.SizeOf(meta.EventTypes[i]);
                if ((elemSize & 3) != 0)
                    throw new InvalidOperationException(
                        $"Event type {meta.EventTypes[i].FullName} has C# element size {elemSize}, " +
                        $"not divisible by 4. Native EventBuffer requires a four-byte-aligned unmanaged element layout.");
            }

            for (int i = 0; i < meta.Count; i++)
            {
                int elemSize = ManagedValueLayout.SizeOf(meta.EventTypes[i]);
                var dataPtr = Marshal.AllocHGlobal(1024 * elemSize);
                var countPtr = Marshal.AllocHGlobal(sizeof(int));
                *(int*)countPtr = 0;
                headers.Add(new EventBufferHeader
                {
                    dataPtr = dataPtr,
                    countPtr = countPtr,
                    capacity = 1024,
                    elementSize = elemSize
                });
            }
            return headers;
        }

        /// <summary>释放尚未接管的事件 buffer（无匹配 Chunk 等提前返回路径用）。</summary>
        private static void FreeEventBuffers(List<EventBufferHeader>? headers)
        {
            if (headers == null) return;
            foreach (var hdr in headers)
            {
                if (hdr.dataPtr != IntPtr.Zero) Marshal.FreeHGlobal(hdr.dataPtr);
                if (hdr.countPtr != IntPtr.Zero) Marshal.FreeHGlobal(hdr.countPtr);
            }
        }

        /// <summary>
        /// Drain EventBuffer → EventStream，然后释放非托管内存。
        /// </summary>
        internal static void DrainAndFreeEventBuffers(IntPtr contextPtr, World world, Type jobType)
        {
            if (!LiveEventBuffers.TryRemove(contextPtr, out var headers)) return;

            // world/meta 不可用时仍必须释放缓冲，否则 World.Dispose 置 DefaultWorld=null 后会泄漏。
            bool canDrain = EventMetaCache.TryGetValue(jobType, out var meta);
            world ??= World.DefaultWorld;
            if (world == null) canDrain = false;

            ReleaseContextEventResources(contextPtr);
            for (int i = 0; i < headers.Count; i++)
            {
                var hdr = headers[i];
                try { DrainOne(canDrain && i < meta.Count, world, meta, i, hdr); }
                finally { FreeEventBufferHeader(hdr); }
            }
        }

        // 释放独立分配的指针数组（eventBufferHeaders → __EntJoyEventBuffer*[]）与绑定的 World 句柄。
        private static unsafe void ReleaseContextEventResources(IntPtr contextPtr)
        {
            var ctxHeader = (ChunkContextHeader*)contextPtr;
            if (ctxHeader->eventBufferHeaders != IntPtr.Zero)
            {
                var ptrArr = (IntPtr*)ctxHeader->eventBufferHeaders;
                int bufCount = ctxHeader->eventBufferCount;
                for (int i = 0; i < bufCount; i++)
                {
                    if (ptrArr[i] != IntPtr.Zero) Marshal.FreeHGlobal(ptrArr[i]);
                }
                Marshal.FreeHGlobal(ctxHeader->eventBufferHeaders);
                ctxHeader->eventBufferHeaders = IntPtr.Zero;
            }
            // 清掉句柄：避免 ChunkCleanup 二次 drain 或重复释放。
            if (ctxHeader->eventWorldHandle == IntPtr.Zero) return;
            try { GCHandle.FromIntPtr(ctxHeader->eventWorldHandle).Free(); } catch { }
            ctxHeader->eventWorldHandle = IntPtr.Zero;
        }

        private static unsafe void DrainOne(bool canDrain, World world, EventBufferMeta meta, int index, in EventBufferHeader hdr)
        {
            if (!canDrain) return;
            int count = Math.Min(Volatile.Read(ref *(int*)hdr.countPtr), hdr.capacity);
            if (count <= 0 || meta.EventTypes[index] == null) return;
            world!.GetEventStream(meta.EventTypes[index])?.DrainFromBuffer((void*)hdr.dataPtr, count, hdr.elementSize);
        }

        /// <summary>存储活跃 EventBuffer（调度后、Complete 前，供 drain 释放）。</summary>
        internal static void StoreLiveEventBuffers(IntPtr contextPtr, List<EventBufferHeader>? headers, Type[]? eventTypes = null)
        {
            if (headers != null && headers.Count > 0)
            {
                LiveEventBuffers[contextPtr] = headers;
                if (eventTypes != null) LiveEventBufferTypes[contextPtr] = eventTypes;
            }
        }

        /// <summary>
        /// ChunkCleanup 回调调用的自动 drain：C++ 执行完自动回读事件。
        /// 从 LiveEventBufferTypes 取事件类型 → drain 到 World.EventStream → 释放 buffer 内存。
        /// </summary>
        internal static void DrainEventBuffersFromCleanup(IntPtr contextPtr, World world)
        {
            LiveEventBufferTypes.TryRemove(contextPtr, out var eventTypes);
            if (!LiveEventBuffers.TryRemove(contextPtr, out var headers)) return;

            int typedCount = Math.Min(headers.Count, eventTypes?.Length ?? 0);
            for (int i = 0; i < typedCount; i++)
            {
                var hdr = headers[i];
                try
                {
                    int count = Math.Min(Volatile.Read(ref *(int*)hdr.countPtr), hdr.capacity);
                    if (world != null && count > 0 && eventTypes[i] != null)
                    {
                        var stream = world.GetEventStream(eventTypes[i]);
                        stream?.DrainFromBuffer((void*)hdr.dataPtr, count, hdr.elementSize);
                    }
                }
                finally { FreeEventBufferHeader(hdr); }
            }
            for (int i = typedCount; i < headers.Count; i++)
            {
                var hdr = headers[i];
                FreeEventBufferHeader(hdr);
            }
        }

        private static void FreeEventBufferHeader(EventBufferHeader hdr)
        {
            if (hdr.dataPtr != IntPtr.Zero) Marshal.FreeHGlobal(hdr.dataPtr);
            if (hdr.countPtr != IntPtr.Zero) Marshal.FreeHGlobal(hdr.countPtr);
        }

    }
}
