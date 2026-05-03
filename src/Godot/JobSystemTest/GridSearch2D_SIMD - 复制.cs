//using EntJoy.Collections;
//using EntJoy.Mathematics;
//using Godot;
//using System;
//using System.Numerics;
//using System.Runtime.CompilerServices;
//using System.Runtime.Intrinsics;
//using System.Runtime.Intrinsics.X86;
//using System.Threading;
//using Exception = System.Exception;
//using Vector3 = System.Numerics.Vector3;

///// <summary>
///// 二维网格空间索引（构建使用 NativeArray，查询保留 SIMD 加速）
///// </summary>
//public class GridSearch2D_SIMD : IDisposable
//{
//    private const int MAX_GRID_SIZE = 1024;
//    private int _targetGridSize;
//    private float _resolution;

//    // 核心数据（SoA 布局）
//    private NativeArray<float2> _positions;
//    private NativeArray<float> _sortedX;
//    private NativeArray<float> _sortedY;
//    private NativeArray<int2> _hashIndex;
//    private NativeList<int2> _cellStartEnd;
//    private NativeArray<float2> _minMaxPositions;
//    private NativeArray<int2> _gridDimensions;
//    private NativeArray<float> _gridResolution;

//    // 公共属性
//    public float2 GridMin => _minMaxPositions[0];
//    public float2 GridMax => _minMaxPositions[1];
//    public int2 GridDimensions => _gridDimensions[0];
//    public float Resolution => _gridResolution[0];

//    public GridSearch2D_SIMD(float resolution = -1f, int targetGrid = 32)
//    {
//        if (resolution > 0f)
//        {
//            _resolution = resolution;
//            _targetGridSize = 0;
//        }
//        else if (targetGrid > 0)
//        {
//            _targetGridSize = targetGrid;
//        }
//        else
//        {
//            throw new Exception("必须指定 resolution (>0) 或 targetGrid (>0)");
//        }
//    }

//    // ---------- 初始化接口 ----------
//    public JobHandle InitializeGrid(Vector3[] positions)
//    {
//        Dispose();
//        _gridResolution = new NativeArray<float>(1, Allocator.Persistent) { [0] = _resolution };
//        _positions = new NativeArray<float2>(positions.Length, Allocator.Persistent);
//        for (int i = 0; i < positions.Length; i++)
//            _positions[i] = new float2(positions[i].X, positions[i].Y);
//        _hashIndex = new NativeArray<int2>(positions.Length, Allocator.Persistent);
//        _sortedX = new NativeArray<float>(positions.Length, Allocator.Persistent);
//        _sortedY = new NativeArray<float>(positions.Length, Allocator.Persistent);
//        _cellStartEnd = new NativeList<int2>(Allocator.Persistent);
//        return InitializeGridInternal();
//    }

//    public JobHandle InitializeGrid(NativeArray<float2> positions)
//    {
//        Dispose();
//        _gridResolution = new NativeArray<float>(1, Allocator.Persistent) { [0] = _resolution };
//        _positions = new NativeArray<float2>(positions.Length, Allocator.Persistent);
//        _hashIndex = new NativeArray<int2>(positions.Length, Allocator.Persistent);
//        _sortedX = new NativeArray<float>(positions.Length, Allocator.Persistent);
//        _sortedY = new NativeArray<float>(positions.Length, Allocator.Persistent);
//        _cellStartEnd = new NativeList<int2>(Allocator.Persistent);
//        positions.CopyTo(_positions);
//        return InitializeGridInternal();
//    }

//    // ---------- 查询接口 ----------
//    public int[] SearchClosestPoint(Vector3[] queryPoints, bool ignoreSelf = false, float epsilon = 0.001f)
//    {
//        NativeArray<float2> qPoints = new NativeArray<float2>(queryPoints.Length, Allocator.TempJob);
//        for (int i = 0; i < queryPoints.Length; i++)
//            qPoints[i] = new float2(queryPoints[i].X, queryPoints[i].Y);
//        NativeArray<int> results = new NativeArray<int>(queryPoints.Length, Allocator.TempJob);

//        var job = new ClosestPointJob
//        {
//            GridOrigin = _minMaxPositions[0],
//            GridResolutionInv = 1.0f / _gridResolution[0],
//            GridDimensions = _gridDimensions[0],
//            QueryPositions = qPoints,
//            SortedX = _sortedX,
//            SortedY = _sortedY,
//            HashIndex = _hashIndex,
//            CellStartEnd = _cellStartEnd.AsArray(),
//            Results = results,
//            IgnoreSelf = ignoreSelf,
//            SquaredEpsilonSelf = epsilon * epsilon
//        };

