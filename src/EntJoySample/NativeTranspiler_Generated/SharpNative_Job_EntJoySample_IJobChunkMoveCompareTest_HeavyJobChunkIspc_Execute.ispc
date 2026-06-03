// Auto-generated ISPC source for HeavyJobChunkIspc
#include "EntJoyCommon.ispc"
#include "EntJoySample_IJobChunkMoveCompareTest_MovePosition.ispc"
#include "EntJoySample_IJobChunkMoveCompareTest_MoveVelocity.ispc"

export void SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute_impl(uniform MovePosition positions_ptr[], uniform int positions_length, uniform MoveVelocity velocities_ptr[], uniform int velocities_length, uniform float * uniform DeltaTime_ptr)
{
    float DeltaTime = *DeltaTime_ptr;
foreach (index = 0 ... positions_length) {
    MovePosition position = positions_ptr[index];
    MoveVelocity velocity = velocities_ptr[index];
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
        float wave = sin(phaseX) + cos(phaseY);
        float radius = sqrt(accX * accX + accY * accY + 1.0f);
        accX = accX * 0.985f + wave * 0.015f + radius * 0.0002f + vx * 0.0001f;
        accY = accY * 0.982f - wave * 0.012f + radius * 0.0003f + vy * 0.0001f;
    }
    position.Value.x = px + vx * DeltaTime + accX * 0.001f;
    position.Value.y = py + vy * DeltaTime + accY * 0.001f;
    positions_ptr[index] = position;
}
}
