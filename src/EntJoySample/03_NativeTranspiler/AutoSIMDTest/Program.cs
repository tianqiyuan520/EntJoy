// Auto-SIMD — Static | IJob | IJobFor | IJobPF  x  C# | C++ | SIMD | ISPC = 16 variants
using EntJoy;
using EntJoy.Collections;
using EntJoy.Mathematics;
using EntJoySample.AutoSIMDTest;
using NativeTranspiler.Bindings;
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
            // Output buffers: 16 variants reusing 6 buffers (sequential T() calls, no conflict)
            using var p0 = NA(N); using var p1 = NA(N); using var p2 = NA(N);
            using var p3 = NA(N); using var p4 = NA(N); using var p5 = NA(N);

            const int W9 = 9;
            int cols = 16;
            int totalW = 10 + cols * (W9 + 1); // case(10) + 16*10 = 170
            var sep = "".PadRight(totalW, '-');
            var eq = "".PadRight(totalW, '=');

            Console.WriteLine(eq);
            Console.WriteLine($"  {"",-9} Static─         IJob──          IJobFor─         IJobPF──");
            Console.WriteLine($"  {"Case",-9} C#     C++    SIMD   ISPC   C#     C++    SIMD   ISPC   C#     C++    SIMD   ISPC   C#     C++    SIMD   ISPC");
            Console.WriteLine($"  {"────",-9} ───── ───── ───── ───── ───── ───── ───── ───── ───── ───── ───── ───── ───── ───── ───── ─────");

            // Static helper
            static double St(System.Action fn, int w, int t)
            { for (int i = 0; i < w; i++) fn(); var s = Stopwatch.StartNew(); for (int i = 0; i < t; i++) fn(); s.Stop(); return s.Elapsed.TotalMilliseconds / t; }

            // (StC=StaticC#, StCpp=StaticC++, StSD=StaticSIMD, StIP=StaticISPC,
            //  JbC=IJobC#, JbCpp=IJobC++, JbSD=IJobSIMD, JbIP=IJobISPC,
            //  FrC=IJobForC#, FrCpp=IJobForC++, FrSD=IJobForSIMD, FrIP=IJobForISPC,
            //  PfC=IJobPFC#, PfCpp=IJobPFC++, PfSD=IJobPFSIMD, PfIP=IJobPFISPC)
            void T(string name, int it,
                System.Func<double> StC, System.Func<double> StCpp, System.Func<double> StSD, System.Func<double> StIP,
                System.Func<double> JbC, System.Func<double> JbCpp, System.Func<double> JbSD, System.Func<double> JbIP,
                System.Func<double> FrC, System.Func<double> FrCpp, System.Func<double> FrSD, System.Func<double> FrIP,
                System.Func<double> PfC, System.Func<double> PfCpp, System.Func<double> PfSD, System.Func<double> PfIP)
            {
                Console.Write($"  {name,-9}");
                foreach (var fn in new[] { StC, StCpp, StSD, StIP, JbC, JbCpp, JbSD, JbIP, FrC, FrCpp, FrSD, FrIP, PfC, PfCpp, PfSD, PfIP })
                    Console.Write($" {fn(),W9:F3}");
                Console.WriteLine();
            }

            // ═══════════ Case 1: SimpleArith ═══════════
            T("SimpleArith", I,
                // Static
                () => St(() => SimpleArith_StaticFuncs.SimpleArith_Stc_CSharp(nA, nB, nC, p0, N), W, I),
                () => St(() => NativeExports.SimpleArith_Stc_Cpp(nA, nB, nC, p0, N), W, I),
                () => St(() => NativeExports.SimpleArith_Stc_SIMD(nA, nB, nC, p0, N), W, I),
                () => St(() => NativeExports.SimpleArith_Stc_ISPC(nA, nB, nC, p0, N), W, I),
                // IJob
                () => Ij(new SimpleArith_CSharp_Job { A = nA, B = nB, C = nC, Result = p0, Count = N }, W, I),
                () => Ij(new SimpleArith_Cpp_Job { A = nA, B = nB, C = nC, Result = p0, Count = N }, W, I),
                () => Ij(new SimpleArith_SIMD_Job { A = nA, B = nB, C = nC, Result = p3, Count = N }, W, I),
                () => Ij(new SimpleArith_ISPC_Job { A = nA, B = nB, C = nC, Result = p0, Count = N }, W, I),
                // IJobFor
                () => Fo(new SimpleArith_CSharp_For { A = nA, B = nB, C = nC, Result = p1 }, N, W, I),
                () => Fo(new SimpleArith_Cpp_For { A = nA, B = nB, C = nC, Result = p1 }, N, W, I),
                () => Fo(new SimpleArith_SIMD_For { A = nA, B = nB, C = nC, Result = p4 }, N, W, I),
                () => Fo(new SimpleArith_ISPC_For { A = nA, B = nB, C = nC, Result = p0 }, N, W, I),
                // IJobPF
                () => Pf(new SimpleArith_CSharp_PF { A = nA, B = nB, C = nC, Result = p2 }, N, W, I),
                () => Pf(new SimpleArith_Cpp_PF { A = nA, B = nB, C = nC, Result = p2 }, N, W, I),
                () => Pf(new SimpleArith_SIMD_PF { A = nA, B = nB, C = nC, Result = p5 }, N, W, I),
                () => Pf(new SimpleArith_ISPC_PF { A = nA, B = nB, C = nC, Result = p0 }, N, W, I));

            // ═══════════ Case 2: MathFuncs ═══════════
            T("MathFuncs", I,
                // Static
                () => St(() => MathFuncs_StaticFuncs.MathFuncs_Stc_CSharp(nA, p0, N), W, I),
                () => St(() => NativeExports.MathFuncs_Stc_Cpp(nA, p0, N), W, I),
                () => St(() => NativeExports.MathFuncs_Stc_SIMD(nA, p0, N), W, I),
                () => St(() => NativeExports.MathFuncs_Stc_ISPC(nA, p0, N), W, I),
                // IJob
                () => Ij(new MathFuncs_CSharp_Job { A = nA, Result = p0, Count = N }, W, I),
                () => Ij(new MathFuncs_Cpp_Job { A = nA, Result = p0, Count = N }, W, I),
                () => Ij(new MathFuncs_SIMD_Job { A = nA, Result = p3, Count = N }, W, I),
                () => Ij(new MathFuncs_ISPC_Job { A = nA, Result = p0, Count = N }, W, I),
                // IJobFor
                () => Fo(new MathFuncs_CSharp_For { A = nA, Result = p1 }, N, W, I),
                () => Fo(new MathFuncs_Cpp_For { A = nA, Result = p1 }, N, W, I),
                () => Fo(new MathFuncs_SIMD_For { A = nA, Result = p4 }, N, W, I),
                () => Fo(new MathFuncs_ISPC_For { A = nA, Result = p0 }, N, W, I),
                // IJobPF
                () => Pf(new MathFuncs_CSharp_PF { A = nA, Result = p2 }, N, W, I),
                () => Pf(new MathFuncs_Cpp_PF { A = nA, Result = p2 }, N, W, I),
                () => Pf(new MathFuncs_SIMD_PF { A = nA, Result = p5 }, N, W, I),
                () => Pf(new MathFuncs_ISPC_PF { A = nA, Result = p0 }, N, W, I));

            // ═══════════ Case 3: Reduce ═══════════
            T("Reduce", H,
                // Static
                () => St(() => SimpleReduce_StaticFuncs.SimpleReduce_Stc_CSharp(nR, p0, N), W, H),
                () => St(() => NativeExports.SimpleReduce_Stc_Cpp(nR, p0, N), W, H),
                () => St(() => NativeExports.SimpleReduce_Stc_SIMD(nR, p0, N), W, H),
                () => St(() => NativeExports.SimpleReduce_Stc_ISPC(nR, p0, N), W, H),
                // IJob
                () => Ij(new SimpleReduce_CSharp_Job { A = nR, Result = p0, Count = N }, W, H),
                () => Ij(new SimpleReduce_Cpp_Job { A = nR, Result = p0, Count = N }, W, H),
                () => Ij(new SimpleReduce_SIMD_Job { A = nR, Result = p3, Count = N }, W, H),
                () => Ij(new SimpleReduce_ISPC_Job { A = nR, Result = p0, Count = N }, W, H),
                // IJobFor
                () => Fo(new SimpleReduce_CSharp_For { A = nR, Result = p1 }, N, W, H),
                () => Fo(new SimpleReduce_Cpp_For { A = nR, Result = p1 }, N, W, H),
                () => Fo(new SimpleReduce_SIMD_For { A = nR, Result = p4 }, N, W, H),
                () => Fo(new SimpleReduce_ISPC_For { A = nR, Result = p0 }, N, W, H),
                // IJobPF
                () => Pf(new SimpleReduce_CSharp_PF { A = nR, Result = p2 }, N, W, H),
                () => Pf(new SimpleReduce_Cpp_PF { A = nR, Result = p2 }, N, W, H),
                () => Pf(new SimpleReduce_SIMD_PF { A = nR, Result = p5 }, N, W, H),
                () => Pf(new SimpleReduce_ISPC_PF { A = nR, Result = p0 }, N, W, H));

            // ═══════════ Case 4: ComplexFlow ═══════════
            T("ComplexFlow", I,
                // Static
                () => St(() => ComplexFlow_StaticFuncs.ComplexFlow_Stc_CSharp(nA, nB, p0, 50, N), W, I),
                () => St(() => NativeExports.ComplexFlow_Stc_Cpp(nA, nB, p0, 50, N), W, I),
                () => St(() => NativeExports.ComplexFlow_Stc_SIMD(nA, nB, p0, 50, N), W, I),
                () => St(() => NativeExports.ComplexFlow_Stc_ISPC(nA, nB, p0, 50, N), W, I),
                // IJob
                () => Ij(new ComplexFlow_CSharp_Job { A = nA, B = nB, Result = p0, Threshold = 50, Count = N }, W, I),
                () => Ij(new ComplexFlow_Cpp_Job { A = nA, B = nB, Result = p0, Threshold = 50, Count = N }, W, I),
                () => Ij(new ComplexFlow_SIMD_Job { A = nA, B = nB, Result = p3, Threshold = 50, Count = N }, W, I),
                () => Ij(new ComplexFlow_ISPC_Job { A = nA, B = nB, Result = p0, Threshold = 50, Count = N }, W, I),
                // IJobFor
                () => Fo(new ComplexFlow_CSharp_For { A = nA, B = nB, Result = p1, Threshold = 50 }, N, W, I),
                () => Fo(new ComplexFlow_Cpp_For { A = nA, B = nB, Result = p1, Threshold = 50 }, N, W, I),
                () => Fo(new ComplexFlow_SIMD_For { A = nA, B = nB, Result = p4, Threshold = 50 }, N, W, I),
                () => Fo(new ComplexFlow_ISPC_For { A = nA, B = nB, Result = p0, Threshold = 50 }, N, W, I),
                // IJobPF
                () => Pf(new ComplexFlow_CSharp_PF { A = nA, B = nB, Result = p2, Threshold = 50 }, N, W, I),
                () => Pf(new ComplexFlow_Cpp_PF { A = nA, B = nB, Result = p2, Threshold = 50 }, N, W, I),
                () => Pf(new ComplexFlow_SIMD_PF { A = nA, B = nB, Result = p5, Threshold = 50 }, N, W, I),
                () => Pf(new ComplexFlow_ISPC_PF { A = nA, B = nB, Result = p0, Threshold = 50 }, N, W, I));

            // ═══════════ Case 5: GatherReduce ═══════════
            T("GatherReduce", H,
                // Static
                () => St(() => GatherReduce_StaticFuncs.GatherReduce_Stc_CSharp(nQx, nQy, nDx, nDy, nIx, p0, N), W, H),
                () => St(() => NativeExports.GatherReduce_Stc_Cpp(nQx, nQy, nDx, nDy, nIx, p0, N), W, H),
                () => St(() => NativeExports.GatherReduce_Stc_SIMD(nQx, nQy, nDx, nDy, nIx, p0, N), W, H),
                () => St(() => NativeExports.GatherReduce_Stc_ISPC(nQx, nQy, nDx, nDy, nIx, p0, N), W, H),
                // IJob
                () => Ij(new GatherReduce_CSharp_Job { QueryX = nQx, QueryY = nQy, DataX = nDx, DataY = nDy, Index = nIx, Result = p0, Count = N }, W, H),
                () => Ij(new GatherReduce_Cpp_Job { QueryX = nQx, QueryY = nQy, DataX = nDx, DataY = nDy, Index = nIx, Result = p0, Count = N }, W, H),
                () => Ij(new GatherReduce_SIMD_Job { QueryX = nQx, QueryY = nQy, DataX = nDx, DataY = nDy, Index = nIx, Result = p3, Count = N }, W, H),
                () => Ij(new GatherReduce_ISPC_Job { QueryX = nQx, QueryY = nQy, DataX = nDx, DataY = nDy, Index = nIx, Result = p0, Count = N }, W, H),
                // IJobFor
                () => Fo(new GatherReduce_CSharp_For { QueryX = nQx, QueryY = nQy, DataX = nDx, DataY = nDy, Index = nIx, Result = p1 }, N, W, H),
                () => Fo(new GatherReduce_Cpp_For { QueryX = nQx, QueryY = nQy, DataX = nDx, DataY = nDy, Index = nIx, Result = p1 }, N, W, H),
                () => Fo(new GatherReduce_SIMD_For { QueryX = nQx, QueryY = nQy, DataX = nDx, DataY = nDy, Index = nIx, Result = p4 }, N, W, H),
                () => Fo(new GatherReduce_ISPC_For { QueryX = nQx, QueryY = nQy, DataX = nDx, DataY = nDy, Index = nIx, Result = p0 }, N, W, H),
                // IJobPF
                () => Pf(new GatherReduce_CSharp_PF { QueryX = nQx, QueryY = nQy, DataX = nDx, DataY = nDy, Index = nIx, Result = p2 }, N, W, H),
                () => Pf(new GatherReduce_Cpp_PF { QueryX = nQx, QueryY = nQy, DataX = nDx, DataY = nDy, Index = nIx, Result = p2 }, N, W, H),
                () => Pf(new GatherReduce_SIMD_PF { QueryX = nQx, QueryY = nQy, DataX = nDx, DataY = nDy, Index = nIx, Result = p5 }, N, W, H),
                () => Pf(new GatherReduce_ISPC_PF { QueryX = nQx, QueryY = nQy, DataX = nDx, DataY = nDy, Index = nIx, Result = p0 }, N, W, H));

            Console.WriteLine(sep);
            Console.WriteLine("  Stc=Static(direct call) | Job=Execute | For=Schedule | PF=Schedule(64)");
            Console.WriteLine();

            // ─────────────────────────────────────────────────────────────────────
            // IJobChunk / IJobEntity benchmarks (ECS World-based)
            // ─────────────────────────────────────────────────────────────────────
            const int ChunkN = 100000;
            var query = new QueryBuilder().WithAll<MovePosition, MoveVelocity>();
            float dt = 1.0f / 60.0f;

            // Light: MoveJob
            using var lightWorld = new World("Light");
            for (int i = 0; i < ChunkN; i++)
            {
                var e = lightWorld.EntityManager.NewEntity(typeof(MovePosition), typeof(MoveVelocity));
                lightWorld.EntityManager.Set(e, new MovePosition { Value = new float2(i % 1920, i % 1080) });
                lightWorld.EntityManager.Set(e, new MoveVelocity { Value = new float2(((i * 17) % 201 - 100) * 0.25f, ((i * 31) % 201 - 100) * 0.25f) });
            }

            // Heavy: HeavyJob
            using var heavyWorld = new World("Heavy");
            for (int i = 0; i < ChunkN; i++)
            {
                var e = heavyWorld.EntityManager.NewEntity(typeof(MovePosition), typeof(MoveVelocity));
                heavyWorld.EntityManager.Set(e, new MovePosition { Value = new float2(i % 1920, i % 1080) });
                heavyWorld.EntityManager.Set(e, new MoveVelocity { Value = new float2(((i * 17) % 201 - 100) * 0.25f, ((i * 31) % 201 - 100) * 0.25f) });
            }

            int chunkW = 3, chunkI = 30;
            double ChRun(System.Action a) { for (int i = 0; i < chunkW; i++) a(); var s = Stopwatch.StartNew(); for (int i = 0; i < chunkI; i++) a(); s.Stop(); return s.Elapsed.TotalMilliseconds / chunkI; }

            Console.WriteLine();
            Console.WriteLine(eq);
            Console.WriteLine($"  {"",-9} IJobChunk──      IJobEntity─");
            Console.WriteLine($"  {"Case",-9} C++    SIMD     C++    SIMD ");
            Console.WriteLine($"  {"────",-9} ───── ─────   ───── ───── ");

            double LightChunkCpp = ChRun(() => { World.DefaultWorld = lightWorld; new MoveJobChunk_Cpp { DeltaTime = dt }.Schedule(query).Complete(); });
            double LightChunkSIMD = ChRun(() => { World.DefaultWorld = lightWorld; new MoveJobChunk_SIMD { DeltaTime = dt }.Schedule(query).Complete(); });
            double LightEntCpp = ChRun(() => { World.DefaultWorld = lightWorld; new MoveJobEntity_Cpp { DeltaTime = dt }.Schedule(query).Complete(); });
            double LightEntSIMD = ChRun(() => { World.DefaultWorld = lightWorld; new MoveJobEntity_SIMD { DeltaTime = dt }.Schedule(query).Complete(); });
            double HeavyChunkCpp = ChRun(() => { World.DefaultWorld = heavyWorld; new HeavyJobChunk_Cpp { DeltaTime = dt }.Schedule(query).Complete(); });
            double HeavyChunkSIMD = ChRun(() => { World.DefaultWorld = heavyWorld; new HeavyJobChunk_SIMD { DeltaTime = dt }.Schedule(query).Complete(); });
            double HeavyEntCpp = ChRun(() => { World.DefaultWorld = heavyWorld; new HeavyJobEntity_Cpp { DeltaTime = dt }.Schedule(query).Complete(); });
            double HeavyEntSIMD = ChRun(() => { World.DefaultWorld = heavyWorld; new HeavyJobEntity_SIMD { DeltaTime = dt }.Schedule(query).Complete(); });

            Console.WriteLine($"  {"LightMove",-9} {LightChunkCpp,5:F3} {LightChunkSIMD,5:F3}   {LightEntCpp,5:F3} {LightEntSIMD,5:F3}");
            Console.WriteLine($"  {"HeavyMove",-9} {HeavyChunkCpp,5:F3} {HeavyChunkSIMD,5:F3}   {HeavyEntCpp,5:F3} {HeavyEntSIMD,5:F3}");
            Console.WriteLine(sep);
            Console.WriteLine("  Schedule().Complete() via ECS World");
            Console.WriteLine();
        }

        static NativeArray<float> NA(float[] d) => new NativeArray<float>(d, Allocator.Persistent);
        static NativeArray<float> NA(int n) => new NativeArray<float>(n, Allocator.Persistent);
        static NativeArray<int> NI(int[] d) => new NativeArray<int>(d, Allocator.Persistent);
        static float[] GF(int n, Random r) { var d = new float[n]; for (int i = 0; i < n; i++) d[i] = (float)(r.NextDouble() * 200 - 100); return d; }

        static double Pf<T>(T j, int n, int w, int t) where T : struct, IJobParallelFor
        { for (int i = 0; i < w; i++) j.Schedule(n, 64).Complete(); var s = Stopwatch.StartNew(); for (int i = 0; i < t; i++) { j.Schedule(n, 64).Complete(); } s.Stop(); return s.Elapsed.TotalMilliseconds / t; }

        static double Fo<T>(T j, int n, int w, int t) where T : struct, IJobFor
        { for (int i = 0; i < w; i++) j.Schedule(n).Complete(); var s = Stopwatch.StartNew(); for (int i = 0; i < t; i++) { j.Schedule(n).Complete(); } s.Stop(); return s.Elapsed.TotalMilliseconds / t; }

        static double Ij<T>(T j, int w, int t) where T : struct, IJob
        { for (int i = 0; i < w; i++) j.Execute(); var s = Stopwatch.StartNew(); for (int i = 0; i < t; i++) j.Execute(); s.Stop(); return s.Elapsed.TotalMilliseconds / t; }
    }
}
