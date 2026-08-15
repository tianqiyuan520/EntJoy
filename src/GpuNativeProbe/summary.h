// summary.h — GpuNativeProbe 最终汇总表结构（各探针填充，main 最后打印直观对比）
// 独立轻量头：wgpu_probe.cpp 自包含（不 include common.h），需要此结构但不想要 common.h 的
// float2/now_ms 等符号。后端索引：0=native OpenCL  1=wgpu-native  2=native CUDA；值 <0 = 不适用/未测。
#pragma once

#define GPU_BACKENDS 3

typedef struct {
    double heavyResident[3], heavyRoundtrip[3], heavyPinned[3];
    double lightResident[3], lightRoundtrip[3], lightPinned[3];
    double gridResident[3], gridRoundtrip[3], gridPinned[3];
    double heavyMt, lightMt;   /* NativeDll JobSystem 多线程（main.cpp 填；<0 = 未跑） */
    int    gridMismatch[3];    /* GridSearch parity mismatch；<0 = 仅 sanity 未逐元素比对 */
} ProbeSummary;
extern ProbeSummary g_sum;
