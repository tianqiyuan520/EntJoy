// ============================================================
// GpuCompute.cpp — NativeDll 的 wgpu compute 执行后端实现。
//   动态加载 wgpu_native.dll（LoadLibrary + GetProcAddress 函数表，
//   镜像 GpuNativeProbe/wgpu_probe.cpp），v29 异步回调 + DevicePoll 轮询同步。
//   数据面（buffer 生命周期、staging 回读）归 C++，C# 只持句柄。
// ============================================================
#include "GpuCompute.h"

#include <windows.h>
#include <string.h>
#include <stdio.h>
#include <stdarg.h>
#include <vector>

#include "thirdParty/wgpu/include/webgpu/wgpu.h"

// ---------------- 函数表（GetProcAddress；定义见 GpuCompute.h 的 WgpuApi） ----------------

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
    WP(CommandBufferRelease); WP(CommandEncoderRelease); WP(ComputePassEncoderRelease);
    WP(ShaderModuleRelease);
    WP(ComputePipelineRelease); WP(BindGroupRelease); WP(BindGroupLayoutRelease); WP(PipelineLayoutRelease);
    WP(QueueRelease);
    if (!W.CreateInstance || !W.InstanceRequestAdapter || !W.AdapterRequestDevice || !W.DeviceCreateBuffer ||
        !W.BufferMapAsync || !W.DeviceGetQueue || !W.QueueSubmit || !W.QueueWriteBuffer || !W.DevicePoll ||
        !W.DeviceCreateShaderModule || !W.DeviceCreateComputePipeline || !W.DeviceCreateBindGroup ||
        !W.DeviceCreateBindGroupLayout || !W.DeviceCreatePipelineLayout || !W.DeviceCreateCommandEncoder ||
        !W.CommandEncoderBeginComputePass || !W.CommandEncoderFinish || !W.BufferGetMappedRange || !W.BufferUnmap) {
        return 1;
    }
    return 0;
}
#undef WP

// ---------------- 全局状态（定义；extern 声明见 GpuCompute.h，供 GpuResidency.cpp 复用） ----------------

WgpuApi W;
WGPUInstance g_instance = nullptr;
WGPUAdapter  g_adapter = nullptr;
WGPUDevice   g_device = nullptr;
WGPUQueue    g_queue = nullptr;

// 错误文本（单提交线程语义；多线程接入时改为 TLS/加锁）
static char g_lastError[1024] = { 0 };
static void setError(const char* fmt, ...) {
    va_list ap; va_start(ap, fmt);
    vsnprintf(g_lastError, sizeof(g_lastError), fmt, ap);
    va_end(ap);
}
static void clearError() { g_lastError[0] = 0; }

// ---------------- 异步回调等待（v29：AllowProcessEvents + 轮询） ----------------

struct AsyncWait { volatile int done = 0; volatile int status = 0; WGPUAdapter adapter = nullptr; WGPUDevice device = nullptr; };

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
static void onUncapturedError(const WGPUDevice* device, WGPUErrorType type, WGPUStringView message, void* u1, void* u2) {
    if (message.data && message.length > 0 && message.length < sizeof(g_lastError) - 1) {
        memcpy(g_lastError, message.data, message.length);
        g_lastError[message.length] = 0;
    } else {
        snprintf(g_lastError, sizeof(g_lastError), "wgpu error type=%d", (int)type);
    }
}

static void pollInstance(WGPUInstance inst, volatile int* done) {
    for (int i = 0; !(*done) && i < 5000000; i++) {
        W.InstanceProcessEvents(inst);
        if (!(*done)) Sleep(0);
    }
}
static void pollDevice(WGPUDevice dev, volatile int* done) {
    for (int i = 0; !(*done) && i < 5000000; i++) {
        W.DevicePoll(dev, WGPU_FALSE, NULL);
        if (!(*done)) Sleep(0);
    }
}

// ---------------- 通用创建辅助 ----------------

// GpuKernel 结构定义见 GpuCompute.h

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

// ---------------- 导出实现 ----------------

