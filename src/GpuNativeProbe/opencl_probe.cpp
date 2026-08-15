// opencl_probe.cpp — native OpenCL C 探针（脱离 ILGPU，LoadLibrary 动态加载 OpenCL.dll，免 opencl.h / 免 OpenCL.lib）
// 负载与 main.cpp 相同（GridSearch closest@100k + HeavyMove/LightMove@1M），每负载测两种传输：
//   · staged：clEnqueueWriteBuffer/ReadBuffer（pageable host → 驱动内部 pinned staging → GPU，两跳）
//   · 页锁定：CL_MEM_ALLOC_HOST_PTR buffer + clEnqueueMapBuffer/Unmap 直写（host 侧 pinned，GPU 直接 DMA，等价 cudaHostAlloc）

#include "common.h"
#include "opencl_probe.h"
#include <malloc.h>   /* _aligned_malloc（VirtualLock 页锁定 host 内存） */

/* ================= 最小 OpenCL 声明 ================= */
typedef int cl_int;
typedef unsigned cl_uint;
typedef cl_uint cl_bool;
typedef cl_uint cl_device_type;
typedef cl_uint cl_mem_flags;
typedef cl_uint cl_device_info;
typedef cl_uint cl_command_queue_properties;
typedef cl_uint cl_map_flags;
typedef void* cl_platform_id;
typedef void* cl_device_id;
typedef void* cl_context;
typedef void* cl_command_queue;
typedef void* cl_mem;
typedef void* cl_program;
typedef void* cl_kernel;
/* cl_context_properties 必须是 64 位（x64 下平台句柄是指针，32 位 cl_uint 会截断 → CL_INVALID_VALUE） */
typedef long long cl_context_properties;

#define CL_DEVICE_TYPE_GPU   (1u << 2)
#define CL_DEVICE_NAME       0x102B
#define CL_CONTEXT_PLATFORM  0x1084
#define CL_MEM_READ_WRITE    (1u << 0)
#define CL_MEM_WRITE_ONLY    (1u << 1)
#define CL_MEM_READ_ONLY     (1u << 2)
#define CL_MEM_ALLOC_HOST_PTR (1u << 3)   /* 分配 host-accessible pinned 内存（页锁定等价物） */
#define CL_MEM_COPY_HOST_PTR (1u << 5)
#define CL_MAP_READ          (1u << 0)
#define CL_MAP_WRITE         (1u << 1)
#define CL_SUCCESS           0
#define CL_PROGRAM_BUILD_LOG 0x1183

#define FN(name) typedef cl_int (*name##_t)
FN(clGetPlatformIDs)(cl_uint, cl_platform_id*, cl_uint*);
FN(clGetDeviceIDs)(cl_platform_id, cl_device_type, cl_uint, cl_device_id*, cl_uint*);
FN(clGetDeviceInfo)(cl_device_id, cl_device_info, size_t, void*, size_t*);
FN(clEnqueueWriteBuffer)(cl_command_queue, cl_mem, cl_bool, size_t, size_t, const void*, cl_uint, void*, void*);
FN(clEnqueueReadBuffer)(cl_command_queue, cl_mem, cl_bool, size_t, size_t, void*, cl_uint, void*, void*);
FN(clBuildProgram)(cl_program, cl_uint, const cl_device_id*, const char*, void*, void*);
FN(clSetKernelArg)(cl_kernel, cl_uint, size_t, const void*);
FN(clEnqueueNDRangeKernel)(cl_command_queue, cl_kernel, cl_uint, const size_t*, const size_t*, const size_t*, cl_uint, void*, void*);
FN(clFinish)(cl_command_queue);
FN(clReleaseMemObject)(cl_mem);
FN(clReleaseKernel)(cl_kernel);
FN(clReleaseProgram)(cl_program);
FN(clReleaseCommandQueue)(cl_command_queue);
FN(clReleaseContext)(cl_context);
FN(clEnqueueUnmapMemObject)(cl_command_queue, cl_mem, void*, cl_uint, void*, void*);
#undef FN
/* 返回指针（非 cl_int）的工厂 */
typedef cl_context (*clCreateContext_t)(const cl_context_properties*, cl_uint, const cl_device_id*, void*, void*, cl_int*);
typedef cl_command_queue (*clCreateCommandQueue_t)(cl_context, cl_device_id, cl_command_queue_properties, cl_int*);
typedef cl_mem (*clCreateBuffer_t)(cl_context, cl_mem_flags, size_t, void*, cl_int*);
typedef cl_program (*clCreateProgramWithSource_t)(cl_context, cl_uint, const char**, const size_t*, cl_int*);
typedef cl_kernel (*clCreateKernel_t)(cl_program, const char*, cl_int*);
typedef void* (*clEnqueueMapBuffer_t)(cl_command_queue, cl_mem, cl_bool, cl_map_flags, size_t, size_t, cl_uint, void*, void*, cl_int*);

