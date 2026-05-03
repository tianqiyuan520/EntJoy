#include "NativeMath.h"
#include "NativeContainers.h"
#include "SharpNative_Job__global_namespace__ISPCFastJob_Execute_Batch_ispc.h"

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


HEAD void CALLINGCONVENTION SharpNative_Job__global_namespace__ISPCFastJob_Execute_Batch(int __startIndex, int __count, float* RESTRICT Input_ptr, float* RESTRICT Output_ptr)
{
    ispc::SharpNative_Job__global_namespace__ISPCFastJob_Execute_Batch_impl(__startIndex, __count, Input_ptr, Output_ptr);
}