//        unsafe
//        {
//            job.SortedXPtr = (float*)_sortedX.GetUnsafePtr();
//            job.SortedYPtr = (float*)_sortedY.GetUnsafePtr();
//            job.HashIndexPtr = (int2*)_hashIndex.GetUnsafePtr();
//            job.CellStartEndPtr = (int2*)_cellStartEnd.GetUnsafePtr();
//        }

//        var handle = job.Schedule(qPoints.Length, 0);
//        handle.Complete();

//        int[] res = new int[results.Length];
//        results.CopyTo(res);
//        results.Dispose();
//        qPoints.Dispose();
//        return res;
//    }

//    public NativeArray<int> SearchClosestPoint(NativeArray<float2> queryPoints, bool ignoreSelf = false, float epsilon = 0.001f)
//    {
//        NativeArray<int> results = new NativeArray<int>(queryPoints.Length, Allocator.TempJob);

//        var job = new ClosestPointJob
//        {
//            GridOrigin = _minMaxPositions[0],
//            GridResolutionInv = 1.0f / _gridResolution[0],
//            GridDimensions = _gridDimensions[0],
//            QueryPositions = queryPoints,
//            SortedX = _sortedX,
//            SortedY = _sortedY,
//            HashIndex = _hashIndex,
//            CellStartEnd = _cellStartEnd.AsArray(),
//            Results = results,
//            IgnoreSelf = ignoreSelf,
//            SquaredEpsilonSelf = epsilon * epsilon
//        };

//        unsafe
//        {
//            job.SortedXPtr = (float*)_sortedX.GetUnsafePtr();
//            job.SortedYPtr = (float*)_sortedY.GetUnsafePtr();
//            job.HashIndexPtr = (int2*)_hashIndex.GetUnsafePtr();
//            job.CellStartEndPtr = (int2*)_cellStartEnd.GetUnsafePtr();
//        }

//        var handle = job.Schedule(queryPoints.Length, 0);
//        handle.Complete();
//        return results;
//    }

//    public int[] SearchWithin(Vector3[] queryPoints, float radius, int maxNeighborPerQuery)
//    {
//        NativeArray<float2> qPoints = new NativeArray<float2>(queryPoints.Length, Allocator.TempJob);
//        for (int i = 0; i < queryPoints.Length; i++)
//            qPoints[i] = new float2(queryPoints[i].X, queryPoints[i].Y);
//        NativeArray<int> results = new NativeArray<int>(queryPoints.Length * maxNeighborPerQuery, Allocator.TempJob);
//        int cellsToLoop = (int)math.ceil(radius / _gridResolution[0]);

//        var job = new FindWithinJob
//        {
//            SquaredRadius = radius * radius,
//            MaxNeighbor = maxNeighborPerQuery,
//            CellsToLoop = cellsToLoop,
//            GridOrigin = _minMaxPositions[0],
//            GridResolutionInv = 1.0f / _gridResolution[0],
//            GridDimensions = _gridDimensions[0],
//            QueryPositions = qPoints,
//            SortedX = _sortedX,
//            SortedY = _sortedY,
//            HashIndex = _hashIndex,
//            CellStartEnd = _cellStartEnd.AsArray(),
//            Results = results
//        };
//        var handle = job.Schedule(qPoints.Length, 0);
//        handle.Complete();

//        int[] res = new int[results.Length];
//        results.CopyTo(res);
//        results.Dispose();
//        qPoints.Dispose();
//        return res;
//    }

//    public NativeArray<int> SearchWithin(NativeArray<float2> queryPoints, float radius, int maxNeighborPerQuery)
//    {
//        NativeArray<int> results = new NativeArray<int>(queryPoints.Length * maxNeighborPerQuery, Allocator.TempJob);
//        int cellsToLoop = (int)math.ceil(radius / _gridResolution[0]);
//        var job = new FindWithinJob
//        {
//            SquaredRadius = radius * radius,
//            MaxNeighbor = maxNeighborPerQuery,
//            CellsToLoop = cellsToLoop,
//            GridOrigin = _minMaxPositions[0],
//            GridResolutionInv = 1.0f / _gridResolution[0],
//            GridDimensions = _gridDimensions[0],
//            QueryPositions = queryPoints,
//            SortedX = _sortedX,
//            SortedY = _sortedY,
//            HashIndex = _hashIndex,
//            CellStartEnd = _cellStartEnd.AsArray(),
//            Results = results
//        };
//        var handle = job.Schedule(queryPoints.Length, 0);
//        handle.Complete();
//        return results;
//    }

