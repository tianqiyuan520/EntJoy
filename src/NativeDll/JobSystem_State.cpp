#include "JobSystemInternal.h"
#include "ChaseLevScheduler.h"
#include "CpuPause.h"

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <functional>
#include <stdexcept>
#include <thread>
#include <utility>

#if defined(_MSC_VER) && (defined(_M_IX86) || defined(_M_X64))
#include <immintrin.h>
#endif

namespace JobSystem
{
    // ── 托管侧 ABI 钉子（C# NativeJobHandle.IsCompletedFast）──────────────────────────────
    // 托管侧不走 P/Invoke、直接读 HandleState 前 8 字节判定"已完成"（每帧每个被覆盖的写句柄
    // 少两次跨层调用）。这里把布局钉死：refCount 4 字节 @0 → completed 1 字节 @4（偏移 5 是
    // backendRetired，恒为 1，所以托管侧必须按 1 字节读；曾经按 int 读 ⇒ 恒判"已完成"）。
    static_assert(offsetof(HandleState, completed) == 4,
        "C# NativeJobHandle.IsCompletedFast 依赖 HandleState::completed 位于偏移 4");
    static_assert(sizeof(std::atomic<bool>) == 1,
        "C# NativeJobHandle.IsCompletedFast 按 1 字节读取 completed");

    // ============================================================
    // Complete 分段诊断（`ENTJOY_DIAG_NATIVE_PHASE=1`）
    //
    // 背景：`gridsearch/07` §7e 实测"S+C 交替"形状下每 job 的 82% 花在 `JobSystem_Complete` 里，
    // 但**内部**（快路径 / 自旋 / completedCv 阻塞等待 / 等 backendRetired / 取异常）的比例未知，
    // 于是无法判断该动哪一段（先前"在等退役期间 assist"的尝试实测无效，就属于没先分段就动手）。
    //
    // 本诊断按段累加纳秒与次数，`Scheduler::Shutdown` 时打印一次（不新增导出、不改协议）。
    // 未启用时每段只有一次静态 bool 读，热路径零成本。
    // ============================================================
    namespace DiagPhase
    {
        enum : int { FastPath = 0, Spin2048 = 1, Spin256 = 2, BlockWait = 3, WaitRetired = 4, ExcCheck = 5, Count = 6 };
        std::atomic<uint64_t> g_sumNs[Count];
        std::atomic<uint64_t> g_calls[Count];
        std::atomic<uint64_t> g_entries;

        bool Enabled()
        {
            static const bool enabled = [] {
                const char* v = std::getenv("ENTJOY_DIAG_NATIVE_PHASE");
                return v != nullptr && v[0] == '1';
            }();
            return enabled;
        }

        inline void Add(int slot, uint64_t ns)
        {
            g_sumNs[slot].fetch_add(ns, std::memory_order_relaxed);
            g_calls[slot].fetch_add(1, std::memory_order_relaxed);
        }

        void Dump()
        {
            if (!Enabled()) return;
            static const char* kNames[Count] = {
                "fastpath(already completed)",
                "spin2048(to completed)",
                "spin256(to completed)",
                "blockWait(completedCv)",
                "waitBackendRetired",
                "excCheck" };
            const uint64_t entries = g_entries.load(std::memory_order_relaxed);
            uint64_t sumAll = 0;
            for (int i = 0; i < Count; ++i) sumAll += g_sumNs[i].load(std::memory_order_relaxed);
            std::printf("[NPHS] JobSystem_Complete: entries=%llu sum=%.1f us (sum of segment means=%.3f us/entry)\n",
                (unsigned long long)entries, sumAll / 1000.0,
                entries ? (sumAll / 1000.0) / (double)entries : 0.0);
            for (int i = 0; i < Count; ++i)
            {
                const uint64_t ns = g_sumNs[i].load(std::memory_order_relaxed);
                const uint64_t n = g_calls[i].load(std::memory_order_relaxed);
                std::printf("[NPHS]   %-28s calls=%llu total=%.1f us mean=%.3f us\n",
                    kNames[i], (unsigned long long)n, ns / 1000.0, n ? (ns / 1000.0) / (double)n : 0.0);
            }
            std::fflush(stdout);
        }
    }

    // ============================================================
    // E1：忙比 / 相位尾部 / 批级并行度（`ENTJOY_DIAG_E1=1`，默认档零成本）
    //
    // 三个问题各自对应一组指标：
    //   ① 忙比      = Σ_tile执行时间 / (worker 数 × 墙钟)                  —— 有多少并行松弛
    //   ② 相位尾部  = 批完成链分段 + 启停斜坡（按参与度分桶）              —— 每批的串行尾/启停
    //   ③ 批内并行度= Σ(批内 worker busy) / 批墙钟（每批"平均同时几个在干"）—— 大批内部是否吃饱
    // 归属按**线程**（TLS lane），不信传入的 workerIndex（实测后者会让两线程写同一槽）。
    // 未启用时每执行窗口只有一次静态 bool 读。
    // ============================================================
    namespace E1
    {
        // —— ① 忙比 ——
        std::atomic<uint64_t> g_busyNs[kMaxTrackedWorkers];
        std::atomic<uint64_t> g_windows[kMaxTrackedWorkers];
        std::atomic<uint64_t> g_mainBusyNs{ 0 };
        std::atomic<uint64_t> g_mainWindows{ 0 };
        std::atomic<uint32_t> g_nextLane{ 0 };
        std::atomic<int32_t>  g_workers{ 0 };
        std::atomic<int64_t>  g_startNs{ 0 };
        std::atomic<int64_t>  g_lastPrintNs{ 0 };
        std::atomic<uint64_t> g_lastBusyNs{ 0 };
        std::atomic<uint64_t> g_badWindows{ 0 };
        std::atomic<uint64_t> g_orphanEnds{ 0 };
        // —— ③ 批级：参与度 / 墙钟 / Σbusy / 并发度 ——
        std::atomic<uint64_t> g_batchCount[16];
        std::atomic<uint64_t> g_batchWallNs[16];
        std::atomic<uint64_t> g_batchBusyNs[16];
        std::atomic<uint64_t> g_concSumMilli[16];
        std::atomic<uint64_t> g_concHist[16];
        std::atomic<uint64_t> g_tileCountBatches[25];
        std::atomic<uint64_t> g_tileCountWallNs[25];
        std::atomic<uint64_t> g_batchTotal{ 0 };
        std::atomic<uint64_t> g_batchTotalWallNs{ 0 };
        std::atomic<uint64_t> g_batchTotalBusyNs{ 0 };
        std::atomic<uint64_t> g_curBatchBusyNs{ 0 };   // 当前批 Σbusy（本工作量批不重叠）
        // —— 批间空隙 ——
        std::atomic<uint64_t> g_gapNs{ 0 };
        std::atomic<uint64_t> g_gapCount{ 0 };
        std::atomic<uint64_t> g_gapMaxNs{ 0 };
        std::atomic<uint64_t> g_lastTopologyNs{ 0 };
        // —— ② 退役链分段 ——
        std::atomic<uint64_t> g_retireNs[7];
        std::atomic<uint64_t> g_retireCount[7];
        std::atomic<uint64_t> g_retireMaxNs{ 0 };
        std::atomic<uint64_t> g_retireBucket[5];
        std::atomic<uint64_t> g_retireBucketNs[5];
        // —— ② 启停斜坡（按参与度分桶）——
        std::atomic<uint64_t> g_spreadNs[16];
        std::atomic<uint64_t> g_spreadCount[16];
        std::atomic<uint64_t> g_spreadMaxNs[16];
        thread_local int64_t  t_begin = 0;
        thread_local int32_t  t_depth = 0;
        thread_local int32_t  t_lane = -1;
        thread_local bool     t_isMain = false;

        static constexpr int64_t kMaxWindowNs = 2000000000LL;   // 单窗口上限 2 s
        static int64_t g_intervalNs = 3000000000LL;            // `ENTJOY_DIAG_E1_MS=<ms>` 可覆盖

        static inline int64_t Now() noexcept
        {
            return std::chrono::duration_cast<std::chrono::nanoseconds>(
                std::chrono::steady_clock::now().time_since_epoch()).count();
        }

        bool Enabled() noexcept
        {
            static const bool enabled = [] {
                const char* v = std::getenv("ENTJOY_DIAG_E1");
                if (v == nullptr || v[0] != '1') return false;
                const char* ms = std::getenv("ENTJOY_DIAG_E1_MS");
                if (ms != nullptr)
                {
                    const long long v2 = std::atoll(ms);
                    if (v2 >= 50LL && v2 <= 600000LL) g_intervalNs = v2 * 1000000LL;
                }
                return true;
            }();
            return enabled;
        }

        static uint64_t SumBusy() noexcept
        {
            uint64_t total = 0;
            for (int i = 0; i < kMaxTrackedWorkers; ++i)
                total += g_busyNs[i].load(std::memory_order_relaxed);
            return total;
        }

        static int WorkerCount() noexcept
        {
            const int w = g_workers.load(std::memory_order_relaxed);
            return w > 0 ? w : 1;
        }

