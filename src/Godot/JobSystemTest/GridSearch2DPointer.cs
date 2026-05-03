//using EntJoy.Collections;
//using EntJoy.JobSystem;
//using EntJoy.Mathematics;
//using System;
//using System.Collections.Concurrent;
//using System.Runtime.CompilerServices;
//using System.Runtime.InteropServices;
//using System.Threading;
//using Vector3 = System.Numerics.Vector3;

///// <summary>
///// 二维网格空间索引，使用指针而不是 NativeCollection 来减少索引开销
///// 基于 GrideSearchOrigin，但所有 Job 都使用指针
///// </summary>
//public class GridSearch2DPointer : IDisposable
//{
//    private const int MAX_GRID_SIZE = 1024;
//    private int _targetGridSize;
//    private float _resolution;

//    // 核心数据
//    private NativeArray<float2> _positions;
//    private NativeArray<float2> _sortedPositions;
//    private NativeArray<int2> _hashIndex;
//    private NativeList<int2> _cellStartEnd;
//    private NativeArray<float2> _minMaxPositions;
//    private NativeArray<int2> _gridDimensions;
//    private NativeArray<float> _gridResolution;

//    // 临时缓冲区
//    private NativeArray<int> _countsScratch;
//    private NativeArray<int2> _sortedHashIndexScratch;

//    public float2 GridMin => _minMaxPositions[0];
//    public float2 GridMax => _minMaxPositions[1];
//    public int2 GridDimensions => _gridDimensions[0];
//    public float Resolution => _gridResolution[0];

//    /// <summary>
//    /// 构造函数。
//    /// </summary>
//    /// <param name="resolution">网格分辨率（>0）。若 <=0，则根据 targetGrid 自动计算。</param>
//    /// <param name="targetGrid">目标网格总数（仅在 resolution<=0 时生效）。</param>
//    public GridSearch2DPointer(float resolution = -1f, int targetGrid = 32)
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

//    // ---------- 公共接口 ----------

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

//    public JobHandle InitializeGrid(Vector3[] positions)
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

//        unsafe
//        {
//            var job = new ClosestPointJobPointer
//            {
//                GridOrigin = _minMaxPositions[0],
//                GridResolutionInv = 1.0f / _gridResolution[0],
//                GridDimensions = _gridDimensions[0],
//                QueryPositionsPtr = (float2*)qPoints.GetUnsafePtr(),
//                SortedPositionsPtr = (float2*)_sortedPositions.GetUnsafePtr(),
//                HashIndexPtr = (int2*)_hashIndex.GetUnsafePtr(),
//                CellStartEndPtr = (int2*)_cellStartEnd.AsArray().GetUnsafePtr(),
//                SortedLength = _sortedPositions.Length,
//                IgnoreSelf = ignoreSelf ? 1 : 0,
//                SquaredEpsilonSelf = epsilon * epsilon,
//                ResultsPtr = (int*)results.GetUnsafePtr()
//            };
//            var handle = ZeroAllocJobScheduler.ScheduleParallelFor(job, qPoints.Length, 0, default, null);
//            handle.Complete();
//        }

//        int[] res = new int[results.Length];
//        results.CopyTo(res);
//        results.Dispose();
//        qPoints.Dispose();
//        return res;
//    }

//    public NativeArray<int> SearchClosestPoint(NativeArray<float2> queryPoints, bool ignoreSelf = false, float epsilon = 0.001f)
//    {
//        NativeArray<int> results = new NativeArray<int>(queryPoints.Length, Allocator.TempJob);

//        unsafe
//        {
//            var job = new ClosestPointJobPointer
//            {
//                GridOrigin = _minMaxPositions[0],
//                GridResolutionInv = 1.0f / _gridResolution[0],
//                GridDimensions = _gridDimensions[0],
//                QueryPositionsPtr = (float2*)queryPoints.GetUnsafePtr(),
//                SortedPositionsPtr = (float2*)_sortedPositions.GetUnsafePtr(),
//                HashIndexPtr = (int2*)_hashIndex.GetUnsafePtr(),
//                CellStartEndPtr = (int2*)_cellStartEnd.AsArray().GetUnsafePtr(),
//                SortedLength = _sortedPositions.Length,
//                IgnoreSelf = ignoreSelf ? 1 : 0,
//                SquaredEpsilonSelf = epsilon * epsilon,
//                ResultsPtr = (int*)results.GetUnsafePtr()
//            };
//            var handle = ZeroAllocJobScheduler.ScheduleParallelFor(job, queryPoints.Length, 0, default, null);
//            handle.Complete();
//        }
//        return results;
//    }

