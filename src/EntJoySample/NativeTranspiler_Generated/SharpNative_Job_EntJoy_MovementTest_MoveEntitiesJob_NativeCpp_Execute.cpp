#include "SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeCpp_Execute.h"
#include <algorithm>
#include <cmath>
#include <cstdio>

HEAD void CALLINGCONVENTION SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeCpp_Execute_Batch(int __startIndex, int __count, EntJoy::Mathematics::float2* RESTRICT Positions_ptr, int Positions_length, EntJoy::Mathematics::float2* RESTRICT Velocities_ptr, int Velocities_length, float* RESTRICT Dt_ptr, float* RESTRICT ViewportWidth_ptr, float* RESTRICT ViewportHeight_ptr, int* RESTRICT Count_ptr)
{
    const float& Dt = *Dt_ptr;
    const float& ViewportWidth = *ViewportWidth_ptr;
    const float& ViewportHeight = *ViewportHeight_ptr;
    const int& Count = *Count_ptr;
    for (int index = __startIndex; index < __startIndex + __count; ++index)
    {
EntJoy::Mathematics::float2 pos = Positions_ptr[index];
EntJoy::Mathematics::float2 vel = Velocities_ptr[index];
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

