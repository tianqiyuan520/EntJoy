// NativeSIMD.h - cross-platform SIMD abstraction layer
// Provides AVX2 / SSE4 / NEON / scalar fallback via unified interface
// All loads use unaligned access (loadu), no alignment guarantees needed
#pragma once
#include <cmath>
#include <cstring>
#include <algorithm>
#include <limits>

// ============================================================
// Platform detection (priority: x86 > ARM > scalar)
// ============================================================
#if defined(__AVX2__)
    #define NSIMD_AVX2 1
    #define NSIMD_WIDTH 8
    #include <immintrin.h>
#elif defined(__AVX__)
    #define NSIMD_AVX 1
    #define NSIMD_WIDTH 8
    #include <immintrin.h>
#elif defined(__SSE4_2__) || defined(__SSE4_1__) || defined(__SSE__) || defined(_M_IX86_FP) || defined(_M_X64)
    #define NSIMD_SSE4 1
    #define NSIMD_WIDTH 4
    #include <smmintrin.h>
#elif defined(__ARM_NEON) || defined(__aarch64__) || defined(_M_ARM64)
    #define NSIMD_NEON 1
    #define NSIMD_WIDTH 4
    #include <arm_neon.h>
#else
    #define NSIMD_SCALAR 1
    #define NSIMD_WIDTH 1
#endif

// ============================================================
// Type definitions
// ============================================================
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
    typedef __m256  n_float;
    typedef __m256i n_int;
    typedef __m256  n_mask;
#elif defined(NSIMD_SSE4)
    typedef __m128  n_float;
    typedef __m128i n_int;
    typedef __m128  n_mask;
#elif defined(NSIMD_NEON)
    typedef float32x4_t n_float;
    typedef int32x4_t   n_int;
    typedef uint32x4_t  n_mask;
#else
    typedef float  n_float;
    typedef int    n_int;
    typedef bool   n_mask;
#endif

// ============================================================
// Load / Store (all unaligned)
// ============================================================
static inline n_float n_load_ps(const float* p) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
    return _mm256_loadu_ps(p);
#elif defined(NSIMD_SSE4)
    return _mm_loadu_ps(p);
#elif defined(NSIMD_NEON)
    return vld1q_f32(p);
#else
    return *p;
#endif
}

static inline void n_store_ps(float* p, n_float v) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
    _mm256_storeu_ps(p, v);
#elif defined(NSIMD_SSE4)
    _mm_storeu_ps(p, v);
#elif defined(NSIMD_NEON)
    vst1q_f32(p, v);
#else
    *p = v;
#endif
}

static inline n_int n_load_epi32(const int* p) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
    return _mm256_loadu_si256((const __m256i*)p);
#elif defined(NSIMD_SSE4)
    return _mm_loadu_si128((const __m128i*)p);
#elif defined(NSIMD_NEON)
    return vld1q_s32(p);
#else
    return *p;
#endif
}

static inline void n_store_epi32(int* p, n_int v) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
    _mm256_storeu_si256((__m256i*)p, v);
#elif defined(NSIMD_SSE4)
    _mm_storeu_si128((__m128i*)p, v);
#elif defined(NSIMD_NEON)
    vst1q_s32(p, v);
#else
    *p = v;
#endif
}

// ============================================================
// Broadcast (scalar -> vector)
// ============================================================
static inline n_float n_set1_ps(float s) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
    return _mm256_set1_ps(s);
#elif defined(NSIMD_SSE4)
    return _mm_set1_ps(s);
#elif defined(NSIMD_NEON)
    return vdupq_n_f32(s);
#else
    return s;
#endif
}

static inline n_int n_set1_epi32(int s) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
    return _mm256_set1_epi32(s);
#elif defined(NSIMD_SSE4)
    return _mm_set1_epi32(s);
#elif defined(NSIMD_NEON)
    return vdupq_n_s32(s);
#else
    return s;
#endif
}

static inline n_int n_set_epi32(int i7, int i6, int i5, int i4,
                                int i3, int i2, int i1, int i0) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
    return _mm256_set_epi32(i7, i6, i5, i4, i3, i2, i1, i0);
