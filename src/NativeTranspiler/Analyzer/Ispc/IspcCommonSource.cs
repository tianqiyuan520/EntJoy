namespace NativeTranspiler.Analyzer
{
    /// <summary>EntJoyCommon.ispc 的内容：ISPC 侧数学、向量结构与 Interlocked 宏。</summary>
    internal static class IspcCommonSource
    {
        public static string Generate()
        {
            // include guard：job 的 .ispc 与它 #include 的 helper .ispc 都会引入本文件，
            // 无 guard 会在同一翻译单元内重复定义 make_*/operator* → ISPC 报重定义。
            return "#ifndef __ENTJOY_ISPC_COMMON_DEFINED\n#define __ENTJOY_ISPC_COMMON_DEFINED\n" + @"
// NativeMath.ispc – ISPC compatible math library
struct float2 { float x; float y; };
struct int2   { int x; int y; };
struct uint2  { unsigned int x; unsigned int y; };

// EventBuffer POD（SendEvent 生成的 ISPC 代码依赖） 
// 注意：ISPC 中 uniform void* 非法（void 不能带 uniform 限定），data 用 uniform int*（uniform→uniform cast 合法，
// 且 varying→uniform 指针 cast 被禁止，裸 void* 是 varying 指针无法 cast 到 uniform T*）。
// count 保持 uniform int*（atomic 需要 uniform 指针）。
struct __EntJoyEventBuffer {
    uniform int* data;
    uniform int* count;
    uniform int capacity;
    uniform int elementSize;
};

// helpers (static to avoid duplicate symbols) 
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
static uniform struct float2 make_uniform_float2(uniform float x, uniform float y) {
    uniform struct float2 r; r.x = x; r.y = y; return r;
}
static uniform struct int2 make_uniform_int2(uniform int x, uniform int y) {
    uniform struct int2 r; r.x = x; r.y = y; return r;
}
static uniform struct uint2 make_uniform_uint2(uniform unsigned int x, uniform unsigned int y) {
    uniform struct uint2 r; r.x = x; r.y = y; return r;
}

// type conversions
static struct float2 float2_from_int2(struct int2 v) { return make_float2(v.x, v.y); }
static struct int2 int2_from_float2(struct float2 v) { return make_int2((int)v.x, (int)v.y); }

// float2 operators 
static struct float2 operator-(struct float2 a) {
    return make_float2(-a.x, -a.y);
}
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
// uniform 变体：uniform 标量循环（for (uniform int) + if (programIndex!=0) return）里
//   所有局部都是 uniform，`uniform float2 * uniform float` 若只匹配上面的 varying 重载，
//   结果会是 varying struct，无法赋给 uniform 局部（探针 _probe5 实证）。
//   ISPC 允许仅靠 uniform/varying 区分重载；varying 场景传 uniform 实参会自动 splat，故两者可共存。
static uniform struct float2 operator*(uniform struct float2 v, uniform float s) {
    uniform struct float2 r; r.x = v.x * s; r.y = v.y * s; return r;
}
static uniform struct float2 operator*(uniform float s, uniform struct float2 v) { return v * s; }
static uniform struct float2 operator/(uniform struct float2 v, uniform float s) {
    uniform struct float2 r; r.x = v.x / s; r.y = v.y / s; return r;
}
static uniform struct float2 operator+(uniform struct float2 a, uniform struct float2 b) {
    uniform struct float2 r; r.x = a.x + b.x; r.y = a.y + b.y; return r;
}
static uniform struct float2 operator-(uniform struct float2 a, uniform struct float2 b) {
    uniform struct float2 r; r.x = a.x - b.x; r.y = a.y - b.y; return r;
}

// int2 operators 
static struct int2 operator-(struct int2 a) {
    return make_int2(-a.x, -a.y);
}
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

// uint2 operators 
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

// math functions 
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

// Interlocked 宏（ISPC 侧） 
// 基类 StatementTranslator.TranslateInterlockedCall 会吐 C++ 的 INTERLOCKED_* 宏。
// ISPC 的静态方法 lane-callable helper（IspcGenerator.Helper）若把 C++ 宏直接落进 .ispc，
// 会报 Undeclared symbol INTERLOCKED_EXCHANGE32。这里给 ISPC 等价定义：
// ISPC 的 atomic_add_global 是 fetch-add（返回旧值），故 ADD/INCREMENT 系补回增量以对齐 C# 的 add-fetch 语义。
// 已知限制：atomic_add_global 只接受 32 位 load/store（64 位原子在 ISPC 不可用）。
" + IspcAtomicMacros + @"
" + "\n#endif // __ENTJOY_ISPC_COMMON_DEFINED\n";
        }

        /// <summary>
        /// ISPC 侧 Interlocked 宏定义（拼进 EntJoyCommon.ispc）。
        /// 单独做成普通字符串常量：C# 逐字字符串里写 <c>#</c> 开头会被当成 C# 预处理指令（CS1032），
        /// 而 <c>\u0023</c> 在逐字字符串里不转义（CS1056）。
        /// </summary>
        private const string IspcAtomicMacros =
            "#define INTERLOCKED_FETCH_ADD32(ptr, val)       atomic_add_global((ptr), (val))\n" +
            "#define INTERLOCKED_FETCH_SUB32(ptr, val)       atomic_subtract_global((ptr), (val))\n" +
            "#define INTERLOCKED_EXCHANGE32(ptr, val)        atomic_swap_global((ptr), (val))\n" +
            "#define INTERLOCKED_ADD_AND_FETCH32(ptr, val)   (atomic_add_global((ptr), (val)) + (val))\n" +
            "#define INTERLOCKED_INCREMENT_AND_FETCH32(ptr)  (atomic_add_global((ptr), 1) + 1)\n" +
            "#define INTERLOCKED_DECREMENT_AND_FETCH32(ptr)  (atomic_subtract_global((ptr), 1) - 1)\n" +
            "#define INTERLOCKED_COMPARE_EXCHANGE32(ptr, oldVal, newVal) atomic_compare_exchange_global((ptr), (oldVal), (newVal))\n";
    }
}