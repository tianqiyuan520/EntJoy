//using System;
//using System.Threading;
//using EntJoy.JobSystem;
//using EntJoy.Collections;
//using NativeTranspiler;

//namespace EntJoySample.SIMD
//{
//    // =====================================================================
//    // 10_SIMD 用例 job 定义 + C# 标量 oracle。
//    // 每个用例 3 个后端 job（Cpp=普通翻译基准 / ISPC / AutoSIMD）+ 1 个 oracle。
//    //
//    // 硬约束（来自 NativeBenchJobs.cs 踩坑备忘）：
//    //   1) Execute 必须块体 { ... }，不能表达式体 => ...。
//    //   2) 字段必须非托管：NativeArray<T>/标量/float2,int2；不能用 int[]。
//    //   3) ISPC 不支持 long/int64，重算用 int/uint。
//    // =====================================================================

//    // ── C01_Light: 纯算术 R = A*B + C（float） ──
//    [NativeTranspile(Target = BackendTarget.Cpp)]
//    public struct C01LightCpp : IJobParallelFor
//    {
//        public NativeArray<float> A, B, C, R;
//        public void Execute(int i) { R[i] = A[i] * B[i] + C[i]; }
//    }

//    [NativeTranspile(Target = BackendTarget.Ispc, MathLib = IspcMathLib.fast)]
//    public struct C01LightIspc : IJobParallelFor
//    {
//        public NativeArray<float> A, B, C, R;
//        public void Execute(int i) { R[i] = A[i] * B[i] + C[i]; }
//    }

//    [NativeTranspile(Target = BackendTarget.Cpp, AutoSIMD = AutoSIMD.Enabled)]
//    public struct C01LightAuto : IJobParallelFor
//    {
//        public NativeArray<float> A, B, C, R;
//        public void Execute(int i) { R[i] = A[i] * B[i] + C[i]; }
//    }

//    // ── C02_Heavy: 16 次 sin/cos 迭代累积（float） ──
//    [NativeTranspile(Target = BackendTarget.Cpp)]
//    public struct C02HeavyCpp : IJobParallelFor
//    {
//        public NativeArray<float> A, R;
//        public void Execute(int i)
//        {
//            float acc = A[i];
//            for (int it = 0; it < 16; it++)
//            {
//                float w = MathF.Sin(acc + it * 0.03125f) + MathF.Cos(acc - it * 0.0625f);
//                acc = acc * 0.985f + w * 0.015f;
//            }
//            R[i] = acc;
//        }
//    }

//    [NativeTranspile(Target = BackendTarget.Ispc, MathLib = IspcMathLib.fast)]
//    public struct C02HeavyIspc : IJobParallelFor
//    {
//        public NativeArray<float> A, R;
//        public void Execute(int i)
//        {
//            float acc = A[i];
//            for (int it = 0; it < 16; it++)
//            {
//                float w = MathF.Sin(acc + it * 0.03125f) + MathF.Cos(acc - it * 0.0625f);
//                acc = acc * 0.985f + w * 0.015f;
//            }
//            R[i] = acc;
//        }
//    }

//    [NativeTranspile(Target = BackendTarget.Cpp, AutoSIMD = AutoSIMD.Enabled)]
//    public struct C02HeavyAuto : IJobParallelFor
//    {
//        public NativeArray<float> A, R;
//        public void Execute(int i)
//        {
//            float acc = A[i];
//            for (int it = 0; it < 16; it++)
//            {
//                float w = MathF.Sin(acc + it * 0.03125f) + MathF.Cos(acc - it * 0.0625f);
//                acc = acc * 0.985f + w * 0.015f;
//            }
//            R[i] = acc;
//        }
//    }

//    // ── C03_ControlFlow: 5 路 if/else-if/else + float 累加 ──
//    [NativeTranspile(Target = BackendTarget.Cpp)]
//    public struct C03FlowCpp : IJobParallelFor
//    {
//        public NativeArray<float> A, B, R;
//        public void Execute(int i)
//        {
//            float v = A[i];
//            float r;
//            if (v > 90f) r = v * 0.5f;
//            else if (v > 60f) r = v * 0.25f;
//            else if (v > 30f) r = v * 0.125f;
//            else if (v > 0f) r = v * 0.0625f;
//            else r = v * -0.5f;
//            R[i] = r + B[i];
//        }
//    }

