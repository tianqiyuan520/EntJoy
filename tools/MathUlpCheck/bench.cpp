// 对照 Fastest 档内联 exp 与原来逐 lane 调 expf 的开销。
// 同一批数据、同一编译器档，交替多轮取中位数，避免缓存/降频偏向。
#include <cstdio>
#include <cstdint>
#include <cmath>
#include <vector>
#include <algorithm>
#include <chrono>
#include "NativeSIMD_math.h"

namespace
{
    using Clock = std::chrono::steady_clock;

    double Median(std::vector<double> v)
    {
        std::sort(v.begin(), v.end());
        return v[v.size() / 2];
    }

    // 逐 lane 标量：等价于改动前的 _n_exp_fallback
    static void ExpFallback(const float* in, float* out, int n)
    {
        for (int i = 0; i < n; i += NSIMD_WIDTH)
        {
            for (int k = 0; k < NSIMD_WIDTH; k++) out[i + k] = ::expf(in[i + k]);
        }
    }

    static void ExpInline(const float* in, float* out, int n)
    {
        for (int i = 0; i < n; i += NSIMD_WIDTH)
        {
            _mm256_storeu_ps(out + i, _n_exp_avx2(_mm256_loadu_ps(in + i)));
        }
    }
}

int main()
{
    constexpr int N = 1 << 22;          // 4M 元素
    constexpr int Rounds = 7;
    std::vector<float> in(N), out(N);
    for (int i = 0; i < N; i++) in[i] = -20.0f + 40.0f * ((float)i / (float)N);

    // 预热，让首次缺页与频率爬升落在计时之外
    ExpFallback(in.data(), out.data(), N);
    ExpInline(in.data(), out.data(), N);

    std::vector<double> scalarNs, inlineNs;
    unsigned long long sink = 0;
    for (int r = 0; r < Rounds; r++)
    {
        auto t0 = Clock::now();
        ExpFallback(in.data(), out.data(), N);
        auto t1 = Clock::now();
        ExpInline(in.data(), out.data(), N);
        auto t2 = Clock::now();

        scalarNs.push_back(std::chrono::duration<double, std::nano>(t1 - t0).count() / N);
        inlineNs.push_back(std::chrono::duration<double, std::nano>(t2 - t1).count() / N);
        sink += (unsigned long long)(out[N / 2] * 1e6) + (unsigned long long)(out[N / 3] * 1e6);
    }

    double s = Median(scalarNs), v = Median(inlineNs);
    std::printf("exp  per-element（%d 元素 x %d 轮，取中位）\n", N, Rounds);
    std::printf("  逐 lane expf : %6.2f ns/元素  （%.1f M 元素/秒）\n", s, 1000.0 / s);
    std::printf("  内联 AVX2    : %6.2f ns/元素  （%.1f M 元素/秒）\n", v, 1000.0 / v);
    std::printf("  加速         : %5.2fx\n", s / v);
    if (sink == 0) std::printf("\n");
    return 0;
}
