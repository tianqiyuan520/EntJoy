// cuda_probe.h — CUDA runtime API 探针
#pragma once
double RunCudaProbe(void);
void RunDiffProbe(void);   // 影子 diff 成本探针（diff_probe.cpp）
void RunDensityProbe(void); // 瓶颈分类器阈值验证探针（density_probe.cpp）
