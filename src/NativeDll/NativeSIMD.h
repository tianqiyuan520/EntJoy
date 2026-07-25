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
#if defined(NSIMD_AVX2)
    typedef __m256  n_float;
    typedef __m256i n_int;
    typedef __m256  n_mask;
#elif defined(NSIMD_AVX)
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
    // n_mask is uint32x4_t on NEON (comparison/bitwise ops return this type)
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
#if defined(NSIMD_AVX2)
    return _mm256_loadu_ps(p);
#elif defined(NSIMD_AVX)
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
#if defined(NSIMD_AVX2)
    _mm256_storeu_ps(p, v);
#elif defined(NSIMD_AVX)
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
#if defined(NSIMD_AVX2)
    return _mm256_loadu_si256((const __m256i*)p);
#elif defined(NSIMD_AVX)
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
#if defined(NSIMD_AVX2)
    _mm256_storeu_si256((__m256i*)p, v);
#elif defined(NSIMD_AVX)
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
#if defined(NSIMD_AVX2)
    return _mm256_set1_ps(s);
#elif defined(NSIMD_AVX)
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
#if defined(NSIMD_AVX2)
    return _mm256_set1_epi32(s);
#elif defined(NSIMD_AVX)
    __m128i lo = _mm_set1_epi32(s);
    return _mm256_set_m128i(lo, lo);
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
#if defined(NSIMD_AVX2)
    return _mm256_set_epi32(i7, i6, i5, i4, i3, i2, i1, i0);
#elif defined(NSIMD_AVX)
    __m128i lo = _mm_set_epi32(i3, i2, i1, i0);
    __m128i hi = _mm_set_epi32(i7, i6, i5, i4);
    return _mm256_set_m128i(hi, lo);
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
#if defined(NSIMD_AVX2)
    return _mm256_add_ps(a, b);
#elif defined(NSIMD_AVX)
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
#if defined(NSIMD_AVX2)
    return _mm256_sub_ps(a, b);
#elif defined(NSIMD_AVX)
return _mm256_sub_ps(a, b);
#elif defined(NSIMD_SSE4)
    return _mm_sub_ps(a, b);
#elif defined(NSIMD_NEON)
    return vsubq_f32(a, b);
#else
    return a - b;
#endif
}


static inline n_float n_div_ps(n_float a, n_float b) {
#if defined(NSIMD_AVX2)
    return _mm256_div_ps(a, b);
#elif defined(NSIMD_AVX)
    return _mm256_div_ps(a, b);
#elif defined(NSIMD_SSE4)
    return _mm_div_ps(a, b);
#elif defined(NSIMD_NEON)
    return vdivq_f32(a, b);
#else
    return a / b;
#endif
}
static inline n_float n_mul_ps(n_float a, n_float b) {
#if defined(NSIMD_AVX2)
    return _mm256_mul_ps(a, b);
#elif defined(NSIMD_AVX)
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
#elif defined(NSIMD_SSE4)
    return _mm_add_ps(_mm_mul_ps(a, b), c);
#elif defined(NSIMD_NEON)
    return vfmaq_f32(c, a, b);
#else
    return n_add_ps(n_mul_ps(a, b), c);
#endif
}

static inline n_int n_add_epi32(n_int a, n_int b) {
#if defined(NSIMD_AVX2)
    return _mm256_add_epi32(a, b);
#elif defined(NSIMD_AVX)
    __m128i lo_a = _mm256_castsi256_si128(a);
    __m128i lo_b = _mm256_castsi256_si128(b);
    __m128i hi_a = _mm256_extractf128_si256(a, 1);
    __m128i hi_b = _mm256_extractf128_si256(b, 1);
    __m128i lo = _mm_add_epi32(lo_a, lo_b);
    __m128i hi = _mm_add_epi32(hi_a, hi_b);
    return _mm256_set_m128i(hi, lo);
#elif defined(NSIMD_SSE4)
    return _mm_add_epi32(a, b);
#elif defined(NSIMD_NEON)
    return vaddq_s32(a, b);
#else
    return a + b;
#endif
}

