// Fastest 档（SIMD_MATH_PRECISION=1）内联多项式的误差测量。
//
// 参考值：double 计算后按 round-to-nearest 折回 float。
// 两个口径：
//   absULP = |out - ref| / 2^-24        —— 以 float 在 1.0 处的 ulp 为单位；sin/cos 的 |结果| ≤ 1，这一口径就是绝对精度
//   relULP = (|out - ref| / |ref|) / 2^-24 —— 相对精度；结果接近 0 时会爆炸，只对 |ref| ≥ 1e-3 的样本统计
//
// 用法（与 EntJoy 原生产物同工具链）：
//   cmake -S tools/MathUlpCheck -B tools/MathUlpCheck/build -G "Visual Studio 17 2022" -T ClangCL -A x64
//   cmake --build tools/MathUlpCheck/build --config Release
//   tools/MathUlpCheck/build/Release/MathUlpCheck.exe
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <cmath>
#include <vector>
#include <algorithm>
#include "NativeSIMD_math.h"

namespace
{
    constexpr double Eps1 = 5.9604644775390625e-8;  // 2^-24

    int32_t OrderedBits(float v)
    {
        int32_t b;
        std::memcpy(&b, &v, sizeof(b));
        return b < 0 ? (int32_t)(0x80000000u - (uint32_t)b) : b;
    }

    bool UlpDistance(float actual, float expected, double& out)
    {
        if (!std::isfinite(actual) || !std::isfinite(expected)) return false;
        out = std::abs((double)OrderedBits(actual) - (double)OrderedBits(expected));
        return true;
    }

    struct Stat
    {
        const char* Name;
        long long Count = 0;
        double MaxAbs = 0, SumAbs = 0;
        double MaxRel = 0, SumRel = 0;
        long long RelCount = 0;
        double MaxUlp = 0;
        float WorstIn = 0, WorstOut = 0, WorstRef = 0;

        void Add(float in, float out, double reference)
        {
            float ref = (float)reference;
            if (!std::isfinite(ref) || std::abs(ref) > 3.0e38f) return;
            double ulp;
            if (!UlpDistance(out, ref, ulp)) return;

            Count++;
            double absUlp = std::abs((double)out - (double)ref) / Eps1;
            SumAbs += absUlp;
            if (ulp > MaxUlp) MaxUlp = ulp;
            if (absUlp > MaxAbs)
            {
                MaxAbs = absUlp;
                WorstIn = in; WorstOut = out; WorstRef = ref;
            }
            if (std::abs((double)ref) >= 1e-3)
            {
                double relUlp = (std::abs((double)out - (double)ref) / std::abs((double)ref)) / Eps1;
                SumRel += relUlp;
                RelCount++;
                if (relUlp > MaxRel) MaxRel = relUlp;
            }
        }

        void Report() const
        {
            double meanAbs = Count ? SumAbs / (double)Count : 0;
            double meanRel = RelCount ? SumRel / (double)RelCount : 0;
            std::printf("  %-4s n=%-7lld absULP max=%8.3f mean=%8.3f | relULP max=%10.3f mean=%8.3f | worst in=%.9g out=%.9g ref=%.9g\n",
                Name, Count, MaxAbs, meanAbs, MaxRel, meanRel,
                (double)WorstIn, (double)WorstOut, (double)WorstRef);
        }
    };

    void RunTrig(const char* label, double lo, double hi, int samples)
    {
        Stat sinStat{ "sin" }, cosStat{ "cos" };
        alignas(32) float in[8], out[8];
        for (int i = 0; i < samples; i += 8)
        {
            for (int k = 0; k < 8; k++)
            {
                double t = (double)(i + k) / (double)samples;
                in[k] = (float)(lo + (hi - lo) * t);
            }
            __m256 x = _mm256_loadu_ps(in);
            _mm256_storeu_ps(out, _n_sin_avx2(x));
            for (int k = 0; k < 8; k++) sinStat.Add(in[k], out[k], std::sin((double)in[k]));
            _mm256_storeu_ps(out, _n_cos_avx2(x));
            for (int k = 0; k < 8; k++) cosStat.Add(in[k], out[k], std::cos((double)in[k]));
        }
        std::printf("%s\n", label);
        sinStat.Report();
        cosStat.Report();
    }


