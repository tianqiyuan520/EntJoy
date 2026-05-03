// Auto-generated ISPC source for FillIspcFast
#include "EntJoyCommon.ispc"

export void SharpNative__global_namespace__IspccComparison_FillIspcFast_impl(uniform float * uniform data_ptr, uniform float * uniform output_ptr)
{
for (int i = 0; i < 20000000; i++)
{
    float x = data_ptr[i];
    float res = sqrt(x) + sin(x) * cos(x) + log(x + 1);
    output_ptr[i] = res;
}
}
