// Auto-generated ISPC source for MoveJobEntityIspc
#include "EntJoyCommon.ispc"
#include "EntJoySample_IJobChunkMoveCompareTest_MovePosition.ispc"
#include "EntJoySample_IJobChunkMoveCompareTest_MoveVelocity.ispc"

export void SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute_impl(uniform MovePosition position_ptr[], uniform MoveVelocity velocity_ptr[], uniform int __entity_count, uniform float * uniform DeltaTime_ptr)
{
    float DeltaTime = *DeltaTime_ptr;
    foreach (__entity_index = 0 ... __entity_count) {
        MovePosition position = position_ptr[__entity_index];
        MoveVelocity velocity = velocity_ptr[__entity_index];
    position.Value = position.Value + velocity.Value * DeltaTime;
        position_ptr[__entity_index] = position;
    }
}
