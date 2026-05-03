#include "SharpNative_Job__global_namespace__CPPJob_Execute.h"
#include <algorithm>
#include <cmath>
#include <cstdio>

HEAD void CALLINGCONVENTION SharpNative_Job__global_namespace__CPPJob_Execute_Batch(int __startIndex, int __count, float* RESTRICT Input_ptr, float* RESTRICT Output_ptr)
{
    for (int i = __startIndex; i < __startIndex + __count; ++i)
    {
float x = Input_ptr[i];
float res = std::sqrt(x) + std::sin(x) * std::cos(x) + std::log(x + 1);
Output_ptr[i] = res;
    }
}

