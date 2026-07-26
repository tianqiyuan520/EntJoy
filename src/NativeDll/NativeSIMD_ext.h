// NativeSIMD_ext.h — Extended SIMD types
// n_float2  / n_int2  — struct-of-SIMD-arrays for vector types
// n_double operations not here (they're in NativeSIMD.h)
#pragma once
#include "NativeSIMD.h"

// ============================================================
// n_float2 — float2 as pair of n_float
// ============================================================
struct n_float2 {
    n_float x;
    n_float y;

    // Component-wise arithmetic: n_float2 × n_float2
    static inline n_float2 add(n_float2 a, n_float2 b) { return { n_add_ps(a.x, b.x), n_add_ps(a.y, b.y) }; }
    static inline n_float2 sub(n_float2 a, n_float2 b) { return { n_sub_ps(a.x, b.x), n_sub_ps(a.y, b.y) }; }
    static inline n_float2 mul(n_float2 a, n_float2 b) { return { n_mul_ps(a.x, b.x), n_mul_ps(a.y, b.y) }; }
    static inline n_float2 div(n_float2 a, n_float2 b) { return { n_div_ps(a.x, b.x), n_div_ps(a.y, b.y) }; }

    // scalar float broadcast
    static inline n_float2 add_s(n_float2 a, float b) { return { n_add_ps(a.x, n_set1_ps(b)), n_add_ps(a.y, n_set1_ps(b)) }; }
    static inline n_float2 sub_s(n_float2 a, float b) { return { n_sub_ps(a.x, n_set1_ps(b)), n_sub_ps(a.y, n_set1_ps(b)) }; }
    static inline n_float2 mul_s(n_float2 a, float b) { return { n_mul_ps(a.x, n_set1_ps(b)), n_mul_ps(a.y, n_set1_ps(b)) }; }
    static inline n_float2 div_s(n_float2 a, float b) { return { n_div_ps(a.x, n_set1_ps(b)), n_div_ps(a.y, n_set1_ps(b)) }; }

    // scalar int → float promotion
    static inline n_float2 add_i(n_float2 a, int b) { return n_float2::add_s(a, (float)b); }
    static inline n_float2 sub_i(n_float2 a, int b) { return n_float2::sub_s(a, (float)b); }
    static inline n_float2 mul_i(n_float2 a, int b) { return n_float2::mul_s(a, (float)b); }
    static inline n_float2 div_i(n_float2 a, int b) { return n_float2::div_s(a, (float)b); }
};

// Operator-style free functions for n_float2
static inline n_float2 n_float2_add(n_float2 a, n_float2 b) { return n_float2::add(a, b); }
static inline n_float2 n_float2_sub(n_float2 a, n_float2 b) { return n_float2::sub(a, b); }
static inline n_float2 n_float2_mul(n_float2 a, n_float2 b) { return n_float2::mul(a, b); }
static inline n_float2 n_float2_div(n_float2 a, n_float2 b) { return n_float2::div(a, b); }
static inline n_float2 n_float2_add_s(n_float2 a, float b) { return n_float2::add_s(a, b); }
static inline n_float2 n_float2_sub_s(n_float2 a, float b) { return n_float2::sub_s(a, b); }
static inline n_float2 n_float2_mul_s(n_float2 a, float b) { return n_float2::mul_s(a, b); }
static inline n_float2 n_float2_div_s(n_float2 a, float b) { return n_float2::div_s(a, b); }
static inline n_float2 n_float2_add_i(n_float2 a, int b) { return n_float2::add_i(a, b); }
static inline n_float2 n_float2_sub_i(n_float2 a, int b) { return n_float2::sub_i(a, b); }
static inline n_float2 n_float2_mul_i(n_float2 a, int b) { return n_float2::mul_i(a, b); }
static inline n_float2 n_float2_div_i(n_float2 a, int b) { return n_float2::div_i(a, b); }
// Reverse-order float scalar
static inline n_float2 n_float2_add_sr(float a, n_float2 b) { return n_float2_add_s(b, a); }
static inline n_float2 n_float2_sub_sr(float a, n_float2 b) { return { n_sub_ps(n_set1_ps(a), b.x), n_sub_ps(n_set1_ps(a), b.y) }; }
static inline n_float2 n_float2_mul_sr(float a, n_float2 b) { return n_float2_mul_s(b, a); }
static inline n_float2 n_float2_div_sr(float a, n_float2 b) { return { n_div_ps(n_set1_ps(a), b.x), n_div_ps(n_set1_ps(a), b.y) }; }

// Gather: stride-gather both components from struct array
template<int stride>
static inline n_float2 n_float2_gather(const void* base, n_int idx) {
    return { n_gather_ps<stride>((const float*)base, idx), n_gather_ps<stride>(((const float*)base) + 1, idx) };
}
// Gather from specific field offset (base + fieldByteOffset is already added)
template<int stride>
static inline n_float2 n_float2_gather_at(const float* base_x, n_int idx) {
    return { n_gather_ps<stride>(base_x, idx), n_gather_ps<stride>(base_x + 1, idx) };
}

// ============================================================
// n_int2 — int2 as pair of n_int
// ============================================================
struct n_int2 {
    n_int x;
    n_int y;

