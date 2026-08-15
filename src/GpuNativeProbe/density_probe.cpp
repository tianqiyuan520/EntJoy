// density_probe.cpp — 瓶颈分类器（GpuEvaluator）阈值验证探针
// 目标：扫 computeDensity = kernel / max(upload, readback) 连续区间，验证运行时自适应分类器
//   （docs/gpu/17 §六.3）的阈值边界（0.3 / 0.6）稳定性 + 量化选错路代价。
//   表1：N=1M 固定，iter ∈ {0,1,2,4,8,16,32,64,128} 扫 density 连续区间。
//        每档测 full 三拆（cudaEvent upload|kernel|readback）+ resident（常驻 kernel）+ incr@10%（hash diff 闭环，非流水）。
//   表2：iter ∈ {0,16,64} × N ∈ {256k,512k,1M} 验证 density 随规模漂移（scale-invariance）
//        ——分类器「注册时校准一次」是否可靠，还是必须运行时持续测量。
// 用法：GpuNativeProbe.exe density（独立入口，不并入 all）

#define ENTJOY_NO_FLOAT2
#include <cuda_runtime.h>
#include <immintrin.h>
#include "common.h"
/* common.h 定义 GridSearch 的 N/K 宏（density 探针不用）；#undef 使 N 可作局部变量名 */
#undef N
#undef K

extern "C" void cuda_launch_heavy_iter_s(float2* dPos, const float2* dVel, float dt, int n, int iters, int grid, int block, cudaStream_t s);
extern "C" void cuda_launch_scatter_chunk(const float2* src, float2* dst, const int* chunkIdx, int nchunks, int chunkSize, int block, cudaStream_t s);
extern "C" void cuda_launch_gather_chunk(const float2* src, float2* dst, const int* chunkIdx, int nchunks, int chunkSize, int block, cudaStream_t s);

#define BLOCK 256
#define CHUNK 128
#define CHUNK_BYTES (CHUNK * sizeof(float2))   /* 2KB/chunk/分量 */
#define DENSITY_WARMUP 3
#define DENSITY_FRAMES 20
#define SP_INCR 0.10f   /* 增量测量固定 dirty 率（增量窗口内代表点，sp=10%） */

#define CUDA_CHECK(expr) do { cudaError_t _e = (expr); if (_e != cudaSuccess) { \
    printf("  CUDA error at %s:%d: %s\n", __FILE__, __LINE__, cudaGetErrorString(_e)); goto fail; } } while (0)
#define CUDA_MALLOC(ptr, size) CUDA_CHECK(cudaMalloc((void**)&(ptr), (size)))

/* ================= hash 索引 diff（从 diff_probe.cpp 复制；density 探针独立编译） ================= */
struct chunk_hash { unsigned hPos, hVel; };

static unsigned crc32c_4chain(const void* data, size_t n8) {
    const unsigned long long* p = (const unsigned long long*)data;
    unsigned c0 = 0, c1 = 0, c2 = 0, c3 = 0;
    size_t i = 0;
    for (; i + 4 <= n8; i += 4) {
        c0 = (unsigned)_mm_crc32_u64(c0, p[i]);
        c1 = (unsigned)_mm_crc32_u64(c1, p[i + 1]);
        c2 = (unsigned)_mm_crc32_u64(c2, p[i + 2]);
        c3 = (unsigned)_mm_crc32_u64(c3, p[i + 3]);
    }
    for (; i < n8; i++) c0 = (unsigned)_mm_crc32_u64(c0, p[i]);
    return (unsigned)_mm_crc32_u64((unsigned)_mm_crc32_u64(c0, c1), (unsigned)_mm_crc32_u64(c2, c3));
}
static chunk_hash hash_chunk_crc(const float2* pos, const float2* vel) {
    chunk_hash h;
    h.hPos = crc32c_4chain(pos, CHUNK_BYTES / 8);
    h.hVel = crc32c_4chain(vel, CHUNK_BYTES / 8);
    return h;
}
static int diff_hash(const float2* pos, const float2* vel, chunk_hash* hashTab, int nchunk, int* dirtyIdx) {
    int nd = 0;
    for (int c = 0; c < nchunk; c++) {
        chunk_hash h = hash_chunk_crc(pos + (size_t)c * CHUNK, vel + (size_t)c * CHUNK);
        if (h.hPos != hashTab[c].hPos || h.hVel != hashTab[c].hVel) {
            hashTab[c] = h;
            if (dirtyIdx) dirtyIdx[nd] = c;
            nd++;
        }
    }
    return nd;
}
static void build_staging(const int* dirty, int nd, const float2* pos, const float2* vel,
                          float2* posStg, float2* velStg) {
    for (int j = 0; j < nd; j++) {
        int c = dirty[j];
        memcpy(posStg + (size_t)j * CHUNK, pos + (size_t)c * CHUNK, CHUNK * sizeof(float2));
        memcpy(velStg + (size_t)j * CHUNK, vel + (size_t)c * CHUNK, CHUNK * sizeof(float2));
    }
}
static void patch_outcache(float2* outCache, const float2* outStg, const int* dirty, int nd) {
    for (int j = 0; j < nd; j++) {
        int c = dirty[j];
        memcpy(outCache + (size_t)c * CHUNK, outStg + (size_t)j * CHUNK, CHUNK * sizeof(float2));
    }
}

