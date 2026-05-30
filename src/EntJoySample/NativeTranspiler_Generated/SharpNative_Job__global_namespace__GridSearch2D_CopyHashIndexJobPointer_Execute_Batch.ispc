// Auto-generated ISPC source for CopyHashIndexJobPointer
#include "EntJoyCommon.ispc"

export void SharpNative_Job__global_namespace__GridSearch2D_CopyHashIndexJobPointer_Execute_Batch_impl(uniform int __startIndex, uniform int __count, uniform int2 Src_ptr[], uniform int Src_length, uniform int2 Dst_ptr[], uniform int Dst_length)
{
    foreach (index = __startIndex ... min(__startIndex + __count, Src_length)) {
Dst_ptr[index] = Src_ptr[index];
    }
}
