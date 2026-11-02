using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace NativeTranspilerFixture
{
    [NativeTranspile]
    public unsafe struct ArrayAliasJob : IJobParallelFor
    {
        public NativeArray<int> Input;
        public NativeArray<int> Output;

        public void Execute(int index)
        {
            if (index >= Input.Length || index >= Output.Length) return;
            int* input = (int*)Input.GetUnsafePtr();
            int* output = (int*)Output.GetUnsafePtr();
            if ((index & 3) == 0) output[index] = input[index];
            else
            {
                output[index] = input[index] + 1;
                output[index] += input[index];
            }
        }
    }
}
