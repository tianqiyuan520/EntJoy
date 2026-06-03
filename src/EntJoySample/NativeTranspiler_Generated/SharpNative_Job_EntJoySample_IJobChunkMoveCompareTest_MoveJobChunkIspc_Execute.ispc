// Auto-generated ISPC source for MoveJobChunkIspc
#include "EntJoyCommon.ispc"
#include "EntJoySample_IJobChunkMoveCompareTest_MovePosition.ispc"
#include "EntJoySample_IJobChunkMoveCompareTest_MoveVelocity.ispc"

export void SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute_impl(uniform MovePosition positions_ptr[], uniform int positions_length, uniform MoveVelocity velocities_ptr[], uniform int velocities_length, uniform float * uniform DeltaTime_ptr)
{
    float DeltaTime = *DeltaTime_ptr;
foreach (index = 0 ... positions_length) {
    MovePosition position = positions_ptr[index];
    position.Value = position.Value + velocities_ptr[index].Value * DeltaTime;
    positions_ptr[index] = position;
}
}