//    /// <summary>批量查找半径内的邻居</summary>
//    public int[] SearchWithin(Vector3[] queryPoints, float radius, int maxNeighborPerQuery)
//    {
//        NativeArray<float2> qPoints = new NativeArray<float2>(queryPoints.Length, Allocator.TempJob);
//        for (int i = 0; i < queryPoints.Length; i++)
//            qPoints[i] = new float2(queryPoints[i].X, queryPoints[i].Y);
//        NativeArray<int> results = new NativeArray<int>(queryPoints.Length * maxNeighborPerQuery, Allocator.TempJob);
//        int cellsToLoop = (int)math.ceil(radius / _gridResolution[0]);

//        unsafe
//        {
//            var job = new FindWithinJobPointer
//            {
//                SquaredRadius = radius * radius,
//                MaxNeighbor = maxNeighborPerQuery,
//                CellsToLoop = cellsToLoop,
//                GridOrigin = _minMaxPositions[0],
//                GridResolutionInv = 1.0f / _gridResolution[0],
//                GridDimensions = _gridDimensions[0],
//                QueryPositionsPtr = (float2*)qPoints.GetUnsafePtr(),
//                SortedPositionsPtr = (float2*)_sortedPositions.GetUnsafePtr(),
//                HashIndexPtr = (int2*)_hashIndex.GetUnsafePtr(),
//                CellStartEndPtr = (int2*)_cellStartEnd.AsArray().GetUnsafePtr(),
//                ResultsPtr = (int*)results.GetUnsafePtr()
//            };
//            var handle = ZeroAllocJobScheduler.ScheduleParallelFor(job, qPoints.Length, 0, default, null);
//            handle.Complete();
//        }

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

//        unsafe
//        {
//            var job = new FindWithinJobPointer
//            {
//                SquaredRadius = radius * radius,
//                MaxNeighbor = maxNeighborPerQuery,
//                CellsToLoop = cellsToLoop,
//                GridOrigin = _minMaxPositions[0],
//                GridResolutionInv = 1.0f / _gridResolution[0],
//                GridDimensions = _gridDimensions[0],
//                QueryPositionsPtr = (float2*)queryPoints.GetUnsafePtr(),
//                SortedPositionsPtr = (float2*)_sortedPositions.GetUnsafePtr(),
//                HashIndexPtr = (int2*)_hashIndex.GetUnsafePtr(),
//                CellStartEndPtr = (int2*)_cellStartEnd.AsArray().GetUnsafePtr(),
//                ResultsPtr = (int*)results.GetUnsafePtr()
//            };
//            var handle = ZeroAllocJobScheduler.ScheduleParallelFor(job, queryPoints.Length, 0, default, null);
//            handle.Complete();
//        }
//        return results;
//    }

//    public void Dispose()
//    {
//        DisposeGridData();
//    }

//    private void DisposeGridData()
//    {
//        if (_positions.IsCreated) _positions.Dispose();
//        if (_hashIndex.IsCreated) _hashIndex.Dispose();
//        if (_cellStartEnd.IsCreated) _cellStartEnd.Dispose();
//        if (_sortedPositions.IsCreated) _sortedPositions.Dispose();
//        if (_minMaxPositions.IsCreated) _minMaxPositions.Dispose();
//        if (_gridDimensions.IsCreated) _gridDimensions.Dispose();
//        if (_gridResolution.IsCreated) _gridResolution.Dispose();
//        if (_countsScratch.IsCreated) _countsScratch.Dispose();
//        if (_sortedHashIndexScratch.IsCreated) _sortedHashIndexScratch.Dispose();
//    }

//    // ---------- 私有方法 ----------

//    private unsafe JobHandle InitializeGridInternal()
//    {
//        if (_positions.Length == 0)
//            throw new Exception("位置数组为空");
//        //_minMaxPositions = new NativeArray<float2>(2, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

//        //_gridDimensions = new NativeArray<int2>(1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

//        // 重用或创建 minMaxPositions 和 gridDimensions
//        if (!_minMaxPositions.IsCreated || _minMaxPositions.Length != 2)
//        {
//            if (_minMaxPositions.IsCreated) _minMaxPositions.Dispose();
//            _minMaxPositions = new NativeArray<float2>(2, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
//        }

