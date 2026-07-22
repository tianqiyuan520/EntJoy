// SimdValue.h — SIMD types aligned with EntJoy.Mathematics
// simd_value<float>  = WIDTH-wide float
// simd_value<int>    = WIDTH-wide int
// simd_value<float2> = pair of simd_value<float> (x,y)
// simd_value<int2>   = pair of simd_value<int> (x,y)
#pragma once
#include "NativeSIMD.h"
#include "NativeMath.h"

// Forward declaration
template<typename T> struct simd_value;

// ============================================================
// simd_value<float>
// ============================================================
template<>
struct simd_value<float> {
    n_float v;
    simd_value() = default;
    explicit simd_value(n_float val) : v(val) {}

    static simd_value broadcast(float s) { return simd_value{ n_set1_ps(s) }; }
    static simd_value load(const float* p) { return simd_value{ n_load_ps(p) }; }
    void store(float* p) const { n_store_ps(p, v); }

    template<typename Tarr>
    static inline simd_value gathf(const Tarr* base, n_int idx) {
        return simd_value{ n_gather_ps<sizeof(Tarr)>((const float*)base, idx) };
    }
    template<typename Tarr>
    static inline simd_value gathfy(const Tarr* base, n_int idx) {
        return simd_value{ n_gather_ps<sizeof(Tarr)>(((const float*)base) + 1, idx) };
    }


    friend simd_value operator+(simd_value a, simd_value b) { return simd_value{ n_add_ps(a.v, b.v) }; }
    friend simd_value operator-(simd_value a, simd_value b) { return simd_value{ n_sub_ps(a.v, b.v) }; }
    friend simd_value operator*(simd_value a, simd_value b) { return simd_value{ n_mul_ps(a.v, b.v) }; }
    friend simd_value operator+(simd_value a, float b) { return simd_value{ n_add_ps(a.v, n_set1_ps(b)) }; }
    friend simd_value operator-(simd_value a, float b) { return simd_value{ n_sub_ps(a.v, n_set1_ps(b)) }; }
    friend simd_value operator*(simd_value a, float b) { return simd_value{ n_mul_ps(a.v, n_set1_ps(b)) }; }
    friend simd_value operator+(float a, simd_value b) { return simd_value{ n_add_ps(n_set1_ps(a), b.v) }; }
    friend simd_value operator-(float a, simd_value b) { return simd_value{ n_sub_ps(n_set1_ps(a), b.v) }; }
    friend simd_value operator*(float a, simd_value b) { return simd_value{ n_mul_ps(n_set1_ps(a), b.v) }; }
    friend simd_value min(simd_value a, simd_value b) { return simd_value{ n_min_ps(a.v, b.v) }; }
    friend simd_value max(simd_value a, simd_value b) { return simd_value{ n_max_ps(a.v, b.v) }; }

    // Full-Width SIMD: floor() - vectorized
    simd_value floor() const { return simd_value{ n_floor_ps(v) }; }
};

// ============================================================
// simd_mask
// ============================================================
// simd_mask — stores n_mask (uint32x4_t on NEON, __m256/__m128 on x86, bool on scalar)
// ============================================================
struct simd_mask {
    n_mask m;
    simd_mask() = default;
    explicit simd_mask(n_mask val) : m(val) {}
    static simd_mask all_true() {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
        return simd_mask{ _mm256_castsi256_ps(_mm256_set1_epi32(-1)) };
#elif defined(NSIMD_SSE4)
        return simd_mask{ _mm_castsi128_ps(_mm_set1_epi32(-1)) };
#elif defined(NSIMD_NEON)
        return simd_mask{ vdupq_n_u32(0xFFFFFFFF) };
#else
        return simd_mask{ true };
#endif
    }
    bool all_false() const { return n_all_zero(m) != 0; }
    bool any_true() const { return n_any_true(m) != 0; }

    friend simd_mask operator&(simd_mask a, simd_mask b) { return simd_mask{ n_and_mask(a.m, b.m) }; }
    simd_mask operator~() const { return simd_mask{ n_not_mask(m) }; }
    simd_mask& operator&=(simd_mask other) { m = n_and_mask(m, other.m); return *this; }
};

// ============================================================
// simd_value<int>
// ============================================================
template<>
struct simd_value<int> {
    n_int v;
    simd_value() = default;
    explicit simd_value(n_int val) : v(val) {}

    static simd_value broadcast(int s) { return simd_value{ n_set1_epi32(s) }; }
    static simd_value sequence(int base) {
        return simd_value{ n_set_epi32(base+7, base+6, base+5, base+4, base+3, base+2, base+1, base) };
    }
    static simd_value load(const int* p) { return simd_value{ n_load_epi32(p) }; }
    void store(int* p) const { n_store_epi32(p, v); }

