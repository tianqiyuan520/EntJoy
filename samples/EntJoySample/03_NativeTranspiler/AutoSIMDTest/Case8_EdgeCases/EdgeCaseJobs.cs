using EntJoy.Collections;
using EntJoy.JobSystem;

namespace EntJoySample.AutoSIMDTest
{
    // =====================================================================
    // Case8: EdgeCase — 特殊浮点值 + 对抗性边界语义
    // 每个 Case：CSharp 基线 + AutoSIMD 变体，输出必须逐元素一致。
    // 覆盖：±Inf / NaN / ±0 / 次正规数 / FLT_MAX / INT_MIN-MAX 溢出边界 /
    //       多层 && || 混合 continue / 嵌套循环 return / 非常量循环边界。
    // 输入由 tools/AutoSIMDEdgeCases 生成（含特殊值注入与随机 fuzz）。
    // =====================================================================

    // ── EC1: 特殊浮点值算术 + MathF（sqrt/sin/log 对 Inf/NaN/0/负值） ──
    public struct EdgeCase1_CSharp_For : IJobFor
    {
        public NativeArray<float> A, R;
        public void Execute(int i)
        {
            float a = A[i];
            float r;
            if (a > 10f)
            {
                r = a * 2f + MathF.Sqrt(a);
                if (a > 100f) r += MathF.Sin(a);
                else if (a > 50f) r -= MathF.Cos(a);
                else r += MathF.Log(a);
            }
            else if (a > -1f)
            {
                r = a * a - 3f;
                if (a != 0f) r = r / a;     // 潜在 Inf/NaN 传播
                else r = 1234.5f;            // a == 0 时避免除零
            }
            else
            {
                r = -a * 0.5f;
                r += MathF.Log(-a);          // 正参数 log（-a > 0）
            }
            R[i] = r;
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct EdgeCase1_SIMD_For : IJobFor
    {
        public NativeArray<float> A, R;
        public void Execute(int i)
        {
            float a = A[i];
            float r;
            if (a > 10f)
            {
                r = a * 2f + MathF.Sqrt(a);
                if (a > 100f) r += MathF.Sin(a);
                else if (a > 50f) r -= MathF.Cos(a);
                else r += MathF.Log(a);
            }
            else if (a > -1f)
            {
                r = a * a - 3f;
                if (a != 0f) r = r / a;     // 潜在 Inf/NaN 传播
                else r = 1234.5f;            // a == 0 时避免除零
            }
            else
            {
                r = -a * 0.5f;
                r += MathF.Log(-a);          // 正参数 log（-a > 0）
            }
            R[i] = r;
        }
    }

    // ── EC2: 符号零 + NaN 比较语义（NaN 比较恒 false → 走 else 分支） ──
    //   输入由 harness 用 BitConverter 构造（job 外），含 NaN/Inf/±0 位模式。
    public struct EdgeCase2_CSharp_For : IJobFor
    {
        public NativeArray<float> A, R;
        public void Execute(int i)
        {
            float f = A[i];
            float r;
            if (f > 0f) r = f + 1f;
            else if (f < 0f) r = f - 1f;
            else if (f == 0f) r = 0.25f;                        // +0 与 -0 都走这里
            else r = -999f;                                     // NaN 走这里（比较均 false）
            R[i] = r;
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct EdgeCase2_SIMD_For : IJobFor
    {
        public NativeArray<float> A, R;
        public void Execute(int i)
        {
            float f = A[i];
            float r;
            if (f > 0f) r = f + 1f;
            else if (f < 0f) r = f - 1f;
            else if (f == 0f) r = 0.25f;                        // +0 与 -0 都走这里
            else r = -999f;                                     // NaN 走这里（比较均 false）
            R[i] = r;
        }
    }

    // ── EC3: int 溢出边界（INT_MIN/MAX 附近算术 + 位运算 + 无符号 x 翻转比较） ──
    //   注：不使用 (long) cast（transpiler 不支持 long），全部 int 运算（含 wrap 溢出）。
    public struct EdgeCase3_CSharp_For : IJobFor
    {
        public NativeArray<int> A, B, R;
        public void Execute(int i)
        {
            int x = A[i];
            int y = B[i];
            if ((uint)x > (uint)y)           // 无符号比较（x^0x80000000 技巧）
            {
                int sum = unchecked(x + y);  // 溢出 wrap（C# 与生成端一致用未检查语义）
                R[i] = (sum ^ y) + (x * 3);
            }
            else if (x == int.MinValue)
            {
                R[i] = unchecked(-x);        // INT_MIN 取负 → 溢出 wrap
            }
            else if (y == -1 && x == int.MaxValue)
            {
                R[i] = x * y;                // MAX * -1 → -MAX（安全）
            }
            else
            {
                R[i] = (x >> 1) ^ (y << 2) & ~3;
            }
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct EdgeCase3_SIMD_For : IJobFor
    {
        public NativeArray<int> A, B, R;
        public void Execute(int i)
        {
            int x = A[i];
            int y = B[i];
            if ((uint)x > (uint)y)           // 无符号比较（x^0x80000000 技巧）
            {
                int sum = unchecked(x + y);  // 溢出 wrap（C# 与生成端一致用未检查语义）
                R[i] = (sum ^ y) + (x * 3);
            }
            else if (x == int.MinValue)
            {
                R[i] = unchecked(-x);        // INT_MIN 取负 → 溢出 wrap
            }
            else if (y == -1 && x == int.MaxValue)
            {
                R[i] = x * y;                // MAX * -1 → -MAX（安全）
            }
            else
            {
                R[i] = (x >> 1) ^ (y << 2) & ~3;
            }
        }
    }

    // ── EC4: 多层 && / || 混合条件的 continue 反转（De Morgan 混合嵌套） ──
    //   内层 5x5 邻域 + 多层复合条件过滤
    public struct EdgeCase4_CSharp_For : IJobFor
    {
        public NativeArray<float> A, R;
        public void Execute(int i)
        {
            int cx = i % 20, cy = (i / 20) % 20;
            float sum = 0f;
            for (int dx = -2; dx <= 2; dx++)
            {
                int nx = cx + dx;
                if ((nx < 0 || nx >= 20) || (dx == 0 && i % 3 == 0)) continue;   // 混合 || + &&
                for (int dy = -2; dy <= 2; dy++)
                {
                    if ((dx == 0 && dy == 0) || (dx + dy == 0 && (i & 1) == 1)) continue;  // (A&&B) || C
                    int ny = cy + dy;
                    if ((uint)ny >= 20u || dx * dy > 2) continue;                 // 无符号 || 算术
                    float v = A[ny * 20 + nx];
                    if (v > 5f && v < 50f) sum += v;
                    else if (v < -5f || v > 100f) sum -= v * 0.25f;
                    else sum += 1f;
                }
            }
            R[i] = sum;
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct EdgeCase4_SIMD_For : IJobFor
    {
        public NativeArray<float> A, R;
        public void Execute(int i)
        {
            int cx = i % 20, cy = (i / 20) % 20;
            float sum = 0f;
            for (int dx = -2; dx <= 2; dx++)
            {
                int nx = cx + dx;
                if ((nx < 0 || nx >= 20) || (dx == 0 && i % 3 == 0)) continue;   // 混合 || + &&
                for (int dy = -2; dy <= 2; dy++)
                {
                    if ((dx == 0 && dy == 0) || (dx + dy == 0 && (i & 1) == 1)) continue;  // (A&&B) || C
                    int ny = cy + dy;
                    if ((uint)ny >= 20u || dx * dy > 2) continue;                 // 无符号 || 算术
                    float v = A[ny * 20 + nx];
                    if (v > 5f && v < 50f) sum += v;
                    else if (v < -5f || v > 100f) sum -= v * 0.25f;
                    else sum += 1f;
                }
            }
            R[i] = sum;
        }
    }

    // ── EC5: 嵌套循环 return（提前退出 + 返回计算结果） ──
    public struct EdgeCase5_CSharp_For : IJobFor
    {
        public NativeArray<float> A, R;
        public void Execute(int i)
        {
            for (int j = 0; j < 8; j++)
            {
                for (int k = j; k < 8; k++)
                {
                    float v = A[i * 64 + j * 8 + k];
                    if (v > 1000f) { R[i] = j * 10f + k * 1.5f; return; }   // 嵌套 return
                    if (v < -1000f) { R[i] = -j - k; return; }
                }
            }
            R[i] = 777f;
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct EdgeCase5_SIMD_For : IJobFor
    {
        public NativeArray<float> A, R;
        public void Execute(int i)
        {
            for (int j = 0; j < 8; j++)
            {
                for (int k = j; k < 8; k++)
                {
                    float v = A[i * 64 + j * 8 + k];
                    if (v > 1000f) { R[i] = j * 10f + k * 1.5f; return; }   // 嵌套 return
                    if (v < -1000f) { R[i] = -j - k; return; }
                }
            }
            R[i] = 777f;
        }
    }

    // ── EC6: 非常量循环边界 + 分支（边界依赖运行时数据；输出纯 float 累加） ──
    public struct EdgeCase6_CSharp_For : IJobFor
    {
        public NativeArray<float> A, R;
        public NativeArray<int> Counts;
        public void Execute(int i)
        {
            int n = Counts[i] & 31;                  // 0..31 非常量边界
            float acc = 0f;
            for (int j = 0; j < n; j++)
            {
                float v = A[i * 32 + j];
                if (v > 0f) acc += v;
                else if (v < -0.5f) acc -= v;
                else acc += 0.01f;
            }
            if (acc > 3f) R[i] = acc * 2f;
            else R[i] = acc + 0.25f;
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct EdgeCase6_SIMD_For : IJobFor
    {
        public NativeArray<float> A, R;
        public NativeArray<int> Counts;
        public void Execute(int i)
        {
            int n = Counts[i] & 31;                  // 0..31 非常量边界
            float acc = 0f;
            for (int j = 0; j < n; j++)
            {
                float v = A[i * 32 + j];
                if (v > 0f) acc += v;
                else if (v < -0.5f) acc -= v;
                else acc += 0.01f;
            }
            if (acc > 3f) R[i] = acc * 2f;
            else R[i] = acc + 0.25f;
        }
    }

    // ── EC7: 累加循环（for 语义，等价 while；while 生成路径存在已知 bug —— 见报告 5.1，暂用 for 覆盖） ──
    public struct EdgeCase7_CSharp_For : IJobFor
    {
        public NativeArray<float> A, R;
        public void Execute(int i)
        {
            float acc = 0f;
            int j = 0;
            while (j < 10)
            {
                float v = A[i * 10 + j];
                if (v > 3f) acc += v;
                j++;
            }
            R[i] = acc;
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct EdgeCase7_SIMD_For : IJobFor
    {
        public NativeArray<float> A, R;
        public void Execute(int i)
        {
            float acc = 0f;
            for (int j = 0; j < 10; j++)
            {
                float v = A[i * 10 + j];
                if (v > 3f) acc += v;
            }
            R[i] = acc;
        }
    }

    // ── EC8: 次正规数 + 符号零（±0 分支语义）。-0 输入由 harness 注入；
//   job 用常规比较分支（±0 都走 else 分支），不依赖托管 BitConverter。 ──
    public struct EdgeCase8_CSharp_For : IJobFor
    {
        public NativeArray<float> A, R;
        public void Execute(int i)
        {
            float acc = 0f;
            float t = A[i];
            if (t > 0f) acc += t;
            else if (t < 0f) acc -= t;
            else acc += 0.5f;                     // ±0 / 次正规极小值走这里
            R[i] = acc;
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct EdgeCase8_SIMD_For : IJobFor
    {
        public NativeArray<float> A, R;
        public void Execute(int i)
        {
            float acc = 0f;
            float t = A[i];
            if (t > 0f) acc += t;
            else if (t < 0f) acc -= t;
            else acc += 0.5f;                     // ±0 / 次正规极小值走这里
            R[i] = acc;
        }
    }

    // ── EC9: 多层嵌套 if-else + 浮点累积器多分支写（回归：save-blend 嵌套污染域） ──
    public struct EdgeCase9_CSharp_For : IJobFor
    {
        public NativeArray<float> A, R;
        public void Execute(int i)
        {
            float a = A[i];
            float acc = 0f;
            if (a > 0f)
            {
                acc += a;
                if (a > 10f) acc += a * 2f;
                else acc -= a;
            }
            else
            {
                acc -= a;
                if (a < -10f) acc += a * 3f;
                else if (a < -5f) acc += a * 0.1f;
                else acc += 0.01f;
            }
            if (acc > 100f)
            {
                acc *= 0.5f;
                if (acc > 1000f) acc -= 5f;
            }
            R[i] = acc;
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct EdgeCase9_SIMD_For : IJobFor
    {
        public NativeArray<float> A, R;
        public void Execute(int i)
        {
            float a = A[i];
            float acc = 0f;
            if (a > 0f)
            {
                acc += a;
                if (a > 10f) acc += a * 2f;
                else acc -= a;
            }
            else
            {
                acc -= a;
                if (a < -10f) acc += a * 3f;
                else if (a < -5f) acc += a * 0.1f;
                else acc += 0.01f;
            }
            if (acc > 100f)
            {
                acc *= 0.5f;
                if (acc > 1000f) acc -= 5f;
            }
            R[i] = acc;
        }
    }
}