//    [NativeTranspile(Target = BackendTarget.Ispc, MathLib = IspcMathLib.fast)]
//    public struct C03FlowIspc : IJobParallelFor
//    {
//        public NativeArray<float> A, B, R;
//        public void Execute(int i)
//        {
//            float v = A[i];
//            float r;
//            if (v > 90f) r = v * 0.5f;
//            else if (v > 60f) r = v * 0.25f;
//            else if (v > 30f) r = v * 0.125f;
//            else if (v > 0f) r = v * 0.0625f;
//            else r = v * -0.5f;
//            R[i] = r + B[i];
//        }
//    }

//    [NativeTranspile(Target = BackendTarget.Cpp, AutoSIMD = AutoSIMD.Enabled)]
//    public struct C03FlowAuto : IJobParallelFor
//    {
//        public NativeArray<float> A, B, R;
//        public void Execute(int i)
//        {
//            float v = A[i];
//            float r;
//            if (v > 90f) r = v * 0.5f;
//            else if (v > 60f) r = v * 0.25f;
//            else if (v > 30f) r = v * 0.125f;
//            else if (v > 0f) r = v * 0.0625f;
//            else r = v * -0.5f;
//            R[i] = r + B[i];
//        }
//    }

//    // ── C04_NestedFor: 3×3 邻域 + 双层 for + continue（float） ──
//    [NativeTranspile(Target = BackendTarget.Cpp)]
//    public struct C04NestedCpp : IJobParallelFor
//    {
//        public NativeArray<float> A, B, C, R;
//        public void Execute(int i)
//        {
//            float acc = 0f;
//            for (int dx = -1; dx <= 1; dx++)
//            {
//                for (int dy = -1; dy <= 1; dy++)
//                {
//                    if (dx == 0 && dy == 0) continue;
//                    acc += (A[i] * dx + B[i] * dy) * (dx + dy + C[i]);
//                }
//            }
//            R[i] = acc;
//        }
//    }

//    [NativeTranspile(Target = BackendTarget.Ispc, MathLib = IspcMathLib.fast)]
//    public struct C04NestedIspc : IJobParallelFor
//    {
//        public NativeArray<float> A, B, C, R;
//        public void Execute(int i)
//        {
//            float acc = 0f;
//            for (int dx = -1; dx <= 1; dx++)
//            {
//                for (int dy = -1; dy <= 1; dy++)
//                {
//                    if (dx == 0 && dy == 0) continue;
//                    acc += (A[i] * dx + B[i] * dy) * (dx + dy + C[i]);
//                }
//            }
//            R[i] = acc;
//        }
//    }

//    [NativeTranspile(Target = BackendTarget.Cpp, AutoSIMD = AutoSIMD.Enabled)]
//    public struct C04NestedAuto : IJobParallelFor
//    {
//        public NativeArray<float> A, B, C, R;
//        public void Execute(int i)
//        {
//            float acc = 0f;
//            for (int dx = -1; dx <= 1; dx++)
//            {
//                for (int dy = -1; dy <= 1; dy++)
//                {
//                    if (dx == 0 && dy == 0) continue;
//                    acc += (A[i] * dx + B[i] * dy) * (dx + dy + C[i]);
//                }
//            }
//            R[i] = acc;
//        }
//    }

//    // ── C05_While: while + 变体条件 + break（float） ──
//    [NativeTranspile(Target = BackendTarget.Cpp)]
//    public struct C05WhileCpp : IJobParallelFor
//    {
//        public NativeArray<float> A, B, R;
//        public void Execute(int i)
//        {
//            int k = 0;
//            float acc = A[i];
//            while (k < 16)
//            {
//                if (acc >= 1000f) break;
//                acc = acc * 1.1f + B[i];
//                k = k + 1;
//            }
//            R[i] = acc;
//        }
//    }