    static inline n_int2 add(n_int2 a, n_int2 b) { return { n_add_epi32(a.x, b.x), n_add_epi32(a.y, b.y) }; }
    static inline n_int2 sub(n_int2 a, n_int2 b) { return { n_sub_epi32(a.x, b.x), n_sub_epi32(a.y, b.y) }; }
    static inline n_int2 mul(n_int2 a, n_int2 b) { return { n_mullo_epi32(a.x, b.x), n_mullo_epi32(a.y, b.y) }; }

    // scalar int broadcast
    static inline n_int2 add_s(n_int2 a, int b) { return { n_add_epi32(a.x, n_set1_epi32(b)), n_add_epi32(a.y, n_set1_epi32(b)) }; }
    static inline n_int2 sub_s(n_int2 a, int b) { return { n_sub_epi32(a.x, n_set1_epi32(b)), n_sub_epi32(a.y, n_set1_epi32(b)) }; }
    static inline n_int2 mul_s(n_int2 a, int b) { return { n_mullo_epi32(a.x, n_set1_epi32(b)), n_mullo_epi32(a.y, n_set1_epi32(b)) }; }
    static inline n_int2 div_s(n_int2 a, int b) { // per-lane division
        int lx[8], ly[8], lr[8];
        n_store_epi32(lx, a.x); n_store_epi32(ly, a.y);
        for (int i = 0; i < NSIMD_WIDTH; i++) { lr[i] = lx[i] / b; } n_int rx = n_load_epi32(lr);
        for (int i = 0; i < NSIMD_WIDTH; i++) { lr[i] = ly[i] / b; } n_int ry = n_load_epi32(lr);
        return { rx, ry };
    }

    // int2 × float → float2 promotion
    static inline n_float2 mul_f(n_int2 a, float b) {
        return { n_mul_ps(n_cvtepi32_ps(a.x), n_set1_ps(b)), n_mul_ps(n_cvtepi32_ps(a.y), n_set1_ps(b)) };
    }
    static inline n_float2 add_f(n_int2 a, float b) {
        return { n_add_ps(n_cvtepi32_ps(a.x), n_set1_ps(b)), n_add_ps(n_cvtepi32_ps(a.y), n_set1_ps(b)) };
    }
    static inline n_float2 sub_f(n_int2 a, float b) {
        return { n_sub_ps(n_cvtepi32_ps(a.x), n_set1_ps(b)), n_sub_ps(n_cvtepi32_ps(a.y), n_set1_ps(b)) };
    }
    static inline n_float2 div_f(n_int2 a, float b) {
        return { n_div_ps(n_cvtepi32_ps(a.x), n_set1_ps(b)), n_div_ps(n_cvtepi32_ps(a.y), n_set1_ps(b)) };
    }
};

// Free functions
static inline n_int2 n_int2_add(n_int2 a, n_int2 b) { return n_int2::add(a, b); }
static inline n_int2 n_int2_sub(n_int2 a, n_int2 b) { return n_int2::sub(a, b); }
static inline n_int2 n_int2_mul(n_int2 a, n_int2 b) { return n_int2::mul(a, b); }
static inline n_int2 n_int2_add_s(n_int2 a, int b) { return n_int2::add_s(a, b); }
static inline n_int2 n_int2_sub_s(n_int2 a, int b) { return n_int2::sub_s(a, b); }
static inline n_int2 n_int2_mul_s(n_int2 a, int b) { return n_int2::mul_s(a, b); }
static inline n_int2 n_int2_div_s(n_int2 a, int b) { return n_int2::div_s(a, b); }
static inline n_int2 n_int2_add_sr(int a, n_int2 b) { return n_int2_add_s(b, a); }
static inline n_int2 n_int2_sub_sr(int a, n_int2 b) {
    return { n_sub_epi32(n_set1_epi32(a), b.x), n_sub_epi32(n_set1_epi32(a), b.y) };
}
static inline n_int2 n_int2_mul_sr(int a, n_int2 b) { return n_int2_mul_s(b, a); }
// int2 × float → float2 (promotion)
static inline n_float2 n_int2_mul_f(n_int2 a, float b) { return n_int2::mul_f(a, b); }
static inline n_float2 n_int2_add_f(n_int2 a, float b) { return n_int2::add_f(a, b); }
static inline n_float2 n_int2_sub_f(n_int2 a, float b) { return n_int2::sub_f(a, b); }
static inline n_float2 n_int2_div_f(n_int2 a, float b) { return n_int2::div_f(a, b); }
static inline n_float2 n_int2_mul_fr(float a, n_int2 b) { return n_int2_mul_f(b, a); }
static inline n_float2 n_int2_add_fr(float a, n_int2 b) { return n_int2_add_f(b, a); }
static inline n_float2 n_int2_sub_fr(float a, n_int2 b) {
    return { n_sub_ps(n_set1_ps(a), n_cvtepi32_ps(b.x)), n_sub_ps(n_set1_ps(a), n_cvtepi32_ps(b.y)) };
}

// int2 gather
template<int stride>
static inline n_int2 n_int2_gather(const void* base, n_int idx) {
    return { n_gather_epi32<stride>((const int*)base, idx), n_gather_epi32<stride>(((const int*)base) + 1, idx) };
}

// n_int2 min/max
static inline n_int2 n_int2_min(n_int2 a, n_int2 b) {
    return { n_min_epi32(a.x, b.x), n_min_epi32(a.y, b.y) };
}
static inline n_int2 n_int2_max(n_int2 a, n_int2 b) {
    return { n_max_epi32(a.x, b.x), n_max_epi32(a.y, b.y) };
}
