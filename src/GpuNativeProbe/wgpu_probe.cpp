// wgpu_probe.cpp — wgpu-native (WebGPU C ABI) 探针实现
// 走 Vulkan/D3D12/Metal（桌面无需浏览器），与 native OpenCL/CUDA 同为独立驱动栈。
// 用 v29.0.1.1 的 webgpu.h（异步 future API，回调 + devicePoll 轮询同步）。
// 负载与 main.cpp 相同：HeavyMove@1M / LightMove@1M / GridSearch closest@100k。
// 接入：把 wgpu.dll（= wgpu_native.dll 改名）放 PATH 或本目录；LoadLibrary 动态加载，不依赖 import lib。

#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include "wgpu/include/webgpu/wgpu.h"
#include "summary.h"

#define MOVE_N 1000000
#define HEAVY_ITER 16
#define DT 1.0f / 60.0f
#define MOVE_WARMUP 5
#define MOVE_FRAMES 20

#define N 100000
#define K 100000
#define DIM 200
#define CELL (DIM * DIM)
#define GRID_FRAMES 20

typedef struct { float x, y; } float2;
typedef struct { int x, y; } int2;

static double now_ms(void) {
    static LARGE_INTEGER f; static int once = 1;
    if (once) { QueryPerformanceFrequency(&f); once = 0; }
    LARGE_INTEGER t; QueryPerformanceCounter(&t);
    return (double)t.QuadPart * 1000.0 / (double)f.QuadPart;
}
static double median(double* a, int n) {
    double* s = (double*)malloc(n * sizeof(double));
    memcpy(s, a, n * sizeof(double));
    for (int i = 0; i < n - 1; i++) for (int j = i + 1; j < n; j++) if (s[j] < s[i]) { double t = s[i]; s[i] = s[j]; s[j] = t; }
    double m = s[n / 2]; free(s); return m;
}

/* ================= 动态加载 wgpu 函数表 ================= */
struct WgpuApi {
    HMODULE module;
    WGPUInstance (*CreateInstance)(const WGPUInstanceDescriptor*);
    WGPUFuture (*InstanceRequestAdapter)(WGPUInstance, const WGPURequestAdapterOptions*, WGPURequestAdapterCallbackInfo);
    WGPUBool (*DevicePoll)(WGPUDevice, WGPUBool, const WGPUSubmissionIndex*);
    WGPUFuture (*AdapterRequestDevice)(WGPUAdapter, const WGPUDeviceDescriptor*, WGPURequestDeviceCallbackInfo);
    WGPUBuffer (*DeviceCreateBuffer)(WGPUDevice, const WGPUBufferDescriptor*);
    void (*BufferDestroy)(WGPUBuffer);
    WGPUFuture (*BufferMapAsync)(WGPUBuffer, WGPUMapMode, size_t, size_t, WGPUBufferMapCallbackInfo);
    void* (*BufferGetMappedRange)(WGPUBuffer, size_t, size_t);
    void (*BufferUnmap)(WGPUBuffer);
    WGPUCommandEncoder (*DeviceCreateCommandEncoder)(WGPUDevice, const WGPUCommandEncoderDescriptor*);
    WGPUComputePassEncoder (*CommandEncoderBeginComputePass)(WGPUCommandEncoder, const WGPUComputePassDescriptor*);
    void (*ComputePassEncoderSetPipeline)(WGPUComputePassEncoder, WGPUComputePipeline);
    void (*ComputePassEncoderSetBindGroup)(WGPUComputePassEncoder, uint32_t, WGPUBindGroup, size_t, const uint32_t*);
    void (*ComputePassEncoderDispatchWorkgroups)(WGPUComputePassEncoder, uint32_t, uint32_t, uint32_t);
    void (*ComputePassEncoderEnd)(WGPUComputePassEncoder);
    WGPUCommandBuffer (*CommandEncoderFinish)(WGPUCommandEncoder, const WGPUCommandBufferDescriptor*);
    void (*CommandEncoderCopyBufferToBuffer)(WGPUCommandEncoder, WGPUBuffer, uint64_t, WGPUBuffer, uint64_t, uint64_t);
    void (*QueueSubmit)(WGPUQueue, size_t, const WGPUCommandBuffer*);
    void (*QueueWriteBuffer)(WGPUQueue, WGPUBuffer, uint64_t, const void*, size_t);
    WGPUQueue (*DeviceGetQueue)(WGPUDevice);
    WGPUShaderModule (*DeviceCreateShaderModule)(WGPUDevice, const WGPUShaderModuleDescriptor*);
    WGPUComputePipeline (*DeviceCreateComputePipeline)(WGPUDevice, const WGPUComputePipelineDescriptor*);
    WGPUBindGroup (*DeviceCreateBindGroup)(WGPUDevice, const WGPUBindGroupDescriptor*);
    WGPUBindGroupLayout (*DeviceCreateBindGroupLayout)(WGPUDevice, const WGPUBindGroupLayoutDescriptor*);
    WGPUPipelineLayout (*DeviceCreatePipelineLayout)(WGPUDevice, const WGPUPipelineLayoutDescriptor*);
    WGPUStatus (*InstanceProcessEvents)(WGPUInstance);
    void (*InstanceRelease)(WGPUInstance);
    void (*AdapterRelease)(WGPUAdapter);
    void (*DeviceRelease)(WGPUDevice);
    void (*CommandBufferRelease)(WGPUCommandBuffer);
    void (*CommandEncoderRelease)(WGPUCommandEncoder);
    void (*ShaderModuleRelease)(WGPUShaderModule);
    void (*ComputePipelineRelease)(WGPUComputePipeline);
    void (*BindGroupRelease)(WGPUBindGroup);
    void (*BindGroupLayoutRelease)(WGPUBindGroupLayout);
    void (*PipelineLayoutRelease)(WGPUPipelineLayout);
};
static struct WgpuApi W;

