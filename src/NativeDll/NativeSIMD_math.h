// SIMD 数学内核：AVX2 / AVX512 / SSE4.1 / NEON 的内联实现，其余平台走逐 lane 标量回退。
// 精度档由 -DSIMD_MATH_PRECISION=1|2|3 选择：1 = Fastest（~3.5 ULP），2 = High（~1.0 ULP），3 = IEEE（逐 lane libm）。
// 定义域（NaN / ±inf / 0 / 负数）统一由 _n_dom_* helper 处理，见下方“定义域策略”一节。
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
// 定义域策略：所有入口在宏层统一套用，内核只算数学主体。
// 两类「看起来重复」的代码不要删：exp 的 ±溢出饱和与 tanh 的 ±88 夹取属区间语义
// （float 的 2^n 指数域限制），删掉会让 sinh/cosh 在 |x| 很大时算错。
//   exp  : +inf -> +inf，-inf -> 0，NaN 透传
//   log  : 0 -> -inf，负数 -> NaN，+inf -> +inf，NaN 透传
//   tanh : |x| 超过饱和点 -> 带符号的 1，NaN 透传
//   atan2: y = ±0 且 x > 0 时结果为带符号的零
// ================================================================
static N_FORCEINLINE n_float _n_dom_exp(n_float x, n_float r) {
  r = n_blendv_ps(r, n_set1_ps(INFINITY), n_cmp_eq_ps(x, n_set1_ps(INFINITY)));
  r = n_blendv_ps(r, n_set1_ps(0.0f), n_cmp_eq_ps(x, n_set1_ps(-INFINITY)));
  return n_blendv_ps(r, x, n_is_nan_ps(x));
}

static N_FORCEINLINE n_float _n_dom_log(n_float x, n_float r) {
  r = n_blendv_ps(r, n_set1_ps(-INFINITY), n_cmp_eq_ps(x, n_set1_ps(0.0f)));
  r = n_blendv_ps(r, n_set1_ps(NAN), n_cmp_lt_ps(x, n_set1_ps(0.0f)));
  r = n_blendv_ps(r, n_set1_ps(INFINITY), n_cmp_eq_ps(x, n_set1_ps(INFINITY)));
  return n_blendv_ps(r, x, n_is_nan_ps(x));
}

static N_FORCEINLINE n_float _n_dom_pow(n_float x, n_float y, n_float r) {
  // 特殊值按 IEEE：y = ±0 或 x = 1 -> 1；±inf 底数；零底数带符号
  const n_float inf = n_set1_ps(INFINITY), zero = n_set1_ps(0.0f), one = n_set1_ps(1.0f);
  n_mask yZero = n_cmp_eq_ps(y, zero), yPos = n_cmp_gt_ps(y, zero);
  n_mask xInf = n_cmp_eq_ps(n_fabs_ps(x), inf), xZero = n_cmp_eq_ps(x, zero);
  n_mask xNeg = n_or_mask(n_cmp_lt_ps(x, zero), n_cmp_lt_ps(n_div_ps(one, x), zero));
  n_float xSign = n_blendv_ps(one, n_set1_ps(-1.0f), xNeg);
  n_mask yInt = n_cmp_eq_ps(y, n_trunc_ps(y));
  n_mask yOdd = n_and_mask(n_cmp_ne_ps(n_mul_ps(n_trunc_ps(n_mul_ps(y, n_set1_ps(0.5f))), n_set1_ps(2.0f)), y),
                           n_cmp_lt_ps(n_fabs_ps(y), n_set1_ps(8388608.0f)));
  // 负底数：整数指数按奇偶给符号，非整数为 NaN（内核只算 |x|^y）
  r = n_mul_ps(r, n_blendv_ps(one, n_set1_ps(-1.0f), n_and_mask(xNeg, n_and_mask(yInt, yOdd))));
  r = n_blendv_ps(r, n_set1_ps(NAN), n_and_mask(xNeg, n_not_mask(yInt)));
  n_float magInf = n_blendv_ps(zero, inf, yPos);
  n_float magZero = n_blendv_ps(inf, zero, yPos);
  // -inf 底数：整数指数按奇偶给 ±inf / ±0，非整数为 NaN
  n_float negInf = n_mul_ps(xSign, n_blendv_ps(magInf, n_neg_ps(magInf), yOdd));
  r = n_blendv_ps(r, n_blendv_ps(n_set1_ps(NAN), negInf, yInt), n_and_mask(xInf, xNeg));
  r = n_blendv_ps(r, n_blendv_ps(magZero, n_neg_ps(magZero), n_and_mask(xNeg, yOdd)), xZero);
  r = n_blendv_ps(r, n_blendv_ps(zero, inf, yPos), n_and_mask(xInf, n_not_mask(xNeg)));
  r = n_blendv_ps(r, one, n_cmp_eq_ps(x, one));
  r = n_blendv_ps(r, one, yZero);
  return n_blendv_ps(r, n_set1_ps(NAN), n_or_mask(n_is_nan_ps(x), n_is_nan_ps(y)));
}
static N_FORCEINLINE n_float _n_dom_tanh(n_float x, n_float r) {
  n_float one = n_blendv_ps(n_set1_ps(1.0f), n_set1_ps(-1.0f), n_cmp_lt_ps(x, n_set1_ps(0.0f)));
  r = n_blendv_ps(r, one, n_cmp_gt_ps(n_fabs_ps(x), n_set1_ps(20.0f)));
  return n_blendv_ps(r, x, n_is_nan_ps(x));
}

static N_FORCEINLINE n_float _n_dom_atan2(n_float y, n_float x, n_float r) {
  // ±inf 组合与零：atan 内核只覆盖有限输入，这里补齐标准定义
  const n_float inf = n_set1_ps(INFINITY), zero = n_set1_ps(0.0f);
  const n_float quarter = n_set1_ps(0.7853981633974483f);
  const n_float half = n_set1_ps(1.5707963267948966f);
  const n_float pi = n_set1_ps(3.14159265358979323846f);
  n_mask yInf = n_cmp_eq_ps(n_fabs_ps(y), inf), xInf = n_cmp_eq_ps(n_fabs_ps(x), inf);
  n_float rx = n_div_ps(n_set1_ps(1.0f), x);
  n_mask yNeg = n_or_mask(n_cmp_lt_ps(y, zero), n_cmp_lt_ps(n_div_ps(n_set1_ps(1.0f), y), zero));
  n_mask xNeg = n_or_mask(n_cmp_lt_ps(x, zero), n_cmp_lt_ps(rx, zero));
  n_mask xPos = n_or_mask(n_cmp_gt_ps(x, zero), n_cmp_gt_ps(rx, zero));
  n_float ySign = n_blendv_ps(n_set1_ps(1.0f), n_set1_ps(-1.0f), yNeg);
  // y 无穷、x 有限：±π/2；两者都无穷：±π/4 或 ±3π/4；x 无穷、y 有限：±0 或 ±π
  r = n_blendv_ps(r, n_mul_ps(ySign, half), n_and_mask(yInf, n_not_mask(xInf)));
  r = n_blendv_ps(r, n_mul_ps(ySign, n_blendv_ps(quarter, n_mul_ps(n_set1_ps(3.0f), quarter), xNeg)),
                  n_and_mask(yInf, xInf));
  r = n_blendv_ps(r, n_mul_ps(ySign, n_blendv_ps(zero, pi, xNeg)), n_and_mask(xInf, n_not_mask(yInf)));
  // y = ±0 且 x > 0：带符号零
  // y = ±0：x > 0 给带符号零，x < 0 给带符号 π
  n_mask yZeroM = n_cmp_eq_ps(y, zero);
  r = n_blendv_ps(r, n_mul_ps(ySign, pi), n_and_mask(yZeroM, xNeg));
  return n_blendv_ps(r, n_mul_ps(y, zero), n_and_mask(yZeroM, xPos));
}

// 奇函数在 ±0 处的结果等于输入（保留 -0）；sin/tan/atan/asin/sinh/tanh 共用。
// 宏层会把操作数求值两次，只接受无副作用的操作数。
static N_FORCEINLINE n_float _n_dom_odd_zero(n_float x, n_float r) {
  return n_blendv_ps(r, x, n_cmp_eq_ps(x, n_set1_ps(0.0f)));
}

// asin/acos 的定义域：|x| > 1 为 NaN；acos(1) 精确为 +0
static N_FORCEINLINE n_float _n_dom_asin(n_float x, n_float r) {
  r = n_blendv_ps(r, n_set1_ps(NAN), n_cmp_gt_ps(n_fabs_ps(x), n_set1_ps(1.0f)));
  return n_blendv_ps(r, x, n_is_nan_ps(x));
}

static N_FORCEINLINE n_float _n_dom_acos(n_float x, n_float r) {
  r = n_blendv_ps(r, n_set1_ps(NAN), n_cmp_gt_ps(n_fabs_ps(x), n_set1_ps(1.0f)));
  r = n_blendv_ps(r, n_set1_ps(0.0f), n_cmp_eq_ps(x, n_set1_ps(1.0f)));
  return n_blendv_ps(r, x, n_is_nan_ps(x));
}
// 三角函数的大参数：Cody-Waite 归约在 1e7 之上不可靠（tan 还会因 sin/cos 分别回退而相除出 0/0），
// 超过阈值整 lane 走 libm。三个入口共用同一实现，新增函数只需换 libm 指针。
static N_FORCEINLINE n_float _n_trig_big(n_float x, n_float r, float (*f)(float)) {
  if (!n_any_true(n_cmp_gt_ps(n_fabs_ps(x), n_set1_ps(1.0e7f)))) return r;
  float la[NSIMD_WIDTH], lr[NSIMD_WIDTH];
  n_store_ps(la, x);
  n_store_ps(lr, r);
  for (int i = 0; i < NSIMD_WIDTH; i++)
    if (!(la[i] <= 1.0e7f && la[i] >= -1.0e7f)) lr[i] = f(la[i]);
  return n_load_ps(lr);
}

static N_FORCEINLINE n_float _n_sin_big(n_float x, n_float r) { return _n_trig_big(x, r, ::sinf); }
static N_FORCEINLINE n_float _n_cos_big(n_float x, n_float r) { return _n_trig_big(x, r, ::cosf); }
static N_FORCEINLINE n_float _n_tan_big(n_float x, n_float r) { return _n_trig_big(x, r, ::tanf); }


// ================================================================
// Inline AVX2 polynomial implementations (SLEEF coefficients, zero call overhead)
// Other platforms fall through to per-lane scalar fallbacks below.
// ================================================================
// Constants from sleef/src/libm/sleefsimdsp.c: PI_A2/PI_B2/PI_C2 = 閿?2 in 3-parts
#define _N_PIO2_A 1.57079637e+00f
#define _N_PIO2_B -4.37113883e-08f

#if defined(NSIMD_AVX2) || defined(NSIMD_AVX512)

static N_FORCEINLINE __m256 _n256_abs(__m256 a) { return _mm256_andnot_ps(_mm256_set1_ps(-0.0f), a); }
static N_FORCEINLINE __m256 _n256_round(__m256 a) { return _mm256_round_ps(a, _MM_FROUND_TO_NEAREST_INT | _MM_FROUND_NO_EXC); }
static N_FORCEINLINE __m256 _n256_trunc(__m256 a) { return _mm256_round_ps(a, _MM_FROUND_TO_ZERO | _MM_FROUND_NO_EXC); }
static N_FORCEINLINE __m256 _n256_eq(__m256 a, __m256 b) { return _mm256_cmp_ps(a, b, _CMP_EQ_OQ); }
static N_FORCEINLINE __m256 _n256_lt(__m256 a, __m256 b) { return _mm256_cmp_ps(a, b, _CMP_LT_OQ); }
static N_FORCEINLINE __m256 _n256_gt(__m256 a, __m256 b) { return _mm256_cmp_ps(a, b, _CMP_GT_OQ); }
static N_FORCEINLINE __m256 _n256_ne(__m256 a, __m256 b) { return _mm256_cmp_ps(a, b, _CMP_NEQ_UQ); }
static N_FORCEINLINE __m256 _n256_not(__m256 m) { return _mm256_xor_ps(m, _mm256_castsi256_ps(_mm256_set1_epi32(-1))); }

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

// 单精度 3 段 Cody-Waite 归约：|x| <= 6000 时 q < 2^12，pio2_hi 低 12 位尾数为零，
// 因此 q*pio2_hi 精确，r 的绝对误差 < 1 ulp。免去 double 域往返（12 条 double 指令 + 4 条转换）。
static N_FORCEINLINE __m256 _n_reduce_pio2_ps_avx2(__m256 d, __m256i* qOut) {
  const __m256 two_over_pi = _mm256_set1_ps(0.636619772f);
  const __m256 pio2_hi = _mm256_set1_ps(1.5703125f);
  const __m256 pio2_lo = _mm256_set1_ps(4.83826792e-04f);
  const __m256 pio2_lo2 = _mm256_set1_ps(2.56328292e-12f);
  __m256 qf = _mm256_round_ps(_mm256_mul_ps(d, two_over_pi), _MM_FROUND_TO_NEAREST_INT | _MM_FROUND_NO_EXC);
  *qOut = _mm256_cvtps_epi32(qf);
  __m256 r = _mm256_fnmadd_ps(qf, pio2_hi, d);
  r = _mm256_fnmadd_ps(qf, pio2_lo, r);
  r = _mm256_fnmadd_ps(qf, pio2_lo2, r);
  return r;
}

