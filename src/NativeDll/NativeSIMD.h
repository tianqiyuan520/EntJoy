// NativeSIMD.h — 跨平台 SIMD 抽象层
// 提供 AVX2 / SSE4 / NEON / 标量回退的统一接口
// 所有加载使用非对齐访问（loadu），不需额外对齐保证
#pragma once
#include <cmath>
#include <cstring>
#include <algorithm>
#include <limits>

// ============================================================
// 平台检测
// ============================================================
#if defined(__AVX2__)
    #define NSIMD_AVX2 1
    #define NSIMD_WIDTH 8
    #include <immintrin.h>
#elif defined(__SSE4_2__) || defined(__SSE4_1__) || defined(__SSE__)
    #define NSIMD_SSE4 1
    #define NSIMD_WIDTH 4
    #include <smmintrin.h>
#else
    #define NSIMD_SCALAR 1
    #define NSIMD_WIDTH 1
#endif

// ============================================================
// 类型定义
// ============================================================
#if defined(NSIMD_AVX2)
    typedef __m256  n_float;
    typedef __m256i n_int;
    typedef __m256  n_mask;
#elif defined(NSIMD_SSE4)
    typedef __m128  n_float;
    typedef __m128i n_int;
    typedef __m128  n_mask;
#else
    typedef float  n_float;
    typedef int    n_int;
    typedef bool   n_mask;
#endif

// ============================================================
// Load / Store（全部非对齐）
// ============================================================
static inline n_float n_load_ps(const float* p) {
#if defined(NSIMD_AVX2)
    return _mm256_loadu_ps(p);
#elif defined(NSIMD_SSE4)
    return _mm_loadu_ps(p);
#else
    return *p;
#endif
}

static inline void n_store_ps(float* p, n_float v) {
#if defined(NSIMD_AVX2)
    _mm256_storeu_ps(p, v);
#elif defined(NSIMD_SSE4)
    _mm_storeu_ps(p, v);
#else
    *p = v;
#endif
}

static inline n_int n_load_epi32(const int* p) {
#if defined(NSIMD_AVX2)
    return _mm256_loadu_si256((const __m256i*)p);
#elif defined(NSIMD_SSE4)
    return _mm_loadu_si128((const __m128i*)p);
#else
    return *p;
#endif
}

static inline void n_store_epi32(int* p, n_int v) {
#if defined(NSIMD_AVX2)
    _mm256_storeu_si256((__m256i*)p, v);
#elif defined(NSIMD_SSE4)
    _mm_storeu_si128((__m128i*)p, v);
#else
    *p = v;
#endif
}

// ============================================================
// Broadcast（标量 → 向量）
// ============================================================
static inline n_float n_set1_ps(float s) {
#if defined(NSIMD_AVX2)
    return _mm256_set1_ps(s);
#elif defined(NSIMD_SSE4)
    return _mm_set1_ps(s);
#else
    return s;
#endif
}

static inline n_int n_set1_epi32(int s) {
#if defined(NSIMD_AVX2)
    return _mm256_set1_epi32(s);
#elif defined(NSIMD_SSE4)
    return _mm_set1_epi32(s);
#else
    return s;
#endif
}

static inline n_int n_set_epi32(int i7, int i6, int i5, int i4,
                                int i3, int i2, int i1, int i0) {
#if defined(NSIMD_AVX2)
    return _mm256_set_epi32(i7, i6, i5, i4, i3, i2, i1, i0);
#elif defined(NSIMD_SSE4)
    (void)i7; (void)i6; (void)i5; (void)i4;
    return _mm_set_epi32(i3, i2, i1, i0);
#else
    (void)i7; (void)i6; (void)i5; (void)i4; (void)i3; (void)i2;
    return i1 ? i0 : i0;
#endif
}

// ============================================================
// 算术运算
// ============================================================
static inline n_float n_add_ps(n_float a, n_float b) {
#if defined(NSIMD_AVX2)
    return _mm256_add_ps(a, b);
#elif defined(NSIMD_SSE4)
    return _mm_add_ps(a, b);
#else
    return a + b;
#endif
}

static inline n_float n_sub_ps(n_float a, n_float b) {
#if defined(NSIMD_AVX2)
    return _mm256_sub_ps(a, b);
#elif defined(NSIMD_SSE4)
    return _mm_sub_ps(a, b);
#else
    return a - b;
#endif
}

static inline n_float n_mul_ps(n_float a, n_float b) {
#if defined(NSIMD_AVX2)
    return _mm256_mul_ps(a, b);
#elif defined(NSIMD_SSE4)
    return _mm_mul_ps(a, b);
#else
    return a * b;
#endif
}