#define WP(name) W.name = (decltype(W.name))GetProcAddress(W.module, "wgpu" #name)
static int LoadWgpu(const char* path) {
    W.module = LoadLibraryA(path);
    if (!W.module) return 1;
    WP(CreateInstance); WP(InstanceRequestAdapter); WP(DevicePoll); WP(AdapterRequestDevice);
    WP(DeviceCreateBuffer); WP(BufferDestroy); WP(BufferMapAsync); WP(BufferGetMappedRange); WP(BufferUnmap);
    WP(DeviceCreateCommandEncoder); WP(CommandEncoderBeginComputePass);
    WP(ComputePassEncoderSetPipeline); WP(ComputePassEncoderSetBindGroup);
    WP(ComputePassEncoderDispatchWorkgroups); WP(ComputePassEncoderEnd); WP(CommandEncoderFinish);
    WP(CommandEncoderCopyBufferToBuffer); WP(QueueSubmit); WP(QueueWriteBuffer); WP(DeviceGetQueue);
    WP(DeviceCreateShaderModule); WP(DeviceCreateComputePipeline);
    WP(DeviceCreateBindGroup); WP(DeviceCreateBindGroupLayout); WP(DeviceCreatePipelineLayout);
    WP(InstanceProcessEvents); WP(InstanceRelease); WP(AdapterRelease); WP(DeviceRelease);
    WP(CommandBufferRelease); WP(CommandEncoderRelease); WP(ShaderModuleRelease);
    WP(ComputePipelineRelease); WP(BindGroupRelease); WP(BindGroupLayoutRelease); WP(PipelineLayoutRelease);
    if (!W.CreateInstance || !W.InstanceRequestAdapter || !W.AdapterRequestDevice || !W.DeviceCreateBuffer ||
        !W.BufferMapAsync || !W.DeviceGetQueue || !W.QueueSubmit || !W.QueueWriteBuffer || !W.DevicePoll ||
        !W.DeviceCreateShaderModule || !W.DeviceCreateComputePipeline || !W.DeviceCreateBindGroup ||
        !W.DeviceCreateBindGroupLayout || !W.DeviceCreatePipelineLayout || !W.DeviceCreateCommandEncoder ||
        !W.CommandEncoderBeginComputePass || !W.CommandEncoderFinish || !W.BufferGetMappedRange || !W.BufferUnmap) {
        printf("  wgpu.dll 缺关键导出\n");
        return 1;
    }
    return 0;
}
#undef WP

/* ================= 异步回调等待（v29：回调 mode=AllowProcessEvents + devicePoll 轮询） ================= */
struct AsyncWait { volatile int done; volatile int status; WGPUAdapter adapter; WGPUDevice device; };

static void onRequestAdapter(WGPURequestAdapterStatus status, WGPUAdapter adapter, WGPUStringView message, void* u1, void* u2) {
    AsyncWait* w = (AsyncWait*)u1;
    w->status = (int)status; w->adapter = adapter; w->done = 1;
}
static void onRequestDevice(WGPURequestDeviceStatus status, WGPUDevice device, WGPUStringView message, void* u1, void* u2) {
    AsyncWait* w = (AsyncWait*)u1;
    w->status = (int)status; w->device = device; w->done = 1;
}
static void onBufferMap(WGPUMapAsyncStatus status, WGPUStringView message, void* u1, void* u2) {
    AsyncWait* w = (AsyncWait*)u1;
    w->status = (int)status; w->done = 1;
}

/* 轮询直到回调触发：instance 已建但 device 未建时用 InstanceProcessEvents，之后用 DevicePoll */
static void pollInstance(WGPUInstance inst, volatile int* done) {
    for (int i = 0; !(*done) && i < 2000000; i++) {
        W.InstanceProcessEvents(inst);
        if (!(*done)) Sleep(0);
    }
}
static void pollDevice(WGPUDevice dev, volatile int* done) {
    for (int i = 0; !(*done) && i < 2000000; i++) {
        W.DevicePoll(dev, WGPU_FALSE, NULL);
        if (!(*done)) Sleep(0);
    }
}

/* ================= 通用：计算管线 + 单 buffer 集跑一个 kernel ================= */
struct Kernel {
    WGPUComputePipeline pipe;
    WGPUBindGroup bg;
    WGPUBindGroupLayout bgl;
    WGPUPipelineLayout pl;
    WGPUBuffer* buffers;
    int bufferCount;
    size_t outSize;
    WGPUBuffer outBuffer;   /* storage 输出（GPU 写，不可 map） */
    WGPUBuffer staging;     /* MAP_READ|COPY_DST，读回中转 */
};

static WGPUShaderModule makeShader(WGPUDevice dev, const char* wgsl) {
    WGPUShaderSourceWGSL src;
    memset(&src, 0, sizeof(src));
    src.chain.sType = WGPUSType_ShaderSourceWGSL;
    src.code.data = wgsl;
    src.code.length = strlen(wgsl);
    WGPUShaderModuleDescriptor d;
    memset(&d, 0, sizeof(d));
    d.nextInChain = (WGPUChainedStruct*)&src;
    return W.DeviceCreateShaderModule(dev, &d);
}

