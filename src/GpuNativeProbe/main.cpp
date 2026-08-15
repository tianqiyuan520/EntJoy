// GpuNativeProbe — 独立 GPU 原生探针（脱离 ILGPU，直接对比驱动栈）
// 后端：① OpenCL（native OpenCL C，实现见 opencl_probe.cpp）
//       ② wgpu-native / WebGPU（实现见 wgpu_probe.cpp）
//       ③ CUDA runtime API（手写 CUDA C 内核 + cudaHostAlloc 页锁定，实现见 cuda_probe.cpp / cuda_kernels.cu）
//       ④ NativeDll JobSystem（多线程 CPU，LoadLibrary bin/NativeDll.dll，本文件）
// 负载（与 08/09 相同测试案例，见 common.h）：
//   · HeavyMove  @1M 实体 16 次迭代 sin/cos 累加（镜像 transpiler 生成的 HeavyJobChunkCpp 数学）
//   · LightMove  @1M 实体 单行 pos += vel*dt（镜像 MoveJobChunkCpp）
//   · GridSearch closest @100k 点 / 100k 查询 / 200×200 cell（与 09 同形态）
// 每负载后端：CPU 单线程(C++ 镜像 transpiler) / NativeDll JobSystem 多线程 / GPU（常驻+往返[staged/页锁定]）
// 编译：msbuild GpuNativeProbe.vcxproj -p:Configuration=Release -p:Platform=x64
// 用法：GpuNativeProbe.exe [ocl|wgpu|cuda|cpu|all]   （默认 all）

#include "common.h"
#include "opencl_probe.h"
#include "cuda_probe.h"

/* ================= NativeDll JobSystem 多线程（LoadLibrary 接入，C ABI 见 Exports.h） ================= */
typedef void (*JobSystem_Init_t)(int);
typedef int  (*JobSystem_GetWorkerCount_t)();
typedef void (*JobSystem_Shutdown_t)();
typedef void* (*JobSystem_ScheduleParallelForBatch_t)(void(*func)(void*, int, int), void* context,
    void(*cleanup)(void*), int length, int batchSize, void* dependency);
typedef void (*JobSystem_CompleteAndRelease_t)(void*);

struct NativeDllApi {
    HMODULE module;
    JobSystem_Init_t init;
    JobSystem_GetWorkerCount_t workerCount;
    JobSystem_Shutdown_t shutdown;
    JobSystem_ScheduleParallelForBatch_t scheduleBatch;
    JobSystem_CompleteAndRelease_t completeAndRelease;
};
static struct NativeDllApi g_native;

/* 探针 exe 可能在 x64\Release\ 下跑，NativeDll.dll 在仓库 bin\：逐个候选路径尝试 */
static const char* kNativeDllCandidates[] = {
    "NativeDll.dll",
    "..\\..\\..\\..\\bin\\NativeDll.dll",       // x64\Release -> src -> GpuNativeProbe...
    "..\\..\\..\\..\\..\\bin\\NativeDll.dll",   // 深度备选
    "E:\\GODOT\\Project\\EntJoy\\bin\\NativeDll.dll",
};

static int LoadNativeDll(void) {
    HMODULE h = NULL;
    for (int i = 0; i < (int)(sizeof(kNativeDllCandidates) / sizeof(kNativeDllCandidates[0])) && !h; i++) {
        h = LoadLibraryA(kNativeDllCandidates[i]);
    }
    if (!h) { printf("  LoadLibrary(NativeDll.dll) 失败（候选路径全试过）\n"); return 1; }
    g_native.module = h;
    g_native.init = (JobSystem_Init_t)GetProcAddress(h, "JobSystem_Initialize");
    g_native.workerCount = (JobSystem_GetWorkerCount_t)GetProcAddress(h, "JobSystem_GetWorkerCount");
    g_native.shutdown = (JobSystem_Shutdown_t)GetProcAddress(h, "JobSystem_Shutdown");
    g_native.scheduleBatch = (JobSystem_ScheduleParallelForBatch_t)GetProcAddress(h, "JobSystem_ScheduleParallelForBatch");
    g_native.completeAndRelease = (JobSystem_CompleteAndRelease_t)GetProcAddress(h, "JobSystem_CompleteAndRelease");
    if (!g_native.init || !g_native.scheduleBatch || !g_native.completeAndRelease) {
        printf("  NativeDll.dll 缺关键导出\n"); return 1;
    }
    g_native.init(0);   // 0 = 自动选 worker 数
    printf("  NativeDll worker 数: %d\n", g_native.workerCount ? g_native.workerCount() : 0);
    return 0;
}

