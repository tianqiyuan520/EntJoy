#include "SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkCppFast_Execute.h"
#include <algorithm>
#include <cmath>
#include <cstdio>

HEAD void CALLINGCONVENTION SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkCppFast_Execute(const ChunkJobData* __chunkData, const int* __requiredComponentTypeIds, float* RESTRICT DeltaTime_ptr)
{
    const float& DeltaTime = *DeltaTime_ptr;
auto* RESTRICT positions_ptr = reinterpret_cast<EntJoySample::IJobChunkMoveCompareTest::MovePosition*>(EntJoy::ChunkNativeArray::GetRequiredChunkComponentArray(__chunkData, 0, __requiredComponentTypeIds[0]));
int positions_length = __chunkData->entityCount;
auto* RESTRICT velocities_ptr = reinterpret_cast<EntJoySample::IJobChunkMoveCompareTest::MoveVelocity*>(EntJoy::ChunkNativeArray::GetRequiredChunkComponentArray(__chunkData, 1, __requiredComponentTypeIds[1]));
int velocities_length = __chunkData->entityCount;
for (int index = 0; index < positions_length; index++)
{
    EntJoySample::IJobChunkMoveCompareTest::MovePosition position = positions_ptr[index];
    EntJoySample::IJobChunkMoveCompareTest::MoveVelocity velocity = velocities_ptr[index];
    float px = position.Value.x;
    float py = position.Value.y;
    float vx = velocity.Value.x;
    float vy = velocity.Value.y;
    float accX = px * 0.001f + vx * 0.01f;
    float accY = py * 0.001f + vy * 0.01f;
    for (int iteration = 0; iteration < 16; iteration++)
    {
        float phaseX = accX + iteration * 0.03125f;
        float phaseY = accY - iteration * 0.0625f;
        float wave = EntJoy::FastMath::Sin(phaseX) + EntJoy::FastMath::Cos(phaseY);
        float radius = EntJoy::FastMath::Sqrt(accX * accX + accY * accY + 1.0f);
        accX = accX * 0.985f + wave * 0.015f + radius * 0.0002f + vx * 0.0001f;
        accY = accY * 0.982f - wave * 0.012f + radius * 0.0003f + vy * 0.0001f;
    }
    position.Value.x = px + vx * DeltaTime + accX * 0.001f;
    position.Value.y = py + vy * DeltaTime + accY * 0.001f;
    positions_ptr[index] = position;
}
}
