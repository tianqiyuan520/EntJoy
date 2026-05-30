// Auto-generated ISPC source for AssignAndCountJobPointer
#include "EntJoyCommon.ispc"

export void SharpNative_Job__global_namespace__GridSearch2D_AssignAndCountJobPointer_Execute_Batch_impl(uniform int __startIndex, uniform int __count, uniform float2 Positions_ptr[], uniform int Positions_length, uniform int2 HashIndex_ptr[], uniform int HashIndex_length, uniform int Counts_ptr[], uniform int Counts_length, uniform float2 * uniform Origin_ptr, uniform float * uniform InvRes_ptr, uniform int2 * uniform Dim_ptr, uniform int * uniform MaxHash_ptr)
{
    float2 Origin = *Origin_ptr;
    float InvRes = *InvRes_ptr;
    int2 Dim = *Dim_ptr;
    int MaxHash = *MaxHash_ptr;
    foreach (index = __startIndex ... min(__startIndex + __count, Positions_length)) {
float2 p = Positions_ptr[index];
int cellX = (int)((p.x - Origin.x) * InvRes);
int cellY = (int)((p.y - Origin.y) * InvRes);
cellX = clamp(cellX, 0, Dim.x - 1);
cellY = clamp(cellY, 0, Dim.y - 1);
int hash = cellY * Dim.x + cellX;
hash = clamp(hash, 0, MaxHash);
HashIndex_ptr[index] = make_int2(hash, index);
atomic_add_global(&((int*)Counts_ptr)[hash], 1);
    }
}