        void Reset() noexcept
        {
            if (!Enabled()) return;
            for (int i = 0; i < kMaxTrackedWorkers; ++i)
            {
                g_busyNs[i].store(0, std::memory_order_relaxed);
                g_windows[i].store(0, std::memory_order_relaxed);
            }
            g_mainBusyNs.store(0, std::memory_order_relaxed);
            g_mainWindows.store(0, std::memory_order_relaxed);
            g_nextLane.store(0, std::memory_order_relaxed);
            g_workers.store(0, std::memory_order_relaxed);
            g_badWindows.store(0, std::memory_order_relaxed);
            g_orphanEnds.store(0, std::memory_order_relaxed);
            for (int i = 0; i < 16; ++i)
            {
                g_batchCount[i].store(0, std::memory_order_relaxed);
                g_batchWallNs[i].store(0, std::memory_order_relaxed);
                g_batchBusyNs[i].store(0, std::memory_order_relaxed);
                g_concSumMilli[i].store(0, std::memory_order_relaxed);
                g_concHist[i].store(0, std::memory_order_relaxed);
                g_spreadNs[i].store(0, std::memory_order_relaxed);
                g_spreadCount[i].store(0, std::memory_order_relaxed);
                g_spreadMaxNs[i].store(0, std::memory_order_relaxed);
            }
            for (int i = 0; i < 25; ++i)
            {
                g_tileCountBatches[i].store(0, std::memory_order_relaxed);
                g_tileCountWallNs[i].store(0, std::memory_order_relaxed);
            }
            for (int i = 0; i < 7; ++i)
            {
                g_retireNs[i].store(0, std::memory_order_relaxed);
                g_retireCount[i].store(0, std::memory_order_relaxed);
            }
            for (int i = 0; i < 5; ++i)
            {
                g_retireBucket[i].store(0, std::memory_order_relaxed);
                g_retireBucketNs[i].store(0, std::memory_order_relaxed);
            }
            g_retireMaxNs.store(0, std::memory_order_relaxed);
            g_batchTotal.store(0, std::memory_order_relaxed);
            g_batchTotalWallNs.store(0, std::memory_order_relaxed);
            g_batchTotalBusyNs.store(0, std::memory_order_relaxed);
            g_curBatchBusyNs.store(0, std::memory_order_relaxed);
            g_gapNs.store(0, std::memory_order_relaxed);
            g_gapCount.store(0, std::memory_order_relaxed);
            g_gapMaxNs.store(0, std::memory_order_relaxed);
            g_lastTopologyNs.store(0, std::memory_order_relaxed);
            const int64_t now = Now();
            g_startNs.store(now, std::memory_order_relaxed);
            g_lastPrintNs.store(now, std::memory_order_relaxed);
            g_lastBusyNs.store(0, std::memory_order_relaxed);
        }

        void Begin() noexcept
        {
            if (!Enabled()) return;
            if (t_depth < 0 || t_depth > 4) t_depth = 0;
            if (t_depth++ != 0) return;
            t_begin = Now();
            if (t_lane < 0)
            {
                t_isMain = (std::this_thread::get_id() == g_mainThreadId);
                const uint32_t lane = g_nextLane.fetch_add(1, std::memory_order_relaxed);
                t_lane = lane < static_cast<uint32_t>(kMaxTrackedWorkers)
                    ? static_cast<int32_t>(lane) : (kMaxTrackedWorkers - 1);
                if (g_workers.load(std::memory_order_relaxed) == 0)
                {
                    const int n = g_numThreads.load(std::memory_order_relaxed);
                    if (n > 0) g_workers.store(n, std::memory_order_relaxed);
                }
            }
        }

        void End(uint32_t workerIndex) noexcept
        {
            (void)workerIndex;
            if (!Enabled()) return;
            if (t_depth <= 0) { g_orphanEnds.fetch_add(1, std::memory_order_relaxed); return; }
            if (--t_depth != 0) return;
            const int64_t now = Now();
            const int64_t d = now - t_begin;
            if (d < 0 || d > kMaxWindowNs) { g_badWindows.fetch_add(1, std::memory_order_relaxed); return; }
            const uint64_t du = static_cast<uint64_t>(d);
            if (t_isMain || t_lane < 0)
            {
                g_mainBusyNs.fetch_add(du, std::memory_order_relaxed);
                g_mainWindows.fetch_add(1, std::memory_order_relaxed);
            }
            else
            {
                g_busyNs[t_lane].fetch_add(du, std::memory_order_relaxed);
                g_windows[t_lane].fetch_add(1, std::memory_order_relaxed);
            }
            g_curBatchBusyNs.fetch_add(du, std::memory_order_relaxed);   // ③ 归属当前批

            int64_t last = g_lastPrintNs.load(std::memory_order_relaxed);
            if (now - last >= g_intervalNs &&
                g_lastPrintNs.compare_exchange_strong(last, now, std::memory_order_relaxed))
            {
                const uint64_t busy = SumBusy();
                const uint64_t dBusy = busy - g_lastBusyNs.exchange(busy, std::memory_order_relaxed);
                const int64_t dt = now - last;
                const int w = WorkerCount();
                const double ratio = (dt > 0)
                    ? static_cast<double>(dBusy) / (static_cast<double>(dt) * w) : 0.0;
                std::printf("[E1] t=%.1fs dt=%.1fs busy_ratio=%.4f busy=%.1fms wall=%.1fms workers=%d\n",
                    static_cast<double>(now - g_startNs.load(std::memory_order_relaxed)) / 1e9,
                    static_cast<double>(dt) / 1e9, ratio, static_cast<double>(dBusy) / 1e6,
                    static_cast<double>(dt) / 1e6, w);
                std::fflush(stdout);
            }
        }

        // 批完成：参与度 / 墙钟 / Σbusy / 并发度 分桶。
        void RecordBatch(uint32_t tileCount, uint32_t enteredWorkers, uint64_t wallNs) noexcept
        {
            if (!Enabled()) return;
            const int b = enteredWorkers > 15u ? 15 : static_cast<int>(enteredWorkers);
            const uint64_t busyNs = g_curBatchBusyNs.exchange(0, std::memory_order_relaxed);
            g_batchCount[b].fetch_add(1, std::memory_order_relaxed);
            g_batchWallNs[b].fetch_add(wallNs, std::memory_order_relaxed);
            g_batchBusyNs[b].fetch_add(busyNs, std::memory_order_relaxed);
            if (wallNs > 0)
            {
                const double conc = static_cast<double>(busyNs) / static_cast<double>(wallNs);
                g_concSumMilli[b].fetch_add(static_cast<uint64_t>(conc * 1000.0), std::memory_order_relaxed);
                int ci = static_cast<int>(conc + 0.5);
                if (ci > 15) ci = 15;
                if (ci < 0) ci = 0;
                g_concHist[ci].fetch_add(1, std::memory_order_relaxed);
            }
            int t = 0;
            for (uint32_t v = tileCount; v > 1u; v >>= 1) ++t;
            if (t > 24) t = 24;
            g_tileCountBatches[t].fetch_add(1, std::memory_order_relaxed);
            g_tileCountWallNs[t].fetch_add(wallNs, std::memory_order_relaxed);
            g_batchTotal.fetch_add(1, std::memory_order_relaxed);
            g_batchTotalWallNs.fetch_add(wallNs, std::memory_order_relaxed);
            g_batchTotalBusyNs.fetch_add(busyNs, std::memory_order_relaxed);
        }

        void RecordPublish(uint64_t publishedNs) noexcept
        {
            if (!Enabled()) return;
            g_curBatchBusyNs.store(0, std::memory_order_relaxed);   // 新批开始
            const uint64_t prev = g_lastTopologyNs.load(std::memory_order_relaxed);
            if (prev != 0 && publishedNs > prev)
            {
                const uint64_t gap = publishedNs - prev;
                g_gapNs.fetch_add(gap, std::memory_order_relaxed);
                g_gapCount.fetch_add(1, std::memory_order_relaxed);
                uint64_t mx = g_gapMaxNs.load(std::memory_order_relaxed);
                while (gap > mx && !g_gapMaxNs.compare_exchange_weak(mx, gap, std::memory_order_relaxed)) {}
            }
        }

        void MarkTopologyDone(uint64_t topologyDoneNs) noexcept
        {
            if (!Enabled()) return;
            g_lastTopologyNs.store(topologyDoneNs, std::memory_order_relaxed);
        }

        void RecordWorkerSpread(uint32_t enteredWorkers, uint64_t spreadNs) noexcept
        {
            if (!Enabled()) return;
            const int b = enteredWorkers > 15u ? 15 : static_cast<int>(enteredWorkers);
            g_spreadNs[b].fetch_add(spreadNs, std::memory_order_relaxed);
            g_spreadCount[b].fetch_add(1, std::memory_order_relaxed);
            uint64_t mx = g_spreadMaxNs[b].load(std::memory_order_relaxed);
            while (spreadNs > mx && !g_spreadMaxNs[b].compare_exchange_weak(mx, spreadNs, std::memory_order_relaxed)) {}
        }

