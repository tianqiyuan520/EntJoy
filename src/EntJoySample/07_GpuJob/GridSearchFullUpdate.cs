using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using EntJoy.Collections;
using EntJoy.JobSystem;
using EntJoy.Mathematics;

namespace EntJoySample.GpuJob
{
    /// <summary>
    /// GridSearch closest 全量更新：全部单位每帧移动 → 每帧全量上传 pos + 重建 grid + 查询小回读。
    /// 算法（counting-sort grid 5-pass）：
    ///   Pass1 count：每实体 hash(cell) → atomicAdd(cellCounts)
    ///   CPU prefix：回读 cellCounts(160KB) → 前缀和 → cursor = cellStart
    ///   Pass2 place：每实体 atomicAdd(cursor[hash]) → sortedPositions[slot] / hashIndex[slot]
    ///   Pass3 query：每 query 扫 3x3 邻 cell 的 sorted → result（K 个最近邻原索引）
    /// 每帧传输：pos 8MB↑（全量）+ cellCounts 160KB↓ + cursor 160KB↑ + result K*4B↓。
    /// </summary>
    public static class GridSearchFullUpdate
    {
        private const int N = 1_000_000;       // 实体数
        private const int K = 100_000;         // 查询数
        private const int DIM = 200;           // 网格 200x200
        private const int CELL = DIM * DIM;
        private const int Warmup = 2, Measure = 5;

        // ---------------- WGSL 内核 ----------------

        private const string CountWGSL = @"
struct CP { dimX : i32, dimY : i32, cellCount : i32, n : i32 };
@group(0) @binding(0) var<storage, read_write> pos : array<vec2f>;
@group(0) @binding(1) var<storage, read_write> counts : array<atomic<i32>>;
@group(0) @binding(2) var<uniform> p : CP;
@compute @workgroup_size(256)
fn main(@builtin(global_invocation_id) gid : vec3<u32>) {
    let i = i32(gid.x);
    if (i >= p.n) { return; }
    let q = pos[i];
    var cx = i32(floor((q.x + 100.0) * 1.0));
    cx = clamp(cx, 0, p.dimX - 1);
    var cy = i32(floor((q.y + 100.0) * 1.0));
    cy = clamp(cy, 0, p.dimY - 1);
    atomicAdd(&counts[cx + cy * p.dimX], 1);
}";

        private const string PlaceWGSL = @"
struct PP { dimX : i32, dimY : i32, cellCount : i32, n : i32 };
@group(0) @binding(0) var<storage, read_write> pos : array<vec2f>;
@group(0) @binding(1) var<storage, read_write> cursor : array<atomic<i32>>;
@group(0) @binding(2) var<storage, read_write> sorted : array<vec2f>;
@group(0) @binding(3) var<storage, read_write> hashIdx : array<vec2i>;
@group(0) @binding(4) var<uniform> p : PP;
@compute @workgroup_size(256)
fn main(@builtin(global_invocation_id) gid : vec3<u32>) {
    let i = i32(gid.x);
    if (i >= p.n) { return; }
    let q = pos[i];
    var cx = i32(floor((q.x + 100.0) * 1.0));
    cx = clamp(cx, 0, p.dimX - 1);
    var cy = i32(floor((q.y + 100.0) * 1.0));
    cy = clamp(cy, 0, p.dimY - 1);
    let h = cx + cy * p.dimX;
    let slot = atomicAdd(&cursor[h], 1);
    sorted[slot] = pos[i];
    hashIdx[slot] = vec2i(h, i);
}";

        private const string QueryWGSL = @"
struct QP { dimX : i32, dimY : i32, sortedLength : i32, k : i32 };
@group(0) @binding(0) var<storage, read_write> query : array<vec2f>;
@group(0) @binding(1) var<storage, read_write> cellStart : array<i32>;
@group(0) @binding(2) var<storage, read_write> sorted : array<vec2f>;
@group(0) @binding(3) var<storage, read_write> hashIdx : array<vec2i>;
@group(0) @binding(4) var<storage, read_write> result : array<i32>;
@group(0) @binding(5) var<uniform> p : QP;
@compute @workgroup_size(256)
fn main(@builtin(global_invocation_id) gid : vec3<u32>) {
    let i = i32(gid.x);
    if (i >= p.k) { return; }
    result[i] = -1;
    let q = query[i];
    var cx = i32(floor((q.x + 100.0) * 1.0));
    cx = clamp(cx, 0, p.dimX - 1);
    var cy = i32(floor((q.y + 100.0) * 1.0));
    cy = clamp(cy, 0, p.dimY - 1);
    var bestD = 3.402823466e38;
    var bestIdx = -1;
    for (var dx = -1; dx <= 1; dx++) {
        let nx = cx + dx;
        if (nx >= 0 && nx < p.dimX) {
            for (var dy = -1; dy <= 1; dy++) {
                let ny = cy + dy;
                if (ny >= 0 && ny < p.dimY) {
                    let c = ny * p.dimX + nx;
                    let start = cellStart[c];
                    var end : i32 = p.sortedLength;
                    if (c + 1 < p.dimX * p.dimY) { end = cellStart[c + 1]; }
                    for (var j = start; j < end; j++) {
                        let sp = sorted[j];
                        let d2 = (q.x - sp.x) * (q.x - sp.x) + (q.y - sp.y) * (q.y - sp.y);
                        if (d2 < bestD) { bestD = d2; bestIdx = hashIdx[j].y; }
                    }
                }
            }
        }
    }
    if (bestIdx >= 0) { result[i] = bestIdx; }
}";

        // ---------------- DllImport（复用 GpuCompute_*） ----------------

        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr GpuCompute_CreateKernel([MarshalAs(UnmanagedType.LPStr)] string wgsl, int storageBindingCount, int hasUniform);
        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr GpuCompute_CreateStorageBuffer(ulong size);
        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr GpuCompute_CreateUniformBuffer(ulong size);
        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void GpuCompute_WriteBuffer(IntPtr buffer, IntPtr data, ulong size);
        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr GpuCompute_CreateBindGroup(IntPtr kernel, IntPtr[] buffers, ulong[] sizes, int bufferCount);
        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void GpuCompute_Dispatch(IntPtr kernel, IntPtr bindGroup, uint workgroupX);
        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void GpuCompute_Sync();
        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int GpuCompute_ReadBack(IntPtr buffer, IntPtr outData, ulong size);
        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void GpuCompute_ReleaseBuffer(IntPtr buffer);
        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void GpuCompute_ReleaseBindGroup(IntPtr group);
        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void GpuCompute_ReleaseKernel(IntPtr kernel);
        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int GpuCompute_Initialize([MarshalAs(UnmanagedType.LPStr)] string path);
        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr GpuCompute_GetLastError();
        private static string Err() { IntPtr p = GpuCompute_GetLastError(); return p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p); }