//    [NativeTranspile(Target = BackendTarget.Ispc, MathLib = IspcMathLib.fast)]
//    public struct C05WhileIspc : IJobParallelFor
//    {
//        public NativeArray<float> A, B, R;
//        public void Execute(int i)
//        {
//            int k = 0;
//            float acc = A[i];
//            while (k < 16)
//            {
//                if (acc >= 1000f) break;
//                acc = acc * 1.1f + B[i];
//                k = k + 1;
//            }
//            R[i] = acc;
//        }
//    }

//    [NativeTranspile(Target = BackendTarget.Cpp, AutoSIMD = AutoSIMD.Enabled)]
//    public struct C05WhileAuto : IJobParallelFor
//    {
//        public NativeArray<float> A, B, R;
//        public void Execute(int i)
//        {
//            int k = 0;
//            float acc = A[i];
//            while (k < 16)
//            {
//                if (acc >= 1000f) break;
//                acc = acc * 1.1f + B[i];
//                k = k + 1;
//            }
//            R[i] = acc;
//        }
//    }

//    // ── C06_Reduction: min 归约（float） ──
//    [NativeTranspile(Target = BackendTarget.Cpp)]
//    public struct C06ReduceCpp : IJobParallelFor
//    {
//        public NativeArray<float> A, B, R;
//        public void Execute(int i)
//        {
//            float best = float.MaxValue;
//            for (int j = 0; j < 16; j++)
//            {
//                float v = A[i] * (j + 1) + B[i];
//                if (v < best) best = v;
//            }
//            R[i] = best;
//        }
//    }

//    [NativeTranspile(Target = BackendTarget.Ispc, MathLib = IspcMathLib.fast)]
//    public struct C06ReduceIspc : IJobParallelFor
//    {
//        public NativeArray<float> A, B, R;
//        public void Execute(int i)
//        {
//            float best = float.MaxValue;
//            for (int j = 0; j < 16; j++)
//            {
//                float v = A[i] * (j + 1) + B[i];
//                if (v < best) best = v;
//            }
//            R[i] = best;
//        }
//    }

//    [NativeTranspile(Target = BackendTarget.Cpp, AutoSIMD = AutoSIMD.Enabled)]
//    public struct C06ReduceAuto : IJobParallelFor
//    {
//        public NativeArray<float> A, B, R;
//        public void Execute(int i)
//        {
//            float best = float.MaxValue;
//            for (int j = 0; j < 16; j++)
//            {
//                float v = A[i] * (j + 1) + B[i];
//                if (v < best) best = v;
//            }
//            R[i] = best;
//        }
//    }

//    // ── C07_GatherScatter: 随机索引 gather（float） ──
//    [NativeTranspile(Target = BackendTarget.Cpp)]
//    public struct C07GatherCpp : IJobParallelFor
//    {
//        public NativeArray<float> A, B, R;
//        public NativeArray<int> Indices;
//        public void Execute(int i) { R[i] = A[Indices[i]] * B[i]; }
//    }

//    [NativeTranspile(Target = BackendTarget.Ispc, MathLib = IspcMathLib.fast)]
//    public struct C07GatherIspc : IJobParallelFor
//    {
//        public NativeArray<float> A, B, R;
//        public NativeArray<int> Indices;
//        public void Execute(int i) { R[i] = A[Indices[i]] * B[i]; }
//    }

//    [NativeTranspile(Target = BackendTarget.Cpp, AutoSIMD = AutoSIMD.Enabled)]
//    public struct C07GatherAuto : IJobParallelFor
//    {
//        public NativeArray<float> A, B, R;
//        public NativeArray<int> Indices;
//        public void Execute(int i) { R[i] = A[Indices[i]] * B[i]; }
//    }

