//using EntJoy.Collections;
//using EntJoy.Mathematics;
//using System;
//using System.Threading;
//using Exception = System.Exception;
//using Vector3 = System.Numerics.Vector3;

///// <summary>
///// 二维网格空间索引，支持快速最近邻和半径内查找。
///// 分辨率可固定，也可根据目标网格总数自动计算。
///// </summary>
//public class GridSearch2D : IDisposable
//{
//    private const int MAX_GRID_SIZE = 1024;          // 单方向最大网格数（避免内存爆炸）
//    private int _targetGridSize;                      // 目标网格总数（若 resolution <= 0 则使用此值自动计算）
//    private float _resolution;                         // 网格分辨率（单元边长）

//    // 核心数据
//    private NativeArray<float2> _positions;           // 原始坐标（顺序与输入一致）
//    private NativeArray<float2> _sortedPositions;     // 按哈希排序后的坐标
//    private NativeArray<int2> _hashIndex;             // (hash, originalIndex)
//    private NativeList<int2> _cellStartEnd;           // 每个网格的起始+结束索引（长度 = 网格总数）
//    private NativeArray<float2> _minMaxPositions;     // [0] min, [1] max
//    private NativeArray<int2> _gridDimensions;        // (gridX, gridY)
//    private NativeArray<float> _gridResolution;       // 实际使用的分辨率

//    public float2 GridMin => _minMaxPositions[0];
//    public float2 GridMax => _minMaxPositions[1];
//    public int2 GridDimensions => _gridDimensions[0];
//    public float Resolution => _gridResolution[0];

//    /// <summary>
//    /// 构造函数。
//    /// </summary>
//    /// <param name="resolution">网格分辨率（>0）。若 <=0，则根据 targetGrid 自动计算。</param>
//    /// <param name="targetGrid">目标网格总数（仅在 resolution<=0 时生效）。</param>
//    public GridSearch2D(float resolution = -1f, int targetGrid = 32)
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

//    // ---------- 公共接口（支持 float2[] 和 Vector3[]）----------

//    /// <summary>初始化网格（异步 Job 形式）</summary>
//    public JobHandle InitializeGrid(NativeArray<float2> positions)
//    {
//        Dispose();
//        _gridResolution = new NativeArray<float>(1, Allocator.Persistent);
//        _gridResolution[0] = _resolution;
//        _positions = new NativeArray<float2>(positions.Length, Allocator.Persistent);
//        _hashIndex = new NativeArray<int2>(positions.Length, Allocator.Persistent);
//        _sortedPositions = new NativeArray<float2>(positions.Length, Allocator.Persistent);
//        _cellStartEnd = new NativeList<int2>(Allocator.Persistent);
//        positions.CopyTo(_positions);
//        return InitializeGridInternal();
//    }

//    public JobHandle InitializeGrid(Vector3[] positions) // 自动取 x,y
//    {
//        Dispose();
//        _gridResolution = new NativeArray<float>(1, Allocator.Persistent);
//        _gridResolution[0] = _resolution;
//        _positions = new NativeArray<float2>(positions.Length, Allocator.Persistent);
//        for (int i = 0; i < positions.Length; i++)
//            _positions[i] = new float2(positions[i].X, positions[i].Y);
//        _hashIndex = new NativeArray<int2>(positions.Length, Allocator.Persistent);
//        _sortedPositions = new NativeArray<float2>(positions.Length, Allocator.Persistent);
//        _cellStartEnd = new NativeList<int2>(Allocator.Persistent);
//        return InitializeGridInternal();
//    }

//    /// <summary>更新所有点的位置（重新构建网格）</summary>
//    public JobHandle UpdatePositions(Vector3[] newPositions)
//    {
//        if (_positions.Length != newPositions.Length)
//            throw new Exception("数组长度不一致");
//        for (int i = 0; i < newPositions.Length; i++)
//            _positions[i] = new float2(newPositions[i].X, newPositions[i].Y);
//        return InitializeGridInternal();
//    }

