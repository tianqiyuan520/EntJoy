using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace NativeTranspilerFixture
{
    [NativeTranspile]
    public unsafe struct ColumnViewJob : IJobParallelFor
    {
        public NativeColumn<int> Values;
        public int Addend;

        public void Execute(int index)
        {
            Values[index] += Addend;
            System.Threading.Interlocked.Add(ref *Values.GetElementPtr(index), 1);
        }
    }
}
