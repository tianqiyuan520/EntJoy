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
///// 二维网格空间索引（批大小0优化版，无Unity专用属性）
///// 构建阶段通过指针减少开销，计数仍使用原子操作。
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
//        _hashIndex = new NativeArray<int2>(positions.Length, Allocator.Persistent);
//        _sortedX = new NativeArray<float>(positions.Length, Allocator.Persistent);
//        _sortedY = new NativeArray<float>(positions.Length, Allocator.Persistent);
//        _cellStartEnd = new NativeList<int2>(Allocator.Persistent);

//        // 使用循环进行复制
//        for (int i = 0; i < positions.Length; i++)
//        {
//            var pos = positions[i];
//            _positions[i] = new float2(pos.X, pos.Y);
//        }

//        return InitializeGridInternal();
//    }

//    public JobHandle InitializeGrid(float2[] positions)
//    {
//        Dispose();
//        _gridResolution = new NativeArray<float>(1, Allocator.Persistent) { [0] = _resolution };
//        _positions = new NativeArray<float2>(positions.Length, Allocator.Persistent);
//        _hashIndex = new NativeArray<int2>(positions.Length, Allocator.Persistent);
//        _sortedX = new NativeArray<float>(positions.Length, Allocator.Persistent);
//        _sortedY = new NativeArray<float>(positions.Length, Allocator.Persistent);
//        _cellStartEnd = new NativeList<int2>(Allocator.Persistent);

//        // 使用循环进行复制
//        for (int i = 0; i < positions.Length; i++)
//        {
//            _positions[i] = positions[i];
//        }

//        return InitializeGridInternal();
//    }

//    public JobHandle InitializeGrid(NativeArray<float2> positions)
//    {
//        // 转换为数组
//        float2[] array = new float2[positions.Length];
//        positions.CopyTo(array);
//        return InitializeGrid(array);
//    }

//    // ---------- 查询接口 ----------
//    public int[] SearchClosestPoint(Vector3[] queryPoints, bool ignoreSelf = false, float epsilon = 0.001f)
//    {
//        NativeArray<float2> qPoints = new NativeArray<float2>(queryPoints.Length, Allocator.TempJob);
//        NativeArray<int> results = new NativeArray<int>(queryPoints.Length, Allocator.TempJob);

//        try
//        {
//            // 使用循环进行复制
//            for (int i = 0; i < queryPoints.Length; i++)
//            {
//                var q = queryPoints[i];
//                qPoints[i] = new float2(q.X, q.Y);
//            }

//            SearchClosestPointInternal(qPoints, results, ignoreSelf, epsilon);

//            int[] res = new int[results.Length];
//            results.CopyTo(res);
//            return res;
//        }
//        finally
//        {
//            qPoints.Dispose();
//            results.Dispose();
//        }
//    }

//    public int[] SearchClosestPoint(float2[] queryPoints, bool ignoreSelf = false, float epsilon = 0.001f)
//    {
//        NativeArray<float2> qPoints = new NativeArray<float2>(queryPoints.Length, Allocator.TempJob);
//        NativeArray<int> results = new NativeArray<int>(queryPoints.Length, Allocator.TempJob);

//        try
//        {
//            // 使用循环进行复制
//            for (int i = 0; i < queryPoints.Length; i++)
//            {
//                qPoints[i] = queryPoints[i];
//            }

//            SearchClosestPointInternal(qPoints, results, ignoreSelf, epsilon);

//            int[] res = new int[results.Length];
//            results.CopyTo(res);
//            return res;
//        }
//        finally
//        {
//            qPoints.Dispose();
//            results.Dispose();
//        }
//    }

//    public NativeArray<int> SearchClosestPoint(NativeArray<float2> queryPoints, bool ignoreSelf = false, float epsilon = 0.001f)
//    {
//        NativeArray<int> results = new NativeArray<int>(queryPoints.Length, Allocator.TempJob);
//        SearchClosestPointInternal(queryPoints, results, ignoreSelf, epsilon);
//        return results;
//    }