        void RetirePhase(int slot, uint64_t ns) noexcept
        {
            if (!Enabled()) return;
            if (slot < 0 || slot >= kRetireSlots) return;
            g_retireNs[slot].fetch_add(ns, std::memory_order_relaxed);
            g_retireCount[slot].fetch_add(1, std::memory_order_relaxed);
            if (slot == kRetireTotal)
            {
                uint64_t mx = g_retireMaxNs.load(std::memory_order_relaxed);
                while (ns > mx && !g_retireMaxNs.compare_exchange_weak(mx, ns, std::memory_order_relaxed)) {}
                int b = 0;
                if (ns >= 100000) b = 4; else if (ns >= 20000) b = 3; else if (ns >= 5000) b = 2; else if (ns >= 2000) b = 1;
                g_retireBucket[b].fetch_add(1, std::memory_order_relaxed);
                g_retireBucketNs[b].fetch_add(ns, std::memory_order_relaxed);
            }
        }

        void Dump() noexcept
        {
            if (!Enabled()) return;
            const int64_t start = g_startNs.load(std::memory_order_relaxed);
            if (start == 0) return;
            const int64_t now = Now();
            const int64_t wall = now - start;
            const int w = WorkerCount();
            const uint64_t busy = SumBusy();
            const double ratio = (wall > 0)
                ? static_cast<double>(busy) / (static_cast<double>(wall) * w) : 0.0;
            std::printf("[E1] TOTAL t=%.1fs workers=%d busy_ratio=%.4f busy=%.1fms wall=%.1fms\n",
                static_cast<double>(wall) / 1e9, w, ratio,
                static_cast<double>(busy) / 1e6, static_cast<double>(wall) / 1e6);
            std::printf("[E1] main_exec_busy_ms=%.1f windows=%llu (main thread tile execution; NOT in busy_ratio)\n",
                static_cast<double>(g_mainBusyNs.load(std::memory_order_relaxed)) / 1e6,
                (unsigned long long)g_mainWindows.load(std::memory_order_relaxed));
            std::printf("[E1] health: bad_windows=%llu orphan_ends=%llu (0 is ideal)\n",
                (unsigned long long)g_badWindows.load(std::memory_order_relaxed),
                (unsigned long long)g_orphanEnds.load(std::memory_order_relaxed));
            std::printf("[E1] tail/wake EWMA(us): submitToFirstWorker=%.2f workerStartSpread=%.2f lastTileToTopologyDone=%.2f completeWakeToReturn=%.2f\n",
                static_cast<double>(g_submitToFirstWorkerEwmaNs.load(std::memory_order_relaxed)) / 1000.0,
                static_cast<double>(g_workerStartSpreadEwmaNs.load(std::memory_order_relaxed)) / 1000.0,
                static_cast<double>(g_lastTileToTopologyDoneEwmaNs.load(std::memory_order_relaxed)) / 1000.0,
                static_cast<double>(g_completeWakeToReturnEwmaNs.load(std::memory_order_relaxed)) / 1000.0);
            const uint64_t totalWall = g_batchTotalWallNs.load(std::memory_order_relaxed);
            const uint64_t totalBusy = g_batchTotalBusyNs.load(std::memory_order_relaxed);
            std::printf("[E1] batches n=%llu totalWall=%.1fms totalBusy=%.1fms meanConc=%.2f (meanConc must be <= workers)\n",
                (unsigned long long)g_batchTotal.load(std::memory_order_relaxed),
                static_cast<double>(totalWall) / 1e6, static_cast<double>(totalBusy) / 1e6,
                totalWall > 0 ? static_cast<double>(totalBusy) / static_cast<double>(totalWall) : 0.0);
            std::printf("[E1] batch by entered-workers (n / wall_ms / busy_ms / meanConcurrency):");
            for (int i = 0; i < 16; ++i)
            {
                const uint64_t c = g_batchCount[i].load(std::memory_order_relaxed);
                if (!c) continue;
                const uint64_t bw = g_batchWallNs[i].load(std::memory_order_relaxed);
                const uint64_t bb = g_batchBusyNs[i].load(std::memory_order_relaxed);
                std::printf(" w%d:n=%llu,wall=%.1f,busy=%.1f,conc=%.2f", i, (unsigned long long)c,
                    static_cast<double>(bw) / 1e6, static_cast<double>(bb) / 1e6,
                    bw > 0 ? static_cast<double>(bb) / static_cast<double>(bw) : 0.0);
            }
            std::printf("\n");
            std::printf("[E1] batch concurrency histogram (avg busy workers per batch, rounded):");
            for (int i = 0; i < 16; ++i)
            {
                const uint64_t c = g_concHist[i].load(std::memory_order_relaxed);
                if (c) std::printf(" c%d:n=%llu", i, (unsigned long long)c);
            }
            std::printf("\n");
            std::printf("[E1] batch by log2(tiles):");
            for (int i = 0; i < 25; ++i)
            {
                const uint64_t c = g_tileCountBatches[i].load(std::memory_order_relaxed);
                if (c) std::printf(" 2^%d:n=%llu,wall=%.1fms", i, (unsigned long long)c,
                    static_cast<double>(g_tileCountWallNs[i].load(std::memory_order_relaxed)) / 1e6);
            }
            std::printf("\n");
            const uint64_t gapN = g_gapCount.load(std::memory_order_relaxed);
            std::printf("[E1] inter-batch gap n=%llu total=%.1fms mean=%.1fus max=%.1fus\n",
                (unsigned long long)gapN,
                static_cast<double>(g_gapNs.load(std::memory_order_relaxed)) / 1e6,
                gapN ? static_cast<double>(g_gapNs.load(std::memory_order_relaxed)) / static_cast<double>(gapN) / 1000.0 : 0.0,
                static_cast<double>(g_gapMaxNs.load(std::memory_order_relaxed)) / 1000.0);
            std::printf("[E1] steal/dispatch: attempts=%llu success=%llu emptyExits=%llu victimScans=%llu stealCount=%llu hotSpin=%llu parkWake=%llu claimedW=%llu claimedM=%llu\n",
                (unsigned long long)g_stealAttempts.load(std::memory_order_relaxed),
                (unsigned long long)g_stealSuccesses.load(std::memory_order_relaxed),
                (unsigned long long)g_stealEmptyExits.load(std::memory_order_relaxed),
                (unsigned long long)g_victimScans.load(std::memory_order_relaxed),
                (unsigned long long)g_stealCount.load(std::memory_order_relaxed),
                (unsigned long long)g_hotSpinHits.load(std::memory_order_relaxed),
                (unsigned long long)g_parkWakeCount.load(std::memory_order_relaxed),
                (unsigned long long)g_workerClaimedTokens.load(std::memory_order_relaxed),
                (unsigned long long)g_mainClaimedTokens.load(std::memory_order_relaxed));
            {
                static const char* kRetireNames[7] = {
                    "cas", "RecordFinalizedTiming", "longBatchBarrier",
                    "RunBatchCleanup", "CompleteState", "RecordTopologyCompletion", "TOTAL_lastTile2topology" };
                std::printf("[E1] retire chain(us/batch):");
                for (int i = 0; i < 7; ++i)
                {
                    const uint64_t n = g_retireCount[i].load(std::memory_order_relaxed);
                    std::printf(" %s=%.2f", kRetireNames[i],
                        n ? static_cast<double>(g_retireNs[i].load(std::memory_order_relaxed))
                            / static_cast<double>(n) / 1000.0 : 0.0);
                }
                std::printf(" (n=%llu max=%.1fus)\n",
                    (unsigned long long)g_retireCount[6].load(std::memory_order_relaxed),
                    static_cast<double>(g_retireMaxNs.load(std::memory_order_relaxed)) / 1000.0);
                std::printf("[E1] retire TOTAL buckets(us): <2:n=%llu(%.0fms) 2-5:n=%llu(%.0fms) 5-20:n=%llu(%.0fms) 20-100:n=%llu(%.0fms) >=100:n=%llu(%.0fms)\n",
                    (unsigned long long)g_retireBucket[0].load(std::memory_order_relaxed), static_cast<double>(g_retireBucketNs[0].load(std::memory_order_relaxed)) / 1e6,
                    (unsigned long long)g_retireBucket[1].load(std::memory_order_relaxed), static_cast<double>(g_retireBucketNs[1].load(std::memory_order_relaxed)) / 1e6,
                    (unsigned long long)g_retireBucket[2].load(std::memory_order_relaxed), static_cast<double>(g_retireBucketNs[2].load(std::memory_order_relaxed)) / 1e6,
                    (unsigned long long)g_retireBucket[3].load(std::memory_order_relaxed), static_cast<double>(g_retireBucketNs[3].load(std::memory_order_relaxed)) / 1e6,
                    (unsigned long long)g_retireBucket[4].load(std::memory_order_relaxed), static_cast<double>(g_retireBucketNs[4].load(std::memory_order_relaxed)) / 1e6);
            }
            std::printf("[E1] worker start spread by entered(us):");
            for (int i = 0; i < 16; ++i)
            {
                const uint64_t n = g_spreadCount[i].load(std::memory_order_relaxed);
                if (n) std::printf(" w%d:mean=%.1f,max=%.1f,n=%llu", i,
                    static_cast<double>(g_spreadNs[i].load(std::memory_order_relaxed)) / static_cast<double>(n) / 1000.0,
                    static_cast<double>(g_spreadMaxNs[i].load(std::memory_order_relaxed)) / 1000.0,
                    (unsigned long long)n);
            }
            std::printf("\n");
            std::printf("[E1] per_worker_busy_ratio=[");
            for (int i = 0; i < w && i < kMaxTrackedWorkers; ++i)
                std::printf("%s%.3f", i ? " " : "",
                    wall > 0 ? static_cast<double>(g_busyNs[i].load(std::memory_order_relaxed)) / static_cast<double>(wall) : 0.0);
            std::printf("]\n");
            std::fflush(stdout);
        }
    }
    // ---------- State lifecycle ----------
    // 无锁 continuation 节点：fn 完整构造后才 CAS 入原子槽（无发布竞态）。
    // CompleteState 摘取后执行并 delete。槽位 ≤1 节点，CAS 只对 nullptr 比较，
    // 无 Treiber 栈的 ABA 问题（不会拿陈旧节点指针做比较）。
    struct ContinuationNode {
        std::function<void()> fn;
        ContinuationNode* next{ nullptr };
    };

