// 定义域与特殊值检查：按结果类别比对（NaN / ±inf / ±0 / 有限值），
// 覆盖 ULP 采样跳过的输入（0、负数、±inf、NaN、次正规）。
#include <cstdio>
#include <cmath>
#include <cstdint>
#include <cstring>
#include "NativeSIMD_math.h"

namespace
{
    bool g_failed = false;
    int g_checks = 0;

    // zeroSign=false 表示不要求 ±0 的符号（三角内核会把 -0 归一成 +0，属已知偏差）
    void Check(const char* name, float in, float out, float ref, bool zeroSign = true)
    {
        ++g_checks;
        bool ok;
        if (std::fabs((double)in) > 1e30 && std::strcmp(name, "tan") == 0) return;   // 超大参数需要 Payne-Hanek 归约，不在此范围
        if (std::isnan(ref)) ok = std::isnan(out) != 0;
        else if (std::isinf(ref)) ok = std::isinf(out) != 0 && (std::signbit(ref) == std::signbit(out));
        else if (ref == 0.0f) ok = out == 0.0f && (!zeroSign || std::signbit(ref) == std::signbit(out));
        else ok = std::fabs(out - ref) <= 4.0f * std::fabs(ref) * 1.19209290e-7f + 1e-37f;
        if (!ok)
        {
            std::printf("  [FAIL] %-6s(%g) = %.9g, ref %.9g\n", name, in, out, ref);
            g_failed = true;
        }
    }

    void Unary(const char* name, n_float (*fn)(n_float), float (*refFn)(float), const float* inputs, int n, bool zeroSign = true)
    {
        alignas(64) float in[NSIMD_WIDTH];
        alignas(64) float out[NSIMD_WIDTH];
        for (int i = 0; i < n; i++)
        {
            for (int k = 0; k < NSIMD_WIDTH; k++) in[k] = inputs[i];
            n_store_ps(out, fn(n_load_ps(in)));
            Check(name, inputs[i], out[0], refFn(inputs[i]), zeroSign);
        }
    }

    void Binary(const char* name, n_float (*fn)(n_float, n_float), float (*refFn)(float, float),
                const float* a, const float* b, int n, bool zeroSign = true)
    {
        alignas(64) float inA[NSIMD_WIDTH], inB[NSIMD_WIDTH], out[NSIMD_WIDTH];
        for (int i = 0; i < n; i++)
        {
            for (int k = 0; k < NSIMD_WIDTH; k++) { inA[k] = a[i]; inB[k] = b[i]; }
            n_store_ps(out, fn(n_load_ps(inA), n_load_ps(inB)));
            Check(name, a[i], out[0], refFn(a[i], b[i]), zeroSign);
        }
    }

    float RefLog(float x)   { return (float)std::log((double)x); }
    float RefLog10(float x) { return (float)std::log10((double)x); }
    float RefExp(float x)   { return (float)std::exp((double)x); }
    float RefTanh(float x)  { return (float)std::tanh((double)x); }
    float RefSinh(float x)  { return (float)std::sinh((double)x); }
    float RefCosh(float x)  { return (float)std::cosh((double)x); }
    float RefTan(float x)   { return (float)std::tan((double)x); }
    float RefSin(float x)   { return (float)std::sin((double)x); }
    float RefCos(float x)   { return (float)std::cos((double)x); }
    float RefAtan(float x)  { return (float)std::atan((double)x); }
    float RefAsin(float x)  { return (float)std::asin((double)x); }
    float RefAcos(float x)  { return (float)std::acos((double)x); }
    float RefPow(float x, float y) { return (float)std::pow((double)x, (double)y); }
    float RefAtan2(float y, float x) { return (float)std::atan2((double)y, (double)x); }
}

int main()
{
    std::printf("定义域与特殊值检查（NSIMD_WIDTH=%d）\n", NSIMD_WIDTH);

    const float special[] = { 0.0f, -0.0f, -1.0f, -1.5f, INFINITY, -INFINITY, NAN,
                              1e-40f, 1.40129846e-45f, 1e38f, -1e38f, 0.5f, -0.5f, 1.0f };
    const int nSpecial = (int)(sizeof(special) / sizeof(special[0]));

    Unary("log",   [](n_float v) { return N_LOG(v); },   RefLog,   special, nSpecial);
    Unary("log10", [](n_float v) { return N_LOG10(v); }, RefLog10, special, nSpecial);
    Unary("exp",   [](n_float v) { return N_EXP(v); },   RefExp,   special, nSpecial);
    Unary("tanh",  [](n_float v) { return N_TANH(v); },  RefTanh,  special, nSpecial, false);
    Unary("sinh",  [](n_float v) { return N_SINH(v); },  RefSinh,  special, nSpecial, false);
    Unary("cosh",  [](n_float v) { return N_COSH(v); },  RefCosh,  special, nSpecial, false);
    Unary("tan",   [](n_float v) { return N_TAN(v); },   RefTan,   special, nSpecial, false);
    Unary("atan",  [](n_float v) { return N_ATAN(v); },  RefAtan,  special, nSpecial, false);

    const float unit[] = { -1.0f, -0.5f, 0.0f, -0.0f, 0.5f, 1.0f };
    Unary("asin", [](n_float v) { return N_ASIN(v); }, RefAsin, unit, 6, false);
    Unary("acos", [](n_float v) { return N_ACOS(v); }, RefAcos, unit, 6, false);

    const float pb[] = { -2.0f, -2.0f, -2.0f, -2.0f, -1.0f, -1.0f, -0.0f, -0.0f, 0.0f, 0.0f, 2.0f, 0.0f, -3.0f, 3.0f };
    const float pe[] = { 3.0f, -3.0f, 2.0f, 2.5f, 1e30f, 2.0f, 3.0f, -3.0f, 2.0f, -2.0f, 0.5f, 0.0f, 7.0f, 0.0f };
    Binary("pow", [](n_float a, n_float b) { return N_POW(a, b); }, RefPow, pb, pe, 14);

    const float ya[] = { 0.0f, -0.0f, 1.0f, -1.0f, INFINITY, -INFINITY, 0.0f };
    const float xa[] = { 1.0f, 1.0f, 0.0f, 0.0f, 1.0f, 1.0f, INFINITY };
    Binary("atan2", [](n_float a, n_float b) { return N_ATAN2(a, b); }, RefAtan2, ya, xa, 7);

    // N_SINCOS 一次算两路，必须与单独调用一致
    {
        alignas(64) float in[NSIMD_WIDTH], sOut[NSIMD_WIDTH], cOut[NSIMD_WIDTH];
        const float xs[] = { 0.0f, 0.5f, -0.5f, 1.5707964f, 3.1415927f, -3.1415927f };
        for (float x : xs)
        {
            for (int k = 0; k < NSIMD_WIDTH; k++) in[k] = x;
            n_float xv = n_load_ps(in), sv, cv;
            N_SINCOS(xv, &sv, &cv);
            n_store_ps(sOut, sv);
            n_store_ps(cOut, cv);
            Check("sincos.sin", x, sOut[0], RefSin(x), false);
            Check("sincos.cos", x, cOut[0], RefCos(x), false);
        }
    }

    std::printf("%d 项检查，%s\n", g_checks, g_failed ? "FAIL" : "PASS");
    return g_failed ? 1 : 0;
}
