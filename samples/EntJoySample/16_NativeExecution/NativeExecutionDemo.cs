//using System.Runtime.CompilerServices;
//using EntJoy.Collections;
//using EntJoy.ECS;
//using EntJoy.JobSystem;
//using NativeTranspiler;

//namespace EntJoySample.NativeExecution;

//[NativeTranspile]
//// 同一份内核既能处理连续数组，也能处理固定容量的分段视图。
//public unsafe struct NativeFeatureColumnJob : IJobParallelFor
//{
//    public SampleColumn<int> Values;
//    public int Delta;
//    public void Execute(int index)
//    {
//        int* p = SampleColumnAccess.ElementPtr(ref Values, index);
//        *p = NativeFeatureMath.Add(*p, Delta);
//    }
//}

//// 示例自己的固定槽位视图：数据与块表由调用方拥有，地址和槽位顺序在 Job 完成前保持稳定。
//public unsafe struct SampleColumn<T> where T : unmanaged
//{
//    public T* Flat;
//    public T** Chunks;
//    public int Capacity, Length;
//    public static implicit operator SampleColumn<T>(NativeArray<T> values)
//        => new SampleColumn<T> { Flat = (T*)values.GetUnsafePtr(), Length = values.Length };
//}

//public static unsafe class SampleColumnAccess
//{
//    // 同程序集静态助手由转译器按普通函数处理；调用方保证索引有效。
//    public static T* ElementPtr<T>(ref SampleColumn<T> view, int index) where T : unmanaged
//    {
//        if (view.Chunks == null) return view.Flat + index;
//        int block = index / view.Capacity;
//        int offset = index - block * view.Capacity;
//        return view.Chunks[block] + offset;
//    }
//}

//public static class NativeFeatureMath
//{
//    // 转译器把此提示映射到 C++；调用方仍写普通静态方法调用。
//    [MethodImpl(MethodImplOptions.AggressiveInlining)]
//    public static int Add(int value, int delta) { return value + delta; }
//}

//[NativeTranspile]
//public struct NativeFeatureOptionsJob : IJobParallelFor
//{
//    public NativeArray<int> Input;
//    public NativeArray<int> Output;
//    public ulong Options;

//    public void Execute(int index)
//    {
//        int value = Input[index];
//        // 数组句柄保持不变，只读写元素；固定小循环和三个位开关由生成器分析。
//        // 使用高位掩码，验证该能力同时适用于 64 位整数。
//        for (int k = 0; k < 4; k++)
//        {
//            if ((Options & 1UL) != 0) value += k;
//            if ((Options & (1UL << 40)) != 0) value *= 2;
//            if ((Options & (1UL << 63)) != 0) value -= 1;
//        }
//        Output[index] = value;
//    }
//}

//public struct NativeFeatureStatus : IComponentData
//{
//    // ECS 列步长应与 C# 实际布局一致，不能把 bool 按 interop 的大小计算。
//    public bool Active;
//    public byte Rank;
//}

//public static unsafe class Program
//{
//    public static int Main()
//    {
//        JobScheduler.Initialize(2);
//        try
//        {
//            if (!JobScheduler.IsNative)
//                throw new InvalidOperationException("This sample requires the compiled NativeDll and NativeTranspiled libraries.");
//            ShowColumnViews();
//            ShowDependencies();
//            ShowOrdinaryCodeOptimizations();
//            CopiedContextDemo.Run();
//            ShowLayoutAndSnapshot();
//            Console.WriteLine("PASS: native execution examples.");
//            return 0;
//        }
//        finally { JobScheduler.Shutdown(); }
//    }

//    private static void ShowColumnViews()
//    {
//        using var first = new NativeArray<int>(4);
//        using var second = new NativeArray<int>(4);
//        using var tail = new NativeArray<int>(2);
//        using var copy = new NativeArray<int>(10);
//        // 三块容量分别为 4、4、2，前两块固定容量为 4，最后一块允许不满。
//        // 块表在栈上，数据属于三个 NativeArray；全部 Job 完成前它们必须保持有效。
//        void** chunks = stackalloc void*[3] { first.GetUnsafePtr(), second.GetUnsafePtr(), tail.GetUnsafePtr() };
//        var segmented = new SampleColumn<int> { Chunks = (int**)chunks, Capacity = 4, Length = 10 };
//        new NativeFeatureColumnJob { Values = segmented, Delta = 7 }.Schedule(segmented.Length, 3).Complete();
//        for (int i = 0; i < segmented.Length; i++) Require(*SampleColumnAccess.ElementPtr(ref segmented, i) == 7, "Segmented access failed.");
//        Require(SampleColumnAccess.ElementPtr(ref segmented, 8) == (int*)tail.GetUnsafePtr(), "Tail chunk address differs.");

