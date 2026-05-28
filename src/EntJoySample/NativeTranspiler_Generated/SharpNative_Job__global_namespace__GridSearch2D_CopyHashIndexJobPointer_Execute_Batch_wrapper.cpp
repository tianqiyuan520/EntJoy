#include "NativeMath.h"
#include "NativeContainers.h"
#include "SharpNative_Job__global_namespace__GridSearch2D_CopyHashIndexJobPointer_Execute_Batch_ispc.h"

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


HEAD void CALLINGCONVENTION SharpNative_Job__global_namespace__GridSearch2D_CopyHashIndexJobPointer_Execute_Batch(int __startIndex, int __count, EntJoy::Mathematics::int2* RESTRICT Src_ptr, int Src_length, EntJoy::Mathematics::int2* RESTRICT Dst_ptr, int Dst_length)
{
    ispc::SharpNative_Job__global_namespace__GridSearch2D_CopyHashIndexJobPointer_Execute_Batch_impl(__startIndex, __count, reinterpret_cast<ispc::int2*>(Src_ptr), Src_length, reinterpret_cast<ispc::int2*>(Dst_ptr), Dst_length);
}