static inline n_float n_fmadd_ps(n_float a, n_float b, n_float c) {
#if defined(NSIMD_AVX2)
    return _mm256_fmadd_ps(a, b, c);
#elif defined(NSIMD_SSE4) && defined(__FMA__)
    return _mm_fmadd_ps(a, b, c);
#else
    return a * b + c;
#endif
}

// ============================================================
// Min / Max
// ============================================================
static inline n_float n_min_ps(n_float a, n_float b) {
#if defined(NSIMD_AVX2)
    return _mm256_min_ps(a, b);
#elif defined(NSIMD_SSE4)
    return _mm_min_ps(a, b);
#else
    return (a < b) ? a : b;
#endif
}

static inline n_float n_max_ps(n_float a, n_float b) {
#if defined(NSIMD_AVX2)
    return _mm256_max_ps(a, b);
#elif defined(NSIMD_SSE4)
    return _mm_max_ps(a, b);
#else
    return (a > b) ? a : b;
#endif
}

// ============================================================
// 比较 → mask
// ============================================================
static inline n_mask n_cmp_lt_ps(n_float a, n_float b) {
#if defined(NSIMD_AVX2)
    return _mm256_cmp_ps(a, b, _CMP_LT_OS);
#elif defined(NSIMD_SSE4)
    return _mm_cmplt_ps(a, b);
#else
    return a < b;
#endif
}

static inline n_mask n_cmp_gt_ps(n_float a, n_float b) {
#if defined(NSIMD_AVX2)
    return _mm256_cmp_ps(a, b, _CMP_GT_OS);
#elif defined(NSIMD_SSE4)
    return _mm_cmpgt_ps(a, b);
#else
    return a > b;
#endif
}

// ============================================================
// Mask 逻辑运算
// ============================================================
static inline n_mask n_and_mask(n_mask a, n_mask b) {
#if defined(NSIMD_AVX2)
    return _mm256_and_ps(a, b);
#elif defined(NSIMD_SSE4)
    return _mm_and_ps(a, b);
#else
    return a && b;
#endif
}

static inline n_mask n_andnot_mask(n_mask a, n_mask b) {
#if defined(NSIMD_AVX2)
    return _mm256_andnot_ps(a, b);
#elif defined(NSIMD_SSE4)
    return _mm_andnot_ps(a, b);
#else
    return !a && b;
#endif
}

// ============================================================
// Blend（条件选择）
// ============================================================
static inline n_float n_blend_ps(n_float v_false, n_float v_true, n_mask mask) {
#if defined(NSIMD_AVX2)
    return _mm256_blendv_ps(v_false, v_true, mask);
#elif defined(NSIMD_SSE4)
    return _mm_blendv_ps(v_false, v_true, mask);
#else
    return mask ? v_true : v_false;
#endif
}

static inline n_int n_blend_epi32(n_int v_false, n_int v_true, n_mask mask) {
#if defined(NSIMD_AVX2)
    return _mm256_castps_si256(
        _mm256_blendv_ps(_mm256_castsi256_ps(v_false), _mm256_castsi256_ps(v_true), mask));
#elif defined(NSIMD_SSE4)
    return _mm_castps_si128(
        _mm_blendv_ps(_mm_castsi128_ps(v_false), _mm_castsi128_ps(v_true), mask));
#else
    return mask ? v_true : v_false;
#endif
}

// ============================================================
// Gather（AoS stride gather）
// ============================================================
template<int stride>
static inline n_float n_gather_ps(const float* base, n_int indices) {
#if defined(NSIMD_AVX2)
    return _mm256_i32gather_ps(base, indices, stride);
#else
    int w = NSIMD_WIDTH;
    int idx[8];
    float val[8];
    n_store_epi32(idx, indices);
    const char* cb = (const char*)base;
    for (int i = 0; i < w; i++)
        val[i] = *(const float*)(cb + idx[i] * stride);
    return n_load_ps(val);
#endif
}

#if NSIMD_WIDTH > 1
static inline n_int n_gather_epi32(const int* base, n_int indices) {
#if defined(NSIMD_AVX2)
    return _mm256_i32gather_epi32(base, indices, 4);
#else
    int w = NSIMD_WIDTH;
    int idx[8];
    int val[8];
    n_store_epi32(idx, indices);
    for (int i = 0; i < w; i++)
        val[i] = base[idx[i]];
    return n_load_epi32(val);
#endif
}
#endif

// ============================================================
// 全零检测
// ============================================================
static inline int n_all_zero(n_mask mask) {
#if defined(NSIMD_AVX2)
    return _mm256_testz_ps(mask, mask);
#elif defined(NSIMD_SSE4)
    return _mm_testz_ps(mask, mask);
#else
    return mask ? 0 : 1;
#endif
}