    static inline simd_value gather(const int* base, simd_value idx) {
        return simd_value{ n_gather_epi32(base, idx.v) };
    }

    friend simd_value operator+(simd_value a, simd_value b) { return simd_value{ n_add_epi32(a.v, b.v) }; }
    friend simd_value operator+(simd_value a, int b) { return simd_value{ n_add_epi32(a.v, n_set1_epi32(b)) }; }
    friend simd_value operator+(int a, simd_value b) { return simd_value{ n_add_epi32(n_set1_epi32(a), b.v) }; }
    friend simd_value operator-(simd_value a, simd_value b) { return simd_value{ n_sub_epi32(a.v, b.v) }; }
    friend simd_value operator-(simd_value a, int b) { return simd_value{ n_sub_epi32(a.v, n_set1_epi32(b)) }; }
    friend simd_value operator-(int a, simd_value b) { return simd_value{ n_sub_epi32(n_set1_epi32(a), b.v) }; }
    friend simd_value operator*(simd_value a, simd_value b) { return simd_value{ n_mullo_epi32(a.v, b.v) }; }
    friend simd_value operator*(simd_value a, int b) { return simd_value{ n_mullo_epi32(a.v, n_set1_epi32(b)) }; }
    friend simd_value operator*(int a, simd_value b) { return simd_value{ n_mullo_epi32(n_set1_epi32(a), b.v) }; }

    // Full-Width SIMD: min/max (vectorized int)
    friend simd_value min(simd_value a, simd_value b) { return simd_value{ n_min_epi32(a.v, b.v) }; }
    friend simd_value max(simd_value a, simd_value b) { return simd_value{ n_max_epi32(a.v, b.v) }; }

    // Full-Width SIMD: float->int truncation conversion
    static simd_value convert(simd_value<float> f) { return simd_value{ n_cvttps_epi32(f.v) }; }

    // Full-Width SIMD: per-lane comparisons with scalar return simd_mask
    simd_mask operator<(int s) const { return simd_mask{ n_cmp_lt_epi32(v, n_set1_epi32(s)) }; }
    simd_mask operator<=(int s) const { return simd_mask{ n_cmp_le_epi32(v, n_set1_epi32(s)) }; }
    simd_mask operator>(int s) const { return simd_mask{ n_cmp_gt_epi32(v, n_set1_epi32(s)) }; }
    simd_mask operator>=(int s) const { return simd_mask{ n_cmp_ge_epi32(v, n_set1_epi32(s)) }; }
    simd_mask operator==(int s) const { return simd_mask{ n_cmp_eq_epi32(v, n_set1_epi32(s)) }; }
    simd_mask operator!=(int s) const { return simd_mask{ n_not_mask((*this == s).m) }; }
    // scalar < simd_value
    friend simd_mask operator<(int s, simd_value a) { return simd_mask{ n_cmp_lt_epi32(n_set1_epi32(s), a.v) }; }
    friend simd_mask operator<=(int s, simd_value a) { return simd_mask{ n_cmp_le_epi32(n_set1_epi32(s), a.v) }; }
    friend simd_mask operator>(int s, simd_value a) { return simd_mask{ n_cmp_gt_epi32(n_set1_epi32(s), a.v) }; }
    friend simd_mask operator>=(int s, simd_value a) { return simd_mask{ n_cmp_ge_epi32(n_set1_epi32(s), a.v) }; }
    friend simd_mask operator==(int s, simd_value a) { return simd_mask{ n_cmp_eq_epi32(n_set1_epi32(s), a.v) }; }

    // simd_value < simd_value → simd_mask
    simd_mask operator<(simd_value a) const { return simd_mask{ n_cmp_lt_epi32(v, a.v) }; }
    simd_mask operator<=(simd_value a) const { return simd_mask{ n_cmp_le_epi32(v, a.v) }; }
    simd_mask operator>(simd_value a) const { return simd_mask{ n_cmp_gt_epi32(v, a.v) }; }
    simd_mask operator>=(simd_value a) const { return simd_mask{ n_cmp_ge_epi32(v, a.v) }; }
    simd_mask operator==(simd_value a) const { return simd_mask{ n_cmp_eq_epi32(v, a.v) }; }
    simd_mask operator!=(simd_value a) const { return simd_mask{ n_not_mask((*this == a).m) }; }
};

