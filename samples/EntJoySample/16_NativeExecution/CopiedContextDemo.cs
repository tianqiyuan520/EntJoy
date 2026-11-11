//using System.Runtime.CompilerServices;
//using System.Runtime.InteropServices;
//using System.Threading;
//using EntJoy.Collections;
//using EntJoy.JobSystem;

//namespace EntJoySample.NativeExecution;

//// 高级互操作示例：通过 C ABI 回调执行 C#，用于说明上下文复制与所有权。
//// 普通应用优先使用 [NativeTranspile] 和生成的 Schedule/Run 来执行原生内核。
//public static unsafe class CopiedContextDemo
//{
//    [StructLayout(LayoutKind.Sequential)]
//    private struct Payload
//    {
//        // 指针指向外部资源，复制 Payload 不会深拷贝这些资源。
//        public int* Output;
//        public int* Gate;
//        public int Value;
//    }

//    public static void Run()
//    {
//        using var values = new NativeArray<int>(19);
//        using var gate = new NativeArray<int>(1);
//        int* output = (int*)values.GetUnsafePtr();
//        int* ready = (int*)gate.GetUnsafePtr();
//        nint scalar = (nint)(delegate* unmanaged[Cdecl]<Payload*, void>)&WriteScalar;
//        nint range = (nint)(delegate* unmanaged[Cdecl]<Payload*, int, int, void>)&WriteRange;

//        var payload = new Payload { Output = output, Gate = ready, Value = 7 };
//        var copied = NativeJobScheduler.ScheduleCopiedRaw(scalar, (nint)(&payload), sizeof(Payload));
//        // 提交返回前已复制参数。先修改源值再放行，确保回调使用的是副本中的 7。
//        payload.Value = 999;
//        Volatile.Write(ref *ready, 1);
//        NativeJobScheduler.Complete(ref copied);
//        Program.Require(values[0] == 7, "Scalar submission retained the caller's payload address.");

//        Volatile.Write(ref *ready, 0);
//        payload.Value = 13;
//        // 显式 Batch 保留每批的起点和长度；19 个元素还会覆盖不满 4 个元素的尾批。
//        var batch = NativeJobScheduler.ScheduleParallelForCopiedRaw(range, (nint)(&payload), sizeof(Payload),
//            values.Length, batchSize: 4, indexedRanges: false);
//        payload.Value = 999;
//        Volatile.Write(ref *ready, 1);
//        NativeJobScheduler.Complete(ref batch);
//        for (int i = 0; i < values.Length; i++) Program.Require(values[i] == 13 + i, "Copied range payload differs.");

//        var indexedContext = (Payload*)NativeJobScheduler.RentJobContext(sizeof(Payload));
//        *indexedContext = new Payload { Output = output, Gate = ready, Value = 17 };
//        // 逐索引 adapter 可能收到合并后的连续区间，必须遍历区间内的全部索引。
//        var indexed = NativeJobScheduler.ScheduleParallelForRangeRaw(range, (nint)indexedContext,
//            NativeJobScheduler.PooledJobContextCleanupPtr, values.Length, batchSize: 4);
//        NativeJobScheduler.Complete(ref indexed);
//        for (int i = 0; i < values.Length; i++) Program.Require(values[i] == 17 + i, "Owned indexed range differs.");

//        // 租用的上下文与 PooledJobContextCleanupPtr 配对。
//        // ScheduleRaw 接管后由调度器清理，调用方不再访问或释放 owned。
//        var owned = (Payload*)NativeJobScheduler.RentJobContext(sizeof(Payload));
//        *owned = new Payload { Output = output + 18, Gate = ready, Value = 31 };
//        var handle = NativeJobScheduler.ScheduleRaw(scalar, (nint)owned, NativeJobScheduler.PooledJobContextCleanupPtr);
//        NativeJobScheduler.Complete(ref handle);
//        Program.Require(values[18] == 31, "Pooled context execution failed.");
//        Console.WriteLine("PASS: copied scalar/range contexts and pooled-context cleanup ownership.");
//    }

//    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
//    private static void WriteScalar(Payload* payload)
//    {
//        // 等调用方改写源参数后再读取副本；Gate 自身仍是双方共享的外部内存。
//        while (Volatile.Read(ref *payload->Gate) == 0) Thread.Yield();
//        *payload->Output = payload->Value;
//    }

//    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
//    private static void WriteRange(Payload* payload, int first, int count)
//    {
//        while (Volatile.Read(ref *payload->Gate) == 0) Thread.Yield();
//        for (int i = first; i < first + count; i++) payload->Output[i] = payload->Value + i;
//    }
//}
