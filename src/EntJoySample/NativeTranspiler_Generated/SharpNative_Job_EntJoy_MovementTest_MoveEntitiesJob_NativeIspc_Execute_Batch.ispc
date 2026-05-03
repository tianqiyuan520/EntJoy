// Auto-generated ISPC source for MoveEntitiesJob_NativeIspc
#include "EntJoyCommon.ispc"

export void SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Batch_impl(uniform int __startIndex, uniform int __count, uniform float2 Positions_ptr[], uniform int Positions_length, uniform float2 Velocities_ptr[], uniform int Velocities_length, uniform float * uniform Dt_ptr, uniform float * uniform ViewportWidth_ptr, uniform float * uniform ViewportHeight_ptr, uniform int * uniform Count_ptr)
{
    uniform float Dt = *Dt_ptr;
    uniform float ViewportWidth = *ViewportWidth_ptr;
    uniform float ViewportHeight = *ViewportHeight_ptr;
    uniform int Count = *Count_ptr;
    // 仅 lane 0 执行 uniform for 循环（防止重复处理同一 ID）
    if (programIndex != 0) return;

    for (uniform int index = __startIndex; index < min(__startIndex + __count, Positions_length); index++) {
uniform float2 pos = Positions_ptr[index];
uniform float2 vel = Velocities_ptr[index];
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
