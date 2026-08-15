// cuda_kernels.cu — CUDA C 内核（nvcc 编译）。
// float2/int2 用 CUDA 内建（vector_types.h，布局 {x,y} 与 common.h 一致，host 端可直接传指针）。
// 三个负载 kernel 数学逐句镜像：
//   heavy_kernel  = heavy_cpu / HeavyJobChunkCpp（16-iter sin/cos，常数 0.001/0.01/0.03125/0.0625/0.985/0.982/0.015/0.012/0.0002/0.0003/0.0001）
//   light_kernel  = light_cpu / MoveJobChunkCpp（pos += vel*dt）
//   closest_kernel = GridSearch2D.ClosestPointJobPointer.Execute / 09 ClosestPointKernel（3×3 邻域 + 空邻域全局回退）
// launch 包装 extern "C"，host 端（cuda_probe.cpp）用 common.h 的 float2/int2（布局与 CUDA 内建一致）直接传指针。

#ifndef RESTRICT
#ifdef _MSC_VER
#define RESTRICT __restrict
#else
#define RESTRICT restrict
#endif
#endif

/* closest kernel 的 uniform 参数（布局必须与 host 端 ClosestParams 一致，见 cuda_probe.cpp） */
typedef struct {
    float OriginX, OriginY, GridResolutionInv, SquaredEpsilonSelf;
    int GridDimX, GridDimY, SortedLength, IgnoreSelf;
} ClosestParams;

/* ================= heavy：16-iter sin/cos（镜像 HeavyJobChunkCpp） ================= */
extern "C" __global__ void heavy_kernel(float2* RESTRICT pos, const float2* RESTRICT vel, float dt, int n) {
    int index = blockIdx.x * blockDim.x + threadIdx.x;
    if (index >= n) return;
    float px = pos[index].x, py = pos[index].y;
    float vx = vel[index].x, vy = vel[index].y;
    float accX = px * 0.001f + vx * 0.01f;
    float accY = py * 0.001f + vy * 0.01f;
    for (int iteration = 0; iteration < 16; iteration++) {
        float phaseX = accX + iteration * 0.03125f;
        float phaseY = accY - iteration * 0.0625f;
        float wave = sinf(phaseX) + cosf(phaseY);
        float radius = sqrtf(accX * accX + accY * accY + 1.0f);
        accX = accX * 0.985f + wave * 0.015f + radius * 0.0002f + vx * 0.0001f;
        accY = accY * 0.982f - wave * 0.012f + radius * 0.0003f + vy * 0.0001f;
    }
    pos[index].x = px + vx * dt + accX * 0.001f;
    pos[index].y = py + vy * dt + accY * 0.001f;
}

/* ================= heavy 可变迭代（density 探针：iter 运行时传参，扫 computeDensity 连续区间） =================
   与 heavy_kernel 唯一差异：内层循环上界 iters 运行时传入（0 = 只剩头尾，等价 light+边缘计算）。 */
extern "C" __global__ void heavy_iter_kernel(float2* RESTRICT pos, const float2* RESTRICT vel, float dt, int n, int iters) {
    int index = blockIdx.x * blockDim.x + threadIdx.x;
    if (index >= n) return;
    float px = pos[index].x, py = pos[index].y;
    float vx = vel[index].x, vy = vel[index].y;
    float accX = px * 0.001f + vx * 0.01f;
    float accY = py * 0.001f + vy * 0.01f;
    for (int iteration = 0; iteration < iters; iteration++) {
        float phaseX = accX + iteration * 0.03125f;
        float phaseY = accY - iteration * 0.0625f;
        float wave = sinf(phaseX) + cosf(phaseY);
        float radius = sqrtf(accX * accX + accY * accY + 1.0f);
        accX = accX * 0.985f + wave * 0.015f + radius * 0.0002f + vx * 0.0001f;
        accY = accY * 0.982f - wave * 0.012f + radius * 0.0003f + vy * 0.0001f;
    }
    pos[index].x = px + vx * dt + accX * 0.001f;
    pos[index].y = py + vy * dt + accY * 0.001f;
}

/* ================= light：pos += vel*dt（镜像 MoveJobChunkCpp） ================= */
extern "C" __global__ void light_kernel(float2* RESTRICT pos, const float2* RESTRICT vel, float dt, int n) {
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i >= n) return;
    pos[i].x += vel[i].x * dt;
    pos[i].y += vel[i].y * dt;
}

