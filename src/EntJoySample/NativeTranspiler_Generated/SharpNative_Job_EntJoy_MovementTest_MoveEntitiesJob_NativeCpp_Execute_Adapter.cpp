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


#include "SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeCpp_Execute.h"

HEAD void CALLINGCONVENTION SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeCpp_Execute_Adapter(void* context, int __startIndex, int __count)
{
    auto* Positions_ptr = *(EntJoy::Mathematics::float2**)((char*)context + 0);
    int Positions_length = *(int*)((char*)context + 8);
    auto* Velocities_ptr = *(EntJoy::Mathematics::float2**)((char*)context + 32);
    int Velocities_length = *(int*)((char*)context + 40);
    auto* Dt_ptr = (float*)((char*)context + 64);
    auto* ViewportWidth_ptr = (float*)((char*)context + 68);
    auto* ViewportHeight_ptr = (float*)((char*)context + 72);
    auto* Count_ptr = (int*)((char*)context + 76);

    SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeCpp_Execute_Batch(__startIndex, __count, Positions_ptr, Positions_length, Velocities_ptr, Velocities_length, Dt_ptr, ViewportWidth_ptr, ViewportHeight_ptr, Count_ptr);
}

HEAD void* CALLINGCONVENTION Get_SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeCpp_Execute_AdapterPtr()
{
    return (void*)SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeCpp_Execute_Adapter;
}
