// cuda_probe.cpp — CUDA 探针（runtime API，cudaHostAlloc 页锁定）
// 与 opencl_probe.cpp / wgpu_probe.cpp 相同负载与测量结构：
//   · GridSearch closest@100k：常驻（grid+query 上传一次，纯 kernel）/ 往返（每帧传 query↑+result↓）/ parity vs CPU 参考
//   · HeavyMove/LightMove@1M：常驻 / 往返(staged pageable) / 往返(页锁定 cudaHostAlloc 单跳)
// CUDA 的页锁定是真驱动级（cudaHostAlloc → cudaMemcpy 单跳 DMA），等价 ILGPU CUDA 的 cudaHostAlloc 列，
// 与 OpenCL 的 VirtualLock hack / wgpu 的 mapped-buffer 直写形成对照。
// 内核为手写 CUDA C（cuda_kernels.cu，数学镜像 transpiler），对照 ILGPU 编译出的 CUDA 列。
//
// 类型：cuda_runtime.h 的 vector_types.h 定义 CUDA 内建 float2/int2；common.h 的自定义版本用
// ENTJOY_NO_FLOAT2 跳过，host 端统一用 CUDA 类型（字段布局一致），避免 C2371 重定义。

#define ENTJOY_NO_FLOAT2
#include <cuda_runtime.h>
#include "common.h"
#include "cuda_probe.h"

/* CUDA 内建 int2 的 mk2 补定义（common.h 版本被 ENTJOY_NO_FLOAT2 跳过；float2 直接字面量赋值） */
static int2 mk2(int x, int y) { int2 v; v.x = x; v.y = y; return v; }

/* 与 cuda_kernels.cu 的 ClosestParams 布局一致的 host 版本 */
typedef struct {
    float OriginX, OriginY, GridResolutionInv, SquaredEpsilonSelf;
    int GridDimX, GridDimY, SortedLength, IgnoreSelf;
} ClosestParams;

/* cuda_kernels.cu 的 launch 包装（extern "C"，与 .cu 的 C 符号一致；参数类型是 .cu 的 CUDA float2/int2，布局与 common.h 一致） */
extern "C" void cuda_launch_heavy(float2* dPos, const float2* dVel, float dt, int n, int grid, int block);
extern "C" void cuda_launch_light(float2* dPos, const float2* dVel, float dt, int n, int grid, int block);
extern "C" void cuda_launch_closest(const float2* dQuery, const int2* dHash, const int2* dCell, const float2* dSorted,
    int* dResults, ClosestParams p, int k, int grid, int block);
extern "C" void cuda_launch_reduce_pos(const float2* dPos, float4* dPart, float4* dOut, int n, int block);
extern "C" void cuda_launch_heavy_s(float2* dPos, const float2* dVel, float dt, int n, int grid, int block, cudaStream_t s);
extern "C" void cuda_launch_light_s(float2* dPos, const float2* dVel, float dt, int n, int grid, int block, cudaStream_t s);
extern "C" void cuda_launch_reduce_pos_s(const float2* dPos, float4* dPart, float4* dOut, int n, int block, cudaStream_t s);

/* cudaMalloc 是 void** 接口；MSVC 需要显式 cast（C 的 void** 不做隐式） */
#define CUDA_MALLOC(ptr, size) CUDA_CHECK(cudaMalloc((void**)&(ptr), (size)))

#define BLOCK 256
#define CUDA_CHECK(expr) do { cudaError_t _e = (expr); if (_e != cudaSuccess) { \
    printf("  CUDA error at %s:%d: %s\n", __FILE__, __LINE__, cudaGetErrorString(_e)); goto fail; } } while (0)

/* CPU closest 参考：逐字镜像 kernel（3×3 邻域 + 空邻域全局回退），供 parity 逐元素比对 */
static int closest_cpu_ref(
    const float2* queryPositions, const int2* hashIndex, const int2* cellStartEnd,
    const float2* sortedPositions, int i, ClosestParams p)
{
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
    if (bestIdx != -1) return hashIndex[bestIdx].y;
    for (int j = 0; j < p.SortedLength; j++) {
        float2 pnt = sortedPositions[j];
        float dx2 = q.x - pnt.x, dy2 = q.y - pnt.y;
        float distSq = dx2 * dx2 + dy2 * dy2;
        if (p.IgnoreSelf != 0 && distSq < p.SquaredEpsilonSelf) continue;
        if (distSq < bestDistSq) { bestDistSq = distSq; bestIdx = j; }
    }
    return bestIdx != -1 ? hashIndex[bestIdx].y : -1;
}

