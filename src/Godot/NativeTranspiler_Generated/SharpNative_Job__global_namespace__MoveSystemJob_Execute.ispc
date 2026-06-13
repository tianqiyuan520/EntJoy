// Auto-generated ISPC source for MoveSystemJob
#include "EntJoyCommon.ispc"
#include "_global_namespace__Position.ispc"
#include "_global_namespace__Vel.ispc"

export void SharpNative_Job__global_namespace__MoveSystemJob_Execute_impl(uniform float * uniform dt_ptr)
{
    float dt = *dt_ptr;
Span positions = chunk.GetComponentDataSpan();
Span velocities = chunk.GetComponentDataSpan();
int count = chunk.Count;
for (int i = 0; i < count; i++)
{
    positions[i].pos.X += velocities[i].vel.X * dt;
    positions[i].pos.Y += velocities[i].vel.Y * dt;
}
}