//        if (!_gridDimensions.IsCreated || _gridDimensions.Length != 1)
//        {
//            if (_gridDimensions.IsCreated) _gridDimensions.Dispose();
//            _gridDimensions = new NativeArray<int2>(1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
//        }

//        // 1. 计算包围盒并确定网格维度
//        unsafe
//        {
//            var initJob = new GridInitializationJobPointer
//            {
//                PositionsPtr = (float2*)_positions.GetUnsafePtr(),
//                PositionLength = _positions.Length,
//                GridResolutionPtr = (float*)_gridResolution.GetUnsafePtr(),
//                TargetGridSize = _targetGridSize,
//                MinMaxPositionsPtr = (float2*)_minMaxPositions.GetUnsafePtr(),
//                GridDimensionsPtr = (int2*)_gridDimensions.GetUnsafePtr(),
//                CellStartEnd = _cellStartEnd
//            };
//            var initHandle = ZeroAllocJobScheduler.Schedule(initJob, default);
//            initHandle.Complete();
//        }

//        int2 dim = _gridDimensions[0];
//        int cellCount = dim.x * dim.y;
//        _cellStartEnd.Resize(cellCount, NativeArrayOptions.UninitializedMemory);

//        // 确保临时缓冲区足够大
//        EnsureScratchBuffers(cellCount, _positions.Length);

//        // 2. 为每个点分配哈希值并计数
//        var assignHashJob = new AssignHashAndCountJobPointer
//        {
//            PositionsPtr = (float2*)_positions.GetUnsafePtr(),
//            HashIndexPtr = (int2*)_hashIndex.GetUnsafePtr(),
//            CountsPtr = (int*)_countsScratch.GetUnsafePtr(),
//            Origin = _minMaxPositions[0],
//            InvRes = 1.0f / _gridResolution[0],
//            Dim = _gridDimensions[0],
//            MaxHash = cellCount - 1
//        };
//        var assignHandle = ZeroAllocJobScheduler.ScheduleParallelFor(assignHashJob, _positions.Length, 0, default(JobHandle), null);

//        // 3. 计算前缀和
//        var prefixJob = new PrefixSumJobPointer
//        {
//            CountsPtr = (int*)_countsScratch.GetUnsafePtr(),
//            Length = cellCount
//        };
//        var prefixHandle = ZeroAllocJobScheduler.Schedule(prefixJob, assignHandle);

//        // 4. 放置元素到排序位置
//        var placeJob = new PlaceElementsJobPointer
//        {
//            OriginalHashIndexPtr = (int2*)_hashIndex.GetUnsafePtr(),
//            PositionsPtr = (float2*)_positions.GetUnsafePtr(),
//            SortedPositionsPtr = (float2*)_sortedPositions.GetUnsafePtr(),
//            SortedHashIndexPtr = (int2*)_sortedHashIndexScratch.GetUnsafePtr(),
//            CountsPtr = (int*)_countsScratch.GetUnsafePtr()
//        };
//        var placeHandle = ZeroAllocJobScheduler.ScheduleParallelFor(placeJob, _hashIndex.Length, 0, prefixHandle, null);

//        // 5. 填充 cellStartEnd
//        var fillCellJob = new FillCellStartEndJobPointer
//        {
//            SortedHashIndexPtr = (int2*)_sortedHashIndexScratch.GetUnsafePtr(),
//            SortedLength = _sortedHashIndexScratch.Length,
//            CellStartEnd = _cellStartEnd
//        };
//        var fillHandle = ZeroAllocJobScheduler.Schedule(fillCellJob, placeHandle);

//        // 6. 将 sortedHashIndex 复制回 _hashIndex
//        var copyJob = new CopyHashIndexJobPointer
//        {
//            Src = (int2*)_sortedHashIndexScratch.GetUnsafePtr(),
//            Dst = (int2*)_hashIndex.GetUnsafePtr()
//        };
//        var copyHandle = ZeroAllocJobScheduler.ScheduleParallelFor(copyJob, _sortedHashIndexScratch.Length, 0, fillHandle, null);

//        return copyHandle;
//    }

