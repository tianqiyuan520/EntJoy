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
HEAD void CALLINGCONVENTION SharpNative_Job__global_namespace__GridSearch2D_AssignAndCountJobPointer_Execute_Batch(int __startIndex, int __count, EntJoy::Mathematics::float2* RESTRICT Positions_ptr, int Positions_length, EntJoy::Mathematics::int2* RESTRICT HashIndex_ptr, int HashIndex_length, int* RESTRICT Counts_ptr, int Counts_length, EntJoy::Mathematics::float2* RESTRICT Origin_ptr, float* RESTRICT InvRes_ptr, EntJoy::Mathematics::int2* RESTRICT Dim_ptr, int* RESTRICT MaxHash_ptr);

HEAD void CALLINGCONVENTION SharpNative_Job__global_namespace__GridSearch2D_AssignAndCountJobPointer_Execute_Adapter(void* context, int __startIndex, int __count)
{
    auto* Positions_ptr = *(EntJoy::Mathematics::float2**)((char*)context + 0);
    int Positions_length = *(int*)((char*)context + 8);
    auto* HashIndex_ptr = *(EntJoy::Mathematics::int2**)((char*)context + 32);
    int HashIndex_length = *(int*)((char*)context + 40);
    auto* Counts_ptr = *(int**)((char*)context + 64);
    int Counts_length = *(int*)((char*)context + 72);
    auto* Origin_ptr = (EntJoy::Mathematics::float2*)((char*)context + 96);
    auto* InvRes_ptr = (float*)((char*)context + 104);
    auto* Dim_ptr = (EntJoy::Mathematics::int2*)((char*)context + 108);
    auto* MaxHash_ptr = (int*)((char*)context + 116);

    SharpNative_Job__global_namespace__GridSearch2D_AssignAndCountJobPointer_Execute_Batch(__startIndex, __count, Positions_ptr, Positions_length, HashIndex_ptr, HashIndex_length, Counts_ptr, Counts_length, Origin_ptr, InvRes_ptr, Dim_ptr, MaxHash_ptr);
}

HEAD void* CALLINGCONVENTION Get_SharpNative_Job__global_namespace__GridSearch2D_AssignAndCountJobPointer_Execute_AdapterPtr()
{
    return (void*)SharpNative_Job__global_namespace__GridSearch2D_AssignAndCountJobPointer_Execute_Adapter;
}
