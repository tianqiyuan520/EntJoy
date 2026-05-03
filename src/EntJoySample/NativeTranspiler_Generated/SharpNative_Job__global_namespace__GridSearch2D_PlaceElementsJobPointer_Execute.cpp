#include "SharpNative_Job__global_namespace__GridSearch2D_PlaceElementsJobPointer_Execute.h"
#include <algorithm>
#include <cmath>
#include <cstdio>

HEAD void CALLINGCONVENTION SharpNative_Job__global_namespace__GridSearch2D_PlaceElementsJobPointer_Execute_Batch(int __startIndex, int __count, EntJoy::Mathematics::int2* RESTRICT OriginalHashIndex_ptr, int OriginalHashIndex_length, EntJoy::Mathematics::float2* RESTRICT Positions_ptr, int Positions_length, EntJoy::Mathematics::float2* RESTRICT SortedPositions_ptr, int SortedPositions_length, EntJoy::Mathematics::int2* RESTRICT SortedHashIndex_ptr, int SortedHashIndex_length, int* RESTRICT Counts_ptr, int Counts_length, int* RESTRICT SortedPositionsLen_ptr)
{
    const int& SortedPositionsLen = *SortedPositionsLen_ptr;
    for (int index = __startIndex; index < __startIndex + __count; ++index)
    {
EntJoy::Mathematics::int2 entry = OriginalHashIndex_ptr[index];
int hash = entry.x;
int origIdx = entry.y;
int destIdx = INTERLOCKED_ADD_AND_FETCH(&((int*)Counts_ptr)[hash], 1) - 1;
if (destIdx < SortedPositionsLen)
{
    SortedPositions_ptr[destIdx] = Positions_ptr[origIdx];
    SortedHashIndex_ptr[destIdx] = EntJoy::Mathematics::int2(hash, origIdx);
}
    }
}

