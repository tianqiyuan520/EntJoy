using EntJoy.Collections;
using EntJoy.JobSystem;

namespace EntJoySample.AutoSIMDTest
{
    // =====================================================================
    // 瀵规姉鎬у帇鍔涙祴璇曪紙宓屽 脳 鍒嗘敮 脳 寰幆 脳 澶氬彉閲忥級
    // 姣忎釜 Case锛欳Sharp 鍩虹嚎 + AutoSIMD 鍙樹綋锛岃緭鍑哄繀椤婚€愬厓绱犱竴鑷淬€?
    // 瑕嗙洊锛氭繁灞傚垎鏀摼 / 澶氬彉閲忓垎鏀啓 / 寰幆鍐呭垎鏀?/ 鍒嗘敮鍐呭惊鐜?/
    //       reduction+鍒嗘敮 / 宓屽寰幆+鍒嗘敮 / break 鎺у埗娴?/ uint 娣峰悎 /
    //       gather+鍒嗘敮 / 澶氭暟缁?merge銆?
    // =====================================================================

    // 鈹€鈹€ ST1: 娣卞眰 if-else-if 閾撅紙5 鍒嗘敮锛夆攢鈹€
    public struct Stress1_CSharp_For : IJobFor
    {
        public NativeArray<float> A, R;
        public void Execute(int i)
        {
            float a = A[i];
            float v;
            if (a > 40f) v = a * 2f;
            else if (a > 20f) v = a + 1f;
            else if (a > 0f) v = a - 1f;
            else if (a > -20f) v = a * 0.5f;
            else v = -a * 3f;
            R[i] = v;
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct Stress1_SIMD_For : IJobFor
    {
        public NativeArray<float> A, R;
        public void Execute(int i)
        {
            float a = A[i];
            float v;
            if (a > 40f) v = a * 2f;
            else if (a > 20f) v = a + 1f;
            else if (a > 0f) v = a - 1f;
            else if (a > -20f) v = a * 0.5f;
            else v = -a * 3f;
            R[i] = v;
        }
    }

    // 鈹€鈹€ ST2: 鍒嗘敮淇敼澶氫釜 varying 鍙橀噺 鈹€鈹€
    public struct Stress2_CSharp_For : IJobFor
    {
        public NativeArray<float> A, B, R;
        public void Execute(int i)
        {
            float x = A[i];
            float y = B[i];
            float t = x * 0.1f;
            if (A[i] > 0f) { x = x * 2f; y = y + 1f; t = t - 3f; }
            else { x = x + 1f; y = y * 2f; t = t + 3f; }
            R[i] = x + y + t;
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct Stress2_SIMD_For : IJobFor
    {
        public NativeArray<float> A, B, R;
        public void Execute(int i)
        {
            float x = A[i];
            float y = B[i];
            float t = x * 0.1f;
            if (A[i] > 0f) { x = x * 2f; y = y + 1f; t = t - 3f; }
            else { x = x + 1f; y = y * 2f; t = t + 3f; }
            R[i] = x + y + t;
        }
    }

    // 鈹€鈹€ ST3: 鍐呭眰寰幆 + 鍒嗘敮锛坧arity accumulate锛夆攢鈹€
    public struct Stress3_CSharp_For : IJobFor
    {
        public NativeArray<float> A, R;
        public void Execute(int i)
        {
            float acc = 0f;
            for (int j = 0; j < 100; j++)
            {
                float v = A[i * 100 + j];
                if ((j & 1) == 0) acc += v;
                else acc -= v;
            }
            R[i] = acc;
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct Stress3_SIMD_For : IJobFor
    {
        public NativeArray<float> A, R;
        public void Execute(int i)
        {
            float acc = 0f;
            for (int j = 0; j < 100; j++)
            {
                float v = A[i * 100 + j];
                if ((j & 1) == 0) acc += v;
                else acc -= v;
            }
            R[i] = acc;
        }
    }

    // 鈹€鈹€ ST4: 鍒嗘敮鍐?reduction + 澶栭儴鍒嗘敮 鈹€鈹€
    public struct Stress4_CSharp_For : IJobFor
    {
        public NativeArray<float> A, R;
        public void Execute(int i)
        {
            float best = float.MaxValue;
            for (int j = 0; j < 50; j++)
            {
                float v = A[i * 50 + j];
                if (v < best) best = v;
            }
            float r;
            if (best < 0f) r = best; else r = -best;
            R[i] = r;
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct Stress4_SIMD_For : IJobFor
    {
        public NativeArray<float> A, R;
        public void Execute(int i)
        {
            float best = float.MaxValue;
            for (int j = 0; j < 50; j++)
            {
                float v = A[i * 50 + j];
                if (v < best) best = v;
            }
            float r;
            if (best < 0f) r = best; else r = -best;
            R[i] = r;
        }
    }

    // 鈹€鈹€ ST5: 宓屽寰幆 + 宓屽鍒嗘敮锛?x3 閭诲煙锛夆攢鈹€
    public struct Stress5_CSharp_For : IJobFor
    {
        public NativeArray<float> A, R;
        public void Execute(int i)
        {
            int cx = i % 64, cy = (i / 64) % 64;
            float sum = 0f;
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = cx + dx;
                if ((uint)nx >= 64u) continue;
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int ny = cy + dy;
                    if ((uint)ny >= 64u) continue;
                    float v = A[ny * 64 + nx];
                    if (v > 10f) sum += v;
                    else if (v < -10f) sum -= v;
                }
            }
            R[i] = sum;
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct Stress5_SIMD_For : IJobFor
    {
        public NativeArray<float> A, R;
        public void Execute(int i)
        {
            int cx = i % 64, cy = (i / 64) % 64;
            float sum = 0f;
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = cx + dx;
                if ((uint)nx >= 64u) continue;
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int ny = cy + dy;
                    if ((uint)ny >= 64u) continue;
                    float v = A[ny * 64 + nx];
                    if (v > 10f) sum += v;
                    else if (v < -10f) sum -= v;
                }
            }
            R[i] = sum;
        }
    }

    // 鈹€鈹€ ST6: 澶?int 鍙橀噺 if-else 閾?+ 浣嶈繍绠?鈹€鈹€
    public struct Stress6_CSharp_For : IJobFor
    {
        public NativeArray<int> A, B, R;
        public void Execute(int i)
        {
            int p = A[i];
            int q = B[i];
            int s = p ^ q;
            if ((uint)p > (uint)q) { p = p + 1; q = q - 2; s = s ^ 7; }
            else if (p < -100) { p = p - 1; q = q + 2; s = s & ~3; }
            else { p = 0; q = 0; s = 0; }
            R[i] = p + q + s;
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct Stress6_SIMD_For : IJobFor
    {
        public NativeArray<int> A, B, R;
        public void Execute(int i)
        {
            int p = A[i];
            int q = B[i];
            int s = p ^ q;
            if ((uint)p > (uint)q) { p = p + 1; q = q - 2; s = s ^ 7; }
            else if (p < -100) { p = p - 1; q = q + 2; s = s & ~3; }
            else { p = 0; q = 0; s = 0; }
            R[i] = p + q + s;
        }
    }

    // 鈹€鈹€ ST7: reduction 鍙岀疮绉紙min + max 澶氬彉閲忥級+ 鍒嗘敮 鈹€鈹€
    public struct Stress7_CSharp_For : IJobFor
    {
        public NativeArray<float> A, R;
        public void Execute(int i)
        {
            float mn = float.MaxValue, mx = -float.MaxValue;
            for (int j = 0; j < 64; j++)
            {
                float v = A[i * 64 + j];
                if (v < mn) mn = v;
                if (v > mx) mx = v;
            }
            float r;
            if (mn * mx > 0f) r = mn * 2f + mx;
            else r = -mn - mx * 2f;
            R[i] = r;
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct Stress7_SIMD_For : IJobFor
    {
        public NativeArray<float> A, R;
        public void Execute(int i)
        {
            float mn = float.MaxValue, mx = -float.MaxValue;
            for (int j = 0; j < 64; j++)
            {
                float v = A[i * 64 + j];
                if (v < mn) mn = v;
                if (v > mx) mx = v;
            }
            float r;
            if (mn * mx > 0f) r = mn * 2f + mx;
            else r = -mn - mx * 2f;
            R[i] = r;
        }
    }

    // 鈹€鈹€ ST8: 鎻愬墠 break + 鍒嗘敮 鈹€鈹€
    public struct Stress8_CSharp_For : IJobFor
    {
        public NativeArray<float> A;
        public NativeArray<int> R;
        public void Execute(int i)
        {
            int first = -1;
            for (int j = 0; j < 100; j++)
            {
                if (A[i * 100 + j] > 50f) { first = j; break; }
            }
            R[i] = first;
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct Stress8_SIMD_For : IJobFor
    {
        public NativeArray<float> A;
        public NativeArray<int> R;
        public void Execute(int i)
        {
            int first = -1;
            for (int j = 0; j < 100; j++)
            {
                if (A[i * 100 + j] > 50f) { first = j; break; }
            }
            R[i] = first;
        }
    }

    // 鈹€鈹€ ST9: uint 娣峰悎姣旇緝 + 绉讳綅 + 鍒嗘敮 鈹€鈹€
    public struct Stress9_CSharp_For : IJobFor
    {
        public NativeArray<int> A, R;
        public void Execute(int i)
        {
            uint x = (uint)(A[i] * 3u);
            int sum = 0;
            if (x < 1000u) sum += (int)(x * 2u);
            else if (x < 100000u) sum -= (int)(x >> 2);
            else sum += (int)(x ^ 0x5555u);
            R[i] = sum;
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct Stress9_SIMD_For : IJobFor
    {
        public NativeArray<int> A, R;
        public void Execute(int i)
        {
            uint x = (uint)(A[i] * 3u);
            int sum = 0;
            if (x < 1000u) sum += (int)(x * 2u);
            else if (x < 100000u) sum -= (int)(x >> 2);
            else sum += (int)(x ^ 0x5555u);
            R[i] = sum;
        }
    }

    // 鈹€鈹€ ST10: gather + 鍒嗘敮绱姞锛堝鏁扮粍绱㈠紩锛夆攢鈹€
    public struct Stress10_CSharp_For : IJobFor
    {
        public NativeArray<float> Q, D, R;
        public NativeArray<int> Idx;
        public void Execute(int i)
        {
            float q = Q[i];
            float acc = 0f;
            for (int j = 0; j < 32; j++)
            {
                int idx = Idx[i * 32 + j];
                float d = D[idx];
                if (d < q) acc += d;
                else if (d > q * 2f) acc += d * 0.5f;
                else acc -= d;
            }
            R[i] = acc;
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct Stress10_SIMD_For : IJobFor
    {
        public NativeArray<float> Q, D, R;
        public NativeArray<int> Idx;
        public void Execute(int i)
        {
            float q = Q[i];
            float acc = 0f;
            for (int j = 0; j < 32; j++)
            {
                int idx = Idx[i * 32 + j];
                float d = D[idx];
                if (d < q) acc += d;
                else if (d > q * 2f) acc += d * 0.5f;
                else acc -= d;
            }
            R[i] = acc;
        }
    }
}
