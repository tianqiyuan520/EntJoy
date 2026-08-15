// diff_probe.cpp — 影子 diff 成本探针：GpuResidencyManager 每帧固定开销（AVX2 vs memcmp）+ 净收益曲线
// 负载：HeavyMove@1M（镜像 transpiler 数学），chunk=128 实体（1KB/chunk/分量）。
// 五形态（每档测）：
//   full      全量上传 + heavy + 全量回读（每帧同步基线）
//   resSync   完整每帧同步闭环 = 影子 diff → staging 拼接 / 退化全量 → scatter → heavy → gather →
//             增量回读 patch → shadow 更新（diff 税显式暴露）
//   resCross  跨帧流水（真实 GpuResidencyManager 形态）：CPU diff/staging 下一帧与 GPU 执行当前帧重叠，
//             diff 税藏进 GPU 执行（wall ≈ max(CPU diff+staging, GPU exec)）
//   fullMode  job 级全量模式：跳过 diff，直接全量 memcpy + heavy + 全量回读（diff 税归零）
//   resident  常驻 kernel 上限（无传输）
// 验证门：
//   · sp=100% 时 fullMode == full（diff 税归零 → 满足「全改时性能=全量」）
//   · sp=1% 时 resSync < full（每帧同步净收益）；resCross << full（跨帧藏 diff 税）
//   · parity 0/1M（增量 patch 全量镜像 == 全量回读）
// 用法：GpuNativeProbe.exe diff

#define ENTJOY_NO_FLOAT2
#include <cuda_runtime.h>
#include <immintrin.h>
#include <string.h>
#include "common.h"

extern "C" void cuda_launch_heavy_s(float2* dPos, const float2* dVel, float dt, int n, int grid, int block, cudaStream_t s);
extern "C" void cuda_launch_scatter_chunk(const float2* src, float2* dst, const int* chunkIdx, int nchunks, int chunkSize, int block, cudaStream_t s);
extern "C" void cuda_launch_gather_chunk(const float2* src, float2* dst, const int* chunkIdx, int nchunks, int chunkSize, int block, cudaStream_t s);

#define BLOCK 256
#define CHUNK 128
#define NCHUNK (MOVE_N / CHUNK)
#define CHUNK_BYTES (CHUNK * sizeof(float2))   /* 2KB/chunk/分量 */
#define CHUNK_LANES (CHUNK_BYTES / 32)         /* 32 个 256-bit 向量 */
#define DIFF_WARMUP 3
#define DIFF_FRAMES 20
#define SYNC_DEGRADE 0.30f                     /* dirty 率 ≥ 30% → 传输退化为全量 memcpy（crossover 由探针 B 精确定位） */

#define CUDA_CHECK(expr) do { cudaError_t _e = (expr); if (_e != cudaSuccess) { \
    printf("  CUDA error at %s:%d: %s\n", __FILE__, __LINE__, cudaGetErrorString(_e)); goto fail; } } while (0)
#define CUDA_MALLOC(ptr, size) CUDA_CHECK(cudaMalloc((void**)&(ptr), (size)))

/* 本地 LCG（不碰 common.h 的 g_rng，避免干扰其他探针的确定性） */
static unsigned long long d_rng;
static double d_rnd(void) {
    d_rng = d_rng * 6364136223846793005ULL + 1442695040888963407ULL;
    return (double)((d_rng >> 33) & 0x7FFFFFFF) / 2147483647.0;
}

/* ================= AVX2 chunk diff（pos+vel 合并比较，chunk 内提前退出） ================= */
static int chunk_dirty_avx2(const float2* pos, const float2* vel, const float2* spos, const float2* svel) {
    const __m256i* p = (const __m256i*)pos;
    const __m256i* v = (const __m256i*)vel;
    const __m256i* ps = (const __m256i*)spos;
    const __m256i* vs = (const __m256i*)svel;
    for (int i = 0; i < CHUNK_LANES; i++) {
        __m256i a = _mm256_cmpeq_epi32(_mm256_loadu_si256(p + i), _mm256_loadu_si256(ps + i));
        __m256i b = _mm256_cmpeq_epi32(_mm256_loadu_si256(v + i), _mm256_loadu_si256(vs + i));
        if (_mm256_movemask_epi8(a) != -1 || _mm256_movemask_epi8(b) != -1) return 1;
    }
    return 0;
}
static int chunk_dirty_memcmp(const float2* pos, const float2* vel, const float2* spos, const float2* svel) {
    return memcmp(pos, spos, CHUNK_BYTES) != 0 || memcmp(vel, svel, CHUNK_BYTES) != 0;
}

/* 全扫 diff：返回 dirty 数；dirtyIdx != NULL 时填充（升序，供 staging 拼接 / shadow 更新 / 退化判断） */
static int diff_all(int useAvx2, const float2* pos, const float2* vel,
                    const float2* spos, const float2* svel, int* dirtyIdx) {
    int nd = 0;
    for (int c = 0; c < NCHUNK; c++) {
        int d = useAvx2 ? chunk_dirty_avx2(pos + (size_t)c * CHUNK, vel + (size_t)c * CHUNK,
                                           spos + (size_t)c * CHUNK, svel + (size_t)c * CHUNK)
                        : chunk_dirty_memcmp(pos + (size_t)c * CHUNK, vel + (size_t)c * CHUNK,
                                             spos + (size_t)c * CHUNK, svel + (size_t)c * CHUNK);
        if (d) { if (dirtyIdx) dirtyIdx[nd] = c; nd++; }
    }
    return nd;
}

/* 每帧 gameplay 修改 sp% 随机 chunk 的 host pos/vel（不改 shadow）——真实中发生在同步闭环外 */
static void gen_changes(int nd, int* used, float2* pos, float2* vel) {
    memset(used, 0, NCHUNK * sizeof(int));
    if (nd >= NCHUNK) {
        /* 全改：顺序选满（nd=NCHUNK 时 do-while 随机选不重复索引 → 末尾碰撞重试爆炸近似死循环） */
        for (int c = 0; c < NCHUNK; c++) {
            used[c] = 1;
            for (int e = c * CHUNK; e < (c + 1) * CHUNK; e++) {
                pos[e].x += 0.1f; pos[e].y -= 0.05f;
                vel[e].x += 0.02f; vel[e].y += 0.01f;
            }
        }
        return;
    }
    for (int j = 0; j < nd; j++) {
        int c;
        do { c = (int)(d_rnd() * NCHUNK); } while (used[c]);
        used[c] = 1;
        for (int e = c * CHUNK; e < (c + 1) * CHUNK; e++) {
            pos[e].x += 0.1f; pos[e].y -= 0.05f;
            vel[e].x += 0.02f; vel[e].y += 0.01f;
        }
    }
}

