using System;
using System.Runtime.CompilerServices;

namespace EntJoy.Mathematics
{
    public struct float2 : IEquatable<float2>
    {
        public float x, y;

        public static readonly float2 zero = new float2(0f);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float2(float x, float y) { this.x = x; this.y = y; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float2(float v) { x = y = v; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float2(bool v) { x = y = v ? 1f : 0f; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float2(int v) { x = y = v; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float2(uint v) { x = y = v; }

        // 转换
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator float2(float v) => new float2(v);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static explicit operator float2(bool v) => new float2(v);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator float2(int v) => new float2(v);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator float2(uint v) => new float2(v);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static explicit operator int2(float2 v) => new int2((int)v.x, (int)v.y);

        // 运算符
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 operator +(float2 lhs, float2 rhs) => new float2(lhs.x + rhs.x, lhs.y + rhs.y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 operator -(float2 lhs, float2 rhs) => new float2(lhs.x - rhs.x, lhs.y - rhs.y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 operator *(float2 lhs, float2 rhs) => new float2(lhs.x * rhs.x, lhs.y * rhs.y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 operator *(float2 lhs, float rhs) => new float2(lhs.x * rhs, lhs.y * rhs);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 operator *(float lhs, float2 rhs) => new float2(lhs * rhs.x, lhs * rhs.y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 operator /(float2 lhs, float2 rhs) => new float2(lhs.x / rhs.x, lhs.y / rhs.y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 operator /(float2 lhs, float rhs) => new float2(lhs.x / rhs, lhs.y / rhs);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 operator %(float2 lhs, float2 rhs) => new float2(lhs.x % rhs.x, lhs.y % rhs.y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 operator %(float2 lhs, float rhs) => new float2(lhs.x % rhs, lhs.y % rhs);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 operator ++(float2 val) => new float2(++val.x, ++val.y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 operator --(float2 val) => new float2(--val.x, --val.y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 operator -(float2 val) => new float2(-val.x, -val.y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 operator +(float2 val) => val;

        // 比较
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(float2 lhs, float2 rhs) => lhs.x == rhs.x && lhs.y == rhs.y;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(float2 lhs, float2 rhs) => !(lhs == rhs);

        // 索引器
        public float this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => index == 0 ? x : index == 1 ? y : throw new IndexOutOfRangeException("float2 index must be 0 or 1");
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set { if (index == 0) x = value; else if (index == 1) y = value; else throw new IndexOutOfRangeException("float2 index must be 0 or 1"); }
        }

        // Swizzle
        public float2 xx { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new float2(x, x); }
        public float2 xy { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new float2(x, y); set { x = value.x; y = value.y; } }
        public float2 yx { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new float2(y, x); set { y = value.x; x = value.y; } }
        public float2 yy { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new float2(y, y); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(float2 other) => x == other.x && y == other.y;
        public override bool Equals(object obj) => obj is float2 other && Equals(other);
        public override int GetHashCode()
        {
            unchecked { int hash = 17; hash = hash * 31 + x.GetHashCode(); hash = hash * 31 + y.GetHashCode(); return hash; }
        }
        public override string ToString() => $"float2({x}f, {y}f)";
    }

    public struct int2 : IEquatable<int2>
    {
        public int x, y;

        public static readonly int2 zero = new int2(0);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int2(int x, int y) { this.x = x; this.y = y; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int2(int v) { x = y = v; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int2(bool v) { x = y = v ? 1 : 0; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int2(uint v) { x = y = (int)v; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int2(float v) { x = y = (int)v; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator int2(int v) => new int2(v);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static explicit operator int2(bool v) => new int2(v);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static explicit operator int2(uint v) => new int2((int)v);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static explicit operator int2(float v) => new int2((int)v);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator float2(int2 v) => new float2(v.x, v.y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int2 operator +(int2 lhs, int2 rhs) => new int2(lhs.x + rhs.x, lhs.y + rhs.y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int2 operator -(int2 lhs, int2 rhs) => new int2(lhs.x - rhs.x, lhs.y - rhs.y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int2 operator *(int2 lhs, int2 rhs) => new int2(lhs.x * rhs.x, lhs.y * rhs.y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int2 operator *(int2 lhs, int rhs) => new int2(lhs.x * rhs, lhs.y * rhs);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int2 operator *(int lhs, int2 rhs) => new int2(lhs * rhs.x, lhs * rhs.y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int2 operator /(int2 lhs, int2 rhs) => new int2(lhs.x / rhs.x, lhs.y / rhs.y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int2 operator /(int2 lhs, int rhs) => new int2(lhs.x / rhs, lhs.y / rhs);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int2 operator %(int2 lhs, int2 rhs) => new int2(lhs.x % rhs.x, lhs.y % rhs.y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int2 operator %(int2 lhs, int rhs) => new int2(lhs.x % rhs, lhs.y % rhs);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int2 operator ++(int2 val) => new int2(++val.x, ++val.y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int2 operator --(int2 val) => new int2(--val.x, --val.y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int2 operator -(int2 val) => new int2(-val.x, -val.y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int2 operator +(int2 val) => val;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(int2 lhs, int2 rhs) => lhs.x == rhs.x && lhs.y == rhs.y;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(int2 lhs, int2 rhs) => !(lhs == rhs);

        public int this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => index == 0 ? x : index == 1 ? y : throw new IndexOutOfRangeException("int2 index must be 0 or 1");
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set { if (index == 0) x = value; else if (index == 1) y = value; else throw new IndexOutOfRangeException("int2 index must be 0 or 1"); }
        }

        public int2 xx { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new int2(x, x); }
        public int2 xy { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new int2(x, y); set { x = value.x; y = value.y; } }
        public int2 yx { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new int2(y, x); set { y = value.x; x = value.y; } }
        public int2 yy { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new int2(y, y); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(int2 other) => x == other.x && y == other.y;
        public override bool Equals(object obj) => obj is int2 other && Equals(other);
        public override int GetHashCode()
        {
            unchecked { int hash = 17; hash = hash * 31 + x; hash = hash * 31 + y; return hash; }
        }
        public override string ToString() => $"int2({x}, {y})";
    }

    public struct uint2 : IEquatable<uint2>
    {
        public uint x, y;

        public static readonly uint2 zero = new uint2(0u);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint2(uint x, uint y) { this.x = x; this.y = y; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint2(uint v) { x = y = v; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint2(int v) { x = y = (uint)v; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint2 operator +(uint2 lhs, uint2 rhs) => new uint2(lhs.x + rhs.x, lhs.y + rhs.y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint2 operator -(uint2 lhs, uint2 rhs) => new uint2(lhs.x - rhs.x, lhs.y - rhs.y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint2 operator *(uint2 lhs, uint2 rhs) => new uint2(lhs.x * rhs.x, lhs.y * rhs.y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint2 operator *(uint2 lhs, uint rhs) => new uint2(lhs.x * rhs, lhs.y * rhs);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint2 operator /(uint2 lhs, uint2 rhs) => new uint2(lhs.x / rhs.x, lhs.y / rhs.y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint2 operator %(uint2 lhs, uint2 rhs) => new uint2(lhs.x % rhs.x, lhs.y % rhs.y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint2 operator ++(uint2 val) => new uint2(++val.x, ++val.y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint2 operator --(uint2 val) => new uint2(--val.x, --val.y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(uint2 lhs, uint2 rhs) => lhs.x == rhs.x && lhs.y == rhs.y;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(uint2 lhs, uint2 rhs) => !(lhs == rhs);

        public uint this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => index == 0 ? x : index == 1 ? y : throw new IndexOutOfRangeException("uint2 index must be 0 or 1");
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set { if (index == 0) x = value; else if (index == 1) y = value; else throw new IndexOutOfRangeException("uint2 index must be 0 or 1"); }
        }

        public uint2 xx { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new uint2(x, x); }
        public uint2 xy { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new uint2(x, y); set { x = value.x; y = value.y; } }
        public uint2 yx { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new uint2(y, x); set { y = value.x; x = value.y; } }
        public uint2 yy { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new uint2(y, y); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(uint2 other) => x == other.x && y == other.y;
        public override bool Equals(object obj) => obj is uint2 other && Equals(other);
        public override int GetHashCode()
        {
            unchecked { int hash = 17; hash = hash * 31 + (int)x; hash = hash * 31 + (int)y; return hash; }
        }
        public override string ToString() => $"uint2({x}, {y})";
    }

    // 数学函数库
    public static partial class math
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float dot(float2 a, float2 b) => a.x * b.x + a.y * b.y;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float lengthsq(float2 v) => dot(v, v);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float length(float2 v) => (float)Math.Sqrt(lengthsq(v));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 normalize(float2 v)
        {
            float lenSq = lengthsq(v);
            if (lenSq > 0) { float invLen = 1.0f / (float)Math.Sqrt(lenSq); return v * invLen; }
            return float2.zero;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 abs(float2 v) => new float2(Math.Abs(v.x), Math.Abs(v.y));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int2 abs(int2 v) => new int2(Math.Abs(v.x), Math.Abs(v.y));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 min(float2 a, float2 b) => new float2(Math.Min(a.x, b.x), Math.Min(a.y, b.y));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int2 min(int2 a, int2 b) => new int2(Math.Min(a.x, b.x), Math.Min(a.y, b.y));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 max(float2 a, float2 b) => new float2(Math.Max(a.x, b.x), Math.Max(a.y, b.y));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int2 max(int2 a, int2 b) => new int2(Math.Max(a.x, b.x), Math.Max(a.y, b.y));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int max(int x, int y) => (x > y) ? x : y;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float max(float x, float y) => (x > y) ? x : y;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int min(int x, int y) => (x < y) ? x : y;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float min(float x, float y) => (x < y) ? x : y;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 clamp(float2 v, float2 min, float2 max) => math.min(math.max(v, min), max);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int2 clamp(int2 v, int2 min, int2 max) => math.min(math.max(v, min), max);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int clamp(int v, int min, int max) => math.min(math.max(v, min), max);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 lerp(float2 a, float2 b, float t) => a + (b - a) * t;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float floor(float x) => (float)Math.Floor(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 floor(float2 x) => new float2(floor(x.x), floor(x.y));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 ceil(float2 x) => new float2(MathF.Ceiling(x.x), MathF.Ceiling(x.y));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ceil(float x) => MathF.Ceiling(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float distancesq(float x, float y) => (y - x) * (y - x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float distancesq(float2 x, float2 y) => lengthsq(y - x);

        // 哈希
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint hash(float2 v) => (uint)(v.x * 0x9e3779b9u) ^ (uint)(v.y * 0x85ebca77u);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint hash(int2 v) => (uint)(v.x * 0x9e3779b9) ^ (uint)(v.y * 0x85ebca77);

        // asuint（位 reinterpret）
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint asuint(float f) => Unsafe.As<float, uint>(ref f);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint2 asuint(float2 v) => new uint2(asuint(v.x), asuint(v.y));
    }
}
