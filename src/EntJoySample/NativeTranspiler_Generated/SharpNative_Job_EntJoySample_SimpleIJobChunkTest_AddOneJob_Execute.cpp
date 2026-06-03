#include "SharpNative_Job_EntJoySample_SimpleIJobChunkTest_AddOneJob_Execute.h"
#include <algorithm>
#include <cmath>
#include <cstdio>

HEAD void CALLINGCONVENTION SharpNative_Job_EntJoySample_SimpleIJobChunkTest_AddOneJob_Execute(const ChunkJobData* __chunkData, const int* __requiredComponentTypeIds)
{
auto* RESTRICT values_ptr = reinterpret_cast<EntJoySample::SimpleIJobChunkTest::TestValue*>(EntJoy::ChunkNativeArray::GetRequiredChunkComponentArray(__chunkData, 0, __requiredComponentTypeIds[0]));
int values_length = __chunkData->entityCount;
for (int index = 0; index < values_length; index++)
{
    EntJoySample::SimpleIJobChunkTest::TestValue value = values_ptr[index];
    value.Value += 1;
    values_ptr[index] = value;
}
}
