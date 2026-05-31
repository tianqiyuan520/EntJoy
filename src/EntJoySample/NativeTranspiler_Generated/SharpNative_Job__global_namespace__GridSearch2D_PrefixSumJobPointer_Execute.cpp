#include "SharpNative_Job__global_namespace__GridSearch2D_PrefixSumJobPointer_Execute.h"
#include <algorithm>
#include <cmath>
#include <cstdio>

HEAD void CALLINGCONVENTION SharpNative_Job__global_namespace__GridSearch2D_PrefixSumJobPointer_Execute(int* RESTRICT Counts_ptr, int Counts_length, int* RESTRICT Length_ptr)
{
    const int& Length = *Length_ptr;
int sum = 0;
for (int i = 0; i < Length; i++)
{
    int c = Counts_ptr[i];
    Counts_ptr[i] = sum;
    sum += c;
}
}
