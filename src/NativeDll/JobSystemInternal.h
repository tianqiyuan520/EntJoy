#pragma once

// EntJoy JobSystem 内部共享头：承载跨模块的类型定义、extern 全局与函数原型，
// 使各 TU 可独立编译。
//
// 文件布局：
//   JobSystem.cpp            —— base：全局定义 + 统计快照 + 时钟/CPU 诊断助手
//   JobSystem_State.cpp      —— State：HandleState 生命周期 + 依赖链 + JobHandle
//   JobSystem_Tiles.cpp      —— Tiles：ExecutionTile/BatchState/BatchStorage + 执行循环
//   JobSystem_Scheduler.cpp  —— Scheduler：适配器 + Schedule 系列 + IJob* 调度入口

#include "JobSystem.h"
#include "ChunkJobData.h"
#include "EntityBatchData.h"
#include "JobCostCache.h"
#include "JobProfiler.h"
#include "SparseTileDeque.h"

#include <array>
#include <exception>
#include <limits>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <functional>
#include <limits>
#include <memory>
#include <mutex>
#include <thread>
#include <vector>

// 加载期 banner（见下方 LoadBannerAppend 的说明）：把 `std::fprintf(stderr, ...)` 换成这个宏。
#define EJ_LOADBANNER(...) ::JobSystem::LoadBannerAppend(__VA_ARGS__)

namespace JobSystem
{
    class ChaseLevScheduler; // forward declare

    // ---- 加载期 banner 缓冲 ----
    // DLL 的全局动态初始化跑在 `_CRT_INIT`（= DllMain 期，持有 loader lock）里，那条路径上
    // 不允许做 I/O：stderr 可能是父进程的管道，锁内写管道会阻塞；而加载期任何一步失败都会
    // 整体表现为"DLL 载不进来"，而不是"某一行日志没打出来"。
    // ⇒ 加载期的所有 banner 改为追加进内存缓冲，由 `JobSystem_Initialize()` 一次性 flush。
    void LoadBannerAppend(const char* fmt, ...) noexcept;
    void LoadBannerFlush() noexcept;

    // ---- 跨模块常量（inline 保证 ODR，各 TU 一份） ----
    // 共享池容量上限：超出即直接 delete，不再囤积。
    inline constexpr size_t kMaxPooledStates = 4096;
    inline constexpr size_t kMaxPooledBatchStorage = 256;

    // per-thread state 缓存上限。命中零锁；满额批量迁移共享池。
    // state 单 owner（refCount==0 才回池），跨线程迁移只发生在共享池锁内，无 ABA。
    inline constexpr size_t kStateCacheCap = 64;
    // 非"创建者"线程（worker：每 job 都回收、但几乎从不 CreateState）的 TLS 缓存上限刻意更小：
    // 否则回收的 state 会滞留在这类线程手里，而真正需要复用它们的调度/提交线程从共享池里拿不到
    // ⇒ CreateState 退回 `new`。
    inline constexpr size_t kStateCacheCapNonCreator = 8;

    inline constexpr uint64_t kLongBatchBarrierNs = 800'000;

    // 并行 for 默认 tiles/worker（batchSize=0 时 ResolveChunkSize 使用）。
    // tpw 是"每个 worker 的 tile 数"，但真正决定墙钟的是 tile 的元素大小：
    //   tile 越小 → 尾部越平，但每-tile 固定开销占比越高；64 是各 worker 数下的折中点。
    // 作用域：只影响 flat parallel-for 路径（ECS chunk/entity-batch 路径自带
    //   `JobSystem_Tiles.cpp` 的 kTargetTilesPerWorker，不受本值影响）。
    // 可覆盖：`JobSystem_ConfigureTilesPerWorker()` / `NativeJobScheduler.TilesPerWorker` /
    //   环境变量 `ENTJOY_TILES_PER_WORKER`（env 优先）。
    inline constexpr int kDefaultTilesPerWorker = 64;

    // ceil(a/b)，a>=0, b>0；用 (a-1)/b+1 避免 a+b-1 在 a 接近类型上限时的 signed overflow（UB）。
    template <typename T>
    inline T CeilDiv(T a, T b) noexcept
    {
        if (a <= T(0)) return T(0);
        return (a - T(1)) / b + T(1);
    }

    // per-thread state 缓存：定义在本头使 t_stateCache 可跨 TU extern（State 模块直接读写）。
    // t_stateCreator：本线程是否调用过 CreateState。回收只在"自己会再造"的线程进 TLS 缓存；
    // 其它线程（.NET 终结器线程、渲染线程等）直接还共享池——否则回收全堆在那些线程的缓存里，
    // 调度线程永远命中不到、每次 CreateState 都走 `new`。
    extern thread_local bool t_stateCreator;
    // 析构时批量交还共享池；g_statePoolMutex 在 Shutdown 中始终存活（本对象先于其初始化，按标准后销毁），线程退出取锁安全。
    extern std::mutex g_statePoolMutex;
    extern std::vector<HandleState*> g_statePool;
    struct ThreadStateCache
    {
        std::vector<HandleState*> entries;
        ~ThreadStateCache()
        {
            if (entries.empty()) return;
            std::lock_guard<std::mutex> lock(g_statePoolMutex);
            for (auto* s : entries)
            {
                if (g_statePool.size() < kMaxPooledStates)
                    g_statePool.push_back(s);
                else
                    delete s;
            }
            entries.clear();
        }
    };

    // ---- 调试面板 per-worker 实时状态 ----
    inline constexpr int kMaxTrackedWorkers = 64;
    // 每 worker 独占一个 cache line 的原子计数器：避免相邻 worker 共享 cache line 造成
    // false sharing（相邻 worker 每次自增都让对方那行失效）。`sizeof` 断言保证真的独占一行。
    template <typename T>
    struct alignas(64) PaddedAtomic : public std::atomic<T>
    {
        PaddedAtomic() noexcept : std::atomic<T>(T{}) {}
    };
    static_assert(sizeof(PaddedAtomic<uint64_t>) == 64, "per-worker counter must own a cache line");
    static_assert(sizeof(PaddedAtomic<uint32_t>) == 64, "per-worker counter must own a cache line");
    static_assert(sizeof(PaddedAtomic<bool>) == 64, "per-worker counter must own a cache line");
    extern PaddedAtomic<uint64_t> g_workerCurrentBatchId[kMaxTrackedWorkers];
    extern PaddedAtomic<uint32_t> g_workerCurrentTile[kMaxTrackedWorkers];
    extern PaddedAtomic<uint32_t> g_workerBatchTileCount[kMaxTrackedWorkers];
    extern PaddedAtomic<bool>     g_workerIsActive[kMaxTrackedWorkers];

