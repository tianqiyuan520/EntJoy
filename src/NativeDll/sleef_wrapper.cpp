// sleef_wrapper.cpp — Thin wrapper around SLEEF renamed symbols.
// Provides clean `sleef_sin_ps`, `sleef_cos_ps` etc names (C linkage).
// Compiled as part of NativeDll when HAS_SLEEF=1.
// SLEEF renames its symbols with platform-specific suffixes (verified via dumpbin):
//   AVX2: Sleef_sinf8_u35avx2, ...
//   SSE4: Sleef_sinf4_u35sse2, ...
//   NEON: Sleef_sinf4_u35advsimd, ...
#include "NativeSIMD.h"

// ================================================================
// Per-platform SLEEF symbol declarations
// Each SLEEF function takes/returns n_float (__m256 on AVX2, __m128 on SSE4/NEON)
// ================================================================

#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
  // AVX2: 8-wide, suffix avx2
  #define SLEEF_PASTE8(f) f##8
  // Declare per-precision
  #define DECL1u35(f) extern "C" n_float SLEEF_PASTE8(Sleef_##f)_u35avx2(n_float)
  #define DECL1u10(f) extern "C" n_float SLEEF_PASTE8(Sleef_##f)_u10avx2(n_float)
  #define DECL2u35(f) extern "C" n_float SLEEF_PASTE8(Sleef_##f)_u35avx2(n_float, n_float)
  #define DECL2u10(f) extern "C" n_float SLEEF_PASTE8(Sleef_##f)_u10avx2(n_float, n_float)
  #define DECL3u35(f) extern "C" void SLEEF_PASTE8(Sleef_##f)_u35avx2(n_float, n_float*, n_float*)
  #define DECL3u10(f) extern "C" void SLEEF_PASTE8(Sleef_##f)_u10avx2(n_float, n_float*, n_float*)
  // Call definitions
  #define CALL1u35(f) SLEEF_PASTE8(Sleef_##f)_u35avx2
  #define CALL1u10(f) SLEEF_PASTE8(Sleef_##f)_u10avx2
  #define CALL2u35(f) SLEEF_PASTE8(Sleef_##f)_u35avx2
  #define CALL2u10(f) SLEEF_PASTE8(Sleef_##f)_u10avx2
  #define CALL3u35(f) SLEEF_PASTE8(Sleef_##f)_u35avx2
  #define CALL3u10(f) SLEEF_PASTE8(Sleef_##f)_u10avx2

#elif defined(NSIMD_SSE4)
  // SSE4: 4-wide, suffix sse2
  #define SLEEF_PASTE4(f) f##4
  #define DECL1u35(f) extern "C" n_float SLEEF_PASTE4(Sleef_##f)_u35sse2(n_float)
  #define DECL1u10(f) extern "C" n_float SLEEF_PASTE4(Sleef_##f)_u10sse2(n_float)
  #define DECL2u35(f) extern "C" n_float SLEEF_PASTE4(Sleef_##f)_u35sse2(n_float, n_float)
  #define DECL2u10(f) extern "C" n_float SLEEF_PASTE4(Sleef_##f)_u10sse2(n_float, n_float)
  #define DECL3u35(f) extern "C" void SLEEF_PASTE4(Sleef_##f)_u35sse2(n_float, n_float*, n_float*)
  #define DECL3u10(f) extern "C" void SLEEF_PASTE4(Sleef_##f)_u10sse2(n_float, n_float*, n_float*)
  #define CALL1u35(f) SLEEF_PASTE4(Sleef_##f)_u35sse2
  #define CALL1u10(f) SLEEF_PASTE4(Sleef_##f)_u10sse2
  #define CALL2u35(f) SLEEF_PASTE4(Sleef_##f)_u35sse2
  #define CALL2u10(f) SLEEF_PASTE4(Sleef_##f)_u10sse2
  #define CALL3u35(f) SLEEF_PASTE4(Sleef_##f)_u35sse2
  #define CALL3u10(f) SLEEF_PASTE4(Sleef_##f)_u10sse2

