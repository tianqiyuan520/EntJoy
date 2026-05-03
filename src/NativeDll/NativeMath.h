// NativeMath.h
#pragma once
#include <cmath>
#include <cstdint>

namespace EntJoy {
	namespace Mathematics {

		// ---------- float2 ----------
		struct float2 {
			float x, y;
			float2() : x(0), y(0) {}
			float2(float v) : x(v), y(v) {}
			float2(float x, float y) : x(x), y(y) {}
			float2(const float2&) = default;
			float2& operator=(const float2&) = default;

			float2 operator+(const float2& rhs) const { return float2(x + rhs.x, y + rhs.y); }
			float2 operator-(const float2& rhs) const { return float2(x - rhs.x, y - rhs.y); }
			float2 operator*(const float2& rhs) const { return float2(x * rhs.x, y * rhs.y); }
			float2 operator/(const float2& rhs) const { return float2(x / rhs.x, y / rhs.y); }
			float2 operator*(float s) const { return float2(x * s, y * s); }
			friend float2 operator*(float s, const float2& v) { return v * s; }
			float2 operator/(float s) const { return float2(x / s, y / s); }

			float2 operator-() const { return float2(-x, -y); }
			float2 operator+() const { return *this; }

			float2& operator+=(const float2& rhs) { x += rhs.x; y += rhs.y; return *this; }
			float2& operator-=(const float2& rhs) { x -= rhs.x; y -= rhs.y; return *this; }
			float2& operator*=(const float2& rhs) { x *= rhs.x; y *= rhs.y; return *this; }
			float2& operator/=(const float2& rhs) { x /= rhs.x; y /= rhs.y; return *this; }
			float2& operator*=(float s) { x *= s; y *= s; return *this; }
			float2& operator/=(float s) { x /= s; y /= s; return *this; }

			bool operator==(const float2& rhs) const { return x == rhs.x && y == rhs.y; }
			bool operator!=(const float2& rhs) const { return !(*this == rhs); }

			float& operator[](int i) { return (&x)[i]; }
			const float& operator[](int i) const { return (&x)[i]; }

			// 从 int2 转换
			float2(const struct int2& v);
			// 从 uint2 转换
			float2(const struct uint2& v);

			float2 xx() const { return float2(x, x); }
			float2 xy() const { return float2(x, y); }
			float2 yx() const { return float2(y, x); }
			float2 yy() const { return float2(y, y); }
		};

		// ---------- int2 ----------
		struct int2 {
			int x, y;
			int2() : x(0), y(0) {}
			int2(int v) : x(v), y(v) {}
			int2(int x, int y) : x(x), y(y) {}

			int2 operator+(const int2& rhs) const { return int2(x + rhs.x, y + rhs.y); }
			int2 operator-(const int2& rhs) const { return int2(x - rhs.x, y - rhs.y); }
			int2 operator*(const int2& rhs) const { return int2(x * rhs.x, y * rhs.y); }
			int2 operator/(const int2& rhs) const { return int2(x / rhs.x, y / rhs.y); }
			int2 operator*(int s) const { return int2(x * s, y * s); }
			friend int2 operator*(int s, const int2& v) { return v * s; }
			int2 operator-() const { return int2(-x, -y); }

			int2& operator+=(const int2& rhs) { x += rhs.x; y += rhs.y; return *this; }
			int2& operator-=(const int2& rhs) { x -= rhs.x; y -= rhs.y; return *this; }

			bool operator==(const int2& rhs) const { return x == rhs.x && y == rhs.y; }
			bool operator!=(const int2& rhs) const { return !(*this == rhs); }

			int& operator[](int i) { return (&x)[i]; }
			const int& operator[](int i) const { return (&x)[i]; }

			// 从 float2 转换（截断小数）
			int2(const float2& v) : x(static_cast<int>(v.x)), y(static_cast<int>(v.y)) {}
			// 从 uint2 转换
			int2(const struct uint2& v);

			int2 xx() const { return int2(x, x); }
			int2 xy() const { return int2(x, y); }
			int2 yx() const { return int2(y, x); }
			int2 yy() const { return int2(y, y); }
		};

		// ---------- uint2 ----------
		struct uint2 {
			uint32_t x, y;
			uint2() : x(0), y(0) {}
			uint2(uint32_t v) : x(v), y(v) {}
			uint2(uint32_t x, uint32_t y) : x(x), y(y) {}

