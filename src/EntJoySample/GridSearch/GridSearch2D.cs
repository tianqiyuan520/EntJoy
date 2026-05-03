using EntJoy.Collections;
using EntJoy.Mathematics;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Vector3 = System.Numerics.Vector3;

public class GridSearch2D : IDisposable
{
    private const int MAX_GRID_SIZE = 1024;
    private int _targetGridSize;
    private float _resolution;

    private NativeArray<float2> _positions;
    private NativeArray<float2> _sortedPositions;
    private NativeArray<int2> _hashIndex;
    private NativeList<int2> _cellStartEnd;
    private NativeArray<float2> _minMaxPositions;
    private NativeArray<int2> _gridDimensions;
    private NativeArray<float> _gridResolution;

    public struct BuildTimings
    {
        public float DisposeNative;
        public float CreateAndCopy;
        public float BoundingBox;
        public float HashCounting;
        public float PrefixAndFill;
        public float ElementPlacement;
        public float CoreBuildTotal;
        public float QueryTotal;
    }

    private BuildTimings _lastTimings;
    public BuildTimings LastBuildTimings => _lastTimings;

    public float2 GridMin => _minMaxPositions[0];
    public float2 GridMax => _minMaxPositions[1];
    public int2 GridDimensions => _gridDimensions[0];
    public float Resolution => _gridResolution[0];

    public GridSearch2D(float resolution = -1f, int targetGrid = 32)
    {
        if (resolution > 0f)
        {
            _resolution = resolution;
            _targetGridSize = 0;
        }
        else if (targetGrid > 0)
        {
            _targetGridSize = targetGrid;
        }
        else
        {
            throw new System.Exception("必须指定 resolution (>0) 或 targetGrid (>0)");
        }
    }

    // ================= 构建接口 =================

    public JobHandle InitializeGrid(NativeArray<float2> positions)
    {
        var sw = new Stopwatch();
        _lastTimings = new BuildTimings();

        sw.Restart();
        Dispose();
        sw.Stop();
        _lastTimings.DisposeNative = (float)sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        _gridResolution = new NativeArray<float>(1, Allocator.Persistent);
        _gridResolution[0] = _resolution;
        _positions = new NativeArray<float2>(positions.Length, Allocator.Persistent);
        _hashIndex = new NativeArray<int2>(positions.Length, Allocator.Persistent);
        _sortedPositions = new NativeArray<float2>(positions.Length, Allocator.Persistent);
        _cellStartEnd = new NativeList<int2>(Allocator.Persistent);
        positions.CopyTo(_positions);
        sw.Stop();
        _lastTimings.CreateAndCopy = (float)sw.Elapsed.TotalMilliseconds;

        return InitializeGridInternal();
    }

    public JobHandle InitializeGrid(Vector3[] positions)
    {
        var sw = new Stopwatch();
        _lastTimings = new BuildTimings();

        sw.Restart();
        Dispose();
        sw.Stop();
        _lastTimings.DisposeNative = (float)sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        _gridResolution = new NativeArray<float>(1, Allocator.Persistent);
        _gridResolution[0] = _resolution;
        _positions = new NativeArray<float2>(positions.Length, Allocator.Persistent);
        for (int i = 0; i < positions.Length; i++)
            _positions[i] = new float2(positions[i].X, positions[i].Y);
        _hashIndex = new NativeArray<int2>(positions.Length, Allocator.Persistent);
        _sortedPositions = new NativeArray<float2>(positions.Length, Allocator.Persistent);
        _cellStartEnd = new NativeList<int2>(Allocator.Persistent);
        sw.Stop();
        _lastTimings.CreateAndCopy = (float)sw.Elapsed.TotalMilliseconds;

        return InitializeGridInternal();
    }

    public JobHandle UpdatePositions(Vector3[] newPositions)
    {
        if (_positions.Length != newPositions.Length)
            throw new System.Exception("数组长度不一致");
        for (int i = 0; i < newPositions.Length; i++)
            _positions[i] = new float2(newPositions[i].X, newPositions[i].Y);
        return InitializeGridInternal();
    }

