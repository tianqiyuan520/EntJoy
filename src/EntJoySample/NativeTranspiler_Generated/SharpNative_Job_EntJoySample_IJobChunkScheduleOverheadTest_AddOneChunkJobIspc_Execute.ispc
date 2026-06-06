// Auto-generated ISPC source for AddOneChunkJobIspc
#include "EntJoyCommon.ispc"
#include "EntJoySample_IJobChunkScheduleOverheadTest_ScheduleValue.ispc"

export void SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_AddOneChunkJobIspc_Execute_impl(uniform ScheduleValue values_ptr[], uniform int values_length)
{
foreach (index = 0 ... values_length) {
    ScheduleValue value = values_ptr[index];
    value.Value += 1;
    values_ptr[index] = value;
}
}