//    public void Dispose()
//    {
//        if (_positions.IsCreated) _positions.Dispose();
//        if (_hashIndex.IsCreated) _hashIndex.Dispose();
//        if (_cellStartEnd.IsCreated) _cellStartEnd.Dispose();
//        if (_sortedX.IsCreated) _sortedX.Dispose();
//        if (_sortedY.IsCreated) _sortedY.Dispose();
//        if (_minMaxPositions.IsCreated) _minMaxPositions.Dispose();
//        if (_gridDimensions.IsCreated) _gridDimensions.Dispose();
//        if (_gridResolution.IsCreated) _gridResolution.Dispose();
//    }

//    // ---------- 私有构建方法（全部使用 NativeArray，不引入额外指针） ----------
//    private JobHandle InitializeGridInternal()
//    {
//        if (_positions.Length == 0)
//            throw new Exception("位置数组为空");

//        if (_minMaxPositions.IsCreated) _minMaxPositions.Dispose();
//        if (_gridDimensions.IsCreated) _gridDimensions.Dispose();

//        _minMaxPositions = new NativeArray<float2>(2, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
//        _gridDimensions = new NativeArray<int2>(1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

//        // 1. 计算包围盒并确定网格维度（使用 SIMD 加速，需要临时指针）
//        var initJob = new GridInitializationSimdJob
//        {
//            Positions = _positions,
//            GridResolution = _gridResolution,
//            TargetGridSize = _targetGridSize,
//            MinMaxPositions = _minMaxPositions,
//            GridDimensions = _gridDimensions,
//            CellStartEnd = _cellStartEnd
//        };
//        var handle = initJob.Schedule();
//        handle.Complete();

//        int cellCount = _cellStartEnd.Length;

//        // 2. 为每个点分配哈希值（使用 NativeArray）
//        var assignJob = new AssignHashJob
//        {
//            Origin = _minMaxPositions[0],
//            InvRes = 1.0f / _gridResolution[0],
//            Dim = _gridDimensions[0],
//            Positions = _positions,
//            HashIndex = _hashIndex
//        };
//        handle = assignJob.Schedule(_positions.Length, 0, handle);

//        // 3. 计数（使用 NativeArray 原子操作，当前已足够快）
//        NativeArray<int> counts = new NativeArray<int>(cellCount, Allocator.TempJob, NativeArrayOptions.ClearMemory);
//        var countJob = new CountHashJob
//        {
//            HashIndex = _hashIndex,
//            Counts = counts
//        };
//        handle = countJob.Schedule(_hashIndex.Length, 0, handle);

//        // 4. 前缀和（标量）
//        var prefixJob = new PrefixSumJob { Counts = counts };
//        handle = prefixJob.Schedule(handle);

//        NativeArray<int2> sortedHashIndex = new NativeArray<int2>(_hashIndex.Length, Allocator.TempJob);
//        var placeJob = new PlaceElementsJob
//        {
//            OriginalHashIndex = _hashIndex,
//            Positions = _positions,
//            SortedX = _sortedX,
//            SortedY = _sortedY,
//            SortedHashIndex = sortedHashIndex,
//            Counts = counts
//        };
//        handle = placeJob.Schedule(_hashIndex.Length, 0, handle);

//        // 5. 合并填充 CellStartEnd 和复制回 HashIndex
//        var finalizeJob = new FinalizeJob
//        {
//            SortedHashIndex = sortedHashIndex,
//            CellStartEnd = _cellStartEnd,
//            DstHashIndex = _hashIndex
//        };
//        handle = finalizeJob.Schedule(handle);

//        counts.Dispose(handle);
//        sortedHashIndex.Dispose(handle);

//        return handle;
//    }

//    // ---------- SIMD 辅助方法 ----------
//    [MethodImpl(MethodImplOptions.AggressiveInlining)]
//    private static int2 SpaceToGrid(float2 pos, float2 origin, float invRes)
//    {
//        return (int2)math.floor((pos - origin) * invRes);
//    }