    // ---- base 模块（JobSystem.cpp）定义的全局 ----
    extern std::atomic<bool> g_workerAffinityEnabled;
    extern std::mutex g_schedulerMutex;
    extern std::shared_ptr<ChaseLevScheduler> g_chaseLevScheduler;
    // 发布伴生裸指针（release 发布），使热路径退化为一次 acquire 载入（只读缓存行，
    // 仅 Initialize/Shutdown 写）——无锁、无 RMW、无引用计数弹跳；取代
    // `atomic_load(shared_ptr)` 每次调用的 CAS 加锁/解锁。
    //
    // 生命周期安全（无需读者计数即可证明无 UAF）：调度器实例进程内唯一且永不析构
    //（`g_chaseLevSchedulerInstance`，见 JobSystem.cpp）。Initialize 复用同一实例（只 Start），
    // Shutdown 只 Stop 不销毁，因此任何线程一旦取得过该指针，其解引用在进程生命周期内永远有效。
    // Shutdown 先把本裸指针清空再 Stop，使"之后"的新读者拿到 nullptr —— 调用方一律
    // `if (scheduler) ...` 判空，且关键路径再叠加 `IsRunning()`。
    extern std::shared_ptr<ChaseLevScheduler> g_chaseLevSchedulerInstance;
    extern std::atomic<ChaseLevScheduler*> g_chaseLevSchedulerRaw;
    inline ChaseLevScheduler* LoadChaseLevScheduler() noexcept
    {
        return g_chaseLevSchedulerRaw.load(std::memory_order_acquire);
    }
    extern std::atomic<int> g_numThreads;
    extern std::atomic<int> g_configuredTilesPerWorker;
    extern std::atomic<int> g_guidedEnabled;
    extern std::atomic<int> g_guidedK;
    extern std::atomic<int> g_guidedFloor;
    extern std::atomic<bool> g_mainThreadAssistEnabled;  // 主线程 assist 开关（默认 false，由 API 控制）
    // JobCostCache export flag：用户显式启用（C# JobSystem_SetJobCostCacheEnabled(1)）后，
    // worker 按 per-job 每元素成本 EWMA 自动求解最优 tile 数；热路径 relaxed 读取足够（延迟生效无害）。
    extern std::atomic<bool> g_jobCostCacheEnabled;
    // 提交期"延迟唤醒"深度：>0 时 SubmitBatch 跳过末尾 notify_all，由显式 Flush 在提交窗口结束时统一唤醒。
    // 安全：任务已入注入器，worker 自旋自取；全 park 时由 Flush 的 notify_all 唤醒。
    extern std::atomic<int> g_submitDeferDepth;
    // 隐式批（native 收集）：开启时主线程直接提交的 tile 路径 job 挂入 pending，
    // 由 FlushPendingSubmits（EndFrame/Complete 自动触发）统一提交 + 单次唤醒；
    // 依赖未完成路径（continuation）不受 pending 影响，照常立即提交。
    extern std::atomic<bool> g_implicitBatchEnabled;
    extern std::mutex g_pendingBatchesMutex;
    extern std::vector<BatchState*> g_pendingBatches;
    // 诊断：ENTJOY_JCC_VERBOSE=1 时打印 ResolveChunkSize 决策 + 退役学习快照。
    // 只在 flag 开启时读取；进程启动时从 env 初始化一次，之后只读 → 无竞态。
    extern bool g_jobCostCacheVerbose;
    // 认领粒子上限（`ENTJOY_CLAIM_BATCH=<n>`，0/未设 = 用内置 kClaimBatchSize=4）。
    // `batch->nextTile.fetch_add` 是同一条 cacheline 上的 contended RMW（核间来回迁移）；
    // 把每次认领覆盖的 tile 数放大即可线性摊薄它，而 `step = clamp(tileCount/workers, 1, cap)`
    // 里的 `tileCount/workers` 项保证小批次自动退回细粒度（tileCount ≤ cap×workers 时与 cap 无关）。
    // 语义：cap 同时管首次块与 guided 收缩的上界。
    extern uint32_t g_claimBatchSize;
    // 按元素跨度认领（`ENTJOY_CLAIM_SPAN=<元素数>`，0/未设 = 关；内置默认 1024）。
    // 语义：`itemsPerTile = totalElements/tileCount`；仅当 `itemsPerTile <= kClaimSpanThinElems` 时
    // `capEff = clamp(SPAN/itemsPerTile, cap, SPAN)`，否则 `capEff = cap`（= 旧行为）。
    // 目的：薄 tile（≈1 元素/tile）需要摊薄认领，厚 tile 需要 worker 空间邻近（抬 cap 反而更差）
    // ⇒ 用"元素跨度恒定"一条规则同时满足，且默认档（全是厚 tile）逐位不变。
    // ⚠ 本常量恰好等于 `ResolveChunkSize` 五处 `std::max(16, …)` 的硬编码下限 ⇒ 任何以
    //   `cs <= kClaimSpanThinElems` 为形的判据（F2/F4 的 `thinTiles` 就是）实际等价于
    //   "JCC 顶在下限上" = "这次调度的 length 够短"，与 tile 厚薄无关；且"进不进这个 regime"
    //   本身不稳定（同一 len 既可见恒薄，也可见不薄）。
    // ⚠ 改本常量会同时移动 F1（认领跨度）与 F2/F4（thickness 门）两条轴的阈值，不是局部改动。
    static constexpr uint32_t kClaimSpanThinElems = 16;
    // `ENTJOY_CLAIM_SPAN` 的量程上限；只约束实验/回退用的 env，内置默认仍是已验收的 1024。
    static constexpr uint32_t kClaimSpanElemsMax = 16384;
    extern uint32_t g_claimSpanElems;
    // F2（默认开；`ENTJOY_TILES_UNIFORM=0` 关闭）：等宽 GeneralRange 不物化 tileBuffer。
    // 只在"非 guided 的 General 路"生效；chunk/entity/packed/guided 一律不走这里。
    extern bool g_uniformTilesEnabled;
    // F4（默认开；`ENTJOY_TILE_FASTPATH=0` 关闭）：把每-tile 的固定开销提到每批。
    // 见 BatchState 里 traceOn/timingOn/tileFast/firstTileAt 的注释。
    extern bool g_tileFastPath;
    // F6（默认开；`ENTJOY_CLAIM_ADAPT=0` 关闭）：per-job 认领几何学习。
    // 依据：静态几何对 job 是相反符号 —— 散开对共享计数器争用的 job 有利、对靠空间复用的 job 有害
    // ⇒ 只能按 job 学（详见 JobCostCache.h 的 ClaimMode 段）。
    extern bool g_claimAdaptiveEnabled;
    // 目标每 tile 串行量（µs）。`ENTJOY_JCC_TARGET_US=<n>` 可运行期覆盖（同一构建多臂 A/B）。
    extern double g_jccTargetTileUs;
    // 强制显式内批（`ENTJOY_FORCE_INNER_BATCH=<n>`，0/未设 = 关）。
    // 语义：把所有 `batchSize=0` 的 flat parallel-for 派发强制成显式内批 n，并跳过 JCC
    // （funcHash 置 0 ⇒ 不查成本缓存、不学习），即与"调用方显式传 batch"完全同一条路径。
    // 用途：做"与 Unity `Schedule(n,64)` 完全同粒度、且排除自适应/学习影响"的跨栈对照。
    extern uint32_t g_forceInnerBatch;
    // 按 job 的内批档表（`ENTJOY_JOB_BATCH_TABLE="<key>:<n>[,<key>:<n>...]"`，默认空 = 关）。
    // `<key>` = 内核函数在其所属模块内的 RVA（8 位十六进制），由 `JobFuncKey()` 算出 —— 不用
    // `HashFuncPtr`（那是对指针值做哈希，受 ASLR 影响 ⇒ 同一构建下键会整组变）。
    // 语义：只在 auto（batchSize<=0）时生效 —— 命中 ⇒ 等价于调用方显式传该内批（跳过 JCC、
    // funcHash 置 0、不学习）；未命中 ⇒ 回落到 `ENTJOY_FORCE_INNER_BATCH`，再回落 JCC；
    // 显式 batchSize>0 完全不受影响。
    // 制表：`ENTJOY_JOB_BATCH_TABLE_DUMP=1` ⇒ 每个首见键打一行
    // `[JOBBATCHTBL] key=... N=... tiles=... applied=...`（走 stdout，不在 Godot 的 --log-file 里）；
    // 键→job 名可用 `dumpbin /exports NativeTranspiled.dll` 对齐（导出名即 `SharpNative_Job_<类型>_...`）。
    // ⚠ 只是制表时的做法：日常只用 `ENTJOY_JOB_BATCH_BY_NAME="<job类型名>:<n>"` 即可 ——
    //   那条路径不经过本表，也不需要知道任何 RVA/导出名（见下方 `BindJobBatchName`）。
    static constexpr uint32_t kJobBatchTableCap = 32;
    // D1：认领跨度成为调用点声明（通解形态）。
    // 依据：真正的自变量是每次内核调用的元素数（两条独立轴都塌缩到它，最优 ≈1024–2048），
    // 而它跟 tile 厚薄无关 —— F1 的薄-tile 门会把"cs=64 的 Integrate/Count"关在门外，
    // 使它们退回内置 cap=4（= 256 元素/次），这就是它们剩下的赤字来源。
    // 形态：把跨度交回调用点声明（键 = 调用点，不按 job 名特判、不改 batch、不动内批镜像）。
    //   · 声明值 = 元素数（不是 tile 数）⇒ 与内批解耦：cs 变了语义不变；
    //   · 与 F1 的薄-tile 门无关（声明即生效），但同样只在"等宽 GeneralRange"这条已验收路径上；
    //   · 未声明（0）⇒ 走 F1 旧规则 ⇒ 逐位不变。
    static constexpr uint32_t kClaimSpanDeclaredMax = 1u << 20;
    // 认领几何取值（表项第四字段；也是将来 `ClaimPolicy` 参数的值域）。
    static constexpr uint32_t kClaimGeomAuto     = 0;   // 缺省：调用点声明 / F6 学习
    static constexpr uint32_t kClaimGeomSpread   = 1;   // 每 worker 独占连续段、空手才窃取
    static constexpr uint32_t kClaimGeomAdjacent = 2;   // 共享游标发相邻窗口（Melee 靠它吃空间复用）
    struct JobBatchTableEntry { uint32_t key; uint32_t batch; uint32_t claim; uint32_t geom; uint32_t span; };
    extern JobBatchTableEntry g_jobBatchTable[kJobBatchTableCap];
    extern uint32_t g_jobBatchTableCount;
    extern bool g_jobBatchTableDump;
    /// 把"按 job 名登记"的批表槽位绑定到该 job 实际派发用的函数指针。
    /// 由托管侧生成绑定在静态构造期逐个 job 调用（`NativeJobScheduler.BindNativeJobBatchName`）——
    /// 名字 = 托管 `Type.Name`，指针 = 托管真正交给调度器的那个指针 ⇒ 与符号命名规则无关。
    /// 必须在任何派发之前完成（早于读侧 ⇒ 表对读侧只读，无竞态/数据竞争）。返回 1 = 名字在表里。
    int BindJobBatchName(const char* name, void* func) noexcept;
    /// 首次真正查表时打一行"按名槽位"的对账（纯诊断；并发调用最多多打一行，不阻塞任何人）。
    void ReportJobBatchNames() noexcept;
    uint32_t LookupJobBatch(uint32_t key) noexcept;
    // 表项第三字段的 `<n>` 形态 = 该 job 的认领上限覆盖（单位 = tile）；0 = 不覆盖。
    // 动机：逐趟证据定位 Build 赤字主要在 `count`，机制 = 认领窗口造成的跨核原子争用；
    //   要量"只给计数/放置类批放大认领、Melee 保持小认领"的收益，就必须有按 job的旋钮。
    // 表为空时 `claim` 恒 0 ⇒ 逐位不变。
    uint32_t LookupJobClaim(uint32_t key) noexcept;
    // 表项第三字段的 `e<N>` 形态 —— 元素跨度（单位 = 元素）；0 = 未声明。
    uint32_t LookupJobSpan(uint32_t key) noexcept;
    void NoteJobBatchTableHash(uint32_t key, int length, int tiles, uint32_t applied, uint32_t geom = 0, uint32_t span = 0) noexcept;

