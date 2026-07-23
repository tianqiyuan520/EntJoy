// Case 5: Gather + Reduce — 间接索引 gather + 距离计算 + 归约
// 测试 gather + inner reduction + mask 链

using EntJoy.Collections;

namespace EntJoySample.AutoSIMDTest
{
    public struct GatherReduce_CSharp : IJobParallelFor
    {
        public NativeArray<float> QueryX, QueryY;
        public NativeArray<float> DataX, DataY;
        public NativeArray<int> Index;        // 间接索引: Index[i*50 .. i*50+49]
        public NativeArray<float> Result;     // 最佳距离
        public void Execute(int i)
        {
            float qx = QueryX[i], qy = QueryY[i];
            float best = float.MaxValue;
            for (int j = 0; j < 50; j++)
            {
                int idx = Index[i * 50 + j];
                float dx = qx - DataX[idx];
                float dy = qy - DataY[idx];
                float distSq = dx * dx + dy * dy;
                if (distSq < best)
                    best = distSq;
            }
            Result[i] = best;
        }
    }

    [NativeTranspiler.NativeTranspile]
    public struct GatherReduce_Cpp : IJobParallelFor
    {
        public NativeArray<float> QueryX, QueryY;
        public NativeArray<float> DataX, DataY;
        public NativeArray<int> Index;
        public NativeArray<float> Result;
        public void Execute(int i)
        {
            float qx = QueryX[i], qy = QueryY[i];
            float best = float.MaxValue;
            for (int j = 0; j < 50; j++)
            {
                int idx = Index[i * 50 + j];
                float dx = qx - DataX[idx];
                float dy = qy - DataY[idx];
                float distSq = dx * dx + dy * dy;
                if (distSq < best)
                    best = distSq;
            }
            Result[i] = best;
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct GatherReduce_SIMD : IJobFor
    {
        public NativeArray<float> QueryX, QueryY;
        public NativeArray<float> DataX, DataY;
        public NativeArray<int> Index;
        public NativeArray<float> Result;
        public void Execute(int i)
        {
            float qx = QueryX[i], qy = QueryY[i];
            float best = float.MaxValue;
            for (int j = 0; j < 50; j++)
            {
                int idx = Index[i * 50 + j];
                float dx = qx - DataX[idx];
                float dy = qy - DataY[idx];
                float distSq = dx * dx + dy * dy;
                if (distSq < best)
                    best = distSq;
            }
            Result[i] = best;
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct GatherReduce_SIMD_IJob : IJob
    {
        public NativeArray<float> QueryX, QueryY;
        public NativeArray<float> DataX, DataY;
        public NativeArray<int> Index;
        public NativeArray<float> Result;
        public int Count;
        public void Execute()
        {
            for (int i = 0; i < Count; i++)
            {
                float qx = QueryX[i], qy = QueryY[i];
                float best = float.MaxValue;
                for (int j = 0; j < 50; j++)
                {
                    int idx = Index[i * 50 + j];
                    float dx = qx - DataX[idx];
                    float dy = qy - DataY[idx];
                    float distSq = dx * dx + dy * dy;
                    if (distSq < best)
                        best = distSq;
                }
                Result[i] = best;
            }
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
    public struct GatherReduce_ISPC : IJobParallelFor
    {
        public NativeArray<float> QueryX, QueryY;
        public NativeArray<float> DataX, DataY;
        public NativeArray<int> Index;
        public NativeArray<float> Result;
        public void Execute(int i)
        {
            float qx = QueryX[i], qy = QueryY[i];
            float best = float.MaxValue;
            for (int j = 0; j < 50; j++)
            {
                int idx = Index[i * 50 + j];
                float dx = qx - DataX[idx];
                float dy = qy - DataY[idx];
                float distSq = dx * dx + dy * dy;
                if (distSq < best)
                    best = distSq;
            }
            Result[i] = best;
        }
    }
}