//    private unsafe void SearchClosestPointInternal(NativeArray<float2> queryPoints, NativeArray<int> results, bool ignoreSelf, float epsilon)
//    {
//        // 优化：根据查询点数量动态调整批处理大小
//        int batchSize = queryPoints.Length < 1000 ? 32 :
//                       queryPoints.Length < 10000 ? 64 :
//                       queryPoints.Length < 100000 ? 128 : 256;

//        var job = new ClosestPointJob
//        {
//            GridOrigin = _minMaxPositions[0],
//            GridResolutionInv = 1.0f / _gridResolution[0],
//            GridDimensions = _gridDimensions[0],
//            QueryPositionsPtr = (float2*)queryPoints.GetUnsafePtr(),
//            QueryPositionsLength = queryPoints.Length,
//            SortedXPtr = (float*)_sortedX.GetUnsafePtr(),
//            SortedYPtr = (float*)_sortedY.GetUnsafePtr(),
//            SortedLength = _sortedX.Length,
//            HashIndexPtr = (int2*)_hashIndex.GetUnsafePtr(),
//            CellStartEndPtr = (int2*)_cellStartEnd.GetUnsafePtr(),
//            CellStartEndLength = _cellStartEnd.Length,
//            ResultsPtr = (int*)results.GetUnsafePtr(),
//            IgnoreSelf = ignoreSelf,
//            SquaredEpsilonSelf = epsilon * epsilon
//        };

//        var handle = job.Schedule(queryPoints.Length, batchSize);
//        handle.Complete();
//    }

//    public int[] SearchWithin(Vector3[] queryPoints, float radius, int maxNeighborPerQuery)
//    {
//        NativeArray<float2> qPoints = new NativeArray<float2>(queryPoints.Length, Allocator.TempJob);
//        NativeArray<int> results = new NativeArray<int>(queryPoints.Length * maxNeighborPerQuery, Allocator.TempJob);

//        try
//        {
//            // 使用循环进行复制
//            for (int i = 0; i < queryPoints.Length; i++)
//            {
//                var q = queryPoints[i];
//                qPoints[i] = new float2(q.X, q.Y);
//            }

//            SearchWithinInternal(qPoints, radius, maxNeighborPerQuery, results);

//            int[] res = new int[results.Length];
//            results.CopyTo(res);
//            return res;
//        }
//        finally
//        {
//            qPoints.Dispose();
//            results.Dispose();
//        }
//    }

//    public int[] SearchWithin(float2[] queryPoints, float radius, int maxNeighborPerQuery)
//    {
//        NativeArray<float2> qPoints = new NativeArray<float2>(queryPoints.Length, Allocator.TempJob);
//        NativeArray<int> results = new NativeArray<int>(queryPoints.Length * maxNeighborPerQuery, Allocator.TempJob);

//        try
//        {
//            // 使用循环进行复制
//            for (int i = 0; i < queryPoints.Length; i++)
//            {
//                qPoints[i] = queryPoints[i];
//            }

//            SearchWithinInternal(qPoints, radius, maxNeighborPerQuery, results);

//            int[] res = new int[results.Length];
//            results.CopyTo(res);
//            return res;
//        }
//        finally
//        {
//            qPoints.Dispose();
//            results.Dispose();
//        }
//    }

//    public NativeArray<int> SearchWithin(NativeArray<float2> queryPoints, float radius, int maxNeighborPerQuery)
//    {
//        NativeArray<int> results = new NativeArray<int>(queryPoints.Length * maxNeighborPerQuery, Allocator.TempJob);
//        SearchWithinInternal(queryPoints, radius, maxNeighborPerQuery, results);
//        return results;
//    }

//    private unsafe void SearchWithinInternal(NativeArray<float2> queryPoints, float radius, int maxNeighborPerQuery, NativeArray<int> results)
//    {
//        // 优化：根据查询点数量动态调整批处理大小
//        int batchSize = queryPoints.Length < 1000 ? 32 :
//                       queryPoints.Length < 10000 ? 64 :
//                       queryPoints.Length < 100000 ? 128 : 256;