    // 表项第四字段 = 该调用点的认领几何（`<key>:<batch>[:<claim>][:s|a]`，缺省 = Auto）。
    // 意义：`count`/`place` 这类"每元素共享原子 RMW"的便宜 job 要散开（Spread），
    //   `Melee` 这类"邻居表/空间复用"的贵 job 要邻近（Adjacent）——同一条静态几何对二者反号，
    //   所以在调用点声明，而不是靠按名字特判或全局一刀切。
    uint32_t LookupJobGeom(uint32_t key) noexcept;
    // 跨进程稳定的 per-job 键：内核函数在其所属模块内的 RVA（拿不到模块基址时退化为指针值低 32 位）。
    // 带 64 项直接映射缓存 ⇒ 只在首见某个内核时进一次加载器，重复 dispatch 为纯内存读。
    uint32_t JobFuncKey(void (*func)() noexcept) noexcept;
    // JCC/分块决策计数仪器（`ENTJOY_DIAG_JCC=1`，默认关）。
    // JCC 的 mode/chunk 由测出来的成本决定 ⇒ 决策本身是运行态相关的、可能逐次运行不同。
    // 本仪器把每个 return 路径的次数 + 返回 chunk 的分布 + 每批 workerCount 打出来，
    // 用来把"模式"与"决策"对齐。默认关 ⇒ 零开销。
    extern bool g_jccDiagEnabled;
    void JccDiagNoteWorkers(uint32_t workers) noexcept;
    void JccDiagMaybeDump() noexcept;
    extern thread_local ThreadStateCache t_stateCache;

    // 统计计数器（base 定义；Tiles 递增 / base GetStatsSnapshot 读取）。
    // ── 诊断统计总开关 ──
    // 热路径只做一次 relaxed 载入 + 分支即可旁路全部纯诊断 RMW；默认 true（数值不变），
    // `ENTJOY_STATS=0` 关闭。同步/账本类原子（g_backendBatchesOutstanding、batch->tilesRemaining、
    // batch->pendingTasks）不属于诊断计数，永不 gate。
    extern std::atomic<bool> g_statsEnabled;
    inline bool StatsEnabled() noexcept
    {
        return g_statsEnabled.load(std::memory_order_relaxed);
    }
    extern std::atomic<uint64_t> g_completeWaitLoops;
    extern std::atomic<uint64_t> g_assistAttempts;
    extern std::atomic<uint64_t> g_assistExecuted;
    extern std::atomic<uint64_t> g_frameTasksSubmitted;
    extern std::atomic<uint64_t> g_workerExecutedRanges;
    extern std::atomic<uint64_t> g_mainExecutedRanges;
    extern std::atomic<uint64_t> g_stealCount;
    // 提交侧跳过 futex 广播的次数（无人等待、或已醒人数已够本批名额）——自证用。
    extern std::atomic<uint64_t> g_notifySkipped;
    extern std::atomic<uint64_t> g_parkWakeCount;
    extern std::atomic<uint64_t> g_hotSpinHits;
    // F2 / F4 的生效证据。两者都受同一条 thinTiles（cs ≤ kClaimSpanThinElems）判据门控。
    // ⚠ 该门等价于"JCC 顶在下限 16 上"，不代表 tile 厚薄 ⇒ 默认档与对齐档都非 0，只差占比。
    //   判据：薄 tile 占比骤降为 0 即为回归告警。
    extern std::atomic<uint64_t> g_uniformTilesApplied;
    extern std::atomic<uint64_t> g_tileFastApplied;

    // ── 认领几何（`ClaimPolicy` / 批表第 4 字段 / F6）的生效证据 ──
    // 按声明值分三桶计数（Auto/Spread/Adjacent），在几何写入批的那一刻累加。
    // 必要性：`[JOBBATCHTBL] geom=` 打的是批表里的值，不是调用点声明带进来的值 ⇒ 两者不能互相替代。
    extern std::atomic<uint64_t> g_claimGeomDeclSpread;
    extern std::atomic<uint64_t> g_claimGeomDeclAdjacent;
    extern std::atomic<uint64_t> g_claimGeomDeclAuto;