static inline n_int n_sub_epi32(n_int a, n_int b) {
#if defined(NSIMD_AVX2)
    return _mm256_sub_epi32(a, b);
#elif defined(NSIMD_AVX)
    __m128i lo_a = _mm256_castsi256_si128(a);
    __m128i lo_b = _mm256_castsi256_si128(b);
    __m128i hi_a = _mm256_extractf128_si256(a, 1);
    __m128i hi_b = _mm256_extractf128_si256(b, 1);
    __m128i lo = _mm_sub_epi32(lo_a, lo_b);
    __m128i hi = _mm_sub_epi32(hi_a, hi_b);
    return _mm256_set_m128i(hi, lo);
#elif defined(NSIMD_SSE4)
    return _mm_sub_epi32(a, b);
#elif defined(NSIMD_NEON)
    return vsubq_s32(a, b);
#else
    return a - b;
#endif
}

static inline n_int n_mullo_epi32(n_int a, n_int b) {
#if defined(NSIMD_AVX2)
    return _mm256_mullo_epi32(a, b);
#elif defined(NSIMD_AVX)
    __m128i lo_a = _mm256_castsi256_si128(a);
    __m128i lo_b = _mm256_castsi256_si128(b);
    __m128i hi_a = _mm256_extractf128_si256(a, 1);
    __m128i hi_b = _mm256_extractf128_si256(b, 1);
    __m128i lo = _mm_mullo_epi32(lo_a, lo_b);
    __m128i hi = _mm_mullo_epi32(hi_a, hi_b);
    return _mm256_set_m128i(hi, lo);
#elif defined(NSIMD_SSE4)
    return _mm_mullo_epi32(a, b);
#elif defined(NSIMD_NEON)
    return vmulq_s32(a, b);
#else
    return a * b;
#endif
}

