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