//    [MethodImpl(MethodImplOptions.AggressiveInlining)]
//    private static int Flatten2DTo1D(int2 cell, int2 dim)
//    {
//        return cell.y * dim.x + cell.x;
//    }

//    // ---------- 内部 Job 定义 ----------

//    // 包围盒计算保留 SIMD 指针，因为需要高效访问内存
//    private unsafe struct GridInitializationSimdJob : IJob
//    {
//        public NativeArray<float2> Positions;
//        public NativeArray<float> GridResolution;
//        public int TargetGridSize;
//        public NativeArray<float2> MinMaxPositions;
//        public NativeArray<int2> GridDimensions;
//        public NativeList<int2> CellStartEnd;

//        public void Execute()
//        {
//            int length = Positions.Length;
//            if (length == 0) return;

//            float2 min, max;
//            ComputeBoundsSimd(Positions, out min, out max);
//            MinMaxPositions[0] = min;
//            MinMaxPositions[1] = max;

//            float2 range = max - min;
//            float maxRange = math.max(range.x, range.y);
//            float resolution = GridResolution[0];
//            if (resolution <= 0f)
//            {
//                resolution = maxRange / TargetGridSize;
//                GridResolution[0] = resolution;
//            }

//            int2 dim = new int2(
//                math.max(1, (int)math.ceil(range.x / resolution)),
//                math.max(1, (int)math.ceil(range.y / resolution))
//            );

//            if (dim.x > MAX_GRID_SIZE || dim.y > MAX_GRID_SIZE)
//                throw new Exception($"网格维度 {dim} 超过最大允许值 {MAX_GRID_SIZE}");

//            GridDimensions[0] = dim;
//            int cellCount = dim.x * dim.y;
//            CellStartEnd.Resize(cellCount, NativeArrayOptions.ClearMemory);
//        }

//        private static void ComputeBoundsSimd(NativeArray<float2> positions, out float2 min, out float2 max)
//        {
//            int vecSize = Vector<float>.Count;
//            int length = positions.Length;

//            float minX = float.MaxValue, minY = float.MaxValue;
//            float maxX = float.MinValue, maxY = float.MinValue;

//            if (length >= vecSize)
//            {
//                var vminX = new Vector<float>(float.MaxValue);
//                var vminY = new Vector<float>(float.MaxValue);
//                var vmaxX = new Vector<float>(float.MinValue);
//                var vmaxY = new Vector<float>(float.MinValue);

//                int i = 0;
//                float* fptr = (float*)positions.GetUnsafePtr();
//                for (; i <= length - vecSize; i += vecSize)
//                {
//                    var vx = Unsafe.ReadUnaligned<Vector<float>>(fptr + i * 2);
//                    var vy = Unsafe.ReadUnaligned<Vector<float>>(fptr + i * 2 + vecSize);
//                    vminX = Vector.Min(vminX, vx);
//                    vminY = Vector.Min(vminY, vy);
//                    vmaxX = Vector.Max(vmaxX, vx);
//                    vmaxY = Vector.Max(vmaxY, vy);
//                }

//                for (int j = 0; j < vecSize; j++)
//                {
//                    minX = Math.Min(minX, vminX[j]);
//                    minY = Math.Min(minY, vminY[j]);
//                    maxX = Math.Max(maxX, vmaxX[j]);
//                    maxY = Math.Max(maxY, vmaxY[j]);
//                }

//                for (; i < length; i++)
//                {
//                    var p = positions[i];
//                    minX = Math.Min(minX, p.x);
//                    minY = Math.Min(minY, p.y);
//                    maxX = Math.Max(maxX, p.x);
//                    maxY = Math.Max(maxY, p.y);
//                }
//            }
//            else
//            {
//                for (int i = 0; i < length; i++)
//                {
//                    var p = positions[i];
//                    minX = Math.Min(minX, p.x);
//                    minY = Math.Min(minY, p.y);
//                    maxX = Math.Max(maxX, p.x);
//                    maxY = Math.Max(maxY, p.y);
//                }
//            }

//            min = new float2(minX, minY);
//            max = new float2(maxX, maxY);
//        }
//    }

//    // 哈希分配（NativeArray 版）
//    private struct AssignHashJob : IJobParallelFor
//    {
//        public float2 Origin;
//        public float InvRes;
//        public int2 Dim;
//        public NativeArray<float2> Positions;
//        public NativeArray<int2> HashIndex;