    void RunExp(const char* label, double lo, double hi, int samples)
    {
        Stat expStat{ "exp" };
        alignas(32) float in[8], out[8];
        for (int i = 0; i < samples; i += 8)
        {
            for (int k = 0; k < 8; k++)
            {
                double t = (double)(i + k) / (double)samples;
                in[k] = (float)(lo + (hi - lo) * t);
            }
            _mm256_storeu_ps(out, _n_exp_avx2(_mm256_loadu_ps(in)));
            for (int k = 0; k < 8; k++) expStat.Add(in[k], out[k], std::exp((double)in[k]));
        }
        std::printf("%s\n", label);
        expStat.Report();
    }

    void RunLog10(const char* label, double loExp, double hiExp, int samples)
    {
        Stat log10Stat{ "log10" };
        alignas(32) float in[8], out[8];
        for (int i = 0; i < samples; i += 8)
        {
            for (int k = 0; k < 8; k++)
            {
                double t = (double)(i + k) / (double)samples;
                in[k] = (float)std::pow(10.0, loExp + (hiExp - loExp) * t);
            }
            _mm256_storeu_ps(out, _mm256_mul_ps(_n_log_avx2(_mm256_loadu_ps(in)), _mm256_set1_ps(0.4342944819032518f)));
            for (int k = 0; k < 8; k++) log10Stat.Add(in[k], out[k], std::log10((double)in[k]));
        }
        std::printf("%s\n", label);
        log10Stat.Report();
    }
    void RunLog(const char* label, double loExp, double hiExp, int samples, bool denseNearOne)
    {
        Stat logStat{ "log" };
        alignas(32) float in[8], out[8];
        for (int i = 0; i < samples; i += 8)
        {
            for (int k = 0; k < 8; k++)
            {
                double t = (double)(i + k) / (double)samples;
                in[k] = denseNearOne ? (float)(1.0 + (t - 0.5) * 2e-3)
                                     : (float)std::pow(10.0, loExp + (hiExp - loExp) * t);
            }
            _mm256_storeu_ps(out, _n_log_avx2(_mm256_loadu_ps(in)));
            for (int k = 0; k < 8; k++) logStat.Add(in[k], out[k], std::log((double)in[k]));
        }
        std::printf("%s\n", label);
        logStat.Report();
    }
}

int main()
{
    std::printf("SIMD_MATH_PRECISION=%d NSIMD_WIDTH=%d AVX2=%s\n", SIMD_MATH_PRECISION, NSIMD_WIDTH,
#ifdef NSIMD_AVX2
        "on"
#else
        "off"
#endif
    );
    std::printf("absULP = |out-ref|/2^-24（|结果|<=1 时即绝对精度）；relULP = (|out-ref|/|ref|)/2^-24\n\n");

    RunTrig("sin/cos: [-pi, pi]", -3.14159265358979323846, 3.14159265358979323846, 400000);
    RunTrig("sin/cos: [0, 8pi]", 0.0, 25.132741228718345, 200000);
    RunTrig("sin/cos: [-100, 100]", -100.0, 100.0, 200000);
    RunTrig("sin/cos: [-1000, 1000]", -1000.0, 1000.0, 200000);
    RunTrig("sin/cos: [-10000, 10000]", -10000.0, 10000.0, 200000);

    RunLog("log: [1e-37, 1e38]", -37.0, 38.0, 400000, false);
    RunLog("log: [0.999, 1.001]", 0, 0, 200000, true);
    RunExp("exp: [-87, 88]", -87.0, 88.0, 400000);
    RunExp("exp: [-1, 1]", -1.0, 1.0, 200000);
    RunLog10("log10: [1e-37, 1e38]", -37.0, 38.0, 400000);
    return 0;
}