//        int cellsToLoop = (int)math.ceil(radius / _gridResolution[0]);
//        var job = new FindWithinJob
//        {
//            SquaredRadius = radius * radius,
//            MaxNeighbor = maxNeighborPerQuery,
//            CellsToLoop = cellsToLoop,
//            GridOrigin = _minMaxPositions[0],
//            GridResolutionInv = 1.0f / _gridResolution[0],
//            GridDimensions = _gridDimensions[0],
//            QueryPositionsPtr = (float2*)queryPoints.GetUnsafePtr(),
//            QueryPositionsLength = queryPoints.Length,
//            SortedXPtr = (float*)_sortedX.GetUnsafePtr(),
//            SortedYPtr = (float*)_sortedY.GetUnsafePtr(),
//            SortedLength = _sortedX.Length,
//            HashIndexPtr = (int2*)_hashIndex.GetUnsafePtr(),
//            CellStartEndPtr = (int2*)_cellStartEnd.GetUnsafePtr(),
//            CellStartEndLength = _cellStartEnd.Length,
//            ResultsPtr = (int*)results.GetUnsafePtr()
//        };

//        var handle = job.Schedule(queryPoints.Length, batchSize);
//        handle.Complete();
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

//    // ---------- 私有构建方法（优化重点） ----------
//    private unsafe JobHandle InitializeGridInternal()
//    {
//        if (_positions.Length == 0)
//            throw new Exception("位置数组为空");

//        if (_minMaxPositions.IsCreated) _minMaxPositions.Dispose();
//        if (_gridDimensions.IsCreated) _gridDimensions.Dispose();

//        _minMaxPositions = new NativeArray<float2>(2, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
//        _gridDimensions = new NativeArray<int2>(1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

//        // 1. 计算包围盒并确定网格维度（使用 SIMD 加速）
//        var initJob = new GridInitializationSimdJob
//        {
//            PositionsPtr = (float2*)_positions.GetUnsafePtr(),
//            PositionsLength = _positions.Length,
//            GridResolutionPtr = (float*)_gridResolution.GetUnsafePtr(),
//            TargetGridSize = _targetGridSize,
//            MinMaxPositionsPtr = (float2*)_minMaxPositions.GetUnsafePtr(),
//            GridDimensionsPtr = (int2*)_gridDimensions.GetUnsafePtr()
//        };
//        var handle = initJob.Schedule();
//        handle.Complete();

//        // 根据实际网格维度调整_cellStartEnd大小
//        int2 dim = _gridDimensions[0];
//        int cellCount = dim.x * dim.y;
//        _cellStartEnd.Resize(cellCount, NativeArrayOptions.ClearMemory);

//        // 2. 为每个点分配哈希值（使用指针）
//        var assignJob = new AssignHashJob
//        {
//            Origin = _minMaxPositions[0],
//            InvRes = 1.0f / _gridResolution[0],
//            Dim = _gridDimensions[0],
//            PositionsPtr = (float2*)_positions.GetUnsafePtr(),
//            HashIndexPtr = (int2*)_hashIndex.GetUnsafePtr()
//        };
//        handle = assignJob.Schedule(_positions.Length, 64, handle);

//        // 3. 计数（原子操作，无法避免竞争，但使用指针减少开销）
//        NativeArray<int> counts = new NativeArray<int>(cellCount, Allocator.TempJob, NativeArrayOptions.ClearMemory);
//        var countJob = new CountHashJob
//        {
//            HashIndexPtr = (int2*)_hashIndex.GetUnsafePtr(),
//            CountsPtr = (int*)counts.GetUnsafePtr()
//        };
//        handle = countJob.Schedule(_hashIndex.Length, 64, handle);

//        // 4. 前缀和（标量）
//        var prefixJob = new PrefixSumJob
//        {
//            CountsPtr = (int*)counts.GetUnsafePtr(),
//            CountsLength = counts.Length
//        };
//        handle = prefixJob.Schedule(handle);

//        NativeArray<int2> sortedHashIndex = new NativeArray<int2>(_hashIndex.Length, Allocator.TempJob);
//        var placeJob = new PlaceElementsJob
//        {
//            OriginalHashIndexPtr = (int2*)_hashIndex.GetUnsafePtr(),
//            PositionsPtr = (float2*)_positions.GetUnsafePtr(),
//            SortedXPtr = (float*)_sortedX.GetUnsafePtr(),
//            SortedYPtr = (float*)_sortedY.GetUnsafePtr(),
//            SortedHashIndexPtr = (int2*)sortedHashIndex.GetUnsafePtr(),
//            CountsPtr = (int*)counts.GetUnsafePtr()
//        };
//        handle = placeJob.Schedule(_hashIndex.Length, 64, handle);