// ============================================================
// Min / Max
// ============================================================
static inline n_float n_min_ps(n_float a, n_float b) {
#if defined(NSIMD_AVX2)
    return _mm256_min_ps(a, b);
#elif defined(NSIMD_AVX)
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
#if defined(NSIMD_AVX2)
    return _mm256_max_ps(a, b);
#elif defined(NSIMD_AVX)
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
// Absolute value & negation (cross-platform)
// ============================================================
static inline n_float n_fabs_ps(n_float a) {
#if defined(NSIMD_AVX2)
    return _mm256_and_ps(a, _mm256_castsi256_ps(_mm256_set1_epi32(0x7fffffff)));
#elif defined(NSIMD_AVX)
    return _mm256_and_ps(a, _mm256_castsi256_ps(_mm256_set1_epi32(0x7fffffff)));
#elif defined(NSIMD_SSE4)
    return _mm_and_ps(a, _mm_castsi128_ps(_mm_set1_epi32(0x7fffffff)));
#elif defined(NSIMD_NEON)
    return vabsq_f32(a);
#else
    return (a < 0) ? -a : a;
#endif
}

static inline n_float n_neg_ps(n_float a) {
#if defined(NSIMD_AVX2)
    return _mm256_xor_ps(a, _mm256_castsi256_ps(_mm256_set1_epi32(0x80000000)));
#elif defined(NSIMD_AVX)
    return _mm256_xor_ps(a, _mm256_castsi256_ps(_mm256_set1_epi32(0x80000000)));
#elif defined(NSIMD_SSE4)
    return _mm_xor_ps(a, _mm_castsi128_ps(_mm_set1_epi32(0x80000000)));
#elif defined(NSIMD_NEON)
    return vnegq_f32(a);
#else
    return -a;
#endif
}

// ============================================================
// Comparison -> mask
// ============================================================
static inline n_mask n_cmp_lt_ps(n_float a, n_float b) {
#if defined(NSIMD_AVX2)
    return _mm256_cmp_ps(a, b, _CMP_LT_OS);
#elif defined(NSIMD_AVX)
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
#if defined(NSIMD_AVX2)
    return _mm256_cmp_ps(a, b, _CMP_GT_OS);
#elif defined(NSIMD_AVX)
    return _mm256_cmp_ps(a, b, _CMP_GT_OS);
#elif defined(NSIMD_SSE4)
    return _mm_cmpgt_ps(a, b);
#elif defined(NSIMD_NEON)
    return vcgtq_f32(a, b);
#else
    return a > b;
#endif
}

// Float a == b
static inline n_mask n_cmp_eq_ps(n_float a, n_float b) {
#if defined(NSIMD_AVX2)
    return _mm256_cmp_ps(a, b, _CMP_EQ_OS);
#elif defined(NSIMD_AVX)
    return _mm256_cmp_ps(a, b, _CMP_EQ_OS);
#elif defined(NSIMD_SSE4)
    return _mm_cmpeq_ps(a, b);
#elif defined(NSIMD_NEON)
    return vceqq_f32(a, b);
#else
    return a == b;
#endif
}

// Float a != b
static inline n_mask n_cmp_ne_ps(n_float a, n_float b) {
#if defined(NSIMD_AVX2)
    return _mm256_cmp_ps(a, b, _CMP_NEQ_OS);
#elif defined(NSIMD_AVX)
    return _mm256_cmp_ps(a, b, _CMP_NEQ_OS);
#elif defined(NSIMD_SSE4)
    return _mm_cmpneq_ps(a, b);
#elif defined(NSIMD_NEON)
    // NEON vcneq requires aarch64; use ~vceqq for aarch32 compatibility
    return vmvnq_u32(vceqq_f32(a, b));
#else
    return a != b;
#endif
}

// Float a >= b
static inline n_mask n_cmp_ge_ps(n_float a, n_float b) {
#if defined(NSIMD_AVX2)
    return _mm256_cmp_ps(a, b, _CMP_GE_OS);
#elif defined(NSIMD_AVX)
    return _mm256_cmp_ps(a, b, _CMP_GE_OS);
#elif defined(NSIMD_SSE4)
    return _mm_cmpge_ps(a, b);
#elif defined(NSIMD_NEON)
    return vcgeq_f32(a, b);
#else
    return a >= b;
#endif
}

// Float a <= b
static inline n_mask n_cmp_le_ps(n_float a, n_float b) {
#if defined(NSIMD_AVX2)
    return _mm256_cmp_ps(a, b, _CMP_LE_OS);
#elif defined(NSIMD_AVX)
    return _mm256_cmp_ps(a, b, _CMP_LE_OS);
#elif defined(NSIMD_SSE4)
    return _mm_cmple_ps(a, b);
#elif defined(NSIMD_NEON)
    return vcleq_f32(a, b);
#else
    return a <= b;
#endif
}

// ============================================================
// Mask inversion + logic
// ============================================================
static inline n_mask n_not_mask(n_mask m) {
#if defined(NSIMD_AVX2)
    return _mm256_xor_ps(m, _mm256_castsi256_ps(_mm256_set1_epi32(-1)));
#elif defined(NSIMD_AVX)
    __m128i all_ones = _mm_set1_epi32(-1);
    return _mm256_xor_ps(m, _mm256_castsi256_ps(_mm256_set_m128i(all_ones, all_ones)));
#elif defined(NSIMD_SSE4)
    return _mm_xor_ps(m, _mm_castsi128_ps(_mm_set1_epi32(-1)));
#elif defined(NSIMD_NEON)
    return vmvnq_u32(m);
#else
    return !m;
#endif
}

// ============================================================
// Blend: mask ? b : a (cross-platform)
// ============================================================
static inline n_float n_blendv_ps(n_float a, n_float b, n_mask mask) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
    return _mm256_blendv_ps(a, b, mask);
#elif defined(NSIMD_SSE4)
    return _mm_blendv_ps(a, b, mask);
#elif defined(NSIMD_NEON)
    return vbslq_f32(mask, b, a);
#else
    return (mask != 0) ? b : a;
#endif
}

// ============================================================
// Integer comparison -> mask (for Full-Width SIMD control flow)
// ============================================================