//    // ── C08_IntUint: uint LCG + 移位 + 位运算 + 混合比较（int，精确比对） ──
//    [NativeTranspile(Target = BackendTarget.Cpp)]
//    public struct C08IntCpp : IJobParallelFor
//    {
//        public NativeArray<int> R;
//        public void Execute(int i)
//        {
//            int sum = 0;
//            uint x = (uint)(i * 2654435761u) + 1u;
//            for (int j = 0; j < 1000; j++)
//            {
//                x = x * 1664525u + 1013904223u;
//                uint r = x % 13u;
//                if (r < 4u) sum += (int)x;
//                else if (r < 8u) sum ^= (int)x;
//                else sum -= (int)(x >> 3);
//                if ((x & 7u) == 0u) sum += j;
//            }
//            R[i] = sum;
//        }
//    }

//    [NativeTranspile(Target = BackendTarget.Ispc, MathLib = IspcMathLib.fast)]
//    public struct C08IntIspc : IJobParallelFor
//    {
//        public NativeArray<int> R;
//        public void Execute(int i)
//        {
//            int sum = 0;
//            uint x = (uint)(i * 2654435761u) + 1u;
//            for (int j = 0; j < 1000; j++)
//            {
//                x = x * 1664525u + 1013904223u;
//                uint r = x % 13u;
//                if (r < 4u) sum += (int)x;
//                else if (r < 8u) sum ^= (int)x;
//                else sum -= (int)(x >> 3);
//                if ((x & 7u) == 0u) sum += j;
//            }
//            R[i] = sum;
//        }
//    }

//    [NativeTranspile(Target = BackendTarget.Cpp, AutoSIMD = AutoSIMD.Enabled)]
//    public struct C08IntAuto : IJobParallelFor
//    {
//        public NativeArray<int> R;
//        public void Execute(int i)
//        {
//            int sum = 0;
//            uint x = (uint)(i * 2654435761u) + 1u;
//            for (int j = 0; j < 1000; j++)
//            {
//                x = x * 1664525u + 1013904223u;
//                uint r = x % 13u;
//                if (r < 4u) sum += (int)x;
//                else if (r < 8u) sum ^= (int)x;
//                else sum -= (int)(x >> 3);
//                if ((x & 7u) == 0u) sum += j;
//            }
//            R[i] = sum;
//        }
//    }

//    // ── C09_FindNearest: 找最近目标（min 距离平方归约 + sqrt，AI/碰撞常见） ──
//    [NativeTranspile(Target = BackendTarget.Cpp)]
//    public struct C09NearestCpp : IJobParallelFor
//    {
//        public NativeArray<float> A, B, R;
//        public void Execute(int i)
//        {
//            float best = float.MaxValue;
//            for (int j = 0; j < 16; j++)
//            {
//                float v = A[i] * (j + 1) + B[i];
//                float d = v * v;
//                if (d < best) best = d;
//            }
//            R[i] = MathF.Sqrt(best);
//        }
//    }
//    [NativeTranspile(Target = BackendTarget.Ispc, MathLib = IspcMathLib.fast)]
//    public struct C09NearestIspc : IJobParallelFor
//    {
//        public NativeArray<float> A, B, R;
//        public void Execute(int i)
//        {
//            float best = float.MaxValue;
//            for (int j = 0; j < 16; j++)
//            {
//                float v = A[i] * (j + 1) + B[i];
//                float d = v * v;
//                if (d < best) best = d;
//            }
//            R[i] = MathF.Sqrt(best);
//        }
//    }
//    [NativeTranspile(Target = BackendTarget.Cpp, AutoSIMD = AutoSIMD.Enabled)]
//    public struct C09NearestAuto : IJobParallelFor
//    {
//        public NativeArray<float> A, B, R;
//        public void Execute(int i)
//        {
//            float best = float.MaxValue;
//            for (int j = 0; j < 16; j++)
//            {
//                float v = A[i] * (j + 1) + B[i];
//                float d = v * v;
//                if (d < best) best = d;
//            }
//            R[i] = MathF.Sqrt(best);
//        }
//    }