/* 每函数一个全局函数指针，装入 OpenCLBackend 结构 */
struct OpenCLApi {
    clGetPlatformIDs_t getPlatformIDs; clGetDeviceIDs_t getDeviceIDs; clGetDeviceInfo_t getDeviceInfo;
    clCreateContext_t createContext; clCreateCommandQueue_t createCommandQueue; clCreateBuffer_t createBuffer;
    clEnqueueWriteBuffer_t enqueueWriteBuffer; clEnqueueReadBuffer_t enqueueReadBuffer;
    clCreateProgramWithSource_t createProgramWithSource; clBuildProgram_t buildProgram; clCreateKernel_t createKernel;
    clSetKernelArg_t setKernelArg; clEnqueueNDRangeKernel_t enqueueNDRangeKernel; clFinish_t finish;
    clEnqueueMapBuffer_t enqueueMapBuffer; clEnqueueUnmapMemObject_t enqueueUnmapMemObject;
    clReleaseMemObject_t releaseMemObject; clReleaseKernel_t releaseKernel; clReleaseProgram_t releaseProgram;
    clReleaseCommandQueue_t releaseCommandQueue; clReleaseContext_t releaseContext;
    HMODULE module;
};

static int LoadOpenCL(OpenCLApi* api) {
    api->module = LoadLibraryA("OpenCL.dll");
    if (!api->module) { printf("  LoadLibrary(OpenCL.dll) 失败\n"); return 1; }
    api->getPlatformIDs = (clGetPlatformIDs_t)GetProcAddress(api->module, "clGetPlatformIDs");
    api->getDeviceIDs = (clGetDeviceIDs_t)GetProcAddress(api->module, "clGetDeviceIDs");
    api->getDeviceInfo = (clGetDeviceInfo_t)GetProcAddress(api->module, "clGetDeviceInfo");
    api->createContext = (clCreateContext_t)GetProcAddress(api->module, "clCreateContext");
    api->createCommandQueue = (clCreateCommandQueue_t)GetProcAddress(api->module, "clCreateCommandQueue");
    api->createBuffer = (clCreateBuffer_t)GetProcAddress(api->module, "clCreateBuffer");
    api->enqueueWriteBuffer = (clEnqueueWriteBuffer_t)GetProcAddress(api->module, "clEnqueueWriteBuffer");
    api->enqueueReadBuffer = (clEnqueueReadBuffer_t)GetProcAddress(api->module, "clEnqueueReadBuffer");
    api->createProgramWithSource = (clCreateProgramWithSource_t)GetProcAddress(api->module, "clCreateProgramWithSource");
    api->buildProgram = (clBuildProgram_t)GetProcAddress(api->module, "clBuildProgram");
    api->createKernel = (clCreateKernel_t)GetProcAddress(api->module, "clCreateKernel");
    api->setKernelArg = (clSetKernelArg_t)GetProcAddress(api->module, "clSetKernelArg");
    api->enqueueNDRangeKernel = (clEnqueueNDRangeKernel_t)GetProcAddress(api->module, "clEnqueueNDRangeKernel");
    api->finish = (clFinish_t)GetProcAddress(api->module, "clFinish");
    api->enqueueMapBuffer = (clEnqueueMapBuffer_t)GetProcAddress(api->module, "clEnqueueMapBuffer");
    api->enqueueUnmapMemObject = (clEnqueueUnmapMemObject_t)GetProcAddress(api->module, "clEnqueueUnmapMemObject");
    api->releaseMemObject = (clReleaseMemObject_t)GetProcAddress(api->module, "clReleaseMemObject");
    api->releaseKernel = (clReleaseKernel_t)GetProcAddress(api->module, "clReleaseKernel");
    api->releaseProgram = (clReleaseProgram_t)GetProcAddress(api->module, "clReleaseProgram");
    api->releaseCommandQueue = (clReleaseCommandQueue_t)GetProcAddress(api->module, "clReleaseCommandQueue");
    api->releaseContext = (clReleaseContext_t)GetProcAddress(api->module, "clReleaseContext");
    if (!api->getPlatformIDs || !api->getDeviceIDs || !api->createContext || !api->createCommandQueue ||
        !api->createBuffer || !api->enqueueWriteBuffer || !api->enqueueReadBuffer || !api->createProgramWithSource ||
        !api->buildProgram || !api->createKernel || !api->setKernelArg || !api->enqueueNDRangeKernel || !api->finish ||
        !api->enqueueMapBuffer || !api->enqueueUnmapMemObject) {
        printf("  OpenCL.dll 缺函数（驱动版本过旧？）\n"); return 1;
    }
    return 0;
}