    // ── 每-job（按 funcHash）分母计数：回答"这个 pass 是每次调用贵还是每元素贵" ──
    // 宿主的 `[M-19]` 只给每个子趟的 ms、没有分母 ⇒ 无法判断"调用次数多、每次贵"还是
    // "元素多、每元素贵"，而两者对应完全不同的优化方向。
    // 归因安全性：索引在 Schedule 时解析一次并写进批（主线程），worker 只按索引累加 ⇒
    // 无哈希二次查找、无碰撞混行（否则就会重犯"槽位碰撞"那类归因错误）。
    // 一致性判据：`elemsScheduled` 必须等于 `elemsCalled`（不等 = tile 漏执行/重复执行）。
    static constexpr uint32_t kPerKeySlots = 256;
    extern std::atomic<uint32_t> g_perKeyHash[kPerKeySlots];
    extern std::atomic<uint32_t> g_perKeyCount;
    extern std::atomic<uint64_t> g_perKeyBatches[kPerKeySlots];
    extern std::atomic<uint64_t> g_perKeyElems[kPerKeySlots];
    extern std::atomic<uint64_t> g_perKeyTiles[kPerKeySlots];
    // 本键有多少个批落进 `thinTiles`（F2/F4 的门）。读数注意：该门已证明等价于
    // "JCC 顶在下限 16 上 = 这次调度的 length 够短"，所以这一列读出来是"短调度分布"，
    // 不是"这个内核薄"。
    extern std::atomic<uint64_t> g_perKeyThin[kPerKeySlots];
    extern std::atomic<uint64_t> g_perKeyCalls[kPerKeySlots];
    extern std::atomic<uint64_t> g_perKeyElemsCalled[kPerKeySlots];
    // 内核自计时（抽样 1/32 次调用，累计 ns）—— 把"一趟的宿主 ms"拆成"内核真干活"与
    // "框架/宿主侧的每趟固定开销（派发+等待+段外工作）"。没有这一半，无法判断某趟慢
    // 是内核循环慢还是框架开销大。
    extern std::atomic<uint64_t> g_perKeyNsSamples[kPerKeySlots];
    extern std::atomic<uint64_t> g_perKeyNsSum[kPerKeySlots];
    extern std::atomic<uint64_t> g_perKeyNsMax[kPerKeySlots];
    // 取/建本 key 的槽位索引（线性扫描 + 首次插入走互斥）；返回 <0 表示关闭或表满。
    int JobPerKeyIndexFor(uint32_t key) noexcept;
    // 由内核函数指针解析本 job 的槽位索引（`ENTJOY_JOB_BATCH_TABLE_DUMP=1` 才启用 ⇒ 产品路径零开销）。
    // ⚠ 必须用 `JobFuncKey`（= 内核在模块内的 RVA，与批表键同一键空间），不能用 `funcHash`：
    //   后者在"表/强制档 + `ENTJOY_CLAIM_ADAPT=0`"时被有意置 0 ⇒ 用它会让本仪器在需要它的那一档静默失效。
    // 返回 <0 表示未归因；此时 `outUnkeyedReason` 给出原因（kUnkeyed* 枚举），否则置 -1。
    int JobPerKeyResolve(void (*fn)() noexcept, int& outUnkeyedReason) noexcept;
    // 关停时打印（`ENTJOY_JOB_BATCH_TABLE_DUMP=1`，与 [JOBBATCHTBL] 同一个开关）。
    void JobPerKeyDump() noexcept;
    // ── 「无法逐键归因」的批：按原因分桶 ──
    // 必要性：`JobPerKeyResolve` 返回 -1 有四种不同原因（诊断关 / 函数指针为空 / 键算不出 /
    // 槽位表满），原先一律并成 -1 ⇒ 未归因的批既不进逐键行、也无从判断来自哪条通路。
    // 分原因计数 + `log2(length)` 直方图 ⇒ 一眼看出"哪条通路在产生未归因批、长度落在哪个量级"。
    // 只由该诊断开关驱动 ⇒ 默认档零开销、零输出（诊断关时 outUnkeyedReason = -1，不记任何账）。
    enum : int { kUnkeyedFnNull = 0, kUnkeyedKeyZero = 1, kUnkeyedTableFull = 2, kUnkeyedReasons = 3 };
    extern std::atomic<uint64_t> g_unkeyedBatches[kUnkeyedReasons];
    extern std::atomic<uint64_t> g_unkeyedThin[kUnkeyedReasons];
    extern std::atomic<uint64_t> g_unkeyedLenBucket[kUnkeyedReasons][32];
    inline int Log2Bucket(uint64_t n) noexcept
    {
        int b = 0;
        while (n > 1) { n >>= 1; ++b; }
        return b;
    }
    // ── `ENTJOY_WAKE_POLL`（默认开；`=0` 关闭）自证计数 ──
    // g_wakePollSkips：提交侧"没有写任何被轮询的字"的次数（有人正在轮询注入器 ⇒ 它自己会领到）。
    // g_wakePollWakes：进入慢路径（真的 bump epoch + notify_all）的次数。
    // 判定开关是否真的生效靠这两个计数：只有 skip 数随真实负载量级增长，才说明快路径被走到。
    extern std::atomic<uint64_t> g_wakePollSkips;
    extern std::atomic<uint64_t> g_wakePollWakes;
    // 分入口计数：小 job 快路径（`SubmitWork`，每次只需 1 个 worker）与真并行批（`SubmitBatch`，
    // 需要 `batch->workerCount` 个 worker）不能用同一个谓词；分开计数才能在真实宿主上判定
    // "哪条入口在承载负载"——否则无从解释一次整机回归。
    extern std::atomic<uint64_t> g_wakePollSkipsWork;
    extern std::atomic<uint64_t> g_wakePollWakesWork;
    extern std::atomic<uint64_t> g_wakePollSkipsBatch;
    extern std::atomic<uint64_t> g_wakePollWakesBatch;
    extern std::atomic<uint64_t> g_publishedJobs;
    extern std::atomic<uint64_t> g_waitFallbacks;
    extern std::atomic<uint64_t> g_notifiedWorkers;
    extern std::atomic<uint64_t> g_workerClaimedTokens;
    extern std::atomic<uint64_t> g_mainClaimedTokens;
    extern std::atomic<uint64_t> g_activeWorkersPeak;
    extern std::atomic<uint64_t> g_activeWorkers;
    extern std::atomic<uint64_t> g_workerTargetTotal;
    extern std::atomic<uint64_t> g_totalTilesPublished;
    extern std::atomic<uint64_t> g_localTiles;
    extern std::atomic<uint64_t> g_stolenTiles;
    extern std::atomic<uint64_t> g_assistTiles;
    extern std::atomic<uint64_t> g_stealAttempts;
    extern std::atomic<uint64_t> g_stealSuccesses;
    extern std::atomic<uint64_t> g_victimScans;
    extern std::atomic<uint64_t> g_stealEmptyExits;
    // 认领点探针（观测开关 `ENTJOY_CLAIM_STAT=1`，默认关 ⇒ 逐位不变、零开销）：
    // 目的 = 判"认领几何该散还是该聚"。我们的认领是 `fetch_add`（不会有 CAS 失败），
    // 所以争用只能表现为共享游标 cacheline 的弹跳延迟 —— 用 rdtsc 包住 fetch_add 直接量它。
    // 只在开关打开时取时间戳；累加走 per-thread 本地量、每令牌一次写入（不引入新的原子热路径）。
    extern std::atomic<bool>     g_claimStatEnabled;
    extern std::atomic<uint64_t> g_claimProbeN;
    extern std::atomic<uint64_t> g_claimProbeCycles;
    extern std::atomic<uint64_t> g_claimProbeMax;
    extern std::atomic<uint64_t> g_batchStorageCreated;
    extern std::atomic<uint64_t> g_batchStorageReused;
    // State 池命中分布（用于分解 CreateState 的耗时构成）。
    extern std::atomic<uint64_t> g_statePoolHit;
    extern std::atomic<uint64_t> g_statePoolRefill;
    extern std::atomic<uint64_t> g_statePoolNew;
    // 回收侧：RecycleState 总次数 + 其中发生在 worker 线程的比例（worker 的 TLS 缓存会吃掉回收，
    // 使主调度线程的缓存恒空 → 每次都走 new）。
    extern std::atomic<uint64_t> g_stateRecycled;
    extern std::atomic<uint64_t> g_stateRecycledOnWorker;
    // 存活句柄 state 数：CreateState 分配 / RecycleState 回收的差值（即"已借出但未归还"的
    // HandleState 数）。不受 `ENTJOY_STATS=0` 门控——托管侧用它断言"句柄是否被确定性
    // 回收"（见 JobSystem_GetLiveHandleCount），关闭统计时仍须给出真实值。
    // 代价：每次 CreateState/RecycleState 各一次 relaxed RMW（每 job 1 次创建 + N 次回收）。
    extern std::atomic<int64_t> g_liveHandleStates;
    // 按线程槽统计"创建/回收"分布：判定二者是否落在同一批线程上。
    inline constexpr size_t kStateThreadSlots = 8;
    extern std::atomic<uint64_t> g_stateCreateByThread[kStateThreadSlots];
    extern std::atomic<uint64_t> g_stateRecycleByThread[kStateThreadSlots];
    extern std::atomic<uint64_t> g_batchStorageReturned;
    extern std::atomic<uint64_t> g_batchStorageDropped;
    extern std::atomic<uint64_t> g_submitToFirstWorkerEwmaNs;
    extern std::atomic<uint64_t> g_workerStartSpreadEwmaNs;
    extern std::atomic<uint64_t> g_lastTileToTopologyDoneEwmaNs;
    extern std::atomic<uint64_t> g_completeWakeToReturnEwmaNs;
    extern std::atomic<uint64_t> g_nativeBatches;
    extern std::atomic<uint64_t> g_invalidBackendSelections;
    extern std::atomic<int64_t> g_wakeLatencyEwmaNs;
    extern std::atomic<uint64_t> g_publishToCompletionEwmaNs;
    extern std::atomic<uint64_t> g_perRangeExecEwmaNs;
    extern std::atomic<uint64_t> g_nextDiagnosticBatchId;
    extern std::atomic<bool> g_shuttingDown;
    // 主线程 id：Initialize 时记录。Shutdown 用它 + `ChaseLevScheduler_IsWorkerThread()` 判定调用者
    // 是否为本调度器的 worker —— 只有 worker 调 Shutdown 会 join 自身死锁；其余线程（含 ProcessExit）允许。
    extern std::thread::id g_mainThreadId;
    // 当前线程是否为本调度器的 worker 线程（定义在 ChaseLevScheduler.cpp）。
    bool ChaseLevScheduler_IsWorkerThread() noexcept;
    extern std::atomic<bool> g_timingDiagnosticsEnabled;
    extern std::atomic<void (*)(uint64_t)> g_currentBatchIdCallback;
    extern std::atomic<uint32_t> g_backendBatchesOutstanding;
    // 代次校验的可观测性（Shutdown 时打 `[JOBGEN]` 一行），两者都应为 0：
    // staleSettleDropped = 结算时代次不匹配被拒的次数（非 0 ⇒ 批已回收而令牌仍在飞）。
    // pendingTasksWrap = `pendingTasks.fetch_sub(1)` 在已为 0 时返回 0 的次数（非 0 ⇒ 计数回绕）。
    extern std::atomic<uint64_t> g_staleSettleDropped;
    extern std::atomic<uint64_t> g_pendingTasksWrap;

    // ---- 原生发布活动事件（供 GUI Activity 完整记录微秒级 batch；动态保留全部）----
    struct NativeActivityEvent { uint64_t batchId; uint32_t tiles; double timeMs; };
    extern std::atomic<bool> g_nativeActivityCaptureEnabled;
    extern std::atomic<bool> g_debugPaused; // GUI 暂停时停止记录新段，避免环形缓冲覆盖历史
    void RecordPublishedJob(uint64_t batchId, uint32_t tiles) noexcept;
    int ConsumePublishedJobs(NativeActivityEvent* out, int maxCount, uint64_t* readIndex) noexcept;
    void ClearPublishedJobs() noexcept;
    // 直接调用（不经 JobSystem 调度器，如 ISPC-MT 方法直跑）也记录进 activity，并维护 id→名字表
    void RecordDirectCall(const char* jobName, uint32_t tiles) noexcept;
    // 直调执行窗口：Begin 分配 id + 记发布 + 开执行窗口（当前线程泳道），End 关闭窗口
    //（追加共享时间线段）。由 transpiler 包装器在 native 调用前后成对调用。
    uint64_t BeginDirectCall(const char* jobName, uint32_t tiles) noexcept;
    void EndDirectCall(uint64_t id) noexcept;
    int ResolveNativeJobName(uint64_t batchId, char* buf, int bufLen) noexcept;

    // ---- State 模块（JobSystem_State.cpp）定义的全局 ----
    extern std::mutex g_longBatchBarrierMutex;
    extern std::vector<HandleState*> g_longBatchBarriers;
    // `g_longBatchBarriers` 的无锁快照计数。该列表绝大多数提交时为空，
    // 但 ConsumeLongBatchBarriers 此前每次提交都取全局互斥体 + 交换两个 vector。
    // 计数与列表在同一把锁内同增同减，故 0 一定蕴含"列表空"（保守真值，不是提示性估计）；
    // 非 0 只是"可能有"，仍需进锁复核。
    extern std::atomic<uint32_t> g_longBatchBarrierCount;
    extern thread_local HandleState* g_completingBatchState;

    // ---- 跨模块类型 ----

    // Tile 是负载均衡单位 —— 一个或多个 chunk（IJobChunk）或 entity 子区间（IJobEntity）。
    enum class TileKind : uint8_t
    {
        GeneralRange,
        ChunkCallbacks,
        ChunkRange,
        EntityBatchRange,
        // 打包 plain IJob 分片（见 SubmitPackedPlainJobs）：firstItem/itemCount 是
        // PackedPlainBatch::jobs 的下标区间，不走 ChunkBatchContext 预取路径。
        PackedJobs
    };

    struct ExecutionTile {
        uint32_t firstItem;
        uint32_t itemCount;
        TileKind kind;
    };

    struct BatchTimingSample
    {
        uint64_t batchId{ 0 };
        uint64_t batchTotalNs{ 0 };
        uint64_t submitToFirstWorkerNs{ 0 };
        uint64_t workerStartSpreadNs{ 0 };
        uint64_t executionSpanNs{ 0 };
        uint64_t maxRangeNs{ 0 };
        uint64_t slowRangeThreadCpuNs{ 0 };
        uint64_t slowRangeThreadCycles{ 0 };
        uint64_t minRangeThreadCycles{ 0 };
        uint64_t averageRangeThreadCycles{ 0 };
        uint64_t coreMigrations{ 0 };
        uint64_t assistTiles{ 0 };
        int32_t slowRangeIndex{ -1 };
        int32_t slowRangeWorker{ -1 };
        int32_t slowRangeStartLogicalCore{ -1 };
        int32_t slowRangeEndLogicalCore{ -1 };
        int32_t slowRangeStartPhysicalCore{ -1 };
        int32_t slowRangeEndPhysicalCore{ -1 };
    };