//    // ── C10_MoveBounce: 移动 + 边界反弹（movement 系统，if + 复合赋值 + 速度反转） ──
//    [NativeTranspile(Target = BackendTarget.Cpp)]
//    public struct C10BounceCpp : IJobParallelFor
//    {
//        public NativeArray<float> A, B, R;
//        public void Execute(int i)
//        {
//            float pos = A[i];
//            float vel = B[i];
//            pos += vel * 0.1f;
//            if (pos > 100f) { pos = 100f; vel = -vel; }
//            else if (pos < -100f) { pos = -100f; vel = -vel; }
//            R[i] = pos + vel * 0.5f;
//        }
//    }
//    [NativeTranspile(Target = BackendTarget.Ispc, MathLib = IspcMathLib.fast)]
//    public struct C10BounceIspc : IJobParallelFor
//    {
//        public NativeArray<float> A, B, R;
//        public void Execute(int i)
//        {
//            float pos = A[i];
//            float vel = B[i];
//            pos += vel * 0.1f;
//            if (pos > 100f) { pos = 100f; vel = -vel; }
//            else if (pos < -100f) { pos = -100f; vel = -vel; }
//            R[i] = pos + vel * 0.5f;
//        }
//    }
//    [NativeTranspile(Target = BackendTarget.Cpp, AutoSIMD = AutoSIMD.Enabled)]
//    public struct C10BounceAuto : IJobParallelFor
//    {
//        public NativeArray<float> A, B, R;
//        public void Execute(int i)
//        {
//            float pos = A[i];
//            float vel = B[i];
//            pos += vel * 0.1f;
//            if (pos > 100f) { pos = 100f; vel = -vel; }
//            else if (pos < -100f) { pos = -100f; vel = -vel; }
//            R[i] = pos + vel * 0.5f;
//        }
//    }

//    // ── C11_Lifetime: 粒子生命周期（while 衰减 + break，粒子系统常见） ──
//    [NativeTranspile(Target = BackendTarget.Cpp)]
//    public struct C11LifetimeCpp : IJobParallelFor
//    {
//        public NativeArray<float> A, R;
//        public void Execute(int i)
//        {
//            float energy = A[i];
//            int frames = 0;
//            while (energy > 1f && frames < 16)
//            {
//                energy = energy * 0.9f;
//                frames = frames + 1;
//            }
//            R[i] = energy;
//        }
//    }
//    [NativeTranspile(Target = BackendTarget.Ispc, MathLib = IspcMathLib.fast)]
//    public struct C11LifetimeIspc : IJobParallelFor
//    {
//        public NativeArray<float> A, R;
//        public void Execute(int i)
//        {
//            float energy = A[i];
//            int frames = 0;
//            while (energy > 1f && frames < 16)
//            {
//                energy = energy * 0.9f;
//                frames = frames + 1;
//            }
//            R[i] = energy;
//        }
//    }
//    [NativeTranspile(Target = BackendTarget.Cpp, AutoSIMD = AutoSIMD.Enabled)]
//    public struct C11LifetimeAuto : IJobParallelFor
//    {
//        public NativeArray<float> A, R;
//        public void Execute(int i)
//        {
//            float energy = A[i];
//            int frames = 0;
//            while (energy > 1f && frames < 16)
//            {
//                energy = energy * 0.9f;
//                frames = frames + 1;
//            }
//            R[i] = energy;
//        }
//    }

