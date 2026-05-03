#include "SharpNative_Job__global_namespace__GridSearch2D_FindWithinJobPointer_Execute.h"
#include <algorithm>
#include <cmath>
#include <cstdio>

HEAD void CALLINGCONVENTION SharpNative_Job__global_namespace__GridSearch2D_FindWithinJobPointer_Execute_Batch(int __startIndex, int __count, float* RESTRICT SquaredRadius_ptr, int* RESTRICT MaxNeighbor_ptr, int* RESTRICT CellsToLoop_ptr, EntJoy::Mathematics::float2* RESTRICT GridOrigin_ptr, float* RESTRICT GridResolutionInv_ptr, EntJoy::Mathematics::int2* RESTRICT GridDimensions_ptr, EntJoy::Mathematics::float2* RESTRICT QueryPositions_ptr, int QueryPositions_length, EntJoy::Mathematics::float2* RESTRICT SortedPositions_ptr, int SortedPositions_length, EntJoy::Mathematics::int2* RESTRICT HashIndex_ptr, int HashIndex_length, EntJoy::Collections::UnsafeList<EntJoy::Mathematics::int2>* RESTRICT CellStartEnd_listData, int* RESTRICT Results_ptr, int Results_length)
{
    EntJoy::Collections::UnsafeList<EntJoy::Mathematics::int2>& CellStartEnd = *CellStartEnd_listData;
    const float& SquaredRadius = *SquaredRadius_ptr;
    const int& MaxNeighbor = *MaxNeighbor_ptr;
    const int& CellsToLoop = *CellsToLoop_ptr;
    const EntJoy::Mathematics::float2& GridOrigin = *GridOrigin_ptr;
    const float& GridResolutionInv = *GridResolutionInv_ptr;
    const EntJoy::Mathematics::int2& GridDimensions = *GridDimensions_ptr;
    for (int index = __startIndex; index < __startIndex + __count; ++index)
    {
int baseIdx = index * MaxNeighbor;
for (int i = 0; i < MaxNeighbor; i++)
        Results_ptr[baseIdx + i] = -1;
EntJoy::Mathematics::float2 q = QueryPositions_ptr[index];
EntJoy::Mathematics::int2 centerCell = ((EntJoy::Mathematics::int2)EntJoy::Mathematics::floor((q - GridOrigin) * GridResolutionInv));
centerCell = EntJoy::Mathematics::clamp(centerCell, EntJoy::Mathematics::int2(0), GridDimensions - EntJoy::Mathematics::int2(1, 1));
int found = 0;
int centerHash = centerCell.y * GridDimensions.x + centerCell.x;
EntJoy::Mathematics::int2 centerRange = CellStartEnd[centerHash];
int start = centerRange.x;
int end = centerRange.y;
if (start >= 0)
{
    for (int iCell = start; iCell < end; iCell++)
    {
        EntJoy::Mathematics::float2 pos = SortedPositions_ptr[iCell];
        if (EntJoy::Mathematics::distancesq(q, pos) <= SquaredRadius)
        {
            Results_ptr[baseIdx + found] = HashIndex_ptr[iCell].y;
            found++;
            if (found == MaxNeighbor)
                                return;
        }
    }
}
for (int dx = -CellsToLoop; dx <= CellsToLoop; dx++)
{
    int nx = centerCell.x + dx;
    if (((unsigned int)nx) >= ((unsigned int)GridDimensions.x))
                continue;
    for (int dy = -CellsToLoop; dy <= CellsToLoop; dy++)
    {
        if (dx == 0 && dy == 0)
                        continue;
        int ny = centerCell.y + dy;
        if (((unsigned int)ny) >= ((unsigned int)GridDimensions.y))
                        continue;
        int hash = ny * GridDimensions.x + nx;
        EntJoy::Mathematics::int2 range = CellStartEnd[hash];
        int s = range.x;
        int e = range.y;
        if (s < 0)
                        continue;
        for (int iCell = s; iCell < e; iCell++)
        {
            EntJoy::Mathematics::float2 pos = SortedPositions_ptr[iCell];
            if (EntJoy::Mathematics::distancesq(q, pos) <= SquaredRadius)
            {
                Results_ptr[baseIdx + found] = HashIndex_ptr[iCell].y;
                found++;
                if (found == MaxNeighbor)
                                        return;
            }
        }
    }
}
    }
}

