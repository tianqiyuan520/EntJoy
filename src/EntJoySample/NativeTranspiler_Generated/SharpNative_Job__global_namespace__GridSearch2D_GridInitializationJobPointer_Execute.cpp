#include "SharpNative_Job__global_namespace__GridSearch2D_GridInitializationJobPointer_Execute.h"
#include <algorithm>
#include <cmath>
#include <cstdio>

HEAD void CALLINGCONVENTION SharpNative_Job__global_namespace__GridSearch2D_GridInitializationJobPointer_Execute(EntJoy::Mathematics::float2* RESTRICT Positions_ptr, int Positions_length, int* RESTRICT PositionLength_ptr, float* RESTRICT GridResolution_ptr, int GridResolution_length, int* RESTRICT TargetGridSize_ptr, EntJoy::Mathematics::float2* RESTRICT MinMaxPositions_ptr, int MinMaxPositions_length, EntJoy::Mathematics::int2* RESTRICT GridDimensions_ptr, int GridDimensions_length, EntJoy::Collections::UnsafeList<EntJoy::Mathematics::int2>* RESTRICT CellStartEnd_listData)
{
    EntJoy::Collections::UnsafeList<EntJoy::Mathematics::int2>& CellStartEnd = *CellStartEnd_listData;
    const int& PositionLength = *PositionLength_ptr;
    const int& TargetGridSize = *TargetGridSize_ptr;
EntJoy::Mathematics::float2 min = Positions_ptr[0];
EntJoy::Mathematics::float2 max = Positions_ptr[0];
for (int i = 1; i < PositionLength; i++)
{
    min = EntJoy::Mathematics::min(min, Positions_ptr[i]);
    max = EntJoy::Mathematics::max(max, Positions_ptr[i]);
}
MinMaxPositions_ptr[0] = min;
MinMaxPositions_ptr[1] = max;
EntJoy::Mathematics::float2 range = max - min;
float maxRange = EntJoy::Mathematics::max(range.x, range.y);
float resolution = GridResolution_ptr[0];
if (resolution <= 0.0f)
{
    resolution = maxRange / TargetGridSize;
    GridResolution_ptr[0] = resolution;
}
EntJoy::Mathematics::int2 dim = EntJoy::Mathematics::int2(EntJoy::Mathematics::max(1, ((int)EntJoy::Mathematics::ceil(range.x / resolution))), EntJoy::Mathematics::max(1, ((int)EntJoy::Mathematics::ceil(range.y / resolution))));
if (dim.x > 1024 || dim.y > 1024)
        return;
GridDimensions_ptr[0] = dim;
int cellCount = dim.x * dim.y;
CellStartEnd.Resize(cellCount, static_cast<EntJoy::Collections::NativeArrayOptions>(0));
}