//        public void Execute(int index)
//        {
//            float2 p = Positions[index];
//            int2 cell = SpaceToGrid(p, Origin, InvRes);
//            cell = math.clamp(cell, int2.zero, Dim - 1);
//            int hash = Flatten2DTo1D(cell, Dim);
//            HashIndex[index] = new int2(hash, index);
//        }
//    }

//    // 计数（原子操作）
//    private unsafe struct CountHashJob : IJobParallelFor
//    {
//        public NativeArray<int2> HashIndex;
//        public NativeArray<int> Counts;

//        public void Execute(int index)
//        {
//            int hash = HashIndex[index].x;
//            Interlocked.Increment(ref UnsafeUtility.ArrayElementAsRef<int>(Counts.GetUnsafePtr(), hash));
//        }
//    }

//    private struct PrefixSumJob : IJob
//    {
//        public NativeArray<int> Counts;
//        public void Execute()
//        {
//            int sum = 0;
//            for (int i = 0; i < Counts.Length; i++)
//            {
//                int c = Counts[i];
//                Counts[i] = sum;
//                sum += c;
//            }
//        }
//    }

//    // 放置元素（原子操作）
//    private unsafe struct PlaceElementsJob : IJobParallelFor
//    {
//        public NativeArray<int2> OriginalHashIndex;
//        public NativeArray<float2> Positions;
//        public NativeArray<float> SortedX;
//        public NativeArray<float> SortedY;
//        public NativeArray<int2> SortedHashIndex;
//        public NativeArray<int> Counts; // 前缀和，将被原子递增

//        public void Execute(int index)
//        {
//            int2 entry = OriginalHashIndex[index];
//            int hash = entry.x;
//            int origIdx = entry.y;

//            int destIdx = Interlocked.Add(ref UnsafeUtility.ArrayElementAsRef<int>(Counts.GetUnsafePtr(), hash), 1) - 1;

//            float2 p = Positions[origIdx];
//            SortedX[destIdx] = p.x;
//            SortedY[destIdx] = p.y;
//            SortedHashIndex[destIdx] = new int2(hash, origIdx);
//        }
//    }

//    // 最终合并（填充 CellStartEnd 并复制回 HashIndex）
//    private struct FinalizeJob : IJob
//    {
//        public NativeArray<int2> SortedHashIndex;
//        public NativeList<int2> CellStartEnd;
//        public NativeArray<int2> DstHashIndex;

//        public void Execute()
//        {
//            // 初始化 CellStartEnd 为 -1
//            for (int i = 0; i < CellStartEnd.Length; i++)
//                CellStartEnd[i] = new int2(-1, -1);

//            if (SortedHashIndex.Length == 0) return;

//            int currentHash = SortedHashIndex[0].x;
//            int startIdx = 0;
//            for (int i = 1; i <= SortedHashIndex.Length; i++)
//            {
//                if (i == SortedHashIndex.Length || SortedHashIndex[i].x != currentHash)
//                {
//                    CellStartEnd[currentHash] = new int2(startIdx, i);
//                    if (i < SortedHashIndex.Length)
//                    {
//                        currentHash = SortedHashIndex[i].x;
//                        startIdx = i;
//                    }
//                }
//            }

//            // 复制回 DstHashIndex
//            for (int i = 0; i < SortedHashIndex.Length; i++)
//                DstHashIndex[i] = SortedHashIndex[i];
//        }
//    }

//    // ---------- 查询 Job（保留 AVX2 加速）----------
//    private unsafe struct ClosestPointJob : IJobParallelFor
//    {
//        public float2 GridOrigin;
//        public float GridResolutionInv;
//        public int2 GridDimensions;
//        public NativeArray<float2> QueryPositions;
//        public NativeArray<float> SortedX;
//        public NativeArray<float> SortedY;
//        public NativeArray<int2> HashIndex;
//        public NativeArray<int2> CellStartEnd;
//        public bool IgnoreSelf;
//        public float SquaredEpsilonSelf;
//        public NativeArray<int> Results;

//        public float* SortedXPtr;
//        public float* SortedYPtr;
//        public int2* HashIndexPtr;
//        public int2* CellStartEndPtr;

