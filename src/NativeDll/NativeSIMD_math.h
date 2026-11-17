// NativeSIMD_math.h — SIMD vector math (inline AVX2 + per-lane scalar fallbacks)
// Precision level controlled by -DSIMD_MATH_PRECISION=1|2|3
//   1 = Fastest (~3.5 ULP, game physics/animation quality)
//   2 = High   (~1.0 ULP)
//   3 = IEEE   (exact scalar sinf/cosf/..., fallback)
#pragma once
#include "NativeSIMD.h"

// Cross-platform forceinline: MSVC __forceinline, GCC/Clang always_inline
#if defined(_MSC_VER)
    #define N_FORCEINLINE __forceinline
#elif defined(__GNUC__) || defined(__clang__)
    #define N_FORCEINLINE inline __attribute__((always_inline))
#else
    #define N_FORCEINLINE inline
#endif

// ================================================================
// Inline AVX2 polynomial implementations (SLEEF coefficients, zero call overhead)
// Other platforms fall through to per-lane scalar fallbacks below.
// ================================================================
// Constants from sleef/src/libm/sleefsimdsp.c: PI_A2/PI_B2/PI_C2 = π/2 in 3-parts
#define _N_PIO2_A 1.57079637e+00f
#define _N_PIO2_B -4.37113883e-08f

#if defined(NSIMD_AVX2) || defined(NSIMD_AVX512)

// AVX2 内核在 AVX512 编译单元里也要可用：全部用显式 __m256 类型与本地 helper，
// 不依赖 __m256 别名（它在 AVX512 TU 里是 __m512）。
static N_FORCEINLINE __m256 _n256_abs(__m256 a) { return _mm256_andnot_ps(_mm256_set1_ps(-0.0f), a); }
static N_FORCEINLINE __m256 _n256_round(__m256 a) { return _mm256_round_ps(a, _MM_FROUND_TO_NEAREST_INT | _MM_FROUND_NO_EXC); }
static N_FORCEINLINE __m256 _n256_trunc(__m256 a) { return _mm256_round_ps(a, _MM_FROUND_TO_ZERO | _MM_FROUND_NO_EXC); }
static N_FORCEINLINE __m256 _n256_eq(__m256 a, __m256 b) { return _mm256_cmp_ps(a, b, _CMP_EQ_OQ); }
static N_FORCEINLINE __m256 _n256_lt(__m256 a, __m256 b) { return _mm256_cmp_ps(a, b, _CMP_LT_OQ); }
static N_FORCEINLINE __m256 _n256_gt(__m256 a, __m256 b) { return _mm256_cmp_ps(a, b, _CMP_GT_OQ); }
static N_FORCEINLINE __m256 _n256_ne(__m256 a, __m256 b) { return _mm256_cmp_ps(a, b, _CMP_NEQ_UQ); }
static N_FORCEINLINE __m256 _n256_not(__m256 m) { return _mm256_xor_ps(m, _mm256_castsi256_ps(_mm256_set1_epi32(-1))); }


// 8 lane 的 π/2 归约：用 double 算 q 与 r，避免 float 常数在大 |x| 下的乘积误差。
// 返回 |r| <= π/4 的 float 余数，象限 q 由 qOut 带出（q = round(x*2/π)）。
static N_FORCEINLINE __m256 _n_reduce_pio2_avx2(__m256 d, __m256i* qOut) {
  __m256d lo = _mm256_cvtps_pd(_mm256_castps256_ps128(d));
  __m256d hi = _mm256_cvtps_pd(_mm256_extractf128_ps(d, 1));

  const __m256d two_over_pi = _mm256_set1_pd(0.63661977236758134307553505349006);
  const __m256d pio2_1 = _mm256_set1_pd(1.57079632673412561417e+00);
  const __m256d pio2_2 = _mm256_set1_pd(6.07710050650619224932e-11);
  const __m256d pio2_3 = _mm256_set1_pd(2.02226624879595063154e-21);

  __m256d qlo = _mm256_round_pd(_mm256_mul_pd(lo, two_over_pi), _MM_FROUND_TO_NEAREST_INT | _MM_FROUND_NO_EXC);
  __m256d qhi = _mm256_round_pd(_mm256_mul_pd(hi, two_over_pi), _MM_FROUND_TO_NEAREST_INT | _MM_FROUND_NO_EXC);

  __m256d rlo = _mm256_fnmadd_pd(qlo, pio2_1, lo);
  rlo = _mm256_fnmadd_pd(qlo, pio2_2, rlo);
  rlo = _mm256_fnmadd_pd(qlo, pio2_3, rlo);
  __m256d rhi = _mm256_fnmadd_pd(qhi, pio2_1, hi);
  rhi = _mm256_fnmadd_pd(qhi, pio2_2, rhi);
  rhi = _mm256_fnmadd_pd(qhi, pio2_3, rhi);

  __m128i qloi = _mm256_cvtpd_epi32(qlo);
  __m128i qhii = _mm256_cvtpd_epi32(qhi);
  *qOut = _mm256_insertf128_si256(_mm256_castsi128_si256(qloi), qhii, 1);

  __m128 rf_lo = _mm256_cvtpd_ps(rlo);
  __m128 rf_hi = _mm256_cvtpd_ps(rhi);
  return _mm256_insertf128_ps(_mm256_castps128_ps256(rf_lo), rf_hi, 1);
}

// sin(r) / cos(r)，|r| <= π/4
static N_FORCEINLINE __m256 _n_sin_poly_avx2(__m256 r) {
  __m256 s = _mm256_mul_ps(r, r);
  __m256 p = _mm256_set1_ps(2.6083159809786593541503e-06f);
  p = _mm256_fmadd_ps(p, s, _mm256_set1_ps(-0.0001981069071916863322258f));
  p = _mm256_fmadd_ps(p, s, _mm256_set1_ps(0.00833307858556509017944336f));
  p = _mm256_fmadd_ps(p, s, _mm256_set1_ps(-0.166666597127914428710938f));
  return _mm256_fmadd_ps(_mm256_mul_ps(s, r), p, r);
}

static N_FORCEINLINE __m256 _n_cos_poly_avx2(__m256 r) {
  __m256 s = _mm256_mul_ps(r, r);
  __m256 p = _mm256_set1_ps(2.48015873015873e-05f);
  p = _mm256_fmadd_ps(p, s, _mm256_set1_ps(-0.00138888892251998f));
  p = _mm256_fmadd_ps(p, s, _mm256_set1_ps(0.0416666664189304f));
  p = _mm256_fmadd_ps(p, s, _mm256_set1_ps(-0.5f));
  return _mm256_fmadd_ps(p, s, _mm256_set1_ps(1.0f));
}

static N_FORCEINLINE __m256 _n_sin_avx2(__m256 d) {
  // sin(x) = ±sin(r) / ±cos(r)：q 偶数取 sin，奇数取 cos；q&2 决定符号。
  __m256i qi;
  __m256 r = _n_reduce_pio2_avx2(d, &qi);
  __m256 u_sin = _n_sin_poly_avx2(r);
  __m256 u_cos = _n_cos_poly_avx2(r);
  __m256 even = _mm256_castsi256_ps(_mm256_cmpeq_epi32(_mm256_and_si256(qi, _mm256_set1_epi32(1)),
                                                       _mm256_setzero_si256()));
  __m256 u = _mm256_blendv_ps(u_cos, u_sin, even);
  __m256i flip = _mm256_and_si256(qi, _mm256_set1_epi32(2));
  __m256 rl = _mm256_xor_ps(u, _mm256_castsi256_ps(_mm256_slli_epi32(flip, 30)));
  // |x| > 1e7 时 double 三段归约的余数已不可靠，交给 libm 精确归约（罕见路径）。
  __m256 big = _mm256_cmp_ps(_mm256_and_ps(d, _mm256_castsi256_ps(_mm256_set1_epi32(0x7fffffff))),
                              _mm256_set1_ps(1.0e7f), _CMP_GT_OQ);
  if (_mm256_movemask_ps(big) != 0)
  {
    float lane[8], res[8];
    _mm256_storeu_ps(lane, d);
    _mm256_storeu_ps(res, rl);
    int m = _mm256_movemask_ps(big);
    for (int k = 0; k < NSIMD_WIDTH; k++) if (m & (1 << k)) res[k] = ::sinf(lane[k]);
    rl = _mm256_loadu_ps(res);
  }
  return rl;
}