        // ---------------- 数据 ----------------

        private static int Hash(float2 p)
        {
            int cx = (int)MathF.Floor((p.x + 100f) * 1f);
            if (cx < 0) cx = 0; else if (cx > DIM - 1) cx = DIM - 1;
            int cy = (int)MathF.Floor((p.y + 100f) * 1f);
            if (cy < 0) cy = 0; else if (cy > DIM - 1) cy = DIM - 1;
            return cx + cy * DIM;
        }

        private static float2[] Gen(int seed, int n)
        {
            var rng = new Random(seed);
            var a = new float2[n];
            for (int i = 0; i < n; i++)
                a[i] = new float2((float)(rng.NextDouble() * 200 - 100), (float)(rng.NextDouble() * 200 - 100));
            return a;
        }

        private static float Dist(float2 a, float2 b)
        {
            float dx = a.x - b.x, dy = a.y - b.y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>C# 多线程基线：全量重建 grid + 查询（parallel，模拟生产 CPU 侧）；返回 (buildMs, queryMs, resultCs)</summary>
        private static (double build, double query, int[] result) CsBaseline(float2[] pos, float2[] qry)
        {
            var counts = new int[CELL];
            var cellStart = new int[CELL];
            var cursor = new int[CELL];
            var sorted = new float2[N];
            var hashIdx = new int[N];
            var sw = Stopwatch.StartNew();
            Parallel.For(0, N, i => { int h = Hash(pos[i]); Interlocked.Increment(ref counts[h]); });
            int sum = 0;
            for (int c = 0; c < CELL; c++) { cellStart[c] = sum; sum += counts[c]; }
            Array.Copy(cellStart, cursor, CELL);
            Parallel.For(0, N, i => { int h = Hash(pos[i]); int s = Interlocked.Increment(ref cursor[h]) - 1; sorted[s] = pos[i]; hashIdx[s] = i; });
            sw.Stop();
            double buildMs = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            var result = new int[K];
            Parallel.For(0, K, k =>
            {
                float2 q = qry[k];
                int cx = (int)MathF.Floor((q.x + 100f) * 1f);
                if (cx < 0) cx = 0; else if (cx > DIM - 1) cx = DIM - 1;
                int cy = (int)MathF.Floor((q.y + 100f) * 1f);
                if (cy < 0) cy = 0; else if (cy > DIM - 1) cy = DIM - 1;
                float bestD = float.MaxValue; int bestIdx = -1;
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = cx + dx;
                    if (nx < 0 || nx >= DIM) continue;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int ny = cy + dy;
                        if (ny < 0 || ny >= DIM) continue;
                        int c = ny * DIM + nx;
                        int start = cellStart[c];
                        int end = (c + 1 < CELL) ? cellStart[c + 1] : N;
                        for (int j = start; j < end; j++)
                        {
                            float dx2 = q.x - sorted[j].x, dy2 = q.y - sorted[j].y;
                            float d2 = dx2 * dx2 + dy2 * dy2;
                            if (d2 < bestD) { bestD = d2; bestIdx = hashIdx[j]; }
                        }
                    }
                }
                result[k] = bestIdx;
            });
            sw.Stop();
            return (buildMs, sw.Elapsed.TotalMilliseconds, result);
        }

        /// <summary>C++ (JobSystem) 对照：transpiled C++ job（CppGridCount/Place/Query）构建（count+prefix+place）+ 查询 拆分计时</summary>
        private static (double build, double query) RunCppComparison(float2[] pos, float2[] qry)
        {
            var naPos = new NativeArray<float2>(N, Allocator.Persistent);
            var naCounts = new NativeArray<int>(CELL, Allocator.Persistent);
            var naCursor = new NativeArray<int>(CELL, Allocator.Persistent);
            var naSorted = new NativeArray<float2>(N, Allocator.Persistent);
            var naHash = new NativeArray<int2>(N, Allocator.Persistent);
            var naQuery = new NativeArray<float2>(K, Allocator.Persistent);
            var naResult = new NativeArray<int>(K, Allocator.Persistent);
            try
            {
                for (int i = 0; i < N; i++) naPos[i] = pos[i];
                for (int k = 0; k < K; k++) naQuery[k] = qry[k];

                var cellStart = new int[CELL];
                double[] buildTimes = new double[Measure];
                double[] queryTimes = new double[Measure];
                for (int r = 0; r < Warmup + Measure; r++)
                {
                    var swB = Stopwatch.StartNew();
                    for (int c = 0; c < CELL; c++) naCounts[c] = 0;
                    new CppGridCountJob { Positions = naPos, Counts = naCounts, DimX = DIM, DimY = DIM }.Schedule(N).Complete();
                    int sum = 0;
                    for (int c = 0; c < CELL; c++) { cellStart[c] = sum; sum += naCounts[c]; }
                    for (int c = 0; c < CELL; c++) naCursor[c] = cellStart[c];
                    new CppGridPlaceJob { Positions = naPos, Cursor = naCursor, Sorted = naSorted, HashIdx = naHash, DimX = DIM, DimY = DIM }.Schedule(N).Complete();
                    swB.Stop();
                    var swQ = Stopwatch.StartNew();
                    new CppGridQueryJob { Query = naQuery, CellStart = naCursor, Sorted = naSorted, HashIdx = naHash, Result = naResult, DimX = DIM, DimY = DIM, SortedLength = N }.Schedule(K).Complete();
                    swQ.Stop();
                    if (r >= Warmup) { buildTimes[r - Warmup] = swB.Elapsed.TotalMilliseconds; queryTimes[r - Warmup] = swQ.Elapsed.TotalMilliseconds; }
                }
                Array.Sort(buildTimes);
                Array.Sort(queryTimes);
                return (buildTimes[Measure / 2], queryTimes[Measure / 2]);
            }
            finally
            {
                naPos.Dispose(); naCounts.Dispose(); naCursor.Dispose();
                naSorted.Dispose(); naHash.Dispose(); naQuery.Dispose(); naResult.Dispose();
            }
        }

        /// <summary>ISPC (JobSystem) 对照：transpiled ISPC job（IspcGridCount/Place/Query）构建（count+prefix+place）+ 查询 拆分计时</summary>
        private static (double build, double query) RunIspcComparison(float2[] pos, float2[] qry)
        {
            var naPos = new NativeArray<float2>(N, Allocator.Persistent);
            var naCounts = new NativeArray<int>(CELL, Allocator.Persistent);
            var naCursor = new NativeArray<int>(CELL, Allocator.Persistent);
            var naSorted = new NativeArray<float2>(N, Allocator.Persistent);
            var naHash = new NativeArray<int2>(N, Allocator.Persistent);
            var naQuery = new NativeArray<float2>(K, Allocator.Persistent);
            var naResult = new NativeArray<int>(K, Allocator.Persistent);
            try
            {
                for (int i = 0; i < N; i++) naPos[i] = pos[i];
                for (int k = 0; k < K; k++) naQuery[k] = qry[k];

                var cellStart = new int[CELL];
                double[] buildTimes = new double[Measure];
                double[] queryTimes = new double[Measure];
                for (int r = 0; r < Warmup + Measure; r++)
                {
                    var swB = Stopwatch.StartNew();
                    for (int c = 0; c < CELL; c++) naCounts[c] = 0;
                    new IspcGridCountJob { Positions = naPos, Counts = naCounts, DimX = DIM, DimY = DIM }.Schedule(N).Complete();
                    int sum = 0;
                    for (int c = 0; c < CELL; c++) { cellStart[c] = sum; sum += naCounts[c]; }
                    for (int c = 0; c < CELL; c++) naCursor[c] = cellStart[c];
                    new IspcGridPlaceJob { Positions = naPos, Cursor = naCursor, Sorted = naSorted, HashIdx = naHash, DimX = DIM, DimY = DIM }.Schedule(N).Complete();
                    swB.Stop();
                    var swQ = Stopwatch.StartNew();
                    new IspcGridQueryJob { Query = naQuery, CellStart = naCursor, Sorted = naSorted, HashIdx = naHash, Result = naResult, DimX = DIM, DimY = DIM, SortedLength = N }.Schedule(K).Complete();
                    swQ.Stop();
                    if (r >= Warmup) { buildTimes[r - Warmup] = swB.Elapsed.TotalMilliseconds; queryTimes[r - Warmup] = swQ.Elapsed.TotalMilliseconds; }
                }
                Array.Sort(buildTimes);
                Array.Sort(queryTimes);
                return (buildTimes[Measure / 2], queryTimes[Measure / 2]);
            }
            finally
            {
                naPos.Dispose(); naCounts.Dispose(); naCursor.Dispose();
                naSorted.Dispose(); naHash.Dispose(); naQuery.Dispose(); naResult.Dispose();
            }
        }

        // ---------------- CUDA 驱动后端（GpuComputeCuda_*，PTX 运行时加载） ----------------

        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int GpuComputeCuda_Initialize();
        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr GpuComputeCuda_GetLastError();
        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr GpuComputeCuda_CreateKernel(byte[] cubin, [MarshalAs(UnmanagedType.LPStr)] string entry);
        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void GpuComputeCuda_ReleaseKernel(IntPtr kernel);
        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr GpuComputeCuda_CreateDeviceBuffer(ulong size);
        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr GpuComputeCuda_CreatePinnedBuffer(ulong size);
        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void GpuComputeCuda_ReleaseDeviceBuffer(IntPtr buffer);
        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void GpuComputeCuda_ReleasePinnedBuffer(IntPtr buffer);
        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void GpuComputeCuda_WriteBuffer(IntPtr device, IntPtr data, ulong size);
        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void GpuComputeCuda_ReadBack(IntPtr device, IntPtr outData, ulong size);
        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void GpuComputeCuda_Dispatch(IntPtr kernel, IntPtr[] kernelParams, uint gridX, uint blockX);
        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void GpuComputeCuda_Sync();
        private static string CudaErr() { IntPtr p = GpuComputeCuda_GetLastError(); return p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p); }

        /// <summary>cuLaunchKernel 参数槽：u64（设备指针）在前、i32（标量）在后，各 8 字节对齐；返回各参数值地址。
        /// slot 由调用者分配（stackalloc 生命周期贯穿测量循环，不能是 Args8 内局部）</summary>
        private static unsafe IntPtr[] Args8(byte* slot, int slotBytes, ulong[] u64s, int[] i32s)
        {
            int total = u64s.Length + i32s.Length;
            var ptrs = new IntPtr[total];
            int off = 0, idx = 0;
            foreach (var u in u64s) { *(ulong*)(slot + off) = u; ptrs[idx++] = (IntPtr)(slot + off); off += 8; }
            foreach (var i in i32s) { *(int*)(slot + off) = i; ptrs[idx++] = (IntPtr)(slot + off); off += 8; }
            return ptrs;
        }

        /// <summary>CUDA（驱动 API + cubin + 页锁定）GridSearch：构建（upload+count+prefix+place）+ 查询 拆分计时。
        /// 返回 (构建 p50, 查询 p50, parity 不一致数)</summary>
        private static unsafe (double build, double query, long mismatch) RunCudaComparison(float2[] pos, float2[] qry, int[] csResult)
        {
            string cubinPath = Path.Combine(AppContext.BaseDirectory, "GridSearchKernels.cubin");
            if (!File.Exists(cubinPath)) { Console.WriteLine($"  [CUDA SKIP] 未找到 {cubinPath}"); return (-1, -1, -1); }
            byte[] cubin = File.ReadAllBytes(cubinPath);
            if (GpuComputeCuda_Initialize() == 0) { Console.WriteLine($"  [CUDA SKIP] init: {CudaErr()}"); return (-1, -1, -1); }

            IntPtr kCount = GpuComputeCuda_CreateKernel(cubin, "grid_count");
            IntPtr kPlace = GpuComputeCuda_CreateKernel(cubin, "grid_place");
            IntPtr kQuery = GpuComputeCuda_CreateKernel(cubin, "grid_query");
            if (kCount == IntPtr.Zero || kPlace == IntPtr.Zero || kQuery == IntPtr.Zero)
            { Console.WriteLine($"  [CUDA SKIP] kernel: {CudaErr()}"); return (-1, -1, -1); }

            IntPtr dPos = GpuComputeCuda_CreateDeviceBuffer((ulong)N * 8);
            IntPtr dCounts = GpuComputeCuda_CreateDeviceBuffer((ulong)CELL * 4);
            IntPtr dCursor = GpuComputeCuda_CreateDeviceBuffer((ulong)CELL * 4);
            IntPtr dCellStart = GpuComputeCuda_CreateDeviceBuffer((ulong)CELL * 4);   // query 用（place 不改它）
            IntPtr dSorted = GpuComputeCuda_CreateDeviceBuffer((ulong)N * 8);
            IntPtr dHash = GpuComputeCuda_CreateDeviceBuffer((ulong)N * 8);
            IntPtr dQuery = GpuComputeCuda_CreateDeviceBuffer((ulong)K * 8);
            IntPtr dResult = GpuComputeCuda_CreateDeviceBuffer((ulong)K * 4);
            IntPtr hPos = GpuComputeCuda_CreatePinnedBuffer((ulong)N * 8);
            IntPtr hQry = GpuComputeCuda_CreatePinnedBuffer((ulong)K * 8);
            IntPtr hCounts = GpuComputeCuda_CreatePinnedBuffer((ulong)CELL * 4);
            IntPtr hCursor = GpuComputeCuda_CreatePinnedBuffer((ulong)CELL * 4);
            IntPtr hResult = GpuComputeCuda_CreatePinnedBuffer((ulong)K * 4);
            var zeroHost = new int[CELL];

            try
            {
                // pinned host buffer 直接填充（初始化一次，不在计时内）
                var pp = (float2*)hPos;
                for (int i = 0; i < N; i++) pp[i] = pos[i];
                var pq = (float2*)hQry;
                for (int k = 0; k < K; k++) pq[k] = qry[k];
                GpuComputeCuda_WriteBuffer(dPos, hPos, (ulong)N * 8);
                GpuComputeCuda_WriteBuffer(dQuery, hQry, (ulong)K * 8);
                GpuComputeCuda_Sync();

                uint block = 256;
                uint gN = (uint)((N + 255) / 256);
                uint gK = (uint)((K + 255) / 256);
                var cursorHost = new int[CELL];
                var resultHost = new int[K];
                // 参数槽（stackalloc 生命周期 = 本方法，贯穿测量循环）
                var slotC = stackalloc byte[5 * 8];
                var slotP = stackalloc byte[7 * 8];
                var slotQ = stackalloc byte[9 * 8];
                double[] buildTimes = new double[Measure];
                double[] queryTimes = new double[Measure];
                for (int r = 0; r < Warmup + Measure; r++)
                {
                    // 构建：① 上传 pos ② count ③ 回读 counts + CPU prefix ④ cursor/cellStart 上传 + place
                    var swB = Stopwatch.StartNew();
                    GpuComputeCuda_WriteBuffer(dPos, hPos, (ulong)N * 8);
                    fixed (int* pz = zeroHost) GpuComputeCuda_WriteBuffer(dCounts, (IntPtr)pz, (ulong)CELL * 4);
                    GpuComputeCuda_Dispatch(kCount, Args8(slotC, 40, new ulong[] { (ulong)dPos, (ulong)dCounts }, new[] { DIM, DIM, N }), gN, block);
                    GpuComputeCuda_Sync();
                    GpuComputeCuda_ReadBack(dCounts, hCounts, (ulong)CELL * 4);
                    Marshal.Copy(hCounts, cursorHost, 0, CELL);
                    int sum = 0;
                    for (int c = 0; c < CELL; c++) { int cnt = cursorHost[c]; cursorHost[c] = sum; sum += cnt; }
                    fixed (int* pc = cursorHost)
                    {
                        GpuComputeCuda_WriteBuffer(dCursor, (IntPtr)pc, (ulong)CELL * 4);
                        GpuComputeCuda_WriteBuffer(dCellStart, (IntPtr)pc, (ulong)CELL * 4);
                    }
                    GpuComputeCuda_Dispatch(kPlace, Args8(slotP, 56, new ulong[] { (ulong)dPos, (ulong)dCursor, (ulong)dSorted, (ulong)dHash }, new[] { DIM, DIM, N }), gN, block);
                    swB.Stop();
                    // 查询：⑤ query + sync + 回读结果
                    var swQ = Stopwatch.StartNew();
                    GpuComputeCuda_Dispatch(kQuery, Args8(slotQ, 72, new ulong[] { (ulong)dQuery, (ulong)dCellStart, (ulong)dSorted, (ulong)dHash, (ulong)dResult }, new[] { DIM, DIM, N, K }), gK, block);
                    GpuComputeCuda_Sync();
                    GpuComputeCuda_ReadBack(dResult, hResult, (ulong)K * 4);
                    Marshal.Copy(hResult, resultHost, 0, K);
                    swQ.Stop();
                    if (r >= Warmup) { buildTimes[r - Warmup] = swB.Elapsed.TotalMilliseconds; queryTimes[r - Warmup] = swQ.Elapsed.TotalMilliseconds; }
                }
                Array.Sort(buildTimes);
                Array.Sort(queryTimes);
                long mismatch = 0;
                for (int k = 0; k < K; k++) if (resultHost[k] != csResult[k]) mismatch++;
                return (buildTimes[Measure / 2], queryTimes[Measure / 2], mismatch);
            }
            finally
            {
                GpuComputeCuda_ReleaseKernel(kQuery); GpuComputeCuda_ReleaseKernel(kPlace); GpuComputeCuda_ReleaseKernel(kCount);
                GpuComputeCuda_ReleaseDeviceBuffer(dResult); GpuComputeCuda_ReleaseDeviceBuffer(dQuery);
                GpuComputeCuda_ReleaseDeviceBuffer(dHash); GpuComputeCuda_ReleaseDeviceBuffer(dSorted);
                GpuComputeCuda_ReleaseDeviceBuffer(dCellStart); GpuComputeCuda_ReleaseDeviceBuffer(dCursor);
                GpuComputeCuda_ReleaseDeviceBuffer(dCounts); GpuComputeCuda_ReleaseDeviceBuffer(dPos);
                GpuComputeCuda_ReleasePinnedBuffer(hResult); GpuComputeCuda_ReleasePinnedBuffer(hCursor);
                GpuComputeCuda_ReleasePinnedBuffer(hCounts); GpuComputeCuda_ReleasePinnedBuffer(hQry);
                GpuComputeCuda_ReleasePinnedBuffer(hPos);
            }
        }

        /// <summary>Light/Heavy Move 的 CUDA 常驻 + 全量往返（vs wgpu / C++ / ISPC，四路对比同体）</summary>
        private static unsafe void RunMoveCudaComparison()
        {
            const int MOVE_N = 1_000_000;
            const float Dt = 1f / 60f, VW = 1920f, VH = 1080f;
            string cubinPath = Path.Combine(AppContext.BaseDirectory, "GridSearchKernels.cubin");
            if (!File.Exists(cubinPath) || GpuComputeCuda_Initialize() == 0)
            { Console.WriteLine("  [CUDA SKIP] Move 对比：cubin 缺失或 init 失败"); return; }
            byte[] cubin = File.ReadAllBytes(cubinPath);
            IntPtr kMove = GpuComputeCuda_CreateKernel(cubin, "move_kernel");
            IntPtr kHeavy = GpuComputeCuda_CreateKernel(cubin, "heavy_kernel");
            if (kMove == IntPtr.Zero || kHeavy == IntPtr.Zero)
            { Console.WriteLine($"  [CUDA SKIP] Move kernel: {CudaErr()}"); return; }

            IntPtr dPos = GpuComputeCuda_CreateDeviceBuffer((ulong)MOVE_N * 8);
            IntPtr dVel = GpuComputeCuda_CreateDeviceBuffer((ulong)MOVE_N * 8);
            IntPtr hPos = GpuComputeCuda_CreatePinnedBuffer((ulong)MOVE_N * 8);
            IntPtr hVel = GpuComputeCuda_CreatePinnedBuffer((ulong)MOVE_N * 8);
            IntPtr hPosOut = GpuComputeCuda_CreatePinnedBuffer((ulong)MOVE_N * 8);
            IntPtr hVelOut = GpuComputeCuda_CreatePinnedBuffer((ulong)MOVE_N * 8);
            try
            {
                var rng = new Random(1234);
                var pp = (float2*)hPos; var pv = (float2*)hVel;
                for (int i = 0; i < MOVE_N; i++)
                {
                    pp[i] = new float2((float)(rng.NextDouble() * 200 - 100), (float)(rng.NextDouble() * 200 - 100));
                    pv[i] = new float2((float)(rng.NextDouble() * 200 - 100), (float)(rng.NextDouble() * 200 - 100));
                }
                GpuComputeCuda_WriteBuffer(dPos, hPos, (ulong)MOVE_N * 8);
                GpuComputeCuda_WriteBuffer(dVel, hVel, (ulong)MOVE_N * 8);
                GpuComputeCuda_Sync();

                uint block = 256;
                uint gN = (uint)((MOVE_N + 255) / 256);
                var slot = stackalloc byte[3 * 8];
                double[] tRes = new double[5], tRt = new double[5];
                foreach (var (name, kern) in new[] { ("LightMove", kMove), ("HeavyMove", kHeavy) })
                {
                    // 常驻（纯 dispatch，p50）
                    for (int r = 0; r < 7; r++)
                    {
                        var sw = Stopwatch.StartNew();
                        GpuComputeCuda_Dispatch(kern, Args8(slot, 24, new ulong[] { (ulong)dPos, (ulong)dVel }, new[] { MOVE_N }), gN, block);
                        GpuComputeCuda_Sync();
                        sw.Stop();
                        if (r >= 2) tRes[r - 2] = sw.Elapsed.TotalMilliseconds;
                    }
                    Array.Sort(tRes);
                    // 全量往返（上传 16MB + dispatch + 回读 16MB，p50）
                    for (int r = 0; r < 7; r++)
                    {
                        var sw = Stopwatch.StartNew();
                        GpuComputeCuda_WriteBuffer(dPos, hPos, (ulong)MOVE_N * 8);
                        GpuComputeCuda_WriteBuffer(dVel, hVel, (ulong)MOVE_N * 8);
                        GpuComputeCuda_Dispatch(kern, Args8(slot, 24, new ulong[] { (ulong)dPos, (ulong)dVel }, new[] { MOVE_N }), gN, block);
                        GpuComputeCuda_Sync();
                        GpuComputeCuda_ReadBack(dPos, hPosOut, (ulong)MOVE_N * 8);
                        GpuComputeCuda_ReadBack(dVel, hVelOut, (ulong)MOVE_N * 8);
                        sw.Stop();
                        if (r >= 2) tRt[r - 2] = sw.Elapsed.TotalMilliseconds;
                    }
                    Array.Sort(tRt);
                    Console.WriteLine($"  CUDA {name}：常驻 {tRes[2]:F3}ms / 全量往返(32MB) {tRt[2]:F3}ms");
                }
                GpuComputeCuda_ReleaseKernel(kHeavy); GpuComputeCuda_ReleaseKernel(kMove);
                GpuComputeCuda_ReleaseDeviceBuffer(dVel); GpuComputeCuda_ReleaseDeviceBuffer(dPos);
                GpuComputeCuda_ReleasePinnedBuffer(hVelOut); GpuComputeCuda_ReleasePinnedBuffer(hPosOut);
                GpuComputeCuda_ReleasePinnedBuffer(hVel); GpuComputeCuda_ReleasePinnedBuffer(hPos);
            }
            catch (Exception ex) { Console.WriteLine($"  [CUDA Move FAIL] {ex.Message}"); }
        }

        // ---------------- ScheduleCuda 声明即用（BindingsGenerator 自动生成调度） ----------------

        private static unsafe void RunScheduleCuda()
        {
            const int M = 1_000_000;
            const float Dt = 1f / 60f, VW = 1920f, VH = 1080f;
            var rng = new Random(777);
            using var pos = new NativeArray<float2>(M, Allocator.Persistent);
            using var vel = new NativeArray<float2>(M, Allocator.Persistent);
            var hp = (float2*)pos.GetUnsafePtr();
            var hv = (float2*)vel.GetUnsafePtr();
            var initPos = new float2[M]; var initVel = new float2[M];
            for (int i = 0; i < M; i++)
            {
                initPos[i] = new float2((float)(rng.NextDouble() * 200 - 100), (float)(rng.NextDouble() * 200 - 100));
                initVel[i] = new float2((float)(rng.NextDouble() * 200 - 100), (float)(rng.NextDouble() * 200 - 100));
            }
            // C# 参考（推进 1 次）
            var refPos = (float2[])initPos.Clone(); var refVel = (float2[])initVel.Clone();
            for (int i = 0; i < M; i++)
            {
                refPos[i].x += refVel[i].x * Dt; refPos[i].y += refVel[i].y * Dt;
                if (refPos[i].x < 0 || refPos[i].x > VW) refVel[i].x = -refVel[i].x;
                if (refPos[i].y < 0 || refPos[i].y > VH) refVel[i].y = -refVel[i].y;
            }
            var heavyRef = (float2[])initPos.Clone();
            for (int i = 0; i < M; i++)
            {
                float acc = 0f, x = heavyRef[i].x;
                for (int k = 0; k < 16; k++) { acc += MathF.Sin(x) + MathF.Cos(x) + MathF.Sqrt(x * x + 1f); x += initVel[i].x * Dt; }
                heavyRef[i].x += acc * Dt;
                heavyRef[i].y += initVel[i].y * Dt;
            }

            Console.WriteLine("  --- ScheduleCuda 自动生成调度（声明即用，无手写 marshalling） ---");
            var moveJob = new CudaMoveJob { Positions = pos, Velocities = vel, Dt = Dt, ViewportWidth = VW, ViewportHeight = VH };
            var heavyJob = new CudaHeavyJob { Positions = pos, Velocities = vel, Dt = Dt };

            // Move：每轮重置初始数据 → ScheduleCuda（内部完成上传/dispatch/回读）→ 校验 + 计时
            double[] times = new double[5];
            for (int r = 0; r < 7; r++)
            {
                for (int i = 0; i < M; i++) { hp[i] = initPos[i]; hv[i] = initVel[i]; }
                var sw = Stopwatch.StartNew();
                moveJob.ScheduleCuda(M);
                sw.Stop();
                if (r >= 2) times[r - 2] = sw.Elapsed.TotalMilliseconds;
            }
            Array.Sort(times);
            long mismatch = 0; float maxDiff = 0;
            for (int i = 0; i < M; i++)
            {
                if (BitConverter.SingleToInt32Bits(hp[i].x) != BitConverter.SingleToInt32Bits(refPos[i].x)) { mismatch++; maxDiff = MathF.Max(maxDiff, MathF.Abs(hp[i].x - refPos[i].x)); }
                if (BitConverter.SingleToInt32Bits(hp[i].y) != BitConverter.SingleToInt32Bits(refPos[i].y)) { mismatch++; maxDiff = MathF.Max(maxDiff, MathF.Abs(hp[i].y - refPos[i].y)); }
                if (BitConverter.SingleToInt32Bits(hv[i].x) != BitConverter.SingleToInt32Bits(refVel[i].x)) mismatch++;
                if (BitConverter.SingleToInt32Bits(hv[i].y) != BitConverter.SingleToInt32Bits(refVel[i].y)) mismatch++;
            }
            Console.WriteLine($"  ScheduleCuda CudaMoveJob：全量往返 {times[2]:F3}ms，parity 不等 {mismatch}/{M * 4} max|diff|={maxDiff:E2}");

            // Heavy
            times = new double[5];
            for (int r = 0; r < 7; r++)
            {
                for (int i = 0; i < M; i++) { hp[i] = initPos[i]; hv[i] = initVel[i]; }
                var sw = Stopwatch.StartNew();
                heavyJob.ScheduleCuda(M);
                sw.Stop();
                if (r >= 2) times[r - 2] = sw.Elapsed.TotalMilliseconds;
            }
            Array.Sort(times);
            mismatch = 0; maxDiff = 0;
            for (int i = 0; i < M; i++)
            {
                if (BitConverter.SingleToInt32Bits(hp[i].x) != BitConverter.SingleToInt32Bits(heavyRef[i].x)) { mismatch++; maxDiff = MathF.Max(maxDiff, MathF.Abs(hp[i].x - heavyRef[i].x)); }
                if (BitConverter.SingleToInt32Bits(hp[i].y) != BitConverter.SingleToInt32Bits(heavyRef[i].y)) { mismatch++; maxDiff = MathF.Max(maxDiff, MathF.Abs(hp[i].y - heavyRef[i].y)); }
            }
            Console.WriteLine($"  ScheduleCuda CudaHeavyJob：全量往返 {times[2]:F3}ms，parity 不等 {mismatch}/{M * 2} max|diff|={maxDiff:E2}");

            // pinned NativeArray 直连（路径 A）：CPU 直写页锁定内存 → ScheduleCuda 上传/回读免 C# 拷贝
            void* pp = (void*)GpuComputeCuda_CreatePinnedBuffer((ulong)M * 8);
            void* pv = (void*)GpuComputeCuda_CreatePinnedBuffer((ulong)M * 8);
            if (pp != null && pv != null)
            {
                try
                {
                    var ppin = NativeArray<float2>.FromExternalPtr((float2*)pp, M, pinned: true);
                    var pvin = NativeArray<float2>.FromExternalPtr((float2*)pv, M, pinned: true);
                    var pinMove = new CudaMoveJob { Positions = ppin, Velocities = pvin, Dt = Dt, ViewportWidth = VW, ViewportHeight = VH };

                    times = new double[5];
                    for (int r = 0; r < 7; r++)
                    {
                        for (int i = 0; i < M; i++) { ppin[i] = initPos[i]; pvin[i] = initVel[i]; }   // CPU 直写 pinned
                        var sw = Stopwatch.StartNew();
                        pinMove.ScheduleCuda(M);
                        sw.Stop();
                        if (r >= 2) times[r - 2] = sw.Elapsed.TotalMilliseconds;
                    }
                    Array.Sort(times);
                    mismatch = 0; maxDiff = 0;
                    for (int i = 0; i < M; i++)
                    {
                        if (BitConverter.SingleToInt32Bits(ppin[i].x) != BitConverter.SingleToInt32Bits(refPos[i].x)) { mismatch++; maxDiff = MathF.Max(maxDiff, MathF.Abs(ppin[i].x - refPos[i].x)); }
                        if (BitConverter.SingleToInt32Bits(ppin[i].y) != BitConverter.SingleToInt32Bits(refPos[i].y)) { mismatch++; maxDiff = MathF.Max(maxDiff, MathF.Abs(ppin[i].y - refPos[i].y)); }
                        if (BitConverter.SingleToInt32Bits(pvin[i].x) != BitConverter.SingleToInt32Bits(refVel[i].x)) mismatch++;
                        if (BitConverter.SingleToInt32Bits(pvin[i].y) != BitConverter.SingleToInt32Bits(refVel[i].y)) mismatch++;
                    }
                    Console.WriteLine($"  ScheduleCuda CudaMoveJob（pinned NativeArray 直连）：全量往返 {times[2]:F3}ms，parity 不等 {mismatch}/{M * 4} max|diff|={maxDiff:E2}");
                }
                finally
                {
                    GpuComputeCuda_ReleasePinnedBuffer((IntPtr)pp);
                    GpuComputeCuda_ReleasePinnedBuffer((IntPtr)pv);
                }
            }
            else
            {
                Console.WriteLine("  [CUDA SKIP] pinned 直连：CreatePinnedBuffer 失败");
            }
            Console.WriteLine();
        }

        // ---------------- CUDA-only 验证（绕过 JobSystem gate；供 ENTJOY_CUDA_ONLY=1） ----------------

        public static unsafe void RunCudaOnly()
        {
            if (GpuComputeCuda_Initialize() == 0)
            {
                Console.WriteLine($"  [CUDA SKIP] init: {CudaErr()}");
                return;
            }
            RunMoveCudaComparison();
            RunScheduleCuda();
        }

        // ---------------- GPU 全量更新流程 ----------------

        // ---------------- GridSearch 构建/查询 拆分计时（五路对比的 GridSearch 部分） ----------------

        /// <summary>GPU（wgpu）GridSearch：构建（upload+count+prefix+place）+ 查询 拆分计时 + parity。</summary>
        private static unsafe (double build, double query, long mismatch, long gpuHits, long csHits) MeasureGpuGrid(float2[] pos, float2[] qry, int[] csResult)
        {
            fixed (float2* ppos = pos) fixed (float2* pqry = qry)
            {
                IntPtr posPtr = (IntPtr)ppos;
                IntPtr qryPtr = (IntPtr)pqry;
                IntPtr kCount = GpuCompute_CreateKernel(CountWGSL, 2, 1);
                IntPtr kPlace = GpuCompute_CreateKernel(PlaceWGSL, 4, 1);
                IntPtr kQuery = GpuCompute_CreateKernel(QueryWGSL, 5, 1);
                if (kCount == IntPtr.Zero || kPlace == IntPtr.Zero || kQuery == IntPtr.Zero)
                    throw new InvalidOperationException("GPU kernel: " + Err());

                IntPtr bPos = GpuCompute_CreateStorageBuffer((ulong)N * 8);
                IntPtr bCounts = GpuCompute_CreateStorageBuffer((ulong)CELL * 4);
                IntPtr bCursor = GpuCompute_CreateStorageBuffer((ulong)CELL * 4);
                IntPtr bCellStart = GpuCompute_CreateStorageBuffer((ulong)CELL * 4);
                IntPtr bSorted = GpuCompute_CreateStorageBuffer((ulong)N * 8);
                IntPtr bHash = GpuCompute_CreateStorageBuffer((ulong)N * 8);
                IntPtr bQuery = GpuCompute_CreateStorageBuffer((ulong)K * 8);
                IntPtr bResult = GpuCompute_CreateStorageBuffer((ulong)K * 4);
                IntPtr uCount = GpuCompute_CreateUniformBuffer(16);
                IntPtr uPlace = GpuCompute_CreateUniformBuffer(16);
                IntPtr uQuery = GpuCompute_CreateUniformBuffer(16);

                var uni = new byte[16];
                BitConverter.GetBytes(DIM).CopyTo(uni, 0);
                BitConverter.GetBytes(DIM).CopyTo(uni, 4);
                BitConverter.GetBytes(CELL).CopyTo(uni, 8);
                BitConverter.GetBytes(N).CopyTo(uni, 12);
                var uq = new byte[16];
                BitConverter.GetBytes(DIM).CopyTo(uq, 0);
                BitConverter.GetBytes(DIM).CopyTo(uq, 4);
                BitConverter.GetBytes(N).CopyTo(uq, 8);
                BitConverter.GetBytes(K).CopyTo(uq, 12);

                IntPtr bgCount = GpuCompute_CreateBindGroup(kCount, new[] { bPos, bCounts, uCount }, new ulong[] { (ulong)N * 8, (ulong)CELL * 4, 16 }, 3);
                IntPtr bgPlace = GpuCompute_CreateBindGroup(kPlace, new[] { bPos, bCursor, bSorted, bHash, uPlace }, new ulong[] { (ulong)N * 8, (ulong)CELL * 4, (ulong)N * 8, (ulong)N * 8, 16 }, 5);
                IntPtr bgQuery = GpuCompute_CreateBindGroup(kQuery, new[] { bQuery, bCellStart, bSorted, bHash, bResult, uQuery }, new ulong[] { (ulong)K * 8, (ulong)CELL * 4, (ulong)N * 8, (ulong)N * 8, (ulong)K * 4, 16 }, 6);
                if (bgCount == IntPtr.Zero || bgPlace == IntPtr.Zero || bgQuery == IntPtr.Zero)
                    throw new InvalidOperationException("bindgroup: " + Err());

                fixed (byte* pu = uni) GpuCompute_WriteBuffer(uCount, (IntPtr)pu, 16);
                fixed (byte* pu = uni) GpuCompute_WriteBuffer(uPlace, (IntPtr)pu, 16);
                fixed (byte* pu2 = uq) GpuCompute_WriteBuffer(uQuery, (IntPtr)pu2, 16);
                GpuCompute_WriteBuffer(bQuery, qryPtr, (ulong)K * 8);
                GpuCompute_Sync();

                var countsHost = new int[CELL];
                var cursorHost = new int[CELL];
                var resultHost = new int[K];
                var zeroHost = new int[CELL];
                uint gCount = (uint)((N + 255) / 256);
                uint gPlace = (uint)((N + 255) / 256);
                uint gQuery = (uint)((K + 255) / 256);

                var buildTimes = new List<double>();
                var queryTimes = new List<double>();
                for (int i = 0; i < Warmup; i++) RunFrame();
                for (int i = 0; i < Measure; i++)
                {
                    var (b, q) = RunFrame();
                    buildTimes.Add(b);
                    queryTimes.Add(q);
                }
                buildTimes.Sort();
                queryTimes.Sort();

                long gpuHits = 0, csHits = 0, mismatch = 0;
                for (int k = 0; k < K; k++)
                {
                    if (resultHost[k] != -1) gpuHits++;
                    if (csResult[k] != -1) csHits++;
                    if (resultHost[k] != csResult[k]) mismatch++;
                }

                GpuCompute_ReleaseBindGroup(bgQuery); GpuCompute_ReleaseBindGroup(bgPlace); GpuCompute_ReleaseBindGroup(bgCount);
                GpuCompute_ReleaseBuffer(bResult); GpuCompute_ReleaseBuffer(bQuery); GpuCompute_ReleaseBuffer(bHash);
                GpuCompute_ReleaseBuffer(bSorted); GpuCompute_ReleaseBuffer(bCellStart); GpuCompute_ReleaseBuffer(bCursor);
                GpuCompute_ReleaseBuffer(bCounts); GpuCompute_ReleaseBuffer(bPos);
                GpuCompute_ReleaseBuffer(uQuery); GpuCompute_ReleaseBuffer(uPlace); GpuCompute_ReleaseBuffer(uCount);
                GpuCompute_ReleaseKernel(kQuery); GpuCompute_ReleaseKernel(kPlace); GpuCompute_ReleaseKernel(kCount);

                return (buildTimes[buildTimes.Count / 2], queryTimes[queryTimes.Count / 2], mismatch, gpuHits, csHits);

                (double, double) RunFrame()
                {
                    // 构建：上传 pos + count + 回读 counts + CPU prefix + cursor/cellStart 上传 + place
                    var swB = Stopwatch.StartNew();
                    GpuCompute_WriteBuffer(bPos, posPtr, (ulong)N * 8);
                    fixed (int* pz = zeroHost) GpuCompute_WriteBuffer(bCounts, (IntPtr)pz, (ulong)CELL * 4);
                    GpuCompute_Dispatch(kCount, bgCount, gCount);
                    GpuCompute_Sync();
                    fixed (int* pc = countsHost) GpuCompute_ReadBack(bCounts, (IntPtr)pc, (ulong)CELL * 4);
                    int sum = 0;
                    for (int c = 0; c < CELL; c++) { cursorHost[c] = sum; sum += countsHost[c]; }
                    fixed (int* pcu = cursorHost)
                    {
                        GpuCompute_WriteBuffer(bCellStart, (IntPtr)pcu, (ulong)CELL * 4);
                        GpuCompute_WriteBuffer(bCursor, (IntPtr)pcu, (ulong)CELL * 4);
                    }
                    GpuCompute_Dispatch(kPlace, bgPlace, gPlace);
                    swB.Stop();
                    // 查询：query + sync + 回读结果
                    var swQ = Stopwatch.StartNew();
                    GpuCompute_Dispatch(kQuery, bgQuery, gQuery);
                    GpuCompute_Sync();
                    fixed (int* pr = resultHost) GpuCompute_ReadBack(bResult, (IntPtr)pr, (ulong)K * 4);
                    swQ.Stop();
                    return (swB.Elapsed.TotalMilliseconds, swQ.Elapsed.TotalMilliseconds);
                }
            }
        }

        /// <summary>GridSearch 构建/查询耗时对比表（默认输出）：C# / C++ / CUDA / GPU。</summary>
        public static unsafe void RunGridCompare()
        {
            Console.WriteLine();
            Console.WriteLine($"----- GridSearch closest @{N:N0} 实体 / {K:N0} 查询（grid {DIM}x{DIM}） -----");
            GpuCompute_Initialize("wgpu_native.dll");
            if (Err() != null) { Console.WriteLine($"[FAIL] init: {Err()}"); return; }

            var pos = Gen(1234, N);
            var qry = Gen(777, K);
            var (csBuild, csQuery, csResult) = CsBaseline(pos, qry);
            var (cppB, cppQ) = RunCppComparison(pos, qry);
            var (ispcB, ispcQ) = RunIspcComparison(pos, qry);
            var (cudaB, cudaQ, cudaMismatch) = RunCudaComparison(pos, qry, csResult);
            var (gpuB, gpuQ, gpuMismatch, gpuHits, csHits) = MeasureGpuGrid(pos, qry, csResult);

            Console.WriteLine($"  {"实现",-16}{"构建 ms",10}{"查询 ms",10}{"合计 ms",10}   parity");
            Console.WriteLine($"  {"C# 多线程",-16}{csBuild,10:F3}{csQuery,10:F3}{csBuild + csQuery,10:F3}   —");
            Console.WriteLine($"  {"C++ (JobSystem)",-16}{cppB,10:F3}{cppQ,10:F3}{cppB + cppQ,10:F3}   —");
            Console.WriteLine($"  {"ISPC (JobSystem)",-16}{ispcB,10:F3}{ispcQ,10:F3}{ispcB + ispcQ,10:F3}   —");
            if (cudaB >= 0)
                Console.WriteLine($"  {"CUDA (pinned)",-16}{cudaB,10:F3}{cudaQ,10:F3}{cudaB + cudaQ,10:F3}   {cudaMismatch}/{K}");
            Console.WriteLine($"  {"GPU (wgpu)",-16}{gpuB,10:F3}{gpuQ,10:F3}{gpuB + gpuQ,10:F3}   {gpuMismatch}/{K}（命中 {gpuHits}/{K} vs C# {csHits}/{K}）");
            Console.WriteLine();
        }

        // ---------------- GPU 全量更新流程（诊断模式） ----------------

        public static unsafe void Run()
        {
            Console.WriteLine();
            Console.WriteLine($"----- GridSearch closest 全量更新 @{N:N0} 实体 / {K:N0} 查询（grid {DIM}x{DIM}） -----");
            RunGridCompare();
            Console.WriteLine();
            RunMoveCudaComparison();
            RunScheduleCuda();
        }
    }
}
