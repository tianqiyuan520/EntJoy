//using System;
using System.Diagnostics;
using System.Threading.Tasks;
using EntJoy;
using EntJoy.Collections;
using EntJoy.Mathematics;
using EntJoySample.IJobChunkMoveCompareTest;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.Cuda;

namespace EntJoySample.GpuIlgpu
{
    /// <summary>
    /// ILGPU Gate 1a 基准：在同一负载（HeavyMove / LightMove）上对比
    /// C# 单线程、C# 多线程(Parallel.For)、C++(JobSystem)、ISPC(JobSystem)、ISPC MT、GPU。
    ///
    /// GPU 档除整体墙钟外，额外用 CUDA 事件（ProfilingMarker）单独拆出三项：
    ///   纯内核计算耗时 / 上传耗时 / 回读耗时。
    ///
    /// 侧重点（见 docs/10-GPU-Offload探索分析.md §11 Gate 1a）：
    ///   · 常驻（resident）= 纯计算上限（数据常驻 GPU，无每帧传输）
    ///   · 全量往返（roundtrip）= 真实传输税（每帧 upload + kernel + download）
    ///
    /// 常量全部写死（无 env 依赖）。
    /// </summary>
    public sealed class GpuIlgpuBenchmark : IDisposable
    {
        // ---- 基准参数（写死，不读环境变量） ----
        private const int Entities = 1_000_000;
        private const int Warmup = 5;
        private const int Measure = 100;
        private const int HeavyIterations = 16;    // 与 IJobChunkMoveCompareTest 的 Heavy 一致
        private const float DeltaTime = 1.0f / 60.0f;

        private readonly float2[] _initPos;
        private readonly float2[] _initVel;
        private readonly float2[] _csSingle;
        private readonly float2[] _csParallel;

        private World _cppWorld = null!;
        private World _ispcWorld = null!;
        private World _ispcMtWorld = null!;
        private readonly QueryBuilder _query = new QueryBuilder().WithAll<MovePosition, MoveVelocity>();

        // HeavyMove 汇总（p50 ms）
        private double _csSingleHeavyMs, _csParallelHeavyMs, _cppHeavyMs, _ispcHeavyMs, _ispcMtHeavyMs;

        // LightMove 汇总（p50 ms）
        private double _csSingleLightMs, _csParallelLightMs, _cppLightMs, _ispcLightMs, _ispcMtLightMs;

        // 统一最终对比（10_GPU_FinalCompare）读取
        private GpuBackend? _backend;

        public double HeavyCsSingleMs => _csSingleHeavyMs;
        public double HeavyCsParallelMs => _csParallelHeavyMs;
        public double HeavyCppMs => _cppHeavyMs;
        public double HeavyIspcMs => _ispcHeavyMs;
        public double LightCsSingleMs => _csSingleLightMs;
        public double LightCsParallelMs => _csParallelLightMs;
        public double LightCppMs => _cppLightMs;
        public double LightIspcMs => _ispcLightMs;
        public double HeavyGpuResidentMs => _backend?.ResidentHeavyWallMs ?? 0;
        public double HeavyGpuRoundtripMs => _backend?.RoundtripHeavyWallMs ?? 0;
        public double HeavyGpuPlMs => _backend?.RoundtripPlHeavyWallMs ?? 0;
        public double LightGpuResidentMs => _backend?.LightResidentWallMs ?? 0;
        public double LightGpuRoundtripMs => _backend?.LightRoundtripWallMs ?? 0;
        public double LightGpuPlMs => _backend?.LightRoundtripPlWallMs ?? 0;

        private readonly bool _verbose;
        public GpuIlgpuBenchmark(bool verbose = true)
        {
            _verbose = verbose;
            _initPos = new float2[Entities];
            _initVel = new float2[Entities];
            var rnd = new Random(1234);
            for (int i = 0; i < Entities; i++)
            {
                _initPos[i] = new float2((float)(rnd.NextDouble() * 200 - 100), (float)(rnd.NextDouble() * 200 - 100));
                _initVel[i] = new float2((float)(rnd.NextDouble() * 200 - 100), (float)(rnd.NextDouble() * 200 - 100));
            }
            _csSingle = (float2[])_initPos.Clone();
            _csParallel = (float2[])_initPos.Clone();
        }

        public void Run()
        {
            if (_verbose)
            {
                Console.WriteLine("=== ILGPU GPU Offload 对比（Gate 1a, docs/10 探索分析） ===");
                Console.WriteLine($"entities={Entities:N0}, warmup={Warmup}, measure={Measure}, " +
                    $"heavyIters={HeavyIterations}, dt={DeltaTime:F6}");
            }

            using (var gpu = InitGpuSafely())
            {
                _backend = gpu;
                SetupEcsWorlds();

                try
                {
                    if (gpu != null)
                    {
                        gpu.PrimeJit(_initPos, _initVel);
                    }

                    RunLoad("HeavyMove", HeavyIterations, gpu);
                    RunLoad("LightMove", 0, gpu);
                    if (_verbose)
                    {
                        PrintSummaryMatrix(gpu);
                        if (gpu != null)
                        {
                            gpu.RunTransferProbe(Entities);
                            gpu.RunDualStreamProbe(Entities);
                        }
                    }
                }
                finally
                {
                    foreach (var w in new[] { _cppWorld, _ispcWorld, _ispcMtWorld }) w?.Dispose();
                }
            }
        }