// 全部 lane 都在单精度归约的安全范围内才走快路径，否则整条走精确路径。
static N_FORCEINLINE bool _n_all_small_avx2(__m256 d, float limit) {
  __m256 a = _mm256_and_ps(d, _mm256_castsi256_ps(_mm256_set1_epi32(0x7fffffff)));
  return _mm256_movemask_ps(_mm256_cmp_ps(a, _mm256_set1_ps(limit), _CMP_GT_OQ)) == 0;
}

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
  if (_n_all_small_avx2(d, 6000.0f)) {
    __m256i qs;
    __m256 rs = _n_reduce_pio2_ps_avx2(d, &qs);
    __m256 s_sin = _n_sin_poly_avx2(rs);
    __m256 s_cos = _n_cos_poly_avx2(rs);
    __m256 ev = _mm256_castsi256_ps(_mm256_cmpeq_epi32(_mm256_and_si256(qs, _mm256_set1_epi32(1)),
                                                       _mm256_setzero_si256()));
    __m256 sv = _mm256_blendv_ps(s_cos, s_sin, ev);
    __m256i fl = _mm256_and_si256(qs, _mm256_set1_epi32(2));
    return _mm256_xor_ps(sv, _mm256_castsi256_ps(_mm256_slli_epi32(fl, 30)));
  }
  __m256i qi;
  __m256 r = _n_reduce_pio2_avx2(d, &qi);
  __m256 u_sin = _n_sin_poly_avx2(r);
  __m256 u_cos = _n_cos_poly_avx2(r);
  __m256 even = _mm256_castsi256_ps(_mm256_cmpeq_epi32(_mm256_and_si256(qi, _mm256_set1_epi32(1)),
                                                       _mm256_setzero_si256()));
  __m256 u = _mm256_blendv_ps(u_cos, u_sin, even);
  __m256i flip = _mm256_and_si256(qi, _mm256_set1_epi32(2));
  __m256 rl = _mm256_xor_ps(u, _mm256_castsi256_ps(_mm256_slli_epi32(flip, 30)));
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
  if (_n_all_small_avx2(d, 6000.0f)) {
    __m256i qs;
    __m256 rs = _n_reduce_pio2_ps_avx2(d, &qs);
    __m256 s_sin = _n_sin_poly_avx2(rs);
    __m256 s_cos = _n_cos_poly_avx2(rs);
    __m256 ev = _mm256_castsi256_ps(_mm256_cmpeq_epi32(_mm256_and_si256(qs, _mm256_set1_epi32(1)),
                                                       _mm256_setzero_si256()));
    __m256 sv = _mm256_blendv_ps(s_sin, s_cos, ev);
    __m256i fl = _mm256_and_si256(_mm256_add_epi32(qs, _mm256_set1_epi32(1)), _mm256_set1_epi32(2));
    return _mm256_xor_ps(sv, _mm256_castsi256_ps(_mm256_slli_epi32(fl, 30)));
  }
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
  // 次正规先放大 2^24，末尾补回 24*ln2
  __m256 sub = _mm256_cmp_ps(d, _mm256_set1_ps(1.17549435e-38f), _CMP_LT_OQ);
  d = _mm256_blendv_ps(d, _mm256_mul_ps(d, _mm256_set1_ps(16777216.0f)), sub);
  __m256i b = _mm256_castps_si256(d);
  __m256i e = _mm256_sub_epi32(_mm256_srli_epi32(b, 23), _mm256_set1_epi32(127));
  __m256 m = _mm256_castsi256_ps(_mm256_or_si256(_mm256_and_si256(b, _mm256_set1_epi32(0x007fffff)),
                                                _mm256_set1_epi32(0x3f800000)));
  // 尾数搬到 [sqrt(1/2), sqrt(2))，避免 e*ln2 与 log(m) 相消
  __m256 big = _mm256_cmp_ps(m, _mm256_set1_ps(1.41421356f), _CMP_GT_OQ);
  m = _mm256_blendv_ps(m, _mm256_mul_ps(m, _mm256_set1_ps(0.5f)), big);
  e = _mm256_add_epi32(e, _mm256_srli_epi32(_mm256_castps_si256(big), 31));
  __m256 u = _mm256_sub_ps(m, _mm256_set1_ps(1.0f));
  __m256 p = _mm256_set1_ps(-7.539791614e-02f);
  p = _mm256_fmadd_ps(p, u, _mm256_set1_ps(1.274866313e-01f));  // c1
  p = _mm256_fmadd_ps(p, u, _mm256_set1_ps(-1.319895834e-01f));  // c2
  p = _mm256_fmadd_ps(p, u, _mm256_set1_ps(1.420261115e-01f));  // c3
  p = _mm256_fmadd_ps(p, u, _mm256_set1_ps(-1.661652625e-01f));  // c4
  p = _mm256_fmadd_ps(p, u, _mm256_set1_ps(2.000115514e-01f));  // c5
  p = _mm256_fmadd_ps(p, u, _mm256_set1_ps(-2.500126958e-01f));  // c6
  p = _mm256_fmadd_ps(p, u, _mm256_set1_ps(3.333333433e-01f));  // c7
  p = _mm256_fmadd_ps(p, u, _mm256_set1_ps(-4.999999106e-01f));  // c8
  p = _mm256_fmadd_ps(p, u, _mm256_set1_ps(1.000000000e+00f));  // c9
  __m256 ef = _mm256_cvtepi32_ps(e);
  __m256 r = _mm256_fmadd_ps(ef, _mm256_set1_ps(0.693145751953125f), _mm256_mul_ps(u, p));
  r = _mm256_fmadd_ps(ef, _mm256_set1_ps(1.428606765330187045e-6f), r);
  // 定义域（0 / 负数 / ±inf / NaN）由宏层 _n_dom_log 负责
  return _mm256_sub_ps(r, _mm256_and_ps(sub, _mm256_set1_ps(16.635532333438687f)));
}
static N_FORCEINLINE __m256 _n_exp_avx2(__m256 d) {
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
  p = _mm256_fmadd_ps(p, r, _mm256_set1_ps(1.0f));

  // 2^n 拆成两段等量因子：|n| <= 252 时每段都在 float 指数域内，且先乘 p 再乘 s2 不会提前溢出
  __m256i ni = _mm256_cvtps_epi32(_mm256_max_ps(_mm256_min_ps(n, _mm256_set1_ps(252.0f)),
                                                 _mm256_set1_ps(-252.0f)));
  __m256i n1 = _mm256_srai_epi32(ni, 1);
  __m256i n2 = _mm256_sub_epi32(ni, n1);
  __m256 s1 = _mm256_castsi256_ps(_mm256_slli_epi32(_mm256_add_epi32(n1, _mm256_set1_epi32(127)), 23));
  __m256 s2 = _mm256_castsi256_ps(_mm256_slli_epi32(_mm256_add_epi32(n2, _mm256_set1_epi32(127)), 23));

  __m256 result = _mm256_mul_ps(_mm256_mul_ps(p, s1), s2);

  result = _mm256_blendv_ps(result, _mm256_set1_ps(INFINITY),
                            _mm256_cmp_ps(d, _mm256_set1_ps(88.722839f), _CMP_GE_OQ));
  result = _mm256_blendv_ps(result, _mm256_setzero_ps(),
                            _mm256_cmp_ps(d, _mm256_set1_ps(-103.972084f), _CMP_LE_OQ));
  return result;
}

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

static N_FORCEINLINE __m256d _n_log1p_series_pd(__m256d t) {
  __m256d s = _mm256_set1_pd(1.0 / 13.0);
  s = _mm256_fmadd_pd(s, t, _mm256_set1_pd(-1.0 / 12.0));
  s = _mm256_fmadd_pd(s, t, _mm256_set1_pd(1.0 / 11.0));
  s = _mm256_fmadd_pd(s, t, _mm256_set1_pd(-1.0 / 10.0));
  s = _mm256_fmadd_pd(s, t, _mm256_set1_pd(1.0 / 9.0));
  s = _mm256_fmadd_pd(s, t, _mm256_set1_pd(-1.0 / 8.0));
  s = _mm256_fmadd_pd(s, t, _mm256_set1_pd(1.0 / 7.0));
  s = _mm256_fmadd_pd(s, t, _mm256_set1_pd(-1.0 / 6.0));
  s = _mm256_fmadd_pd(s, t, _mm256_set1_pd(1.0 / 5.0));
  s = _mm256_fmadd_pd(s, t, _mm256_set1_pd(-1.0 / 4.0));
  s = _mm256_fmadd_pd(s, t, _mm256_set1_pd(1.0 / 3.0));
  s = _mm256_fmadd_pd(s, t, _mm256_set1_pd(-1.0 / 2.0));
  s = _mm256_fmadd_pd(s, t, _mm256_set1_pd(1.0));
  return s;
}

