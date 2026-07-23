// Auto-SIMD — C# | C++ | SIMD | ISPC x Job | For | PF = 10+ variants
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
            int N = 100000, W = 3, I = 100, H = 10;
            var rnd = new Random(42);
            var rd = GF(N * 100, rnd);
            using var nR = new NativeArray<float>(rd, Allocator.Persistent);
            var a = GF(N, rnd); var b = GF(N, rnd); var c = GF(N, rnd);
            var qx = GF(N, rnd); var qy = GF(N, rnd);
            var dx = GF(N * 2, rnd); var dy = GF(N * 2, rnd);
            var ix = new int[N * 50]; for (int t = 0; t < N * 50; t++) ix[t] = rnd.Next(N * 2);
            using var nA = NA(a); using var nB = NA(b); using var nC = NA(c);
            using var nQx = NA(qx); using var nQy = NA(qy);
            using var nDx = NA(dx); using var nDy = NA(dy); using var nIx = NI(ix);
            using var p0 = NA(N); using var p1 = NA(N); using var p2 = NA(N);
            using var p3 = NA(N); using var p4 = NA(N); using var p5 = NA(N);

            Console.WriteLine("=".PadRight(105, '='));
            Console.WriteLine("  Auto-SIMD: C# | C++ | SIMD | ISPC  x  Job | IJobFor | IJobPF");
            Console.WriteLine("=".PadRight(105, '='));
            Console.WriteLine($"{"Case",-14} {"C#Job",-9}{"C#For",-9}{"C#PF",-9} {"CppJob",-9}{"CppFor",-9}{"CppPF",-9} {"SMDJob",-9}{"SMDFor",-9}{"SMDPF",-9} {"ISPF",-9}");
            Console.WriteLine("-".PadRight(105, '-'));

            // (CJ=C#Job, CF=C#For, CP=C#PF, NJ=CppJob, NF=CppFor, NP=CppPF, SJ=SIMDJob, SF=SIMDFor, SP=SIMDPF, IP=ISPC)
            void T(string name, int it,
                System.Func<double> CJ, System.Func<double> CF, System.Func<double> CP,
                System.Func<double> NJ, System.Func<double> NF, System.Func<double> NP,
                System.Func<double> SJ, System.Func<double> SF, System.Func<double> SP,
                System.Func<double> IP)
            { Console.WriteLine($"  {name,-12}{CJ(),8:F3}{CF(),8:F3}{CP(),8:F3}{NJ(),8:F3}{NF(),8:F3}{NP(),8:F3}{SJ(),8:F3}{SF(),8:F3}{SP(),8:F3}{IP(),8:F3}"); }

            // 1
            T("SimpleArith", I,
                ()=>Ij(new SimpleArith_CSharp_Job{A=nA,B=nB,C=nC,Result=p0,Count=N},W,I),
                ()=>Fo(new SimpleArith_CSharp_For{A=nA,B=nB,C=nC,Result=p1},N,W,I),
                ()=>Pf(new SimpleArith_CSharp_PF{A=nA,B=nB,C=nC,Result=p2},N,W,I),
                ()=>Ij(new SimpleArith_Cpp_Job{A=nA,B=nB,C=nC,Result=p0,Count=N},W,I),
                ()=>Fo(new SimpleArith_Cpp_For{A=nA,B=nB,C=nC,Result=p1},N,W,I),
                ()=>Pf(new SimpleArith_Cpp_PF{A=nA,B=nB,C=nC,Result=p2},N,W,I),
                ()=>Ij(new SimpleArith_SIMD_Job{A=nA,B=nB,C=nC,Result=p3,Count=N},W,I),
                ()=>Fo(new SimpleArith_SIMD_For{A=nA,B=nB,C=nC,Result=p4},N,W,I),
                ()=>Pf(new SimpleArith_SIMD_PF{A=nA,B=nB,C=nC,Result=p5},N,W,I),
                ()=>Pf(new SimpleArith_ISPC_PF{A=nA,B=nB,C=nC,Result=p0},N,W,I));

            // 2
            T("MathFuncs", I,
                ()=>Ij(new MathFuncs_CSharp_Job{A=nA,Result=p0,Count=N},W,I),
                ()=>Fo(new MathFuncs_CSharp_For{A=nA,Result=p1},N,W,I),
                ()=>Pf(new MathFuncs_CSharp_PF{A=nA,Result=p2},N,W,I),
                ()=>Ij(new MathFuncs_Cpp_Job{A=nA,Result=p0,Count=N},W,I),
                ()=>Fo(new MathFuncs_Cpp_For{A=nA,Result=p1},N,W,I),
                ()=>Pf(new MathFuncs_Cpp_PF{A=nA,Result=p2},N,W,I),
                ()=>Ij(new MathFuncs_SIMD_Job{A=nA,Result=p3,Count=N},W,I),
                ()=>Fo(new MathFuncs_SIMD_For{A=nA,Result=p4},N,W,I),
                ()=>Pf(new MathFuncs_SIMD_PF{A=nA,Result=p5},N,W,I),
                ()=>Pf(new MathFuncs_ISPC_PF{A=nA,Result=p0},N,W,I));

            // 3
            T("Reduce", H,
                ()=>Ij(new SimpleReduce_CSharp_Job{A=nR,Result=p0,Count=N},W,H),
                ()=>Fo(new SimpleReduce_CSharp_For{A=nR,Result=p1},N,W,H),
                ()=>Pf(new SimpleReduce_CSharp_PF{A=nR,Result=p2},N,W,H),
                ()=>Ij(new SimpleReduce_Cpp_Job{A=nR,Result=p0,Count=N},W,H),
                ()=>Fo(new SimpleReduce_Cpp_For{A=nR,Result=p1},N,W,H),
                ()=>Pf(new SimpleReduce_Cpp_PF{A=nR,Result=p2},N,W,H),
                ()=>Ij(new SimpleReduce_SIMD_Job{A=nR,Result=p3,Count=N},W,H),
                ()=>Fo(new SimpleReduce_SIMD_For{A=nR,Result=p4},N,W,H),
                ()=>Pf(new SimpleReduce_SIMD_PF{A=nR,Result=p5},N,W,H),
                ()=>Pf(new SimpleReduce_ISPC_PF{A=nR,Result=p0},N,W,H));

            // 4
            T("ComplexFlow", I,
                ()=>Ij(new ComplexFlow_CSharp_Job{A=nA,B=nB,Result=p0,Threshold=50,Count=N},W,I),
                ()=>Fo(new ComplexFlow_CSharp_For{A=nA,B=nB,Result=p1,Threshold=50},N,W,I),
                ()=>Pf(new ComplexFlow_CSharp_PF{A=nA,B=nB,Result=p2,Threshold=50},N,W,I),
                ()=>Ij(new ComplexFlow_Cpp_Job{A=nA,B=nB,Result=p0,Threshold=50,Count=N},W,I),
                ()=>Fo(new ComplexFlow_Cpp_For{A=nA,B=nB,Result=p1,Threshold=50},N,W,I),
                ()=>Pf(new ComplexFlow_Cpp_PF{A=nA,B=nB,Result=p2,Threshold=50},N,W,I),
                ()=>Ij(new ComplexFlow_SIMD_Job{A=nA,B=nB,Result=p3,Threshold=50,Count=N},W,I),
                ()=>Fo(new ComplexFlow_SIMD_For{A=nA,B=nB,Result=p4,Threshold=50},N,W,I),
                ()=>Pf(new ComplexFlow_SIMD_PF{A=nA,B=nB,Result=p5,Threshold=50},N,W,I),
                ()=>Pf(new ComplexFlow_ISPC_PF{A=nA,B=nB,Result=p0,Threshold=50},N,W,I));

            // 5
            T("GatherReduce", H,
                ()=>Ij(new GatherReduce_CSharp_Job{QueryX=nQx,QueryY=nQy,DataX=nDx,DataY=nDy,Index=nIx,Result=p0,Count=N},W,H),
                ()=>Fo(new GatherReduce_CSharp_For{QueryX=nQx,QueryY=nQy,DataX=nDx,DataY=nDy,Index=nIx,Result=p1},N,W,H),
                ()=>Pf(new GatherReduce_CSharp_PF{QueryX=nQx,QueryY=nQy,DataX=nDx,DataY=nDy,Index=nIx,Result=p2},N,W,H),
                ()=>Ij(new GatherReduce_Cpp_Job{QueryX=nQx,QueryY=nQy,DataX=nDx,DataY=nDy,Index=nIx,Result=p0,Count=N},W,H),
                ()=>Fo(new GatherReduce_Cpp_For{QueryX=nQx,QueryY=nQy,DataX=nDx,DataY=nDy,Index=nIx,Result=p1},N,W,H),
                ()=>Pf(new GatherReduce_Cpp_PF{QueryX=nQx,QueryY=nQy,DataX=nDx,DataY=nDy,Index=nIx,Result=p2},N,W,H),
                ()=>Ij(new GatherReduce_SIMD_Job{QueryX=nQx,QueryY=nQy,DataX=nDx,DataY=nDy,Index=nIx,Result=p3,Count=N},W,H),
                ()=>Fo(new GatherReduce_SIMD_For{QueryX=nQx,QueryY=nQy,DataX=nDx,DataY=nDy,Index=nIx,Result=p4},N,W,H),
                ()=>Pf(new GatherReduce_SIMD_PF{QueryX=nQx,QueryY=nQy,DataX=nDx,DataY=nDy,Index=nIx,Result=p5},N,W,H),
                ()=>Pf(new GatherReduce_ISPC_PF{QueryX=nQx,QueryY=nQy,DataX=nDx,DataY=nDy,Index=nIx,Result=p0},N,W,H));

            Console.WriteLine("-".PadRight(105, '-'));
            Console.WriteLine("  Job=Execute | For=Schedule | PF=Schedule(64) | ISPC only PF");
            Console.WriteLine();
        }

        static NativeArray<float> NA(float[] d) => new NativeArray<float>(d, Allocator.Persistent);
        static NativeArray<float> NA(int n) => new NativeArray<float>(n, Allocator.Persistent);
        static NativeArray<int> NI(int[] d) => new NativeArray<int>(d, Allocator.Persistent);
        static float[] GF(int n, Random r) { var d = new float[n]; for (int i=0;i<n;i++) d[i]=(float)(r.NextDouble()*200-100); return d; }

        static double Pf<T>(T j, int n, int w, int t) where T : struct, IJobParallelFor
        { for (int i=0;i<w;i++) j.Schedule(n,64).Complete(); var s=Stopwatch.StartNew(); for (int i=0;i<t;i++){j.Schedule(n,64).Complete();} s.Stop(); return s.Elapsed.TotalMilliseconds/t; }

        static double Fo<T>(T j, int n, int w, int t) where T : struct, IJobFor
        { for (int i=0;i<w;i++) j.Schedule(n).Complete(); var s=Stopwatch.StartNew(); for (int i=0;i<t;i++){j.Schedule(n).Complete();} s.Stop(); return s.Elapsed.TotalMilliseconds/t; }

        static double Ij<T>(T j, int w, int t) where T : struct, IJob
        { for (int i=0;i<w;i++) j.Execute(); var s=Stopwatch.StartNew(); for (int i=0;i<t;i++) j.Execute(); s.Stop(); return s.Elapsed.TotalMilliseconds/t; }
    }
}
