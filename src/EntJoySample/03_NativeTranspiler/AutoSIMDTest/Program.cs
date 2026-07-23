// Auto-SIMD Benchmark — 7 后端 / 4 Job 类型全面对比
using EntJoy;
using EntJoy.Collections;
using EntJoySample.AutoSIMDTest;
using System.Diagnostics;

namespace EntJoySample.AutoSIMDTest
{
    public static class Program
    {
        // 每个 Case 的数据：名称、C# struct、C++ struct、SIMD-PF、SIMD-For、SIMD-IJob、ISPC struct
        // 静态函数组独立处理
        public static void Main()
        {
            NativeJobScheduler.Initialize();
            int N = 100000, W = 3, I = 100;
            var rnd = new Random(42);
            var rd = GenFloats(N * 100, rnd);
            using var nR = new NativeArray<float>(rd, Allocator.Persistent);
            var a = GenFloats(N, rnd); var b = GenFloats(N, rnd); var c = GenFloats(N, rnd);
            var qx = GenFloats(N, rnd); var qy = GenFloats(N, rnd);
            var dx = GenFloats(N * 2, rnd); var dy = GenFloats(N * 2, rnd);
            var ix = new int[N * 50]; for (int t = 0; t < N * 50; t++) ix[t] = rnd.Next(N * 2);
            using var nA = new NativeArray<float>(a, Allocator.Persistent);
            using var nB = new NativeArray<float>(b, Allocator.Persistent);
            using var nC = new NativeArray<float>(c, Allocator.Persistent);
            using var nQx = new NativeArray<float>(qx, Allocator.Persistent);
            using var nQy = new NativeArray<float>(qy, Allocator.Persistent);
            using var nDx = new NativeArray<float>(dx, Allocator.Persistent);
            using var nDy = new NativeArray<float>(dy, Allocator.Persistent);
            using var nIx = new NativeArray<int>(ix, Allocator.Persistent);
            var rp = new NativeArray<float>[6];
            for (int i = 0; i < 6; i++) rp[i] = new NativeArray<float>(N, Allocator.Persistent);

            Console.WriteLine("=".PadRight(90, '='));
            Console.WriteLine("  Auto-SIMD — C# / C++ / SIMD-PF / SIMD-For / SIMD-IJob / ISPC");
            Console.WriteLine("  N=100k | 预热=3 | 迭代=100(R/G=10) | 批=64");
            Console.WriteLine("=".PadRight(90, '='));
            Console.WriteLine($"{"Case",-20} {"C#",-9} {"C++",-9} {"SIMDPF",-9} {"SIMDFor",-9} {"IJob",-9} {"ISPC",-9}");
            Console.WriteLine("-".PadRight(90, '-'));

            int heavyIter = 10; // Reduce/Gather
            int normalIter = I;

            // Case 1
            double i1 = CCSharp(new SimpleArith_CSharp{A=nA,B=nB,C=nC,Result=rp[0]}, N, W, normalIter);
            double i2 = CSched<SimpleArith_Cpp>(new SimpleArith_Cpp{A=nA,B=nB,C=nC,Result=rp[1]}, N, W, normalIter);
            double i3 = CSched<SimpleArith_SIMD_PF>(new SimpleArith_SIMD_PF{A=nA,B=nB,C=nC,Result=rp[2]}, N, W, normalIter);
            double i4 = CSchedFor<SimpleArith_SIMD_For>(new SimpleArith_SIMD_For{A=nA,B=nB,C=nC,Result=rp[3]}, N, W, normalIter);
            double i5 = CIJob(new SimpleArith_SIMD_IJob{A=nA,B=nB,C=nC,Result=rp[4],Count=N}, W, normalIter);
            double i6 = CSched<SimpleArith_ISPC>(new SimpleArith_ISPC{A=nA,B=nB,C=nC,Result=rp[5]}, N, W, normalIter);
            PrintRow("1_SimpleArith", i1, i2, i3, i4, i5, i6);

            // Case 2
            double j1 = CCSharp(new MathFuncs_CSharp{A=nA,Result=rp[0]}, N, W, normalIter);
            double j2 = CSched<MathFuncs_Cpp>(new MathFuncs_Cpp{A=nA,Result=rp[1]}, N, W, normalIter);
            double j3 = CSched<MathFuncs_SIMD_PF>(new MathFuncs_SIMD_PF{A=nA,Result=rp[2]}, N, W, normalIter);
            double j4 = CSchedFor<MathFuncs_SIMD_For>(new MathFuncs_SIMD_For{A=nA,Result=rp[3]}, N, W, normalIter);
            double j5 = CIJob(new MathFuncs_SIMD_IJob{A=nA,Result=rp[4],Count=N}, W, normalIter);
            double j6 = CSched<MathFuncs_ISPC>(new MathFuncs_ISPC{A=nA,Result=rp[5]}, N, W, normalIter);
            PrintRow("2_MathFunctions", j1, j2, j3, j4, j5, j6);

            // Case 3
            double k1 = CCSharp(new SimpleReduce_CSharp{A=nR,Result=rp[0]}, N, W, heavyIter);
            double k2 = CSched<SimpleReduce_Cpp>(new SimpleReduce_Cpp{A=nR,Result=rp[1]}, N, W, heavyIter);
            double k3 = CSched<SimpleReduce_SIMD_PF>(new SimpleReduce_SIMD_PF{A=nR,Result=rp[2]}, N, W, heavyIter);
            double k4 = CSchedFor<SimpleReduce_SIMD_For>(new SimpleReduce_SIMD_For{A=nR,Result=rp[3]}, N, W, heavyIter);
            double k5 = CIJob(new SimpleReduce_SIMD_IJob{A=nR,Result=rp[4],Count=N}, W, heavyIter);
            double k6 = CSched<SimpleReduce_ISPC>(new SimpleReduce_ISPC{A=nR,Result=rp[5]}, N, W, heavyIter);
            PrintRow("3_SimpleReduce", k1, k2, k3, k4, k5, k6);

            // Case 4
            double l1 = CCSharp(new ComplexFlow_CSharp{A=nA,B=nB,Result=rp[0],Threshold=50}, N, W, normalIter);
            double l2 = CSched<ComplexFlow_Cpp>(new ComplexFlow_Cpp{A=nA,B=nB,Result=rp[1],Threshold=50}, N, W, normalIter);
            double l3 = CSched<ComplexFlow_SIMD_PF>(new ComplexFlow_SIMD_PF{A=nA,B=nB,Result=rp[2],Threshold=50}, N, W, normalIter);
            double l4 = CSchedFor<ComplexFlow_SIMD_For>(new ComplexFlow_SIMD_For{A=nA,B=nB,Result=rp[3],Threshold=50}, N, W, normalIter);
            double l5 = CIJob(new ComplexFlow_SIMD_IJob{A=nA,B=nB,Result=rp[4],Threshold=50,Count=N}, W, normalIter);
            double l6 = CSched<ComplexFlow_ISPC>(new ComplexFlow_ISPC{A=nA,B=nB,Result=rp[5],Threshold=50}, N, W, normalIter);
            PrintRow("4_ComplexFlow", l1, l2, l3, l4, l5, l6);

            // Case 5
            double m1 = CCSharp(new GatherReduce_CSharp{QueryX=nQx,QueryY=nQy,DataX=nDx,DataY=nDy,Index=nIx,Result=rp[0]}, N, W, heavyIter);
            double m2 = CSched<GatherReduce_Cpp>(new GatherReduce_Cpp{QueryX=nQx,QueryY=nQy,DataX=nDx,DataY=nDy,Index=nIx,Result=rp[1]}, N, W, heavyIter);
            double m3 = CSched<GatherReduce_SIMD_PF>(new GatherReduce_SIMD_PF{QueryX=nQx,QueryY=nQy,DataX=nDx,DataY=nDy,Index=nIx,Result=rp[2]}, N, W, heavyIter);
            double m4 = CSchedFor<GatherReduce_SIMD_For>(new GatherReduce_SIMD_For{QueryX=nQx,QueryY=nQy,DataX=nDx,DataY=nDy,Index=nIx,Result=rp[3]}, N, W, heavyIter);
            double m5 = CIJob(new GatherReduce_SIMD_IJob{QueryX=nQx,QueryY=nQy,DataX=nDx,DataY=nDy,Index=nIx,Result=rp[4],Count=N}, W, heavyIter);
            double m6 = CSched<GatherReduce_ISPC>(new GatherReduce_ISPC{QueryX=nQx,QueryY=nQy,DataX=nDx,DataY=nDy,Index=nIx,Result=rp[5]}, N, W, heavyIter);
            PrintRow("5_GatherReduce", m1, m2, m3, m4, m5, m6);

            Console.WriteLine("-".PadRight(90, '-'));
            Console.WriteLine("  C#=CSharp Execute | C++=IJobPF Schedule | SIMDPF=IJobPF Schedule");
            Console.WriteLine("  SIMDFor=IJobFor Schedule | IJob=Execute | ISPC=IJobPF Schedule");
            Console.WriteLine();
            for (int i = 0; i < 6; i++) rp[i].Dispose();
        }

        static void PrintRow(string name, params double[] v)
        { Console.WriteLine($"  {name,-20} {v[0],8:F3}{v[1],8:F3}{v[2],8:F3}{v[3],8:F3}{v[4],8:F3}{v[5],8:F3}"); }

        static float[] GenFloats(int n, Random r) { var d = new float[n]; for (int i=0;i<n;i++) d[i]=(float)(r.NextDouble()*200-100); return d; }

        static double CCSharp<T>(T job, int n, int w, int it) where T : struct, IJobParallelFor
        { for (int i=0;i<w;i++) for (int j=0;j<n;j++) job.Execute(j); var s=Stopwatch.StartNew(); for (int t=0;t<it;t++) for (int i=0;i<n;i++) job.Execute(i); s.Stop(); return s.Elapsed.TotalMilliseconds/it; }

        static double CSched<T>(T job, int n, int w, int it) where T : struct, IJobParallelFor
        { for (int i=0;i<w;i++) job.Schedule(n,64).Complete(); var s=Stopwatch.StartNew(); for (int t=0;t<it;t++){job.Schedule(n,64).Complete();} s.Stop(); return s.Elapsed.TotalMilliseconds/it; }

        static double CSchedFor<T>(T job, int n, int w, int it) where T : struct, IJobFor
        { for (int i=0;i<w;i++) job.Schedule(n).Complete(); var s=Stopwatch.StartNew(); for (int t=0;t<it;t++){job.Schedule(n).Complete();} s.Stop(); return s.Elapsed.TotalMilliseconds/it; }

        static double CIJob<T>(T job, int w, int it) where T : struct, IJob
        { for (int i=0;i<w;i++) job.Execute(); var s=Stopwatch.StartNew(); for (int t=0;t<it;t++) job.Execute(); s.Stop(); return s.Elapsed.TotalMilliseconds/it; }
    }
}