static WGPUBuffer makeBuffer(WGPUDevice dev, WGPUBufferUsage usage, size_t size) {
    WGPUBufferDescriptor d;
    memset(&d, 0, sizeof(d));
    d.usage = usage;
    d.size = size;
    return W.DeviceCreateBuffer(dev, &d);
}

/* ================= WGSL 内核 ================= */
static const char* HEAVY_WGSL = R"(
@group(0) @binding(0) var<storage, read_write> pos: array<vec2<f32>>;
@group(0) @binding(1) var<storage, read> vel: array<vec2<f32>>;
const dt: f32 = 1.0 / 60.0;
@compute @workgroup_size(256)
fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
    let i: u32 = gid.x;
    if (i >= 1000000u) { return; }
    let px = pos[i].x; let py = pos[i].y;
    let vx = vel[i].x; let vy = vel[i].y;
    var accX: f32 = px * 0.001 + vx * 0.01;
    var accY: f32 = py * 0.001 + vy * 0.01;
    for (var it: u32 = 0u; it < 16u; it++) {
        let phaseX: f32 = accX + f32(it) * 0.03125;
        let phaseY: f32 = accY - f32(it) * 0.0625;
        let wave: f32 = sin(phaseX) + cos(phaseY);
        let radius: f32 = sqrt(accX * accX + accY * accY + 1.0);
        accX = accX * 0.985 + wave * 0.015 + radius * 0.0002 + vx * 0.0001;
        accY = accY * 0.982 - wave * 0.012 + radius * 0.0003 + vy * 0.0001;
    }
    pos[i] = vec2<f32>(px + vx * dt + accX * 0.001, py + vy * dt + accY * 0.001);
}
)";

static const char* LIGHT_WGSL = R"(
@group(0) @binding(0) var<storage, read_write> pos: array<vec2<f32>>;
@group(0) @binding(1) var<storage, read> vel: array<vec2<f32>>;
const dt: f32 = 1.0 / 60.0;
@compute @workgroup_size(256)
fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
    let i: u32 = gid.x;
    if (i >= 1000000u) { return; }
    pos[i] = vec2<f32>(pos[i].x + vel[i].x * dt, pos[i].y + vel[i].y * dt);
}
)";

static const char* CLOSEST_WGSL = R"(
@group(0) @binding(0) var<storage, read> query: array<vec2<f32>>;
@group(0) @binding(1) var<storage, read> hashIndex: array<vec2<i32>>;
@group(0) @binding(2) var<storage, read> cellSE: array<vec2<i32>>;
@group(0) @binding(3) var<storage, read> sorted: array<vec2<f32>>;
@group(0) @binding(4) var<storage, read_write> results: array<i32>;
const GridDimX: i32 = 200;
const GridDimY: i32 = 200;
const SortedLength: i32 = 100000;
@compute @workgroup_size(256)
fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
    let i: i32 = i32(gid.x);
    if (i >= 100000) { return; }
    results[i] = -1;
    let q = query[gid.x];
    // 与 09 相同：bbox = [-100,100]，resInv = 200/200 = 1
    var cx: i32 = i32(floor((q.x + 100.0) * 1.0));
    cx = clamp(cx, 0, GridDimX - 1);
    var cy: i32 = i32(floor((q.y + 100.0) * 1.0));
    cy = clamp(cy, 0, GridDimY - 1);
    var bestDistSq: f32 = 3.402823466e38;
    var bestIdx: i32 = -1;
    var dx: i32 = -1;
    while (dx <= 1) {
        let nx: i32 = cx + dx;
        if (nx >= 0 && nx < GridDimX) {
            var dy: i32 = -1;
            while (dy <= 1) {
                let ny: i32 = cy + dy;
                if (ny >= 0 && ny < GridDimY) {
                    let range = cellSE[ny * GridDimX + nx];
                    let start: i32 = range.x;
                    let end: i32 = range.y;
                    if (start >= 0) {
                        var j: i32 = start;
                        while (j < end) {
                            let sp = sorted[j];
                            let dx2: f32 = q.x - sp.x;
                            let dy2: f32 = q.y - sp.y;
                            let d2: f32 = dx2 * dx2 + dy2 * dy2;
                            if (d2 < bestDistSq) { bestDistSq = d2; bestIdx = j; }
                            j++;
                        }
                    }
                }
                dy++;
            }
        }
        dx++;
    }
    if (bestIdx >= 0) {
        results[i] = hashIndex[bestIdx].y;
    } else {
        var j: i32 = 0;
        while (j < SortedLength) {
            let sp = sorted[j];
            let dx2: f32 = q.x - sp.x;
            let dy2: f32 = q.y - sp.y;
            let d2: f32 = dx2 * dx2 + dy2 * dy2;
            if (d2 < bestDistSq) { bestDistSq = d2; bestIdx = j; }
            j++;
        }
        if (bestIdx >= 0) { results[i] = hashIndex[bestIdx].y; }
    }
}
)";

/* ================= 数据准备（与 main.cpp 同 seed） ================= */
static unsigned long long g_rng = 1234;
static double rnd(void) {
    g_rng = g_rng * 6364136223846793005ULL + 1442695040888963407ULL;
    return (double)((g_rng >> 33) & 0x7FFFFFFF) / 2147483647.0;
}

static int hash_of(float2 p) {
    int cx = (int)floorf((p.x + 100.0f) * 1.0f);
    if (cx < 0) cx = 0; else if (cx > DIM - 1) cx = DIM - 1;
    int cy = (int)floorf((p.y + 100.0f) * 1.0f);
    if (cy < 0) cy = 0; else if (cy > DIM - 1) cy = DIM - 1;
    return cx + cy * DIM;
}