/* ================= closest kernel 源码（逐句镜像 09 ClosestPointKernel） ================= */
static const char* KERNEL_SRC =
"__kernel void closest(\n"
"    __global const float2* query,\n"
"    __global const int2* hashIndex,\n"
"    __global const int2* cellSE,\n"
"    __global const float2* sorted,\n"
"    __global int* results,\n"
"    float OriginX, float OriginY, float GridResInv,\n"
"    int GridDimX, int GridDimY, int IgnoreSelf,\n"
"    float SquaredEpsilonSelf, int SortedLength)\n"
"{\n"
"    int i = get_global_id(0);\n"
"    results[i] = -1;\n"
"    float2 q = query[i];\n"
"    int cx = (int)floor((q.x - OriginX) * GridResInv);\n"
"    cx = cx < 0 ? 0 : (cx > GridDimX - 1 ? GridDimX - 1 : cx);\n"
"    int cy = (int)floor((q.y - OriginY) * GridResInv);\n"
"    cy = cy < 0 ? 0 : (cy > GridDimY - 1 ? GridDimY - 1 : cy);\n"
"    float bestDistSq = 3.402823466e38f;\n"
"    int bestIdx = -1;\n"
"    for (int dx = -1; dx <= 1; dx++) {\n"
"        int nx = cx + dx;\n"
"        if ((unsigned)nx >= (unsigned)GridDimX) continue;\n"
"        for (int dy = -1; dy <= 1; dy++) {\n"
"            int ny = cy + dy;\n"
"            if ((unsigned)ny >= (unsigned)GridDimY) continue;\n"
"            int cellHash = ny * GridDimX + nx;\n"
"            int2 range = cellSE[cellHash];\n"
"            int start = range.x, end = range.y;\n"
"            if (start < 0) continue;\n"
"            for (int j = start; j < end; j++) {\n"
"                float2 pos = sorted[j];\n"
"                float dx2 = q.x - pos.x, dy2 = q.y - pos.y;\n"
"                float distSq = dx2 * dx2 + dy2 * dy2;\n"
"                if (IgnoreSelf != 0 && distSq < SquaredEpsilonSelf) continue;\n"
"                if (distSq < bestDistSq) { bestDistSq = distSq; bestIdx = j; }\n"
"            }\n"
"        }\n"
"    }\n"
"    if (bestIdx != -1) { results[i] = hashIndex[bestIdx].y; }\n"
"    else {\n"
"        for (int j = 0; j < SortedLength; j++) {\n"
"            float2 pos = sorted[j];\n"
"            float dx2 = q.x - pos.x, dy2 = q.y - pos.y;\n"
"            float distSq = dx2 * dx2 + dy2 * dy2;\n"
"            if (IgnoreSelf != 0 && distSq < SquaredEpsilonSelf) continue;\n"
"            if (distSq < bestDistSq) { bestDistSq = distSq; bestIdx = j; }\n"
"        }\n"
"        if (bestIdx != -1) { results[i] = hashIndex[bestIdx].y; }\n"
"    }\n"
"}\n";

/* Move kernel 源码：HeavyMove/LightMove 逐行镜像 CPU 内核（sin/cos/sqrt 用 OpenCL C 内置） */
static const char* HEAVY_KERNEL_SRC =
"__kernel void heavy(\n"
"    __global float2* pos, __global const float2* vel, float dt)\n"
"{\n"
"    int i = get_global_id(0);\n"
"    float px = pos[i].x, py = pos[i].y;\n"
"    float vx = vel[i].x, vy = vel[i].y;\n"
"    float accX = px * 0.001f + vx * 0.01f;\n"
"    float accY = py * 0.001f + vy * 0.01f;\n"
"    for (int iteration = 0; iteration < 16; iteration++) {\n"
"        float phaseX = accX + iteration * 0.03125f;\n"
"        float phaseY = accY - iteration * 0.0625f;\n"
"        float wave = sin(phaseX) + cos(phaseY);\n"
"        float radius = sqrt(accX * accX + accY * accY + 1.0f);\n"
"        accX = accX * 0.985f + wave * 0.015f + radius * 0.0002f + vx * 0.0001f;\n"
"        accY = accY * 0.982f - wave * 0.012f + radius * 0.0003f + vy * 0.0001f;\n"
"    }\n"
"    pos[i].x = px + vx * dt + accX * 0.001f;\n"
"    pos[i].y = py + vy * dt + accY * 0.001f;\n"
"}\n";
static const char* LIGHT_KERNEL_SRC =
"__kernel void light(\n"
"    __global float2* pos, __global const float2* vel, float dt)\n"
"{\n"
"    int i = get_global_id(0);\n"
"    pos[i].x += vel[i].x * dt;\n"
"    pos[i].y += vel[i].y * dt;\n"
"}\n";

