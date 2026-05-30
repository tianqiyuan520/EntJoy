// Auto-generated ISPC source for NativeMoveJob_NativeIspc
#include "EntJoyCommon.ispc"

export void SharpNative_Job__global_namespace__NativeMoveJob_NativeIspc_Execute_Batch_impl(uniform int __startIndex, uniform int __count, uniform float2 Positions_ptr[], uniform int Positions_length, uniform float2 Velocities_ptr[], uniform int Velocities_length, uniform float * uniform Dt_ptr, uniform float * uniform ViewportWidth_ptr, uniform float * uniform ViewportHeight_ptr)
{
    float Dt = *Dt_ptr;
    float ViewportWidth = *ViewportWidth_ptr;
    float ViewportHeight = *ViewportHeight_ptr;
    foreach (index = __startIndex ... min(__startIndex + __count, Positions_length)) {
float2 pos = Positions_ptr[index];
float2 vel = Velocities_ptr[index];
pos.x += vel.x * Dt;
pos.y += vel.y * Dt;
if (pos.x < 0.0f || pos.x > ViewportWidth)
        vel.x = -vel.x;
if (pos.y < 0.0f || pos.y > ViewportHeight)
        vel.y = -vel.y;
Positions_ptr[index] = pos;
Velocities_ptr[index] = vel;
    }
}