// Signed int a < b
static inline n_mask n_cmp_lt_epi32(n_int a, n_int b) {
#if defined(NSIMD_AVX2)
    return _mm256_castsi256_ps(_mm256_cmpgt_epi32(b, a));  // a < b  →  b > a
#elif defined(NSIMD_AVX)
    __m128i lo_a = _mm256_castsi256_si128(a);
    __m128i lo_b = _mm256_castsi256_si128(b);
    __m128i hi_a = _mm256_extractf128_si256(a, 1);
    __m128i hi_b = _mm256_extractf128_si256(b, 1);
    __m128i lo = _mm_castps_si128(_mm_cmplt_ps(_mm_castsi128_ps(lo_a), _mm_castsi128_ps(lo_b)));
    __m128i hi = _mm_castps_si128(_mm_cmplt_ps(_mm_castsi128_ps(hi_a), _mm_castsi128_ps(hi_b)));
    return _mm256_castsi256_ps(_mm256_set_m128i(hi, lo));
#elif defined(NSIMD_SSE4)
    return _mm_castsi128_ps(_mm_cmpgt_epi32(b, a));
#elif defined(NSIMD_NEON)
    return vcltq_s32(a, b);
#else
    return a < b;
#endif
}

// Signed int a > b
static inline n_mask n_cmp_gt_epi32(n_int a, n_int b) {
#if defined(NSIMD_AVX2)
    return _mm256_castsi256_ps(_mm256_cmpgt_epi32(a, b));
#elif defined(NSIMD_AVX)
    __m128i lo_a = _mm256_castsi256_si128(a);
    __m128i lo_b = _mm256_castsi256_si128(b);
    __m128i hi_a = _mm256_extractf128_si256(a, 1);
    __m128i hi_b = _mm256_extractf128_si256(b, 1);
    __m128i lo = _mm_castps_si128(_mm_cmpgt_ps(_mm_castsi128_ps(lo_a), _mm_castsi128_ps(lo_b)));
    __m128i hi = _mm_castps_si128(_mm_cmpgt_ps(_mm_castsi128_ps(hi_a), _mm_castsi128_ps(hi_b)));
    return _mm256_castsi256_ps(_mm256_set_m128i(hi, lo));
#elif defined(NSIMD_SSE4)
    return _mm_castsi128_ps(_mm_cmpgt_epi32(a, b));
#elif defined(NSIMD_NEON)
    return vcgtq_s32(a, b);
#else
    return a > b;
#endif
}

// Unsigned int a < b
static inline n_mask n_cmp_ult_epi32(n_int a, n_int b) {
#if defined(NSIMD_AVX2)
    n_int sign = _mm256_set1_epi32(0x80000000);
    return _mm256_castsi256_ps(_mm256_cmpgt_epi32(_mm256_xor_si256(b, sign), _mm256_xor_si256(a, sign)));
#elif defined(NSIMD_AVX)
    n_int sign = _mm256_set1_epi32(0x80000000);
    // Use _mm256_xor_ps (AVX) instead of _mm256_xor_si256 (AVX2)
    n_int bx = _mm256_castps_si256(_mm256_xor_ps(_mm256_castsi256_ps(b), _mm256_castsi256_ps(sign)));
    n_int ax = _mm256_castps_si256(_mm256_xor_ps(_mm256_castsi256_ps(a), _mm256_castsi256_ps(sign)));
    __m128i lo_b = _mm256_castsi256_si128(bx);
    __m128i lo_a = _mm256_castsi256_si128(ax);
    __m128i hi_b = _mm256_extractf128_si256(bx, 1);
    __m128i hi_a = _mm256_extractf128_si256(ax, 1);
    __m128i lo = _mm_castps_si128(_mm_cmpgt_ps(_mm_castsi128_ps(lo_b), _mm_castsi128_ps(lo_a)));
    __m128i hi = _mm_castps_si128(_mm_cmpgt_ps(_mm_castsi128_ps(hi_b), _mm_castsi128_ps(hi_a)));
    return _mm256_castsi256_ps(_mm256_set_m128i(hi, lo));
#elif defined(NSIMD_SSE4)
    n_int sign = _mm_set1_epi32(0x80000000);
    return _mm_castsi128_ps(_mm_cmpgt_epi32(_mm_xor_si128(b, sign), _mm_xor_si128(a, sign)));
#elif defined(NSIMD_NEON)
    return vcltq_u32(a, b);
#else
    return (unsigned int)a < (unsigned int)b;
#endif
}

