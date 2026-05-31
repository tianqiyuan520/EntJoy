#include "NativeMath.h"
#include "NativeContainers.h"
#include "SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeIspcStatic_ispc.h"

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


HEAD void CALLINGCONVENTION SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeIspcStatic(EntJoy::Mathematics::float2* RESTRICT pos_ptr, int pos_length, EntJoy::Mathematics::float2* RESTRICT vel_ptr, int vel_length, int* RESTRICT count_ptr)
{
    ispc::SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeIspcStatic_impl(reinterpret_cast<ispc::float2*>(pos_ptr), pos_length, reinterpret_cast<ispc::float2*>(vel_ptr), vel_length, count_ptr);
}
