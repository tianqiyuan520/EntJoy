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


#include "SharpNative_Job__global_namespace__GridSearch2D_FindWithinJobPointer_Execute.h"

HEAD void CALLINGCONVENTION SharpNative_Job__global_namespace__GridSearch2D_FindWithinJobPointer_Execute_Adapter(void* context, int __startIndex, int __count)
{
    auto* SquaredRadius_ptr = (float*)((char*)context + 0);
    auto* MaxNeighbor_ptr = (int*)((char*)context + 4);
    auto* CellsToLoop_ptr = (int*)((char*)context + 8);
    auto* GridOrigin_ptr = (EntJoy::Mathematics::float2*)((char*)context + 12);
    auto* GridResolutionInv_ptr = (float*)((char*)context + 20);
    auto* GridDimensions_ptr = (EntJoy::Mathematics::int2*)((char*)context + 24);
    auto* QueryPositions_ptr = *(EntJoy::Mathematics::float2**)((char*)context + 32);
    int QueryPositions_length = *(int*)((char*)context + 40);
    auto* SortedPositions_ptr = *(EntJoy::Mathematics::float2**)((char*)context + 64);
    int SortedPositions_length = *(int*)((char*)context + 72);
    auto* HashIndex_ptr = *(EntJoy::Mathematics::int2**)((char*)context + 96);
    int HashIndex_length = *(int*)((char*)context + 104);
    auto* CellStartEnd_listData = *(EntJoy::Collections::UnsafeList<EntJoy::Mathematics::int2>**)((char*)context + 128);
    auto* Results_ptr = *(int**)((char*)context + 152);
    int Results_length = *(int*)((char*)context + 160);

    SharpNative_Job__global_namespace__GridSearch2D_FindWithinJobPointer_Execute_Batch(__startIndex, __count, SquaredRadius_ptr, MaxNeighbor_ptr, CellsToLoop_ptr, GridOrigin_ptr, GridResolutionInv_ptr, GridDimensions_ptr, QueryPositions_ptr, QueryPositions_length, SortedPositions_ptr, SortedPositions_length, HashIndex_ptr, HashIndex_length, CellStartEnd_listData, Results_ptr, Results_length);
}

HEAD void* CALLINGCONVENTION Get_SharpNative_Job__global_namespace__GridSearch2D_FindWithinJobPointer_Execute_AdapterPtr()
{
    return (void*)SharpNative_Job__global_namespace__GridSearch2D_FindWithinJobPointer_Execute_Adapter;
}
