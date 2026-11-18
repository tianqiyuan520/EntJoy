using EntJoy.JobSystem;
using EntJoy.Collections;

namespace JobLibsBenchmark;

// Each backend uses the same uint arithmetic; the runner checks every value against Workload.
[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
public struct CppAdd : IJobParallelFor
{
    public NativeArray<uint> Values;
    public void Execute(int index)
    {
        Values[index] = Values[index] + 1u;
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
public struct IspcAdd : IJobParallelFor
{
    public NativeArray<uint> Values;
    public void Execute(int index)
    {
        Values[index] = Values[index] + 1u;
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp, AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
public struct SimdAdd : IJobParallelFor
{
    public NativeArray<uint> Values;
    public void Execute(int index)
    {
        Values[index] = Values[index] + 1u;
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
public struct CppXorshift : IJobParallelFor
{
    public NativeArray<uint> Values;
    public void Execute(int index)
    {
        uint x = Values[index];
        for (int j = 0; j < 1000; j++)
        {
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
        }
        Values[index] = x;
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
public struct IspcXorshift : IJobParallelFor
{
    public NativeArray<uint> Values;
    public void Execute(int index)
    {
        uint x = Values[index];
        for (int j = 0; j < 1000; j++)
        {
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
        }
        Values[index] = x;
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp, AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
public struct SimdXorshift : IJobParallelFor
{
    public NativeArray<uint> Values;
    public void Execute(int index)
    {
        uint x = Values[index];
        for (int j = 0; j < 1000; j++)
        {
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
        }
        Values[index] = x;
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
public struct CppBranch : IJobParallelFor
{
    public NativeArray<uint> Values;
    public void Execute(int index)
    {
        uint x = Values[index], sum = 0u;
        for (int j = 0; j < 1000; j++)
        {
            x = x * 1664525u + 1013904223u;
            uint r = x % 13u;
            if (r < 4u) sum += x;
            else if (r < 8u) sum ^= x;
            else sum -= x >> 3;
            if ((x & 7u) == 0u) sum += (uint)j;
        }
        Values[index] = sum;
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
public struct IspcBranch : IJobParallelFor
{
    public NativeArray<uint> Values;
    public void Execute(int index)
    {
        uint x = Values[index], sum = 0u;
        for (int j = 0; j < 1000; j++)
        {
            x = x * 1664525u + 1013904223u;
            uint r = x % 13u;
            if (r < 4u) sum += x;
            else if (r < 8u) sum ^= x;
            else sum -= x >> 3;
            if ((x & 7u) == 0u) sum += (uint)j;
        }
        Values[index] = sum;
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp, AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
public struct SimdBranch : IJobParallelFor
{
    public NativeArray<uint> Values;
    public void Execute(int index)
    {
        uint x = Values[index], sum = 0u;
        for (int j = 0; j < 1000; j++)
        {
            x = x * 1664525u + 1013904223u;
            uint r = x % 13u;
            if (r < 4u) sum += x;
            else if (r < 8u) sum ^= x;
            else sum -= x >> 3;
            if ((x & 7u) == 0u) sum += (uint)j;
        }
        Values[index] = sum;
    }
}

// 其余场景：同一份 Execute 源码分别走三个后端。
// 覆盖纯算术、谓词选择、单/多超越函数、短内层循环、邻域求和、间接寻址、窄元素。

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
public struct CppFmaF32 : IJobParallelFor
{
    public NativeArray<float> In;
    public NativeArray<float> Out;
    public void Execute(int index)
    {
        Out[index] = In[index] * 2.5f + 3.0f;
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
public struct IspcFmaF32 : IJobParallelFor
{
    public NativeArray<float> In;
    public NativeArray<float> Out;
    public void Execute(int index)
    {
        Out[index] = In[index] * 2.5f + 3.0f;
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp, AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
public struct SimdFmaF32 : IJobParallelFor
{
    public NativeArray<float> In;
    public NativeArray<float> Out;
    public void Execute(int index)
    {
        Out[index] = In[index] * 2.5f + 3.0f;
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
public struct CppSelectF32 : IJobParallelFor
{
    public NativeArray<float> In;
    public NativeArray<float> Out;
    public void Execute(int index)
    {
        float x = In[index] * 3.0f;
        Out[index] = x < -1.0f ? -1.0f : (x > 1.0f ? 1.0f : x);
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
public struct IspcSelectF32 : IJobParallelFor
{
    public NativeArray<float> In;
    public NativeArray<float> Out;
    public void Execute(int index)
    {
        float x = In[index] * 3.0f;
        Out[index] = x < -1.0f ? -1.0f : (x > 1.0f ? 1.0f : x);
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp, AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
public struct SimdSelectF32 : IJobParallelFor
{
    public NativeArray<float> In;
    public NativeArray<float> Out;
    public void Execute(int index)
    {
        float x = In[index] * 3.0f;
        Out[index] = x < -1.0f ? -1.0f : (x > 1.0f ? 1.0f : x);
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
public struct CppSinF32 : IJobParallelFor
{
    public NativeArray<float> In;
    public NativeArray<float> Out;
    public void Execute(int index)
    {
        Out[index] = MathF.Sin(In[index]);
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
public struct IspcSinF32 : IJobParallelFor
{
    public NativeArray<float> In;
    public NativeArray<float> Out;
    public void Execute(int index)
    {
        Out[index] = MathF.Sin(In[index]);
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp, AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
public struct SimdSinF32 : IJobParallelFor
{
    public NativeArray<float> In;
    public NativeArray<float> Out;
    public void Execute(int index)
    {
        Out[index] = MathF.Sin(In[index]);
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
public struct CppSinCosLogF32 : IJobParallelFor
{
    public NativeArray<float> In;
    public NativeArray<float> Out;
    public void Execute(int index)
    {
        float x = In[index];
        Out[index] = MathF.Sin(x) + MathF.Cos(x) + MathF.Log(MathF.Abs(x) + 1.0f);
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
public struct IspcSinCosLogF32 : IJobParallelFor
{
    public NativeArray<float> In;
    public NativeArray<float> Out;
    public void Execute(int index)
    {
        float x = In[index];
        Out[index] = MathF.Sin(x) + MathF.Cos(x) + MathF.Log(MathF.Abs(x) + 1.0f);
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp, AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
public struct SimdSinCosLogF32 : IJobParallelFor
{
    public NativeArray<float> In;
    public NativeArray<float> Out;
    public void Execute(int index)
    {
        float x = In[index];
        Out[index] = MathF.Sin(x) + MathF.Cos(x) + MathF.Log(MathF.Abs(x) + 1.0f);
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
public struct CppLoop8F32 : IJobParallelFor
{
    public NativeArray<float> In;
    public NativeArray<float> Out;
    public void Execute(int index)
    {
        float x = In[index];
        for (int k = 0; k < 8; k++) x = x * 0.5f + 1.0f;
        Out[index] = x;
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
public struct IspcLoop8F32 : IJobParallelFor
{
    public NativeArray<float> In;
    public NativeArray<float> Out;
    public void Execute(int index)
    {
        float x = In[index];
        for (int k = 0; k < 8; k++) x = x * 0.5f + 1.0f;
        Out[index] = x;
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp, AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
public struct SimdLoop8F32 : IJobParallelFor
{
    public NativeArray<float> In;
    public NativeArray<float> Out;
    public void Execute(int index)
    {
        float x = In[index];
        for (int k = 0; k < 8; k++) x = x * 0.5f + 1.0f;
        Out[index] = x;
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
public struct CppStencilF32 : IJobParallelFor
{
    public NativeArray<float> In;
    public NativeArray<float> Out;
    public void Execute(int index)
    {
        float sum = 0.0f;
        for (int k = 0; k < 8; k++) sum += In[index + k];
        Out[index] = sum;
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
public struct IspcStencilF32 : IJobParallelFor
{
    public NativeArray<float> In;
    public NativeArray<float> Out;
    public void Execute(int index)
    {
        float sum = 0.0f;
        for (int k = 0; k < 8; k++) sum += In[index + k];
        Out[index] = sum;
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp, AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
public struct SimdStencilF32 : IJobParallelFor
{
    public NativeArray<float> In;
    public NativeArray<float> Out;
    public void Execute(int index)
    {
        float sum = 0.0f;
        for (int k = 0; k < 8; k++) sum += In[index + k];
        Out[index] = sum;
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
public struct CppGatherF32 : IJobParallelFor
{
    public NativeArray<float> Table;
    public NativeArray<uint> Idx;
    public NativeArray<float> Out;
    public void Execute(int index)
    {
        Out[index] = Table[(int)(Idx[index]) & 16383];
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
public struct IspcGatherF32 : IJobParallelFor
{
    public NativeArray<float> Table;
    public NativeArray<uint> Idx;
    public NativeArray<float> Out;
    public void Execute(int index)
    {
        Out[index] = Table[(int)(Idx[index]) & 16383];
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp, AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
public struct SimdGatherF32 : IJobParallelFor
{
    public NativeArray<float> Table;
    public NativeArray<uint> Idx;
    public NativeArray<float> Out;
    public void Execute(int index)
    {
        Out[index] = Table[(int)(Idx[index]) & 16383];
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
public struct CppAddU8 : IJobParallelFor
{
    public NativeArray<byte> Values;
    public void Execute(int index)
    {
        Values[index] += 1;
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
public struct IspcAddU8 : IJobParallelFor
{
    public NativeArray<byte> Values;
    public void Execute(int index)
    {
        Values[index] += 1;
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp, AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
public struct SimdAddU8 : IJobParallelFor
{
    public NativeArray<byte> Values;
    public void Execute(int index)
    {
        Values[index] += 1;
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
public struct CppCopyU8 : IJobParallelFor
{
    public NativeArray<byte> In;
    public NativeArray<byte> Out;
    public void Execute(int index)
    {
        Out[index] = (byte)(In[index] + 1);
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
public struct IspcCopyU8 : IJobParallelFor
{
    public NativeArray<byte> In;
    public NativeArray<byte> Out;
    public void Execute(int index)
    {
        Out[index] = (byte)(In[index] + 1);
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp, AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
public struct SimdCopyU8 : IJobParallelFor
{
    public NativeArray<byte> In;
    public NativeArray<byte> Out;
    public void Execute(int index)
    {
        Out[index] = (byte)(In[index] + 1);
    }
}

// 运行时循环边界 + 运行时段 stride：下标 = index + k*Stride（k 不是编译期常量）。
[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
public struct CppWindowF32 : IJobParallelFor
{
    public NativeArray<float> In;
    public NativeArray<float> Out;
    public int Stride;
    public int K;
    public void Execute(int index)
    {
        float sum = 0.0f;
        for (int k = 0; k < K; k++) sum += In[index + k * Stride];
        Out[index] = sum;
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
public struct IspcWindowF32 : IJobParallelFor
{
    public NativeArray<float> In;
    public NativeArray<float> Out;
    public int Stride;
    public int K;
    public void Execute(int index)
    {
        float sum = 0.0f;
        for (int k = 0; k < K; k++) sum += In[index + k * Stride];
        Out[index] = sum;
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp, AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
public struct SimdWindowF32 : IJobParallelFor
{
    public NativeArray<float> In;
    public NativeArray<float> Out;
    public int Stride;
    public int K;
    public void Execute(int index)
    {
        float sum = 0.0f;
        for (int k = 0; k < K; k++) sum += In[index + k * Stride];
        Out[index] = sum;
    }
}

// 两个运行时循环变量同时进下标：index + k*Stride + m。
[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
public struct CppWindow2F32 : IJobParallelFor
{
    public NativeArray<float> In;
    public NativeArray<float> Out;
    public int Stride;
    public int K;
    public void Execute(int index)
    {
        float sum = 0.0f;
        for (int k = 0; k < K; k++)
            for (int m = 0; m < K; m++)
                sum += In[index + k * Stride + m];
        Out[index] = sum;
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
public struct IspcWindow2F32 : IJobParallelFor
{
    public NativeArray<float> In;
    public NativeArray<float> Out;
    public int Stride;
    public int K;
    public void Execute(int index)
    {
        float sum = 0.0f;
        for (int k = 0; k < K; k++)
            for (int m = 0; m < K; m++)
                sum += In[index + k * Stride + m];
        Out[index] = sum;
    }
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp, AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
public struct SimdWindow2F32 : IJobParallelFor
{
    public NativeArray<float> In;
    public NativeArray<float> Out;
    public int Stride;
    public int K;
    public void Execute(int index)
    {
        float sum = 0.0f;
        for (int k = 0; k < K; k++)
            for (int m = 0; m < K; m++)
                sum += In[index + k * Stride + m];
        Out[index] = sum;
    }
}
