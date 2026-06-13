#include "SharpNative_Job__global_namespace__MoveSystemJobCpp_Execute.h"
#include <algorithm>
#include <cmath>
#include <cstdio>

HEAD void CALLINGCONVENTION SharpNative_Job__global_namespace__MoveSystemJobCpp_Execute(const ChunkJobData* __chunkData, const int* __requiredComponentTypeIds, float* RESTRICT dt_ptr)
{
    const float& dt = *dt_ptr;
auto* RESTRICT positions_ptr = reinterpret_cast<Position*>(EntJoy::ChunkNativeArray::GetRequiredChunkComponentArray(__chunkData, 0, __requiredComponentTypeIds[0]));
int positions_length = __chunkData->entityCount;
auto* RESTRICT velocities_ptr = reinterpret_cast<Vel*>(EntJoy::ChunkNativeArray::GetRequiredChunkComponentArray(__chunkData, 1, __requiredComponentTypeIds[1]));
int velocities_length = __chunkData->entityCount;
for (int i = 0; i < positions_length; i++)
{
    Position position = positions_ptr[i];
    Vel velocity = velocities_ptr[i];
    position.pos.x += velocity.vel.x * dt;
    position.pos.y += velocity.vel.y * dt;
    positions_ptr[i] = position;
}
}
