// ============================================================
// CodeTemplates.cs — 共享代码模板
//   供 C++/ISPC 生成器使用的导出宏、原子操作宏、全局 ISPC 头等
// ============================================================
using System.Text;

namespace NativeTranspiler.Analyzer.Common
{
    /// <summary>
    /// 提供所有生成器共享的代码模板（宏定义、头文件等），消除跨文件重复。
    /// </summary>
    public static class CodeTemplates
    {
        /// <summary>
        /// 导出宏定义：
        ///   HEAD: dllexport/dllimport, CALLINGCONVENTION: __cdecl,
        ///   RESTRICT: __restrict/__restrict__ 跨平台兼容
        /// </summary>
        public static string GenerateExportMacros() => @"
#ifdef __cplusplus
#define EXTERNC extern ""C""
#else
#define EXTERNC
#endif

#ifdef _WIN32
#define CALLINGCONVENTION __cdecl
#else
#define CALLINGCONVENTION
#endif

#ifdef DLL_IMPORT
#define HEAD EXTERNC __declspec(dllimport)
#else
#define HEAD EXTERNC __declspec(dllexport)
#endif

// restrict keyword compatibility
#if defined(_MSC_VER) || defined(__clang__)
  #define RESTRICT __restrict
#else
  #define RESTRICT __restrict__
#endif
";

        /// <summary>
        /// 跨编译器原子操作宏：
        ///   MSVC → _InterlockedExchangeAdd / _InterlockedIncrement 等
        ///   GCC  → __sync_fetch_and_add / __sync_add_and_fetch 等
        /// </summary>
        public static string GenerateAtomicMacros() => @"
// Cross-compiler atomic macros (stateless, no std::atomic_ref)
#ifdef _MSC_VER
#include <intrin.h>
#define INTERLOCKED_FETCH_ADD(ptr, val) _InterlockedExchangeAdd((long*)(ptr), (val))
#define INTERLOCKED_FETCH_SUB(ptr, val) _InterlockedExchangeAdd((long*)(ptr), -(val))
#define INTERLOCKED_EXCHANGE(ptr, val)   _InterlockedExchange((long*)(ptr), (val))
#define INTERLOCKED_ADD_AND_FETCH(ptr, val)   (_InterlockedExchangeAdd((long*)(ptr), (val)) + (val))
#define INTERLOCKED_INCREMENT_AND_FETCH(ptr)  _InterlockedIncrement((long*)(ptr))
#define INTERLOCKED_DECREMENT_AND_FETCH(ptr)  _InterlockedDecrement((long*)(ptr))
#define INTERLOCKED_SUB_AND_FETCH(ptr, val)   (_InterlockedExchangeAdd((long*)(ptr), -(val)) - (val))
#define INTERLOCKED_COMPARE_EXCHANGE(ptr, oldVal, newVal) _InterlockedCompareExchange((long*)(ptr), (newVal), (oldVal))
#else
#define INTERLOCKED_FETCH_ADD(ptr, val) __sync_fetch_and_add((ptr), (val))
#define INTERLOCKED_FETCH_SUB(ptr, val) __sync_fetch_and_sub((ptr), (val))
#define INTERLOCKED_EXCHANGE(ptr, val)   __sync_lock_test_and_set((ptr), (val))
#define INTERLOCKED_ADD_AND_FETCH(ptr, val)   __sync_add_and_fetch((ptr), (val))
#define INTERLOCKED_INCREMENT_AND_FETCH(ptr)  __sync_add_and_fetch((ptr), 1)
#define INTERLOCKED_DECREMENT_AND_FETCH(ptr)  __sync_sub_and_fetch((ptr), 1)
#define INTERLOCKED_SUB_AND_FETCH(ptr, val)   __sync_sub_and_fetch((ptr), (val))
#define INTERLOCKED_COMPARE_EXCHANGE(ptr, oldVal, newVal) __sync_val_compare_and_swap((ptr), (oldVal), (newVal))
#endif
";

