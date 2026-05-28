// Auto-generated ISPC source for RunNativeIspcStatic
#include "EntJoyCommon.ispc"

export void SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeIspcStatic_impl(uniform float2 pos_ptr[], uniform int pos_length, uniform float2 vel_ptr[], uniform int vel_length, uniform int * uniform count_ptr)
{
foreach (i = 0 ... (*count_ptr))
{
    float2 p = pos_ptr[i];
    float2 v = vel_ptr[i];
    p.x += v.x * 0.016f;
    p.y += v.y * 0.016f;
    if (p.x < 0.0f || p.x > 1920f)
                v.x = -v.x;
    if (p.y < 0.0f || p.y > 1080f)
                v.y = -v.y;
    pos_ptr[i] = p;
    vel_ptr[i] = v;
}
}