// ============================================================
// 水平规约
// ============================================================

// n_hmin_ps: 向量 → 标量最小值
static inline float n_hmin_ps(n_float v) {
#if defined(NSIMD_AVX2)
    __m128 lo = _mm256_castps256_ps128(v);
    __m128 hi = _mm256_extractf128_ps(v, 1);
    __m128 m = _mm_min_ps(lo, hi);
    m = _mm_min_ps(m, _mm_shuffle_ps(m, m, _MM_SHUFFLE(2, 3, 0, 1)));
    m = _mm_min_ps(m, _mm_shuffle_ps(m, m, _MM_SHUFFLE(1, 0, 3, 2)));
    return _mm_cvtss_f32(m);
#elif defined(NSIMD_SSE4)
    __m128 m = v;
    m = _mm_min_ps(m, _mm_shuffle_ps(m, m, _MM_SHUFFLE(2, 3, 0, 1)));
    m = _mm_min_ps(m, _mm_shuffle_ps(m, m, _MM_SHUFFLE(1, 0, 3, 2)));
    return _mm_cvtss_f32(m);
#else
    return v;
#endif
}

// n_hmax_ps: 向量 → 标量最大值
static inline float n_hmax_ps(n_float v) {
#if defined(NSIMD_AVX2)
    __m128 lo = _mm256_castps256_ps128(v);
    __m128 hi = _mm256_extractf128_ps(v, 1);
    __m128 m = _mm_max_ps(lo, hi);
    m = _mm_max_ps(m, _mm_shuffle_ps(m, m, _MM_SHUFFLE(2, 3, 0, 1)));
    m = _mm_max_ps(m, _mm_shuffle_ps(m, m, _MM_SHUFFLE(1, 0, 3, 2)));
    return _mm_cvtss_f32(m);
#elif defined(NSIMD_SSE4)
    __m128 m = v;
    m = _mm_max_ps(m, _mm_shuffle_ps(m, m, _MM_SHUFFLE(2, 3, 0, 1)));
    m = _mm_max_ps(m, _mm_shuffle_ps(m, m, _MM_SHUFFLE(1, 0, 3, 2)));
    return _mm_cvtss_f32(m);
#else
    return v;
#endif
}

// n_hsum_ps: 向量 → 标量和
static inline float n_hsum_ps(n_float v) {
#if defined(NSIMD_AVX2)
    __m128 lo = _mm256_castps256_ps128(v);
    __m128 hi = _mm256_extractf128_ps(v, 1);
    __m128 s = _mm_add_ps(lo, hi);
    s = _mm_add_ps(s, _mm_shuffle_ps(s, s, _MM_SHUFFLE(2, 3, 0, 1)));
    s = _mm_add_ps(s, _mm_shuffle_ps(s, s, _MM_SHUFFLE(1, 0, 3, 2)));
    return _mm_cvtss_f32(s);
#elif defined(NSIMD_SSE4)
    __m128 s = v;
    s = _mm_add_ps(s, _mm_shuffle_ps(s, s, _MM_SHUFFLE(2, 3, 0, 1)));
    s = _mm_add_ps(s, _mm_shuffle_ps(s, s, _MM_SHUFFLE(1, 0, 3, 2)));
    return _mm_cvtss_f32(s);
#else
    return v;
#endif
}

// n_hsum_epi32: 整数向量的水平求和（store + 标量循环，最移植友好）
static inline int n_hsum_epi32(n_int v) {
    int buf[8];
    n_store_epi32(buf, v);
    int s = 0;
    for (int i = 0; i < NSIMD_WIDTH; i++) s += buf[i];
    return s;
}

// n_hmax_epi32: 整数向量的水平最大值
static inline int n_hmax_epi32(n_int v) {
    int buf[8];
    n_store_epi32(buf, v);
    int m = buf[0];
    for (int i = 1; i < NSIMD_WIDTH; i++)
        if (buf[i] > m) m = buf[i];
    return m;
}

// n_hmin_idx: 向量最小值 + 对应索引
static inline int n_hmin_idx(n_float v_val, n_int v_idx) {
    float val[8];
    int idx[8];
    n_store_ps(val, v_val);
    n_store_epi32(idx, v_idx);
    float best_v = val[0];
    int best_i = idx[0];
    for (int i = 1; i < NSIMD_WIDTH; i++) {
        if (val[i] < best_v) {
            best_v = val[i];
            best_i = idx[i];
        }
    }
    return best_i;
}