//    public JobHandle UpdatePositions(NativeArray<float2> newPositions)
//    {
//        if (_positions.Length != newPositions.Length)
//            throw new Exception("数组长度不一致");
//        newPositions.CopyTo(_positions);
//        return InitializeGridInternal();
//    }

//    // ---------- 查询接口 ----------

//    /// <summary>批量查找每个查询点的最近邻（返回原始索引，-1表示未找到）</summary>
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
//            SortedPositions = _sortedPositions,
//            HashIndex = _hashIndex,
//            CellStartEnd = _cellStartEnd.AsArray(),
//            Results = results,
//            IgnoreSelf = ignoreSelf,
//            SquaredEpsilonSelf = epsilon * epsilon
//        };
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
//            SortedPositions = _sortedPositions,
//            HashIndex = _hashIndex,
//            CellStartEnd = _cellStartEnd.AsArray(),
//            Results = results,
//            IgnoreSelf = ignoreSelf,
//            SquaredEpsilonSelf = epsilon * epsilon
//        };
//        var handle = job.Schedule(queryPoints.Length, 0);
//        handle.Complete();
//        return results;
//    }

//    /// <summary>批量查找半径内的邻居（每个查询点最多返回 maxNeighbor 个结果，结果数组为 [query0_0, query0_1, ..., query1_0,...]）</summary>
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
//            SortedPositions = _sortedPositions,
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
//            SortedPositions = _sortedPositions,
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
//        if (_sortedPositions.IsCreated) _sortedPositions.Dispose();
//        if (_minMaxPositions.IsCreated) _minMaxPositions.Dispose();
//        if (_gridDimensions.IsCreated) _gridDimensions.Dispose();
//        if (_gridResolution.IsCreated) _gridResolution.Dispose();
//    }

//    // ---------- 私有方法 ----------

//    private JobHandle InitializeGridInternal()
//    {
//        if (_positions.Length == 0)
//            throw new Exception("位置数组为空");

//        if (_minMaxPositions.IsCreated) _minMaxPositions.Dispose();
//        if (_gridDimensions.IsCreated) _gridDimensions.Dispose();

//        _minMaxPositions = new NativeArray<float2>(2, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
//        _gridDimensions = new NativeArray<int2>(1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);


//        //var handle = new JobHandle();
//        // 1. 计算包围盒并确定网格维度
//        var initJob = new GridInitializationJob
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


//        // 2. 为每个点分配哈希值
//        var assignHashJob = new AssignHashJob
//        {
//            MinMaxPositions = _minMaxPositions,
//            GridResolution = _gridResolution,
//            GridDimensions = _gridDimensions,
//            Positions = _positions,
//            HashIndex = _hashIndex,
//            CellStartEnd = _cellStartEnd.AsArray()
//        };
//        handle = assignHashJob.Schedule(_positions.Length, 0, handle);

//        int cellCount = _cellStartEnd.Length; // 网格总数

//        // 3. 并行计数排序

//        // 3.1 统计每个哈希值的出现次数
//        NativeArray<int> counts = new NativeArray<int>(cellCount, Allocator.TempJob, NativeArrayOptions.ClearMemory);
//        var countJob = new CountHashJob
//        {
//            HashIndex = _hashIndex,
//            Counts = counts
//        };
//        handle = countJob.Schedule(_hashIndex.Length, 0, handle);

//        // 3.2 计算前缀和（原位操作，变为起始索引）
//        var prefixJob = new PrefixSumJob
//        {
//            Counts = counts
//        };
//        handle = prefixJob.Schedule(handle);


//        // 3.3 放置元素到 sortedPositions 和 sortedHashIndex，并更新 counts 为当前指针
//        NativeArray<int2> sortedHashIndex = new NativeArray<int2>(_hashIndex.Length, Allocator.TempJob);
//        var placeJob = new PlaceElementsJob
//        {
//            OriginalHashIndex = _hashIndex,
//            Positions = _positions,
//            SortedPositions = _sortedPositions,
//            SortedHashIndex = sortedHashIndex,
//            Counts = counts
//        };
//        handle = placeJob.Schedule(_hashIndex.Length, 0, handle);