        /// <summary>
        /// 生成 ISPC 通用头文件 EntJoyCommon.ispc 的内容，
        /// 包含 float2/int2/uint2 结构定义 + 所有运算符 + 数学函数
        /// </summary>
        public static string GenerateCommonIspcHeader() => @"
// NativeMath.ispc – ISPC compatible math library
struct float2 { float x; float y; };
struct int2   { int x; int y; };
struct uint2  { unsigned int x; unsigned int y; };

// ---------- helpers (static to avoid duplicate symbols) ----------
static struct float2 make_float2(float x, float y) {
    struct float2 r; r.x = x; r.y = y; return r;
}
static struct float2 make_float2(float v) { return make_float2(v, v); }
static struct int2 make_int2(int x, int y) {
    struct int2 r; r.x = x; r.y = y; return r;
}
static struct int2 make_int2(int v) { return make_int2(v, v); }
static struct uint2 make_uint2(unsigned int x, unsigned int y) {
    struct uint2 r; r.x = x; r.y = y; return r;
}
static struct uint2 make_uint2(unsigned int v) { return make_uint2(v, v); }

// type conversions
static struct float2 float2_from_int2(struct int2 v) { return make_float2(v.x, v.y); }
static struct int2 int2_from_float2(struct float2 v) { return make_int2((int)v.x, (int)v.y); }

// ---------- float2 operators ----------
static struct float2 operator+(struct float2 a, struct float2 b) {
    struct float2 r; r.x = a.x + b.x; r.y = a.y + b.y; return r;
}
static struct float2 operator-(struct float2 a, struct float2 b) {
    struct float2 r; r.x = a.x - b.x; r.y = a.y - b.y; return r;
}
static struct float2 operator*(struct float2 a, struct float2 b) {
    struct float2 r; r.x = a.x * b.x; r.y = a.y * b.y; return r;
}
static struct float2 operator/(struct float2 a, struct float2 b) {
    struct float2 r; r.x = a.x / b.x; r.y = a.y / b.y; return r;
}
static struct float2 operator*(struct float2 v, float s) {
    struct float2 r; r.x = v.x * s; r.y = v.y * s; return r;
}
static struct float2 operator*(float s, struct float2 v) { return v * s; }
static struct float2 operator/(struct float2 v, float s) {
    struct float2 r; r.x = v.x / s; r.y = v.y / s; return r;
}

// ---------- int2 operators ----------
static struct int2 operator+(struct int2 a, struct int2 b) {
    struct int2 r; r.x = a.x + b.x; r.y = a.y + b.y; return r;
}
static struct int2 operator-(struct int2 a, struct int2 b) {
    struct int2 r; r.x = a.x - b.x; r.y = a.y - b.y; return r;
}
static struct int2 operator*(struct int2 a, struct int2 b) {
    struct int2 r; r.x = a.x * b.x; r.y = a.y * b.y; return r;
}
static struct int2 operator/(struct int2 a, struct int2 b) {
    struct int2 r; r.x = a.x / b.x; r.y = a.y / b.y; return r;
}
static struct int2 operator*(struct int2 v, int s) {
    struct int2 r; r.x = v.x * s; r.y = v.y * s; return r;
}
static struct int2 operator*(int s, struct int2 v) { return v * s; }
static struct int2 operator+(struct int2 a, int b) {
    struct int2 r; r.x = a.x + b; r.y = a.y + b; return r;
}
static struct int2 operator-(struct int2 a, int b) {
    struct int2 r; r.x = a.x - b; r.y = a.y - b; return r;
}

// ---------- uint2 operators ----------
static struct uint2 operator+(struct uint2 a, struct uint2 b) {
    struct uint2 r; r.x = a.x + b.x; r.y = a.y + b.y; return r;
}
static struct uint2 operator-(struct uint2 a, struct uint2 b) {
    struct uint2 r; r.x = a.x - b.x; r.y = a.y - b.y; return r;
}
static struct uint2 operator*(struct uint2 a, struct uint2 b) {
    struct uint2 r; r.x = a.x * b.x; r.y = a.y * b.y; return r;
}
static struct uint2 operator*(struct uint2 v, unsigned int s) {
    struct uint2 r; r.x = v.x * s; r.y = v.y * s; return r;
}

// ---------- math functions ----------
static float dot(struct float2 a, struct float2 b) { return a.x * b.x + a.y * b.y; }
static float lengthsq(struct float2 v) { return dot(v, v); }
static float length(struct float2 v) { return sqrt(lengthsq(v)); }
static struct float2 normalize(struct float2 v) {
    float l = length(v);
    if (l > 0.f)
        return v * (1.f / l);
    else
        return make_float2(0.f, 0.f);
}
static struct float2 abs(struct float2 v) {
    struct float2 r; r.x = abs(v.x); r.y = abs(v.y); return r;
}
static struct int2 abs(struct int2 v) {
    struct int2 r; r.x = abs(v.x); r.y = abs(v.y); return r;
}
static struct float2 min(struct float2 a, struct float2 b) {
    struct float2 r; r.x = (a.x < b.x ? a.x : b.x); r.y = (a.y < b.y ? a.y : b.y); return r;
}
static struct int2 min(struct int2 a, struct int2 b) {
    struct int2 r; r.x = (a.x < b.x ? a.x : b.x); r.y = (a.y < b.y ? a.y : b.y); return r;
}
static struct float2 max(struct float2 a, struct float2 b) {
    struct float2 r; r.x = (a.x > b.x ? a.x : b.x); r.y = (a.y > b.y ? a.y : b.y); return r;
}
static struct int2 max(struct int2 a, struct int2 b) {
    struct int2 r; r.x = (a.x > b.x ? a.x : b.x); r.y = (a.y > b.y ? a.y : b.y); return r;
}
static struct float2 clamp(struct float2 v, struct float2 lo, struct float2 hi) {
    return min(max(v, lo), hi);
}
static struct int2 clamp(struct int2 v, struct int2 lo, struct int2 hi) {
    return min(max(v, lo), hi);
}
static struct float2 floor(struct float2 v) {
    struct float2 r; r.x = floor(v.x); r.y = floor(v.y); return r;
}
static struct float2 ceil(struct float2 v) {
    struct float2 r; r.x = ceil(v.x); r.y = ceil(v.y); return r;
}
static float distancesq(struct float2 a, struct float2 b) { return lengthsq(b - a); }
static float lerp(float a, float b, float t) { return a + (b - a) * t; }
static struct float2 lerp(struct float2 a, struct float2 b, float t) {
    return a + (b - a) * t;
}
";
    }
}