struct MoveCtx { float2* pos; const float2* vel; int n; float dt; };

/* Batch 回调：与 heavy_cpu/light_cpu 完全相同的数学体，按 range 切分给 worker 并行 */
static void heavy_batch_cb(void* c, int start, int count) {
    MoveCtx* ctx = (MoveCtx*)c;
    for (int index = start; index < start + count; index++) {
        float px = ctx->pos[index].x, py = ctx->pos[index].y;
        float vx = ctx->vel[index].x, vy = ctx->vel[index].y;
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
        ctx->pos[index].x = px + vx * ctx->dt + accX * 0.001f;
        ctx->pos[index].y = py + vy * ctx->dt + accY * 0.001f;
    }
}
static void light_batch_cb(void* c, int start, int count) {
    MoveCtx* ctx = (MoveCtx*)c;
    for (int i = start; i < start + count; i++) {
        ctx->pos[i].x += ctx->vel[i].x * ctx->dt;
        ctx->pos[i].y += ctx->vel[i].y * ctx->dt;
    }
}
static void noop_cleanup(void*) {}

/* CPU 单线程 / NativeDll 多线程 的 Move 测量结果（opencl/wgpu probe 打印对照用） */
double g_nativeHeavyCpu, g_nativeHeavyMt, g_nativeLightCpu, g_nativeLightMt;

/* 最终汇总表（各探针填充；main 末尾打印直观对比） */
ProbeSummary g_sum;

static void InitSummary(void) {
    for (int i = 0; i < GPU_BACKENDS; i++) {
        g_sum.heavyResident[i] = g_sum.heavyRoundtrip[i] = g_sum.heavyPinned[i] = -1;
        g_sum.lightResident[i] = g_sum.lightRoundtrip[i] = g_sum.lightPinned[i] = -1;
        g_sum.gridResident[i] = g_sum.gridRoundtrip[i] = g_sum.gridPinned[i] = -1;
        g_sum.gridMismatch[i] = -1;
    }
    g_sum.heavyMt = g_sum.lightMt = -1;
}

/* 把 double 值格式化成 %8.3f 或 "n/a"（<0）到固定 buffer（同一 printf 内多次调用安全） */
static void fmtv(char* out, double v) {
    if (v < 0) snprintf(out, 16, "   n/a");
    else       snprintf(out, 16, "%8.3f", v);
}

