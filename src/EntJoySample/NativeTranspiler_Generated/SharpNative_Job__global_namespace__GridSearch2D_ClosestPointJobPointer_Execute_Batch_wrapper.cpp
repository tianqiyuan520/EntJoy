#include "NativeMath.h"
#include "NativeContainers.h"
#include "SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_Batch_ispc.h"

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


static void UnsafeList_Resize_int2_callback(void** data, int* length, int* capacity, int* allocator, int newSize, bool clear) {
    using Alloc = EntJoy::Collections::Allocator;
    EntJoy::Collections::UnsafeList<EntJoy::Mathematics::int2> tmp;
    tmp.Ptr = static_cast<EntJoy::Mathematics::int2*>(*data);
    tmp.Length = *length;
    tmp.Capacity = *capacity;
    tmp.Allocator = static_cast<Alloc>(*allocator);
    EntJoy::Collections::NativeArrayOptions opts = clear ? EntJoy::Collections::NativeArrayOptions::ClearMemory : EntJoy::Collections::NativeArrayOptions::UninitializedMemory;
    tmp.Resize(newSize, opts);
    *data = tmp.Ptr;
    *length = tmp.Length;
    *capacity = tmp.Capacity;
    *allocator = static_cast<int>(tmp.Allocator);
}
HEAD void CALLINGCONVENTION SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_Batch_true(int __startIndex, int __count, EntJoy::Mathematics::float2* RESTRICT GridOrigin_ptr, float* RESTRICT GridResolutionInv_ptr, EntJoy::Mathematics::int2* RESTRICT GridDimensions_ptr, EntJoy::Mathematics::float2* RESTRICT QueryPositions_ptr, int QueryPositions_length, EntJoy::Mathematics::float2* RESTRICT SortedPositions_ptr, int SortedPositions_length, EntJoy::Mathematics::int2* RESTRICT HashIndex_ptr, int HashIndex_length, EntJoy::Collections::UnsafeList<EntJoy::Mathematics::int2>* RESTRICT CellStartEnd_listData, int* RESTRICT SortedLength_ptr, bool* RESTRICT IgnoreSelf_ptr, float* RESTRICT SquaredEpsilonSelf_ptr, int* RESTRICT Results_ptr, int Results_length)
{
    ispc::UnsafeList_Context_int2 CellStartEnd_ctx;
    CellStartEnd_ctx._data = CellStartEnd_listData->Ptr;
    CellStartEnd_ctx._length = CellStartEnd_listData->Length;
    CellStartEnd_ctx._capacity = CellStartEnd_listData->Capacity;
    CellStartEnd_ctx._allocator = static_cast<int>(CellStartEnd_listData->Allocator);
    CellStartEnd_ctx.ResizeFunc = UnsafeList_Resize_int2_callback;
    ispc::SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_Batch_true_impl(__startIndex, __count, reinterpret_cast<ispc::float2*>(GridOrigin_ptr), GridResolutionInv_ptr, reinterpret_cast<ispc::int2*>(GridDimensions_ptr), reinterpret_cast<ispc::float2*>(QueryPositions_ptr), QueryPositions_length, reinterpret_cast<ispc::float2*>(SortedPositions_ptr), SortedPositions_length, reinterpret_cast<ispc::int2*>(HashIndex_ptr), HashIndex_length, &CellStartEnd_ctx, SortedLength_ptr, IgnoreSelf_ptr, SquaredEpsilonSelf_ptr, Results_ptr, Results_length);
    CellStartEnd_listData->Length = CellStartEnd_ctx._length;
    CellStartEnd_listData->Capacity = CellStartEnd_ctx._capacity;
    CellStartEnd_listData->Ptr = static_cast<EntJoy::Mathematics::int2*>(CellStartEnd_ctx._data);
    CellStartEnd_listData->Allocator = static_cast<EntJoy::Collections::Allocator>(CellStartEnd_ctx._allocator);
}

HEAD void CALLINGCONVENTION SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_Batch_false(int __startIndex, int __count, EntJoy::Mathematics::float2* RESTRICT GridOrigin_ptr, float* RESTRICT GridResolutionInv_ptr, EntJoy::Mathematics::int2* RESTRICT GridDimensions_ptr, EntJoy::Mathematics::float2* RESTRICT QueryPositions_ptr, int QueryPositions_length, EntJoy::Mathematics::float2* RESTRICT SortedPositions_ptr, int SortedPositions_length, EntJoy::Mathematics::int2* RESTRICT HashIndex_ptr, int HashIndex_length, EntJoy::Collections::UnsafeList<EntJoy::Mathematics::int2>* RESTRICT CellStartEnd_listData, int* RESTRICT SortedLength_ptr, bool* RESTRICT IgnoreSelf_ptr, float* RESTRICT SquaredEpsilonSelf_ptr, int* RESTRICT Results_ptr, int Results_length)
{
    ispc::UnsafeList_Context_int2 CellStartEnd_ctx;
    CellStartEnd_ctx._data = CellStartEnd_listData->Ptr;
    CellStartEnd_ctx._length = CellStartEnd_listData->Length;
    CellStartEnd_ctx._capacity = CellStartEnd_listData->Capacity;
    CellStartEnd_ctx._allocator = static_cast<int>(CellStartEnd_listData->Allocator);
    CellStartEnd_ctx.ResizeFunc = UnsafeList_Resize_int2_callback;
    ispc::SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_Batch_false_impl(__startIndex, __count, reinterpret_cast<ispc::float2*>(GridOrigin_ptr), GridResolutionInv_ptr, reinterpret_cast<ispc::int2*>(GridDimensions_ptr), reinterpret_cast<ispc::float2*>(QueryPositions_ptr), QueryPositions_length, reinterpret_cast<ispc::float2*>(SortedPositions_ptr), SortedPositions_length, reinterpret_cast<ispc::int2*>(HashIndex_ptr), HashIndex_length, &CellStartEnd_ctx, SortedLength_ptr, IgnoreSelf_ptr, SquaredEpsilonSelf_ptr, Results_ptr, Results_length);
    CellStartEnd_listData->Length = CellStartEnd_ctx._length;
    CellStartEnd_listData->Capacity = CellStartEnd_ctx._capacity;
    CellStartEnd_listData->Ptr = static_cast<EntJoy::Mathematics::int2*>(CellStartEnd_ctx._data);
    CellStartEnd_listData->Allocator = static_cast<EntJoy::Collections::Allocator>(CellStartEnd_ctx._allocator);
}

