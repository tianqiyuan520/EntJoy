// NativeSIMD_math.h — SIMD vector math (inline AVX2 + per-lane scalar fallbacks)
// Precision level controlled by -DSIMD_MATH_PRECISION=1|2|3
//   1 = Fastest (~3.5 ULP, game physics/animation quality)
//   2 = High   (~1.0 ULP)
//   3 = IEEE   (exact scalar sinf/cosf/..., fallback)
#pragma once
#include "NativeSIMD.h"

// ================================================================
// Inline AVX2 polynomial implementations (SLEEF coefficients, zero call overhead)
// Other platforms fall through to per-lane scalar fallbacks below.
// ================================================================
#if defined(NSIMD_AVX2)

// Constants from sleef/src/libm/sleefsimdsp.c: PI_A2/PI_B2/PI_C2 = π/2 in 3-parts
#define _N_PIO2_A 1.57079637e+00f
#define _N_PIO2_B -4.37113883e-08f

static inline n_float _n_sin_avx2(n_float d) {
  // Range reduction: q = round(d * 2/π), r = d - q * π/2  (same as SLEEF xsinf)
  n_float qf = n_round_ps(_mm256_mul_ps(d, _mm256_set1_ps(0.636619772f))); // 2/π
  n_int qi = _mm256_cvtps_epi32(qf);
  d = _mm256_sub_ps(d, _mm256_mul_ps(qf, _mm256_set1_ps(_N_PIO2_A)));
  d = _mm256_sub_ps(d, _mm256_mul_ps(qf, _mm256_set1_ps(_N_PIO2_B)));

  // Sign inversion: if (qi & 1) d = -d
  n_int odd = _mm256_and_si256(qi, _mm256_set1_epi32(1));
  n_int sign = _mm256_slli_epi32(odd, 31);
  d = _mm256_xor_ps(d, _mm256_castsi256_ps(sign));

  // SLEEF xsinf polynomial: sin(x) ≈ x + x³·c1 + x⁵·c2 + x⁷·c3 + x⁹·c4
  // Coefficients from sleefsimdsp.c lines 470-473 (Remez, ~3.5 ULP)
  n_float s = _mm256_mul_ps(d, d);
  n_float u = _mm256_set1_ps(2.6083159809786593541503e-06f);
  u = _mm256_fmadd_ps(u, s, _mm256_set1_ps(-0.0001981069071916863322258f));
  u = _mm256_fmadd_ps(u, s, _mm256_set1_ps(0.00833307858556509017944336f));
  u = _mm256_fmadd_ps(u, s, _mm256_set1_ps(-0.166666597127914428710938f));
  return _mm256_add_ps(d, _mm256_mul_ps(_mm256_mul_ps(s, u), d));
}

static inline n_float _n_cos_avx2(n_float d) {
  // cos(x) = sin(x + π/2) — reuse sin with phase shift
  return _n_sin_avx2(_mm256_add_ps(d, _mm256_set1_ps(1.57079633f)));
}

static inline n_float _n_log_avx2(n_float d) {
  // Based on SLEEF xlogf (sleefsimdsp.c lines 1277-1312):
  // 1. Extract exponent e from IEEE-754 representation
  n_int emm0 = _mm256_srli_epi32(_mm256_castps_si256(d), 23);
  n_int e = _mm256_sub_epi32(emm0, _mm256_set1_epi32(127));
  // 2. Normalize mantissa m to [0.75, 1.5) (SLEEF: d * (1/0.75) ilogbk)
  n_int mant = _mm256_and_si256(_mm256_castps_si256(d), _mm256_set1_epi32(0x807fffff));
  mant = _mm256_or_si256(mant, _mm256_set1_epi32(0x3f000000));  // exponent bias = 127 → [1, 2)
  n_float m = _mm256_castsi256_ps(mant);
  // Scale to [0.75, 1.5) like SLEEF
  n_float m_scaled = _mm256_div_ps(m, _mm256_set1_ps(0.75f));
  // Re-extract exponent after scaling (SLEEF ilogbk approach)
  n_int e2 = _mm256_sub_epi32(_mm256_srli_epi32(_mm256_castps_si256(m_scaled), 23), _mm256_set1_epi32(127));
  e = _mm256_add_epi32(e, e2);
  // Re-normalize mantissa
  n_int adjusted = _mm256_and_si256(_mm256_castps_si256(m_scaled), _mm256_set1_epi32(0x807fffff));
  adjusted = _mm256_or_si256(adjusted, _mm256_set1_epi32(0x3f000000));
  m = _mm256_castsi256_ps(adjusted);

  // 3. x = (m - 1) / (m + 1), x2 = x^2
  n_float x = _mm256_div_ps(_mm256_sub_ps(m, _mm256_set1_ps(1.0f)),
                             _mm256_add_ps(m, _mm256_set1_ps(1.0f)));
  n_float x2 = _mm256_mul_ps(x, x);

  // 4. Polynomial in x2 (SLEEF xlogf coefficients, lines 1295-1299)
  n_float t = _mm256_set1_ps(0.2392828464508056640625f);    // c4
  t = _mm256_fmadd_ps(t, x2, _mm256_set1_ps(0.28518211841583251953125f));  // + c3
  t = _mm256_fmadd_ps(t, x2, _mm256_set1_ps(0.400005877017974853515625f)); // + c2
  t = _mm256_fmadd_ps(t, x2, _mm256_set1_ps(0.666666686534881591796875f)); // + c1
  t = _mm256_fmadd_ps(t, x2, _mm256_set1_ps(2.0f));                       // + c0

  // 5. log(m) = x * t, then log(d) = log(m) + e * ln(2)
  return _mm256_add_ps(_mm256_mul_ps(_mm256_cvtepi32_ps(e), _mm256_set1_ps(0.693147180559945286226764f)),
                        _mm256_mul_ps(x, t));
}