static N_FORCEINLINE __m256d _n_exp2_pd(__m256d v) {
  __m256d n = _mm256_round_pd(v, _MM_FROUND_TO_NEAREST_INT | _MM_FROUND_NO_EXC);
  __m256d f = _mm256_sub_pd(v, n);
  __m256d r = _mm256_mul_pd(f, _mm256_set1_pd(0.69314718055994530942));  // f*ln2

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

  __m128i ni32 = _mm256_cvtpd_epi32(_mm256_max_pd(_mm256_min_pd(n, _mm256_set1_pd(1023.0)),
                                                  _mm256_set1_pd(-1022.0)));
  __m256i ni64 = _mm256_cvtepi32_epi64(ni32);
  __m256d scale = _mm256_castsi256_pd(_mm256_slli_epi64(_mm256_add_epi64(ni64, _mm256_set1_epi64x(1023)), 52));
  return _mm256_mul_pd(p, scale);
}
static N_FORCEINLINE __m256 _n_pow_avx2_ps(__m256 x, __m256 y) {
  __m256d yl = _mm256_cvtps_pd(_mm256_castps256_ps128(y));
  __m256d yh = _mm256_cvtps_pd(_mm256_extractf128_ps(y, 1));

  __m256 ax = _n256_abs(x);
  __m256i ei = _mm256_sub_epi32(_mm256_srli_epi32(_mm256_castps_si256(ax), 23), _mm256_set1_epi32(127));
  __m256 mf = _mm256_castsi256_ps(_mm256_or_si256(_mm256_and_si256(_mm256_castps_si256(ax), _mm256_set1_epi32(0x807fffff)),
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

  // 只算 |x|^y：符号、奇偶、零底数与 NaN 全部由 _n_dom_pow 负责
  return mag;
}
// ================================================================
// ================================================================
static N_FORCEINLINE void _n_reduce4_pd(__m256d x, __m256d* rOut, __m128i* qOut) {
  const __m256d two_over_pi = _mm256_set1_pd(0.63661977236758134307553505349006);
  __m256d q = _mm256_round_pd(_mm256_mul_pd(x, two_over_pi), _MM_FROUND_TO_NEAREST_INT | _MM_FROUND_NO_EXC);
  __m256d r = _mm256_fnmadd_pd(q, _mm256_set1_pd(1.57079632673412561417e+00), x);
  r = _mm256_fnmadd_pd(q, _mm256_set1_pd(6.07710050650619224932e-11), r);
  r = _mm256_fnmadd_pd(q, _mm256_set1_pd(2.02226624879595063154e-21), r);
  *rOut = r;
  *qOut = _mm256_cvtpd_epi32(q);
}

static N_FORCEINLINE __m256d _n_sin_poly4_pd(__m256d r) {
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

static N_FORCEINLINE __m256d _n_quad4_pd(__m128i q) {
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
  r = _mm256_add_pd(r, _mm256_and_pd(mid, pi_4));
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
  // y = ±0 且 x > 0 时结果为 ±0
  __m256d yZeroPos = _mm256_and_pd(_mm256_cmp_pd(y, zero, _CMP_EQ_OQ), _mm256_cmp_pd(x, zero, _CMP_GT_OQ));
  r = _mm256_blendv_pd(r, _mm256_and_pd(y, _mm256_set1_pd(-0.0)), yZeroPos);
  return _mm256_blendv_pd(r, zero, _mm256_and_pd(_mm256_cmp_pd(x, zero, _CMP_EQ_OQ), _mm256_cmp_pd(y, zero, _CMP_EQ_OQ)));
}

static N_FORCEINLINE __m256d _n_exp4_pd(__m256d x) {
  // 定义域：+inf -> +inf，-inf -> 0，NaN 透传
  __m256d res = _n_exp2_pd(_mm256_mul_pd(x, _mm256_set1_pd(1.4426950408889634)));
  // 定义域：+inf -> +inf，-inf -> 0，NaN 透传（指数位构造对特殊值无定义）
  res = _mm256_blendv_pd(res, _mm256_set1_pd(INFINITY), _mm256_cmp_pd(x, _mm256_set1_pd(709.78), _CMP_GT_OQ));
  res = _mm256_blendv_pd(res, _mm256_setzero_pd(), _mm256_cmp_pd(x, _mm256_set1_pd(-745.2), _CMP_LT_OQ));
  return _mm256_blendv_pd(res, x, _mm256_cmp_pd(x, x, _CMP_UNORD_Q));
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
  __m256d ax = _mm256_and_pd(x, _mm256_castsi256_pd(_mm256_set1_epi64x(0x7fffffffffffffffLL)));
  // |x| > 20 时 exp(2x) 溢出，(inf-1)/(inf+1) 与 NaN 都会出错；直接返回带符号的 1
  __m256d xc = _mm256_blendv_pd(x, _mm256_or_pd(_mm256_set1_pd(20.0), _mm256_and_pd(x, _mm256_set1_pd(-0.0))),
                                _mm256_cmp_pd(ax, _mm256_set1_pd(20.0), _CMP_GT_OQ));
  __m256d e2 = _n_exp4_pd(_mm256_mul_pd(_mm256_set1_pd(2.0), xc));
  return _mm256_div_pd(_mm256_sub_pd(e2, _mm256_set1_pd(1.0)), _mm256_add_pd(e2, _mm256_set1_pd(1.0)));
}

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
  // log1p：|t| <= 0.25 时走级数，t = x - 1
  __m256d xd_lo = _mm256_cvtps_pd(_mm256_castps256_ps128(x));
  __m256d xd_hi = _mm256_cvtps_pd(_mm256_extractf128_ps(x, 1));
  __m256d tlo = _mm256_sub_pd(xd_lo, _mm256_set1_pd(1.0));
  __m256d thi = _mm256_sub_pd(xd_hi, _mm256_set1_pd(1.0));

  __m256 ax = _mm256_and_ps(x, _mm256_castsi256_ps(_mm256_set1_epi32(0x7fffffff)));
  // float 次正规：先放大 2^24 再取对数，末尾补回 24*ln2
  __m256 subM = _mm256_cmp_ps(ax, _mm256_set1_ps(1.17549435e-38f), _CMP_LT_OQ);
  __m256 xs = _mm256_blendv_ps(x, _mm256_mul_ps(x, _mm256_set1_ps(16777216.0f)), subM);
  __m256 axs = _mm256_and_ps(xs, _mm256_castsi256_ps(_mm256_set1_epi32(0x7fffffff)));
  __m256i ei = _mm256_sub_epi32(_mm256_srli_epi32(_mm256_castps_si256(axs), 23), _mm256_set1_epi32(127));
  __m256 mf = _mm256_castsi256_ps(_mm256_or_si256(_mm256_and_si256(_mm256_castps_si256(xs), _mm256_set1_epi32(0x807fffff)),
                                                                             _mm256_set1_epi32(0x3f800000)));
  const __m256d ln2 = _mm256_set1_pd(0.69314718055994530942);
  __m256d lo = _mm256_add_pd(_mm256_mul_pd(_mm256_cvtepi32_pd(_mm256_castsi256_si128(ei)), ln2),
                             _n_log_mant_pd(_mm256_cvtps_pd(_mm256_castps256_ps128(mf))));
  __m256d hi = _mm256_add_pd(_mm256_mul_pd(_mm256_cvtepi32_pd(_mm256_extracti128_si256(ei, 1)), ln2),
                             _n_log_mant_pd(_mm256_cvtps_pd(_mm256_extractf128_ps(mf, 1))));

  // log(1+t) = t*(1 - t/2 + ... + t^12/13)閿涘瘝t| <= 0.25
  __m256d nearLo = _mm256_mul_pd(tlo, _n_log1p_series_pd(tlo));
  __m256d nearHi = _mm256_mul_pd(thi, _n_log1p_series_pd(thi));
  __m256d absLo = _mm256_and_pd(tlo, _mm256_castsi256_pd(_mm256_set1_epi64x(0x7fffffffffffffffLL)));
  __m256d absHi = _mm256_and_pd(thi, _mm256_castsi256_pd(_mm256_set1_epi64x(0x7fffffffffffffffLL)));
  const __m256d q = _mm256_set1_pd(0.25);
  lo = _mm256_blendv_pd(lo, nearLo, _mm256_cmp_pd(absLo, q, _CMP_LE_OQ));
  hi = _mm256_blendv_pd(hi, nearHi, _mm256_cmp_pd(absHi, q, _CMP_LE_OQ));

  __m128 rl = _mm256_cvtpd_ps(lo), rh = _mm256_cvtpd_ps(hi);
  __m256 res = _mm256_insertf128_ps(_mm256_castps128_ps256(rl), rh, 1);
  // 定义域由宏层 _n_dom_log 负责；次正规已在上面放大，这里补回 24*ln2
  __m256 corr = _mm256_and_ps(subM, _mm256_set1_ps(16.635532333438687f));
  return _mm256_sub_ps(res, corr);
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

// High 档在 AVX512 上复用 AVX2 的 __m256 实现：16 lane 拆两组 8 lane
#define _N_HIGH_AVX512_UNARY(name)                                                     \
  static N_FORCEINLINE n_float _n_high_##name##_avx512(n_float x) {                    \
    __m256 lo = _n_high_##name(_mm512_castps512_ps256(x));                             \
    __m256 hi = _n_high_##name(_mm512_extractf32x8_ps(x, 1));                          \
    return _mm512_insertf32x8(_mm512_castps256_ps512(lo), hi, 1);                      \
  }

_N_HIGH_AVX512_UNARY(sin)
_N_HIGH_AVX512_UNARY(cos)
_N_HIGH_AVX512_UNARY(tan)
_N_HIGH_AVX512_UNARY(atan)
_N_HIGH_AVX512_UNARY(asin)
_N_HIGH_AVX512_UNARY(acos)
_N_HIGH_AVX512_UNARY(sinh)
_N_HIGH_AVX512_UNARY(cosh)
_N_HIGH_AVX512_UNARY(tanh)
_N_HIGH_AVX512_UNARY(exp)
_N_HIGH_AVX512_UNARY(log)
_N_HIGH_AVX512_UNARY(log10)

static N_FORCEINLINE n_float _n_high_atan2_avx512(n_float y, n_float x) {
  __m256 lo = _n_high_atan2(_mm512_castps512_ps256(y), _mm512_castps512_ps256(x));
  __m256 hi = _n_high_atan2(_mm512_extractf32x8_ps(y, 1), _mm512_extractf32x8_ps(x, 1));
  return _mm512_insertf32x8(_mm512_castps256_ps512(lo), hi, 1);
}

static N_FORCEINLINE void _n_high_sincos_avx512(n_float x, n_float* s, n_float* c) {
  *s = _n_high_sin_avx512(x);
  *c = _n_high_cos_avx512(x);
}

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
// AVX512 组合函数：与 AVX2 同一套公式，掩码改用 __mmask16 + _mm512_mask_blend_ps。
// 放在 AVX512 基础实现之后，供 Fastest 档宏使用。
static N_FORCEINLINE n_float _n_abs_avx512(n_float a) {
  return _mm512_castsi512_ps(_mm512_and_si512(_mm512_castps_si512(a), _mm512_set1_epi32(0x7fffffff)));
}

static N_FORCEINLINE n_float _n_tan_avx512(n_float a) {
  return _mm512_div_ps(_n_sin_avx512(a), _n_cos_avx512(a));
}

static N_FORCEINLINE n_float _n_atan_avx512(n_float a) {
  const __m512 one = _mm512_set1_ps(1.0f);
  const __m512 zero = _mm512_setzero_ps();
  __m512 ax = _n_abs_avx512(a);
  __mmask16 big = _mm512_cmp_ps_mask(ax, one, _CMP_GT_OQ);
  __m512 t = _mm512_mask_blend_ps(big, ax, _mm512_div_ps(one, ax));
  __m512 z = _mm512_mul_ps(t, t);
  __m512 q = _mm512_set1_ps(2.922975910e-03f);
  q = _mm512_fmadd_ps(q, z, _mm512_set1_ps(-1.637927326e-02f));
  q = _mm512_fmadd_ps(q, z, _mm512_set1_ps(4.323442722e-02f));
  q = _mm512_fmadd_ps(q, z, _mm512_set1_ps(-7.554515826e-02f));
  q = _mm512_fmadd_ps(q, z, _mm512_set1_ps(1.066727714e-01f));
  q = _mm512_fmadd_ps(q, z, _mm512_set1_ps(-1.421142270e-01f));
  q = _mm512_fmadd_ps(q, z, _mm512_set1_ps(1.999382071e-01f));
  q = _mm512_fmadd_ps(q, z, _mm512_set1_ps(-3.333315467e-01f));
  __m512 r = _mm512_fmadd_ps(_mm512_mul_ps(_mm512_mul_ps(t, t), t), q, t);
  r = _mm512_mask_blend_ps(big, r, _mm512_sub_ps(_mm512_set1_ps(1.5707963267948966f), r));
  __mmask16 neg = _mm512_cmp_ps_mask(a, zero, _CMP_LT_OQ);
  return _mm512_mask_blend_ps(neg, r, _mm512_sub_ps(zero, r));
}

static N_FORCEINLINE n_float _n_atan2_avx512(n_float y, n_float x) {
  const __m512 zero = _mm512_setzero_ps();
  const __m512 pi = _mm512_set1_ps(3.14159265358979323846f);
  __m512 r = _n_atan_avx512(_mm512_div_ps(y, x));
  __mmask16 yn = _mm512_cmp_ps_mask(y, zero, _CMP_GE_OQ);
  __m512 adj = _mm512_mask_blend_ps(yn, _mm512_sub_ps(zero, pi), pi);
  __mmask16 xn = _mm512_cmp_ps_mask(x, zero, _CMP_LT_OQ);
  r = _mm512_mask_blend_ps(xn, r, _mm512_add_ps(r, adj));
  __mmask16 bothZero = _mm512_cmp_ps_mask(x, zero, _CMP_EQ_OQ) & _mm512_cmp_ps_mask(y, zero, _CMP_EQ_OQ);
  return _mm512_mask_blend_ps(bothZero, r, zero);
}

static N_FORCEINLINE n_float _n_asin_avx512(n_float a) {
  const __m512 one = _mm512_set1_ps(1.0f);
  __m512 t = _mm512_sqrt_ps(_mm512_max_ps(_mm512_mul_ps(_mm512_sub_ps(one, a), _mm512_add_ps(one, a)), _mm512_setzero_ps()));
  return _mm512_mul_ps(_mm512_set1_ps(2.0f), _n_atan2_avx512(a, _mm512_add_ps(one, t)));
}

static N_FORCEINLINE n_float _n_acos_avx512(n_float a) {
  const __m512 one = _mm512_set1_ps(1.0f);
  __m512 t = _mm512_sqrt_ps(_mm512_max_ps(_mm512_mul_ps(_mm512_sub_ps(one, a), _mm512_add_ps(one, a)), _mm512_setzero_ps()));
  __m512 r = _mm512_mul_ps(_mm512_set1_ps(2.0f), _n_atan2_avx512(t, _mm512_add_ps(one, a)));
  __mmask16 low = _mm512_cmp_ps_mask(a, _mm512_set1_ps(-1.0f), _CMP_LE_OQ);
  return _mm512_mask_blend_ps(low, r, _mm512_set1_ps(3.14159265358979323846f));
}

static N_FORCEINLINE n_float _n_sinh_avx512(n_float a) {
  __m512 e = _n_exp_avx512(a);
  __m512 inv = _n_exp_avx512(_mm512_sub_ps(_mm512_setzero_ps(), a));
  __m512 wide = _mm512_mul_ps(_mm512_set1_ps(0.5f), _mm512_sub_ps(e, inv));
  __m512 z = _mm512_mul_ps(a, a);
  __m512 sp = _mm512_set1_ps(1.0f / 5040.0f);
  sp = _mm512_fmadd_ps(sp, z, _mm512_set1_ps(1.0f / 120.0f));
  sp = _mm512_fmadd_ps(sp, z, _mm512_set1_ps(1.0f / 6.0f));
  sp = _mm512_fmadd_ps(sp, z, _mm512_set1_ps(1.0f));
  __mmask16 small = _mm512_cmp_ps_mask(_n_abs_avx512(a), _mm512_set1_ps(0.25f), _CMP_LT_OQ);
  return _mm512_mask_blend_ps(small, wide, _mm512_mul_ps(a, sp));
}

static N_FORCEINLINE n_float _n_cosh_avx512(n_float a) {
  __m512 e = _n_exp_avx512(a);
  __m512 inv = _n_exp_avx512(_mm512_sub_ps(_mm512_setzero_ps(), a));
  __m512 wide = _mm512_mul_ps(_mm512_set1_ps(0.5f), _mm512_add_ps(e, inv));
  __m512 z = _mm512_mul_ps(a, a);
  __m512 sp = _mm512_set1_ps(1.0f / 720.0f);
  sp = _mm512_fmadd_ps(sp, z, _mm512_set1_ps(1.0f / 24.0f));
  sp = _mm512_fmadd_ps(sp, z, _mm512_set1_ps(0.5f));
  sp = _mm512_fmadd_ps(sp, z, _mm512_set1_ps(1.0f));
  __mmask16 small = _mm512_cmp_ps_mask(_n_abs_avx512(a), _mm512_set1_ps(0.25f), _CMP_LT_OQ);
  return _mm512_mask_blend_ps(small, wide, sp);
}

static N_FORCEINLINE n_float _n_tanh_avx512(n_float a) {
  // |x| > 44 时 exp(2x) 溢出成 inf，(inf-1)/(inf+1) 变 NaN；按位夹到 ±88（保留 NaN 与符号）
  __mmask16 big512 = _mm512_cmp_ps_mask(_n_abs_avx512(a), _mm512_set1_ps(44.0f), _CMP_GT_OQ);
  __m512 t2 = _mm512_mask_blend_ps(big512, _mm512_mul_ps(_mm512_set1_ps(2.0f), a),
                                   _mm512_or_ps(_mm512_set1_ps(88.0f), _mm512_and_ps(a, _mm512_set1_ps(-0.0f))));
  __m512 e2 = _n_exp_avx512(t2);
  __m512 wide = _mm512_div_ps(_mm512_sub_ps(e2, _mm512_set1_ps(1.0f)), _mm512_add_ps(e2, _mm512_set1_ps(1.0f)));
  __m512 z = _mm512_mul_ps(a, a);
  __m512 sp = _mm512_set1_ps(62.0f / 2835.0f);
  sp = _mm512_fmadd_ps(sp, z, _mm512_set1_ps(-17.0f / 315.0f));
  sp = _mm512_fmadd_ps(sp, z, _mm512_set1_ps(2.0f / 15.0f));
  sp = _mm512_fmadd_ps(sp, z, _mm512_set1_ps(-1.0f / 3.0f));
  sp = _mm512_fmadd_ps(sp, z, _mm512_set1_ps(1.0f));
  __mmask16 small = _mm512_cmp_ps_mask(_n_abs_avx512(a), _mm512_set1_ps(0.25f), _CMP_LT_OQ);
  return _mm512_mask_blend_ps(small, wide, _mm512_mul_ps(a, sp));
}

static N_FORCEINLINE void _n_sincos_avx512(n_float a, n_float* s, n_float* c) {
  *s = _n_sin_avx512(a);
  *c = _n_cos_avx512(a);
}

#endif // NSIMD_AVX512

// ================================================================
// provide per-lane scalar fallbacks when no SIMD path is defined.
// ================================================================

#if !defined(SIMD_MATH_PRECISION)
  #define SIMD_MATH_PRECISION 1   // default: Fastest
#endif

// ================================================================
// NEON (aarch64) inline math -- Fastest tier.
// Same algorithms as the AVX2 kernels: argument reduction in double
// lanes (float64x2_t), polynomials in float lanes. Kept in one place so
// the constants stay in sync with the AVX2 versions.
// ================================================================
#if defined(NSIMD_NEON) && defined(__aarch64__)

static N_FORCEINLINE void _n_reduce_neon(n_float d, n_float* rOut, n_int* qOut) {
  float64x2_t dlo = vcvt_f64_f32(vget_low_f32(d));
  float64x2_t dhi = vcvt_f64_f32(vget_high_f32(d));
  const float64x2_t twoOverPi = vdupq_n_f64(0.63661977236758134308);
  float64x2_t qlo = vrndnq_f64(vmulq_f64(dlo, twoOverPi));
  float64x2_t qhi = vrndnq_f64(vmulq_f64(dhi, twoOverPi));
  const float64x2_t pio2a = vdupq_n_f64(1.57079632673412561417e+00);
  const float64x2_t pio2b = vdupq_n_f64(6.07710050650619224932e-11);
  const float64x2_t pio2c = vdupq_n_f64(2.02226624879595063154e-21);
  float64x2_t rlo = vfmaq_f64(dlo, vnegq_f64(qlo), pio2a);
  float64x2_t rhi = vfmaq_f64(dhi, vnegq_f64(qhi), pio2a);
  rlo = vfmaq_f64(rlo, vnegq_f64(qlo), pio2b);
  rhi = vfmaq_f64(rhi, vnegq_f64(qhi), pio2b);
  rlo = vfmaq_f64(rlo, vnegq_f64(qlo), pio2c);
  rhi = vfmaq_f64(rhi, vnegq_f64(qhi), pio2c);
  *rOut = vcombine_f32(vcvt_f32_f64(rlo), vcvt_f32_f64(rhi));
  int64x2_t qloi = vcvtq_s64_f64(qlo);
  int64x2_t qhii = vcvtq_s64_f64(qhi);
  *qOut = vcombine_s32(vmovn_s64(qloi), vmovn_s64(qhii));
}

static N_FORCEINLINE n_float _n_sin_poly_neon(n_float r) {
  n_float z = vmulq_f32(r, r);
  n_float p = vdupq_n_f32(-2.50521083854417187751e-8f);
  p = vfmaq_f32(vdupq_n_f32(2.75573192239858925109e-6f), p, z);
  p = vfmaq_f32(vdupq_n_f32(-1.98412698412698412522e-4f), p, z);
  p = vfmaq_f32(vdupq_n_f32(8.33333333333333321769e-3f), p, z);
  p = vfmaq_f32(vdupq_n_f32(-1.66666666666666657415e-1f), p, z);
  return vfmaq_f32(r, vmulq_f32(z, r), p);
}

static N_FORCEINLINE n_float _n_cos_poly_neon(n_float r) {
  n_float z = vmulq_f32(r, r);
  n_float p = vdupq_n_f32(2.4801587301587301587e-5f);
  p = vfmaq_f32(vdupq_n_f32(-1.3888888888888888889e-3f), p, z);
  p = vfmaq_f32(vdupq_n_f32(4.1666666666666666667e-2f), p, z);
  p = vfmaq_f32(vdupq_n_f32(-5.0e-1f), p, z);
  return vfmaq_f32(vdupq_n_f32(1.0f), p, z);
}

static N_FORCEINLINE n_float _n_sin_neon(n_float d) {
  n_float r; n_int q;
  _n_reduce_neon(d, &r, &q);
  n_int odd = vandq_s32(q, vdupq_n_s32(1));
  n_float u = vbslq_f32(vceqq_s32(odd, vdupq_n_s32(1)), _n_cos_poly_neon(r), _n_sin_poly_neon(r));
  n_int flip = vandq_s32(q, vdupq_n_s32(2));
  n_float res = vbslq_f32(vceqq_s32(flip, vdupq_n_s32(2)), vnegq_f32(u), u);
  if (vmaxvq_f32(vabsq_f32(d)) > 1.0e7f) {
    float lane[NSIMD_WIDTH]; n_store_ps(lane, d);
    for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::sinf(lane[i]);
    res = n_load_ps(lane);
  }
  return res;
}

static N_FORCEINLINE n_float _n_cos_neon(n_float d) {
  n_float r; n_int q;
  _n_reduce_neon(d, &r, &q);
  n_int odd = vandq_s32(q, vdupq_n_s32(1));
  n_float u = vbslq_f32(vceqq_s32(odd, vdupq_n_s32(1)), _n_sin_poly_neon(r), _n_cos_poly_neon(r));
  n_int flip = vandq_s32(vaddq_s32(q, vdupq_n_s32(1)), vdupq_n_s32(2));
  n_float res = vbslq_f32(vceqq_s32(flip, vdupq_n_s32(2)), vnegq_f32(u), u);
  if (vmaxvq_f32(vabsq_f32(d)) > 1.0e7f) {
    float lane[NSIMD_WIDTH]; n_store_ps(lane, d);
    for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::cosf(lane[i]);
    res = n_load_ps(lane);
  }
  return res;
}

static N_FORCEINLINE void _n_sincos_neon(n_float x, n_float* s, n_float* c) {
  *s = _n_sin_neon(x);
  *c = _n_cos_neon(x);
}

static N_FORCEINLINE n_float _n_exp_neon(n_float d) {
  n_float n = vrndnq_f32(vmulq_f32(d, vdupq_n_f32(1.4426950408889634f)));
  n_int ni = vcvtq_s32_f32(n);
  n_float r = vfmaq_f32(d, vnegq_f32(n), vdupq_n_f32(0.693145751953125f));
  r = vfmaq_f32(r, vnegq_f32(n), vdupq_n_f32(1.428606765330187045e-6f));
  n_float p = vdupq_n_f32(1.0f / 5040.0f);
  p = vfmaq_f32(vdupq_n_f32(1.0f / 720.0f), p, r);
  p = vfmaq_f32(vdupq_n_f32(1.0f / 120.0f), p, r);
  p = vfmaq_f32(vdupq_n_f32(1.0f / 24.0f), p, r);
  p = vfmaq_f32(vdupq_n_f32(1.0f / 6.0f), p, r);
  p = vfmaq_f32(vdupq_n_f32(0.5f), p, r);
  p = vfmaq_f32(vdupq_n_f32(1.0f), p, r);
  p = vfmaq_f32(vdupq_n_f32(1.0f), p, r);
  // 2^n 拆成两段等量因子：|n| <= 252 时每段都在 float 指数域内
  n_int nc = vmaxq_s32(vminq_s32(ni, vdupq_n_s32(252)), vdupq_n_s32(-252));
  n_int hi = vshrq_n_s32(nc, 1);
  n_int lo = vsubq_s32(nc, hi);
  n_float s1 = vreinterpretq_f32_s32(vshlq_n_s32(vaddq_s32(hi, vdupq_n_s32(127)), 23));
  n_float s2 = vreinterpretq_f32_s32(vshlq_n_s32(vaddq_s32(lo, vdupq_n_s32(127)), 23));
  n_float result = vmulq_f32(vmulq_f32(p, s1), s2);
  result = vbslq_f32(vcgtq_f32(d, vdupq_n_f32(88.722839f)), vdupq_n_f32(INFINITY), result);
  return vbslq_f32(vcltq_f32(d, vdupq_n_f32(-103.972084f)), vdupq_n_f32(0.0f), result);
}

static N_FORCEINLINE n_float _n_log_neon(n_float d) {
  // 次正规输入先放大 2^24 再取对数，末尾补回 24*ln2
  n_mask sub = vcltq_f32(d, vdupq_n_f32(1.17549435e-38f));
  d = vbslq_f32(sub, vmulq_f32(d, vdupq_n_f32(16777216.0f)), d);
  n_float t = vsubq_f32(d, vdupq_n_f32(1.0f));
  n_float at = vabsq_f32(t);

  n_float s = vdupq_n_f32(1.0f / 13.0f);
  s = vfmaq_f32(vdupq_n_f32(-1.0f / 12.0f), s, t);
  s = vfmaq_f32(vdupq_n_f32(1.0f / 11.0f), s, t);
  s = vfmaq_f32(vdupq_n_f32(-1.0f / 10.0f), s, t);
  s = vfmaq_f32(vdupq_n_f32(1.0f / 9.0f), s, t);
  s = vfmaq_f32(vdupq_n_f32(-1.0f / 8.0f), s, t);
  s = vfmaq_f32(vdupq_n_f32(1.0f / 7.0f), s, t);
  s = vfmaq_f32(vdupq_n_f32(-1.0f / 6.0f), s, t);
  s = vfmaq_f32(vdupq_n_f32(1.0f / 5.0f), s, t);
  s = vfmaq_f32(vdupq_n_f32(-1.0f / 4.0f), s, t);
  s = vfmaq_f32(vdupq_n_f32(1.0f / 3.0f), s, t);
  s = vfmaq_f32(vdupq_n_f32(-1.0f / 2.0f), s, t);
  s = vfmaq_f32(vdupq_n_f32(1.0f), s, t);
  n_float near = vmulq_f32(t, s);

  n_int k = vreinterpretq_s32_f32(d);
  n_int e = vsubq_s32(vshrq_n_s32(k, 23), vdupq_n_s32(127));
  n_float m = vreinterpretq_f32_s32(vorrq_s32(vandq_s32(k, vdupq_n_s32(0x807fffff)), vdupq_n_s32(0x3f800000)));
  n_float x = vdivq_f32(vsubq_f32(m, vdupq_n_f32(1.0f)), vaddq_f32(m, vdupq_n_f32(1.0f)));
  n_float x2 = vmulq_f32(x, x);
  n_float p = vdupq_n_f32(0.15384615384615385f);
  p = vfmaq_f32(vdupq_n_f32(0.18181818181818182f), p, x2);
  p = vfmaq_f32(vdupq_n_f32(0.2222222222222222f), p, x2);
  p = vfmaq_f32(vdupq_n_f32(0.2857142857142857f), p, x2);
  p = vfmaq_f32(vdupq_n_f32(0.4f), p, x2);
  p = vfmaq_f32(vdupq_n_f32(0.6666666666666666f), p, x2);
  p = vfmaq_f32(vdupq_n_f32(2.0f), p, x2);
  n_float ef = vcvtq_f32_s32(e);
  n_float wide = vmulq_f32(ef, vdupq_n_f32(0.693145751953125f));
  wide = vfmaq_f32(wide, ef, vdupq_n_f32(1.428606765330187045e-6f));
  wide = vfmaq_f32(wide, x, p);
  n_float res = vbslq_f32(vcleq_f32(at, vdupq_n_f32(0.25f)), near, wide);
  // 定义域（0 / 负数 / ±inf / NaN）由宏层 _n_dom_log 负责
  return vsubq_f32(res, vbslq_f32(sub, vdupq_n_f32(16.635532333438687f), vdupq_n_f32(0.0f)));
}

static N_FORCEINLINE n_float _n_atan_neon(n_float a) {
  n_float ax = vabsq_f32(a);
  n_float one = vdupq_n_f32(1.0f);
  uint32x4_t big = vcgtq_f32(ax, one);
  n_float t = vbslq_f32(big, vdivq_f32(one, ax), ax);
  n_float z = vmulq_f32(t, t);
  n_float q = vdupq_n_f32(2.922975910e-03f);
  q = vfmaq_f32(vdupq_n_f32(-1.637927326e-02f), q, z);
  q = vfmaq_f32(vdupq_n_f32(4.323442722e-02f), q, z);
  q = vfmaq_f32(vdupq_n_f32(-7.554515826e-02f), q, z);
  q = vfmaq_f32(vdupq_n_f32(1.066727714e-01f), q, z);
  q = vfmaq_f32(vdupq_n_f32(-1.421142270e-01f), q, z);
  q = vfmaq_f32(vdupq_n_f32(1.999382071e-01f), q, z);
  q = vfmaq_f32(vdupq_n_f32(-3.333315467e-01f), q, z);
  n_float r = vfmaq_f32(t, vmulq_f32(vmulq_f32(t, t), t), q);
  r = vbslq_f32(big, vsubq_f32(vdupq_n_f32(1.5707963267948966f), r), r);
  return vbslq_f32(vcltq_f32(a, vdupq_n_f32(0.0f)), vnegq_f32(r), r);
}

static N_FORCEINLINE n_float _n_atan2_neon(n_float y, n_float x) {
  n_float zero = vdupq_n_f32(0.0f);
  n_float pi = vdupq_n_f32(3.14159265358979323846f);
  n_float r = _n_atan_neon(vdivq_f32(y, x));
  n_float adj = vbslq_f32(vcgeq_f32(y, zero), pi, vnegq_f32(pi));
  r = vbslq_f32(vcltq_f32(x, zero), vaddq_f32(r, adj), r);
  // y = ±0 且 x > 0 时结果为 ±0
  n_mask yZeroPos = vandq_u32(vceqq_f32(y, zero), vcgtq_f32(x, zero));
  r = vbslq_f32(yZeroPos, vreinterpretq_f32_u32(vandq_u32(vreinterpretq_u32_f32(y), vdupq_n_u32(0x80000000u))), r);
  return vbslq_f32(vandq_u32(vceqq_f32(x, zero), vceqq_f32(y, zero)), zero, r);
}

static N_FORCEINLINE n_float _n_asin_neon(n_float a) {
  n_float one = vdupq_n_f32(1.0f);
  n_float t = vsqrtq_f32(vmaxq_f32(vmulq_f32(vsubq_f32(one, a), vaddq_f32(one, a)), vdupq_n_f32(0.0f)));
  return vmulq_f32(vdupq_n_f32(2.0f), _n_atan2_neon(a, vaddq_f32(one, t)));
}

static N_FORCEINLINE n_float _n_acos_neon(n_float a) {
  n_float one = vdupq_n_f32(1.0f);
  n_float t = vsqrtq_f32(vmaxq_f32(vmulq_f32(vsubq_f32(one, a), vaddq_f32(one, a)), vdupq_n_f32(0.0f)));
  n_float r = vmulq_f32(vdupq_n_f32(2.0f), _n_atan2_neon(t, vaddq_f32(one, a)));
  return vbslq_f32(vcleq_f32(a, vdupq_n_f32(-1.0f)), vdupq_n_f32(3.14159265358979323846f), r);
}

static N_FORCEINLINE n_float _n_sinh_neon(n_float a) {
  n_float e = _n_exp_neon(a);
  n_float inv = _n_exp_neon(vnegq_f32(a));
  n_float wide = vmulq_f32(vdupq_n_f32(0.5f), vsubq_f32(e, inv));
  n_float z = vmulq_f32(a, a);
  n_float sp = vdupq_n_f32(1.0f / 5040.0f);
  sp = vfmaq_f32(vdupq_n_f32(1.0f / 120.0f), sp, z);
  sp = vfmaq_f32(vdupq_n_f32(1.0f / 6.0f), sp, z);
  sp = vfmaq_f32(vdupq_n_f32(1.0f), sp, z);
  return vbslq_f32(vcltq_f32(vabsq_f32(a), vdupq_n_f32(0.25f)), vmulq_f32(a, sp), wide);
}

static N_FORCEINLINE n_float _n_cosh_neon(n_float a) {
  n_float e = _n_exp_neon(a);
  n_float inv = _n_exp_neon(vnegq_f32(a));
  n_float wide = vmulq_f32(vdupq_n_f32(0.5f), vaddq_f32(e, inv));
  n_float z = vmulq_f32(a, a);
  n_float sp = vdupq_n_f32(1.0f / 720.0f);
  sp = vfmaq_f32(vdupq_n_f32(1.0f / 24.0f), sp, z);
  sp = vfmaq_f32(vdupq_n_f32(0.5f), sp, z);
  sp = vfmaq_f32(vdupq_n_f32(1.0f), sp, z);
  return vbslq_f32(vcltq_f32(vabsq_f32(a), vdupq_n_f32(0.25f)), sp, wide);
}

static N_FORCEINLINE n_float _n_tanh_neon(n_float a) {
  n_float t2 = vmulq_f32(vdupq_n_f32(2.0f), a);
  t2 = vbslq_f32(vcgtq_f32(vabsq_f32(a), vdupq_n_f32(44.0f)),
                 vreinterpretq_f32_u32(vorrq_u32(vreinterpretq_u32_f32(vdupq_n_f32(88.0f)), vandq_u32(vreinterpretq_u32_f32(a), vreinterpretq_u32_f32(vdupq_n_f32(-0.0f))))),
                 t2);
  n_float e2 = _n_exp_neon(t2);
  n_float wide = vdivq_f32(vsubq_f32(e2, vdupq_n_f32(1.0f)), vaddq_f32(e2, vdupq_n_f32(1.0f)));
  n_float z = vmulq_f32(a, a);
  n_float sp = vdupq_n_f32(62.0f / 2835.0f);
  sp = vfmaq_f32(vdupq_n_f32(-17.0f / 315.0f), sp, z);
  sp = vfmaq_f32(vdupq_n_f32(2.0f / 15.0f), sp, z);
  sp = vfmaq_f32(vdupq_n_f32(-1.0f / 3.0f), sp, z);
  sp = vfmaq_f32(vdupq_n_f32(1.0f), sp, z);
  return vbslq_f32(vcltq_f32(vabsq_f32(a), vdupq_n_f32(0.25f)), vmulq_f32(a, sp), wide);
}

// NEON pow：log2/exp2 在 double lane 里算（同 AVX2 路线），最后折回 float
static N_FORCEINLINE float64x2_t _n_log2_mant_neon(float64x2_t m) {
  float64x2_t t = vdivq_f64(vsubq_f64(m, vdupq_n_f64(1.0)), vaddq_f64(m, vdupq_n_f64(1.0)));
  float64x2_t z = vmulq_f64(t, t);
  float64x2_t s = vdupq_n_f64(1.0 / 13.0);
  s = vfmaq_f64(vdupq_n_f64(1.0 / 11.0), s, z);
  s = vfmaq_f64(vdupq_n_f64(1.0 / 9.0), s, z);
  s = vfmaq_f64(vdupq_n_f64(1.0 / 7.0), s, z);
  s = vfmaq_f64(vdupq_n_f64(1.0 / 5.0), s, z);
  s = vfmaq_f64(vdupq_n_f64(1.0 / 3.0), s, z);
  s = vfmaq_f64(vdupq_n_f64(1.0), s, z);
  return vmulq_f64(vmulq_f64(t, s), vdupq_n_f64(2.8853900817779268));
}

static N_FORCEINLINE float64x2_t _n_exp2_neon(float64x2_t v) {
  float64x2_t n = vrndnq_f64(v);
  float64x2_t f = vsubq_f64(v, n);
  float64x2_t r = vmulq_f64(f, vdupq_n_f64(0.69314718055994530942));

  float64x2_t p = vdupq_n_f64(1.0 / 479001600.0);
  p = vfmaq_f64(vdupq_n_f64(1.0 / 39916800.0), p, r);
  p = vfmaq_f64(vdupq_n_f64(1.0 / 3628800.0), p, r);
  p = vfmaq_f64(vdupq_n_f64(1.0 / 362880.0), p, r);
  p = vfmaq_f64(vdupq_n_f64(1.0 / 40320.0), p, r);
  p = vfmaq_f64(vdupq_n_f64(1.0 / 5040.0), p, r);
  p = vfmaq_f64(vdupq_n_f64(1.0 / 720.0), p, r);
  p = vfmaq_f64(vdupq_n_f64(1.0 / 120.0), p, r);
  p = vfmaq_f64(vdupq_n_f64(1.0 / 24.0), p, r);
  p = vfmaq_f64(vdupq_n_f64(1.0 / 6.0), p, r);
  p = vfmaq_f64(vdupq_n_f64(0.5), p, r);
  p = vfmaq_f64(vdupq_n_f64(1.0), p, r);
  p = vfmaq_f64(vdupq_n_f64(1.0), p, r);

  float64x2_t nc = vmaxq_f64(vminq_f64(n, vdupq_n_f64(1023.0)), vdupq_n_f64(-1022.0));
  int64x2_t ni = vcvtq_s64_f64(nc);
  float64x2_t scale = vreinterpretq_f64_s64(vshlq_n_s64(vaddq_s64(ni, vdupq_n_s64(1023)), 52));
  return vmulq_f64(p, scale);
}

static N_FORCEINLINE n_float _n_pow_neon(n_float x, n_float y) {
  // 尾数取绝对值（负底数的符号不进 log2），符号与奇偶由 _n_dom_pow 统一处理
  n_int ki = vreinterpretq_s32_f32(x);
  n_int ei = vsubq_s32(vshrq_n_s32(vandq_s32(ki, vdupq_n_s32(0x7fffffff)), 23), vdupq_n_s32(127));
  n_float mf = vreinterpretq_f32_s32(vorrq_s32(vandq_s32(ki, vdupq_n_s32(0x007fffff)), vdupq_n_s32(0x3f800000)));

  float64x2_t elo = vcvtq_f64_s64(vmovl_s32(vget_low_s32(ei)));
  float64x2_t ehi = vcvtq_f64_s64(vmovl_s32(vget_high_s32(ei)));
  float64x2_t llo = vaddq_f64(elo, _n_log2_mant_neon(vcvt_f64_f32(vget_low_f32(mf))));
  float64x2_t lhi = vaddq_f64(ehi, _n_log2_mant_neon(vcvt_f64_f32(vget_high_f32(mf))));
  float64x2_t vlo = vmulq_f64(llo, vcvt_f64_f32(vget_low_f32(y)));
  float64x2_t vhi = vmulq_f64(lhi, vcvt_f64_f32(vget_high_f32(y)));
  n_float mag = vcombine_f32(vcvt_f32_f64(_n_exp2_neon(vlo)), vcvt_f32_f64(_n_exp2_neon(vhi)));

  // 负底数：仅整数指数成立，奇数指数取负；|y| >= 2^23 的 float 必为偶数整数
  // 只算 |x|^y：符号、奇偶、零底数与 NaN 全部由 _n_dom_pow 负责
  return mag;
}
#endif // NSIMD_NEON && __aarch64__

#if defined(NSIMD_NEON) && defined(__aarch64__)

static N_FORCEINLINE void _n_reduce2_neon(float64x2_t d, float64x2_t* rOut, int32x2_t* qOut) {
  float64x2_t q = vrndnq_f64(vmulq_f64(d, vdupq_n_f64(0.63661977236758134308)));
  float64x2_t r = vfmaq_f64(d, vnegq_f64(q), vdupq_n_f64(1.57079632673412561417e+00));
  r = vfmaq_f64(r, vnegq_f64(q), vdupq_n_f64(6.07710050650619224932e-11));
  r = vfmaq_f64(r, vnegq_f64(q), vdupq_n_f64(2.02226624879595063154e-21));
  *rOut = r;
  *qOut = vmovn_s64(vcvtq_s64_f64(q));
}

static N_FORCEINLINE float64x2_t _n_sin_poly2_neon(float64x2_t r) {
  float64x2_t z = vmulq_f64(r, r);
  float64x2_t p = vdupq_n_f64(-2.50521083854417187751e-8);
  p = vfmaq_f64(vdupq_n_f64(2.75573192239858925109e-6), p, z);
  p = vfmaq_f64(vdupq_n_f64(-1.98412698412698412522e-4), p, z);
  p = vfmaq_f64(vdupq_n_f64(8.33333333333333321769e-3), p, z);
  p = vfmaq_f64(vdupq_n_f64(-1.66666666666666657415e-1), p, z);
  return vfmaq_f64(r, vmulq_f64(z, r), p);
}

static N_FORCEINLINE float64x2_t _n_cos_poly2_neon(float64x2_t r) {
  float64x2_t z = vmulq_f64(r, r);
  float64x2_t p = vdupq_n_f64(2.4801587301587301587e-5);
  p = vfmaq_f64(vdupq_n_f64(-1.3888888888888888889e-3), p, z);
  p = vfmaq_f64(vdupq_n_f64(4.1666666666666666667e-2), p, z);
  p = vfmaq_f64(vdupq_n_f64(-5.0e-1), p, z);
  return vfmaq_f64(vdupq_n_f64(1.0), p, z);
}

static N_FORCEINLINE uint64x2_t _n_not_u64(uint64x2_t m) {
  return vreinterpretq_u64_u32(vmvnq_u32(vreinterpretq_u32_u64(m)));
}

static N_FORCEINLINE float64x2_t _n_quad_mod4_neon(int32x2_t q) {
  int64x2_t qd = vmovl_s32(q);
  float64x2_t qf = vcvtq_f64_s64(qd);
  float64x2_t q4 = vrndmq_f64(vmulq_f64(qf, vdupq_n_f64(0.25)));
  return vsubq_f64(qf, vmulq_f64(q4, vdupq_n_f64(4.0)));
}

static N_FORCEINLINE uint64x2_t _n_is_odd2_neon(float64x2_t m4) {
  float64x2_t half = vmulq_f64(m4, vdupq_n_f64(0.5));
  float64x2_t two = vmulq_f64(vrndmq_f64(half), vdupq_n_f64(2.0));
  return vcgtq_f64(m4, two);
}

static N_FORCEINLINE float64x2_t _n_sin2_neon(float64x2_t x) {
  float64x2_t r; int32x2_t q;
  _n_reduce2_neon(x, &r, &q);
  float64x2_t m4 = _n_quad_mod4_neon(q);
  uint64x2_t odd = _n_is_odd2_neon(m4);
  float64x2_t u = vbslq_f64(odd, _n_cos_poly2_neon(r), _n_sin_poly2_neon(r));
  uint64x2_t neg = vcgeq_f64(m4, vdupq_n_f64(2.0));
  return vbslq_f64(neg, vnegq_f64(u), u);
}

static N_FORCEINLINE float64x2_t _n_cos2_neon(float64x2_t x) {
  float64x2_t r; int32x2_t q;
  _n_reduce2_neon(x, &r, &q);
  float64x2_t m4 = _n_quad_mod4_neon(q);
  uint64x2_t odd = _n_is_odd2_neon(m4);
  float64x2_t u = vbslq_f64(odd, _n_sin_poly2_neon(r), _n_cos_poly2_neon(r));
  uint64x2_t neg = vandq_u64(vcgeq_f64(m4, vdupq_n_f64(1.0)), vcltq_f64(m4, vdupq_n_f64(3.0)));
  return vbslq_f64(neg, vnegq_f64(u), u);
}

static N_FORCEINLINE float64x2_t _n_exp2_neon_hi(float64x2_t v) {
  return _n_exp2_neon(v);
}

static N_FORCEINLINE float64x2_t _n_exp2d_neon(float64x2_t v) {
  return _n_exp2_neon(v);
}

static N_FORCEINLINE float64x2_t _n_atan2d_neon(float64x2_t x) {
  const float64x2_t one = vdupq_n_f64(1.0);
  const float64x2_t t8 = vdupq_n_f64(0.41421356237309503);
  float64x2_t ax = vabsq_f64(x);
  uint64x2_t big = vcgtq_f64(ax, one);
  float64x2_t t = vbslq_f64(big, vdivq_f64(one, ax), ax);
  uint64x2_t mid = vcgtq_f64(t, t8);
  float64x2_t u = vbslq_f64(mid, vdivq_f64(vsubq_f64(t, one), vaddq_f64(t, one)), t);

  float64x2_t z = vmulq_f64(u, u);
  float64x2_t q = vdupq_n_f64(0.0050311800327567501);
  q = vfmaq_f64(vdupq_n_f64(0.012939895549208824), q, z);
  q = vfmaq_f64(vdupq_n_f64(0.022542847412336964), q, z);
  q = vfmaq_f64(vdupq_n_f64(0.0084661109042136508), q, z);
  q = vfmaq_f64(vdupq_n_f64(-0.051163910574811677), q, z);
  q = vfmaq_f64(vdupq_n_f64(0.074302501400929838), q, z);
  q = vfmaq_f64(vdupq_n_f64(-0.090650342969366443), q, z);
  q = vfmaq_f64(vdupq_n_f64(0.11109618177625467), q, z);
  q = vfmaq_f64(vdupq_n_f64(-0.14285666574105049), q, z);
  q = vfmaq_f64(vdupq_n_f64(0.19999999262220255), q, z);
  q = vfmaq_f64(vdupq_n_f64(-0.33333333329431702), q, z);
  float64x2_t r = vfmaq_f64(u, vmulq_f64(u, z), q);
  r = vaddq_f64(r, vbslq_f64(mid, vdupq_n_f64(0.78539816339744830962), vdupq_n_f64(0.0)));
  r = vbslq_f64(big, vsubq_f64(vdupq_n_f64(1.5707963267948966192), r), r);
  return vbslq_f64(vcgtq_f64(x, vdupq_n_f64(0.0)), r, vnegq_f64(r));
}

static N_FORCEINLINE float64x2_t _n_atan2_2_neon(float64x2_t y, float64x2_t x) {
  const float64x2_t zero = vdupq_n_f64(0.0);
  const float64x2_t pi = vdupq_n_f64(3.14159265358979323846);
  float64x2_t r = _n_atan2d_neon(vdivq_f64(y, x));
  float64x2_t adj = vbslq_f64(vcgeq_f64(y, zero), pi, vnegq_f64(pi));
  r = vbslq_f64(vcltq_f64(x, zero), vaddq_f64(r, adj), r);
  uint64x2_t bothZero = vandq_u64(vceqq_f64(x, zero), vceqq_f64(y, zero));
  return vbslq_f64(bothZero, zero, r);
}

static N_FORCEINLINE float64x2_t _n_exp2d_exp_neon(float64x2_t x) {
  float64x2_t res = _n_exp2d_neon(vmulq_f64(x, vdupq_n_f64(1.4426950408889634)));
  // 定义域：+inf -> +inf，-inf -> 0，NaN 透传
  res = vbslq_f64(vcgtq_f64(x, vdupq_n_f64(709.78)), vdupq_n_f64(INFINITY), res);
  res = vbslq_f64(vcltq_f64(x, vdupq_n_f64(-745.2)), vdupq_n_f64(0.0), res);
  return vbslq_f64(vreinterpretq_u64_u32(vmvnq_u32(vreinterpretq_u32_u64(vceqq_f64(x, x)))), x, res);
}

static N_FORCEINLINE float64x2_t _n_log2d_neon(float64x2_t d) {
  float64x2_t t = vsubq_f64(d, vdupq_n_f64(1.0));
  float64x2_t at = vabsq_f64(t);
  float64x2_t s = vdupq_n_f64(1.0 / 13.0);
  s = vfmaq_f64(vdupq_n_f64(-1.0 / 12.0), s, t);
  s = vfmaq_f64(vdupq_n_f64(1.0 / 11.0), s, t);
  s = vfmaq_f64(vdupq_n_f64(-1.0 / 10.0), s, t);
  s = vfmaq_f64(vdupq_n_f64(1.0 / 9.0), s, t);
  s = vfmaq_f64(vdupq_n_f64(-1.0 / 8.0), s, t);
  s = vfmaq_f64(vdupq_n_f64(1.0 / 7.0), s, t);
  s = vfmaq_f64(vdupq_n_f64(-1.0 / 6.0), s, t);
  s = vfmaq_f64(vdupq_n_f64(1.0 / 5.0), s, t);
  s = vfmaq_f64(vdupq_n_f64(-1.0 / 4.0), s, t);
  s = vfmaq_f64(vdupq_n_f64(1.0 / 3.0), s, t);
  s = vfmaq_f64(vdupq_n_f64(-1.0 / 2.0), s, t);
  s = vfmaq_f64(vdupq_n_f64(1.0), s, t);
  float64x2_t near = vmulq_f64(t, s);

  uint64x2_t bits = vreinterpretq_u64_f64(d);
  int64x2_t e = vsubq_s64(vreinterpretq_s64_u64(vshrq_n_u64(bits, 52)), vdupq_n_s64(1023));
  float64x2_t m = vreinterpretq_f64_u64(vorrq_u64(vandq_u64(bits, vdupq_n_u64(0x000fffffffffffffULL)),
                                                    vdupq_n_u64(0x3ff0000000000000ULL)));
  float64x2_t x = vdivq_f64(vsubq_f64(m, vdupq_n_f64(1.0)), vaddq_f64(m, vdupq_n_f64(1.0)));
  float64x2_t x2 = vmulq_f64(x, x);
  float64x2_t p = vdupq_n_f64(1.0 / 13.0);
  p = vfmaq_f64(vdupq_n_f64(1.0 / 11.0), p, x2);
  p = vfmaq_f64(vdupq_n_f64(1.0 / 9.0), p, x2);
  p = vfmaq_f64(vdupq_n_f64(1.0 / 7.0), p, x2);
  p = vfmaq_f64(vdupq_n_f64(1.0 / 5.0), p, x2);
  p = vfmaq_f64(vdupq_n_f64(1.0 / 3.0), p, x2);
  p = vfmaq_f64(vdupq_n_f64(1.0), p, x2);
  float64x2_t ef = vcvtq_f64_s64(e);
  float64x2_t wide = vmulq_f64(ef, vdupq_n_f64(0.69314718055994530942));
  wide = vfmaq_f64(wide, vmulq_f64(vmulq_f64(x, p), vdupq_n_f64(2.0)), vdupq_n_f64(1.0));
  float64x2_t res = vbslq_f64(vcleq_f64(at, vdupq_n_f64(0.25)), near, wide);
  return res;
}

static N_FORCEINLINE float64x2_t _n_log2d_base2_neon(float64x2_t d) {
  uint64x2_t bits = vreinterpretq_u64_f64(d);
  int64x2_t e = vsubq_s64(vreinterpretq_s64_u64(vshrq_n_u64(bits, 52)), vdupq_n_s64(1023));
  float64x2_t m = vreinterpretq_f64_u64(vorrq_u64(vandq_u64(bits, vdupq_n_u64(0x000fffffffffffffULL)),
                                                    vdupq_n_u64(0x3ff0000000000000ULL)));
  float64x2_t ef = vcvtq_f64_s64(e);
  return vaddq_f64(ef, _n_log2_mant_neon(m));
}

static N_FORCEINLINE n_float _n_high_split2(n_float x, float64x2_t (*fn)(float64x2_t)) {
  float64x2_t lo = fn(vcvt_f64_f32(vget_low_f32(x)));
  float64x2_t hi = fn(vcvt_f64_f32(vget_high_f32(x)));
  return vcombine_f32(vcvt_f32_f64(lo), vcvt_f32_f64(hi));
}

static N_FORCEINLINE n_float _n_high_binary2(n_float a, n_float b, float64x2_t (*fn)(float64x2_t, float64x2_t)) {
  float64x2_t lo = fn(vcvt_f64_f32(vget_low_f32(a)), vcvt_f64_f32(vget_low_f32(b)));
  float64x2_t hi = fn(vcvt_f64_f32(vget_high_f32(a)), vcvt_f64_f32(vget_high_f32(b)));
  return vcombine_f32(vcvt_f32_f64(lo), vcvt_f32_f64(hi));
}

static N_FORCEINLINE float64x2_t _n_tan2d_neon(float64x2_t x) {
  return vdivq_f64(_n_sin2_neon(x), _n_cos2_neon(x));
}

static N_FORCEINLINE float64x2_t _n_asin2d_neon(float64x2_t x) {
  float64x2_t one = vdupq_n_f64(1.0);
  float64x2_t t = vsqrtq_f64(vmaxq_f64(vmulq_f64(vsubq_f64(one, x), vaddq_f64(one, x)), vdupq_n_f64(0.0)));
  return vmulq_f64(vdupq_n_f64(2.0), _n_atan2_2_neon(x, vaddq_f64(one, t)));
}

static N_FORCEINLINE float64x2_t _n_acos2d_neon(float64x2_t x) {
  float64x2_t one = vdupq_n_f64(1.0);
  float64x2_t t = vsqrtq_f64(vmaxq_f64(vmulq_f64(vsubq_f64(one, x), vaddq_f64(one, x)), vdupq_n_f64(0.0)));
  float64x2_t r = vmulq_f64(vdupq_n_f64(2.0), _n_atan2_2_neon(t, vaddq_f64(one, x)));
  return vbslq_f64(vcleq_f64(x, vdupq_n_f64(-1.0)), vdupq_n_f64(3.14159265358979323846), r);
}

static N_FORCEINLINE float64x2_t _n_sinh2d_neon(float64x2_t x) {
  float64x2_t e = _n_exp2d_exp_neon(x);
  float64x2_t inv = _n_exp2d_exp_neon(vnegq_f64(x));
  return vmulq_f64(vdupq_n_f64(0.5), vsubq_f64(e, inv));
}

static N_FORCEINLINE float64x2_t _n_cosh2d_neon(float64x2_t x) {
  float64x2_t e = _n_exp2d_exp_neon(x);
  float64x2_t inv = _n_exp2d_exp_neon(vnegq_f64(x));
  return vmulq_f64(vdupq_n_f64(0.5), vaddq_f64(e, inv));
}

static N_FORCEINLINE float64x2_t _n_tanh2d_neon(float64x2_t x) {
  float64x2_t ax = vabsq_f64(x);
  float64x2_t xc = vbslq_f64(vcgtq_f64(ax, vdupq_n_f64(20.0)),
                             vreinterpretq_f64_u64(vorrq_u64(vreinterpretq_u64_f64(vdupq_n_f64(20.0)),
                                                             vandq_u64(vreinterpretq_u64_f64(x), vdupq_n_u64(0x8000000000000000ULL)))),
                             x);
  float64x2_t e2 = _n_exp2d_exp_neon(vmulq_f64(vdupq_n_f64(2.0), xc));
  return vdivq_f64(vsubq_f64(e2, vdupq_n_f64(1.0)), vaddq_f64(e2, vdupq_n_f64(1.0)));
}

static N_FORCEINLINE float64x2_t _n_log10_2d_neon(float64x2_t x) {
  return vmulq_f64(_n_log2d_neon(x), vdupq_n_f64(0.4342944819032518));
}

static N_FORCEINLINE n_float _n_high_sin_neon(n_float x) { return _n_high_split2(x, _n_sin2_neon); }
static N_FORCEINLINE n_float _n_high_cos_neon(n_float x) { return _n_high_split2(x, _n_cos2_neon); }
static N_FORCEINLINE n_float _n_high_tan_neon(n_float x) { return _n_high_split2(x, _n_tan2d_neon); }
static N_FORCEINLINE n_float _n_high_atan_neon(n_float x) { return _n_high_split2(x, _n_atan2d_neon); }
static N_FORCEINLINE n_float _n_high_asin_neon(n_float x) { return _n_high_split2(x, _n_asin2d_neon); }
static N_FORCEINLINE n_float _n_high_acos_neon(n_float x) { return _n_high_split2(x, _n_acos2d_neon); }
static N_FORCEINLINE n_float _n_high_exp_neon(n_float x) { return _n_high_split2(x, _n_exp2d_exp_neon); }
static N_FORCEINLINE n_float _n_high_log_neon(n_float x) { return _n_high_split2(x, _n_log2d_neon); }
static N_FORCEINLINE n_float _n_high_log10_neon(n_float x) { return _n_high_split2(x, _n_log10_2d_neon); }
static N_FORCEINLINE void _n_high_sincos_neon(n_float x, n_float* s, n_float* c) {
  *s = _n_high_sin_neon(x);
  *c = _n_high_cos_neon(x);
}

static N_FORCEINLINE n_float _n_high_sinh_neon(n_float x) { return _n_high_split2(x, _n_sinh2d_neon); }
static N_FORCEINLINE n_float _n_high_cosh_neon(n_float x) { return _n_high_split2(x, _n_cosh2d_neon); }
static N_FORCEINLINE n_float _n_high_tanh_neon(n_float x) { return _n_high_split2(x, _n_tanh2d_neon); }
static N_FORCEINLINE n_float _n_high_atan2_neon(n_float y, n_float x) { return _n_high_binary2(y, x, _n_atan2_2_neon); }

#endif // NSIMD_NEON && __aarch64__

// ================================================================
// SSE4.1 (4-wide, no FMA) inline math -- Fastest tier.
// Same algorithms as the AVX2/NEON kernels: argument reduction in
// double lanes (2 x __m128d), polynomials in float lanes evaluated
// with mul+add because SSE4.1 has no FMA.
// ================================================================
#if defined(NSIMD_SSE4)

// SSE2 原语实现：SSE4.1 的 round/blend 在 /arch:SSE2 下不可用
static N_FORCEINLINE __m128d _n_round_nearest_pd(__m128d v) {
  return _mm_cvtepi32_pd(_mm_cvtpd_epi32(v));
}

static N_FORCEINLINE __m128 _n_round_nearest_ps(__m128 v) {
  return _mm_cvtepi32_ps(_mm_cvtps_epi32(v));
}

static N_FORCEINLINE __m128 _n_trunc_ps(__m128 v) {
  return _mm_cvtepi32_ps(_mm_cvttps_epi32(v));
}

static N_FORCEINLINE __m128 _n_blend_sse41(__m128 f, __m128 t, __m128 m) {
  return _mm_or_ps(_mm_and_ps(m, t), _mm_andnot_ps(m, f));
}

static N_FORCEINLINE __m128 _n_abs_sse41(__m128 a) {
  return _mm_and_ps(a, _mm_castsi128_ps(_mm_set1_epi32(0x7fffffff)));
}

static N_FORCEINLINE __m128d _n_neg_pd(__m128d a) {
  return _mm_sub_pd(_mm_setzero_pd(), a);
}

// 无 FMA：a + b*c 走两次运算
static N_FORCEINLINE __m128d _n_madd_pd(__m128d a, __m128d b, __m128d c) {
  return _mm_add_pd(a, _mm_mul_pd(b, c));
}

static N_FORCEINLINE __m128 _n_madd_ps(__m128 a, __m128 b, __m128 c) {
  return _mm_add_ps(a, _mm_mul_ps(b, c));
}

static N_FORCEINLINE void _n_reduce_sse41(__m128 d, __m128* rOut, __m128i* qOut) {
  __m128d dlo = _mm_cvtps_pd(d);
  __m128d dhi = _mm_cvtps_pd(_mm_movehl_ps(d, d));
  const __m128d twoOverPi = _mm_set1_pd(0.63661977236758134308);
  __m128d qlo = _n_round_nearest_pd(_mm_mul_pd(dlo, twoOverPi));
  __m128d qhi = _n_round_nearest_pd(_mm_mul_pd(dhi, twoOverPi));
  // 无 FMA：首段只保留 29 位有效位，使 |q| < 2^23 时 q*pio2a 与 x-q*pio2a 都精确
  const __m128d pio2a = _mm_set1_pd(1.57079632580280303955e+00);
  const __m128d pio2b = _mm_set1_pd(9.92093577428798667484e-10);
  const __m128d pio2c = _mm_set1_pd(2.25174177415621760794e-18);
  __m128d rlo = _n_madd_pd(dlo, _n_neg_pd(qlo), pio2a);
  __m128d rhi = _n_madd_pd(dhi, _n_neg_pd(qhi), pio2a);
  rlo = _n_madd_pd(rlo, _n_neg_pd(qlo), pio2b);
  rhi = _n_madd_pd(rhi, _n_neg_pd(qhi), pio2b);
  rlo = _n_madd_pd(rlo, _n_neg_pd(qlo), pio2c);
  rhi = _n_madd_pd(rhi, _n_neg_pd(qhi), pio2c);
  *rOut = _mm_movelh_ps(_mm_cvtpd_ps(rlo), _mm_cvtpd_ps(rhi));
  __m128i qloi = _mm_cvtpd_epi32(qlo);
  __m128i qhii = _mm_cvtpd_epi32(qhi);
  *qOut = _mm_unpacklo_epi64(qloi, qhii);
}

static N_FORCEINLINE __m128 _n_sin_poly_sse41(__m128 r) {
  __m128 z = _mm_mul_ps(r, r);
  __m128 p = _mm_set1_ps(-2.50521083854417187751e-8f);
  p = _n_madd_ps(_mm_set1_ps(2.75573192239858925109e-6f), p, z);
  p = _n_madd_ps(_mm_set1_ps(-1.98412698412698412522e-4f), p, z);
  p = _n_madd_ps(_mm_set1_ps(8.33333333333333321769e-3f), p, z);
  p = _n_madd_ps(_mm_set1_ps(-1.66666666666666657415e-1f), p, z);
  return _n_madd_ps(r, _mm_mul_ps(z, r), p);
}

static N_FORCEINLINE __m128 _n_cos_poly_sse41(__m128 r) {
  __m128 z = _mm_mul_ps(r, r);
  __m128 p = _mm_set1_ps(2.4801587301587301587e-5f);
  p = _n_madd_ps(_mm_set1_ps(-1.3888888888888888889e-3f), p, z);
  p = _n_madd_ps(_mm_set1_ps(4.1666666666666666667e-2f), p, z);
  p = _n_madd_ps(_mm_set1_ps(-5.0e-1f), p, z);
  return _n_madd_ps(_mm_set1_ps(1.0f), p, z);
}

static N_FORCEINLINE __m128 _n_sin_sse41(__m128 d) {
  __m128 r; __m128i q;
  _n_reduce_sse41(d, &r, &q);
  __m128 mOdd = _mm_castsi128_ps(_mm_cmpeq_epi32(_mm_and_si128(q, _mm_set1_epi32(1)), _mm_set1_epi32(1)));
  __m128 u = _n_blend_sse41(_n_sin_poly_sse41(r), _n_cos_poly_sse41(r), mOdd);
  __m128 mFlip = _mm_castsi128_ps(_mm_cmpeq_epi32(_mm_and_si128(q, _mm_set1_epi32(2)), _mm_set1_epi32(2)));
  __m128 res = _n_blend_sse41(u, _mm_sub_ps(_mm_setzero_ps(), u), mFlip);
  if (_mm_movemask_ps(_mm_cmpge_ps(_n_abs_sse41(d), _mm_set1_ps(1.0e7f))) != 0) {
    float lane[4]; _mm_storeu_ps(lane, d);
    for (int i = 0; i < 4; i++) lane[i] = ::sinf(lane[i]);
    res = _mm_loadu_ps(lane);
  }
  return res;
}

static N_FORCEINLINE __m128 _n_cos_sse41(__m128 d) {
  __m128 r; __m128i q;
  _n_reduce_sse41(d, &r, &q);
  __m128 mOdd = _mm_castsi128_ps(_mm_cmpeq_epi32(_mm_and_si128(q, _mm_set1_epi32(1)), _mm_set1_epi32(1)));
  __m128 u = _n_blend_sse41(_n_cos_poly_sse41(r), _n_sin_poly_sse41(r), mOdd);
  __m128i flip = _mm_and_si128(_mm_add_epi32(q, _mm_set1_epi32(1)), _mm_set1_epi32(2));
  __m128 mFlip = _mm_castsi128_ps(_mm_cmpeq_epi32(flip, _mm_set1_epi32(2)));
  __m128 res = _n_blend_sse41(u, _mm_sub_ps(_mm_setzero_ps(), u), mFlip);
  if (_mm_movemask_ps(_mm_cmpge_ps(_n_abs_sse41(d), _mm_set1_ps(1.0e7f))) != 0) {
    float lane[4]; _mm_storeu_ps(lane, d);
    for (int i = 0; i < 4; i++) lane[i] = ::cosf(lane[i]);
    res = _mm_loadu_ps(lane);
  }
  return res;
}

static N_FORCEINLINE __m128 _n_exp_sse41(__m128 d) {
  __m128 n = _n_round_nearest_ps(_mm_mul_ps(d, _mm_set1_ps(1.4426950408889634f)));
  __m128i ni = _mm_cvtps_epi32(n);
  __m128 r = _n_madd_ps(d, _mm_sub_ps(_mm_setzero_ps(), n), _mm_set1_ps(0.693145751953125f));
  r = _n_madd_ps(r, _mm_sub_ps(_mm_setzero_ps(), n), _mm_set1_ps(1.428606765330187045e-6f));
  __m128 p = _mm_set1_ps(1.0f / 5040.0f);
  p = _n_madd_ps(_mm_set1_ps(1.0f / 720.0f), p, r);
  p = _n_madd_ps(_mm_set1_ps(1.0f / 120.0f), p, r);
  p = _n_madd_ps(_mm_set1_ps(1.0f / 24.0f), p, r);
  p = _n_madd_ps(_mm_set1_ps(1.0f / 6.0f), p, r);
  p = _n_madd_ps(_mm_set1_ps(0.5f), p, r);
  p = _n_madd_ps(_mm_set1_ps(1.0f), p, r);
  p = _n_madd_ps(_mm_set1_ps(1.0f), p, r);
  __m128 nc = _mm_max_ps(_mm_min_ps(n, _mm_set1_ps(127.0f)), _mm_set1_ps(-126.0f));
  __m128i hi = _mm_cvtps_epi32(nc);
  __m128i lo = _mm_sub_epi32(ni, hi);
  __m128 s1 = _mm_castsi128_ps(_mm_slli_epi32(_mm_add_epi32(hi, _mm_set1_epi32(127)), 23));
  __m128 lof = _mm_cvtepi32_ps(lo);
  __m128 lc = _mm_max_ps(_mm_min_ps(lof, _mm_set1_ps(127.0f)), _mm_set1_ps(-126.0f));
  __m128 s2 = _mm_castsi128_ps(_mm_slli_epi32(_mm_add_epi32(_mm_cvtps_epi32(lc), _mm_set1_epi32(127)), 23));
  __m128 result = _mm_mul_ps(_mm_mul_ps(p, s1), s2);
  result = _n_blend_sse41(result, _mm_set1_ps(INFINITY), _mm_cmpgt_ps(d, _mm_set1_ps(88.722839f)));
  return _n_blend_sse41(result, _mm_setzero_ps(), _mm_cmplt_ps(d, _mm_set1_ps(-103.972084f)));
}

static N_FORCEINLINE __m128 _n_log_sse41(__m128 d) {
  // 次正规输入先放大 2^24 再取对数，末尾补回 24*ln2
  __m128 sub = _mm_cmplt_ps(d, _mm_set1_ps(1.17549435e-38f));
  d = _n_blend_sse41(d, _mm_mul_ps(d, _mm_set1_ps(16777216.0f)), sub);
  __m128 t = _mm_sub_ps(d, _mm_set1_ps(1.0f));
  __m128 at = _n_abs_sse41(t);

  __m128 s = _mm_set1_ps(1.0f / 13.0f);
  s = _n_madd_ps(_mm_set1_ps(-1.0f / 12.0f), s, t);
  s = _n_madd_ps(_mm_set1_ps(1.0f / 11.0f), s, t);
  s = _n_madd_ps(_mm_set1_ps(-1.0f / 10.0f), s, t);
  s = _n_madd_ps(_mm_set1_ps(1.0f / 9.0f), s, t);
  s = _n_madd_ps(_mm_set1_ps(-1.0f / 8.0f), s, t);
  s = _n_madd_ps(_mm_set1_ps(1.0f / 7.0f), s, t);
  s = _n_madd_ps(_mm_set1_ps(-1.0f / 6.0f), s, t);
  s = _n_madd_ps(_mm_set1_ps(1.0f / 5.0f), s, t);
  s = _n_madd_ps(_mm_set1_ps(-1.0f / 4.0f), s, t);
  s = _n_madd_ps(_mm_set1_ps(1.0f / 3.0f), s, t);
  s = _n_madd_ps(_mm_set1_ps(-1.0f / 2.0f), s, t);
  s = _n_madd_ps(_mm_set1_ps(1.0f), s, t);
  __m128 near = _mm_mul_ps(t, s);

  __m128i k = _mm_castps_si128(d);
  __m128i e = _mm_sub_epi32(_mm_srli_epi32(k, 23), _mm_set1_epi32(127));
  __m128 m = _mm_castsi128_ps(_mm_or_si128(_mm_and_si128(k, _mm_set1_epi32(0x807fffff)), _mm_set1_epi32(0x3f800000)));
  __m128 x = _mm_div_ps(_mm_sub_ps(m, _mm_set1_ps(1.0f)), _mm_add_ps(m, _mm_set1_ps(1.0f)));
  __m128 x2 = _mm_mul_ps(x, x);
  __m128 p = _mm_set1_ps(0.15384615384615385f);
  p = _n_madd_ps(_mm_set1_ps(0.18181818181818182f), p, x2);
  p = _n_madd_ps(_mm_set1_ps(0.2222222222222222f), p, x2);
  p = _n_madd_ps(_mm_set1_ps(0.2857142857142857f), p, x2);
  p = _n_madd_ps(_mm_set1_ps(0.4f), p, x2);
  p = _n_madd_ps(_mm_set1_ps(0.6666666666666666f), p, x2);
  p = _n_madd_ps(_mm_set1_ps(2.0f), p, x2);
  __m128 ef = _mm_cvtepi32_ps(e);
  __m128 wide = _mm_mul_ps(ef, _mm_set1_ps(0.693145751953125f));
  wide = _n_madd_ps(wide, ef, _mm_set1_ps(1.428606765330187045e-6f));
  wide = _n_madd_ps(wide, x, p);
  __m128 res = _n_blend_sse41(wide, near, _mm_cmple_ps(at, _mm_set1_ps(0.25f)));
  // 定义域：0 -> -inf，负数 -> NaN，+inf -> +inf
  // NaN 必须原样透传：NaN 的指数域是 255，按位取尾数会算出有限值
  return _mm_sub_ps(res, _mm_and_ps(sub, _mm_set1_ps(16.635532333438687f)));
}

static N_FORCEINLINE __m128 _n_log10_sse41(__m128 d) {
  return _mm_mul_ps(_n_log_sse41(d), _mm_set1_ps(0.4342944819032518f));
}

static N_FORCEINLINE __m128 _n_tan_sse41(__m128 a) {
  return _mm_div_ps(_n_sin_sse41(a), _n_cos_sse41(a));
}

static N_FORCEINLINE __m128 _n_atan_sse41(__m128 a) {
  const __m128 one = _mm_set1_ps(1.0f);
  const __m128 zero = _mm_setzero_ps();
  __m128 ax = _n_abs_sse41(a);
  __m128 big = _mm_cmpgt_ps(ax, one);
  __m128 t = _n_blend_sse41(ax, _mm_div_ps(one, ax), big);
  __m128 z = _mm_mul_ps(t, t);
  __m128 q = _mm_set1_ps(2.922975910e-03f);
  q = _n_madd_ps(_mm_set1_ps(-1.637927326e-02f), q, z);
  q = _n_madd_ps(_mm_set1_ps(4.323442722e-02f), q, z);
  q = _n_madd_ps(_mm_set1_ps(-7.554515826e-02f), q, z);
  q = _n_madd_ps(_mm_set1_ps(1.066727714e-01f), q, z);
  q = _n_madd_ps(_mm_set1_ps(-1.421142270e-01f), q, z);
  q = _n_madd_ps(_mm_set1_ps(1.999382071e-01f), q, z);
  q = _n_madd_ps(_mm_set1_ps(-3.333315467e-01f), q, z);
  __m128 r = _n_madd_ps(t, _mm_mul_ps(_mm_mul_ps(t, t), t), q);
  r = _n_blend_sse41(r, _mm_sub_ps(_mm_set1_ps(1.5707963267948966f), r), big);
  return _n_blend_sse41(r, _mm_sub_ps(zero, r), _mm_cmplt_ps(a, zero));
}

static N_FORCEINLINE __m128 _n_atan2_sse41(__m128 y, __m128 x) {
  const __m128 zero = _mm_setzero_ps();
  const __m128 pi = _mm_set1_ps(3.14159265358979323846f);
  __m128 r = _n_atan_sse41(_mm_div_ps(y, x));
  __m128 adj = _n_blend_sse41(_mm_sub_ps(zero, pi), pi, _mm_cmpge_ps(y, zero));
  r = _n_blend_sse41(r, _mm_add_ps(r, adj), _mm_cmplt_ps(x, zero));
  // y = ±0 且 x > 0 时结果为 ±0
  __m128 yZeroPos = _mm_and_ps(_mm_cmpeq_ps(y, zero), _mm_cmpgt_ps(x, zero));
  r = _n_blend_sse41(r, _mm_and_ps(y, _mm_set1_ps(-0.0f)), yZeroPos);
  __m128 bothZero = _mm_and_ps(_mm_cmpeq_ps(x, zero), _mm_cmpeq_ps(y, zero));
  return _n_blend_sse41(r, zero, bothZero);
}

static N_FORCEINLINE __m128 _n_asin_sse41(__m128 a) {
  const __m128 one = _mm_set1_ps(1.0f);
  __m128 t = _mm_sqrt_ps(_mm_max_ps(_mm_mul_ps(_mm_sub_ps(one, a), _mm_add_ps(one, a)), _mm_setzero_ps()));
  return _mm_mul_ps(_mm_set1_ps(2.0f), _n_atan2_sse41(a, _mm_add_ps(one, t)));
}

static N_FORCEINLINE __m128 _n_acos_sse41(__m128 a) {
  const __m128 one = _mm_set1_ps(1.0f);
  __m128 t = _mm_sqrt_ps(_mm_max_ps(_mm_mul_ps(_mm_sub_ps(one, a), _mm_add_ps(one, a)), _mm_setzero_ps()));
  __m128 r = _mm_mul_ps(_mm_set1_ps(2.0f), _n_atan2_sse41(t, _mm_add_ps(one, a)));
  return _n_blend_sse41(r, _mm_set1_ps(3.14159265358979323846f), _mm_cmple_ps(a, _mm_set1_ps(-1.0f)));
}

static N_FORCEINLINE __m128 _n_sinh_sse41(__m128 a) {
  __m128 e = _n_exp_sse41(a);
  __m128 inv = _n_exp_sse41(_mm_sub_ps(_mm_setzero_ps(), a));
  __m128 wide = _mm_mul_ps(_mm_set1_ps(0.5f), _mm_sub_ps(e, inv));
  __m128 z = _mm_mul_ps(a, a);
  __m128 sp = _mm_set1_ps(1.0f / 5040.0f);
  sp = _n_madd_ps(_mm_set1_ps(1.0f / 120.0f), sp, z);
  sp = _n_madd_ps(_mm_set1_ps(1.0f / 6.0f), sp, z);
  sp = _n_madd_ps(_mm_set1_ps(1.0f), sp, z);
  return _n_blend_sse41(wide, _mm_mul_ps(a, sp), _mm_cmplt_ps(_n_abs_sse41(a), _mm_set1_ps(0.25f)));
}

static N_FORCEINLINE __m128 _n_cosh_sse41(__m128 a) {
  __m128 e = _n_exp_sse41(a);
  __m128 inv = _n_exp_sse41(_mm_sub_ps(_mm_setzero_ps(), a));
  __m128 wide = _mm_mul_ps(_mm_set1_ps(0.5f), _mm_add_ps(e, inv));
  __m128 z = _mm_mul_ps(a, a);
  __m128 sp = _mm_set1_ps(1.0f / 720.0f);
  sp = _n_madd_ps(_mm_set1_ps(1.0f / 24.0f), sp, z);
  sp = _n_madd_ps(_mm_set1_ps(0.5f), sp, z);
  sp = _n_madd_ps(_mm_set1_ps(1.0f), sp, z);
  return _n_blend_sse41(wide, sp, _mm_cmplt_ps(_n_abs_sse41(a), _mm_set1_ps(0.25f)));
}

static N_FORCEINLINE __m128 _n_tanh_sse41(__m128 a) {
  __m128 t2 = _mm_mul_ps(_mm_set1_ps(2.0f), a);
  t2 = _n_blend_sse41(t2, _mm_or_ps(_mm_set1_ps(88.0f), _mm_and_ps(a, _mm_set1_ps(-0.0f))),
                      _mm_cmpgt_ps(_mm_and_ps(a, _mm_castsi128_ps(_mm_set1_epi32(0x7fffffff))), _mm_set1_ps(44.0f)));
  __m128 e2 = _n_exp_sse41(t2);
  __m128 wide = _mm_div_ps(_mm_sub_ps(e2, _mm_set1_ps(1.0f)), _mm_add_ps(e2, _mm_set1_ps(1.0f)));
  __m128 z = _mm_mul_ps(a, a);
  __m128 sp = _mm_set1_ps(62.0f / 2835.0f);
  sp = _n_madd_ps(_mm_set1_ps(-17.0f / 315.0f), sp, z);
  sp = _n_madd_ps(_mm_set1_ps(2.0f / 15.0f), sp, z);
  sp = _n_madd_ps(_mm_set1_ps(-1.0f / 3.0f), sp, z);
  sp = _n_madd_ps(_mm_set1_ps(1.0f), sp, z);
  return _n_blend_sse41(wide, _mm_mul_ps(a, sp), _mm_cmplt_ps(_n_abs_sse41(a), _mm_set1_ps(0.25f)));
}

static N_FORCEINLINE void _n_sincos_sse41(__m128 a, __m128* s, __m128* c) {
  *s = _n_sin_sse41(a);
  *c = _n_cos_sse41(a);
}

#endif // NSIMD_SSE4

// ===== Fastest (~3.5 ULP) =====
#if SIMD_MATH_PRECISION == 1

  // Inline AVX512 (16-wide) / AVX2 (8-wide) polynomial (fastest, zero function-call overhead)
  #if defined(NSIMD_AVX512)
    #define N_SIN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_sin_big((_v), _n_sin_avx512(_v))); }((a))
    #define N_COS(a) [&](n_float _v) { return _n_cos_big((_v), _n_cos_avx512(_v)); }((a))
    #define N_LOG(a) [&](n_float _v) { return _n_dom_log((_v), _n_log_avx512(_v)); }((a))
    #define N_EXP(a) [&](n_float _v) { return _n_dom_exp((_v), _n_exp_avx512(_v)); }((a))
    #define N_LOG10(a) [&](n_float _v) { return _mm512_mul_ps(N_LOG(_v), _mm512_set1_ps(0.4342944819032518f)); }((a))
    #define N_POW(a,b) [&](n_float _va, n_float _vb) { return _n_dom_pow((_va), (_vb), _n_pow_avx512(_va, _vb)); }((a), (b))
  #define N_TAN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_tan_big((_v), _n_tan_avx512(_v))); }((a))
  #define N_ASIN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_dom_asin((_v), _n_asin_avx512(_v))); }((a))
  #define N_ACOS(a) [&](n_float _v) { return _n_dom_acos((_v), _n_acos_avx512(_v)); }((a))
  #define N_ATAN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_atan_avx512(_v)); }((a))
  #define N_ATAN2(a,b) [&](n_float _va, n_float _vb) { return _n_dom_atan2((_va), (_vb), _n_atan2_avx512(_va, _vb)); }((a), (b))
  #define N_SINH(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_sinh_avx512(_v)); }((a))
  #define N_COSH(a) [&](n_float _v) { return _n_cosh_avx512(_v); }((a))
  #define N_TANH(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_dom_tanh((_v), _n_tanh_avx512(_v))); }((a))
  #define N_SINCOS(a,s,c) _n_sincos_avx512(a, s, c)
  #elif defined(NSIMD_AVX2)
    #define N_SIN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_sin_big((_v), _n_sin_avx2(_v))); }((a))
    #define N_COS(a) [&](n_float _v) { return _n_cos_big((_v), _n_cos_avx2(_v)); }((a))
    #define N_LOG(a) [&](n_float _v) { return _n_dom_log((_v), _n_log_avx2(_v)); }((a))
    #define N_EXP(a) [&](n_float _v) { return _n_dom_exp((_v), _n_exp_avx2(_v)); }((a))
    #define N_LOG10(a) [&](n_float _v) { return _mm256_mul_ps(N_LOG(_v), _mm256_set1_ps(0.4342944819032518f)); }((a))
  #elif defined(NSIMD_SSE4)
  #define N_SIN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_sin_big((_v), _n_sin_sse41(_v))); }((a))
  #define N_COS(a) [&](n_float _v) { return _n_cos_big((_v), _n_cos_sse41(_v)); }((a))
  #define N_LOG(a) [&](n_float _v) { return _n_dom_log((_v), _n_log_sse41(_v)); }((a))
  #define N_EXP(a) [&](n_float _v) { return _n_dom_exp((_v), _n_exp_sse41(_v)); }((a))
  #define N_LOG10(a) [&](n_float _v) { return _mm_mul_ps(N_LOG(_v), _mm_set1_ps(0.4342944819032518f)); }((a))
  // pow 在 SSE4 上走 libm 回退：无 FMA 时 log2/exp2 精度不足，守不住 ULP 门禁
  #define N_TAN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_tan_big((_v), _n_tan_sse41(_v))); }((a))
  #define N_ASIN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_dom_asin((_v), _n_asin_sse41(_v))); }((a))
  #define N_ACOS(a) [&](n_float _v) { return _n_dom_acos((_v), _n_acos_sse41(_v)); }((a))
  #define N_ATAN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_atan_sse41(_v)); }((a))
  #define N_ATAN2(a,b) [&](n_float _va, n_float _vb) { return _n_dom_atan2((_va), (_vb), _n_atan2_sse41(_va, _vb)); }((a), (b))
  #define N_SINH(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_sinh_sse41(_v)); }((a))
  #define N_COSH(a) [&](n_float _v) { return _n_cosh_sse41(_v); }((a))
  #define N_TANH(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_dom_tanh((_v), _n_tanh_sse41(_v))); }((a))
  #define N_SINCOS(a,s,c) _n_sincos_sse41(a, s, c)
#elif defined(NSIMD_NEON) && defined(__aarch64__)
#define N_SIN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_sin_big((_v), _n_sin_neon(_v))); }((a))
#define N_COS(a) [&](n_float _v) { return _n_cos_big((_v), _n_cos_neon(_v)); }((a))
#define N_LOG(a) [&](n_float _v) { return _n_dom_log((_v), _n_log_neon(_v)); }((a))
#define N_EXP(a) [&](n_float _v) { return _n_dom_exp((_v), _n_exp_neon(_v)); }((a))
#define N_LOG10(a) [&](n_float _v) { return vmulq_f32(N_LOG(_v), vdupq_n_f32(0.4342944819032518f)); }((a))
  #endif
#if defined(NSIMD_AVX2) && !defined(NSIMD_AVX512)
    #define N_TAN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_tan_big((_v), _n_tan_ps(_v))); }((a))
    #define N_ASIN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_dom_asin((_v), _n_asin_ps(_v))); }((a))
    #define N_ACOS(a) [&](n_float _v) { return _n_dom_acos((_v), _n_acos_ps(_v)); }((a))
    #define N_ATAN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_atan_ps(_v)); }((a))
    #define N_ATAN2(a,b) [&](n_float _va, n_float _vb) { return _n_dom_atan2((_va), (_vb), _n_atan2_ps(_va, _vb)); }((a), (b))
    #define N_SINH(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_sinh_ps(_v)); }((a))
    #define N_COSH(a) [&](n_float _v) { return _n_cosh_ps(_v); }((a))
    #define N_TANH(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_dom_tanh((_v), _n_tanh_ps(_v))); }((a))
    #define N_POW(a,b) [&](n_float _va, n_float _vb) { return _n_dom_pow((_va), (_vb), _n_pow_avx2_ps(_va, _vb)); }((a), (b))
    #define N_SINCOS(a,s,c) _n_sincos_ps(a, s, c)
#elif defined(NSIMD_NEON) && defined(__aarch64__)
    #define N_TAN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_tan_big((_v), vdivq_f32(_n_sin_neon(_v), _n_cos_neon(_v)))); }((a))
    #define N_ASIN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_dom_asin((_v), _n_asin_neon(_v))); }((a))
    #define N_ACOS(a) [&](n_float _v) { return _n_dom_acos((_v), _n_acos_neon(_v)); }((a))
    #define N_ATAN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_atan_neon(_v)); }((a))
    #define N_ATAN2(a,b) [&](n_float _va, n_float _vb) { return _n_dom_atan2((_va), (_vb), _n_atan2_neon(_va, _vb)); }((a), (b))
    #define N_SINH(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_sinh_neon(_v)); }((a))
    #define N_COSH(a) [&](n_float _v) { return _n_cosh_neon(_v); }((a))
    #define N_TANH(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_dom_tanh((_v), _n_tanh_neon(_v))); }((a))
    #define N_POW(a,b) [&](n_float _va, n_float _vb) { return _n_dom_pow((_va), (_vb), _n_pow_neon(_va, _vb)); }((a), (b))
    #define N_SINCOS(a,s,c) _n_sincos_neon(a, s, c)
#endif
  // Non-AVX2: #ifndef guards below provide per-lane scalar fallback

// NEON High 档：double lane 实现（1 ULP 级），与 AVX2 的 4×double 内核同算法。
// 输入 float32x4 拆成两组 float64x2，算完折回。

// ===== High (~1.0 ULP) =====
#elif SIMD_MATH_PRECISION == 2
  #if defined(NSIMD_AVX512)
    #define N_SIN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_sin_big((_v), _n_high_sin_avx512(_v))); }((a))
    #define N_COS(a) [&](n_float _v) { return _n_cos_big((_v), _n_high_cos_avx512(_v)); }((a))
    #define N_TAN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_tan_big((_v), _n_high_tan_avx512(_v))); }((a))
    #define N_ASIN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_dom_asin((_v), _n_high_asin_avx512(_v))); }((a))
    #define N_ACOS(a) [&](n_float _v) { return _n_dom_acos((_v), _n_high_acos_avx512(_v)); }((a))
    #define N_ATAN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_high_atan_avx512(_v)); }((a))
    #define N_ATAN2(a,b) [&](n_float _va, n_float _vb) { return _n_dom_atan2((_va), (_vb), _n_high_atan2_avx512(_va, _vb)); }((a), (b))
    #define N_SINH(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_high_sinh_avx512(_v)); }((a))
    #define N_COSH(a) [&](n_float _v) { return _n_high_cosh_avx512(_v); }((a))
    #define N_TANH(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_dom_tanh((_v), _n_high_tanh_avx512(_v))); }((a))
    #define N_EXP(a) [&](n_float _v) { return _n_dom_exp((_v), _n_high_exp_avx512(_v)); }((a))
    #define N_LOG(a) [&](n_float _v) { return _n_dom_log((_v), _n_high_log_avx512(_v)); }((a))
    #define N_LOG10(a) [&](n_float _v) { return _n_dom_log((_v), [&](n_float _v) { return _n_high_log10_avx512(_v); }((_v))); }((a))
    #define N_POW(a,b) [&](n_float _va, n_float _vb) { return _n_dom_pow((_va), (_vb), _n_pow_avx512(_va, _vb)); }((a), (b))
    #define N_SINCOS(a,s,c) _n_high_sincos_avx512(a, s, c)
  #elif defined(NSIMD_SSE4)
    // SSE4.1 无 FMA、只有 4 lane：double lane 方案实测比 libm 慢（0.32–0.79x），
    // 因此 High 档复用 float 域实现（1–3 ULP），仍快于逐 lane libm。
    #define N_SIN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_sin_big((_v), _n_sin_sse41(_v))); }((a))
    #define N_COS(a) [&](n_float _v) { return _n_cos_big((_v), _n_cos_sse41(_v)); }((a))
    #define N_TAN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_tan_big((_v), _n_tan_sse41(_v))); }((a))
    #define N_ASIN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_dom_asin((_v), _n_asin_sse41(_v))); }((a))
    #define N_ACOS(a) [&](n_float _v) { return _n_dom_acos((_v), _n_acos_sse41(_v)); }((a))
    #define N_ATAN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_atan_sse41(_v)); }((a))
    #define N_ATAN2(a,b) [&](n_float _va, n_float _vb) { return _n_dom_atan2((_va), (_vb), _n_atan2_sse41(_va, _vb)); }((a), (b))
    #define N_SINH(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_sinh_sse41(_v)); }((a))
    #define N_COSH(a) [&](n_float _v) { return _n_cosh_sse41(_v); }((a))
    #define N_TANH(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_dom_tanh((_v), _n_tanh_sse41(_v))); }((a))
    #define N_EXP(a) [&](n_float _v) { return _n_dom_exp((_v), _n_exp_sse41(_v)); }((a))
    #define N_LOG(a) [&](n_float _v) { return _n_dom_log((_v), _n_log_sse41(_v)); }((a))
    #define N_LOG10(a) [&](n_float _v) { return _n_dom_log((_v), [&](n_float _v) { return _n_log10_sse41(_v); }((_v))); }((a))
  // pow 在 SSE4 上走 libm 回退（无 FMA 时 log2/exp2 精度不足）
    #define N_SINCOS(a,s,c) _n_sincos_sse41(a, s, c)
  #elif defined(NSIMD_NEON) && defined(__aarch64__)
    #define N_SIN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_sin_big((_v), _n_high_sin_neon(_v))); }((a))
    #define N_COS(a) [&](n_float _v) { return _n_cos_big((_v), _n_high_cos_neon(_v)); }((a))
    #define N_TAN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_tan_big((_v), _n_high_tan_neon(_v))); }((a))
    #define N_ASIN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_dom_asin((_v), _n_high_asin_neon(_v))); }((a))
    #define N_ACOS(a) [&](n_float _v) { return _n_dom_acos((_v), _n_high_acos_neon(_v)); }((a))
    #define N_ATAN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_high_atan_neon(_v)); }((a))
    #define N_ATAN2(a,b) [&](n_float _va, n_float _vb) { return _n_dom_atan2((_va), (_vb), _n_high_atan2_neon(_va, _vb)); }((a), (b))
    #define N_SINH(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_high_sinh_neon(_v)); }((a))
    #define N_COSH(a) [&](n_float _v) { return _n_high_cosh_neon(_v); }((a))
    #define N_TANH(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_dom_tanh((_v), _n_high_tanh_neon(_v))); }((a))
    #define N_EXP(a) [&](n_float _v) { return _n_dom_exp((_v), _n_high_exp_neon(_v)); }((a))
    #define N_LOG(a) [&](n_float _v) { return _n_dom_log((_v), _n_high_log_neon(_v)); }((a))
    #define N_LOG10(a) [&](n_float _v) { return _n_dom_log((_v), [&](n_float _v) { return _n_high_log10_neon(_v); }((_v))); }((a))
    // pow 在 NEON High 上走 libm 回退：double lane 版尚未处理负底数/零底数定义域
    #define N_SINCOS(a,s,c) _n_high_sincos_neon(a, s, c)
  #elif defined(NSIMD_AVX2)
    #define N_SIN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_sin_big((_v), _n_high_sin(_v))); }((a))
    #define N_COS(a) [&](n_float _v) { return _n_cos_big((_v), _n_high_cos(_v)); }((a))
    #define N_TAN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_tan_big((_v), _n_high_tan(_v))); }((a))
    #define N_ASIN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_dom_asin((_v), _n_high_asin(_v))); }((a))
    #define N_ACOS(a) [&](n_float _v) { return _n_dom_acos((_v), _n_high_acos(_v)); }((a))
    #define N_ATAN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_high_atan(_v)); }((a))
    #define N_ATAN2(a,b) [&](n_float _va, n_float _vb) { return _n_dom_atan2((_va), (_vb), _n_high_atan2(_va, _vb)); }((a), (b))
    #define N_SINH(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_high_sinh(_v)); }((a))
    #define N_COSH(a) [&](n_float _v) { return _n_high_cosh(_v); }((a))
    #define N_TANH(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_dom_tanh((_v), _n_high_tanh(_v))); }((a))
    #define N_EXP(a) [&](n_float _v) { return _n_dom_exp((_v), _n_high_exp(_v)); }((a))
    #define N_LOG(a) [&](n_float _v) { return _n_dom_log((_v), _n_high_log(_v)); }((a))
    #define N_LOG10(a) [&](n_float _v) { return _n_dom_log((_v), [&](n_float _v) { return _n_high_log10(_v); }((_v))); }((a))
#if !defined(NSIMD_AVX512)
#define N_POW(a,b) [&](n_float _va, n_float _vb) { return _n_dom_pow((_va), (_vb), _n_pow_avx2_ps(_va, _vb)); }((a), (b))
#endif
    #define N_SINCOS(a,s,c) _n_high_sincos(a, s, c)
  #endif

// ===== IEEE (exact scalar) =====
#else
  // On NEON, round/trunc have native instructions even in IEEE mode
  #if defined(NSIMD_NEON)
    #define N_ROUND(a) [&](n_float _v) { return vrndaq_f32(_v); }((a))
    #define N_TRUNC(a) [&](n_float _v) { return vrndq_f32(_v); }((a))
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
#define N_SIN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_sin_big((_v), _n_sin_fallback(_v))); }((a))
#endif

#ifndef N_COS
static N_FORCEINLINE n_float _n_cos_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::cosf(lane[i]);
  return n_load_ps(lane);
}
#define N_COS(a) [&](n_float _v) { return _n_cos_big((_v), _n_cos_fallback(_v)); }((a))
#endif

#ifndef N_TAN
static N_FORCEINLINE n_float _n_tan_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::tanf(lane[i]);
  return n_load_ps(lane);
}
#define N_TAN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_tan_big((_v), _n_tan_fallback(_v))); }((a))
#endif