//        // 5. 合并填充 CellStartEnd 和复制回 HashIndex
//        var finalizeJob = new FinalizeJob
//        {
//            SortedHashIndexPtr = (int2*)sortedHashIndex.GetUnsafePtr(),
//            SortedLength = sortedHashIndex.Length,
//            CellStartEndPtr = (int2*)_cellStartEnd.GetUnsafePtr(),
//            CellStartEndLength = _cellStartEnd.Length,
//            DstHashIndexPtr = (int2*)_hashIndex.GetUnsafePtr()
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

//    private unsafe struct GridInitializationSimdJob : IJob
//    {
//        public float2* PositionsPtr;
//        public int PositionsLength;
//        public float* GridResolutionPtr;
//        public int TargetGridSize;
//        public float2* MinMaxPositionsPtr;
//        public int2* GridDimensionsPtr;

//        public void Execute()
//        {
//            if (PositionsLength == 0) return;

//            float2 min, max;
//            ComputeBoundsSimd(PositionsPtr, PositionsLength, out min, out max);
//            MinMaxPositionsPtr[0] = min;
//            MinMaxPositionsPtr[1] = max;

//            float2 range = max - min;
//            float maxRange = math.max(range.x, range.y);
//            float resolution = GridResolutionPtr[0];
//            if (resolution <= 0f)
//            {
//                resolution = maxRange / TargetGridSize;
//                GridResolutionPtr[0] = resolution;
//            }

//            int2 dim = new int2(
//                math.max(1, (int)math.ceil(range.x / resolution)),
//                math.max(1, (int)math.ceil(range.y / resolution))
//            );

//            if (dim.x > MAX_GRID_SIZE || dim.y > MAX_GRID_SIZE)
//                throw new Exception($"网格维度 {dim} 超过最大允许值 {MAX_GRID_SIZE}");

//            GridDimensionsPtr[0] = dim;
//        }

//        private static unsafe void ComputeBoundsSimd(float2* positions, int length, out float2 min, out float2 max)
//        {
//            if (length == 0)
//            {
//                min = max = float2.zero;
//                return;
//            }

//            // 使用简单但高效的循环（经过验证的性能更好）
//            float minX = positions[0].x, minY = positions[0].y;
//            float maxX = positions[0].x, maxY = positions[0].y;

//            // 手动展开4次循环以获得更好性能
//            int i = 1;
//            int lenMinus3 = length - 3;

//            // 手动展开4次循环
//            for (; i <= lenMinus3; i += 4)
//            {
//                // 预取数据
//                var p0 = positions[i];
//                var p1 = positions[i + 1];
//                var p2 = positions[i + 2];
//                var p3 = positions[i + 3];

//                // 计算最小值
//                minX = Math.Min(minX, p0.x);
//                minX = Math.Min(minX, p1.x);
//                minX = Math.Min(minX, p2.x);
//                minX = Math.Min(minX, p3.x);

//                minY = Math.Min(minY, p0.y);
//                minY = Math.Min(minY, p1.y);
//                minY = Math.Min(minY, p2.y);
//                minY = Math.Min(minY, p3.y);

//                // 计算最大值
//                maxX = Math.Max(maxX, p0.x);
//                maxX = Math.Max(maxX, p1.x);
//                maxX = Math.Max(maxX, p2.x);
//                maxX = Math.Max(maxX, p3.x);

//                maxY = Math.Max(maxY, p0.y);
//                maxY = Math.Max(maxY, p1.y);
//                maxY = Math.Max(maxY, p2.y);
//                maxY = Math.Max(maxY, p3.y);
//            }

//            // 处理剩余元素
//            for (; i < length; i++)
//            {
//                var p = positions[i];
//                minX = Math.Min(minX, p.x);
//                minY = Math.Min(minY, p.y);
//                maxX = Math.Max(maxX, p.x);
//                maxY = Math.Max(maxY, p.y);
//            }