//        public void Execute(int index)
//        {
//            Results[index] = -1;
//            float2 q = QueryPositions[index];
//            float qx = q.x;
//            float qy = q.y;

//            int cx = (int)math.floor((qx - GridOrigin.x) * GridResolutionInv);
//            int cy = (int)math.floor((qy - GridOrigin.y) * GridResolutionInv);
//            cx = math.clamp(cx, 0, GridDimensions.x - 1);
//            cy = math.clamp(cy, 0, GridDimensions.y - 1);

//            float bestDistSq = float.MaxValue;
//            int bestIdx = -1;

//            int dimX = GridDimensions.x;

//            for (int dx = -1; dx <= 1; dx++)
//            {
//                int nx = cx + dx;
//                if (nx < 0 || nx >= dimX) continue;
//                for (int dy = -1; dy <= 1; dy++)
//                {
//                    int ny = cy + dy;
//                    if (ny < 0 || ny >= GridDimensions.y) continue;

//                    int cellHash = ny * dimX + nx;
//                    int start = CellStartEndPtr[cellHash].x;
//                    int end = CellStartEndPtr[cellHash].y;
//                    if (start < 0) continue;

//                    int count = end - start;
//                    float* xs = SortedXPtr + start;
//                    float* ys = SortedYPtr + start;

//                    int localBestIdx = -1;
//                    float localBestDist = bestDistSq;

//                    if (Avx2.IsSupported && count >= Vector256<float>.Count)
//                    {
//                        localBestIdx = FindClosestAvx2(qx, qy, xs, ys, count, start);
//                        if (localBestIdx >= 0)
//                        {
//                            float dx_ = SortedXPtr[localBestIdx] - qx;
//                            float dy_ = SortedYPtr[localBestIdx] - qy;
//                            localBestDist = dx_ * dx_ + dy_ * dy_;
//                        }
//                    }
//                    else
//                    {
//                        for (int i = 0; i < count; i++)
//                        {
//                            float ddx = xs[i] - qx;
//                            float ddy = ys[i] - qy;
//                            float d = ddx * ddx + ddy * ddy;
//                            if (d < localBestDist)
//                            {
//                                localBestDist = d;
//                                localBestIdx = start + i;
//                            }
//                        }
//                    }

//                    if (localBestDist < bestDistSq)
//                    {
//                        bestDistSq = localBestDist;
//                        bestIdx = localBestIdx;
//                    }
//                }
//            }

//            if (bestIdx != -1)
//                Results[index] = HashIndexPtr[bestIdx].y;
//            else
//            {
//                for (int i = 0; i < SortedX.Length; i++)
//                {
//                    float dx = SortedXPtr[i] - qx;
//                    float dy = SortedYPtr[i] - qy;
//                    float d = dx * dx + dy * dy;
//                    if (d < bestDistSq)
//                    {
//                        bestDistSq = d;
//                        bestIdx = i;
//                    }
//                }
//                if (bestIdx != -1)
//                    Results[index] = HashIndexPtr[bestIdx].y;
//            }
//        }

//        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
//        private unsafe int FindClosestAvx2(float qx, float qy, float* xs, float* ys, int count, int startIdx)
//        {
//            var vqx = Vector256.Create(qx);
//            var vqy = Vector256.Create(qy);
//            var vbestDist = Vector256.Create(float.MaxValue);
//            var vbestIdx = Vector256.Create(-1f);
//            var vstep = Vector256.Create(0f, 1f, 2f, 3f, 4f, 5f, 6f, 7f);

//            int i = 0;
//            for (; i <= count - Vector256<float>.Count; i += Vector256<float>.Count)
//            {
//                var vx = Avx.LoadVector256(xs + i);
//                var vy = Avx.LoadVector256(ys + i);
//                var dx = Avx.Subtract(vx, vqx);
//                var dy = Avx.Subtract(vy, vqy);
//                var distSq = Avx.Add(Avx.Multiply(dx, dx), Avx.Multiply(dy, dy));

//                var lt = Avx.Compare(distSq, vbestDist, FloatComparisonMode.OrderedLessThanNonSignaling);

//                vbestDist = Avx.BlendVariable(vbestDist, distSq, lt);
//                var currentIndices = Avx.Add(Vector256.Create((float)i), vstep);
//                vbestIdx = Avx.BlendVariable(vbestIdx, currentIndices, lt);
//            }