static N_FORCEINLINE __m256 _n_cos_avx2(__m256 d) {
  // cos(x) = ±cos(r) / ±sin(r)：q 偶数取 cos，奇数取 sin；(q+1)&2 决定符号。
  __m256i qi;
  __m256 r = _n_reduce_pio2_avx2(d, &qi);
  __m256 u_sin = _n_sin_poly_avx2(r);
  __m256 u_cos = _n_cos_poly_avx2(r);
  __m256 even = _mm256_castsi256_ps(_mm256_cmpeq_epi32(_mm256_and_si256(qi, _mm256_set1_epi32(1)),
                                                       _mm256_setzero_si256()));
  __m256 u = _mm256_blendv_ps(u_sin, u_cos, even);
  __m256i flip = _mm256_and_si256(_mm256_add_epi32(qi, _mm256_set1_epi32(1)), _mm256_set1_epi32(2));
  __m256 rl = _mm256_xor_ps(u, _mm256_castsi256_ps(_mm256_slli_epi32(flip, 30)));
  __m256 big = _mm256_cmp_ps(_mm256_and_ps(d, _mm256_castsi256_ps(_mm256_set1_epi32(0x7fffffff))),
                              _mm256_set1_ps(1.0e7f), _CMP_GT_OQ);
  if (_mm256_movemask_ps(big) != 0)
  {
    float lane[8], res[8];
    _mm256_storeu_ps(lane, d);
    _mm256_storeu_ps(res, rl);
    int m = _mm256_movemask_ps(big);
    for (int k = 0; k < NSIMD_WIDTH; k++) if (m & (1 << k)) res[k] = ::cosf(lane[k]);
    rl = _mm256_loadu_ps(res);
  }
  return rl;
}
static N_FORCEINLINE __m256 _n_log_avx2(__m256 d) {
  // |x - 1| <= 0.25：直接展开 log(1+t)，避免尾数与指数拼接在近 1 处丢绝对精度
  __m256 t = _mm256_sub_ps(d, _mm256_set1_ps(1.0f));
  __m256 at = _mm256_and_ps(t, _mm256_castsi256_ps(_mm256_set1_epi32(0x7fffffff)));

  // log(1+t) = t*(1 - t/2 + t^2/3 - ... + t^8/9)
  __m256 s = _mm256_set1_ps(-1.0f / 9.0f);
  s = _mm256_fmadd_ps(s, t, _mm256_set1_ps(1.0f / 8.0f));
  s = _mm256_fmadd_ps(s, t, _mm256_set1_ps(-1.0f / 7.0f));
  s = _mm256_fmadd_ps(s, t, _mm256_set1_ps(1.0f / 6.0f));
  s = _mm256_fmadd_ps(s, t, _mm256_set1_ps(-1.0f / 5.0f));
  s = _mm256_fmadd_ps(s, t, _mm256_set1_ps(1.0f / 4.0f));
  s = _mm256_fmadd_ps(s, t, _mm256_set1_ps(-1.0f / 3.0f));
  s = _mm256_fmadd_ps(s, t, _mm256_set1_ps(0.5f));
  s = _mm256_fmadd_ps(s, t, _mm256_set1_ps(-1.0f));
  __m256 near = _mm256_mul_ps(_mm256_sub_ps(_mm256_setzero_ps(), t), s);   // t*(1 - t/2 + ...)

  // 其余范围：尾数归约到 [1,2) + 两段 ln2 拼接
  __m256 dpos = _mm256_and_ps(d, _mm256_castsi256_ps(_mm256_set1_epi32(0x7fffffff)));
  __m256i emm0 = _mm256_srli_epi32(_mm256_castps_si256(dpos), 23);
  __m256i e = _mm256_sub_epi32(emm0, _mm256_set1_epi32(127));
  __m256i mant = _mm256_and_si256(_mm256_castps_si256(d), _mm256_set1_epi32(0x807fffff));
  mant = _mm256_or_si256(mant, _mm256_set1_epi32(0x3f800000));
  __m256 m = _mm256_castsi256_ps(mant);
  __m256 x = _mm256_div_ps(_mm256_sub_ps(m, _mm256_set1_ps(1.0f)),
                            _mm256_add_ps(m, _mm256_set1_ps(1.0f)));
  __m256 x2 = _mm256_mul_ps(x, x);
  // log(m) = x*(2 + 2x^2/3 + 2x^4/5 + ... + 2x^12/13)，7 项使 m -> 2 处的截断误差降到 1e-8
  __m256 p = _mm256_set1_ps(0.15384615384615385f);      // 2/13
  p = _mm256_fmadd_ps(p, x2, _mm256_set1_ps(0.18181818181818182f));   // 2/11
  p = _mm256_fmadd_ps(p, x2, _mm256_set1_ps(0.2222222222222222f));    // 2/9
  p = _mm256_fmadd_ps(p, x2, _mm256_set1_ps(0.2857142857142857f));    // 2/7
  p = _mm256_fmadd_ps(p, x2, _mm256_set1_ps(0.4f));                   // 2/5
  p = _mm256_fmadd_ps(p, x2, _mm256_set1_ps(0.6666666666666666f));    // 2/3
  p = _mm256_fmadd_ps(p, x2, _mm256_set1_ps(2.0f));
  __m256 ef = _mm256_cvtepi32_ps(e);
  __m256 wide = _mm256_add_ps(_mm256_mul_ps(ef, _mm256_set1_ps(0.693145751953125f)),
                               _mm256_mul_ps(ef, _mm256_set1_ps(1.428606765330187045e-6f)));
  wide = _mm256_add_ps(wide, _mm256_mul_ps(x, p));

  return _mm256_blendv_ps(wide, near, _mm256_cmp_ps(at, _mm256_set1_ps(0.25f), _CMP_LE_OQ));
}
static N_FORCEINLINE __m256 _n_exp_avx2(__m256 d) {
  // exp(d) = 2^n * exp(r)，n = round(d/ln2)，r = d - n*ln2（两段 ln2 抵消减法误差）。
  // |r| <= ln2/2 ≈ 0.3466，7 次 Taylor 的截断误差 r^8/40320 ≈ 2.4e-9，低于 float 的半个 ulp。
  __m256 n = _n256_round(_mm256_mul_ps(d, _mm256_set1_ps(1.4426950408889634f)));   // 1/ln2
  __m256 r = _mm256_fnmadd_ps(n, _mm256_set1_ps(0.693145751953125f), d);          // d - n*ln2_hi
  r = _mm256_fnmadd_ps(n, _mm256_set1_ps(1.428606765330187045e-6f), r);            //   - n*ln2_lo

  __m256 p = _mm256_set1_ps(1.0f / 5040.0f);
  p = _mm256_fmadd_ps(p, r, _mm256_set1_ps(1.0f / 720.0f));
  p = _mm256_fmadd_ps(p, r, _mm256_set1_ps(1.0f / 120.0f));
  p = _mm256_fmadd_ps(p, r, _mm256_set1_ps(1.0f / 24.0f));
  p = _mm256_fmadd_ps(p, r, _mm256_set1_ps(1.0f / 6.0f));
  p = _mm256_fmadd_ps(p, r, _mm256_set1_ps(0.5f));
  p = _mm256_fmadd_ps(p, r, _mm256_set1_ps(1.0f));
  p = _mm256_fmadd_ps(p, r, _mm256_set1_ps(1.0f));                                // exp(r) ≈ 1 + r*(1 + r*(…))

  // 2^n 拆成两段指数域拼接，使 n < -126 的次正规结果仍按指数衰减（单段拼接会丢低位指数）。
  __m256i ni = _mm256_cvtps_epi32(_mm256_max_ps(_mm256_min_ps(n, _mm256_set1_ps(127.0f)),
                                               _mm256_set1_ps(-252.0f)));
  __m256i n1 = _mm256_max_epi32(ni, _mm256_set1_epi32(-126));
  __m256i n2 = _mm256_sub_epi32(ni, n1);
  __m256 s1 = _mm256_castsi256_ps(_mm256_slli_epi32(_mm256_add_epi32(n1, _mm256_set1_epi32(127)), 23));
  __m256 s2 = _mm256_castsi256_ps(_mm256_slli_epi32(_mm256_add_epi32(n2, _mm256_set1_epi32(127)), 23));

  __m256 result = _mm256_mul_ps(_mm256_mul_ps(p, s1), s2);

  // 与 expf 的边界一致：上溢为 +inf，下溢为 0；NaN 输入保持 NaN。
  result = _mm256_blendv_ps(result, _mm256_set1_ps(INFINITY),
                            _mm256_cmp_ps(d, _mm256_set1_ps(88.722839f), _CMP_GE_OQ));
  result = _mm256_blendv_ps(result, _mm256_setzero_ps(),
                            _mm256_cmp_ps(d, _mm256_set1_ps(-103.972084f), _CMP_LE_OQ));
  return result;
}

