// ============================================================
// GpuResidency.cpp — GpuResidencyManager（wgpu 后端，docs/17 resHashMode 实施，v2 跨帧流水）。
//   常驻 buffer + 4 链 CRC32C hash 索引 diff（chunk=128 实体）+ staging 拼接 +
//   scatter/gather（WGSL 内嵌）+ job 级模式切换（dirty≥20% 连续 2 帧切全量；
//   全量每 4 帧采样 hash，<10% 连续 2 次切回）。
//   v2：输入/输出分离（inPtrs 纯输入 / outPtrs 结果镜像）+ 双 staging 快照 +
//   diff/拼接藏进 GPU 执行期（Sync 先 diff+build → 完成上帧 → 提交本帧不 wait 即返回）。
//   复用 GpuCompute 的 W/g_device/g_queue（GpuCompute.h）。
//   数据面归 C++，C# 只传 host 指针（NativeArray GetUnsafePtr，可缓存内存）。
// ============================================================
#include "GpuResidency.h"

#include <windows.h>
#include <string.h>
#include <stdio.h>
#include <stdarg.h>
#include <vector>
#include <nmmintrin.h>   // _mm_crc32_u64 (SSE4.2)

#include "GpuCompute.h"

// ---------------- 常量与 WGSL ----------------

static const int kDefaultChunkEntities = 128;
static const float kDegradeThreshold = 0.20f;   // 增量→全量（dirty 率 ≥20%）
static const float kSwitchBackThreshold = 0.10f; // 全量→增量（采样 <10%）
static const int kFullSwitchStreak = 2;          // 连续 2 帧
static const int kSwitchBackStreak = 2;          // 连续 2 次采样

// scatter/gather 内核（u32 视图；同一 device buffer 可被 job kernel 以 vec2f 视图绑定）
static const char* kScatterGatherWGSL = R"(
struct ResParams {
    chunkU32s : i32,
    dirtyCount : i32,
    pad0 : i32,
    pad1 : i32,
};
@group(0) @binding(0) var<storage, read_write> staging : array<u32>;
@group(0) @binding(1) var<storage, read_write> resident : array<u32>;
@group(0) @binding(2) var<storage, read_write> chunkIdx : array<i32>; // read_write：与 GpuCompute 统一 layout（Storage read_write）一致；read 会被 wgpu-core pipeline 校验拒绝
@group(0) @binding(3) var<uniform> p : ResParams;

@compute @workgroup_size(64)
fn scatterMain(@builtin(global_invocation_id) gid : vec3<u32>) {
    let idx = i32(gid.x);
    if (idx >= p.dirtyCount * p.chunkU32s) { return; }
    let c = idx / p.chunkU32s;
    let o = idx % p.chunkU32s;
    let dst = chunkIdx[c];
    if (dst >= 0) { resident[dst * p.chunkU32s + o] = staging[idx]; }
}

@compute @workgroup_size(64)
fn gatherMain(@builtin(global_invocation_id) gid : vec3<u32>) {
    let idx = i32(gid.x);
    if (idx >= p.dirtyCount * p.chunkU32s) { return; }
    let c = idx / p.chunkU32s;
    let o = idx % p.chunkU32s;
    let dst = chunkIdx[c];
    if (dst >= 0) { staging[idx] = resident[dst * p.chunkU32s + o]; }
}
)";

// ---------------- 错误缓冲 ----------------

static char g_resError[512] = { 0 };
static void resError(const char* fmt, ...) {
    va_list ap; va_start(ap, fmt);
    vsnprintf(g_resError, sizeof(g_resError), fmt, ap);
    va_end(ap);
}
static void resClear() { g_resError[0] = 0; }

// ---------------- 异步等待（复制自 GpuCompute.cpp，独立 TU） ----------------

struct AsyncWait { volatile int done = 0; volatile int status = 0; };
static void onBufferMapRes(WGPUMapAsyncStatus status, WGPUStringView message, void* u1, void* u2) {
    AsyncWait* w = (AsyncWait*)u1;
    w->status = (int)status; w->done = 1;
}
static void pollDeviceRes(volatile int* done) {
    for (int i = 0; !(*done) && i < 5000000; i++) {
        W.DevicePoll(g_device, WGPU_FALSE, NULL);
        if (!(*done)) Sleep(0);
    }
}

// ---------------- 4 链并行 CRC32C（探针 diff_probe.cpp 移植） ----------------