    public JobHandle UpdatePositions(NativeArray<float2> newPositions)
    {
        if (_positions.Length != newPositions.Length)
            throw new System.Exception("数组长度不一致");
        newPositions.CopyTo(_positions);
        return InitializeGridInternal();
    }

    // ================= 查询接口 (最近点) =================

    public int[] SearchClosestPoint(Vector3[] queryPoints, bool ignoreSelf = false, float epsilon = 0.001f)
    {
        var qPoints = new NativeArray<float2>(queryPoints.Length, Allocator.TempJob);
        for (int i = 0; i < queryPoints.Length; i++)
            qPoints[i] = new float2(queryPoints[i].X, queryPoints[i].Y);
        var results = new NativeArray<int>(queryPoints.Length, Allocator.TempJob);

        var job = new ClosestPointJobPointer
        {
            GridOrigin = _minMaxPositions[0],
            GridResolutionInv = 1.0f / _gridResolution[0],
            GridDimensions = _gridDimensions[0],
            QueryPositions = qPoints,
            SortedPositions = _sortedPositions,
            HashIndex = _hashIndex,
            CellStartEnd = _cellStartEnd,
            SortedLength = _sortedPositions.Length,
            IgnoreSelf = ignoreSelf,
            SquaredEpsilonSelf = epsilon * epsilon,
            Results = results
        };
        var handle = job.Schedule(qPoints.Length, 0); // 批量大小 1024，减少调度开销
        handle.Complete();

        int[] res = new int[results.Length];
        results.CopyTo(res);
        results.Dispose();
        qPoints.Dispose();
        return res;
    }

    public NativeArray<int> SearchClosestPoint(
        NativeArray<float2> queryPoints,
        bool ignoreSelf = false, float epsilon = 0.001f)
    {
        var results = new NativeArray<int>(queryPoints.Length, Allocator.TempJob);
        var sw = Stopwatch.StartNew();

        var job = new ClosestPointJobPointer
        {
            GridOrigin = _minMaxPositions[0],
            GridResolutionInv = 1.0f / _gridResolution[0],
            GridDimensions = _gridDimensions[0],
            QueryPositions = queryPoints,
            SortedPositions = _sortedPositions,
            HashIndex = _hashIndex,
            CellStartEnd = _cellStartEnd,
            SortedLength = _sortedPositions.Length,
            IgnoreSelf = ignoreSelf,
            SquaredEpsilonSelf = epsilon * epsilon,
            Results = results
        };
        var handle = job.Schedule(queryPoints.Length, 0);
        handle.Complete();

        sw.Stop();
        _lastTimings.QueryTotal = (float)sw.Elapsed.TotalMilliseconds;
        return results;
    }

    // ================= 查询接口 (半径搜索) =================

    public int[] SearchWithin(Vector3[] queryPoints, float radius, int maxNeighborPerQuery)
    {
        var qPoints = new NativeArray<float2>(queryPoints.Length, Allocator.TempJob);
        for (int i = 0; i < queryPoints.Length; i++)
            qPoints[i] = new float2(queryPoints[i].X, queryPoints[i].Y);
        var results = new NativeArray<int>(queryPoints.Length * maxNeighborPerQuery, Allocator.TempJob);
        int cellsToLoop = (int)math.ceil(radius / _gridResolution[0]);

        var job = new FindWithinJobPointer
        {
            SquaredRadius = radius * radius,
            MaxNeighbor = maxNeighborPerQuery,
            CellsToLoop = cellsToLoop,
            GridOrigin = _minMaxPositions[0],
            GridResolutionInv = 1.0f / _gridResolution[0],
            GridDimensions = _gridDimensions[0],
            QueryPositions = qPoints,
            SortedPositions = _sortedPositions,
            HashIndex = _hashIndex,
            CellStartEnd = _cellStartEnd,
            Results = results
        };
        var handle = job.Schedule(qPoints.Length, 64);
        handle.Complete();

        int[] res = new int[results.Length];
        results.CopyTo(res);
        results.Dispose();
        qPoints.Dispose();
        return res;
    }