// ---- SLEEF 路线的高精度 pow：log2 与 exp2 都在 double 里算，float 只做最后折回 ----
// log2(m)，m ∈ [1,2)：不需要指数位提取，指数由调用方在 float 域带进来
static N_FORCEINLINE __m256d _n_log2_mant_pd(__m256d m) {
  __m256d t = _mm256_div_pd(_mm256_sub_pd(m, _mm256_set1_pd(1.0)), _mm256_add_pd(m, _mm256_set1_pd(1.0)));
  __m256d z = _mm256_mul_pd(t, t);
  __m256d s = _mm256_set1_pd(1.0 / 13.0);
  s = _mm256_fmadd_pd(s, z, _mm256_set1_pd(1.0 / 11.0));
  s = _mm256_fmadd_pd(s, z, _mm256_set1_pd(1.0 / 9.0));
  s = _mm256_fmadd_pd(s, z, _mm256_set1_pd(1.0 / 7.0));
  s = _mm256_fmadd_pd(s, z, _mm256_set1_pd(1.0 / 5.0));
  s = _mm256_fmadd_pd(s, z, _mm256_set1_pd(1.0 / 3.0));
  s = _mm256_fmadd_pd(s, z, _mm256_set1_pd(1.0));
  return _mm256_mul_pd(_mm256_mul_pd(t, s), _mm256_set1_pd(2.8853900817779268));  // 2/ln2
}

static N_FORCEINLINE __m256d _n_log_mant_pd(__m256d m) {
  return _mm256_mul_pd(_n_log2_mant_pd(m), _mm256_set1_pd(0.69314718055994530942));
}

static N_FORCEINLINE __m256d _n_exp2_pd(__m256d v) {
  __m256d n = _mm256_round_pd(v, _MM_FROUND_TO_NEAREST_INT | _MM_FROUND_NO_EXC);
  __m256d f = _mm256_sub_pd(v, n);
  __m256d r = _mm256_mul_pd(f, _mm256_set1_pd(0.69314718055994530942));  // f*ln2

  // exp(r)，|r| <= 0.347：12 次 Taylor，截断误差 < 1e-17
  __m256d p = _mm256_set1_pd(1.0 / 479001600.0);
  p = _mm256_fmadd_pd(p, r, _mm256_set1_pd(1.0 / 39916800.0));
  p = _mm256_fmadd_pd(p, r, _mm256_set1_pd(1.0 / 3628800.0));
  p = _mm256_fmadd_pd(p, r, _mm256_set1_pd(1.0 / 362880.0));
  p = _mm256_fmadd_pd(p, r, _mm256_set1_pd(1.0 / 40320.0));
  p = _mm256_fmadd_pd(p, r, _mm256_set1_pd(1.0 / 5040.0));
  p = _mm256_fmadd_pd(p, r, _mm256_set1_pd(1.0 / 720.0));
  p = _mm256_fmadd_pd(p, r, _mm256_set1_pd(1.0 / 120.0));
  p = _mm256_fmadd_pd(p, r, _mm256_set1_pd(1.0 / 24.0));
  p = _mm256_fmadd_pd(p, r, _mm256_set1_pd(1.0 / 6.0));
  p = _mm256_fmadd_pd(p, r, _mm256_set1_pd(0.5));
  p = _mm256_fmadd_pd(p, r, _mm256_set1_pd(1.0));
  p = _mm256_fmadd_pd(p, r, _mm256_set1_pd(1.0));

  // 2^n：int32 夹到 [-1022, 1023] 后转 int64 拼指数位
  __m128i ni32 = _mm256_cvtpd_epi32(_mm256_max_pd(_mm256_min_pd(n, _mm256_set1_pd(1023.0)),
                                                  _mm256_set1_pd(-1022.0)));
  __m256i ni64 = _mm256_cvtepi32_epi64(ni32);
  __m256d scale = _mm256_castsi256_pd(_mm256_slli_epi64(_mm256_add_epi64(ni64, _mm256_set1_epi64x(1023)), 52));
  return _mm256_mul_pd(p, scale);
}
static N_FORCEINLINE __m256 _n_pow_avx2_ps(__m256 x, __m256 y) {
  __m256d yl = _mm256_cvtps_pd(_mm256_castps256_ps128(y));
  __m256d yh = _mm256_cvtps_pd(_mm256_extractf128_ps(y, 1));

  // 指数与尾数在 float 域取好，log2 只对尾数 [1,2) 展开
  __m256 ax = _n256_abs(x);
  __m256i ei = _mm256_sub_epi32(_mm256_srli_epi32(_mm256_castps_si256(ax), 23), _mm256_set1_epi32(127));
  __m256 mf = _mm256_castsi256_ps(_mm256_or_si256(_mm256_and_si256(_mm256_castps_si256(x), _mm256_set1_epi32(0x807fffff)),
                                                                 _mm256_set1_epi32(0x3f800000)));
  __m256d logl = _mm256_add_pd(_mm256_cvtepi32_pd(_mm256_castsi256_si128(ei)),
                                _n_log2_mant_pd(_mm256_cvtps_pd(_mm256_castps256_ps128(mf))));
  __m256d logh = _mm256_add_pd(_mm256_cvtepi32_pd(_mm256_extracti128_si256(ei, 1)),
                                _n_log2_mant_pd(_mm256_cvtps_pd(_mm256_extractf128_ps(mf, 1))));
  __m256d vl = _mm256_mul_pd(yl, logl);
  __m256d vh = _mm256_mul_pd(yh, logh);
  __m128 rlo = _mm256_cvtpd_ps(_n_exp2_pd(vl));
  __m128 rhi = _mm256_cvtpd_ps(_n_exp2_pd(vh));
  __m256 mag = _mm256_insertf128_ps(_mm256_castps128_ps256(rlo), rhi, 1);

  // 负底数：仅整数指数成立，奇数指数取负；非整数指数为 NaN
  __m256 ay = _n256_abs(y);
  __m256 yInt = _mm256_and_ps(_n256_eq(y, _n256_trunc(y)), _n256_lt(ay, _mm256_set1_ps(8388608.0f)));
  __m256 half = _n256_trunc(_mm256_mul_ps(y, _mm256_set1_ps(0.5f)));
  __m256 yOdd = _n256_ne(_mm256_mul_ps(half, _mm256_set1_ps(2.0f)), y);
  __m256 sign = _mm256_blendv_ps(_mm256_set1_ps(1.0f), _mm256_set1_ps(-1.0f),
                             _mm256_and_ps(_n256_lt(x, _mm256_set1_ps(0.0f)), _mm256_and_ps(yInt, yOdd)));
  __m256 result = _mm256_mul_ps(mag, sign);

  __m256 xNeg = _n256_lt(x, _mm256_set1_ps(0.0f));
  result = _mm256_blendv_ps(result, _mm256_set1_ps(NAN), _mm256_and_ps(xNeg, _n256_not(yInt)));
  result = _mm256_blendv_ps(result, _mm256_set1_ps(1.0f), _n256_eq(y, _mm256_set1_ps(0.0f)));
  __m256 xZero = _n256_eq(x, _mm256_set1_ps(0.0f));
  result = _mm256_blendv_ps(result, _mm256_set1_ps(INFINITY),
                       _mm256_and_ps(xZero, _n256_lt(y, _mm256_set1_ps(0.0f))));
  result = _mm256_blendv_ps(result, _mm256_set1_ps(0.0f),
                       _mm256_and_ps(xZero, _n256_gt(y, _mm256_set1_ps(0.0f))));
  return _mm256_blendv_ps(result, _mm256_set1_ps(NAN),
                     _mm256_or_ps(_n256_ne(x, x), _n256_ne(y, y)));
}
// ================================================================
// High 档：double 中间精度实现（AVX2，目标 <=1 ULP float）
// 8 个 float lane 拆成两组 4 lane double，算完折回。
// ================================================================
static N_FORCEINLINE void _n_reduce4_pd(__m256d x, __m256d* rOut, __m128i* qOut) {
  // 三段 π/2 归约：|r| <= π/4；q = round(x*2/π)
  const __m256d two_over_pi = _mm256_set1_pd(0.63661977236758134307553505349006);
  __m256d q = _mm256_round_pd(_mm256_mul_pd(x, two_over_pi), _MM_FROUND_TO_NEAREST_INT | _MM_FROUND_NO_EXC);
  __m256d r = _mm256_fnmadd_pd(q, _mm256_set1_pd(1.57079632673412561417e+00), x);
  r = _mm256_fnmadd_pd(q, _mm256_set1_pd(6.07710050650619224932e-11), r);
  r = _mm256_fnmadd_pd(q, _mm256_set1_pd(2.02226624879595063154e-21), r);
  *rOut = r;
  *qOut = _mm256_cvtpd_epi32(q);
}

