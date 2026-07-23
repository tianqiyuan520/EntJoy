// Auto-SIMD Benchmark — C# / C++ / AutoSIMD / ISPC
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
            var a = G(N, rnd); var b = G(N, rnd); var c = G(N, rnd);
            var rd = G(N * 100, rnd);
            var qx = G(N, rnd); var qy = G(N, rnd);
            var dx = G(N * 2, rnd); var dy = G(N * 2, rnd);
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
            using var r0 = new NativeArray<float>(N, Allocator.Persistent);
            using var r1 = new NativeArray<float>(N, Allocator.Persistent);
            using var r2 = new NativeArray<float>(N, Allocator.Persistent);
            using var r3 = new NativeArray<float>(N, Allocator.Persistent);

            Console.WriteLine("=".PadRight(65, '='));
            Console.WriteLine("  Auto-SIMD Benchmark — C# / C++ / AutoSIMD / ISPC");
            Console.WriteLine("  N=100k | C#=Execute | C++/SIMD/ISPC=Schedule->NativeDll");
            Console.WriteLine("  C++/ISPC=IJobPF(并行) | AutoSIMD=IJobFor(串行), 需对比请等比换算");
            Console.WriteLine("=".PadRight(65, '='));
            Console.WriteLine($"{"Case",-20} {"C#(ms)",-9} {"C++(ms)",-9} {"AutoSIMD(ms)",-14} {"ISPC(ms)",-9}");
            Console.WriteLine("-".PadRight(65, '-'));

            // 1_SimpleArith
            R("1_SimpleArith",
                Pf(new SimpleArith_CSharp { A=nA, B=nB, C=nC, Result=r0 }, N, W, I),
                Sch<SimpleArith_Cpp>(new SimpleArith_Cpp { A=nA, B=nB, C=nC, Result=r1 }, N, W, I),
                SchF<SimpleArith_SIMD>(new SimpleArith_SIMD { A=nA, B=nB, C=nC, Result=r2 }, N, W, I),
                Sch<SimpleArith_ISPC>(new SimpleArith_ISPC { A=nA, B=nB, C=nC, Result=r3 }, N, W, I));
            // 2_MathFunctions
            R("2_MathFunctions",
                Pf(new MathFuncs_CSharp { A=nA, Result=r0 }, N, W, I),
                Sch<MathFuncs_Cpp>(new MathFuncs_Cpp { A=nA, Result=r1 }, N, W, I),
                SchF<MathFuncs_SIMD>(new MathFuncs_SIMD { A=nA, Result=r2 }, N, W, I),
                Sch<MathFuncs_ISPC>(new MathFuncs_ISPC { A=nA, Result=r3 }, N, W, I));
            // 3_SimpleReduce
            R("3_SimpleReduce",
                Pf(new SimpleReduce_CSharp { A=nR, Result=r0 }, N, W, 10),
                Sch<SimpleReduce_Cpp>(new SimpleReduce_Cpp { A=nR, Result=r1 }, N, W, 10),
                SchF<SimpleReduce_SIMD>(new SimpleReduce_SIMD { A=nR, Result=r2 }, N, W, 10),
                Sch<SimpleReduce_ISPC>(new SimpleReduce_ISPC { A=nR, Result=r3 }, N, W, 10));
            // 4_ComplexFlow
            R("4_ComplexFlow",
                Pf(new ComplexFlow_CSharp { A=nA, B=nB, Result=r0, Threshold=50 }, N, W, I),
                Sch<ComplexFlow_Cpp>(new ComplexFlow_Cpp { A=nA, B=nB, Result=r1, Threshold=50 }, N, W, I),
                SchF<ComplexFlow_SIMD>(new ComplexFlow_SIMD { A=nA, B=nB, Result=r2, Threshold=50 }, N, W, I),
                Sch<ComplexFlow_ISPC>(new ComplexFlow_ISPC { A=nA, B=nB, Result=r3, Threshold=50 }, N, W, I));
            // 5_GatherReduce
            R("5_GatherReduce",
                Pf(new GatherReduce_CSharp { QueryX=nQx, QueryY=nQy, DataX=nDx, DataY=nDy, Index=nIx, Result=r0 }, N, W, 10),
                Sch<GatherReduce_Cpp>(new GatherReduce_Cpp { QueryX=nQx, QueryY=nQy, DataX=nDx, DataY=nDy, Index=nIx, Result=r1 }, N, W, 10),
                SchF<GatherReduce_SIMD>(new GatherReduce_SIMD { QueryX=nQx, QueryY=nQy, DataX=nDx, DataY=nDy, Index=nIx, Result=r2 }, N, W, 10),
                Sch<GatherReduce_ISPC>(new GatherReduce_ISPC { QueryX=nQx, QueryY=nQy, DataX=nDx, DataY=nDy, Index=nIx, Result=r3 }, N, W, 10));

            Console.WriteLine("-".PadRight(65, '-'));
            Console.WriteLine("  C++=IJobPF Schedule | AutoSIMD=IJobFor Schedule | ISPC=IJobPF Schedule");
            Console.WriteLine();
        }

        static void R(string n, params double[] v)
        { Console.WriteLine($"  {n,-20} {v[0],8:F3}{v[1],8:F3}{v[2],10:F3}   {v[3],8:F3}"); }
        static float[] G(int n, Random r) { var d = new float[n]; for (int i=0;i<n;i++) d[i]=(float)(r.NextDouble()*200-100); return d; }

        static double Pf<T>(T job, int n, int w, int it) where T : struct, IJobParallelFor
        { for (int i=0;i<w;i++) for (int j=0;j<n;j++) job.Execute(j); var s=Stopwatch.StartNew(); for (int t=0;t<it;t++) for (int i=0;i<n;i++) job.Execute(i); s.Stop(); return s.Elapsed.TotalMilliseconds/it; }

        static double Sch<T>(T job, int n, int w, int it) where T : struct, IJobParallelFor
        { for (int i=0;i<w;i++) job.Schedule(n,64).Complete(); var s=Stopwatch.StartNew(); for (int t=0;t<it;t++){job.Schedule(n,64).Complete();} s.Stop(); return s.Elapsed.TotalMilliseconds/it; }

        static double SchF<T>(T job, int n, int w, int it) where T : struct, IJobFor
        { for (int i=0;i<w;i++) job.Schedule(n).Complete(); var s=Stopwatch.StartNew(); for (int t=0;t<it;t++){job.Schedule(n).Complete();} s.Stop(); return s.Elapsed.TotalMilliseconds/it; }
    }
}
