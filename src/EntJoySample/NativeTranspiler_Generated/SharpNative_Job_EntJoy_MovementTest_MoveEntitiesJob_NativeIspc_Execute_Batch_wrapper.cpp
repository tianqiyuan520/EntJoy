#include "NativeMath.h"
#include "NativeContainers.h"
#include "SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Batch_ispc.h"

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


HEAD void CALLINGCONVENTION SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Batch(int __startIndex, int __count, EntJoy::Mathematics::float2* RESTRICT Positions_ptr, int Positions_length, EntJoy::Mathematics::float2* RESTRICT Velocities_ptr, int Velocities_length, float* RESTRICT Dt_ptr, float* RESTRICT ViewportWidth_ptr, float* RESTRICT ViewportHeight_ptr, int* RESTRICT Count_ptr)
{
    ispc::SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Batch_impl(__startIndex, __count, reinterpret_cast<ispc::float2*>(Positions_ptr), Positions_length, reinterpret_cast<ispc::float2*>(Velocities_ptr), Velocities_length, Dt_ptr, ViewportWidth_ptr, ViewportHeight_ptr, Count_ptr);
}

