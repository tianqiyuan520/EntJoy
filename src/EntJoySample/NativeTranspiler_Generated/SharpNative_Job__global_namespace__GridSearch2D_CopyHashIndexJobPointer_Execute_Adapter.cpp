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
HEAD void CALLINGCONVENTION SharpNative_Job__global_namespace__GridSearch2D_CopyHashIndexJobPointer_Execute_Batch(int __startIndex, int __count, EntJoy::Mathematics::int2* RESTRICT Src_ptr, int Src_length, EntJoy::Mathematics::int2* RESTRICT Dst_ptr, int Dst_length);

HEAD void CALLINGCONVENTION SharpNative_Job__global_namespace__GridSearch2D_CopyHashIndexJobPointer_Execute_Adapter(void* context, int __startIndex, int __count)
{
    auto* Src_ptr = *(EntJoy::Mathematics::int2**)((char*)context + 0);
    int Src_length = *(int*)((char*)context + 8);
    auto* Dst_ptr = *(EntJoy::Mathematics::int2**)((char*)context + 32);
    int Dst_length = *(int*)((char*)context + 40);

    SharpNative_Job__global_namespace__GridSearch2D_CopyHashIndexJobPointer_Execute_Batch(__startIndex, __count, Src_ptr, Src_length, Dst_ptr, Dst_length);
}

HEAD void* CALLINGCONVENTION Get_SharpNative_Job__global_namespace__GridSearch2D_CopyHashIndexJobPointer_Execute_AdapterPtr()
{
    return (void*)SharpNative_Job__global_namespace__GridSearch2D_CopyHashIndexJobPointer_Execute_Adapter;
}