    // 执行并释放一条 continuation 链（含单个节点）；异常吞掉。
    static void RunContinuationChain(ContinuationNode* head) noexcept
    {
        while (head)
        {
            ContinuationNode* next = head->next;
            if (head->fn) { try { head->fn(); } catch (...) {} }
            delete head;
            head = next;
        }
    }

    // 兜底取回 state 上可能残留的 continuation（正常路径 CompleteState 已摘尽；
    // 仅供 RecycleState 防泄漏）。
    static void DrainContinuationSlot(HandleState* state) noexcept
    {
        if (auto* leftover = state->continuationSlot.exchange(nullptr, std::memory_order_acq_rel))
            RunContinuationChain(leftover);
    }

    // 线程槽：判定"创建"与"回收"是否落在同一批线程上（TLS 缓存命中率只有 7% 的真因）。
    static uint32_t StateThreadSlot() noexcept
    {
        thread_local const uint32_t slot = static_cast<uint32_t>(
            std::hash<std::thread::id>{}(std::this_thread::get_id()) % kStateThreadSlots);
        return slot;
    }

    void RecycleState(HandleState* state) noexcept
    {
        if (!state) return;
        g_liveHandleStates.fetch_sub(1, std::memory_order_relaxed);
        // 性能项 3：state 创建/回收是 plain IJob 的必经路径（每 job 各一次），下面三个计数
        // 都是纯诊断 RMW（只被 GetStatsSnapshot 读取）。默认统计开启 ⇒ 计数口径与改动前逐位一致。
        if (StatsEnabled())
        {
            g_stateRecycled.fetch_add(1, std::memory_order_relaxed);
            g_stateRecycleByThread[StateThreadSlot()].fetch_add(1, std::memory_order_relaxed);
            if (WorkerIndexManager::GetCurrentIndex() >= 0)
                g_stateRecycledOnWorker.fetch_add(1, std::memory_order_relaxed);
        }
        // 释放依赖链持有引用（依赖 state 可能仍被自身 batch 持有，不会悬垂）。
        if (state->dependency)
        {
            auto* dep = state->dependency;
            state->dependency = nullptr;
            ReleaseState(dep);
        }
        for (auto* dep : state->dependencies)
            ReleaseState(dep);
        state->dependencies.clear();
        DrainContinuationSlot(state);
        state->hasExtraContinuations.store(false, std::memory_order_relaxed);
        state->continuations.clear();
        // 性能项 3：仅当确有异常被记录过才取 exceptionMutex（每 job 回收一次的无谓加锁）。
        // 安全：RecycleState 只在 refCount 归零（ReleaseState 的 acq_rel fetch_sub 返回 1）时
        // 被调用，而记录方必须持有引用才能写 batchExceptionPtr ⇒ 其 release 与本次 acquire 同步，
        // hasException 不可能读到陈旧 false。
        if (state->hasException.load(std::memory_order_acquire))
        {
            std::lock_guard<std::mutex> lock(state->exceptionMutex);
            state->batchExceptionPtr = nullptr;
        }
        state->hasException.store(false, std::memory_order_relaxed);
        state->diagnosticBatchId.store(0, std::memory_order_relaxed);
        state->completed.store(false, std::memory_order_relaxed);
        state->backendRetired.store(true, std::memory_order_relaxed);
        state->refCount.store(1, std::memory_order_relaxed);
        // Handles released after Shutdown cannot be reused by a later scheduler
        // generation. Destroy them directly instead of repopulating the pool
        // after Shutdown has already drained it.
        if (g_shuttingDown.load(std::memory_order_acquire))
        {
            delete state;
            return;
        }
        // 先入 per-thread 缓存；满额时一次性迁移共享池（一次锁 / 64 次回收）。
        // 【性能项 3】进入 TLS 缓存的条件从"本线程调用过 CreateState"放宽为
        // "创建者 **或 worker 线程**"：worker 是回收热路径的主角（每个 job 的
        // ReleaseState 都发生在 worker 上），但它们几乎从不调用 CreateState
        // ⇒ 旧条件下每个 job 回收都要取一次全局 g_statePoolMutex。
        // 不无条件放宽的理由（保留原设计意图）：.NET 终结器线程 / 渲染线程等
        // 长期存活且从不创建 state 的线程若也进 TLS 缓存，被回收的 state 会
        // 永久堆在它们的缓存里，调度线程永远命中不到 → CreateState 退回每次 new。
        if (!t_stateCreator && WorkerIndexManager::GetCurrentIndex() < 0)
        {
            std::lock_guard<std::mutex> lock(g_statePoolMutex);
            if (g_statePool.size() < kMaxPooledStates)
                g_statePool.push_back(state);
            else
                delete state;
            return;
        }
        if (t_stateCache.entries.size() < (t_stateCreator ? kStateCacheCap : kStateCacheCapNonCreator))
        {
            t_stateCache.entries.push_back(state);
            return;
        }
        FlushStateCacheToSharedPool();
        t_stateCache.entries.push_back(state);
    }

    HandleState* CreateState(bool completed)
    {
        t_stateCreator = true;
        // 性能项 3：入门一次 relaxed 载入，旁路本函数内全部 4 个纯诊断 RMW（池命中/补池/新分配/线程槽）。
        const bool stats = StatsEnabled();
        if (stats)
            g_stateCreateByThread[StateThreadSlot()].fetch_add(1, std::memory_order_relaxed);
        HandleState* state = nullptr;
        if (!t_stateCache.entries.empty())
        {
            state = t_stateCache.entries.back();
            t_stateCache.entries.pop_back();
            if (stats)
                g_statePoolHit.fetch_add(1, std::memory_order_relaxed);
        }
        else
        {
            // 从共享池批量补满线程缓存（一次锁 / 64 次创建），池空则 new。
            std::lock_guard<std::mutex> lock(g_statePoolMutex);
            const size_t available = std::min(g_statePool.size(), kStateCacheCap);
            if (available > 0)
            {
                state = g_statePool.back();
                g_statePool.pop_back();
                for (size_t i = 1; i < available; ++i)
                {
                    t_stateCache.entries.push_back(g_statePool.back());
                    g_statePool.pop_back();
                }
                if (stats)
                    g_statePoolRefill.fetch_add(1, std::memory_order_relaxed);
            }
            else
            {
                if (stats)
                    g_statePoolNew.fetch_add(1, std::memory_order_relaxed);
            }
        }
        if (!state) state = new HandleState(completed);
        state->refCount.store(1, std::memory_order_relaxed);
        state->completed.store(completed, std::memory_order_relaxed);
        state->backendRetired.store(true, std::memory_order_relaxed);
        state->diagnosticBatchId.store(0, std::memory_order_relaxed);
        state->continuationSlot.store(nullptr, std::memory_order_relaxed);
        state->hasExtraContinuations.store(false, std::memory_order_relaxed);
        state->estBatchNs = 0;   // §7aq：状态复用前必须清零，否则会继承上一批的预期时长
        state->continuations.clear();
        state->hasException.store(false, std::memory_order_relaxed);
        state->dependency = nullptr;
        state->dependencies.clear();
        g_liveHandleStates.fetch_add(1, std::memory_order_relaxed);
        return state;
    }

    // 把依赖 state 挂到被依赖 state 上并持引用，保证传递协助链不会悬垂。
    // 释放点在 RecycleState（refcount 归零时）。仅在依赖未完成（需要等）时调用。
    void RetainDependency(HandleState* state, HandleState* dep) noexcept
    {
        if (!state || !dep) return;
        AcquireState(dep);
        state->dependency = dep;
    }

    void AcquireState(HandleState* state) noexcept
    {
        if (state) state->refCount.fetch_add(1, std::memory_order_relaxed);
    }

    void ReleaseState(HandleState* state) noexcept
    {
        if (state && state->refCount.fetch_sub(1, std::memory_order_acq_rel) == 1)
            RecycleState(state);
    }

    void RecordStateException(HandleState* state, std::exception_ptr exception) noexcept
    {
        if (!state || !exception) return;
        try
        {
            std::lock_guard<std::mutex> lock(state->exceptionMutex);
            if (!state->batchExceptionPtr)
            {
                state->batchExceptionPtr = std::move(exception);
                // 性能项 3：release 发布"有异常"标志，供 RecycleState 无锁跳过 exceptionMutex。
                state->hasException.store(true, std::memory_order_release);
            }
        }
        catch (...)
        {
            // Exception recording is best effort if allocation/locking itself
            // fails.  The worker must still reach its completion protocol.
        }
    }

