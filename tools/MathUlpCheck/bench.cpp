// 内联 SIMD 数学实现与原逐 lane libm 调用的吞吐对照。
// 每个函数交替跑 7 轮取中位，输入同一批数据。
#include <cstdio>
#include <cmath>
#include <vector>
#include <algorithm>
#include <chrono>
#include <cstdlib>
#include <functional>
#include "NativeSIMD_math.h"

namespace
{
    using Clock = std::chrono::steady_clock;
    // qemu 下元素数按 ENTJOY_BENCH_N 缩小（比值仍可比）；向上取整到 NSIMD_WIDTH 的整数倍
    static int BenchN()
    {
        const char* v = std::getenv("ENTJOY_BENCH_N");
        if (!v) return 1 << 20;
        char* end = nullptr;
        long n = std::strtol(v, &end, 10);
        if (end == v || *end != 0 || n <= 0)
        {
        std::fprintf(stderr, "ENTJOY_BENCH_N must be a positive integer\n");
            std::exit(2);
        }
        return (int)n;
        return ((n + NSIMD_WIDTH - 1) / NSIMD_WIDTH) * NSIMD_WIDTH;
    }
    static const int N = BenchN();
    double Median(std::vector<double> v)
    {
        std::sort(v.begin(), v.end());
        return v[v.size() / 2];
    }

    template <typename VecFn, typename ScalarFn>
    void Compare(const char* name, const float* in, VecFn vecFn, ScalarFn scalarFn, bool scalarLast = false)
    {
        std::vector<float> out(N);
        std::vector<double> vecNs, scNs;
        for (int r = 0; r < 7; r++)
        {
            auto t0 = Clock::now();
            for (int i = 0; i < N; i += NSIMD_WIDTH) vecFn(in + i, out.data() + i);
            auto t1 = Clock::now();
            for (int i = 0; i < N; i++) out[i] = scalarFn(in[i]);
            auto t2 = Clock::now();
            vecNs.push_back(std::chrono::duration<double, std::nano>(t1 - t0).count() / N);
            scNs.push_back(std::chrono::duration<double, std::nano>(t2 - t1).count() / N);
        }
        double v = Median(vecNs), s = Median(scNs);
        std::printf("  %-6s 内联 %6.2f ns   libm %6.2f ns   加速 %5.2fx%s\n",
            name, v, s, s / v, scalarLast ? "（double 路径）" : "");
    }
}

int main()
{
    std::vector<float> in(N);
    for (int i = 0; i < N; i++) in[i] = -1.0f + 2.0f * ((float)i / (float)N);
    std::vector<float> inPos(N);
    for (int i = 0; i < N; i++) inPos[i] = 0.01f + 10.0f * ((float)i / (float)N);

    std::printf("每元素耗时（%d 元素 x 7 轮，取中位）\n", N);

    Compare("exp", in.data(),
        [](const float* x, float* o) { n_store_ps(o, N_EXP(n_load_ps(x))); },
        [](float x) { return ::expf(x); });
    Compare("log", inPos.data(),
        [](const float* x, float* o) { n_store_ps(o, N_LOG(n_load_ps(x))); },
        [](float x) { return ::logf(x); });
    Compare("tan", in.data(),
        [](const float* x, float* o) { n_store_ps(o, N_TAN(n_load_ps(x))); },
        [](float x) { return ::tanf(x); });
    Compare("atan", in.data(),
        [](const float* x, float* o) { n_store_ps(o, N_ATAN(n_load_ps(x))); },
        [](float x) { return ::atanf(x); });
    Compare("asin", in.data(),
        [](const float* x, float* o) { n_store_ps(o, N_ASIN(n_load_ps(x))); },
        [](float x) { return ::asinf(x); });
    Compare("acos", in.data(),
        [](const float* x, float* o) { n_store_ps(o, N_ACOS(n_load_ps(x))); },
        [](float x) { return ::acosf(x); });
    Compare("sinh", in.data(),
        [](const float* x, float* o) { n_store_ps(o, N_SINH(n_load_ps(x))); },
        [](float x) { return ::sinhf(x); });
    Compare("cosh", in.data(),
        [](const float* x, float* o) { n_store_ps(o, N_COSH(n_load_ps(x))); },
        [](float x) { return ::coshf(x); });
    Compare("tanh", in.data(),
        [](const float* x, float* o) { n_store_ps(o, N_TANH(n_load_ps(x))); },
        [](float x) { return ::tanhf(x); });
    Compare("pow", inPos.data(),
        [](const float* x, float* o) {
            n_float b = n_set1_ps(1.7f);
            n_store_ps(o, N_POW(n_load_ps(x), b));
        },
        [](float x) { return ::powf(x, 1.7f); }, true);
    return 0;
}