    struct BatchState {
        struct BatchStorage* storage{ nullptr };
        HandleState* handle{ nullptr };
        void* context{ nullptr };
        void (*cleanup)(void*){ nullptr };

        // 勿标 noexcept：用户回调可能抛异常，须经 TryExecuteOneTile 的 try/catch 记录，
        // 否则 C++ 直接 terminate。
        bool (*executeTile)(void* ctx, const ExecutionTile& tile){ nullptr };

        // Unified lightweight BatchRange path. Physical ECS chunks remain
        // storage boundaries; tiles are contiguous descriptor/index ranges.
        ExecutionTile* tiles{ nullptr };
        uint32_t tileCount{ 0 };
        // 热计数器各自独占 cache line：三个计数器若同处一行，每次认领 RMW 都会连带失效
        // "入场/完成计数"的行副本 ⇒ 单行反复弹跳。拆开后 RMW 次数不变、跨计数器误失效消失。
        alignas(64) std::atomic<uint32_t> nextTile{ 0 };
        uint32_t workerCount{ 0 };
        alignas(64) std::atomic<uint32_t> workerSlotsEntered{ 0 };
        // 逻辑完成由 tile 完成驱动，而非任务退役：公共 JobHandle 已完成后，
        // 慢 worker 槽可能仍在退出窃取循环。
        alignas(64) std::atomic<uint32_t> tilesRemaining{ 0 };
        std::atomic<bool> logicalCompleted{ false };
        // ---- Chase-Lev：在飞任务计数（防 use-after-free）----
        // SubmitBatch 时 = 任务数，每个任务执行完 fetch_sub(1)。
        // 退役须满足 tilesRemaining==0 && pendingTasks==0：tilesRemaining=0 仅代表 tile 执行完，
        // deque 中可能仍有已 pop 未执行的任务（task.batch 引用本 storage），须等其全部完成才能 ReleaseBatch。
        // 由"最后者"（tile 完成者或 task 完成者）执行退役。
        alignas(64) std::atomic<uint32_t> pendingTasks{ 0 };

        std::atomic<uint64_t> publishedAt{ 0 };
        std::atomic<uint64_t> firstWorkerAt{ 0 };
        std::atomic<uint64_t> lastWorkerAt{ 0 };
        std::atomic<uint64_t> firstTileAt{ 0 };
        std::atomic<uint64_t> lastTileAt{ 0 };
        std::atomic<uint64_t> topologyDoneAt{ 0 };
        std::atomic<uint64_t> maxRangeDurationNs{ 0 };
        std::atomic<uint64_t> minRangeThreadCycles{ (std::numeric_limits<uint64_t>::max)() };
        std::atomic<uint64_t> totalRangeThreadCycles{ 0 };
        std::atomic<uint64_t> measuredRangeThreadCycles{ 0 };
        std::atomic_flag slowRangeLock = ATOMIC_FLAG_INIT;
        uint64_t slowRangeThreadCpuNs{ 0 };
        uint64_t slowRangeThreadCycles{ 0 };
        int32_t slowRangeIndex{ -1 };
        int32_t slowRangeWorker{ -1 };
        int32_t slowRangeStartLogicalCore{ -1 };
        int32_t slowRangeEndLogicalCore{ -1 };
        int32_t slowRangeStartPhysicalCore{ -1 };
        int32_t slowRangeEndPhysicalCore{ -1 };
        std::atomic<uint64_t> coreMigrations{ 0 };
        std::atomic<uint64_t> batchAssistTiles{ 0 };

        // 物理退役与逻辑完成刻意分离：最后回调结束后，worker 槽与 Complete() 协助读取者
        // 仍引用调度器元数据。
        std::atomic<bool> cleanupStarted{ false };
        std::atomic<bool> finalized{ false };
        std::atomic<bool> workersFinished{ false };

        uint64_t diagnosticId{ 0 };

        // ---- JobCostCache：per-job 自动 batch ----
        // funcHash：Schedule 入口设置，退役时按此更新 per-job 每元素成本 EWMA；0 = 未标记（不参与自动 batch）。
        uint32_t funcHash{ 0 };
        // jccFine：本次分块是否由 JCC 公式（细粒度）产出（ResolveChunkSize 设置）。
        // 退役时据此把学习样本归为细/粗（粗 = tpw 兜底/mem-bound/显式 batchSize）。
        bool jccFine{ false };
        // totalElements：Schedule 入口设置（IJobParallelFor 的 length）。
        // 退役时 perElemNs = (topologyDoneAt - publishedAt) / totalElements。
        uint32_t totalElements{ 0 };

        // 每批快照一次的开关（关闭时与逐 tile 判断逐位不变）：traceOn/timingOn 免去每 tile 的全局 atomic
        // 载入 + 分支；tileFast 标记本批是否走快速路径；firstTileAt 由每 tile 的 `load==0` + 可能 CAS 改为
        // 每个认领令牌一次（语义差仅"首个令牌开始" vs "首个 tile 开始"，只影响 JCC execSpan/诊断）。
        bool traceOn{ false };
        bool timingOn{ false };
        bool tileFast{ false };

        // ---- F2（`ENTJOY_TILES_UNIFORM`）：等宽 GeneralRange 不物化 tileBuffer ----
        // >0 ⇒ 本批 tile 等宽且宽 = 该值，`tiles` 可为 nullptr：worker 侧用
        //    `first = tileIndex*uniformTileSize`、`count = min(size, total-first)` 算术推导，
        //    从而消掉提交线程的 O(tileCount) 填表（1e6 tiles = 16 MB）与执行期的 tile 数组读。
        // =0 ⇒ 旧行为（从 `tiles[]` 读）。必须在 AcquireBatchStorage 里复位（storage 池化复用，
        //    否则陈旧值会泄给后续的 chunk/packed 批）。
        uint32_t uniformTileSize{ 0 };

        // 每-job 分母计数的槽位索引（Schedule 时解析一次并写进批；-1 = 未登记/开关关闭）。
        int32_t perKeyIndex{ -1 };

        // ---- 切片认领 + 空手才窃取 ----
        // 根因：`nextTile.fetch_add` 把相邻 tile 交给同时刻的不同 worker，而真实数据在 index 序上
        // 空间连贯 ⇒ 相邻 worker 争同一条 cell 计数器的 cacheline（慢的是共享模式，
        // 不是核函数算术，也不是粒度大小）。
        // 方案：把 tile 空间切成 sliceCount = min(workers, tileCount) 段，每段自带游标：
        //   ① 常态：worker 只碰自己那一段的游标（独占 cacheline）⇒ 零跨核弹跳 + 自己的访问严格顺序；
        //   ② 空手才窃取：自己的段跑干后，才去别的段的游标上偷 ⇒ 尾部均衡不丢。
        //      （"静态大块不可窃取"正是块状几何的失败面；切片把"不可窃取"改成"空手才窃取"补上这一半。）
        // 正确性：每个 tile 只由"所属段的游标"发放一次（fetch_add 唯一递增）⇒ 不会重复执行；
        //   记账仍走 `TileAcctGroupBegin/Flush`（一次 fetch_sub 归零）⇒ tilesRemaining/退役语义不变。
        // 每段游标独占一条 cacheline（避免两个段主互相弹跳 —— 那正是要消除的问题）。
        static constexpr uint32_t kMaxSliceSlots = 32;   // >32 worker 时多出的 worker 走窃取（仍正确）
        struct SliceCursor
        {
            alignas(64) std::atomic<uint32_t> next{ 0 };
        };
        alignas(64) SliceCursor sliceCursors[kMaxSliceSlots];
        uint32_t sliceEnd[kMaxSliceSlots]{ 0 };          // 初始化后只读
        uint32_t sliceCount{ 0 };                        // 0 = 未启用切片认领
        alignas(64) std::atomic<uint32_t> sliceTaken{ 0 };
        // 按 job 的认领上限覆盖（0 = 用全局 claimCap）。
        // ⚠ 必须在 `AcquireBatchStorage` 里清零：BatchStorage 会被复用 ⇒ 否则继承陈旧值（与 F1 同款坑）。
        uint32_t claimCapOverride{ 0 };
        // D1：调用点声明的元素跨度（0 = 未声明 ⇒ 走 F1 旧规则 ⇒ 逐位不变）。
        // 语义：`capEff = clamp(claimSpanOverride / itemsPerTile, 1, tileCount)` —— 与 tile 厚薄无关
        // （F1 的 `itemsPerTile <= kClaimSpanThinElems` 门只约束全局规则那条腿）。单位为元素，
        // 因此与内批解耦：tile 大小变了、每次内核调用的元素数不变。同样必须在 `AcquireBatchStorage`
        // 里清零（BatchStorage 池化复用 ⇒ 否则下一个批会继承别的调用点声明的跨度）。
        uint32_t claimSpanOverride{ 0 };
        // 按调用点声明的认领几何（`kClaimGeom*`；0 = Auto ⇒ 全局 env / 学习值）。同样必须在
        // `AcquireBatchStorage` 里清零（复用陈旧值会让"没声明的 job"拿到别人的几何）。
        uint32_t claimGeomOverride{ 0 };
    };