/* ================= 本地 rng + gameplay 修改（与 dirty/diff 探针同构） ================= */
static unsigned long long d_rng;
static double d_rnd(void) {
    d_rng = d_rng * 6364136223846793005ULL + 1442695040888963407ULL;
    return (double)((d_rng >> 33) & 0x7FFFFFFF) / 2147483647.0;
}
static void gen_changes(int nd, int nchunk, int* used, float2* pos, float2* vel) {
    memset(used, 0, nchunk * sizeof(int));
    if (nd >= nchunk) {   /* 全改：顺序选满防 do-while 碰撞爆炸 */
        for (int c = 0; c < nchunk; c++)
            for (int e = c * CHUNK; e < (c + 1) * CHUNK; e++) {
                pos[e].x += 0.1f; pos[e].y -= 0.05f;
                vel[e].x += 0.02f; vel[e].y += 0.01f;
            }
        return;
    }
    for (int j = 0; j < nd; j++) {
        int c;
        do { c = (int)(d_rnd() * nchunk); } while (used[c]);
        used[c] = 1;
        for (int e = c * CHUNK; e < (c + 1) * CHUNK; e++) {
            pos[e].x += 0.1f; pos[e].y -= 0.05f;
            vel[e].x += 0.02f; vel[e].y += 0.01f;
        }
    }
}

static const char* classify(double density) {
    if (density > 0.6) return "COMPUTE ";
    if (density < 0.3) return "BANDWIDTH";
    return "MIXED   ";
}