    std::exception_ptr TakeStateException(HandleState* state) noexcept
    {
        if (!state) return {};
        try
        {
            std::lock_guard<std::mutex> lock(state->exceptionMutex);
            auto exception = state->batchExceptionPtr;
            state->batchExceptionPtr = nullptr;
            state->hasException.store(false, std::memory_order_release);
            return exception;
        }
        catch (...)
        {
            return {};
        }
    }

    std::mutex g_longBatchBarrierMutex;
    std::vector<HandleState*> g_longBatchBarriers;
    std::atomic<uint32_t> g_longBatchBarrierCount{ 0 };
    thread_local HandleState* g_completingBatchState = nullptr;

    void RegisterLongBatchBarrier(HandleState* state) noexcept
    {
        if (!state || state->backendRetired.load(std::memory_order_acquire))
            return;
        AcquireState(state);
        try
        {
            std::lock_guard<std::mutex> lock(g_longBatchBarrierMutex);
            g_longBatchBarriers.push_back(state);
            // 与列表在同一把锁内同增，保证 count==0 ⇒ 列表必空（保守真值）。
            g_longBatchBarrierCount.fetch_add(1, std::memory_order_release);
        }
        catch (...)
        {
            // 入列失败（分配异常）时平衡上面的 AcquireState，避免引用泄漏。
            ReleaseState(state);
        }
    }

    static void WaitBackendRetired(HandleState* state) noexcept;   // 定义见 Complete 段（含兜底唤醒看门狗）

    void ConsumeLongBatchBarriers() noexcept
    {
        // 性能项 4：无锁短路。长批 barrier 是罕见事件（批墙钟 > 800 µs 才登记），
        // 但本函数在**每次** Schedule/Complete 上被调用，此前无条件取全局互斥体 +
        // 两个 vector 的构造/析构。绝大多数提交 count==0 ⇒ 直接返回。
        // 语义不变：stale-0 最多让已登记的 barrier 延后到下一次 Flush/Shutdown 消费
        //（与"没有下一次提交就不消费"的既有行为等价，不产生泄漏也不阻塞退役）。
        if (g_longBatchBarrierCount.load(std::memory_order_acquire) == 0)
            return;

        std::vector<HandleState*> barriers;
        std::vector<HandleState*> deferred;
        {
            std::lock_guard<std::mutex> lock(g_longBatchBarrierMutex);
            barriers.swap(g_longBatchBarriers);
            // 与 swap 同锁：并发登记者的 fetch_add 要么在本行之前（列表已含它）要么在其之后，
            // 两种情况 count 都等于列表实际长度，不会丢计数。
            g_longBatchBarrierCount.fetch_sub(
                static_cast<uint32_t>(barriers.size()), std::memory_order_release);
        }
        for (auto* state : barriers)
        {
            if (state == g_completingBatchState)
            {
                deferred.push_back(state);
                continue;
            }
            WaitBackendRetired(state);   // defer 窗口补广播 + 等待退役
            ReleaseState(state);
        }
        if (!deferred.empty())
        {
            std::lock_guard<std::mutex> lock(g_longBatchBarrierMutex);
            g_longBatchBarriers.insert(
                g_longBatchBarriers.end(), deferred.begin(), deferred.end());
            g_longBatchBarrierCount.fetch_add(
                static_cast<uint32_t>(deferred.size()), std::memory_order_release);
        }
    }

    void CompleteState(HandleState* state)
    {
        if (!state) return;
        if (state->completed.exchange(true, std::memory_order_acq_rel)) return;

        // 无锁快路径：原子摘取 continuation 槽（≤1 节点）。completed 先置位再摘取，
        // 保证 AddContinuationOrRunNow 的 G2 重检能看到本摘取已发生或未发生。
        ContinuationNode* node =
            state->continuationSlot.exchange(nullptr, std::memory_order_acq_rel);
        state->completed.notify_all();
        state->completedCv.notify_all();
        if (node) RunContinuationChain(node);

        // 多 continuation（同 handle 扇出）溢出到 mtx + vector。hasExtra 原子跳过空
        // 路径，使单 continuation 的常见完成路径零 mutex。
        if (state->hasExtraContinuations.exchange(false, std::memory_order_acq_rel))
        {
            std::vector<std::function<void()>> extra;
            {
                std::lock_guard<std::mutex> lock(state->mtx);
                extra.swap(state->continuations);
            }
            for (auto& cont : extra)
                if (cont) { try { cont(); } catch (...) {} }
        }
    }

    void AddContinuationOrRunNow(HandleState* state, std::function<void()> continuation)
    {
        if (!state || state->completed.load(std::memory_order_acquire))
        {
            if (continuation) continuation();
            return;
        }
        // 无锁快路径：单 continuation 直接 CAS 入原子槽。fn 先完整 move 进节点再发布，
        // 无数据竞态；CAS 失败时 move 回调用方走慢路径。
        auto* node = new ContinuationNode{ {}, nullptr };
        node->fn.swap(continuation);
        ContinuationNode* expected = nullptr;
        if (state->continuationSlot.compare_exchange_strong(
            expected, node, std::memory_order_acq_rel, std::memory_order_relaxed))
        {
            // 发布后已完成：Completer 可能已摘取本节点（正常执行），也可能漏掉
            // （摘取早于本 CAS）——此时自己取回并执行，保证每节点恰执行一次。
            if (state->completed.load(std::memory_order_acquire))
            {
                if (auto* mine = state->continuationSlot.exchange(nullptr, std::memory_order_acq_rel))
                    RunContinuationChain(mine);
            }
            return;
        }
        continuation.swap(node->fn);
        delete node;

        // 慢路径：槽已占（第 2+ 个 continuation）。mtx 内判 completed，完成后不再入列。
        std::function<void()> toRun;
        {
            std::lock_guard<std::mutex> lock(state->mtx);
            if (state->completed.load(std::memory_order_acquire)) toRun = std::move(continuation);
            else state->continuations.emplace_back(std::move(continuation));
        }
        if (toRun) { toRun(); return; }
        // 已入列。若 CompleteState 的 hasExtra 摘取早于本发布而漏检（completed 已置位），
        // 取回自己的条目执行；向量已空说明被 Completer 取走，不会重复。
        state->hasExtraContinuations.store(true, std::memory_order_release);
        if (state->completed.load(std::memory_order_acquire))
        {
            std::function<void()> mine;
            {
                std::lock_guard<std::mutex> lock(state->mtx);
                if (!state->continuations.empty())
                {
                    mine = std::move(state->continuations.back());
                    state->continuations.pop_back();
                    if (state->continuations.empty())
                        state->hasExtraContinuations.store(false, std::memory_order_release);
                }
            }
            if (mine) { try { mine(); } catch (...) {} }
        }
    }

    struct BackendAsyncContext
    {
        std::function<void()> work;
        HandleState* state{ nullptr };
    };

    // ============================================================
    // 性能项 2（2026-09-26）：BackendAsyncContext 池化（原：每 job 一次 `new` + `delete`）
    //
    // 动机（实测）：plain IJob / IJobFor(≤64) 走 SubmitBackendAsync ⇒ 每次提交
    // `new BackendAsyncContext` + 执行完 `delete`。该路径 `probe ijob` 实测
    // sched ≈ 800～900 ns/job（N=1000/10000 individual pipelined），其中堆分配/释放是
    // 固定且可省的一项；对象生命周期与 job 一一对应、无跨 job 存活需求 ⇒ 可安全复用。
    //
    // 形状完全复用同仓库既有两级池（JobSystem_Tiles.cpp:300-355 的 BatchContext、
    // JobSystem_State.cpp:108-216 的 HandleState）：
    //   - 对象在**提交线程**（多为 main）获取、由**执行完成该 job 的 worker** 释放 ⇒
    //     只有"线程本地缓存 + 共享池兜底"才能让提交线程命中（worker 释放的实例经共享池回流）。
    //   - 线程退出时 TLS 缓存整体交还共享池（worker 线程在 Shutdown 的 Stop()/join 期间退出，
    //     此后 ClearAsyncContextPool 统一删除）。
    // 语义不变：work/state 每次 acquire 时覆盖赋值（复用 std::function 对象本身，省掉一次
    // 目标存储构造），cleanup 回调与异常捕获路径完全未改。
    // ============================================================
    std::mutex g_asyncCtxPoolMutex;
    std::vector<BackendAsyncContext*> g_asyncCtxPool;
    constexpr size_t kAsyncCtxCacheCap = 16;
    constexpr size_t kMaxPooledAsyncCtx = 256;

    struct ThreadAsyncCtxCache
    {
        std::vector<BackendAsyncContext*> entries;
        ~ThreadAsyncCtxCache()
        {
            if (entries.empty()) return;
            std::lock_guard<std::mutex> lock(g_asyncCtxPoolMutex);
            for (auto* p : entries)
            {
                if (g_asyncCtxPool.size() < kMaxPooledAsyncCtx) g_asyncCtxPool.push_back(p);
                else delete p;
            }
            entries.clear();
        }
    };
    thread_local ThreadAsyncCtxCache t_asyncCtxCache;

