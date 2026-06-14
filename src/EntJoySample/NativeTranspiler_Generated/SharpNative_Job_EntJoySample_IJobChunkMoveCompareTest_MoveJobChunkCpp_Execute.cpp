#include "SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkCpp_Execute.h"
#include <algorithm>
#include <cmath>
#include <cstdio>

HEAD void CALLINGCONVENTION SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkCpp_Execute(const ChunkJobData* __chunkData, const int* __requiredComponentTypeIds, float* RESTRICT DeltaTime_ptr)
{
    const float& DeltaTime = *DeltaTime_ptr;
auto* RESTRICT positions_ptr = reinterpret_cast<EntJoySample::IJobChunkMoveCompareTest::MovePosition*>(EntJoy::ChunkNativeArray::GetRequiredChunkComponentArray(__chunkData, 0, __requiredComponentTypeIds[0]));
int positions_length = __chunkData->entityCount;
auto* RESTRICT velocities_ptr = reinterpret_cast<EntJoySample::IJobChunkMoveCompareTest::MoveVelocity*>(EntJoy::ChunkNativeArray::GetRequiredChunkComponentArray(__chunkData, 1, __requiredComponentTypeIds[1]));
int velocities_length = __chunkData->entityCount;
for (int index = 0; index < positions_length; index++)
{
    positions_ptr[index].Value += velocities_ptr[index].Value * DeltaTime;
}
}