// Signed int a == b
static inline n_mask n_cmp_eq_epi32(n_int a, n_int b) {
#if defined(NSIMD_AVX2)
    return _mm256_castsi256_ps(_mm256_cmpeq_epi32(a, b));
#elif defined(NSIMD_AVX)
    __m128i lo_a = _mm256_castsi256_si128(a);
    __m128i lo_b = _mm256_castsi256_si128(b);
    __m128i hi_a = _mm256_extractf128_si256(a, 1);
    __m128i hi_b = _mm256_extractf128_si256(b, 1);
    __m128i lo = _mm_cmpeq_epi32(lo_a, lo_b);
    __m128i hi = _mm_cmpeq_epi32(hi_a, hi_b);
    return _mm256_castsi256_ps(_mm256_set_m128i(hi, lo));
#elif defined(NSIMD_SSE4)
    return _mm_castsi128_ps(_mm_cmpeq_epi32(a, b));
#elif defined(NSIMD_NEON)
    return vceqq_s32(a, b);
#else
    return a == b;
#endif
}

// Signed int a != b
static inline n_mask n_cmp_ne_epi32(n_int a, n_int b) {
    return n_not_mask(n_cmp_eq_epi32(a, b));
}

// Signed int a >= b
static inline n_mask n_cmp_ge_epi32(n_int a, n_int b) {
    return n_not_mask(n_cmp_lt_epi32(a, b));
}

// Signed int a <= b
static inline n_mask n_cmp_le_epi32(n_int a, n_int b) {
    return n_not_mask(n_cmp_gt_epi32(a, b));
}

// ============================================================
// Integer min / max
// ============================================================
static inline n_int n_min_epi32(n_int a, n_int b) {
#if defined(NSIMD_AVX2)
    return _mm256_min_epi32(a, b);
#elif defined(NSIMD_AVX)
    __m128i lo_a = _mm256_castsi256_si128(a);
    __m128i lo_b = _mm256_castsi256_si128(b);
    __m128i hi_a = _mm256_extractf128_si256(a, 1);
    __m128i hi_b = _mm256_extractf128_si256(b, 1);
    __m128i lo = _mm_min_epi32(lo_a, lo_b);
    __m128i hi = _mm_min_epi32(hi_a, hi_b);
    return _mm256_set_m128i(hi, lo);
#elif defined(NSIMD_SSE4)
    return _mm_min_epi32(a, b);
#elif defined(NSIMD_NEON)
    return vminq_s32(a, b);
#else
    return (a < b) ? a : b;
#endif
}

static inline n_int n_max_epi32(n_int a, n_int b) {
#if defined(NSIMD_AVX2)
    return _mm256_max_epi32(a, b);
#elif defined(NSIMD_AVX)
    __m128i lo_a = _mm256_castsi256_si128(a);
    __m128i lo_b = _mm256_castsi256_si128(b);
    __m128i hi_a = _mm256_extractf128_si256(a, 1);
    __m128i hi_b = _mm256_extractf128_si256(b, 1);
    __m128i lo = _mm_max_epi32(lo_a, lo_b);
    __m128i hi = _mm_max_epi32(hi_a, hi_b);
    return _mm256_set_m128i(hi, lo);
#elif defined(NSIMD_SSE4)
    return _mm_max_epi32(a, b);
#elif defined(NSIMD_NEON)
    return vmaxq_s32(a, b);
#else
    return (a > b) ? a : b;
#endif
}

// ============================================================
// Float floor (Full-Width SIMD cell computation)
// ============================================================
static inline n_float n_floor_ps(n_float a) {
#if defined(NSIMD_AVX2)
    return _mm256_floor_ps(a);
#elif defined(NSIMD_AVX)
    return _mm256_floor_ps(a);
#elif defined(NSIMD_SSE4)
    return _mm_floor_ps(a);
#elif defined(NSIMD_NEON)
    // H1: vreinterpretq_f32_u32(0xFFFFFFFF) = NaN! Use vbslq_f32 instead.
    // floor(x) = trunc - 1  for negative non-integers,  trunc  otherwise
    float32x4_t trunc = vcvtq_f32_s32(vcvtq_s32_f32(a));
    float32x4_t frac = vsubq_f32(a, trunc);
    uint32x4_t neg = vcltq_f32(frac, vdupq_n_f32(0.0f));
    float32x4_t fix = vbslq_f32(neg, vdupq_n_f32(1.0f), vdupq_n_f32(0.0f));
    return vsubq_f32(trunc, fix);
#else
    return floorf(a);
#endif
}