static N_FORCEINLINE __m256d _n_sin_poly4_pd(__m256d r) {
  // sin(r) = r + r^3*(c1 + c2 z + ...)，z = r^2，|r| <= π/4
  __m256d z = _mm256_mul_pd(r, r);
  __m256d p = _mm256_set1_pd(-2.50521083854417187751e-8);            // c5 = -1/39916800
  p = _mm256_fmadd_pd(p, z, _mm256_set1_pd(2.75573192239858925109e-6));   // c4 = 1/362880
  p = _mm256_fmadd_pd(p, z, _mm256_set1_pd(-1.98412698412698412522e-4));  // c3 = -1/5040
  p = _mm256_fmadd_pd(p, z, _mm256_set1_pd(8.33333333333333321769e-3));   // c2 = 1/120
  p = _mm256_fmadd_pd(p, z, _mm256_set1_pd(-1.66666666666666657415e-1));  // c1 = -1/6
  return _mm256_fmadd_pd(_mm256_mul_pd(z, r), p, r);
}
static N_FORCEINLINE __m256d _n_cos_poly4_pd(__m256d r) {
  __m256d z = _mm256_mul_pd(r, r);
  __m256d p = _mm256_set1_pd(2.4801587301587301587e-5);
  p = _mm256_fmadd_pd(p, z, _mm256_set1_pd(-1.3888888888888888889e-3));
  p = _mm256_fmadd_pd(p, z, _mm256_set1_pd(4.1666666666666666667e-2));
  p = _mm256_fmadd_pd(p, z, _mm256_set1_pd(-5.0e-1));
  return _mm256_fmadd_pd(p, z, _mm256_set1_pd(1.0));
}

// q mod 4，用于选择函数与符号
static N_FORCEINLINE __m256d _n_quad4_pd(__m128i q) {
  // q mod 4，落在 [0,3]（负 q 用 floor 除法，保证结果非负）
  __m256d qd = _mm256_cvtepi32_pd(q);
  __m256d q4 = _mm256_floor_pd(_mm256_mul_pd(qd, _mm256_set1_pd(0.25)));
  return _mm256_sub_pd(qd, _mm256_mul_pd(q4, _mm256_set1_pd(4.0)));
}

static N_FORCEINLINE __m256d _n_is_odd4_pd(__m256d m4) {
  return _mm256_cmp_pd(m4, _mm256_mul_pd(_mm256_floor_pd(_mm256_mul_pd(m4, _mm256_set1_pd(0.5))),
                                         _mm256_set1_pd(2.0)), _CMP_GT_OQ);
}
static N_FORCEINLINE __m256d _n_sin4_pd(__m256d x) {
  __m256d r; __m128i q;
  _n_reduce4_pd(x, &r, &q);
  __m256d m4 = _n_quad4_pd(q);
  __m256d odd = _n_is_odd4_pd(m4);
  __m256d u = _mm256_blendv_pd(_n_sin_poly4_pd(r), _n_cos_poly4_pd(r), odd);
  return _mm256_blendv_pd(u, _mm256_sub_pd(_mm256_set1_pd(0.0), u),
                          _mm256_cmp_pd(m4, _mm256_set1_pd(2.0), _CMP_GE_OQ));
}
static N_FORCEINLINE __m256d _n_cos4_pd(__m256d x) {
  __m256d r; __m128i q;
  _n_reduce4_pd(x, &r, &q);
  __m256d m4 = _n_quad4_pd(q);
  __m256d odd = _n_is_odd4_pd(m4);
  __m256d u = _mm256_blendv_pd(_n_cos_poly4_pd(r), _n_sin_poly4_pd(r), odd);
  __m256d neg = _mm256_and_pd(_mm256_cmp_pd(m4, _mm256_set1_pd(1.0), _CMP_GE_OQ),
                              _mm256_cmp_pd(m4, _mm256_set1_pd(3.0), _CMP_LT_OQ));
  return _mm256_blendv_pd(u, _mm256_sub_pd(_mm256_set1_pd(0.0), u), neg);
}static N_FORCEINLINE __m256d _n_atan4_pd(__m256d x) {
  // |x| <= tan(pi/8) 用奇多项式；|x| > 1 先取倒数；中间区间用 (x-1)/(x+1) 折到 pi/4 邻域
  __m256d absMask = _mm256_castsi256_pd(_mm256_set1_epi64x(0x7fffffffffffffffLL));
  __m256d ax = _mm256_and_pd(x, absMask);
  const __m256d t8 = _mm256_set1_pd(0.41421356237309503);
  const __m256d one = _mm256_set1_pd(1.0);
  const __m256d pi_4 = _mm256_set1_pd(0.78539816339744830962);
  const __m256d pi_2 = _mm256_set1_pd(1.5707963267948966192);

  __m256d big = _mm256_cmp_pd(ax, one, _CMP_GT_OQ);
  __m256d t = _mm256_blendv_pd(ax, _mm256_div_pd(one, ax), big);       // t <= 1
  __m256d mid = _mm256_cmp_pd(t, t8, _CMP_GT_OQ);
  __m256d u = _mm256_blendv_pd(t, _mm256_div_pd(_mm256_sub_pd(t, one), _mm256_add_pd(t, one)), mid);

  __m256d z = _mm256_mul_pd(u, u);
  __m256d q = _mm256_set1_pd(0.0050311800327567501);
  q = _mm256_fmadd_pd(q, z, _mm256_set1_pd(0.012939895549208824));
  q = _mm256_fmadd_pd(q, z, _mm256_set1_pd(0.022542847412336964));
  q = _mm256_fmadd_pd(q, z, _mm256_set1_pd(0.0084661109042136508));
  q = _mm256_fmadd_pd(q, z, _mm256_set1_pd(-0.051163910574811677));
  q = _mm256_fmadd_pd(q, z, _mm256_set1_pd(0.074302501400929838));
  q = _mm256_fmadd_pd(q, z, _mm256_set1_pd(-0.090650342969366443));
  q = _mm256_fmadd_pd(q, z, _mm256_set1_pd(0.11109618177625467));
  q = _mm256_fmadd_pd(q, z, _mm256_set1_pd(-0.14285666574105049));
  q = _mm256_fmadd_pd(q, z, _mm256_set1_pd(0.19999999262220255));
  q = _mm256_fmadd_pd(q, z, _mm256_set1_pd(-0.33333333329431702));
  __m256d r = _mm256_fmadd_pd(_mm256_mul_pd(u, z), q, u);
  r = _mm256_add_pd(r, _mm256_and_pd(mid, pi_4));                       // 中间区间补 pi/4
  r = _mm256_blendv_pd(r, _mm256_sub_pd(pi_2, r), big);                 // |x|>1: pi/2 - atan(1/x)
  return _mm256_blendv_pd(_mm256_sub_pd(_mm256_set1_pd(0.0), r), r,
                          _mm256_cmp_pd(x, _mm256_set1_pd(0.0), _CMP_GT_OQ));
}

static N_FORCEINLINE __m256d _n_atan2_4_pd(__m256d y, __m256d x) {
  const __m256d zero = _mm256_set1_pd(0.0);
  const __m256d pi = _mm256_set1_pd(3.14159265358979323846);
  __m256d r = _n_atan4_pd(_mm256_div_pd(y, x));
  __m256d adj = _mm256_blendv_pd(_mm256_sub_pd(zero, pi), pi, _mm256_cmp_pd(y, zero, _CMP_GE_OQ));
  r = _mm256_blendv_pd(r, _mm256_add_pd(r, adj), _mm256_cmp_pd(x, zero, _CMP_LT_OQ));
  return _mm256_blendv_pd(r, zero, _mm256_and_pd(_mm256_cmp_pd(x, zero, _CMP_EQ_OQ),
                                                 _mm256_cmp_pd(y, zero, _CMP_EQ_OQ)));
}

static N_FORCEINLINE __m256d _n_exp4_pd(__m256d x) {
  // 复用 pow 路径里的 exp2：exp(x) = 2^(x/ln2)
  return _n_exp2_pd(_mm256_mul_pd(x, _mm256_set1_pd(1.4426950408889634)));
}

static N_FORCEINLINE __m256d _n_tan4_pd(__m256d x) {
  return _mm256_div_pd(_n_sin4_pd(x), _n_cos4_pd(x));
}