//        SampleColumn<int> flat = copy; // 隐式转换只创建连续视图，不复制数组。
//        new NativeFeatureColumnJob { Values = flat, Delta = 2 }.Run(flat.Length);
//        Require(copy[9] == 2, "Flat column Run failed.");
//        segmented = default; // 只清空视图，原数组和块指针表仍由调用方管理。
//        Require(first[0] == 7, "Resetting a view changed its owner.");
//        Console.WriteLine("PASS: application-owned flat/segmented view and static native helper.");
//    }

//    private static void ShowDependencies()
//    {
//        using var left = new NativeArray<int>(17);
//        using var right = new NativeArray<int>(17);
//        var a = new NativeFeatureColumnJob { Values = left, Delta = 1 }.Schedule(left.Length, 4);
//        var b = new NativeFeatureColumnJob { Values = right, Delta = 2 }.Schedule(right.Length, 4);
//        // 两个独立数组可并行处理；Native 两句柄重载无需创建 params 数组。
//        var ready = JobHandle.CombineDependencies(a, b);
//        new NativeFeatureColumnJob { Values = left, Delta = 10 }.Schedule(left.Length, 4, ready).Complete();
//        Require(left[16] == 11 && right[16] == 2, "Combined dependency did not protect its inputs.");
//        Console.WriteLine("PASS: two-handle CombineDependencies and a dependent generated job.");
//    }

//    private static void ShowOrdinaryCodeOptimizations()
//    {
//        var input = new NativeArray<int>(19);
//        using var output = new NativeArray<int>(19);
//        try
//        {
//            // 依次覆盖零/非零开关、分离/同址数组、Run/Schedule，结果都与标量参考对照。
//            // 同址时每个 index 只访问自己的元素，因此不同 index 之间没有数据依赖。
//            foreach (ulong options in new[] { 0UL, 1UL | (1UL << 40) | (1UL << 63) })
//                for (int alias = 0; alias < 2; alias++)
//                    for (int path = 0; path < 2; path++)
//                    {
//                        for (int i = 0; i < input.Length; i++) input[i] = i;
//                        var job = new NativeFeatureOptionsJob { Input = input, Output = alias == 0 ? output : input, Options = options };
//                        if (path == 0) job.Run(input.Length); else job.Schedule(input.Length, 4).Complete();
//                        for (int i = 0; i < input.Length; i++)
//                        {
//                            int expected = i;
//                            for (int k = 0; k < 4; k++)
//                            {
//                                if ((options & 1UL) != 0) expected += k;
//                                if ((options & (1UL << 40)) != 0) expected *= 2;
//                                if ((options & (1UL << 63)) != 0) expected--;
//                            }
//                            Require(job.Output[i] == expected, "Run/Schedule or aliased input changed the result.");
//                        }
//                    }
//        }
//        finally { input.Dispose(); }
//        Console.WriteLine("PASS: zero/nonzero ulong options, fixed loop, stable array fields and same-buffer aliases.");
//    }

//    private static void ShowLayoutAndSnapshot()
//    {
//        using var world = new World("Native execution snapshot");
//        // 使用 ECS 默认容量，259 个实体跨越多个普通 Chunk，不设置大 Chunk。
//        var entities = world.EntityManager.CreateEntities(259, typeof(NativeFeatureStatus));
//        var entity = entities[258];
//        world.EntityManager.SetComponent(entity, new NativeFeatureStatus { Active = true, Rank = 7 });
//        Require(ComponentTypeManager.GetComponentType(typeof(NativeFeatureStatus)).Size == Unsafe.SizeOf<NativeFeatureStatus>(),
//            "ECS component stride differs from the C# value layout.");
//        var snapshot = world.TakeSnapshot();
//        // 修改后恢复，核对 byte 和 bool 字段都按原始 C# 值布局保存。
//        world.EntityManager.SetComponent(entity, new NativeFeatureStatus { Rank = 99 });
//        world.Restore(snapshot);
//        Require(world.EntityManager.GetComponent<NativeFeatureStatus>(entity).Rank == 7, "Snapshot restore failed.");

//        Require(world.EntityManager.GetComponent<NativeFeatureStatus>(entity).Active, "Snapshot lost the bool field.");
//        Console.WriteLine("PASS: CLR bool/byte layout, normal ECS chunks and snapshot restore.");
//    }

//    internal static void Require(bool condition, string message)
//    {
//        if (!condition) throw new InvalidOperationException(message);
//    }
//}