static unsigned crc32c_4chain(const void* data, size_t n8) {
    const uint64_t* p = (const uint64_t*)data;
    size_t n4 = n8 / 4;
    uint64_t a = 0, b = 0, c = 0, d = 0;
    for (size_t i = 0; i < n4; i++) {
        a = _mm_crc32_u64(a, p[i * 4 + 0]);
        b = _mm_crc32_u64(b, p[i * 4 + 1]);
        c = _mm_crc32_u64(c, p[i * 4 + 2]);
        d = _mm_crc32_u64(d, p[i * 4 + 3]);
    }
    return (unsigned)(a ^ b ^ c ^ d);
}

// ---------------- 驻留 job 结构 ----------------

struct ResidencyJob {
    GpuKernel* jobKernel = nullptr;       // 主 job kernel（binding 0..n-1 storage + n uniform）
    GpuKernel* scatterKernel = nullptr;   // staging→resident
    GpuKernel* gatherKernel = nullptr;    // resident→staging
    int storageCount = 0;
    int hasUniform = 0;
    int chunkEntities = kDefaultChunkEntities;
    int nchunk = 0;
    int* elemBytes = nullptr;             // per storage
    int* chunkU32s = nullptr;             // per storage = chunkEntities*elemBytes/4
    int* chunkBytes = nullptr;            // per storage = chunkEntities*elemBytes

    WGPUBuffer* resident = nullptr;       // per storage（STORAGE|COPY_DST|COPY_SRC）
    WGPUBuffer* staging[2] = { nullptr, nullptr };   // 双缓冲 per storage（scatter/gather 目标，快照轮换）
    WGPUBuffer* readback = nullptr;       // per storage（MAP_READ|COPY_DST；D3D12 禁 MapRead+Storage 混用）
    WGPUBuffer chunkIdx = nullptr;        // dirty chunk 索引（i32，容量 nchunk）
    WGPUBuffer resParams = nullptr;       // uniform {chunkU32s, dirtyCount}（16B）
    WGPUBuffer uniform = nullptr;         // job uniform（hasUniform）
    WGPUBindGroup jobBG = nullptr;        // [resident0..n-1, uniform]
    WGPUBindGroup* scatterBG[2] = { nullptr, nullptr };  // per storage × 双缓冲 [staging, resident, chunkIdx, params]
    WGPUBindGroup* gatherBG[2] = { nullptr, nullptr };   // per storage × 双缓冲

    uint32_t* hashTab = nullptr;          // nchunk * storageCount（基于 inPtrs）
    int* dirtyIdx[2] = { nullptr, nullptr };  // 双缓冲（nchunk）
    int nd[2] = { 0, 0 };                 // 每缓冲 dirty chunk 数
    uint8_t** stagingHost[2] = { nullptr, nullptr };  // 双缓冲 per storage 拼接缓冲（容量 = 全量）
    int cur = 0;                          // 当前构建/提交缓冲（cur^1 = 上帧已提交）
    int pending = 0;                      // 1 = 有已提交未完成（等待下帧 Sync / Complete）
    int lastIncr = 0;                     // 上帧提交是否增量（0=全量；完成时决定读回/patch 方式）
    void* const* lastOutPtrs = nullptr;   // 上次 Sync 的 outPtrs（Complete 复用；C# 数组跨帧稳定）

    size_t fullBytes = 0;                 // 每 storage 全量字节（count*elemBytes）
    bool residentUploaded = false;
    int mode = 0;                         // 0=增量 1=全量
    int fullStreak = 0;
    int fullSampleCnt = 0;
    int incrSampleStreak = 0;
};

// ---------------- 内部 helper ----------------

static WGPUBuffer resMakeBuffer(WGPUBufferUsage usage, size_t size) {
    WGPUBufferDescriptor d;
    memset(&d, 0, sizeof(d));
    d.usage = usage;
    d.size = size;
    return W.DeviceCreateBuffer(g_device, &d);
}

/// 创建 bind group（buffers 数组 + sizes，绑定 0..n-1）
static WGPUBindGroup resMakeBindGroup(WGPUBindGroupLayout bgl, WGPUBuffer* buffers,
                                      const unsigned long long* sizes, int count) {
    std::vector<WGPUBindGroupEntry> entries((size_t)count);
    for (int i = 0; i < count; i++) {
        WGPUBindGroupEntry& e = entries[i];
        memset(&e, 0, sizeof(e));
        e.binding = (uint32_t)i;
        e.buffer = buffers[i];
        e.size = sizes[i];
    }
    WGPUBindGroupDescriptor desc;
    memset(&desc, 0, sizeof(desc));
    desc.layout = bgl;
    desc.entryCount = (size_t)count;
    desc.entries = entries.data();
    return W.DeviceCreateBindGroup(g_device, &desc);
}