#endif // NSIMD_AVX2


// ================================================================
// Precision selection: dispatch to inline → per-lane scalar
// Only SIMD paths are defined here — the #ifndef guards at the bottom
// provide per-lane scalar fallbacks when no SIMD path is defined.
// ================================================================

#if !defined(SIMD_MATH_PRECISION)
  #define SIMD_MATH_PRECISION 1   // default: Fastest
#endif

// ===== Fastest (~3.5 ULP) =====
#if SIMD_MATH_PRECISION == 1

  // Inline AVX2 polynomial (fastest, zero function-call overhead)
  #if defined(NSIMD_AVX2)
    #define N_SIN(a)     _n_sin_avx2(a)
    #define N_COS(a)     _n_cos_avx2(a)
    #define N_LOG(a)     _n_log_avx2(a)
    // All other math functions: per-lane scalar fallback
  #else
    #define N_SIN(a)     sleef_sin_ps(a)
    #define N_COS(a)     sleef_cos_ps(a)
    #define N_SINCOS(a,s,c) sleef_sincos_ps(a,s,c)
    #define N_TAN(a)     sleef_tan_ps(a)
    #define N_ASIN(a)    sleef_asin_ps(a)
    #define N_ACOS(a)    sleef_acos_ps(a)
    #define N_ATAN(a)    sleef_atan_ps(a)
    #define N_ATAN2(a,b) sleef_atan2_ps(a,b)
    #define N_SINH(a)    sleef_sinh_ps(a)
    #define N_COSH(a)    sleef_cosh_ps(a)
    #define N_TANH(a)    sleef_tanh_ps(a)
    #define N_EXP(a)     sleef_exp_ps(a)
    #define N_LOG(a)     sleef_log_ps(a)
    #define N_LOG10(a)   sleef_log10_ps(a)
    #define N_POW(a,b)   sleef_pow_ps(a,b)
  #endif
  // Non-AVX2: #ifndef guards below provide per-lane scalar fallback

// ===== High (~1.0 ULP) =====
#elif SIMD_MATH_PRECISION == 2
  // No SIMD path — #ifndef guards below provide per-lane scalar fallback

// ===== IEEE (exact scalar) =====
#else
  // On NEON, round/trunc have native instructions even in IEEE mode
  #if defined(NSIMD_NEON)
    #define N_ROUND(a)  vrndaq_f32(a)
    #define N_TRUNC(a)  vrndq_f32(a)
  #endif
  // All other math: #ifndef guards below provide per-lane scalar loops
#endif

// ================================================================
// Per-lane scalar fallback definitions for n_float = SIMD type
// These are only used when no SLEEF SIMD path was defined above.
// Each loops over NSIMD_WIDTH lanes and calls the standard math function.
// ================================================================

#ifndef N_SINCOS
static inline void _n_sincos_fallback(n_float a, n_float* s, n_float* c) {
  float ls[NSIMD_WIDTH], lc[NSIMD_WIDTH];
  n_store_ps(ls, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) ls[i] = ::sinf(ls[i]);
  n_store_ps(lc, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lc[i] = ::cosf(lc[i]);
  *s = n_load_ps(ls);
  *c = n_load_ps(lc);
}
#define N_SINCOS(a,s,c) _n_sincos_fallback(a,s,c)
#endif

#ifndef N_SIN
static inline n_float _n_sin_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::sinf(lane[i]);
  return n_load_ps(lane);
}
#define N_SIN(a) _n_sin_fallback(a)
#endif

