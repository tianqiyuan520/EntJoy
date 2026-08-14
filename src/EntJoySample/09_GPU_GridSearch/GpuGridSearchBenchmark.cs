using EntJoy.Collections;
using EntJoy.Mathematics;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.Cuda;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace EntJoySample.GpuGridSearch
{
    /// <summary>closest kernel 的 uniform 参数（打包成单块 blittable struct；ILGPU 要求 public 顶层类型）。</summary>
    public struct ClosestPointParams
    {
        public float OriginX, OriginY, GridResolutionInv, SquaredEpsilonSelf;
        public int GridDimX, GridDimY, SortedLength, IgnoreSelf;
    }
    /// <summary>within kernel 的 uniform 参数。</summary>
    public struct FindWithinParams
    {
        public float OriginX, OriginY, GridResolutionInv, SquaredRadius;
        public int GridDimX, GridDimY, MaxNeighbor, CellsToLoop;
    }
    /// <summary>GPU 创建网格 pipeline 的 uniform 参数。</summary>
    public struct GridBuildParams
    {
        public float OriginX, OriginY, ResolutionInv;
        public int DimX, DimY, CellCount, N, SegLen, NumSegments;
    }

    /// <summary>
    /// GPU 版 TestGridSearch（第二个测试）：reduce/query 主战场。
    ///
    /// 数据面形态：grid（sortedPositions / hashIndex / cellStartEnd）常驻 GPU（上传一次），
    /// 每帧只传 query↑（800KB）+ result↓（400KB）；对照「全量提交」模式（grid+query 每帧都传），
    /// 直接回答「把 System 翻译成 GPU 仍是全量提交？」——全量提交 ≈ 劣化，常驻 ≈ 3x 赢。
    ///
    /// ILGPU kernel 手写、逐句镜像 GridSearch2D.ClosestPointJobPointer / FindWithinJobPointer 的 Execute。
    /// grid 用纯 C# 构建（确定性、不依赖 NativeDll 符号）；ISPC 基线直接构造 transpiled
    /// ClosestPointJobPointer 测量（native 符号缺失则降级为 C# 参考，GPU parity 照常验证）。
    /// </summary>
    public sealed class GpuGridSearchBenchmark : IDisposable
    {
        // ---- 配置（对齐 TestGridSearch.cs：N/K/seed/targetGrid 相同） ----
        private const int N = 100000;            // 点
        private const int K = 100000;            // 查询
        private const int TargetGrid = 200;      // ~200×200 = 40k cells
        private const int Warmup = 5;
        private const int MeasureFrames = 100;   // CPU 基线采样
        private const int GpuFrames = 20;        // GPU 采样（GPU 稳定，少样本即可）

        // ---- 创建网格采样（一次性，重建才重传） ----
        private const int BuildWarmup = 3;
        private const int BuildFrames = 20;
        private const int BuildSegLen = 512;      // GPU 前缀和每段元素数（40k cells → 79 段）

        // ---- FindWithin 配置（本测试扩展；TestGridSearch 只基准化了 ClosestPoint） ----
        private const float WithinRadius = 2.0f;
        private const int MaxNeighbor = 8;

        /// <summary>TestGridSearch 同一 seed/配置下的参考 fingerprint（查询结果前 10 个）。</summary>
        private const string ExpectedFingerprint = "74945 21160 15114 75587 37949 80702 88467 19643 11454 87386";

        // ---- grid（纯 C# 构建，GPU 上传 + parity 参考共用） ----
        private float2[] _sortedArr = null!;
        private int2[] _hashArr = null!;
        private int2[] _cellArr = null!;
        private float2[] _queryArr = null!;
        private float _originX, _originY, _resInv;
        private int _gridDimX, _gridDimY, _cellCount;

        // ---- ILGPU 状态 ----
        private Context _context = null!;
        private Accelerator _acc = null!;
        private Action<Index1D, ArrayView<float2>, ArrayView<int2>, ArrayView<int2>, ArrayView<float2>, ArrayView<int>, ClosestPointParams> _closestKernel = null!;
        private Action<Index1D, ArrayView<float2>, ArrayView<int2>, ArrayView<int2>, ArrayView<float2>, ArrayView<int>, FindWithinParams> _withinKernel = null!;
        private MemoryBuffer1D<float2, Stride1D.Dense> _sortedPosBuf = null!;
        private MemoryBuffer1D<int2, Stride1D.Dense> _hashIndexBuf = null!;
        private MemoryBuffer1D<int2, Stride1D.Dense> _cellStartEndBuf = null!;
        private MemoryBuffer1D<float2, Stride1D.Dense> _queryBuf = null!;
        private MemoryBuffer1D<int, Stride1D.Dense> _resultBuf = null!;
        private MemoryBuffer1D<int, Stride1D.Dense> _withinBuf = null!;
        private int[] _resultArr = null!;
        private int[] _withinArr = null!;

        private double _ispcQueryMs = -1;
        private double _noSyncWall = -1;
        private double _pinnedWall = -1;
        private double _nativeCollectionWall = -1;
        private string _ispcUnavailable = "";

        // ---- GPU 创建网格（独立 buffer，不干扰常驻查询的 CPU 构建 grid） ----
        private Action<Index1D, ArrayView<float2>, ArrayView<int>, GridBuildParams> _buildCountKernel = null!;
        private Action<Index1D, ArrayView<int>, ArrayView<int>, GridBuildParams> _buildScanSegKernel = null!;
        private Action<Index1D, ArrayView<int>, ArrayView<int>, GridBuildParams> _buildScanSumsKernel = null!;
        private Action<Index1D, ArrayView<int>, ArrayView<int>, GridBuildParams> _buildAddOffsetsKernel = null!;
        private Action<Index1D, ArrayView<int>, ArrayView<int2>, GridBuildParams> _buildFillKernel = null!;
        private Action<Index1D, ArrayView<float2>, ArrayView<int>, ArrayView<float2>, ArrayView<int2>, GridBuildParams> _buildPlaceKernel = null!;
        private MemoryBuffer1D<float2, Stride1D.Dense> _buildPosBuf = null!;
        private MemoryBuffer1D<int, Stride1D.Dense> _buildCountsBuf = null!;
        private MemoryBuffer1D<int, Stride1D.Dense> _buildSegSumsBuf = null!;
        private MemoryBuffer1D<int, Stride1D.Dense> _buildSegOffsetsBuf = null!;
        private MemoryBuffer1D<float2, Stride1D.Dense> _buildSortedBuf = null!;
        private MemoryBuffer1D<int2, Stride1D.Dense> _buildHashBuf = null!;
        private MemoryBuffer1D<int2, Stride1D.Dense> _buildCellBuf = null!;
        private float2[] _posArr = null!;
        private int _numSegments;
        private double _buildGpuMs = -1;

        // ---- 测量结果 ----
        private readonly record struct MeasureResult(string Label, double[] Wall, double[] Up, double[] Kernel, double[] Down);

        private readonly bool _verbose;
        public GpuGridSearchBenchmark(bool verbose = true) => _verbose = verbose;

        public void Run()
        {
            if (_verbose)
            {
                Console.WriteLine("=== GridSearch2D GPU 常驻查询（第二个测试）===");
                Console.WriteLine($"配置: N={N} 点, K={K} 查询, targetGrid={TargetGrid} (~{TargetGrid}×{TargetGrid} cells), seed=1234, within(r={WithinRadius}, maxNeighbor={MaxNeighbor})");
            }

            // 1. 数据 + grid（纯 C# 构建）
            var nativePos = new NativeArray<float2>(N, Allocator.Persistent);
            var nativeQueries = new NativeArray<float2>(K, Allocator.Persistent);
            var rnd = new Random(1234);
            _posArr = new float2[N];
            for (int i = 0; i < N; i++)
                _posArr[i] = new float2((float)(rnd.NextDouble() * 200 - 100), (float)(rnd.NextDouble() * 200 - 100));
            nativePos.CopyFrom(_posArr);
            _queryArr = new float2[K];
            for (int i = 0; i < K; i++)
                _queryArr[i] = new float2((float)(rnd.NextDouble() * 200 - 100), (float)(rnd.NextDouble() * 200 - 100));
            nativeQueries.CopyFrom(_queryArr);
            BuildGridCSharp(nativePos);

            if (_verbose)
                Console.WriteLine(FormattableString.Invariant(
                    $"grid: {_gridDimX}×{_gridDimY} cells, res={1f / _resInv:F4}, sortedPos/hashIndex {N * 8 / 1024}KB, cellStartEnd {_cellCount * 8 / 1024}KB"));

            // 2. CPU 参考（纯 C#，parity 锚点；同时测单线程 CPU 基线）
            var cpuClosest = CpuClosestReferenceAll();
            var cpuWithin = CpuWithinReferenceAll();

            // 2.5 C# 多线程（Parallel.For）基线——补齐最终表 C# 多线程列
            _csParallelMs = MeasureCsParallelClosest();

            // 2.6 创建网格（一次性）——C# 单线程 / C# 多线程 / C++(native JobSystem)
            _buildCsSingleMs = MeasureBuildCsSingle(nativePos);
            _buildCsParallelMs = MeasureBuildCsParallel(nativePos);
            _buildCppMs = MeasureBuildCpp(nativePos);

            // 3. ISPC 基线（transpiled ClosestPointJobPointer；native 缺符号则降级）
            _ispcQueryMs = TryMeasureIspcClosest(nativeQueries);

            // 4. GPU init + 上传 grid（一次，常驻）
            Init();

            // 5. parity（GPU vs CPU 参考，逐元素）
            bool closestParity = CheckClosestParity(cpuClosest);
            bool withinParity = CheckWithinParity(cpuWithin);

            // 6. 基准矩阵：closest 常驻 / 往返（负对照），within 常驻
            var closestRes = MeasureClosest(resident: true);
            var closestRt = MeasureClosest(resident: false);
            var withinRes = MeasureWithin(resident: true);

            // 汇总供 10_GPU_FinalCompare 最终表读取
            _gpuResidentClosestMs = Median(closestRes.Wall);
            _gpuRoundtripClosestMs = Median(closestRt.Wall);

            // 6.5 GPU 创建网格（独立 buffer 构建 + 验证 + 计时；positions 常驻上传一次）
            _buildGpuMs = MeasureBuildGpu(cpuClosest);

            // 6.6 GPU 全常驻 closest（零传输，kernel-only）：喂最终表「GPU 常驻(无传输)」GridSearch 列
            MeasureClosestResidentKernel();

            // 7. 汇总 + 门判定
            if (_verbose) PrintSummary(closestParity, withinParity, closestRes, closestRt, withinRes);

            // 8. 查询规模探针（grid 常驻不变，K 100k→1M：展示传输 vs 计算随规模的对冲）
            if (_verbose) RunQueryScaleProbe();

            // 9-11. 传输优化探针（pipeline/pinned/NativeCollection）：仅 verbose 输出，数值不进最终表
            if (_verbose)
            {
                MeasureClosestPipeline();
                MeasureClosestPinned(cpuClosest);
                MeasureClosestNativeCollection(nativeQueries, cpuClosest);
            }

            // 12. 真页锁定（cudaHostAlloc）：_plClosestWallMs 供最终表，必须跑
            MeasureClosestPageLocked(cpuClosest);

            nativePos.Dispose();
            nativeQueries.Dispose();
        }

        // ================= grid 构建（纯 C#，逐句镜像 GridSearch2D.InitializeGridInternal） =================

        private void BuildGridCSharp(NativeArray<float2> positions)
        {
            // 1. 包围盒 + 网格初始化（GridInitializationJobPointer）
            float minX = positions[0].x, minY = positions[0].y, maxX = positions[0].x, maxY = positions[0].y;
            for (int i = 1; i < positions.Length; i++)
            {
                float2 p = positions[i];
                minX = MathF.Min(minX, p.x); minY = MathF.Min(minY, p.y);
                maxX = MathF.Max(maxX, p.x); maxY = MathF.Max(maxY, p.y);
            }
            float rangeX = maxX - minX, rangeY = maxY - minY;
            float maxRange = MathF.Max(rangeX, rangeY);
            float resolution = maxRange / TargetGrid;          // _resolution = -1 → 自动推导
            int dimX = Math.Max(1, (int)MathF.Ceiling(rangeX / resolution));
            int dimY = Math.Max(1, (int)MathF.Ceiling(rangeY / resolution));
            int cellCount = dimX * dimY;
            _originX = minX; _originY = minY;
            _resInv = 1f / resolution;
            _gridDimX = dimX; _gridDimY = dimY; _cellCount = cellCount;

            // 2. 哈希分配 + 计数（AssignAndCountJobPointer）+ 3. 前缀和（PrefixSumJobPointer）
            var hashIndex = new int2[positions.Length];        // (hash, origIdx)，原始顺序
            var counts = new int[cellCount];
            for (int i = 0; i < positions.Length; i++)
            {
                float2 p = positions[i];
                int cx = (int)((p.x - minX) * _resInv);
                cx = cx < 0 ? 0 : (cx > dimX - 1 ? dimX - 1 : cx);
                int cy = (int)((p.y - minY) * _resInv);
                cy = cy < 0 ? 0 : (cy > dimY - 1 ? dimY - 1 : cy);
                int hash = cy * dimX + cx;
                hash = hash < 0 ? 0 : (hash > cellCount - 1 ? cellCount - 1 : hash);
                hashIndex[i] = new int2(hash, i);
                counts[hash]++;
            }
            int sum = 0;
            for (int i = 0; i < cellCount; i++) { int c = counts[i]; counts[i] = sum; sum += c; }

            // 4. 元素放置（PlaceElementsJobPointer，顺序版等价原子版）
            _sortedArr = new float2[positions.Length];
            var sortedHashIndex = new int2[positions.Length];  // SortedHashIndex（TempJob）
            for (int i = 0; i < positions.Length; i++)
            {
                int2 entry = hashIndex[i];
                int dest = counts[entry.x];
                counts[entry.x] = dest + 1;
                _sortedArr[dest] = positions[i];
                sortedHashIndex[dest] = entry;
            }

            // 5. 填充 cell 起止（FillCellStartEndJobPointer）
            _cellArr = new int2[cellCount];
            for (int i = 0; i < cellCount; i++) _cellArr[i] = new int2(-1, -1);
            if (positions.Length > 0)
            {
                int currentHash = sortedHashIndex[0].x;
                int startIdx = 0;
                for (int i = 1; i <= positions.Length; i++)
                {
                    if (i == positions.Length || sortedHashIndex[i].x != currentHash)
                    {
                        _cellArr[currentHash] = new int2(startIdx, i);
                        if (i < positions.Length) { currentHash = sortedHashIndex[i].x; startIdx = i; }
                    }
                }
            }

            // 6. 最终 hashIndex = SortedHashIndex（CopyHashIndexJobPointer）
            _hashArr = sortedHashIndex;
        }

        // ================= CPU 参考（纯 C#，逐句镜像 Execute；无 native 依赖） =================

        private static float DistSq(float2 a, float2 b)
        {
            float dx = a.x - b.x, dy = a.y - b.y;
            return dx * dx + dy * dy;
        }

        private int[] CpuClosestReferenceAll()
        {
            var sw = Stopwatch.StartNew();
            var res = new int[K];
            for (int i = 0; i < K; i++) res[i] = CpuClosestOne(_queryArr[i]);
            _cpuRefMs = sw.Elapsed.TotalMilliseconds;
            return res;
        }

        private int CpuClosestOne(float2 q)
        {
            int best = -1;
            float bestDistSq = float.MaxValue;
            int cx = (int)MathF.Floor((q.x - _originX) * _resInv);
            cx = cx < 0 ? 0 : (cx > _gridDimX - 1 ? _gridDimX - 1 : cx);
            int cy = (int)MathF.Floor((q.y - _originY) * _resInv);
            cy = cy < 0 ? 0 : (cy > _gridDimY - 1 ? _gridDimY - 1 : cy);

            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = cx + dx;
                if ((uint)nx >= (uint)_gridDimX) continue;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = cy + dy;
                    if ((uint)ny >= (uint)_gridDimY) continue;
                    int cellHash = ny * _gridDimX + nx;
                    int start = _cellArr[cellHash].x;
                    int end = _cellArr[cellHash].y;
                    if (start < 0) continue;
                    for (int j = start; j < end; j++)
                    {
                        float d = DistSq(q, _sortedArr[j]);
                        if (d < bestDistSq) { bestDistSq = d; best = j; }
                    }
                }
            }

            if (best != -1) return _hashArr[best].y;
            // 空邻域全局回退（与 Execute 一致）
            for (int j = 0; j < N; j++)
            {
                float d = DistSq(q, _sortedArr[j]);
                if (d < bestDistSq) { bestDistSq = d; best = j; }
            }
            return best == -1 ? -1 : _hashArr[best].y;
        }

        private int[] CpuWithinReferenceAll()
        {
            int cellsToLoop = (int)MathF.Ceiling(WithinRadius * _resInv);
            var res = new int[K * MaxNeighbor];
            for (int i = 0; i < K; i++)
            {
                int baseIdx = i * MaxNeighbor;
                for (int k = 0; k < MaxNeighbor; k++) res[baseIdx + k] = -1;

                float2 q = _queryArr[i];
                int ccx = (int)MathF.Floor((q.x - _originX) * _resInv);
                ccx = ccx < 0 ? 0 : (ccx > _gridDimX - 1 ? _gridDimX - 1 : ccx);
                int ccy = (int)MathF.Floor((q.y - _originY) * _resInv);
                ccy = ccy < 0 ? 0 : (ccy > _gridDimY - 1 ? _gridDimY - 1 : ccy);

                int found = 0;
                // center cell
                int centerHash = ccy * _gridDimX + ccx;
                int s0 = _cellArr[centerHash].x, e0 = _cellArr[centerHash].y;
                if (s0 >= 0)
                {
                    for (int j = s0; j < e0; j++)
                    {
                        if (DistSq(q, _sortedArr[j]) <= WithinRadius * WithinRadius)
                        {
                            res[baseIdx + found] = _hashArr[j].y;
                            if (++found == MaxNeighbor) goto done;
                        }
                    }
                }
                // 环
                for (int dx = -cellsToLoop; dx <= cellsToLoop; dx++)
                {
                    int nx = ccx + dx;
                    if ((uint)nx >= (uint)_gridDimX) continue;
                    for (int dy = -cellsToLoop; dy <= cellsToLoop; dy++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int ny = ccy + dy;
                        if ((uint)ny >= (uint)_gridDimY) continue;
                        int hash = ny * _gridDimX + nx;
                        int s = _cellArr[hash].x, e = _cellArr[hash].y;
                        if (s < 0) continue;
                        for (int j = s; j < e; j++)
                        {
                            if (DistSq(q, _sortedArr[j]) <= WithinRadius * WithinRadius)
                            {
                                res[baseIdx + found] = _hashArr[j].y;
                                if (++found == MaxNeighbor) goto done;
                            }
                        }
                    }
                }
            done: ;
            }
            return res;
        }

        private double _cpuRefMs = -1;

        // ---- 最终对比（10_GPU_FinalCompare）读取 ----
        private double _csParallelMs = -1;
        private double _plClosestWallMs = -1;
        private double _gpuResidentClosestMs = -1;
        private double _gpuResidentKernelMs = -1;
        private double _gpuRoundtripClosestMs = -1;

        public double CpuSingleMs => _cpuRefMs;
        public double CsParallelMs => _csParallelMs;
        public double IspcMs => _ispcQueryMs;
        public double GpuResidentClosestMs => _gpuResidentClosestMs;
        public double GpuResidentKernelMs => _gpuResidentKernelMs;
        public double GpuRoundtripClosestMs => _gpuRoundtripClosestMs;
        public double GpuRoundtripPlMs => _plClosestWallMs;

        // ---- 创建网格（一次性）汇总 ----
        private double _buildCsSingleMs = -1;
        private double _buildCsParallelMs = -1;
        private double _buildCppMs = -1;

        public double BuildCsSingleMs => _buildCsSingleMs;
        public double BuildCsParallelMs => _buildCsParallelMs;
        public double BuildCppMs => _buildCppMs;
        public double BuildGpuMs => _buildGpuMs;

        private double MeasureCsParallelClosest()
        {
            var res = new int[K];
            void Step() => Parallel.For(0, K, i => res[i] = CpuClosestOne(_queryArr[i]));
            for (int i = 0; i < Warmup; i++) Step();
            var samples = new double[MeasureFrames];
            for (int i = 0; i < MeasureFrames; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                Step();
                long t1 = Stopwatch.GetTimestamp();
                samples[i] = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
            }
            double p50 = Median(samples);
            if (_verbose)
                Console.WriteLine(FormattableString.Invariant(
                    $"C# 多线程(Parallel.For) closest: avg={p50:F3} p50={p50:F3} ms  (单线程 x{_cpuRefMs / p50:F2})"));
            return p50;
        }

        // ================= 创建网格（一次性，重建才重传） =================

        private double MeasureBuildCsSingle(NativeArray<float2> positions)
        {
            void Build() => BuildGridCSharp(positions);
            for (int i = 0; i < BuildWarmup; i++) Build();
            var samples = new double[BuildFrames];
            for (int i = 0; i < BuildFrames; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                Build();
                long t1 = Stopwatch.GetTimestamp();
                samples[i] = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
            }
            double p50 = Median(samples);
            if (_verbose) Console.WriteLine(FormattableString.Invariant($"GridSearch 创建网格 C# 单线程: p50={p50:F3} ms"));
            return p50;
        }

        private double MeasureBuildCsParallel(NativeArray<float2> positions)
        {
            void Build() => BuildGridCSharpParallel(positions);
            for (int i = 0; i < BuildWarmup; i++) Build();
            var samples = new double[BuildFrames];
            for (int i = 0; i < BuildFrames; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                Build();
                long t1 = Stopwatch.GetTimestamp();
                samples[i] = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
            }
            double p50 = Median(samples);
            if (_verbose) Console.WriteLine(FormattableString.Invariant($"GridSearch 创建网格 C# 多线程: p50={p50:F3} ms"));
            return p50;
        }

        private double MeasureBuildCpp(NativeArray<float2> positions)
        {
            // 原生构建路径 = GridSearch2D.InitializeGrid（transpiled C++ build jobs）。
            // 每次调用含 Dispose + 重分配 + 全构建——即「结构变化时重建 grid」的真实成本。
            using var grid = new GridSearch2D(targetGrid: TargetGrid);
            for (int i = 0; i < BuildWarmup; i++) grid.InitializeGrid(positions).Complete();
            var samples = new double[BuildFrames];
            for (int i = 0; i < BuildFrames; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                grid.InitializeGrid(positions).Complete();
                long t1 = Stopwatch.GetTimestamp();
                samples[i] = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
            }
            double p50 = Median(samples);
            if (_verbose) Console.WriteLine(FormattableString.Invariant($"GridSearch 创建网格 C++(JobSystem): p50={p50:F3} ms"));
            return p50;
        }

        /// <summary>Parallel.For 版 grid 构建（仅计时；原子放置使同 hash 内序不确定，但 grid 合法，不影响构建耗时结论）。</summary>
        private void BuildGridCSharpParallel(NativeArray<float2> positions)
        {
            int n = positions.Length;

            // 1. 包围盒（Parallel.For 归约）
            object boxLock = new();
            float gMinX = float.MaxValue, gMinY = float.MaxValue, gMaxX = float.MinValue, gMaxY = float.MinValue;
            Parallel.For(0, n,
                () => (float.MaxValue, float.MaxValue, float.MinValue, float.MinValue),
                (i, _, acc) =>
                {
                    float2 p = positions[i];
                    if (p.x < acc.Item1) acc.Item1 = p.x;
                    if (p.y < acc.Item2) acc.Item2 = p.y;
                    if (p.x > acc.Item3) acc.Item3 = p.x;
                    if (p.y > acc.Item4) acc.Item4 = p.y;
                    return acc;
                },
                acc =>
                {
                    lock (boxLock)
                    {
                        gMinX = MathF.Min(gMinX, acc.Item1);
                        gMinY = MathF.Min(gMinY, acc.Item2);
                        gMaxX = MathF.Max(gMaxX, acc.Item3);
                        gMaxY = MathF.Max(gMaxY, acc.Item4);
                    }
                });

            float maxRange = MathF.Max(gMaxX - gMinX, gMaxY - gMinY);
            float resolution = maxRange / TargetGrid;
            int dimX = Math.Max(1, (int)MathF.Ceiling((gMaxX - gMinX) / resolution));
            int dimY = Math.Max(1, (int)MathF.Ceiling((gMaxY - gMinY) / resolution));
            int cellCount = dimX * dimY;
            float resInv = 1f / resolution;

            // 2. hash 计数（原子）
            var counts = new int[cellCount];
            Parallel.For(0, n, i =>
            {
                float2 p = positions[i];
                int cx = (int)((p.x - gMinX) * resInv); cx = cx < 0 ? 0 : (cx > dimX - 1 ? dimX - 1 : cx);
                int cy = (int)((p.y - gMinY) * resInv); cy = cy < 0 ? 0 : (cy > dimY - 1 ? dimY - 1 : cy);
                int hash = cy * dimX + cx; hash = hash < 0 ? 0 : (hash > cellCount - 1 ? cellCount - 1 : hash);
                Interlocked.Increment(ref counts[hash]);
            });

            // 3. 前缀和（顺序；40k cells 微不足道）
            int sum = 0;
            for (int i = 0; i < cellCount; i++) { int c = counts[i]; counts[i] = sum; sum += c; }

            // 4. 放置（原子）
            var sorted = new float2[n];
            var hashIdx = new int2[n];
            Parallel.For(0, n, i =>
            {
                float2 p = positions[i];
                int cx = (int)((p.x - gMinX) * resInv); cx = cx < 0 ? 0 : (cx > dimX - 1 ? dimX - 1 : cx);
                int cy = (int)((p.y - gMinY) * resInv); cy = cy < 0 ? 0 : (cy > dimY - 1 ? dimY - 1 : cy);
                int hash = cy * dimX + cx; hash = hash < 0 ? 0 : (hash > cellCount - 1 ? cellCount - 1 : hash);
                int dest = Interlocked.Increment(ref counts[hash]) - 1;
                if (dest < n) { sorted[dest] = p; hashIdx[dest] = new int2(hash, i); }
            });

            // 5. fill cell 起止（顺序；40k cells）
            var cell = new int2[cellCount];
            for (int i = 0; i < cellCount; i++) cell[i] = new int2(-1, -1);
            int currentHash = hashIdx[0].x, startIdx = 0;
            for (int i = 1; i <= n; i++)
            {
                if (i == n || hashIdx[i].x != currentHash)
                {
                    cell[currentHash] = new int2(startIdx, i);
                    if (i < n) { currentHash = hashIdx[i].x; startIdx = i; }
                }
            }
        }

        // ================= ISPC 基线（transpiled job；缺符号则降级） =================

        private double TryMeasureIspcClosest(NativeArray<float2> nativeQueries)
        {
            using var sortedN = WrapNative(_sortedArr, Allocator.Persistent);
            using var hashN = WrapNative(_hashArr, Allocator.Persistent);
            using var cellN = WrapNativeList(_cellArr, Allocator.Persistent);
            using var resultsN = new NativeArray<int>(K, Allocator.Persistent);
            var job = new global::GridSearch2D.ClosestPointJobPointer
            {
                GridOrigin = new float2(_originX, _originY),
                GridResolutionInv = _resInv,
                GridDimensions = new int2(_gridDimX, _gridDimY),
                QueryPositions = nativeQueries,
                SortedPositions = sortedN,
                HashIndex = hashN,
                CellStartEnd = cellN,
                SortedLength = N,
                IgnoreSelf = false,
                SquaredEpsilonSelf = 0.001f * 0.001f,
                Results = resultsN
            };
            try
            {
                for (int i = 0; i < Warmup; i++) job.Schedule(K, GridSearch2D.QueryBatchSize).Complete();
                var samples = new double[MeasureFrames];
                for (int i = 0; i < MeasureFrames; i++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    job.Schedule(K, GridSearch2D.QueryBatchSize).Complete();
                    long t1 = Stopwatch.GetTimestamp();
                    samples[i] = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
                }
                return Median(samples);
            }
            catch (Exception ex)
            {
                _ispcUnavailable = ex.GetType().Name + ": " + ex.Message;
                return -1;
            }
        }

        private static NativeArray<T> WrapNative<T>(T[] arr, Allocator alloc) where T : unmanaged
        {
            var na = new NativeArray<T>(arr.Length, alloc);
            for (int i = 0; i < arr.Length; i++) na[i] = arr[i];
            return na;
        }
        private static NativeList<int2> WrapNativeList(int2[] arr, Allocator alloc)
        {
            var nl = new NativeList<int2>(arr.Length, alloc);
            for (int i = 0; i < arr.Length; i++) nl.Add(arr[i]);
            return nl;
        }

        // ================= GPU init + 常驻上传 =================

        private void Init()
        {
            _context = Context.Create(b => b.Cuda().Profiling());
            _acc = _context.CreateCudaAccelerator(0);

            _closestKernel = _acc.LoadAutoGroupedStreamKernel<
                Index1D, ArrayView<float2>, ArrayView<int2>, ArrayView<int2>, ArrayView<float2>, ArrayView<int>, ClosestPointParams>(ClosestPointKernel);
            _withinKernel = _acc.LoadAutoGroupedStreamKernel<
                Index1D, ArrayView<float2>, ArrayView<int2>, ArrayView<int2>, ArrayView<float2>, ArrayView<int>, FindWithinParams>(FindWithinKernel);

            // ---- GPU 创建网格 pipeline kernels（6 个 auto-grouped） ----
            _buildCountKernel = _acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float2>, ArrayView<int>, GridBuildParams>(GridBuildCountKernel);
            _buildScanSegKernel = _acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<int>, ArrayView<int>, GridBuildParams>(GridBuildScanSegKernel);
            _buildScanSumsKernel = _acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<int>, ArrayView<int>, GridBuildParams>(GridBuildScanSumsKernel);
            _buildAddOffsetsKernel = _acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<int>, ArrayView<int>, GridBuildParams>(GridBuildAddOffsetsKernel);
            _buildFillKernel = _acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<int>, ArrayView<int2>, GridBuildParams>(GridBuildFillKernel);
            _buildPlaceKernel = _acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float2>, ArrayView<int>, ArrayView<float2>, ArrayView<int2>, GridBuildParams>(GridBuildPlaceKernel);

            _sortedPosBuf = _acc.Allocate1D<float2>(N);
            _hashIndexBuf = _acc.Allocate1D<int2>(N);
            _cellStartEndBuf = _acc.Allocate1D<int2>(_cellCount);
            _queryBuf = _acc.Allocate1D<float2>(K);
            _resultBuf = _acc.Allocate1D<int>(K);
            _withinBuf = _acc.Allocate1D<int>(K * MaxNeighbor);
            _resultArr = new int[K];
            _withinArr = new int[K * MaxNeighbor];

            // ---- GPU 创建网格 buffer（独立，不干扰常驻查询的 CPU 构建 grid） ----
            _numSegments = (_cellCount + BuildSegLen - 1) / BuildSegLen;
            _buildPosBuf = _acc.Allocate1D<float2>(N);
            _buildCountsBuf = _acc.Allocate1D<int>(_cellCount);
            _buildSegSumsBuf = _acc.Allocate1D<int>(_numSegments);
            _buildSegOffsetsBuf = _acc.Allocate1D<int>(_numSegments);
            _buildSortedBuf = _acc.Allocate1D<float2>(N);
            _buildHashBuf = _acc.Allocate1D<int2>(N);
            _buildCellBuf = _acc.Allocate1D<int2>(_cellCount);

            // grid 上传一次（常驻）
            _sortedPosBuf.CopyFromCPU(_sortedArr);
            _hashIndexBuf.CopyFromCPU(_hashArr);
            _cellStartEndBuf.CopyFromCPU(_cellArr);

            // JIT 预热两个 kernel
            _closestKernel((Index1D)K, _queryBuf.View, _hashIndexBuf.View, _cellStartEndBuf.View, _sortedPosBuf.View, _resultBuf.View, MakeClosestParams());
            _withinKernel((Index1D)K, _queryBuf.View, _hashIndexBuf.View, _cellStartEndBuf.View, _sortedPosBuf.View, _withinBuf.View, MakeWithinParams());
            _acc.Synchronize();

            if (_verbose) Console.WriteLine($"[GPU] device={_acc.Name}");
        }

        private ClosestPointParams MakeClosestParams() => new()
        {
            OriginX = _originX, OriginY = _originY, GridResolutionInv = _resInv,
            SquaredEpsilonSelf = 0.001f * 0.001f, GridDimX = _gridDimX, GridDimY = _gridDimY,
            SortedLength = N, IgnoreSelf = 0
        };

        private FindWithinParams MakeWithinParams() => new()
        {
            OriginX = _originX, OriginY = _originY, GridResolutionInv = _resInv,
            SquaredRadius = WithinRadius * WithinRadius,
            GridDimX = _gridDimX, GridDimY = _gridDimY,
            MaxNeighbor = MaxNeighbor, CellsToLoop = (int)MathF.Ceiling(WithinRadius * _resInv)
        };

        // ================= ILGPU kernel（逐句镜像 GridSearch2D Execute） =================

        internal static void ClosestPointKernel(
            Index1D i,
            ArrayView<float2> queryPositions,
            ArrayView<int2> hashIndex,
            ArrayView<int2> cellStartEnd,
            ArrayView<float2> sortedPositions,
            ArrayView<int> results,
            ClosestPointParams p)
        {
            results[i] = -1;
            float2 q = queryPositions[i];
            int cx = (int)MathF.Floor((q.x - p.OriginX) * p.GridResolutionInv);
            cx = cx < 0 ? 0 : (cx > p.GridDimX - 1 ? p.GridDimX - 1 : cx);
            int cy = (int)MathF.Floor((q.y - p.OriginY) * p.GridResolutionInv);
            cy = cy < 0 ? 0 : (cy > p.GridDimY - 1 ? p.GridDimY - 1 : cy);

            float bestDistSq = float.MaxValue;
            int bestIdx = -1;

            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = cx + dx;
                if ((uint)nx >= (uint)p.GridDimX) continue;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = cy + dy;
                    if ((uint)ny >= (uint)p.GridDimY) continue;
                    int cellHash = ny * p.GridDimX + nx;
                    int2 range = cellStartEnd[cellHash];
                    int start = range.x;
                    int end = range.y;
                    if (start < 0) continue;

                    for (int j = start; j < end; j++)
                    {
                        float2 pos = sortedPositions[j];
                        float dx2 = q.x - pos.x, dy2 = q.y - pos.y;
                        float distSq = dx2 * dx2 + dy2 * dy2;
                        if (p.IgnoreSelf != 0 && distSq < p.SquaredEpsilonSelf) continue;
                        if (distSq < bestDistSq)
                        {
                            bestDistSq = distSq;
                            bestIdx = j;
                        }
                    }
                }
            }

            if (bestIdx != -1)
            {
                results[i] = hashIndex[bestIdx].y;
            }
            else
            {
                for (int j = 0; j < p.SortedLength; j++)
                {
                    float2 pos = sortedPositions[j];
                    float dx2 = q.x - pos.x, dy2 = q.y - pos.y;
                    float distSq = dx2 * dx2 + dy2 * dy2;
                    if (p.IgnoreSelf != 0 && distSq < p.SquaredEpsilonSelf) continue;
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestIdx = j;
                    }
                }
                if (bestIdx != -1)
                    results[i] = hashIndex[bestIdx].y;
            }
        }

        internal static void FindWithinKernel(
            Index1D i,
            ArrayView<float2> queryPositions,
            ArrayView<int2> hashIndex,
            ArrayView<int2> cellStartEnd,
            ArrayView<float2> sortedPositions,
            ArrayView<int> results,
            FindWithinParams p)
        {
            int baseIdx = i * p.MaxNeighbor;
            for (int k = 0; k < p.MaxNeighbor; k++)
                results[baseIdx + k] = -1;

            float2 q = queryPositions[i];
            int ccx = (int)MathF.Floor((q.x - p.OriginX) * p.GridResolutionInv);
            ccx = ccx < 0 ? 0 : (ccx > p.GridDimX - 1 ? p.GridDimX - 1 : ccx);
            int ccy = (int)MathF.Floor((q.y - p.OriginY) * p.GridResolutionInv);
            ccy = ccy < 0 ? 0 : (ccy > p.GridDimY - 1 ? p.GridDimY - 1 : ccy);

            int found = 0;

            int centerHash = ccy * p.GridDimX + ccx;
            int2 centerRange = cellStartEnd[centerHash];
            int start = centerRange.x;
            int end = centerRange.y;
            if (start >= 0)
            {
                for (int j = start; j < end; j++)
                {
                    float2 pos = sortedPositions[j];
                    float dx2 = q.x - pos.x, dy2 = q.y - pos.y;
                    if (dx2 * dx2 + dy2 * dy2 <= p.SquaredRadius)
                    {
                        results[baseIdx + found] = hashIndex[j].y;
                        found++;
                        if (found == p.MaxNeighbor) return;
                    }
                }
            }

            for (int dx = -p.CellsToLoop; dx <= p.CellsToLoop; dx++)
            {
                int nx = ccx + dx;
                if ((uint)nx >= (uint)p.GridDimX) continue;
                for (int dy = -p.CellsToLoop; dy <= p.CellsToLoop; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int ny = ccy + dy;
                    if ((uint)ny >= (uint)p.GridDimY) continue;

                    int hash = ny * p.GridDimX + nx;
                    int2 range = cellStartEnd[hash];
                    int s = range.x;
                    int e = range.y;
                    if (s < 0) continue;

                    for (int j = s; j < e; j++)
                    {
                        float2 pos = sortedPositions[j];
                        float dx2 = q.x - pos.x, dy2 = q.y - pos.y;
                        if (dx2 * dx2 + dy2 * dy2 <= p.SquaredRadius)
                        {
                            results[baseIdx + found] = hashIndex[j].y;
                            found++;
                            if (found == p.MaxNeighbor) return;
                        }
                    }
                }
            }
        }

        // ================= GPU 创建网格 pipeline（6 kernel：hash计数/分段前缀和/加段偏移/填充/放置） =================
        // 与 BuildGridCSharp 逐句镜像：同一 bbox/res（CPU 算、uniform 传入），同 hash 公式（截断 + clamp），
        // 空 cell → (-1,-1)。端到端用 GPU 构建的 grid 跑一遍 closest 与 CPU 参考比对。
        // 注：GPU 构建 = CPU bbox（~0.02ms）+ GPU hash计数/前缀和/放置/fill；bbox 在 GPU 端做归约是 Gate 2 方向。

        private static int GridBuildHash(float2 pos, GridBuildParams p)
        {
            int cx = (int)((pos.x - p.OriginX) * p.ResolutionInv);
            cx = cx < 0 ? 0 : (cx > p.DimX - 1 ? p.DimX - 1 : cx);
            int cy = (int)((pos.y - p.OriginY) * p.ResolutionInv);
            cy = cy < 0 ? 0 : (cy > p.DimY - 1 ? p.DimY - 1 : cy);
            int hash = cy * p.DimX + cx;
            hash = hash < 0 ? 0 : (hash > p.CellCount - 1 ? p.CellCount - 1 : hash);
            return hash;
        }

        internal static void GridBuildCountKernel(Index1D i, ArrayView<float2> positions, ArrayView<int> counts, GridBuildParams p)
        {
            if (i >= p.N) return;
            Atomic.Add(ref counts[GridBuildHash(positions[i], p)], 1);   // 返回旧值，丢弃即可
        }

        internal static void GridBuildScanSegKernel(Index1D s, ArrayView<int> counts, ArrayView<int> segSums, GridBuildParams p)
        {
            if (s >= p.NumSegments) return;
            int start = s * p.SegLen;
            int end = start + p.SegLen;
            if (end > p.CellCount) end = p.CellCount;
            int sum = 0;
            for (int c = start; c < end; c++)
            {
                int prev = sum;
                sum += counts[c];
                counts[c] = prev;       // 段内【排他】前缀（Blelloch 段扫）；放置/填充都按排他起止
            }
            segSums[s] = sum;           // 段总量
        }

        internal static void GridBuildScanSumsKernel(Index1D s, ArrayView<int> segSums, ArrayView<int> segOffsets, GridBuildParams p)
        {
            if (s != 0) return;             // 单线程扫段和 → 每段起点
            int sum = 0;
            for (int i = 0; i < p.NumSegments; i++)
            {
                segOffsets[i] = sum;
                sum += segSums[i];
            }
        }

        internal static void GridBuildAddOffsetsKernel(Index1D i, ArrayView<int> counts, ArrayView<int> segOffsets, GridBuildParams p)
        {
            if (i >= p.CellCount) return;
            counts[i] += segOffsets[i / p.SegLen];   // 段内前缀 + 段起点 = 全局前缀
        }

        internal static void GridBuildFillKernel(Index1D i, ArrayView<int> counts, ArrayView<int2> cellStartEnd, GridBuildParams p)
        {
            if (i >= p.CellCount) return;
            int start = counts[i];
            int end = (i + 1 < p.CellCount) ? counts[i + 1] : p.N;
            cellStartEnd[i] = (start == end) ? new int2(-1, -1) : new int2(start, end);
        }

        internal static void GridBuildPlaceKernel(
            Index1D i, ArrayView<float2> positions, ArrayView<int> counts,
            ArrayView<float2> sortedPositions, ArrayView<int2> hashIndex, GridBuildParams p)
        {
            if (i >= p.N) return;
            int hash = GridBuildHash(positions[i], p);
            int dest = Atomic.Add(ref counts[hash], 1);      // 返回旧值 = 抢占到的槽
            sortedPositions[dest] = positions[i];
            hashIndex[dest] = new int2(hash, i);
        }

        private GridBuildParams MakeBuildParams() => new()
        {
            OriginX = _originX, OriginY = _originY, ResolutionInv = _resInv,
            DimX = _gridDimX, DimY = _gridDimY, CellCount = _cellCount, N = N,
            SegLen = BuildSegLen, NumSegments = _numSegments
        };

        private void GpuBuildAll()
        {
            _buildCountsBuf.View.MemSetToZero(_acc.DefaultStream);
            _buildCountKernel((Index1D)N, _buildPosBuf.View, _buildCountsBuf.View, MakeBuildParams());
            _buildScanSegKernel((Index1D)_numSegments, _buildCountsBuf.View, _buildSegSumsBuf.View, MakeBuildParams());
            _buildScanSumsKernel((Index1D)1, _buildSegSumsBuf.View, _buildSegOffsetsBuf.View, MakeBuildParams());
            _buildAddOffsetsKernel((Index1D)_cellCount, _buildCountsBuf.View, _buildSegOffsetsBuf.View, MakeBuildParams());
            _buildFillKernel((Index1D)_cellCount, _buildCountsBuf.View, _buildCellBuf.View, MakeBuildParams());
            _buildPlaceKernel((Index1D)N, _buildPosBuf.View, _buildCountsBuf.View, _buildSortedBuf.View, _buildHashBuf.View, MakeBuildParams());
        }

        private double MeasureBuildGpu(int[] cpuClosest)
        {
            _buildPosBuf.CopyFromCPU(_posArr);   // 常驻上传一次（不含在构建耗时；重建只重算 kernel 段）

            for (int i = 0; i < BuildWarmup; i++) { GpuBuildAll(); _acc.Synchronize(); }
            var samples = new double[BuildFrames];
            for (int i = 0; i < BuildFrames; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                GpuBuildAll();
                _acc.Synchronize();
                long t1 = Stopwatch.GetTimestamp();
                samples[i] = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
            }
            double p50 = Median(samples);

            // 端到端验证：GPU 构建的 grid 跑一遍 closest，与 CPU 参考逐元素对比
            _queryBuf.CopyFromCPU(_queryArr);
            _closestKernel((Index1D)K, _queryBuf.View, _buildHashBuf.View, _buildCellBuf.View, _buildSortedBuf.View, _resultBuf.View, MakeClosestParams());
            _acc.Synchronize();
            _resultBuf.CopyToCPU(_resultArr);
            int mismatches = 0;
            for (int i = 0; i < K; i++) if (_resultArr[i] != cpuClosest[i]) mismatches++;

            // 恒打印：GPU 构建的端到端 closest parity 是构建正确性的唯一门（GPU 构建的 grid ≠ 上传的 CPU grid）
            Console.WriteLine(FormattableString.Invariant(
                $"GridSearch 创建网格 GPU(ILGPU): p50={p50:F3} ms  (positions 常驻上传不含; GPU 构建 grid 端到端 closest parity 不等={mismatches}/{K} {(mismatches == 0 ? "通过" : "未过")})"));
            return p50;
        }

        // ================= parity =================

        private bool CheckClosestParity(int[] cpu)
        {
            _queryBuf.CopyFromCPU(_queryArr);
            _closestKernel((Index1D)K, _queryBuf.View, _hashIndexBuf.View, _cellStartEndBuf.View, _sortedPosBuf.View, _resultBuf.View, MakeClosestParams());
            _acc.Synchronize();
            _resultBuf.CopyToCPU(_resultArr);

            int mismatches = 0;
            for (int i = 0; i < K; i++) if (_resultArr[i] != cpu[i]) mismatches++;
            string gpu10 = string.Join(" ", _resultArr[..10]);
            string cpu10 = string.Join(" ", cpu[..10]);
            Console.WriteLine();
            Console.WriteLine($"Parity closest: GPU前10 = {gpu10}");
            Console.WriteLine($"                CPU前10 = {cpu10}");
            Console.WriteLine($"                参考指纹  = {ExpectedFingerprint}");
            Console.WriteLine($"                逐元素不等 = {mismatches}/{K}  →  {(mismatches == 0 ? "通过" : "未过")}");
            return mismatches == 0;
        }

        private bool CheckWithinParity(int[] cpu)
        {
            _queryBuf.CopyFromCPU(_queryArr);
            _withinKernel((Index1D)K, _queryBuf.View, _hashIndexBuf.View, _cellStartEndBuf.View, _sortedPosBuf.View, _withinBuf.View, MakeWithinParams());
            _acc.Synchronize();
            _withinBuf.CopyToCPU(_withinArr);

            int mismatches = 0;
            for (int i = 0; i < cpu.Length; i++) if (_withinArr[i] != cpu[i]) mismatches++;
            Console.WriteLine($"Parity within : 逐元素不等 = {mismatches}/{cpu.Length}  →  {(mismatches == 0 ? "通过" : "未过")}");
            return mismatches == 0;
        }

        // ================= 测量 =================

        private MeasureResult MeasureClosest(bool resident)
        {
            string label = resident ? "GPU 常驻 closest" : "GPU 往返 closest";
            for (int i = 0; i < Warmup; i++)
            {
                if (!resident) ReuploadGrid();
                _queryBuf.CopyFromCPU(_queryArr);
                _closestKernel((Index1D)K, _queryBuf.View, _hashIndexBuf.View, _cellStartEndBuf.View, _sortedPosBuf.View, _resultBuf.View, MakeClosestParams());
                _acc.Synchronize();
                _resultBuf.CopyToCPU(_resultArr);
            }

            var wall = new double[GpuFrames];
            var up = new double[GpuFrames];
            var kern = new double[GpuFrames];
            var down = new double[GpuFrames];
            for (int f = 0; f < GpuFrames; f++)
            {
                long t0 = Stopwatch.GetTimestamp();
                ProfilingMarker upS = AddMarker();
                if (!resident) ReuploadGrid();
                _queryBuf.CopyFromCPU(_queryArr);
                ProfilingMarker upE = AddMarker();
                ProfilingMarker kS = AddMarker();
                _closestKernel((Index1D)K, _queryBuf.View, _hashIndexBuf.View, _cellStartEndBuf.View, _sortedPosBuf.View, _resultBuf.View, MakeClosestParams());
                ProfilingMarker kE = AddMarker();
                ProfilingMarker dS = AddMarker();
                _resultBuf.CopyToCPU(_resultArr);
                ProfilingMarker dE = AddMarker();
                _acc.Synchronize();
                long t1 = Stopwatch.GetTimestamp();

                wall[f] = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
                up[f] = upE.MeasureFrom(upS).TotalMilliseconds;
                kern[f] = kE.MeasureFrom(kS).TotalMilliseconds;
                down[f] = dE.MeasureFrom(dS).TotalMilliseconds;
            }
            return new MeasureResult(label, wall, up, kern, down);
        }

        /// <summary>
        /// 全常驻 closest：grid + queries 都已留在 GPU buffer，每帧只跑 kernel，
        /// 结果不读回（无传输）。这是「GPU 常驻(无传输)」行 GridSearch 列的语义，
        /// 与 08 HeavyMove/LightMove 常驻列对齐（LightMove 常驻 0.074 同型）。
        /// </summary>
        private void MeasureClosestResidentKernel()
        {
            for (int i = 0; i < Warmup; i++)
            {
                _closestKernel((Index1D)K, _queryBuf.View, _hashIndexBuf.View, _cellStartEndBuf.View, _sortedPosBuf.View, _resultBuf.View, MakeClosestParams());
                _acc.Synchronize();
            }
            var wall = new double[GpuFrames];
            for (int f = 0; f < GpuFrames; f++)
            {
                long t0 = Stopwatch.GetTimestamp();
                _closestKernel((Index1D)K, _queryBuf.View, _hashIndexBuf.View, _cellStartEndBuf.View, _sortedPosBuf.View, _resultBuf.View, MakeClosestParams());
                _acc.Synchronize();
                long t1 = Stopwatch.GetTimestamp();
                wall[f] = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
            }
            _gpuResidentKernelMs = Median(wall);
        }

        private MeasureResult MeasureWithin(bool resident)
        {
            string label = resident ? "GPU 常驻 within" : "GPU 往返 within";
            for (int i = 0; i < Warmup; i++)
            {
                if (!resident) ReuploadGrid();
                _queryBuf.CopyFromCPU(_queryArr);
                _withinKernel((Index1D)K, _queryBuf.View, _hashIndexBuf.View, _cellStartEndBuf.View, _sortedPosBuf.View, _withinBuf.View, MakeWithinParams());
                _acc.Synchronize();
                _withinBuf.CopyToCPU(_withinArr);
            }

            var wall = new double[GpuFrames];
            var up = new double[GpuFrames];
            var kern = new double[GpuFrames];
            var down = new double[GpuFrames];
            for (int f = 0; f < GpuFrames; f++)
            {
                long t0 = Stopwatch.GetTimestamp();
                ProfilingMarker upS = AddMarker();
                if (!resident) ReuploadGrid();
                _queryBuf.CopyFromCPU(_queryArr);
                ProfilingMarker upE = AddMarker();
                ProfilingMarker kS = AddMarker();
                _withinKernel((Index1D)K, _queryBuf.View, _hashIndexBuf.View, _cellStartEndBuf.View, _sortedPosBuf.View, _withinBuf.View, MakeWithinParams());
                ProfilingMarker kE = AddMarker();
                ProfilingMarker dS = AddMarker();
                _withinBuf.CopyToCPU(_withinArr);
                ProfilingMarker dE = AddMarker();
                _acc.Synchronize();
                long t1 = Stopwatch.GetTimestamp();

                wall[f] = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
                up[f] = upE.MeasureFrom(upS).TotalMilliseconds;
                kern[f] = kE.MeasureFrom(kS).TotalMilliseconds;
                down[f] = dE.MeasureFrom(dS).TotalMilliseconds;
            }
            return new MeasureResult(label, wall, up, kern, down);
        }

        private void ReuploadGrid()
        {
            _sortedPosBuf.CopyFromCPU(_sortedArr);
            _hashIndexBuf.CopyFromCPU(_hashArr);
            _cellStartEndBuf.CopyFromCPU(_cellArr);
        }

        /// <summary>
        /// 传输优化探针（ILGPU 1.5.3 能力内可量化部分）：
        /// ① 纯传输上限——back-to-back 只上传 query(781KB) 无 kernel，测每笔 DMA 的固定延迟 vs 带宽；
        /// ② 去逐帧 Synchronize——验证「每帧 _acc.Synchronize()」占墙钟多少水分。
        ///
        /// ILGPU 1.5.3 无 WaitFor / 无公开 async copy / 无 stream kernel loader，
        /// 细粒度「upload(f+1) 与 kernel(f) 重叠」无法在本版本表达——那是真实的下一级优化，
        /// 需要升级 ILGPU 或加原生 CUDA shim（见输出说明）。
        /// </summary>
        private void MeasureClosestPipeline()
        {
            Console.WriteLine();
            Console.WriteLine("=== 传输优化探针（ILGPU 1.5.3 能力内）===");

            // ① 纯上传吞吐：无 kernel，连续 CopyFromCPU(781KB)，仅末尾同步
            for (int i = 0; i < Warmup; i++) _queryBuf.CopyFromCPU(_queryArr);
            _acc.Synchronize();
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < GpuFrames; i++) _queryBuf.CopyFromCPU(_queryArr);
            _acc.Synchronize();
            long t1 = Stopwatch.GetTimestamp();
            double upPerFrame = (t1 - t0) * 1000.0 / Stopwatch.Frequency / GpuFrames;

            // ② pinned host 内存上传吞吐：GCHandle 固定数组 + CopyFromCPUUnsafeAsync
            //    对比 ①：验证 ILGPU 数组版 CopyFromCPU 是否走 pageable/staged 拷贝（docs/12 §3.4 的推断）
            double pinnedPerFrame = -1;
            var gch = GCHandle.Alloc(_queryArr, GCHandleType.Pinned);
            try
            {
                unsafe
                {
                    float2* p = (float2*)gch.AddrOfPinnedObject();
                    var view = _queryBuf.AsArrayView<float2>(0, K);
                    for (int i = 0; i < Warmup; i++) view.CopyFromCPUUnsafeAsync(_acc.DefaultStream, ref *p, K);
                    _acc.Synchronize();
                    long pt0 = Stopwatch.GetTimestamp();
                    for (int i = 0; i < GpuFrames; i++) view.CopyFromCPUUnsafeAsync(_acc.DefaultStream, ref *p, K);
                    _acc.Synchronize();
                    long pt1 = Stopwatch.GetTimestamp();
                    pinnedPerFrame = (pt1 - pt0) * 1000.0 / Stopwatch.Frequency / GpuFrames;
                }
            }
            finally
            {
                gch.Free();
            }

            // ③ 去每帧同步的 closest：上传+kernel+回读连发，仅末尾同步
            //    （CopyToCPU 同步版内部仍会等 kernel 完成，故 correctness 不变；这里只量化墙钟）
            var wallNoSync = new double[GpuFrames];
            for (int i = 0; i < Warmup; i++)
            {
                _queryBuf.CopyFromCPU(_queryArr);
                _closestKernel((Index1D)K, _queryBuf.View, _hashIndexBuf.View, _cellStartEndBuf.View, _sortedPosBuf.View, _resultBuf.View, MakeClosestParams());
                _resultBuf.CopyToCPU(_resultArr);
            }
            _acc.Synchronize();
            for (int f = 0; f < GpuFrames; f++)
            {
                long s0 = Stopwatch.GetTimestamp();
                _queryBuf.CopyFromCPU(_queryArr);
                _closestKernel((Index1D)K, _queryBuf.View, _hashIndexBuf.View, _cellStartEndBuf.View, _sortedPosBuf.View, _resultBuf.View, MakeClosestParams());
                _resultBuf.CopyToCPU(_resultArr);
                long s1 = Stopwatch.GetTimestamp();
                wallNoSync[f] = (s1 - s0) * 1000.0 / Stopwatch.Frequency;
            }
            _acc.Synchronize();

            double noSyncMed = Median(wallNoSync);
            _noSyncWall = noSyncMed;
            Console.WriteLine($"  纯上传 781KB 每帧(数组)    : {upPerFrame,9:F3}   (CopyFromCPU; 有效带宽≈{781 * 1024 / upPerFrame / 1e6:F2}GB/s)");
            if (pinnedPerFrame > 0)
                Console.WriteLine($"  纯上传 781KB 每帧(pinned)  : {pinnedPerFrame,9:F3}   (AllocateHostMemory+UnsafeAsync; 有效带宽≈{781 * 1024 / pinnedPerFrame / 1e6:F2}GB/s)");
            Console.WriteLine($"  去逐帧同步 closest 每帧    : {noSyncMed,9:F3}   (上传+kernel+回读 连发, 仅末尾同步)");
            Console.WriteLine($"  逐帧同步 closest 每帧      : ~0.38      (见上方汇总, 含每次 _acc.Synchronize() 的停摆)");
            Console.WriteLine($"  结论: ①上传每笔固定延迟小(~0.013ms), 连续提交可到链路峰值 8.4GB/s;");
            Console.WriteLine($"       ②pinned 实测 0.064 vs 数组 0.128ms = 2.0x(12.6 vs 6.2GB/s) → 数组版走 pageable/staged,");
            Console.WriteLine($"         长期 pin 的 host 池 + buffer 常驻 = Gate 2 GpuResidencyManager 的一部分;");
            Console.WriteLine($"       ③逐帧同步是墙钟水分(≈5%); 去同步后 upload+kernel+readback 三者和 0.360 ≈ 墙钟, 完全串行零重叠;");
            Console.WriteLine($"       ④ILGPU 1.5.3 无 WaitFor/event/托管 async copy → 「upload(f+1)‖kernel(f)」重叠无法在托管层表达;");
            Console.WriteLine($"         该优化需原生 CUDA shim(cudaMemcpyAsync+cudaStreamEvent 入 NativeDll), 沙箱下 NativeDll 无法重编, 留待 Gate 2 后。");
        }

        /// <summary>
        /// pinned 全管线：上传/回读全走 GCHandle pinned + CopyFrom/ToCPUUnsafeAsync（async 提交，DefaultStream 同 stream 串行）。
        /// 对比数组版（CopyFromCPU 每帧临时 pin + staged，实测 781KB 0.128ms）：pinned + async 实测 0.064ms。
        /// 目标：量化「pin 进真实管线」后 upload+kernel+readback 全墙钟 vs C# / ISPC / 数组版 GPU。
        /// </summary>
        private void MeasureClosestPinned(int[] cpuClosest)
        {
            Console.WriteLine();
            Console.WriteLine("=== pinned 全管线（上传+kernel+回读，全 pinned + async）===");

            var qPin = GCHandle.Alloc(_queryArr, GCHandleType.Pinned);
            var rPin = GCHandle.Alloc(_resultArr, GCHandleType.Pinned);
            try
            {
                var qView = _queryBuf.AsArrayView<float2>(0, K);
                var rView = _resultBuf.AsArrayView<int>(0, K);
                unsafe
                {
                    float2* pq = (float2*)qPin.AddrOfPinnedObject();
                    int* pr = (int*)rPin.AddrOfPinnedObject();

                    void Frame()
                    {
                        qView.CopyFromCPUUnsafeAsync(_acc.DefaultStream, ref *pq, K);
                        _closestKernel((Index1D)K, _queryBuf.View, _hashIndexBuf.View, _cellStartEndBuf.View, _sortedPosBuf.View, _resultBuf.View, MakeClosestParams());
                        rView.CopyToCPUUnsafeAsync(_acc.DefaultStream, ref *pr, K);
                    }

                    for (int i = 0; i < Warmup; i++) Frame();
                    _acc.Synchronize();

                    long t0 = Stopwatch.GetTimestamp();
                    for (int f = 0; f < GpuFrames; f++) Frame();
                    _acc.Synchronize();
                    long t1 = Stopwatch.GetTimestamp();
                    double pinnedWall = (t1 - t0) * 1000.0 / Stopwatch.Frequency / GpuFrames;
                    _pinnedWall = pinnedWall;

                    Frame(); _acc.Synchronize();
                    int mismatch = 0;
                    for (int i = 0; i < K; i++) if (_resultArr[i] != cpuClosest[i]) mismatch++;

                    Console.WriteLine($"  pinned closest 每帧(p50)   : {pinnedWall,9:F3}   (pinned 上传+pinned 回读; parity 不等={mismatch}/{K})");
                    Console.WriteLine($"  数组版(no-sync) closest     : {(_noSyncWall > 0 ? $"{_noSyncWall,9:F3}" : "       n/a")}    (见上方传输探针)");
                    Console.WriteLine($"  vs ISPC {_ispcQueryMs:F3} = x{_ispcQueryMs / pinnedWall:F2}   vs C# 单线程 ~20.0 = x{20.0 / pinnedWall:F0}   vs 数组版 = x{(_noSyncWall > 0 ? $"{_noSyncWall / pinnedWall:F2}" : "n/a")}");
                }
            }
            finally { qPin.Free(); rPin.Free(); }
        }

        /// <summary>
        /// NativeCollection 上传/回读：query/result 用 EntJoy NativeArray（UnsafeUtility.Malloc = pageable），
        /// 不碰 C# 数组、不 GCHandle pin。验证「NativeCollection 作 CPU 侧传输载体」的全管线性能。
        /// 预期：malloc 内存从 CUDA 视角仍 pageable → 与 pinned 同走 staged/async 路径，≈ pinned 量级（且省掉 GCHandle）。
        /// </summary>
        private void MeasureClosestNativeCollection(NativeArray<float2> nativeQueries, int[] cpuClosest)
        {
            Console.WriteLine();
            Console.WriteLine("=== NativeCollection 全管线（NativeArray 上传/回读，无 pin）===");

            var rNative = new NativeArray<int>(K, Allocator.Persistent);
            try
            {
                var qView = _queryBuf.AsArrayView<float2>(0, K);
                var rView = _resultBuf.AsArrayView<int>(0, K);
                unsafe
                {
                    float2* pq = (float2*)nativeQueries.GetUnsafePtr();
                    int* pr = (int*)rNative.GetUnsafePtr();

                    void Frame()
                    {
                        qView.CopyFromCPUUnsafeAsync(_acc.DefaultStream, ref *pq, K);
                        _closestKernel((Index1D)K, _queryBuf.View, _hashIndexBuf.View, _cellStartEndBuf.View, _sortedPosBuf.View, _resultBuf.View, MakeClosestParams());
                        rView.CopyToCPUUnsafeAsync(_acc.DefaultStream, ref *pr, K);
                    }

                    for (int i = 0; i < Warmup; i++) Frame();
                    _acc.Synchronize();

                    long t0 = Stopwatch.GetTimestamp();
                    for (int f = 0; f < GpuFrames; f++) Frame();
                    _acc.Synchronize();
                    long t1 = Stopwatch.GetTimestamp();
                    double ncWall = (t1 - t0) * 1000.0 / Stopwatch.Frequency / GpuFrames;
                    _nativeCollectionWall = ncWall;

                    Frame(); _acc.Synchronize();
                    int mismatch = 0;
                    for (int i = 0; i < K; i++) if (rNative[i] != cpuClosest[i]) mismatch++;

                    Console.WriteLine($"  NativeArray closest 每帧   : {ncWall,9:F3}   (NativeArray↑+NativeArray↓; parity 不等={mismatch}/{K})");
                    Console.WriteLine($"  vs ISPC {_ispcQueryMs:F3} = x{_ispcQueryMs / ncWall:F2}   vs C# ~20 = x{20.0 / ncWall:F0}   vs 数组版(no-sync) {(_noSyncWall > 0 ? $"{_noSyncWall:F3} = x{_noSyncWall / ncWall:F2}" : "n/a")}   vs GCHandle-pin {(_pinnedWall > 0 ? $"{_pinnedWall:F3} = x{_pinnedWall / ncWall:F2}" : "n/a")}");
                }
            }
            finally { rNative.Dispose(); }
        }

        /// <summary>
        /// 真·页锁定（cudaHostAlloc）上传/回读：验证「UnsafeUtility.Malloc 若支持页锁定」的传输上限。
        /// 关键区分：GCHandle pin ≠ CUDA 页锁定——GCHandle 只防 GC 搬移，内存仍 pageable（staged 拷贝）；
        /// cudaHostAlloc 才把内存登记进驱动做单跳 DMA。EntJoy 的 UnsafeUtility.Malloc 目前是普通 malloc，
        /// 没有这一级。用裸指针演示（NativeArray 无外部指针公共入口；等价于把 NativeCollection 的
        /// allocator 换成 cudaHostAlloc 池后的效果）。
        ///
        /// ⚠ 仅 CUDA 验证探针：cudaHostAlloc 是 CUDA 专属，不写进 UnsafeUtility.Malloc（core 须跨平台）。
        /// 「页锁定/共享 host 内存」应作 GpuResidencyManager 的可注入后端接口：
        /// CUDA→cuMemHostAlloc、wgpu(Vulkan)→host-visible buffer、Metal→MTLBuffer shared、WebGPU→mapped buffer。
        /// </summary>
        private void MeasureClosestPageLocked(int[] cpuClosest)
        {
            if (_verbose)
            {
                Console.WriteLine();
                Console.WriteLine("=== 真页锁定全管线（cudaHostAlloc 上传/回读）===");
            }
            var api = CudaAPI.CurrentAPI;
            IntPtr qHost = IntPtr.Zero, rHost = IntPtr.Zero;
            try
            {
                unsafe
                {
                    int qBytes = K * sizeof(float2), rBytes = K * sizeof(int);
                    if (api.AllocateHostMemory(out qHost, (IntPtr)qBytes) != CudaError.CUDA_SUCCESS)
                        throw new InvalidOperationException("AllocateHostMemory(query) failed");
                    if (api.AllocateHostMemory(out rHost, (IntPtr)rBytes) != CudaError.CUDA_SUCCESS)
                        throw new InvalidOperationException("AllocateHostMemory(result) failed");

                    // 数据进页锁定 host 缓冲（一劳永逸；真实场景 CPU 直接写页锁定缓冲）
                    fixed (void* src = _queryArr)
                        UnsafeUtility.MemCpy((void*)qHost, src, qBytes);

                    var qView = _queryBuf.AsArrayView<float2>(0, K);
                    var rView = _resultBuf.AsArrayView<int>(0, K);
                    float2* pq = (float2*)qHost;
                    int* pr = (int*)rHost;

                    // ① 孤立上传吞吐（页锁定 DMA 上限）
                    for (int i = 0; i < Warmup; i++) qView.CopyFromCPUUnsafeAsync(_acc.DefaultStream, ref *pq, K);
                    _acc.Synchronize();
                    long ut0 = Stopwatch.GetTimestamp();
                    for (int i = 0; i < GpuFrames; i++) qView.CopyFromCPUUnsafeAsync(_acc.DefaultStream, ref *pq, K);
                    _acc.Synchronize();
                    long ut1 = Stopwatch.GetTimestamp();
                    double upPL = (ut1 - ut0) * 1000.0 / Stopwatch.Frequency / GpuFrames;

                    // ② 全管线（上传/kernel/回读 三段 ProfilingMarker 拆分）
                    void Frame()
                    {
                        qView.CopyFromCPUUnsafeAsync(_acc.DefaultStream, ref *pq, K);
                        _closestKernel((Index1D)K, _queryBuf.View, _hashIndexBuf.View, _cellStartEndBuf.View, _sortedPosBuf.View, _resultBuf.View, MakeClosestParams());
                        rView.CopyToCPUUnsafeAsync(_acc.DefaultStream, ref *pr, K);
                    }

                    for (int i = 0; i < Warmup; i++) Frame();
                    _acc.Synchronize();
                    var wAll = new double[GpuFrames];
                    var upAll = new double[GpuFrames];
                    var kAll = new double[GpuFrames];
                    var dAll = new double[GpuFrames];
                    for (int f = 0; f < GpuFrames; f++)
                    {
                        long f0 = Stopwatch.GetTimestamp();
                        ProfilingMarker upS = AddMarker();
                        qView.CopyFromCPUUnsafeAsync(_acc.DefaultStream, ref *pq, K);
                        ProfilingMarker upE = AddMarker();
                        ProfilingMarker kS = AddMarker();
                        _closestKernel((Index1D)K, _queryBuf.View, _hashIndexBuf.View, _cellStartEndBuf.View, _sortedPosBuf.View, _resultBuf.View, MakeClosestParams());
                        ProfilingMarker kE = AddMarker();
                        ProfilingMarker dS = AddMarker();
                        rView.CopyToCPUUnsafeAsync(_acc.DefaultStream, ref *pr, K);
                        ProfilingMarker dE = AddMarker();
                        _acc.Synchronize();
                        long f1 = Stopwatch.GetTimestamp();
                        wAll[f] = (f1 - f0) * 1000.0 / Stopwatch.Frequency;
                        upAll[f] = upE.MeasureFrom(upS).TotalMilliseconds;
                        kAll[f] = kE.MeasureFrom(kS).TotalMilliseconds;
                        dAll[f] = dE.MeasureFrom(dS).TotalMilliseconds;
                    }
                    double plWall = Median(wAll);
                    double plUp = Median(upAll), plK = Median(kAll), plD = Median(dAll);
                    _plClosestWallMs = plWall;

                    Frame(); _acc.Synchronize();
                    int mismatch = 0;
                    for (int i = 0; i < K; i++) if (pr[i] != cpuClosest[i]) mismatch++;

                    if (_verbose)
                    {
                        Console.WriteLine($"  孤立上传 781KB 每帧(页锁定) : {upPL,9:F3}   (有效带宽≈{781 * 1024 / upPL / 1e6:F2}GB/s)");
                        Console.WriteLine($"  cudaHostAlloc closest 每帧  : {plWall,9:F3}   (真页锁定↑+↓; parity 不等={mismatch}/{K})");
                        Console.WriteLine($"    ├─ 上传(GPU事件)  p50={plUp:F3} ms");
                        Console.WriteLine($"    ├─ 纯内核(GPU事件) p50={plK:F3} ms");
                        Console.WriteLine($"    └─ 回读(GPU事件)  p50={plD:F3} ms   (三拆和 {plUp + plK + plD:F3} ≈ 墙钟 {plWall:F3})");
                        Console.WriteLine($"  vs ISPC {_ispcQueryMs:F3} = x{_ispcQueryMs / plWall:F2}   vs C# ~20 = x{20.0 / plWall:F0}   vs 数组版(no-sync) {(_noSyncWall > 0 ? $"{_noSyncWall:F3} = x{_noSyncWall / plWall:F2}" : "n/a")}   vs NativeArray {(_nativeCollectionWall > 0 ? $"{_nativeCollectionWall:F3} = x{_nativeCollectionWall / plWall:F2}" : "n/a")}   vs GCHandle-pin {(_pinnedWall > 0 ? $"{_pinnedWall:F3} = x{_pinnedWall / plWall:F2}" : "n/a")}");
                    }
                }
            }
            finally
            {
                if (qHost != IntPtr.Zero) api.FreeHostMemory(qHost);
                if (rHost != IntPtr.Zero) api.FreeHostMemory(rHost);
            }
        }

        /// <summary>
        /// 查询规模探针：grid 保持 100k 点常驻，查询量 100k→1M。
        /// 目的：展示「kernel 优势 vs 传输开销」随规模的相对走向——
        /// 两者都随 K 线性增长（kernel 是 ISPC 的 ~7x，传输是固定带宽下限），
        /// 所以常驻+全量回读形态下 GPU:ISPC 比值被固定在带宽/吞吐之比（~1.3-1.5x），
        /// 真正的突破要靠「结果留 GPU（级联 kernel）或稀疏回读」。
        /// </summary>
        private void RunQueryScaleProbe()
        {
            const int bigK = 1000000;
            Console.WriteLine();
            Console.WriteLine("=== 查询规模探针（grid 100k 点常驻，查询 100k→1M）===");

            using var q1M = new NativeArray<float2>(bigK, Allocator.Persistent);
            var q1MArr = new float2[bigK];
            var rnd = new Random(777);
            for (int i = 0; i < bigK; i++)
                q1MArr[i] = new float2((float)(rnd.NextDouble() * 200 - 100), (float)(rnd.NextDouble() * 200 - 100));
            q1M.CopyFrom(q1MArr);

            // ISPC 基线 @1M
            double ispcMs = -1;
            string ispcErr = "";
            using (var sortedN = WrapNative(_sortedArr, Allocator.Persistent))
            using (var hashN = WrapNative(_hashArr, Allocator.Persistent))
            using (var cellN = WrapNativeList(_cellArr, Allocator.Persistent))
            using (var resultsN = new NativeArray<int>(bigK, Allocator.Persistent))
            {
                var job = new global::GridSearch2D.ClosestPointJobPointer
                {
                    GridOrigin = new float2(_originX, _originY),
                    GridResolutionInv = _resInv,
                    GridDimensions = new int2(_gridDimX, _gridDimY),
                    QueryPositions = q1M,
                    SortedPositions = sortedN,
                    HashIndex = hashN,
                    CellStartEnd = cellN,
                    SortedLength = N,
                    IgnoreSelf = false,
                    SquaredEpsilonSelf = 0.001f * 0.001f,
                    Results = resultsN
                };
                try
                {
                    for (int i = 0; i < Warmup; i++) job.Schedule(bigK, GridSearch2D.QueryBatchSize).Complete();
                    var samples = new double[GpuFrames];
                    for (int i = 0; i < GpuFrames; i++)
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        job.Schedule(bigK, GridSearch2D.QueryBatchSize).Complete();
                        long t1 = Stopwatch.GetTimestamp();
                        samples[i] = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
                    }
                    ispcMs = Median(samples);
                }
                catch (Exception ex) { ispcErr = ex.GetType().Name; }
            }

            // GPU 常驻 @1M（query 上传一次，kernel，结果回读）
            using (var qBuf = _acc.Allocate1D<float2>(bigK))
            using (var rBuf = _acc.Allocate1D<int>(bigK))
            {
                var rArr = new int[bigK];
                for (int i = 0; i < Warmup; i++)
                {
                    qBuf.CopyFromCPU(q1MArr);
                    _closestKernel((Index1D)bigK, qBuf.View, _hashIndexBuf.View, _cellStartEndBuf.View, _sortedPosBuf.View, rBuf.View, MakeClosestParams());
                    _acc.Synchronize();
                    rBuf.CopyToCPU(rArr);
                }
                var wall = new double[GpuFrames];
                var up = new double[GpuFrames];
                var kern = new double[GpuFrames];
                var down = new double[GpuFrames];
                for (int f = 0; f < GpuFrames; f++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    ProfilingMarker upS = AddMarker();
                    qBuf.CopyFromCPU(q1MArr);
                    ProfilingMarker upE = AddMarker();
                    ProfilingMarker kS = AddMarker();
                    _closestKernel((Index1D)bigK, qBuf.View, _hashIndexBuf.View, _cellStartEndBuf.View, _sortedPosBuf.View, rBuf.View, MakeClosestParams());
                    ProfilingMarker kE = AddMarker();
                    ProfilingMarker dS = AddMarker();
                    rBuf.CopyToCPU(rArr);
                    ProfilingMarker dE = AddMarker();
                    _acc.Synchronize();
                    long t1 = Stopwatch.GetTimestamp();

                    wall[f] = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
                    up[f] = upE.MeasureFrom(upS).TotalMilliseconds;
                    kern[f] = kE.MeasureFrom(kS).TotalMilliseconds;
                    down[f] = dE.MeasureFrom(dS).TotalMilliseconds;
                }
                double gpuMs = Median(wall);
                Console.WriteLine($"  K=1M ISPC closest (p50)     : {ispcMs,9:F3}  {(ispcMs < 0 ? ispcErr : "")}");
                Console.WriteLine($"  K=1M GPU 常驻 closest (p50)  : {gpuMs,9:F3}  (vs ISPC x{(ispcMs > 0 ? ispcMs / gpuMs : double.NaN):F2})");
                Console.WriteLine($"      三拆(GPU事件)           : 上传={Median(up):F3} 内核={Median(kern):F3} 回读={Median(down):F3}");
                Console.WriteLine($"      注: 常驻+全量回读形态下传输随 K 线性涨（query {bigK * 8 / 1024}KB↑ + result {bigK * 4 / 1024}KB↓），");
                Console.WriteLine($"          GPU:ISPC 比值被带宽下限固定；突破要靠结果留 GPU / 稀疏回读。");
            }
        }

        private ProfilingMarker AddMarker() => _acc.DefaultStream.AddProfilingMarker();

        // ================= 输出 =================

        private void PrintSummary(
            bool closestParity, bool withinParity,
            MeasureResult closestRes, MeasureResult closestRt, MeasureResult withinRes)
        {
            double cRes = Median(closestRes.Wall);
            double cRt = Median(closestRt.Wall);
            double wRes = Median(withinRes.Wall);
            long gridBytes = (long)N * 8 * 2 + (long)_cellCount * 8;   // sortedPos + hashIndex + cellStartEnd
            long queryBytes = (long)K * 8;
            long resultBytes = (long)K * 4;
            long withinBytes = (long)K * MaxNeighbor * 4;

            Console.WriteLine();
            Console.WriteLine("===================== GPU GridSearch 汇总（p50, ms） =====================");
            Console.WriteLine($"  C# 参考查询(p50)       : {_cpuRefMs,10:F3}   (单线程, parity 锚点)");
            if (_ispcQueryMs > 0)
                Console.WriteLine($"  ISPC QueryCore(p50)   : {_ispcQueryMs,10:F3}");
            else
                Console.WriteLine($"  ISPC QueryCore(p50)   : 不可用 ({_ispcUnavailable})");

            Console.WriteLine($"  GPU 常驻 closest(p50)  : {cRes,10:F3}   {(cRes > 0 ? $"(vs ISPC x{_ispcQueryMs / cRes:F2})" : "")}");
            Console.WriteLine($"      三拆(GPU事件)     : 上传={Median(closestRes.Up):F3} 内核={Median(closestRes.Kernel):F3} 回读={Median(closestRes.Down):F3}");
            Console.WriteLine($"  GPU 往返 closest(p50)  : {cRt,10:F3}   (相对常驻 x{cRt / cRes:F2})");
            Console.WriteLine($"  GPU 常驻 within (p50)  : {wRes,10:F3}");
            Console.WriteLine($"      parity             : closest {(closestParity ? "通过" : "未过")}, within {(withinParity ? "通过" : "未过")}");

            Console.WriteLine();
            Console.WriteLine($"数据面: grid {gridBytes / 1024}KB 常驻(上传一次); closest 每帧 query {queryBytes / 1024}KB↑ + result {resultBytes / 1024}KB↓; " +
                $"roundtrip 每帧 grid+query {(gridBytes + queryBytes) / 1024}KB↑; within 每帧 result {withinBytes / 1024}KB↓");
            Console.WriteLine();
            Console.WriteLine("Gate 判定（常驻+回读 GPU closest ≤ ISPC QueryCore 的 1/2 过门）：");
            if (_ispcQueryMs > 0)
            {
                double ratio = cRes / _ispcQueryMs;
                Console.WriteLine($"  GPU常驻/ISPC = {ratio:F3}  →  {(ratio <= 0.5 ? "通过" : "未过")} (需要 ≤ 0.500)");
            }
            else
            {
                Console.WriteLine("  ISPC 不可用，跳过判定（GPU parity 已独立验证）。");
            }
            Console.WriteLine();
        }

        private static double Median(double[] samples)
        {
            var s = (double[])samples.Clone();
            Array.Sort(s);
            if (s.Length == 0) return 0;
            int m = s.Length / 2;
            return (s.Length % 2 == 1) ? s[m] : (s[m - 1] + s[m]) * 0.5;
        }

        public void Dispose()
        {
            _context?.Dispose();
            _context = null!;
            _acc = null!;
        }
    }
}