//    private void EnsureScratchBuffers(int cellCount, int positionCount)
//    {
//        // 确保 countsScratch 足够大
//        if (!_countsScratch.IsCreated || _countsScratch.Length < cellCount)
//        {
//            if (_countsScratch.IsCreated) _countsScratch.Dispose();
//            _countsScratch = new NativeArray<int>(cellCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
//        }
//        else if (_countsScratch.Length > cellCount)
//        {
//            // 如果现有数组太大，重新分配以避免浪费内存
//            _countsScratch.Dispose();
//            _countsScratch = new NativeArray<int>(cellCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
//        }

//        // 确保 sortedHashIndexScratch 足够大
//        if (!_sortedHashIndexScratch.IsCreated || _sortedHashIndexScratch.Length < positionCount)
//        {
//            if (_sortedHashIndexScratch.IsCreated) _sortedHashIndexScratch.Dispose();
//            _sortedHashIndexScratch = new NativeArray<int2>(positionCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
//        }
//        else if (_sortedHashIndexScratch.Length > positionCount)
//        {
//            // 如果现有数组太大，重新分配以避免浪费内存
//            _sortedHashIndexScratch.Dispose();
//            _sortedHashIndexScratch = new NativeArray<int2>(positionCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
//        }
//    }

//    // ---------- 坐标转换辅助 ----------
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

//    // ---------- 内部 Job 定义（使用指针） ----------

//    [StructLayout(LayoutKind.Sequential)]
//    private unsafe struct GridInitializationJobPointer : IJob
//    {
//        public float2* PositionsPtr;
//        public int PositionLength;
//        public float* GridResolutionPtr;
//        public int TargetGridSize;
//        public float2* MinMaxPositionsPtr;
//        public int2* GridDimensionsPtr;
//        public NativeList<int2> CellStartEnd;

//        public void Execute()
//        {
//            float2 min = PositionsPtr[0];
//            float2 max = PositionsPtr[0];
//            for (int i = 1; i < PositionLength; i++)
//            {
//                min = math.min(min, PositionsPtr[i]);
//                max = math.max(max, PositionsPtr[i]);
//            }
//            MinMaxPositionsPtr[0] = min;
//            MinMaxPositionsPtr[1] = max;

//            float2 range = max - min;
//            float maxRange = math.max(range.x, range.y);

//            float resolution = GridResolutionPtr[0];
//            if (resolution <= 0f)
//            {
//                // 自动计算分辨率
//                resolution = maxRange / TargetGridSize;
//                GridResolutionPtr[0] = resolution; // 更新为实际使用的分辨率
//            }

//            int2 dim = new int2(
//                math.max(1, (int)math.ceil(range.x / resolution)),
//                math.max(1, (int)math.ceil(range.y / resolution))
//            );

//            if (dim.x > MAX_GRID_SIZE || dim.y > MAX_GRID_SIZE)
//                throw new Exception($"网格维度 {dim} 超过最大允许值 {MAX_GRID_SIZE}，请增大分辨率或调整范围。");

//            GridDimensionsPtr[0] = dim;
//            int cellCount = dim.x * dim.y;
//            CellStartEnd.Resize(cellCount, NativeArrayOptions.ClearMemory);
//        }
//    }

//    [StructLayout(LayoutKind.Sequential)]
//    private unsafe struct AssignHashAndCountJobPointer : IJobParallelFor
//    {
//        public float2* PositionsPtr;
//        public int2* HashIndexPtr;
//        public int* CountsPtr;
//        public float2 Origin;
//        public float InvRes;
//        public int2 Dim;
//        public int MaxHash;

//        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
//        public void Execute(int index)
//        {
//            float2 p = PositionsPtr[index];
//            float cellXF = (p.x - Origin.x) * InvRes;
//            float cellYF = (p.y - Origin.y) * InvRes;
//            int cellX = (int)cellXF;
//            int cellY = (int)cellYF;

//            // 使用无符号比较进行边界检查（更快）
//            if ((uint)cellX >= (uint)Dim.x)
//                cellX = cellX < 0 ? 0 : Dim.x - 1;
//            if ((uint)cellY >= (uint)Dim.y)
//                cellY = cellY < 0 ? 0 : Dim.y - 1;

//            int hash = cellY * Dim.x + cellX;
//            if ((uint)hash > (uint)MaxHash)
//                hash = hash < 0 ? 0 : MaxHash;

//            HashIndexPtr[index] = new int2(hash, index);
//            Interlocked.Increment(ref CountsPtr[hash]);
//        }
//    }

//    [StructLayout(LayoutKind.Sequential)]
//    private unsafe struct PrefixSumJobPointer : IJob
//    {
//        public int* CountsPtr;
//        public int Length;