//    // ── C12_SearchSkip: 查找 + continue + break（AI 搜索：跳过无效值，找到即停） ──
//    //   ⚠ 靶向 while 循环里的 continue 翻译（潜在 Bug D）。
//    [NativeTranspile(Target = BackendTarget.Cpp)]
//    public struct C12SearchCpp : IJobParallelFor
//    {
//        public NativeArray<float> A, B, R;
//        public void Execute(int i)
//        {
//            int found = -1;
//            int j = 0;
//            float acc = A[i];
//            while (j < 16)
//            {
//                float v = acc * (j + 1) + B[i];
//                if (v < 0f) { j = j + 1; continue; }
//                if (v > 50f) { found = j; break; }
//                acc = v;
//                j = j + 1;
//            }
//            R[i] = found;
//        }
//    }
//    [NativeTranspile(Target = BackendTarget.Ispc, MathLib = IspcMathLib.fast)]
//    public struct C12SearchIspc : IJobParallelFor
//    {
//        public NativeArray<float> A, B, R;
//        public void Execute(int i)
//        {
//            int found = -1;
//            int j = 0;
//            float acc = A[i];
//            while (j < 16)
//            {
//                float v = acc * (j + 1) + B[i];
//                if (v < 0f) { j = j + 1; continue; }
//                if (v > 50f) { found = j; break; }
//                acc = v;
//                j = j + 1;
//            }
//            R[i] = found;
//        }
//    }
//    [NativeTranspile(Target = BackendTarget.Cpp, AutoSIMD = AutoSIMD.Enabled)]
//    public struct C12SearchAuto : IJobParallelFor
//    {
//        public NativeArray<float> A, B, R;
//        public void Execute(int i)
//        {
//            int found = -1;
//            int j = 0;
//            float acc = A[i];
//            while (j < 16)
//            {
//                float v = acc * (j + 1) + B[i];
//                if (v < 0f) { j = j + 1; continue; }
//                if (v > 50f) { found = j; break; }
//                acc = v;
//                j = j + 1;
//            }
//            R[i] = found;
//        }
//    }

//    // ── C13_BoundBreak: while(uniform上限 && varying条件) + break（验证方向2标量化+break） ──
//    [NativeTranspile(Target = BackendTarget.Cpp)]
//    public struct C13BoundBreakCpp : IJobParallelFor
//    {
//        public NativeArray<float> A, R;
//        public void Execute(int i)
//        {
//            float acc = A[i];
//            int k = 0;
//            while (k < 16 && acc < 1000f)
//            {
//                if (acc > 500f) break;
//                acc = acc * 1.1f;
//                k = k + 1;
//            }
//            R[i] = acc;
//        }
//    }
//    [NativeTranspile(Target = BackendTarget.Ispc, MathLib = IspcMathLib.fast)]
//    public struct C13BoundBreakIspc : IJobParallelFor
//    {
//        public NativeArray<float> A, R;
//        public void Execute(int i)
//        {
//            float acc = A[i];
//            int k = 0;
//            while (k < 16 && acc < 1000f)
//            {
//                if (acc > 500f) break;
//                acc = acc * 1.1f;
//                k = k + 1;
//            }
//            R[i] = acc;
//        }
//    }
//    [NativeTranspile(Target = BackendTarget.Cpp, AutoSIMD = AutoSIMD.Enabled)]
//    public struct C13BoundBreakAuto : IJobParallelFor
//    {
//        public NativeArray<float> A, R;
//        public void Execute(int i)
//        {
//            float acc = A[i];
//            int k = 0;
//            while (k < 16 && acc < 1000f)
//            {
//                if (acc > 500f) break;
//                acc = acc * 1.1f;
//                k = k + 1;
//            }
//            R[i] = acc;
//        }
//    }

//    // ── C14_Cas: Interlocked.CompareExchange（ISPC 翻译补全验证：2026-08-30 Fix 1） ──
//    //   每个元素对独立槽位 T[i] 做 CAS：确定性（无跨 lane 竞争），oracle 可比较。
//    //   R[i] = 旧值；命中 comparand 时 T[i] 被改写为 V[i]（破坏性 → 测试侧每次运行前重置 T）。
//    //   ⚠ 仅 Cpp+Ispc 后端：AutoSIMD(Simd*) 无 Interlocked 翻译分支，不支持。
//    [NativeTranspile(Target = BackendTarget.Cpp)]
//    public unsafe struct C14CasCpp : IJobParallelFor
//    {
//        public int* T;
//        public NativeArray<int> V, C, R;
//        public void Execute(int i)
//        {
//            R[i] = Interlocked.CompareExchange(ref T[i], V[i], C[i]);
//        }
//    }
//    [NativeTranspile(Target = BackendTarget.Ispc)]
//    public unsafe struct C14CasIspc : IJobParallelFor
//    {
//        public int* T;
//        public NativeArray<int> V, C, R;
//        public void Execute(int i)
//        {
//            R[i] = Interlocked.CompareExchange(ref T[i], V[i], C[i]);
//        }
//    }