/* ================= closest：3×3 邻域 + 空邻域全局回退（镜像 09 ClosestPointKernel） ================= */
extern "C" __global__ void closest_kernel(
    const float2* RESTRICT queryPositions, const int2* RESTRICT hashIndex,
    const int2* RESTRICT cellStartEnd, const float2* RESTRICT sortedPositions,
    int* RESTRICT results, ClosestParams p, int k)
{
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i >= k) return;
    results[i] = -1;
    float2 q = queryPositions[i];
    int cx = (int)floorf((q.x - p.OriginX) * p.GridResolutionInv);
    cx = cx < 0 ? 0 : (cx > p.GridDimX - 1 ? p.GridDimX - 1 : cx);
    int cy = (int)floorf((q.y - p.OriginY) * p.GridResolutionInv);
    cy = cy < 0 ? 0 : (cy > p.GridDimY - 1 ? p.GridDimY - 1 : cy);

    float bestDistSq = 3.40282347e38f;
    int bestIdx = -1;

    for (int dx = -1; dx <= 1; dx++) {
        int nx = cx + dx;
        if ((unsigned)nx >= (unsigned)p.GridDimX) continue;
        for (int dy = -1; dy <= 1; dy++) {
            int ny = cy + dy;
            if ((unsigned)ny >= (unsigned)p.GridDimY) continue;
            int cellHash = ny * p.GridDimX + nx;
            int2 range = cellStartEnd[cellHash];
            int start = range.x, end = range.y;
            if (start < 0) continue;
            for (int j = start; j < end; j++) {
                float2 pnt = sortedPositions[j];
                float dx2 = q.x - pnt.x, dy2 = q.y - pnt.y;
                float distSq = dx2 * dx2 + dy2 * dy2;
                if (p.IgnoreSelf != 0 && distSq < p.SquaredEpsilonSelf) continue;
                if (distSq < bestDistSq) { bestDistSq = distSq; bestIdx = j; }
            }
        }
    }

    if (bestIdx != -1) {
        results[i] = hashIndex[bestIdx].y;
    } else {
        for (int j = 0; j < p.SortedLength; j++) {
            float2 pnt = sortedPositions[j];
            float dx2 = q.x - pnt.x, dy2 = q.y - pnt.y;
            float distSq = dx2 * dx2 + dy2 * dy2;
            if (p.IgnoreSelf != 0 && distSq < p.SquaredEpsilonSelf) continue;
            if (distSq < bestDistSq) { bestDistSq = distSq; bestIdx = j; }
        }
        if (bestIdx != -1) results[i] = hashIndex[bestIdx].y;
    }
}

/* ================= host launch 包装（extern "C"，供 cuda_probe.cpp 调用） ================= */
extern "C" void cuda_launch_heavy(float2* dPos, const float2* dVel, float dt, int n, int grid, int block) {
    heavy_kernel<<<grid, block>>>(dPos, dVel, dt, n);
}
extern "C" void cuda_launch_light(float2* dPos, const float2* dVel, float dt, int n, int grid, int block) {
    light_kernel<<<grid, block>>>(dPos, dVel, dt, n);
}
extern "C" void cuda_launch_closest(
    const float2* dQuery, const int2* dHash, const int2* dCell, const float2* dSorted,
    int* dResults, ClosestParams p, int k, int grid, int block)
{
    closest_kernel<<<grid, block>>>(dQuery, dHash, dCell, dSorted, dResults, p, k);
}

/* ================= reduce：pos 归约为 sum+bbox（float4: sumX,sumY,minX,maxX，16B 小输出） =================
   探针 ①（16 §六.1）：数据留 GPU，reduce 聚合后只回读 16B，对照全量回读 8MB（Move）/400KB（GridSearch）。
   两级归约：K1 每 block 归约局部段写 part[blockIdx]（grid×16B）；K2 单 block 归约 part → out（16B）。 */
extern "C" __global__ void reduce_pos_k1(const float2* RESTRICT pos, float4* RESTRICT part, int n) {
    int tid = threadIdx.x;
    int stride = blockDim.x * gridDim.x;
    float sx = 0.0f, sy = 0.0f, mn = 3.40282347e38f, mx = -3.40282347e38f;
    for (int i = blockIdx.x * blockDim.x + tid; i < n; i += stride) {
        float2 p = pos[i];
        sx += p.x; sy += p.y;
        if (p.x < mn) mn = p.x; if (p.x > mx) mx = p.x;
    }
    __shared__ float shX[256], shY[256], shMn[256], shMx[256];
    shX[tid] = sx; shY[tid] = sy; shMn[tid] = mn; shMx[tid] = mx;
    __syncthreads();
    for (int s = blockDim.x >> 1; s > 0; s >>= 1) {
        if (tid < s) {
            shX[tid] += shX[tid + s];
            shY[tid] += shY[tid + s];
            if (shMn[tid + s] < shMn[tid]) shMn[tid] = shMn[tid + s];
            if (shMx[tid + s] > shMx[tid]) shMx[tid] = shMx[tid + s];
        }
        __syncthreads();
    }
    if (tid == 0) part[blockIdx.x] = make_float4(shX[0], shY[0], shMn[0], shMx[0]);
}