/// 单 kernel dispatch（同步提交，不 sync）
static void resDispatch(GpuKernel* k, WGPUBindGroup bg, uint32_t x) {
    WGPUCommandEncoder enc = W.DeviceCreateCommandEncoder(g_device, nullptr);
    WGPUComputePassEncoder pass = W.CommandEncoderBeginComputePass(enc, nullptr);
    W.ComputePassEncoderSetPipeline(pass, k->pipe);
    W.ComputePassEncoderSetBindGroup(pass, 0, bg, 0, nullptr);
    W.ComputePassEncoderDispatchWorkgroups(pass, x, 1, 1);
    W.ComputePassEncoderEnd(pass);
    WGPUCommandBuffer cmd = W.CommandEncoderFinish(enc, nullptr);
    W.QueueSubmit(g_queue, 1, &cmd);
    W.ComputePassEncoderRelease(pass);
    W.CommandEncoderRelease(enc);
    W.CommandBufferRelease(cmd);
}

/// mapAsync 读回（readback buffer 直接 map；调用前已 copy 完成）
static bool resMapRead(WGPUBuffer buf, void* out, size_t size) {
    AsyncWait aw;
    WGPUBufferMapCallbackInfo info;
    memset(&info, 0, sizeof(info));
    info.mode = WGPUCallbackMode_AllowProcessEvents;
    info.callback = onBufferMapRes;
    info.userdata1 = &aw;
    W.BufferMapAsync(buf, WGPUMapMode_Read, 0, size, info);
    // mapAsync 回调由 DevicePoll 处理：wait=true 等已提交工作 + process events，
    // 通常直接触发回调（省去忙等轮询的固定延迟）；未触发则回退忙等保险
    W.DevicePoll(g_device, WGPU_TRUE, nullptr);
    if (!aw.done) pollDeviceRes(&aw.done);
    if (!aw.done || aw.status != WGPUMapAsyncStatus_Success) { resError("mapAsync 失败 status=%d", aw.status); return false; }
    void* mapped = W.BufferGetMappedRange(buf, 0, size);
    if (mapped) memcpy(out, mapped, size);
    W.BufferUnmap(buf);
    return mapped != nullptr;
}

/// 设备侧回读：src → readback(COPY_DST|MAP_READ) → mapAsync → memcpy → unmap。
/// （D3D12 下 MapRead 与 Storage 不可混用，故 staging 与 readback 分两个 buffer）
static bool resCopyMapRead(WGPUBuffer src, WGPUBuffer readback, void* out, size_t size) {
    WGPUCommandEncoder enc = W.DeviceCreateCommandEncoder(g_device, nullptr);
    W.CommandEncoderCopyBufferToBuffer(enc, src, 0, readback, 0, size);
    WGPUCommandBuffer cmd = W.CommandEncoderFinish(enc, nullptr);
    W.QueueSubmit(g_queue, 1, &cmd);
    W.CommandBufferRelease(cmd);
    W.CommandEncoderRelease(enc);
    W.DevicePoll(g_device, WGPU_TRUE, nullptr);
    return resMapRead(readback, out, size);
}

/// 完成上帧 pending：读回 → patch outPtrs → pending=0。
///   （wait 由 resMapRead 内部 DevicePoll(wait) 覆盖：copy 是上帧提交的队列工作，
///     wait 等队列空 + process events 触发 map 回调，无需单独 wait）
///   增量上帧：staging[nxt]（gather 输出）→ readback → stagingHost[nxt] → patch outPtrs 的 dirty chunk
///   全量上帧：resident → readback → 直接覆盖 outPtrs
static bool resCompleteFrame(ResidencyJob* j, void* const* outPtrs) {
    if (!j->pending) return true;
    int nxt = j->cur ^ 1;
    int ndPrev = j->nd[nxt];
    if (j->lastIncr) {
        for (int s = 0; s < j->storageCount; s++) {
            if (!resMapRead(j->readback[s], j->stagingHost[nxt][s], (size_t)ndPrev * j->chunkBytes[s])) return false;
            uint8_t* out = (uint8_t*)outPtrs[s];
            for (int k = 0; k < ndPrev; k++)
                memcpy(out + (size_t)j->dirtyIdx[nxt][k] * j->chunkBytes[s],
                       j->stagingHost[nxt][s] + (size_t)k * j->chunkBytes[s], (size_t)j->chunkBytes[s]);
        }
    } else {
        for (int s = 0; s < j->storageCount; s++)
            if (!resMapRead(j->readback[s], outPtrs[s], (size_t)j->fullBytes)) return false;
    }
    j->pending = 0;
    return true;
}