/* ================= 页锁定 host 内存（等价 cudaHostAlloc） =================
   注意：NVIDIA OpenCL 驱动上 CL_MEM_ALLOC_HOST_PTR 分配系统性失败（-37），故不用。
   改用 Windows VirtualLock 钉住 host 页面（不换出）→ clEnqueueWriteBuffer/ReadBuffer 从 pinned 内存直接 DMA（单跳）。
   VirtualLock 需进程 SeLockMemoryPrivilege（默认无），失败返回 NULL → 调用处跳过 pinned 对照并说明。 */
static void* lock_pages(size_t bytes) {
    void* p = _aligned_malloc(bytes, 4096);
    if (!p) return NULL;
    if (!VirtualLock(p, bytes)) { _aligned_free(p); return NULL; }
    return p;
}

double RunOpenCLProbe(void) {
    OpenCLApi api;
    memset(&api, 0, sizeof(api));
    if (LoadOpenCL(&api) != 0) return -1;

    cl_int err = CL_SUCCESS;
    cl_platform_id plat; cl_uint nplat;
    err = api.getPlatformIDs(1, &plat, &nplat);
    if (err != CL_SUCCESS) { printf("  clGetPlatformIDs err=%d\n", err); FreeLibrary(api.module); return -1; }
    cl_device_id dev; cl_uint ndev;
    err = api.getDeviceIDs(plat, CL_DEVICE_TYPE_GPU, 1, &dev, &ndev);
    if (err != CL_SUCCESS) { printf("  clGetDeviceIDs(GPU) err=%d\n", err); FreeLibrary(api.module); return -1; }
    char devname[128] = { 0 }; size_t sz = 0;
    if (api.getDeviceInfo) api.getDeviceInfo(dev, CL_DEVICE_NAME, sizeof(devname) - 1, devname, &sz);
    printf("  device: %s\n", devname);

    cl_context_properties ctxprop[] = { CL_CONTEXT_PLATFORM, (cl_context_properties)plat, 0 };
    cl_context ctx = api.createContext(ctxprop, 1, &dev, NULL, NULL, &err);
    if (err != CL_SUCCESS) { printf("  clCreateContext err=%d\n", err); FreeLibrary(api.module); return -1; }
    cl_command_queue q = api.createCommandQueue(ctx, dev, 0, &err);
    if (err != CL_SUCCESS) { printf("  clCreateCommandQueue err=%d\n", err); FreeLibrary(api.module); return -1; }

    /* ============ GridSearch closest @100k ============ */
    float2* pos = (float2*)malloc(N * sizeof(float2));
    float2* qry = (float2*)malloc(K * sizeof(float2));
    float2* sorted = (float2*)malloc(N * sizeof(float2));
    int2* hashIdx = (int2*)malloc(N * sizeof(int2));
    int2* cellSE = (int2*)malloc(CELL * sizeof(int2));
    int* results = (int*)malloc(K * sizeof(int));
    for (int i = 0; i < N; i++) { pos[i].x = (float)(rnd() * 200 - 100); pos[i].y = (float)(rnd() * 200 - 100); }
    for (int i = 0; i < K; i++) { qry[i].x = (float)(rnd() * 200 - 100); qry[i].y = (float)(rnd() * 200 - 100); }

    float minx = pos[0].x, maxx = pos[0].x, miny = pos[0].y, maxy = pos[0].y;
    for (int i = 1; i < N; i++) {
        if (pos[i].x < minx) minx = pos[i].x; if (pos[i].x > maxx) maxx = pos[i].x;
        if (pos[i].y < miny) miny = pos[i].y; if (pos[i].y > maxy) maxy = pos[i].y;
    }
    float ox = minx, oy = miny;
    float invX = DIM / (maxx - minx > 0 ? maxx - minx : 1e-6f);
    float invY = DIM / (maxy - miny > 0 ? maxy - miny : 1e-6f);
    int* counts = (int*)calloc(CELL, sizeof(int));
    for (int i = 0; i < N; i++) counts[hash_of(pos[i], ox, oy, invX, invY)]++;
    int sum = 0;
    for (int c = 0; c < CELL; c++) { int cnt = counts[c]; counts[c] = sum; sum += cnt; }
    for (int c = 0; c < CELL; c++) {
        int start = counts[c];
        int end = (c + 1 < CELL) ? counts[c + 1] : N;
        cellSE[c] = (start == end) ? mk2(-1, -1) : mk2(start, end);
    }
    for (int i = 0; i < N; i++) {
        int hash = hash_of(pos[i], ox, oy, invX, invY);
        int dest = counts[hash]++;
        sorted[dest] = pos[i];
        hashIdx[dest] = mk2(hash, i);
    }

    cl_mem dPos = api.createBuffer(ctx, CL_MEM_READ_ONLY | CL_MEM_COPY_HOST_PTR, N * sizeof(float2), sorted, &err);
    cl_mem dHash = api.createBuffer(ctx, CL_MEM_READ_ONLY | CL_MEM_COPY_HOST_PTR, N * sizeof(int2), hashIdx, &err);
    cl_mem dCell = api.createBuffer(ctx, CL_MEM_READ_ONLY | CL_MEM_COPY_HOST_PTR, CELL * sizeof(int2), cellSE, &err);
    cl_mem dQuery = api.createBuffer(ctx, CL_MEM_READ_WRITE, K * sizeof(float2), NULL, &err);
    cl_mem dRes = api.createBuffer(ctx, CL_MEM_WRITE_ONLY, K * sizeof(int), NULL, &err);
    if (err != CL_SUCCESS) { printf("  clCreateBuffer err=%d\n", err); return -1; }

    const char* src = KERNEL_SRC;
    cl_program prog = api.createProgramWithSource(ctx, 1, &src, NULL, &err);
    err = api.buildProgram(prog, 1, &dev, NULL, NULL, NULL);
    if (err != CL_SUCCESS) {
        char log[16384] = { 0 };
        size_t logSz = 0;
        if (api.getDeviceInfo) api.getDeviceInfo(dev, CL_PROGRAM_BUILD_LOG, sizeof(log), log, &logSz);
        fprintf(stderr, "  clBuildProgram err=%d (log %zu bytes):\n%s\n", err, logSz, log);
        fflush(stderr);
        return -1;
    }
    cl_kernel kern = api.createKernel(prog, "closest", &err);
    if (err != CL_SUCCESS) { printf("  clCreateKernel err=%d\n", err); return -1; }

    api.setKernelArg(kern, 0, sizeof(cl_mem), &dQuery);
    api.setKernelArg(kern, 1, sizeof(cl_mem), &dHash);
    api.setKernelArg(kern, 2, sizeof(cl_mem), &dCell);
    api.setKernelArg(kern, 3, sizeof(cl_mem), &dPos);
    api.setKernelArg(kern, 4, sizeof(cl_mem), &dRes);
    api.setKernelArg(kern, 5, sizeof(float), &ox);
    api.setKernelArg(kern, 6, sizeof(float), &oy);
    api.setKernelArg(kern, 7, sizeof(float), &invX);
    int gdx = DIM, gdy = DIM, ign = 0; float eps = 0.0f; int sortedLen = N;
    api.setKernelArg(kern, 8, sizeof(int), &gdx);
    api.setKernelArg(kern, 9, sizeof(int), &gdy);
    api.setKernelArg(kern, 10, sizeof(int), &ign);
    api.setKernelArg(kern, 11, sizeof(float), &eps);
    api.setKernelArg(kern, 12, sizeof(int), &sortedLen);

    size_t global = K;
    size_t* local = NULL;   // NULL = 驱动自选 work-group，避免 global 不被 local 整除导致 enqueue 失败

    for (int i = 0; i < GRID_WARMUP; i++) { err = api.enqueueNDRangeKernel(q, kern, 1, NULL, &global, local, 0, NULL, NULL); api.finish(q); }
    double wk[GRID_FRAMES];
    for (int f = 0; f < GRID_FRAMES; f++) {
        double t0 = now_ms();
        err = api.enqueueNDRangeKernel(q, kern, 1, NULL, &global, local, 0, NULL, NULL);
        api.finish(q);
        wk[f] = now_ms() - t0;
    }
    if (err != CL_SUCCESS) { printf("  enqueue(kernel) err=%d\n", err); return -1; }
    double gridResident = median(wk, GRID_FRAMES);

    double wr[GRID_FRAMES];
    for (int f = 0; f < GRID_FRAMES; f++) {
        double t0 = now_ms();
        err = api.enqueueWriteBuffer(q, dQuery, 0, 0, K * sizeof(float2), qry, 0, NULL, NULL);
        api.enqueueNDRangeKernel(q, kern, 1, NULL, &global, local, 0, NULL, NULL);
        err = api.enqueueReadBuffer(q, dRes, 0, 0, K * sizeof(int), results, 0, NULL, NULL);
        api.finish(q);
        wr[f] = now_ms() - t0;
    }
    if (err != CL_SUCCESS) { printf("  enqueue(read) err=%d\n", err); return -1; }
    double gridRoundtrip = median(wr, GRID_FRAMES);

    int found = 0;
    for (int i = 0; i < K; i++) if (results[i] != -1) found++;

    /* ============ Move @1M：HeavyMove / LightMove ============ */
    float2* mPos = (float2*)malloc(MOVE_N * sizeof(float2));
    float2* mVel = (float2*)malloc(MOVE_N * sizeof(float2));
    float2* mPosOut = (float2*)malloc(MOVE_N * sizeof(float2));
    for (int i = 0; i < MOVE_N; i++) {
        mPos[i].x = (float)(rnd() * 200 - 100); mPos[i].y = (float)(rnd() * 200 - 100);
        mVel[i].x = (float)(rnd() * 200 - 100); mVel[i].y = (float)(rnd() * 200 - 100);
    }

    cl_mem dMPos = api.createBuffer(ctx, CL_MEM_READ_WRITE, MOVE_N * sizeof(float2), NULL, &err);
    cl_mem dMVel = api.createBuffer(ctx, CL_MEM_READ_ONLY, MOVE_N * sizeof(float2), NULL, &err);
    if (err != CL_SUCCESS) { printf("  clCreateBuffer(Move) err=%d\n", err); return -1; }

    const char* hsrc = HEAVY_KERNEL_SRC;
    cl_program hprog = api.createProgramWithSource(ctx, 1, &hsrc, NULL, &err);
    err = api.buildProgram(hprog, 1, &dev, NULL, NULL, NULL);
    if (err != CL_SUCCESS) { printf("  clBuildProgram(heavy) err=%d\n", err); return -1; }
    cl_kernel hkern = api.createKernel(hprog, "heavy", &err);
    const char* lsrc = LIGHT_KERNEL_SRC;
    cl_program lprog = api.createProgramWithSource(ctx, 1, &lsrc, NULL, &err);
    err = api.buildProgram(lprog, 1, &dev, NULL, NULL, NULL);
    if (err != CL_SUCCESS) { printf("  clBuildProgram(light) err=%d\n", err); return -1; }
    cl_kernel lkern = api.createKernel(lprog, "light", &err);

    float dt = DT;
    size_t mglobal = MOVE_N;

    /* ---- staged 往返（clEnqueueWriteBuffer/ReadBuffer，pageable 两跳） ---- */
    api.setKernelArg(hkern, 0, sizeof(cl_mem), &dMPos);
    api.setKernelArg(hkern, 1, sizeof(cl_mem), &dMVel);
    api.setKernelArg(hkern, 2, sizeof(float), &dt);
    api.setKernelArg(lkern, 0, sizeof(cl_mem), &dMPos);
    api.setKernelArg(lkern, 1, sizeof(cl_mem), &dMVel);
    api.setKernelArg(lkern, 2, sizeof(float), &dt);

    struct { cl_kernel k; const char* name; } moveKernels[] = {
        { hkern, "HeavyMove" },
        { lkern, "LightMove" },
    };
    double moveRoundtrip[2], moveRoundtripPin[2], moveResident[2];
    for (int mk = 0; mk < 2; mk++) {
        cl_kernel k = moveKernels[mk].k;
        for (int i = 0; i < MOVE_WARMUP; i++) { api.enqueueNDRangeKernel(q, k, 1, NULL, &mglobal, local, 0, NULL, NULL); api.finish(q); }
        double ws[MOVE_FRAMES];
        for (int f = 0; f < MOVE_FRAMES; f++) {
            double t0 = now_ms();
            err = api.enqueueNDRangeKernel(q, k, 1, NULL, &mglobal, local, 0, NULL, NULL);
            api.finish(q);
            ws[f] = now_ms() - t0;
        }
        if (err != CL_SUCCESS) { printf("  enqueue(move kernel) err=%d\n", err); return -1; }
        moveResident[mk] = median(ws, MOVE_FRAMES);

        double wrr[MOVE_FRAMES];
        for (int f = 0; f < MOVE_FRAMES; f++) {
            double t0 = now_ms();
            api.enqueueWriteBuffer(q, dMPos, 0, 0, MOVE_N * sizeof(float2), mPos, 0, NULL, NULL);
            api.enqueueWriteBuffer(q, dMVel, 0, 0, MOVE_N * sizeof(float2), mVel, 0, NULL, NULL);
            err = api.enqueueNDRangeKernel(q, k, 1, NULL, &mglobal, local, 0, NULL, NULL);
            err = api.enqueueReadBuffer(q, dMPos, 0, 0, MOVE_N * sizeof(float2), mPosOut, 0, NULL, NULL);
            api.finish(q);
            wrr[f] = now_ms() - t0;
        }
        if (err != CL_SUCCESS) { printf("  enqueue(move roundtrip) err=%d\n", err); return -1; }
        moveRoundtrip[mk] = median(wrr, MOVE_FRAMES);
    }

    /* ---- 页锁定往返：VirtualLock host 内存 + Write/ReadBuffer（等价 cudaHostAlloc 直接 DMA） ---- */
    int pinnedOk = 1;
    float2* hPosPin = (float2*)lock_pages(MOVE_N * sizeof(float2));
    float2* hVelPin = (float2*)lock_pages(MOVE_N * sizeof(float2));
    float2* hOutPin = (float2*)lock_pages(MOVE_N * sizeof(float2));
    if (!hPosPin || !hVelPin || !hOutPin) {
        pinnedOk = 0;
        printf("  OpenCL 页锁定不可用：VirtualLock 需 SeLockMemoryPrivilege（进程默认无）→ pinned 对照跳过\n");
        _aligned_free(hPosPin); _aligned_free(hVelPin); _aligned_free(hOutPin);
        hPosPin = hVelPin = hOutPin = NULL;
    } else {
        memcpy(hPosPin, mPos, MOVE_N * sizeof(float2));
        memcpy(hVelPin, mVel, MOVE_N * sizeof(float2));
    }
    for (int mk = 0; mk < 2; mk++) {
        moveRoundtripPin[mk] = -1;
        if (!pinnedOk) continue;
        cl_kernel k = moveKernels[mk].k;
        double wp[MOVE_FRAMES];
        for (int f = 0; f < MOVE_FRAMES; f++) {
            double t0 = now_ms();
            api.enqueueWriteBuffer(q, dMPos, 0, 0, MOVE_N * sizeof(float2), hPosPin, 0, NULL, NULL);
            api.enqueueWriteBuffer(q, dMVel, 0, 0, MOVE_N * sizeof(float2), hVelPin, 0, NULL, NULL);
            err = api.enqueueNDRangeKernel(q, k, 1, NULL, &mglobal, local, 0, NULL, NULL);
            err = api.enqueueReadBuffer(q, dMPos, 0, 0, MOVE_N * sizeof(float2), hOutPin, 0, NULL, NULL);
            api.finish(q);
            wp[f] = now_ms() - t0;
        }
        if (err != CL_SUCCESS) { printf("  enqueue(move pinned) err=%d\n", err); return -1; }
        moveRoundtripPin[mk] = median(wp, MOVE_FRAMES);
    }
    if (hPosPin) { VirtualUnlock(hPosPin, MOVE_N * sizeof(float2)); _aligned_free(hPosPin); }
    if (hVelPin) { VirtualUnlock(hVelPin, MOVE_N * sizeof(float2)); _aligned_free(hVelPin); }
    if (hOutPin) { VirtualUnlock(hOutPin, MOVE_N * sizeof(float2)); _aligned_free(hOutPin); }

    /* sanity：roundtrip 读回结果有限 + 非 NaN（浮点实现差异，不做逐元素相等） */
    int bad = 0;
    for (int i = 0; i < 1024; i++) {
        float2 v = mPosOut[(i * 977) % MOVE_N];
        if (!(v.x >= -1e9f && v.x <= 1e9f) || !(v.y >= -1e9f && v.y <= 1e9f)) bad++;
    }

    printf("\n  GridSearch@100k closest 常驻  : %8.3f ms\n", gridResident);
    printf("  GridSearch@100k closest 往返  : %8.3f ms  (staged)\n", gridRoundtrip);
    printf("  sanity: 有结果查询 %d/%d；Move 读回有限 %d 坏\n", found, K, bad);
    printf("  对照 ILGPU OpenCL 常驻 0.915 / 往返 1.409；CUDA 常驻 0.151 / 往返(页锁定) 0.311\n");

    /* ---- 汇总：原生 OpenCL(staged/页锁定) vs NativeDll 多线程 vs CPU 单线程 ---- */
    printf("\n  HeavyMove@1M(16iter) p50 ms：\n");
    printf("    CPU 单线程(C++ 镜像 transpiler): %8.3f\n", g_nativeHeavyCpu);
    printf("    NativeDll JobSystem 多线程       : %8.3f   (单线程 x%.2f)\n", g_nativeHeavyMt, g_nativeHeavyCpu / g_nativeHeavyMt);
    printf("    OpenCL 常驻(kernel-only)         : %8.3f   (vs CPU 单线程 x%.2f)\n", moveResident[0], g_nativeHeavyCpu / moveResident[0]);
    printf("    OpenCL 往返 staged               : %8.3f   (vs CPU 单线程 x%.2f)\n", moveRoundtrip[0], g_nativeHeavyCpu / moveRoundtrip[0]);
    if (moveRoundtripPin[0] >= 0)
        printf("    OpenCL 往返 页锁定(驱动)         : %8.3f   (vs CPU 单线程 x%.2f)\n", moveRoundtripPin[0], g_nativeHeavyCpu / moveRoundtripPin[0]);
    else
        printf("    OpenCL 往返 页锁定               :      n/a   (VirtualLock 权限不足)\n");
    printf("  LightMove@1M p50 ms：\n");
    printf("    CPU 单线程(C++ 镜像 transpiler): %8.3f\n", g_nativeLightCpu);
    printf("    NativeDll JobSystem 多线程       : %8.3f   (单线程 x%.2f)\n", g_nativeLightMt, g_nativeLightCpu / g_nativeLightMt);
    printf("    OpenCL 常驻(kernel-only)         : %8.3f   (vs CPU 单线程 x%.2f)\n", moveResident[1], g_nativeLightCpu / moveResident[1]);
    printf("    OpenCL 往返 staged               : %8.3f   (vs CPU 单线程 x%.2f)\n", moveRoundtrip[1], g_nativeLightCpu / moveRoundtrip[1]);
    if (moveRoundtripPin[1] >= 0)
        printf("    OpenCL 往返 页锁定(驱动)         : %8.3f   (vs CPU 单线程 x%.2f)\n", moveRoundtripPin[1], g_nativeLightCpu / moveRoundtripPin[1]);
    else
        printf("    OpenCL 往返 页锁定               :      n/a   (VirtualLock 权限不足)\n");

    /* ---- 汇总到全局（main 最后打印直观对比表） ---- */
    g_sum.gridResident[0] = gridResident;
    g_sum.gridRoundtrip[0] = gridRoundtrip;
    g_sum.gridPinned[0] = -1;                 /* VirtualLock 权限不足，无页锁定列 */
    g_sum.gridMismatch[0] = -1;               /* 仅 sanity（有结果数），未逐元素 parity */
    g_sum.heavyResident[0] = moveResident[0]; g_sum.heavyRoundtrip[0] = moveRoundtrip[0];
    g_sum.heavyPinned[0] = moveRoundtripPin[0];
    g_sum.lightResident[0] = moveResident[1]; g_sum.lightRoundtrip[0] = moveRoundtrip[1];
    g_sum.lightPinned[0] = moveRoundtripPin[1];

    api.releaseKernel(hkern); api.releaseProgram(hprog);
    api.releaseKernel(lkern); api.releaseProgram(lprog);
    api.releaseMemObject(dMPos); api.releaseMemObject(dMVel);
    api.releaseKernel(kern); api.releaseProgram(prog);
    api.releaseMemObject(dPos); api.releaseMemObject(dHash); api.releaseMemObject(dCell);
    api.releaseMemObject(dQuery); api.releaseMemObject(dRes);
    api.releaseCommandQueue(q); api.releaseContext(ctx);
    free(pos); free(qry); free(sorted); free(hashIdx); free(cellSE); free(results); free(counts);
    free(mPos); free(mVel); free(mPosOut);
    FreeLibrary(api.module);
    return gridResident;
}
