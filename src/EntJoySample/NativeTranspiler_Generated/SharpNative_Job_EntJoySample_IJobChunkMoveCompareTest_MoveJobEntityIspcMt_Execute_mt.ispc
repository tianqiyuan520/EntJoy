// Auto-generated ISPC MT source for MoveJobEntityIspcMt
#include "EntJoyCommon.ispc"
#include "EntJoySample_IJobChunkMoveCompareTest_MovePosition.ispc"
#include "EntJoySample_IJobChunkMoveCompareTest_MoveVelocity.ispc"

task void SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_task(uniform MovePosition position_ptr[], uniform MoveVelocity velocity_ptr[], uniform int __entity_count, uniform float * uniform DeltaTime_ptr)
{
    uniform int n_per_task = max(1, __entity_count / taskCount);
    uniform int __task_start = taskIndex * n_per_task;
    uniform int __task_end = (taskIndex == taskCount - 1) ? __entity_count : min(__task_start + n_per_task, __entity_count);
    float DeltaTime = *DeltaTime_ptr;
    foreach (__entity_index = __task_start ... __task_end) {
        MovePosition position = position_ptr[__entity_index];
        MoveVelocity velocity = velocity_ptr[__entity_index];
    position.Value = position.Value + velocity.Value * DeltaTime;
        position_ptr[__entity_index] = position;
    }
}

export void SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt_impl(uniform MovePosition position_ptr[], uniform MoveVelocity velocity_ptr[], uniform int __entity_count, uniform float * uniform DeltaTime_ptr, uniform int numTasks)
{
    launch[numTasks] SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_task(position_ptr, velocity_ptr, __entity_count, DeltaTime_ptr);
    sync;
}
