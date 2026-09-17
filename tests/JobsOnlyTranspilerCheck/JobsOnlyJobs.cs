using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace JobsOnlyTranspilerCheck
{
    // 本工程刻意不引用 EntJoy.ECS：验证 [NativeTranspile] 的数组类 job 在纯 JobSystem 消费方可用。
    // 覆盖 4 种数组形态（IJob / IJobFor / IJobParallelFor / IJobParallelForBatch）。
    // 配套检查：check.ps1（4 条断言，含"守卫不可被绕过"自证）。

    [NativeTranspile(Target = BackendTarget.Cpp)]
    public struct CppSingleJob : IJob
    {
        public NativeArray<int> Values;
        public int Delta;

        public void Execute()
        {
            for (int i = 0; i < Values.Length; i++)
            {
                Values[i] += Delta;
            }
        }
    }

    [NativeTranspile(Target = BackendTarget.Cpp)]
    public struct CppForJob : IJobFor
    {
        public NativeArray<int> Values;
        public int Delta;

        public void Execute(int index)
        {
            Values[index] += Delta;
        }
    }

    [NativeTranspile(Target = BackendTarget.Cpp)]
    public struct CppParallelForJob : IJobParallelFor
    {
        public NativeArray<int> Values;
        public int Delta;

        public void Execute(int index)
        {
            Values[index] += Delta;
        }
    }

    [NativeTranspile(Target = BackendTarget.Cpp)]
    public struct CppParallelForBatchJob : IJobParallelForBatch
    {
        public NativeArray<int> Values;
        public int Delta;

        public void Execute(int startIndex, int count)
        {
            for (int i = startIndex; i < startIndex + count; i++)
            {
                Values[i] += Delta;
            }
        }
    }
}