#ifndef N_COS
static inline n_float _n_cos_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::cosf(lane[i]);
  return n_load_ps(lane);
}
#define N_COS(a) _n_cos_fallback(a)
#endif

#ifndef N_TAN
static inline n_float _n_tan_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::tanf(lane[i]);
  return n_load_ps(lane);
}
#define N_TAN(a) _n_tan_fallback(a)
#endif

#ifndef N_ASIN
static inline n_float _n_asin_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::asinf(lane[i]);
  return n_load_ps(lane);
}
#define N_ASIN(a) _n_asin_fallback(a)
#endif

#ifndef N_ACOS
static inline n_float _n_acos_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::acosf(lane[i]);
  return n_load_ps(lane);
}
#define N_ACOS(a) _n_acos_fallback(a)
#endif

#ifndef N_ATAN
static inline n_float _n_atan_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::atanf(lane[i]);
  return n_load_ps(lane);
}
#define N_ATAN(a) _n_atan_fallback(a)
#endif

#ifndef N_ATAN2
static inline n_float _n_atan2_fallback(n_float a, n_float b) {
  float la[NSIMD_WIDTH], lb[NSIMD_WIDTH];
  n_store_ps(la, a); n_store_ps(lb, b);
  for (int i = 0; i < NSIMD_WIDTH; i++) la[i] = ::atan2f(la[i], lb[i]);
  return n_load_ps(la);
}
#define N_ATAN2(a,b) _n_atan2_fallback(a,b)
#endif

#ifndef N_SINH
static inline n_float _n_sinh_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::sinhf(lane[i]);
  return n_load_ps(lane);
}
#define N_SINH(a) _n_sinh_fallback(a)
#endif

#ifndef N_COSH
static inline n_float _n_cosh_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::coshf(lane[i]);
  return n_load_ps(lane);
}
#define N_COSH(a) _n_cosh_fallback(a)
#endif

#ifndef N_TANH
static inline n_float _n_tanh_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::tanhf(lane[i]);
  return n_load_ps(lane);
}
#define N_TANH(a) _n_tanh_fallback(a)
#endif

#ifndef N_EXP
static inline n_float _n_exp_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::expf(lane[i]);
  return n_load_ps(lane);
}
#define N_EXP(a) _n_exp_fallback(a)
#endif

#ifndef N_LOG
static inline n_float _n_log_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::logf(lane[i]);
  return n_load_ps(lane);
}
#define N_LOG(a) _n_log_fallback(a)
#endif

#ifndef N_LOG10
static inline n_float _n_log10_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::log10f(lane[i]);
  return n_load_ps(lane);
}
#define N_LOG10(a) _n_log10_fallback(a)
#endif

#ifndef N_POW
static inline n_float _n_pow_fallback(n_float a, n_float b) {
  float la[NSIMD_WIDTH], lb[NSIMD_WIDTH];
  n_store_ps(la, a); n_store_ps(lb, b);
  for (int i = 0; i < NSIMD_WIDTH; i++) la[i] = ::powf(la[i], lb[i]);
  return n_load_ps(la);
}
#define N_POW(a,b) _n_pow_fallback(a,b)
#endif

// ================================================================
// Public API: n_sin_ps / n_cos_ps / ... (SIMD width transparent)
// These work with n_float (__m256 / __m128 / float32x4_t / float)
// ================================================================

static inline n_float n_sin_ps(n_float a)     { return N_SIN(a); }
static inline n_float n_cos_ps(n_float a)     { return N_COS(a); }
static inline void n_sincos_ps(n_float a, n_float* s, n_float* c) { N_SINCOS(a, s, c); }
static inline n_float n_tan_ps(n_float a)     { return N_TAN(a); }
static inline n_float n_asin_ps(n_float a)    { return N_ASIN(a); }
static inline n_float n_acos_ps(n_float a)    { return N_ACOS(a); }
static inline n_float n_atan_ps(n_float a)    { return N_ATAN(a); }
static inline n_float n_atan2_ps(n_float a, n_float b) { return N_ATAN2(a, b); }
static inline n_float n_sinh_ps(n_float a)    { return N_SINH(a); }
static inline n_float n_cosh_ps(n_float a)    { return N_COSH(a); }
static inline n_float n_tanh_ps(n_float a)    { return N_TANH(a); }
static inline n_float n_exp_ps(n_float a)     { return N_EXP(a); }
static inline n_float n_log_ps(n_float a)     { return N_LOG(a); }
static inline n_float n_log10_ps(n_float a)   { return N_LOG10(a); }
static inline n_float n_pow_ps(n_float a, n_float b) { return N_POW(a, b); }
// ================================================================
// Cross-platform inline math (via n_xxx_ps abstraction — all platforms)
// These work on AVX2, SSE4, NEON — n_xxx_ps maps to the right intrinsic.
// ================================================================