/* ================= 测量单个计算内核（dispatch-only 常驻 / 全量上传+读回） ================= */
static void runCompute(Kernel* k, WGPUQueue q, WGPUDevice dev, uint32_t groups,
    const void* uploadSrc, size_t uploadBytes, const float2* initPos, size_t posBytes,
    int measureRoundtrip, const char* name, double* residentOut, double* roundtripOut) {
    /* 常驻：数据已留 GPU，只 dispatch */
    double wr[20];
    for (int f = 0; f < 20; f++) {
        WGPUCommandEncoder enc = W.DeviceCreateCommandEncoder(dev, NULL);
        WGPUComputePassEncoder pass = W.CommandEncoderBeginComputePass(enc, NULL);
        W.ComputePassEncoderSetPipeline(pass, k->pipe);
        W.ComputePassEncoderSetBindGroup(pass, 0, k->bg, 0, NULL);
        W.ComputePassEncoderDispatchWorkgroups(pass, groups, 1, 1);
        W.ComputePassEncoderEnd(pass);
        WGPUCommandBuffer cmd = W.CommandEncoderFinish(enc, NULL);
        W.QueueSubmit(q, 1, &cmd);
        W.CommandBufferRelease(cmd); W.CommandEncoderRelease(enc);
    }
    /* 等最后一帧完成 */
    W.DevicePoll(dev, WGPU_TRUE, NULL);
    /* 重新上传初始 pos（常驻测量会累积移动，读回帧从初值开始） */
    if (initPos) W.QueueWriteBuffer(q, k->buffers[0], 0, initPos, posBytes);

    /* 常驻计时（dispatch 仅，无传输） */
    double ws[20];
    for (int f = 0; f < 20; f++) {
        double t0 = now_ms();
        WGPUCommandEncoder enc = W.DeviceCreateCommandEncoder(dev, NULL);
        WGPUComputePassEncoder pass = W.CommandEncoderBeginComputePass(enc, NULL);
        W.ComputePassEncoderSetPipeline(pass, k->pipe);
        W.ComputePassEncoderSetBindGroup(pass, 0, k->bg, 0, NULL);
        W.ComputePassEncoderDispatchWorkgroups(pass, groups, 1, 1);
        W.ComputePassEncoderEnd(pass);
        WGPUCommandBuffer cmd = W.CommandEncoderFinish(enc, NULL);
        W.QueueSubmit(q, 1, &cmd);
        W.CommandBufferRelease(cmd); W.CommandEncoderRelease(enc);
        ws[f] = now_ms() - t0;
    }
    W.DevicePoll(dev, WGPU_TRUE, NULL);

    if (residentOut) *residentOut = median(ws, 20);

    if (!measureRoundtrip) return;

    /* 往返：writeBuffer(输入) + dispatch + submit + poll + map 读回 */
    double wm[20];
    for (int f = 0; f < 20; f++) {
        AsyncWait aw; memset(&aw, 0, sizeof(aw));
        double t0 = now_ms();
        /* 上传当前帧输入（move: pos 重置；closest: query 已在 bind，跳过上传则用 out 读回） */
        if (uploadSrc) W.QueueWriteBuffer(q, k->buffers[0], 0, uploadSrc, uploadBytes);
        WGPUCommandEncoder enc = W.DeviceCreateCommandEncoder(dev, NULL);
        WGPUComputePassEncoder pass = W.CommandEncoderBeginComputePass(enc, NULL);
        W.ComputePassEncoderSetPipeline(pass, k->pipe);
        W.ComputePassEncoderSetBindGroup(pass, 0, k->bg, 0, NULL);
        W.ComputePassEncoderDispatchWorkgroups(pass, groups, 1, 1);
        W.ComputePassEncoderEnd(pass);
        WGPUCommandBuffer cmd = W.CommandEncoderFinish(enc, NULL);
        W.QueueSubmit(q, 1, &cmd);
        W.CommandBufferRelease(cmd); W.CommandEncoderRelease(enc);
        W.DevicePoll(dev, WGPU_TRUE, NULL);          /* 等 kernel 完成 */
        /* wgpu 读回唯一路径：storage 输出 → copyBufferToBuffer → MAP_READ staging → map */
        WGPUCommandEncoder cenc = W.DeviceCreateCommandEncoder(dev, NULL);
        W.CommandEncoderCopyBufferToBuffer(cenc, k->outBuffer, 0, k->staging, 0, k->outSize);
        WGPUCommandBuffer ccmd = W.CommandEncoderFinish(cenc, NULL);
        W.QueueSubmit(q, 1, &ccmd);
        W.CommandBufferRelease(ccmd); W.CommandEncoderRelease(cenc);
        W.DevicePoll(dev, WGPU_TRUE, NULL);
        WGPUBufferMapCallbackInfo mc;
        memset(&mc, 0, sizeof(mc));
        mc.mode = WGPUCallbackMode_AllowProcessEvents;
        mc.callback = onBufferMap; mc.userdata1 = &aw;
        W.BufferMapAsync(k->staging, WGPUMapMode_Read, 0, k->outSize, mc);
        pollDevice(dev, &aw.done);
        if (aw.status == WGPUMapAsyncStatus_Success) {
            W.BufferGetMappedRange(k->staging, 0, k->outSize);
            W.BufferUnmap(k->staging);
        } else {
            printf("  map 失败 status=%d\n", aw.status);
        }
        wm[f] = now_ms() - t0;
    }
    if (roundtripOut) *roundtripOut = median(wm, 20);
    (void)name;
}