#ifndef N_ASIN
static N_FORCEINLINE n_float _n_asin_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::asinf(lane[i]);
  return n_load_ps(lane);
}
#define N_ASIN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_dom_asin((_v), _n_asin_fallback(_v))); }((a))
#endif

#ifndef N_ACOS
static N_FORCEINLINE n_float _n_acos_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::acosf(lane[i]);
  return n_load_ps(lane);
}
#define N_ACOS(a) [&](n_float _v) { return _n_dom_acos((_v), _n_acos_fallback(_v)); }((a))
#endif

#ifndef N_ATAN
static N_FORCEINLINE n_float _n_atan_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::atanf(lane[i]);
  return n_load_ps(lane);
}
#define N_ATAN(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_atan_fallback(_v)); }((a))
#endif

#ifndef N_ATAN2
static N_FORCEINLINE n_float _n_atan2_fallback(n_float a, n_float b) {
  float la[NSIMD_WIDTH], lb[NSIMD_WIDTH];
  n_store_ps(la, a); n_store_ps(lb, b);
  for (int i = 0; i < NSIMD_WIDTH; i++) la[i] = ::atan2f(la[i], lb[i]);
  return n_load_ps(la);
}
#define N_ATAN2(a,b) [&](n_float _va, n_float _vb) { return _n_dom_atan2((_va), (_vb), _n_atan2_fallback(_va, _vb)); }((a), (b))
#endif