// ---------------- 导出实现 ----------------

extern "C" {

JOB_API void* GpuResidency_RegisterJob(const char* wgsl, int storageCount, int hasUniform,
                                       int chunkEntities, const int* elemBytes) {
    resClear();
    if (!g_device) { resError("GpuCompute 未初始化（先调 GpuCompute_Initialize）"); return nullptr; }
    if (!wgsl || storageCount <= 0 || !elemBytes) { resError("参数非法"); return nullptr; }

    ResidencyJob* j = new ResidencyJob();
    j->storageCount = storageCount;
    j->hasUniform = hasUniform;
    j->chunkEntities = chunkEntities > 0 ? chunkEntities : kDefaultChunkEntities;
    j->elemBytes = new int[storageCount];
    j->chunkU32s = new int[storageCount];
    j->chunkBytes = new int[storageCount];
    for (int s = 0; s < storageCount; s++) {
        j->elemBytes[s] = elemBytes[s];
        j->chunkBytes[s] = j->chunkEntities * elemBytes[s];
        j->chunkU32s[s] = j->chunkBytes[s] / 4;
    }

    j->jobKernel = (GpuKernel*)GpuCompute_CreateKernel(wgsl, storageCount, hasUniform);
    j->scatterKernel = (GpuKernel*)GpuCompute_CreateKernelEx(kScatterGatherWGSL, 3, 1, "scatterMain");
    j->gatherKernel = (GpuKernel*)GpuCompute_CreateKernelEx(kScatterGatherWGSL, 3, 1, "gatherMain");
    if (!j->jobKernel || !j->scatterKernel || !j->gatherKernel) {
        resError("kernel 编译失败");
        GpuResidency_ReleaseJob(j);
        return nullptr;
    }
    return j;
}

JOB_API void GpuResidency_ReleaseJob(void* job) {
    ResidencyJob* j = (ResidencyJob*)job;
    if (!j) return;
    if (j->jobBG) W.BindGroupRelease(j->jobBG);
    for (int b = 0; b < 2; b++) {
        for (int s = 0; s < j->storageCount; s++) {
            if (j->scatterBG[b] && j->scatterBG[b][s]) W.BindGroupRelease(j->scatterBG[b][s]);
            if (j->gatherBG[b] && j->gatherBG[b][s]) W.BindGroupRelease(j->gatherBG[b][s]);
            if (j->staging[b] && j->staging[b][s]) W.BufferDestroy(j->staging[b][s]);
        }
    }
    for (int s = 0; s < j->storageCount; s++) {
        if (j->resident && j->resident[s]) W.BufferDestroy(j->resident[s]);
        if (j->readback && j->readback[s]) W.BufferDestroy(j->readback[s]);
    }
    if (j->chunkIdx) W.BufferDestroy(j->chunkIdx);
    if (j->resParams) W.BufferDestroy(j->resParams);
    if (j->uniform) W.BufferDestroy(j->uniform);
    if (j->jobKernel) GpuCompute_ReleaseKernel(j->jobKernel);
    if (j->scatterKernel) GpuCompute_ReleaseKernel(j->scatterKernel);
    if (j->gatherKernel) GpuCompute_ReleaseKernel(j->gatherKernel);
    delete[] j->elemBytes;
    delete[] j->chunkU32s;
    delete[] j->chunkBytes;
    delete[] j->resident;
    delete[] j->readback;
    for (int b = 0; b < 2; b++) {
        delete[] j->staging[b];
        delete[] j->scatterBG[b];
        delete[] j->gatherBG[b];
        delete[] j->dirtyIdx[b];
        if (j->stagingHost[b]) for (int s = 0; s < j->storageCount; s++) delete[] j->stagingHost[b][s];
        delete[] j->stagingHost[b];
    }
    delete[] j->hashTab;
    delete j;
}

JOB_API int GpuResidency_GetMode(void* job) {
    return job ? ((ResidencyJob*)job)->mode : -1;
}

JOB_API const char* GpuResidency_GetLastError() {
    return g_resError[0] ? g_resError : nullptr;
}

JOB_API int GpuResidency_Sync(void* job, void* const* inPtrs, void* const* outPtrs,
                              const int* lengths, const void* uniformBytes, int uniformSize, int count) {
    ResidencyJob* j = (ResidencyJob*)job;
    if (!j || !inPtrs || !outPtrs || !lengths || count <= 0) { resError("参数非法"); return 0; }
    resClear();

    // ---- 容量初始化（首次按 count 分配；之后 count 必须一致） ----
    if (!j->residentUploaded) {
        j->nchunk = (count + j->chunkEntities - 1) / j->chunkEntities;
        j->fullBytes = (size_t)count * j->elemBytes[0];
        j->resident = new WGPUBuffer[j->storageCount];
        j->readback = new WGPUBuffer[j->storageCount];
        for (int b = 0; b < 2; b++) {
            j->staging[b] = new WGPUBuffer[j->storageCount];
            j->scatterBG[b] = new WGPUBindGroup[j->storageCount];
            j->gatherBG[b] = new WGPUBindGroup[j->storageCount];
            j->dirtyIdx[b] = new int[j->nchunk];
            j->stagingHost[b] = new uint8_t*[j->storageCount];
        }
        j->hashTab = new uint32_t[(size_t)j->nchunk * j->storageCount];
        for (int s = 0; s < j->storageCount; s++) {
            size_t bytes = (size_t)count * j->elemBytes[s];
            j->resident[s] = resMakeBuffer(WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst | WGPUBufferUsage_CopySrc, bytes);
            j->readback[s] = resMakeBuffer(WGPUBufferUsage_MapRead | WGPUBufferUsage_CopyDst, bytes);
            for (int b = 0; b < 2; b++) {
                j->staging[b][s] = resMakeBuffer(WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst | WGPUBufferUsage_CopySrc, bytes);
                j->stagingHost[b][s] = new uint8_t[bytes];
            }
            if (!j->resident[s] || !j->readback[s] || !j->staging[0][s] || !j->staging[1][s]) {
                resError("创建常驻 buffer 失败"); return 0;
            }
        }
        j->chunkIdx = resMakeBuffer(WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst, (size_t)j->nchunk * sizeof(int));
        j->resParams = resMakeBuffer(WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst, 16);
        if (j->hasUniform) j->uniform = resMakeBuffer(WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst, (size_t)uniformSize);

        // job bind group [resident0..n-1, uniform]
        {
            int n = j->storageCount + (j->hasUniform ? 1 : 0);
            std::vector<WGPUBuffer> bufs;
            std::vector<unsigned long long> sizes;
            for (int s = 0; s < j->storageCount; s++) {
                bufs.push_back(j->resident[s]);
                sizes.push_back((size_t)count * j->elemBytes[s]);
            }
            if (j->hasUniform) { bufs.push_back(j->uniform); sizes.push_back((size_t)uniformSize); }
            j->jobBG = resMakeBindGroup(j->jobKernel->bgl, bufs.data(), sizes.data(), n);
        }
        // scatter/gather bind group（per storage × 双缓冲；绑定各自 staging[b][s]）
        for (int b = 0; b < 2; b++) {
            for (int s = 0; s < j->storageCount; s++) {
                WGPUBuffer bufs[4] = { j->staging[b][s], j->resident[s], j->chunkIdx, j->resParams };
                unsigned long long sizes[4] = { (size_t)count * j->elemBytes[s], (size_t)count * j->elemBytes[s],
                                                (size_t)j->nchunk * sizeof(int), 16 };
                j->scatterBG[b][s] = resMakeBindGroup(j->scatterKernel->bgl, bufs, sizes, 4);
                j->gatherBG[b][s] = resMakeBindGroup(j->gatherKernel->bgl, bufs, sizes, 4);
                if (!j->scatterBG[b][s] || !j->gatherBG[b][s]) {
                    resError("scatter/gather bind group 创建失败 b=%d s=%d: %s", b, s,
                             GpuCompute_GetLastError() ? GpuCompute_GetLastError() : "(无 uncaptured error)");
                    return 0;
                }
            }
        }

        // ---- 首帧：全量上传 inPtrs → 执行 → 全量回读 outPtrs（结果镜像）→ hashTab = inPtrs ----
        for (int s = 0; s < j->storageCount; s++)
            W.QueueWriteBuffer(g_queue, j->resident[s], 0, inPtrs[s], (size_t)count * j->elemBytes[s]);
        if (j->hasUniform) W.QueueWriteBuffer(g_queue, j->uniform, 0, uniformBytes, (size_t)uniformSize);
        resDispatch(j->jobKernel, j->jobBG, (uint32_t)((count + 63) / 64));
        W.DevicePoll(g_device, WGPU_TRUE, nullptr);
        for (int s = 0; s < j->storageCount; s++) {
            if (!resCopyMapRead(j->resident[s], j->readback[s], outPtrs[s], (size_t)count * j->elemBytes[s])) return 0;
            for (int c = 0; c < j->nchunk; c++)
                j->hashTab[(size_t)c * j->storageCount + s] =
                    crc32c_4chain((const uint8_t*)inPtrs[s] + (size_t)c * j->chunkBytes[s], (size_t)j->chunkBytes[s] / 8);
        }
        j->residentUploaded = true;
        return 1;
    }
    if ((count + j->chunkEntities - 1) / j->chunkEntities != j->nchunk) {
        resError("count 变化不支持（固定容量）");
        return 0;
    }

    // ---- 阶段 A：diff + build（GPU 跑上帧期间 → diff 藏进 GPU 执行期） ----
    int c0 = j->cur;
    int ndc = 0;
    bool runDiff = (j->mode == 0) || ((j->fullSampleCnt & 3) == 0);
    if (runDiff) {
        for (int c = 0; c < j->nchunk; c++) {
            bool dirty = false;
            for (int s = 0; s < j->storageCount; s++) {
                unsigned h = crc32c_4chain((const uint8_t*)inPtrs[s] + (size_t)c * j->chunkBytes[s], (size_t)j->chunkBytes[s] / 8);
                if (h != j->hashTab[(size_t)c * j->storageCount + s]) {
                    j->hashTab[(size_t)c * j->storageCount + s] = h;
                    dirty = true;
                }
            }
            if (dirty) j->dirtyIdx[c0][ndc++] = c;
        }
        // 模式切换（阈值 20% / 10%，滞后 2 帧）
        if (j->mode == 0) {
            if (ndc >= (int)(j->nchunk * kDegradeThreshold)) {
                if (++j->fullStreak >= kFullSwitchStreak) { j->mode = 1; j->fullSampleCnt = 0; }
            } else j->fullStreak = 0;
        } else {
            if (ndc < (int)(j->nchunk * kSwitchBackThreshold)) {
                if (++j->incrSampleStreak >= kSwitchBackStreak) { j->mode = 0; j->incrSampleStreak = 0; }
            } else j->incrSampleStreak = 0;
        }
    }
    j->fullSampleCnt++;
    j->nd[c0] = ndc;
    bool doIncr = (j->mode == 0 && ndc < (int)(j->nchunk * kDegradeThreshold));
    if (doIncr) {
        // staging 拼接（快照：上传后 gen 改 inPtrs 不影响本帧提交）
        for (int s = 0; s < j->storageCount; s++) {
            const uint8_t* src = (const uint8_t*)inPtrs[s];
            uint8_t* dst = j->stagingHost[c0][s];
            for (int k = 0; k < ndc; k++)
                memcpy(dst + (size_t)k * j->chunkBytes[s], src + (size_t)j->dirtyIdx[c0][k] * j->chunkBytes[s], (size_t)j->chunkBytes[s]);
        }
    }

    // ---- 阶段 B：完成上帧（wait + 读回 + patch outPtrs） ----
    if (!resCompleteFrame(j, outPtrs)) return 0;

    // ---- 阶段 C：提交本帧（不 wait，下帧 Sync / Complete 完成） ----
    if (j->hasUniform) W.QueueWriteBuffer(g_queue, j->uniform, 0, uniformBytes, (size_t)uniformSize);
    if (doIncr) {
        W.QueueWriteBuffer(g_queue, j->chunkIdx, 0, j->dirtyIdx[c0], (size_t)ndc * sizeof(int));
        uint8_t params[16] = { 0 };
        memcpy(params, &j->chunkU32s[0], 4);   // elemBytes 相同（均为 float2=8）时 chunkU32s 全等
        memcpy(params + 4, &ndc, 4);
        W.QueueWriteBuffer(g_queue, j->resParams, 0, params, 16);
        for (int s = 0; s < j->storageCount; s++)
            W.QueueWriteBuffer(g_queue, j->staging[c0][s], 0, j->stagingHost[c0][s], (size_t)ndc * j->chunkBytes[s]);
        // 单 encoder：scatter×S → job → gather×S → copy(staging→readback)×S，一次提交
        WGPUCommandEncoder enc = W.DeviceCreateCommandEncoder(g_device, nullptr);
        WGPUComputePassEncoder pass = W.CommandEncoderBeginComputePass(enc, nullptr);
        for (int s = 0; s < j->storageCount; s++) {
            W.ComputePassEncoderSetPipeline(pass, j->scatterKernel->pipe);
            W.ComputePassEncoderSetBindGroup(pass, 0, j->scatterBG[c0][s], 0, nullptr);
            W.ComputePassEncoderDispatchWorkgroups(pass, (uint32_t)(((size_t)ndc * j->chunkU32s[s] + 63) / 64), 1, 1);
        }
        W.ComputePassEncoderSetPipeline(pass, j->jobKernel->pipe);
        W.ComputePassEncoderSetBindGroup(pass, 0, j->jobBG, 0, nullptr);
        W.ComputePassEncoderDispatchWorkgroups(pass, (uint32_t)((count + 63) / 64), 1, 1);
        for (int s = 0; s < j->storageCount; s++) {
            W.ComputePassEncoderSetPipeline(pass, j->gatherKernel->pipe);
            W.ComputePassEncoderSetBindGroup(pass, 0, j->gatherBG[c0][s], 0, nullptr);
            W.ComputePassEncoderDispatchWorkgroups(pass, (uint32_t)(((size_t)ndc * j->chunkU32s[s] + 63) / 64), 1, 1);
        }
        W.ComputePassEncoderEnd(pass);
        W.ComputePassEncoderRelease(pass);
        for (int s = 0; s < j->storageCount; s++)
            W.CommandEncoderCopyBufferToBuffer(enc, j->staging[c0][s], 0, j->readback[s], 0, (size_t)ndc * j->chunkBytes[s]);
        WGPUCommandBuffer cmd = W.CommandEncoderFinish(enc, nullptr);
        W.QueueSubmit(g_queue, 1, &cmd);
        W.CommandBufferRelease(cmd);
        W.CommandEncoderRelease(enc);
    } else {
        // 全量：上传 inPtrs → job → copy(resident→readback)×S，一次提交
        for (int s = 0; s < j->storageCount; s++)
            W.QueueWriteBuffer(g_queue, j->resident[s], 0, inPtrs[s], (size_t)count * j->elemBytes[s]);
        WGPUCommandEncoder enc = W.DeviceCreateCommandEncoder(g_device, nullptr);
        WGPUComputePassEncoder pass = W.CommandEncoderBeginComputePass(enc, nullptr);
        W.ComputePassEncoderSetPipeline(pass, j->jobKernel->pipe);
        W.ComputePassEncoderSetBindGroup(pass, 0, j->jobBG, 0, nullptr);
        W.ComputePassEncoderDispatchWorkgroups(pass, (uint32_t)((count + 63) / 64), 1, 1);
        W.ComputePassEncoderEnd(pass);
        W.ComputePassEncoderRelease(pass);
        for (int s = 0; s < j->storageCount; s++)
            W.CommandEncoderCopyBufferToBuffer(enc, j->resident[s], 0, j->readback[s], 0, (size_t)count * j->elemBytes[s]);
        WGPUCommandBuffer cmd = W.CommandEncoderFinish(enc, nullptr);
        W.QueueSubmit(g_queue, 1, &cmd);
        W.CommandBufferRelease(cmd);
        W.CommandEncoderRelease(enc);
    }
    j->lastIncr = doIncr ? 1 : 0;
    j->pending = 1;
    j->lastOutPtrs = outPtrs;
    j->cur ^= 1;
    return 1;
}

JOB_API int GpuResidency_Complete(void* job) {
    ResidencyJob* j = (ResidencyJob*)job;
    if (!j) { resError("参数非法"); return 0; }
    resClear();
    if (!j->residentUploaded) return 1;
    return resCompleteFrame(j, j->lastOutPtrs) ? 1 : 0;
}

} // extern "C"
