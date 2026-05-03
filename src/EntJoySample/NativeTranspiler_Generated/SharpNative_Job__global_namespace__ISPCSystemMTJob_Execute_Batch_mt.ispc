// Auto-generated ISPC MT source for ISPCSystemMTJob
#include "EntJoyCommon.ispc"

task void SharpNative_Job__global_namespace__ISPCSystemMTJob_Execute_Batch_task(uniform int __startIndex, uniform int __count, uniform float * uniform Input_ptr, uniform float * uniform Output_ptr)
{
    uniform int n_per_task = max(1, (int)(__count / taskCount));
    uniform int start = __startIndex + taskIndex * n_per_task;
    uniform int end = (taskIndex == taskCount - 1) ? (__startIndex + __count) : min(start + n_per_task, __startIndex + (int)__count);
    // 仅 lane 0 执行 uniform for 循环（防止重复处理同一 ID）
    if (programIndex != 0) return;

    for (uniform int i = start; i < end; i++) {
    uniform float x = Input_ptr[i];
    uniform float res = sqrt(x) + sin(x) * cos(x) + log(x + 1);
    Output_ptr[i] = res;
    }
}

export void SharpNative_Job__global_namespace__ISPCSystemMTJob_Execute_Batch_mt_impl(uniform int __startIndex, uniform int __count, uniform float * uniform Input_ptr, uniform float * uniform Output_ptr, uniform int numTasks)
{
    launch[numTasks] SharpNative_Job__global_namespace__ISPCSystemMTJob_Execute_Batch_task(__startIndex, __count, Input_ptr, Output_ptr);
    sync;
}
