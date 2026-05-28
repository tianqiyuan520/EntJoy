#include "JobProfiler.h"

// 全局 Profiler 实例定义
ProfilerRingBuffer g_profilerBuffer;
std::atomic<bool> g_profilerEnabled{ false };