static N_FORCEINLINE __m256d _n_asin4_pd(__m256d x) {
  __m256d one = _mm256_set1_pd(1.0);
  __m256d t = _mm256_sqrt_pd(_mm256_max_pd(_mm256_mul_pd(_mm256_sub_pd(one, x), _mm256_add_pd(one, x)),
                                           _mm256_set1_pd(0.0)));
  return _mm256_mul_pd(_mm256_set1_pd(2.0), _n_atan2_4_pd(x, _mm256_add_pd(one, t)));
}

static N_FORCEINLINE __m256d _n_acos4_pd(__m256d x) {
  __m256d one = _mm256_set1_pd(1.0);
  __m256d t = _mm256_sqrt_pd(_mm256_max_pd(_mm256_mul_pd(_mm256_sub_pd(one, x), _mm256_add_pd(one, x)),
                                           _mm256_set1_pd(0.0)));
  __m256d r = _mm256_mul_pd(_mm256_set1_pd(2.0), _n_atan2_4_pd(t, _mm256_add_pd(one, x)));
  return _mm256_blendv_pd(r, _mm256_set1_pd(3.14159265358979323846),
                          _mm256_cmp_pd(x, _mm256_set1_pd(-1.0), _CMP_LE_OQ));
}

static N_FORCEINLINE __m256d _n_sinh4_pd(__m256d x) {
  __m256d e = _n_exp4_pd(x);
  __m256d inv = _n_exp4_pd(_mm256_sub_pd(_mm256_set1_pd(0.0), x));
  return _mm256_mul_pd(_mm256_set1_pd(0.5), _mm256_sub_pd(e, inv));
}

static N_FORCEINLINE __m256d _n_cosh4_pd(__m256d x) {
  __m256d e = _n_exp4_pd(x);
  __m256d inv = _n_exp4_pd(_mm256_sub_pd(_mm256_set1_pd(0.0), x));
  return _mm256_mul_pd(_mm256_set1_pd(0.5), _mm256_add_pd(e, inv));
}

static N_FORCEINLINE __m256d _n_tanh4_pd(__m256d x) {
  __m256d e2 = _n_exp4_pd(_mm256_mul_pd(_mm256_set1_pd(2.0), x));
  return _mm256_div_pd(_mm256_sub_pd(e2, _mm256_set1_pd(1.0)), _mm256_add_pd(e2, _mm256_set1_pd(1.0)));
}

// ---- High 档对外入口：8 个 float lane 拆两组 double 计算后折回 ----
static N_FORCEINLINE __m256 _n_high_map(__m256 x, __m256d (*f)(__m256d)) {
  __m128 lo = _mm256_cvtpd_ps(f(_mm256_cvtps_pd(_mm256_castps256_ps128(x))));
  __m128 hi = _mm256_cvtpd_ps(f(_mm256_cvtps_pd(_mm256_extractf128_ps(x, 1))));
  return _mm256_insertf128_ps(_mm256_castps128_ps256(lo), hi, 1);
}

static N_FORCEINLINE __m256 _n_high_sin(__m256 x)  { return _n_high_map(x, _n_sin4_pd); }
static N_FORCEINLINE __m256 _n_high_cos(__m256 x)  { return _n_high_map(x, _n_cos4_pd); }
static N_FORCEINLINE __m256 _n_high_tan(__m256 x)  { return _n_high_map(x, _n_tan4_pd); }
static N_FORCEINLINE __m256 _n_high_atan(__m256 x) { return _n_high_map(x, _n_atan4_pd); }
static N_FORCEINLINE __m256 _n_high_asin(__m256 x) { return _n_high_map(x, _n_asin4_pd); }
static N_FORCEINLINE __m256 _n_high_acos(__m256 x) { return _n_high_map(x, _n_acos4_pd); }
static N_FORCEINLINE __m256 _n_high_sinh(__m256 x) { return _n_high_map(x, _n_sinh4_pd); }
static N_FORCEINLINE __m256 _n_high_cosh(__m256 x) { return _n_high_map(x, _n_cosh4_pd); }
static N_FORCEINLINE __m256 _n_high_tanh(__m256 x) { return _n_high_map(x, _n_tanh4_pd); }
static N_FORCEINLINE __m256 _n_high_exp(__m256 x)  { return _n_high_map(x, _n_exp4_pd); }

static N_FORCEINLINE __m256 _n_high_log(__m256 x) {
  // 指数与尾数在 float 域取出（精确），log(m) 在 double 算
  __m256 ax = _mm256_and_ps(x, _mm256_castsi256_ps(_mm256_set1_epi32(0x7fffffff)));
  __m256i ei = _mm256_sub_epi32(_mm256_srli_epi32(_mm256_castps_si256(ax), 23), _mm256_set1_epi32(127));
  __m256 mf = _mm256_castsi256_ps(_mm256_or_si256(_mm256_and_si256(_mm256_castps_si256(x), _mm256_set1_epi32(0x807fffff)),
                                                   _mm256_set1_epi32(0x3f800000)));
  const __m256d ln2 = _mm256_set1_pd(0.69314718055994530942);
  __m256d lo = _mm256_add_pd(_mm256_mul_pd(_mm256_cvtepi32_pd(_mm256_castsi256_si128(ei)), ln2),
                             _n_log_mant_pd(_mm256_cvtps_pd(_mm256_castps256_ps128(mf))));
  __m256d hi = _mm256_add_pd(_mm256_mul_pd(_mm256_cvtepi32_pd(_mm256_extracti128_si256(ei, 1)), ln2),
                             _n_log_mant_pd(_mm256_cvtps_pd(_mm256_extractf128_ps(mf, 1))));
  __m128 rl = _mm256_cvtpd_ps(lo), rh = _mm256_cvtpd_ps(hi);
  return _mm256_insertf128_ps(_mm256_castps128_ps256(rl), rh, 1);
}

static N_FORCEINLINE __m256 _n_high_log10(__m256 x) {
  return _mm256_mul_ps(_n_high_log(x), _mm256_set1_ps(0.4342944819032518f));
}

static N_FORCEINLINE __m256 _n_high_atan2(__m256 y, __m256 x) {
  __m128 lo = _mm256_cvtpd_ps(_n_atan2_4_pd(_mm256_cvtps_pd(_mm256_castps256_ps128(y)),
                                            _mm256_cvtps_pd(_mm256_castps256_ps128(x))));
  __m128 hi = _mm256_cvtpd_ps(_n_atan2_4_pd(_mm256_cvtps_pd(_mm256_extractf128_ps(y, 1)),
                                            _mm256_cvtps_pd(_mm256_extractf128_ps(x, 1))));
  return _mm256_insertf128_ps(_mm256_castps128_ps256(lo), hi, 1);
}

static N_FORCEINLINE void _n_high_sincos(__m256 x, __m256* s, __m256* c) {
  *s = _n_high_sin(x);
  *c = _n_high_cos(x);
}

#endif // NSIMD_AVX2 || NSIMD_AVX512

// ================================================================
// AVX512 16-wide SLEEF-style polynomials (translated from _n_sin_avx2 etc.)
// ================================================================
#if defined(NSIMD_AVX512)

// AVX512 复用已验证的 AVX2 内核：16 lane 拆两组 8 lane。
// 512 位只用于访存与算术组合，数学实现保持单份，避免两套代码精度分叉。
static N_FORCEINLINE n_float _n_sin_avx512(n_float d) {
  __m256 lo = _n_sin_avx2(_mm512_castps512_ps256(d));
  __m256 hi = _n_sin_avx2(_mm512_extractf32x8_ps(d, 1));
  return _mm512_insertf32x8(_mm512_castps256_ps512(lo), hi, 1);
}

static N_FORCEINLINE n_float _n_cos_avx512(n_float d) {
  __m256 lo = _n_cos_avx2(_mm512_castps512_ps256(d));
  __m256 hi = _n_cos_avx2(_mm512_extractf32x8_ps(d, 1));
  return _mm512_insertf32x8(_mm512_castps256_ps512(lo), hi, 1);
}

static N_FORCEINLINE n_float _n_log_avx512(n_float d) {
  __m256 lo = _n_log_avx2(_mm512_castps512_ps256(d));
  __m256 hi = _n_log_avx2(_mm512_extractf32x8_ps(d, 1));
  return _mm512_insertf32x8(_mm512_castps256_ps512(lo), hi, 1);
}

static N_FORCEINLINE n_float _n_exp_avx512(n_float d) {
  __m256 lo = _n_exp_avx2(_mm512_castps512_ps256(d));
  __m256 hi = _n_exp_avx2(_mm512_extractf32x8_ps(d, 1));
  return _mm512_insertf32x8(_mm512_castps256_ps512(lo), hi, 1);
}