static inline n_float _n_tan_ps(n_float a) {
  return n_div_ps(n_sin_ps(a), n_cos_ps(a));
}

static inline n_float _n_atan2_ps(n_float y, n_float x) {
  n_float zero = n_set1_ps(0.0f), pi = n_set1_ps(3.141592653589793f), pi_2 = n_set1_ps(1.5707963267948966f);
  n_float r = n_atan_ps(n_div_ps(y, x));
  // Quadrant adjustment: if x<0: r ± pi, if x=0: ±pi/2
  n_float adj = n_blendv_ps(zero, n_mul_ps(n_blendv_ps(pi, n_sub_ps(zero, pi), n_cmp_lt_ps(y, zero)), n_set1_ps(-1.0f)), n_cmp_lt_ps(x, zero));
  r = n_add_ps(r, adj);
  // x=0 case: sign(y)*pi/2
  n_mask xz = n_cmp_eq_ps(x, zero);
  return n_blendv_ps(r, n_blendv_ps(n_mul_ps(pi_2, n_set1_ps(-1.0f)), pi_2, n_cmp_gt_ps(y, zero)), xz);
}

static inline n_float _n_asin_ps(n_float a) {
  // asin(x) = atan2(x, sqrt(1 - x²)) using standard formula
  n_float s = n_sqrt_ps(n_sub_ps(n_set1_ps(1.0f), n_mul_ps(a, a)));
  return n_mul_ps(n_set1_ps(2.0f), _n_atan2_ps(a, n_add_ps(n_set1_ps(1.0f), s)));
}

static inline n_float _n_acos_ps(n_float a) {
  return n_sub_ps(n_set1_ps(1.5707963267948966f), n_asin_ps(a));
}

static inline n_float _n_atan_ps(n_float a) {
  // atan polynomial on [-1,1], ~3.5 ULP; for |x|>1 use pi/2 - atan(1/x)
  n_float one = n_set1_ps(1.0f), pi_2 = n_set1_ps(1.5707963267948966f);
  // Reduce to [0,1]: x = min(|a|, 1/|a|)
  n_float ax = n_fabs_ps(a);
  n_float rx = n_div_ps(one, ax);
  n_float x = n_blendv_ps(ax, rx, n_cmp_gt_ps(ax, one));
  n_float x2 = n_mul_ps(x, x);
  n_float poly = n_set1_ps(0.00282363896258175373077393f);
  poly = n_fmadd_ps(poly, x2, n_set1_ps(-0.015956902976496002127883f));
  poly = n_fmadd_ps(poly, x2, n_set1_ps(0.0425049886108115014595081f));
  poly = n_fmadd_ps(poly, x2, n_set1_ps(-0.074890092593843260168697f));
  poly = n_fmadd_ps(poly, x2, n_set1_ps(0.166664004772415161132812f));
  n_float r = n_fmadd_ps(n_mul_ps(x, x2), poly, x);
  r = n_blendv_ps(r, n_sub_ps(pi_2, r), n_cmp_gt_ps(ax, one));
  return n_blendv_ps(n_mul_ps(r, n_set1_ps(-1.0f)), r, n_cmp_gt_ps(a, n_set1_ps(0)));
}

static inline n_float _n_sinh_ps(n_float a) {
  n_float e = n_exp_ps(a);
  return n_mul_ps(n_set1_ps(0.5f), n_sub_ps(e, n_exp_ps(n_mul_ps(n_set1_ps(-1.0f), a))));
}

static inline n_float _n_cosh_ps(n_float a) {
  n_float e = n_exp_ps(a);
  return n_mul_ps(n_set1_ps(0.5f), n_add_ps(e, n_exp_ps(n_mul_ps(n_set1_ps(-1.0f), a))));
}

static inline n_float _n_tanh_ps(n_float a) {
  n_float e2 = n_exp_ps(n_mul_ps(n_set1_ps(2.0f), a));
  return n_div_ps(n_sub_ps(e2, n_set1_ps(1.0f)), n_add_ps(e2, n_set1_ps(1.0f)));
}

// ================================================================
// Double-precision SLEEF (n_*_pd)
// ================================================================

// Double precision sin — inline AVX2 polynomial (SLEEF-derived)
#if defined(NSIMD_AVX2)
// _PIO2 constants for double
#define _N_PIO2_A_PD -1.5707963050
#define _N_PIO2_B_PD -4.37113883e-8

