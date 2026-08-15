// common.h — GpuNativeProbe 共享头：类型 / 工具 / 宏 / CPU 内核 / 跨文件全局
// main.cpp（CPU+NativeDll+入口）与 opencl_probe.cpp / wgpu_probe.cpp 共用。
#pragma once
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>

/* MSVC 无 C99 restrict；transpiler 生成代码用 RESTRICT 宏，探针同样定义 */
#ifndef RESTRICT
#ifdef _MSC_VER
#define RESTRICT __restrict
#else
#define RESTRICT restrict
#endif
#endif

/* ================= 数据类型 ================= */
/* cuda_probe.cpp 定义 ENTJOY_NO_FLOAT2：改用 CUDA 内建 float2/int2（vector_types.h），避免重定义。
   hash_of / heavy_cpu / light_cpu 等仍可用 CUDA 类型（字段布局一致），mk2 由 cuda_probe.cpp 补定义。 */
#ifndef ENTJOY_NO_FLOAT2
typedef struct { float x, y; } float2;
typedef struct { int x, y; }   int2;
static int2 mk2(int x, int y) { int2 v; v.x = x; v.y = y; return v; }
#endif

static unsigned long long g_rng = 1234;
static double rnd(void) {
    g_rng = g_rng * 6364136223846793005ULL + 1442695040888963407ULL;
    return (double)((g_rng >> 33) & 0x7FFFFFFF) / 2147483647.0;
}

/* ================= 计时 ================= */
static double now_ms(void) {
    static LARGE_INTEGER f; static int once = 1;
    if (once) { QueryPerformanceFrequency(&f); once = 0; }
    LARGE_INTEGER t; QueryPerformanceCounter(&t);
    return (double)t.QuadPart * 1000.0 / (double)f.QuadPart;
}
static double median(double* a, int n) {
    double* s = (double*)malloc(n * sizeof(double));
    memcpy(s, a, n * sizeof(double));
    for (int i = 0; i < n - 1; i++) for (int j = i + 1; j < n; j++) if (s[j] < s[i]) { double t = s[i]; s[i] = s[j]; s[j] = t; }
    double m = s[n / 2]; free(s); return m;
}

/* ================= GridSearch 数据形态（对齐 09：N=100k / K=100k / dim=200 / LCG seed 1234） ================= */
#define N       100000
#define K       100000
#define DIM     200
#define CELL    (DIM * DIM)
#define GRID_WARMUP 5
#define GRID_FRAMES 20

static int hash_of(float2 p, float ox, float oy, float invX, float invY) {
    int cx = (int)floorf((p.x - ox) * invX);
    if (cx < 0) cx = 0; else if (cx > DIM - 1) cx = DIM - 1;
    int cy = (int)floorf((p.y - oy) * invY);
    if (cy < 0) cy = 0; else if (cy > DIM - 1) cy = DIM - 1;
    return cx + cy * DIM;
}

/* ================= Move 负载（镜像 08：N=1M / Heavy 16 iter / dt=1/60 / seed 1234） ================= */
#define MOVE_N       1000000
#define MOVE_WARMUP  5
#define MOVE_FRAMES  20
#define HEAVY_ITER   16
#define DT           1.0f / 60.0f

/* ---- CPU 内核：数学逐行镜像 NativeTranspiler_Generated 的 HeavyJobChunkCpp / MoveJobChunkCpp ----
   inline：header 内定义，被 main.cpp / opencl_probe.cpp 共同 include，避免多重定义 */
inline void heavy_cpu(float2* RESTRICT pos, const float2* RESTRICT vel, int n, float dt) {
    for (int index = 0; index < n; index++) {
        float px = pos[index].x, py = pos[index].y;
        float vx = vel[index].x, vy = vel[index].y;
        float accX = px * 0.001f + vx * 0.01f;
        float accY = py * 0.001f + vy * 0.01f;
        for (int iteration = 0; iteration < HEAVY_ITER; iteration++) {
            float phaseX = accX + iteration * 0.03125f;
            float phaseY = accY - iteration * 0.0625f;
            float wave = sinf(phaseX) + cosf(phaseY);
            float radius = sqrtf(accX * accX + accY * accY + 1.0f);
            accX = accX * 0.985f + wave * 0.015f + radius * 0.0002f + vx * 0.0001f;
            accY = accY * 0.982f - wave * 0.012f + radius * 0.0003f + vy * 0.0001f;
        }
        pos[index].x = px + vx * dt + accX * 0.001f;
        pos[index].y = py + vy * dt + accY * 0.001f;
    }
}
inline void light_cpu(float2* RESTRICT pos, const float2* RESTRICT vel, int n, float dt) {
    for (int i = 0; i < n; i++) {
        pos[i].x += vel[i].x * dt;
        pos[i].y += vel[i].y * dt;
    }
}

/* CPU 单线程 / NativeDll 多线程 的 Move 测量结果（OpenCL/CUDA probe 打印对照用；main.cpp 定义） */
extern double g_nativeHeavyCpu, g_nativeHeavyMt, g_nativeLightCpu, g_nativeLightMt;

/* 最终汇总表（各探针填充，main 最后打印直观对比表）：结构见 summary.h */
#include "summary.h"