/* 按 dirty 集合拼接 staging（真实：diff 输出 dirty → 从 host 读 dirty chunk 拼进连续 staging） */
static void build_staging(const int* dirty, int nd, const float2* pos, const float2* vel,
                          float2* posStg, float2* velStg) {
    for (int j = 0; j < nd; j++) {
        int c = dirty[j];
        memcpy(posStg + (size_t)j * CHUNK, pos + (size_t)c * CHUNK, CHUNK * sizeof(float2));
        memcpy(velStg + (size_t)j * CHUNK, vel + (size_t)c * CHUNK, CHUNK * sizeof(float2));
    }
}

/* shadow 增量更新：只拷 dirty chunk 的 host 值（保持 shadow = 上次上传的 GPU 输入） */
static void shadow_update(float2* spos, float2* svel, const float2* pos, const float2* vel,
                          const int* dirty, int nd) {
    for (int j = 0; j < nd; j++) {
        int c = dirty[j];
        memcpy(spos + (size_t)c * CHUNK, pos + (size_t)c * CHUNK, CHUNK * sizeof(float2));
        memcpy(svel + (size_t)c * CHUNK, vel + (size_t)c * CHUNK, CHUNK * sizeof(float2));
    }
}

/* 增量回读 patch：dirty chunk 新值铺回 CPU 全量镜像 outCache */
static void patch_outcache(float2* outCache, const float2* outStg, const int* dirty, int nd) {
    for (int j = 0; j < nd; j++) {
        int c = dirty[j];
        memcpy(outCache + (size_t)c * CHUNK, outStg + (size_t)j * CHUNK, CHUNK * sizeof(float2));
    }
}

/* ================= 通用块粒度 dirty（chunkSize 参数化，对照 2KB vs 16KB 分块） ================= */
static int chunk_dirty_gen(const float2* pos, const float2* vel, const float2* spos, const float2* svel, int chunkSize) {
    const __m256i* p = (const __m256i*)pos;
    const __m256i* v = (const __m256i*)vel;
    const __m256i* ps = (const __m256i*)spos;
    const __m256i* vs = (const __m256i*)svel;
    int lanes = (chunkSize * (int)sizeof(float2)) / 32;
    for (int i = 0; i < lanes; i++) {
        __m256i a = _mm256_cmpeq_epi32(_mm256_loadu_si256(p + i), _mm256_loadu_si256(ps + i));
        __m256i b = _mm256_cmpeq_epi32(_mm256_loadu_si256(v + i), _mm256_loadu_si256(vs + i));
        if (_mm256_movemask_epi8(a) != -1 || _mm256_movemask_epi8(b) != -1) return 1;
    }
    return 0;
}
static int diff_all_gen(int chunkSize, const float2* pos, const float2* vel,
                        const float2* spos, const float2* svel, int* dirtyIdx) {
    int nchunk = MOVE_N / chunkSize;
    int nd = 0;
    for (int c = 0; c < nchunk; c++) {
        if (chunk_dirty_gen(pos + (size_t)c * chunkSize, vel + (size_t)c * chunkSize,
                            spos + (size_t)c * chunkSize, svel + (size_t)c * chunkSize, chunkSize)) {
            if (dirtyIdx) dirtyIdx[nd] = c;
            nd++;
        }
    }
    return nd;
}

/* ================= hash 索引 diff（SSE4.2 CRC32C 硬件指令，每 chunk 8B 双链 hash） =================
   原理：每帧只读 host 16MB 算每 chunk 的 hash → 对比上帧 hash 表（NCHUNK×8B≈62KB，L2 热，近免费）
         → hash 变 = dirty。不读 shadow 16MB → 读取从 32MB 降到 16MB → 预期 ~0.6ms（全扫的一半）。
   碰撞：CRC32C 硬件指令双链（pos/vel 各 4B 拼 8B），碰撞概率 ~2^-64；hash 表在 diff 时同步更新
         （hashTab = host 最新 hash），与 AVX2 全扫的 shadow 语义一致。
   特性：hash 计算必须读完整 chunk（无法提前退出）→ 成本与 dirty 率无关（恒 16MB）；AVX2 全扫 100%
         dirty 时 chunk 内提前退出只读 ~250KB → dense 时反而更快（但那已走全量模式跳过 diff）。 */
struct chunk_hash { unsigned hPos, hVel; };

/* 4 链并行 CRC32C：CRC 是串行依赖链（延迟 3cyc），单链 128 次 = 384cyc/chunk 延迟税吃掉带宽收益。
   4 条独立链交错（各 32 次，延迟 96cyc）+ 尾部合并 → 延迟降 4 倍，带宽才成瓶颈。 */
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
static int diff_hash(const float2* pos, const float2* vel, chunk_hash* hashTab, int* dirtyIdx) {
    int nd = 0;
    for (int c = 0; c < NCHUNK; c++) {
        chunk_hash h = hash_chunk_crc(pos + (size_t)c * CHUNK, vel + (size_t)c * CHUNK);
        if (h.hPos != hashTab[c].hPos || h.hVel != hashTab[c].hVel) {
            hashTab[c] = h;                                /* 同步更新：hashTab = host 最新 hash */
            if (dirtyIdx) dirtyIdx[nd] = c;
            nd++;
        }
    }
    return nd;
}
static int median_int(int* a, int n) {   /* 中位（hash 段 dirty 数列用，n=DIFF_FRAMES 偶数取中下） */
    for (int i = 0; i < n - 1; i++)
        for (int j = i + 1; j < n; j++)
            if (a[j] < a[i]) { int t = a[i]; a[i] = a[j]; a[j] = t; }
    return a[n / 2];
}

