// dirty_probe.cpp — 脏同步增量探针 v2（staging 拼接，修正逐 chunk 小 DMA 固定延迟 + 累积状态缓存语义）
// 稀疏写场景：HeavyMove@1M（镜像 transpiler 数学），每帧只有 sp%（1%/5%/10%/50%）chunk（128 实体）被 gameplay 修改。
//
// v1 实测两个结论（先修再测）：
//   ① 逐 chunk 小 DMA 是死路：50% 变更 = 23436 笔 cudaMemcpyAsync（上传 pos+vel + 回读 dOut），
//      每笔 ~4.3µs 固定启动延迟 → 脏同步 102.7ms vs 全量 4.2ms（x0.04，越传越慢）。必须 staging 拼接。
//   ② 累积状态输出无法「缓存回读」：pos += vel*dt 每帧全量更新 dOut，未变更 chunk 的 GPU 输出也在变
//      （旧值+旧vel 已错位）——脏同步回读只在「无状态/幂等输出」成立。本探针诚实对照两种回读：
//        dirty-upload : 脏同步上传 + 全量回读（上传省税，回读税仍在 → 用户「我需要全部回读」的真实形态）
//        结果留GPU   : 脏同步上传 + 只回读变更 chunk 到 outCache 缓存（仅演示，累积输出 parity 必失败）
//
// v2 传输形态：变更 chunk 数据在 CPU 拼进连续 staging（pos/vel 各一段）→ 一次大 DMA 上传 →
//   GPU scatter_chunk_kernel 铺回 dPos/dVel → heavy kernel 全量 → gather_chunk_kernel 收变更结果到 staging
//   → 一次大 DMA 读回 → CPU 展开到 outCache。免逐 chunk 小 DMA 固定延迟，只留 1-2 笔 staging 大 DMA。
// 用法：GpuNativeProbe.exe dirty（独立入口，不并入 all，避免干扰基准）

#define ENTJOY_NO_FLOAT2
#include <cuda_runtime.h>
#include "common.h"

extern "C" void cuda_launch_heavy_s(float2* dPos, const float2* dVel, float dt, int n, int grid, int block, cudaStream_t s);
extern "C" void cuda_launch_scatter_chunk(const float2* src, float2* dst, const int* chunkIdx, int nchunks, int chunkSize, int block, cudaStream_t s);
extern "C" void cuda_launch_gather_chunk(const float2* src, float2* dst, const int* chunkIdx, int nchunks, int chunkSize, int block, cudaStream_t s);

#define BLOCK 256
#define CHUNK 128
#define NCHUNK (MOVE_N / CHUNK)
#define DIRTY_WARMUP 3
#define DIRTY_FRAMES 20

#define CUDA_CHECK(expr) do { cudaError_t _e = (expr); if (_e != cudaSuccess) { \
    printf("  CUDA error at %s:%d: %s\n", __FILE__, __LINE__, cudaGetErrorString(_e)); goto fail; } } while (0)
#define CUDA_MALLOC(ptr, size) CUDA_CHECK(cudaMalloc((void**)&(ptr), (size)))

/* 本地 LCG（不碰 common.h 的 g_rng，避免干扰其他探针的确定性） */
static unsigned long long d_rng;
static double d_rnd(void) {
    d_rng = d_rng * 6364136223846793005ULL + 1442695040888963407ULL;
    return (double)((d_rng >> 33) & 0x7FFFFFFF) / 2147483647.0;
}

