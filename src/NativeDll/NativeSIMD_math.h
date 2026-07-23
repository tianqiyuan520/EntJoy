// NativeSIMD_math.h — SIMD vector math via SLEEF (cross-platform sin/cos/log/exp/...)
// Uses SLEEF as git submodule at src/NativeDll/sleef/
// Precision level controlled by -DSIMD_MATH_PRECISION=1|2|3
//   1 = Fastest (~3.5 ULP, game physics/animation quality)
//   2 = High   (~1.0 ULP)
//   3 = IEEE   (exact scalar sinf/cosf/..., fallback)
#pragma once
#include "NativeSIMD.h"

// SLEEF link: CMakeLists.txt compiles sleef_wrapper.cpp which wraps SLEEF
// with predictable `sleef_sin_ps`, `sleef_cos_ps` etc. function names.
// These are C-linkage functions that resolve through the sleef libraries.
// If HAS_SLEEF=0, native fallbacks uses per-lane scalar loops.
#pragma once
#include "NativeSIMD.h"

// Wrapper functions defined in sleef_wrapper.cpp. When HAS_SLEEF=0,
// these are replaced by the per-lane fallbacks below.
#if HAS_SLEEF
extern "C" {
  n_float sleef_sin_ps(n_float);
  n_float sleef_cos_ps(n_float);
  void   sleef_sincos_ps(n_float, n_float*, n_float*);
  n_float sleef_tan_ps(n_float);
  n_float sleef_asin_ps(n_float);
  n_float sleef_acos_ps(n_float);
  n_float sleef_atan_ps(n_float);
  n_float sleef_atan2_ps(n_float, n_float);
  n_float sleef_sinh_ps(n_float);
  n_float sleef_cosh_ps(n_float);
  n_float sleef_tanh_ps(n_float);
  n_float sleef_exp_ps(n_float);
  n_float sleef_log_ps(n_float);
  n_float sleef_log10_ps(n_float);
  n_float sleef_pow_ps(n_float, n_float);
}
#endif

// ================================================================
// Precision selection: dispatch to SLEEF _u35, _u10, or scalar
// Only SIMD paths are defined here — the #ifndef guards at the bottom
// provide per-lane scalar fallbacks for any platform/SLEEF combo
// that doesn't get a SIMD definition above.
// ================================================================

#if !defined(SIMD_MATH_PRECISION)
  #define SIMD_MATH_PRECISION 1   // default: Fastest
#endif

// ===== Fastest (~3.5 ULP) =====
#if SIMD_MATH_PRECISION == 1

  #if HAS_SLEEF
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
  // No #else — #ifndef guards below provide per-lane scalar fallback

// ===== High (~1.0 ULP) =====
#elif SIMD_MATH_PRECISION == 2

  #if HAS_SLEEF
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
  // No #else — #ifndef guards below provide per-lane scalar fallback

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
