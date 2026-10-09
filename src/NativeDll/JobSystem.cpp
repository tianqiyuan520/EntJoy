#include "JobSystemInternal.h"
#include "ChaseLevScheduler.h"

#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <algorithm>
#include <array>
#include <chrono>
#include <cctype>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <memory>
#include <mutex>
#include <limits>
#include <thread>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

#if defined(_WIN32)
// 批表按导出名编写、加载时解析成本模块内的 RVA，因此需要读本模块自己的 PE 导出表。
#include <windows.h>
#endif

#if defined(__linux__)
#include <sched.h>
#endif

#if defined(_MSC_VER) && (defined(_M_IX86) || defined(_M_X64))
#include <immintrin.h>
#include <windows.h>
#include <timeapi.h>
#pragma comment(lib, "winmm.lib")
#endif

namespace JobSystem
{
    // ---------- 加载期 banner 缓冲（实现见 JobSystemInternal.h 的说明） ----------
    // 放在本文件（而不是 Exports.cpp）：本文件同时被主 DLL 与 `tests/NativeDll.Tests` 的
    // 各个 .vcxproj 编译，而 Exports.cpp 只在主 DLL 里 ⇒ 放这里两端都能链接。
    // 加载期（`_CRT_INIT` = DllMain 期，持有 loader lock）不做 I/O：banner 先入缓冲，
    // 由 `JobSystem_Initialize()`（Exports.cpp）一次性 flush。
    static char g_loadBannerBuf[8192];
    static size_t g_loadBannerLen = 0;
    static std::atomic<bool> g_loadBannerFlushed{ false };

    void LoadBannerAppend(const char* fmt, ...) noexcept
    {
        if (g_loadBannerFlushed.load(std::memory_order_relaxed)) return;   // flush 之后（运行期）直接丢
        const size_t room = sizeof(g_loadBannerBuf) - g_loadBannerLen;
        if (room == 0) return;
        va_list ap;
        va_start(ap, fmt);
        const int n = std::vsnprintf(g_loadBannerBuf + g_loadBannerLen, room, fmt, ap);
        va_end(ap);
        if (n <= 0) return;
        const size_t wanted = static_cast<size_t>(n);
        g_loadBannerLen += (wanted < room) ? wanted : (room - 1);
    }

    void LoadBannerFlush() noexcept
    {
        if (g_loadBannerFlushed.exchange(true, std::memory_order_relaxed)) return;
        if (g_loadBannerLen == 0) return;
        std::fwrite(g_loadBannerBuf, 1, g_loadBannerLen, stderr);
        std::fflush(stderr);
        g_loadBannerLen = 0;
    }

    // ---------- 调试面板 per-worker 实时状态 ----------
    PaddedAtomic<uint64_t> g_workerCurrentBatchId[kMaxTrackedWorkers];
    PaddedAtomic<uint32_t> g_workerCurrentTile[kMaxTrackedWorkers];
    PaddedAtomic<uint32_t> g_workerBatchTileCount[kMaxTrackedWorkers];
    PaddedAtomic<bool>     g_workerIsActive[kMaxTrackedWorkers];
    std::atomic<bool>     g_debugPaused{ false }; // GUI 暂停标志：暂停时停止记录新段
    ExecWindowRing g_execWindows[kMaxTrackedWorkers]{};
    // 共享时间线历史：job 执行线程在结束瞬间追加（DebugEndExec），GUI 线程只读渲染
    DebugSegment g_debugSegments[kDebugSegmentMax]{};
    std::atomic<unsigned int> g_debugSegHead{ 0 };
    std::atomic<unsigned int> g_debugSegVisible{ 0 };
    std::atomic<uint64_t> g_debugSegSeq[kDebugSegmentMax]{};

    std::atomic<bool> g_workerAffinityEnabled{ false };

    // ---------- Globals ----------
    std::mutex g_schedulerMutex;
    std::shared_ptr<ChaseLevScheduler> g_chaseLevScheduler;
    // 调度器进程内唯一实例（永不析构）+ 伴生裸指针（热路径无锁读取）。
    // 详见 JobSystemInternal.h 的 LoadChaseLevScheduler 注释。
    std::shared_ptr<ChaseLevScheduler> g_chaseLevSchedulerInstance;
    std::atomic<ChaseLevScheduler*> g_chaseLevSchedulerRaw{ nullptr };
    std::atomic<int> g_numThreads{ 0 };

    // 并行 for 默认 tiles/worker（batchSize=0 时 ResolveChunkSize 使用）。
    // 默认 16 为可变代价与均匀代价 job 的折中；env 可覆盖。
    std::atomic<int> g_configuredTilesPerWorker{ kDefaultTilesPerWorker };

    // JobCostCache export flag（State 模块 ResolveChunkSize 与 Tiles 退役路径读取）。
    // C# Initialize 强制同步此值（防 DLL 重载不一致）。关闭 = 纯 tpw=4（冷启动/保守场景）。
    // `ENTJOY_JOB_COST_CACHE=0` 关断 JCC（默认 1 = 逐位不变）。托管侧默认值读同一个 env，
    // 否则 C# 的 Initialize 会把这个值盖回去。
    std::atomic<bool> g_jobCostCacheEnabled{ []() -> bool {
        const char* v = std::getenv("ENTJOY_JOB_COST_CACHE");
        return !(v != nullptr && v[0] == '0');
    }() };

    // 提交期延迟唤醒深度（ChaseLevScheduler::SubmitBatch 尾部读取；defer>0 跳过逐批 notify）
    std::atomic<int> g_submitDeferDepth{ 0 };

    // ── 诊断统计总开关 ──
    // 这些计数器只被 GetStatsSnapshot / 调试面板消费，本身不是同步原语（唯一例外
    // g_backendBatchesOutstanding：它是 WaitForBackendBatches 的等待条件，见该处注释，不 gate）。
    // 关闭（`ENTJOY_STATS=0`）可省掉热路径的 locked RMW；此时统计读数不再精确。
    std::atomic<bool> g_statsEnabled{ true };
    // 进程启动读一次 env（与 g_jobCostCacheVerbose 同法；同 TU 内 g_statsEnabled 已常量初始化）。
    static const bool g_statsEnvInitialized = []() -> bool {
        const char* v = std::getenv("ENTJOY_STATS");
        if (v != nullptr && v[0] == '0')
            g_statsEnabled.store(false, std::memory_order_relaxed);
        return true;
    }();

    // 隐式批（native 收集）开关 + pending 列表（extern 声明见 JobSystemInternal.h）。
    // 默认关闭：Schedule* 直接提交；开启后 tile 路径 job 挂入 pending，由 FlushPendingSubmits 统一提交 + 单次唤醒。
    std::atomic<bool> g_implicitBatchEnabled{ false };
    std::mutex g_pendingBatchesMutex;
    std::vector<BatchState*> g_pendingBatches;

    // 诊断开关（进程启动时读 env 一次，之后只读）：ENTJOY_JCC_VERBOSE=1
    // 打印 per-job 自动 batch 的决策与学习快照。
    bool g_jobCostCacheVerbose = []() -> bool {
        const char* v = std::getenv("ENTJOY_JCC_VERBOSE");
        return v != nullptr && v[0] == '1';
    }();

    // 认领粒子上限的运行期覆盖（`ENTJOY_CLAIM_BATCH=<n>`；0/未设 ⇒ 用 kClaimBatchSize）。
    // 只调上限 cap，`step = clamp(tileCount/workers, 1, cap)` 保留自适应项
    // ⇒ 小批次自动退回细粒度，只有大批次才被摊薄。
    uint32_t g_claimBatchSize = []() -> uint32_t {
        const char* v = std::getenv("ENTJOY_CLAIM_BATCH");
        if (v == nullptr) return 0;
        const long n = std::strtol(v, nullptr, 10);
        if (n <= 0) return 0;              // 0/负数 = 用内置默认（默认档逐位不变）
        const uint32_t cap = (n > 4096) ? 4096u : static_cast<uint32_t>(n);
        // 仅在 env 显式设置时打印一次实际生效的认领上限（默认档不打印 ⇒ 生产路径零噪声）。
        EJ_LOADBANNER( "[CLAIMBATCH] cap=%u (builtin default = 4, see ChaseLevScheduler.h kClaimBatchSize)\n",
            static_cast<unsigned>(cap));
        return cap;
    }();

    // 按元素跨度认领的运行期覆盖（`ENTJOY_CLAIM_SPAN=<元素数>`）：把"每次认领的元素跨度"钉住
    //（而不是钉 tile 数）——`itemsPerTile = totalElements/tileCount`；仅当
    // `itemsPerTile ≤ kClaimSpanThinElems(=16)` 时 `capEff = clamp(SPAN/itemsPerTile, cap, SPAN)`，
    // 否则 `capEff = cap`。厚 tile 不受影响（保持 worker 邻近）。内置默认 1024；显式 0 = 关。
    uint32_t g_claimSpanElems = []() -> uint32_t {
        const char* v = std::getenv("ENTJOY_CLAIM_SPAN");
        // 显式 `ENTJOY_CLAIM_SPAN=0` = 关闭（回退逐 tile 认领）。
        if (v == nullptr) return 1024u;
        const long n = std::strtol(v, nullptr, 10);
        if (n <= 0) return 0;              // 显式 0/负数 = 关（复现旧行为）
        const uint32_t span = (n > kClaimSpanElemsMax) ? kClaimSpanElemsMax : static_cast<uint32_t>(n);
        EJ_LOADBANNER( "[CLAIMSPAN] span=%u elements (thin-tile gate: itemsPerTile<=%u)\n",
            static_cast<unsigned>(span), static_cast<unsigned>(kClaimSpanThinElems));
        return span;
    }();