    static void SpillAsyncCtxCacheToSharedPool()
    {
        if (t_asyncCtxCache.entries.empty()) return;
        std::lock_guard<std::mutex> lock(g_asyncCtxPoolMutex);
        for (auto* p : t_asyncCtxCache.entries)
        {
            if (g_asyncCtxPool.size() < kMaxPooledAsyncCtx) g_asyncCtxPool.push_back(p);
            else delete p;
        }
        t_asyncCtxCache.entries.clear();
    }

    // Shutdown 路径：主线程缓存先交还共享池，再清空共享池（与 BatchContext/BatchStorage 同序）。
    void FlushAsyncContextCacheToSharedPool()
    {
        SpillAsyncCtxCacheToSharedPool();
    }

    void ClearAsyncContextPool() noexcept
    {
        std::vector<BackendAsyncContext*> idle;
        try
        {
            std::lock_guard<std::mutex> lock(g_asyncCtxPoolMutex);
            idle.swap(g_asyncCtxPool);
        }
        catch (...) { return; }
        for (auto* p : idle) delete p;
    }

    static BackendAsyncContext* AcquireAsyncContext()
    {
        BackendAsyncContext* ctx = nullptr;
        if (!t_asyncCtxCache.entries.empty())
        {
            ctx = t_asyncCtxCache.entries.back();
            t_asyncCtxCache.entries.pop_back();
            return ctx;
        }
        {
            std::lock_guard<std::mutex> lock(g_asyncCtxPoolMutex);
            if (!g_asyncCtxPool.empty())
            {
                ctx = g_asyncCtxPool.back();
                g_asyncCtxPool.pop_back();
                // 一次性补满线程缓存（一次锁 / 最多 kAsyncCtxCacheCap 次获取）
                for (size_t i = 1; i < kAsyncCtxCacheCap && !g_asyncCtxPool.empty(); ++i)
                {
                    t_asyncCtxCache.entries.push_back(g_asyncCtxPool.back());
                    g_asyncCtxPool.pop_back();
                }
                return ctx;
            }
        }
        return new BackendAsyncContext{};
    }

    static void ReleaseAsyncContext(BackendAsyncContext* ctx) noexcept
    {
        if (!ctx) return;
        // 释放捕获（含 C# 侧闭包）并清 state，避免复用对象把上一次 job 的引用带过去。
        ctx->work = nullptr;
        ctx->state = nullptr;
        try
        {
            if (t_asyncCtxCache.entries.size() < kAsyncCtxCacheCap)
            {
                t_asyncCtxCache.entries.push_back(ctx);
                return;
            }
            SpillAsyncCtxCacheToSharedPool();
            t_asyncCtxCache.entries.push_back(ctx);
        }
        catch (...)
        {
            delete ctx;   // 池化失败绝不吞掉实例（否则内存泄漏）
        }
    }

    static void RunBackendAsync(void* raw) noexcept
    {
        auto* context = static_cast<BackendAsyncContext*>(raw);
        try
        {
            context->work();
        }
        catch (...)
        {
            // 正常路径由操作自身收尾完成；此为意外异常的兜底边界：保持句柄终结状态
            // 并释放在飞引用，避免 Complete() 永久阻塞。
            if (context->state)
            {
                RecordStateException(context->state, std::current_exception());
                try { CompleteState(context->state); } catch (...) {}
            }
        }
        if (context->state)
            ReleaseState(context->state);
    }

    static void CompleteBackendAsync(void* raw) noexcept
    {
        // 性能项 2：原来在这里 `delete`，现改为交还池（复用对象，同上注释）。
        ReleaseAsyncContext(static_cast<BackendAsyncContext*>(raw));
    }

    bool SubmitBackendAsync(
        std::function<void()> work,
        HandleState* state,
        void (*failureCleanup)(void*),
        void* failureContext) noexcept
    {
        BackendAsyncContext* context = nullptr;
        try
        {
            auto scheduler = LoadChaseLevScheduler();
            if (!scheduler || !scheduler->IsRunning())
                throw std::runtime_error("JobSystem backend is not running");

            context = AcquireAsyncContext();
            if (!context)
                throw std::bad_alloc();
            context->state = state;
            context->work = std::move(work);
            // 统一走 Chase-Lev SubmitWork：worker 异步执行，不阻塞调用线程。
            // SubmitWork 内部 PushTaskBackoff 有限退避，injector 满时短暂自旋。
            if (!scheduler->SubmitWork(
                    &RunBackendAsync, context, &CompleteBackendAsync))
            {
                CompleteBackendAsync(context);
                context = nullptr;
                throw std::runtime_error("JobSystem backend rejected asynchronous work");
            }
            // Ownership of context and the acquired state reference now belongs
            // to the queued RangeTask/RunBackendAsync wrapper.
            return true;
        }
        catch (...)
        {
            if (context)
                CompleteBackendAsync(context);
            if (state)
            {
                // 调用方在进入前已取得在飞引用，所有失败路径必须消费；清理先于发布
                // 完成执行，调用方不会观察到句柄已终结而 context 仍存活。
                RecordStateException(state, std::current_exception());
                if (failureCleanup)
                {
                    try
                    {
                        failureCleanup(failureContext);
                    }
                    catch (...)
                    {
                        RecordStateException(state, std::current_exception());
                    }
                }
                try { CompleteState(state); }
                catch (...) { RecordStateException(state, std::current_exception()); }
                ReleaseState(state);
            }
            else if (failureCleanup)
            {
                try { failureCleanup(failureContext); }
                catch (...) {}
            }
            return false;
        }
    }

    int ResolveChunkSize(int length, int requestedChunk)
    {
        return ResolveChunkSize(length, requestedChunk, 0);
    }