extern "C" __global__ void reduce_pos_k2(const float4* RESTRICT part, float4* RESTRICT out, int p) {
    int tid = threadIdx.x;
    float sx = 0.0f, sy = 0.0f, mn = 3.40282347e38f, mx = -3.40282347e38f;
    for (int i = tid; i < p; i += blockDim.x) {
        float4 v = part[i];
        sx += v.x; sy += v.y;
        if (v.z < mn) mn = v.z; if (v.w > mx) mx = v.w;
    }
    __shared__ float shX[256], shY[256], shMn[256], shMx[256];
    shX[tid] = sx; shY[tid] = sy; shMn[tid] = mn; shMx[tid] = mx;
    __syncthreads();
    for (int s = blockDim.x >> 1; s > 0; s >>= 1) {
        if (tid < s) {
            shX[tid] += shX[tid + s];
            shY[tid] += shY[tid + s];
            if (shMn[tid + s] < shMn[tid]) shMn[tid] = shMn[tid + s];
            if (shMx[tid + s] > shMx[tid]) shMx[tid] = shMx[tid + s];
        }
        __syncthreads();
    }
    if (tid == 0) out[0] = make_float4(shX[0], shY[0], shMn[0], shMx[0]);
}

extern "C" void cuda_launch_reduce_pos(const float2* dPos, float4* dPart, float4* dOut, int n, int block) {
    int grid = (n + block - 1) / block;
    reduce_pos_k1<<<grid, block>>>(dPos, dPart, n);
    reduce_pos_k2<<<1, block>>>(dPart, dOut, grid);
}

/* ================= stream 版 launch（探针② 跨帧流水：上传 sUp ‖ kernel sC 重叠） ================= */
extern "C" void cuda_launch_heavy_s(float2* dPos, const float2* dVel, float dt, int n, int grid, int block, cudaStream_t s) {
    heavy_kernel<<<grid, block, 0, s>>>(dPos, dVel, dt, n);
}
extern "C" void cuda_launch_heavy_iter_s(float2* dPos, const float2* dVel, float dt, int n, int iters, int grid, int block, cudaStream_t s) {
    heavy_iter_kernel<<<grid, block, 0, s>>>(dPos, dVel, dt, n, iters);
}
extern "C" void cuda_launch_light_s(float2* dPos, const float2* dVel, float dt, int n, int grid, int block, cudaStream_t s) {
    light_kernel<<<grid, block, 0, s>>>(dPos, dVel, dt, n);
}
extern "C" void cuda_launch_reduce_pos_s(const float2* dPos, float4* dPart, float4* dOut, int n, int block, cudaStream_t s) {
    int grid = (n + block - 1) / block;
    reduce_pos_k1<<<grid, block, 0, s>>>(dPos, dPart, n);
    reduce_pos_k2<<<1, block, 0, s>>>(dPart, dOut, grid);
}

/* ================= scatter/gather：staging 连续段 ↔ 常驻 buffer 的变更 chunk 位置 =================
   脏同步增量用（dirty_probe.cpp）：变更 chunk 数据拼进 staging 一次大 DMA 上传，scatter 铺回常驻 buffer；
   回读 gather 变更 chunk 到 staging 一次大 DMA 读回。免「逐 chunk 小 DMA」的每笔固定延迟（~4-6µs）。
   chunkIdx = 变更 chunk 索引列表（前 nchunks 有效）；chunkSize = 每 chunk 元素数（CHUNK）。 */
extern "C" __global__ void scatter_chunk_kernel(const float2* RESTRICT src, float2* RESTRICT dst,
    const int* RESTRICT chunkIdx, int nchunks, int chunkSize) {
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i >= nchunks * chunkSize) return;
    int c = chunkIdx[i / chunkSize];
    dst[c * chunkSize + (i % chunkSize)] = src[i];
}
extern "C" __global__ void gather_chunk_kernel(const float2* RESTRICT src, float2* RESTRICT dst,
    const int* RESTRICT chunkIdx, int nchunks, int chunkSize) {
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i >= nchunks * chunkSize) return;
    int c = chunkIdx[i / chunkSize];
    dst[i] = src[c * chunkSize + (i % chunkSize)];
}
extern "C" void cuda_launch_scatter_chunk(const float2* src, float2* dst, const int* chunkIdx,
    int nchunks, int chunkSize, int block, cudaStream_t s) {
    int n = nchunks * chunkSize;
    scatter_chunk_kernel<<<(n + block - 1) / block, block, 0, s>>>(src, dst, chunkIdx, nchunks, chunkSize);
}
extern "C" void cuda_launch_gather_chunk(const float2* src, float2* dst, const int* chunkIdx,
    int nchunks, int chunkSize, int block, cudaStream_t s) {
    int n = nchunks * chunkSize;
    gather_chunk_kernel<<<(n + block - 1) / block, block, 0, s>>>(src, dst, chunkIdx, nchunks, chunkSize);
}