    // 等宽 GeneralRange 一律不物化 tileBuffer（默认开；`ENTJOY_TILES_UNIFORM=0` 回退）。
    // 规则：`uniformTiles = 本开关 && !guided`，只作用于"非 guided 的等宽 GeneralRange"；
    // chunk/entity/packed/guided 路径不变。收益是消掉提交侧 O(tileCount) 填表与执行侧 tile 数组读。
    bool g_uniformTilesEnabled = []() -> bool {
        const char* v = std::getenv("ENTJOY_TILES_UNIFORM");
        const bool on = (v == nullptr) ? true : (v[0] == '1');
        if (v != nullptr)
            EJ_LOADBANNER("[TILESUNIFORM] %s (explicit ENTJOY_TILES_UNIFORM=%s)\n", on ? "on" : "off", v);
        else
            EJ_LOADBANNER("[TILESUNIFORM] on (built-in default; ENTJOY_TILES_UNIFORM=0 disables)\n");
        return on;
    }();

    // 把 `TryExecuteOneTile` 的每-tile 固定开销提到每批/每令牌（默认开；`ENTJOY_TILE_FASTPATH=0` 回退）：
    // 批构造时把 `g_traceEnabled/g_timingDiagnosticsEnabled` 快照进 `BatchState.traceOn/timingOn`，
    // `firstTileAt` 判据从"每 tile"改为"每令牌一次"。不动 `tilesRemaining` 记账（无挂起风险）。
    bool g_tileFastPath = []() -> bool {
        const char* v = std::getenv("ENTJOY_TILE_FASTPATH");
        const bool on = (v == nullptr) ? true : (v[0] == '1');
        if (v != nullptr)
            EJ_LOADBANNER("[TILEFASTPATH] %s (explicit ENTJOY_TILE_FASTPATH=%s)\n", on ? "on" : "off", v);
        else
            EJ_LOADBANNER("[TILEFASTPATH] on (built-in default; ENTJOY_TILE_FASTPATH=0 disables)\n");
        return on;
    }();

    // per-job 认领几何学习（默认开；`ENTJOY_CLAIM_ADAPT=0` 关闭）。学习期奇偶交替（交错/切片），
    // 两臂各有 ≥4 样本后按"每元素执行成本更低者"定型（3% 迟滞 + 冷却 + 每 64 次反向探针）。
    // 设计见 JobCostCache.h 的 ClaimMode 段。批表/API 的几何声明优先级更高，已声明的 kernel 不受影响。
    bool g_claimAdaptiveEnabled = []() -> bool {
        const char* v = std::getenv("ENTJOY_CLAIM_ADAPT");
        const bool on = (v == nullptr) ? true : (v[0] == '1');
        if (v != nullptr)
            EJ_LOADBANNER("[CLAIMADAPT] %s (explicit ENTJOY_CLAIM_ADAPT=%s)\n", on ? "on" : "off", v);
        else
            EJ_LOADBANNER("[CLAIMADAPT] on (built-in default; ENTJOY_CLAIM_ADAPT=0 disables)\n");
        return on;
    }();

    // 目标每 tile 串行量（µs），默认 6400：`ResolveChunkSize` 的 `two_factor` 分支的唯一消费者
    //（`tileSize = (kTargetTileUs − C_fixed)/C_elem`）。`ENTJOY_JCC_TARGET_US=<n>` 运行期覆盖。
    double g_jccTargetTileUs = []() -> double {
        const char* v = std::getenv("ENTJOY_JCC_TARGET_US");
        if (v == nullptr) return 6400.0;
        const double n = std::strtod(v, nullptr);
        if (!(n >= 1.0 && n <= 100000.0)) return 6400.0;
        EJ_LOADBANNER( "[JCCTARGETUS] target=%.1f us (builtin default = 6400)\n", n);
        return n;
    }();

    // 强制显式内批（`ENTJOY_FORCE_INNER_BATCH=<n>`，默认 0 = 关）；
    // 语义/用途见 JobSystemInternal.h 的同名声明。
    uint32_t g_forceInnerBatch = []() -> uint32_t {
        const char* v = std::getenv("ENTJOY_FORCE_INNER_BATCH");
        if (v == nullptr) return 0;
        const long n = std::strtol(v, nullptr, 10);
        if (n <= 0) return 0;
        const uint32_t b = (n > (1 << 20)) ? (1u << 20) : static_cast<uint32_t>(n);
        EJ_LOADBANNER( "[FORCEINNERBATCH] batch=%u (all auto-batch dispatches forced; JCC bypassed)\n",
            static_cast<unsigned>(b));
        return b;
    }();

    // 按 job 的内批档表（`ENTJOY_JOB_BATCH_TABLE`，默认空 = 关）。语义/用途/前提见
    // JobSystemInternal.h 的同名声明块。加载时解析一次 env（与 FORCE_INNER_BATCH 同款）。
    JobBatchTableEntry g_jobBatchTable[kJobBatchTableCap] = {};
    /// 与上表同槽：非空表示该槽是"按 job 名登记"的（key 由 BindJobBatchName 填）。
    char g_jobBatchSlotName[kJobBatchTableCap][96] = {};
    bool g_jobBatchTableDump = false;

    uint32_t g_jobBatchTableCount = []() -> uint32_t {
        const char* d = std::getenv("ENTJOY_JOB_BATCH_TABLE_DUMP");
        g_jobBatchTableDump = (d != nullptr && d[0] == '1');
        const char* v = std::getenv("ENTJOY_JOB_BATCH_TABLE");
        uint32_t n = 0;
        if (v != nullptr)
        {
            const char* p = v;
            while (*p != '\0' && n < kJobBatchTableCap)
            {
                while (*p == ',' || *p == ';' || *p == ' ' || *p == '\t') ++p;
                if (*p == '\0') break;
                char* end = nullptr;
                const unsigned long h = std::strtoul(p, &end, 16);
                if (end == p) break;
                p = end;
                // 段内解析 `<key>:<batch>[:<claim>][:<geom>]`，各段都可省略
                //   ⇒ `key::1024` 合法（只覆盖认领、不改内批）。
                // 第四字段 = 认领几何：`s`/`S` = Spread（每 worker 独占连续段，空手才窃取）、
                //   `a`/`A` = Adjacent（共享游标发相邻窗口）；缺省 = Auto（走全局 env / F6 学习）。
                //   语义是"调用点声明"（键就是调用点，不按 job 名特判）。
                {
                    const char* segEnd = p;
                    while (*segEnd != '\0' && *segEnd != ',' && *segEnd != ';') ++segEnd;
                    long b = 0, c = 0, sp = 0;
                    uint32_t g = kClaimGeomAuto;
                    char* e2 = nullptr;
                    const char* q = p;
                    if (q < segEnd && (*q == ':' || *q == '=')) ++q;                                // 只跳过一个键后分隔符
                    if (q < segEnd && *q != ':') { b = std::strtol(q, &e2, 10); q = e2; }            // 内批（可空 ⇒ 停在 ':'）
                    if (q < segEnd && *q == ':') ++q;                                              // 跳过内批后的分隔符
                    // 第三字段两种形态 —— `<claim>`（tile 数）或 `e<N>`（元素跨度）：
                    //   前导 'e'/'E' 即元素形态；两者互斥（同一字段）。
                    if (q < segEnd && (*q == 'e' || *q == 'E')) { ++q; sp = std::strtol(q, &e2, 10); q = e2; }
                    else if (q < segEnd && *q != ':') { c = std::strtol(q, &e2, 10); q = e2; }
                    if (q < segEnd && *q == ':') ++q;                                              // 跳过认领后的分隔符
                    if (q < segEnd)                                                                 // 几何（可空）
                    {
                        if (*q == 's' || *q == 'S') g = kClaimGeomSpread;
                        else if (*q == 'a' || *q == 'A') g = kClaimGeomAdjacent;
                    }
                    p = segEnd;
                    if (b > 0 || c > 0 || sp > 0 || g != kClaimGeomAuto)
                    {
                        g_jobBatchTable[n].key = static_cast<uint32_t>(h);
                        g_jobBatchTable[n].batch = (b > 0)
                            ? ((b > (1 << 20)) ? (1u << 20) : static_cast<uint32_t>(b)) : 0u;
                        g_jobBatchTable[n].claim = (c > 0)
                            ? ((c > (1 << 20)) ? (1u << 20) : static_cast<uint32_t>(c)) : 0u;
                        g_jobBatchTable[n].span = (sp > 0)
                            ? ((static_cast<uint32_t>(sp) > kClaimSpanDeclaredMax)
                                ? kClaimSpanDeclaredMax : static_cast<uint32_t>(sp)) : 0u;
                        g_jobBatchTable[n].geom = g;
                        ++n;
                    }
                }
            }
            EJ_LOADBANNER(
                "[JOBBATCHTABLE] hex_entries=%u dump=%d (auto-batch: table hit wins over ENTJOY_FORCE_INNER_BATCH; JCC bypassed)\n",
                static_cast<unsigned>(n), g_jobBatchTableDump ? 1 : 0);
        }
        else
        {
            EJ_LOADBANNER( "[JOBBATCHTABLE] hex_entries=0 dump=%d\n", g_jobBatchTableDump ? 1 : 0);
        }

        // 按 job 名编写的批表（`ENTJOY_JOB_BATCH_BY_NAME`）：`Name:batch[,Name:batch]…`，`Name` = 托管 job
        // 类型名（`typeof(T).Name`）。加载期只登记名字（key 留 0），key 由 `BindJobBatchName` 在静态构造期
        // （任何派发之前）按派发用的函数指针填入：`JobFuncKey` = 该指针在其所属模块内的 RVA，与派发侧同式
        // ⇒ 必然同键，且与符号命名规则无关。名字不随重编漂移；始终绑不上的名字会大声打印。
        {
            const char* vn = std::getenv("ENTJOY_JOB_BATCH_BY_NAME");
            if (vn != nullptr && vn[0] != '\0')
            {
                uint32_t named = 0;
                const char* p = vn;
                while (*p != '\0' && n < kJobBatchTableCap)
                {
                    while (*p == ',' || *p == ';' || *p == ' ' || *p == '\t') ++p;
                    if (*p == '\0') break;
                    const char* segEnd = p;
                    while (*segEnd != '\0' && *segEnd != ',' && *segEnd != ';') ++segEnd;
                    const char* colon = p;
                    while (colon < segEnd && *colon != ':' && *colon != '=') ++colon;
                    const size_t tl = static_cast<size_t>(colon - p);
                    if (tl > 0 && tl < sizeof(g_jobBatchSlotName[0]))
                    {
                        std::memcpy(g_jobBatchSlotName[n], p, tl);
                        g_jobBatchSlotName[n][tl] = '\0';
                        const long b = (colon < segEnd) ? std::strtol(colon + 1, nullptr, 10) : 0;
                        g_jobBatchTable[n].key = 0u;            // 未解析：首次派发时填
                        g_jobBatchTable[n].batch = (b > 0)
                            ? ((b > (1 << 20)) ? (1u << 20) : static_cast<uint32_t>(b)) : 0u;
                        g_jobBatchTable[n].claim = 0u;
                        g_jobBatchTable[n].span = 0u;
                        g_jobBatchTable[n].geom = kClaimGeomAuto;
                        ++n; ++named;
                    }
                    p = segEnd;
                }
                EJ_LOADBANNER(
                    "[JOBBATCHBYNAME] registered=%u total_slots=%u (key 由托管侧 BindJobBatchName 在静态构造期填；始终未绑的名字会另打一行)\n",
                    static_cast<unsigned>(named), static_cast<unsigned>(n));
            }
        }
        return n;
    }();