//            float* dists = (float*)&vbestDist;
//            float* idxs = (float*)&vbestIdx;
//            int bestLocalIdx = -1;
//            float bestDist = float.MaxValue;
//            for (int j = 0; j < Vector256<float>.Count; j++)
//            {
//                if (dists[j] < bestDist)
//                {
//                    bestDist = dists[j];
//                    int idxCandidate = (int)(idxs[j] + 0.5f);
//                    if (idxCandidate >= 0)
//                        bestLocalIdx = idxCandidate;
//                }
//            }

//            for (; i < count; i++)
//            {
//                float dx = xs[i] - qx;
//                float dy = ys[i] - qy;
//                float d = dx * dx + dy * dy;
//                if (d < bestDist)
//                {
//                    bestDist = d;
//                    bestLocalIdx = startIdx + i;
//                }
//            }

//            return bestLocalIdx;
//        }
//    }

//    // 半径查询（非 SIMD，保持 NativeArray）
//    private struct FindWithinJob : IJobParallelFor
//    {
//        public float SquaredRadius;
//        public int MaxNeighbor;
//        public int CellsToLoop;
//        public float2 GridOrigin;
//        public float GridResolutionInv;
//        public int2 GridDimensions;
//        public NativeArray<float2> QueryPositions;
//        public NativeArray<float> SortedX;
//        public NativeArray<float> SortedY;
//        public NativeArray<int2> HashIndex;
//        public NativeArray<int2> CellStartEnd;
//        public NativeArray<int> Results;

//        public void Execute(int index)
//        {
//            int baseIdx = index * MaxNeighbor;
//            for (int i = 0; i < MaxNeighbor; i++)
//                Results[baseIdx + i] = -1;

//            float2 q = QueryPositions[index];
//            int2 centerCell = SpaceToGrid(q, GridOrigin, GridResolutionInv);
//            centerCell = math.clamp(centerCell, int2.zero, GridDimensions - 1);

//            int found = 0;
//            int dimX = GridDimensions.x;

//            int centerHash = centerCell.y * dimX + centerCell.x;
//            int start = CellStartEnd[centerHash].x;
//            int end = CellStartEnd[centerHash].y;
//            if (start >= 0)
//            {
//                for (int iCell = start; iCell < end; iCell++)
//                {
//                    float dx = SortedX[iCell] - q.x;
//                    float dy = SortedY[iCell] - q.y;
//                    float distSq = dx * dx + dy * dy;
//                    if (distSq <= SquaredRadius)
//                    {
//                        Results[baseIdx + found] = HashIndex[iCell].y;
//                        found++;
//                        if (found == MaxNeighbor) return;
//                    }
//                }
//            }

//            for (int dx = -CellsToLoop; dx <= CellsToLoop; dx++)
//            {
//                int nx = centerCell.x + dx;
//                if (nx < 0 || nx >= dimX) continue;
//                for (int dy = -CellsToLoop; dy <= CellsToLoop; dy++)
//                {
//                    if (dx == 0 && dy == 0) continue;

//                    int ny = centerCell.y + dy;
//                    if (ny < 0 || ny >= GridDimensions.y) continue;

//                    int cellHash = ny * dimX + nx;
//                    int s = CellStartEnd[cellHash].x;
//                    int e = CellStartEnd[cellHash].y;
//                    if (s < 0) continue;

//                    for (int iCell = s; iCell < e; iCell++)
//                    {
//                        float ddx = SortedX[iCell] - q.x;
//                        float ddy = SortedY[iCell] - q.y;
//                        float distSq = ddx * ddx + ddy * ddy;
//                        if (distSq <= SquaredRadius)
//                        {
//                            Results[baseIdx + found] = HashIndex[iCell].y;
//                            found++;
//                            if (found == MaxNeighbor) return;
//                        }
//                    }
//                }
//            }
//        }
//    }

//    // 调试方法
//    public void DebugPrintCellStats()
//    {
//        GD.Print($"CellStartEnd length: {_cellStartEnd.Length}");
//        int nonEmpty = 0;
//        for (int i = 0; i < Math.Min(10, _cellStartEnd.Length); i++)
//        {
//            var se = _cellStartEnd[i];
//            GD.Print($"Cell {i}: ({se.x}, {se.y})");
//            if (se.x >= 0) nonEmpty++;
//        }
//        GD.Print($"Non-empty cells (first 10): {nonEmpty}");
//    }
//}