// ============================================================
// Float sqrt
// ============================================================
static inline n_float n_sqrt_ps(n_float a) {
#if defined(NSIMD_AVX2)
    return _mm256_sqrt_ps(a);
#elif defined(NSIMD_AVX)
    return _mm256_sqrt_ps(a);
#elif defined(NSIMD_SSE4)
    return _mm_sqrt_ps(a);
#elif defined(NSIMD_NEON)
    return vsqrtq_f32(a);
#else
    return sqrtf(a);
#endif
}

// ============================================================
// Float ceil / round / trunc (lightweight, native instructions)
// ============================================================

static inline n_float n_ceil_ps(n_float a) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
    return _mm256_ceil_ps(a);
#elif defined(NSIMD_SSE4)
    return _mm_ceil_ps(a);
#elif defined(NSIMD_NEON)
    // vrndpq_f32 = round toward +inf (ceil), available on AArch32/AArch64
    return vrndpq_f32(a);
#else
    return ceilf(a);
#endif
}

static inline n_float n_round_ps(n_float a) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
    return _mm256_round_ps(a, _MM_FROUND_TO_NEAREST_INT | _MM_FROUND_NO_EXC);
#elif defined(NSIMD_SSE4)
    return _mm_round_ps(a, _MM_FROUND_TO_NEAREST_INT | _MM_FROUND_NO_EXC);
#elif defined(NSIMD_NEON)
    // vrndaq_f32 = round to nearest (ties to even)
    return vrndaq_f32(a);
#else
    return roundf(a);
#endif
}

static inline n_float n_trunc_ps(n_float a) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
    return _mm256_round_ps(a, _MM_FROUND_TO_ZERO | _MM_FROUND_NO_EXC);
#elif defined(NSIMD_SSE4)
    return _mm_round_ps(a, _MM_FROUND_TO_ZERO | _MM_FROUND_NO_EXC);
#elif defined(NSIMD_NEON)
    // vrndq_f32 = round toward zero (trunc)
    return vrndq_f32(a);
#else
    return truncf(a);
#endif
}

// ============================================================
// Float -> int truncation (toward zero)
// ============================================================
static inline n_float n_cvtepi32_ps(n_int a) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
    return _mm256_cvtepi32_ps(a);
#elif defined(NSIMD_SSE4)
    return _mm_cvtepi32_ps(a);
#elif defined(NSIMD_NEON)
    return vcvtq_f32_s32(a);
#else
    return (float)a;
#endif
}

static inline n_int n_cvttps_epi32(n_float a) {
#if defined(NSIMD_AVX2)
    return _mm256_cvttps_epi32(a);
#elif defined(NSIMD_AVX)
    return _mm256_cvttps_epi32(a);
#elif defined(NSIMD_SSE4)
    return _mm_cvttps_epi32(a);
#elif defined(NSIMD_NEON)
    return vcvtq_s32_f32(a);
#else
    return (int)a;
#endif
}

// ============================================================
// Mask logic
// ============================================================
static inline n_mask n_and_mask(n_mask a, n_mask b) {
#if defined(NSIMD_AVX2)
    return _mm256_and_ps(a, b);
#elif defined(NSIMD_AVX)
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
#if defined(NSIMD_AVX2)
    return _mm256_andnot_ps(a, b);
#elif defined(NSIMD_AVX)
    return _mm256_andnot_ps(a, b);
#elif defined(NSIMD_SSE4)
    return _mm_andnot_ps(a, b);
#elif defined(NSIMD_NEON)
    // H2: vbicq_u32(a,b) = a & ~b, but andnotps(a,b) = ~a & b. Swap operands.
    return vbicq_u32(b, a);
#else
    return !a && b;
#endif
}

