#include "NativeMath.h"
#include "NativeContainers.h"
#include "SharpNative__global_namespace__IspccComparison_FillIspcFast_ispc.h"

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


HEAD void CALLINGCONVENTION SharpNative__global_namespace__IspccComparison_FillIspcFast(float* RESTRICT data_ptr, float* RESTRICT output_ptr)
{
    ispc::SharpNative__global_namespace__IspccComparison_FillIspcFast_impl(data_ptr, output_ptr);
}
