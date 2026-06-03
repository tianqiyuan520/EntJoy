#include "SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkCpp_Execute.h"
#include <algorithm>
#include <cmath>
#include <cstdio>

HEAD void CALLINGCONVENTION SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkCpp_Execute(const ChunkJobData* __chunkData, const int* __requiredComponentTypeIds, float* RESTRICT DeltaTime_ptr)
{
    const float& DeltaTime = *DeltaTime_ptr;
EntJoy::Collections::NativeArray<EntJoySample::IJobChunkMoveCompareTest::MovePosition> positions = EntJoy::ChunkNativeArray::GetChunkNativeArray<EntJoySample::IJobChunkMoveCompareTest::MovePosition>(__chunkData, __requiredComponentTypeIds[0]);
EntJoy::Collections::NativeArray<EntJoySample::IJobChunkMoveCompareTest::MoveVelocity> velocities = EntJoy::ChunkNativeArray::GetChunkNativeArray<EntJoySample::IJobChunkMoveCompareTest::MoveVelocity>(__chunkData, __requiredComponentTypeIds[1]);
for (int index = 0; index < positions.length(); index++)
{
    EntJoySample::IJobChunkMoveCompareTest::MovePosition position = positions[index];
    EntJoySample::IJobChunkMoveCompareTest::MoveVelocity velocity = velocities[index];
    float px = position.Value.x;
    float py = position.Value.y;
    float vx = velocity.Value.x;
    float vy = velocity.Value.y;
    float accX = px * 0.001f + vx * 0.01f;
    float accY = py * 0.001f + vy * 0.01f;
    for (int iteration = 0; iteration < 32; iteration++)
    {
        float phaseX = accX + iteration * 0.03125f;
        float phaseY = accY - iteration * 0.0625f;
        float wave = std::sin(phaseX) + std::cos(phaseY);
        float radius = std::sqrt(accX * accX + accY * accY + 1.0f);
        accX = accX * 0.985f + wave * 0.015f + radius * 0.0002f + vx * 0.0001f;
        accY = accY * 0.982f - wave * 0.012f + radius * 0.0003f + vy * 0.0001f;
    }
    position.Value.x = px + vx * DeltaTime + accX * 0.001f;
    position.Value.y = py + vy * DeltaTime + accY * 0.001f;
    positions[index] = position;
}
}
