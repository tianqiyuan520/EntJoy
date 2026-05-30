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


// ISPC wrapper function (defined in wrapper.cpp)
HEAD void CALLINGCONVENTION SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_Batch_false(int __startIndex, int __count, EntJoy::Mathematics::float2* RESTRICT GridOrigin_ptr, float* RESTRICT GridResolutionInv_ptr, EntJoy::Mathematics::int2* RESTRICT GridDimensions_ptr, EntJoy::Mathematics::float2* RESTRICT QueryPositions_ptr, int QueryPositions_length, EntJoy::Mathematics::float2* RESTRICT SortedPositions_ptr, int SortedPositions_length, EntJoy::Mathematics::int2* RESTRICT HashIndex_ptr, int HashIndex_length, EntJoy::Collections::UnsafeList<EntJoy::Mathematics::int2>* RESTRICT CellStartEnd_listData, int* RESTRICT SortedLength_ptr, bool* RESTRICT IgnoreSelf_ptr, float* RESTRICT SquaredEpsilonSelf_ptr, int* RESTRICT Results_ptr, int Results_length);
HEAD void CALLINGCONVENTION SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_Batch_true(int __startIndex, int __count, EntJoy::Mathematics::float2* RESTRICT GridOrigin_ptr, float* RESTRICT GridResolutionInv_ptr, EntJoy::Mathematics::int2* RESTRICT GridDimensions_ptr, EntJoy::Mathematics::float2* RESTRICT QueryPositions_ptr, int QueryPositions_length, EntJoy::Mathematics::float2* RESTRICT SortedPositions_ptr, int SortedPositions_length, EntJoy::Mathematics::int2* RESTRICT HashIndex_ptr, int HashIndex_length, EntJoy::Collections::UnsafeList<EntJoy::Mathematics::int2>* RESTRICT CellStartEnd_listData, int* RESTRICT SortedLength_ptr, bool* RESTRICT IgnoreSelf_ptr, float* RESTRICT SquaredEpsilonSelf_ptr, int* RESTRICT Results_ptr, int Results_length);

HEAD void CALLINGCONVENTION SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_Adapter(void* context, int __startIndex, int __count)
{
    auto* GridOrigin_ptr = (EntJoy::Mathematics::float2*)((char*)context + 0);
    auto* GridResolutionInv_ptr = (float*)((char*)context + 8);
    auto* GridDimensions_ptr = (EntJoy::Mathematics::int2*)((char*)context + 12);
    auto* QueryPositions_ptr = *(EntJoy::Mathematics::float2**)((char*)context + 24);
    int QueryPositions_length = *(int*)((char*)context + 32);
    auto* SortedPositions_ptr = *(EntJoy::Mathematics::float2**)((char*)context + 56);
    int SortedPositions_length = *(int*)((char*)context + 64);
    auto* HashIndex_ptr = *(EntJoy::Mathematics::int2**)((char*)context + 88);
    int HashIndex_length = *(int*)((char*)context + 96);
    auto* CellStartEnd_listData = *(EntJoy::Collections::UnsafeList<EntJoy::Mathematics::int2>**)((char*)context + 120);
    auto* SortedLength_ptr = (int*)((char*)context + 144);
    auto* IgnoreSelf_ptr = (bool*)((char*)context + 148);
    auto* SquaredEpsilonSelf_ptr = (float*)((char*)context + 152);
    auto* Results_ptr = *(int**)((char*)context + 160);
    int Results_length = *(int*)((char*)context + 168);

    bool __IgnoreSelf = *(bool*)((char*)context + 148);

    if (!__IgnoreSelf)
        SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_Batch_false(__startIndex, __count, GridOrigin_ptr, GridResolutionInv_ptr, GridDimensions_ptr, QueryPositions_ptr, QueryPositions_length, SortedPositions_ptr, SortedPositions_length, HashIndex_ptr, HashIndex_length, CellStartEnd_listData, SortedLength_ptr, IgnoreSelf_ptr, SquaredEpsilonSelf_ptr, Results_ptr, Results_length);
    else
        SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_Batch_true(__startIndex, __count, GridOrigin_ptr, GridResolutionInv_ptr, GridDimensions_ptr, QueryPositions_ptr, QueryPositions_length, SortedPositions_ptr, SortedPositions_length, HashIndex_ptr, HashIndex_length, CellStartEnd_listData, SortedLength_ptr, IgnoreSelf_ptr, SquaredEpsilonSelf_ptr, Results_ptr, Results_length);
}

HEAD void* CALLINGCONVENTION Get_SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_AdapterPtr()
{
    return (void*)SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_Adapter;
}