#elif defined(NSIMD_SSE4) || defined(NSIMD_NEON)
    (void)i7; (void)i6; (void)i5; (void)i4;
#if defined(NSIMD_SSE4)
    return _mm_set_epi32(i3, i2, i1, i0);
#else
    int32_t data[4] = { i0, i1, i2, i3 };
    return vld1q_s32(data);
#endif
#else
    (void)i7; (void)i6; (void)i5; (void)i4;
    (void)i3; (void)i2;
    return i1 ? i0 : i0;
#endif
}

// ============================================================
// Arithmetic
// ============================================================
static inline n_float n_add_ps(n_float a, n_float b) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
    return _mm256_add_ps(a, b);
#elif defined(NSIMD_SSE4)
    return _mm_add_ps(a, b);
#elif defined(NSIMD_NEON)
    return vaddq_f32(a, b);
#else
    return a + b;
#endif
}

static inline n_float n_sub_ps(n_float a, n_float b) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
    return _mm256_sub_ps(a, b);
#elif defined(NSIMD_SSE4)
    return _mm_sub_ps(a, b);
#elif defined(NSIMD_NEON)
    return vsubq_f32(a, b);
#else
    return a - b;
#endif
}

static inline n_float n_mul_ps(n_float a, n_float b) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
    return _mm256_mul_ps(a, b);
#elif defined(NSIMD_SSE4)
    return _mm_mul_ps(a, b);
#elif defined(NSIMD_NEON)
    return vmulq_f32(a, b);
#else
    return a * b;
#endif
}

static inline n_float n_fmadd_ps(n_float a, n_float b, n_float c) {
#if defined(NSIMD_AVX2)
    return _mm256_fmadd_ps(a, b, c);
#elif defined(NSIMD_AVX) && defined(__FMA__)
    return _mm256_fmadd_ps(a, b, c);
#elif defined(NSIMD_SSE4) && defined(__FMA__)
    return _mm_fmadd_ps(a, b, c);
#elif defined(NSIMD_NEON)
    return vfmaq_f32(c, a, b);
#else
    return a * b + c;
#endif
}

static inline n_int n_add_epi32(n_int a, n_int b) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
    return _mm256_add_epi32(a, b);
#elif defined(NSIMD_SSE4)
    return _mm_add_epi32(a, b);
#elif defined(NSIMD_NEON)
    return vaddq_s32(a, b);
#else
    return a + b;
#endif
}

// ============================================================
// Min / Max
// ============================================================
static inline n_float n_min_ps(n_float a, n_float b) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
    return _mm256_min_ps(a, b);
#elif defined(NSIMD_SSE4)
    return _mm_min_ps(a, b);
#elif defined(NSIMD_NEON)
    return vminq_f32(a, b);
#else
    return (a < b) ? a : b;
#endif
}

static inline n_float n_max_ps(n_float a, n_float b) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
    return _mm256_max_ps(a, b);
#elif defined(NSIMD_SSE4)
    return _mm_max_ps(a, b);
#elif defined(NSIMD_NEON)
    return vmaxq_f32(a, b);
#else
    return (a > b) ? a : b;
#endif
}

// ============================================================
// Comparison -> mask
// ============================================================
static inline n_mask n_cmp_lt_ps(n_float a, n_float b) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
    return _mm256_cmp_ps(a, b, _CMP_LT_OS);
#elif defined(NSIMD_SSE4)
    return _mm_cmplt_ps(a, b);
#elif defined(NSIMD_NEON)
    return vcltq_f32(a, b);
#else
    return a < b;
#endif
}

static inline n_mask n_cmp_gt_ps(n_float a, n_float b) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
    return _mm256_cmp_ps(a, b, _CMP_GT_OS);
#elif defined(NSIMD_SSE4)
    return _mm_cmpgt_ps(a, b);
#elif defined(NSIMD_NEON)
    return vcgtq_f32(a, b);
#else
    return a > b;
#endif
}

// ============================================================
// Mask logic
// ============================================================
static inline n_mask n_and_mask(n_mask a, n_mask b) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
    return _mm256_and_ps(a, b);
#elif defined(NSIMD_SSE4)
    return _mm_and_ps(a, b);
