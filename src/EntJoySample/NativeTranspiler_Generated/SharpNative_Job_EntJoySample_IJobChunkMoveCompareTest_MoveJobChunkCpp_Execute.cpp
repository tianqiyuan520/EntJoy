#include "SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkCpp_Execute.h"
#include <algorithm>
#include <cmath>
#include <cstdio>

HEAD void CALLINGCONVENTION SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkCpp_Execute(const ChunkJobData* __chunkData, const int* __requiredComponentTypeIds, float* RESTRICT DeltaTime_ptr)
{
    const float& DeltaTime = *DeltaTime_ptr;
EntJoy::Collections::NativeArray<EntJoySample::IJobChunkMoveCompareTest::MovePosition> positions = EntJoy::ChunkNativeArray::GetChunkNativeArray<EntJoySample::IJobChunkMoveCompareTest::MovePosition>(__chunkData, __requiredComponentTypeIds[0]);
EntJoy::Collections::NativeArray<EntJoySample::IJobChunkMoveCompareTest::MoveVelocity> velocities = EntJoy::ChunkNativeArray::GetChunkNativeArray<EntJoySample::IJobChunkMoveCompareTest::MoveVelocity>(__chunkData, __requiredComponentTypeIds[1]);
for (int index = 0; index < positions.length(); index++)
{
    EntJoySample::IJobChunkMoveCompareTest::MovePosition position = positions[index];
    position.Value += velocities[index].Value * DeltaTime;
    positions[index] = position;
}
}