    struct BatchStorage
    {
        BatchState batch;
        ExecutionTile* tileBuffer{ nullptr };
        uint32_t tileCapacity{ 0 };
        // 池化代次（对应 Unity `AtomicSafetyHandle` 的中心记录版本号）。
        // 归还池时 ++（`ReleaseBatchStorage`），令牌在创建时把当时的代次抄一份在自己身上
        // （`RangeTask::batchGen` / `TileTask::batchGen`），结算时比对；不等 ⇒ 上一代的迟到结算
        // ⇒ 丢弃（不碰 pendingTasks、不触发退役、不减 outstanding）。
        // ⚠ 必须放 BatchStorage，不能放 BatchState：`ReleaseBatchStorage` 对 `storage->batch`
        //   做 `destroy_at` + placement new 整体重建，放 BatchState 里会在"正好该前进"的
        //   那一刻被清零 ⇒ 失效检测恒真。且 `batch` 是本结构第一个成员、`batch.storage` 恒指回
        //   自己（构造/Acquire/Release 三处），故结算侧 `batch->storage->generation` 即可取到。
        std::atomic<uint32_t> generation{ 0 };

        BatchStorage() noexcept { batch.storage = this; }
        ~BatchStorage()
        {
            delete[] tileBuffer;
        }
    };

    struct ChunkBatchContext {
        void (*func)(void*, const ChunkJobData*);
        void (*rangeFunc)(void*, const ChunkJobData*, int, int);
        void (*entityRangeFunc)(void*, const EntityBatchData*, int, int);
        void* originalContext;
        void (*originalCleanup)(void*);
        const ChunkJobData* chunks;
        const EntityBatchData* entityBatches;
    };

    struct GeneralBatchContext {
        void (*indexFunc)(void*, int);
        void (*batchFunc)(void*, int, int);
        void* originalContext;
        void (*originalCleanup)(void*);
        // JobCostCache：Schedule 入口设置的 funcPtr hash（0 = 未标记）。
        uint32_t funcHash{ 0 };
        // 每-job 分母计数的槽位索引（Schedule 时由 funcHash 解析一次；-1 = 未登记）
        int32_t perKeyIndex{ -1 };
    };

    // ---- 打包 plain IJob 描述符（SubmitPackedPlainJobs 的输入） ----
    // 与 Exports.h 的 JobFunc / ContextCleanupFunc 是同一底层类型（void(*)(void*)），
    // 但此处刻意不复用导出头，保持内部头不依赖 C ABI 头。
    struct PackedPlainJobDesc {
        void (*func)(void*);
        void* context;
        void (*cleanup)(void*);
    };
    // 把 K 个无依赖 plain IJob 描述符当作一个 batch 提交：
    // 每个 worker token 连续执行若干 tile，每 tile = 一段连续描述符（func → 该 job 的 cleanup），
    // 每 job 各自发布其 HandleState 终态。
    // 返回写入 outStates 的句柄数；0 表示"未走打包路径"（调用方回退逐描述符提交），
    // 此时不消费任何 context（所有权仍归调用方）。
    int SubmitPackedPlainJobs(const PackedPlainJobDesc* descs, int count, void** outStates) noexcept;

    // ---- base 模块助手（定义在 JobSystem.cpp） ----
    inline void SetCurrentBatchId(uint64_t id) noexcept
    {
        auto cb = g_currentBatchIdCallback.load(std::memory_order_acquire);
        if (cb) cb(id);
    }
    uint64_t AssignStateDiagnosticId(HandleState* state) noexcept;

    // ---- 调试面板：执行窗口上报（事件驱动，非每帧采样）----
    // 入口记录"开始"事件（压栈 + 时间戳），出口把完整窗口 [startMs, endMs] 追加进共享历史；
    // GUI 只读渲染共享历史，微秒级 Job（两帧之间跑完）也不丢失。
    // 有 worker 索引的线程上报其泳道；无索引的调用线程（主线程 inline）上报 M 泳道（index == CurrentWorkerCount()）。
    inline int DebugReportLaneId() noexcept
    {
        const int wi = WorkerIndexManager::GetCurrentIndex();
        if (wi >= 0) return wi;
        const int mc = CurrentWorkerCount();
        return (mc >= 0 && mc < kMaxTrackedWorkers) ? mc : -1;
    }
    inline double DebugNowMs() noexcept
    {
        using namespace std::chrono;
        return duration_cast<duration<double, std::milli>>(
            steady_clock::now().time_since_epoch()).count();
    }

    // ---- 共享时间线历史（base 模块定义；job 执行线程在结束瞬间追加，GUI 只读渲染）----
    constexpr int kDebugSegmentMax = 16384;
    // isDirect：是否为直调方法（transpiler 直跑，非调度式 Job）；GUI 用于把直调标记为 [D]。
    // workers：参与本次执行 / 认领工作的 worker 数（batch 为计划参与数；同步/快速路径为 1；直调为并行度）。
    struct DebugSegment { int lane; uint64_t batchId; double startMs; double endMs; uint32_t tiles; uint32_t workers; bool isDirect; };
    extern DebugSegment g_debugSegments[kDebugSegmentMax];
    extern std::atomic<unsigned int> g_debugSegHead;    // 槽位分配（写者 fetch_add relaxed）
    extern std::atomic<unsigned int> g_debugSegVisible; // 已发布段数（写者 release 发布；读者 acquire）
    // 每槽 seqlock 代次：奇=写中、偶=写完。修复并发写者乱序完成时，读者凭 visible
    // 定位到"已发布但尚未写完"的槽（visible 计数先行、槽内容滞后的撕裂）。
    extern std::atomic<uint64_t> g_debugSegSeq[kDebugSegmentMax];

    // 每个泳道的嵌套执行栈（job 体内再 inline 调度 job）：保存开始时间戳与 id，
    // 结束时弹栈配对。非原子——仅单写者线程访问（M 泳道罕见多写者时仅可能短暂错配）。
    struct ExecWindowRing
    {
        int depth = 0;
        struct ExecFrame { uint64_t id; double startMs; uint32_t tiles; uint32_t workers; bool isDirect; } stack[8];
    };
    extern ExecWindowRing g_execWindows[kMaxTrackedWorkers];

    inline void DebugBeginExec(uint64_t id, uint32_t tiles, uint32_t workers, bool isDirect) noexcept
    {
        // 零开销守门：面板关闭时不触碰任何原子（此判断须先于 id==0 判断）。
        if (!g_nativeActivityCaptureEnabled.load(std::memory_order_relaxed)) return;
        // 暂停时停止记录新段，环形缓冲不被覆盖、历史得以保留。
        if (g_debugPaused.load(std::memory_order_relaxed)) return;
        const int lane = DebugReportLaneId();
        if (id == 0 || lane < 0) return;
        auto& st = g_execWindows[lane];
        if (st.depth < 8)
            st.stack[st.depth++] = ExecWindowRing::ExecFrame{ id, DebugNowMs(), tiles, workers, isDirect };
        else if (st.depth == 8) // 栈满：覆盖最内层，至少保持"活跃"语义
            st.stack[7] = ExecWindowRing::ExecFrame{ id, DebugNowMs(), tiles, workers, isDirect };
        g_workerCurrentBatchId[lane].store(id, std::memory_order_relaxed);
        g_workerCurrentTile[lane].store(0, std::memory_order_relaxed);
        g_workerBatchTileCount[lane].store(tiles, std::memory_order_relaxed);
        g_workerIsActive[lane].store(true, std::memory_order_release);
    }
    inline void DebugEndExec() noexcept
    {
        if (!g_nativeActivityCaptureEnabled.load(std::memory_order_relaxed)) return;
        const int lane = DebugReportLaneId();
        if (lane < 0) return;
        auto& st = g_execWindows[lane];
        if (st.depth > 0)
        {
            const ExecWindowRing::ExecFrame f = st.stack[--st.depth];
            if (g_nativeActivityCaptureEnabled.load(std::memory_order_relaxed) &&
                !g_debugPaused.load(std::memory_order_relaxed))
            {
                // 结束事件：完整窗口直接追加进共享时间线历史（GUI 只读渲染）。
                // 先写槽内容，再 fetch_add(release) 发布计数——读者永不读到未写完的槽。
                const unsigned int h = g_debugSegHead.fetch_add(1, std::memory_order_relaxed);
                const unsigned int slot = h % kDebugSegmentMax;
                g_debugSegSeq[slot].fetch_add(1, std::memory_order_acquire);  // 奇：写中
                g_debugSegments[slot] = DebugSegment{ lane, f.id, f.startMs, DebugNowMs(), f.tiles, f.workers, f.isDirect };
                g_debugSegSeq[slot].fetch_add(1, std::memory_order_release);  // 偶：写完
                g_debugSegVisible.fetch_add(1, std::memory_order_release);
            }
        }
        g_workerIsActive[lane].store(false, std::memory_order_release);
        g_workerCurrentBatchId[lane].store(0, std::memory_order_release);
        g_workerCurrentTile[lane].store(0, std::memory_order_release);
        g_workerBatchTileCount[lane].store(0, std::memory_order_release);
    }
    // 尝试读一个已完成的 debug segment（跳过写中的槽）。seqlock：seq 奇=写中。
    // 返回 false 表示该槽正在写或读期间被写，调用方应跳过。
    inline bool DebugTryReadSegment(unsigned int slot, DebugSegment& out) noexcept
    {
        const uint64_t s1 = g_debugSegSeq[slot].load(std::memory_order_acquire);
        if (s1 & 1) return false;
        out = g_debugSegments[slot];
        const uint64_t s2 = g_debugSegSeq[slot].load(std::memory_order_acquire);
        return s1 == s2;
    }
    // 更新当前执行窗口（栈顶）已认领执行的 tile 数（worker 在 batch 执行中累计后调用）。
    // 让 segment.tiles = 该 worker 实际领取的 tile 数，而非整批 tileCount。
    inline void DebugUpdateExecTiles(uint32_t tiles) noexcept
    {
        if (!g_nativeActivityCaptureEnabled.load(std::memory_order_relaxed)) return;
        const int lane = DebugReportLaneId();
        if (lane < 0) return;
        auto& st = g_execWindows[lane];
        if (st.depth > 0)
            st.stack[st.depth - 1].tiles = tiles;
    }