/* ================= mapped 直写往返（MAP_WRITE host-visible buffer 直写，对照 cudaHostAlloc） =================
   wgpu 约束：MAP_WRITE 只能与 COPY_SRC 组合（storage 不能 map）→ 上传走 mapped buffer → copyBufferToBuffer → storage。
   优化形态：pos+vel 合并进单块 upload buffer（2*bytes，连续），每帧【一次】map 同步 + 一次连续 memcpy，
   替代旧版每帧两次 BufferMapAsync+pollDevice（pos/vel 各一）——那两次同步等待是纯冗余。
   queueWriteBuffer 内部已是持久映射 host buffer（页锁定级上传），此路径只作对照，见 docs/gpu/15。 */
static double wgpuPinnedRoundtrip(WGPUQueue q, WGPUDevice dev,
    WGPUBuffer up, WGPUBuffer posSto, WGPUBuffer velSto, WGPUBuffer outSto, WGPUBuffer staging,
    WGPUComputePipeline pipe, WGPUBindGroup bg, uint32_t groups, size_t bytes,
    const float2* initPos, const float2* initVel, const char* name) {
    double ws[MOVE_FRAMES];
    for (int f = 0; f < MOVE_FRAMES; f++) {
        double t0 = now_ms();
        /* 上传 pos+vel 合并：一次 map → 连续 memcpy 两段（pos 在前 vel 在后）→ 一次 unmap */
        AsyncWait aw; memset(&aw, 0, sizeof(aw));
        WGPUBufferMapCallbackInfo mc; memset(&mc, 0, sizeof(mc));
        mc.mode = WGPUCallbackMode_AllowProcessEvents;
        mc.callback = onBufferMap; mc.userdata1 = &aw;
        W.BufferMapAsync(up, WGPUMapMode_Write, 0, 2 * bytes, mc);
        pollDevice(dev, &aw.done);
        if (aw.status == WGPUMapAsyncStatus_Success) {
            char* p = (char*)W.BufferGetMappedRange(up, 0, 2 * bytes);
            memcpy(p, initPos, bytes);
            memcpy(p + bytes, initVel, bytes);
            W.BufferUnmap(up);
        }
        /* copy 上传 → storage + dispatch */
        WGPUCommandEncoder enc = W.DeviceCreateCommandEncoder(dev, NULL);
        W.CommandEncoderCopyBufferToBuffer(enc, up, 0, posSto, 0, bytes);
        W.CommandEncoderCopyBufferToBuffer(enc, up, bytes, velSto, 0, bytes);
        WGPUComputePassEncoder pass = W.CommandEncoderBeginComputePass(enc, NULL);
        W.ComputePassEncoderSetPipeline(pass, pipe);
        W.ComputePassEncoderSetBindGroup(pass, 0, bg, 0, NULL);
        W.ComputePassEncoderDispatchWorkgroups(pass, groups, 1, 1);
        W.ComputePassEncoderEnd(pass);
        WGPUCommandBuffer cmd = W.CommandEncoderFinish(enc, NULL);
        W.QueueSubmit(q, 1, &cmd);
        W.CommandBufferRelease(cmd); W.CommandEncoderRelease(enc);
        W.DevicePoll(dev, WGPU_TRUE, NULL);
        /* 读回：storage → MAP_READ staging → map */
        WGPUCommandEncoder cenc = W.DeviceCreateCommandEncoder(dev, NULL);
        W.CommandEncoderCopyBufferToBuffer(cenc, outSto, 0, staging, 0, bytes);
        WGPUCommandBuffer ccmd = W.CommandEncoderFinish(cenc, NULL);
        W.QueueSubmit(q, 1, &ccmd);
        W.CommandBufferRelease(ccmd); W.CommandEncoderRelease(cenc);
        W.DevicePoll(dev, WGPU_TRUE, NULL);
        memset(&aw, 0, sizeof(aw));
        mc.userdata1 = &aw;
        W.BufferMapAsync(staging, WGPUMapMode_Read, 0, bytes, mc);
        pollDevice(dev, &aw.done);
        if (aw.status == WGPUMapAsyncStatus_Success) {
            W.BufferGetMappedRange(staging, 0, bytes);
            W.BufferUnmap(staging);
        }
        ws[f] = now_ms() - t0;
    }
    (void)name;
    return median(ws, MOVE_FRAMES);
}

