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


#include "SharpNative_Job__global_namespace__GridSearch2D_PrefixSumJobPointer_Execute.h"

HEAD void CALLINGCONVENTION SharpNative_Job__global_namespace__GridSearch2D_PrefixSumJobPointer_Execute_Adapter(void* context)
{
    auto* Counts_ptr = *(int**)((char*)context + 0);
    int Counts_length = *(int*)((char*)context + 8);
    auto* Length_ptr = (int*)((char*)context + 32);

    SharpNative_Job__global_namespace__GridSearch2D_PrefixSumJobPointer_Execute(Counts_ptr, Counts_length, Length_ptr);
}

HEAD void* CALLINGCONVENTION Get_SharpNative_Job__global_namespace__GridSearch2D_PrefixSumJobPointer_Execute_AdapterPtr()
{
    return (void*)SharpNative_Job__global_namespace__GridSearch2D_PrefixSumJobPointer_Execute_Adapter;
}