    template <typename Fn>
    void RunSyncJob(HandleState* state, Fn&& fn) noexcept
    {
        // 幂等：调用方可能已为 state 预分配诊断 id（inline 路径先报到 published），
        // 复用同一 id 保证 事件 id == 执行窗口 id == handle 的 diagnosticBatchId。
        uint64_t id = state->diagnosticBatchId.load(std::memory_order_acquire);
        if (id == 0) id = AssignStateDiagnosticId(state);
        DebugBeginExec(id, 1, 1, false); // 同步 inline Job：单线程执行
        SetCurrentBatchId(id);
        // C++ 异常协议：inline/同步路径（小 job 直跑）异常也捕获到 handle state，
        // 由 Complete() 统一重抛（与批量路径一致；noexcept 下若不捕获会 terminate）。
        try
        {
            fn();
        }
        catch (...)
        {
            RecordStateException(state, std::current_exception());
        }
        SetCurrentBatchId(0);
        DebugEndExec();
    }
    void RecordBatchTiming(const BatchTimingSample& sample) noexcept;
    uint64_t MonotonicNowNs() noexcept;
    int CurrentProcessorIndexForDiagnostics() noexcept;
    uint64_t CurrentThreadCpuTimeNsForDiagnostics() noexcept;
    uint64_t CurrentThreadCyclesForDiagnostics() noexcept;
    int PhysicalCoreIndexForDiagnostics(int logicalCore) noexcept;
    void FlushStateCacheToSharedPool();
    void ConsumeLongBatchBarriers() noexcept;

    // ---- State 模块（定义在 JobSystem_State.cpp） ----
    void RetainDependency(HandleState* state, HandleState* dep) noexcept;
    void RegisterLongBatchBarrier(HandleState* state) noexcept;
    // 提交异步 state-owned 操作：即使操作在正常尾声前抛异常，state 引用也由后端包装器释放。
    bool SubmitBackendAsync(
        std::function<void()> work,
        HandleState* state = nullptr,
        void (*failureCleanup)(void*) = nullptr,
        void* failureContext = nullptr) noexcept;
    int ResolveChunkSize(int length, int requestedChunk);
    // 带 funcHash 的重载：flag 开启且有 per-job 成本数据时按 perElem EWMA 自动求解最优 tile 数，否则 tpw=4 兜底；funcHash=0 等价两参版本。
    // outJccFine（可空）：分块是否由 JCC 公式（细粒度）产出，退役时据此归细/粗（公式可能产出 < tpw 的 tiles，tile 数比较会误判）。
    int ResolveChunkSize(int length, int requestedChunk, uint32_t funcHash,
        bool* outJccFine = nullptr);

    // ---- 实体数衡 tile（定义在 JobSystem_Tiles.cpp） ----
    int UnitEntityCount(const ChunkBatchContext* cc, TileKind kind, int unit) noexcept;
    int ResolveEcsEntityTileTarget(int64_t totalEntities, int workerCount) noexcept;
    int BuildEntityBalancedTiles(ExecutionTile* tiles, const ChunkBatchContext* cc,
        TileKind kind, int itemCount, int targetEntities) noexcept;

    // ---- shutdown 残留 batch 强制退役（定义在 JobSystem_Tiles.cpp） ----
    // Shutdown 对每个未退役 batch 执行与正常退役 finalized 块等价的
    // cleanup + ReleaseBatch + backendRetired + ReleaseState，但不检查 tilesRemaining/pendingTasks
    // ——shutdown 时 worker 已 join，单线程安全。context 已清理或 finalized 已置位时幂等跳过。
    void ForceFinalizeBatch(BatchState* batch) noexcept;

    // 中止从未到达后端的 batch（分配/生命周期/continuation 失败）：持有初始 BatchStorage 引用，
    // 发布终态 HandleState，不触碰调度器计数器。
    void AbortUnsubmittedBatch(BatchState* batch, std::exception_ptr exception) noexcept;

    // ---- ISPC MT 任务挂钩（tasksys.cpp 调用，事件驱动显示每个参与 worker 的耗时）----
    // 每个 ISPC 任务在自己的 ConcRT 线程上执行，分配到保留的高位泳道（W/M 之后）。
    // tasksys.cpp 位于 NativeTranspiled.dll，故本 API 须从 NativeDll 导出：
    // JOB_SYSTEM_EXPORT 已定义→dllexport；未定义→dllimport。
#ifdef _WIN32
#ifdef JOB_SYSTEM_EXPORT
#define ENTJOY_ISPC_DEBUG_API __declspec(dllexport)
#else
#define ENTJOY_ISPC_DEBUG_API __declspec(dllimport)
#endif
#else
#define ENTJOY_ISPC_DEBUG_API
#endif
    ENTJOY_ISPC_DEBUG_API uint64_t DebugIspcTaskBegin(const char* name) noexcept;
    ENTJOY_ISPC_DEBUG_API void DebugIspcTaskEnd(uint64_t id) noexcept;
    // 当前已分配的最高 ISPC 泳道数（GUI 据此扩展泳道条数）
    int DebugIspcLaneCount() noexcept;
    constexpr int kIspcLaneBase = kMaxTrackedWorkers - 16; // 预留 16 条高位泳道给 ISPC

    // ---- Tiles 模块（定义在 JobSystem_Tiles.cpp） ----
    int ResolveWorkerTarget(int workerCap, int targetCount) noexcept;
    // 小 job（每 worker ≤2 chunk）且 worker 目标 > 物理核数时，退到物理核数（A/B：`ENTJOY_PHYSCAP_SMALLJOB=1`）。
    int ApplyPhysCoreCapForSmallJob(int targetWorkers, uint32_t tileCount, int length) noexcept;
    // 本机物理核数（0 = 不可知）。定义在 JobSystem.cpp；由 Scheduler::Initialize 刷新。
    int PhysicalCoreCountForDiagnostics() noexcept;
    void RefreshPhysicalCoreCount() noexcept;
    extern std::atomic<int> g_physicalCores;
    extern std::atomic<uint64_t> g_physCapApplied;
    int ResolveEcsBatchRangeSize(int itemCount, int workerCount) noexcept;
    int GuidedTileCount(int length, int workerCount, int k, int floor) noexcept;
    int BuildGuidedTiles(ExecutionTile* tiles, int length, int workerCount,
        int k, int floor, TileKind kind = TileKind::GeneralRange) noexcept;
    BatchStorage* AcquireBatchStorage(uint32_t tileCapacity);
    void ReleaseBatchStorage(BatchStorage* storage) noexcept;
#ifdef ENTJOY_TESTING
    // Test-only allocation fault injection. Production builds do not expose this hook.
    void FailNextBatchStorageAcquireForTests(int count) noexcept;
#endif
    void ClearBatchStoragePool() noexcept;
    void FlushBatchContextCacheToSharedPool();
    void ClearBatchContextPool() noexcept;
    // BackendAsyncContext（plain IJob 的异步窗口上下文）池的两个 Shutdown 收尾点，
    // 定义在 JobSystem_State.cpp；语义与 BatchContext 池一致（先交还 main 的 TLS 缓存，再清共享池）。
    void FlushAsyncContextCacheToSharedPool();
    void ClearAsyncContextPool() noexcept;
    void FlushBatchStorageCacheToSharedPool();
    void SubmitBatch(BatchState* batch, int workerCap = 0);
    // 隐式批（native 收集）入口：开关开 → 挂 pending（持 state 引用防悬垂）；否则直接 SubmitBatch。
    void SubmitOrPending(BatchState* batch);
    // 隐式批 force point：defer 窗口内提交全部 pending + 单次唤醒（EndFrame / Complete 自动触发）。
    void FlushPendingSubmits();
    // ── `ENTJOY_WAKE_POLL`（默认开；`=0` 关闭）：提交侧的"需求感知"唤醒决策 ──
    // 问题：生产者每条派发都写一条被 W 个 worker 轮询的 cacheline，每次写都要把它从 W 个共享者
    //   手里抢回独占 ⇒ per-job 成本随 worker 数增长。正确的省法不是"少叫人"，而是
    //   "有人正在轮询注入器、自己就能领到"时一个字节都不写。
    //   区分两个量：parkedWorkers = 已登记停靠（含 futex 等待）的人数；
    //              idlePollers   = 登记在"搜索区"（每轮都读注入器；或刚领到任务，执行完必然回主循环）的人数。
    // 协议（rayon-core `sleep` 模块的 posted-without-storing 快路径，README 的
    //   "Using seq-cst fences to prevent deadlock" 证明骨架）：
    //     提交侧：token 入注入器（release）→ fence(seq_cst) → 读 idlePollers → 读 sleepers
    //             · idlePollers >= need ⇒ 一个字节都不写（登记者的下一次读必然在 push 之后）；
    //             · 不足且 sleepers > 0 ⇒ bump epoch + notify_all（真有人在 futex 上等）；
    //             · 不足且 sleepers == 0 ⇒ 不写（其余 worker 正在执行，执行完的下一轮会读注入器）。
    //     停靠侧：退"搜索区登记"（idlePollers--）→ 登记停靠（sleepers++）→ fence(seq_cst)
    //             → 最后一次读注入器/本线程 deque → 才 futex wait。
    //   ⚠ 关键顺序：登记必须在最后复查之前，两处 fence 才是配对的
    //     （rayon README: PushFence / SleepFence）。顺序写反就会丢唤醒。
    //   ⚠ `need` 必须分入口：小 job（`SubmitWork`）需要 1 个 worker，真并行趟（`SubmitBatch`）
    //     需要本趟真实令牌数 `tokenCount`（= min(workerCap, workerCount_, tileCount)，不是
    //     `batch->workerCount` 这个上限）。用同一个谓词套两条入口会让一整趟只被 1~2 个 worker 拖着跑。
    //   ⚠ 搜索区登记必须粘性（只在"进停靠协议"与"退出主循环"两处递减）：每次进出都写会让
    //     连发的每个 job 多付共享行 RMW。
    //   ⚠ 与 rayon 的一处有意偏离：rayon 把 [sleeping, inactive, JEC] 打包进同一个字；我们不能把
    //     JEC 与被频繁写的线程计数同放一字 —— 我们的 futex 原语是
    //     `std::atomic::wait(wakeEpoch, stamp)`（按值比较），线程计数的任何写入都会让全部等待者
    //     立刻"被唤醒"（虚假唤醒风暴）。故"搜索区人数"与既有的 `parkedWorkers`(sleepers) 分字存放，
    //     读序固定为 idle 先、sleepers 后，安全性见 .cpp。
    //   ⚠ 计数不得在派发热路径上写全局原子（那正是本改动要消除的模式）：走 thread_local 累加 +
    //     每 1024 次合并（`WakePollFlushCurrentThread`），读取侧先 flush 本线程尾巴。
    //   ⚠ 未做：rayon 的"找到活的人顺手叫醒 1~2 个睡着的人"级联（`work_found → wake_any_threads`）。
    //     需求感知谓词已覆盖当前两个入口；若将来有入口低报 need，那才是正确的下一步补丁。
    bool WakePollEnabled() noexcept;
    // 生效证据计数：把当前线程的 thread_local 累加值合并进全局计数（见 ChaseLevScheduler.cpp
    // 的说明；读取侧必须先调一次，否则 <1024 的尾巴读不到）。worker 退出主循环时也会自己调一次。
    void WakePollFlushCurrentThread() noexcept;
    bool ChunkExecuteTile(void* ctx, const ExecutionTile& tile);
    void CleanupChunkContext(void* ctx);

