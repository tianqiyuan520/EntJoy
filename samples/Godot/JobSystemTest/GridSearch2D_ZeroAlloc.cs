//using EntJoy.Collections;
//using EntJoy.Mathematics;
//using Godot;
//using System;
//using System.Collections.Concurrent;
//using System.Numerics;
//using System.Runtime.CompilerServices;
//using System.Threading;
//using System.Threading.Tasks;
//using Vector3 = System.Numerics.Vector3;

///// <summary>
///// 二维网格空间索引（零分配优化版）
///// 集成ZeroAllocJobScheduler思想，完全避免堆分配
///// </summary>
//public class GridSearch2D_ZeroAlloc : IDisposable
//{
//    private const int MAX_GRID_SIZE = 1024;
//    private int _targetGridSize;
//    private float _resolution;

//    // 核心数据（SoA布局）
//    private NativeArray<float> _sortedX;
//    private NativeArray<float> _sortedY;
//    private NativeArray<int> _originalIndices;
//    private NativeArray<int> _cellStart;
//    private NativeArray<int> _cellEnd;
//    private float2 _gridMin;
//    private float2 _gridMax;
//    private int2 _gridDimensions;
//    private float _gridResolution;
//    private float _gridResolutionInv;

//    // 对象池（避免重复分配）
//    private static readonly ConcurrentBag<AssignCellJob> s_assignJobPool = new();
//    private static readonly ConcurrentBag<PrefixSumJob> s_prefixJobPool = new();
//    private static readonly ConcurrentBag<PlaceElementsJob> s_placeJobPool = new();
//    private static readonly ConcurrentBag<SetCellEndJob> s_setEndJobPool = new();

//    public GridSearch2D_ZeroAlloc(float resolution = -1f, int targetGrid = 32)
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
        
//        // 1. 计算包围盒
//        var bounds = ComputeBounds(positions);
//        _gridMin = bounds.min;
//        _gridMax = bounds.max;
        
//        // 2. 确定网格分辨率
//        float2 range = _gridMax - _gridMin;
//        float maxRange = math.max(range.x, range.y);
//        _gridResolution = _resolution > 0f ? _resolution : maxRange / _targetGridSize;
//        _gridResolutionInv = 1.0f / _gridResolution;
        
//        // 3. 计算网格维度
//        _gridDimensions = new int2(
//            math.max(1, (int)math.ceil(range.x / _gridResolution)),
//            math.max(1, (int)math.ceil(range.y / _gridResolution))
//        );
        
//        if (_gridDimensions.x > MAX_GRID_SIZE || _gridDimensions.y > MAX_GRID_SIZE)
//            throw new Exception($"网格维度 {_gridDimensions} 超过最大允许值 {MAX_GRID_SIZE}");
        
//        int cellCount = _gridDimensions.x * _gridDimensions.y;
        
//        // 4. 分配内存
//        _sortedX = new NativeArray<float>(positions.Length, Allocator.Persistent);
//        _sortedY = new NativeArray<float>(positions.Length, Allocator.Persistent);
//        _originalIndices = new NativeArray<int>(positions.Length, Allocator.Persistent);
//        _cellStart = new NativeArray<int>(cellCount, Allocator.Persistent);
//        _cellEnd = new NativeArray<int>(cellCount, Allocator.Persistent);
        
//        // 5. 初始化cell数组为-1
//        for (int i = 0; i < cellCount; i++)
//        {
//            _cellStart[i] = -1;
//            _cellEnd[i] = -1;
//        }
        
//        // 6. 构建网格（使用零分配作业）
//        return BuildGridZeroAlloc(positions);
//    }
    
//    private JobHandle BuildGridZeroAlloc(Vector3[] positions)
//    {
//        int length = positions.Length;
//        int cellCount = _gridDimensions.x * _gridDimensions.y;
        
//        // 1. 分配临时数组（使用TempJob避免长期分配）
//        NativeArray<int> cellIndices = new NativeArray<int>(length, Allocator.TempJob);
//        NativeArray<int> counts = new NativeArray<int>(cellCount, Allocator.TempJob, NativeArrayOptions.ClearMemory);
        
//        // 2. 计算每个点的单元格索引
//        var assignJob = new AssignCellJob
//        {
//            Positions = positions,
//            GridMin = _gridMin,
//            GridResolutionInv = _gridResolutionInv,
//            GridDimensions = _gridDimensions,
//            CellIndices = cellIndices,
//            Counts = counts
//        };
        
//        var handle = assignJob.Schedule(length, 128);
        
//        // 3. 前缀和
//        var prefixJob = new PrefixSumJob { Counts = counts };
//        handle = prefixJob.Schedule(handle);
        
//        // 4. 放置元素到排序数组
//        var placeJob = new PlaceElementsJob
//        {
//            Positions = positions,
//            CellIndices = cellIndices,
//            Counts = counts,
//            SortedX = _sortedX,
//            SortedY = _sortedY,
//            OriginalIndices = _originalIndices,
//            CellStart = _cellStart,
//            CellEnd = _cellEnd
//        };
        
//        handle = placeJob.Schedule(length, 128, handle);
        
