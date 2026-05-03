#include "SharpNative__global_namespace__IspccComparison_FillCPP.h"
#include <algorithm>
#include <cmath>
#include <cstdio>

HEAD void CALLINGCONVENTION SharpNative__global_namespace__IspccComparison_FillCPP(float* RESTRICT data_ptr, float* RESTRICT output_ptr)
{
for (int i = 0; i < 20000000; i++)
{
    float x = data_ptr[i];
    float res = std::sqrt(x) + std::sin(x) * std::cos(x) + std::log(x + 1);
    output_ptr[i] = res;
}
}