//        public void Execute()
//        {
//            int sum = 0;
//            for (int i = 0; i < Length; i++)
//            {
//                int c = CountsPtr[i];
//                CountsPtr[i] = sum;
//                sum += c;
//            }
//        }
//    }

//    [StructLayout(LayoutKind.Sequential)]
//    private unsafe struct PlaceElementsJobPointer : IJobParallelFor
//    {
//        public int2* OriginalHashIndexPtr;
//        public float2* PositionsPtr;
//        public float2* SortedPositionsPtr;
//        public int2* SortedHashIndexPtr;
//        public int* CountsPtr;

//        public void Execute(int index)
//        {
//            int2 entry = OriginalHashIndexPtr[index];
//            int hash = entry.x;
//            int origIdx = entry.y;
//            int destIdx = Interlocked.Add(ref CountsPtr[hash], 1) - 1;
//            SortedPositionsPtr[destIdx] = PositionsPtr[origIdx];
//            SortedHashIndexPtr[destIdx] = new int2(hash, origIdx);
//        }
//    }

//    [StructLayout(LayoutKind.Sequential)]
//    private unsafe struct FillCellStartEndJobPointer : IJob
//    {
//        public int2* SortedHashIndexPtr;
//        public int SortedLength;
//        public NativeList<int2> CellStartEnd;

//        public void Execute()
//        {
//            // 初始化所有 CellStartEnd 为 (-1, -1) 表示空
//            for (int i = 0; i < CellStartEnd.Length; i++)
//            {
//                CellStartEnd[i] = new int2(-1, -1);
//            }

//            if (SortedLength == 0) return;

//            int currentHash = SortedHashIndexPtr[0].x;
//            int startIdx = 0;

//            for (int i = 1; i <= SortedLength; i++)
//            {
//                if (i == SortedLength || SortedHashIndexPtr[i].x != currentHash)
//                {
//                    // 记录 currentHash 的起止索引
//                    int2 se = new int2(startIdx, i);
//                    CellStartEnd[currentHash] = se;

//                    if (i < SortedLength)
//                    {
//                        currentHash = SortedHashIndexPtr[i].x;
//                        startIdx = i;
//                    }
//                }
//            }
//        }
//    }

//    [StructLayout(LayoutKind.Sequential)]
//    private unsafe struct CopyHashIndexJobPointer : IJobParallelFor
//    {
//        public int2* Src;
//        public int2* Dst;

//        public void Execute(int index)
//        {
//            Dst[index] = Src[index];
//        }
//    }

//    // ---------- 查询 Job（使用指针） ----------

//    [StructLayout(LayoutKind.Sequential)]
//    private unsafe struct ClosestPointJobPointer : IJobParallelFor
//    {
//        public float2 GridOrigin;
//        public float GridResolutionInv;
//        public int2 GridDimensions;
//        public float2* QueryPositionsPtr;
//        public float2* SortedPositionsPtr;
//        public int2* HashIndexPtr;
//        public int2* CellStartEndPtr;
//        public int SortedLength;
//        public int IgnoreSelf;
//        public float SquaredEpsilonSelf;
//        public int* ResultsPtr;

//        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
//        public void Execute(int index)
//        {
//            ResultsPtr[index] = -1;
//            float2 q = QueryPositionsPtr[index];
//            int2 cell = SpaceToGrid(q, GridOrigin, GridResolutionInv);
//            cell = math.clamp(cell, int2.zero, GridDimensions - 1);

//            float bestDistSq = float.MaxValue;
//            int bestIdx = -1;

//            // 遍历 3x3 邻域
//            for (int dx = -1; dx <= 1; dx++)
//            {
//                int nx = cell.x + dx;
//                if ((uint)nx >= (uint)GridDimensions.x) continue;

//                for (int dy = -1; dy <= 1; dy++)
//                {
//                    int ny = cell.y + dy;
//                    if ((uint)ny >= (uint)GridDimensions.y) continue;

//                    int2 neighborCell = new int2(nx, ny);
//                    int cellHash = Flatten2DTo1D(neighborCell, GridDimensions);
//                    int2 range = CellStartEndPtr[cellHash];
//                    int start = range.x;
//                    int end = range.y;

//                    if (start < 0) continue; // 网格为空