    /// 把"按名字登记"的批表槽位绑定到该 job 实际派发用的函数指针。
    /// 由托管侧的生成绑定在静态构造里逐个 job 调用（`NativeJobScheduler.BindNativeJobBatchName`）。
    /// 通用性：名字来自托管 `Type.Name`，指针来自托管真正交给调度器的那个函数指针 ⇒
    ///   不需要知道任何符号命名规则/命名空间/ABI 约定。旧实现拼
    ///   `SharpNative_Job_<命名空间>_<类型>_Execute_Adapter` 并扫 PE 导出表 ⇒ 等价于把本工程的
    ///   命名空间（`CPUBattle`）硬编码进框架，换工程一条都解析不出来。
    /// 并发：键由 `JobFuncKey`（= 指针在其所属模块内的 RVA）算出，与派发侧对同一指针的算式
    ///   逐字相同 ⇒ 必然同键；且本函数在任何派发之前完成 ⇒ 表在读侧是只读的（不再有
    ///   "首次派发时其它线程在 CAS 观察者路径上读到半成品 key"的竞态与数据竞争）。
    /// 返回值：1 = 该名字在表里（已绑定）；0 = 表里没有这个名字（调用方无需处理）。
    int BindJobBatchName(const char* name, void* func) noexcept
    {
        if (name == nullptr || name[0] == '\0' || func == nullptr) return 0;
        const uint32_t key = JobFuncKey(reinterpret_cast<void (*)() noexcept>(func));
        if (key == 0u) return 0;
        int bound = 0;
        for (uint32_t i = 0; i < g_jobBatchTableCount; ++i)
        {
            if (g_jobBatchSlotName[i][0] == '\0') continue;
            if (std::strcmp(g_jobBatchSlotName[i], name) != 0) continue;
            if (g_jobBatchTable[i].key == 0u) g_jobBatchTable[i].key = key;
            bound = 1;
        }
        return bound;
    }

    /// 首次真正查表时打一行"按名槽位"的对账（`resolved` = 已被 `BindJobBatchName` 绑上的槽位数）。
    /// 纯诊断：只读表、无副作用，重复调用/并发调用最多多打一行（故用 relaxed 交换，不阻塞任何人）。
    void ReportJobBatchNames() noexcept
    {
        static std::atomic<bool> s_reported{ false };
        bool expected = false;
        if (!s_reported.compare_exchange_strong(expected, true, std::memory_order_relaxed))
            return;
        uint32_t resolved = 0;
        uint32_t named = 0;
        bool truncated = false;
        char unresolved[1024];
        size_t urLen = 0;
        unresolved[0] = '\0';
        for (uint32_t i = 0; i < g_jobBatchTableCount; ++i)
        {
            if (g_jobBatchSlotName[i][0] == '\0') continue;
            ++named;
            if (g_jobBatchTable[i].key != 0u) { ++resolved; continue; }
            const size_t tl = std::strlen(g_jobBatchSlotName[i]);
            if (urLen + tl + 2 < sizeof(unresolved))
            {
                std::memcpy(unresolved + urLen, g_jobBatchSlotName[i], tl);
                urLen += tl; unresolved[urLen++] = ' '; unresolved[urLen] = '\0';
            }
            else truncated = true;
        }
        if (named == 0) return;   // 只有 hex 形态（或表为空）⇒ 无"按名槽位"可对账，不打这一行
        // ⚠ 这里不能用 `EJ_LOADBANNER`：那是"加载期缓冲"，而本函数在首次派发时执行
        //   （缓冲早已 flush）⇒ 报警会被丢掉。承诺是"不再静默失效"，故直接写 stderr。
        std::fprintf(stderr,
            "[JOBBATCHBYNAME] resolved=%u/%u unresolved=(%s)%s\n",
            static_cast<unsigned>(resolved), static_cast<unsigned>(named),
            unresolved[0] != '\0' ? unresolved : "none",
            truncated ? " +more" : "");
        std::fflush(stderr);
    }

    uint32_t LookupJobBatch(uint32_t key) noexcept
    {
        for (uint32_t i = 0; i < g_jobBatchTableCount; ++i)
            if (g_jobBatchTable[i].key == key) return g_jobBatchTable[i].batch;
        return 0;
    }

    // 表项第三字段：按 job 的认领上限覆盖（缺省 0 = 不覆盖）
    uint32_t LookupJobClaim(uint32_t key) noexcept
    {
        for (uint32_t i = 0; i < g_jobBatchTableCount; ++i)
            if (g_jobBatchTable[i].key == key) return g_jobBatchTable[i].claim;
        return 0;
    }

    // 表项第三字段的 `e<N>` 元素跨度形态（0 = 未声明），与 LookupJobClaim 互斥（同一字段的两种形态）；
    // 上界已在解析时钳到 kClaimSpanDeclaredMax。
    uint32_t LookupJobSpan(uint32_t key) noexcept
    {
        for (uint32_t i = 0; i < g_jobBatchTableCount; ++i)
            if (g_jobBatchTable[i].key == key) return g_jobBatchTable[i].span;
        return 0;
    }

    // 表项第四字段 = 该调用点的认领几何（0=Auto / 1=Spread / 2=Adjacent）。
    // 缺省/未命中 ⇒ Auto ⇒ 走全局 env 与 F6 学习。
    uint32_t LookupJobGeom(uint32_t key) noexcept
    {
        for (uint32_t i = 0; i < g_jobBatchTableCount; ++i)
            if (g_jobBatchTable[i].key == key) return g_jobBatchTable[i].geom;
        return 0;
    }

    uint32_t JobFuncKey(void (*func)() noexcept) noexcept
    {
        const uintptr_t p = reinterpret_cast<uintptr_t>(func);
        constexpr uint32_t kSlots = 64;
        static std::atomic<uintptr_t> s_ptr[kSlots];
        static std::atomic<uint32_t> s_key[kSlots];
        const uint32_t slot = static_cast<uint32_t>(((p >> 4) ^ (p >> 13)) & (kSlots - 1));
        if (s_ptr[slot].load(std::memory_order_relaxed) == p)
            return s_key[slot].load(std::memory_order_relaxed);
        uint32_t key = static_cast<uint32_t>(p);
#if defined(_WIN32)
        HMODULE hmod = nullptr;
        if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCWSTR>(p), &hmod) && hmod != nullptr)
        {
            const uintptr_t base = reinterpret_cast<uintptr_t>(hmod);
            if (p >= base) key = static_cast<uint32_t>(p - base);
        }