    int ResolveChunkSize(int length, int requestedChunk, uint32_t funcHash,
        bool* outJccFine)
    {
        if (outJccFine) *outJccFine = false;
        if (length <= 0) return 1;
        if (requestedChunk > 0) return requestedChunk;
        int wc = std::max(1, g_numThreads.load(std::memory_order_relaxed));

        // 自动 batch：仅当成本缓存开启且有该 job 成本数据时（热路径开销极小）。
        if (funcHash != 0 && g_jobCostCacheEnabled.load(std::memory_order_relaxed))
        {
            // ---- 带宽/延迟绑定自适应 ----
            // memory-bound job 总耗时由共享 DRAM 带宽主导，按元素成本推 tile 数会错标 →
            // 固定 tpw；compute-bound 走下方公式。两阶段学习：先采粗样本，再以粗成本
            // 为代理产细样本，TryClassify 定 mode（mem-bound / parallel）。
            const auto mode = g_jobCostCache.GetMode(funcHash);
            const bool robustNow = g_jobCostCache.IsRobust();
            const int tpwChunk = std::max(16, CeilDiv(length, wc * g_configuredTilesPerWorker.load(std::memory_order_relaxed)));
            if (mode == JobSystem::kModeMemBound)
            {
                // 健壮模式：mem-bound 期**周期探针**（每 kRobustProbeInterval 次解析放一次公式分块）。
                // 否则锁死后 mem-bound 分支永不产出细样本 ⇒ 判错就永久错、无法纠正（07 §7an）。
                const bool probe = robustNow && g_jobCostCache.ProbeDue(funcHash);
                if (!probe)
                {
                    if (g_jobCostCacheVerbose)
                        std::printf("[JCC] R length=%d MEM-BOUND → tpw chunk\n", length);
                    return tpwChunk;
                }
                if (g_jobCostCacheVerbose)
                    std::printf("[JCC] R length=%d MEM-BOUND PROBE → formula\n", length);
            }
            else if (robustNow && mode == JobSystem::kModeUnknown)
            {
                // ⚠ 关键（07 §7an 第二轮）：未分类期必须**交错**放粗/细两种分块。
                // 旧两阶段是"先 3 个粗样本、之后永远走公式"⇒ 重 job 的粗样本永远停在 3 个，
                // 够不到健壮判据的样本下限 ⇒ mode 永为 unknown ⇒ 永远走公式（= 永远细粒度）
                // ⇒ grad 受益但 Melee 受损，净收益被抵消（实测正好如此）。
                if (!g_jobCostCache.ParityProbe(funcHash))
                {
                    if (g_jobCostCacheVerbose)
                        std::printf("[JCC] R length=%d UNKNOWN → tpw chunk (parity coarse)\n", length);
                    return tpwChunk;
                }
                if (g_jobCostCacheVerbose)
                    std::printf("[JCC] R length=%d UNKNOWN → formula (parity fine)\n", length);
            }
            const double perElemNs = g_jobCostCache.GetPerElemCost(funcHash);
            if (!robustNow && mode == JobSystem::kModeUnknown && !g_jobCostCache.HasLearnedCoarse(funcHash))
            {
                // 阶段 1：粗样本未齐 → tpw（perElemNs 通常为 0，本分支与兜底一致）
                return tpwChunk;
            }
            // 阶段 2（或 parallel 稳态）：细成本优先，缺省用粗成本代理（学习中/冷启动）
            double costNs = perElemNs;
            if (costNs <= 0.0) costNs = g_jobCostCache.GetCoarseCost(funcHash);
            if (costNs > 0.0)
            {
                constexpr double kTargetTileUs = 150.0;     // 目标每 tile 串行量
                constexpr int kMaxAdaptiveTpw = 16;         // tiles 上限 = workers×16
                constexpr int kMaxAutoChunk = 32768;        // 单 tile 最多 32k 元素
                constexpr double kSchedulingOverheadNs = 16000.0;  // ~16μs per tile
                const int chunkTpw4 = std::max(16, CeilDiv(length, wc * g_configuredTilesPerWorker.load(std::memory_order_relaxed)));

                // ── 两因子（C_fixed 每 tile 固定 + C_elem 每元素）优先 ──
                const double cfixed = g_jobCostCache.GetPerTileCost(funcHash);
                const double celem = perElemNs > 0.0 ? perElemNs : costNs;
                if (cfixed > 0.0 && celem > 0.0)
                {
                    // 空体/超轻：执行≈0，总成本由调度/唤醒/worker 抖动主导，
                    // 任何执行成本模型都无解 → tpw 兜底。
                    const double tileTimeTpw =
                        cfixed + (static_cast<double>(length) / (wc * g_configuredTilesPerWorker.load(std::memory_order_relaxed))) * celem;
                    if (tileTimeTpw < kSchedulingOverheadNs)
                    {
                        // 仍按"公式产出"登记细样本：使细/粗比值≈1 → mem-bound 分类 →
                        // 稳态固定 tpw，且细 EWMA 有值（JccConcurrentHeterogeneous 断言 perElem>0）。
                        // 健壮模式下如实标注为**粗样本**（它返回的就是 tpw chunk）。
                        if (outJccFine) *outJccFine = g_jobCostCache.IsRobust() ? false : true;
                        return chunkTpw4;
                    }
                    // 执行主导：目标每 tile ≈150µs，tileSize = (target − C_fixed)/C_elem，
                    // 下限 256 元素/tile 防 C_fixed 占比过高。
                    double tileSize = (kTargetTileUs * 1000.0 - cfixed) / celem;
                    if (tileSize < 256.0) tileSize = 256.0;
                    int targetTiles = static_cast<int>(length / tileSize + 0.9999);
                    if (targetTiles < wc) targetTiles = wc;
                    if (targetTiles > wc * kMaxAdaptiveTpw) targetTiles = wc * kMaxAdaptiveTpw;
                    const int chunk2f = std::max(1, CeilDiv(length, targetTiles));
                    // 健壮模式：只有**严格细于** tpw 兜底的分块才算细样本（旧代码把 15 tiles 这种
                    // 比 tpw 的 60 还粗 4× 的分块也记成细样本 ⇒ 系统性推高比值 ⇒ 全判 mem-bound）。
                    if (outJccFine) *outJccFine = g_jobCostCache.IsRobust() ? (chunk2f < chunkTpw4) : true;
                    return chunk2f;
                }

                // ── 单因子回退（冷启动，C_fixed 未学）：既有公式 ──
                if (outJccFine && !robustNow) *outJccFine = true;   // JCC 公式产出（细粒度学习样本）
                const double totalUs = length * costNs / 1000.0;
                // perElem 是「并行 wall 稀释」成本，直接用它算 tiles 会产出巨型 tile
                // 「串行总量」：totalUs × wc ≈ 单 worker 串行所需时间。
                const double serialUs = totalUs * wc;
                double targetTilesD = std::clamp(serialUs / kTargetTileUs, 1.0,
                    static_cast<double>(wc) * kMaxAdaptiveTpw);
                int targetTiles = static_cast<int>(targetTilesD);
                if (targetTiles < 1) targetTiles = 1;
                // 安全护栏：单 tile 元素数上限（kMaxAutoChunk）。
                int floorTiles = CeilDiv(length, kMaxAutoChunk);
                if (floorTiles > wc) floorTiles = wc;
                if (targetTiles < floorTiles) targetTiles = floorTiles;
                int chunk = std::max(1, CeilDiv(length, targetTiles));
                // Floor：chunk 不比 tpw 兜底更粗，防止快 job 退化（tpw 冗余吸收 worker 抖动）。
                double tileTimeNs = costNs * chunkTpw4;
                bool schedulingDominated = (tileTimeNs < kSchedulingOverheadNs);
                double jccTiles = length * costNs * wc / (kTargetTileUs * 1000.0);
                bool loadBalancingOK = (jccTiles >= wc);
                if (!schedulingDominated || !loadBalancingOK) {
                    chunk = std::min(chunk, chunkTpw4);
                }
                if (g_jobCostCacheVerbose)
                    std::printf("[JCC] R length=%d perElem=%.2fns totalUs=%.1f serialUs=%.1f formula=%d floor=%d chunk=%d rc=%d\n",
                        length, costNs, totalUs, serialUs, (int)(serialUs / kTargetTileUs),
                        floorTiles, chunk, CeilDiv(length, chunk));
                // 健壮模式：只有严格细于 tpw 兜底的分块才算细样本（见上面的理由）。
                if (outJccFine && robustNow) *outJccFine = (chunk < chunkTpw4);
                return chunk;
            }
        }
        // 冷启动 / flag 关闭 / 无数据 → tpw 兜底：batch = N/(W×k) 随 N 自动缩放，
        // 无需每 job 标代价。
        return std::max(16, CeilDiv(length, wc * g_configuredTilesPerWorker.load(std::memory_order_relaxed)));
    }

    // ============================================================
    // JobHandle
    // ============================================================
    JobHandle::JobHandle(HandleState* state, bool addRef) noexcept : _state(state) {
        if (addRef) Acquire(_state);
    }
    JobHandle::JobHandle(const JobHandle& other) noexcept : _state(other._state) { Acquire(_state); }
    JobHandle::JobHandle(JobHandle&& other) noexcept : _state(other._state) { other._state = nullptr; }
    JobHandle& JobHandle::operator=(const JobHandle& other) noexcept {
        if (this != &other) { Acquire(other._state); Release(_state); _state = other._state; }
        return *this;
    }
    JobHandle& JobHandle::operator=(JobHandle&& other) noexcept {
        if (this != &other) { Release(_state); _state = other._state; other._state = nullptr; }
        return *this;
    }
    JobHandle::~JobHandle() { Release(_state); }

    void JobHandle::Acquire(HandleState* state) noexcept {
        if (state) state->refCount.fetch_add(1, std::memory_order_relaxed);
    }
    void JobHandle::Release(HandleState* state) noexcept {
        if (state && state->refCount.fetch_sub(1, std::memory_order_acq_rel) == 1)
            RecycleState(state);
    }

    // CpuPause() defined in CpuPause.h (unity build safe)

    // Chase-Lev 退役是异步的（completed 由最后 tile 设置，退役由最后 taskDone 触发）。
    // Complete() 返回前等 backendRetired，保证"Complete 后 batch 已完全退役"
    // （cleanup/存储回收已完成）——测试与用户代码依赖这一契约。
    // 存在未关闭 deferNotify 窗口时补广播（C++ Complete 不自动 Flush，
    // defer 窗口内提交的 job 可能无人唤醒，这里补一次）。
    static void WaitBackendRetired(HandleState* state) noexcept
    {
        if (!state) return;
        if (state->backendRetired.load(std::memory_order_acquire))
            return;
        if (g_submitDeferDepth.load(std::memory_order_relaxed) > 0)
        {
            if (auto scheduler = LoadChaseLevScheduler())
                scheduler->WakePending();
        }
        while (!state->backendRetired.load(std::memory_order_acquire))
            state->backendRetired.wait(false, std::memory_order_relaxed);
    }

    // C++ 异常协议：Complete 的每个退出点在等退役后调用；异常通过冷路径
    // mutex 一次性摘取，多个 Complete 调用不会并发读写 exception_ptr。
    static void RethrowBatchException(HandleState* state)
    {
        auto ex = TakeStateException(state);
        if (ex) std::rethrow_exception(ex);
    }

