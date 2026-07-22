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
    static simd_value gathf(const Tarr* base, n_int idx) {
        return simd_value{ n_gather_ps<sizeof(Tarr)>((const float*)base, idx) };
    }
    template<typename Tarr>
    static simd_value gathfy(const Tarr* base, n_int idx) {
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

    static simd_value gather(const int* base, simd_value idx) {
        return simd_value{ n_gather_epi32(base, idx.v) };
    }

    friend simd_value operator+(simd_value a, simd_value b) { return simd_value{ n_add_epi32(a.v, b.v) }; }
    friend simd_value operator+(simd_value a, int b) { return simd_value{ n_add_epi32(a.v, n_set1_epi32(b)) }; }
    friend simd_value operator+(int a, simd_value b) { return simd_value{ n_add_epi32(n_set1_epi32(a), b.v) }; }
};

// ============================================================
// simd_value<EntJoy::Mathematics::float2>
// ============================================================
template<>
struct simd_value<EntJoy::Mathematics::float2> {
    simd_value<float> x;
    simd_value<float> y;

    static simd_value gather(const EntJoy::Mathematics::float2* base, simd_value<int> idx) {
        simd_value v;
        v.x = simd_value<float>::gathf(base, idx.v);
        v.y = simd_value<float>::gathfy(base, idx.v);
        return v;
    }
};

// ============================================================
// simd_value<EntJoy::Mathematics::int2>
// ============================================================
template<>
struct simd_value<EntJoy::Mathematics::int2> {
    simd_value<int> x;
    simd_value<int> y;

    static simd_value gather(const EntJoy::Mathematics::int2* base, simd_value<int> idx) {
        simd_value v;
        v.x = simd_value<int>::gather((const int*)base, idx);
        v.y = simd_value<int>::gather(((const int*)base) + 1, idx);
        return v;
    }
};

// ============================================================
// simd_mask
// ============================================================
struct simd_mask {
    n_float m;
    simd_mask() = default;
    explicit simd_mask(n_float val) : m(val) {}
    static simd_mask all_true() {
#if defined(NSIMD_AVX2) || defined(NSIMD_AVX)
        return simd_mask{ _mm256_castsi256_ps(_mm256_set1_epi32(-1)) };
#elif defined(NSIMD_SSE4)
        return simd_mask{ _mm_castsi128_ps(_mm_set1_epi32(-1)) };
#elif defined(NSIMD_NEON)
        return simd_mask{ vreinterpretq_f32_u32(vdupq_n_u32(0xFFFFFFFF)) };
#else
        return simd_mask{ n_set1_ps(-1.0f) };
#endif
    }
    bool all_false() const { return n_all_zero(m) != 0; }
};

// ============================================================
// Global SIMD operations
// ============================================================
static simd_value<float> blend(simd_value<float> f, simd_value<float> t, simd_mask m) {
    return simd_value<float>{ n_blend_ps(f.v, t.v, m.m) };
}
static simd_value<int> blend(simd_value<int> f, simd_value<int> t, simd_mask m) {
    return simd_value<int>{ n_blend_epi32(f.v, t.v, m.m) };
}
static float hmin(simd_value<float> v) { return n_hmin_ps(v.v); }
static float hmax(simd_value<float> v) { return n_hmax_ps(v.v); }
static float hsum(simd_value<float> v) { return n_hsum_ps(v.v); }
static int hmin_idx(simd_value<float> val, simd_value<int> idx) { return n_hmin_idx(val.v, idx.v); }
