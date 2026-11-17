// NativeSIMD_math.h 闁?SIMD vector math (inline AVX2 + per-lane scalar fallbacks)
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
// Constants from sleef/src/libm/sleefsimdsp.c: PI_A2/PI_B2/PI_C2 = 閿?2 in 3-parts
#define _N_PIO2_A 1.57079637e+00f
#define _N_PIO2_B -4.37113883e-08f

#if defined(NSIMD_AVX2) || defined(NSIMD_AVX512)

// AVX2 闁告劕鎳忛悧鎶藉捶?AVX512 缂傚倹鐗為惁褔宕￠弴鐐插笚闂佹彃濂旂弧鍐啺娴ｇ璁查柣銏╃厜缁变即宕楅妸鈺佸姤闁活潿鍔嶅Ο澶婎嚕?__m256 缂侇偉顕ч悗閿嬬▔鎼淬垺鎷遍柛?helper闁?
// 濞戞挸绉崇欢椋庢導?__m256 闁告帩鍋勯幃鏇㈡晬閸繄鏆婇柛?AVX512 TU 闂佹彃鏈Σ?__m512闁挎稑顦埀?
static N_FORCEINLINE __m256 _n256_abs(__m256 a) { return _mm256_andnot_ps(_mm256_set1_ps(-0.0f), a); }
static N_FORCEINLINE __m256 _n256_round(__m256 a) { return _mm256_round_ps(a, _MM_FROUND_TO_NEAREST_INT | _MM_FROUND_NO_EXC); }
static N_FORCEINLINE __m256 _n256_trunc(__m256 a) { return _mm256_round_ps(a, _MM_FROUND_TO_ZERO | _MM_FROUND_NO_EXC); }
static N_FORCEINLINE __m256 _n256_eq(__m256 a, __m256 b) { return _mm256_cmp_ps(a, b, _CMP_EQ_OQ); }
static N_FORCEINLINE __m256 _n256_lt(__m256 a, __m256 b) { return _mm256_cmp_ps(a, b, _CMP_LT_OQ); }
static N_FORCEINLINE __m256 _n256_gt(__m256 a, __m256 b) { return _mm256_cmp_ps(a, b, _CMP_GT_OQ); }
static N_FORCEINLINE __m256 _n256_ne(__m256 a, __m256 b) { return _mm256_cmp_ps(a, b, _CMP_NEQ_UQ); }
static N_FORCEINLINE __m256 _n256_not(__m256 m) { return _mm256_xor_ps(m, _mm256_castsi256_ps(_mm256_set1_epi32(-1))); }


// 8 lane 闁?閿?2 鐟滅増甯炵€规娊鏁嶅杈ㄦ殢 double 缂?q 濞?r闁挎稑鐭傛导鈺呭礂?float 閻㈩垰鎲￠弳鐔煎捶閵娿儯浜?|x| 濞戞挸顑囧▓鎴炵▕濡す婵堟嫚椤栨碍鈻曢柕?
// 閺夆晜鏌ㄥú?|r| <= 閿?4 闁?float 濞达絾鐟﹂弳鐔兼晬瀹€鍐澖闂?q 闁?qOut 閻㈩垽绠戦崵顓㈡晬閸?= round(x*2/閿?闁挎稑顦埀?
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

