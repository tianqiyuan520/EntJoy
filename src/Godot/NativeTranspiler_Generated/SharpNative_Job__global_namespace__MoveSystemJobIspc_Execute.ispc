// Auto-generated ISPC source for MoveSystemJobIspc
#include "EntJoyCommon.ispc"
#include "_global_namespace__Position.ispc"
#include "_global_namespace__Vel.ispc"

export void SharpNative_Job__global_namespace__MoveSystemJobIspc_Execute_impl(uniform Position positions_ptr[], uniform int positions_length, uniform Vel velocities_ptr[], uniform int velocities_length, uniform float * uniform dt_ptr)
{
    float dt = *dt_ptr;
foreach (i = 0 ... positions_length) {
    Position position = positions_ptr[i];
    Vel velocity = velocities_ptr[i];
    position.pos.x += velocity.vel.x * dt;
    position.pos.y += velocity.vel.y * dt;
    positions_ptr[i] = position;
}
}
