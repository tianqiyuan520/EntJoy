#include "NativeMath.h"
#include "NativeContainers.h"
#include "SharpNative_Job__global_namespace__GridSearch2D_AssignAndCountJobPointer_Execute_Batch_ispc.h"

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


HEAD void CALLINGCONVENTION SharpNative_Job__global_namespace__GridSearch2D_AssignAndCountJobPointer_Execute_Batch(int __startIndex, int __count, EntJoy::Mathematics::float2* RESTRICT Positions_ptr, int Positions_length, EntJoy::Mathematics::int2* RESTRICT HashIndex_ptr, int HashIndex_length, int* RESTRICT Counts_ptr, int Counts_length, EntJoy::Mathematics::float2* RESTRICT Origin_ptr, float* RESTRICT InvRes_ptr, EntJoy::Mathematics::int2* RESTRICT Dim_ptr, int* RESTRICT MaxHash_ptr)
{
    ispc::SharpNative_Job__global_namespace__GridSearch2D_AssignAndCountJobPointer_Execute_Batch_impl(__startIndex, __count, reinterpret_cast<ispc::float2*>(Positions_ptr), Positions_length, reinterpret_cast<ispc::int2*>(HashIndex_ptr), HashIndex_length, Counts_ptr, Counts_length, reinterpret_cast<ispc::float2*>(Origin_ptr), InvRes_ptr, reinterpret_cast<ispc::int2*>(Dim_ptr), MaxHash_ptr);
}