    public NativeArray<int> SearchWithin(
        NativeArray<float2> queryPoints,
        float radius, int maxNeighborPerQuery)
    {
        var results = new NativeArray<int>(queryPoints.Length * maxNeighborPerQuery, Allocator.TempJob);
        int cellsToLoop = (int)math.ceil(radius / _gridResolution[0]);

        var job = new FindWithinJobPointer
        {
            SquaredRadius = radius * radius,
            MaxNeighbor = maxNeighborPerQuery,
            CellsToLoop = cellsToLoop,
            GridOrigin = _minMaxPositions[0],
            GridResolutionInv = 1.0f / _gridResolution[0],
            GridDimensions = _gridDimensions[0],
            QueryPositions = queryPoints,
            SortedPositions = _sortedPositions,
            HashIndex = _hashIndex,
            CellStartEnd = _cellStartEnd,
            Results = results
        };
        var handle = job.Schedule(queryPoints.Length, 64);
        handle.Complete();
        return results;
    }

    public void Dispose()
    {
        DisposeGridData();
    }

    private void DisposeGridData()
    {
        if (_positions.IsCreated) _positions.Dispose();
        if (_hashIndex.IsCreated) _hashIndex.Dispose();
        if (_cellStartEnd.IsCreated) _cellStartEnd.Dispose();
        if (_sortedPositions.IsCreated) _sortedPositions.Dispose();
        if (_minMaxPositions.IsCreated) _minMaxPositions.Dispose();
        if (_gridDimensions.IsCreated) _gridDimensions.Dispose();
        if (_gridResolution.IsCreated) _gridResolution.Dispose();
    }

    // ================= 内部构建逻辑 =================