// sin(r) / cos(r)闁挎稑鐦漴| <= 閿?4
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
  // sin(x) = 閸楊槞in(r) / 閸楊槈os(r)闁挎稒顒?闁稿鍩栭弳鐔煎矗?sin闁挎稑鑻〃宀勫极閺夊灝绲?cos闁挎稒纭?2 闁告劕鍟块悾鍓х箔閿曗偓瑜板潡濡?
  __m256i qi;
  __m256 r = _n_reduce_pio2_avx2(d, &qi);
  __m256 u_sin = _n_sin_poly_avx2(r);
  __m256 u_cos = _n_cos_poly_avx2(r);
  __m256 even = _mm256_castsi256_ps(_mm256_cmpeq_epi32(_mm256_and_si256(qi, _mm256_set1_epi32(1)),
                                                       _mm256_setzero_si256()));
  __m256 u = _mm256_blendv_ps(u_cos, u_sin, even);
  __m256i flip = _mm256_and_si256(qi, _mm256_set1_epi32(2));
  __m256 rl = _mm256_xor_ps(u, _mm256_castsi256_ps(_mm256_slli_epi32(flip, 30)));
  // |x| > 1e7 闁?double 濞戞挸顦伴宀冦亹閹烘柨顔婇柣銊ュ缂嶆垿寮弶鍨殥濞戞挸绉磋ぐ鏌ユ閻欏懐绀夊ù婧垮€楃划?libm 缂侇喖澧介垾妯裤亹閹烘柨顔婇柨娑樼墢缂嶅秶鎲存担鐣岀唴鐎垫澘瀚哥槐姘跺Υ?
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
  // cos(x) = 閸楊槈os(r) / 閸楊槞in(r)闁挎稒顒?闁稿鍩栭弳鐔煎矗?cos闁挎稑鑻〃宀勫极閺夊灝绲?sin闁?q+1)&2 闁告劕鍟块悾鍓х箔閿曗偓瑜板潡濡?
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
  // |x - 1| <= 0.25闁挎稒姘ㄥú鍧楀箳閵夈儳娼旂€殿喒鍋?log(1+t)闁挎稑鐭傛导鈺呭礂瀹ュ懐鍟查柡渚€顣︾粭宀勫箰閸ャ劍娈堕柟宄板悑鐢挳宕烽妸銊х 1 濠㈣泛瀚☉顏嗙磼濠靛棭鍤犵紒顔藉劤鐎?
  __m256 t = _mm256_sub_ps(d, _mm256_set1_ps(1.0f));
  __m256 at = _mm256_and_ps(t, _mm256_castsi256_ps(_mm256_set1_epi32(0x7fffffff)));

  // log(1+t) = t*(1 - t/2 + t^2/3 - ... + t^12/13)闁挎稒绋戦崺?t^13 濡炪倗娅㈢槐娼€t| <= 0.25 闁哄啳鍩栭崺鍛村棘椤撯槅鍤栫€?< 3e-9
  __m256 s = _mm256_set1_ps(1.0f / 13.0f);
  s = _mm256_fmadd_ps(s, t, _mm256_set1_ps(-1.0f / 12.0f));
  s = _mm256_fmadd_ps(s, t, _mm256_set1_ps(1.0f / 11.0f));
  s = _mm256_fmadd_ps(s, t, _mm256_set1_ps(-1.0f / 10.0f));
  s = _mm256_fmadd_ps(s, t, _mm256_set1_ps(1.0f / 9.0f));
  s = _mm256_fmadd_ps(s, t, _mm256_set1_ps(-1.0f / 8.0f));
  s = _mm256_fmadd_ps(s, t, _mm256_set1_ps(1.0f / 7.0f));
  s = _mm256_fmadd_ps(s, t, _mm256_set1_ps(-1.0f / 6.0f));
  s = _mm256_fmadd_ps(s, t, _mm256_set1_ps(1.0f / 5.0f));
  s = _mm256_fmadd_ps(s, t, _mm256_set1_ps(-1.0f / 4.0f));
  s = _mm256_fmadd_ps(s, t, _mm256_set1_ps(1.0f / 3.0f));
  s = _mm256_fmadd_ps(s, t, _mm256_set1_ps(-1.0f / 2.0f));
  s = _mm256_fmadd_ps(s, t, _mm256_set1_ps(1.0f));
  __m256 near = _mm256_mul_ps(t, s);

  // 闁稿繑婀圭紞鎴︽嚑閸愩劍绾柨娑欒壘閻旑剟寮弶璺ㄧШ缂佹拝绠戦崺?[1,2) + 濞戞挶鍊栭?ln2 闁瑰嘲鍚嬬敮?
  __m256 dpos = _mm256_and_ps(d, _mm256_castsi256_ps(_mm256_set1_epi32(0x7fffffff)));
  __m256i emm0 = _mm256_srli_epi32(_mm256_castps_si256(dpos), 23);
  __m256i e = _mm256_sub_epi32(emm0, _mm256_set1_epi32(127));
  __m256i mant = _mm256_and_si256(_mm256_castps_si256(d), _mm256_set1_epi32(0x807fffff));
  mant = _mm256_or_si256(mant, _mm256_set1_epi32(0x3f800000));
  __m256 m = _mm256_castsi256_ps(mant);
  __m256 x = _mm256_div_ps(_mm256_sub_ps(m, _mm256_set1_ps(1.0f)),
                            _mm256_add_ps(m, _mm256_set1_ps(1.0f)));
  __m256 x2 = _mm256_mul_ps(x, x);
  // log(m) = x*(2 + 2x^2/3 + 2x^4/5 + ... + 2x^12/13)闁? 濡炪倝鈧稑鈻?m -> 2 濠㈣泛瀚▓鎴﹀箣椤忓懏鐒介悹鍥跺灠濡﹪姊藉鍛厒 1e-8
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
  // exp(d) = 2^n * exp(r)闁挎稑顔?= round(d/ln2)闁挎稑顔?= d - n*ln2闁挎稑鐗呯悮鍗炩枔?ln2 闁硅泛鐏氱粔鐑藉礄韫囨梻銆婇悹鍥跺灠濡﹪鏁嶆径鍫氬亾?
  // |r| <= ln2/2 闁?0.3466闁? 婵?Taylor 闁汇劌瀚崺鍛村棘椤撯槅鍤栫€?r^8/40320 闁?2.4e-9闁挎稑濂旂紞鍡樼?float 闁汇劌瀚畷鎰▔?ulp闁?
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
  p = _mm256_fmadd_ps(p, r, _mm256_set1_ps(1.0f));                                // exp(r) 闁?1 + r*(1 + r*(闁?)

  // 2^n 闁瑰嘲妫欓崹姘▔閵堝棭鍞介柟绋挎处閺嗙喖宕洪悢绋款伝闁规亽鍎荤槐婵囨媴?n < -126 闁汇劌瀚鐓庮潰閿濆牜娼愮紓浣规尰閻忓绂掑鍡楃樆闁圭娲﹂弳鐔烘偘閺夊灝娅ら柨娑樼墕瀹曠喎鈻撻崹顐㈩伝闁规亽鍎扮槐鐗堢▔椤厾绉靛ù锝呯У鐎垫岸寮敮顔剧闁?
  __m256i ni = _mm256_cvtps_epi32(_mm256_max_ps(_mm256_min_ps(n, _mm256_set1_ps(127.0f)),
                                               _mm256_set1_ps(-252.0f)));
  __m256i n1 = _mm256_max_epi32(ni, _mm256_set1_epi32(-126));
  __m256i n2 = _mm256_sub_epi32(ni, n1);
  __m256 s1 = _mm256_castsi256_ps(_mm256_slli_epi32(_mm256_add_epi32(n1, _mm256_set1_epi32(127)), 23));
  __m256 s2 = _mm256_castsi256_ps(_mm256_slli_epi32(_mm256_add_epi32(n2, _mm256_set1_epi32(127)), 23));

  __m256 result = _mm256_mul_ps(_mm256_mul_ps(p, s1), s2);

  // 濞?expf 闁汇劌瀚粩鐔兼偩鐏炶偐顏遍柤鐤彧缁辩増绋夋繝鍐劉濞?+inf闁挎稑濂旂粭鍛攦椤叀绀?0闁挎稒閽N 閺夊牊鎸搁崣鍡樼┍濠靛洤鐦?NaN闁?
  result = _mm256_blendv_ps(result, _mm256_set1_ps(INFINITY),
                            _mm256_cmp_ps(d, _mm256_set1_ps(88.722839f), _CMP_GE_OQ));
  result = _mm256_blendv_ps(result, _mm256_setzero_ps(),
                            _mm256_cmp_ps(d, _mm256_set1_ps(-103.972084f), _CMP_LE_OQ));
  return result;
}