//                    for (int i = start; i < end; i++)
//                    {
//                        float2 pos = SortedPositionsPtr[i];
//                        float deltaX = q.x - pos.x;
//                        float deltaY = q.y - pos.y;
//                        float distSq = deltaX * deltaX + deltaY * deltaY;

//                        if (IgnoreSelf != 0 && distSq < SquaredEpsilonSelf)
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
//                ResultsPtr[index] = HashIndexPtr[bestIdx].y;
//            else
//            {
//                // 保底：遍历全部（极少发生）
//                for (int i = 0; i < SortedLength; i++)
//                {
//                    float2 pos = SortedPositionsPtr[i];
//                    float deltaX = q.x - pos.x;
//                    float deltaY = q.y - pos.y;
//                    float distSq = deltaX * deltaX + deltaY * deltaY;
//                    if (IgnoreSelf != 0 && distSq < SquaredEpsilonSelf) continue;
//                    if (distSq < bestDistSq)
//                    {
//                        bestDistSq = distSq;
//                        bestIdx = i;
//                    }
//                }
//                if (bestIdx != -1)
//                    ResultsPtr[index] = HashIndexPtr[bestIdx].y;
//            }
//        }
//    }

//    [StructLayout(LayoutKind.Sequential)]
//    private unsafe struct FindWithinJobPointer : IJobParallelFor
//    {
//        public float SquaredRadius;
//        public int MaxNeighbor;
//        public int CellsToLoop;
//        public float2 GridOrigin;
//        public float GridResolutionInv;
//        public int2 GridDimensions;
//        public float2* QueryPositionsPtr;
//        public float2* SortedPositionsPtr;
//        public int2* HashIndexPtr;
//        public int2* CellStartEndPtr;
//        public int* ResultsPtr;

//        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
//        public void Execute(int index)
//        {
//            int baseIdx = index * MaxNeighbor;
//            for (int i = 0; i < MaxNeighbor; i++)
//                ResultsPtr[baseIdx + i] = -1;

//            float2 q = QueryPositionsPtr[index];
//            int2 centerCell = SpaceToGrid(q, GridOrigin, GridResolutionInv);
//            centerCell = math.clamp(centerCell, int2.zero, GridDimensions - 1);

//            int found = 0;

//            // 先查中心格
//            int centerHash = Flatten2DTo1D(centerCell, GridDimensions);
//            int2 centerRange = CellStartEndPtr[centerHash];
//            int start = centerRange.x;
//            int end = centerRange.y;
//            if (start >= 0)
//            {
//                for (int iCell = start; iCell < end; iCell++)
//                {
//                    float2 pos = SortedPositionsPtr[iCell];
//                    float deltaX = q.x - pos.x;
//                    float deltaY = q.y - pos.y;
//                    float distSq = deltaX * deltaX + deltaY * deltaY;
//                    if (distSq <= SquaredRadius)
//                    {
//                        ResultsPtr[baseIdx + found] = HashIndexPtr[iCell].y;
//                        found++;
//                        if (found == MaxNeighbor) return;
//                    }
//                }
//            }

//            // 再查周围 CellsToLoop 层
//            for (int dx = -CellsToLoop; dx <= CellsToLoop; dx++)
//            {
//                int nx = centerCell.x + dx;
//                if ((uint)nx >= (uint)GridDimensions.x) continue;
//                for (int dy = -CellsToLoop; dy <= CellsToLoop; dy++)
//                {
//                    if (dx == 0 && dy == 0) continue; // 已查

//                    int ny = centerCell.y + dy;
//                    if ((uint)ny >= (uint)GridDimensions.y) continue;

//                    int2 cell = new int2(nx, ny);
//                    int hash = Flatten2DTo1D(cell, GridDimensions);
//                    int2 range = CellStartEndPtr[hash];
//                    int s = range.x;
//                    int e = range.y;
//                    if (s < 0) continue;

//                    for (int iCell = s; iCell < e; iCell++)
//                    {
//                        float2 pos = SortedPositionsPtr[iCell];
//                        float deltaX = q.x - pos.x;
//                        float deltaY = q.y - pos.y;
//                        float distSq = deltaX * deltaX + deltaY * deltaY;
//                        if (distSq <= SquaredRadius)
//                        {
//                            ResultsPtr[baseIdx + found] = HashIndexPtr[iCell].y;
//                            found++;
//                            if (found == MaxNeighbor) return;
//                        }
//                    }
//                }
//            }
//        }
//    }
//}