			uint2 operator+(const uint2& rhs) const { return uint2(x + rhs.x, y + rhs.y); }
			uint2 operator-(const uint2& rhs) const { return uint2(x - rhs.x, y - rhs.y); }
			uint2 operator*(const uint2& rhs) const { return uint2(x * rhs.x, y * rhs.y); }
			uint2 operator*(uint32_t s) const { return uint2(x * s, y * s); }

			bool operator==(const uint2& rhs) const { return x == rhs.x && y == rhs.y; }
			bool operator!=(const uint2& rhs) const { return !(*this == rhs); }

			uint32_t& operator[](int i) { return (&x)[i]; }
			const uint32_t& operator[](int i) const { return (&x)[i]; }

			// 从 float2 转换
			uint2(const float2& v) : x(static_cast<uint32_t>(v.x)), y(static_cast<uint32_t>(v.y)) {}
			// 从 int2 转换
			uint2(const int2& v) : x(static_cast<uint32_t>(v.x)), y(static_cast<uint32_t>(v.y)) {}

		};

		// ---------- 数学函数 ----------
		inline float dot(const float2& a, const float2& b) { return a.x * b.x + a.y * b.y; }
		inline float lengthsq(const float2& v) { return dot(v, v); }
		inline float length(const float2& v) { return std::sqrt(lengthsq(v)); }
		inline float2 normalize(const float2& v) {
			float l = length(v);
			return l > 0 ? v * (1.0f / l) : float2(0);
		}

		// 向量 abs
		inline float2 abs(const float2& v) { return float2(std::abs(v.x), std::abs(v.y)); }
		inline int2 abs(const int2& v) { return int2(std::abs(v.x), std::abs(v.y)); }

		// 向量 min/max（自定义实现，不使用 std::min/max）
		inline float2 min(const float2& a, const float2& b) {
			return float2(a.x < b.x ? a.x : b.x, a.y < b.y ? a.y : b.y);
		}
		inline int2 min(const int2& a, const int2& b) {
			return int2(a.x < b.x ? a.x : b.x, a.y < b.y ? a.y : b.y);
		}

		inline float2 max(const float2& a, const float2& b) {
			return float2(a.x > b.x ? a.x : b.x, a.y > b.y ? a.y : b.y);
		}
		inline int2 max(const int2& a, const int2& b) {
			return int2(a.x > b.x ? a.x : b.x, a.y > b.y ? a.y : b.y);
		}

		inline float2 clamp(const float2& v, const float2& minVal, const float2& maxVal) {
			return min(max(v, minVal), maxVal);
		}
		inline int2 clamp(const int2& v, const int2& minVal, const int2& maxVal) {
			return min(max(v, minVal), maxVal);
		}

		inline float2 floor(const float2& v) { return float2(std::floor(v.x), std::floor(v.y)); }
		inline float2 ceil(const float2& v) { return float2(std::ceil(v.x), std::ceil(v.y)); }

		inline float distancesq(const float2& a, const float2& b) { return lengthsq(b - a); }

		// 标量 min/max（同样自定义实现，避免包含 <algorithm>）
		inline float min(float a, float b) { return a < b ? a : b; }
		inline int min(int a, int b) { return a < b ? a : b; }

		inline float max(float a, float b) { return a > b ? a : b; }
		inline int max(int a, int b) { return a > b ? a : b; }

		inline int clamp(int v, int minVal, int maxVal) { return min(max(v, minVal), maxVal); }
		inline float clamp(float v, float minVal, float maxVal) { return min(max(v, minVal), maxVal); }

		inline float floor(float x) { return std::floor(x); }
		inline float ceil(float x) { return std::ceil(x); }

		inline float lerp(float a, float b, float t) { return a + (b - a) * t; }
		inline float2 lerp(const float2& a, const float2& b, float t) { return a + (b - a) * t; }

		// 实现 float2 的构造函数（因为需要完整类型定义）
		inline float2::float2(const int2& v) : x(static_cast<float>(v.x)), y(static_cast<float>(v.y)) {}
		inline float2::float2(const uint2& v) : x(static_cast<float>(v.x)), y(static_cast<float>(v.y)) {}
		inline int2::int2(const uint2& v) : x(static_cast<int>(v.x)), y(static_cast<int>(v.y)) {}
	} // namespace Mathematics
} // namespace EntJoy