    // Complete 分段诊断（实现在 JobSystem_State.cpp；未设 `ENTJOY_DIAG_NATIVE_PHASE=1` 时为空操作）。
    namespace DiagPhase { void Dump(); }
    // E1：worker 忙比 / 相位尾部（实现在 JobSystem_State.cpp；未设 `ENTJOY_DIAG_E1=1` 时为空操作）。
    // `Begin/End` 由 ChaseLevScheduler 的执行窗口成对调用（每线程 TLS 记窗口起点）。
    namespace E1
    {
        bool Enabled() noexcept;
        void Reset() noexcept;                       // Initialize：清计数 + 记起点
        void Begin() noexcept;                       // 进入 tile 执行窗口
        void End(uint32_t workerIndex) noexcept;     // 离开窗口（>= kMaxTrackedWorkers ⇒ 主线程口径）
        void Dump() noexcept;                        // Shutdown：打印忙比与抖动
        // 批级：参与者数 / tile 数直方图 + 批间空隙（回答"为什么喂不饱 N 个 worker"）
        void RecordBatch(uint32_t tileCount, uint32_t enteredWorkers, uint64_t wallNs) noexcept;
        void RecordPublish(uint64_t publishedNs) noexcept;
        void MarkTopologyDone(uint64_t topologyDoneNs) noexcept;
        // 退役链分段（`lastTileAt` → `topologyDoneAt` 之间那 ~11 µs 到底花在哪）
        enum : int { kRetireCas = 0, kRetireTiming = 1, kRetireBarrier = 2, kRetireCleanup = 3,
                     kRetireCompleteState = 4, kRetireTopology = 5, kRetireTotal = 6, kRetireSlots = 7 };
        void RetirePhase(int slot, uint64_t ns) noexcept;
        // 启停斜坡：按"实际参与 worker 数"分桶记录 `firstWorkerAt → lastWorkerAt`（EWMA 不稳，需分布）
        void RecordWorkerSpread(uint32_t enteredWorkers, uint64_t spreadNs) noexcept;
    }
    // 原生 Schedule 分段诊断（实现主体在 JobSystem_Scheduler.cpp；未设
    // `ENTJOY_DIAG_NATIVE_SCHED=1` 时为空操作）。SubmitAcct 段由 JobSystem_Tiles.cpp 的
    // SubmitBatch 自记；SubmitTokens / SubmitNotify 两段由 ChaseLevScheduler::SubmitBatch 自记
    // （都嵌套于 Schedule 的 submit 段内）。
    namespace SchedPhase
    {
        enum : int {
            Resolve = 0, Ctx = 1, Storage = 2, StateCreate = 3, StateFields = 4,
            Tile = 5, SubmitDs = 6, SubmitAcct = 7, SubmitTokens = 8, SubmitNotify = 9,
            SubmitOther = 10, Calib = 11, Count = 12 };
        extern std::atomic<uint64_t> g_sumNs[Count];
        extern std::atomic<uint64_t> g_calls[Count];
        extern std::atomic<uint64_t> g_entries;
        extern std::atomic<uint64_t> g_submitEntries;
        bool Enabled();
        void Dump();
        inline void Add(int slot, uint64_t ns)
        {
            g_sumNs[slot].fetch_add(ns, std::memory_order_relaxed);
            g_calls[slot].fetch_add(1, std::memory_order_relaxed);
        }
    }

    // ── 批上下文池（A：「共享批上下文」的最后一块）──
    // GeneralBatchContext / ChunkBatchContext 每次调度一个：线程本地缓存 + 共享池两级复用（与 BatchStorage 同款），去掉每 job 一次 malloc/free。
    GeneralBatchContext* AcquireGeneralBatchContext();
    void ReleaseGeneralBatchContext(GeneralBatchContext* bc) noexcept;
    ChunkBatchContext* AcquireChunkBatchContext();
    void ReleaseChunkBatchContext(ChunkBatchContext* cc) noexcept;
    void DestroyChunkContextWithoutCleanup(void* ctx) noexcept;
    bool GeneralExecuteTile(void* ctx, const ExecutionTile& tile);
    // 等宽 tile 的逐 tile 直调：契约不变（每个 tile 仍恰好一次内核调用），
    // 只把 `executor_→TryExecuteOneTile→executeTile→GeneralExecuteTile→batchFunc` 压成 `batchFunc` 一跳，
    // 并跳过诊断未开启时不需要的逐 tile 判据。返回实际消费的 tile 数（0 = 不适用 ⇒ 回退通用路径）。
    uint32_t TileExecuteUniformRun(BatchState* batch, uint32_t tileIndex, uint32_t runTiles) noexcept;
    void CleanupGeneralContext(void* ctx);
    void DestroyGeneralContextWithoutCleanup(void* ctx) noexcept;

    // ---- Chase-Lev tile 级窃取（定义在 JobSystem_Tiles.cpp） ----
    // ChaseLevScheduler 回调 trampoline（供 Scheduler::Initialize 传给 ChaseLevScheduler::Start）
    void ChaseLevExecuteTile(BatchState* batch, uint32_t tileIndex) noexcept;
    // ChaseLev 双条件退役：tilesRemaining==0 && pendingTasks==0 时才释放 storage。
    void TryFinalizeChaseLevBatch(BatchState* batch) noexcept;
    // ChaseLev 任务完成回调：先做代次校验（batchGen 为令牌创建时的代次；不等即丢弃迟到结算），
    // 通过后 pendingTasks--，归零时触发双条件退役检查。
    void ChaseLevTaskDone(BatchState* batch, uint32_t batchGen) noexcept;
    // 记录 worker 进入批次的时间（firstWorkerAt/lastWorkerAt），供 timing 诊断。
    void ChaseLevRecordWorkerEntry(BatchState* batch) noexcept;

    // ---- 认领组聚合 tile 完成计数（固定启用，无开关） ----
    // 每 tile 唯一的共享写就是 TryExecuteOneTile 末尾的 `tilesRemaining.fetch_sub(1, acq_rel)`；
    // 参与者一多，这一行就被反复跨核争用（每 tile 成本随 worker 数增长的那部分几乎全来自它）。
    // 组模式：认领组内 tile 只在本线程本地计数，组末一次 `fetch_sub(组内实际执行数)`（step=4 ⇒ RMW ÷4）。
    // 语义等价：tile 仍是"执行完才记账" ⇒ 计数归零仍 ⟺ 全部 tile 执行完（`lastTileAt` 最多延后一组，
    // 它是纯诊断时间戳）；异常由 TryExecuteOneTile 内部记录，不影响本组记账。
    void TileAcctGroupBegin() noexcept;
    void TileAcctGroupFlush(BatchState* batch) noexcept;
    // 切片认领的段游标初始化（`sliced=true` 时生效，否则 sliceCount=0）。
    // 谁决定 `sliced`：调用点声明（`claimGeomOverride`）或 F6（`g_claimAdaptiveEnabled` 逐 job 学习）。
    // 必须在发布之前调用（发布后 worker 可能立刻开始执行 ⇒ 不能懒初始化，否则会有
    // "边初始化边用全局 nextTile"的路径并存 ⇒ 同一 tile 可能被执行两次）。
    void InitSliceCursors(BatchState* batch, bool sliced) noexcept;
    // 组模式的线程局部状态（定义在 JobSystem_Tiles.cpp；TryExecuteOneTile 在文件前段读取）。
    extern thread_local bool t_tileAcctGroupActive;
    extern thread_local uint32_t t_tileAcctGroupCount;
    // （标准 Chase-Lev 不需要共享注册表追踪）
} // namespace JobSystem