double RunCudaProbe(void) {
    /* ===== 所有局部变量声明集中在函数顶部（goto fail 之前），避免 MSVC C2362「跳过初始化」 ===== */
    float2 *pos = NULL, *qry = NULL, *sorted = NULL, *mPos = NULL, *mVel = NULL, *mPosOut = NULL;
    int2 *hashIdx = NULL, *cellSE = NULL;
    int *results = NULL, *counts = NULL;
    float2 *dSorted = NULL, *dQuery = NULL, *dMPos = NULL, *dMVel = NULL;
    int2 *dHash = NULL, *dCell = NULL;
    int* dResults = NULL;
    float2 *hQueryPin = NULL;
    int* hResultsPin = NULL;
    float2 *hPosPin = NULL, *hVelPin = NULL, *hOutPin = NULL;
    int devCount = 0;
    cudaDeviceProp prop; memset(&prop, 0, sizeof(prop));
    float minx = 0, maxx = 0, miny = 0, maxy = 0;
    float ox = 0, oy = 0, invX = 0, invY = 0;
    int sum = 0;
    ClosestParams cp; memset(&cp, 0, sizeof(cp));
    int cgrid = 0, mgrid = 0;
    float dt = 0;
    double gridResident = 0, gridRoundtrip = 0, gridRoundtripPinned = 0;
    int mismatch = 0, found = 0, bad = 0;
    double moveResident[2] = {0}, moveRoundtrip[2] = {0}, movePinned[2] = {0};
    double reducePinned[2] = {0}, pipe[2] = {0};
    float4 *dReducePart = NULL, *dReduceOut = NULL;
    float4 hReduceOut; memset(&hReduceOut, 0, sizeof(hReduceOut));
    cudaStream_t sUp = NULL, sC = NULL;
    cudaEvent_t evUp[2] = {NULL, NULL};
    float2 *dPosBuf[2] = {NULL, NULL}, *dVelBuf[2] = {NULL, NULL};
    cudaGraph_t gr = NULL; cudaGraphExec_t grEx = NULL;
    double graphP[2] = {0}, asyncSerial[2] = {0};

    if (cudaGetDeviceCount(&devCount) != cudaSuccess || devCount == 0) {
        printf("  cudaGetDeviceCount: 无 CUDA 设备\n");
        return -1;
    }
    CUDA_CHECK(cudaSetDevice(0));
    CUDA_CHECK(cudaGetDeviceProperties(&prop, 0));
    printf("  device: %s (sm_%d%d)\n", prop.name, prop.major, prop.minor);

    /* ============ GridSearch closest @100k（数据构建与 opencl_probe 完全一致：seed 1234 顺序） ============ */
    pos = (float2*)malloc(N * sizeof(float2));
    qry = (float2*)malloc(K * sizeof(float2));
    sorted = (float2*)malloc(N * sizeof(float2));
    hashIdx = (int2*)malloc(N * sizeof(int2));
    cellSE = (int2*)malloc(CELL * sizeof(int2));
    results = (int*)malloc(K * sizeof(int));
    for (int i = 0; i < N; i++) { pos[i].x = (float)(rnd() * 200 - 100); pos[i].y = (float)(rnd() * 200 - 100); }
    for (int i = 0; i < K; i++) { qry[i].x = (float)(rnd() * 200 - 100); qry[i].y = (float)(rnd() * 200 - 100); }

    minx = pos[0].x; maxx = pos[0].x; miny = pos[0].y; maxy = pos[0].y;
    for (int i = 1; i < N; i++) {
        if (pos[i].x < minx) minx = pos[i].x; if (pos[i].x > maxx) maxx = pos[i].x;
        if (pos[i].y < miny) miny = pos[i].y; if (pos[i].y > maxy) maxy = pos[i].y;
    }
    ox = minx; oy = miny;
    invX = DIM / (maxx - minx > 0 ? maxx - minx : 1e-6f);
    invY = DIM / (maxy - miny > 0 ? maxy - miny : 1e-6f);
    counts = (int*)calloc(CELL, sizeof(int));
    for (int i = 0; i < N; i++) counts[hash_of(pos[i], ox, oy, invX, invY)]++;
    sum = 0;
    for (int c = 0; c < CELL; c++) { int cnt = counts[c]; counts[c] = sum; sum += cnt; }
    for (int c = 0; c < CELL; c++) {
        int start = counts[c];
        int end = (c + 1 < CELL) ? counts[c + 1] : N;
        cellSE[c] = (start == end) ? mk2(-1, -1) : mk2(start, end);
    }
    for (int i = 0; i < N; i++) {
        int hash = hash_of(pos[i], ox, oy, invX, invY);
        int dest = counts[hash]++;
        sorted[dest] = pos[i];
        hashIdx[dest] = mk2(hash, i);
    }

    cp.OriginX = ox; cp.OriginY = oy;
    cp.GridResolutionInv = invX;   /* kernel 与 opencl 一致：单值 GridResolutionInv（x 向）同时用于两轴 */
    cp.SquaredEpsilonSelf = 0.0f;
    cp.GridDimX = DIM; cp.GridDimY = DIM;
    cp.SortedLength = N; cp.IgnoreSelf = 0;

    CUDA_MALLOC(dSorted, N * sizeof(float2));
    CUDA_MALLOC(dHash, N * sizeof(int2));
    CUDA_MALLOC(dCell, CELL * sizeof(int2));
    CUDA_MALLOC(dQuery, K * sizeof(float2));
    CUDA_MALLOC(dResults, K * sizeof(int));
    CUDA_CHECK(cudaMemcpy(dSorted, sorted, N * sizeof(float2), cudaMemcpyHostToDevice));
    CUDA_CHECK(cudaMemcpy(dHash, hashIdx, N * sizeof(int2), cudaMemcpyHostToDevice));
    CUDA_CHECK(cudaMemcpy(dCell, cellSE, CELL * sizeof(int2), cudaMemcpyHostToDevice));
    CUDA_CHECK(cudaMemcpy(dQuery, qry, K * sizeof(float2), cudaMemcpyHostToDevice));

    /* ---- GridSearch 页锁定 host：query 上传（WriteCombined，GPU 单跳读）+ result 读回（普通 pinned） ---- */
    CUDA_CHECK(cudaHostAlloc((void**)&hQueryPin, K * sizeof(float2), cudaHostAllocWriteCombined));
    CUDA_CHECK(cudaHostAlloc((void**)&hResultsPin, K * sizeof(int), cudaHostAllocDefault));
    memcpy(hQueryPin, qry, K * sizeof(float2));

    cgrid = (K + BLOCK - 1) / BLOCK;

    /* ---- 常驻：grid+query 已上传，纯 kernel ---- */
    for (int i = 0; i < GRID_WARMUP; i++) {
        cuda_launch_closest(dQuery, dHash, dCell, dSorted, dResults, cp, K, cgrid, BLOCK);
        CUDA_CHECK(cudaDeviceSynchronize());
    }
    double wk[GRID_FRAMES];
    for (int f = 0; f < GRID_FRAMES; f++) {
        double t0 = now_ms();
        cuda_launch_closest(dQuery, dHash, dCell, dSorted, dResults, cp, K, cgrid, BLOCK);
        CUDA_CHECK(cudaDeviceSynchronize());
        wk[f] = now_ms() - t0;
    }
    gridResident = median(wk, GRID_FRAMES);

    /* ---- 往返 staged：pageable 每帧上传 ---- */
    double wr[GRID_FRAMES];
    for (int f = 0; f < GRID_FRAMES; f++) {
        double t0 = now_ms();
        CUDA_CHECK(cudaMemcpy(dQuery, qry, K * sizeof(float2), cudaMemcpyHostToDevice));
        cuda_launch_closest(dQuery, dHash, dCell, dSorted, dResults, cp, K, cgrid, BLOCK);
        CUDA_CHECK(cudaMemcpy(results, dResults, K * sizeof(int), cudaMemcpyDeviceToHost));
        CUDA_CHECK(cudaDeviceSynchronize());
        wr[f] = now_ms() - t0;
    }
    gridRoundtrip = median(wr, GRID_FRAMES);

    /* ---- 往返 页锁定：cudaHostAlloc 单跳 DMA（query 上传 WriteCombined + result 读回） ---- */
    double wp_g[GRID_FRAMES];
    for (int f = 0; f < GRID_FRAMES; f++) {
        double t0 = now_ms();
        CUDA_CHECK(cudaMemcpy(dQuery, hQueryPin, K * sizeof(float2), cudaMemcpyHostToDevice));
        cuda_launch_closest(dQuery, dHash, dCell, dSorted, dResults, cp, K, cgrid, BLOCK);
        CUDA_CHECK(cudaMemcpy(hResultsPin, dResults, K * sizeof(int), cudaMemcpyDeviceToHost));
        CUDA_CHECK(cudaDeviceSynchronize());
        wp_g[f] = now_ms() - t0;
    }
    gridRoundtripPinned = median(wp_g, GRID_FRAMES);

    /* ---- parity：GPU 结果（页锁定读回）vs CPU 参考逐元素 ---- */
    for (int i = 0; i < K; i++) {
        if (hResultsPin[i] != closest_cpu_ref(qry, hashIdx, cellSE, sorted, i, cp)) mismatch++;
    }
    for (int i = 0; i < K; i++) if (hResultsPin[i] != -1) found++;

    /* ============ Move @1M：HeavyMove / LightMove ============ */
    mPos = (float2*)malloc(MOVE_N * sizeof(float2));
    mVel = (float2*)malloc(MOVE_N * sizeof(float2));
    mPosOut = (float2*)malloc(MOVE_N * sizeof(float2));
    for (int i = 0; i < MOVE_N; i++) {
        mPos[i].x = (float)(rnd() * 200 - 100); mPos[i].y = (float)(rnd() * 200 - 100);
        mVel[i].x = (float)(rnd() * 200 - 100); mVel[i].y = (float)(rnd() * 200 - 100);
    }

    CUDA_MALLOC(dMPos, MOVE_N * sizeof(float2));
    CUDA_MALLOC(dMVel, MOVE_N * sizeof(float2));
    CUDA_MALLOC(dReducePart, ((MOVE_N + BLOCK - 1) / BLOCK) * sizeof(float4));
    CUDA_MALLOC(dReduceOut, sizeof(float4));
    CUDA_MALLOC(dPosBuf[0], MOVE_N * sizeof(float2));
    CUDA_MALLOC(dVelBuf[0], MOVE_N * sizeof(float2));
    CUDA_MALLOC(dPosBuf[1], MOVE_N * sizeof(float2));
    CUDA_MALLOC(dVelBuf[1], MOVE_N * sizeof(float2));
    CUDA_CHECK(cudaStreamCreate(&sUp));
    CUDA_CHECK(cudaStreamCreate(&sC));
    CUDA_CHECK(cudaEventCreate(&evUp[0]));
    CUDA_CHECK(cudaEventCreate(&evUp[1]));
    CUDA_CHECK(cudaMemcpy(dMPos, mPos, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice));
    CUDA_CHECK(cudaMemcpy(dMVel, mVel, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice));
    mgrid = (MOVE_N + BLOCK - 1) / BLOCK;
    dt = DT;

    /* ---- 页锁定 host 内存（cudaHostAlloc，真驱动级 pinned；staged 对照用普通 pageable）。
          上传 buffer 用 WriteCombined（GPU 只读，单跳 DMA 更优）；读回 buffer 用默认（CPU 每帧读）。 ---- */
    CUDA_CHECK(cudaHostAlloc((void**)&hPosPin, MOVE_N * sizeof(float2), cudaHostAllocWriteCombined));
    CUDA_CHECK(cudaHostAlloc((void**)&hVelPin, MOVE_N * sizeof(float2), cudaHostAllocWriteCombined));
    CUDA_CHECK(cudaHostAlloc((void**)&hOutPin, MOVE_N * sizeof(float2), cudaHostAllocDefault));
    memcpy(hPosPin, mPos, MOVE_N * sizeof(float2));
    memcpy(hVelPin, mVel, MOVE_N * sizeof(float2));

    for (int mk = 0; mk < 2; mk++) {
        int heavy = (mk == 0);
        /* 常驻：数据已在设备，纯 kernel */
        for (int i = 0; i < MOVE_WARMUP; i++) {
            if (heavy) cuda_launch_heavy(dMPos, dMVel, dt, MOVE_N, mgrid, BLOCK);
            else       cuda_launch_light(dMPos, dMVel, dt, MOVE_N, mgrid, BLOCK);
            CUDA_CHECK(cudaDeviceSynchronize());
        }
        double ws[MOVE_FRAMES];
        for (int f = 0; f < MOVE_FRAMES; f++) {
            double t0 = now_ms();
            if (heavy) cuda_launch_heavy(dMPos, dMVel, dt, MOVE_N, mgrid, BLOCK);
            else       cuda_launch_light(dMPos, dMVel, dt, MOVE_N, mgrid, BLOCK);
            CUDA_CHECK(cudaDeviceSynchronize());
            ws[f] = now_ms() - t0;
        }
        moveResident[mk] = median(ws, MOVE_FRAMES);

        /* 往返 staged：pageable host 每帧上传 */
        double wrr[MOVE_FRAMES];
        for (int f = 0; f < MOVE_FRAMES; f++) {
            double t0 = now_ms();
            CUDA_CHECK(cudaMemcpy(dMPos, mPos, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice));
            CUDA_CHECK(cudaMemcpy(dMVel, mVel, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice));
            if (heavy) cuda_launch_heavy(dMPos, dMVel, dt, MOVE_N, mgrid, BLOCK);
            else       cuda_launch_light(dMPos, dMVel, dt, MOVE_N, mgrid, BLOCK);
            CUDA_CHECK(cudaMemcpy(mPosOut, dMPos, MOVE_N * sizeof(float2), cudaMemcpyDeviceToHost));
            CUDA_CHECK(cudaDeviceSynchronize());
            wrr[f] = now_ms() - t0;
        }
        moveRoundtrip[mk] = median(wrr, MOVE_FRAMES);

        /* 往返 页锁定：cudaHostAlloc 单跳 DMA */
        double wp[MOVE_FRAMES];
        for (int f = 0; f < MOVE_FRAMES; f++) {
            double t0 = now_ms();
            CUDA_CHECK(cudaMemcpy(dMPos, hPosPin, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice));
            CUDA_CHECK(cudaMemcpy(dMVel, hVelPin, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice));
            if (heavy) cuda_launch_heavy(dMPos, dMVel, dt, MOVE_N, mgrid, BLOCK);
            else       cuda_launch_light(dMPos, dMVel, dt, MOVE_N, mgrid, BLOCK);
            CUDA_CHECK(cudaMemcpy(hOutPin, dMPos, MOVE_N * sizeof(float2), cudaMemcpyDeviceToHost));
            CUDA_CHECK(cudaDeviceSynchronize());
            wp[f] = now_ms() - t0;
        }
        movePinned[mk] = median(wp, MOVE_FRAMES);
    }

    /* ---- 探针① reduce 小输出回读：上传 16MB + kernel + reduce(sum+bbox) + 回读 16B ----
       对照 movePinned（全量回读 8MB）：量化「输出小」形态的税下降（16 §六.1）。 */
    for (int mk = 0; mk < 2; mk++) {
        int heavy = (mk == 0);
        double wrd[MOVE_FRAMES];
        for (int f = 0; f < MOVE_FRAMES; f++) {
            double t0 = now_ms();
            CUDA_CHECK(cudaMemcpy(dMPos, hPosPin, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice));
            CUDA_CHECK(cudaMemcpy(dMVel, hVelPin, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice));
            if (heavy) cuda_launch_heavy(dMPos, dMVel, dt, MOVE_N, mgrid, BLOCK);
            else       cuda_launch_light(dMPos, dMVel, dt, MOVE_N, mgrid, BLOCK);
            cuda_launch_reduce_pos(dMPos, dReducePart, dReduceOut, MOVE_N, BLOCK);
            CUDA_CHECK(cudaMemcpy(&hReduceOut, dReduceOut, sizeof(float4), cudaMemcpyDeviceToHost));
            CUDA_CHECK(cudaDeviceSynchronize());
            wrd[f] = now_ms() - t0;
        }
        reducePinned[mk] = median(wrd, MOVE_FRAMES);
    }
    if (hReduceOut.x < -1e15f || hReduceOut.x > 1e15f || hReduceOut.y < -1e15f || hReduceOut.y > 1e15f) bad++;

    /* ---- 探针② 跨帧流水：双 stream + 双 buffer，上传(f+1)‖kernel(f)+reduce 重叠 ----
       上传走 sUp（DMA 引擎），kernel+reduce+读回走 sC（SM）；sC 等 sUp 的 event 确认输入就绪。
       稳态每帧墙钟 ≈ max(16MB DMA, kernel+reduce)，kernel 完全藏进上传（16 §六.2）。 */
    for (int mk = 0; mk < 2; mk++) {
        int heavy = (mk == 0);
        /* 预上传 frame0 到 buf[0]，warmup 3 帧稳态 */
        CUDA_CHECK(cudaMemcpyAsync(dPosBuf[0], hPosPin, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, sUp));
        CUDA_CHECK(cudaMemcpyAsync(dVelBuf[0], hVelPin, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, sUp));
        CUDA_CHECK(cudaEventRecord(evUp[0], sUp));
        for (int w = 0; w < 3; w++) {
            int cur = (w & 1) ? 1 : 0, nxt = cur ^ 1;
            CUDA_CHECK(cudaMemcpyAsync(dPosBuf[nxt], hPosPin, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, sUp));
            CUDA_CHECK(cudaMemcpyAsync(dVelBuf[nxt], hVelPin, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, sUp));
            CUDA_CHECK(cudaEventRecord(evUp[nxt], sUp));
            CUDA_CHECK(cudaStreamWaitEvent(sC, evUp[cur], 0));
            if (heavy) cuda_launch_heavy_s(dPosBuf[cur], dVelBuf[cur], dt, MOVE_N, mgrid, BLOCK, sC);
            else       cuda_launch_light_s(dPosBuf[cur], dVelBuf[cur], dt, MOVE_N, mgrid, BLOCK, sC);
            cuda_launch_reduce_pos_s(dPosBuf[cur], dReducePart, dReduceOut, MOVE_N, BLOCK, sC);
            CUDA_CHECK(cudaMemcpyAsync(&hReduceOut, dReduceOut, sizeof(float4), cudaMemcpyDeviceToHost, sC));
            CUDA_CHECK(cudaStreamSynchronize(sC));
        }
        double wp2[MOVE_FRAMES];
        for (int f = 1; f <= MOVE_FRAMES; f++) {
            int cur = f & 1, nxt = cur ^ 1;
            double t0 = now_ms();
            CUDA_CHECK(cudaMemcpyAsync(dPosBuf[nxt], hPosPin, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, sUp));
            CUDA_CHECK(cudaMemcpyAsync(dVelBuf[nxt], hVelPin, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, sUp));
            CUDA_CHECK(cudaEventRecord(evUp[nxt], sUp));
            CUDA_CHECK(cudaStreamWaitEvent(sC, evUp[cur], 0));
            if (heavy) cuda_launch_heavy_s(dPosBuf[cur], dVelBuf[cur], dt, MOVE_N, mgrid, BLOCK, sC);
            else       cuda_launch_light_s(dPosBuf[cur], dVelBuf[cur], dt, MOVE_N, mgrid, BLOCK, sC);
            cuda_launch_reduce_pos_s(dPosBuf[cur], dReducePart, dReduceOut, MOVE_N, BLOCK, sC);
            CUDA_CHECK(cudaMemcpyAsync(&hReduceOut, dReduceOut, sizeof(float4), cudaMemcpyDeviceToHost, sC));
            CUDA_CHECK(cudaStreamSynchronize(sC));
            wp2[f - 1] = now_ms() - t0;
        }
        pipe[mk] = median(wp2, MOVE_FRAMES);
    }

    /* ---- 探针③ CUDA Graphs：kernel+reduce 两级捕获成图，每帧 1 graph launch vs 3 launch ----
       launch 开销量化（16 §六.3）：同 stream 串行 async 上传 + 读回，对照组（3-launch）与实验组（graph）
       交替测同一帧；差 = 省掉的 launch 开销。graph 指针固定单 buffer（dMPos/dMVel）。 */
    for (int mk = 0; mk < 2; mk++) {
        int heavy = (mk == 0);
        if (gr)   { cudaGraphDestroy(gr); gr = NULL; }
        if (grEx) { cudaGraphExecDestroy(grEx); grEx = NULL; }
        CUDA_CHECK(cudaStreamBeginCapture(sC, cudaStreamCaptureModeGlobal));
        if (heavy) cuda_launch_heavy_s(dMPos, dMVel, dt, MOVE_N, mgrid, BLOCK, sC);
        else       cuda_launch_light_s(dMPos, dMVel, dt, MOVE_N, mgrid, BLOCK, sC);
        cuda_launch_reduce_pos_s(dMPos, dReducePart, dReduceOut, MOVE_N, BLOCK, sC);
        CUDA_CHECK(cudaStreamEndCapture(sC, &gr));
        CUDA_CHECK(cudaGraphInstantiate(&grEx, gr, 0));
        for (int w = 0; w < 3; w++) {
            CUDA_CHECK(cudaMemcpyAsync(dMPos, hPosPin, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, sC));
            CUDA_CHECK(cudaMemcpyAsync(dMVel, hVelPin, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, sC));
            CUDA_CHECK(cudaGraphLaunch(grEx, sC));
            CUDA_CHECK(cudaMemcpyAsync(&hReduceOut, dReduceOut, sizeof(float4), cudaMemcpyDeviceToHost, sC));
            CUDA_CHECK(cudaStreamSynchronize(sC));
        }
        double wa[MOVE_FRAMES], wg[MOVE_FRAMES];
        for (int f = 0; f < MOVE_FRAMES; f++) {
            double t0 = now_ms();
            CUDA_CHECK(cudaMemcpyAsync(dMPos, hPosPin, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, sC));
            CUDA_CHECK(cudaMemcpyAsync(dMVel, hVelPin, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, sC));
            if (heavy) cuda_launch_heavy_s(dMPos, dMVel, dt, MOVE_N, mgrid, BLOCK, sC);
            else       cuda_launch_light_s(dMPos, dMVel, dt, MOVE_N, mgrid, BLOCK, sC);
            cuda_launch_reduce_pos_s(dMPos, dReducePart, dReduceOut, MOVE_N, BLOCK, sC);
            CUDA_CHECK(cudaMemcpyAsync(&hReduceOut, dReduceOut, sizeof(float4), cudaMemcpyDeviceToHost, sC));
            CUDA_CHECK(cudaStreamSynchronize(sC));
            wa[f] = now_ms() - t0;

            t0 = now_ms();
            CUDA_CHECK(cudaMemcpyAsync(dMPos, hPosPin, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, sC));
            CUDA_CHECK(cudaMemcpyAsync(dMVel, hVelPin, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, sC));
            CUDA_CHECK(cudaGraphLaunch(grEx, sC));
            CUDA_CHECK(cudaMemcpyAsync(&hReduceOut, dReduceOut, sizeof(float4), cudaMemcpyDeviceToHost, sC));
            CUDA_CHECK(cudaStreamSynchronize(sC));
            wg[f] = now_ms() - t0;
        }
        asyncSerial[mk] = median(wa, MOVE_FRAMES);
        graphP[mk] = median(wg, MOVE_FRAMES);
    }
    if (gr)   { cudaGraphDestroy(gr); gr = NULL; }
    if (grEx) { cudaGraphExecDestroy(grEx); grEx = NULL; }

    /* sanity：roundtrip 读回结果有限（float 实现差异，不做逐元素相等） */
    for (int i = 0; i < 1024; i++) {
        float2 v = mPosOut[(i * 977) % MOVE_N];
        if (!(v.x >= -1e9f && v.x <= 1e9f) || !(v.y >= -1e9f && v.y <= 1e9f)) bad++;
    }

    printf("\n  GridSearch@100k closest 常驻  : %8.3f ms\n", gridResident);
    printf("  GridSearch@100k closest 往返 staged : %8.3f ms  (pageable)\n", gridRoundtrip);
    printf("  GridSearch@100k closest 往返 页锁定 : %8.3f ms  (cudaHostAlloc 单跳)\n", gridRoundtripPinned);
    printf("  parity(closest GPU vs CPU)   : %d / %d mismatch；有结果查询 %d/%d\n", mismatch, K, found, K);
    printf("  sanity: Move 读回有限 %d 坏\n", bad);

    printf("\n  HeavyMove@1M(16iter) p50 ms：\n");
    printf("    CPU 单线程(C++ 镜像 transpiler): %8.3f\n", g_nativeHeavyCpu);
    printf("    NativeDll JobSystem 多线程       : %8.3f   (单线程 x%.2f)\n", g_nativeHeavyMt, g_nativeHeavyCpu / g_nativeHeavyMt);
    printf("    CUDA 常驻(kernel-only)           : %8.3f   (vs CPU 单线程 x%.2f)\n", moveResident[0], g_nativeHeavyCpu / moveResident[0]);
    printf("    CUDA 往返 staged(pageable)       : %8.3f   (vs CPU 单线程 x%.2f)\n", moveRoundtrip[0], g_nativeHeavyCpu / moveRoundtrip[0]);
    printf("    CUDA 往返 页锁定(cudaHostAlloc)  : %8.3f   (vs CPU 单线程 x%.2f)\n", movePinned[0], g_nativeHeavyCpu / movePinned[0]);
    printf("    CUDA 往返 页锁定 + reduce 小输出 : %8.3f   (回读 8MB→16B, vs 全量 pinned 省 %.3f ms)\n", reducePinned[0], movePinned[0] - reducePinned[0]);
    printf("    CUDA 往返 流水(双stream 上传‖kernel): %8.3f   (vs 串行 reduce 省 %.3f ms)\n", pipe[0], reducePinned[0] - pipe[0]);
    printf("    CUDA 往返 graph(1 launch) : %8.3f   (vs async 3-launch %.3f, 省 launch %.4f ms)\n", graphP[0], asyncSerial[0], asyncSerial[0] - graphP[0]);
    printf("    reduce 输出 sum(%.3e,%.3e) bbox-x[%.3f,%.3f]\n", hReduceOut.x, hReduceOut.y, hReduceOut.z, hReduceOut.w);
    printf("  LightMove@1M p50 ms：\n");
    printf("    CPU 单线程(C++ 镜像 transpiler): %8.3f\n", g_nativeLightCpu);
    printf("    NativeDll JobSystem 多线程       : %8.3f   (单线程 x%.2f)\n", g_nativeLightMt, g_nativeLightCpu / g_nativeLightMt);
    printf("    CUDA 常驻(kernel-only)           : %8.3f   (vs CPU 单线程 x%.2f)\n", moveResident[1], g_nativeLightCpu / moveResident[1]);
    printf("    CUDA 往返 staged(pageable)       : %8.3f   (vs CPU 单线程 x%.2f)\n", moveRoundtrip[1], g_nativeLightCpu / moveRoundtrip[1]);
    printf("    CUDA 往返 页锁定(cudaHostAlloc)  : %8.3f   (vs CPU 单线程 x%.2f)\n", movePinned[1], g_nativeLightCpu / movePinned[1]);
    printf("    CUDA 往返 页锁定 + reduce 小输出 : %8.3f   (回读 8MB→16B, vs 全量 pinned 省 %.3f ms)\n", reducePinned[1], movePinned[1] - reducePinned[1]);
    printf("    CUDA 往返 流水(双stream 上传‖kernel): %8.3f   (vs 串行 reduce 省 %.3f ms)\n", pipe[1], reducePinned[1] - pipe[1]);
    printf("    CUDA 往返 graph(1 launch) : %8.3f   (vs async 3-launch %.3f, 省 launch %.4f ms)\n", graphP[1], asyncSerial[1], asyncSerial[1] - graphP[1]);

    /* ---- 汇总到全局（main 最后打印直观对比表） ---- */
    g_sum.gridResident[2] = gridResident;
    g_sum.gridRoundtrip[2] = gridRoundtrip;
    g_sum.gridPinned[2] = gridRoundtripPinned;
    g_sum.gridMismatch[2] = mismatch;          /* 0/100000 严格逐元素 parity */
    g_sum.heavyResident[2] = moveResident[0]; g_sum.heavyRoundtrip[2] = moveRoundtrip[0];
    g_sum.heavyPinned[2] = movePinned[0];
    g_sum.lightResident[2] = moveResident[1]; g_sum.lightRoundtrip[2] = moveRoundtrip[1];
    g_sum.lightPinned[2] = movePinned[1];

    cudaFreeHost(hQueryPin); cudaFreeHost(hResultsPin);
    cudaFreeHost(hPosPin); cudaFreeHost(hVelPin); cudaFreeHost(hOutPin);
    cudaFree(dMPos); cudaFree(dMVel); cudaFree(dReducePart); cudaFree(dReduceOut);
    cudaFree(dPosBuf[0]); cudaFree(dVelBuf[0]); cudaFree(dPosBuf[1]); cudaFree(dVelBuf[1]);
    cudaStreamDestroy(sUp); cudaStreamDestroy(sC);
    cudaEventDestroy(evUp[0]); cudaEventDestroy(evUp[1]);
    if (gr)   cudaGraphDestroy(gr);
    if (grEx) cudaGraphExecDestroy(grEx);
    cudaFree(dSorted); cudaFree(dHash); cudaFree(dCell); cudaFree(dQuery); cudaFree(dResults);
    free(pos); free(qry); free(sorted); free(hashIdx); free(cellSE); free(results); free(counts);
    free(mPos); free(mVel); free(mPosOut);
    cudaDeviceReset();
    return gridResident;

fail:
    cudaFreeHost(hQueryPin); cudaFreeHost(hResultsPin);
    cudaFreeHost(hPosPin); cudaFreeHost(hVelPin); cudaFreeHost(hOutPin);
    cudaFree(dMPos); cudaFree(dMVel); cudaFree(dReducePart); cudaFree(dReduceOut);
    cudaFree(dPosBuf[0]); cudaFree(dVelBuf[0]); cudaFree(dPosBuf[1]); cudaFree(dVelBuf[1]);
    cudaStreamDestroy(sUp); cudaStreamDestroy(sC);
    cudaEventDestroy(evUp[0]); cudaEventDestroy(evUp[1]);
    if (gr)   cudaGraphDestroy(gr);
    if (grEx) cudaGraphExecDestroy(grEx);
    cudaFree(dSorted); cudaFree(dHash); cudaFree(dCell); cudaFree(dQuery); cudaFree(dResults);
    free(pos); free(qry); free(sorted); free(hashIdx); free(cellSE); free(results); free(counts);
    free(mPos); free(mVel); free(mPosOut);
    cudaDeviceReset();
    return -1;
}