//            min = new float2(minX, minY);
//            max = new float2(maxX, maxY);
//        }
//    }

//    private unsafe struct AssignHashJob : IJobParallelFor
//    {
//        public float2 Origin;
//        public float InvRes;
//        public int2 Dim;
//        public float2* PositionsPtr;
//        public int2* HashIndexPtr;

//        public void Execute(int index)
//        {
//            float2 p = PositionsPtr[index];
//            int2 cell = SpaceToGrid(p, Origin, InvRes);
//            cell = math.clamp(cell, int2.zero, Dim - 1);
//            int hash = Flatten2DTo1D(cell, Dim);
//            HashIndexPtr[index] = new int2(hash, index);
//        }
//    }

//    private unsafe struct CountHashJob : IJobParallelFor
//    {
//        public int2* HashIndexPtr;
//        public int* CountsPtr;

//        public void Execute(int index)
//        {
//            int hash = HashIndexPtr[index].x;
//            Interlocked.Increment(ref CountsPtr[hash]);
//        }
//    }

//    private unsafe struct PrefixSumJob : IJob
//    {
//        public int* CountsPtr;
//        public int CountsLength;

//        public void Execute()
//        {
//            int sum = 0;
//            for (int i = 0; i < CountsLength; i++)
//            {
//                int c = CountsPtr[i];
//                CountsPtr[i] = sum;
//                sum += c;
//            }
//        }
//    }

//    private unsafe struct PlaceElementsJob : IJobParallelFor
//    {
//        public int2* OriginalHashIndexPtr;
//        public float2* PositionsPtr;
//        public float* SortedXPtr;
//        public float* SortedYPtr;
//        public int2* SortedHashIndexPtr;
//        public int* CountsPtr; // 前缀和，将被原子递增

//        public void Execute(int index)
//        {
//            int2 entry = OriginalHashIndexPtr[index];
//            int hash = entry.x;
//            int origIdx = entry.y;

//            int destIdx = Interlocked.Add(ref CountsPtr[hash], 1) - 1;

//            float2 p = PositionsPtr[origIdx];
//            SortedXPtr[destIdx] = p.x;
//            SortedYPtr[destIdx] = p.y;
//            SortedHashIndexPtr[destIdx] = new int2(hash, origIdx);
//        }
//    }

//    private unsafe struct FinalizeJob : IJob
//    {
//        public int2* SortedHashIndexPtr;
//        public int SortedLength;
//        public int2* CellStartEndPtr;
//        public int CellStartEndLength;
//        public int2* DstHashIndexPtr;

//        public void Execute()
//        {
//            // 初始化 CellStartEnd 为 -1
//            for (int i = 0; i < CellStartEndLength; i++)
//                CellStartEndPtr[i] = new int2(-1, -1);

//            if (SortedLength == 0) return;

//            int currentHash = SortedHashIndexPtr[0].x;
//            int startIdx = 0;
//            for (int i = 1; i <= SortedLength; i++)
//            {
//                if (i == SortedLength || SortedHashIndexPtr[i].x != currentHash)
//                {
//                    CellStartEndPtr[currentHash] = new int2(startIdx, i);
//                    if (i < SortedLength)
//                    {
//                        currentHash = SortedHashIndexPtr[i].x;
//                        startIdx = i;
//                    }
//                }
//            }

//            // 复制回 DstHashIndex
//            for (int i = 0; i < SortedLength; i++)
//                DstHashIndexPtr[i] = SortedHashIndexPtr[i];
//        }
//    }

//    // ---------- 查询 Job（指针化版本）----------
//    private unsafe struct ClosestPointJob : IJobParallelFor
//    {
//        public float2 GridOrigin;
//        public float GridResolutionInv;
//        public int2 GridDimensions;
//        public float2* QueryPositionsPtr;
//        public int QueryPositionsLength;
//        public float* SortedXPtr;
//        public float* SortedYPtr;
//        public int SortedLength;
//        public int2* HashIndexPtr;
//        public int2* CellStartEndPtr;
//        public int CellStartEndLength;
//        public bool IgnoreSelf;
//        public float SquaredEpsilonSelf;
//        public int* ResultsPtr;

