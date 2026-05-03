// Auto-generated ISPC source for ISPCDefaultJob
#include "EntJoyCommon.ispc"

export void SharpNative_Job__global_namespace__ISPCDefaultJob_Execute_Batch_impl(uniform int __startIndex, uniform int __count, uniform float * uniform Input_ptr, uniform float * uniform Output_ptr)
{
    foreach (i = __startIndex ... __startIndex + __count) {
float x = Input_ptr[i];
float res = sqrt(x) + sin(x) * cos(x) + log(x + 1);
Output_ptr[i] = res;
    }
}