//        // 5. 设置CellEnd
//        var setEndJob = new SetCellEndJob
//        {
//            Counts = counts,
//            CellStart = _cellStart,
//            CellEnd = _cellEnd
//        };
        
//        handle = setEndJob.Schedule(handle);
        
//        // 6. 清理临时数组
//        cellIndices.Dispose(handle);
//        counts.Dispose(handle);
        
//        return handle;
//    }
    
//    private static (float2 min, float2 max) ComputeBounds(Vector3[] positions)
//    {
//        float minX = float.MaxValue, minY = float.MaxValue;
//        float maxX = float.MinValue, maxY = float.MinValue;
        
//        // 手动展开循环，避免边界检查
//        int length = positions.Length;
//        for (int i = 0; i < length; i++)
//        {
//            float x = positions[i].X;
//            float y = positions[i].Y;
            
//            if (x < minX) minX = x;
//            if (y < minY) minY = y;
//            if (x > maxX) maxX = x;
//            if (y > maxY) maxY = y;
//        }
        
//        return (new float2(minX, minY), new float2(maxX, maxY));
//    }
    
//    // ---------- 查询接口（零分配优化） ----------
//    public int[] SearchClosestPoint(Vector3[] queryPoints, bool ignoreSelf = false, float epsilon = 0.001f)
//    {
//        int[] results = new int[queryPoints.Length];
        
//        // 使用栈分配临时变量，避免堆分配
//        int queryCount = queryPoints.Length;
//        int dimX = _gridDimensions.x;
//        int dimY = _gridDimensions.y;
//        float gridMinX = _gridMin.x;
//        float gridMinY = _gridMin.y;
//        float resolutionInv = _gridResolutionInv;
        
//        // 手动并行化，避免Parallel.For的分配
//        int numThreads = System.Environment.ProcessorCount;
//        int chunkSize = (queryCount + numThreads - 1) / numThreads;
        
//        var tasks = new Task[numThreads];
//        for (int t = 0; t < numThreads; t++)
//        {
//            int threadIndex = t;
//            tasks[t] = Task.Run(() =>
//            {
//                int start = threadIndex * chunkSize;
//                int end = Math.Min(start + chunkSize, queryCount);
                
//                for (int i = start; i < end; i++)
//                {
//                    Vector3 q = queryPoints[i];
//                    float qx = q.X;
//                    float qy = q.Y;
                    
//                    // 计算查询点所在的单元格
//                    int cx = (int)math.floor((qx - gridMinX) * resolutionInv);
//                    int cy = (int)math.floor((qy - gridMinY) * resolutionInv);
//                    cx = math.clamp(cx, 0, dimX - 1);
//                    cy = math.clamp(cy, 0, dimY - 1);
                    
//                    float bestDistSq = float.MaxValue;
//                    int bestIdx = -1;
                    
//                    // 搜索3x3邻域
//                    for (int dx = -1; dx <= 1; dx++)
//                    {
//                        int nx = cx + dx;
//                        if (nx < 0 || nx >= dimX) continue;
                        
//                        for (int dy = -1; dy <= 1; dy++)
//                        {
//                            int ny = cy + dy;
//                            if (ny < 0 || ny >= dimY) continue;
                            
//                            int cellHash = ny * dimX + nx;
//                            int startIdx = _cellStart[cellHash];
//                            int endIdx = _cellEnd[cellHash];
                            
//                            if (startIdx < 0) continue;
                            
//                            // 搜索当前单元格
//                            for (int j = startIdx; j < endIdx; j++)
//                            {
//                                float dx_ = _sortedX[j] - qx;
//                                float dy_ = _sortedY[j] - qy;
//                                float distSq = dx_ * dx_ + dy_ * dy_;
                                
//                                if (distSq < bestDistSq)
//                                {
//                                    bestDistSq = distSq;
//                                    bestIdx = j;
//                                }
//                            }
//                        }
//                    }
                    
//                    // 如果没找到，搜索所有点
//                    if (bestIdx == -1)
//                    {
//                        for (int j = 0; j < _sortedX.Length; j++)
//                        {
//                            float dx_ = _sortedX[j] - qx;
//                            float dy_ = _sortedY[j] - qy;
//                            float distSq = dx_ * dx_ + dy_ * dy_;
                            
//                            if (distSq < bestDistSq)
//                            {
//                                bestDistSq = distSq;
//                                bestIdx = j;
//                            }
//                        }
//                    }
                    
//                    results[i] = bestIdx >= 0 ? _originalIndices[bestIdx] : -1;
//                }
//            });
//        }
        
//        Task.WaitAll(tasks);
//        return results;
//    }
    
//    public void Dispose()
//    {
//        if (_sortedX.IsCreated) _sortedX.Dispose();
//        if (_sortedY.IsCreated) _sortedY.Dispose();
//        if (_originalIndices.IsCreated) _originalIndices.Dispose();
//        if (_cellStart.IsCreated) _cellStart.Dispose();
//        if (_cellEnd.IsCreated) _cellEnd.Dispose();
//    }
    
//    // ---------- 对象池管理 ----------
    
