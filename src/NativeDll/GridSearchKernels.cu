// GridSearchKernels.cu - GridSearch closest full-update CUDA kernels (nvcc -ptx, driver API load)
// Same counting-sort grid as the WGSL version: count -> CPU prefix -> place -> query.
#include <vector_types.h>
#include <device_atomic_functions.h>

#ifndef RESTRICT
#ifdef _MSC_VER
#define RESTRICT __restrict
#else
#define RESTRICT restrict
#endif
#endif
extern "C" __global__ void grid_count(const float2* RESTRICT pos, int* RESTRICT counts, int dimX, int dimY, int n) {
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i >= n) return;
    float px = pos[i].x, py = pos[i].y;
    int cx = (int)floorf((px + 100.f) * 1.f); if (cx < 0) cx = 0; else if (cx >= dimX) cx = dimX - 1;
    int cy = (int)floorf((py + 100.f) * 1.f); if (cy < 0) cy = 0; else if (cy >= dimY) cy = dimY - 1;
    atomicAdd(&counts[cx + cy * dimX], 1);
}

extern "C" __global__ void grid_place(const float2* RESTRICT pos, int* RESTRICT cursor,
                                      float2* RESTRICT sorted, int2* RESTRICT hashIdx,
                                      int dimX, int dimY, int n) {
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i >= n) return;
    float px = pos[i].x, py = pos[i].y;
    int cx = (int)floorf((px + 100.f) * 1.f); if (cx < 0) cx = 0; else if (cx >= dimX) cx = dimX - 1;
    int cy = (int)floorf((py + 100.f) * 1.f); if (cy < 0) cy = 0; else if (cy >= dimY) cy = dimY - 1;
    int h = cx + cy * dimX;
    int slot = atomicAdd(&cursor[h], 1);
    sorted[slot] = pos[i];
    hashIdx[slot] = make_int2(h, i);
}

extern "C" __global__ void grid_query(const float2* RESTRICT query, const int* RESTRICT cellStart,
                                      const float2* RESTRICT sorted, const int2* RESTRICT hashIdx,
                                      int* RESTRICT result, int dimX, int dimY, int sortedLength, int k) {
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i >= k) return;
    result[i] = -1;
    float qx = query[i].x, qy = query[i].y;
    int cx = (int)floorf((qx + 100.f) * 1.f); if (cx < 0) cx = 0; else if (cx >= dimX) cx = dimX - 1;
    int cy = (int)floorf((qy + 100.f) * 1.f); if (cy < 0) cy = 0; else if (cy >= dimY) cy = dimY - 1;
    float bestD = 3.402823466e38f;
    int bestIdx = -1;
    for (int dx = -1; dx <= 1; dx++) {
        int nx = cx + dx;
        if (nx < 0 || nx >= dimX) continue;
        for (int dy = -1; dy <= 1; dy++) {
            int ny = cy + dy;
            if (ny < 0 || ny >= dimY) continue;
            int c = ny * dimX + nx;
            int start = cellStart[c];
            int end = (c + 1 < dimX * dimY) ? cellStart[c + 1] : sortedLength;
            for (int j = start; j < end; j++) {
                float dx2 = qx - sorted[j].x, dy2 = qy - sorted[j].y;
                float d2 = dx2 * dx2 + dy2 * dy2;
                if (d2 < bestD) { bestD = d2; bestIdx = hashIdx[j].y; }
            }
        }
    }
    if (bestIdx >= 0) result[i] = bestIdx;
}

// ---- Light/Heavy Move：数学与 EntJoySample.GpuJob 的 GpuMoveJob / GpuHeavyJob 逐句一致（parity 可比） ----
#define DT (1.0f / 60.0f)
#define VIEWPORT_W 1920.f
#define VIEWPORT_H 1080.f

extern "C" __global__ void move_kernel(float2* RESTRICT pos, float2* RESTRICT vel, int n) {
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i >= n) return;
    float px = pos[i].x + vel[i].x * DT;
    float py = pos[i].y + vel[i].y * DT;
    float vx = vel[i].x, vy = vel[i].y;
    if (px < 0.f || px > VIEWPORT_W) vx = -vx;
    if (py < 0.f || py > VIEWPORT_H) vy = -vy;
    pos[i].x = px; pos[i].y = py;
    vel[i].x = vx; vel[i].y = vy;
}

extern "C" __global__ void heavy_kernel(float2* RESTRICT pos, float2* RESTRICT vel, int n) {
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i >= n) return;
    float px = pos[i].x, py = pos[i].y;
    float vx = vel[i].x, vy = vel[i].y;
    float acc = 0.f, x = px;
    for (int k = 0; k < 16; k++) {
        acc += sinf(x) + cosf(x) + sqrtf(x * x + 1.f);
        x += vx * DT;
    }
    px += acc * DT;
    py += vy * DT;
    pos[i].x = px; pos[i].y = py;
    vel[i].x = vx; vel[i].y = vy;
}
