#include "SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeCppStatic.h"
#include "EntJoySample_IJobChunkScheduleOverheadTest_ScheduleValue.h"
#include "EntJoySample_IJobChunkMoveCompareTest_MovePosition.h"
#include "EntJoySample_IJobChunkMoveCompareTest_MoveVelocity.h"
#include "EntJoySample_SimpleIJobChunkTest_TestValue.h"
#include <algorithm>
#include <cmath>
#include <cstdio>

HEAD void CALLINGCONVENTION SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeCppStatic(EntJoy::Mathematics::float2* RESTRICT pos_ptr, int pos_length, EntJoy::Mathematics::float2* RESTRICT vel_ptr, int vel_length, int* RESTRICT count_ptr)
{
    int& count = *count_ptr;
for (int i = 0; i < count; i++)
{
    EntJoy::Mathematics::float2 p = pos_ptr[i];
    EntJoy::Mathematics::float2 v = vel_ptr[i];
    p.x += v.x * 0.016f;
    p.y += v.y * 0.016f;
    v.x = p.x < 0.0f || p.x > 1920.0f ? -v.x : v.x;
    v.y = p.y < 0.0f || p.y > 1080.0f ? -v.y : v.y;
    pos_ptr[i] = p;
    vel_ptr[i] = v;
}
}
