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


#include "SharpNative_Job__global_namespace__GridSearch2D_FillCellStartEndJobPointer_Execute.h"

HEAD void CALLINGCONVENTION SharpNative_Job__global_namespace__GridSearch2D_FillCellStartEndJobPointer_Execute_Adapter(void* context)
{
    auto* SortedHashIndex_ptr = *(EntJoy::Mathematics::int2**)((char*)context + 0);
    int SortedHashIndex_length = *(int*)((char*)context + 8);
    auto* SortedLength_ptr = (int*)((char*)context + 32);
    auto* CellStartEnd_listData = *(EntJoy::Collections::UnsafeList<EntJoy::Mathematics::int2>**)((char*)context + 40);

    SharpNative_Job__global_namespace__GridSearch2D_FillCellStartEndJobPointer_Execute(SortedHashIndex_ptr, SortedHashIndex_length, SortedLength_ptr, CellStartEnd_listData);
}

HEAD void* CALLINGCONVENTION Get_SharpNative_Job__global_namespace__GridSearch2D_FillCellStartEndJobPointer_Execute_AdapterPtr()
{
    return (void*)SharpNative_Job__global_namespace__GridSearch2D_FillCellStartEndJobPointer_Execute_Adapter;
}
