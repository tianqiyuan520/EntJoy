#include "NativeMath.h"
#include "NativeContainers.h"
#include "../../NativeDll/EntityBatchData.h"
#include "SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt_ispc.h"
#include <thread>

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

HEAD void CALLINGCONVENTION SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_EntityBatchAdapter(void* context, const EntityBatchData* __batches, int __batch_start, int __batch_count)
{
    auto* __header = (__EntJoyChunkContextHeader*)context;
    int __headerSize = (int)sizeof(__EntJoyChunkContextHeader);
    int __typesDataSize = __header->allEnabledCount * (int)sizeof(int);
    int __requiredTypesDataSize = __header->requiredComponentTypeIdCount * (int)sizeof(int);
    char* __jobContext = (char*)context + __headerSize + __typesDataSize + __requiredTypesDataSize;

    auto* DeltaTime_ptr = (float*)(__jobContext + 0);
    const int __batch_end = __batch_start + __batch_count;
    for (int __batch_index = __batch_start; __batch_index < __batch_end; ++__batch_index)
    {
        const EntityBatchData* __batchData = &__batches[__batch_index];
        auto* position_ptr = reinterpret_cast<ispc::MovePosition*>(__batchData->componentArrays[0]);
        auto* velocity_ptr = reinterpret_cast<ispc::MoveVelocity*>(__batchData->componentArrays[1]);
        ispc::SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt_impl(position_ptr, velocity_ptr, __batchData->entityCount, DeltaTime_ptr, std::thread::hardware_concurrency());
    }
}

HEAD void* CALLINGCONVENTION Get_SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_EntityBatchAdapterPtr()
{
    return (void*)SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_EntityBatchAdapter;
}
