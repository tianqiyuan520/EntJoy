// ============================================================
// GpuResidency.h — GpuResidencyManager（wgpu 后端，docs/17 resHashMode 实施）。
//   常驻 buffer + 4 链 CRC32C hash 索引 diff + staging 拼接 + scatter/gather +
//   job 级模式切换（dirty ≥20% 连续 2 帧切全量；全量每 4 帧采样 hash，<10% 连续 2 次切回）。
//   复用 GpuCompute 的 device/queue/函数表；C# 走此 C ABI（docs/17 §八 第 1 项）。
//   v2 跨帧流水：输入/输出分离（inPtrs 纯输入、outPtrs 结果镜像）+ 双 staging 快照 +
//   diff/拼接藏进 GPU 执行期（Sync 先 diff+build 再完成上帧再提交本帧，不 wait 即返回）。
// ============================================================
#pragma once

#include "Exports.h"

extern "C" {

    /// 注册一个 GPU job 的驻留实例：
    ///   wgsl            —— job kernel WGSL（transpiler 生成，含 _count 越界保护）
    ///   storageCount    —— storage buffer 数（与 WGSL binding 0..n-1 对应）
    ///   hasUniform      —— 是否有 uniform（binding n）
    ///   chunkEntities   —— chunk 粒度（实体数，默认 128）
    ///   elemBytes       —— 每 storage 元素字节（float2=8 / f32=4 …），长度 storageCount
    ///   返回驻留句柄（NULL=失败，错误见 GpuResidency_GetLastError）。
    JOB_API void* GpuResidency_RegisterJob(const char* wgsl, int storageCount, int hasUniform,
                                           int chunkEntities, const int* elemBytes);
    JOB_API void  GpuResidency_ReleaseJob(void* job);

    /// 每帧同步（流水语义）：
    ///   inPtrs    —— storageCount 个 host 输入数组指针（NativeArray GetUnsafePtr，纯输入：
    ///                游戏逻辑只改它；hash 基准与 diff 都基于它）
    ///   outPtrs   —— storageCount 个 host 结果数组指针（结果镜像：首帧全量回读 + 每帧只 patch
    ///                dirty chunk —— 探针 outCache 语义；非 dirty 保持上帧结果）
    ///   lengths   —— 每数组实体数（元素数）
    ///   uniformBytes/uniformSize —— uniform 打包字节（标量字段序 + _count:i32；无 uniform 传 NULL/0）
    ///   count     —— 调度长度（实体数）
    ///   内部：先 diff+build（GPU 跑上帧期间）→ 完成上帧（wait+读回+patch outPtrs）→
    ///   提交本帧（不 wait 即返回，下帧 Sync 或 GpuResidency_Complete 完成）。返回 1=成功。
    JOB_API int GpuResidency_Sync(void* job, void* const* inPtrs, void* const* outPtrs,
                                  const int* lengths, const void* uniformBytes, int uniformSize, int count);

    /// 显式完成最后一帧的 pending 提交（wait + 读回 + patch outPtrs）。ReleaseJob 前调用，
    /// 或帧循环收尾时调用。返回 1=成功。
    JOB_API int GpuResidency_Complete(void* job);

    /// 当前执行模式（0=增量, 1=全量）——调试/验证用
    JOB_API int GpuResidency_GetMode(void* job);

    JOB_API const char* GpuResidency_GetLastError();

} // extern "C"
