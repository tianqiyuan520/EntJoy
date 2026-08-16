// ============================================================
// GpuCompute.h — NativeDll 的 wgpu compute 执行后端（C ABI 导出）。
//   wgpu-native v29 作为 thirdParty 集成（src/NativeDll/thirdParty/wgpu），
//   动态加载 wgpu_native.dll（镜像 GpuNativeProbe/wgpu_probe.cpp 的函数表方式）。
//   C# 侧只做薄 P/Invoke；GPU 提交 + 数据面（buffer 常驻/脏同步/staging）由
//   C++ 拥有 —— 这是 GpuResidencyManager（docs/17 §八）的执行层雏形。
//   句柄均为指针；失败返回 NULL/0，错误文本经 GpuCompute_GetLastError 读取。
// ============================================================
#pragma once

#include <windows.h>
#include "Exports.h"
#include "thirdParty/wgpu/include/webgpu/wgpu.h"

// ---------------- 共享内部状态（GpuCompute.cpp 定义，GpuResidency.cpp 复用） ----------------

/// wgpu 函数表（GetProcAddress 加载，GpuCompute.cpp 定义）
struct WgpuApi {
    HMODULE module = nullptr;
    WGPUInstance (*CreateInstance)(const WGPUInstanceDescriptor*) = nullptr;
    WGPUFuture (*InstanceRequestAdapter)(WGPUInstance, const WGPURequestAdapterOptions*, WGPURequestAdapterCallbackInfo) = nullptr;
    WGPUBool (*DevicePoll)(WGPUDevice, WGPUBool, const WGPUSubmissionIndex*) = nullptr;
    WGPUFuture (*AdapterRequestDevice)(WGPUAdapter, const WGPUDeviceDescriptor*, WGPURequestDeviceCallbackInfo) = nullptr;
    WGPUBuffer (*DeviceCreateBuffer)(WGPUDevice, const WGPUBufferDescriptor*) = nullptr;
    void (*BufferDestroy)(WGPUBuffer) = nullptr;
    WGPUFuture (*BufferMapAsync)(WGPUBuffer, WGPUMapMode, size_t, size_t, WGPUBufferMapCallbackInfo) = nullptr;
    void* (*BufferGetMappedRange)(WGPUBuffer, size_t, size_t) = nullptr;
    void (*BufferUnmap)(WGPUBuffer) = nullptr;
    WGPUCommandEncoder (*DeviceCreateCommandEncoder)(WGPUDevice, const WGPUCommandEncoderDescriptor*) = nullptr;
    WGPUComputePassEncoder (*CommandEncoderBeginComputePass)(WGPUCommandEncoder, const WGPUComputePassDescriptor*) = nullptr;
    void (*ComputePassEncoderSetPipeline)(WGPUComputePassEncoder, WGPUComputePipeline) = nullptr;
    void (*ComputePassEncoderSetBindGroup)(WGPUComputePassEncoder, uint32_t, WGPUBindGroup, size_t, const uint32_t*) = nullptr;
    void (*ComputePassEncoderDispatchWorkgroups)(WGPUComputePassEncoder, uint32_t, uint32_t, uint32_t) = nullptr;
    void (*ComputePassEncoderEnd)(WGPUComputePassEncoder) = nullptr;
    WGPUCommandBuffer (*CommandEncoderFinish)(WGPUCommandEncoder, const WGPUCommandBufferDescriptor*) = nullptr;
    void (*CommandEncoderCopyBufferToBuffer)(WGPUCommandEncoder, WGPUBuffer, uint64_t, WGPUBuffer, uint64_t, uint64_t) = nullptr;
    void (*QueueSubmit)(WGPUQueue, size_t, const WGPUCommandBuffer*) = nullptr;
    void (*QueueWriteBuffer)(WGPUQueue, WGPUBuffer, uint64_t, const void*, size_t) = nullptr;
    WGPUQueue (*DeviceGetQueue)(WGPUDevice) = nullptr;
    WGPUShaderModule (*DeviceCreateShaderModule)(WGPUDevice, const WGPUShaderModuleDescriptor*) = nullptr;
    WGPUComputePipeline (*DeviceCreateComputePipeline)(WGPUDevice, const WGPUComputePipelineDescriptor*) = nullptr;
    WGPUBindGroup (*DeviceCreateBindGroup)(WGPUDevice, const WGPUBindGroupDescriptor*) = nullptr;
    WGPUBindGroupLayout (*DeviceCreateBindGroupLayout)(WGPUDevice, const WGPUBindGroupLayoutDescriptor*) = nullptr;
    WGPUPipelineLayout (*DeviceCreatePipelineLayout)(WGPUDevice, const WGPUPipelineLayoutDescriptor*) = nullptr;
    void (*InstanceProcessEvents)(WGPUInstance) = nullptr;
    void (*InstanceRelease)(WGPUInstance) = nullptr;
    void (*AdapterRelease)(WGPUAdapter) = nullptr;
    void (*DeviceRelease)(WGPUDevice) = nullptr;
    void (*CommandBufferRelease)(WGPUCommandBuffer) = nullptr;
    void (*CommandEncoderRelease)(WGPUCommandEncoder) = nullptr;
    void (*ComputePassEncoderRelease)(WGPUComputePassEncoder) = nullptr;
    void (*ShaderModuleRelease)(WGPUShaderModule) = nullptr;
    void (*ComputePipelineRelease)(WGPUComputePipeline) = nullptr;
    void (*BindGroupRelease)(WGPUBindGroup) = nullptr;
    void (*BindGroupLayoutRelease)(WGPUBindGroupLayout) = nullptr;
    void (*PipelineLayoutRelease)(WGPUPipelineLayout) = nullptr;
    void (*QueueRelease)(WGPUQueue) = nullptr;
};
extern WgpuApi W;

