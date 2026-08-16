using EntJoy.Collections;
using EntJoy.Mathematics;
using EntJoy.JobSystem;
using System;

namespace EntJoySample.GpuJob
{
    /// <summary>
    /// GPU Move Job（LightMove 形态）：逐元素 pos += vel * dt + 视口反弹。
    /// 标记 [NativeTranspile(Target = BackendTarget.Gpu)] → NativeTranspiler 生成 .wgsl compute 内核。
    /// </summary>
    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Gpu)]
    public struct GpuMoveJob : IJobParallelFor
    {
        public NativeArray<float2> Positions;
        public NativeArray<float2> Velocities;
        public float Dt;
        public float ViewportWidth;
        public float ViewportHeight;

        public void Execute(int i)
        {
            float2 pos = Positions[i];
            float2 vel = Velocities[i];

            pos.x += vel.x * Dt;
            pos.y += vel.y * Dt;

            if (pos.x < 0f || pos.x > ViewportWidth) vel.x = -vel.x;
            if (pos.y < 0f || pos.y > ViewportHeight) vel.y = -vel.y;

            Positions[i] = pos;
            Velocities[i] = vel;
        }
    }

    /// <summary>
    /// GPU Heavy Job（HeavyMove 形态）：每实体 16 次超越函数累加（ALU 密集），
    /// 用于验证 WGSL 生成器对 MathF 调用 / for 循环 / 累加器的翻译。
    /// </summary>
    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Gpu)]
    public struct GpuHeavyJob : IJobParallelFor
    {
        public NativeArray<float2> Positions;
        public NativeArray<float2> Velocities;
        public float Dt;

        public void Execute(int i)
        {
            float2 pos = Positions[i];
            float2 vel = Velocities[i];

            float acc = 0f;
            float x = pos.x;
            for (int k = 0; k < 16; k++)
            {
                acc += MathF.Sin(x) + MathF.Cos(x) + MathF.Sqrt(x * x + 1f);
                x += vel.x * Dt;
            }

            pos.x += acc * Dt;
            pos.y += vel.y * Dt;
            Positions[i] = pos;
            Velocities[i] = vel;
        }
    }

    /// <summary>
    /// GPU 标量形态（IJob 单次执行 + int 标量 + uint 索引运算）：
    /// 验证 uniform 标量字段 / int 运算 / 向量与标量混合的翻译。
    /// </summary>
    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Gpu)]
    public struct GpuScalarJob : IJob
    {
        public NativeArray<int> Counts;
        public NativeArray<uint2> Flags;
        public int Add;

        public void Execute()
        {
            int total = 0;
            for (int i = 0; i < 10; i++)
                total += i * Add;
            Counts[0] = total;
            Flags[0] = new uint2(1u, 2u);
        }
    }

    // ============================================================
    // 同体 C++ / ISPC Job —— 与 GPU Job 完全相同的字段与 Execute body，
    // 仅 [NativeTranspile] Target 不同（Cpp / Ispc），用于四路性能对比。
    // ============================================================

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
    public struct CppMoveJob : IJobParallelFor
    {
        public NativeArray<float2> Positions;
        public NativeArray<float2> Velocities;
        public float Dt;
        public float ViewportWidth;
        public float ViewportHeight;

        public void Execute(int i)
        {
            float2 pos = Positions[i];
            float2 vel = Velocities[i];
            pos.x += vel.x * Dt;
            pos.y += vel.y * Dt;
            if (pos.x < 0f || pos.x > ViewportWidth) vel.x = -vel.x;
            if (pos.y < 0f || pos.y > ViewportHeight) vel.y = -vel.y;
            Positions[i] = pos;
            Velocities[i] = vel;
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.fast)]
    public struct IspcMoveJob : IJobParallelFor
    {
        public NativeArray<float2> Positions;
        public NativeArray<float2> Velocities;
        public float Dt;
        public float ViewportWidth;
        public float ViewportHeight;

        public void Execute(int i)
        {
            float2 pos = Positions[i];
            float2 vel = Velocities[i];
            pos.x += vel.x * Dt;
            pos.y += vel.y * Dt;
            if (pos.x < 0f || pos.x > ViewportWidth) vel.x = -vel.x;
            if (pos.y < 0f || pos.y > ViewportHeight) vel.y = -vel.y;
            Positions[i] = pos;
            Velocities[i] = vel;
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
    public struct CppHeavyJob : IJobParallelFor
    {
        public NativeArray<float2> Positions;
        public NativeArray<float2> Velocities;
        public float Dt;

        public void Execute(int i)
        {
            float2 pos = Positions[i];
            float2 vel = Velocities[i];
            float acc = 0f;
            float x = pos.x;
            for (int k = 0; k < 16; k++)
            {
                acc += MathF.Sin(x) + MathF.Cos(x) + MathF.Sqrt(x * x + 1f);
                x += vel.x * Dt;
            }
            pos.x += acc * Dt;
            pos.y += vel.y * Dt;
            Positions[i] = pos;
            Velocities[i] = vel;
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.fast)]
    public struct IspcHeavyJob : IJobParallelFor
    {
        public NativeArray<float2> Positions;
        public NativeArray<float2> Velocities;
        public float Dt;

        public void Execute(int i)
        {
            float2 pos = Positions[i];
            float2 vel = Velocities[i];
            float acc = 0f;
            float x = pos.x;
            for (int k = 0; k < 16; k++)
            {
                acc += MathF.Sin(x) + MathF.Cos(x) + MathF.Sqrt(x * x + 1f);
                x += vel.x * Dt;
            }
            pos.x += acc * Dt;
            pos.y += vel.y * Dt;
            Positions[i] = pos;
            Velocities[i] = vel;
        }
    }
}
