// Auto-generated ISPC MT source for FillIspcSystemMT
#include "EntJoyCommon.ispc"

task void SharpNative__global_namespace__IspccComparison_FillIspcSystemMT_task0(uniform int __startIndex, uniform int __count, uniform float * uniform data_ptr, uniform float * uniform output_ptr)
{
    uniform int n_per_task = max(1, __count / taskCount);
    uniform int start = __startIndex + taskIndex * n_per_task;
    uniform int end = (taskIndex == taskCount - 1) ? (__startIndex + __count) : min(start + n_per_task, __startIndex + __count);
    for (uniform int i = start; i < end; i++) {
        uniform float x = data_ptr[i];
        uniform float res = sqrt(x) + sin(x) * cos(x) + log(x + 1);
        output_ptr[i] = res;
    }
}

export void SharpNative__global_namespace__IspccComparison_FillIspcSystemMT_mt_impl(uniform float * uniform data_ptr, uniform float * uniform output_ptr, uniform int numTasks)
{
    launch[numTasks] SharpNative__global_namespace__IspccComparison_FillIspcSystemMT_task0(0, 20000000, data_ptr, output_ptr);
    sync;
}