//        // 3.4 填充 cellStartEnd（遍历 sortedHashIndex 记录每个哈希的起止索引）
//        var fillCellJob = new FillCellStartEndJob
//        {
//            SortedHashIndex = sortedHashIndex,
//            CellStartEnd = _cellStartEnd
//        };
//        handle = fillCellJob.Schedule(handle);

//        // 3.5 将 sortedHashIndex 复制回 _hashIndex，以便后续查询使用
//        var copyJob = new CopyHashIndexJob
//        {
//            Src = sortedHashIndex,
//            Dst = _hashIndex
//        };
//        handle = copyJob.Schedule(sortedHashIndex.Length, 0, handle);

//        // 释放临时数组（依赖链确保在 Job 完成后释放）
//        counts.Dispose(handle);
//        sortedHashIndex.Dispose(handle);

//        return handle;
//    }

//    // ---------- 坐标转换辅助 ----------
//    private static int2 SpaceToGrid(float2 pos, float2 origin, float invRes)
//    {
//        return (int2)math.floor((pos - origin) * invRes);
//    }

//    private static int Flatten2DTo1D(int2 cell, int2 dim)
//    {
//        return cell.y * dim.x + cell.x;
//    }

//    // ---------- 内部 Job 定义 ----------


//    private struct GridInitializationJob : IJob
//    {
//        public NativeArray<float2> Positions;
//        public NativeArray<float> GridResolution;
//        public int TargetGridSize;

//        public NativeArray<float2> MinMaxPositions;
//        public NativeArray<int2> GridDimensions;
//        public NativeList<int2> CellStartEnd;

//        public void Execute()
//        {
//            float2 min = Positions[0];
//            float2 max = Positions[0];
//            for (int i = 1; i < Positions.Length; i++)
//            {
//                min = math.min(min, Positions[i]);
//                max = math.max(max, Positions[i]);
//            }
//            MinMaxPositions[0] = min;
//            MinMaxPositions[1] = max;

//            float2 range = max - min;
//            float maxRange = math.max(range.x, range.y);

//            float resolution = GridResolution[0];
//            if (resolution <= 0f)
//            {
//                // 自动计算分辨率
//                resolution = maxRange / TargetGridSize;
//                GridResolution[0] = resolution; // 更新为实际使用的分辨率
//            }

//            int2 dim = new int2(
//                math.max(1, (int)math.ceil(range.x / resolution)),
//                math.max(1, (int)math.ceil(range.y / resolution))
//            );

//            if (dim.x > MAX_GRID_SIZE || dim.y > MAX_GRID_SIZE)
//                throw new Exception($"网格维度 {dim} 超过最大允许值 {MAX_GRID_SIZE}，请增大分辨率或调整范围。");

//            GridDimensions[0] = dim;
//            int cellCount = dim.x * dim.y;
//            CellStartEnd.Resize(cellCount, NativeArrayOptions.ClearMemory);
//        }
//    }


//    private struct AssignHashJob : IJobParallelFor
//    {
//        public NativeArray<float2> MinMaxPositions;
//        public NativeArray<float> GridResolution;
//        public NativeArray<int2> GridDimensions;
//        public NativeArray<float2> Positions;
//        public NativeArray<int2> CellStartEnd;

//        public NativeArray<int2> HashIndex;

//        public void Execute(int index)
//        {
//            float2 origin = MinMaxPositions[0];
//            float resolution = GridResolution[0];
//            float invRes = 1.0f / resolution;
//            float2 p = Positions[index];
//            int2 cell = SpaceToGrid(p, origin, invRes);
//            cell = math.clamp(cell, int2.zero, GridDimensions[0] - 1);
//            int hash = Flatten2DTo1D(cell, GridDimensions[0]);
//            hash = math.clamp(hash, 0, CellStartEnd.Length - 1);
//            HashIndex[index] = new int2(hash, index);
//        }
//    }


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


