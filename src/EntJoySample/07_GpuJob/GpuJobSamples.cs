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
            pos.x += acc * Dt;            pos.y += vel.y * Dt;
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

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cuda)]
    public struct CudaMoveJob : IJobParallelFor
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

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cuda)]
    public struct CudaHeavyJob : IJobParallelFor
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

    // ============================================================
    // GridSearch closest（全量更新）C++/ISPC 对照 jobs —— 与 GridSearchFullUpdate 的 GPU 版
    // 同算法（counting-sort grid）：count（原子计数）→ CPU prefix → place（原子占位）→ query（3x3 邻域）。
    // ============================================================

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
    public unsafe struct CppGridCountJob : IJobParallelFor
    {
        public NativeArray<float2> Positions;
        public NativeArray<int> Counts;
        public int DimX;
        public int DimY;

        public void Execute(int i)
        {
            float2 p = Positions[i];
            int cx = (int)MathF.Floor((p.x + 100f) * 1f);
            if (cx < 0) cx = 0; else if (cx >= DimX) cx = DimX - 1;
            int cy = (int)MathF.Floor((p.y + 100f) * 1f);
            if (cy < 0) cy = 0; else if (cy >= DimY) cy = DimY - 1;
            Interlocked.Increment(ref UnsafeUtility.ArrayElementAsRef<int>(Counts.GetUnsafePtr(), cx + cy * DimX));
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
    public unsafe struct CppGridPlaceJob : IJobParallelFor
    {
        public NativeArray<float2> Positions;
        public NativeArray<int> Cursor;      // 初始 = cellStart（前缀和），place 原子递增
        public NativeArray<float2> Sorted;
        public NativeArray<int2> HashIdx;
        public int DimX;
        public int DimY;

        public void Execute(int i)
        {
            float2 p = Positions[i];
            int cx = (int)MathF.Floor((p.x + 100f) * 1f);
            if (cx < 0) cx = 0; else if (cx >= DimX) cx = DimX - 1;
            int cy = (int)MathF.Floor((p.y + 100f) * 1f);
            if (cy < 0) cy = 0; else if (cy >= DimY) cy = DimY - 1;
            int hash = cx + cy * DimX;
            int slot = Interlocked.Add(ref UnsafeUtility.ArrayElementAsRef<int>(Cursor.GetUnsafePtr(), hash), 1) - 1;
            Sorted[slot] = p;
            HashIdx[slot] = new int2(hash, i);
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
    public struct CppGridQueryJob : IJobParallelFor
    {
        public NativeArray<float2> Query;
        public NativeArray<int> CellStart;
        public NativeArray<float2> Sorted;
        public NativeArray<int2> HashIdx;
        public NativeArray<int> Result;
        public int DimX;
        public int DimY;
        public int SortedLength;

        public void Execute(int k)
        {
            Result[k] = -1;
            float2 q = Query[k];
            int cx = (int)MathF.Floor((q.x + 100f) * 1f);
            if (cx < 0) cx = 0; else if (cx >= DimX) cx = DimX - 1;
            int cy = (int)MathF.Floor((q.y + 100f) * 1f);
            if (cy < 0) cy = 0; else if (cy >= DimY) cy = DimY - 1;
            float bestD = float.MaxValue;
            int bestIdx = -1;
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = cx + dx;
                if (nx < 0 || nx >= DimX) continue;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = cy + dy;
                    if (ny < 0 || ny >= DimY) continue;
                    int c = ny * DimX + nx;
                    int start = CellStart[c];
                    int end = (c + 1 < DimX * DimY) ? CellStart[c + 1] : SortedLength;
                    for (int j = start; j < end; j++)
                    {
                        float2 sp = Sorted[j];
                        float dx2 = q.x - sp.x, dy2 = q.y - sp.y;
                        float d2 = dx2 * dx2 + dy2 * dy2;
                        if (d2 < bestD) { bestD = d2; bestIdx = HashIdx[j].y; }
                    }
                }
            }
            if (bestIdx >= 0) Result[k] = bestIdx;
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.fast)]
    public unsafe struct IspcGridCountJob : IJobParallelFor
    {
        public NativeArray<float2> Positions;
        public NativeArray<int> Counts;
        public int DimX;
        public int DimY;

        public void Execute(int i)
        {
            float2 p = Positions[i];
            int cx = (int)MathF.Floor((p.x + 100f) * 1f);
            if (cx < 0) cx = 0; else if (cx >= DimX) cx = DimX - 1;
            int cy = (int)MathF.Floor((p.y + 100f) * 1f);
            if (cy < 0) cy = 0; else if (cy >= DimY) cy = DimY - 1;
            Interlocked.Increment(ref UnsafeUtility.ArrayElementAsRef<int>(Counts.GetUnsafePtr(), cx + cy * DimX));
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.fast)]
    public unsafe struct IspcGridPlaceJob : IJobParallelFor
    {
        public NativeArray<float2> Positions;
        public NativeArray<int> Cursor;      // 初始 = cellStart（前缀和），place 原子递增
        public NativeArray<float2> Sorted;
        public NativeArray<int2> HashIdx;
        public int DimX;
        public int DimY;

        public void Execute(int i)
        {
            float2 p = Positions[i];
            int cx = (int)MathF.Floor((p.x + 100f) * 1f);
            if (cx < 0) cx = 0; else if (cx >= DimX) cx = DimX - 1;
            int cy = (int)MathF.Floor((p.y + 100f) * 1f);
            if (cy < 0) cy = 0; else if (cy >= DimY) cy = DimY - 1;
            int hash = cx + cy * DimX;
            int slot = Interlocked.Add(ref UnsafeUtility.ArrayElementAsRef<int>(Cursor.GetUnsafePtr(), hash), 1) - 1;
            Sorted[slot] = p;
            HashIdx[slot] = new int2(hash, i);
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.fast)]
    public struct IspcGridQueryJob : IJobParallelFor
    {
        public NativeArray<float2> Query;
        public NativeArray<int> CellStart;
        public NativeArray<float2> Sorted;
        public NativeArray<int2> HashIdx;
        public NativeArray<int> Result;
        public int DimX;
        public int DimY;
        public int SortedLength;

        public void Execute(int k)
        {
            Result[k] = -1;
            float2 q = Query[k];
            int cx = (int)MathF.Floor((q.x + 100f) * 1f);
            if (cx < 0) cx = 0; else if (cx >= DimX) cx = DimX - 1;
            int cy = (int)MathF.Floor((q.y + 100f) * 1f);
            if (cy < 0) cy = 0; else if (cy >= DimY) cy = DimY - 1;
            float bestD = float.MaxValue;
            int bestIdx = -1;
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = cx + dx;
                if (nx < 0 || nx >= DimX) continue;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = cy + dy;
                    if (ny < 0 || ny >= DimY) continue;
                    int c = ny * DimX + nx;
                    int start = CellStart[c];
                    int end = (c + 1 < DimX * DimY) ? CellStart[c + 1] : SortedLength;
                    for (int j = start; j < end; j++)
                    {
                        float2 sp = Sorted[j];
                        float dx2 = q.x - sp.x, dy2 = q.y - sp.y;
                        float d2 = dx2 * dx2 + dy2 * dy2;
                        if (d2 < bestD) { bestD = d2; bestIdx = HashIdx[j].y; }
                    }
                }
            }
            if (bestIdx >= 0) Result[k] = bestIdx;
        }
    }
}