/* ================= 入口 ================= */
void RunWgpuProbe(void) {
    printf("\n===== wgpu-native v29（WebGPU C ABI；桌面 Vulkan/D3D12/Metal） =====\n");
    static const char* paths[] = {
        "wgpu.dll",
        "..\\..\\..\\..\\bin\\wgpu.dll",
        "..\\..\\wgpu\\wgpu.dll",
        "E:\\GODOT\\Project\\EntJoy\\bin\\wgpu.dll",
    };
    if (LoadWgpu(paths[0]) != 0) {
        /* 备选路径 */
        int ok = 0;
        for (int i = 1; i < 4 && !ok; i++) ok = (LoadWgpu(paths[i]) == 0);
        if (!ok) {
            printf("  未找到 wgpu.dll。已在 src/GpuNativeProbe/wgpu/ 下载 wgpu-native v29.0.1.1\n");
            printf("  （wgpu_native.dll 已复制为 wgpu.dll，桌面直接走 D3D12/Vulkan，无需浏览器）\n");
            return;
        }
    }

    WGPUInstanceDescriptor idesc; memset(&idesc, 0, sizeof(idesc));
    WGPUInstance inst = W.CreateInstance(&idesc);
    if (!inst) { printf("  wgpuCreateInstance 失败\n"); return; }

    WGPURequestAdapterOptions aopts; memset(&aopts, 0, sizeof(aopts));
    AsyncWait aw; memset(&aw, 0, sizeof(aw));
    WGPURequestAdapterCallbackInfo ainfo; memset(&ainfo, 0, sizeof(ainfo));
    ainfo.mode = WGPUCallbackMode_AllowProcessEvents;
    ainfo.callback = onRequestAdapter; ainfo.userdata1 = &aw;
    W.InstanceRequestAdapter(inst, &aopts, ainfo);
    pollInstance(inst, &aw.done);
    if (!aw.done) { printf("  requestAdapter 超时\n"); W.InstanceRelease(inst); return; }
    WGPUAdapter adapter = aw.adapter;
    if (!adapter) { printf("  无适配器（status=%d）\n", aw.status); W.InstanceRelease(inst); return; }

    memset(&aw, 0, sizeof(aw));
    WGPURequestDeviceCallbackInfo dinfo; memset(&dinfo, 0, sizeof(dinfo));
    dinfo.mode = WGPUCallbackMode_AllowProcessEvents;
    dinfo.callback = onRequestDevice; dinfo.userdata1 = &aw;
    W.AdapterRequestDevice(adapter, NULL, dinfo);
    pollInstance(inst, &aw.done);
    if (!aw.done) { printf("  requestDevice 超时\n"); W.AdapterRelease(adapter); W.InstanceRelease(inst); return; }
    WGPUDevice dev = aw.device;
    if (!dev) { printf("  requestDevice 失败\n"); return; }
    WGPUQueue queue = W.DeviceGetQueue(dev);
    printf("  device 就绪（wgpu-native v29, D3D12/Vulkan）\n");

    /* ============ Move @1M（Heavy + Light） ============ */
    float2* mPos = (float2*)malloc(MOVE_N * sizeof(float2));
    float2* mVel = (float2*)malloc(MOVE_N * sizeof(float2));
    float2* mPosInit = (float2*)malloc(MOVE_N * sizeof(float2));
    g_rng = 1234;
    for (int i = 0; i < MOVE_N; i++) {
        mPos[i].x = (float)(rnd() * 200 - 100); mPos[i].y = (float)(rnd() * 200 - 100);
        mVel[i].x = (float)(rnd() * 200 - 100); mVel[i].y = (float)(rnd() * 200 - 100);
    }
    memcpy(mPosInit, mPos, MOVE_N * sizeof(float2));

    /* storage 输出（kernel 读写）；COPY_DST 供 queueWriteBuffer 重置，COPY_SRC 供 copy 到 staging 读回 */
    WGPUBuffer dPos = makeBuffer(dev, WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst | WGPUBufferUsage_CopySrc, MOVE_N * 8);
    WGPUBuffer dVel = makeBuffer(dev, WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst, MOVE_N * 8);
    W.QueueWriteBuffer(queue, dPos, 0, mPos, MOVE_N * 8);
    W.QueueWriteBuffer(queue, dVel, 0, mVel, MOVE_N * 8);
    W.DevicePoll(dev, WGPU_TRUE, NULL);

    struct { const char* name; const char* wgsl; } loads[2] = {
        { "HeavyMove", HEAVY_WGSL }, { "LightMove", LIGHT_WGSL },
    };
    double mRes[2], mRt[2];
    for (int li = 0; li < 2; li++) {
        WGPUShaderModule sm = makeShader(dev, loads[li].wgsl);
        /* bind group layout: binding0=pos(Storage read_write), binding1=vel(Storage read) */
        WGPUBindGroupLayoutEntry entries[2];
        memset(&entries, 0, sizeof(entries));
        entries[0].binding = 0;
        entries[0].visibility = WGPUShaderStage_Compute;
        entries[0].buffer.type = WGPUBufferBindingType_Storage;
        entries[0].buffer.hasDynamicOffset = WGPU_FALSE;
        entries[1].binding = 1;
        entries[1].visibility = WGPUShaderStage_Compute;
        entries[1].buffer.type = WGPUBufferBindingType_ReadOnlyStorage;
        entries[1].buffer.hasDynamicOffset = WGPU_FALSE;
        WGPUBindGroupLayoutDescriptor bld; memset(&bld, 0, sizeof(bld));
        bld.entryCount = 2; bld.entries = entries;
        WGPUBindGroupLayout bgl = W.DeviceCreateBindGroupLayout(dev, &bld);

        WGPUPipelineLayoutDescriptor pld; memset(&pld, 0, sizeof(pld));
        WGPUBindGroupLayout bgls[1] = { bgl };
        pld.bindGroupLayoutCount = 1; pld.bindGroupLayouts = bgls;
        WGPUPipelineLayout pl = W.DeviceCreatePipelineLayout(dev, &pld);

        WGPUBindGroupEntry be[2];
        memset(&be, 0, sizeof(be));
        be[0].binding = 0; be[0].buffer = dPos; be[0].size = MOVE_N * 8;
        be[1].binding = 1; be[1].buffer = dVel; be[1].size = MOVE_N * 8;
        WGPUBindGroupDescriptor bgd; memset(&bgd, 0, sizeof(bgd));
        bgd.layout = bgl; bgd.entryCount = 2; bgd.entries = be;
        WGPUBindGroup bg = W.DeviceCreateBindGroup(dev, &bgd);

        WGPUShaderModule smods[1] = { sm };
        WGPUComputeState cs; memset(&cs, 0, sizeof(cs));
        cs.module = sm;
        const char* ep = "main";
        cs.entryPoint.data = ep; cs.entryPoint.length = 4;
        WGPUComputePipelineDescriptor cpd; memset(&cpd, 0, sizeof(cpd));
        cpd.layout = pl; cpd.compute = cs;
        WGPUComputePipeline pipe = W.DeviceCreateComputePipeline(dev, &cpd);

        Kernel k; memset(&k, 0, sizeof(k));
        k.pipe = pipe; k.bg = bg; k.bgl = bgl; k.pl = pl;
        k.buffers = &dPos; k.bufferCount = 2; k.outSize = MOVE_N * 8; k.outBuffer = dPos;
        k.staging = makeBuffer(dev, WGPUBufferUsage_MapRead | WGPUBufferUsage_CopyDst, MOVE_N * 8);

        runCompute(&k, queue, dev, (MOVE_N + 255) / 256, mPos, MOVE_N * 8, mPosInit, MOVE_N * 8, 1, loads[li].name, &mRes[li], &mRt[li]);
        printf("  %-10s 常驻 : %8.3f ms  往返(queueWriteBuffer) : %8.3f ms\n", loads[li].name, mRes[li], mRt[li]);

        /* mapped 直写往返（对照）：单块 upload buffer 合并 pos+vel（2*bytes），每帧一次 map */
        WGPUBuffer upBuf = makeBuffer(dev, WGPUBufferUsage_MapWrite | WGPUBufferUsage_CopySrc, MOVE_N * 16);
        double mPin = wgpuPinnedRoundtrip(queue, dev, upBuf, dPos, dVel, dPos, k.staging,
            pipe, bg, (MOVE_N + 255) / 256, MOVE_N * 8, mPos, mVel, loads[li].name);
        printf("  %-10s 往返 mapped直写(单buffer)   : %8.3f ms\n", loads[li].name, mPin);
        W.BufferDestroy(upBuf);
        /* 汇总到全局（main 最后打印直观对比表） */
        if (li == 0) { g_sum.heavyResident[1] = mRes[0]; g_sum.heavyRoundtrip[1] = mRt[0]; g_sum.heavyPinned[1] = mPin; }
        else         { g_sum.lightResident[1] = mRes[1]; g_sum.lightRoundtrip[1] = mRt[1]; g_sum.lightPinned[1] = mPin; }

        W.BufferDestroy(k.staging);
        W.ComputePipelineRelease(pipe); W.BindGroupRelease(bg);
        W.PipelineLayoutRelease(pl); W.BindGroupLayoutRelease(bgl); W.ShaderModuleRelease(sm);
    }

    /* ============ GridSearch closest @100k ============ */
    float2* gpos = (float2*)malloc(N * sizeof(float2));
    float2* gqry = (float2*)malloc(K * sizeof(float2));
    float2* gsorted = (float2*)malloc(N * sizeof(float2));
    int2* ghashIdx = (int2*)malloc(N * sizeof(int2));
    int2* gcellSE = (int2*)malloc(CELL * sizeof(int2));
    int* gresults = (int*)malloc(K * sizeof(int));
    g_rng = 1234;
    for (int i = 0; i < N; i++) { gpos[i].x = (float)(rnd() * 200 - 100); gpos[i].y = (float)(rnd() * 200 - 100); }
    for (int i = 0; i < K; i++) { gqry[i].x = (float)(rnd() * 200 - 100); gqry[i].y = (float)(rnd() * 200 - 100); }
    int* gcounts = (int*)calloc(CELL, sizeof(int));
    for (int i = 0; i < N; i++) gcounts[hash_of(gpos[i])]++;
    int gsum = 0;
    for (int c = 0; c < CELL; c++) { int cnt = gcounts[c]; gcounts[c] = gsum; gsum += cnt; }
    for (int c = 0; c < CELL; c++) {
        int st = gcounts[c]; int en = (c + 1 < CELL) ? gcounts[c + 1] : N;
        gcellSE[c].x = (st == en) ? -1 : st; gcellSE[c].y = (st == en) ? -1 : en;
    }
    for (int i = 0; i < N; i++) {
        int h = hash_of(gpos[i]);
        int dest = gcounts[h]++;
        gsorted[dest] = gpos[i]; ghashIdx[dest].x = h; ghashIdx[dest].y = i;
    }

    WGPUBuffer dQuery = makeBuffer(dev, WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst, K * 8);
    WGPUBuffer dHash = makeBuffer(dev, WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst, N * 8);
    WGPUBuffer dCell = makeBuffer(dev, WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst, CELL * 8);
    WGPUBuffer dSorted = makeBuffer(dev, WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst, N * 8);
    WGPUBuffer dRes = makeBuffer(dev, WGPUBufferUsage_Storage | WGPUBufferUsage_CopySrc, K * 4);
    W.QueueWriteBuffer(queue, dQuery, 0, gqry, K * 8);
    W.QueueWriteBuffer(queue, dHash, 0, ghashIdx, N * 8);
    W.QueueWriteBuffer(queue, dCell, 0, gcellSE, CELL * 8);
    W.QueueWriteBuffer(queue, dSorted, 0, gsorted, N * 8);
    W.DevicePoll(dev, WGPU_TRUE, NULL);

    {
        WGPUShaderModule sm = makeShader(dev, CLOSEST_WGSL);
        WGPUBindGroupLayoutEntry entries[5];
        memset(&entries, 0, sizeof(entries));
        WGPUBufferBindingType btypes[5] = {
            WGPUBufferBindingType_ReadOnlyStorage, WGPUBufferBindingType_ReadOnlyStorage,
            WGPUBufferBindingType_ReadOnlyStorage, WGPUBufferBindingType_ReadOnlyStorage,
            WGPUBufferBindingType_Storage,
        };
        for (int i = 0; i < 5; i++) {
            entries[i].binding = i;
            entries[i].visibility = WGPUShaderStage_Compute;
            entries[i].buffer.type = btypes[i];
            entries[i].buffer.hasDynamicOffset = WGPU_FALSE;
        }
        WGPUBindGroupLayoutDescriptor bld; memset(&bld, 0, sizeof(bld));
        bld.entryCount = 5; bld.entries = entries;
        WGPUBindGroupLayout bgl = W.DeviceCreateBindGroupLayout(dev, &bld);

        WGPUPipelineLayoutDescriptor pld; memset(&pld, 0, sizeof(pld));
        WGPUBindGroupLayout bgls[1] = { bgl };
        pld.bindGroupLayoutCount = 1; pld.bindGroupLayouts = bgls;
        WGPUPipelineLayout pl = W.DeviceCreatePipelineLayout(dev, &pld);

        WGPUBuffer bufs[5] = { dQuery, dHash, dCell, dSorted, dRes };
        WGPUBindGroupEntry be[5];
        memset(&be, 0, sizeof(be));
        size_t sizes[5] = { K * 8, N * 8, CELL * 8, N * 8, K * 4 };
        for (int i = 0; i < 5; i++) { be[i].binding = i; be[i].buffer = bufs[i]; be[i].size = sizes[i]; }
        WGPUBindGroupDescriptor bgd; memset(&bgd, 0, sizeof(bgd));
        bgd.layout = bgl; bgd.entryCount = 5; bgd.entries = be;
        WGPUBindGroup bg = W.DeviceCreateBindGroup(dev, &bgd);

        WGPUComputeState cs; memset(&cs, 0, sizeof(cs));
        cs.module = sm;
        const char* ep = "main";
        cs.entryPoint.data = ep; cs.entryPoint.length = 4;
        WGPUComputePipelineDescriptor cpd; memset(&cpd, 0, sizeof(cpd));
        cpd.layout = pl; cpd.compute = cs;
        WGPUComputePipeline pipe = W.DeviceCreateComputePipeline(dev, &cpd);

        Kernel k; memset(&k, 0, sizeof(k));
        k.pipe = pipe; k.bg = bg; k.bgl = bgl; k.pl = pl;
        k.buffers = bufs; k.bufferCount = 5; k.outSize = K * 4; k.outBuffer = dRes;
        k.staging = makeBuffer(dev, WGPUBufferUsage_MapRead | WGPUBufferUsage_CopyDst, K * 4);

        double cres, crt;
        runCompute(&k, queue, dev, (K + 255) / 256, gqry, K * 8, NULL, 0, 1, "GridSearch", &cres, &crt);

        /* sanity：读回 staging 有非 -1 计数（roundtrip 最后一帧已 copy） */
        AsyncWait aw2; memset(&aw2, 0, sizeof(aw2));
        WGPUBufferMapCallbackInfo mc; memset(&mc, 0, sizeof(mc));
        mc.mode = WGPUCallbackMode_AllowProcessEvents;
        mc.callback = onBufferMap; mc.userdata1 = &aw2;
        W.BufferMapAsync(k.staging, WGPUMapMode_Read, 0, K * 4, mc);
        pollDevice(dev, &aw2.done);
        int found = -1;
        if (aw2.status == WGPUMapAsyncStatus_Success) {
            int* mapped = (int*)W.BufferGetMappedRange(k.staging, 0, K * 4);
            int f = 0;
            for (int i = 0; i < K; i++) if (mapped[i] != -1) f++;
            found = f;
            W.BufferUnmap(k.staging);
        }
        printf("  GridSearch@100k closest 常驻 : %8.3f ms  往返 : %8.3f ms  sanity: 有结果 %d/%d\n",
            cres, crt, found, K);
        printf("  对照 native OpenCL 常驻 ~0.03 / 往返 ~0.35-0.45；ILGPU CUDA 常驻 0.151 / 往返(页锁定) 0.311\n");
        /* 汇总到全局 */
        g_sum.gridResident[1] = cres;
        g_sum.gridRoundtrip[1] = crt;
        g_sum.gridPinned[1] = -1;    /* wgpu 无 GridSearch mapped 路径（query 仅 800KB，mapped 无意义） */
        g_sum.gridMismatch[1] = -1;  /* 仅 sanity，未逐元素 parity */

        W.BufferDestroy(k.staging);
        W.ComputePipelineRelease(pipe); W.BindGroupRelease(bg);
        W.PipelineLayoutRelease(pl); W.BindGroupLayoutRelease(bgl); W.ShaderModuleRelease(sm);
    }

    W.BufferDestroy(dPos); W.BufferDestroy(dVel);
    W.BufferDestroy(dQuery); W.BufferDestroy(dHash); W.BufferDestroy(dCell);
    W.BufferDestroy(dSorted); W.BufferDestroy(dRes);
    W.DeviceRelease(dev); W.AdapterRelease(adapter); W.InstanceRelease(inst);
    free(mPos); free(mVel); free(mPosInit);
    free(gpos); free(gqry); free(gsorted); free(ghashIdx); free(gcellSE); free(gresults); free(gcounts);
}
