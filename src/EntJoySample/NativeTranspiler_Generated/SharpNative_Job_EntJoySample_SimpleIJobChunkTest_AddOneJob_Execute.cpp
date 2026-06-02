#include "SharpNative_Job_EntJoySample_SimpleIJobChunkTest_AddOneJob_Execute.h"
#include <algorithm>
#include <cmath>
#include <cstdio>

HEAD void CALLINGCONVENTION SharpNative_Job_EntJoySample_SimpleIJobChunkTest_AddOneJob_Execute(const ChunkJobData* __chunkData, const int* __requiredComponentTypeIds)
{
EntJoy::Collections::NativeArray<EntJoySample::SimpleIJobChunkTest::TestValue> values = EntJoy::ChunkNativeArray::GetChunkNativeArray<EntJoySample::SimpleIJobChunkTest::TestValue>(__chunkData, __requiredComponentTypeIds[0]);
for (int index = 0; index < values.length(); index++)
{
    EntJoySample::SimpleIJobChunkTest::TestValue value = values[index];
    value.Value += 1;
    values[index] = value;
}
}
