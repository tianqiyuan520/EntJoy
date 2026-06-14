#include "SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_AddOneChunkJobCpp_Execute.h"
#include <algorithm>
#include <cmath>
#include <cstdio>

HEAD void CALLINGCONVENTION SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_AddOneChunkJobCpp_Execute(const ChunkJobData* __chunkData, const int* __requiredComponentTypeIds)
{
auto* RESTRICT values_ptr = reinterpret_cast<EntJoySample::IJobChunkScheduleOverheadTest::ScheduleValue*>(EntJoy::ChunkNativeArray::GetRequiredChunkComponentArray(__chunkData, 0, __requiredComponentTypeIds[0]));
int values_length = __chunkData->entityCount;
for (int index = 0; index < values_length; index++)
{
    values_ptr[index].Value += 1;
}
}
