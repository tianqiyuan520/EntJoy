#include "../../NativeDll/NativeMath.h"
#include "../../NativeDll/NativeContainers.h"
#include "../../NativeDll/ChunkJobData.h"
#include "../../NativeDll/EntityBatchData.h"
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


#include "SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityCpp_Execute.h"

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

HEAD void CALLINGCONVENTION SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityCpp_Execute_EntityBatchAdapter(void* context, const EntityBatchData* __batches, int __batch_start, int __batch_count)
{
    auto* __header = (__EntJoyChunkContextHeader*)context;
    int __headerSize = (int)sizeof(__EntJoyChunkContextHeader);
    int __typesDataSize = __header->allEnabledCount * (int)sizeof(int);
    int __requiredTypesDataSize = __header->requiredComponentTypeIdCount * (int)sizeof(int);
    char* __jobContext = (char*)context + __headerSize + __typesDataSize + __requiredTypesDataSize;
    auto DeltaTime = *(float*)(__jobContext + 0);
    const int __batch_end = __batch_start + __batch_count;
    for (int __batch_index = __batch_start; __batch_index < __batch_end; ++__batch_index)
    {
        const EntityBatchData* __batchData = &__batches[__batch_index];
        auto* RESTRICT __entity_param_0_ptr = reinterpret_cast<EntJoySample::IJobChunkMoveCompareTest::MovePosition*>(__batchData->componentArrays[0]);
        const auto* RESTRICT __entity_param_1_ptr = reinterpret_cast<const EntJoySample::IJobChunkMoveCompareTest::MoveVelocity*>(__batchData->componentArrays[1]);
        int __entity_count = __batchData->entityCount;
        for (int __entity_index = 0; __entity_index < __entity_count; ++__entity_index)
        {
            float px = __entity_param_0_ptr[__entity_index].Value.x;
            float py = __entity_param_0_ptr[__entity_index].Value.y;
            float vx = __entity_param_1_ptr[__entity_index].Value.x;
            float vy = __entity_param_1_ptr[__entity_index].Value.y;
            float accX = px * 0.001f + vx * 0.01f;
            float accY = py * 0.001f + vy * 0.01f;
            for (int iteration = 0; iteration < 16; iteration++)
            {
                float phaseX = accX + iteration * 0.03125f;
                float phaseY = accY - iteration * 0.0625f;
                float wave = ::sinf(phaseX) + ::cosf(phaseY);
                float radius = ::sqrtf(accX * accX + accY * accY + 1.0f);
                accX = accX * 0.985f + wave * 0.015f + radius * 0.0002f + vx * 0.0001f;
                accY = accY * 0.982f - wave * 0.012f + radius * 0.0003f + vy * 0.0001f;
            }
            __entity_param_0_ptr[__entity_index].Value.x = px + vx * DeltaTime + accX * 0.001f;
            __entity_param_0_ptr[__entity_index].Value.y = py + vy * DeltaTime + accY * 0.001f;
        }
    }
}

HEAD void* CALLINGCONVENTION Get_SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityCpp_Execute_EntityBatchAdapterPtr()
{
    return (void*)SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityCpp_Execute_EntityBatchAdapter;
}
