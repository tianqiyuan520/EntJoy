#include "../../NativeDll/NativeMath.h"
#include "../../NativeDll/NativeContainers.h"
#include "../../NativeDll/ChunkJobData.h"
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

HEAD void CALLINGCONVENTION SharpNative_Job__global_namespace__MoveSystemJobEntityCpp_Execute_Adapter(void* context, const ChunkJobData* __chunkData)
{
    auto* __header = (__EntJoyChunkContextHeader*)context;
    int __headerSize = (int)sizeof(__EntJoyChunkContextHeader);
    int __typesDataSize = __header->allEnabledCount * (int)sizeof(int);
    int __requiredTypesDataSize = __header->requiredComponentTypeIdCount * (int)sizeof(int);
    char* __jobContext = (char*)context + __headerSize + __typesDataSize + __requiredTypesDataSize;
    const int* __requiredComponentTypeIds = (const int*)__header->requiredComponentTypeIds;
    auto dt = *(float*)(__jobContext + 0);
    auto* RESTRICT __entity_param_0_ptr = reinterpret_cast<Position*>(__chunkData->requiredComponentArrays[0]);
    auto* RESTRICT __entity_param_1_ptr = reinterpret_cast<Vel*>(__chunkData->requiredComponentArrays[1]);
    int __entity_count = __chunkData->entityCount;
    for (int __entity_index = 0; __entity_index < __entity_count; ++__entity_index)
    {
        Position& position = __entity_param_0_ptr[__entity_index];
        const Vel& velocity = __entity_param_1_ptr[__entity_index];
        position.pos.x += velocity.vel.x * dt;
        position.pos.y += velocity.vel.y * dt;
    }
}

HEAD void* CALLINGCONVENTION Get_SharpNative_Job__global_namespace__MoveSystemJobEntityCpp_Execute_AdapterPtr()
{
    return (void*)SharpNative_Job__global_namespace__MoveSystemJobEntityCpp_Execute_Adapter;
}