static void MeasureMoveCpu(void) {
    printf("\n===== Move @1M CPU（数学镜像 NativeTranspiler 生成代码） =====\n");
    float2* pos = (float2*)malloc(MOVE_N * sizeof(float2));
    float2* vel = (float2*)malloc(MOVE_N * sizeof(float2));
    g_rng = 1234;
    for (int i = 0; i < MOVE_N; i++) {
        pos[i].x = (float)(rnd() * 200 - 100); pos[i].y = (float)(rnd() * 200 - 100);
        vel[i].x = (float)(rnd() * 200 - 100); vel[i].y = (float)(rnd() * 200 - 100);
    }

    /* CPU 单线程（Heavy） */
    for (int i = 0; i < MOVE_WARMUP; i++) heavy_cpu(pos, vel, MOVE_N, DT);
    double wh[MOVE_FRAMES];
    for (int f = 0; f < MOVE_FRAMES; f++) {
        double t0 = now_ms();
        heavy_cpu(pos, vel, MOVE_N, DT);
        wh[f] = now_ms() - t0;
    }
    g_nativeHeavyCpu = median(wh, MOVE_FRAMES);

    /* CPU 单线程（Light） */
    for (int i = 0; i < MOVE_WARMUP; i++) light_cpu(pos, vel, MOVE_N, DT);
    double wl[MOVE_FRAMES];
    for (int f = 0; f < MOVE_FRAMES; f++) {
        double t0 = now_ms();
        light_cpu(pos, vel, MOVE_N, DT);
        wl[f] = now_ms() - t0;
    }
    g_nativeLightCpu = median(wl, MOVE_FRAMES);

    printf("  CPU 单线程 HeavyMove : %8.3f ms\n", g_nativeHeavyCpu);
    printf("  CPU 单线程 LightMove : %8.3f ms\n", g_nativeLightCpu);

    /* NativeDll JobSystem 多线程 */
    if (LoadNativeDll() == 0) {
        MoveCtx ctx = { pos, vel, MOVE_N, DT };
        for (int i = 0; i < MOVE_WARMUP; i++) {
            void* h = g_native.scheduleBatch(heavy_batch_cb, &ctx, noop_cleanup, MOVE_N, 4096, NULL);
            g_native.completeAndRelease(h);
        }
        double wmh[MOVE_FRAMES];
        for (int f = 0; f < MOVE_FRAMES; f++) {
            double t0 = now_ms();
            void* h = g_native.scheduleBatch(heavy_batch_cb, &ctx, noop_cleanup, MOVE_N, 4096, NULL);
            g_native.completeAndRelease(h);
            wmh[f] = now_ms() - t0;
        }
        g_nativeHeavyMt = median(wmh, MOVE_FRAMES);
        g_sum.heavyMt = g_nativeHeavyMt;

        for (int i = 0; i < MOVE_WARMUP; i++) {
            void* h = g_native.scheduleBatch(light_batch_cb, &ctx, noop_cleanup, MOVE_N, 4096, NULL);
            g_native.completeAndRelease(h);
        }
        double wml[MOVE_FRAMES];
        for (int f = 0; f < MOVE_FRAMES; f++) {
            double t0 = now_ms();
            void* h = g_native.scheduleBatch(light_batch_cb, &ctx, noop_cleanup, MOVE_N, 4096, NULL);
            g_native.completeAndRelease(h);
            wml[f] = now_ms() - t0;
        }
        g_nativeLightMt = median(wml, MOVE_FRAMES);
        g_sum.lightMt = g_nativeLightMt;

        printf("  NativeDll 多线程 HeavyMove : %8.3f ms   (单线程 x%.2f)\n", g_nativeHeavyMt, g_nativeHeavyCpu / g_nativeHeavyMt);
        printf("  NativeDll 多线程 LightMove : %8.3f ms   (单线程 x%.2f)\n", g_nativeLightMt, g_nativeLightCpu / g_nativeLightMt);
        g_native.shutdown();
        FreeLibrary(g_native.module);
    }

    free(pos); free(vel);
}

/* ================= wgpu-native / WebGPU 后端（实现见 wgpu_probe.cpp） ================= */
void RunWgpuProbe(void);

/* ================= 脏同步增量探针（实现见 dirty_probe.cpp，独立入口，不并入 all） ================= */
void RunDirtyProbe(void);