#elif defined(NSIMD_NEON)
    return vandq_u32(a, b);
#else
    return a && b;
#endif
}

static inline n_mask n_andnot_mask(n_mask a, n_mask b) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
    return _mm256_andnot_ps(a, b);
#elif defined(NSIMD_SSE4)
    return _mm_andnot_ps(a, b);
#elif defined(NSIMD_NEON)
    return vbicq_u32(a, b);
#else
    return !a && b;
#endif
}

// ============================================================
// Blend (conditional select)
// ============================================================
static inline n_float n_blend_ps(n_float v_false, n_float v_true, n_mask mask) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
    return _mm256_blendv_ps(v_false, v_true, mask);
#elif defined(NSIMD_SSE4)
    return _mm_blendv_ps(v_false, v_true, mask);
#elif defined(NSIMD_NEON)
    return vbslq_f32(mask, v_true, v_false);
#else
    return mask ? v_true : v_false;
#endif
}

static inline n_int n_blend_epi32(n_int v_false, n_int v_true, n_mask mask) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
    return _mm256_castps_si256(
        _mm256_blendv_ps(_mm256_castsi256_ps(v_false), _mm256_castsi256_ps(v_true), mask));
#elif defined(NSIMD_SSE4)
    return _mm_castps_si128(
        _mm_blendv_ps(_mm_castsi128_ps(v_false), _mm_castsi128_ps(v_true), mask));
#elif defined(NSIMD_NEON)
    return vbslq_s32(mask, v_true, v_false);
#else
    return mask ? v_true : v_false;
#endif
}

// ============================================================
// Gather (AoS stride gather)
// ============================================================
template<int stride>
static inline n_float n_gather_ps(const float* base, n_int indices) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
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
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
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
// All-zero test
// ============================================================
static inline int n_all_zero(n_mask mask) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
    return _mm256_testz_ps(mask, mask);
#elif defined(NSIMD_SSE4)
    return _mm_testz_ps(mask, mask);
#elif defined(NSIMD_NEON)
    uint32x2_t lo = vget_low_u32(mask);
    uint32x2_t hi = vget_high_u32(mask);
    uint32x2_t orr = vorr_u32(lo, hi);
    return (vget_lane_u32(orr, 0) == 0 && vget_lane_u32(orr, 1) == 0) ? 1 : 0;
#else
    return mask ? 0 : 1;
#endif
}

// ============================================================
// Horizontal reduction
// ============================================================

static inline float n_hmin_ps(n_float v) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
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
#elif defined(NSIMD_NEON)
    float32x2_t lo = vget_low_f32(v);
    float32x2_t hi = vget_high_f32(v);
    float32x2_t m = vpmin_f32(lo, hi);
    m = vpmin_f32(m, m);
    return vget_lane_f32(m, 0);
#else
    return v;
#endif
}

static inline float n_hmax_ps(n_float v) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
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
#elif defined(NSIMD_NEON)
    float32x2_t lo = vget_low_f32(v);
    float32x2_t hi = vget_high_f32(v);
    float32x2_t m = vpmax_f32(lo, hi);
    m = vpmax_f32(m, m);
    return vget_lane_f32(m, 0);
#else
    return v;
#endif
}

static inline float n_hsum_ps(n_float v) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
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
#elif defined(NSIMD_NEON)
    float32x2_t lo = vget_low_f32(v);
    float32x2_t hi = vget_high_f32(v);
    float32x2_t s = vpadd_f32(lo, hi);
    s = vpadd_f32(s, s);
    return vget_lane_f32(s, 0);
#else
    return v;
#endif
}

// Integer horizontal reduction (store+scalar, most portable)
static inline int n_hsum_epi32(n_int v) {
    int buf[8];
    n_store_epi32(buf, v);
    int s = 0;
    for (int i = 0; i < NSIMD_WIDTH; i++) s += buf[i];
    return s;
}

static inline int n_hmax_epi32(n_int v) {
    int buf[8];
    n_store_epi32(buf, v);
    int m = buf[0];
    for (int i = 1; i < NSIMD_WIDTH; i++)
        if (buf[i] > m) m = buf[i];
    return m;
}

// n_hmin_idx: vector min + corresponding index from v_idx
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
