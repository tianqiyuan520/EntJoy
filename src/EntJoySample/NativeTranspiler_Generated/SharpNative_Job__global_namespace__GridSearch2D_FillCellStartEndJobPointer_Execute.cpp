#include "SharpNative_Job__global_namespace__GridSearch2D_FillCellStartEndJobPointer_Execute.h"
#include <algorithm>
#include <cmath>
#include <cstdio>

HEAD void CALLINGCONVENTION SharpNative_Job__global_namespace__GridSearch2D_FillCellStartEndJobPointer_Execute(EntJoy::Mathematics::int2* RESTRICT SortedHashIndex_ptr, int SortedHashIndex_length, int* RESTRICT SortedLength_ptr, EntJoy::Collections::UnsafeList<EntJoy::Mathematics::int2>* RESTRICT CellStartEnd_listData)
{
    EntJoy::Collections::UnsafeList<EntJoy::Mathematics::int2>& CellStartEnd = *CellStartEnd_listData;
    const int& SortedLength = *SortedLength_ptr;
for (int i = 0; i < CellStartEnd.length(); i++)
        CellStartEnd[i] = EntJoy::Mathematics::int2(-1, -1);
if (SortedLength == 0)
        return;
int currentHash = SortedHashIndex_ptr[0].x;
int startIdx = 0;
for (int i = 1; i <= SortedLength; i++)
{
    if (i == SortedLength || SortedHashIndex_ptr[i].x != currentHash)
    {
        CellStartEnd[currentHash] = EntJoy::Mathematics::int2(startIdx, i);
        if (i < SortedLength)
        {
            currentHash = SortedHashIndex_ptr[i].x;
            startIdx = i;
        }
    }
}
}