static N_FORCEINLINE n_float _n_pow_avx512(n_float x, n_float y) {
  __m256 lo = _n_pow_avx2_ps(_mm512_castps512_ps256(x), _mm512_castps512_ps256(y));
  __m256 hi = _n_pow_avx2_ps(_mm512_extractf32x8_ps(x, 1), _mm512_extractf32x8_ps(y, 1));
  return _mm512_insertf32x8(_mm512_castps256_ps512(lo), hi, 1);
}
#endif // NSIMD_AVX512


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

  // Inline AVX512 (16-wide) / AVX2 (8-wide) polynomial (fastest, zero function-call overhead)
  #if defined(NSIMD_AVX512)
    #define N_SIN(a)     _n_sin_avx512(a)
    #define N_COS(a)     _n_cos_avx512(a)
    #define N_LOG(a)     _n_log_avx512(a)
    #define N_EXP(a)     _n_exp_avx512(a)
    #define N_LOG10(a)   _mm512_mul_ps(N_LOG(a), _mm512_set1_ps(0.4342944819032518f))
    #define N_POW(a,b)   _n_pow_avx512(a, b)
  #elif defined(NSIMD_AVX2)
    #define N_SIN(a)     _n_sin_avx2(a)
    #define N_COS(a)     _n_cos_avx2(a)
    #define N_LOG(a)     _n_log_avx2(a)
    #define N_EXP(a)     _n_exp_avx2(a)
    #define N_LOG10(a)   _mm256_mul_ps(N_LOG(a), _mm256_set1_ps(0.4342944819032518f))
  #endif
  // 组合函数复用上面的基础实现（定义在下方，宏在使用处展开，顺序无影响）：
  // tan/asin/acos/atan/atan2/pow 建立在 sin/cos/exp/log 之上，精度随之继承。
#if defined(NSIMD_AVX2) && !defined(NSIMD_AVX512)
    #define N_TAN(a)       _n_tan_ps(a)
    #define N_ASIN(a)      _n_asin_ps(a)
    #define N_ACOS(a)      _n_acos_ps(a)
    #define N_ATAN(a)      _n_atan_ps(a)
    #define N_ATAN2(a,b)   _n_atan2_ps(a, b)
    #define N_SINH(a)      _n_sinh_ps(a)
    #define N_COSH(a)      _n_cosh_ps(a)
    #define N_TANH(a)      _n_tanh_ps(a)
#if !defined(NSIMD_AVX512)
#define N_POW(a,b)     _n_pow_avx2_ps(a, b)
#endif
    #define N_SINCOS(a,s,c) _n_sincos_ps(a, s, c)
  #endif
  // 非 AVX2 平台：Sleef 已移除，由下方 #ifndef N_* 守卫的逐通道标量回退覆盖。
  // Non-AVX2: #ifndef guards below provide per-lane scalar fallback
  // AVX512 暂缺 exp 内联实现，同样走标量回退。

// ===== High (~1.0 ULP) =====
#elif SIMD_MATH_PRECISION == 2
  // double 中间精度实现（AVX2）；其它平台由 #ifndef 守卫回退到逐 lane libm
  #if defined(NSIMD_AVX2)
    #define N_SIN(a)       _n_high_sin(a)
    #define N_COS(a)       _n_high_cos(a)
    #define N_TAN(a)       _n_high_tan(a)
    #define N_ASIN(a)      _n_high_asin(a)
    #define N_ACOS(a)      _n_high_acos(a)
    #define N_ATAN(a)      _n_high_atan(a)
    #define N_ATAN2(a,b)   _n_high_atan2(a, b)
    #define N_SINH(a)      _n_high_sinh(a)
    #define N_COSH(a)      _n_high_cosh(a)
    #define N_TANH(a)      _n_high_tanh(a)
    #define N_EXP(a)       _n_high_exp(a)
    #define N_LOG(a)       _n_high_log(a)
    #define N_LOG10(a)     _n_high_log10(a)
#if !defined(NSIMD_AVX512)
#define N_POW(a,b)     _n_pow_avx2_ps(a, b)
#endif
    #define N_SINCOS(a,s,c) _n_high_sincos(a, s, c)
  #endif

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
static N_FORCEINLINE void _n_sincos_fallback(n_float a, n_float* s, n_float* c) {
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
static N_FORCEINLINE n_float _n_sin_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::sinf(lane[i]);
  return n_load_ps(lane);
}
#define N_SIN(a) _n_sin_fallback(a)
#endif

#ifndef N_COS
static N_FORCEINLINE n_float _n_cos_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::cosf(lane[i]);
  return n_load_ps(lane);
}
#define N_COS(a) _n_cos_fallback(a)
#endif

#ifndef N_TAN
static N_FORCEINLINE n_float _n_tan_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::tanf(lane[i]);
  return n_load_ps(lane);
}
#define N_TAN(a) _n_tan_fallback(a)
#endif

#ifndef N_ASIN
static N_FORCEINLINE n_float _n_asin_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::asinf(lane[i]);
  return n_load_ps(lane);
}
#define N_ASIN(a) _n_asin_fallback(a)
#endif

#ifndef N_ACOS
static N_FORCEINLINE n_float _n_acos_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::acosf(lane[i]);
  return n_load_ps(lane);
}
#define N_ACOS(a) _n_acos_fallback(a)
#endif

#ifndef N_ATAN
static N_FORCEINLINE n_float _n_atan_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::atanf(lane[i]);
  return n_load_ps(lane);
}
#define N_ATAN(a) _n_atan_fallback(a)
#endif

#ifndef N_ATAN2
static N_FORCEINLINE n_float _n_atan2_fallback(n_float a, n_float b) {
  float la[NSIMD_WIDTH], lb[NSIMD_WIDTH];
  n_store_ps(la, a); n_store_ps(lb, b);
  for (int i = 0; i < NSIMD_WIDTH; i++) la[i] = ::atan2f(la[i], lb[i]);
  return n_load_ps(la);
}
#define N_ATAN2(a,b) _n_atan2_fallback(a,b)
#endif

#ifndef N_SINH
static N_FORCEINLINE n_float _n_sinh_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::sinhf(lane[i]);
  return n_load_ps(lane);
}
#define N_SINH(a) _n_sinh_fallback(a)
#endif

#ifndef N_COSH
static N_FORCEINLINE n_float _n_cosh_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::coshf(lane[i]);
  return n_load_ps(lane);
}
#define N_COSH(a) _n_cosh_fallback(a)
#endif

#ifndef N_TANH
static N_FORCEINLINE n_float _n_tanh_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::tanhf(lane[i]);
  return n_load_ps(lane);
}
#define N_TANH(a) _n_tanh_fallback(a)
#endif

#ifndef N_EXP
static N_FORCEINLINE n_float _n_exp_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::expf(lane[i]);
  return n_load_ps(lane);
}
#define N_EXP(a) _n_exp_fallback(a)
#endif

#ifndef N_LOG
static N_FORCEINLINE n_float _n_log_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::logf(lane[i]);
  return n_load_ps(lane);
}
#define N_LOG(a) _n_log_fallback(a)
#endif

#ifndef N_LOG10
static N_FORCEINLINE n_float _n_log10_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::log10f(lane[i]);
  return n_load_ps(lane);
}
#define N_LOG10(a) _n_log10_fallback(a)
#endif

#ifndef N_POW
static N_FORCEINLINE n_float _n_pow_fallback(n_float a, n_float b) {
  float la[NSIMD_WIDTH], lb[NSIMD_WIDTH];
  n_store_ps(la, a); n_store_ps(lb, b);
  for (int i = 0; i < NSIMD_WIDTH; i++) la[i] = ::powf(la[i], lb[i]);
  return n_load_ps(la);
}
#define N_POW(a,b) _n_pow_fallback(a,b)
#endif

// ================================================================
// Public API: n_sin_ps / n_cos_ps / ... (defined as macros to eliminate function call
// boundary entirely — matches ISPC LLVM IR pattern where sin/cos/sqrt polynomials
// are direct intrinsics in the main basic block, with no call/ret overhead)
// These work with n_float (__m256 / __m128 / float32x4_t / float)
// ================================================================