//    private unsafe struct PlaceElementsJob : IJobParallelFor
//    {
//        public NativeArray<int2> OriginalHashIndex;
//        public NativeArray<float2> Positions;
//        public NativeArray<float2> SortedPositions;
//        public NativeArray<int2> SortedHashIndex;
//        public NativeArray<int> Counts; // 前缀和，将被原子递增

//        public void Execute(int index)
//        {
//            int2 entry = OriginalHashIndex[index];
//            int hash = entry.x;
//            int origIdx = entry.y;

//            int destIdx = Interlocked.Add(ref UnsafeUtility.ArrayElementAsRef<int>(Counts.GetUnsafePtr(), hash), 1) - 1;


//            SortedPositions[destIdx] = Positions[origIdx];
//            SortedHashIndex[destIdx] = new int2(hash, origIdx);

//        }
//    }


//    private struct FillCellStartEndJob : IJob
//    {
//        public NativeArray<int2> SortedHashIndex;
//        public NativeList<int2> CellStartEnd;

//        public void Execute()
//        {
//            // 初始化所有 CellStartEnd 为 (-1, -1) 表示空
//            for (int i = 0; i < CellStartEnd.Length; i++)
//            {
//                CellStartEnd[i] = new int2(-1, -1);
//            }

//            if (SortedHashIndex.Length == 0) return;

//            int currentHash = SortedHashIndex[0].x;
//            int startIdx = 0;

//            for (int i = 1; i <= SortedHashIndex.Length; i++)
//            {
//                if (i == SortedHashIndex.Length || SortedHashIndex[i].x != currentHash)
//                {
//                    // 记录 currentHash 的起止索引
//                    int2 se = new int2(startIdx, i);
//                    CellStartEnd[currentHash] = se;

//                    if (i < SortedHashIndex.Length)
//                    {
//                        currentHash = SortedHashIndex[i].x;
//                        startIdx = i;
//                    }
//                }
//            }
//        }
//    }


//    private struct CopyHashIndexJob : IJobParallelFor
//    {
//        public NativeArray<int2> Src;
//        public NativeArray<int2> Dst;

//        public void Execute(int index)
//        {
//            Dst[index] = Src[index];
//        }
//    }

//    // ---------- 原有的查询 Job 保持不变 ----------

//    private struct ClosestPointJob : IJobParallelFor
//    {
//        public float2 GridOrigin;
//        public float GridResolutionInv;
//        public int2 GridDimensions;
//        public NativeArray<float2> QueryPositions;
//        public NativeArray<float2> SortedPositions;
//        public NativeArray<int2> HashIndex;
//        public NativeArray<int2> CellStartEnd;
//        public bool IgnoreSelf;
//        public float SquaredEpsilonSelf;

//        public NativeArray<int> Results;

//        public void Execute(int index)
//        {
//            Results[index] = -1;
//            float2 q = QueryPositions[index];
//            int2 cell = SpaceToGrid(q, GridOrigin, GridResolutionInv);
//            cell = math.clamp(cell, int2.zero, GridDimensions - 1);

//            float bestDistSq = float.MaxValue;
//            int bestIdx = -1;

//            // 遍历 3x3 邻域
//            for (int dx = -1; dx <= 1; dx++)
//            {
//                int nx = cell.x + dx;
//                if (nx < 0 || nx >= GridDimensions.x) continue;

//                for (int dy = -1; dy <= 1; dy++)
//                {
//                    int ny = cell.y + dy;
//                    if (ny < 0 || ny >= GridDimensions.y) continue;

//                    int2 neighborCell = new int2(nx, ny);
//                    int cellHash = Flatten2DTo1D(neighborCell, GridDimensions);
//                    int start = CellStartEnd[cellHash].x;
//                    int end = CellStartEnd[cellHash].y;