        private GpuBackend? InitGpuSafely()
        {
            try
            {
                var gpu = new GpuBackend(Entities, _verbose);
                gpu.Init();
                return gpu;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GPU] ILGPU 初始化失败，跳过 GPU 档（CPU 档继续）。{ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private void RunLoad(string label, int iterations, GpuBackend? gpu)
        {
            if (_verbose)
            {
                Console.WriteLine();
                Console.WriteLine($"----- {label} (@{Entities:N0}) -----");
            }

            double csSingle = MeasureCpu($"{label}: C# 单线程", () => CsSingleExecute(iterations));
            double csParallel = MeasureCpu($"{label}: C# 多线程(Parallel.For)", () => CsParallelExecute(iterations));
            double cpp = MeasureEcs($"{label}: C++(JobSystem)", MakeCppJob(iterations), _cppWorld);
            double ispc = MeasureEcs($"{label}: ISPC(JobSystem)", MakeIspcJob(iterations, false), _ispcWorld);
            double ispcMt = MeasureEcs($"{label}: ISPC MT(JobSystem)", MakeIspcJob(iterations, true), _ispcMtWorld);

            if (iterations > 0)
            {
                _csSingleHeavyMs = csSingle;
                _csParallelHeavyMs = csParallel;
                _cppHeavyMs = cpp;
                _ispcHeavyMs = ispc;
                _ispcMtHeavyMs = ispcMt;
            }
            else
            {
                _csSingleLightMs = csSingle;
                _csParallelLightMs = csParallel;
                _cppLightMs = cpp;
                _ispcLightMs = ispc;
                _ispcMtLightMs = ispcMt;
            }

            if (gpu != null)
            {
                gpu.Measure(resident: true, iterations, label);
                gpu.Measure(resident: false, iterations, label);
                gpu.MeasureRoundtripPageLocked(iterations, label);
            }
        }

        // ---------------- CPU 各档 ----------------

        private double MeasureCpu(string label, Action step)
        {
            for (int i = 0; i < Warmup; i++) step();
            var samples = new double[Measure];
            for (int i = 0; i < Measure; i++)
            {
                long start = Stopwatch.GetTimestamp();
                step();
                long end = Stopwatch.GetTimestamp();
                samples[i] = (end - start) * 1000.0 / Stopwatch.Frequency;
            }
            if (_verbose) Print(label, samples, gpu: false);
            return Median(samples);
        }

        private void CsSingleExecute(int iterations)
        {
            var pos = _csSingle;
            var vel = _initVel;
            float dt = DeltaTime;
            int n = Entities;
            if (iterations > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    float px = pos[i].x, py = pos[i].y;
                    float vx = vel[i].x, vy = vel[i].y;
                    float accX = px * 0.001f + vx * 0.01f;
                    float accY = py * 0.001f + vy * 0.01f;
                    for (int it = 0; it < iterations; it++)
                    {
                        float phaseX = accX + it * 0.03125f;
                        float phaseY = accY - it * 0.0625f;
                        float wave = MathF.Sin(phaseX) + MathF.Cos(phaseY);
                        float radius = MathF.Sqrt(accX * accX + accY * accY + 1.0f);
                        accX = accX * 0.985f + wave * 0.015f + radius * 0.0002f + vx * 0.0001f;
                        accY = accY * 0.982f - wave * 0.012f + radius * 0.0003f + vy * 0.0001f;
                    }
                    pos[i] = new float2(px + vx * dt + accX * 0.001f, py + vy * dt + accY * 0.001f);
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    pos[i] = new float2(pos[i].x + vel[i].x * dt, pos[i].y + vel[i].y * dt);
                }
            }
        }

        private void CsParallelExecute(int iterations)
        {
            var pos = _csParallel;
            var vel = _initVel;
            float dt = DeltaTime;
            int n = Entities;
            if (iterations > 0)
            {
                Parallel.For(0, n, i =>
                {
                    float px = pos[i].x, py = pos[i].y;
                    float vx = vel[i].x, vy = vel[i].y;
                    float accX = px * 0.001f + vx * 0.01f;
                    float accY = py * 0.001f + vy * 0.01f;
                    for (int it = 0; it < iterations; it++)
                    {
                        float phaseX = accX + it * 0.03125f;
                        float phaseY = accY - it * 0.0625f;
                        float wave = MathF.Sin(phaseX) + MathF.Cos(phaseY);
                        float radius = MathF.Sqrt(accX * accX + accY * accY + 1.0f);
                        accX = accX * 0.985f + wave * 0.015f + radius * 0.0002f + vx * 0.0001f;
                        accY = accY * 0.982f - wave * 0.012f + radius * 0.0003f + vy * 0.0001f;
                    }
                    pos[i] = new float2(px + vx * dt + accX * 0.001f, py + vy * dt + accY * 0.001f);
                });
            }
            else
            {
                Parallel.For(0, n, i =>
                {
                    pos[i] = new float2(pos[i].x + vel[i].x * dt, pos[i].y + vel[i].y * dt);
                });
            }
        }

        // ---------------- ECS（C++/ISPC）各档 ----------------

        private void SetupEcsWorlds()
        {
            _cppWorld = CreateEcsWorld("GpuCompare_Cpp");
            _ispcWorld = CreateEcsWorld("GpuCompare_Ispc");
            _ispcMtWorld = CreateEcsWorld("GpuCompare_IspcMt");
        }

        private World CreateEcsWorld(string name)
        {
            var world = new World(name);
            var em = world.EntityManager;
            for (int i = 0; i < Entities; i++)
            {
                var e = em.NewEntity(typeof(MovePosition), typeof(MoveVelocity));
                em.Set(e, new MovePosition { Value = _initPos[i] });
                em.Set(e, new MoveVelocity { Value = _initVel[i] });
            }
            return world;
        }

        private delegate void EcsJobRun();

        private EcsJobRun MakeCppJob(int iterations)
        {
            if (iterations > 0)
            {
                var job = new HeavyJobChunkCpp { DeltaTime = DeltaTime };
                return () => { World.DefaultWorld = _cppWorld; job.Schedule(_query).Complete(); };
            }
            var light = new MoveJobChunkCpp { DeltaTime = DeltaTime };
            return () => { World.DefaultWorld = _cppWorld; light.Schedule(_query).Complete(); };
        }

        private EcsJobRun MakeIspcJob(int iterations, bool isMt)
        {
            if (isMt)
            {
                if (iterations > 0)
                {
                    var job = new HeavyJobEntityIspcMt { DeltaTime = DeltaTime };
                    return () => { World.DefaultWorld = _ispcMtWorld; job.Schedule(_query).Complete(); };
                }
                var light = new MoveJobEntityIspcMt { DeltaTime = DeltaTime };
                return () => { World.DefaultWorld = _ispcMtWorld; light.Schedule(_query).Complete(); };
            }

            if (iterations > 0)
            {
                var job = new HeavyJobEntityIspc { DeltaTime = DeltaTime };
                return () => { World.DefaultWorld = _ispcWorld; job.Schedule(_query).Complete(); };
            }
            var lightIspc = new MoveJobEntityIspc { DeltaTime = DeltaTime };
            return () => { World.DefaultWorld = _ispcWorld; lightIspc.Schedule(_query).Complete(); };
        }

        private double MeasureEcs(string label, EcsJobRun step, World worldRef)
        {
            for (int i = 0; i < Warmup; i++) step();
            var samples = new double[Measure];
            for (int i = 0; i < Measure; i++)
            {
                long start = Stopwatch.GetTimestamp();
                step();
                long end = Stopwatch.GetTimestamp();
                samples[i] = (end - start) * 1000.0 / Stopwatch.Frequency;
            }
            if (_verbose) Print(label, samples, gpu: false);
            return Median(samples);
        }

        // ---------------- 输出 ----------------

        private void PrintSummaryMatrix(GpuBackend? gpu)
        {
            Console.WriteLine();
            Console.WriteLine("===================== HeavyMove 汇总（p50, ms） =====================");
            Console.WriteLine($"  C# 单线程          : {_csSingleHeavyMs,10:F3}");
            Console.WriteLine($"  C# 多线程          : {_csParallelHeavyMs,10:F3}   (单线程 x{_csSingleHeavyMs / _csParallelHeavyMs:F2})");
            Console.WriteLine($"  C++ (JobSystem)    : {_cppHeavyMs,10:F3}   (单线程 x{_csSingleHeavyMs / _cppHeavyMs:F2})");
            Console.WriteLine($"  ISPC (JobSystem)   : {_ispcHeavyMs,10:F3}   (单线程 x{_csSingleHeavyMs / _ispcHeavyMs:F2})");
            Console.WriteLine($"  ISPC MT (JobSystem): {_ispcMtHeavyMs,10:F3}   (单线程 x{_csSingleHeavyMs / _ispcMtHeavyMs:F2})");
            if (gpu != null)
            {
                Console.WriteLine($"  GPU 常驻(p50墙钟)  : {gpu.ResidentHeavyWallMs,10:F3}   (vs ISPC x{_ispcHeavyMs / gpu.ResidentHeavyWallMs:F2})");
                Console.WriteLine($"    纯内核(GPU事件) : {gpu.ResidentHeavyKernelMs,10:F3}   (vs ISPC x{_ispcHeavyMs / gpu.ResidentHeavyKernelMs:F2})");
                Console.WriteLine($"  GPU 往返(p50墙钟)  : {gpu.RoundtripHeavyWallMs,10:F3}   (相对常驻 x{gpu.RoundtripHeavyWallMs / gpu.ResidentHeavyWallMs:F2})");
                if (gpu.Profiling)
                {
                    Console.WriteLine($"    三拆(GPU事件)   : 纯内核={gpu.KernelHeavyMs:F3}  上传={gpu.UploadHeavyMs:F3}  回读={gpu.DownloadHeavyMs:F3}  (ms)");
                }
                Console.WriteLine($"  GPU 相对 ISPC: 常驻 x{_ispcHeavyMs / gpu.ResidentHeavyWallMs:F2},  全往返 x{_ispcHeavyMs / gpu.RoundtripHeavyWallMs:F2}");
                if (gpu.RoundtripPlHeavyWallMs > 0)
                {
                    Console.WriteLine($"  GPU 往返(页锁定)   : {gpu.RoundtripPlHeavyWallMs,10:F3}   (vs ISPC x{_ispcHeavyMs / gpu.RoundtripPlHeavyWallMs:F2})");
                    Console.WriteLine($"    三拆(GPU事件)   : 纯内核={gpu.RoundtripPlHeavyKernelMs:F3}  上传={gpu.RoundtripPlHeavyUploadMs:F3}  回读={gpu.RoundtripPlHeavyDownloadMs:F3}  (ms)");
                }
            }
            Console.WriteLine();
            Console.WriteLine("Gate 1a 判定（常驻无回读 GPU ≤ ISPC 1/3 才过门）：");
            if (gpu != null && _ispcHeavyMs > 0)
            {
                double ratio = gpu.ResidentHeavyWallMs / _ispcHeavyMs;
                Console.WriteLine($"  GPU 常驻/ISPC = {ratio:F3}  →  {(ratio <= 1.0 / 3.0 ? "通过" : "未过")} (需要 ≤ 0.333)");
            }
            else
            {
                Console.WriteLine("  GPU 不可用，跳过判定。");
            }
        }

        private static double Median(double[] samples)
        {
            var s = (double[])samples.Clone();
            Array.Sort(s);
            return Percentile(s, 0.50);
        }

        private static double Percentile(double[] sorted, double p)
        {
            if (sorted.Length == 0) return 0;
            double pos = (sorted.Length - 1) * p;
            int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
            if (lo == hi) return sorted[lo];
            return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
        }

        private static void Print(string label, double[] samples, bool gpu)
        {
            var sorted = (double[])samples.Clone();
            Array.Sort(sorted);
            double avg = 0; foreach (double s in samples) avg += s; avg /= samples.Length;
            double p50 = Percentile(sorted, 0.50);
            double p95 = Percentile(sorted, 0.95);
            double p99 = Percentile(sorted, 0.99);
            double max = sorted[^1];
            Console.WriteLine($"{label,-32}: avg={avg:F3}  p50={p50:F3}  p95={p95:F3}  p99={p99:F3}  max={max:F3} ms");
            Console.WriteLine(FormattableString.Invariant(
                $"BENCH|runtime=EntJoy|case={label}|entities={Entities}|frames={samples.Length}|gpu={gpu}|avg={avg:F6}|p50={p50:F6}|p95={p95:F6}|p99={p99:F6}|max={max:F6}"));
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// ILGPU GPU 后端封装：常驻 / 往返两档。
    /// 用 CUDA 事件（ProfilingMarker）在操作前/后夹取，分别测 纯内核 / 上传 / 回读 耗时。
    /// </summary>
    internal sealed class GpuBackend : IDisposable
    {
        private const int ProbeFrames = 20;
        private const int GpuFrames = 20;   // GPU 档采样数（GPU 稳定，少样本即可）
        private readonly int _entities;
        private Context _context = null!;
        private Accelerator _acc = null!;
        private Action<Index1D, ArrayView<float2>, ArrayView<float2>, float> _heavyKernel = null!;
        private Action<Index1D, ArrayView<float2>, ArrayView<float2>, float> _lightKernel = null!;
        private MemoryBuffer1D<float2, Stride1D.Dense> _posBuf = null!;
        private MemoryBuffer1D<float2, Stride1D.Dense> _velBuf = null!;
        private float2[] _readback = null!;
        private float2[] _initialPos = null!;

        // ---- 真页锁定（cudaHostAlloc）host 缓冲：CUDA 专属验证探针，见 MeasureRoundtripPageLocked ----
        private IntPtr _hostPos = IntPtr.Zero;
        private IntPtr _hostReadback = IntPtr.Zero;

        public bool Profiling { get; private set; }
        public double ResidentHeavyWallMs { get; private set; }
        public double ResidentHeavyKernelMs { get; private set; }
        public double RoundtripHeavyWallMs { get; private set; }
        public double KernelHeavyMs { get; private set; }
        public double UploadHeavyMs { get; private set; }
        public double DownloadHeavyMs { get; private set; }
        public double RoundtripPlWallMs { get; private set; }
        public double RoundtripPlUploadMs { get; private set; }
        public double RoundtripPlKernelMs { get; private set; }
        public double RoundtripPlDownloadMs { get; private set; }
        public double RoundtripPlHeavyWallMs { get; private set; }
        public double RoundtripPlHeavyUploadMs { get; private set; }
        public double RoundtripPlHeavyKernelMs { get; private set; }
        public double RoundtripPlHeavyDownloadMs { get; private set; }

        // LightMove 汇总（iterations==0 档）
        public double LightResidentWallMs { get; private set; }
        public double LightRoundtripWallMs { get; private set; }
        public double LightRoundtripPlWallMs { get; private set; }

        public GpuBackend(int entities, bool verbose) { _entities = entities; _verbose = verbose; }
        private readonly bool _verbose;

        public void Init()
        {
            // CUDA + profiling（ProfilingMarker 依赖 profiling 打开）
            _context = Context.Create(b => b.Cuda().Profiling());
            _acc = _context.CreateCudaAccelerator(0);
            Profiling = true;

            _heavyKernel = _acc.LoadAutoGroupedStreamKernel<
                Index1D, ArrayView<float2>, ArrayView<float2>, float>(GpuIlgpuBenchmark_HeavyKernel);
            _lightKernel = _acc.LoadAutoGroupedStreamKernel<
                Index1D, ArrayView<float2>, ArrayView<float2>, float>(GpuIlgpuBenchmark_LightKernel);

            _posBuf = _acc.Allocate1D<float2>(_entities);
            _velBuf = _acc.Allocate1D<float2>(_entities);
            _readback = new float2[_entities];

            // 页锁定 host 缓冲（cuMemHostAlloc）——仅 CUDA 验证探针（跨平台见 docs/13 §2.2 跨平台注）
            var api = CudaAPI.CurrentAPI;
            int bytes = _entities * 8; // float2 = 8B
            if (api.AllocateHostMemory(out _hostPos, (IntPtr)bytes) != CudaError.CUDA_SUCCESS)
                throw new InvalidOperationException("AllocateHostMemory(_hostPos) failed");
            if (api.AllocateHostMemory(out _hostReadback, (IntPtr)bytes) != CudaError.CUDA_SUCCESS)
                throw new InvalidOperationException("AllocateHostMemory(_hostReadback) failed");

            if (_verbose) Console.WriteLine($"[GPU] device={_acc.Name}, profiling={(Profiling ? "on" : "off")}");
        }

        /// <summary>预热 GPU：cudaMemcpy + JIT 编译双内核。保存初始数据供往返/传输探针使用。</summary>
        public void PrimeJit(float2[] initPos, float2[] initVel)
        {
            _initialPos = initPos;
            _posBuf.CopyFromCPU(initPos);
            _velBuf.CopyFromCPU(initVel);
            _heavyKernel((Index1D)_entities, _posBuf.View, _velBuf.View, 1.0f / 60.0f);
            _acc.Synchronize();
            _lightKernel((Index1D)_entities, _posBuf.View, _velBuf.View, 1.0f / 60.0f);
            _acc.Synchronize();

            // 初始数据拷进页锁定 host 缓冲（一劳永逸；真实场景 CPU 直接写页锁定缓冲）
            unsafe
            {
                fixed (void* p = initPos) UnsafeUtility.MemCpy((void*)_hostPos, p, (long)initPos.Length * sizeof(float2));
                fixed (void* v = initVel) UnsafeUtility.MemCpy((void*)_hostReadback, v, (long)initVel.Length * sizeof(float2));
            }
        }

        internal static void GpuIlgpuBenchmark_HeavyKernel(Index1D i, ArrayView<float2> pos, ArrayView<float2> vel, float dt)
        {
            float px = pos[i].x;
            float py = pos[i].y;
            float vx = vel[i].x;
            float vy = vel[i].y;
            float accX = px * 0.001f + vx * 0.01f;
            float accY = py * 0.001f + vy * 0.01f;
            for (int it = 0; it < 16; it++)
            {
                float phaseX = accX + it * 0.03125f;
                float phaseY = accY - it * 0.0625f;
                float wave = MathF.Sin(phaseX) + MathF.Cos(phaseY);
                float radius = MathF.Sqrt(accX * accX + accY * accY + 1.0f);
                accX = accX * 0.985f + wave * 0.015f + radius * 0.0002f + vx * 0.0001f;
                accY = accY * 0.982f - wave * 0.012f + radius * 0.0003f + vy * 0.0001f;
            }
            pos[i] = new float2(px + vx * dt + accX * 0.001f, py + vy * dt + accY * 0.001f);
        }

        internal static void GpuIlgpuBenchmark_LightKernel(Index1D i, ArrayView<float2> pos, ArrayView<float2> vel, float dt)
        {
            pos[i] = new float2(pos[i].x + vel[i].x * dt, pos[i].y + vel[i].y * dt);
        }

        public void Measure(bool resident, int iterations, string loadLabel)
        {
            string kind = resident ? "GPU 常驻" : "GPU 往返";
            var kernel = iterations > 0 ? _heavyKernel : _lightKernel;
            int n = _entities;

            // 预热（含 JIT 首启已由 PrimeJit 完成，此处仅为数据面热身）
            for (int i = 0; i < 5; i++)
            {
                if (!resident)
                {
                    _posBuf.CopyFromCPU(_initialPos);
                }
                kernel((Index1D)n, _posBuf.View, _velBuf.View, 1.0f / 60.0f);
                _acc.Synchronize();
                if (!resident)
                {
                    _posBuf.CopyToCPU(_readback);
                }
            }

            var wall = new double[GpuFrames];
            var kern = new double[GpuFrames];
            var up = new double[GpuFrames];
            var down = new double[GpuFrames];

            for (int f = 0; f < GpuFrames; f++)
            {
                long t0 = Stopwatch.GetTimestamp();

                // marker 夹取：上传
                ProfilingMarker upS = AddMarker();
                if (!resident)
                {
                    _posBuf.CopyFromCPU(_initialPos);
                }
                ProfilingMarker upE = AddMarker();

                // marker 夹取：纯内核
                ProfilingMarker kS = AddMarker();
                kernel((Index1D)n, _posBuf.View, _velBuf.View, 1.0f / 60.0f);
                ProfilingMarker kE = AddMarker();

                // marker 夹取：回读
                ProfilingMarker dS = AddMarker();
                if (!resident)
                {
                    _posBuf.CopyToCPU(_readback);
                }
                ProfilingMarker dE = AddMarker();

                _acc.Synchronize();
                long t1 = Stopwatch.GetTimestamp();

                wall[f] = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
                up[f] = upE.MeasureFrom(upS).TotalMilliseconds;
                kern[f] = kE.MeasureFrom(kS).TotalMilliseconds;
                down[f] = dE.MeasureFrom(dS).TotalMilliseconds;
            }

            double wallP50 = Median(wall);
            string label = $"{loadLabel}: {kind}";
            PrintGpu(label, wall, kern, up, down, roundtrip: !resident);

            // 捕获 Heavy 汇总
            if (iterations > 0)
            {
                if (resident)
                {
                    ResidentHeavyWallMs = wallP50;
                    ResidentHeavyKernelMs = Median(kern);
                }
                else
                {
                    RoundtripHeavyWallMs = wallP50;
                    KernelHeavyMs = Median(kern);
                    UploadHeavyMs = Median(up);
                    DownloadHeavyMs = Median(down);
                }
            }
            else if (resident)
            {
                LightResidentWallMs = wallP50;
            }
            else
            {
                LightRoundtripWallMs = wallP50;
            }
        }

        /// <summary>
        /// 真页锁定（cudaHostAlloc）全往返：upload(页锁定 host→GPU) + kernel + readback(GPU→页锁定 host)。
        /// 与 Measure(resident:false) 的 pageable 往返对比——传输量一致（仅 pos 8MB↑ / 8MB↓，vel 常驻），
        /// 隔离「页锁定 vs pageable」这条路径差异。
        /// ⚠ CUDA 专属验证探针（跨平台「页锁定/共享 host 内存」抽象见 docs/13 §2.2 跨平台注）。
        /// </summary>
        public void MeasureRoundtripPageLocked(int iterations, string loadLabel)
        {
            var kernel = iterations > 0 ? _heavyKernel : _lightKernel;
            int n = _entities;
            var posView = _posBuf.AsArrayView<float2>(0, n);
            var wall = new double[GpuFrames];
            var up = new double[GpuFrames];
            var kern = new double[GpuFrames];
            var down = new double[GpuFrames];
            unsafe
            {
                float2* hp = (float2*)_hostPos;
                float2* hr = (float2*)_hostReadback;

                void Frame()
                {
                    posView.CopyFromCPUUnsafeAsync(_acc.DefaultStream, ref *hp, n);
                    kernel((Index1D)n, _posBuf.View, _velBuf.View, 1.0f / 60.0f);
                    posView.CopyToCPUUnsafeAsync(_acc.DefaultStream, ref *hr, n);
                }

                for (int i = 0; i < 5; i++) Frame();
                _acc.Synchronize();
                for (int f = 0; f < GpuFrames; f++)
                {
                    long s0 = Stopwatch.GetTimestamp();
                    ProfilingMarker upS = AddMarker();
                    posView.CopyFromCPUUnsafeAsync(_acc.DefaultStream, ref *hp, n);
                    ProfilingMarker upE = AddMarker();
                    ProfilingMarker kS = AddMarker();
                    kernel((Index1D)n, _posBuf.View, _velBuf.View, 1.0f / 60.0f);
                    ProfilingMarker kE = AddMarker();
                    ProfilingMarker dS = AddMarker();
                    posView.CopyToCPUUnsafeAsync(_acc.DefaultStream, ref *hr, n);
                    ProfilingMarker dE = AddMarker();
                    _acc.Synchronize();
                    long s1 = Stopwatch.GetTimestamp();
                    wall[f] = (s1 - s0) * 1000.0 / Stopwatch.Frequency;
                    up[f] = upE.MeasureFrom(upS).TotalMilliseconds;
                    kern[f] = kE.MeasureFrom(kS).TotalMilliseconds;
                    down[f] = dE.MeasureFrom(dS).TotalMilliseconds;
                }

                // correctness sanity：页锁定回读 vs 托管回读，对同一 kernel 结果逐元素抽样比对
                posView.CopyFromCPUUnsafeAsync(_acc.DefaultStream, ref *hp, n);
                kernel((Index1D)n, _posBuf.View, _velBuf.View, 1.0f / 60.0f);
                posView.CopyToCPUUnsafeAsync(_acc.DefaultStream, ref *hr, n);
                _acc.Synchronize();
                _posBuf.CopyToCPU(_readback);
                _acc.Synchronize();
                int mism = 0;
                for (int i = 0; i < n; i += 997)
                    if (((float2*)hr)[i].x != _readback[i].x) mism++;
                if (_verbose) Console.WriteLine($"  [sanity] 页锁定回读 vs 托管回读: 抽样不等={mism} (步长 997, 共 {n / 997 + 1})");
            }

            RoundtripPlWallMs = Median(wall);
            RoundtripPlUploadMs = Median(up);
            RoundtripPlKernelMs = Median(kern);
            RoundtripPlDownloadMs = Median(down);
            if (iterations > 0)
            {
                // 捕获 Heavy 汇总（LightMove iterations==0 不覆盖，供 PrintSummaryMatrix）
                RoundtripPlHeavyWallMs = RoundtripPlWallMs;
                RoundtripPlHeavyUploadMs = RoundtripPlUploadMs;
                RoundtripPlHeavyKernelMs = RoundtripPlKernelMs;
                RoundtripPlHeavyDownloadMs = RoundtripPlDownloadMs;
            }
            else
            {
                LightRoundtripPlWallMs = RoundtripPlWallMs;
            }
            PrintGpu($"{loadLabel}: GPU 往返(页锁定)", wall, kern, up, down, roundtrip: true);
        }

        private ProfilingMarker AddMarker() => _acc.DefaultStream.AddProfilingMarker();

        private void PrintGpu(string label, double[] wall, double[] kern, double[] up, double[] down, bool roundtrip)
        {
            if (!_verbose) return;
            var s = (double[])wall.Clone();
            Array.Sort(s);
            double avg = 0; foreach (double x in wall) avg += x; avg /= wall.Length;
            Console.WriteLine($"{label,-32}: 墙钟 avg={avg:F4} p50={Median(s):F4} p95={Percentile(s, 0.95):F4} ms");
            Console.WriteLine(FormattableString.Invariant(
                $"BENCH|runtime=EntJoy|case={label}|entities=0|frames={wall.Length}|gpu=true|avg={avg:F6}|p50={Median(s):F6}|p95={Percentile(s, 0.95):F6}"));
            if (roundtrip)
            {
                Console.WriteLine($"  ├─ 上传(GPU事件)    p50={Median(up):F4} ms");
                Console.WriteLine($"  ├─ 纯内核(GPU事件)  p50={Median(kern):F4} ms");
                Console.WriteLine($"  └─ 回读(GPU事件)    p50={Median(down):F4} ms");
            }
            else
            {
                Console.WriteLine($"  └─ 纯内核(GPU事件)  p50={Median(kern):F4} ms");
            }
        }

        public void RunTransferProbe(int entities)
        {
            Console.WriteLine();
            Console.WriteLine("----- GPU 传输探针（与实体数同尺寸的 float2 双向） -----");
            int bytesUp = entities * 8;
            int bytesDown = entities * 8;
            var up = new double[ProbeFrames];
            var down = new double[ProbeFrames];
            for (int i = 0; i < ProbeFrames; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                _posBuf.CopyFromCPU(_initialPos);
                _acc.Synchronize();
                long t1 = Stopwatch.GetTimestamp();
                up[i] = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
            }
            for (int i = 0; i < ProbeFrames; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                _posBuf.CopyToCPU(_readback);
                _acc.Synchronize();
                long t1 = Stopwatch.GetTimestamp();
                down[i] = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
            }
            double upP50 = Median(up), downP50 = Median(down);
            double upGBs = upP50 > 0 ? bytesUp / (upP50 * 1e-3) / 1e9 : 0;
            double downGBs = downP50 > 0 ? bytesDown / (downP50 * 1e-3) / 1e9 : 0;
            Console.WriteLine($"upload   {bytesUp,9:N0} B   p50={upP50:F4} ms   有效 {upGBs:F2} GB/s");
            Console.WriteLine($"download {bytesDown,9:N0} B   p50={downP50:F4} ms   有效 {downGBs:F2} GB/s");
            Console.WriteLine($"roundtrip(p50) ≈ {upP50 + downP50:F4} ms  (对齐 docs/10 §6 传输税)");
        }

        /// <summary>
        /// 双流传输探针（页锁定 8MB×2）：单流串行 H2D→D2H vs 双流 H2D‖D2H 并行。
        /// 验证跨帧流水（readback_N-1 ‖ kernel_N ‖ upload_N+1）的物理前提：
        /// copy 引擎可并行 + PCIe 全双工 → 同时双向传输 wall ≈ max 而非和。
        /// 用两个独立 buffer（posBuf/velBuf）避免 ILGPU 跨流隐式同步。
        /// </summary>
        public void RunDualStreamProbe(int entities)
        {
            Console.WriteLine();
            Console.WriteLine("----- 双流传输探针（页锁定 8MB：单流串行 vs 双流 H2D‖D2H 并行） -----");
            int bytes = entities * 8;
            var posView = _posBuf.AsArrayView<float2>(0, entities);
            var velView = _velBuf.AsArrayView<float2>(0, entities);
            int n = 30;
            unsafe
            {
                float2* hp = (float2*)_hostPos;      // 页锁定上传源
                float2* hr = (float2*)_hostReadback; // 页锁定回读目标

                using (var stream2 = _acc.CreateStream())
                {
                    // ① 单流串行：同一 DefaultStream 上 back-to-back（H2D 再 D2H），末尾统一 sync
                    for (int i = 0; i < 5; i++)
                    {
                        posView.CopyFromCPUUnsafeAsync(_acc.DefaultStream, ref *hp, entities);
                        velView.CopyToCPUUnsafeAsync(_acc.DefaultStream, ref *hr, entities);
                    }
                    _acc.Synchronize();
                    long s0 = Stopwatch.GetTimestamp();
                    for (int r = 0; r < n; r++)
                    {
                        posView.CopyFromCPUUnsafeAsync(_acc.DefaultStream, ref *hp, entities);
                        velView.CopyToCPUUnsafeAsync(_acc.DefaultStream, ref *hr, entities);
                    }
                    _acc.Synchronize();
                    long s1 = Stopwatch.GetTimestamp();
                    double sWall = (s1 - s0) * 1000.0 / Stopwatch.Frequency / n;

                    // ② 双流并行：H2D 在 DefaultStream，D2H 在 stream2，不同 buffer 无依赖 → 可重叠
                    for (int i = 0; i < 5; i++)
                    {
                        posView.CopyFromCPUUnsafeAsync(_acc.DefaultStream, ref *hp, entities);
                        velView.CopyToCPUUnsafeAsync(stream2, ref *hr, entities);
                    }
                    _acc.Synchronize(); stream2.Synchronize();
                    long d0 = Stopwatch.GetTimestamp();
                    for (int r = 0; r < n; r++)
                    {
                        posView.CopyFromCPUUnsafeAsync(_acc.DefaultStream, ref *hp, entities);
                        velView.CopyToCPUUnsafeAsync(stream2, ref *hr, entities);
                    }
                    _acc.Synchronize(); stream2.Synchronize();
                    long d1 = Stopwatch.GetTimestamp();
                    double dWall = (d1 - d0) * 1000.0 / Stopwatch.Frequency / n;

                    double serGBs = 2.0 * bytes / (sWall * 1e-3) / 1e9;
                    double dualGBs = 2.0 * bytes / (dWall * 1e-3) / 1e9;
                    Console.WriteLine($"  单流串行 8MB↑+8MB↓   : p50={sWall:F4} ms  (双向合计 {serGBs:F2} GB/s)");
                    Console.WriteLine($"  双流并行 H2D‖D2H    : p50={dWall:F4} ms  (双向合计 {dualGBs:F2} GB/s)  →  x{sWall / dWall:F2}");
                    Console.WriteLine($"  结论: {(dWall < sWall * 0.8 ? "双流并行生效（copy 引擎并行 + PCIe 全双工），跨帧流水可将传输 wall 从「和」压到「max」" : "双流并行无明显收益（copy 引擎已饱和或 ILGPU 隐式同步串行化）")}");
                }
            }
        }

        private static double Median(double[] s)
        {
            var c = (double[])s.Clone();
            Array.Sort(c);
            return Percentile(c, 0.50);
        }

        private static double Percentile(double[] sorted, double p)
        {
            if (sorted.Length == 0) return 0;
            double pos = (sorted.Length - 1) * p;
            int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
            if (lo == hi) return sorted[lo];
            return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
        }

        public void Dispose()
        {
            _posBuf?.Dispose();
            _velBuf?.Dispose();
            _acc?.Dispose();
            _context?.Dispose();
            if (_hostPos != IntPtr.Zero)
            {
                CudaAPI.CurrentAPI.FreeHostMemory(_hostPos);
                _hostPos = IntPtr.Zero;
            }
            if (_hostReadback != IntPtr.Zero)
            {
                CudaAPI.CurrentAPI.FreeHostMemory(_hostReadback);
                _hostReadback = IntPtr.Zero;
            }
        }
    }
}
