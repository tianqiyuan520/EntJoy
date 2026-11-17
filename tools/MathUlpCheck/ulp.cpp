// Fastest 档内联数学函数的标准 ULP 误差测量。
//
// 口径（与 libm 测试惯例一致）：
//   ulp(ref)  = 正确舍入结果所在的 ulp 间距；参考值由 double 计算后按 round-to-nearest 折回 float
//   err       = |bits(out) - bits(ref)|（位模式单调映射后的距离）
//   ref 为次正规或 0 时，改用绝对误差 / 2^-149（次正规 ulp）
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <cmath>
#include <vector>
#include <algorithm>
#include "NativeSIMD_math.h"

namespace
{
    constexpr double DenormUlp = 1.401298464324817e-45; // 2^-149

    int32_t OrderedBits(float v)
    {
        int32_t b;
        std::memcpy(&b, &v, sizeof(b));
        return b < 0 ? (int32_t)(0x80000000u - (uint32_t)b) : b;
    }

    struct Stat
    {
        const char* Name;
        long long Count = 0;
        double Max = 0, Sum = 0;
        float WorstIn = 0, WorstOut = 0, WorstRef = 0;
        std::vector<double> Samples;

        void Add(float in, float out, double reference)
        {
            float ref = (float)reference;
            if (!std::isfinite(ref)) return;                  // 溢出/未定义区间不计入
            double err;
            if (std::fabs(ref) >= 5.877471754111438e-39f)     // >= FLT_MIN：正常数按 ulp 距离
            {
                err = std::fabs((double)OrderedBits(out) - (double)OrderedBits(ref));
            }
            else                                              // 次正规与 0：按次正规 ulp 的绝对误差
            {
                err = std::fabs((double)out - (double)ref) / DenormUlp;
            }
            if (!std::isfinite(err)) return;

            Count++;
            Sum += err;
            if (err > Max) { Max = err; WorstIn = in; WorstOut = out; WorstRef = ref; }
            if (Samples.size() < 4000000) Samples.push_back(err);
        }

        void Report() const
        {
            std::vector<double> s = Samples;
            std::sort(s.begin(), s.end());
            double p999 = s.empty() ? 0 : s[std::min(s.size() - 1, (size_t)(s.size() * 0.999))];
            double mean = Count ? Sum / (double)Count : 0;
            std::printf("  %-6s n=%-7lld max=%7.2f  p99.9=%6.2f  mean=%6.3f  worst in=%.9g out=%.9g ref=%.9g\n",
                Name, Count, Max, p999, mean, (double)WorstIn, (double)WorstOut, (double)WorstRef);
        }
    };

    void Unary(const char* label, const char* name, n_float (*fn)(n_float), double (*ref)(double),
               double lo, double hi, int samples)
    {
        Stat st{ name };
        alignas(32) float in[8], out[8];
        for (int i = 0; i < samples; i += 8)
        {
            for (int k = 0; k < 8; k++)
            {
                double t = (double)(i + k) / (double)samples;
                in[k] = (float)(lo + (hi - lo) * t);
            }
            _mm256_storeu_ps(out, fn(_mm256_loadu_ps(in)));
            for (int k = 0; k < 8; k++) st.Add(in[k], out[k], ref((double)in[k]));
        }
        std::printf("%s\n", label);
        st.Report();
    }

    void Binary(const char* label, const char* name, n_float (*fn)(n_float, n_float), double (*ref)(double, double),
                double loA, double hiA, double loB, double hiB, int samples)
    {
        Stat st{ name };
        alignas(32) float a[8], b[8], out[8];
        for (int i = 0; i < samples; i += 8)
        {
            for (int k = 0; k < 8; k++)
            {
                double t = (double)(i + k) / (double)samples;
                a[k] = (float)(loA + (hiA - loA) * t);
                double u = std::fmod(t * 3.7 + 0.31, 1.0);   // 与 a 解耦，避免 atan2(a,a) 这类退化输入
                b[k] = (float)(loB + (hiB - loB) * u);
            }
            _mm256_storeu_ps(out, fn(_mm256_loadu_ps(a), _mm256_loadu_ps(b)));
            for (int k = 0; k < 8; k++) st.Add(a[k], out[k], ref((double)a[k], (double)b[k]));
        }
        std::printf("%s\n", label);
        st.Report();
    }

