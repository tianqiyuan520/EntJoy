
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
