// Auto-generated ISPC MT source for MoveSystemJobIspc
#include "EntJoyCommon.ispc"
#include "_global_namespace__Position.ispc"
#include "_global_namespace__Vel.ispc"

task void SharpNative_Job__global_namespace__MoveSystemJobIspc_Execute_task(uniform Position positions_ptr[], uniform int positions_length, uniform Vel velocities_ptr[], uniform int velocities_length, uniform float * uniform dt_ptr, uniform int __entity_count)
{
    uniform int n_per_task = max(1, positions_length / taskCount);
    uniform int __task_start = taskIndex * n_per_task;
    uniform int __task_end = (taskIndex == taskCount - 1) ? positions_length : min(__task_start + n_per_task, positions_length);
    float dt = *dt_ptr;
foreach (i = __task_start ... __task_end) {
    Position position = positions_ptr[i];
    Vel velocity = velocities_ptr[i];
    position.pos.x += velocity.vel.x * dt;
    position.pos.y += velocity.vel.y * dt;
    positions_ptr[i] = position;
}
}

export void SharpNative_Job__global_namespace__MoveSystemJobIspc_Execute_mt_impl(uniform Position positions_ptr[], uniform int positions_length, uniform Vel velocities_ptr[], uniform int velocities_length, uniform float * uniform dt_ptr, uniform int __entity_count, uniform int numTasks)
{
    launch[numTasks] SharpNative_Job__global_namespace__MoveSystemJobIspc_Execute_task(positions_ptr, positions_length, velocities_ptr, velocities_length, dt_ptr, __entity_count);
    sync;
}