// Mask OR
static inline n_mask n_or_mask(n_mask a, n_mask b) {
#if defined(NSIMD_AVX2)
    return _mm256_or_ps(a, b);
#elif defined(NSIMD_AVX)
    return _mm256_or_ps(a, b);
#elif defined(NSIMD_SSE4)
    return _mm_or_ps(a, b);
#elif defined(NSIMD_NEON)
    return vorrq_u32(a, b);
#else
    return a || b;
#endif
}

// Mask XOR
static inline n_mask n_xor_mask(n_mask a, n_mask b) {
#if defined(NSIMD_AVX2)
    return _mm256_xor_ps(a, b);
#elif defined(NSIMD_AVX)
    return _mm256_xor_ps(a, b);
#elif defined(NSIMD_SSE4)
    return _mm_xor_ps(a, b);
#elif defined(NSIMD_NEON)
    return veorq_u32(a, b);
#else
    return a != b;
#endif
}

// ============================================================
// Blend (conditional select)
// ============================================================
static inline n_float n_blend_ps(n_float v_false, n_float v_true, n_mask mask) {
#if defined(NSIMD_AVX2)
    return _mm256_blendv_ps(v_false, v_true, mask);
#elif defined(NSIMD_AVX)
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
#if defined(NSIMD_AVX2)
    return _mm256_castps_si256(
        _mm256_blendv_ps(_mm256_castsi256_ps(v_false), _mm256_castsi256_ps(v_true), mask));
#elif defined(NSIMD_AVX)
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

template<int stride = 4>
static inline n_int n_gather_epi32(const int* base, n_int indices) {
#if defined(NSIMD_AVX2)
    return _mm256_i32gather_epi32(base, indices, stride);
#else
    int w = NSIMD_WIDTH;
    int idx[8];
    int val[8];
    n_store_epi32(idx, indices);
    const char* cb = (const char*)base;
    for (int i = 0; i < w; i++)
        val[i] = *(const int*)(cb + idx[i] * stride);
    return n_load_epi32(val);
#endif
}

// ============================================================
// Masked gather (ISPC-style: hardware skips inactive lanes)
// ============================================================
template<int stride>
static inline n_float n_gather_masked_ps(const float* base, n_int indices, n_mask mask) {
#if defined(NSIMD_AVX2)
    // AVX2: _mm256_mask_i32gather_ps takes __m256 mask (n_mask = __m256), pass directly
    n_float src = _mm256_setzero_ps();
    return _mm256_mask_i32gather_ps(src, base, indices, mask, stride);
#elif defined(NSIMD_NEON)
    // NEON: mask is uint32x4_t, store as uint32 to check per lane
    int w = NSIMD_WIDTH;
    int idx[8];
    float val[8];
    uint32_t maskBits[8];
    n_store_epi32(idx, indices);
    vst1q_u32(maskBits, mask);
    const char* cb = (const char*)base;
    for (int i = 0; i < w; i++)
        val[i] = maskBits[i] ? *(const float*)(cb + idx[i] * stride) : 0.0f;
    return n_load_ps(val);
#else
    // SSE4/scalar fallback: mask is float type, store as float bits
    int w = NSIMD_WIDTH;
    int idx[8];
    float val[8];
    float maskBits[8];
    n_store_epi32(idx, indices);
    n_store_ps(maskBits, mask);
    const char* cb = (const char*)base;
    for (int i = 0; i < w; i++)
        val[i] = (*(uint32_t*)&maskBits[i] != 0) ? *(const float*)(cb + idx[i] * stride) : 0.0f;
    return n_load_ps(val);
#endif
}

template<int stride = 8>
static inline n_int n_gather_masked_epi32(const int* base, n_int indices, n_mask mask) {
#if defined(NSIMD_AVX2)
    n_int src = _mm256_setzero_si256();
    return _mm256_mask_i32gather_epi32(src, base, indices, _mm256_castps_si256(mask), stride);
#elif defined(NSIMD_NEON)
    int w = NSIMD_WIDTH;
    int idx[8];
    int val[8];
    uint32_t maskBits[8];
    n_store_epi32(idx, indices);
    vst1q_u32(maskBits, mask);
    const char* cb = (const char*)base;
    for (int i = 0; i < w; i++)
        val[i] = maskBits[i] ? *(const int*)(cb + idx[i] * stride) : 0;
    return n_load_epi32(val);
#else
    int w = NSIMD_WIDTH;
    int idx[8];
    int val[8];
    float maskBits[8];
    n_store_epi32(idx, indices);
    n_store_ps(maskBits, mask);
    const char* cb = (const char*)base;
    for (int i = 0; i < w; i++)
        val[i] = (*(uint32_t*)&maskBits[i] != 0) ? *(const int*)(cb + idx[i] * stride) : 0;
    return n_load_epi32(val);
#endif
}

// ============================================================
// Lane extraction (SIMD register -> scalar)
// ============================================================
static inline float n_extract_lane_f32(n_float v, int lane) {
    // M2: clamp lane to valid range to prevent buffer overrun
    lane = lane & (NSIMD_WIDTH - 1);
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
    // AVX2: extract the right 128-bit half then store+index
    // (store approach avoids _MM_SHUFFLE's compile-time constant requirement)
    __m128 lo = _mm256_castps256_ps128(v);
    __m128 hi = _mm256_extractf128_ps(v, 1);
    __m128 sel = lane < 4 ? lo : hi;
    int idx = lane & 3;
    alignas(16) float buf[4];
    _mm_store_ps(buf, sel);
    return buf[idx];
#elif defined(NSIMD_SSE4)
    alignas(16) float buf[4];
    _mm_store_ps(buf, v);
    return buf[lane];
#elif defined(NSIMD_NEON)
    return vgetq_lane_f32(v, lane);
#else
    (void)lane;
    return v;
#endif
}

static inline int n_extract_lane_epi32(n_int v, int lane) {
    // M2: clamp lane to valid range to prevent buffer overrun
    lane = lane & (NSIMD_WIDTH - 1);
#if defined(NSIMD_AVX2)
    __m128i lo = _mm256_castsi256_si128(v);
    __m128i hi = _mm256_extractf128_si256(v, 1);
    __m128i sel = lane < 4 ? lo : hi;
    int idx = lane & 3;
    alignas(16) int buf[4];
    _mm_store_si128((__m128i*)buf, sel);
    return buf[idx];
#elif defined(NSIMD_AVX)
    alignas(32) int buf[8];
    _mm256_storeu_si256((__m256i*)buf, v);
    return buf[lane];
#elif defined(NSIMD_SSE4)
    alignas(16) int buf[4];
    _mm_store_si128((__m128i*)buf, v);
    return buf[lane];
#elif defined(NSIMD_NEON)
    return vgetq_lane_s32(v, lane);
#else
    (void)lane;
    return v;
#endif
}

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

static inline int n_any_true(n_mask mask) { return !n_all_zero(mask); }

// ============================================================
// Mask → integer bitmask (converts SIMD mask to per-lane bits)
// Used for per-lane scatter guards: each bit = 1 means lane is active
// ============================================================
static inline int n_mask_to_bitmask(n_mask mask) {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
    return _mm256_movemask_ps(mask);
#elif defined(NSIMD_SSE4)
    return _mm_movemask_ps(mask);
#elif defined(NSIMD_NEON)
    // NEON: extract each lane's sign bit (MSB of float32)
    uint32x4_t u = vreinterpretq_u32_f32(mask);
    uint32_t tmp[4];
    vst1q_u32(tmp, u);
    return (int)((tmp[0] >> 31)) | ((int)(tmp[1] >> 31) << 1)
         | ((int)(tmp[2] >> 31) << 2) | ((int)(tmp[3] >> 31) << 3);
#else
    return mask ? 1 : 0;
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

static inline int n_hmin_epi32(n_int v) {
    int buf[8];
    n_store_epi32(buf, v);
    int m = buf[0];
    for (int i = 1; i < NSIMD_WIDTH; i++)
        if (buf[i] < m) m = buf[i];
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