#elif defined(NSIMD_NEON)
  // NEON: 4-wide, suffix advsimd
  #define SLEEF_PASTE4(f) f##4
  #define DECL1u35(f) extern "C" n_float SLEEF_PASTE4(Sleef_##f)_u35advsimd(n_float)
  #define DECL1u10(f) extern "C" n_float SLEEF_PASTE4(Sleef_##f)_u10advsimd(n_float)
  #define DECL2u35(f) extern "C" n_float SLEEF_PASTE4(Sleef_##f)_u35advsimd(n_float, n_float)
  #define DECL2u10(f) extern "C" n_float SLEEF_PASTE4(Sleef_##f)_u10advsimd(n_float, n_float)
  #define DECL3u35(f) extern "C" void SLEEF_PASTE4(Sleef_##f)_u35advsimd(n_float, n_float*, n_float*)
  #define DECL3u10(f) extern "C" void SLEEF_PASTE4(Sleef_##f)_u10advsimd(n_float, n_float*, n_float*)
  #define CALL1u35(f) SLEEF_PASTE4(Sleef_##f)_u35advsimd
  #define CALL1u10(f) SLEEF_PASTE4(Sleef_##f)_u10advsimd
  #define CALL2u35(f) SLEEF_PASTE4(Sleef_##f)_u35advsimd
  #define CALL2u10(f) SLEEF_PASTE4(Sleef_##f)_u10advsimd
  #define CALL3u35(f) SLEEF_PASTE4(Sleef_##f)_u35advsimd
  #define CALL3u10(f) SLEEF_PASTE4(Sleef_##f)_u10advsimd
#else
  #error "SLEEF wrapper compiled without SIMD platform"
#endif

// ===== Declarations of all needed SLEEF symbols =====
DECL1u35(sinf);
DECL1u35(cosf);
DECL1u35(tanf);
DECL1u35(asinf);
DECL1u35(acosf);
DECL1u35(atanf);
DECL2u35(atan2f);
DECL1u35(sinhf);
DECL1u35(coshf);
DECL1u35(tanhf);
DECL1u35(logf);
DECL3u35(sincosf);

DECL1u10(sinf);
DECL1u10(cosf);
DECL1u10(tanf);
DECL1u10(asinf);
DECL1u10(acosf);
DECL1u10(atanf);
DECL2u10(atan2f);
DECL1u10(sinhf);
DECL1u10(coshf);
DECL1u10(tanhf);
DECL1u10(expf);
DECL1u10(logf);
DECL1u10(log10f);
DECL2u10(powf);
DECL3u10(sincosf);

// ===== Public API: sleef_sin_ps / sleef_cos_ps / ... =====
// These are the only functions referenced by NativeSIMD_math.h

#define WRAP1(name, prec) \
  extern "C" n_float sleef_##name##_ps(n_float a) { return CALL1##prec(name##f)(a); }
#define WRAP2(name, prec) \
  extern "C" n_float sleef_##name##_ps(n_float a, n_float b) { return CALL2##prec(name##f)(a, b); }
#define WRAP3(name, prec) \
  extern "C" void sleef_##name##_ps(n_float a, n_float* s, n_float* c) { CALL3##prec(name##f)(a, s, c); }

WRAP1(sin,   u35)
WRAP1(cos,   u35)
WRAP1(tan,   u35)
WRAP1(asin,  u35)
WRAP1(acos,  u35)
WRAP1(atan,  u35)
WRAP2(atan2, u35)
WRAP1(sinh,  u35)
WRAP1(cosh,  u35)
WRAP1(tanh,  u35)
WRAP1(log,   u35)
WRAP3(sincos,u35)

WRAP1(exp,   u10)
WRAP1(log10, u10)
WRAP2(pow,   u10)