#ifndef N_SINH
static N_FORCEINLINE n_float _n_sinh_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::sinhf(lane[i]);
  return n_load_ps(lane);
}
#define N_SINH(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_sinh_fallback(_v)); }((a))
#endif

#ifndef N_COSH
static N_FORCEINLINE n_float _n_cosh_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::coshf(lane[i]);
  return n_load_ps(lane);
}
#define N_COSH(a) [&](n_float _v) { return _n_cosh_fallback(_v); }((a))
#endif

#ifndef N_TANH
static N_FORCEINLINE n_float _n_tanh_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::tanhf(lane[i]);
  return n_load_ps(lane);
}
#define N_TANH(a) [&](n_float _v) { return _n_dom_odd_zero((_v), _n_dom_tanh((_v), _n_tanh_fallback(_v))); }((a))
#endif

#ifndef N_EXP
static N_FORCEINLINE n_float _n_exp_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::expf(lane[i]);
  return n_load_ps(lane);
}
#define N_EXP(a) [&](n_float _v) { return _n_dom_exp((_v), _n_exp_fallback(_v)); }((a))
#endif

#ifndef N_LOG
static N_FORCEINLINE n_float _n_log_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::logf(lane[i]);
  return n_load_ps(lane);
}
#define N_LOG(a) [&](n_float _v) { return _n_dom_log((_v), _n_log_fallback(_v)); }((a))
#endif

