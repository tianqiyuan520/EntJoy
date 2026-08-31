// main.cpp — ISPC 跨 SIMD 宽度边界一致性测试
// 对同一个 ISPC 源码分别用 sse4(4)/avx2(8)/avx512(16) 编译，验证
// foreach / foreach_tiled / reduce_add / 单例(programIndex!=0) 在不同宽度下结果一致。
#include <cstdio>
#include <cstdint>
#include <cmath>
#include <vector>
#include <algorithm>

extern "C" {
    void foreach_copy(float* src, float* dst, int32_t count);
    void foreach_tiled_copy(float* src, float* dst, int32_t count);
    float reduce_sum(float* src, int32_t count);
    void singleton_increment(int32_t* dst, int32_t count);
}

static int g_failures = 0;

static void check(bool ok, const char* what, int count) {
    if (!ok) {
        printf("  FAIL: %s at count=%d\n", what, count);
        ++g_failures;
    }
}

static void run_case(int count) {
    std::vector<float> src(count), dst(count);
    float expected_sum = 0.0f;
    for (int i = 0; i < count; ++i) {
        src[i] = static_cast<float>(i * 3 + 1) * 0.5f;
        expected_sum += src[i];
    }

    // foreach 尾块边界
    std::fill(dst.begin(), dst.end(), -999.0f);
    foreach_copy(src.data(), dst.data(), count);
    for (int i = 0; i < count; ++i)
        check(std::fabs(dst[i] - (src[i] * 2.0f + 1.0f)) < 1e-6f, "foreach_copy", count);

    // foreach_tiled 分块边界
    std::fill(dst.begin(), dst.end(), -999.0f);
    foreach_tiled_copy(src.data(), dst.data(), count);
    for (int i = 0; i < count; ++i)
        check(std::fabs(dst[i] - (src[i] * 2.0f + 1.0f)) < 1e-6f, "foreach_tiled_copy", count);

    // reduce_add 跨 lane 归约（浮点累加顺序随宽度变化，用容差）
    float s = reduce_sum(src.data(), count);
    check(std::fabs(s - expected_sum) < 1e-2f * static_cast<float>(count) + 1e-3f, "reduce_sum", count);

    // 单例执行（programIndex != 0 提前返回，仅 lane 0 累加 count 次）
    int32_t si[4] = {0, 0, 0, 0};
    singleton_increment(si, count);
    check(si[0] == count, "singleton_increment", count);
    check(si[1] == 0 && si[2] == 0 && si[3] == 0, "singleton_side_effect", count);
}

int main() {
    const int counts[] = {1, 3, 7, 8, 15, 16, 17, 31, 32, 100, 101, 1023};
    for (int c : counts) run_case(c);
    if (g_failures == 0) {
        printf("PASS: all boundary cases consistent\n");
        return 0;
    }
    printf("FAILURES: %d\n", g_failures);
    return 1;
}
