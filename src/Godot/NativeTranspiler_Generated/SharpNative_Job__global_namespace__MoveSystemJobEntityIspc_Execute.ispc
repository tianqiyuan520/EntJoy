// Auto-generated ISPC source for MoveSystemJobEntityIspc
#include "EntJoyCommon.ispc"
#include "_global_namespace__Position.ispc"
#include "_global_namespace__Vel.ispc"

export void SharpNative_Job__global_namespace__MoveSystemJobEntityIspc_Execute_impl(uniform Position position_ptr[], uniform Vel velocity_ptr[], uniform int __entity_count, uniform float * uniform dt_ptr)
{
    float dt = *dt_ptr;
    foreach (__entity_index = 0 ... __entity_count) {
        Position position = position_ptr[__entity_index];
        Vel velocity = velocity_ptr[__entity_index];
    position.pos.x += velocity.vel.x * dt;
    position.pos.y += velocity.vel.y * dt;
        position_ptr[__entity_index] = position;
    }
}