#ifndef N_LOG10
static N_FORCEINLINE n_float _n_log10_fallback(n_float a) {
  float lane[NSIMD_WIDTH]; n_store_ps(lane, a);
  for (int i = 0; i < NSIMD_WIDTH; i++) lane[i] = ::log10f(lane[i]);
  return n_load_ps(lane);
}
#define N_LOG10(a) [&](n_float _v) { return _n_dom_log((_v), [&](n_float _v) { return _n_log10_fallback(_v); }((_v))); }((a))
#endif

#ifndef N_POW
static N_FORCEINLINE n_float _n_pow_fallback(n_float a, n_float b) {
  float la[NSIMD_WIDTH], lb[NSIMD_WIDTH];
  n_store_ps(la, a); n_store_ps(lb, b);
  // 只算 |x|^y：符号由 _n_dom_pow 统一负责（其余内核同样只返回幅值）
  for (int i = 0; i < NSIMD_WIDTH; i++) la[i] = ::powf(std::fabs(la[i]), lb[i]);
  return n_load_ps(la);
}
#define N_POW(a,b) [&](n_float _va, n_float _vb) { return _n_dom_pow((_va), (_vb), _n_pow_fallback(_va, _vb)); }((a), (b))
#endif

// ================================================================
// Public API: n_sin_ps / n_cos_ps / ... (defined as macros to eliminate function call
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
#if defined(NSIMD_AVX2) && !defined(NSIMD_AVX512)
// ================================================================