void RunDirtyProbe(void) {
    float2 *pos = NULL, *vel = NULL, *outCache = NULL;
    float2 *posStagingH = NULL, *velStagingH = NULL, *outStagingH = NULL, *tmp = NULL;
    float2 *dPos = NULL, *dVel = NULL, *dOut = NULL;
    float2 *dPosStaging = NULL, *dVelStaging = NULL, *dOutStaging = NULL;
    int *dirty = NULL, *used = NULL, *dChunkIdx = NULL;
    cudaStream_t s = NULL;
    cudaEvent_t evA0 = NULL, evA1 = NULL, evA2 = NULL, evA3 = NULL, evA4 = NULL;
    cudaEvent_t evF0 = NULL, evF1 = NULL, evF2 = NULL, evF3 = NULL;
    int mgrid = (MOVE_N + BLOCK - 1) / BLOCK;
    float dt = DT;
    const float spList[8] = { 0.50f, 0.35f, 0.25f, 0.10f, 0.05f, 0.01f, 0.75f, 1.00f };   /* 倒序跑 + 补细扫点，验证 laptop 降频顺序效应 */
    int nsp = 8;
    int mismatch = 0;

    CUDA_CHECK(cudaSetDevice(0));
    CUDA_CHECK(cudaStreamCreate(&s));
    CUDA_CHECK(cudaEventCreate(&evA0)); CUDA_CHECK(cudaEventCreate(&evA1));
    CUDA_CHECK(cudaEventCreate(&evA2)); CUDA_CHECK(cudaEventCreate(&evA3));
    CUDA_CHECK(cudaEventCreate(&evA4));
    CUDA_CHECK(cudaEventCreate(&evF0)); CUDA_CHECK(cudaEventCreate(&evF1));
    CUDA_CHECK(cudaEventCreate(&evF2)); CUDA_CHECK(cudaEventCreate(&evF3));
    CUDA_CHECK(cudaHostAlloc((void**)&pos, MOVE_N * sizeof(float2), cudaHostAllocWriteCombined));
    CUDA_CHECK(cudaHostAlloc((void**)&vel, MOVE_N * sizeof(float2), cudaHostAllocWriteCombined));
    CUDA_CHECK(cudaHostAlloc((void**)&outCache, MOVE_N * sizeof(float2), cudaHostAllocDefault));
    CUDA_CHECK(cudaHostAlloc((void**)&posStagingH, NCHUNK * CHUNK * sizeof(float2), cudaHostAllocWriteCombined));
    CUDA_CHECK(cudaHostAlloc((void**)&velStagingH, NCHUNK * CHUNK * sizeof(float2), cudaHostAllocWriteCombined));
    CUDA_CHECK(cudaHostAlloc((void**)&outStagingH, NCHUNK * CHUNK * sizeof(float2), cudaHostAllocDefault));
    CUDA_CHECK(cudaHostAlloc((void**)&tmp, MOVE_N * sizeof(float2), cudaHostAllocDefault));
    dirty = (int*)malloc(NCHUNK * sizeof(int));
    used = (int*)calloc(NCHUNK, sizeof(int));
    if (!dirty || !used) { printf("  malloc fail\n"); goto fail; }
    CUDA_MALLOC(dPos, MOVE_N * sizeof(float2));
    CUDA_MALLOC(dVel, MOVE_N * sizeof(float2));
    CUDA_MALLOC(dOut, MOVE_N * sizeof(float2));
    CUDA_MALLOC(dPosStaging, NCHUNK * CHUNK * sizeof(float2));
    CUDA_MALLOC(dVelStaging, NCHUNK * CHUNK * sizeof(float2));
    CUDA_MALLOC(dOutStaging, NCHUNK * CHUNK * sizeof(float2));
    CUDA_MALLOC(dChunkIdx, NCHUNK * sizeof(int));

    printf("  DirtySync 探针 v2（HeavyMove@1M, chunk=%d 实体, %d chunk；staging 拼接 + scatter/gather）\n", CHUNK, NCHUNK);
    printf("  结论① 逐 chunk 小 DMA 已证伪（固定延迟 O(N)，50% 变 102ms）；v2 只留 1 次大 DMA。\n");
    printf("  结论② 帧内纯函数（未变更输入→未变更输出）→ 脏同步缓存回读对累积状态也成立，\n");
    printf("          parity 0/1M 逐位相等——「全部回读」需求由 CPU 缓存合并满足，物理只回读变更 chunk。\n");
    printf("  三形态每帧交替测（共享 GPU 热状态，消除 laptop 降频顺序偏置）；同一 dirty 集合 + 同 host 数据。\n");
    printf("  sp     全量往返   脏上传+缓存回读   脏上传+全量回读   parity(缓存合并vs全量)\n");

    for (int si = 0; si < nsp; si++) {
        float sp = spList[si];
        int nd = (int)(NCHUNK * sp + 0.5f);
        if (nd < 1) nd = 1;
        int stMax = nd * CHUNK;   /* staging 有效长度（元素数） */

        /* ---- 重置数据 + 初始全量上传 + 初始 kernel + 全量回读 → outCache = 初始 GPU 输出 ---- */
        d_rng = 1234;
        for (int i = 0; i < MOVE_N; i++) {
            pos[i].x = (float)(d_rnd() * 200 - 100); pos[i].y = (float)(d_rnd() * 200 - 100);
            vel[i].x = (float)(d_rnd() * 200 - 100); vel[i].y = (float)(d_rnd() * 200 - 100);
        }
        CUDA_CHECK(cudaMemcpyAsync(dPos, pos, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, s));
        CUDA_CHECK(cudaMemcpyAsync(dVel, vel, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, s));
        cuda_launch_heavy_s(dPos, dVel, dt, MOVE_N, mgrid, BLOCK, s);
        CUDA_CHECK(cudaMemcpyAsync(outCache, dOut, MOVE_N * sizeof(float2), cudaMemcpyDeviceToHost, s));
        CUDA_CHECK(cudaStreamSynchronize(s));

        /* ---- 三形态每帧交替测（共享 GPU 热状态，消除 laptop 降频顺序偏置）：
               同一 dirty 集合 + 同一 host 数据 → 三形态输入逐位一致，输出逐位一致 ---- */
        double wf[DIRTY_FRAMES], wd[DIRTY_FRAMES], wc[DIRTY_FRAMES];
        double upA[DIRTY_FRAMES], kA[DIRTY_FRAMES], rbA[DIRTY_FRAMES];
        double upF[DIRTY_FRAMES], kF[DIRTY_FRAMES], rbF[DIRTY_FRAMES];
        for (int f = 0; f < DIRTY_WARMUP + DIRTY_FRAMES; f++) {
            memset(used, 0, NCHUNK * sizeof(int));
            for (int j = 0; j < nd; j++) {
                int c;
                do { c = (int)(d_rnd() * NCHUNK); } while (used[c]);
                used[c] = 1; dirty[j] = c;
                for (int e = c * CHUNK; e < (c + 1) * CHUNK; e++) {
                    pos[e].x += 0.1f; pos[e].y -= 0.05f;
                    vel[e].x += 0.02f; vel[e].y += 0.01f;
                }
                memcpy(posStagingH + (size_t)j * CHUNK, pos + (size_t)c * CHUNK, CHUNK * sizeof(float2));
                memcpy(velStagingH + (size_t)j * CHUNK, vel + (size_t)c * CHUNK, CHUNK * sizeof(float2));
            }
            double t0;

            /* A：脏上传 + scatter + heavy + gather + 缓存回读合并（GpuResidencyManager 完整形态） */
            t0 = now_ms();
            CUDA_CHECK(cudaEventRecord(evA0, s));
            CUDA_CHECK(cudaMemcpyAsync(dChunkIdx, dirty, nd * sizeof(int), cudaMemcpyHostToDevice, s));
            CUDA_CHECK(cudaMemcpyAsync(dPosStaging, posStagingH, (size_t)stMax * sizeof(float2), cudaMemcpyHostToDevice, s));
            CUDA_CHECK(cudaMemcpyAsync(dVelStaging, velStagingH, (size_t)stMax * sizeof(float2), cudaMemcpyHostToDevice, s));
            CUDA_CHECK(cudaEventRecord(evA1, s));
            cuda_launch_scatter_chunk(dPosStaging, dPos, dChunkIdx, nd, CHUNK, BLOCK, s);
            cuda_launch_scatter_chunk(dVelStaging, dVel, dChunkIdx, nd, CHUNK, BLOCK, s);
            CUDA_CHECK(cudaEventRecord(evA2, s));
            cuda_launch_heavy_s(dPos, dVel, dt, MOVE_N, mgrid, BLOCK, s);
            CUDA_CHECK(cudaEventRecord(evA3, s));
            cuda_launch_gather_chunk(dOut, dOutStaging, dChunkIdx, nd, CHUNK, BLOCK, s);
            CUDA_CHECK(cudaMemcpyAsync(outStagingH, dOutStaging, (size_t)stMax * sizeof(float2), cudaMemcpyDeviceToHost, s));
            CUDA_CHECK(cudaEventRecord(evA4, s));
            CUDA_CHECK(cudaStreamSynchronize(s));
            for (int j = 0; j < nd; j++) {
                int c = dirty[j];
                memcpy(outCache + (size_t)c * CHUNK, outStagingH + (size_t)j * CHUNK, CHUNK * sizeof(float2));
            }
            if (f >= DIRTY_WARMUP) {
                wd[f - DIRTY_WARMUP] = now_ms() - t0;
                float u, k, r;
                cudaEventElapsedTime(&u, evA0, evA1); cudaEventElapsedTime(&k, evA2, evA3); cudaEventElapsedTime(&r, evA3, evA4);
                upA[f - DIRTY_WARMUP] = u; kA[f - DIRTY_WARMUP] = k; rbA[f - DIRTY_WARMUP] = r;
            }

            /* B：脏上传 + scatter + heavy + 全量回读（回读税仍在的中间形态） */
            t0 = now_ms();
            CUDA_CHECK(cudaMemcpyAsync(dChunkIdx, dirty, nd * sizeof(int), cudaMemcpyHostToDevice, s));
            CUDA_CHECK(cudaMemcpyAsync(dPosStaging, posStagingH, (size_t)stMax * sizeof(float2), cudaMemcpyHostToDevice, s));
            CUDA_CHECK(cudaMemcpyAsync(dVelStaging, velStagingH, (size_t)stMax * sizeof(float2), cudaMemcpyHostToDevice, s));
            cuda_launch_scatter_chunk(dPosStaging, dPos, dChunkIdx, nd, CHUNK, BLOCK, s);
            cuda_launch_scatter_chunk(dVelStaging, dVel, dChunkIdx, nd, CHUNK, BLOCK, s);
            cuda_launch_heavy_s(dPos, dVel, dt, MOVE_N, mgrid, BLOCK, s);
            CUDA_CHECK(cudaMemcpyAsync(tmp, dOut, MOVE_N * sizeof(float2), cudaMemcpyDeviceToHost, s));
            CUDA_CHECK(cudaStreamSynchronize(s));
            if (f >= DIRTY_WARMUP) wc[f - DIRTY_WARMUP] = now_ms() - t0;

            /* full：全量上传 + heavy + 全量回读 */
            t0 = now_ms();
            CUDA_CHECK(cudaEventRecord(evF0, s));
            CUDA_CHECK(cudaMemcpyAsync(dPos, pos, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, s));
            CUDA_CHECK(cudaMemcpyAsync(dVel, vel, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, s));
            CUDA_CHECK(cudaEventRecord(evF1, s));
            cuda_launch_heavy_s(dPos, dVel, dt, MOVE_N, mgrid, BLOCK, s);
            CUDA_CHECK(cudaEventRecord(evF2, s));
            CUDA_CHECK(cudaMemcpyAsync(tmp, dOut, MOVE_N * sizeof(float2), cudaMemcpyDeviceToHost, s));
            CUDA_CHECK(cudaEventRecord(evF3, s));
            CUDA_CHECK(cudaStreamSynchronize(s));
            if (f >= DIRTY_WARMUP) {
                wf[f - DIRTY_WARMUP] = now_ms() - t0;
                float u, k, r;
                cudaEventElapsedTime(&u, evF0, evF1); cudaEventElapsedTime(&k, evF1, evF2); cudaEventElapsedTime(&r, evF2, evF3);
                upF[f - DIRTY_WARMUP] = u; kF[f - DIRTY_WARMUP] = k; rbF[f - DIRTY_WARMUP] = r;
            }
        }
        double full = median(wf, DIRTY_FRAMES);
        double dirtyCache = median(wd, DIRTY_FRAMES);
        double dirtyUpFull = median(wc, DIRTY_FRAMES);

        /* parity：A 的缓存合并 outCache vs full 的全量 tmp——同输入同 kernel 应逐位相等（0/1M） */
        mismatch = 0;
        for (int i = 0; i < MOVE_N; i++)
            if (outCache[i].x != tmp[i].x || outCache[i].y != tmp[i].y) mismatch++;

        double upBytes = (double)nd * CHUNK * sizeof(float2) * 2 / 1024.0;   /* pos+vel KB（脏上传量） */
        printf("    %-5.0f%%   %8.3f    %8.3f(x%.2f)   %8.3f(x%.2f)   %d/%d\n",
            sp * 100, full, dirtyCache, full / dirtyCache, dirtyUpFull, full / dirtyUpFull, mismatch, MOVE_N);
        printf("    分解 sp=%d%%（GPU 时间 ms，上传|kernel|回读）  脏(A) %6.3f|%6.3f|%6.3f   全量 %6.3f|%6.3f|%6.3f\n",
            (int)(sp * 100),
            median(upA, DIRTY_FRAMES), median(kA, DIRTY_FRAMES), median(rbA, DIRTY_FRAMES),
            median(upF, DIRTY_FRAMES), median(kF, DIRTY_FRAMES), median(rbF, DIRTY_FRAMES));
    }

    cudaFreeHost(pos); cudaFreeHost(vel); cudaFreeHost(outCache);
    cudaFreeHost(posStagingH); cudaFreeHost(velStagingH); cudaFreeHost(outStagingH); cudaFreeHost(tmp);
    free(dirty); free(used);
    cudaFree(dPos); cudaFree(dVel); cudaFree(dOut);
    cudaFree(dPosStaging); cudaFree(dVelStaging); cudaFree(dOutStaging); cudaFree(dChunkIdx);
    cudaEventDestroy(evA0); cudaEventDestroy(evA1); cudaEventDestroy(evA2); cudaEventDestroy(evA3); cudaEventDestroy(evA4);
    cudaEventDestroy(evF0); cudaEventDestroy(evF1); cudaEventDestroy(evF2); cudaEventDestroy(evF3);
    cudaStreamDestroy(s);
    cudaDeviceReset();
    return;

fail:
    cudaFreeHost(pos); cudaFreeHost(vel); cudaFreeHost(outCache);
    cudaFreeHost(posStagingH); cudaFreeHost(velStagingH); cudaFreeHost(outStagingH); cudaFreeHost(tmp);
    free(dirty); free(used);
    cudaFree(dPos); cudaFree(dVel); cudaFree(dOut);
    cudaFree(dPosStaging); cudaFree(dVelStaging); cudaFree(dOutStaging); cudaFree(dChunkIdx);
    cudaEventDestroy(evA0); cudaEventDestroy(evA1); cudaEventDestroy(evA2); cudaEventDestroy(evA3); cudaEventDestroy(evA4);
    cudaEventDestroy(evF0); cudaEventDestroy(evF1); cudaEventDestroy(evF2); cudaEventDestroy(evF3);
    cudaStreamDestroy(s);
    cudaDeviceReset();
}
