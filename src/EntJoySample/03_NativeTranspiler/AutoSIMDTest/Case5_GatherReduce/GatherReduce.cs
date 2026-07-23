using EntJoy.Collections;

namespace EntJoySample.AutoSIMDTest
{
    
    public struct GatherReduce_CSharp_Job : IJob
    {
        public NativeArray<float> QueryX, QueryY, DataX, DataY, Result; public NativeArray<int> Index;
        public int Count;
        public void Execute()
        {
            for (int idx = 0; idx < Count; idx++) { float qx = QueryX[idx], qy = QueryY[idx], b = float.MaxValue; for (int j = 0; j < 50; j++) { int k = Index[idx * 50 + j]; float dx = qx - DataX[k], dy = qy - DataY[k], d = dx*dx + dy*dy; if (d < b) b = d; } Result[idx] = b; }
        }
    }

    
    public struct GatherReduce_CSharp_For : IJobFor
    {
        public NativeArray<float> QueryX, QueryY, DataX, DataY, Result; public NativeArray<int> Index;
        public void Execute(int i)
        {
            float qx = QueryX[i], qy = QueryY[i], best = float.MaxValue; for (int j = 0; j < 50; j++) { int idx = Index[i * 50 + j]; float dx = qx - DataX[idx], dy = qy - DataY[idx], d = dx * dx + dy * dy; if (d < best) best = d; } Result[i] = best;
        }
    }

    
    public struct GatherReduce_CSharp_PF : IJobParallelFor
    {
        public NativeArray<float> QueryX, QueryY, DataX, DataY, Result; public NativeArray<int> Index;
        public void Execute(int i)
        {
            float qx = QueryX[i], qy = QueryY[i], best = float.MaxValue; for (int j = 0; j < 50; j++) { int idx = Index[i * 50 + j]; float dx = qx - DataX[idx], dy = qy - DataY[idx], d = dx * dx + dy * dy; if (d < best) best = d; } Result[i] = best;
        }
    }

    [NativeTranspiler.NativeTranspile]
    public struct GatherReduce_Cpp_Job : IJob
    {
        public NativeArray<float> QueryX, QueryY, DataX, DataY, Result; public NativeArray<int> Index;
        public int Count;
        public void Execute()
        {
            for (int idx = 0; idx < Count; idx++) { float qx = QueryX[idx], qy = QueryY[idx], b = float.MaxValue; for (int j = 0; j < 50; j++) { int k = Index[idx * 50 + j]; float dx = qx - DataX[k], dy = qy - DataY[k], d = dx*dx + dy*dy; if (d < b) b = d; } Result[idx] = b; }
        }
    }

    [NativeTranspiler.NativeTranspile]
    public struct GatherReduce_Cpp_For : IJobFor
    {
        public NativeArray<float> QueryX, QueryY, DataX, DataY, Result; public NativeArray<int> Index;
        public void Execute(int i)
        {
            float qx = QueryX[i], qy = QueryY[i], best = float.MaxValue; for (int j = 0; j < 50; j++) { int idx = Index[i * 50 + j]; float dx = qx - DataX[idx], dy = qy - DataY[idx], d = dx * dx + dy * dy; if (d < best) best = d; } Result[i] = best;
        }
    }

    [NativeTranspiler.NativeTranspile]
    public struct GatherReduce_Cpp_PF : IJobParallelFor
    {
        public NativeArray<float> QueryX, QueryY, DataX, DataY, Result; public NativeArray<int> Index;
        public void Execute(int i)
        {
            float qx = QueryX[i], qy = QueryY[i], best = float.MaxValue; for (int j = 0; j < 50; j++) { int idx = Index[i * 50 + j]; float dx = qx - DataX[idx], dy = qy - DataY[idx], d = dx * dx + dy * dy; if (d < best) best = d; } Result[i] = best;
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct GatherReduce_SIMD_Job : IJob
    {
        public NativeArray<float> QueryX, QueryY, DataX, DataY, Result; public NativeArray<int> Index;
        public int Count;
        public void Execute()
        {
            for (int idx = 0; idx < Count; idx++) { float qx = QueryX[idx], qy = QueryY[idx], b = float.MaxValue; for (int j = 0; j < 50; j++) { int k = Index[idx * 50 + j]; float dx = qx - DataX[k], dy = qy - DataY[k], d = dx*dx + dy*dy; if (d < b) b = d; } Result[idx] = b; }
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct GatherReduce_SIMD_For : IJobFor
    {
        public NativeArray<float> QueryX, QueryY, DataX, DataY, Result; public NativeArray<int> Index;
        public void Execute(int i)
        {
            float qx = QueryX[i], qy = QueryY[i], best = float.MaxValue; for (int j = 0; j < 50; j++) { int idx = Index[i * 50 + j]; float dx = qx - DataX[idx], dy = qy - DataY[idx], d = dx * dx + dy * dy; if (d < best) best = d; } Result[i] = best;
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct GatherReduce_SIMD_PF : IJobParallelFor
    {
        public NativeArray<float> QueryX, QueryY, DataX, DataY, Result; public NativeArray<int> Index;
        public void Execute(int i)
        {
            float qx = QueryX[i], qy = QueryY[i], best = float.MaxValue; for (int j = 0; j < 50; j++) { int idx = Index[i * 50 + j]; float dx = qx - DataX[idx], dy = qy - DataY[idx], d = dx * dx + dy * dy; if (d < best) best = d; } Result[i] = best;
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
    public struct GatherReduce_ISPC_PF : IJobParallelFor
    {
        public NativeArray<float> QueryX, QueryY, DataX, DataY, Result; public NativeArray<int> Index;
        public void Execute(int i)
        {
            float qx = QueryX[i], qy = QueryY[i], best = float.MaxValue; for (int j = 0; j < 50; j++) { int idx = Index[i * 50 + j]; float dx = qx - DataX[idx], dy = qy - DataY[idx], d = dx * dx + dy * dy; if (d < best) best = d; } Result[i] = best;
        }
    }

}