static inline n_double _n_sin_avx2_pd(n_double d) {
    // Range reduction: q = round(d * 2/π), r = d - q * π/2
    n_double qf = _mm256_round_pd(_mm256_mul_pd(d, _mm256_set1_pd(0.6366197723675814)), 0);
    __m128i q_lo = _mm256_cvtpd_epi32(qf);  // q as int (lower 4 lanes)
    __m128i q_hi = _mm256_cvtpd_epi32(_mm256_permute2f128_pd(qf, qf, 1));
    n_int qi = _mm256_insertf128_si256(_mm256_castsi128_si256(q_lo), q_hi, 1);
    n_double r = _mm256_sub_pd(d, _mm256_mul_pd(qf, _mm256_set1_pd(1.5707963267948966)));
    r = _mm256_sub_pd(r, _mm256_mul_pd(qf, _mm256_set1_pd(6.123233995736766e-17)));

    // Determine sin/cos based on quadrant
    n_int qi_mod4 = _mm256_and_si256(qi, _mm256_set1_epi32(3));
    n_mask use_cos_mask = _mm256_castsi256_ps(_mm256_cmpeq_epi32(
        _mm256_and_si256(qi, _mm256_set1_epi32(2)), _mm256_setzero_si256()));
    n_mask neg_mask = _mm256_castsi256_ps(_mm256_cmpeq_epi32(
        _mm256_and_si256(qi_mod4, _mm256_set1_epi32(1)), _mm256_set1_epi32(1)));

    n_double x = _mm256_blendv_pd(r,
        _mm256_sub_pd(_mm256_set1_pd(1.5707963267948966), r), _mm256_castps_pd(use_cos_mask));

    // Polynomial: sin(x) ≈ x + x³·c1 + x⁵·c2 + x⁷·c3 + x⁹·c4
    n_double x2 = _mm256_mul_pd(x, x);
    n_double c = _mm256_set1_pd(-1.66666666666666324348e-01);   // c1
    c = _mm256_fmadd_pd(c, x2, _mm256_set1_pd(8.33333333332248946124e-03));  // c2
    c = _mm256_fmadd_pd(c, x2, _mm256_set1_pd(-1.98412698298579493134e-04)); // c3
    c = _mm256_fmadd_pd(c, x2, _mm256_set1_pd(2.75573137070700676789e-06));  // c4
    c = _mm256_fmadd_pd(c, x2, _mm256_set1_pd(-2.50507602534068634195e-08)); // c5
    n_double result = _mm256_fmadd_pd(c, _mm256_mul_pd(x, x2), x);

    // Negate if needed
    result = _mm256_blendv_pd(result, _mm256_xor_pd(_mm256_castsi256_pd(_mm256_set1_epi64x(0x8000000000000000)), result), _mm256_castps_pd(neg_mask));
    return result;
}
#define N_SIN_PD(a) _n_sin_avx2_pd(a)
#define N_COS_PD(a) _n_sin_avx2_pd(_mm256_add_pd(a, _mm256_set1_pd(1.5707963267948966)))

#elif defined(NSIMD_SSE4) && defined(__aarch64__)
// SSE4: use scalar per-lane for each double lane (only 2-wide anyway, scalar is fine)
#define N_SIN_PD(a) _n_sin_pd_fallback(a)
#else
// Fallback: per-lane scalar sin
static inline n_double _n_sin_pd_fallback(n_double a) {
    double lane[8]; int w = NSIMD_WIDTH < 8 ? NSIMD_WIDTH : 8;
    n_store_pd(lane, a);
    for (int i = 0; i < w; i++) lane[i] = sin(lane[i]);
    return n_load_pd(lane);
}
#define N_SIN_PD(a) _n_sin_pd_fallback(a)
#define N_COS_PD(a) _n_cos_pd_fallback(a)
#endif

#ifndef N_COS_PD
static inline n_double _n_cos_pd_fallback(n_double a) {
    double lane[8]; int w = NSIMD_WIDTH < 8 ? NSIMD_WIDTH : 8;
    n_store_pd(lane, a);
    for (int i = 0; i < w; i++) lane[i] = cos(lane[i]);
    return n_load_pd(lane);
}
#define N_COS_PD(a) _n_cos_pd_fallback(a)
#endif

// Public API
static inline n_double n_sin_pd(n_double a)     { return N_SIN_PD(a); }
static inline n_double n_cos_pd(n_double a)     { return N_COS_PD(a); }
// sqrt_pd is already defined in NativeSIMD.h as n_sqrt_pd