// ---- SLEEF 閻犱警鍨抽崵搴ㄦ儍閸曨垳褰紒顔藉劤鐎?pow闁挎稒顒g2 濞?exp2 闂侇喛妫勫﹢?double 闂佹彃鐬奸悾濠氭晬鐎规潝oat 闁告瑯浜滄禒娑㈠嫉閳ь剟宕ユ惔銏狀潙闁?----
// log2(m)闁挎稑顔?闁?[1,2)闁挎稒鐭粭澶愭閳ь剛鎲版担鐟扮樄闁轰線顣︾紞鍛村箵閹邦剙绲块柨娑樻湰鐎垫岸寮幍顔芥殸閻犲鍟伴弫銈夊棘閻熺増韬?float 闁糕晝鍠庨悽顐ｆ交濞戞瑦闄?
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


// log(1+t) 鐨勭骇鏁板洜瀛愶紙13 椤癸級锛寍t| <= 0.25 鏃舵埅鏂宸?< 1e-16
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

  // exp(r)闁挎稑鐦漴| <= 0.347闁?2 婵?Taylor闁挎稑鏈崺鍛村棘椤撯槅鍤栫€?< 1e-17
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

  // 2^n闁挎稒顒t32 濠㈠墎鎳撻崺?[-1022, 1023] 闁告艾姘﹀ù?int64 闁瑰嘲鍚嬬€垫岸寮０浣虹Т
  __m128i ni32 = _mm256_cvtpd_epi32(_mm256_max_pd(_mm256_min_pd(n, _mm256_set1_pd(1023.0)),
                                                  _mm256_set1_pd(-1022.0)));
  __m256i ni64 = _mm256_cvtepi32_epi64(ni32);
  __m256d scale = _mm256_castsi256_pd(_mm256_slli_epi64(_mm256_add_epi64(ni64, _mm256_set1_epi64x(1023)), 52));
  return _mm256_mul_pd(p, scale);
}
static N_FORCEINLINE __m256 _n_pow_avx2_ps(__m256 x, __m256 y) {
  __m256d yl = _mm256_cvtps_pd(_mm256_castps256_ps128(y));
  __m256d yh = _mm256_cvtps_pd(_mm256_extractf128_ps(y, 1));

  // 闁圭娲﹂弳鐔哥▔鎼达紕鍟查柡浣规緲濠€?float 闁糕晝鍠庤ぐ鍥ㄧ附閺傘倗绀塴og2 闁告瑯浜滈顔句焊閻愵剚娈?[1,2) 閻忕偞娲栫槐?
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

  // 閻犳劗鍠庣花鎶藉极鐢喚绐楀ù鐘叉噺閺嗭綁寮悧鍫濈樄闁轰胶澧楅崹姘辩博鐎ｅ墎绀夊┑鍌氭处閺嗙喖骞愰崶銊︽闁告瑦鐗炵粈瀣晬濞戙垺濮滈柡浣哥摠閺嗙喖骞愰崶銊︽濞?NaN
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
// High 婵℃缍囩槐鐧瞣uble 濞戞搩鍙冨Λ璺ㄥ垝閹冾唺閻庡湱鍋熼獮鍥晬閸︽彜X2闁挎稑鐬煎ú浼村冀?<=1 ULP float闁?
// 8 濞?float lane 闁瑰嘲妫欓崹姘▔閵堝洨鐭?4 lane double闁挎稑鐬奸悾鑽も偓鐟版湰婵矂宕堕悙顏佸亾?
// ================================================================
static N_FORCEINLINE void _n_reduce4_pd(__m256d x, __m256d* rOut, __m128i* qOut) {
  // 濞戞挸顦伴?閿?2 鐟滅増甯炵€规娊鏁嶅▓鈧瑀| <= 閿?4闁挎稒纭?= round(x*2/閿?
  const __m256d two_over_pi = _mm256_set1_pd(0.63661977236758134307553505349006);
  __m256d q = _mm256_round_pd(_mm256_mul_pd(x, two_over_pi), _MM_FROUND_TO_NEAREST_INT | _MM_FROUND_NO_EXC);
  __m256d r = _mm256_fnmadd_pd(q, _mm256_set1_pd(1.57079632673412561417e+00), x);
  r = _mm256_fnmadd_pd(q, _mm256_set1_pd(6.07710050650619224932e-11), r);
  r = _mm256_fnmadd_pd(q, _mm256_set1_pd(2.02226624879595063154e-21), r);
  *rOut = r;
  *qOut = _mm256_cvtpd_epi32(q);
}

static N_FORCEINLINE __m256d _n_sin_poly4_pd(__m256d r) {
  // sin(r) = r + r^3*(c1 + c2 z + ...)闁挎稑鐦?= r^2闁挎稑鐦漴| <= 閿?4
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

// q mod 4闁挎稑鐬奸弫銈嗙鎼淬劉鍋撴径瀣仴闁告垼濮ら弳鐔哥▔鎼达綆鍎婇柛?
static N_FORCEINLINE __m256d _n_quad4_pd(__m128i q) {
  // q mod 4闁挎稑鐭侀幆銈夊捶?[0,3]闁挎稑鐗愮粈?q 闁?floor 闂傚嫨鍊栫涵鍫曟晬鐏炶偐绠介悹鍥﹁兌缁劑寮稿鍫熷閻犳劗鍣︾槐?
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
  // |x| <= tan(pi/8) 闁活潿鍔岄〃灞惧緞濮樿翰鈧秴顕ｈ箛銉у耿|x| > 1 闁稿繐鐗嗚ぐ鍥磹閹烘挻娈堕柨娑欑◥閼垫垿姊婚弶鎴濋殬闂傚倸顕弫?(x-1)/(x+1) 闁硅埖锚閸?pi/4 闂侇叀顕ч悡?
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
  r = _mm256_add_pd(r, _mm256_and_pd(mid, pi_4));                       // 濞戞搩鍙冨Λ鍧楀礌濞差亝锛熼悶?pi/4
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
  // 濠㈣泛绉堕弫?pow 閻犱警鍨扮欢鐐烘煂瀹€鈧▓?exp2闁挎稒鐡眡p(x) = 2^(x/ln2)
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

// ---- High 婵℃绲介顔藉緞閺嵮冨汲闁告瑱缍囩槐? 濞?float lane 闁瑰嘲妫旂悮杈╃磼?double 閻犱緤绱曢悾濠氬触鎼淬垹顫戦柛?----
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
  // 鏉?1 濞堥潧宕熼悪顒冭泲 log1p閿涙碍瀵氶弫?鐏忕偓鏆熼幏鍏煎复閸?x閳? 婢跺嫪绱伴張澶屼绩闂呯偓鈧呮祲濞?
  __m256d xd_lo = _mm256_cvtps_pd(_mm256_castps256_ps128(x));
  __m256d xd_hi = _mm256_cvtps_pd(_mm256_extractf128_ps(x, 1));
  __m256d tlo = _mm256_sub_pd(xd_lo, _mm256_set1_pd(1.0));
  __m256d thi = _mm256_sub_pd(xd_hi, _mm256_set1_pd(1.0));

  __m256 ax = _mm256_and_ps(x, _mm256_castsi256_ps(_mm256_set1_epi32(0x7fffffff)));
  __m256i ei = _mm256_sub_epi32(_mm256_srli_epi32(_mm256_castps_si256(ax), 23), _mm256_set1_epi32(127));
  __m256 mf = _mm256_castsi256_ps(_mm256_or_si256(_mm256_and_si256(_mm256_castps_si256(x), _mm256_set1_epi32(0x807fffff)),
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

// AVX512 濠㈣泛绉堕弫銈咁啅閺屻儳宕ｉ悹鍥﹁兌濞?AVX2 闁告劕鎳忛悧鎶芥晬?6 lane 闁瑰嘲妫旂悮杈╃磼?8 lane闁?
// 512 濞达絽绉磋ぐ褔鎮介妸銈囪壘閻犱礁鐏濋悺銊︾▔鎼达絿鏆柡鍫灣缁秹宕ラ崼顒傜闁轰焦婢橀鐔衡偓鍦仧楠炲洦绌卞┑鍥х槷闁告娲戦崬銈夋晬瀹€鍕級闁稿繐绉崇悮杈ㄧ附濡炴儳鏁╅柣顔昏兌缁ㄦ寧鎯旈敃鈧崹搴ㄥ矗婢跺牃鍋?
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
  __m512 e2 = _n_exp_avx512(_mm512_mul_ps(_mm512_set1_ps(2.0f), a));
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
// Precision selection: dispatch to inline 闁?per-lane scalar
// Only SIMD paths are defined here 闁?the #ifndef guards at the bottom
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
  n_int e = vshlq_n_s32(vaddq_s32(ni, vdupq_n_s32(127)), 23);
  n_float result = vmulq_f32(p, vreinterpretq_f32_s32(e));
  result = vbslq_f32(vcgtq_f32(d, vdupq_n_f32(88.722839f)), vdupq_n_f32(INFINITY), result);
  return vbslq_f32(vcltq_f32(d, vdupq_n_f32(-103.972084f)), vdupq_n_f32(0.0f), result);
}

static N_FORCEINLINE n_float _n_log_neon(n_float d) {
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
  return vbslq_f32(vcleq_f32(at, vdupq_n_f32(0.25f)), near, wide);
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
  n_float e2 = _n_exp_neon(vmulq_f32(vdupq_n_f32(2.0f), a));
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
  // 指数与尾数在 float 域取出，log2 只对 [1,2) 的尾数展开
  n_int ki = vreinterpretq_s32_f32(x);
  n_int ei = vsubq_s32(vshrq_n_s32(vandq_s32(ki, vdupq_n_s32(0x7fffffff)), 23), vdupq_n_s32(127));
  n_float mf = vreinterpretq_f32_s32(vorrq_s32(vandq_s32(ki, vdupq_n_s32(0x807fffff)), vdupq_n_s32(0x3f800000)));

  float64x2_t elo = vcvtq_f64_s64(vmovl_s32(vget_low_s32(ei)));
  float64x2_t ehi = vcvtq_f64_s64(vmovl_s32(vget_high_s32(ei)));
  float64x2_t llo = vaddq_f64(elo, _n_log2_mant_neon(vcvt_f64_f32(vget_low_f32(mf))));
  float64x2_t lhi = vaddq_f64(ehi, _n_log2_mant_neon(vcvt_f64_f32(vget_high_f32(mf))));
  float64x2_t vlo = vmulq_f64(llo, vcvt_f64_f32(vget_low_f32(y)));
  float64x2_t vhi = vmulq_f64(lhi, vcvt_f64_f32(vget_high_f32(y)));
  n_float mag = vcombine_f32(vcvt_f32_f64(_n_exp2_neon(vlo)), vcvt_f32_f64(_n_exp2_neon(vhi)));

  // 负底数：仅整数指数成立，奇数指数取负；非整数指数为 NaN
  n_float ay = vabsq_f32(y);
  uint32x4_t yInt = vandq_u32(vceqq_f32(y, vrndq_f32(y)), vcltq_f32(ay, vdupq_n_f32(8388608.0f)));
  n_float half = vrndq_f32(vmulq_f32(y, vdupq_n_f32(0.5f)));
  uint32x4_t yOdd = vcltq_f32(vsubq_f32(vmulq_f32(half, vdupq_n_f32(2.0f)), y), vdupq_n_f32(0.0f));
  uint32x4_t negSign = vandq_u32(vcltq_f32(x, vdupq_n_f32(0.0f)), vandq_u32(yInt, yOdd));
  n_float result = vbslq_f32(negSign, vnegq_f32(mag), mag);

  uint32x4_t xNeg = vcltq_f32(x, vdupq_n_f32(0.0f));
  result = vbslq_f32(vandq_u32(xNeg, vornq_u32(yInt, yInt)), vdupq_n_f32(0.0f / 0.0f), result);
  result = vbslq_f32(vceqq_f32(y, vdupq_n_f32(0.0f)), vdupq_n_f32(1.0f), result);
  uint32x4_t xZero = vceqq_f32(x, vdupq_n_f32(0.0f));
  result = vbslq_f32(vandq_u32(xZero, vcltq_f32(y, vdupq_n_f32(0.0f))), vdupq_n_f32(INFINITY), result);
  result = vbslq_f32(vandq_u32(xZero, vcgtq_f32(y, vdupq_n_f32(0.0f))), vdupq_n_f32(0.0f), result);
  return vbslq_f32(vorrq_u32(vmvnq_u32(vceqq_f32(x, x)), vmvnq_u32(vceqq_f32(y, y))), vdupq_n_f32(0.0f / 0.0f), result);
}
#endif // NSIMD_NEON && __aarch64__

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
  #define N_TAN(a)       _n_tan_avx512(a)
  #define N_ASIN(a)      _n_asin_avx512(a)
  #define N_ACOS(a)      _n_acos_avx512(a)
  #define N_ATAN(a)      _n_atan_avx512(a)
  #define N_ATAN2(a,b)   _n_atan2_avx512(a, b)
  #define N_SINH(a)      _n_sinh_avx512(a)
  #define N_COSH(a)      _n_cosh_avx512(a)
  #define N_TANH(a)      _n_tanh_avx512(a)
  #define N_SINCOS(a,s,c) _n_sincos_avx512(a, s, c)
  #elif defined(NSIMD_AVX2)
    #define N_SIN(a)     _n_sin_avx2(a)
    #define N_COS(a)     _n_cos_avx2(a)
    #define N_LOG(a)     _n_log_avx2(a)
    #define N_EXP(a)     _n_exp_avx2(a)
    #define N_LOG10(a)   _mm256_mul_ps(N_LOG(a), _mm256_set1_ps(0.4342944819032518f))
#elif defined(NSIMD_NEON) && defined(__aarch64__)
#define N_SIN(a)     _n_sin_neon(a)
#define N_COS(a)     _n_cos_neon(a)
#define N_LOG(a)     _n_log_neon(a)
#define N_EXP(a)     _n_exp_neon(a)
#define N_LOG10(a)   vmulq_f32(N_LOG(a), vdupq_n_f32(0.4342944819032518f))
  #endif
  // 缂備礁瀚幃搴ㄥ礄閼恒儲娈跺璺虹Ф閺併倖绋夋繝鍥ㄦ〃闁汇劌瀚悢鈧痪顓涘亾閻庡湱鍋熼獮鍥晬閸繄鏆板☉鏂款槸濠€顏呯▔鐎ｎ偅鐓欓柨娑樿嫰閻ｎ垶宕烽妸銈呪枏闁活潿鍔岄ˇ鈺冧沪閺囩偟纾婚柨娑樼焸閵嗗孩鎯旇箛鏃€锟ョ憸鏉垮船閹肩兘鏁嶆径娑氱獥
  // tan/asin/acos/atan/atan2/pow 鐎点倛娅ｉ悵娑㈠捶?sin/cos/exp/log 濞戞柨顑勭粭鍌炴晬瀹€鈧花鎸庢償閿曞倹顓瑰☉鏂款儑閹寸兘骞嶇憗銈傚亾?
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
#elif defined(NSIMD_NEON) && defined(__aarch64__)
#define N_TAN(a)       _n_tan_neon(a)
#define N_ASIN(a)      _n_asin_neon(a)
#define N_ACOS(a)      _n_acos_neon(a)
#define N_ATAN(a)      _n_atan_neon(a)
#define N_ATAN2(a,b)   _n_atan2_neon(a, b)
#define N_SINH(a)      _n_sinh_neon(a)
#define N_COSH(a)      _n_cosh_neon(a)
#define N_TANH(a)      _n_tanh_neon(a)
#define N_SINCOS(a,s,c) do { (s) = _n_sin_neon(a); (c) = _n_cos_neon(a); } while (0)
#endif
    #define N_SINCOS(a,s,c) _n_sincos_ps(a, s, c)
  #endif
  // 闂?AVX2 妤犵偛鍟胯ぐ鎾晬濮濆獥eef 鐎规瓕灏欎簺闂傚嫨鍊х槐婵嬫偨閸欐鐟撻柡?#ifndef N_* 閻庣懓鐗嗗畷濂告儍閸曨垪鍋撻幇鎵冲亾濮樻湹澹曢柡宥呮喘閸ｆ椽宕堕悙琛″亾閳ь剛鎲伴崱娆愮０闁?
  // Non-AVX2: #ifndef guards below provide per-lane scalar fallback
  // AVX512 闁哄棗鍊诲?exp 闁告劕鎳撴禒鍫⑩偓鍦仧楠炲洭鏁嶇仦鑺ュ€遍柡宥囨焿閾斿寮介崶顒€娅ら柛銉у仱閳ь兘鍋撻柕?

// ===== High (~1.0 ULP) =====
#elif SIMD_MATH_PRECISION == 2
  // double 濞戞搩鍙冨Λ璺ㄥ垝閹冾唺閻庡湱鍋熼獮鍥晬閸︽彜X2闁挎稑顧€缁遍亶宕楃捄铏规殜妤犵偛鍟胯ぐ鎾偨?#ifndef 閻庣懓鐗嗗畷濂稿炊閻愯　鍋撻埀顒勫礆娴肩补鍋?lane libm
  #if defined(NSIMD_AVX512)
    #define N_SIN(a)       _n_high_sin_avx512(a)
    #define N_COS(a)       _n_high_cos_avx512(a)
    #define N_TAN(a)       _n_high_tan_avx512(a)
    #define N_ASIN(a)      _n_high_asin_avx512(a)
    #define N_ACOS(a)      _n_high_acos_avx512(a)
    #define N_ATAN(a)      _n_high_atan_avx512(a)
    #define N_ATAN2(a,b)   _n_high_atan2_avx512(a, b)
    #define N_SINH(a)      _n_high_sinh_avx512(a)
    #define N_COSH(a)      _n_high_cosh_avx512(a)
    #define N_TANH(a)      _n_high_tanh_avx512(a)
    #define N_EXP(a)       _n_high_exp_avx512(a)
    #define N_LOG(a)       _n_high_log_avx512(a)
    #define N_LOG10(a)     _n_high_log10_avx512(a)
    #define N_POW(a,b)     _n_pow_avx512(a, b)
    #define N_SINCOS(a,s,c) _n_high_sincos_avx512(a, s, c)
  #elif defined(NSIMD_AVX2)
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
// boundary entirely 闁?matches ISPC LLVM IR pattern where sin/cos/sqrt polynomials
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
// 闁哄秴娲崳铏规崉椤栨氨绐為柣銊ュ閺嗙喓鈧冻绠戦崣鍡涘矗閿濆繒绐楀☉鎾抽閹粓鏌岃箛搴ｇЪ闁稿繗浜弫銈夊触鐏炶偐顏卞┑鍌涱殔閻ゅ嫰鎮?
// 闁汇垻鍠愰崹姘閿濆洨鍨抽柣銊ュ閻栵綁鏌岃箛鏃戝斀闁挎稑鐗嗛悢顒勬焾閵娿儱甯楃紒杈╁О閳ь兛鐒﹀﹢顓㈠触閹达箑娅ら柛鏍ㄧ墱濞?job闁挎稑顦抽惃鐔兼偨?EJ_*闁挎稑鐬奸弫閬嶅嫉?TU 闁?
// SIMD_MATH_PRECISION 闁告劕鍟块悾楣冩偨閵娿儱鏁堕柤杈ㄦ煥椤︽寧銇勯悷鎵閺夆晜蓱濡?libm闁挎稑鐭傛导鈺呭礂瀹ュ懏鍊卞☉鎾亾 job 闁告劕鎳嶇悮杈ㄧ附濡ゅ啰缈遍幖杈捐礋閳?
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
#if defined(NSIMD_AVX2) && !defined(NSIMD_AVX512)   // 缂備礁瀚幃搴ｂ偓鍦仧楠炲洭鎮?mask 闁?blend闁挎稑鐡慥X512 闁?mask 缂侇偉顕ч悗閿嬬▔瀹ュ懎鎮戦悗?
// Cross-platform inline math (via n_xxx_ps abstraction 闁?all platforms)
// These work on AVX2, SSE4, NEON 闁?n_xxx_ps maps to the right intrinsic.
// ================================================================

static N_FORCEINLINE n_float _n_tan_ps(n_float a) {
  return n_div_ps(n_sin_ps(a), n_cos_ps(a));
}

static N_FORCEINLINE n_float _n_atan_ps(n_float a);

static N_FORCEINLINE n_float _n_atan2_ps(n_float y, n_float x) {
  // x = 0 闁?y/x = 閸楊槒nf 缂?atan 闁?|x|>1 闁告帒妫欓弫顕€鎳涢鍡楀Ё鐎电増顨呴崺?閸楊槚i/2闁挎稒绋戣ぐ褔妫侀埀顒傛偘?atan2(0,0) = 0闁?
  n_float zero = n_set1_ps(0.0f), pi = n_set1_ps(3.1415926535897932f);
  n_float r = _n_atan_ps(n_div_ps(y, x));
  n_float adj = n_blendv_ps(n_sub_ps(zero, pi), pi, n_cmp_ge_ps(y, zero));
  r = n_blendv_ps(r, n_add_ps(r, adj), n_cmp_lt_ps(x, zero));
  return n_blendv_ps(r, zero, n_and_mask(n_cmp_eq_ps(x, zero), n_cmp_eq_ps(y, zero)));
}
static N_FORCEINLINE n_float _n_asin_ps(n_float a) {
  // asin(x) = 2*atan2(x, 1 + sqrt(1 - x^2))闁? - x^2 闁?(1-x)(1+x) 閻犱緤绱曢悾濠氭晬鐎靛彻| -> 1 濠㈣泛瀚粭澶嬬▔閵忋垻缈遍幖?
  n_float one = n_set1_ps(1.0f);
  n_float t = n_sqrt_ps(n_max_ps(n_mul_ps(n_sub_ps(one, a), n_add_ps(one, a)), n_set1_ps(0.0f)));
  return n_mul_ps(n_set1_ps(2.0f), _n_atan2_ps(a, n_add_ps(one, t)));
}
static N_FORCEINLINE n_float _n_acos_ps(n_float a) {
  // acos(x) = 2*atan2(sqrt(1 - x^2), 1 + x)闁挎稒纭?= -1 闁哄啯婀圭悮杈ㄣ亜瑜版帒鍘村☉?0闁挎稑鑻畷鐔兼偑椤掑叝?pi
  n_float one = n_set1_ps(1.0f);
  n_float t = n_sqrt_ps(n_max_ps(n_mul_ps(n_sub_ps(one, a), n_add_ps(one, a)), n_set1_ps(0.0f)));
  n_float r = n_mul_ps(n_set1_ps(2.0f), _n_atan2_ps(t, n_add_ps(one, a)));
  return n_blendv_ps(r, n_set1_ps(3.1415926535897932f), n_cmp_le_ps(a, n_set1_ps(-1.0f)));
}static N_FORCEINLINE n_float _n_atan_ps(n_float a) {
  // atan(x) = x + x^3*q(x^2) 闁?[0,1] 濞戞挸锕﹀▓鎴︽儎缁嬫鍤?minimax 闁归鍠庨幃搴ㄦ晬閸ь晣x rel err 1.8e-8闁挎稑顧€缁?
  // |x|>1 閻?atan(x) = pi/2 - atan(1/x)闁?
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
  // 閻?|x| 濞?e^x - e^-x 闁哄牆顦卞ù澶娾槈閸綆鍤栫€瑰壊鍣槐婵嬪绩閸︻厽鏆?Taylor闁挎稑鐗嗛崺?x^7闁挎稑顧€缁遍亶姊奸崼婵冨亾?0.25 濞戞挶鍊撻弲鍓佹嫚椤栨碍鈻曢柛姘焸閸ｈ櫣鐥?
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
  // 閻?|x| 濞?e^2x - 1 闁烩晛鎲＄粔鐑芥晬瀹€鈧弫?x - x^3/3 + 2x^5/15 - 17x^7/315 + 62x^9/2835
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
  // MathF.Pow 閻犲浂鍘虹粻鐔兼晬濮橆収鍔€閹煎瓨娲橀弳鐔烘導?exp(y*log x)闁挎稒绋栫粈瀣償閺囩喐娈堕柛娆樹簻濠€?y 濞戞挾鍎ら弳锝夊极閻楀牊顦ч柟瀛樺姉閻濇盯鏁嶇仦绛嬫疄闁轰胶澧楃€垫岸寮弶鍨悼閻犳劗鍣︾槐?
  // 閹煎瓨娲橀弳鐔哥▔?0 闁?log(0) = -inf 缂?exp 闁煎浜為崝褔鎷冮挊澶婄厒 0 / +inf闁挎稒鐩顏堝极鐎涙ɑ娈堕柟绋挎处閺嗙喖鎯冮崟顔绢槹閹煎瓨娲橀弳鐔哥▔?NaN 閺夊牊鎸搁崣鍡樻交閺傛寧绀€ NaN闁?
  n_float ax = n_fabs_ps(x);
  n_float mag = n_exp_ps(n_mul_ps(y, n_log_ps(ax)));

  // y 闁哄嫷鍨伴幆浣圭▔閻戞ɑ娈婚柡浣稿簻缁辩殧 == trunc(y) 濞?|y| < 2^23闁挎稑娼歭oat 闁革负鍔嶉婵嬫嚑閸愩劍绾柛鎰噺閺嗭綁寮弶鍨缂侇喖澧介垾妯兼偘閵娧佷粵闁?
  n_float ay = n_fabs_ps(y);
  n_mask yInt = n_and_mask(n_cmp_eq_ps(y, n_trunc_ps(y)), n_cmp_lt_ps(ay, n_set1_ps(8388608.0f)));
  // 濠靛倸娲﹂弳鐔煎礆閵堝懐鏆伴柨娑欘劧/2 闁汇劌瀚崺鍛村棘椤撴繄顔?2 濞?y 濞戞挸绉堕悺鎴﹀础閸忓懓绀嬪┑鍌氭处閺?
  n_float half = n_trunc_ps(n_mul_ps(y, n_set1_ps(0.5f)));
  n_mask yOdd = n_cmp_ne_ps(n_mul_ps(half, n_set1_ps(2.0f)), y);
  n_mask negate = n_and_mask(yInt, yOdd);

  n_float sign = n_blendv_ps(n_set1_ps(1.0f), n_set1_ps(-1.0f),
                             n_and_mask(n_cmp_lt_ps(x, n_set1_ps(0.0f)), negate));
  n_float result = n_mul_ps(mag, sign);

  // 閻犳劗鍠庣花鎶藉极?+ 闂傚牏鍋為弳锝夊极閻楀牆鐦归柡?闁?NaN
  result = n_blendv_ps(result, n_set1_ps(NAN),
                       n_and_mask(n_cmp_lt_ps(x, n_set1_ps(0.0f)), n_not_mask(yInt)));
  // pow(x, 0) = 1闁挎稑鐗嗛幆?NaN 閹煎瓨娲橀弳鐔兼晬婢舵稓绀夊☉?MathF.Pow 濞戞挴鍋撻柤?
  result = n_blendv_ps(result, n_set1_ps(1.0f), n_cmp_eq_ps(y, n_set1_ps(0.0f)));
  // x = 0 濞?y < 0 闁?+inf闁挎稒纭?= 0 濞?y > 0 闁?0
  n_mask xZero = n_cmp_eq_ps(x, n_set1_ps(0.0f));
  result = n_blendv_ps(result, n_set1_ps(INFINITY),
                       n_and_mask(xZero, n_cmp_lt_ps(y, n_set1_ps(0.0f))));
  result = n_blendv_ps(result, n_set1_ps(0.0f),
                       n_and_mask(xZero, n_cmp_gt_ps(y, n_set1_ps(0.0f))));
  // NaN 濞磋偐濮甸幐閬嶆晬閸繄淇洪柡浣哄閸ㄣ劑骞愰崶銊︽濞寸姾顔婄粩瀛樼▔?NaN 闁?NaN闁挎稑濂旂徊?pow(NaN, 0) = 1 鐎瑰憡褰冨﹢顏呯▔婵犲啯鐓欏璺哄閹﹪鏁?
  result = n_blendv_ps(result, n_set1_ps(NAN),
                       n_or_mask(n_cmp_ne_ps(x, x), n_cmp_ne_ps(y, y)));
  return result;
}

// ================================================================
#endif // 缂備礁瀚幃搴ｂ偓鍦仧楠炲洭鏁嶉崷鎻2 only闁?
// Double-precision SLEEF (n_*_pd)
// ================================================================

// Double precision sin 闁?inline AVX2 polynomial (SLEEF-derived)
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

    // Polynomial: sin(x) 闁?x + x妞翠浇鐭綾1 + x闁筹箑鐥夌粚? + x闁筹缚璞㈢粚? + x闁筹箑顒濈粚?
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
// AVX512 double sin/cos闁挎稒宀搁埀?lane 闁哄秴娲崳娲晬閸噥鍔€缁绢収鍠楅埀顑倻鍠橀柛蹇撶墳缁辨繈鏌嗛崹顔煎赋闂佹彃绉撮崯?8-wide 濠㈣埖宀搁妴宥咁嚕韫囥儳绀?
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