extern "C" {

JOB_API int GpuCompute_Initialize(const char* wgpuDllPath) {
    clearError();
    if (g_device) return 1; // 已初始化

    const char* path = wgpuDllPath ? wgpuDllPath : "wgpu_native.dll";
    if (LoadWgpu(path)) { setError("加载 wgpu_native.dll 失败或缺少导出: %s", path); return 0; }

    // 后端选择：默认全部（Windows 上 wgpu 选 D3D12）。
    // 实验结论（2026-08-16）：强制 Vulkan 无优势——READBACK mapped 带宽（8-12GB/s）
    // 是 GPU 平台普遍特性，Vulkan 回读/常驻 dispatch 均不优于 D3D12 → 保持默认。
    g_instance = W.CreateInstance(nullptr);
    if (!g_instance) { setError("wgpuCreateInstance 失败"); return 0; }
    // adapter（null options = 全后端）
    {
        AsyncWait aw;
        WGPURequestAdapterCallbackInfo info;
        memset(&info, 0, sizeof(info));
        info.mode = WGPUCallbackMode_AllowProcessEvents;
        info.callback = onRequestAdapter;
        info.userdata1 = &aw;
        W.InstanceRequestAdapter(g_instance, nullptr, info);
        pollInstance(g_instance, &aw.done);
        if (!aw.done || aw.status != WGPURequestAdapterStatus_Success || !aw.adapter) {
            setError("wgpuInstanceRequestAdapter 失败 status=%d", aw.status); return 0;
        }
        g_adapter = aw.adapter;
    }

    // device（descriptor 携带 uncaptured-error 回调 → GpuCompute_GetLastError）
    {
        AsyncWait aw;
        WGPUDeviceDescriptor dd;
        memset(&dd, 0, sizeof(dd));
        dd.uncapturedErrorCallbackInfo.callback = onUncapturedError;
        WGPURequestDeviceCallbackInfo info;
        memset(&info, 0, sizeof(info));
        info.mode = WGPUCallbackMode_AllowProcessEvents;
        info.callback = onRequestDevice;
        info.userdata1 = &aw;
        W.AdapterRequestDevice(g_adapter, &dd, info);
        pollInstance(g_instance, &aw.done);
        if (!aw.done || aw.status != WGPURequestDeviceStatus_Success || !aw.device) {
            setError("wgpuAdapterRequestDevice 失败 status=%d", aw.status); return 0;
        }
        g_device = aw.device;
    }

    g_queue = W.DeviceGetQueue(g_device);
    if (!g_queue) { setError("wgpuDeviceGetQueue 失败"); return 0; }
    return 1;
}

JOB_API void GpuCompute_Shutdown() {
    if (g_queue)   { W.QueueRelease(g_queue); g_queue = nullptr; }
    if (g_device)  { W.DeviceRelease(g_device); g_device = nullptr; }
    if (g_adapter) { W.AdapterRelease(g_adapter); g_adapter = nullptr; }
    if (g_instance){ W.InstanceRelease(g_instance); g_instance = nullptr; }
    if (W.module)  { FreeLibrary(W.module); W.module = nullptr; }
    memset(&W, 0, sizeof(W));
    clearError();
}

JOB_API const char* GpuCompute_GetLastError() {
    return g_lastError[0] ? g_lastError : nullptr;
}

JOB_API void* GpuCompute_CreateKernel(const char* wgsl, int storageBindingCount, int hasUniform) {
    return GpuCompute_CreateKernelEx(wgsl, storageBindingCount, hasUniform, "main");
}

JOB_API void* GpuCompute_CreateKernelEx(const char* wgsl, int storageBindingCount, int hasUniform, const char* entryPoint) {
    clearError();
    if (!g_device) { setError("GpuCompute 未初始化"); return nullptr; }
    if (!entryPoint) entryPoint = "main";

    GpuKernel* k = new GpuKernel();
    k->module = makeShader(g_device, wgsl);
    if (!k->module) { setError("createShaderModule 失败（naga 拒绝 WGSL，见错误回调）"); delete k; return nullptr; }

    // bind group layout：storageBindingCount 个 storage(read_write) + 可选 uniform
    int entryCount = storageBindingCount + (hasUniform ? 1 : 0);
    std::vector<WGPUBindGroupLayoutEntry> entries((size_t)entryCount);
    for (int i = 0; i < storageBindingCount; i++) {
        WGPUBindGroupLayoutEntry& e = entries[i];
        memset(&e, 0, sizeof(e));
        e.binding = (uint32_t)i;
        e.visibility = WGPUShaderStage_Compute;
        e.buffer.type = WGPUBufferBindingType_Storage;
    }
    if (hasUniform) {
        WGPUBindGroupLayoutEntry& e = entries[storageBindingCount];
        memset(&e, 0, sizeof(e));
        e.binding = (uint32_t)storageBindingCount;
        e.visibility = WGPUShaderStage_Compute;
        e.buffer.type = WGPUBufferBindingType_Uniform;
    }
    WGPUBindGroupLayoutDescriptor bglDesc;
    memset(&bglDesc, 0, sizeof(bglDesc));
    bglDesc.entryCount = (size_t)entryCount;
    bglDesc.entries = entries.data();
    k->bgl = W.DeviceCreateBindGroupLayout(g_device, &bglDesc);
    if (!k->bgl) { setError("createBindGroupLayout 失败"); W.ShaderModuleRelease(k->module); delete k; return nullptr; }

    WGPUPipelineLayoutDescriptor plDesc;
    memset(&plDesc, 0, sizeof(plDesc));
    WGPUBindGroupLayout layouts[1] = { k->bgl };
    plDesc.bindGroupLayoutCount = 1;
    plDesc.bindGroupLayouts = layouts;
    k->pl = W.DeviceCreatePipelineLayout(g_device, &plDesc);
    if (!k->pl) { setError("createPipelineLayout 失败"); W.ShaderModuleRelease(k->module); W.BindGroupLayoutRelease(k->bgl); delete k; return nullptr; }

    // entryPoint 必须显式提供（v29 空 entryPoint 崩溃）
    WGPUComputePipelineDescriptor cpDesc;
    memset(&cpDesc, 0, sizeof(cpDesc));
    cpDesc.layout = k->pl;
    cpDesc.compute.module = k->module;
    cpDesc.compute.entryPoint.data = entryPoint;
    cpDesc.compute.entryPoint.length = strlen(entryPoint);
    k->pipe = W.DeviceCreateComputePipeline(g_device, &cpDesc);
    if (!k->pipe) {
        setError("createComputePipeline 失败（kernel 绑定与 WGSL 绑定不匹配？）");
        W.ShaderModuleRelease(k->module); W.BindGroupLayoutRelease(k->bgl); W.PipelineLayoutRelease(k->pl);
        delete k; return nullptr;
    }
    return k;
}

JOB_API void GpuCompute_ReleaseKernel(void* kernel) {
    GpuKernel* k = (GpuKernel*)kernel;
    if (!k) return;
    if (k->pipe)   W.ComputePipelineRelease(k->pipe);
    if (k->pl)     W.PipelineLayoutRelease(k->pl);
    if (k->bgl)    W.BindGroupLayoutRelease(k->bgl);
    if (k->module) W.ShaderModuleRelease(k->module);
    delete k;
}

JOB_API void* GpuCompute_CreateStorageBuffer(unsigned long long size) {
    return makeBuffer(g_device, WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst | WGPUBufferUsage_CopySrc, (size_t)size);
}

JOB_API void* GpuCompute_CreateUniformBuffer(unsigned long long size) {
    return makeBuffer(g_device, WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst, (size_t)size);
}

JOB_API void GpuCompute_WriteBuffer(void* buffer, const void* data, unsigned long long size) {
    W.QueueWriteBuffer(g_queue, (WGPUBuffer)buffer, 0, data, (size_t)size);
}

JOB_API void GpuCompute_ReleaseBuffer(void* buffer) {
    if (buffer) W.BufferDestroy((WGPUBuffer)buffer);
}

JOB_API void* GpuCompute_CreateBindGroup(void* kernel, void* const* buffers,
                                         const unsigned long long* sizes, int bufferCount) {
    GpuKernel* k = (GpuKernel*)kernel;
    if (!k || bufferCount <= 0) return nullptr;
    std::vector<WGPUBindGroupEntry> entries((size_t)bufferCount);
    for (int i = 0; i < bufferCount; i++) {
        WGPUBindGroupEntry& e = entries[i];
        memset(&e, 0, sizeof(e));
        e.binding = (uint32_t)i;
        e.buffer = (WGPUBuffer)buffers[i];
        e.size = sizes[i];
    }
    WGPUBindGroupDescriptor desc;
    memset(&desc, 0, sizeof(desc));
    desc.layout = k->bgl;
    desc.entryCount = (size_t)bufferCount;
    desc.entries = entries.data();
    return W.DeviceCreateBindGroup(g_device, &desc);
}

JOB_API void GpuCompute_ReleaseBindGroup(void* group) {
    if (group) W.BindGroupRelease((WGPUBindGroup)group);
}

JOB_API void GpuCompute_Dispatch(void* kernel, void* bindGroup, unsigned int workgroupX) {
    GpuKernel* k = (GpuKernel*)kernel;
    if (!k) return;
    WGPUCommandEncoder enc = W.DeviceCreateCommandEncoder(g_device, nullptr);
    WGPUComputePassEncoder pass = W.CommandEncoderBeginComputePass(enc, nullptr);
    W.ComputePassEncoderSetPipeline(pass, k->pipe);
    W.ComputePassEncoderSetBindGroup(pass, 0, (WGPUBindGroup)bindGroup, 0, nullptr);
    W.ComputePassEncoderDispatchWorkgroups(pass, workgroupX, 1, 1);
    W.ComputePassEncoderEnd(pass);
    WGPUCommandBuffer cmd = W.CommandEncoderFinish(enc, nullptr);
    W.QueueSubmit(g_queue, 1, &cmd);
    W.ComputePassEncoderRelease(pass);
    W.CommandEncoderRelease(enc);
    W.CommandBufferRelease(cmd);
}

JOB_API void GpuCompute_Sync() {
    if (g_device) W.DevicePoll(g_device, WGPU_TRUE, nullptr);
}

JOB_API int GpuCompute_ReadBack(void* buffer, void* outData, unsigned long long size) {
    clearError();
    WGPUBuffer storage = (WGPUBuffer)buffer;
    WGPUBuffer staging = makeBuffer(g_device, WGPUBufferUsage_MapRead | WGPUBufferUsage_CopyDst, (size_t)size);
    if (!staging) { setError("创建 staging buffer 失败"); return 0; }

    WGPUCommandEncoder enc = W.DeviceCreateCommandEncoder(g_device, nullptr);
    W.CommandEncoderCopyBufferToBuffer(enc, storage, 0, staging, 0, (size_t)size);
    WGPUCommandBuffer cmd = W.CommandEncoderFinish(enc, nullptr);
    W.QueueSubmit(g_queue, 1, &cmd);
    W.CommandBufferRelease(cmd);
    W.CommandEncoderRelease(enc);
    // 不在此 wait：mapAsync 后的 DevicePoll(wait) 一并等 copy + 触发 map 回调

    AsyncWait aw;
    WGPUBufferMapCallbackInfo info;
    memset(&info, 0, sizeof(info));
    info.mode = WGPUCallbackMode_AllowProcessEvents;
    info.callback = onBufferMap;
    info.userdata1 = &aw;
    W.BufferMapAsync(staging, WGPUMapMode_Read, 0, (size_t)size, info);
    // wait=true 等队列（copy 已提交）+ process events → 直接触发 map 回调，省忙等固定延迟
    W.DevicePoll(g_device, WGPU_TRUE, nullptr);
    if (!aw.done) pollDevice(g_device, &aw.done);
    if (!aw.done || aw.status != WGPUMapAsyncStatus_Success) {
        setError("mapAsync 失败 status=%d", aw.status);
        W.BufferDestroy(staging);
        return 0;
    }
    void* mapped = W.BufferGetMappedRange(staging, 0, (size_t)size);
    if (mapped) memcpy(outData, mapped, (size_t)size);
    W.BufferUnmap(staging);
    W.BufferDestroy(staging);
    return mapped ? 1 : 0;
}

} // extern "C"