/// 编译后的 compute kernel（module + 绑定布局 + 管线；GpuCompute.cpp 的 GpuKernel）
struct GpuKernel {
    WGPUShaderModule module = nullptr;
    WGPUBindGroupLayout bgl = nullptr;
    WGPUPipelineLayout pl = nullptr;
    WGPUComputePipeline pipe = nullptr;
};

extern WGPUInstance g_instance;
extern WGPUAdapter  g_adapter;
extern WGPUDevice   g_device;
extern WGPUQueue    g_queue;

extern "C" {

    // ---- 生命周期 ----
    // wgpuDllPath：wgpu_native.dll 全路径（NULL = 默认搜索）。返回 1=成功。
    JOB_API int  GpuCompute_Initialize(const char* wgpuDllPath);
    JOB_API void GpuCompute_Shutdown();
    // 最近一次错误文本（内部缓冲，下一次调用可能被覆盖）
    JOB_API const char* GpuCompute_GetLastError();

    // ---- kernel（shader module + bind group layout + pipeline layout + compute pipeline）----
    // storageBindingCount 个 storage(read_write) 绑定 + hasUniform 时末位 uniform 绑定。
    JOB_API void* GpuCompute_CreateKernel(const char* wgsl, int storageBindingCount, int hasUniform);
    // 带显式 entryPoint（多入口 shader 用，如 scatter/gather）；entryPoint NULL = "main"
    JOB_API void* GpuCompute_CreateKernelEx(const char* wgsl, int storageBindingCount, int hasUniform, const char* entryPoint);
    JOB_API void  GpuCompute_ReleaseKernel(void* kernel);

    // ---- buffer ----
    JOB_API void* GpuCompute_CreateStorageBuffer(unsigned long long size);   // STORAGE|COPY_DST|COPY_SRC
    JOB_API void* GpuCompute_CreateUniformBuffer(unsigned long long size);   // UNIFORM|COPY_DST
    JOB_API void  GpuCompute_WriteBuffer(void* buffer, const void* data, unsigned long long size);
    JOB_API void  GpuCompute_ReleaseBuffer(void* buffer);

    // ---- bind group（前 bufferCount-1 个为 storage，最后一个为 uniform；sizes 与 buffers 对齐）----
    JOB_API void* GpuCompute_CreateBindGroup(void* kernel, void* const* buffers,
                                             const unsigned long long* sizes, int bufferCount);
    JOB_API void  GpuCompute_ReleaseBindGroup(void* group);

    // ---- dispatch + sync ----
    JOB_API void GpuCompute_Dispatch(void* kernel, void* bindGroup, unsigned int workgroupX);
    JOB_API void GpuCompute_Sync(void);

    // ---- 回读（内部 staging：COPY_DST|MAP_READ → mapAsync → memcpy；outData 由调用者分配）----
    JOB_API int GpuCompute_ReadBack(void* buffer, void* outData, unsigned long long size);

} // extern "C"