//        public void Execute(int index)
//        {
//            ResultsPtr[index] = -1;
//            float2 q = QueryPositionsPtr[index];
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
//                    if (cellHash >= CellStartEndLength) continue;

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
//                ResultsPtr[index] = HashIndexPtr[bestIdx].y;
//            else
//            {
//                for (int i = 0; i < SortedLength; i++)
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
//                    ResultsPtr[index] = HashIndexPtr[bestIdx].y;
//            }
//        }

//        [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
//        private unsafe int FindClosestAvx2(float qx, float qy, float* xs, float* ys, int count, int startIdx)
//        {
//            // 简化但高效的SIMD最小距离查找
//            var vqx = Vector256.Create(qx);
//            var vqy = Vector256.Create(qy);
//            var vbestDist = Vector256.Create(float.MaxValue);
//            var vbestIdx = Vector256.Create(-1f);

//            // 预计算索引向量
//            var vidxBase = Vector256.Create(0f, 1f, 2f, 3f, 4f, 5f, 6f, 7f);
//            var vstep = Vector256.Create(8f);
//            var vidx = vidxBase;

//            int i = 0;

//            // 简单高效的SIMD循环
//            for (; i <= count - Vector256<float>.Count; i += Vector256<float>.Count)
//            {
//                // 使用非对齐加载
//                var vx = Avx.LoadVector256(xs + i);
//                var vy = Avx.LoadVector256(ys + i);

//                // 计算距离平方
//                var dx = Avx.Subtract(vx, vqx);
//                var dy = Avx.Subtract(vy, vqy);
//                var dxSq = Avx.Multiply(dx, dx);
//                var dySq = Avx.Multiply(dy, dy);
//                var distSq = Avx.Add(dxSq, dySq);

//                // 比较并更新最小距离
//                var mask = Avx.Compare(distSq, vbestDist, FloatComparisonMode.OrderedLessThanNonSignaling);
//                vbestDist = Avx.BlendVariable(vbestDist, distSq, mask);

//                // 更新索引
//                var currentVidx = Avx.Add(vidx, Vector256.Create((float)i));
//                vbestIdx = Avx.BlendVariable(vbestIdx, currentVidx, mask);

//                vidx = Avx.Add(vidx, vstep);
//            }

//            // 使用栈分配数组提取结果
//            const int vecSize = 8; // Vector256<float>.Count
//            float* distArray = stackalloc float[vecSize];
//            float* idxArray = stackalloc float[vecSize];

//            Avx.Store(distArray, vbestDist);
//            Avx.Store(idxArray, vbestIdx);

//            // 查找最小距离
//            float bestDist = float.MaxValue;
//            float bestIdxFloat = -1f;

//            for (int j = 0; j < vecSize; j++)
//            {
//                if (distArray[j] < bestDist)
//                {
//                    bestDist = distArray[j];
//                    bestIdxFloat = idxArray[j];
//                }
//            }

//            int bestLocalIdx = bestIdxFloat >= 0 ? (int)(bestIdxFloat + 0.5f) : -1;

//            // 处理剩余元素
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

//    private unsafe struct FindWithinJob : IJobParallelFor
//    {
//        public float SquaredRadius;
//        public int MaxNeighbor;
//        public int CellsToLoop;
//        public float2 GridOrigin;
//        public float GridResolutionInv;
//        public int2 GridDimensions;
//        public float2* QueryPositionsPtr;
//        public int QueryPositionsLength;
//        public float* SortedXPtr;
//        public float* SortedYPtr;
//        public int SortedLength;
//        public int2* HashIndexPtr;
//        public int2* CellStartEndPtr;
//        public int CellStartEndLength;
//        public int* ResultsPtr;

//        public void Execute(int index)
//        {
//            int baseIdx = index * MaxNeighbor;
//            for (int i = 0; i < MaxNeighbor; i++)
//                ResultsPtr[baseIdx + i] = -1;

//            float2 q = QueryPositionsPtr[index];
//            float qx = q.x;
//            float qy = q.y;

//            int2 centerCell = SpaceToGrid(q, GridOrigin, GridResolutionInv);
//            centerCell = math.clamp(centerCell, int2.zero, GridDimensions - 1);