#endif
        s_key[slot].store(key, std::memory_order_relaxed);
        s_ptr[slot].store(p, std::memory_order_relaxed);
        return key;
    }

    // `ENTJOY_JOB_TILE_TRACE=<K>` ⇒ 每个键前 K 次调度都打印 tiles（默认 0 ⇒ 只打首见），
    // 用于观测稳态 tiling（而非从墙钟反推）。
    uint32_t g_jobTileTrace = []() -> uint32_t {
        const char* v = std::getenv("ENTJOY_JOB_TILE_TRACE");
        const long k = (v != nullptr) ? std::strtol(v, nullptr, 10) : 0;
        return (k > 0) ? static_cast<uint32_t>(k > 64 ? 64 : k) : 0u;
    }();

    void NoteJobBatchTableHash(uint32_t key, int length, int tiles, uint32_t applied, uint32_t geom, uint32_t span) noexcept
    {
        if (!g_jobBatchTableDump || key == 0) return;
        constexpr uint32_t kSeenCap = kJobBatchTableCap * 2;
        static std::atomic<uint32_t> s_seenCount{ 0 };
        static uint32_t s_seen[kSeenCap] = {};
        static uint32_t s_hits[kSeenCap] = {};   // 每个键"见到过几次"（trace 用）
        const uint32_t seen = s_seenCount.load(std::memory_order_relaxed);
        const uint32_t lim = seen < kSeenCap ? seen : kSeenCap;
        for (uint32_t i = 0; i < lim; ++i)
        {
            if (s_seen[i] != key) continue;
            if (g_jobTileTrace != 0 && s_hits[i] < g_jobTileTrace)
                std::printf("[JOBBATCHTBL] key=%08x N=%d tiles=%d applied=%u hit=%u geom=%u span=%u\n",
                    key, length, tiles, static_cast<unsigned>(applied), s_hits[i] + 1,
                    static_cast<unsigned>(geom), static_cast<unsigned>(span));
            ++s_hits[i];
            return;
        }
        const uint32_t slot = s_seenCount.fetch_add(1, std::memory_order_relaxed);
        if (slot < kSeenCap)
        {
            s_seen[slot] = key;
            s_hits[slot] = 1;
            // `span=` 让"声明的元素跨度真的被解析到"在日志里可验（静默 no-op 无法与未生效区分）。
            std::printf("[JOBBATCHTBL] key=%08x N=%d tiles=%d applied=%u hit=1 geom=%u span=%u\n",
                key, length, tiles, static_cast<unsigned>(applied),
                static_cast<unsigned>(geom), static_cast<unsigned>(span));
        }
    }

    // JCC/分块决策计数仪器（`ENTJOY_DIAG_JCC=1`，默认关 ⇒ 零开销）。
    // 用途见 JobSystemInternal.h 的同名声明。
    bool g_jccDiagEnabled = []() -> bool {
        const char* v = std::getenv("ENTJOY_DIAG_JCC");
        const bool on = v != nullptr && v[0] == '1';
        if (on) EJ_LOADBANNER( "[JCCDIAG] on (per-path counts + chunk/worker histograms)\n");
        return on;
    }();

    // Guided（chunk ∝ 剩余工作量）tile 调度（OpenMP schedule(guided) 同族）。0=off；>0=on。
    // on 时 chunk = max(floor, ceil(remaining/(W*k)))，头部大块、尾部小块（钳 straggler 上界）。由 JobSystem_ConfigureGuided 设置。
    std::atomic<int> g_guidedEnabled{ 0 };
    std::atomic<int> g_guidedK{ 2 };
    std::atomic<int> g_guidedFloor{ 16 };

    std::mutex g_statePoolMutex;
    std::vector<HandleState*> g_statePool;

    thread_local ThreadStateCache t_stateCache;
    thread_local bool t_stateCreator = false;

    void FlushStateCacheToSharedPool()
    {
        if (t_stateCache.entries.empty()) return;
        std::lock_guard<std::mutex> lock(g_statePoolMutex);
        for (auto* s : t_stateCache.entries)
        {
            if (g_statePool.size() < kMaxPooledStates)
                g_statePool.push_back(s);
            else
                delete s;
        }
        t_stateCache.entries.clear();
    }

    // Stats — all counters restored
    std::atomic<uint64_t> g_completeWaitLoops{ 0 };
    std::atomic<uint64_t> g_assistAttempts{ 0 };
    std::atomic<uint64_t> g_assistExecuted{ 0 };
    std::atomic<uint64_t> g_frameTasksSubmitted{ 0 };
    std::atomic<uint64_t> g_workerExecutedRanges{ 0 };
    std::atomic<uint64_t> g_mainExecutedRanges{ 0 };
    std::atomic<uint64_t> g_stealCount{ 0 };
    std::atomic<uint64_t> g_parkWakeCount{ 0 };