//                    if (start < 0) continue; // 网格为空

//                    for (int i = start; i < end; i++)
//                    {
//                        float2 pos = SortedPositions[i];

//                        float distSq = math.distancesq(pos.x, pos.y);
//                        //float ddx = pos.x - q.x;
//                        //float ddy = pos.y - q.y;
//                        //float distSq = ddx * ddx + ddy * ddy;

//                        if (IgnoreSelf && distSq < SquaredEpsilonSelf)
//                            continue;

//                        if (distSq < bestDistSq)
//                        {
//                            bestDistSq = distSq;
//                            bestIdx = i;
//                        }
//                    }
//                }
//            }

//            if (bestIdx != -1)
//                Results[index] = HashIndex[bestIdx].y;
//            else
//            {
//                // 保底：遍历全部（极少发生）
//                for (int i = 0; i < SortedPositions.Length; i++)
//                {
//                    float2 pos = SortedPositions[i];
//                    float distSq = math.distancesq(q, pos);
//                    if (IgnoreSelf && distSq < SquaredEpsilonSelf) continue;
//                    if (distSq < bestDistSq)
//                    {
//                        bestDistSq = distSq;
//                        bestIdx = i;
//                    }
//                }
//                if (bestIdx != -1)
//                    Results[index] = HashIndex[bestIdx].y;
//            }
//        }
//    }


//    private struct FindWithinJob : IJobParallelFor
//    {
//        public float SquaredRadius;
//        public int MaxNeighbor;
//        public int CellsToLoop;
//        public float2 GridOrigin;
//        public float GridResolutionInv;
//        public int2 GridDimensions;
//        public NativeArray<float2> QueryPositions;
//        public NativeArray<float2> SortedPositions;
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

//            // 先查中心格
//            int centerHash = Flatten2DTo1D(centerCell, GridDimensions);
//            int start = CellStartEnd[centerHash].x;
//            int end = CellStartEnd[centerHash].y;
//            if (start >= 0)
//            {
//                for (int iCell = start; iCell < end; iCell++)
//                {
//                    float2 pos = SortedPositions[iCell];
//                    float distSq = math.distancesq(q, pos);
//                    if (distSq <= SquaredRadius)
//                    {
//                        Results[baseIdx + found] = HashIndex[iCell].y;
//                        found++;
//                        if (found == MaxNeighbor) return;
//                    }
//                }
//            }

//            // 再查周围 CellsToLoop 层
//            for (int dx = -CellsToLoop; dx <= CellsToLoop; dx++)
//            {
//                int nx = centerCell.x + dx;
//                if (nx < 0 || nx >= GridDimensions.x) continue;
//                for (int dy = -CellsToLoop; dy <= CellsToLoop; dy++)
//                {
//                    if (dx == 0 && dy == 0) continue; // 已查

//                    int ny = centerCell.y + dy;
//                    if (ny < 0 || ny >= GridDimensions.y) continue;

//                    int2 cell = new int2(nx, ny);
//                    int hash = Flatten2DTo1D(cell, GridDimensions);
//                    int s = CellStartEnd[hash].x;
//                    int e = CellStartEnd[hash].y;
//                    if (s < 0) continue;

//                    for (int iCell = s; iCell < e; iCell++)
//                    {
//                        float2 pos = SortedPositions[iCell];
//                        float distSq = math.distancesq(q, pos);
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

//    public void DebugPrintCellStats()
//    {
//        Console.WriteLine($"CellStartEnd length: {_cellStartEnd.Length}");
//        int nonEmpty = 0;
//        for (int i = 0; i < Math.Min(10, _cellStartEnd.Length); i++)
//        {
//            var se = _cellStartEnd[i];
//            Console.WriteLine($"Cell {i}: ({se.x}, {se.y})");
//            if (se.x >= 0) nonEmpty++;
//        }
//        Console.WriteLine($"Non-empty cells (first 10): {nonEmpty}");
//    }

//}



