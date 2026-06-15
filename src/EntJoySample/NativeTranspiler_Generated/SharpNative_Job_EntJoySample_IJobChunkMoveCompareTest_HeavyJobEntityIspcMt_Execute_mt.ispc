// Auto-generated ISPC MT source for HeavyJobEntityIspcMt
#include "EntJoyCommon.ispc"
#include "EntJoySample_IJobChunkMoveCompareTest_MovePosition.ispc"
#include "EntJoySample_IJobChunkMoveCompareTest_MoveVelocity.ispc"

task void SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_task(uniform MovePosition position_ptr[], uniform MoveVelocity velocity_ptr[], uniform int __entity_count, uniform float * uniform DeltaTime_ptr)
{
    uniform int n_per_task = max(1, __entity_count / taskCount);
    uniform int __task_start = taskIndex * n_per_task;
    uniform int __task_end = (taskIndex == taskCount - 1) ? __entity_count : min(__task_start + n_per_task, __entity_count);
    float DeltaTime = *DeltaTime_ptr;
    foreach (__entity_index = __task_start ... __task_end) {
        MovePosition position = position_ptr[__entity_index];
        MoveVelocity velocity = velocity_ptr[__entity_index];
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
        position_ptr[__entity_index] = position;
    }
}

export void SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt_impl(uniform MovePosition position_ptr[], uniform MoveVelocity velocity_ptr[], uniform int __entity_count, uniform float * uniform DeltaTime_ptr, uniform int numTasks)
{
    launch[numTasks] SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_task(position_ptr, velocity_ptr, __entity_count, DeltaTime_ptr);
    sync;
}
