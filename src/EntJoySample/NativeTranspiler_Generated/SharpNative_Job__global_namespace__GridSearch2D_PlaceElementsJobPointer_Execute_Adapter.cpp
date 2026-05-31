#include "../../NativeDll/NativeMath.h"
#include "../../NativeDll/NativeContainers.h"

#ifdef __cplusplus
#define EXTERNC extern "C"
#else
#define EXTERNC
#endif

#ifdef _WIN32
#define CALLINGCONVENTION __cdecl
#else
#define CALLINGCONVENTION
#endif

#ifdef DLL_IMPORT
#define HEAD EXTERNC __declspec(dllimport)
#else
#define HEAD EXTERNC __declspec(dllexport)
#endif

// restrict keyword compatibility
#if defined(_MSC_VER) || defined(__clang__)
  #define RESTRICT __restrict
#else
  #define RESTRICT __restrict__
#endif


#include "SharpNative_Job__global_namespace__GridSearch2D_PlaceElementsJobPointer_Execute.h"

HEAD void CALLINGCONVENTION SharpNative_Job__global_namespace__GridSearch2D_PlaceElementsJobPointer_Execute_Adapter(void* context, int __startIndex, int __count)
{
    auto* OriginalHashIndex_ptr = *(EntJoy::Mathematics::int2**)((char*)context + 0);
    int OriginalHashIndex_length = *(int*)((char*)context + 8);
    auto* Positions_ptr = *(EntJoy::Mathematics::float2**)((char*)context + 32);
    int Positions_length = *(int*)((char*)context + 40);
    auto* SortedPositions_ptr = *(EntJoy::Mathematics::float2**)((char*)context + 64);
    int SortedPositions_length = *(int*)((char*)context + 72);
    auto* SortedHashIndex_ptr = *(EntJoy::Mathematics::int2**)((char*)context + 96);
    int SortedHashIndex_length = *(int*)((char*)context + 104);
    auto* Counts_ptr = *(int**)((char*)context + 128);
    int Counts_length = *(int*)((char*)context + 136);
    auto* SortedPositionsLen_ptr = (int*)((char*)context + 160);

    SharpNative_Job__global_namespace__GridSearch2D_PlaceElementsJobPointer_Execute_Batch(__startIndex, __count, OriginalHashIndex_ptr, OriginalHashIndex_length, Positions_ptr, Positions_length, SortedPositions_ptr, SortedPositions_length, SortedHashIndex_ptr, SortedHashIndex_length, Counts_ptr, Counts_length, SortedPositionsLen_ptr);
}

HEAD void* CALLINGCONVENTION Get_SharpNative_Job__global_namespace__GridSearch2D_PlaceElementsJobPointer_Execute_AdapterPtr()
{
    return (void*)SharpNative_Job__global_namespace__GridSearch2D_PlaceElementsJobPointer_Execute_Adapter;
}