    private unsafe JobHandle InitializeGridInternal()
    {
        if (_positions.Length == 0)
            throw new System.Exception("位置数组为空");

        var sw = new Stopwatch();

        // 确保辅助容器存在
        if (!_minMaxPositions.IsCreated || _minMaxPositions.Length != 2)
        {
            if (_minMaxPositions.IsCreated) _minMaxPositions.Dispose();
            _minMaxPositions = new NativeArray<float2>(2, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }
        if (!_gridDimensions.IsCreated || _gridDimensions.Length != 1)
        {
            if (_gridDimensions.IsCreated) _gridDimensions.Dispose();
            _gridDimensions = new NativeArray<int2>(1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        // 1. 包围盒 & 网格初始化
        sw.Restart();
        var initJob = new GridInitializationJobPointer
        {
            Positions = _positions,
            PositionLength = _positions.Length,
            GridResolution = _gridResolution,
            TargetGridSize = _targetGridSize,
            MinMaxPositions = _minMaxPositions,
            GridDimensions = _gridDimensions,
            CellStartEnd = _cellStartEnd
        };
        var handle = initJob.Schedule();
        handle.Complete();
        sw.Stop();
        _lastTimings.BoundingBox = (float)sw.Elapsed.TotalMilliseconds;

        int2 dim = _gridDimensions[0];
        int cellCount = dim.x * dim.y;
        _cellStartEnd.Resize(cellCount, NativeArrayOptions.UninitializedMemory);

        var counts = new NativeArray<int>(cellCount, Allocator.TempJob);
        var sortedHashIndex = new NativeArray<int2>(_positions.Length, Allocator.TempJob);

        // 2. 哈希分配 & 计数
        sw.Restart();
        var assignAndCountJob = new AssignAndCountJobPointer
        {
            Positions = _positions,
            HashIndex = _hashIndex,
            Counts = counts,
            Origin = _minMaxPositions[0],
            InvRes = 1.0f / _gridResolution[0],
            Dim = dim,
            MaxHash = cellCount - 1
        };
        handle = assignAndCountJob.Schedule(_positions.Length, 0);
        handle.Complete();
        sw.Stop();
        _lastTimings.HashCounting = (float)sw.Elapsed.TotalMilliseconds;

        // 3. 前缀和
        sw.Restart();
        var prefixJob = new PrefixSumJobPointer { Counts = counts, Length = cellCount };
        handle = prefixJob.Schedule();
        handle.Complete();
        float prefixTime = (float)sw.Elapsed.TotalMilliseconds;

        // 4. 元素放置
        sw.Restart();
        var placeJob = new PlaceElementsJobPointer
        {
            OriginalHashIndex = _hashIndex,
            Positions = _positions,
            SortedPositions = _sortedPositions,
            SortedHashIndex = sortedHashIndex,
            Counts = counts,
            SortedPositionsLen = _sortedPositions.Length
        };
        handle = placeJob.Schedule(_hashIndex.Length, 0);
        handle.Complete();
        sw.Stop();
        _lastTimings.ElementPlacement = (float)sw.Elapsed.TotalMilliseconds;

        // 5. 填充单元格起止索引
        sw.Restart();
        var fillCellJob = new FillCellStartEndJobPointer
        {
            SortedHashIndex = sortedHashIndex,
            SortedLength = sortedHashIndex.Length,
            CellStartEnd = _cellStartEnd
        };
        handle = fillCellJob.Schedule();
        handle.Complete();
        sw.Stop();
        float fillTime = (float)sw.Elapsed.TotalMilliseconds;

        _lastTimings.PrefixAndFill = prefixTime + fillTime;

        // 6. 复制排序后的哈希索引 (供查询时使用)
        var copyJob = new CopyHashIndexJobPointer
        {
            Src = sortedHashIndex,
            Dst = _hashIndex
        };
        handle = copyJob.Schedule(_hashIndex.Length, 0);
        handle.Complete();

        _lastTimings.CoreBuildTotal = _lastTimings.BoundingBox +
                                      _lastTimings.HashCounting +
                                      _lastTimings.PrefixAndFill +
                                      _lastTimings.ElementPlacement;

        counts.Dispose();
        sortedHashIndex.Dispose();
        return default;
    }

    // ================= 内部 Job 结构体 =================

    [NativeTranspiler.NativeTranspile]
    public struct GridInitializationJobPointer : IJob
    {
        public NativeArray<float2> Positions;
        public int PositionLength;
        public NativeArray<float> GridResolution;
        public int TargetGridSize;
        public NativeArray<float2> MinMaxPositions;
        public NativeArray<int2> GridDimensions;
        public NativeList<int2> CellStartEnd;

        public void Execute()
        {
            float2 min = Positions[0];
            float2 max = Positions[0];
            for (int i = 1; i < PositionLength; i++)
            {
                min = math.min(min, Positions[i]);
                max = math.max(max, Positions[i]);
            }
            MinMaxPositions[0] = min;
            MinMaxPositions[1] = max;

            float2 range = max - min;
            float maxRange = math.max(range.x, range.y);

            float resolution = GridResolution[0];
            if (resolution <= 0f)
            {
                resolution = maxRange / TargetGridSize;
                GridResolution[0] = resolution;
            }

            int2 dim = new int2(
                math.max(1, (int)math.ceil(range.x / resolution)),
                math.max(1, (int)math.ceil(range.y / resolution))
            );

            if (dim.x > MAX_GRID_SIZE || dim.y > MAX_GRID_SIZE)
                return;

            GridDimensions[0] = dim;
            int cellCount = dim.x * dim.y;
            CellStartEnd.Resize(cellCount, NativeArrayOptions.ClearMemory);
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
    public unsafe struct AssignAndCountJobPointer : IJobParallelFor
    {
        public NativeArray<float2> Positions;
        public NativeArray<int2> HashIndex;
        public NativeArray<int> Counts;
        public float2 Origin;
        public float InvRes;
        public int2 Dim;
        public int MaxHash;

        public void Execute(int index)
        {
            float2 p = Positions[index];
            int cellX = (int)((p.x - Origin.x) * InvRes);
            int cellY = (int)((p.y - Origin.y) * InvRes);
            cellX = math.clamp(cellX, 0, Dim.x - 1);
            cellY = math.clamp(cellY, 0, Dim.y - 1);
            int hash = cellY * Dim.x + cellX;
            hash = math.clamp(hash, 0, MaxHash);
            HashIndex[index] = new int2(hash, index);
            Interlocked.Increment(ref UnsafeUtility.ArrayElementAsRef<int>(Counts.GetUnsafePtr(), hash));
        }
    }

    [NativeTranspiler.NativeTranspile]
    public struct PrefixSumJobPointer : IJob
    {
        public NativeArray<int> Counts;
        public int Length;

        public void Execute()
        {
            int sum = 0;
            for (int i = 0; i < Length; i++)
            {
                int c = Counts[i];
                Counts[i] = sum;
                sum += c;
            }
        }
    }

    [NativeTranspiler.NativeTranspile]
    public unsafe struct PlaceElementsJobPointer : IJobParallelFor
    {
        public NativeArray<int2> OriginalHashIndex;
        public NativeArray<float2> Positions;
        public NativeArray<float2> SortedPositions;
        public NativeArray<int2> SortedHashIndex;
        public NativeArray<int> Counts;
        public int SortedPositionsLen;

        public void Execute(int index)
        {
            int2 entry = OriginalHashIndex[index];
            int hash = entry.x;
            int origIdx = entry.y;
            int destIdx = Interlocked.Add(ref UnsafeUtility.ArrayElementAsRef<int>(Counts.GetUnsafePtr(), hash), 1) - 1;
            if (destIdx < SortedPositionsLen)
            {
                SortedPositions[destIdx] = Positions[origIdx];
                SortedHashIndex[destIdx] = new int2(hash, origIdx);
            }
        }
    }

    [NativeTranspiler.NativeTranspile]
    public struct FillCellStartEndJobPointer : IJob
    {
        public NativeArray<int2> SortedHashIndex;
        public int SortedLength;
        public NativeList<int2> CellStartEnd;

        public void Execute()
        {
            for (int i = 0; i < CellStartEnd.Length; i++)
                CellStartEnd[i] = new int2(-1, -1);

            if (SortedLength == 0) return;

            int currentHash = SortedHashIndex[0].x;
            int startIdx = 0;
            for (int i = 1; i <= SortedLength; i++)
            {
                if (i == SortedLength || SortedHashIndex[i].x != currentHash)
                {
                    CellStartEnd[currentHash] = new int2(startIdx, i);
                    if (i < SortedLength)
                    {
                        currentHash = SortedHashIndex[i].x;
                        startIdx = i;
                    }
                }
            }
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
    public struct CopyHashIndexJobPointer : IJobParallelFor
    {
        public NativeArray<int2> Src;
        public NativeArray<int2> Dst;

        public void Execute(int index)
        {
            Dst[index] = Src[index];
        }
    }

    // ---- 优化的最近点查询 Job (AoS, ISPC, 无全局回退) ----
    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
    public struct ClosestPointJobPointer : IJobParallelFor
    {
        public float2 GridOrigin;
        public float GridResolutionInv;
        public int2 GridDimensions;
        public NativeArray<float2> QueryPositions;
        public NativeArray<float2> SortedPositions;
        public NativeArray<int2> HashIndex;
        public NativeList<int2> CellStartEnd;
        public int SortedLength;
        public bool IgnoreSelf;
        public float SquaredEpsilonSelf;
        public NativeArray<int> Results;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Execute(int index)
        {
            Results[index] = -1;
            float2 q = QueryPositions[index];
            int2 cell = (int2)math.floor((q - GridOrigin) * GridResolutionInv);
            cell = math.clamp(cell, int2.zero, GridDimensions - 1);

            float bestDistSq = float.MaxValue;
            int bestIdx = -1;

            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = cell.x + dx;
                if ((uint)nx >= (uint)GridDimensions.x) continue;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = cell.y + dy;
                    if ((uint)ny >= (uint)GridDimensions.y) continue;
                    int cellHash = ny * GridDimensions.x + nx;
                    int2 range = CellStartEnd[cellHash];
                    int start = range.x;
                    int end = range.y;
                    if (start < 0) continue;

                    for (int i = start; i < end; i++)
                    {
                        float2 pos = SortedPositions[i];
                        float distSq = math.distancesq(q, pos);
                        if (IgnoreSelf && distSq < SquaredEpsilonSelf) continue;
                        if (distSq < bestDistSq)
                        {
                            bestDistSq = distSq;
                            bestIdx = i;
                        }
                    }
                }
            }

            if (bestIdx != -1)
            {
                Results[index] = HashIndex[bestIdx].y;
            }
            else
            {
                for (int i = 0; i < SortedLength; i++)
                {
                    float2 pos = SortedPositions[i];
                    float distSq = math.distancesq(q, pos);
                    if (IgnoreSelf && distSq < SquaredEpsilonSelf) continue;
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestIdx = i;
                    }
                }
                if (bestIdx != -1)
                    Results[index] = HashIndex[bestIdx].y;
            }
        }
    }



    [NativeTranspiler.NativeTranspile]
    public struct FindWithinJobPointer : IJobParallelFor
    {
        public float SquaredRadius;
        public int MaxNeighbor;
        public int CellsToLoop;
        public float2 GridOrigin;
        public float GridResolutionInv;
        public int2 GridDimensions;
        public NativeArray<float2> QueryPositions;
        public NativeArray<float2> SortedPositions;
        public NativeArray<int2> HashIndex;
        public NativeList<int2> CellStartEnd;
        public NativeArray<int> Results;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Execute(int index)
        {
            int baseIdx = index * MaxNeighbor;
            for (int i = 0; i < MaxNeighbor; i++)
                Results[baseIdx + i] = -1;

            float2 q = QueryPositions[index];
            int2 centerCell = (int2)math.floor((q - GridOrigin) * GridResolutionInv);
            centerCell = math.clamp(centerCell, int2.zero, GridDimensions - new int2(1, 1));

            int found = 0;

            int centerHash = centerCell.y * GridDimensions.x + centerCell.x;
            int2 centerRange = CellStartEnd[centerHash];
            int start = centerRange.x;
            int end = centerRange.y;
            if (start >= 0)
            {
                for (int iCell = start; iCell < end; iCell++)
                {
                    float2 pos = SortedPositions[iCell];
                    if (math.distancesq(q, pos) <= SquaredRadius)
                    {
                        Results[baseIdx + found] = HashIndex[iCell].y;
                        found++;
                        if (found == MaxNeighbor) return;
                    }
                }
            }

            for (int dx = -CellsToLoop; dx <= CellsToLoop; dx++)
            {
                int nx = centerCell.x + dx;
                if ((uint)nx >= (uint)GridDimensions.x) continue;
                for (int dy = -CellsToLoop; dy <= CellsToLoop; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int ny = centerCell.y + dy;
                    if ((uint)ny >= (uint)GridDimensions.y) continue;

                    int hash = ny * GridDimensions.x + nx;
                    int2 range = CellStartEnd[hash];
                    int s = range.x;
                    int e = range.y;
                    if (s < 0) continue;

                    for (int iCell = s; iCell < e; iCell++)
                    {
                        float2 pos = SortedPositions[iCell];
                        if (math.distancesq(q, pos) <= SquaredRadius)
                        {
                            Results[baseIdx + found] = HashIndex[iCell].y;
                            found++;
                            if (found == MaxNeighbor) return;
                        }
                    }
                }
            }
        }
    }
}