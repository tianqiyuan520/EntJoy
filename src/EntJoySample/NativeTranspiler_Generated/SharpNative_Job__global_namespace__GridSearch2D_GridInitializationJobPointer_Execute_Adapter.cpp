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


#include "SharpNative_Job__global_namespace__GridSearch2D_GridInitializationJobPointer_Execute.h"

HEAD void CALLINGCONVENTION SharpNative_Job__global_namespace__GridSearch2D_GridInitializationJobPointer_Execute_Adapter(void* context)
{
    auto* Positions_ptr = *(EntJoy::Mathematics::float2**)((char*)context + 0);
    int Positions_length = *(int*)((char*)context + 8);
    auto* PositionLength_ptr = (int*)((char*)context + 32);
    auto* GridResolution_ptr = *(float**)((char*)context + 40);
    int GridResolution_length = *(int*)((char*)context + 48);
    auto* TargetGridSize_ptr = (int*)((char*)context + 72);
    auto* MinMaxPositions_ptr = *(EntJoy::Mathematics::float2**)((char*)context + 80);
    int MinMaxPositions_length = *(int*)((char*)context + 88);
    auto* GridDimensions_ptr = *(EntJoy::Mathematics::int2**)((char*)context + 112);
    int GridDimensions_length = *(int*)((char*)context + 120);
    auto* CellStartEnd_listData = *(EntJoy::Collections::UnsafeList<EntJoy::Mathematics::int2>**)((char*)context + 144);

    SharpNative_Job__global_namespace__GridSearch2D_GridInitializationJobPointer_Execute(Positions_ptr, Positions_length, PositionLength_ptr, GridResolution_ptr, GridResolution_length, TargetGridSize_ptr, MinMaxPositions_ptr, MinMaxPositions_length, GridDimensions_ptr, GridDimensions_length, CellStartEnd_listData);
}

HEAD void* CALLINGCONVENTION Get_SharpNative_Job__global_namespace__GridSearch2D_GridInitializationJobPointer_Execute_AdapterPtr()
{
    return (void*)SharpNative_Job__global_namespace__GridSearch2D_GridInitializationJobPointer_Execute_Adapter;
}
