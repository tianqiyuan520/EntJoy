#include "../../NativeDll/NativeMath.h"
#include "../../NativeDll/NativeContainers.h"
#include "../../NativeDll/ChunkJobData.h"
#include "EntJoySample_IJobChunkMoveCompareTest_MovePosition.h"
#include "EntJoySample_IJobChunkMoveCompareTest_MoveVelocity.h"

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


#include "SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkCpp_Execute.h"

struct __EntJoyChunkContextHeader
{
    int chunkCount;
    int hasEnabledFilter;
    void* queryAllEnabledTypes;
    int allEnabledCount;
    int gcHandleStartIndex;
    void* chunksPtr;
    int cleanupInProgress;
    void* requiredComponentTypeIds;
    int requiredComponentTypeIdCount;
};

HEAD void CALLINGCONVENTION SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkCpp_Execute_Adapter(void* context, const ChunkJobData* __chunkData)
{
    auto* __header = (__EntJoyChunkContextHeader*)context;
    int __headerSize = (int)sizeof(__EntJoyChunkContextHeader);
    int __typesDataSize = __header->allEnabledCount * (int)sizeof(int);
    int __requiredTypesDataSize = __header->requiredComponentTypeIdCount * (int)sizeof(int);
    char* __jobContext = (char*)context + __headerSize + __typesDataSize + __requiredTypesDataSize;
    const int* __requiredComponentTypeIds = (const int*)__header->requiredComponentTypeIds;
    auto* DeltaTime_ptr = (float*)(__jobContext + 0);
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkCpp_Execute(__chunkData, __requiredComponentTypeIds, DeltaTime_ptr);
}

HEAD void* CALLINGCONVENTION Get_SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkCpp_Execute_AdapterPtr()
{
    return (void*)SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkCpp_Execute_Adapter;
}