    void JobHandle::Complete() const
    {
        if (!_state) return;
        // `ENTJOY_DEFER_WAKE=1`：调用方即将阻塞 ⇒ 在这里补一次被推迟的唤醒广播
        // （把唤醒成本挪进"无论如何都要等"的窗口里，见 JobSystemInternal.h 的动机注释）。
        FlushDeferredWake();

        const bool diag = DiagPhase::Enabled();
        uint64_t mark = 0;
        if (diag)
        {
            DiagPhase::g_entries.fetch_add(1, std::memory_order_relaxed);
            mark = MonotonicNowNs();
        }
        // 统一收尾（原实现有 6 处相同的"等退役 → 取异常 → return"）。
        //   exitSlot       = "等到 completed 是在哪一段"（spin2048 / spin256 / blockWait / fastpath）——
        //                    这是判定"每 job 的等待到底是自旋还是阻塞"的关键：自旋等待有上界（µs 级），
        //                    一旦落到 blockWait 就是"等了 256 µs 超时轮"的量级。
        //   WaitRetired 段 = 等 backendRetired（cleanup + 存储回收）
        //   ExcCheck 段    = 摘取并重抛 job 异常
        auto finish = [&](int exitSlot) {
            if (diag)
            {
                uint64_t n = MonotonicNowNs();
                DiagPhase::Add(exitSlot, n - mark);
                mark = n;
            }
            WaitBackendRetired(_state);
            if (diag)
            {
                uint64_t n = MonotonicNowNs();
                DiagPhase::Add(DiagPhase::WaitRetired, n - mark);
                mark = n;
            }
            RethrowBatchException(_state);
            if (diag) DiagPhase::Add(DiagPhase::ExcCheck, MonotonicNowNs() - mark);
        };

        const uint64_t diagnosticId =
            _state->diagnosticBatchId.load(std::memory_order_acquire);
        if (diagnosticId != 0)
            PushTraceEvent(TraceEventType::CompleteEnter, diagnosticId, -1, 0, 0);

        if (_state->completed.load(std::memory_order_acquire))
        {
            if (diag)
            {
                uint64_t n = MonotonicNowNs();
                DiagPhase::Add(DiagPhase::FastPath, n - mark);
                mark = n;
            }
            finish(DiagPhase::FastPath);
            return;
        }

        // Chase-Lev 唯一路径：主线程不参与 tile 级协助计数（tiles 在持久
        // deque），直接进入 spin/wait，由 worker 完成退役并置 completed。

        // Phase 2: 先密集 spin（过早 yield 触发完整 OS 上下文切换）。
        // Chase-Lev：主线程 spin 期间即协助认领执行，消除"慢 worker 被抢占
        // + 主线程干等"的尾延迟。
        // A/B（`ENTJOY_COMPLETE_SPIN=<pause 次数>`，默认 2048 = 原行为；`0` = 直接进 Phase 3）：
        // 主线程第一段自旋窗。动机（docs §7ai）：真实负载上主线程 **82.8% 的时间在 Complete() 里**，
        // 其中 `spin2048` = 54,050 次 × 45.4 µs = **2.45 s ≈ 14.8 ms/步**；而 15 个 worker 只用 15 个逻辑核
        // ⇒ 主线程自旋会与某个 worker 抢 SMT 执行单元。旋钮用于量"自旋 vs 让核"的平衡点。
        static const int kMainSpin = [] {
            const char* v = std::getenv("ENTJOY_COMPLETE_SPIN");
            const int n = (v != nullptr) ? std::atoi(v) : 2048;
            return n < 0 ? 0 : (n > 65536 ? 65536 : n);
        }();
        // ── §7aq 自适应自旋（`ENTJOY_COMPLETE_SPIN_ADAPT=1`，默认关 ⇒ 逐位原行为）──
        // 依据：§7ai 固定值 A/B 显示 2048→512 使 Melee −1.33 ms（5/6 好）但 Flow +1.38（微批需要主线程
        // 自旋期的协助）⇒ 净 0。而 §7ap 的逐 job 剖面把两者分开：Melee 1 批/步、单批 ~70 ms、
        // 227 tiles（SMT 已饱和）；波前 574 批/步、单批 28 µs、17.9 tiles（几乎 1 tile/worker）。
        // 判据：本批预期时长 ≥ g_completeSpinBigNs ⇒ 用最小自旋（把核让给 worker），否则保持完整自旋窗。
        // ⚠ 实测结论：**否证**（07 §7aq）—— 12 对 A/B 整步 +4.15 ms（9/12 更差）、Melee +2.62、wave +0.88。
        //   与 §7ai 的"Melee 要少自旋"方向相反 ⇒ 主线程自旋期的协助对**大批同样有用**，
        //   §7ai 那笔 −1.33（6 对）应为噪声。默认关，仅保留器械。
        static const bool kAdaptiveSpin = g_completeSpinAdaptEnabled;
        static const uint64_t kAdaptiveBigNs = g_completeSpinBigNs;
        const int spinCount = (kAdaptiveSpin && _state->estBatchNs >= kAdaptiveBigNs)
            ? 0 : kMainSpin;
        // 自证（`ENTJOY_JCC_VERBOSE=1`）：自适应到底有没有真的做出区分
        if (kAdaptiveSpin && g_jobCostCacheVerbose)
        {
            static std::atomic<uint64_t> s_adaptDump{ 0 };
            const uint64_t dn = s_adaptDump.fetch_add(1, std::memory_order_relaxed);
            if (dn < 40 || (dn & 255) == 0)
                std::printf("[SPINADAPT] estBatchNs=%llu -> spin=%d (full=%d) batchns=%llu\n",
                    static_cast<unsigned long long>(_state->estBatchNs), spinCount, kMainSpin,
                    static_cast<unsigned long long>(kAdaptiveBigNs));
        }
        for (int i = 0; i < spinCount; i++)
        {
            if (_state->completed.load(std::memory_order_acquire))
            {
                finish(DiagPhase::Spin2048);
                return;
            }
            // Chase-Lev：spin 期间协助认领（每 16 次，更积极兜底慢 worker）
            if (g_mainThreadAssistEnabled.load(std::memory_order_relaxed) && (i & 15) == 0)
            {
                if (auto scheduler = LoadChaseLevScheduler(); scheduler && !scheduler->TryAssistOne()) { /* 无可认领，继续 spin */ }
            }
            CpuPause();
        }
        if (_state->completed.load(std::memory_order_acquire))
        {
            finish(DiagPhase::Spin2048);
            return;
        }

        // Brief yield — let other threads run if the job is truly not done.
        std::this_thread::yield();

        // One more short spin after yielding.
        for (int i = 0; i < 256; i++)
        {
            if (_state->completed.load(std::memory_order_acquire))
            {
                finish(DiagPhase::Spin256);
                return;
            }
            if (g_mainThreadAssistEnabled.load(std::memory_order_relaxed) && (i & 15) == 0)
            {
                if (auto scheduler = LoadChaseLevScheduler(); scheduler && !scheduler->TryAssistOne()) { /* 无可认领 */ }
            }
            CpuPause();
        }
        if (_state->completed.load(std::memory_order_acquire))
        {
            finish(DiagPhase::Spin256);
            return;
        }

        // Phase 3: blocking wait with periodic 主线程协助。
        // 正常路径：worker 完成 → notify_all → 谓词满足立即唤醒；
        // Chase-Lev：主线程也参与认领执行，兜底"最后一片被 OS 抢占"的尾延迟。
        g_waitFallbacks.fetch_add(1, std::memory_order_relaxed);
        g_completeWaitLoops.fetch_add(1, std::memory_order_relaxed);
        constexpr auto kCompleteRevisit = std::chrono::microseconds(256); // 256µs 回访间隔（更快兜底）
        while (!_state->completed.load(std::memory_order_acquire))
        {
            // 先 assist 再 wait：避免干等 256µs 的停顿窗口；每轮最多 assist 16 次，
            // 防止链条级联时主线程无限 assist 不回查 completed。
            if (g_mainThreadAssistEnabled.load(std::memory_order_relaxed))
            {
                auto scheduler = LoadChaseLevScheduler();
                if (!scheduler) continue;
                for (int assistN = 0; assistN < 16; ++assistN)
                {
                    if (_state->completed.load(std::memory_order_acquire)) break;
                    if (!scheduler->TryAssistOne()) break;
                }
            }

            if (_state->completed.load(std::memory_order_acquire)) break;

            // 无事可做才 wait（短超时兜底）
            {
                std::unique_lock<std::mutex> lock(_state->mtx);
                if (!_state->completedCv.wait_for(lock, kCompleteRevisit,
                        [state = _state] { return state->completed.load(std::memory_order_acquire); }))
                {
                    // 超时：继续 assist 循环
                }
            }
        }
        finish(DiagPhase::BlockWait);
        const uint64_t completeWakeAt = MonotonicNowNs();
        const uint64_t completeReturnAt = MonotonicNowNs();
        if (completeReturnAt >= completeWakeAt)
            UpdateUnsignedEwma(
                g_completeWakeToReturnEwmaNs,
                std::max<uint64_t>(1, completeReturnAt - completeWakeAt));
    }

    bool JobHandle::IsCompleted() const noexcept {
        return !_state || _state->completed.load(std::memory_order_acquire);
    }
    HandleState* JobHandle::State() const noexcept { return _state; }

    JobHandle JobHandle::CombineDependencies(const std::vector<JobHandle>& handles)
    {
        std::vector<HandleState*> pending;
        for (const auto& h : handles)
            if (h._state && !h._state->completed.load(std::memory_order_acquire))
                pending.push_back(h._state);
        if (pending.empty()) return JobHandle(CreateState(true));
        auto* cs = CreateState(false);
        auto remaining = std::make_shared<std::atomic<int>>(static_cast<int>(pending.size()));
        // 合成 state 持有每个父依赖的引用，保证传递协助链不悬垂；
        // 在 RecycleState 释放。
        cs->dependencies = pending;
        for (auto* ds : pending) {
            AcquireState(ds);
            AcquireState(cs);
            AddContinuationOrRunNow(ds, [cs, remaining]() {
                if (remaining->fetch_sub(1, std::memory_order_acq_rel) == 1)
                    CompleteState(cs);
                ReleaseState(cs);
            });
        }
        return JobHandle(cs);
    }

} // namespace JobSystem
