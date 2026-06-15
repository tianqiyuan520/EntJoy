// Auto-generated ISPC MT source for MoveSystemJobEntityIspc
#include "EntJoyCommon.ispc"
#include "_global_namespace__Position.ispc"
#include "_global_namespace__Vel.ispc"

task void SharpNative_Job__global_namespace__MoveSystemJobEntityIspc_Execute_task(uniform Position position_ptr[], uniform Vel velocity_ptr[], uniform int __entity_count, uniform float * uniform dt_ptr)
{
    uniform int n_per_task = max(1, __entity_count / taskCount);
    uniform int __task_start = taskIndex * n_per_task;
    uniform int __task_end = (taskIndex == taskCount - 1) ? __entity_count : min(__task_start + n_per_task, __entity_count);
    float dt = *dt_ptr;
    foreach (__entity_index = __task_start ... __task_end) {
        Position position = position_ptr[__entity_index];
        Vel velocity = velocity_ptr[__entity_index];
    position.pos.x += velocity.vel.x * dt;
    position.pos.y += velocity.vel.y * dt;
        position_ptr[__entity_index] = position;
    }
}

export void SharpNative_Job__global_namespace__MoveSystemJobEntityIspc_Execute_mt_impl(uniform Position position_ptr[], uniform Vel velocity_ptr[], uniform int __entity_count, uniform float * uniform dt_ptr, uniform int numTasks)
{
    launch[numTasks] SharpNative_Job__global_namespace__MoveSystemJobEntityIspc_Execute_task(position_ptr, velocity_ptr, __entity_count, dt_ptr);
    sync;
}
