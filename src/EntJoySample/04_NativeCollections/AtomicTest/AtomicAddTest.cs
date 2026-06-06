//using EntJoy.Collections;
//using EntJoy.Mathematics;

//[NativeTranspiler.NativeTranspile]
//public unsafe struct CppAtomicAddTestJob : IJobParallelFor
//{
//    public int* array1;
//    public NativeArray<int> array2;

//    public void Execute(int index)
//    {
//        int v = Interlocked.Add(ref array1[0], 1) - 1;
//        array2[index] = v;
//    }
//}

//[NativeTranspiler.NativeTranspile(Target = "Ispc")]
//public unsafe struct IspcAtomicAddTestJob : IJobParallelFor
//{
//    public int* array1;
//    public NativeArray<int> array2;

//    public void Execute(int index)
//    {
//        int v = Interlocked.Add(ref array1[0], 1) - 1;
//        array2[index] = v;
//    }
//}


//public class AtomicAddTest
//{
//    public unsafe static void Main()
//    {
//        NativeArray<int> a = new(1);
//        a[0] = 0;
//        NativeArray<int> b = new(105);
//        NativeArray<int> c = new(105);

//        var job = new CppAtomicAddTestJob
//        {
//            array1 = (int*)a.GetUnsafePtr(),
//            array2 = b
//        };
//        job.Schedule(100, 0).Complete();

//        Console.WriteLine($"a[0] {a[0]}");
//        for (int i = 0; i < b.Length; i++) { Console.WriteLine(b[i]); }
//        Console.WriteLine();

//        a[0] = 0;
//        var job2 = new IspcAtomicAddTestJob
//        {
//            array1 = (int*)a.GetUnsafePtr(),
//            array2 = c
//        };
//        job2.Schedule(100, 0).Complete();

//        Console.WriteLine($"a[0] {a[0]}");
//        for (int i = 0; i < c.Length; i++) { Console.WriteLine(c[i]); }
//    }
//}