#define n_sin_ps(a)     N_SIN(a)
#define n_cos_ps(a)     N_COS(a)
#define n_tan_ps(a)     N_TAN(a)
#define n_asin_ps(a)    N_ASIN(a)
#define n_acos_ps(a)    N_ACOS(a)
#define n_atan_ps(a)    N_ATAN(a)
#define n_atan2_ps(a,b) N_ATAN2(a,b)
#define n_sinh_ps(a)    N_SINH(a)
#define n_cosh_ps(a)    N_COSH(a)
#define n_tanh_ps(a)    N_TANH(a)
#define n_exp_ps(a)     N_EXP(a)
#define n_log_ps(a)     N_LOG(a)
#define n_log10_ps(a)   N_LOG10(a)
#define n_pow_ps(a,b)   N_POW(a,b)
#define n_sincos_ps(a,s,c) N_SINCOS(a,s,c)
// ================================================================
// 标量路径的数学入口：与向量体共用同一套实现
// 生成代码的标量段（尾部元素、未向量化的 job）调用 EJ_*，由本 TU 的
// SIMD_MATH_PRECISION 决定用内联多项式还是 libm，避免同一 job 内两套精度。
// ================================================================
#if SIMD_MATH_PRECISION == 3
  #define EJ_SINF(a)    ::sinf(a)
  #define EJ_COSF(a)    ::cosf(a)
  #define EJ_TANF(a)    ::tanf(a)
  #define EJ_ASINF(a)   ::asinf(a)
  #define EJ_ACOSF(a)   ::acosf(a)
  #define EJ_ATANF(a)   ::atanf(a)
  #define EJ_ATAN2F(a,b) ::atan2f(a, b)
  #define EJ_SINHF(a)   ::sinhf(a)
  #define EJ_COSHF(a)   ::coshf(a)
  #define EJ_TANHF(a)   ::tanhf(a)
  #define EJ_EXPF(a)    ::expf(a)
  #define EJ_LOGF(a)    ::logf(a)
  #define EJ_LOG10F(a)  ::log10f(a)
  #define EJ_POWF(a,b)  ::powf(a, b)
#else
  static N_FORCEINLINE float _n_lane0_f(n_float v) {
    alignas(32) float t[NSIMD_WIDTH];
    n_store_ps(t, v);
    return t[0];
  }
  #define EJ_SINF(a)    _n_lane0_f(n_sin_ps(n_set1_ps(a)))
  #define EJ_COSF(a)    _n_lane0_f(n_cos_ps(n_set1_ps(a)))
  #define EJ_TANF(a)    _n_lane0_f(n_tan_ps(n_set1_ps(a)))
  #define EJ_ASINF(a)   _n_lane0_f(n_asin_ps(n_set1_ps(a)))
  #define EJ_ACOSF(a)   _n_lane0_f(n_acos_ps(n_set1_ps(a)))
  #define EJ_ATANF(a)   _n_lane0_f(n_atan_ps(n_set1_ps(a)))
  #define EJ_ATAN2F(a,b) _n_lane0_f(n_atan2_ps(n_set1_ps(a), n_set1_ps(b)))
  #define EJ_SINHF(a)   _n_lane0_f(n_sinh_ps(n_set1_ps(a)))
  #define EJ_COSHF(a)   _n_lane0_f(n_cosh_ps(n_set1_ps(a)))
  #define EJ_TANHF(a)   _n_lane0_f(n_tanh_ps(n_set1_ps(a)))
  #define EJ_EXPF(a)    _n_lane0_f(n_exp_ps(n_set1_ps(a)))
  #define EJ_LOGF(a)    _n_lane0_f(n_log_ps(n_set1_ps(a)))
  #define EJ_LOG10F(a)  _n_lane0_f(n_log10_ps(n_set1_ps(a)))
  #define EJ_POWF(a,b)  _n_lane0_f(n_pow_ps(n_set1_ps(a), n_set1_ps(b)))
#endif
// ================================================================
#if defined(NSIMD_AVX2) && !defined(NSIMD_AVX512)   // 组合实现用 mask 做 blend，AVX512 的 mask 类型不兼容
// Cross-platform inline math (via n_xxx_ps abstraction — all platforms)
// These work on AVX2, SSE4, NEON — n_xxx_ps maps to the right intrinsic.
// ================================================================

static N_FORCEINLINE n_float _n_tan_ps(n_float a) {
  return n_div_ps(n_sin_ps(a), n_cos_ps(a));
}

static N_FORCEINLINE n_float _n_atan_ps(n_float a);

static N_FORCEINLINE n_float _n_atan2_ps(n_float y, n_float x) {
  // x = 0 由 y/x = ±inf 经 atan 的 |x|>1 分支自然得到 ±pi/2；只需补 atan2(0,0) = 0。
  n_float zero = n_set1_ps(0.0f), pi = n_set1_ps(3.1415926535897932f);
  n_float r = _n_atan_ps(n_div_ps(y, x));
  n_float adj = n_blendv_ps(n_sub_ps(zero, pi), pi, n_cmp_ge_ps(y, zero));
  r = n_blendv_ps(r, n_add_ps(r, adj), n_cmp_lt_ps(x, zero));
  return n_blendv_ps(r, zero, n_and_mask(n_cmp_eq_ps(x, zero), n_cmp_eq_ps(y, zero)));
}
static N_FORCEINLINE n_float _n_asin_ps(n_float a) {
  // asin(x) = 2*atan2(x, 1 + sqrt(1 - x^2))；1 - x^2 用 (1-x)(1+x) 计算，|x| -> 1 处不丢精度
  n_float one = n_set1_ps(1.0f);
  n_float t = n_sqrt_ps(n_max_ps(n_mul_ps(n_sub_ps(one, a), n_add_ps(one, a)), n_set1_ps(0.0f)));
  return n_mul_ps(n_set1_ps(2.0f), _n_atan2_ps(a, n_add_ps(one, t)));
}
static N_FORCEINLINE n_float _n_acos_ps(n_float a) {
  // acos(x) = 2*atan2(sqrt(1 - x^2), 1 + x)；x = -1 时两项都为 0，单独补 pi
  n_float one = n_set1_ps(1.0f);
  n_float t = n_sqrt_ps(n_max_ps(n_mul_ps(n_sub_ps(one, a), n_add_ps(one, a)), n_set1_ps(0.0f)));
  n_float r = n_mul_ps(n_set1_ps(2.0f), _n_atan2_ps(t, n_add_ps(one, a)));
  return n_blendv_ps(r, n_set1_ps(3.1415926535897932f), n_cmp_le_ps(a, n_set1_ps(-1.0f)));
}static N_FORCEINLINE n_float _n_atan_ps(n_float a) {
  // atan(x) = x + x^3*q(x^2) 在 [0,1] 上的相对 minimax 拟合（max rel err 1.8e-8）；
  // |x|>1 走 atan(x) = pi/2 - atan(1/x)。
  n_float one = n_set1_ps(1.0f), pi_2 = n_set1_ps(1.5707963267948966f);
  n_float ax = n_fabs_ps(a);
  n_float big = n_cmp_gt_ps(ax, one);
  n_float t = n_blendv_ps(ax, n_div_ps(one, ax), big);
  n_float z = n_mul_ps(t, t);
  n_float q = n_set1_ps(2.922975910e-03f);
  q = n_fmadd_ps(q, z, n_set1_ps(-1.637927326e-02f));
  q = n_fmadd_ps(q, z, n_set1_ps(4.323442722e-02f));
  q = n_fmadd_ps(q, z, n_set1_ps(-7.554515826e-02f));
  q = n_fmadd_ps(q, z, n_set1_ps(1.066727714e-01f));
  q = n_fmadd_ps(q, z, n_set1_ps(-1.421142270e-01f));
  q = n_fmadd_ps(q, z, n_set1_ps(1.999382071e-01f));
  q = n_fmadd_ps(q, z, n_set1_ps(-3.333315467e-01f));
  n_float r = n_fmadd_ps(n_mul_ps(t, z), q, t);
  r = n_blendv_ps(r, n_sub_ps(pi_2, r), big);
  return n_blendv_ps(n_sub_ps(n_set1_ps(0.0f), r), r, n_cmp_gt_ps(a, n_set1_ps(0.0f)));
}
static N_FORCEINLINE n_float _n_sinh_ps(n_float a) {
  // 小 |x| 下 e^x - e^-x 有相消误差，改用 Taylor（到 x^7）；阈值 0.25 两侧误差同量级
  n_float e = n_exp_ps(a);
  n_float wide = n_mul_ps(n_set1_ps(0.5f), n_sub_ps(e, n_exp_ps(n_mul_ps(n_set1_ps(-1.0f), a))));
  n_float x2 = n_mul_ps(a, a);
  n_float s = n_fmadd_ps(x2, n_set1_ps(1.0f / 5040.0f), n_set1_ps(1.0f / 120.0f));
  s = n_fmadd_ps(x2, s, n_set1_ps(1.0f / 6.0f));
  n_float series = n_mul_ps(a, n_fmadd_ps(x2, s, n_set1_ps(1.0f)));
  return n_blendv_ps(wide, series, n_cmp_lt_ps(n_fabs_ps(a), n_set1_ps(0.25f)));
}
static N_FORCEINLINE n_float _n_cosh_ps(n_float a) {
  n_float e = n_exp_ps(a);
  n_float wide = n_mul_ps(n_set1_ps(0.5f), n_add_ps(e, n_exp_ps(n_mul_ps(n_set1_ps(-1.0f), a))));
  n_float x2 = n_mul_ps(a, a);
  n_float s = n_fmadd_ps(x2, n_set1_ps(1.0f / 720.0f), n_set1_ps(1.0f / 24.0f));
  s = n_fmadd_ps(x2, s, n_set1_ps(0.5f));
  n_float series = n_fmadd_ps(x2, s, n_set1_ps(1.0f));   // 1 + x^2/2 + x^4/24 + x^6/720
  return n_blendv_ps(wide, series, n_cmp_lt_ps(n_fabs_ps(a), n_set1_ps(0.25f)));
}static N_FORCEINLINE n_float _n_tanh_ps(n_float a) {
  // 小 |x| 下 e^2x - 1 相消，用 x - x^3/3 + 2x^5/15 - 17x^7/315 + 62x^9/2835
  n_float e2 = n_exp_ps(n_mul_ps(n_set1_ps(2.0f), a));
  n_float wide = n_div_ps(n_sub_ps(e2, n_set1_ps(1.0f)), n_add_ps(e2, n_set1_ps(1.0f)));
  n_float x2 = n_mul_ps(a, a);
  n_float s = n_set1_ps(62.0f / 2835.0f);
  s = n_fmadd_ps(x2, s, n_set1_ps(-17.0f / 315.0f));
  s = n_fmadd_ps(x2, s, n_set1_ps(2.0f / 15.0f));
  s = n_fmadd_ps(x2, s, n_set1_ps(-1.0f / 3.0f));
  n_float series = n_mul_ps(a, n_fmadd_ps(x2, s, n_set1_ps(1.0f)));
  return n_blendv_ps(wide, series, n_cmp_lt_ps(n_fabs_ps(a), n_set1_ps(0.25f)));
}static N_FORCEINLINE void _n_sincos_ps(n_float a, n_float* s, n_float* c) {
  *s = n_sin_ps(a);
  *c = n_cos_ps(a);
}