static N_FORCEINLINE n_float _n_tan_ps(n_float a) {
  return n_div_ps(n_sin_ps(a), n_cos_ps(a));
}

static N_FORCEINLINE n_float _n_atan_ps(n_float a);

static N_FORCEINLINE n_float _n_atan2_ps(n_float y, n_float x) {
  n_float zero = n_set1_ps(0.0f), pi = n_set1_ps(3.1415926535897932f);
  n_float r = _n_atan_ps(n_div_ps(y, x));
  n_float adj = n_blendv_ps(n_sub_ps(zero, pi), pi, n_cmp_ge_ps(y, zero));
  r = n_blendv_ps(r, n_add_ps(r, adj), n_cmp_lt_ps(x, zero));
  // y = ±0 且 x > 0 时结果为 ±0（保留零的符号，asin(-0) 才能得到 -0）
  n_float yZeroPos = n_and_mask(n_cmp_eq_ps(y, zero), n_cmp_gt_ps(x, zero));
  r = n_blendv_ps(r, _mm256_and_ps(y, n_set1_ps(-0.0f)), yZeroPos);
  return n_blendv_ps(r, zero, n_and_mask(n_cmp_eq_ps(x, zero), n_cmp_eq_ps(y, zero)));
}
static N_FORCEINLINE n_float _n_asin_ps(n_float a) {
  n_float one = n_set1_ps(1.0f);
  n_float t = n_sqrt_ps(n_max_ps(n_mul_ps(n_sub_ps(one, a), n_add_ps(one, a)), n_set1_ps(0.0f)));
  return n_mul_ps(n_set1_ps(2.0f), _n_atan2_ps(a, n_add_ps(one, t)));
}
static N_FORCEINLINE n_float _n_acos_ps(n_float a) {
  n_float one = n_set1_ps(1.0f);
  n_float t = n_sqrt_ps(n_max_ps(n_mul_ps(n_sub_ps(one, a), n_add_ps(one, a)), n_set1_ps(0.0f)));
  n_float r = n_mul_ps(n_set1_ps(2.0f), _n_atan2_ps(t, n_add_ps(one, a)));
  return n_blendv_ps(r, n_set1_ps(3.1415926535897932f), n_cmp_le_ps(a, n_set1_ps(-1.0f)));
}static N_FORCEINLINE n_float _n_atan_ps(n_float a) {
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
  // atan(-0) = -0：结果与输入同号
  r = n_blendv_ps(r, _mm256_and_ps(a, n_set1_ps(-0.0f)), n_cmp_eq_ps(a, n_set1_ps(0.0f)));
  return n_blendv_ps(n_sub_ps(n_set1_ps(0.0f), r), r, n_cmp_gt_ps(a, n_set1_ps(0.0f)));
}
static N_FORCEINLINE n_float _n_sinh_ps(n_float a) {
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
  // |x| <= 9 用 e^2x 变形；超出时按位夹到 ±88，避免 exp 溢出把结果变成 NaN（保留 NaN 与符号）
  n_float t2 = n_mul_ps(n_set1_ps(2.0f), a);
  t2 = n_blendv_ps(t2, _mm256_or_ps(n_set1_ps(88.0f), _mm256_and_ps(a, n_set1_ps(-0.0f))),
                   n_cmp_gt_ps(_n256_abs(a), n_set1_ps(44.0f)));
  n_float e2 = n_exp_ps(t2);
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


// ================================================================
#endif
// Double-precision SLEEF (n_*_pd)
// ================================================================

#if defined(NSIMD_AVX2)
// _PIO2 constants for double
#define _N_PIO2_A_PD -1.5707963050
#define _N_PIO2_B_PD -4.37113883e-8

static N_FORCEINLINE n_double _n_sin_avx2_pd(n_double d) {
    // Range reduction: q = round(d * 2/閿?, r = d - q * 閿?2
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
#define N_SIN_PD(a) [&](n_double _v) { return _n_sin_avx2_pd(_v); }((a))
#define N_COS_PD(a) _n_sin_avx2_pd(_mm256_add_pd(a, _mm256_set1_pd(1.5707963267948966)))

#elif defined(NSIMD_AVX512)
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
#define N_SIN_PD(a) [&](n_double _v) { return _n_sin_pd_avx512(_v); }((a))
#define N_COS_PD(a) [&](n_double _v) { return _n_cos_pd_avx512(_v); }((a))

#elif defined(NSIMD_SSE4) && defined(__aarch64__)
// SSE4: use scalar per-lane for each double lane (only 2-wide anyway, scalar is fine)
#define N_SIN_PD(a) [&](n_double _v) { return _n_sin_pd_fallback(_v); }((a))
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
#define N_SIN_PD(a) [&](n_double _v) { return _n_sin_pd_fallback(_v); }((a))
#define N_COS_PD(a) [&](n_double _v) { return _n_cos_pd_fallback(_v); }((a))
#endif

#ifndef N_COS_PD
static N_FORCEINLINE n_double _n_cos_pd_fallback(n_double a) {
    double lane[8]; int w = NSIMD_WIDTH < 8 ? NSIMD_WIDTH : 8;
    n_store_pd(lane, a);
    for (int i = 0; i < w; i++) lane[i] = cos(lane[i]);
    return n_load_pd(lane);
}
#define N_COS_PD(a) [&](n_double _v) { return _n_cos_pd_fallback(_v); }((a))
#endif

// Public API
static N_FORCEINLINE n_double n_sin_pd(n_double a)     { return N_SIN_PD(a); }
static N_FORCEINLINE n_double n_cos_pd(n_double a)     { return N_COS_PD(a); }
// sqrt_pd is already defined in NativeSIMD.h as n_sqrt_pd