//    private static AssignCellJob RentAssignCellJob()
//    {
//        if (s_assignJobPool.TryTake(out var job))
//            return job;
//        return new AssignCellJob();
//    }
    
//    private static void ReturnAssignCellJob(AssignCellJob job)
//    {
//        job.Positions = null;
//        job.CellIndices = default;
//        job.Counts = default;
//        if (s_assignJobPool.Count < 32)
//            s_assignJobPool.Add(job);
//    }
    
//    private static PrefixSumJob RentPrefixSumJob()
//    {
//        if (s_prefixJobPool.TryTake(out var job))
//            return job;
//        return new PrefixSumJob();
//    }
    
//    private static void ReturnPrefixSumJob(PrefixSumJob job)
//    {
//        job.Counts = default;
//        if (s_prefixJobPool.Count < 32)
//            s_prefixJobPool.Add(job);
//    }
    
//    private static PlaceElementsJob RentPlaceElementsJob()
//    {
//        if (s_placeJobPool.TryTake(out var job))
//            return job;
//        return new PlaceElementsJob();
//    }
    
//    private static void ReturnPlaceElementsJob(PlaceElementsJob job)
//    {
//        job.Positions = null;
//        job.CellIndices = default;
//        job.Counts = default;
//        job.SortedX = default;
//        job.SortedY = default;
//        job.OriginalIndices = default;
//        job.CellStart = default;
//        job.CellEnd = default;
//        if (s_placeJobPool.Count < 32)
//            s_placeJobPool.Add(job);
//    }
    
//    private static SetCellEndJob RentSetCellEndJob()
//    {
//        if (s_setEndJobPool.TryTake(out var job))
//            return job;
//        return new SetCellEndJob();
//    }
    
//    private static void ReturnSetCellEndJob(SetCellEndJob job)
//    {
//        job.Counts = default;
//        job.CellStart = default;
//        job.CellEnd = default;
//        if (s_setEndJobPool.Count < 32)
//            s_setEndJobPool.Add(job);
//    }
    
//    // ---------- 作业定义 ----------
    
//    private struct AssignCellJob : IJobParallelFor
//    {
//        public Vector3[] Positions;
//        public float2 GridMin;
//        public float GridResolutionInv;
//        public int2 GridDimensions;
//        public NativeArray<int> CellIndices;
//        public NativeArray<int> Counts;
        
//        public void Execute(int index)
//        {
//            Vector3 p = Positions[index];
//            float2 pos = new float2(p.X, p.Y);
            
//            // 计算单元格坐标
//            int2 cell = (int2)math.floor((pos - GridMin) * GridResolutionInv);
//            cell = math.clamp(cell, int2.zero, GridDimensions - 1);
            
//            // 计算一维哈希
//            int hash = cell.y * GridDimensions.x + cell.x;
//            CellIndices[index] = hash;
            
//            // 原子递增计数
//            unsafe
//            {
//                int* countsPtr = (int*)Counts.GetUnsafePtr();
//                Interlocked.Increment(ref countsPtr[hash]);
//            }
//        }
//    }
    
//    private struct PrefixSumJob : IJob
//    {
//        public NativeArray<int> Counts;
        
//        public void Execute()
//        {
//            int sum = 0;
//            int length = Counts.Length;
//            for (int i = 0; i < length; i++)
//            {
//                int count = Counts[i];
//                Counts[i] = sum;
//                sum += count;
//            }
//        }
//    }
    
//    private struct PlaceElementsJob : IJobParallelFor
//    {
//        public Vector3[] Positions;
//        public NativeArray<int> CellIndices;
//        public NativeArray<int> Counts;
//        public NativeArray<float> SortedX;
//        public NativeArray<float> SortedY;
//        public NativeArray<int> OriginalIndices;
//        public NativeArray<int> CellStart;
//        public NativeArray<int> CellEnd;
        
//        public void Execute(int index)
//        {
//            int cellHash = CellIndices[index];
            
//            // 原子获取位置并递增
//            unsafe
//            {
//                int* countsPtr = (int*)Counts.GetUnsafePtr();
//                int destIdx = Interlocked.Add(ref countsPtr[cellHash], 1) - 1;
                
//                // 存储数据
//                Vector3 p = Positions[index];
//                SortedX[destIdx] = p.X;
//                SortedY[destIdx] = p.Y;
//                OriginalIndices[destIdx] = index;
                
//                // 更新单元格边界
//                if (destIdx == 0 || CellStart[cellHash] == -1)
//                {
//                    CellStart[cellHash] = destIdx;
//                }
//            }
//        }
//    }
    
//    private struct SetCellEndJob : IJob
//    {
//        public NativeArray<int> Counts;
//        public NativeArray<int> CellStart;
//        public NativeArray<int> CellEnd;
        
//        public void Execute()
//        {
//            int length = Counts.Length;
//            for (int i = 0; i < length; i++)
//            {
//                int count = Counts[i];
//                if (count > 0 && CellStart[i] >= 0)
//                {
//                    CellEnd[i] = CellStart[i] + count;
//                }
//                else
//                {
//                    CellEnd[i] = -1;
//                }
//            }
//        }
//    }
//}
