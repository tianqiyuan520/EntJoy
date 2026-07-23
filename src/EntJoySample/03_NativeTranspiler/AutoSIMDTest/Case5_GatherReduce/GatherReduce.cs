using EntJoy.Collections;

namespace EntJoySample.AutoSIMDTest
{
    public struct GatherReduce_CSharp : IJobParallelFor
    {
        public NativeArray<float> QueryX, QueryY, DataX, DataY, Result; public NativeArray<int> Index;
        public void Execute(int i) { float qx = QueryX[i], qy = QueryY[i], best = float.MaxValue; for (int j = 0; j < 50; j++) { int idx = Index[i * 50 + j]; float dx = qx - DataX[idx], dy = qy - DataY[idx], d = dx * dx + dy * dy; if (d < best) best = d; } Result[i] = best; }
    }
    [NativeTranspiler.NativeTranspile]
    public struct GatherReduce_Cpp : IJobParallelFor
    {
        public NativeArray<float> QueryX, QueryY, DataX, DataY, Result; public NativeArray<int> Index;
        public void Execute(int i) { float qx = QueryX[i], qy = QueryY[i], best = float.MaxValue; for (int j = 0; j < 50; j++) { int idx = Index[i * 50 + j]; float dx = qx - DataX[idx], dy = qy - DataY[idx], d = dx * dx + dy * dy; if (d < best) best = d; } Result[i] = best; }
    }
    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct GatherReduce_SIMD_PF : IJobParallelFor
    {
        public NativeArray<float> QueryX, QueryY, DataX, DataY, Result; public NativeArray<int> Index;
        public void Execute(int i) { float qx = QueryX[i], qy = QueryY[i], best = float.MaxValue; for (int j = 0; j < 50; j++) { int idx = Index[i * 50 + j]; float dx = qx - DataX[idx], dy = qy - DataY[idx], d = dx * dx + dy * dy; if (d < best) best = d; } Result[i] = best; }
    }
    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
    public struct GatherReduce_ISPC : IJobParallelFor
    {
        public NativeArray<float> QueryX, QueryY, DataX, DataY, Result; public NativeArray<int> Index;
        public void Execute(int i) { float qx = QueryX[i], qy = QueryY[i], best = float.MaxValue; for (int j = 0; j < 50; j++) { int idx = Index[i * 50 + j]; float dx = qx - DataX[idx], dy = qy - DataY[idx], d = dx * dx + dy * dy; if (d < best) best = d; } Result[i] = best; }
    }
    // --- C++ Auto-SIMD (IJobFor) ---
    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct GatherReduce_SIMD_For : IJobFor
    {
        public NativeArray<float> QueryX, QueryY, DataX, DataY, Result; public NativeArray<int> Index;
        public void Execute(int i) { float qx = QueryX[i], qy = QueryY[i], best = float.MaxValue; for (int j = 0; j < 50; j++) { int idx = Index[i * 50 + j]; float dx = qx - DataX[idx], dy = qy - DataY[idx], d = dx * dx + dy * dy; if (d < best) best = d; } Result[i] = best; }
    }

    // --- C++ Auto-SIMD (IJob) ---
    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct GatherReduce_SIMD_IJob : IJob
    {
        public NativeArray<float> QueryX, QueryY, DataX, DataY, Result; public NativeArray<int> Index;
        public int Count;
        public void Execute()
        {
            for (int i = 0; i < Count; i++)
            {
                float qx = QueryX[i], qy = QueryY[i], best = float.MaxValue;
                for (int j = 0; j < 50; j++)
                { int idx = Index[i * 50 + j]; float dx = qx - DataX[idx], dy = qy - DataY[idx]; float d = dx * dx + dy * dy; if (d < best) best = d; }
                Result[i] = best;
            }
        }
    }
}