// ============================================================
// simd_value<EntJoy::Mathematics::float2>
// ============================================================
template<>
struct simd_value<EntJoy::Mathematics::float2> {
    simd_value<float> x;
    simd_value<float> y;

    simd_value() = default;
    simd_value(simd_value<float> x_, simd_value<float> y_) : x(x_), y(y_) {}
    // Broadcast constructor: scalar float2 → all lanes = s
    simd_value(EntJoy::Mathematics::float2 s) : x(simd_value<float>::broadcast(s.x())), y(simd_value<float>::broadcast(s.y())) {}

    static simd_value gather(const EntJoy::Mathematics::float2* base, simd_value<int> idx) {
        simd_value v;
        v.x = simd_value<float>::gathf(base, idx.v);
        v.y = simd_value<float>::gathfy(base, idx.v);
        return v;
    }

    friend simd_value min(simd_value a, simd_value b) {
        return simd_value{ min(a.x, b.x), min(a.y, b.y) };
    }
    friend simd_value max(simd_value a, simd_value b) {
        return simd_value{ max(a.x, b.x), max(a.y, b.y) };
    }
};

// ============================================================
// simd_value<EntJoy::Mathematics::int2>
// ============================================================
template<>
struct simd_value<EntJoy::Mathematics::int2> {
    simd_value<int> x;
    simd_value<int> y;

    simd_value() = default;
    simd_value(simd_value<int> x_, simd_value<int> y_) : x(x_), y(y_) {}
    // Broadcast constructor: scalar int2 → all lanes = s
    simd_value(EntJoy::Mathematics::int2 s) : x(simd_value<int>::broadcast(s.x())), y(simd_value<int>::broadcast(s.y())) {}

    static simd_value gather(const EntJoy::Mathematics::int2* base, simd_value<int> idx) {
        // Use float gather (proven correct for float2) then cast to int via bitcast
        simd_value v;
#if defined(NSIMD_AVX2)
        v.x.v = _mm256_castps_si256(_mm256_i32gather_ps((const float*)base, idx.v, 8));
        v.y.v = _mm256_castps_si256(_mm256_i32gather_ps(((const float*)base) + 1, idx.v, 8));
#else
        // H2+H3: SSE4/NEON/scalar fallback with explicit stride=8
        // int2 is 8 bytes, so stride must be 8, not the default 4
        const int* baseInt = (const int*)base;
        const int* baseIntY = (const int*)base + 1;
        int w = NSIMD_WIDTH;
        int idxArr[8];
        int xArr[8];
        int yArr[8];
        n_store_epi32(idxArr, idx.v);
        for (int i = 0; i < w; i++) {
            xArr[i] = *(const int*)((const char*)baseInt + idxArr[i] * 8);
            yArr[i] = *(const int*)((const char*)baseIntY + idxArr[i] * 8);
        }
        v.x.v = n_load_epi32(xArr);
        v.y.v = n_load_epi32(yArr);
#endif
        return v;
    }

    friend simd_value min(simd_value a, simd_value b) {
        return simd_value{ min(a.x, b.x), min(a.y, b.y) };
    }
    friend simd_value max(simd_value a, simd_value b) {
        return simd_value{ max(a.x, b.x), max(a.y, b.y) };
    }
};

// ============================================================
// Global SIMD operations
// ============================================================
static inline simd_value<float> blend(simd_value<float> f, simd_value<float> t, simd_mask m) {
    return simd_value<float>{ n_blend_ps(f.v, t.v, m.m) };
}
static inline simd_value<int> blend(simd_value<int> f, simd_value<int> t, simd_mask m) {
    return simd_value<int>{ n_blend_epi32(f.v, t.v, m.m) };
}
static inline float hmin(simd_value<float> v) { return n_hmin_ps(v.v); }
static inline float hmax(simd_value<float> v) { return n_hmax_ps(v.v); }
static inline float hsum(simd_value<float> v) { return n_hsum_ps(v.v); }
static inline int hmin(simd_value<int> v) { return n_hmin_epi32(v.v); }
static inline int hmax(simd_value<int> v) { return n_hmax_epi32(v.v); }
static inline int hmin_idx(simd_value<float> val, simd_value<int> idx) { return n_hmin_idx(val.v, idx.v); }

// SIMD int element-wise min/max (named to avoid Windows min/max macro conflict)
static inline simd_value<int> simd_max(simd_value<int> a, simd_value<int> b) { return simd_value<int>{ n_max_epi32(a.v, b.v) }; }
static inline simd_value<int> simd_min(simd_value<int> a, simd_value<int> b) { return simd_value<int>{ n_min_epi32(a.v, b.v) }; }