std::atomic<uint64_t> g_notifySkipped{ 0 };   // 跳过广播次数（自证）
    std::atomic<uint64_t> g_hotSpinHits{ 0 };
    // 连续 tile 融合统计（见 JobSystemInternal.h）
    // 等宽 tile / 每批快路径统计（见 JobSystemInternal.h）
    std::atomic<uint64_t> g_uniformTilesApplied{ 0 };
    std::atomic<uint64_t> g_tileFastApplied{ 0 };
    // 认领几何声明分桶（见 JobSystemInternal.h）
    std::atomic<uint64_t> g_claimGeomDeclSpread{ 0 };
    std::atomic<uint64_t> g_claimGeomDeclAdjacent{ 0 };
    std::atomic<uint64_t> g_claimGeomDeclAuto{ 0 };

    // ── 每-job 分母计数（见 JobSystemInternal.h 的声明处说明）──
    std::atomic<uint32_t> g_perKeyHash[JobSystem::kPerKeySlots];
    std::atomic<uint32_t> g_perKeyCount{ 0 };
    std::atomic<uint64_t> g_perKeyBatches[JobSystem::kPerKeySlots];
    std::atomic<uint64_t> g_perKeyElems[JobSystem::kPerKeySlots];
    std::atomic<uint64_t> g_perKeyTiles[JobSystem::kPerKeySlots];
    std::atomic<uint64_t> g_perKeyThin[JobSystem::kPerKeySlots];   // 本键落进 thinTiles 的批数
    std::atomic<uint64_t> g_unkeyedBatches[JobSystem::kUnkeyedReasons];
    std::atomic<uint64_t> g_unkeyedThin[JobSystem::kUnkeyedReasons];
    std::atomic<uint64_t> g_unkeyedLenBucket[JobSystem::kUnkeyedReasons][32];
    std::atomic<uint64_t> g_perKeyCalls[JobSystem::kPerKeySlots];
    std::atomic<uint64_t> g_perKeyElemsCalled[JobSystem::kPerKeySlots];
    std::atomic<uint64_t> g_perKeyNsSamples[JobSystem::kPerKeySlots];
    std::atomic<uint64_t> g_perKeyNsSum[JobSystem::kPerKeySlots];
    std::atomic<uint64_t> g_perKeyNsMax[JobSystem::kPerKeySlots];

    int JobPerKeyIndexFor(uint32_t key) noexcept
    {
        if (key == 0) return -1;
        const uint32_t n = g_perKeyCount.load(std::memory_order_acquire);
        for (uint32_t i = 0; i < n; ++i)
            if (g_perKeyHash[i].load(std::memory_order_relaxed) == key) return static_cast<int>(i);
        // 首次插入：慢路径加锁 + 再查一次（避免并发重复插入同一 key ⇒ 同一内核被拆成两行）
        static std::mutex s_insMtx;
        std::lock_guard<std::mutex> lock(s_insMtx);
        const uint32_t n2 = g_perKeyCount.load(std::memory_order_relaxed);
        for (uint32_t i = 0; i < n2; ++i)
            if (g_perKeyHash[i].load(std::memory_order_relaxed) == key) return static_cast<int>(i);
        if (n2 >= JobSystem::kPerKeySlots) return -1;
        g_perKeyHash[n2].store(key, std::memory_order_relaxed);
        g_perKeyCount.store(n2 + 1, std::memory_order_release);
        return static_cast<int>(n2);
    }

    int JobPerKeyResolve(void (*fn)() noexcept, int& outUnkeyedReason) noexcept
    {
        // 诊断默认关 ⇒ 产品路径零开销（不调用 JobFuncKey、不查表，也不记任何未归因账）。
        if (!g_jobBatchTableDump) { outUnkeyedReason = -1; return -1; }
        if (fn == nullptr) { outUnkeyedReason = kUnkeyedFnNull; return -1; }
        const uint32_t key = JobFuncKey(fn);
        if (key == 0) { outUnkeyedReason = kUnkeyedKeyZero; return -1; }
        const int idx = JobPerKeyIndexFor(key);
        if (idx < 0) { outUnkeyedReason = kUnkeyedTableFull; return -1; }
        outUnkeyedReason = -1;
        return idx;
    }

    static const char* UnkeyedReasonName(int reason) noexcept
    {
        switch (reason)
        {
        case kUnkeyedFnNull:    return "fnNull";
        case kUnkeyedKeyZero:   return "keyZero";
        case kUnkeyedTableFull: return "tableFull";
        default:                return "?";
        }
    }

    void JobPerKeyDump() noexcept    {
        if (!g_jobBatchTableDump) return;
        const uint32_t n = g_perKeyCount.load(std::memory_order_acquire);
        for (uint32_t i = 0; i < n; ++i)
        {
            const uint64_t calls = g_perKeyCalls[i].load(std::memory_order_relaxed);
            const uint64_t elemsScheduled = g_perKeyElems[i].load(std::memory_order_relaxed);
            const uint64_t elemsCalled = g_perKeyElemsCalled[i].load(std::memory_order_relaxed);
            const uint64_t nsSamples = g_perKeyNsSamples[i].load(std::memory_order_relaxed);
            const uint64_t nsSum = g_perKeyNsSum[i].load(std::memory_order_relaxed);
            const double nsPerCall = nsSamples ? (static_cast<double>(nsSum) / static_cast<double>(nsSamples)) : 0.0;
            const double elemsPerCall = calls ? (static_cast<double>(elemsCalled) / static_cast<double>(calls)) : 0.0;
            const double nsPerElem = elemsPerCall > 0.0 ? (nsPerCall / elemsPerCall) : 0.0;
            std::printf("[JOBPERKEY] key=%08x batches=%llu elems=%llu tiles=%llu calls=%llu"
                        " elemsCalled=%llu elemPerCall=%.1f kernelNs/call=%.0f kernelNs/elem=%.2f"
                        " maxCallUs=%.1f nsSamples=%llu mismatch=%lld thin=%llu\n",
                g_perKeyHash[i].load(std::memory_order_relaxed),
                (unsigned long long)g_perKeyBatches[i].load(std::memory_order_relaxed),
                (unsigned long long)elemsScheduled,
                (unsigned long long)g_perKeyTiles[i].load(std::memory_order_relaxed),
                (unsigned long long)calls,
                (unsigned long long)elemsCalled,
                elemsPerCall, nsPerCall, nsPerElem,
                static_cast<double>(g_perKeyNsMax[i].load(std::memory_order_relaxed)) / 1000.0,
                (unsigned long long)nsSamples,
                (long long)elemsCalled - (long long)elemsScheduled,
                (unsigned long long)g_perKeyThin[i].load(std::memory_order_relaxed));
        }
        // 未归因批：按原因打印（只打非零项）+ 该原因的长度主桶，用来指名"谁在产生未归因的批"。
        for (int r = 0; r < kUnkeyedReasons; ++r)
        {
            const uint64_t b = g_unkeyedBatches[r].load(std::memory_order_relaxed);
            if (b == 0) continue;
            int domBucket = 0;
            uint64_t domN = 0;
            for (int k = 0; k < 32; ++k)
            {
                const uint64_t c = g_unkeyedLenBucket[r][k].load(std::memory_order_relaxed);
                if (c > domN) { domN = c; domBucket = k; }
            }
            std::printf("[JOBPERKEY-UNKEYED] reason=%s batches=%llu thin=%llu"
                        " dominantLen=2^%d (n=%llu)\n",
                UnkeyedReasonName(r),
                (unsigned long long)b,
                (unsigned long long)g_unkeyedThin[r].load(std::memory_order_relaxed),
                domBucket, (unsigned long long)domN);
        }
        std::fflush(stdout);
    }
    // 认领几何学习统计（见 JobCostCache.h 的声明处注释）
    std::atomic<uint64_t> g_claimGeomNoKey{ 0 };
    std::atomic<uint64_t> g_claimGeomNoSample{ 0 };
    std::atomic<uint64_t> g_claimGeomSliced{ 0 };
    std::atomic<uint64_t> g_claimGeomInterleaved{ 0 };
    std::atomic<uint64_t> g_claimGeomFlips{ 0 };
    std::atomic<uint64_t> g_wakePollSkips{ 0 };   // 提交侧"不写唤醒字"的次数（自证快路径被走到）
    std::atomic<uint64_t> g_wakePollWakes{ 0 };   // 提交侧真的 bump+notify_all 的次数
    std::atomic<uint64_t> g_wakePollSkipsWork{ 0 };   // 同上，分入口（小 job 快路径）
    std::atomic<uint64_t> g_wakePollWakesWork{ 0 };
    std::atomic<uint64_t> g_wakePollSkipsBatch{ 0 };  // 同上，分入口（真并行趟）
    std::atomic<uint64_t> g_wakePollWakesBatch{ 0 };
    std::atomic<uint64_t> g_publishedJobs{ 0 };
    std::atomic<uint64_t> g_waitFallbacks{ 0 };
    std::atomic<uint64_t> g_notifiedWorkers{ 0 };
    std::atomic<uint64_t> g_workerClaimedTokens{ 0 };
    std::atomic<uint64_t> g_mainClaimedTokens{ 0 };
    std::atomic<uint64_t> g_activeWorkersPeak{ 0 };
    std::atomic<uint64_t> g_activeWorkers{ 0 };
    std::atomic<uint64_t> g_workerTargetTotal{ 0 };
    std::atomic<uint64_t> g_totalTilesPublished{ 0 };
    std::atomic<uint64_t> g_localTiles{ 0 };
    std::atomic<uint64_t> g_stolenTiles{ 0 };
    std::atomic<uint64_t> g_assistTiles{ 0 };
    std::atomic<uint64_t> g_stealAttempts{ 0 };
    std::atomic<uint64_t> g_stealSuccesses{ 0 };
    std::atomic<uint64_t> g_victimScans{ 0 };
    std::atomic<uint64_t> g_stealEmptyExits{ 0 };
    // 观测开关（`ENTJOY_CLAIM_STAT=1`，默认关）：认领点 rdtsc 探针。认领是 fetch_add（无 CAS 失败），
    // 争用只表现为共享游标 cacheline 的弹跳 ⇒ 直接 rdtsc 包住 fetch_add。
    // 只在开关打开时取时间戳；每令牌 flush 一次（不新增原子热路径）。
    std::atomic<bool>     g_claimStatEnabled{ []() -> bool {
        const char* v = std::getenv("ENTJOY_CLAIM_STAT");
        const bool on = v != nullptr && v[0] == '1';
        if (on) EJ_LOADBANNER(
            "[CLAIMSTAT] on (rdtsc probe around the tile-claim fetch_add; print needs ENTJOY_DIAG_E1=1)\n");
        return on;
    }() };
    std::atomic<uint64_t> g_claimProbeN{ 0 };
    std::atomic<uint64_t> g_claimProbeCycles{ 0 };
    std::atomic<uint64_t> g_claimProbeMax{ 0 };
    std::atomic<uint64_t> g_batchStorageCreated{ 0 };
    std::atomic<uint64_t> g_batchStorageReused{ 0 };
    std::atomic<uint64_t> g_statePoolHit{ 0 };
    std::atomic<uint64_t> g_statePoolRefill{ 0 };
    std::atomic<uint64_t> g_statePoolNew{ 0 };
    std::atomic<uint64_t> g_stateRecycled{ 0 };
    std::atomic<uint64_t> g_stateRecycledOnWorker{ 0 };
    std::atomic<int64_t> g_liveHandleStates{ 0 };
    std::atomic<uint64_t> g_stateCreateByThread[kStateThreadSlots];
    std::atomic<uint64_t> g_stateRecycleByThread[kStateThreadSlots];
    std::atomic<uint64_t> g_batchStorageReturned{ 0 };
    std::atomic<uint64_t> g_batchStorageDropped{ 0 };
    std::atomic<uint64_t> g_submitToFirstWorkerEwmaNs{ 0 };
    std::atomic<uint64_t> g_workerStartSpreadEwmaNs{ 0 };
    std::atomic<uint64_t> g_lastTileToTopologyDoneEwmaNs{ 0 };
    std::atomic<uint64_t> g_completeWakeToReturnEwmaNs{ 0 };
    std::atomic<uint64_t> g_nativeBatches{ 0 };
    std::atomic<uint64_t> g_invalidBackendSelections{ 0 };
    std::atomic<int64_t> g_wakeLatencyEwmaNs{ 300'000 };
    std::atomic<uint64_t> g_publishToCompletionEwmaNs{ 0 };
    std::atomic<uint64_t> g_perRangeExecEwmaNs{ 0 };
    std::atomic<uint64_t> g_nextDiagnosticBatchId{ 0 };
    std::atomic<bool> g_shuttingDown{ false };
    std::thread::id g_mainThreadId{};
    std::atomic<bool> g_timingDiagnosticsEnabled{ false };
    // 主线程 assist 开关（Controller API 可运行时切换）。默认关闭（纯 worker 模式）：
    // 释放主线程参与竞争；慢 worker 被 OS 抢占导致尾延迟时，可运行时开启兜底。
    std::atomic<bool> g_mainThreadAssistEnabled{ false };

    // 线程局部"当前 batch"回调。C# 初始化时注册一次；每次 job 执行窗口入口
    // 调 cb(batchId)、出口 cb(0)，托管异常按此绑定到具体 batch。
    std::atomic<void (*)(uint64_t)> g_currentBatchIdCallback{ nullptr };
    void RegisterCurrentBatchIdCallback(void (*cb)(uint64_t)) noexcept
    {
        g_currentBatchIdCallback.store(cb, std::memory_order_release);
    }

    // GUI Activity 用的原生发布事件（调试面板开启时记录）。
    // ⚠ 必须有界：面板长期开着（或忘记 clear）时事件向量会单调增长——几百 job/帧约 1MB/s。
    // 超上限时丢弃最旧的一半，并用 base 维持读取侧的"绝对索引"语义。
    std::atomic<bool> g_nativeActivityCaptureEnabled{ false };
    static std::mutex g_nativeActivityMutex;
    static std::vector<NativeActivityEvent> g_nativeActivity;
    static size_t g_nativeActivityTotal = 0;   // 累计写入数（含被丢弃者）
    static size_t g_nativeActivityBase = 0;    // 已丢弃的最旧事件数：绝对索引 = base + i
    static constexpr size_t kNativeActivityCap = 65536;

    static double NativeNowMs() noexcept
    {
        using namespace std::chrono;
        return duration_cast<duration<double, std::milli>>(
            steady_clock::now().time_since_epoch()).count();
    }

    void RecordPublishedJob(uint64_t batchId, uint32_t tiles) noexcept
    {
        if (!g_nativeActivityCaptureEnabled.load(std::memory_order_relaxed)) return;
        std::lock_guard<std::mutex> lock(g_nativeActivityMutex);
        if (g_nativeActivity.size() >= kNativeActivityCap)
        {
            const size_t drop = kNativeActivityCap / 2;
            g_nativeActivity.erase(g_nativeActivity.begin(),
                g_nativeActivity.begin() + static_cast<std::ptrdiff_t>(drop));
            g_nativeActivityBase += drop;
        }
        try
        {
            g_nativeActivity.emplace_back(NativeActivityEvent{ batchId, tiles, NativeNowMs() });
            ++g_nativeActivityTotal;
        }
        catch (...)
        {
            // 诊断路径：分配失败不得穿透 noexcept（否则 terminate），静默丢弃本次事件。
        }
    }

    // GUI 从 readIndex（绝对索引，跨"丢弃最旧一半"仍然有效）起读取新增事件。返回读取条数。
    int ConsumePublishedJobs(NativeActivityEvent* out, int maxCount, uint64_t* readIndex) noexcept
    {
        if (out == nullptr || maxCount <= 0) return 0;
        uint64_t startIdx = readIndex ? *readIndex : 0;
        std::lock_guard<std::mutex> lock(g_nativeActivityMutex);
        const uint64_t base = static_cast<uint64_t>(g_nativeActivityBase);
        if (startIdx < base) startIdx = base;   // 起点已被丢弃 → 从最旧可用处继续，不重放旧事件
        const uint64_t end = base + static_cast<uint64_t>(g_nativeActivity.size());
        if (startIdx >= end)
        {
            if (readIndex) *readIndex = end;
            return 0;
        }
        const size_t n = static_cast<size_t>(std::min<uint64_t>(
            static_cast<uint64_t>(maxCount), end - startIdx));
        const size_t first = static_cast<size_t>(startIdx - base);
        for (size_t i = 0; i < n; ++i) out[i] = g_nativeActivity[first + i];
        if (readIndex) *readIndex = startIdx + n;
        return static_cast<int>(n);
    }

    // 直接调用（ISPC-MT 等方法直跑，不经调度器）：也记入 published 计数与 activity，并维护 id→名字
    static std::mutex g_nativeJobNameMutex;
    static std::unordered_map<uint64_t, std::string> g_nativeJobNameMap;

    // id→名字表：键是单调递增的 batchId、永不删除 ⇒ 长时间采集同样会无界增长。
    // 名字只用于面板显示，故超限整体清空（旧 id 在面板上退化为无名）。
    static constexpr size_t kNativeJobNameCap = 65536;

    /// 调用方必须已持有 g_nativeJobNameMutex。
    static void StoreNativeJobNameNoLock(uint64_t id, const char* name) noexcept
    {
        if (name == nullptr) return;
        try
        {
            if (g_nativeJobNameMap.size() >= kNativeJobNameCap)
                g_nativeJobNameMap.clear();
            g_nativeJobNameMap[id] = name;
        }
        catch (...)
        {
            // 诊断路径：分配失败不得影响作业执行
        }
    }

    void ClearPublishedJobs() noexcept
    {
        {
            std::lock_guard<std::mutex> lock(g_nativeActivityMutex);
            g_nativeActivity.clear();
            g_nativeActivityTotal = 0;
            g_nativeActivityBase = 0;
        }
        {
            // 名字表必须一起清：它与 activity 同源（都以单调 batchId 为键）
            std::lock_guard<std::mutex> lock(g_nativeJobNameMutex);
            g_nativeJobNameMap.clear();
        }
    }

    void RecordDirectCall(const char* jobName, uint32_t tiles) noexcept
    {
        // 直调也是一次"发布"：统一计数口径，使 GUI 的 Published Jobs 与 Activity 事件一一对应。
        // 发布计数由 g_statsEnabled 门控：统计关闭时不做这次 RMW。
        if (StatsEnabled())
            g_publishedJobs.fetch_add(1, std::memory_order_relaxed);
        if (!g_nativeActivityCaptureEnabled.load(std::memory_order_relaxed)) return;
        const uint64_t id = g_nextDiagnosticBatchId.fetch_add(1, std::memory_order_relaxed) + 1;
        if (jobName)
        {
            std::lock_guard<std::mutex> lock(g_nativeJobNameMutex);
            StoreNativeJobNameNoLock(id, jobName);
        }
        RecordPublishedJob(id, tiles);
    }

    // ISPC MT 任务挂钩（tasksys.cpp 调用）：每个任务在自己的 ConcRT 线程上执行，分配到保留的高位泳道。
    // 注意：g_ispcLaneNext 须为文件级共享宿主（函数级 static 会各自独立，导致分配计数读不到）。
    static std::atomic<int> g_ispcLaneNext{ kIspcLaneBase };

    uint64_t DebugIspcTaskBegin(const char* name) noexcept
    {
        if (!g_nativeActivityCaptureEnabled.load(std::memory_order_relaxed)) return 0;
        // 每个 ISPC 工作线程分配一条高位泳道（跨线程稳定）
        thread_local int t_ispcLane = -1;
        if (t_ispcLane < 0)
        {
            const int lane = g_ispcLaneNext.fetch_add(1, std::memory_order_relaxed);
            if (lane >= kMaxTrackedWorkers)
                return 0; // 泳道耗尽，放弃记录
            t_ispcLane = lane;
            WorkerIndexManager::SetCurrentIndex(t_ispcLane); // DebugBeginExec 用同一条泳道
        }
        const uint64_t id = g_nextDiagnosticBatchId.fetch_add(1, std::memory_order_relaxed) + 1;
        if (name)
        {
            std::lock_guard<std::mutex> lock(g_nativeJobNameMutex);
            const std::string ispcName = std::string("[ISPC]") + name;
            StoreNativeJobNameNoLock(id, ispcName.c_str());
        }
        DebugBeginExec(id, 1, 1, true); // isDirect=true，复用直调样式
        return id;
    }

    void DebugIspcTaskEnd(uint64_t id) noexcept
    {
        if (id == 0) return;
        DebugEndExec();
    }

    int DebugIspcLaneCount() noexcept
    {
        const int used = g_ispcLaneNext.load(std::memory_order_relaxed) - kIspcLaneBase;
        return used < 0 ? 0 : (used > 16 ? 16 : used);
    }

    uint64_t BeginDirectCall(const char* jobName, uint32_t tiles) noexcept
    {
        // 直调执行窗口开始：发布计数 + 记 Activity + 开当前线程泳道窗口（事件驱动）。
        // isDirect=true：GUI 将直调标记为 [D]，与调度式 Job 区分（直调不经调度器）。
        // 发布计数由 g_statsEnabled 门控（同 RecordDirectCall）。
        if (StatsEnabled())
            g_publishedJobs.fetch_add(1, std::memory_order_relaxed);
        if (!g_nativeActivityCaptureEnabled.load(std::memory_order_relaxed)) return 0;
        const uint64_t id = g_nextDiagnosticBatchId.fetch_add(1, std::memory_order_relaxed) + 1;
        if (jobName)
        {
            std::lock_guard<std::mutex> lock(g_nativeJobNameMutex);
            StoreNativeJobNameNoLock(id, jobName);
        }
        RecordPublishedJob(id, tiles);
        const uint32_t workers = tiles > 0 ? tiles : 1u; // 直调并行度 ≈ tile 数（MT 用 CPU 数）
        DebugBeginExec(id, tiles, workers, true);
        return id;
    }

    void EndDirectCall(uint64_t id) noexcept
    {
        if (id == 0) return; // Begin 未推窗口（采集未开）时不可弹栈
        DebugEndExec();
    }

    int ResolveNativeJobName(uint64_t batchId, char* buf, int bufLen) noexcept
    {
        if (buf == nullptr || bufLen <= 0) return 0;
        std::lock_guard<std::mutex> lock(g_nativeJobNameMutex);
        auto it = g_nativeJobNameMap.find(batchId);
        if (it == g_nativeJobNameMap.end()) return 0;
        const int n = std::min<int>(bufLen - 1, static_cast<int>(it->second.size()));
        memcpy(buf, it->second.data(), static_cast<size_t>(n));
        buf[n] = 0;
        return n;
    }
    // 给非 batch 快速路径 job 分配诊断 id（batch 路径由 SubmitBatch 从
    // batch->diagnosticId 设置），保证 Complete(h) 按 id 抛对应异常。
    uint64_t AssignStateDiagnosticId(HandleState* state) noexcept
    {
        const uint64_t id = g_nextDiagnosticBatchId.fetch_add(1, std::memory_order_relaxed) + 1;
        if (state) state->diagnosticBatchId.store(id, std::memory_order_relaxed);
        return id;
    }

    constexpr size_t kBatchTimingSampleCapacity = 2048;

    std::mutex g_batchTimingMutex;
    std::array<BatchTimingSample, kBatchTimingSampleCapacity> g_batchTimingSamples{};
    size_t g_batchTimingSampleCount{ 0 };
    uint64_t g_batchTimingSamplesDropped{ 0 };
    BatchTimingSample g_slowestBatch{};

    void RecordBatchTiming(const BatchTimingSample& sample) noexcept
    {
        std::lock_guard<std::mutex> lock(g_batchTimingMutex);
        if (g_batchTimingSampleCount < g_batchTimingSamples.size())
            g_batchTimingSamples[g_batchTimingSampleCount++] = sample;
        else
            ++g_batchTimingSamplesDropped;

        if (sample.batchTotalNs >= g_slowestBatch.batchTotalNs)
            g_slowestBatch = sample;
    }

    template <typename Selector>
    static void PopulateTimingPercentiles(
        Selector selector,
        uint64_t& p50,
        uint64_t& p95,
        uint64_t& p99,
        uint64_t& maximum)
    {
        if (g_batchTimingSampleCount == 0) return;
        std::vector<uint64_t> values;
        values.reserve(g_batchTimingSampleCount);
        for (size_t i = 0; i < g_batchTimingSampleCount; ++i)
            values.push_back(selector(g_batchTimingSamples[i]));
        std::sort(values.begin(), values.end());

        const size_t last = values.size() - 1;
        const auto percentileIndex = [last](size_t percentile) {
            return (last * percentile + 99) / 100;
        };
        p50 = values[percentileIndex(50)];
        p95 = values[percentileIndex(95)];
        p99 = values[percentileIndex(99)];
        maximum = values.back();
    }

    static void PopulateBatchTimingSnapshot(JobSystemStatsSnapshot* stats) noexcept
    {
        try
        {
            std::lock_guard<std::mutex> lock(g_batchTimingMutex);
            stats->timingSampleCount = static_cast<uint64_t>(g_batchTimingSampleCount);
            stats->timingSamplesDropped = g_batchTimingSamplesDropped;
            PopulateTimingPercentiles(
                [](const BatchTimingSample& sample) { return sample.batchTotalNs; },
                stats->batchTotalP50Ns, stats->batchTotalP95Ns,
                stats->batchTotalP99Ns, stats->batchTotalMaxNs);
            PopulateTimingPercentiles(
                [](const BatchTimingSample& sample) { return sample.submitToFirstWorkerNs; },
                stats->submitToFirstWorkerP50Ns, stats->submitToFirstWorkerP95Ns,
                stats->submitToFirstWorkerP99Ns, stats->submitToFirstWorkerMaxNs);
            PopulateTimingPercentiles(
                [](const BatchTimingSample& sample) { return sample.workerStartSpreadNs; },
                stats->workerStartSpreadP50Ns, stats->workerStartSpreadP95Ns,
                stats->workerStartSpreadP99Ns, stats->workerStartSpreadMaxNs);
            PopulateTimingPercentiles(
                [](const BatchTimingSample& sample) { return sample.executionSpanNs; },
                stats->executionSpanP50Ns, stats->executionSpanP95Ns,
                stats->executionSpanP99Ns, stats->executionSpanMaxNs);
            PopulateTimingPercentiles(
                [](const BatchTimingSample& sample) { return sample.maxRangeNs; },
                stats->maxRangeP50Ns, stats->maxRangeP95Ns,
                stats->maxRangeP99Ns, stats->maxRangeMaxNs);

            stats->slowBatchId = g_slowestBatch.batchId;
            stats->slowBatchTotalNs = g_slowestBatch.batchTotalNs;
            stats->slowSubmitToFirstWorkerNs = g_slowestBatch.submitToFirstWorkerNs;
            stats->slowWorkerStartSpreadNs = g_slowestBatch.workerStartSpreadNs;
            stats->slowExecutionSpanNs = g_slowestBatch.executionSpanNs;
            stats->slowMaxRangeNs = g_slowestBatch.maxRangeNs;
            stats->slowRangeThreadCpuNs = g_slowestBatch.slowRangeThreadCpuNs;
            stats->slowRangeThreadCycles = g_slowestBatch.slowRangeThreadCycles;
            stats->slowBatchMinRangeThreadCycles = g_slowestBatch.minRangeThreadCycles;
            stats->slowBatchAverageRangeThreadCycles = g_slowestBatch.averageRangeThreadCycles;
            stats->slowCoreMigrations = g_slowestBatch.coreMigrations;
            stats->slowAssistTiles = g_slowestBatch.assistTiles;
            stats->slowRangeIndex = g_slowestBatch.slowRangeIndex;
            stats->slowRangeWorker = g_slowestBatch.slowRangeWorker;
            stats->slowRangeStartLogicalCore = g_slowestBatch.slowRangeStartLogicalCore;
            stats->slowRangeEndLogicalCore = g_slowestBatch.slowRangeEndLogicalCore;
            stats->slowRangeStartPhysicalCore = g_slowestBatch.slowRangeStartPhysicalCore;
            stats->slowRangeEndPhysicalCore = g_slowestBatch.slowRangeEndPhysicalCore;
        }
        catch (...)
        {
            // Stats collection must never affect job completion.
        }
    }

    void UpdateUnsignedEwma(std::atomic<uint64_t>& target, uint64_t sample) noexcept
    {
        if (sample == 0) return;
        uint64_t current = target.load(std::memory_order_relaxed);
        while (true)
        {
            uint64_t next = current == 0
                ? sample
                : (sample >= current
                    ? current + (sample - current) / 8
                    : current - (current - sample) / 8);
            if (target.compare_exchange_weak(current, next, std::memory_order_relaxed)) return;
        }
    }

    uint64_t MonotonicNowNs() noexcept
    {
        return static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(
            std::chrono::steady_clock::now().time_since_epoch()).count());
    }

    int CurrentProcessorIndexForDiagnostics() noexcept
    {
#if defined(_WIN32) && defined(_MSC_VER) && (defined(_M_IX86) || defined(_M_X64))
        PROCESSOR_NUMBER processor{};
        ::GetCurrentProcessorNumberEx(&processor);
        return static_cast<int>(processor.Group) * 64 + static_cast<int>(processor.Number);
#elif defined(__linux__)
        return ::sched_getcpu();
#else
        return -1;
#endif
    }

    uint64_t CurrentThreadCpuTimeNsForDiagnostics() noexcept
    {
#if defined(_WIN32)
        FILETIME creation{}, exit{}, kernel{}, user{};
        if (!::GetThreadTimes(::GetCurrentThread(), &creation, &exit, &kernel, &user))
            return 0;
        ULARGE_INTEGER kernelTime{}, userTime{};
        kernelTime.LowPart = kernel.dwLowDateTime;
        kernelTime.HighPart = kernel.dwHighDateTime;
        userTime.LowPart = user.dwLowDateTime;
        userTime.HighPart = user.dwHighDateTime;
        return (kernelTime.QuadPart + userTime.QuadPart) * 100ull;
#elif defined(__linux__)
        timespec value{};
        if (::clock_gettime(CLOCK_THREAD_CPUTIME_ID, &value) != 0) return 0;
        return static_cast<uint64_t>(value.tv_sec) * 1'000'000'000ull +
            static_cast<uint64_t>(value.tv_nsec);
#else
        return 0;
#endif
    }

    uint64_t CurrentThreadCyclesForDiagnostics() noexcept
    {
#if defined(_WIN32)
        ULONG64 cycles = 0;
        return ::QueryThreadCycleTime(::GetCurrentThread(), &cycles)
            ? static_cast<uint64_t>(cycles) : 0;
#else
        return 0;
#endif
    }

    int PhysicalCoreIndexForDiagnostics(int logicalCore) noexcept
    {
#if defined(_WIN32)
        constexpr size_t kLogicalCoreMapCapacity = 4096;
        static const auto logicalToPhysical = []() noexcept {
            std::array<int, kLogicalCoreMapCapacity> result{};
            result.fill(-1);
            DWORD bytes = 0;
            (void)::GetLogicalProcessorInformationEx(RelationProcessorCore, nullptr, &bytes);
            if (bytes == 0) return result;
            auto* buffer = static_cast<unsigned char*>(std::malloc(bytes));
            if (!buffer) return result;
            if (!::GetLogicalProcessorInformationEx(
                RelationProcessorCore,
                reinterpret_cast<PSYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(buffer),
                &bytes))
            {
                std::free(buffer);
                return result;
            }

            DWORD offset = 0;
            int physicalCore = 0;
            while (offset < bytes)
            {
                auto* info = reinterpret_cast<PSYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(
                    buffer + offset);
                if (info->Relationship == RelationProcessorCore)
                {
                    const auto& processor = info->Processor;
                    for (WORD groupIndex = 0; groupIndex < processor.GroupCount; ++groupIndex)
                    {
                        const GROUP_AFFINITY& affinity = processor.GroupMask[groupIndex];
                        for (int bit = 0; bit < 64; ++bit)
                        {
                            if ((affinity.Mask & (static_cast<KAFFINITY>(1) << bit)) == 0)
                                continue;
                            const int index = static_cast<int>(affinity.Group) * 64 + bit;
                            if (index >= 0 && static_cast<size_t>(index) < result.size())
                                result[static_cast<size_t>(index)] = physicalCore;
                        }
                    }
                    ++physicalCore;
                }
                if (info->Size == 0) break;
                offset += info->Size;
            }
            std::free(buffer);
            return result;
        }();
        return logicalCore >= 0 && static_cast<size_t>(logicalCore) < logicalToPhysical.size()
            ? logicalToPhysical[static_cast<size_t>(logicalCore)] : -1;
#else
        (void)logicalCore;
        return -1;
#endif
    }

    // 本机物理核数（SMT 兄弟共享一个物理核）。在 Scheduler::Initialize 计算一次并缓存：
    // 每 job 调用时再查会太贵；一次性的查询失败绝不能变成进程级永久失效。
    // 0 = 不可知 ⇒ 依赖它的策略保守退化。
    std::atomic<int> g_physicalCores{ 0 };
    std::atomic<uint64_t> g_physCapApplied{ 0 };

    void RefreshPhysicalCoreCount() noexcept
    {
        int maxIndex = -1;
        const int probe = std::max(1, g_numThreads.load(std::memory_order_relaxed)) * 2 + 64;
        for (int i = 0; i < probe; ++i)
        {
            const int pc = PhysicalCoreIndexForDiagnostics(i);
            if (pc > maxIndex) maxIndex = pc;
        }
        g_physicalCores.store(maxIndex + 1, std::memory_order_relaxed);
    }

    int PhysicalCoreCountForDiagnostics() noexcept
    {
        return g_physicalCores.load(std::memory_order_relaxed);
    }

    static void WaitForBackendBatches() noexcept;

    void GetStatsSnapshot(JobSystemStatsSnapshot* stats) noexcept
    {
        if (!stats) return;
        WaitForBackendBatches();
        stats->completeWaitLoops = g_completeWaitLoops.load(std::memory_order_relaxed);
        stats->assistAttempts = g_assistAttempts.load(std::memory_order_relaxed);
        stats->assistExecuted = g_assistExecuted.load(std::memory_order_relaxed);
        stats->frameTasksSubmitted = g_frameTasksSubmitted.load(std::memory_order_relaxed);
        stats->workerExecutedRanges = g_workerExecutedRanges.load(std::memory_order_relaxed);
        stats->mainExecutedRanges = g_mainExecutedRanges.load(std::memory_order_relaxed);
        stats->stealCount = g_stealCount.load(std::memory_order_relaxed);
        // parkWake/hotSpin 由 Chase-Lev 统计（自增点在 ChaseLevScheduler::WorkerLoop 的 park/自旋出口）。
        stats->parkWakeCount = g_parkWakeCount.load(std::memory_order_relaxed);
        stats->hotSpinHits = g_hotSpinHits.load(std::memory_order_relaxed);
        stats->publishedJobs = g_publishedJobs.load(std::memory_order_relaxed);
        stats->waitFallbacks = g_waitFallbacks.load(std::memory_order_relaxed);
        stats->notifiedWorkers = g_notifiedWorkers.load(std::memory_order_relaxed);
        stats->workerClaimedTokens = g_workerClaimedTokens.load(std::memory_order_relaxed);
        stats->mainClaimedTokens = g_mainClaimedTokens.load(std::memory_order_relaxed);
        stats->activeWorkersPeak = g_activeWorkersPeak.load(std::memory_order_relaxed);
        stats->wakeLatencyEwmaNs = static_cast<uint64_t>(
            g_wakeLatencyEwmaNs.load(std::memory_order_relaxed));
        stats->publishToCompletionEwmaNs = g_publishToCompletionEwmaNs.load(std::memory_order_relaxed);
        stats->perRangeExecEwmaNs = g_perRangeExecEwmaNs.load(std::memory_order_relaxed);
        stats->workerTargetTotal = g_workerTargetTotal.load(std::memory_order_relaxed);
        stats->totalTilesPublished = g_totalTilesPublished.load(std::memory_order_relaxed);
        stats->localTiles = g_localTiles.load(std::memory_order_relaxed);
        stats->stolenTiles = g_stolenTiles.load(std::memory_order_relaxed);
        stats->assistTiles = g_assistTiles.load(std::memory_order_relaxed);
        stats->stealAttempts = g_stealAttempts.load(std::memory_order_relaxed);
        stats->stealSuccesses = g_stealSuccesses.load(std::memory_order_relaxed);
        stats->permitsReleased = 0;
        stats->victimScans = g_victimScans.load(std::memory_order_relaxed);
        stats->stealEmptyExits = g_stealEmptyExits.load(std::memory_order_relaxed);
        stats->batchStorageCreated = g_batchStorageCreated.load(std::memory_order_relaxed);
        stats->batchStorageReused = g_batchStorageReused.load(std::memory_order_relaxed);
        stats->batchStorageReturned = g_batchStorageReturned.load(std::memory_order_relaxed);
        stats->batchStorageDropped = g_batchStorageDropped.load(std::memory_order_relaxed);
        stats->submitToFirstWorkerEwmaNs = g_submitToFirstWorkerEwmaNs.load(std::memory_order_relaxed);
        stats->workerStartSpreadEwmaNs = g_workerStartSpreadEwmaNs.load(std::memory_order_relaxed);
        stats->lastTileToTopologyDoneEwmaNs = g_lastTileToTopologyDoneEwmaNs.load(std::memory_order_relaxed);
        stats->completeWakeToReturnEwmaNs = g_completeWakeToReturnEwmaNs.load(std::memory_order_relaxed);
        stats->nativeBatches = g_nativeBatches.load(std::memory_order_relaxed);
        stats->invalidBackendSelections = g_invalidBackendSelections.load(std::memory_order_relaxed);
        PopulateBatchTimingSnapshot(stats);

        const uint64_t workerTiles =
            g_workerExecutedRanges.load(std::memory_order_relaxed);
        const uint64_t assistTiles =
            g_mainExecutedRanges.load(std::memory_order_relaxed);
        const uint64_t totalTiles = workerTiles + assistTiles;
        stats->assistExecPctEwma = totalTiles > 0
            ? (assistTiles * 100 / totalTiles)
            : 0;

        uint64_t compUs = stats->publishToCompletionEwmaNs / 1000;
        uint64_t perUs = stats->perRangeExecEwmaNs / 1000;
        stats->completionOverheadUs = compUs > perUs ? compUs - perUs : 0;

        stats->frameTasksCompleted = 0;
        stats->deferredRuns = 0;
        stats->prewakeCount = 0;
        stats->coldBatches = 0;
        stats->scheduleModePublishNoAssist = 0;
        stats->scheduleModePublishAssist = 0;
        stats->scheduleModeDeferTinyOnly = 0;
        stats->scheduleModeImmediateNative = 0;
        stats->scheduleModeDeferredPublish = 0;
        stats->scheduleModeDeferredPublishNoAssist = 0;
        stats->frameQueueDepthPeak = 0;
    }

    std::atomic<uint32_t> g_backendBatchesOutstanding{ 0 };

    // 代次校验诊断（定义；声明见 JobSystemInternal.h）。
    // 只在冷子分支自增（拒绝迟到结算 / pendingTasks 已为 0），热路径零开销；
    // Shutdown 时打 `[JOBGEN]` 一行。
    std::atomic<uint64_t> g_staleSettleDropped{ 0 };
    std::atomic<uint64_t> g_pendingTasksWrap{ 0 };

    static void WaitForBackendBatches() noexcept
    {
        uint32_t outstanding =
            g_backendBatchesOutstanding.load(std::memory_order_acquire);
        while (outstanding != 0)
        {
            g_backendBatchesOutstanding.wait(
                outstanding, std::memory_order_relaxed);
            outstanding =
                g_backendBatchesOutstanding.load(std::memory_order_acquire);
        }
    }

    // 排空所有在飞批。刻意不关 worker（Shutdown 才是终态）。
    // 用的是与 ResetStatsSnapshot 读统计前同一段序列。
    void DrainAll() noexcept
    {
        ConsumeLongBatchBarriers();
        WaitForBackendBatches();
    }

    void ResetStatsSnapshot() noexcept
    {
        ConsumeLongBatchBarriers();
        WaitForBackendBatches();
        g_completeWaitLoops.store(0, std::memory_order_relaxed);
        g_assistAttempts.store(0, std::memory_order_relaxed);
        g_assistExecuted.store(0, std::memory_order_relaxed);
        g_frameTasksSubmitted.store(0, std::memory_order_relaxed);
        g_workerExecutedRanges.store(0, std::memory_order_relaxed);
        g_mainExecutedRanges.store(0, std::memory_order_relaxed);
        g_stealCount.store(0, std::memory_order_relaxed);
        g_parkWakeCount.store(0, std::memory_order_relaxed);
        g_hotSpinHits.store(0, std::memory_order_relaxed);
        // 每-job 分母计数：整表清空（含 key 列），否则上一代的 key 会与新 key 混行
        {
            const uint32_t n = g_perKeyCount.load(std::memory_order_relaxed);
            for (uint32_t i = 0; i < n && i < JobSystem::kPerKeySlots; ++i)
            {
                g_perKeyHash[i].store(0, std::memory_order_relaxed);
                g_perKeyBatches[i].store(0, std::memory_order_relaxed);
                g_perKeyElems[i].store(0, std::memory_order_relaxed);
                g_perKeyTiles[i].store(0, std::memory_order_relaxed);
                g_perKeyThin[i].store(0, std::memory_order_relaxed);
                g_perKeyCalls[i].store(0, std::memory_order_relaxed);
                g_perKeyElemsCalled[i].store(0, std::memory_order_relaxed);
                g_perKeyNsSamples[i].store(0, std::memory_order_relaxed);
                g_perKeyNsSum[i].store(0, std::memory_order_relaxed);
                g_perKeyNsMax[i].store(0, std::memory_order_relaxed);
            }
            g_perKeyCount.store(0, std::memory_order_relaxed);
        }
        // 未归因批的分原因计数与长度直方图（与逐键计数同时清空）
        for (int r = 0; r < JobSystem::kUnkeyedReasons; ++r)
        {
            g_unkeyedBatches[r].store(0, std::memory_order_relaxed);
            g_unkeyedThin[r].store(0, std::memory_order_relaxed);
            for (int k = 0; k < 32; ++k)
                g_unkeyedLenBucket[r][k].store(0, std::memory_order_relaxed);
        }
        g_uniformTilesApplied.store(0, std::memory_order_relaxed);
        g_tileFastApplied.store(0, std::memory_order_relaxed);
        g_claimGeomDeclSpread.store(0, std::memory_order_relaxed);
        g_claimGeomDeclAdjacent.store(0, std::memory_order_relaxed);
        g_claimGeomDeclAuto.store(0, std::memory_order_relaxed);
        g_claimGeomNoKey.store(0, std::memory_order_relaxed);
        g_claimGeomNoSample.store(0, std::memory_order_relaxed);
        g_claimGeomSliced.store(0, std::memory_order_relaxed);
        g_claimGeomInterleaved.store(0, std::memory_order_relaxed);
        g_claimGeomFlips.store(0, std::memory_order_relaxed);
        g_wakePollSkips.store(0, std::memory_order_relaxed);
        g_wakePollWakes.store(0, std::memory_order_relaxed);
        g_wakePollSkipsWork.store(0, std::memory_order_relaxed);
        g_wakePollWakesWork.store(0, std::memory_order_relaxed);
        g_wakePollSkipsBatch.store(0, std::memory_order_relaxed);
        g_wakePollWakesBatch.store(0, std::memory_order_relaxed);
        g_publishedJobs.store(0, std::memory_order_relaxed);
        g_waitFallbacks.store(0, std::memory_order_relaxed);
        g_notifiedWorkers.store(0, std::memory_order_relaxed);
        g_workerClaimedTokens.store(0, std::memory_order_relaxed);
        g_mainClaimedTokens.store(0, std::memory_order_relaxed);
        g_activeWorkersPeak.store(0, std::memory_order_relaxed);
        g_activeWorkers.store(0, std::memory_order_relaxed);
        g_workerTargetTotal.store(0, std::memory_order_relaxed);
        g_totalTilesPublished.store(0, std::memory_order_relaxed);
        g_localTiles.store(0, std::memory_order_relaxed);
        g_stolenTiles.store(0, std::memory_order_relaxed);
        g_assistTiles.store(0, std::memory_order_relaxed);
        g_stealAttempts.store(0, std::memory_order_relaxed);
        g_stealSuccesses.store(0, std::memory_order_relaxed);
        g_victimScans.store(0, std::memory_order_relaxed);
        g_stealEmptyExits.store(0, std::memory_order_relaxed);
        g_claimProbeN.store(0, std::memory_order_relaxed);
        g_claimProbeCycles.store(0, std::memory_order_relaxed);
        g_claimProbeMax.store(0, std::memory_order_relaxed);
        g_batchStorageCreated.store(0, std::memory_order_relaxed);
        g_batchStorageReused.store(0, std::memory_order_relaxed);
        g_batchStorageReturned.store(0, std::memory_order_relaxed);
        g_batchStorageDropped.store(0, std::memory_order_relaxed);
        g_submitToFirstWorkerEwmaNs.store(0, std::memory_order_relaxed);
        g_workerStartSpreadEwmaNs.store(0, std::memory_order_relaxed);
        g_lastTileToTopologyDoneEwmaNs.store(0, std::memory_order_relaxed);
        g_completeWakeToReturnEwmaNs.store(0, std::memory_order_relaxed);
        g_nativeBatches.store(0, std::memory_order_relaxed);
        g_invalidBackendSelections.store(0, std::memory_order_relaxed);
        g_publishToCompletionEwmaNs.store(0, std::memory_order_relaxed);
        g_perRangeExecEwmaNs.store(0, std::memory_order_relaxed);
        {
            std::lock_guard<std::mutex> lock(g_batchTimingMutex);
            g_batchTimingSampleCount = 0;
            g_batchTimingSamplesDropped = 0;
            g_slowestBatch = {};
        }
    }

    void SetTimingDiagnosticsEnabled(bool enabled) noexcept
    {
        g_timingDiagnosticsEnabled.store(enabled, std::memory_order_release);
    }

    int CurrentWorkerCount()
    {
        return std::max(1, g_numThreads.load(std::memory_order_relaxed));
    }

} // namespace JobSystem