//            int found = 0;
//            int dimX = GridDimensions.x;

//            // 检查中心单元格
//            int centerHash = centerCell.y * dimX + centerCell.x;
//            if (centerHash >= CellStartEndLength) return;

//            int start = CellStartEndPtr[centerHash].x;
//            int end = CellStartEndPtr[centerHash].y;
//            if (start >= 0)
//            {
//                found = ProcessCellSIMD(qx, qy, start, end, baseIdx, found);
//                if (found == MaxNeighbor) return;
//            }

//            // 检查周围单元格
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
//                    if (cellHash >= CellStartEndLength) continue;

//                    int s = CellStartEndPtr[cellHash].x;
//                    int e = CellStartEndPtr[cellHash].y;
//                    if (s < 0) continue;

//                    found = ProcessCellSIMD(qx, qy, s, e, baseIdx, found);
//                    if (found == MaxNeighbor) return;
//                }
//            }
//        }

//        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
//        private unsafe int ProcessCellSIMD(float qx, float qy, int start, int end, int baseIdx, int found)
//        {
//            int count = end - start;
//            if (count <= 0) return found;

//            float* xs = SortedXPtr + start;
//            float* ys = SortedYPtr + start;

//            // 使用 SIMD 处理
//            if (Avx2.IsSupported && count >= Vector256<float>.Count)
//            {
//                return ProcessCellAvx2(qx, qy, xs, ys, count, start, baseIdx, found);
//            }
//            else
//            {
//                // 标量回退
//                for (int i = 0; i < count; i++)
//                {
//                    float dx = xs[i] - qx;
//                    float dy = ys[i] - qy;
//                    float distSq = dx * dx + dy * dy;
//                    if (distSq <= SquaredRadius)
//                    {
//                        ResultsPtr[baseIdx + found] = HashIndexPtr[start + i].y;
//                        found++;
//                        if (found == MaxNeighbor) return found;
//                    }
//                }
//            }

//            return found;
//        }

//        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
//        private unsafe int ProcessCellAvx2(float qx, float qy, float* xs, float* ys, int count, int start, int baseIdx, int found)
//        {
//            var vqx = Vector256.Create(qx);
//            var vqy = Vector256.Create(qy);
//            var vradius = Vector256.Create(SquaredRadius);

//            int i = 0;
//            for (; i <= count - Vector256<float>.Count; i += Vector256<float>.Count)
//            {
//                var vx = Avx.LoadVector256(xs + i);
//                var vy = Avx.LoadVector256(ys + i);
//                var dx = Avx.Subtract(vx, vqx);
//                var dy = Avx.Subtract(vy, vqy);
//                var distSq = Avx.Add(Avx.Multiply(dx, dx), Avx.Multiply(dy, dy));

//                // 比较距离是否小于等于半径
//                var mask = Avx.Compare(distSq, vradius, FloatComparisonMode.OrderedLessThanOrEqualNonSignaling);

//                // 提取掩码 - 注意：这里应该是mask.AsSingle()而不是distSq.AsSingle()
//                int maskInt = Avx.MoveMask(mask.AsSingle());

//                // 处理掩码中的每个位
//                if (maskInt != 0)
//                {
//                    // 使用位操作处理每个设置位
//                    int remainingMask = maskInt;
//                    while (remainingMask != 0)
//                    {
//                        int bitIndex = BitOperations.TrailingZeroCount(remainingMask);
//                        int idx = start + i + bitIndex;
//                        ResultsPtr[baseIdx + found] = HashIndexPtr[idx].y;
//                        found++;
//                        if (found == MaxNeighbor) return found;

//                        // 清除已处理的位
//                        remainingMask &= remainingMask - 1;
//                    }
//                }
//            }

//            // 处理剩余元素
//            for (; i < count; i++)
//            {
//                float dx = xs[i] - qx;
//                float dy = ys[i] - qy;
//                float distSq = dx * dx + dy * dy;
//                if (distSq <= SquaredRadius)
//                {
//                    ResultsPtr[baseIdx + found] = HashIndexPtr[start + i].y;
//                    found++;
//                    if (found == MaxNeighbor) return found;
//                }
//            }

//            return found;
//        }
//    }

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