    void Sincos(const char* label, double lo, double hi, int samples)
    {
        Stat sinStat{ "sin" }, cosStat{ "cos" };
        alignas(32) float in[8], so[8], co[8];
        for (int i = 0; i < samples; i += 8)
        {
            for (int k = 0; k < 8; k++)
            {
                double t = (double)(i + k) / (double)samples;
                in[k] = (float)(lo + (hi - lo) * t);
            }
            __m256 x = _mm256_loadu_ps(in);
            _mm256_storeu_ps(so, _n_sin_avx2(x));
            _mm256_storeu_ps(co, _n_cos_avx2(x));
            for (int k = 0; k < 8; k++)
            {
                sinStat.Add(in[k], so[k], std::sin((double)in[k]));
                cosStat.Add(in[k], co[k], std::cos((double)in[k]));
            }
        }
        std::printf("%s\n", label);
        sinStat.Report();
        cosStat.Report();
    }

    void PowRange(const char* label, int samples)
    {
        Stat st{ "pow" };
        alignas(32) float a[8], b[8], out[8];
        for (int i = 0; i < samples; i += 8)
        {
            for (int k = 0; k < 8; k++)
            {
                double t = (double)(i + k) / (double)samples;
                a[k] = (float)(0.01 + 9.99 * t);
                b[k] = (float)(-3.0 + 6.0 * std::fmod(t * 3.7 + 0.31, 1.0));
            }
            _mm256_storeu_ps(out, N_POW(_mm256_loadu_ps(a), _mm256_loadu_ps(b)));
            for (int k = 0; k < 8; k++) st.Add(a[k], out[k], std::pow((double)a[k], (double)b[k]));
        }
        std::printf("%s\n", label);
        st.Report();
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
    std::printf("标准 ULP：|bits(out) - bits(正确舍入结果)|，次正规按 2^-149 计\n\n");

    Sincos("sin/cos: [-pi, pi]", -3.14159265358979323846, 3.14159265358979323846, 400000);
    Sincos("sin/cos: [0, 8pi]", 0.0, 25.132741228718345, 200000);
    Sincos("sin/cos: [-100, 100]", -100.0, 100.0, 200000);
    Sincos("sin/cos: [-1000, 1000]", -1000.0, 1000.0, 200000);
    Sincos("sin/cos: [-10000, 10000]", -10000.0, 10000.0, 200000);
    Sincos("sin/cos: [1e6, 1e7]", 1e6, 1e7, 200000);

    Unary("log: [1e-37, 1e38]", "log", _n_log_avx2, std::log, 1e-37, 1e38, 400000);
    Unary("exp: [-87, 88]", "exp", _n_exp_avx2, std::exp, -87.0, 88.0, 400000);

    Unary("tan: [-1.5, 1.5]", "tan", _n_tan_ps, std::tan, -1.5, 1.5, 400000);
    Unary("atan: [-20, 20]", "atan", _n_atan_ps, std::atan, -20.0, 20.0, 400000);
    Unary("asin: [-1, 1]", "asin", _n_asin_ps, std::asin, -1.0, 1.0, 400000);
    Unary("acos: [-1, 1]", "acos", _n_acos_ps, std::acos, -1.0, 1.0, 400000);
    Unary("sinh: [-10, 10]", "sinh", _n_sinh_ps, std::sinh, -10.0, 10.0, 400000);
    Unary("cosh: [-10, 10]", "cosh", _n_cosh_ps, std::cosh, -10.0, 10.0, 400000);
    Unary("tanh: [-5, 5]", "tanh", _n_tanh_ps, std::tanh, -5.0, 5.0, 400000);
    Binary("atan2: 全象限", "atan2", _n_atan2_ps, std::atan2, -10.0, 10.0, -10.0, 10.0, 400000);
    PowRange("pow (x>0): 底数[0.01,10] 指数[-3,3]", 400000);
    return 0;
}
