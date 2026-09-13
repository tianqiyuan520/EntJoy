using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace NativeTranspilerFixture
{
    /// <summary>
    /// F-5 夹具：IJobParallelForBatch。<c>Execute(startIndex, count)</c> 是一次**区间**调用，
    /// C++ 侧不得再包一层 index 循环，且两个形参必须分别映射到 <c>__startIndex</c>/<c>__count</c>。
    /// </summary>
    [NativeTranspile]
    public struct BatchFillJob : IJobParallelForBatch
    {
        public NativeArray<int> Out;
        public NativeArray<int> Visits;

        public void Execute(int startIndex, int count)
        {
            for (int i = startIndex; i < startIndex + count; i++)
            {
                Visits[i] = Visits[i] + 1;   // 每个 index 必须恰好被处理一次
                Out[i] = count;              // 区间长度形参必须可用
            }
        }
    }

    /// <summary>
    /// F-4 夹具：批体内 <c>return;</c> 只结束**本次 index**（C# 语义），
    /// 不得退出整个批（旧实现会跳过本批剩余下标）。
    /// </summary>
    [NativeTranspile]
    public struct EarlyReturnJob : IJobParallelFor
    {
        public NativeArray<int> Mark;

        public void Execute(int index)
        {
            if (index == 0) return;
            Mark[index] = index + 1;
        }
    }

    /// <summary>F-4 夹具（带前后语句）：return 之前的语句必须执行、之后的语句必须被跳过。</summary>
    [NativeTranspile]
    public struct EarlyReturnDeepJob : IJobParallelFor
    {
        public NativeArray<int> Before;
        public NativeArray<int> After;

        public void Execute(int index)
        {
            Before[index] = 1;
            if (index % 3 == 0) return;
            After[index] = 2;
        }
    }
}
