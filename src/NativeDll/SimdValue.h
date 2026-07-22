// SimdValue.h — 外层 SIMD 值类型
// 基于 NativeSIMD.h 的 n_* 函数提供 simd_f / simd_i / simd_mask 类型
#pragma once
#include "NativeSIMD.h"
#include "NativeMath.h"

// 标量回退
#if NSIMD_WIDTH == 1
typedef float simd_f;
typedef int simd_i;
typedef bool simd_mask;
static inline float hsum(float v) { return v; }
static inline float hmin(float v) { return v; }
static inline float hmax(float v) { return v; }
#else

struct simd_i; // 前向声明

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

struct simd_f {
    n_float v;
    simd_f() = default;
    explicit simd_f(n_float val) : v(val) {}

    static simd_f broadcast(float s) { return simd_f{ n_set1_ps(s) }; }
    static simd_f load(const float* p) { return simd_f{ n_load_ps(p) }; }
    void store(float* p) const { n_store_ps(p, v); }

    static simd_f gathf(const float* base, simd_i idx);
    static simd_f gathfy(const float* base, simd_i idx);

    friend simd_f operator+(simd_f a, simd_f b) { return simd_f{ n_add_ps(a.v, b.v) }; }
    friend simd_f operator-(simd_f a, simd_f b) { return simd_f{ n_sub_ps(a.v, b.v) }; }
    friend simd_f operator*(simd_f a, simd_f b) { return simd_f{ n_mul_ps(a.v, b.v) }; }
    friend simd_f operator+(simd_f a, float b) { return simd_f{ n_add_ps(a.v, n_set1_ps(b)) }; }
    friend simd_f operator-(simd_f a, float b) { return simd_f{ n_sub_ps(a.v, n_set1_ps(b)) }; }
    friend simd_f operator*(simd_f a, float b) { return simd_f{ n_mul_ps(a.v, n_set1_ps(b)) }; }
    friend simd_f operator+(float a, simd_f b) { return simd_f{ n_add_ps(n_set1_ps(a), b.v) }; }
    friend simd_f operator-(float a, simd_f b) { return simd_f{ n_sub_ps(n_set1_ps(a), b.v) }; }
    friend simd_f operator*(float a, simd_f b) { return simd_f{ n_mul_ps(n_set1_ps(a), b.v) }; }
    friend simd_f min(simd_f a, simd_f b) { return simd_f{ n_min_ps(a.v, b.v) }; }
    friend simd_f max(simd_f a, simd_f b) { return simd_f{ n_max_ps(a.v, b.v) }; }
};

struct simd_i {
    n_int v;
    simd_i() = default;
    explicit simd_i(n_int val) : v(val) {}
    static simd_i broadcast(int s) { return simd_i{ n_set1_epi32(s) }; }
    static simd_i sequence(int base) {
        return simd_i{ n_set_epi32(base+7, base+6, base+5, base+4, base+3, base+2, base+1, base) };
    }
    static simd_i load(const int* p) { return simd_i{ n_load_epi32(p) }; }
    void store(int* p) const { n_store_epi32(p, v); }

    friend simd_i operator+(simd_i a, simd_i b) { return simd_i{ n_add_epi32(a.v, b.v) }; }
    friend simd_i operator+(simd_i a, int b) { return simd_i{ n_add_epi32(a.v, n_set1_epi32(b)) }; }
    friend simd_i operator+(int a, simd_i b) { return simd_i{ n_add_epi32(n_set1_epi32(a), b.v) }; }
};

// 需要在 simd_i 定义完成后实现 gather
inline simd_f simd_f::gathf(const float* base, simd_i idx) {
    return simd_f{ n_gather_ps<sizeof(EntJoy::Mathematics::float2)>(base, idx.v) };
}
inline simd_f simd_f::gathfy(const float* base, simd_i idx) {
    return simd_f{ n_gather_ps<sizeof(EntJoy::Mathematics::float2)>(base + 1, idx.v) };
}

static simd_f blend(simd_f f, simd_f t, simd_mask m) { return simd_f{ n_blend_ps(f.v, t.v, m.m) }; }
static simd_i blend(simd_i f, simd_i t, simd_mask m) { return simd_i{ n_blend_epi32(f.v, t.v, m.m) }; }
static float hmin(simd_f v) { return n_hmin_ps(v.v); }
static float hmax(simd_f v) { return n_hmax_ps(v.v); }
static float hsum(simd_f v) { return n_hsum_ps(v.v); }

#endif
