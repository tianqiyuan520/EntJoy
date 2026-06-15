#include "../../NativeDll/NativeMath.h"
#include "../../NativeDll/NativeContainers.h"
#include "../../NativeDll/ChunkJobData.h"
#include "../../NativeDll/EntityBatchData.h"
#include "_global_namespace__Position.h"
#include "_global_namespace__Vel.h"

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


#include "SharpNative_Job__global_namespace__MoveSystemJobEntityCpp_Execute.h"

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

HEAD void CALLINGCONVENTION SharpNative_Job__global_namespace__MoveSystemJobEntityCpp_Execute_EntityBatchAdapter(void* context, const EntityBatchData* __batches, int __batch_start, int __batch_count)
{
    auto* __header = (__EntJoyChunkContextHeader*)context;
    int __headerSize = (int)sizeof(__EntJoyChunkContextHeader);
    int __typesDataSize = __header->allEnabledCount * (int)sizeof(int);
    int __requiredTypesDataSize = __header->requiredComponentTypeIdCount * (int)sizeof(int);
    char* __jobContext = (char*)context + __headerSize + __typesDataSize + __requiredTypesDataSize;
    auto dt = *(float*)(__jobContext + 0);
    const int __batch_end = __batch_start + __batch_count;
    for (int __batch_index = __batch_start; __batch_index < __batch_end; ++__batch_index)
    {
        const EntityBatchData* __batchData = &__batches[__batch_index];
        auto* RESTRICT __entity_param_0_ptr = reinterpret_cast<Position*>(__batchData->componentArrays[0]);
        const auto* RESTRICT __entity_param_1_ptr = reinterpret_cast<const Vel*>(__batchData->componentArrays[1]);
        int __entity_count = __batchData->entityCount;
        for (int __entity_index = 0; __entity_index < __entity_count; ++__entity_index)
        {
            __entity_param_0_ptr[__entity_index].pos.x += __entity_param_1_ptr[__entity_index].vel.x * dt;
            __entity_param_0_ptr[__entity_index].pos.y += __entity_param_1_ptr[__entity_index].vel.y * dt;
        }
    }
}

HEAD void* CALLINGCONVENTION Get_SharpNative_Job__global_namespace__MoveSystemJobEntityCpp_Execute_EntityBatchAdapterPtr()
{
    return (void*)SharpNative_Job__global_namespace__MoveSystemJobEntityCpp_Execute_EntityBatchAdapter;
}