void RunDiffProbe(void) {
    float2 *pos = NULL, *vel = NULL, *outCache = NULL, *tmp = NULL, *tmpFull = NULL;
    float2 *spos = NULL, *svel = NULL;                          /* shadow（malloc） */
    float2 *posStg[2] = { NULL, NULL }, *velStg[2] = { NULL, NULL }, *outStg = NULL;
    float2 *dPos = NULL, *dVel = NULL, *dOut = NULL;
    float2 *dPosStg[2] = { NULL, NULL }, *dVelStg[2] = { NULL, NULL }, *dOutStg = NULL;
    int *dirty[2] = { NULL, NULL }, *used = NULL, *dChunkIdx = NULL;
    chunk_hash* hashTab = NULL;                                 /* hash 索引 diff 的压缩 shadow（62KB vs 16MB） */
    cudaStream_t s = NULL;
    int mgrid = (MOVE_N + BLOCK - 1) / BLOCK;
    float dt = DT;
    const float spList[7] = { 0.01f, 0.05f, 0.10f, 0.25f, 0.50f, 0.75f, 1.00f };
    int nsp = 7;
    int mismatch = 0;

    CUDA_CHECK(cudaSetDevice(0));
    CUDA_CHECK(cudaStreamCreate(&s));
    /* pos/vel 是 gameplay buffer（CPU 读-改-写 + diff 读）——必须可缓存（cudaHostAllocDefault）。
       WriteCombined 不可缓存读，CPU 读它 ~75ns/32B → diff 全扫 16MB 变 50ms、gen 读改写逐实体崩溃。
       真实 GpuResidencyManager：host buffer = C# NativeArray 普通可缓存内存；WC 只用于 staging 拼接写。 */
    CUDA_CHECK(cudaHostAlloc((void**)&pos, MOVE_N * sizeof(float2), cudaHostAllocDefault));
    CUDA_CHECK(cudaHostAlloc((void**)&vel, MOVE_N * sizeof(float2), cudaHostAllocDefault));
    CUDA_CHECK(cudaHostAlloc((void**)&outCache, MOVE_N * sizeof(float2), cudaHostAllocDefault));
    CUDA_CHECK(cudaHostAlloc((void**)&tmp, MOVE_N * sizeof(float2), cudaHostAllocDefault));
    CUDA_CHECK(cudaHostAlloc((void**)&tmpFull, MOVE_N * sizeof(float2), cudaHostAllocDefault));
    CUDA_CHECK(cudaHostAlloc((void**)&posStg[0], NCHUNK * CHUNK * sizeof(float2), cudaHostAllocWriteCombined));
    CUDA_CHECK(cudaHostAlloc((void**)&velStg[0], NCHUNK * CHUNK * sizeof(float2), cudaHostAllocWriteCombined));
    CUDA_CHECK(cudaHostAlloc((void**)&posStg[1], NCHUNK * CHUNK * sizeof(float2), cudaHostAllocWriteCombined));
    CUDA_CHECK(cudaHostAlloc((void**)&velStg[1], NCHUNK * CHUNK * sizeof(float2), cudaHostAllocWriteCombined));
    CUDA_CHECK(cudaHostAlloc((void**)&outStg, NCHUNK * CHUNK * sizeof(float2), cudaHostAllocDefault));
    spos = (float2*)malloc(MOVE_N * sizeof(float2));
    svel = (float2*)malloc(MOVE_N * sizeof(float2));
    dirty[0] = (int*)malloc(NCHUNK * sizeof(int));
    dirty[1] = (int*)malloc(NCHUNK * sizeof(int));
    used = (int*)calloc(NCHUNK, sizeof(int));
    hashTab = (chunk_hash*)malloc(NCHUNK * sizeof(chunk_hash));
    if (!spos || !svel || !dirty[0] || !dirty[1] || !used || !hashTab) { printf("  malloc fail\n"); goto fail; }
    CUDA_MALLOC(dPos, MOVE_N * sizeof(float2));
    CUDA_MALLOC(dVel, MOVE_N * sizeof(float2));
    CUDA_MALLOC(dOut, MOVE_N * sizeof(float2));
    CUDA_MALLOC(dPosStg[0], NCHUNK * CHUNK * sizeof(float2));
    CUDA_MALLOC(dVelStg[0], NCHUNK * CHUNK * sizeof(float2));
    CUDA_MALLOC(dPosStg[1], NCHUNK * CHUNK * sizeof(float2));
    CUDA_MALLOC(dVelStg[1], NCHUNK * CHUNK * sizeof(float2));
    CUDA_MALLOC(dOutStg, NCHUNK * CHUNK * sizeof(float2));
    CUDA_MALLOC(dChunkIdx, NCHUNK * sizeof(int));

    printf("  DiffSync 探针（HeavyMove@1M, chunk=%d 实体=1KB, %d chunk）——影子 diff 每帧成本 + 净收益曲线\n", CHUNK, NCHUNK);
    printf("  影子 diff = GpuResidencyManager 固定每帧开销：全扫 host+shadow 共 2×输入量（内存带宽受限）\n");
    printf("  形态：full(全量基线) | resSync(每帧同步,diff税显式) | resCross(跨帧流水,diff藏进GPU) | resHash(hash diff闭环) | resHashMode(hash+流水+模式切换) | fullMode(跳过diff) | resident(常驻)\n");
    printf("  sp       diffAVX2   diffMemcmp    resSync    resCross    resHash  resHashMode  fullMode       full   resident  full/resHashMode  parity\n");

    for (int si = 0; si < nsp; si++) {
        float sp = spList[si];
        int nd = (int)(NCHUNK * sp + 0.5f);
        if (nd < 1) nd = 1;
        int stMax = nd * CHUNK;

        /* ---- 重置数据 + shadow + 初始全量上传 + 初始 kernel + 全量回读 → outCache = 初始 GPU 输出 ---- */
        d_rng = 1234;
        for (int i = 0; i < MOVE_N; i++) {
            pos[i].x = (float)(d_rnd() * 200 - 100); pos[i].y = (float)(d_rnd() * 200 - 100);
            vel[i].x = (float)(d_rnd() * 200 - 100); vel[i].y = (float)(d_rnd() * 200 - 100);
        }
        memcpy(spos, pos, MOVE_N * sizeof(float2));
        memcpy(svel, vel, MOVE_N * sizeof(float2));
        CUDA_CHECK(cudaMemcpyAsync(dPos, pos, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, s));
        CUDA_CHECK(cudaMemcpyAsync(dVel, vel, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, s));
        cuda_launch_heavy_s(dPos, dVel, dt, MOVE_N, mgrid, BLOCK, s);
        CUDA_CHECK(cudaMemcpyAsync(outCache, dOut, MOVE_N * sizeof(float2), cudaMemcpyDeviceToHost, s));
        CUDA_CHECK(cudaStreamSynchronize(s));

        /* ---- 4 形态每帧交替测（共享 GPU 热状态 + 同一 dirty 集合 + 同一 host 数据 → 输出逐位一致） ---- */
        double wf[DIFF_FRAMES], wsync[DIFF_FRAMES], wfm[DIFF_FRAMES], wr[DIFF_FRAMES];
        double dAvx[DIFF_FRAMES], dMem[DIFF_FRAMES];
        for (int f = 0; f < DIFF_WARMUP + DIFF_FRAMES; f++) {
            /* gameplay 修改 host（同步闭环外，不计入形态墙钟） */
            gen_changes(nd, used, pos, vel);

            /* resident：数据常驻，只 kernel（上限） */
            double t0 = now_ms();
            cuda_launch_heavy_s(dPos, dVel, dt, MOVE_N, mgrid, BLOCK, s);
            CUDA_CHECK(cudaStreamSynchronize(s));
            if (f >= DIFF_WARMUP) wr[f - DIFF_WARMUP] = now_ms() - t0;

            /* full：全量上传 + heavy + 全量回读 */
            t0 = now_ms();
            CUDA_CHECK(cudaMemcpyAsync(dPos, pos, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, s));
            CUDA_CHECK(cudaMemcpyAsync(dVel, vel, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, s));
            cuda_launch_heavy_s(dPos, dVel, dt, MOVE_N, mgrid, BLOCK, s);
            CUDA_CHECK(cudaMemcpyAsync(tmp, dOut, MOVE_N * sizeof(float2), cudaMemcpyDeviceToHost, s));
            CUDA_CHECK(cudaStreamSynchronize(s));
            if (f >= DIFF_WARMUP) wf[f - DIFF_WARMUP] = now_ms() - t0;

            /* fullMode：跳过 diff，直接全量 memcpy（diff 税归零） */
            t0 = now_ms();
            CUDA_CHECK(cudaMemcpyAsync(dPos, pos, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, s));
            CUDA_CHECK(cudaMemcpyAsync(dVel, vel, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, s));
            cuda_launch_heavy_s(dPos, dVel, dt, MOVE_N, mgrid, BLOCK, s);
            CUDA_CHECK(cudaMemcpyAsync(tmp, dOut, MOVE_N * sizeof(float2), cudaMemcpyDeviceToHost, s));
            CUDA_CHECK(cudaStreamSynchronize(s));
            if (f >= DIFF_WARMUP) wfm[f - DIFF_WARMUP] = now_ms() - t0;

            /* resSync：完整每帧同步闭环（diff 税显式）= diff → 传输(按 dirty 率退化) → GPU → patch → shadow 更新 */
            t0 = now_ms();
            int ndA = diff_all(1, pos, vel, spos, svel, dirty[0]);   /* diff 生成 dirty 集合（升序） */
            double tAvx = now_ms() - t0;
            t0 = now_ms();
            volatile int ndM = diff_all(0, pos, vel, spos, svel, NULL);  /* memcmp 对照（volatile 防消除） */
            double tMem = now_ms() - t0;
            if (ndA != ndM) printf("    diff 不一致 AVX2=%d memcmp=%d\n", ndA, ndM);
            if (ndA >= (int)(NCHUNK * SYNC_DEGRADE)) {
                /* 传输退化：全量 memcpy（diff 税仍在——这正是 fullMode 存在的理由） */
                CUDA_CHECK(cudaMemcpyAsync(dPos, pos, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, s));
                CUDA_CHECK(cudaMemcpyAsync(dVel, vel, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, s));
                cuda_launch_heavy_s(dPos, dVel, dt, MOVE_N, mgrid, BLOCK, s);
                CUDA_CHECK(cudaMemcpyAsync(tmp, dOut, MOVE_N * sizeof(float2), cudaMemcpyDeviceToHost, s));
                CUDA_CHECK(cudaStreamSynchronize(s));
                memcpy(outCache, tmp, MOVE_N * sizeof(float2));
            } else {
                build_staging(dirty[0], ndA, pos, vel, posStg[0], velStg[0]);
                CUDA_CHECK(cudaMemcpyAsync(dChunkIdx, dirty[0], ndA * sizeof(int), cudaMemcpyHostToDevice, s));
                CUDA_CHECK(cudaMemcpyAsync(dPosStg[0], posStg[0], (size_t)ndA * CHUNK * sizeof(float2), cudaMemcpyHostToDevice, s));
                CUDA_CHECK(cudaMemcpyAsync(dVelStg[0], velStg[0], (size_t)ndA * CHUNK * sizeof(float2), cudaMemcpyHostToDevice, s));
                cuda_launch_scatter_chunk(dPosStg[0], dPos, dChunkIdx, ndA, CHUNK, BLOCK, s);
                cuda_launch_scatter_chunk(dVelStg[0], dVel, dChunkIdx, ndA, CHUNK, BLOCK, s);
                cuda_launch_heavy_s(dPos, dVel, dt, MOVE_N, mgrid, BLOCK, s);
                cuda_launch_gather_chunk(dOut, dOutStg, dChunkIdx, ndA, CHUNK, BLOCK, s);
                CUDA_CHECK(cudaMemcpyAsync(outStg, dOutStg, (size_t)ndA * CHUNK * sizeof(float2), cudaMemcpyDeviceToHost, s));
                CUDA_CHECK(cudaStreamSynchronize(s));
                patch_outcache(outCache, outStg, dirty[0], ndA);
            }
            shadow_update(spos, svel, pos, vel, dirty[0], ndA);
            if (f >= DIFF_WARMUP) {
                dAvx[f - DIFF_WARMUP] = tAvx;
                dMem[f - DIFF_WARMUP] = tMem;
                wsync[f - DIFF_WARMUP] = now_ms() - t0;
            }
        }
        double full = median(wf, DIFF_FRAMES);
        double resSync = median(wsync, DIFF_FRAMES);
        double fullMode = median(wfm, DIFF_FRAMES);
        double resident = median(wr, DIFF_FRAMES);

        /* 交替循环 parity：resSync 增量 patch outCache vs full 全量 tmp（同输入同 kernel → 逐位相等） */
        mismatch = 0;
        for (int i = 0; i < MOVE_N; i++)
            if (outCache[i].x != tmp[i].x || outCache[i].y != tmp[i].y) mismatch++;

        /* ================= resCross：跨帧流水（独立循环，双 staging buffer，diff 藏进 GPU 执行） ================= */
        /* 前置：dPos/dVel = 当前 host 全量 + outCache = 初始 GPU 输出（从干净状态起步，parity 才对） */
        CUDA_CHECK(cudaMemcpyAsync(dPos, pos, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, s));
        CUDA_CHECK(cudaMemcpyAsync(dVel, vel, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, s));
        cuda_launch_heavy_s(dPos, dVel, dt, MOVE_N, mgrid, BLOCK, s);
        CUDA_CHECK(cudaMemcpyAsync(outCache, dOut, MOVE_N * sizeof(float2), cudaMemcpyDeviceToHost, s));
        CUDA_CHECK(cudaStreamSynchronize(s));
        /* 预生成 frame 0（gameplay 改 host + diff + 拼 staging + shadow 更新） */
        gen_changes(nd, used, pos, vel);
        int ndc[2];
        ndc[0] = diff_all(1, pos, vel, spos, svel, dirty[0]);
        build_staging(dirty[0], ndc[0], pos, vel, posStg[0], velStg[0]);
        shadow_update(spos, svel, pos, vel, dirty[0], ndc[0]);
        int cur = 0;
        double wc[DIFF_FRAMES];
        int wcIdx = 0;
        for (int f = 0; f < DIFF_WARMUP + DIFF_FRAMES; f++) {
            double tf = now_ms();
            /* 提交 frame f（GPU 异步执行） */
            CUDA_CHECK(cudaMemcpyAsync(dChunkIdx, dirty[cur], ndc[cur] * sizeof(int), cudaMemcpyHostToDevice, s));
            CUDA_CHECK(cudaMemcpyAsync(dPosStg[cur], posStg[cur], (size_t)ndc[cur] * CHUNK * sizeof(float2), cudaMemcpyHostToDevice, s));
            CUDA_CHECK(cudaMemcpyAsync(dVelStg[cur], velStg[cur], (size_t)ndc[cur] * CHUNK * sizeof(float2), cudaMemcpyHostToDevice, s));
            cuda_launch_scatter_chunk(dPosStg[cur], dPos, dChunkIdx, ndc[cur], CHUNK, BLOCK, s);
            cuda_launch_scatter_chunk(dVelStg[cur], dVel, dChunkIdx, ndc[cur], CHUNK, BLOCK, s);
            cuda_launch_heavy_s(dPos, dVel, dt, MOVE_N, mgrid, BLOCK, s);
            cuda_launch_gather_chunk(dOut, dOutStg, dChunkIdx, ndc[cur], CHUNK, BLOCK, s);
            CUDA_CHECK(cudaMemcpyAsync(outStg, dOutStg, (size_t)ndc[cur] * CHUNK * sizeof(float2), cudaMemcpyDeviceToHost, s));
            /* CPU 侧为下一帧做 gameplay+diff+staging+shadow 更新 → 与 GPU 执行 frame f 重叠（diff 税藏进 GPU） */
            if (f + 1 < DIFF_WARMUP + DIFF_FRAMES) {
                int nxt = cur ^ 1;
                gen_changes(nd, used, pos, vel);
                ndc[nxt] = diff_all(1, pos, vel, spos, svel, dirty[nxt]);
                build_staging(dirty[nxt], ndc[nxt], pos, vel, posStg[nxt], velStg[nxt]);
                shadow_update(spos, svel, pos, vel, dirty[nxt], ndc[nxt]);
            }
            /* 等 frame f 完成 */
            CUDA_CHECK(cudaStreamSynchronize(s));
            patch_outcache(outCache, outStg, dirty[cur], ndc[cur]);
            if (f >= DIFF_WARMUP) wc[wcIdx++] = now_ms() - tf;
            cur ^= 1;
        }
        double resCross = median(wc, DIFF_FRAMES);

        /* ================ resHash：最优解形态（hash 索引 diff 集成完整上传+回读闭环，每帧同步） ================
           hash diff 0.5ms（省 AVX2 一半）+ staging 拼接 + scatter + heavy + gather + 增量回读 patch。
           无 16MB shadow：hashTab 62KB 即 shadow 压缩形态，diff 时内部更新 → 免 shadow_update。 */
        double resHash = 0, resHashM = 0;
        {
            for (int c = 0; c < NCHUNK; c++) hashTab[c] = hash_chunk_crc(pos + (size_t)c * CHUNK, vel + (size_t)c * CHUNK);
            double wh[DIFF_FRAMES];
            for (int f = 0; f < DIFF_WARMUP + DIFF_FRAMES; f++) {
                gen_changes(nd, used, pos, vel);
                double t0 = now_ms();
                int ndH = diff_hash(pos, vel, hashTab, dirty[0]);
                if (ndH >= (int)(NCHUNK * SYNC_DEGRADE)) {
                    CUDA_CHECK(cudaMemcpyAsync(dPos, pos, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, s));
                    CUDA_CHECK(cudaMemcpyAsync(dVel, vel, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, s));
                    cuda_launch_heavy_s(dPos, dVel, dt, MOVE_N, mgrid, BLOCK, s);
                    CUDA_CHECK(cudaMemcpyAsync(tmp, dOut, MOVE_N * sizeof(float2), cudaMemcpyDeviceToHost, s));
                    CUDA_CHECK(cudaStreamSynchronize(s));
                    memcpy(outCache, tmp, MOVE_N * sizeof(float2));
                } else {
                    build_staging(dirty[0], ndH, pos, vel, posStg[0], velStg[0]);
                    CUDA_CHECK(cudaMemcpyAsync(dChunkIdx, dirty[0], ndH * sizeof(int), cudaMemcpyHostToDevice, s));
                    CUDA_CHECK(cudaMemcpyAsync(dPosStg[0], posStg[0], (size_t)ndH * CHUNK * sizeof(float2), cudaMemcpyHostToDevice, s));
                    CUDA_CHECK(cudaMemcpyAsync(dVelStg[0], velStg[0], (size_t)ndH * CHUNK * sizeof(float2), cudaMemcpyHostToDevice, s));
                    cuda_launch_scatter_chunk(dPosStg[0], dPos, dChunkIdx, ndH, CHUNK, BLOCK, s);
                    cuda_launch_scatter_chunk(dVelStg[0], dVel, dChunkIdx, ndH, CHUNK, BLOCK, s);
                    cuda_launch_heavy_s(dPos, dVel, dt, MOVE_N, mgrid, BLOCK, s);
                    cuda_launch_gather_chunk(dOut, dOutStg, dChunkIdx, ndH, CHUNK, BLOCK, s);
                    CUDA_CHECK(cudaMemcpyAsync(outStg, dOutStg, (size_t)ndH * CHUNK * sizeof(float2), cudaMemcpyDeviceToHost, s));
                    CUDA_CHECK(cudaStreamSynchronize(s));
                    patch_outcache(outCache, outStg, dirty[0], ndH);
                }
                if (f >= DIFF_WARMUP) wh[f - DIFF_WARMUP] = now_ms() - t0;
            }
            resHash = median(wh, DIFF_FRAMES);
            /* resHash parity：末帧 outCache vs 当前 host 全量重跑 */
            CUDA_CHECK(cudaMemcpyAsync(dPos, pos, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, s));
            CUDA_CHECK(cudaMemcpyAsync(dVel, vel, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, s));
            cuda_launch_heavy_s(dPos, dVel, dt, MOVE_N, mgrid, BLOCK, s);
            CUDA_CHECK(cudaMemcpyAsync(tmpFull, dOut, MOVE_N * sizeof(float2), cudaMemcpyDeviceToHost, s));
            CUDA_CHECK(cudaStreamSynchronize(s));
            int mismH = 0;
            for (int i = 0; i < MOVE_N; i++)
                if (outCache[i].x != tmpFull[i].x || outCache[i].y != tmpFull[i].y) mismH++;
            if (mismH) printf("    resHash parity FAIL %d/%d @sp=%.0f%%\n", mismH, MOVE_N, sp * 100);
        }

        /* ================ resHashMode：最终形态 = resHashX + job 级模式切换（GpuResidencyManager 真实形态） ================
           - 增量模式：hash diff + staging 拼接 + scatter/gather + 跨帧流水（双 staging 快照，上传不碰 host → 无 DMA 竞态）→ 稀疏 x3+
           - 全量模式：跳过 diff，直接全量上传/回读，提交即 sync（非流水，sync 后才 gen_changes → 无 DMA 竞态）→ dense ≈ full
           - 切换（滞后）：增量模式 dirty 率 > 20%（hash 闭环 crossover）连续 2 帧 → 全量模式；
             全量模式每 4 帧采样一次 hash 校准，dirty 率 < 10% 连续 2 次采样 → 切回增量
             （切回帧 diff_hash 对比采样时 hashTab → staging 冗余但正确，上传后 dPos 仍 = 当前 host） */
        {
            for (int c = 0; c < NCHUNK; c++) hashTab[c] = hash_chunk_crc(pos + (size_t)c * CHUNK, vel + (size_t)c * CHUNK);
            CUDA_CHECK(cudaMemcpyAsync(dPos, pos, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, s));
            CUDA_CHECK(cudaMemcpyAsync(dVel, vel, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, s));
            cuda_launch_heavy_s(dPos, dVel, dt, MOVE_N, mgrid, BLOCK, s);
            CUDA_CHECK(cudaMemcpyAsync(outCache, dOut, MOVE_N * sizeof(float2), cudaMemcpyDeviceToHost, s));
            CUDA_CHECK(cudaStreamSynchronize(s));
            gen_changes(nd, used, pos, vel);
            int ndcM[2];
            ndcM[0] = diff_hash(pos, vel, hashTab, dirty[0]);
            build_staging(dirty[0], ndcM[0], pos, vel, posStg[0], velStg[0]);
            int mode = 0, fullStreak = 0, incStreak = 0, sampleCnt = 0;   /* mode: 0=增量 1=全量 */
            int curM = 0;
            double wchM[DIFF_FRAMES]; int wchIdxM = 0;
            for (int f = 0; f < DIFF_WARMUP + DIFF_FRAMES; f++) {
                /* 非流水帧（全量模式 / 增量退化帧）帧首 gen（gameplay 模拟）：上帧 sync 已完成 → 无 DMA 竞态；在 tf 前 → 不计入同步墙钟 */
                if (mode == 1 || ndcM[curM] >= (int)(NCHUNK * SYNC_DEGRADE)) gen_changes(nd, used, pos, vel);
                double tf = now_ms();
                if (mode == 0 && ndcM[curM] < (int)(NCHUNK * SYNC_DEGRADE)) {
                    /* ---- 增量 staging（staging 双缓冲快照 → 上传异步安全，gen 写另一个 buffer，无竞态） ---- */
                    CUDA_CHECK(cudaMemcpyAsync(dChunkIdx, dirty[curM], ndcM[curM] * sizeof(int), cudaMemcpyHostToDevice, s));
                    CUDA_CHECK(cudaMemcpyAsync(dPosStg[curM], posStg[curM], (size_t)ndcM[curM] * CHUNK * sizeof(float2), cudaMemcpyHostToDevice, s));
                    CUDA_CHECK(cudaMemcpyAsync(dVelStg[curM], velStg[curM], (size_t)ndcM[curM] * CHUNK * sizeof(float2), cudaMemcpyHostToDevice, s));
                    cuda_launch_scatter_chunk(dPosStg[curM], dPos, dChunkIdx, ndcM[curM], CHUNK, BLOCK, s);
                    cuda_launch_scatter_chunk(dVelStg[curM], dVel, dChunkIdx, ndcM[curM], CHUNK, BLOCK, s);
                    cuda_launch_heavy_s(dPos, dVel, dt, MOVE_N, mgrid, BLOCK, s);
                    cuda_launch_gather_chunk(dOut, dOutStg, dChunkIdx, ndcM[curM], CHUNK, BLOCK, s);
                    CUDA_CHECK(cudaMemcpyAsync(outStg, dOutStg, (size_t)ndcM[curM] * CHUNK * sizeof(float2), cudaMemcpyDeviceToHost, s));
                    if (f + 1 < DIFF_WARMUP + DIFF_FRAMES) {   /* 跨帧流水：gen + diff + staging 下一帧 */
                        int nxt = curM ^ 1;
                        gen_changes(nd, used, pos, vel);
                        ndcM[nxt] = diff_hash(pos, vel, hashTab, dirty[nxt]);
                        build_staging(dirty[nxt], ndcM[nxt], pos, vel, posStg[nxt], velStg[nxt]);
                    }
                    CUDA_CHECK(cudaStreamSynchronize(s));
                    patch_outcache(outCache, outStg, dirty[curM], ndcM[curM]);
                } else {
                    /* ---- 全量（增量模式传输退化 / 全量模式跳过 diff）：提交即 sync（全量模式 gen 已帧首做，退化帧 gen 在下方） ---- */
                    CUDA_CHECK(cudaMemcpyAsync(dPos, pos, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, s));
                    CUDA_CHECK(cudaMemcpyAsync(dVel, vel, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, s));
                    cuda_launch_heavy_s(dPos, dVel, dt, MOVE_N, mgrid, BLOCK, s);
                    CUDA_CHECK(cudaMemcpyAsync(tmp, dOut, MOVE_N * sizeof(float2), cudaMemcpyDeviceToHost, s));
                    if (f + 1 < DIFF_WARMUP + DIFF_FRAMES && mode == 1) {
                        /* 全量模式采样：CPU 只读 hash（不碰上传中 DMA 数据），藏进 GPU 执行期 → 不计入 wall */
                        int nxt = curM ^ 1;
                        if ((++sampleCnt & 3) == 0) ndcM[nxt] = diff_hash(pos, vel, hashTab, NULL);
                        else ndcM[nxt] = ndcM[curM];
                    }
                    CUDA_CHECK(cudaStreamSynchronize(s));
                    if (mode == 0 || f + 1 == DIFF_WARMUP + DIFF_FRAMES)
                        memcpy(outCache, tmp, MOVE_N * sizeof(float2));   /* 退化帧/末帧(parity)才维护全量镜像；全量模式中间帧用户直读 tmp，免 16MB 拷贝 */
                    if (f + 1 < DIFF_WARMUP + DIFF_FRAMES && mode == 1) {
                        /* 全量模式切回判定：采样 dirty 率 < 10% 连续 2 次采样 → 切回增量（staging 冗余但正确；dPos 其余 = 本帧全量上传值 = 当前 host） */
                        int nxt = curM ^ 1;
                        if (ndcM[nxt] < (int)(NCHUNK * 0.10f)) {
                            if (++incStreak >= 2) {
                                mode = 0; incStreak = 0;
                                memcpy(outCache, tmp, MOVE_N * sizeof(float2));   /* 切回前 outCache 必须最新（下帧增量 patch 依赖） */
                                ndcM[nxt] = diff_hash(pos, vel, hashTab, dirty[nxt]);
                                build_staging(dirty[nxt], ndcM[nxt], pos, vel, posStg[nxt], velStg[nxt]);
                            }
                        } else incStreak = 0;
                    }
                    /* 增量退化帧（mode==0）：帧首已 gen；下帧必切全量，staging 预生成必白做 → 不预生成 */
                }
                /* 增量模式切全量判定（dirty 率 > 20% 连续 2 帧；全量模式不判） */
                if (mode == 0) {
                    if (ndcM[curM] >= (int)(NCHUNK * 0.20f)) { if (++fullStreak >= 2) mode = 1; }
                    else fullStreak = 0;
                }
                if (f >= DIFF_WARMUP) wchM[wchIdxM++] = now_ms() - tf;
                curM ^= 1;
            }
            resHashM = median(wchM, DIFF_FRAMES);
            /* resHashMode parity：末帧 outCache vs 当前 host 全量重跑 */
            CUDA_CHECK(cudaMemcpyAsync(dPos, pos, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, s));
            CUDA_CHECK(cudaMemcpyAsync(dVel, vel, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, s));
            cuda_launch_heavy_s(dPos, dVel, dt, MOVE_N, mgrid, BLOCK, s);
            CUDA_CHECK(cudaMemcpyAsync(tmpFull, dOut, MOVE_N * sizeof(float2), cudaMemcpyDeviceToHost, s));
            CUDA_CHECK(cudaStreamSynchronize(s));
            int mismHM = 0;
            for (int i = 0; i < MOVE_N; i++)
                if (outCache[i].x != tmpFull[i].x || outCache[i].y != tmpFull[i].y) mismHM++;
            if (mismHM) printf("    resHashMode parity FAIL %d/%d @sp=%.0f%%\n", mismHM, MOVE_N, sp * 100);
        }

        /* resCross parity：最后一帧 outCache（增量 patch 全量镜像）vs 同输入全量回读 */
        int lastCur = cur ^ 1;
        (void)lastCur;
        CUDA_CHECK(cudaMemcpyAsync(dPos, pos, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, s));
        CUDA_CHECK(cudaMemcpyAsync(dVel, vel, MOVE_N * sizeof(float2), cudaMemcpyHostToDevice, s));
        cuda_launch_heavy_s(dPos, dVel, dt, MOVE_N, mgrid, BLOCK, s);
        CUDA_CHECK(cudaMemcpyAsync(tmpFull, dOut, MOVE_N * sizeof(float2), cudaMemcpyDeviceToHost, s));
        CUDA_CHECK(cudaStreamSynchronize(s));
        int mismatchX = 0;
        for (int i = 0; i < MOVE_N; i++)
            if (outCache[i].x != tmpFull[i].x || outCache[i].y != tmpFull[i].y) mismatchX++;

        printf("    %-5.0f%%   %8.3f   %8.3f   %8.3f   %8.3f   %8.3f   %8.3f   %8.3f   %8.3f   %8.3f   %8.2f   %d/%d%s\n",
            sp * 100, median(dAvx, DIFF_FRAMES), median(dMem, DIFF_FRAMES),
            resSync, resCross, resHash, resHashM, fullMode, full, resident,
            full / resHashM, mismatch, MOVE_N, mismatchX ? "(x)" : "");
    }

    /* ================ 粒度对照：2KB chunk(128实体) vs 16KB 块(2048实体)（用户提议分块扫描） ================
       diff 全扫读取量恒为 32MB（内存带宽主导）——块粒度不改变扫描量，只改变 dirty 集合/传输开销。
       期望 dirty 数据量 ≈ sp×全量（dirty 实体数守恒），块数差 8 倍。 */
    {
        const int sizes[2] = { 128, 2048 };
        const char* names[2] = { "2KB(128实体)", "16KB(2048实体)" };
        const float sps[3] = { 0.01f, 0.05f, 0.10f };
        printf("\n  ==== 粒度对照：diff 全扫 32MB 恒量（带宽主导），块粒度只影响 dirty 集合/传输 ====\n");
        printf("  sp      粒度            dirty块数   dirty数据量(KB,pos+vel)   diff全扫(ms)\n");
        for (int ssi = 0; ssi < 3; ssi++) {
            float sp = sps[ssi];
            for (int gi = 0; gi < 2; gi++) {
                int chunkSize = sizes[gi];
                int nchunk = MOVE_N / chunkSize;
                int nd = (int)(nchunk * sp + 0.5f);
                if (nd < 1) nd = 1;
                d_rng = 1234;
                for (int i = 0; i < MOVE_N; i++) {
                    pos[i].x = (float)(d_rnd() * 200 - 100); pos[i].y = (float)(d_rnd() * 200 - 100);
                    vel[i].x = (float)(d_rnd() * 200 - 100); vel[i].y = (float)(d_rnd() * 200 - 100);
                }
                memcpy(spos, pos, MOVE_N * sizeof(float2));
                memcpy(svel, vel, MOVE_N * sizeof(float2));
                memset(used, 0, NCHUNK * sizeof(int));
                for (int j = 0; j < nd; j++) {
                    int c;
                    do { c = (int)(d_rnd() * nchunk); } while (used[c]);
                    used[c] = 1;
                    for (int e = c * chunkSize; e < (c + 1) * chunkSize; e++) {
                        pos[e].x += 0.1f; pos[e].y -= 0.05f;
                        vel[e].x += 0.02f; vel[e].y += 0.01f;
                    }
                }
                double tg = now_ms();
                int ndA = diff_all_gen(chunkSize, pos, vel, spos, svel, NULL);
                double tdiff = now_ms() - tg;
                double dirtyKB = (double)ndA * chunkSize * sizeof(float2) * 2 / 1024.0;
                printf("  %-4.0f%%  %-14s %10d %21.1f %14.3f\n", sp * 100, names[gi], ndA, dirtyKB, tdiff);
            }
        }
        printf("  注意：本段是「整块全改」场景（块内全部实体改）——dirty 实体数守恒 ≈ sp×N，两粒度传输量相当。\n");
        printf("        若真实是「单实体稀疏改」（块内 1 实体变），16KB 粒度污染块多 16 倍 → 传输放大，粗粒度更差。\n");
    }

    /* ================ hash 索引 diff：只读 host 16MB（省 shadow 16MB 读取）对照 AVX2 全扫 32MB ================
       验证两个门：① 速度省半预期（~0.6ms vs 1.2ms）；② dirty 检测一致性（hash 无漏检，与 AVX2 全扫逐帧 dirty 数相等）。
       hash 成本与 dirty 率无关（必须读完整 chunk）；AVX2 100% dirty 时 chunk 内提前退出反而更快（但 dense 已走全量模式）。 */
    {
        chunk_hash* hashTab = (chunk_hash*)malloc(NCHUNK * sizeof(chunk_hash));
        if (!hashTab) { printf("  hashTab malloc fail\n"); goto fail; }
        const float sps[6] = { 0.01f, 0.05f, 0.10f, 0.25f, 0.50f, 1.00f };
        const int nsp = 6;
        double thArr[DIFF_FRAMES], taArr[DIFF_FRAMES];
        int ndArr[DIFF_FRAMES];
        printf("\n  ==== hash 索引 diff（CRC32C 双链 8B/chunk）：只读 host 16MB，省 shadow 16MB ====\n");
        printf("  sp      hashDiff(ms)  avx2Full(ms)  省x    dirty数(逐帧)  漏检\n");
        for (int si = 0; si < nsp; si++) {
            float sp = sps[si];
            int nd = (int)(NCHUNK * sp + 0.5f); if (nd < 1) nd = 1;
            d_rng = 1234;
            for (int i = 0; i < MOVE_N; i++) {
                pos[i].x = (float)(d_rnd() * 200 - 100); pos[i].y = (float)(d_rnd() * 200 - 100);
                vel[i].x = (float)(d_rnd() * 200 - 100); vel[i].y = (float)(d_rnd() * 200 - 100);
            }
            memcpy(spos, pos, MOVE_N * sizeof(float2));
            memcpy(svel, vel, MOVE_N * sizeof(float2));
            for (int c = 0; c < NCHUNK; c++) hashTab[c] = hash_chunk_crc(pos + (size_t)c * CHUNK, vel + (size_t)c * CHUNK);
            int totalMiss = 0;
            for (int f = 0; f < DIFF_WARMUP + DIFF_FRAMES; f++) {
                gen_changes(nd, used, pos, vel);   /* 改 host，不改 shadow/hashTab */
                double t0 = now_ms();
                volatile int nh = diff_hash(pos, vel, hashTab, NULL);
                double th = now_ms() - t0;
                t0 = now_ms();
                volatile int na = diff_all(1, pos, vel, spos, svel, dirty[0]);
                double ta = now_ms() - t0;
                if (nh != na) totalMiss += (na - nh);   /* 负=误检，正=漏检 */
                shadow_update(spos, svel, pos, vel, dirty[0], na);   /* 同步 AVX2 的 shadow（diff 后保持 = host 最新值） */
                if (f >= DIFF_WARMUP) { thArr[f - DIFF_WARMUP] = th; taArr[f - DIFF_WARMUP] = ta; ndArr[f - DIFF_WARMUP] = na; }
            }
            double thm = median(thArr, DIFF_FRAMES), tam = median(taArr, DIFF_FRAMES);
            printf("  %-5.0f%%   %9.3f   %10.3f   %4.2f    %6d        %d\n",
                sp * 100, thm, tam, tam / thm, median_int(ndArr, DIFF_FRAMES), totalMiss);
        }
        free(hashTab);
    }

    cudaFreeHost(pos); cudaFreeHost(vel); cudaFreeHost(outCache); cudaFreeHost(tmp); cudaFreeHost(tmpFull);
    cudaFreeHost(posStg[0]); cudaFreeHost(velStg[0]); cudaFreeHost(posStg[1]); cudaFreeHost(velStg[1]); cudaFreeHost(outStg);
    free(spos); free(svel); free(dirty[0]); free(dirty[1]); free(used); free(hashTab);
    cudaFree(dPos); cudaFree(dVel); cudaFree(dOut);
    cudaFree(dPosStg[0]); cudaFree(dVelStg[0]); cudaFree(dPosStg[1]); cudaFree(dVelStg[1]); cudaFree(dOutStg);
    cudaFree(dChunkIdx);
    cudaStreamDestroy(s);
    cudaDeviceReset();
    return;

fail:
    cudaFreeHost(pos); cudaFreeHost(vel); cudaFreeHost(outCache); cudaFreeHost(tmp); cudaFreeHost(tmpFull);
    cudaFreeHost(posStg[0]); cudaFreeHost(velStg[0]); cudaFreeHost(posStg[1]); cudaFreeHost(velStg[1]); cudaFreeHost(outStg);
    free(spos); free(svel); free(dirty[0]); free(dirty[1]); free(used); free(hashTab);
    cudaFree(dPos); cudaFree(dVel); cudaFree(dOut);
    cudaFree(dPosStg[0]); cudaFree(dVelStg[0]); cudaFree(dPosStg[1]); cudaFree(dVelStg[1]); cudaFree(dOutStg);
    cudaFree(dChunkIdx);
    cudaStreamDestroy(s);
    cudaDeviceReset();
}