/* ================= 表1：N=1M 固定，iter 扫 density 连续区间 ================= */
static void run_table1(void) {
    const int iters[] = { 0, 1, 2, 4, 8, 16, 32, 64, 128 };
    const int niter = (int)(sizeof(iters) / sizeof(iters[0]));
    const int N = MOVE_N;
    const int nchunk = N / CHUNK;
    const int mgrid = (N + BLOCK - 1) / BLOCK;
    const int nd = (int)(nchunk * SP_INCR + 0.5f);
    float2 *pos = NULL, *vel = NULL, *outCache = NULL, *tmp = NULL;
    float2 *posStg = NULL, *velStg = NULL, *outStg = NULL;
    float2 *dPos = NULL, *dVel = NULL, *dOut = NULL;
    float2 *dPosStg = NULL, *dVelStg = NULL, *dOutStg = NULL;
    int *dirty = NULL, *used = NULL, *dChunkIdx = NULL;
    chunk_hash* hashTab = NULL;
    cudaStream_t s = NULL;
    cudaEvent_t evF0 = NULL, evF1 = NULL, evF2 = NULL, evF3 = NULL;
    int i;

    printf("  Table1：N=%d 固定，iter 扫 computeDensity 连续区间（sp=%d%% 增量，p50 ms）\n", N, (int)(SP_INCR * 100));
    printf("  iter   kernel  upload  readback  density   分类   full  resident  incr@%d%%  full/incr\n", (int)(SP_INCR * 100));

    CUDA_CHECK(cudaSetDevice(0));
    CUDA_CHECK(cudaStreamCreate(&s));
    CUDA_CHECK(cudaEventCreate(&evF0)); CUDA_CHECK(cudaEventCreate(&evF1));
    CUDA_CHECK(cudaEventCreate(&evF2)); CUDA_CHECK(cudaEventCreate(&evF3));
    CUDA_CHECK(cudaHostAlloc((void**)&pos, (size_t)N * sizeof(float2), cudaHostAllocDefault));
    CUDA_CHECK(cudaHostAlloc((void**)&vel, (size_t)N * sizeof(float2), cudaHostAllocDefault));
    CUDA_CHECK(cudaHostAlloc((void**)&outCache, (size_t)N * sizeof(float2), cudaHostAllocDefault));
    CUDA_CHECK(cudaHostAlloc((void**)&tmp, (size_t)N * sizeof(float2), cudaHostAllocDefault));
    CUDA_CHECK(cudaHostAlloc((void**)&posStg, (size_t)nchunk * CHUNK * sizeof(float2), cudaHostAllocDefault));
    CUDA_CHECK(cudaHostAlloc((void**)&velStg, (size_t)nchunk * CHUNK * sizeof(float2), cudaHostAllocDefault));
    CUDA_CHECK(cudaHostAlloc((void**)&outStg, (size_t)nchunk * CHUNK * sizeof(float2), cudaHostAllocDefault));
    dirty = (int*)malloc(nchunk * sizeof(int));
    used = (int*)malloc(nchunk * sizeof(int));
    hashTab = (chunk_hash*)malloc(nchunk * sizeof(chunk_hash));
    if (!dirty || !used || !hashTab) { printf("  malloc fail\n"); goto fail; }
    CUDA_MALLOC(dPos, (size_t)N * sizeof(float2));
    CUDA_MALLOC(dVel, (size_t)N * sizeof(float2));
    CUDA_MALLOC(dOut, (size_t)N * sizeof(float2));
    CUDA_MALLOC(dPosStg, (size_t)nchunk * CHUNK * sizeof(float2));
    CUDA_MALLOC(dVelStg, (size_t)nchunk * CHUNK * sizeof(float2));
    CUDA_MALLOC(dOutStg, (size_t)nchunk * CHUNK * sizeof(float2));
    CUDA_MALLOC(dChunkIdx, nchunk * sizeof(int));

    for (int ti = 0; ti < niter; ti++) {
        int iter = iters[ti];
        double wk[DENSITY_FRAMES], wu[DENSITY_FRAMES], wr[DENSITY_FRAMES];
        double wfull[DENSITY_FRAMES], wres[DENSITY_FRAMES], wincr[DENSITY_FRAMES];

        /* ---- 重置 host 数据 ---- */
        d_rng = 1234;
        for (i = 0; i < N; i++) {
            pos[i].x = (float)(d_rnd() * 200 - 100); pos[i].y = (float)(d_rnd() * 200 - 100);
            vel[i].x = (float)(d_rnd() * 200 - 100); vel[i].y = (float)(d_rnd() * 200 - 100);
        }
        /* 初始全量上传 + kernel + 全量回读 → outCache = 初始 GPU 输出（增量基线镜像） */
        CUDA_CHECK(cudaMemcpyAsync(dPos, pos, (size_t)N * sizeof(float2), cudaMemcpyHostToDevice, s));
        CUDA_CHECK(cudaMemcpyAsync(dVel, vel, (size_t)N * sizeof(float2), cudaMemcpyHostToDevice, s));
        cuda_launch_heavy_iter_s(dPos, dVel, DT, N, iter, mgrid, BLOCK, s);
        CUDA_CHECK(cudaMemcpyAsync(outCache, dOut, (size_t)N * sizeof(float2), cudaMemcpyDeviceToHost, s));
        CUDA_CHECK(cudaStreamSynchronize(s));
        for (int c = 0; c < nchunk; c++) hashTab[c] = hash_chunk_crc(pos + (size_t)c * CHUNK, vel + (size_t)c * CHUNK);

        /* ---- full 往返三拆：upload|kernel|readback 各 event 段 ---- */
        for (int f = 0; f < DENSITY_WARMUP + DENSITY_FRAMES; f++) {
            double t0 = now_ms();
            CUDA_CHECK(cudaEventRecord(evF0, s));
            CUDA_CHECK(cudaMemcpyAsync(dPos, pos, (size_t)N * sizeof(float2), cudaMemcpyHostToDevice, s));
            CUDA_CHECK(cudaMemcpyAsync(dVel, vel, (size_t)N * sizeof(float2), cudaMemcpyHostToDevice, s));
            CUDA_CHECK(cudaEventRecord(evF1, s));
            cuda_launch_heavy_iter_s(dPos, dVel, DT, N, iter, mgrid, BLOCK, s);
            CUDA_CHECK(cudaEventRecord(evF2, s));
            CUDA_CHECK(cudaMemcpyAsync(tmp, dOut, (size_t)N * sizeof(float2), cudaMemcpyDeviceToHost, s));
            CUDA_CHECK(cudaEventRecord(evF3, s));
            CUDA_CHECK(cudaStreamSynchronize(s));
            if (f >= DENSITY_WARMUP) {
                wfull[f - DENSITY_WARMUP] = now_ms() - t0;
                float u, k, r;
                cudaEventElapsedTime(&u, evF0, evF1);
                cudaEventElapsedTime(&k, evF1, evF2);
                cudaEventElapsedTime(&r, evF2, evF3);
                wk[f - DENSITY_WARMUP] = k; wu[f - DENSITY_WARMUP] = u; wr[f - DENSITY_WARMUP] = r;
            }
        }

        /* ---- resident：常驻 kernel（零传输，上限形态） ---- */
        for (int f = 0; f < DENSITY_WARMUP + DENSITY_FRAMES; f++) {
            double t0 = now_ms();
            cuda_launch_heavy_iter_s(dPos, dVel, DT, N, iter, mgrid, BLOCK, s);
            CUDA_CHECK(cudaStreamSynchronize(s));
            if (f >= DENSITY_WARMUP) wres[f - DENSITY_WARMUP] = now_ms() - t0;
        }

        /* ---- 重置 host（避免 full/resident 的 GPU 状态污染 host；gameplay 从初始态开始） ---- */
        d_rng = 4321;
        for (i = 0; i < N; i++) {
            pos[i].x = (float)(d_rnd() * 200 - 100); pos[i].y = (float)(d_rnd() * 200 - 100);
            vel[i].x = (float)(d_rnd() * 200 - 100); vel[i].y = (float)(d_rnd() * 200 - 100);
        }
        CUDA_CHECK(cudaMemcpyAsync(dPos, pos, (size_t)N * sizeof(float2), cudaMemcpyHostToDevice, s));
        CUDA_CHECK(cudaMemcpyAsync(dVel, vel, (size_t)N * sizeof(float2), cudaMemcpyHostToDevice, s));
        cuda_launch_heavy_iter_s(dPos, dVel, DT, N, iter, mgrid, BLOCK, s);
        CUDA_CHECK(cudaMemcpyAsync(outCache, dOut, (size_t)N * sizeof(float2), cudaMemcpyDeviceToHost, s));
        CUDA_CHECK(cudaStreamSynchronize(s));
        for (int c = 0; c < nchunk; c++) hashTab[c] = hash_chunk_crc(pos + (size_t)c * CHUNK, vel + (size_t)c * CHUNK);

        /* ---- incr@10%：hash diff + staging 拼接 + scatter + kernel + gather + patch（非流水，每帧同步） ---- */
        for (int f = 0; f < DENSITY_WARMUP + DENSITY_FRAMES; f++) {
            gen_changes(nd, nchunk, used, pos, vel);   /* gameplay 修改 host（tf 前，不计入墙钟） */
            double t0 = now_ms();
            int ndc = diff_hash(pos, vel, hashTab, nchunk, dirty);
            build_staging(dirty, ndc, pos, vel, posStg, velStg);
            CUDA_CHECK(cudaMemcpyAsync(dChunkIdx, dirty, ndc * sizeof(int), cudaMemcpyHostToDevice, s));
            CUDA_CHECK(cudaMemcpyAsync(dPosStg, posStg, (size_t)ndc * CHUNK * sizeof(float2), cudaMemcpyHostToDevice, s));
            CUDA_CHECK(cudaMemcpyAsync(dVelStg, velStg, (size_t)ndc * CHUNK * sizeof(float2), cudaMemcpyHostToDevice, s));
            cuda_launch_scatter_chunk(dPosStg, dPos, dChunkIdx, ndc, CHUNK, BLOCK, s);
            cuda_launch_scatter_chunk(dVelStg, dVel, dChunkIdx, ndc, CHUNK, BLOCK, s);
            cuda_launch_heavy_iter_s(dPos, dVel, DT, N, iter, mgrid, BLOCK, s);
            cuda_launch_gather_chunk(dOut, dOutStg, dChunkIdx, ndc, CHUNK, BLOCK, s);
            CUDA_CHECK(cudaMemcpyAsync(outStg, dOutStg, (size_t)ndc * CHUNK * sizeof(float2), cudaMemcpyDeviceToHost, s));
            CUDA_CHECK(cudaStreamSynchronize(s));
            patch_outcache(outCache, outStg, dirty, ndc);
            if (f >= DENSITY_WARMUP) wincr[f - DENSITY_WARMUP] = now_ms() - t0;
        }

        double k = median(wk, DENSITY_FRAMES), u = median(wu, DENSITY_FRAMES), r = median(wr, DENSITY_FRAMES);
        double full = median(wfull, DENSITY_FRAMES);
        double res = median(wres, DENSITY_FRAMES);
        double incr = median(wincr, DENSITY_FRAMES);
        double trans = u > r ? u : r;
        double density = trans > 0 ? k / trans : 0;
        printf("  %4d  %7.3f %7.3f %8.3f  %7.3f  %s  %7.3f  %8.3f  %8.3f   %7.2f\n",
            iter, k, u, r, density, classify(density), full, res, incr,
            incr > 0 ? full / incr : 0);
    }

    cudaFreeHost(pos); cudaFreeHost(vel); cudaFreeHost(outCache); cudaFreeHost(tmp);
    cudaFreeHost(posStg); cudaFreeHost(velStg); cudaFreeHost(outStg);
    free(dirty); free(used); free(hashTab);
    cudaFree(dPos); cudaFree(dVel); cudaFree(dOut);
    cudaFree(dPosStg); cudaFree(dVelStg); cudaFree(dOutStg); cudaFree(dChunkIdx);
    cudaEventDestroy(evF0); cudaEventDestroy(evF1); cudaEventDestroy(evF2); cudaEventDestroy(evF3);
    cudaStreamDestroy(s);
    return;

fail:
    cudaFreeHost(pos); cudaFreeHost(vel); cudaFreeHost(outCache); cudaFreeHost(tmp);
    cudaFreeHost(posStg); cudaFreeHost(velStg); cudaFreeHost(outStg);
    free(dirty); free(used); free(hashTab);
    cudaFree(dPos); cudaFree(dVel); cudaFree(dOut);
    cudaFree(dPosStg); cudaFree(dVelStg); cudaFree(dOutStg); cudaFree(dChunkIdx);
    cudaEventDestroy(evF0); cudaEventDestroy(evF1); cudaEventDestroy(evF2); cudaEventDestroy(evF3);
    cudaStreamDestroy(s);
}