/* ================= 最终直观对比表（main 末尾打印） ================= */
static void PrintSummary(void) {
    const char* names[3] = { "native OpenCL", "wgpu-native", "native CUDA" };
    char r[3][16], rt[3][16], pin[3][16];
    char rc[16], rtc[16], pc[16];
    printf("\n================================================================================\n");
    printf("  最终直观对比（p50 ms，越小越快；n/a = 该后端无此形态）\n");
    printf("================================================================================\n");

    printf("\n[GridSearch closest @100k]       常驻kernel      往返      往返·页锁定\n");
    for (int b = 0; b < 3; b++) {
        fmtv(r[b], g_sum.gridResident[b]);
        fmtv(rt[b], g_sum.gridRoundtrip[b]);
        fmtv(pin[b], g_sum.gridPinned[b]);
        printf("  %-14s   %s   %s   %s\n", names[b], r[b], rt[b], pin[b]);
    }
    if (g_sum.gridMismatch[2] >= 0)
        printf("    parity（native CUDA vs CPU 参考逐元素）: %d/%d mismatch\n", g_sum.gridMismatch[2], K);
    fmtv(rc, 0.151); fmtv(rtc, 1.449); fmtv(pc, 0.311);
    printf("  %-14s   %s   %s   %s   (ILGPU 对照, 历史值)\n", "ILGPU CUDA", rc, rtc, pc);

    printf("\n[HeavyMove @1M, 16 iter]        常驻kernel      往返      往返·页锁定\n");
    if (g_nativeHeavyCpu > 0) {
        char cc[16];
        fmtv(cc, g_nativeHeavyCpu);
        printf("  %-14s   %s   %s   %s\n", "CPU 单线程", "   n/a", cc, "   n/a");
    }
    if (g_sum.heavyMt >= 0) {
        char mh[16];
        fmtv(mh, g_sum.heavyMt);
        printf("  %-14s   %s   %s   %s   (x%.1f)\n", "NativeDll 多线程", "   n/a", mh, "   n/a",
            g_nativeHeavyCpu > 0 ? g_nativeHeavyCpu / g_sum.heavyMt : 0);
    }
    for (int b = 0; b < 3; b++) {
        fmtv(r[b], g_sum.heavyResident[b]);
        fmtv(rt[b], g_sum.heavyRoundtrip[b]);
        fmtv(pin[b], g_sum.heavyPinned[b]);
        printf("  %-14s   %s   %s   %s\n", names[b], r[b], rt[b], pin[b]);
    }
    fmtv(rc, 0.156); fmtv(rtc, 1.449); fmtv(pc, -1);
    printf("  %-14s   %s   %s   %s   (ILGPU 对照, 历史值)\n", "ILGPU CUDA", rc, rtc, pc);

    printf("\n[LightMove @1M]                 常驻kernel      往返      往返·页锁定\n");
    if (g_nativeLightCpu > 0) {
        char cc[16];
        fmtv(cc, g_nativeLightCpu);
        printf("  %-14s   %s   %s   %s\n", "CPU 单线程", "   n/a", cc, "   n/a");
    }
    if (g_sum.lightMt >= 0) {
        char ml[16];
        fmtv(ml, g_sum.lightMt);
        printf("  %-14s   %s   %s   %s   (x%.1f)\n", "NativeDll 多线程", "   n/a", ml, "   n/a",
            g_nativeLightCpu > 0 ? g_nativeLightCpu / g_sum.lightMt : 0);
    }
    for (int b = 0; b < 3; b++) {
        fmtv(r[b], g_sum.lightResident[b]);
        fmtv(rt[b], g_sum.lightRoundtrip[b]);
        fmtv(pin[b], g_sum.lightPinned[b]);
        printf("  %-14s   %s   %s   %s\n", names[b], r[b], rt[b], pin[b]);
    }
    fmtv(rc, 0.067); fmtv(rtc, 1.350); fmtv(pc, -1);
    printf("  %-14s   %s   %s   %s   (ILGPU 对照, 历史值)\n", "ILGPU CUDA", rc, rtc, pc);

    printf("\n要点：三 GPU 后端常驻 kernel 全在 0.02-0.26ms（亚毫秒）——kernel 不是瓶颈；\n");
    printf("      往返 1.8-3.0ms 被传输税主导（常驻差 ~100x）。页锁定(cudaHostAlloc)只省传输不省数据量；\n");
    printf("      LightMove 全量往返所有后端仍输 CPU 单线程 → GPU 赢面在「数据常驻 + 输出小」。\n");
    printf("================================================================================\n");
}

/* ================= main ================= */
int main(int argc, char** argv) {
    SetConsoleOutputCP(CP_UTF8);   // console 直接输出 UTF-8，避免 GBK 乱码
    const char* which = argc > 1 ? argv[1] : "all";
    printf("GpuNativeProbe — GridSearch closest + Move@1M（独立驱动栈探针）\n");
    InitSummary();

    /* CPU + NativeDll 先行（OpenCL probe 打印对照需要其结果） */
    if (strcmp(which, "ocl") == 0 || strcmp(which, "cpu") == 0 || strcmp(which, "all") == 0) {
        MeasureMoveCpu();
    }

    if (strcmp(which, "ocl") == 0 || strcmp(which, "all") == 0) {
        printf("\n===== OpenCL（native OpenCL C，LoadLibrary 动态加载；免 OpenCL.lib） =====\n");
        RunOpenCLProbe();
    }

    if (strcmp(which, "wgpu") == 0 || strcmp(which, "all") == 0) {
        RunWgpuProbe();
    }

    if (strcmp(which, "cuda") == 0 || strcmp(which, "all") == 0) {
        printf("\n===== CUDA（runtime API；手写 CUDA C 内核 + cudaHostAlloc 页锁定） =====\n");
        RunCudaProbe();
    }

    if (strcmp(which, "dirty") == 0) {
        printf("\n===== 脏同步增量探针（HeavyMove 稀疏写，独立文件 dirty_probe.cpp） =====\n");
        RunDirtyProbe();
    }

    if (strcmp(which, "diff") == 0) {
        printf("\n===== 影子 diff 成本探针（AVX2 vs memcmp + resSync/resCross/fullMode/resident，独立文件 diff_probe.cpp） =====\n");
        RunDiffProbe();
    }

    if (strcmp(which, "density") == 0) {
        printf("\n===== 瓶颈分类器阈值验证探针（computeDensity 连续区间，独立文件 density_probe.cpp） =====\n");
        RunDensityProbe();
    }
    PrintSummary();
    return 0;
}
