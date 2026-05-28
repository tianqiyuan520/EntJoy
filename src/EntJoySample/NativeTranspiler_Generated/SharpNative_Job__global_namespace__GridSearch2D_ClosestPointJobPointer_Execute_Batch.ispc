// Auto-generated ISPC source for ClosestPointJobPointer
#include "EntJoyCommon.ispc"

struct UnsafeList_Context_int2 {
    void* uniform _data;
    uniform int _length;
    uniform int _capacity;
    uniform int _allocator;
    void (* uniform ResizeFunc)(void* uniform * uniform _data, uniform int * uniform _length, uniform int * uniform _capacity, uniform int * uniform _allocator, uniform int newSize, uniform bool clear);
};

export void SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_Batch_true_impl(uniform int __startIndex, uniform int __count, uniform float2 * uniform GridOrigin_ptr, uniform float * uniform GridResolutionInv_ptr, uniform int2 * uniform GridDimensions_ptr, uniform float2 QueryPositions_ptr[], uniform int QueryPositions_length, uniform float2 SortedPositions_ptr[], uniform int SortedPositions_length, uniform int2 HashIndex_ptr[], uniform int HashIndex_length, uniform UnsafeList_Context_int2* uniform CellStartEnd, uniform int * uniform SortedLength_ptr, uniform bool * uniform IgnoreSelf_ptr, uniform float * uniform SquaredEpsilonSelf_ptr, uniform int Results_ptr[], uniform int Results_length)
{
    float2 GridOrigin = *GridOrigin_ptr;
    float GridResolutionInv = *GridResolutionInv_ptr;
    int2 GridDimensions = *GridDimensions_ptr;
    int SortedLength = *SortedLength_ptr;
    bool IgnoreSelf = *IgnoreSelf_ptr;
    float SquaredEpsilonSelf = *SquaredEpsilonSelf_ptr;
    foreach (index = __startIndex ... min(__startIndex + __count, QueryPositions_length)) {
Results_ptr[index] = -1;
float2 q = QueryPositions_ptr[index];
int2 cell = int2_from_float2(floor((q - GridOrigin) * GridResolutionInv));
cell = clamp(cell, make_int2(0, 0), GridDimensions - 1);
float bestDistSq = 3.402823466e+38f;
int bestIdx = -1;
for (int dx = -1; dx <= 1; dx++)
{
    int nx = cell.x + dx;
    if ((unsigned int)nx >= (unsigned int)GridDimensions.x)
                continue;
    for (int dy = -1; dy <= 1; dy++)
    {
        int ny = cell.y + dy;
        if ((unsigned int)ny >= (unsigned int)GridDimensions.y)
                        continue;
        int cellHash = ny * GridDimensions.x + nx;
        int2 range = (((int2*)CellStartEnd->_data)[cellHash]);
        int start = range.x;
        int end = range.y;
        if (start < 0)
                        continue;
        for (int i = start; i < end; i++)
        {
            float2 pos = SortedPositions_ptr[i];
            float distSq = distancesq(q, pos);
            if (true && distSq < SquaredEpsilonSelf)
                                continue;
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestIdx = i;
            }
        }
    }
}
if (bestIdx != -1)
{
    Results_ptr[index] = HashIndex_ptr[bestIdx].y;
}
else
{
    for (int i = 0; i < SortedLength; i++)
    {
        float2 pos = SortedPositions_ptr[i];
        float distSq = distancesq(q, pos);
        if (true && distSq < SquaredEpsilonSelf)
                        continue;
        if (distSq < bestDistSq)
        {
            bestDistSq = distSq;
            bestIdx = i;
        }
    }
    if (bestIdx != -1)
                Results_ptr[index] = HashIndex_ptr[bestIdx].y;
}
    }
}

export void SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_Batch_false_impl(uniform int __startIndex, uniform int __count, uniform float2 * uniform GridOrigin_ptr, uniform float * uniform GridResolutionInv_ptr, uniform int2 * uniform GridDimensions_ptr, uniform float2 QueryPositions_ptr[], uniform int QueryPositions_length, uniform float2 SortedPositions_ptr[], uniform int SortedPositions_length, uniform int2 HashIndex_ptr[], uniform int HashIndex_length, uniform UnsafeList_Context_int2* uniform CellStartEnd, uniform int * uniform SortedLength_ptr, uniform bool * uniform IgnoreSelf_ptr, uniform float * uniform SquaredEpsilonSelf_ptr, uniform int Results_ptr[], uniform int Results_length)
{
    float2 GridOrigin = *GridOrigin_ptr;
    float GridResolutionInv = *GridResolutionInv_ptr;
    int2 GridDimensions = *GridDimensions_ptr;
    int SortedLength = *SortedLength_ptr;
    bool IgnoreSelf = *IgnoreSelf_ptr;
    float SquaredEpsilonSelf = *SquaredEpsilonSelf_ptr;
    foreach (index = __startIndex ... min(__startIndex + __count, QueryPositions_length)) {
Results_ptr[index] = -1;
float2 q = QueryPositions_ptr[index];
int2 cell = int2_from_float2(floor((q - GridOrigin) * GridResolutionInv));
cell = clamp(cell, make_int2(0, 0), GridDimensions - 1);
float bestDistSq = 3.402823466e+38f;
int bestIdx = -1;
for (int dx = -1; dx <= 1; dx++)
{
    int nx = cell.x + dx;
    if ((unsigned int)nx >= (unsigned int)GridDimensions.x)
                continue;
    for (int dy = -1; dy <= 1; dy++)
    {
        int ny = cell.y + dy;
        if ((unsigned int)ny >= (unsigned int)GridDimensions.y)
                        continue;
        int cellHash = ny * GridDimensions.x + nx;
        int2 range = (((int2*)CellStartEnd->_data)[cellHash]);
        int start = range.x;
        int end = range.y;
        if (start < 0)
                        continue;
        for (int i = start; i < end; i++)
        {
            float2 pos = SortedPositions_ptr[i];
            float distSq = distancesq(q, pos);
            if (false && distSq < SquaredEpsilonSelf)
                                continue;
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestIdx = i;
            }
        }
    }
}
if (bestIdx != -1)
{
    Results_ptr[index] = HashIndex_ptr[bestIdx].y;
}
else
{
    for (int i = 0; i < SortedLength; i++)
    {
        float2 pos = SortedPositions_ptr[i];
        float distSq = distancesq(q, pos);
        if (false && distSq < SquaredEpsilonSelf)
                        continue;
        if (distSq < bestDistSq)
        {
            bestDistSq = distSq;
            bestIdx = i;
        }
    }
    if (bestIdx != -1)
                Results_ptr[index] = HashIndex_ptr[bestIdx].y;
}
    }
}