//    // =====================================================================
//    // C# 标量 oracle：与 job 完全同语义的托管实现（ground truth）。
//    // =====================================================================
//    public static class Oracles
//    {
//        public static float C01(float a, float b, float c) => a * b + c;

//        public static float C02(float a)
//        {
//            float acc = a;
//            for (int it = 0; it < 16; it++)
//            {
//                float w = MathF.Sin(acc + it * 0.03125f) + MathF.Cos(acc - it * 0.0625f);
//                acc = acc * 0.985f + w * 0.015f;
//            }
//            return acc;
//        }

//        public static float C03(float v, float b)
//        {
//            float r;
//            if (v > 90f) r = v * 0.5f;
//            else if (v > 60f) r = v * 0.25f;
//            else if (v > 30f) r = v * 0.125f;
//            else if (v > 0f) r = v * 0.0625f;
//            else r = v * -0.5f;
//            return r + b;
//        }

//        public static float C04(float a, float b, float c)
//        {
//            float acc = 0f;
//            for (int dx = -1; dx <= 1; dx++)
//                for (int dy = -1; dy <= 1; dy++)
//                {
//                    if (dx == 0 && dy == 0) continue;
//                    acc += (a * dx + b * dy) * (dx + dy + c);
//                }
//            return acc;
//        }

//        public static float C05(float a, float b)
//        {
//            int k = 0;
//            float acc = a;
//            while (k < 16)
//            {
//                if (acc >= 1000f) break;
//                acc = acc * 1.1f + b;
//                k = k + 1;
//            }
//            return acc;
//        }

//        public static float C06(float a, float b)
//        {
//            float best = float.MaxValue;
//            for (int j = 0; j < 16; j++)
//            {
//                float v = a * (j + 1) + b;
//                if (v < best) best = v;
//            }
//            return best;
//        }

//        public static float C07(float aIdx, float b) => aIdx * b;

//        public static int C08(int i)
//        {
//            int sum = 0;
//            uint x = (uint)(i * 2654435761u) + 1u;
//            for (int j = 0; j < 1000; j++)
//            {
//                x = x * 1664525u + 1013904223u;
//                uint r = x % 13u;
//                if (r < 4u) sum += (int)x;
//                else if (r < 8u) sum ^= (int)x;
//                else sum -= (int)(x >> 3);
//                if ((x & 7u) == 0u) sum += j;
//            }
//            return sum;
//        }

//        public static float C09(float a, float b)
//        {
//            float best = float.MaxValue;
//            for (int j = 0; j < 16; j++)
//            {
//                float v = a * (j + 1) + b;
//                float d = v * v;
//                if (d < best) best = d;
//            }
//            return MathF.Sqrt(best);
//        }

//        public static float C10(float pos, float vel)
//        {
//            pos += vel * 0.1f;
//            if (pos > 100f) { pos = 100f; vel = -vel; }
//            else if (pos < -100f) { pos = -100f; vel = -vel; }
//            return pos + vel * 0.5f;
//        }

//        public static float C11(float energy)
//        {
//            int frames = 0;
//            while (energy > 1f && frames < 16)
//            {
//                energy = energy * 0.9f;
//                frames = frames + 1;
//            }
//            return energy;
//        }

//        public static float C12(float a, float b)
//        {
//            int found = -1;
//            int j = 0;
//            float acc = a;
//            while (j < 16)
//            {
//                float v = acc * (j + 1) + b;
//                if (v < 0f) { j = j + 1; continue; }
//                if (v > 50f) { found = j; break; }
//                acc = v;
//                j = j + 1;
//            }
//            return found;
//        }

//        public static float C13(float a)
//        {
//            float acc = a;
//            int k = 0;
//            while (k < 16 && acc < 1000f)
//            {
//                if (acc > 500f) break;
//                acc = acc * 1.1f;
//                k = k + 1;
//            }
//            return acc;
//        }
//    }
//}
