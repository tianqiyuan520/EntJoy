// Auto-SIMD Benchmark — C# / C++ / AutoSIMD / ISPC 同调度路径对比
using EntJoy;
using EntJoy.Collections;
using EntJoySample.AutoSIMDTest;
using System.Diagnostics;

namespace EntJoySample.AutoSIMDTest
{
    public static class Program
    {
        public static void Main()
        {
            NativeJobScheduler.Initialize();
            int N = 100000, W = 3, I = 100;
            var rnd = new Random(42);
            var a = GenFloats(N, rnd); var b = GenFloats(N, rnd); var c = GenFloats(N, rnd);
            var rd = GenFloats(N * 100, rnd);
            var qx = GenFloats(N, rnd); var qy = GenFloats(N, rnd);
            var dx = GenFloats(N * 2, rnd); var dy = GenFloats(N * 2, rnd);
            var ix = new int[N * 50]; for (int t = 0; t < N * 50; t++) ix[t] = rnd.Next(N * 2);

            using var nA = new NativeArray<float>(a, Allocator.Persistent);
            using var nB = new NativeArray<float>(b, Allocator.Persistent);
            using var nC = new NativeArray<float>(c, Allocator.Persistent);
            using var nR = new NativeArray<float>(rd, Allocator.Persistent);
            using var nQx = new NativeArray<float>(qx, Allocator.Persistent);
            using var nQy = new NativeArray<float>(qy, Allocator.Persistent);
            using var nDx = new NativeArray<float>(dx, Allocator.Persistent);
            using var nDy = new NativeArray<float>(dy, Allocator.Persistent);
            using var nIx = new NativeArray<int>(ix, Allocator.Persistent);
            var rp = new NativeArray<float>[4];
            for (int i = 0; i < 4; i++) rp[i] = new NativeArray<float>(N, Allocator.Persistent);

            Console.WriteLine("=".PadRight(66, '='));
            Console.WriteLine("  Auto-SIMD Benchmark — 4 后端均用 IJobPF Schedule");
            Console.WriteLine("  N=100k | 预热=3 | 迭代=100(R/G=10) | 批大小=64");
            Console.WriteLine("=".PadRight(66, '='));
            Console.WriteLine($"{"Case",-20} {"C#(ms)",-9} {"C++(ms)",-9} {"SIMD(ms)",-11} {"ISPC(ms)",-9}");
            Console.WriteLine("-".PadRight(66, '-'));

            PrintRow("1_SimpleArith",
                MeasureCSharp(new SimpleArith_CSharp{A=nA,B=nB,C=nC,Result=rp[0]}, N, W, I),
                MeasureSchedule<SimpleArith_Cpp>(new SimpleArith_Cpp{A=nA,B=nB,C=nC,Result=rp[1]}, N, W, I),
                MeasureSchedule<SimpleArith_SIMD_PF>(new SimpleArith_SIMD_PF{A=nA,B=nB,C=nC,Result=rp[2]}, N, W, I),
                MeasureSchedule<SimpleArith_ISPC>(new SimpleArith_ISPC{A=nA,B=nB,C=nC,Result=rp[3]}, N, W, I));

            PrintRow("2_MathFunctions",
                MeasureCSharp(new MathFuncs_CSharp{A=nA,Result=rp[0]}, N, W, I),
                MeasureSchedule<MathFuncs_Cpp>(new MathFuncs_Cpp{A=nA,Result=rp[1]}, N, W, I),
                MeasureSchedule<MathFuncs_SIMD_PF>(new MathFuncs_SIMD_PF{A=nA,Result=rp[2]}, N, W, I),
                MeasureSchedule<MathFuncs_ISPC>(new MathFuncs_ISPC{A=nA,Result=rp[3]}, N, W, I));

            PrintRow("3_SimpleReduce",
                MeasureCSharp(new SimpleReduce_CSharp{A=nR,Result=rp[0]}, N, W, 10),
                MeasureSchedule<SimpleReduce_Cpp>(new SimpleReduce_Cpp{A=nR,Result=rp[1]}, N, W, 10),
                MeasureSchedule<SimpleReduce_SIMD_PF>(new SimpleReduce_SIMD_PF{A=nR,Result=rp[2]}, N, W, 10),
                MeasureSchedule<SimpleReduce_ISPC>(new SimpleReduce_ISPC{A=nR,Result=rp[3]}, N, W, 10));

            PrintRow("4_ComplexFlow",
                MeasureCSharp(new ComplexFlow_CSharp{A=nA,B=nB,Result=rp[0],Threshold=50}, N, W, I),
                MeasureSchedule<ComplexFlow_Cpp>(new ComplexFlow_Cpp{A=nA,B=nB,Result=rp[1],Threshold=50}, N, W, I),
                MeasureSchedule<ComplexFlow_SIMD_PF>(new ComplexFlow_SIMD_PF{A=nA,B=nB,Result=rp[2],Threshold=50}, N, W, I),
                MeasureSchedule<ComplexFlow_ISPC>(new ComplexFlow_ISPC{A=nA,B=nB,Result=rp[3],Threshold=50}, N, W, I));

            PrintRow("5_GatherReduce",
                MeasureCSharp(new GatherReduce_CSharp{QueryX=nQx,QueryY=nQy,DataX=nDx,DataY=nDy,Index=nIx,Result=rp[0]}, N, W, 10),
                MeasureSchedule<GatherReduce_Cpp>(new GatherReduce_Cpp{QueryX=nQx,QueryY=nQy,DataX=nDx,DataY=nDy,Index=nIx,Result=rp[1]}, N, W, 10),
                MeasureSchedule<GatherReduce_SIMD_PF>(new GatherReduce_SIMD_PF{QueryX=nQx,QueryY=nQy,DataX=nDx,DataY=nDy,Index=nIx,Result=rp[2]}, N, W, 10),
                MeasureSchedule<GatherReduce_ISPC>(new GatherReduce_ISPC{QueryX=nQx,QueryY=nQy,DataX=nDx,DataY=nDy,Index=nIx,Result=rp[3]}, N, W, 10));

            Console.WriteLine("-".PadRight(66, '-'));
            Console.WriteLine("  C++=IJobPF Schedule | AutoSIMD=IJobPF Schedule | ISPC=IJobPF Schedule");
            Console.WriteLine();
            for (int i = 0; i < 4; i++) rp[i].Dispose();
        }

        static void PrintRow(string name, params double[] values)
        { Console.WriteLine($"  {name,-20} {values[0],8:F3}{values[1],8:F3}{values[2],9:F3}   {values[3],8:F3}"); }

        static float[] GenFloats(int n, Random r) { var d = new float[n]; for (int i=0;i<n;i++) d[i]=(float)(r.NextDouble()*200-100); return d; }

        static double MeasureCSharp<T>(T job, int n, int w, int it) where T : struct, IJobParallelFor
        { for (int i=0;i<w;i++) for (int j=0;j<n;j++) job.Execute(j); var s=Stopwatch.StartNew(); for (int t=0;t<it;t++) for (int i=0;i<n;i++) job.Execute(i); s.Stop(); return s.Elapsed.TotalMilliseconds/it; }

        static double MeasureSchedule<T>(T job, int n, int w, int it) where T : struct, IJobParallelFor
        { for (int i=0;i<w;i++) job.Schedule(n,64).Complete(); var s=Stopwatch.StartNew(); for (int t=0;t<it;t++){job.Schedule(n,64).Complete();} s.Stop(); return s.Elapsed.TotalMilliseconds/it; }
    }
}