static N_FORCEINLINE n_float _n_pow_ps(n_float x, n_float y) {
  // MathF.Pow 语义：正底数走 exp(y*log x)；负底数只在 y 为整数时成立，奇数指数取负；
  // 底数为 0 由 log(0) = -inf 经 exp 自然落到 0 / +inf；非整数指数的负底数与 NaN 输入返回 NaN。
  n_float ax = n_fabs_ps(x);
  n_float mag = n_exp_ps(n_mul_ps(y, n_log_ps(ax)));

  // y 是否为整数：y == trunc(y) 且 |y| < 2^23（float 在此范围内整数可精确表示）
  n_float ay = n_fabs_ps(y);
  n_mask yInt = n_and_mask(n_cmp_eq_ps(y, n_trunc_ps(y)), n_cmp_lt_ps(ay, n_set1_ps(8388608.0f)));
  // 奇数判定：y/2 的截断乘 2 与 y 不等即为奇数
  n_float half = n_trunc_ps(n_mul_ps(y, n_set1_ps(0.5f)));
  n_mask yOdd = n_cmp_ne_ps(n_mul_ps(half, n_set1_ps(2.0f)), y);
  n_mask negate = n_and_mask(yInt, yOdd);

  n_float sign = n_blendv_ps(n_set1_ps(1.0f), n_set1_ps(-1.0f),
                             n_and_mask(n_cmp_lt_ps(x, n_set1_ps(0.0f)), negate));
  n_float result = n_mul_ps(mag, sign);

  // 负底数 + 非整数指数 ⇒ NaN
  result = n_blendv_ps(result, n_set1_ps(NAN),
                       n_and_mask(n_cmp_lt_ps(x, n_set1_ps(0.0f)), n_not_mask(yInt)));
  // pow(x, 0) = 1（含 NaN 底数），与 MathF.Pow 一致
  result = n_blendv_ps(result, n_set1_ps(1.0f), n_cmp_eq_ps(y, n_set1_ps(0.0f)));
  // x = 0 且 y < 0 ⇒ +inf；x = 0 且 y > 0 ⇒ 0
  n_mask xZero = n_cmp_eq_ps(x, n_set1_ps(0.0f));
  result = n_blendv_ps(result, n_set1_ps(INFINITY),
                       n_and_mask(xZero, n_cmp_lt_ps(y, n_set1_ps(0.0f))));
  result = n_blendv_ps(result, n_set1_ps(0.0f),
                       n_and_mask(xZero, n_cmp_gt_ps(y, n_set1_ps(0.0f))));
  // NaN 传播（底数或指数任一为 NaN ⇒ NaN，但 pow(NaN, 0) = 1 已在上方处理）
  result = n_blendv_ps(result, n_set1_ps(NAN),
                       n_or_mask(n_cmp_ne_ps(x, x), n_cmp_ne_ps(y, y)));
  return result;
}

// ================================================================
#endif // 组合实现（AVX2 only）
// Double-precision SLEEF (n_*_pd)
// ================================================================

// Double precision sin — inline AVX2 polynomial (SLEEF-derived)
#if defined(NSIMD_AVX2)
// _PIO2 constants for double
#define _N_PIO2_A_PD -1.5707963050
#define _N_PIO2_B_PD -4.37113883e-8

static N_FORCEINLINE n_double _n_sin_avx2_pd(n_double d) {
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

#elif defined(NSIMD_AVX512)
// AVX512 double sin/cos：逐 lane 标量（正确性优先，避免重写 8-wide 多项式）
static N_FORCEINLINE n_double _n_sin_pd_avx512(n_double a) {
    double lane[8]; int w = 8;
    n_store_pd(lane, a);
    for (int i = 0; i < w; i++) lane[i] = sin(lane[i]);
    return n_load_pd(lane);
}
static N_FORCEINLINE n_double _n_cos_pd_avx512(n_double a) {
    double lane[8]; int w = 8;
    n_store_pd(lane, a);
    for (int i = 0; i < w; i++) lane[i] = cos(lane[i]);
    return n_load_pd(lane);
}
#define N_SIN_PD(a) _n_sin_pd_avx512(a)
#define N_COS_PD(a) _n_cos_pd_avx512(a)

#elif defined(NSIMD_SSE4) && defined(__aarch64__)
// SSE4: use scalar per-lane for each double lane (only 2-wide anyway, scalar is fine)
#define N_SIN_PD(a) _n_sin_pd_fallback(a)
#else
// Fallback: per-lane scalar sin/cos
static N_FORCEINLINE n_double _n_sin_pd_fallback(n_double a) {
    double lane[8]; int w = NSIMD_WIDTH < 8 ? NSIMD_WIDTH : 8;
    n_store_pd(lane, a);
    for (int i = 0; i < w; i++) lane[i] = sin(lane[i]);
    return n_load_pd(lane);
}
static N_FORCEINLINE n_double _n_cos_pd_fallback(n_double a) {
    double lane[8]; int w = NSIMD_WIDTH < 8 ? NSIMD_WIDTH : 8;
    n_store_pd(lane, a);
    for (int i = 0; i < w; i++) lane[i] = cos(lane[i]);
    return n_load_pd(lane);
}
#define N_SIN_PD(a) _n_sin_pd_fallback(a)
#define N_COS_PD(a) _n_cos_pd_fallback(a)
#endif

#ifndef N_COS_PD
static N_FORCEINLINE n_double _n_cos_pd_fallback(n_double a) {
    double lane[8]; int w = NSIMD_WIDTH < 8 ? NSIMD_WIDTH : 8;
    n_store_pd(lane, a);
    for (int i = 0; i < w; i++) lane[i] = cos(lane[i]);
    return n_load_pd(lane);
}
#define N_COS_PD(a) _n_cos_pd_fallback(a)
#endif

// Public API
static N_FORCEINLINE n_double n_sin_pd(n_double a)     { return N_SIN_PD(a); }
static N_FORCEINLINE n_double n_cos_pd(n_double a)     { return N_COS_PD(a); }
// sqrt_pd is already defined in NativeSIMD.h as n_sqrt_pd