/* ================= 表2：iter×N scale 稳定性（density 是否随规模漂移） ================= */
static void run_table2(void) {
    const int iters[] = { 0, 16, 64 };
    const int niter = (int)(sizeof(iters) / sizeof(iters[0]));
    const int Ns[] = { 262144, 524288, 1000000 };
    const int nN = (int)(sizeof(Ns) / sizeof(Ns[0]));
    float2 *pos = NULL, *vel = NULL, *tmp = NULL;
    float2 *dPos = NULL, *dVel = NULL, *dOut = NULL;
    cudaStream_t s = NULL;
    cudaEvent_t evF0 = NULL, evF1 = NULL, evF2 = NULL, evF3 = NULL;

    printf("  Table2：iter × N scale 稳定性（density 随规模漂移 → 分类器「注册校准一次」是否可靠）\n");
    printf("  iter       N   kernel  upload  readback  density   分类\n");

    CUDA_CHECK(cudaSetDevice(0));
    CUDA_CHECK(cudaStreamCreate(&s));
    CUDA_CHECK(cudaEventCreate(&evF0)); CUDA_CHECK(cudaEventCreate(&evF1));
    CUDA_CHECK(cudaEventCreate(&evF2)); CUDA_CHECK(cudaEventCreate(&evF3));

    for (int ti = 0; ti < niter; ti++) {
        int iter = iters[ti];
        for (int ni = 0; ni < nN; ni++) {
            int N = Ns[ni];
            int mgrid = (N + BLOCK - 1) / BLOCK;
            double wk[DENSITY_FRAMES], wu[DENSITY_FRAMES], wr[DENSITY_FRAMES];

            CUDA_CHECK(cudaHostAlloc((void**)&pos, (size_t)N * sizeof(float2), cudaHostAllocDefault));
            CUDA_CHECK(cudaHostAlloc((void**)&vel, (size_t)N * sizeof(float2), cudaHostAllocDefault));
            CUDA_CHECK(cudaHostAlloc((void**)&tmp, (size_t)N * sizeof(float2), cudaHostAllocDefault));
            CUDA_MALLOC(dPos, (size_t)N * sizeof(float2));
            CUDA_MALLOC(dVel, (size_t)N * sizeof(float2));
            CUDA_MALLOC(dOut, (size_t)N * sizeof(float2));

            d_rng = 1234;
            for (int i = 0; i < N; i++) {
                pos[i].x = (float)(d_rnd() * 200 - 100); pos[i].y = (float)(d_rnd() * 200 - 100);
                vel[i].x = (float)(d_rnd() * 200 - 100); vel[i].y = (float)(d_rnd() * 200 - 100);
            }
            CUDA_CHECK(cudaMemcpyAsync(dPos, pos, (size_t)N * sizeof(float2), cudaMemcpyHostToDevice, s));
            CUDA_CHECK(cudaMemcpyAsync(dVel, vel, (size_t)N * sizeof(float2), cudaMemcpyHostToDevice, s));
            cuda_launch_heavy_iter_s(dPos, dVel, DT, N, iter, mgrid, BLOCK, s);
            CUDA_CHECK(cudaStreamSynchronize(s));

            for (int f = 0; f < DENSITY_WARMUP + DENSITY_FRAMES; f++) {
                CUDA_CHECK(cudaEventRecord(evF0, s));
                CUDA_CHECK(cudaMemcpyAsync(dPos, pos, (size_t)N * sizeof(float2), cudaMemcpyHostToDevice, s));
                CUDA_CHECK(cudaMemcpyAsync(dVel, vel, (size_t)N * sizeof(float2), cudaMemcpyHostToDevice, s));
                CUDA_CHECK(cudaEventRecord(evF1, s));
                cuda_launch_heavy_iter_s(dPos, dVel, DT, N, iter, mgrid, BLOCK, s);
                CUDA_CHECK(cudaEventRecord(evF2, s));
                CUDA_CHECK(cudaMemcpyAsync(tmp, dOut, (size_t)N * sizeof(float2), cudaMemcpyDeviceToHost, s));
                CUDA_CHECK(cudaEventRecord(evF3, s));
                CUDA_CHECK(cudaStreamSynchronize(s));
                if (f >= DENSITY_WARMUP) {
                    float u, k, r;
                    cudaEventElapsedTime(&u, evF0, evF1);
                    cudaEventElapsedTime(&k, evF1, evF2);
                    cudaEventElapsedTime(&r, evF2, evF3);
                    wk[f - DENSITY_WARMUP] = k; wu[f - DENSITY_WARMUP] = u; wr[f - DENSITY_WARMUP] = r;
                }
            }

            double k = median(wk, DENSITY_FRAMES), u = median(wu, DENSITY_FRAMES), r = median(wr, DENSITY_FRAMES);
            double trans = u > r ? u : r;
            double density = trans > 0 ? k / trans : 0;
            printf("  %4d  %7d  %7.3f %7.3f %8.3f  %7.3f  %s\n",
                iter, N, k, u, r, density, classify(density));

            cudaFreeHost(pos); cudaFreeHost(vel); cudaFreeHost(tmp);
            cudaFree(dPos); cudaFree(dVel); cudaFree(dOut);
            pos = vel = tmp = NULL; dPos = dVel = dOut = NULL;
        }
    }

    cudaEventDestroy(evF0); cudaEventDestroy(evF1); cudaEventDestroy(evF2); cudaEventDestroy(evF3);
    cudaStreamDestroy(s);
    return;

fail:
    cudaFreeHost(pos); cudaFreeHost(vel); cudaFreeHost(tmp);
    cudaFree(dPos); cudaFree(dVel); cudaFree(dOut);
    cudaEventDestroy(evF0); cudaEventDestroy(evF1); cudaEventDestroy(evF2); cudaEventDestroy(evF3);
    cudaStreamDestroy(s);
}

void RunDensityProbe(void) {
    printf("  Density 探针（GpuEvaluator 运行时自适应分类器阈值验证，docs/gpu/17 §六.3）\n");
    printf("  computeDensity = kernelWall / max(uploadWall, readbackWall)；阈值 0.3 / 0.6\n");
    printf("  Host buffer 用 cudaHostAllocDefault（可缓存，hash diff 需 CPU 读）。\n");
    run_table1();
    run_table2();
}
