#include "JobSystemInternal.h"
#include "ChaseLevScheduler.h"

#include <algorithm>
#include <cstdlib>
#include <new>
#include <stdexcept>
#include <thread>
#include <utility>

#if defined(_MSC_VER) && (defined(_M_IX86) || defined(_M_X64))
#include <immintrin.h>
#elif defined(__x86_64__) || defined(__i386__)
#include <xmmintrin.h>
#endif

namespace JobSystem
{
#ifdef ENTJOY_TESTING
    static std::atomic<int> g_failBatchStorageAcquireCount{ 0 };

    void FailNextBatchStorageAcquireForTests(int count) noexcept
    {
        g_failBatchStorageAcquireCount.store(std::max(0, count), std::memory_order_release);
    }
#endif

    // ============================================================
    // Unified execution tiles + dynamic atomic range claiming
    // ============================================================
    int ResolveWorkerTarget(int workerCap, int targetCount) noexcept
    {
        if (targetCount <= 0) return 1;
        // 默认每个 job 可用全部持久 worker 队（逻辑核数-1）；显式 workerCap 优先。
        const int cap = workerCap > 0 ? workerCap : g_numThreads.load(std::memory_order_relaxed);
        return std::max(1, std::min({ cap, g_numThreads.load(std::memory_order_relaxed), targetCount }));
    }


    // ── 小 job 的 worker 上限 = **物理核数**（`ENTJOY_PHYSCAP_SMALLJOB`，默认开；`=0` 关闭）──
    // SMT 兄弟线程的自旋/窃取互抢执行单元，粒度越细越严重（每 worker 只摊到 1 个 tile 时
    // 15 worker 明显慢于 8 worker）。判据：**每 worker 摊到的元素数 ≤ targetWorkers*256** 才封顶。
    // 取"元素数"而不是"chunk 数"：`tileCount` 会漂移，而元素数是 schedule 入参、稳定；
    // 且 compute-bound job 可能只有少数粗块，按 chunk 数封顶会把它们误封顶（曾因此造成回归）。
    // 元素数上限把封顶限定在**总工作量本来就极小**的 job 上。
    // 不可知物理核数（返回 0）或未超订时，一律保持 baseline。
    int ApplyPhysCoreCapForSmallJob(int targetWorkers, uint32_t tileCount, int length) noexcept
    {
        // 默认开；`ENTJOY_PHYSCAP_SMALLJOB=0` 可关闭。
        static const bool enabled = [] {
            const char* v = std::getenv("ENTJOY_PHYSCAP_SMALLJOB");
            return !(v != nullptr && v[0] == '0');
        }();
        if (!enabled) return targetWorkers;
        const int phys = PhysicalCoreCountForDiagnostics();
        if (phys <= 0 || targetWorkers <= phys) return targetWorkers;
        (void)tileCount;
        // 封顶阈值：每 worker 摊到的元素数 ≤ targetWorkers*256 才封顶
        //（用元素数而非 chunk 数：chunk 数会漂移，且粗块 compute-bound job 会被误封顶）。
        if (length <= 0 || length > targetWorkers * 256) return targetWorkers;
        g_physCapApplied.fetch_add(1, std::memory_order_relaxed);
        return phys;
    }

    int ResolveEcsBatchRangeSize(
        int itemCount,
        int workerCount) noexcept    {
        // 保持足够可独立认领的范围以吸收 worker 倾斜，避免每物理 chunk 一次原子认领/回调。
        constexpr int kTargetTilesPerWorker = 4;
        constexpr int kMinChunksPerTile = 4;
        constexpr int kMaxChunksPerTile = 32;
        const int targetTiles = std::max(
            1, workerCount * kTargetTilesPerWorker);
        const int chunksPerTile =
            CeilDiv(itemCount, targetTiles);
        return std::clamp(
            chunksPerTile,
            kMinChunksPerTile,
            kMaxChunksPerTile);
    }

    // ---- 实体数衡 tile（Entity-Count-Balanced Tile）----
    // 每个 unit(chunk/batch) 的存活实体数，实体数衡 tile 用它切分。
    int UnitEntityCount(const ChunkBatchContext* cc, TileKind kind, int unit) noexcept
    {
        if (kind == TileKind::EntityBatchRange)
            return cc->entityBatches[unit].entityCount;
        return cc->chunks[unit].entityCount;
    }

    // 实体数衡 tile 目标：每块约 targetTilesPerWorker 个 worker、约 totalEntities/(workerCount*4) 个实体。
    // 钳制上下限：下限防"极度稀疏实体的空块贪块"，上限防"大工作负载下单块过重/失衡"。
    int ResolveEcsEntityTileTarget(int64_t totalEntities, int workerCount) noexcept
    {
        // 保持足够的并行粒度（~16 tiles/worker）且每块实体数均衡：
        // 小块多 → 并行填充充分、尾部均衡；实体均衡 → 稀疏/聚集实体不再让单块过重。
        constexpr int kTargetTilesPerWorker = 16;
        constexpr int kMinEntitiesPerTile = 256;      // 防"极度稀疏实体的空块贪块"导致块数爆炸
        constexpr int kMaxEntitiesPerTile = 1 << 18;  // 262144，仅防超大均匀负载单块过粗
        const int64_t targetTiles = std::max<int64_t>(1, static_cast<int64_t>(workerCount) * kTargetTilesPerWorker);
        int64_t target = CeilDiv(totalEntities, targetTiles);
        target = std::clamp(target, static_cast<int64_t>(kMinEntitiesPerTile), static_cast<int64_t>(kMaxEntitiesPerTile));
        return static_cast<int>(std::max<int64_t>(1, target));
    }

    // 按实体数前向扫描切 tile：累计实体数达 target 即切一刀。切点恒为整 unit 边界（不拆单块）；
    // 单块实体数>target 的超块自行成块；空块（0 实体）并入当前块、不做无谓切分。
    // tiles==nullptr 时只计数并返回 tile 数；否则写入 tiles 并返回 tile 数（两遍必一致，供先取存储再回填）。
    int BuildEntityBalancedTiles(ExecutionTile* tiles, const ChunkBatchContext* cc,
        TileKind kind, int itemCount, int targetEntities) noexcept
    {
        if (targetEntities < 1) targetEntities = 1;
        int tileCount = 0;
        int unitStart = 0;
        long acc = 0;
        for (int unit = 0; unit < itemCount; ++unit)
        {
            acc += UnitEntityCount(cc, kind, unit);
            const bool last = (unit + 1 == itemCount);
            if (acc >= targetEntities || last)
            {
                if (tiles)
                {
                    tiles[tileCount].kind = kind;
                    tiles[tileCount].firstItem = static_cast<uint32_t>(unitStart);
                    tiles[tileCount].itemCount = static_cast<uint32_t>(unit - unitStart + 1);
                }
                ++tileCount;
                unitStart = unit + 1;
                acc = 0;
            }
        }
        return tileCount;
    }
    // TileKind / ExecutionTile / BatchState / BatchStorage / ChunkBatchContext /
    // GeneralBatchContext 类型定义在 JobSystemInternal.h（跨模块共享：Scheduler 构造
    // batch 字段、Tiles 消费执行）。

    // Guided tile 大小：chunk = ceil(remaining/(W×k))，头部大块、尾部递减到 floor；
    // 总认领数 ≈ W×k×ln(N/floor)，尾部比 uniform 更细。k/floor 由 ConfigureGuided 配置。
    int GuidedTileCount(int length, int workerCount, int k, int floor) noexcept
    {
        const int denom = std::max(1, workerCount) * std::max(1, k);
        const int f = std::max(1, floor);
        int offset = 0;
        int count = 0;
        while (offset < length)
        {
            const int remaining = length - offset;
            int size = CeilDiv(remaining, denom);   // ceil(remaining/denom)
            if (size < f) size = f;                        // floor 兜底
            if (size > remaining) size = remaining;
            offset += size;
            ++count;
        }
        return count;
    }

    int BuildGuidedTiles(ExecutionTile* tiles, int length, int workerCount,
        int k, int floor, TileKind kind) noexcept
    {
        const int denom = std::max(1, workerCount) * std::max(1, k);
        const int f = std::max(1, floor);
        int offset = 0;
        int i = 0;
        while (offset < length)
        {
            const int remaining = length - offset;
            int size = CeilDiv(remaining, denom);   // ceil(remaining/denom)
            if (size < f) size = f;                        // floor 兜底
            if (size > remaining) size = remaining;
            tiles[i] = { static_cast<uint32_t>(offset),
                static_cast<uint32_t>(size), kind };
            offset += size;
            ++i;
        }
        return i;   // 实际 tile 数
    }

    static void AtomicMinNonZero(std::atomic<uint64_t>& target, uint64_t value) noexcept
    {
        if (value == 0) return;
        uint64_t current = target.load(std::memory_order_relaxed);
        while (value < current && !target.compare_exchange_weak(
            current, value, std::memory_order_relaxed)) {}
    }

    static void RecordRangeExecutionDiagnostics(
        BatchState* batch,
        int rangeIndex,
        uint64_t wallNs,
        uint64_t threadCpuNs,
        uint64_t threadCycles,
        int startLogicalCore,
        int endLogicalCore) noexcept
    {
        AtomicMinNonZero(batch->minRangeThreadCycles, threadCycles);
        if (threadCycles != 0)
        {
            batch->totalRangeThreadCycles.fetch_add(threadCycles, std::memory_order_relaxed);
            batch->measuredRangeThreadCycles.fetch_add(1, std::memory_order_relaxed);
        }
        // 慢诊断锁：有界自旋，避免持锁线程崩溃/卡死时其他 worker 无限自旋。
        // 超时（约 ~1s yield 窗）放弃获取 → 跳过本次慢记录（诊断数据丢失可接受）。
        constexpr int kSlowRangeLockSpinLimit = 1'000'000;
        int spinCount = 0;
        while (batch->slowRangeLock.test_and_set(std::memory_order_acquire))
        {
            if (++spinCount >= kSlowRangeLockSpinLimit)
                return;   // 未持锁，绝不能 clear，否则破坏持锁者
            std::this_thread::yield();
        }
        if (wallNs > batch->maxRangeDurationNs.load(std::memory_order_relaxed))
        {
            batch->maxRangeDurationNs.store(wallNs, std::memory_order_relaxed);
            batch->slowRangeThreadCpuNs = threadCpuNs;
            batch->slowRangeThreadCycles = threadCycles;
            batch->slowRangeIndex = rangeIndex;
            batch->slowRangeWorker = WorkerIndexManager::GetCurrentIndex();
            batch->slowRangeStartLogicalCore = startLogicalCore;
            batch->slowRangeEndLogicalCore = endLogicalCore;
            batch->slowRangeStartPhysicalCore =
                PhysicalCoreIndexForDiagnostics(startLogicalCore);
            batch->slowRangeEndPhysicalCore =
                PhysicalCoreIndexForDiagnostics(endLogicalCore);
        }
        batch->slowRangeLock.clear(std::memory_order_release);
    }

    // 近无锁：batch storage per-thread 缓存。命中零锁；共享池仅在缓存空/满时批量
    // 迁移（一次锁 / ~8 次 acquire 或 release）。
    std::mutex g_batchStoragePoolMutex;
    std::vector<BatchStorage*> g_batchStoragePool;

    constexpr size_t kBatchStorageCacheCap = 8;
    struct ThreadBatchStorageCache
    {
        std::vector<BatchStorage*> entries;
        ~ThreadBatchStorageCache()
        {
            // 线程退出时把缓存 storage 交还共享池或释放（池满）。
            // 全局互斥体存活期覆盖本对象（后销毁），此处取锁安全。
            if (entries.empty()) return;
            std::lock_guard<std::mutex> lock(g_batchStoragePoolMutex);
            for (auto* s : entries)
            {
                if (g_batchStoragePool.size() < kMaxPooledBatchStorage)
                    g_batchStoragePool.push_back(s);
                else
                {
                    g_batchStorageDropped.fetch_add(1, std::memory_order_relaxed);
                    delete s;
                }
            }
            entries.clear();
        }
    };
    thread_local ThreadBatchStorageCache t_batchStorageCache;

    // ============================================================
    // 批上下文池（A）：GeneralBatchContext / ChunkBatchContext 去 new/delete
    // 结构与 BatchStorage 同款：线程本地缓存（零锁命中） + 共享池（缓存空/满时批量迁移）。
    // 必要性：上下文在**主线程**获取、由**完成该批的线程**（多为 worker）释放 ⇒ 只有
    // 两级池才能让主线程的 acquire 命中（worker 释放的实例经共享池回到主线程）。
    // ============================================================
    std::mutex g_ctxPoolMutex;
    std::vector<GeneralBatchContext*> g_generalCtxPool;
    std::vector<ChunkBatchContext*> g_chunkCtxPool;
    constexpr size_t kCtxCacheCap = 16;
    constexpr size_t kMaxPooledCtx = 256;

    struct ThreadContextCache
    {
        std::vector<GeneralBatchContext*> general;
        std::vector<ChunkBatchContext*> chunk;
        ~ThreadContextCache()
        {
            if (general.empty() && chunk.empty()) return;
            std::lock_guard<std::mutex> lock(g_ctxPoolMutex);
            for (auto* p : general)
            {
                if (g_generalCtxPool.size() < kMaxPooledCtx) g_generalCtxPool.push_back(p);
                else delete p;
            }
            for (auto* p : chunk)
            {
                if (g_chunkCtxPool.size() < kMaxPooledCtx) g_chunkCtxPool.push_back(p);
                else delete p;
            }
            general.clear();
            chunk.clear();
        }
    };
    thread_local ThreadContextCache t_ctxCache;

    static void SpillGeneralContextCacheToSharedPool()
    {
        if (t_ctxCache.general.empty()) return;
        std::lock_guard<std::mutex> lock(g_ctxPoolMutex);
        for (auto* p : t_ctxCache.general)
        {
            if (g_generalCtxPool.size() < kMaxPooledCtx) g_generalCtxPool.push_back(p);
            else delete p;
        }
        t_ctxCache.general.clear();
    }

    static void SpillChunkContextCacheToSharedPool()
    {
        if (t_ctxCache.chunk.empty()) return;
        std::lock_guard<std::mutex> lock(g_ctxPoolMutex);
        for (auto* p : t_ctxCache.chunk)
        {
            if (g_chunkCtxPool.size() < kMaxPooledCtx) g_chunkCtxPool.push_back(p);
            else delete p;
        }
        t_ctxCache.chunk.clear();
    }

    GeneralBatchContext* AcquireGeneralBatchContext()
    {
        if (!t_ctxCache.general.empty())
        {
            auto* p = t_ctxCache.general.back();
            t_ctxCache.general.pop_back();
            return p;
        }
        {
            std::lock_guard<std::mutex> lock(g_ctxPoolMutex);
            if (!g_generalCtxPool.empty())
            {
                auto* p = g_generalCtxPool.back();
                g_generalCtxPool.pop_back();
                for (size_t i = 1; i < kCtxCacheCap && !g_generalCtxPool.empty(); ++i)
                {
                    t_ctxCache.general.push_back(g_generalCtxPool.back());
                    g_generalCtxPool.pop_back();
                }
                return p;
            }
        }
        return new GeneralBatchContext{};
    }

    void ReleaseGeneralBatchContext(GeneralBatchContext* bc) noexcept
    {
        if (!bc) return;
        // 归还前清空引用型字段：池中实例不得留下上次 job 的指针（防误用；acquire 后调用方整体赋值）。
        bc->indexFunc = nullptr;
        bc->batchFunc = nullptr;
        bc->originalContext = nullptr;
        bc->originalCleanup = nullptr;
        bc->funcHash = 0;
        try
        {
            if (t_ctxCache.general.size() < kCtxCacheCap)
            {
                t_ctxCache.general.push_back(bc);
                return;
            }
            SpillGeneralContextCacheToSharedPool();
            t_ctxCache.general.push_back(bc);
        }
        catch (...)
        {
            delete bc;   // 池化失败绝不吞掉实例（否则内存泄漏）
        }
    }

    ChunkBatchContext* AcquireChunkBatchContext()
    {
        if (!t_ctxCache.chunk.empty())
        {
            auto* p = t_ctxCache.chunk.back();
            t_ctxCache.chunk.pop_back();
            return p;
        }
        {
            std::lock_guard<std::mutex> lock(g_ctxPoolMutex);
            if (!g_chunkCtxPool.empty())
            {
                auto* p = g_chunkCtxPool.back();
                g_chunkCtxPool.pop_back();
                for (size_t i = 1; i < kCtxCacheCap && !g_chunkCtxPool.empty(); ++i)
                {
                    t_ctxCache.chunk.push_back(g_chunkCtxPool.back());
                    g_chunkCtxPool.pop_back();
                }
                return p;
            }
        }
        return new ChunkBatchContext{};
    }

    void ReleaseChunkBatchContext(ChunkBatchContext* cc) noexcept
    {
        if (!cc) return;
        cc->func = nullptr;
        cc->rangeFunc = nullptr;
        cc->entityRangeFunc = nullptr;
        cc->originalContext = nullptr;
        cc->originalCleanup = nullptr;
        cc->chunks = nullptr;
        cc->entityBatches = nullptr;
        try
        {
            if (t_ctxCache.chunk.size() < kCtxCacheCap)
            {
                t_ctxCache.chunk.push_back(cc);
                return;
            }
            SpillChunkContextCacheToSharedPool();
            t_ctxCache.chunk.push_back(cc);
        }
        catch (...)
        {
            delete cc;
        }
    }

    // Shutdown 路径：主线程缓存先交还共享池，再清空共享池（与 BatchStorage 同序）。
    void FlushBatchContextCacheToSharedPool()
    {
        SpillGeneralContextCacheToSharedPool();
        SpillChunkContextCacheToSharedPool();
    }

    // Shutdown 路径：只清**共享池**（其中的实例都已释放、无人引用）。
    // 线程本地缓存交给各自线程的 ThreadContextCache 析构（与 BatchStorage 同款语义），
    // 避免在这里删掉可能仍被在飞批次引用的对象。
    void ClearBatchContextPool() noexcept
    {
        std::vector<GeneralBatchContext*> g;
        std::vector<ChunkBatchContext*> c;
        try
        {
            std::lock_guard<std::mutex> lock(g_ctxPoolMutex);
            g.swap(g_generalCtxPool);
            c.swap(g_chunkCtxPool);
        }
        catch (...) { return; }
        for (auto* p : g) delete p;
        for (auto* p : c) delete p;
    }

    void FlushBatchStorageCacheToSharedPool()
    {
        if (t_batchStorageCache.entries.empty()) return;
        std::lock_guard<std::mutex> lock(g_batchStoragePoolMutex);
        for (auto* s : t_batchStorageCache.entries)
        {
            if (g_batchStoragePool.size() < kMaxPooledBatchStorage)
                g_batchStoragePool.push_back(s);
            else
            {
                g_batchStorageDropped.fetch_add(1, std::memory_order_relaxed);
                delete s;
            }
        }
        t_batchStorageCache.entries.clear();
    }

    BatchStorage* AcquireBatchStorage(uint32_t tileCapacity)
    {
#ifdef ENTJOY_TESTING
        int remaining = g_failBatchStorageAcquireCount.load(std::memory_order_relaxed);
        while (remaining > 0 &&
            !g_failBatchStorageAcquireCount.compare_exchange_weak(
                remaining, remaining - 1, std::memory_order_acq_rel,
                std::memory_order_relaxed)) {}
        if (remaining > 0)
            throw std::bad_alloc();
#endif
        BatchStorage* storage = nullptr;
        // 创建/复用计数只被 GetStatsSnapshot 读取 ⇒ 一次 relaxed 载入整体旁路；
        // 冷路径的 batchStorageDropped 保持原样（不在热路径上）。
        const bool stats = StatsEnabled();
        if (!t_batchStorageCache.entries.empty())
        {
            storage = t_batchStorageCache.entries.back();
            t_batchStorageCache.entries.pop_back();
            if (stats)
                g_batchStorageReused.fetch_add(1, std::memory_order_relaxed);
        }
        else
        {
            // 缓存空：一次性从共享池批量补满（一次锁），池空则 new。
            {
                std::lock_guard<std::mutex> lock(g_batchStoragePoolMutex);
                const size_t available =
                    std::min(g_batchStoragePool.size(), kBatchStorageCacheCap);
                if (available > 0)
                {
                    storage = g_batchStoragePool.back();
                    g_batchStoragePool.pop_back();
                    for (size_t i = 1; i < available; ++i)
                    {
                        t_batchStorageCache.entries.push_back(g_batchStoragePool.back());
                        g_batchStoragePool.pop_back();
                    }
                }
            }
            if (storage)
            {
                if (stats)
                    g_batchStorageReused.fetch_add(1, std::memory_order_relaxed);
            }
            else
            {
                storage = new BatchStorage();
                if (stats)
                    g_batchStorageCreated.fetch_add(1, std::memory_order_relaxed);
            }
        }

        if (storage->tileCapacity < tileCapacity)
        {
            auto* replacement = new ExecutionTile[tileCapacity];
            delete[] storage->tileBuffer;
            storage->tileBuffer = replacement;
            storage->tileCapacity = tileCapacity;
        }
        storage->batch.storage = storage;
        storage->batch.tiles = tileCapacity > 0 ? storage->tileBuffer : nullptr;
        // F2：等宽不物化的标记必须在这里复位（storage 池化复用 ⇒ 否则陈旧值会泄给后续批）。
        storage->batch.uniformTileSize = 0;
        storage->batch.claimCapOverride = 0;   // 必须一起清零（BatchStorage 复用 ⇒ 否则继承陈旧值）
        storage->batch.claimGeomOverride = kClaimGeomAuto;   // 同上（按调用点声明的认领几何）
        storage->batch.claimSpanOverride = 0;   // 同上（按调用点声明的**元素跨度**）
        // F4：每批快照一次全局开关（替代 TryExecuteOneTile 里每 tile 的两次全局载入）。
        storage->batch.tileFast = g_tileFastPath;
        storage->batch.traceOn = g_traceEnabled.load(std::memory_order_relaxed);
        storage->batch.timingOn = g_timingDiagnosticsEnabled.load(std::memory_order_relaxed);
        return storage;
    }

    void ReleaseBatchStorage(BatchStorage* storage) noexcept
    {
        if (!storage) return;
        // 归还池即**推进代次** —— 此后任何携带旧代次的迟到结算都会被 ChaseLevTaskDone 拒绝。
        // 必须在 storage 进入缓存/共享池**之前**执行：否则下一个 Acquire 者可能以旧代次开局。
        // 顺序：++generation → destroy_at/placement new（batch 被整体重建，generation 不受影响）。
        storage->generation.fetch_add(1, std::memory_order_release);
        std::destroy_at(&storage->batch);
        // std::construct_at 是 C++20 才进入 <memory> 的，NDK r23c 的 libc++ 尚未实现；
        // 用等价的 placement new 调用默认构造，语义完全一致且 C++17 即可用。
        new (&storage->batch) BatchState();
        storage->batch.storage = storage;
        // 同 AcquireBatchStorage：纯诊断计数。
        if (StatsEnabled())
            g_batchStorageReturned.fetch_add(1, std::memory_order_relaxed);

        // 先入 per-thread 缓存；满额时整体迁移共享池（一次锁 / ~8 次回收）。
        if (t_batchStorageCache.entries.size() < kBatchStorageCacheCap)
        {
            t_batchStorageCache.entries.push_back(storage);
            return;
        }
        FlushBatchStorageCacheToSharedPool();
        t_batchStorageCache.entries.push_back(storage);
    }

    void ClearBatchStoragePool() noexcept
    {
        std::vector<BatchStorage*> idle;
        {
            std::lock_guard<std::mutex> lock(g_batchStoragePoolMutex);
            idle.swap(g_batchStoragePool);
        }
        for (auto* storage : idle) delete storage;
    }

    // ============================================================
    // Partition-based execution (Phase 1)
    // ============================================================
    static void TryCompleteLogicalBatch(BatchState* batch) noexcept;
    void TryFinalizeChaseLevBatch(BatchState* batch) noexcept;   // Chase-Lev 双条件退役（定义见文件尾）

    // Forward declaration for tile prefetch (defined after ChunkBatchContext).
    static void PrefetchNextTileData(void* context, const ExecutionTile& nextTile) noexcept;

    // Process one tile and update completion counter.
    // Returns true if the tile was processed (for assist comptability).
    static bool TryExecuteOneTile(
        BatchState* batch,
        uint32_t tileIndex) noexcept
    {
        if (!batch || tileIndex >= batch->tileCount) return false;

        // ---- F2（`ENTJOY_TILES_UNIFORM`）：等宽 GeneralRange 的 tile 由 tileIndex **算术推导** ----
        // 与填表口径逐位一致：first = i*size，count = min(size, total-first)（末块裁剪）。
        ExecutionTile derivedTile;
        const ExecutionTile* tilePtr;
        if (batch->uniformTileSize != 0)
        {
            const uint32_t first = tileIndex * batch->uniformTileSize;
            const uint32_t total = batch->totalElements;
            if (first >= total) return false;                 // 防御：tileCount=ceil(total/size) ⇒ 不应发生
            uint32_t n = batch->uniformTileSize;
            if (first + n > total) n = total - first;
            derivedTile = ExecutionTile{ first, n, TileKind::GeneralRange };
            tilePtr = &derivedTile;
        }
        else
        {
            tilePtr = &batch->tiles[tileIndex];
        }
        const ExecutionTile& tile = *tilePtr;
        // trace 关闭时跳过事件上报（fast path：一次内联 load，零 call）。
        // F4（`ENTJOY_TILE_FASTPATH`）：这两个开关已在批构造时快照进 batch ⇒ 直接读批内字段。
        const bool traceOn = batch->tileFast
            ? batch->traceOn : g_traceEnabled.load(std::memory_order_relaxed);
        if (traceOn)
        {
            PushTraceEvent(TraceEventType::Claim, batch->diagnosticId,
                static_cast<int>(tileIndex),
                static_cast<int>(tile.firstItem),
                static_cast<int>(tile.itemCount));
            PushTraceEvent(TraceEventType::ExecuteBegin, batch->diagnosticId,
                static_cast<int>(tileIndex),
                static_cast<int>(tile.firstItem),
                static_cast<int>(tile.itemCount));
        }
        const bool timingEnabled = batch->tileFast
            ? batch->timingOn : g_timingDiagnosticsEnabled.load(std::memory_order_relaxed);
        const uint64_t rangeStartedAt = timingEnabled ? MonotonicNowNs() : 0;
        const uint64_t threadCpuStartedAt = timingEnabled
            ? CurrentThreadCpuTimeNsForDiagnostics() : 0;
        const uint64_t threadCyclesStartedAt = timingEnabled
            ? CurrentThreadCyclesForDiagnostics() : 0;
        const int rangeStartLogicalCore = timingEnabled
            ? CurrentProcessorIndexForDiagnostics() : -1;
        // 无条件记录 firstTileAt（JCC perElem 纯执行口径 + execSpan 诊断共用）。
        // load 快速路径：首个 tile 之后仅 1 次 relaxed load（~1ns/tile），零 MonotonicNowNs 重复调用。
        // F4：`tileFast` 下已在**认领令牌开头**设过一次（ChaseLevScheduler::ExecuteClaimToken）⇒ 这里整段跳过。
        if (!batch->tileFast && batch->firstTileAt.load(std::memory_order_relaxed) == 0)
        {
            uint64_t empty = 0;
            batch->firstTileAt.compare_exchange_strong(
                empty, timingEnabled ? rangeStartedAt : MonotonicNowNs(),
                std::memory_order_release, std::memory_order_relaxed);
        }

        // Prefetch the next tile's data (delegated to a helper below that
        // has access to the full ChunkBatchContext layout).
        // F2：等宽路下 `PrefetchNextTileData` 对 GeneralRange 本就不命中 ⇒ 整段跳过（省一次调用 + 一次数组取址）。
        if (batch->uniformTileSize == 0 && tileIndex + 1 < batch->tileCount)
            PrefetchNextTileData(batch->context, batch->tiles[tileIndex + 1]);

        // C++ 异常协议：捕获用户回调异常 → 记录第一个 → 计数继续正常递减（任务不悬挂）；
        // Complete() 在退役后 rethrow；多次异常只保留第一个。
        try
        {
            batch->executeTile(batch->context, tile);
        }
        catch (...)
        {
            // batch 由在飞 token 保持存活；直接在 HandleState 上记录，使并发 tile 与
            // 并发 Complete 共享同一冷路径同步协议。
            RecordStateException(batch->handle, std::current_exception());
        }
        const int rangeEndLogicalCore = timingEnabled
            ? CurrentProcessorIndexForDiagnostics() : -1;
        const uint64_t threadCyclesFinishedAt = timingEnabled
            ? CurrentThreadCyclesForDiagnostics() : 0;
        const uint64_t threadCpuFinishedAt = timingEnabled
            ? CurrentThreadCpuTimeNsForDiagnostics() : 0;
        const uint64_t rangeFinishedAt = timingEnabled ? MonotonicNowNs() : 0;
        if (timingEnabled && rangeFinishedAt >= rangeStartedAt)
        {
            RecordRangeExecutionDiagnostics(
                batch,
                static_cast<int>(tileIndex),
                rangeFinishedAt - rangeStartedAt,
                threadCpuFinishedAt >= threadCpuStartedAt
                    ? threadCpuFinishedAt - threadCpuStartedAt : 0,
                threadCyclesFinishedAt >= threadCyclesStartedAt
                    ? threadCyclesFinishedAt - threadCyclesStartedAt : 0,
                rangeStartLogicalCore,
                rangeEndLogicalCore);
        }
        if (traceOn)
            PushTraceEvent(TraceEventType::ExecuteEnd, batch->diagnosticId,
                static_cast<int>(tileIndex),
                static_cast<int>(tile.firstItem),
                static_cast<int>(tile.itemCount));

        // 完成计数跟随回调实际完成（热路径原子），无需每个发布槽先进入再退役。
        // 认领组路径（ExecuteClaimToken）内：只在本线程本地累计，组末由 TileAcctGroupFlush 一次提交
        // （把"每 tile 一次共享 fetch_sub"降为"每认领组一次"，削减同一 cache line 的争用）。
        // 其余非认领组的 range 循环保持逐 tile 记账。
        if (t_tileAcctGroupActive)
        {
            ++t_tileAcctGroupCount;
        }
        else if (batch->tilesRemaining.fetch_sub(1, std::memory_order_acq_rel) == 1)
        {
            batch->lastTileAt.store(MonotonicNowNs(), std::memory_order_release);
            TryCompleteLogicalBatch(batch);
        }
        return true;
    }

    // ── 等宽 tile 的**逐 tile 直调**（契约不变：每个 tile 仍恰好一次内核调用）──
    //
    // 动机（2026-10-05，doc16 §14）：薄/等宽路（`uniformTileSize != 0`，内批 ≤ 16 的调用点，例如对齐档里
    // batch=1 的 MarkDead / Flow 各趟）此前每 tile 要付 **4 跳**：
    //   `executor_`(间接) → `ChaseLevExecuteTile` → `TryExecuteOneTile` → `batch->executeTile`(间接)
    //   → `GeneralExecuteTile` → `bc->batchFunc`(间接)
    // 外加逐 tile 的边界检查、等宽派生的乘法、trace/timing/firstTileAt 判据、prefetch 判据。
    // 实测（同状态、只改同 job 的内批）：MarkDead cs=1 → 1.42–1.60 ms，cs=64 → 0.65–0.67 ms，
    // Unity（每元素一次 `Execute(i)`，但**内联**）→ 0.586 ms ⇒ 每工作项 ≈0.85 ns 的调度代价就是赤字。
    //
    // ⚠ 曾经把连续 tile **融合**成一次调用 ⇒ 被 `JobSystemTests` 的
    //   `batch=1 must invoke the kernel exactly once per tile` 判为**契约违反**并回退。
    //   **本函数不改调用次数**，只把上面的链路压成 `bc->batchFunc(...)` 一跳，并跳过
    //   "诊断未开启时不需要"的逐 tile 判据。
    //
    // 适用条件（任一不满足 ⇒ 返回 0，调用方回退到通用逐 tile 路径，语义/诊断逐位不变）：
    //   · `uniformTileSize != 0`（等宽 GeneralRange ⇒ 派生是纯算术，无越界）
    //   · `context` 是 GeneralBatchContext、`batchFunc` 非空、`perKeyIndex < 0`（per-job 记账关）
    //   · trace 与 timing 诊断均关
    // 记账与异常协议与通用路径**逐位相同**（组内累计 `run`，异常记录第一个后继续）。
    uint32_t TileExecuteUniformRun(BatchState* batch, uint32_t tileIndex, uint32_t runTiles) noexcept
    {
        if (!batch || batch->uniformTileSize == 0 || runTiles == 0) return 0;
        if (!batch->context || !batch->executeTile) return 0;
        auto* bc = static_cast<GeneralBatchContext*>(batch->context);
        if (bc->perKeyIndex >= 0 || bc->batchFunc == nullptr) return 0;
        const bool traceOn = batch->tileFast
            ? batch->traceOn : g_traceEnabled.load(std::memory_order_relaxed);
        if (traceOn) return 0;
        const bool timingOn = batch->tileFast
            ? batch->timingOn : g_timingDiagnosticsEnabled.load(std::memory_order_relaxed);
        if (timingOn) return 0;

        uint32_t run = runTiles;
        if (run > batch->tileCount - tileIndex) run = batch->tileCount - tileIndex;
        const uint32_t size = batch->uniformTileSize;
        const uint32_t total = batch->totalElements;
        uint32_t done = 0;
        for (uint32_t k = 0; k < run; ++k)
        {
            const uint32_t first = (tileIndex + k) * size;
            if (first >= total) break;
            uint32_t n = size;
            if (first + n > total) n = total - first;
            try
            {
                bc->batchFunc(bc->originalContext, static_cast<int>(first), static_cast<int>(n));
            }
            catch (...)
            {
                RecordStateException(batch->handle, std::current_exception());
            }
            ++done;
        }
        if (done == 0) return 0;
        if (t_tileAcctGroupActive)
        {
            t_tileAcctGroupCount += done;
        }
        else if (batch->tilesRemaining.fetch_sub(done, std::memory_order_acq_rel) == done)
        {
            batch->lastTileAt.store(MonotonicNowNs(), std::memory_order_release);
            TryCompleteLogicalBatch(batch);
        }
        return done;
    }

    // ── 2026-10-05 记录：**为什么这里没有"等宽 tile 融合执行"**（doc16 §14）──
    // 曾实现过 `TileExecuteFusedRun`：把一个认领令牌里连续的 `run` 个等宽 tile 合并成**一次**
    // `executeTile(context, {first, n})`。动机是实测（对齐档、同状态）：MarkDead 在 cs=1 下
    // 1.42–1.60 ms，同一 job 换成 cs=64 只要 0.65–0.67 ms，而 Unity 在 cs=1 下 0.586 ms
    // ⇒ 每工作项的调度代价就是它 2.5× 赤字的全部来源。
    // ⚠ **但融合违反契约**：`JobSystemTests` 明确断言
    //   `FAIL parallel-for: batch=1 must invoke the kernel exactly once per tile`
    //   —— 每个 tile 恰好一次内核调用是**已发布的语义**（Unity 也是每元素一次 `Execute(i)`；
    //   它便宜是因为 `Execute` 被**内联进 worker 的循环**，不是因为它调用得更少）。
    // ⇒ 融合已**回退**。要吃掉这 0.9 ms/tile，只能**在不改调用次数**的前提下把每次调用变便宜
    //   （等宽路直接走 `bc->batchFunc`、去掉 `GeneralExecuteTile` 的重复间接与检查），见 doc16 §14.4。

    static void RecordWorkerEntry(BatchState* batch) noexcept
    {        // 诊断计数：relaxed 足够（只记录首/末 worker 进入时刻）。
        const uint32_t entered =
            batch->workerSlotsEntered.fetch_add(1, std::memory_order_relaxed) + 1;
        if (entered == 1)
            batch->firstWorkerAt.store(MonotonicNowNs(), std::memory_order_relaxed);
        if (entered == batch->workerCount)
            batch->lastWorkerAt.store(MonotonicNowNs(), std::memory_order_relaxed);
    }

    // 执行入口统一在 ChaseLevScheduler（WorkerLoop / 主线程 TryAssistOne）。

    static void RecordTopologyCompletion(BatchState* batch) noexcept
    {
        const uint64_t now = MonotonicNowNs();
        batch->topologyDoneAt.store(now, std::memory_order_release);
        const uint64_t published = batch->publishedAt.load(std::memory_order_acquire);
        const uint64_t firstWorker = batch->firstWorkerAt.load(std::memory_order_acquire);
        const uint64_t lastWorker = batch->lastWorkerAt.load(std::memory_order_acquire);
        const uint64_t lastTile = batch->lastTileAt.load(std::memory_order_acquire);
        if (published != 0 && firstWorker >= published)
            UpdateUnsignedEwma(g_submitToFirstWorkerEwmaNs,
                std::max<uint64_t>(1, firstWorker - published));
        if (firstWorker != 0 && lastWorker >= firstWorker)
            UpdateUnsignedEwma(g_workerStartSpreadEwmaNs,
                std::max<uint64_t>(1, lastWorker - firstWorker));
        // E1：启停斜坡按"实际参与度"分桶（EWMA 不稳 ⇒ 要看分布）
        if (firstWorker != 0 && lastWorker >= firstWorker)
            E1::RecordWorkerSpread(batch->workerSlotsEntered.load(std::memory_order_relaxed),
                lastWorker - firstWorker);
        if (lastTile != 0 && now >= lastTile)
            UpdateUnsignedEwma(g_lastTileToTopologyDoneEwmaNs,
                std::max<uint64_t>(1, now - lastTile));

        // E1：本批的"实际参与 worker 数（workerSlotsEntered，非 cap）× tile 数 × 批墙钟"入直方图。
        if (published != 0 && now > published)
            E1::RecordBatch(batch->tileCount,
                batch->workerSlotsEntered.load(std::memory_order_relaxed),
                now - published);
        E1::MarkTopologyDone(now);
    }

    static void RecordFinalizedBatchTiming(BatchState* batch) noexcept
    {
        // Always retain cheap batch-boundary timing. Per-tile CPU/core/cycle
        // diagnostics remain gated by g_timingDiagnosticsEnabled.
        const uint64_t now = MonotonicNowNs();
        const uint64_t published = batch->publishedAt.load(std::memory_order_acquire);
        const uint64_t firstWorker = batch->firstWorkerAt.load(std::memory_order_acquire);
        const uint64_t lastWorker = batch->lastWorkerAt.load(std::memory_order_acquire);
        const uint64_t firstTile = batch->firstTileAt.load(std::memory_order_acquire);
        const uint64_t lastTile = batch->lastTileAt.load(std::memory_order_acquire);

        BatchTimingSample sample{};
        sample.batchId = batch->diagnosticId;
        sample.batchTotalNs = published != 0 && now >= published
            ? now - published : 0;
        sample.submitToFirstWorkerNs = published != 0 && firstWorker >= published
            ? firstWorker - published : 0;
        sample.workerStartSpreadNs = firstWorker != 0 && lastWorker >= firstWorker
            ? lastWorker - firstWorker : 0;
        sample.executionSpanNs = firstTile != 0 && lastTile >= firstTile
            ? lastTile - firstTile : 0;
        sample.maxRangeNs = batch->maxRangeDurationNs.load(std::memory_order_relaxed);
        sample.slowRangeThreadCpuNs = batch->slowRangeThreadCpuNs;
        sample.slowRangeThreadCycles = batch->slowRangeThreadCycles;
        const uint64_t minCycles = batch->minRangeThreadCycles.load(std::memory_order_relaxed);
        sample.minRangeThreadCycles = minCycles == (std::numeric_limits<uint64_t>::max)()
            ? 0 : minCycles;
        const uint64_t measuredCycles =
            batch->measuredRangeThreadCycles.load(std::memory_order_relaxed);
        sample.averageRangeThreadCycles = measuredCycles > 0
            ? batch->totalRangeThreadCycles.load(std::memory_order_relaxed) / measuredCycles
            : 0;
        sample.coreMigrations = batch->coreMigrations.load(std::memory_order_relaxed);
        sample.assistTiles = batch->batchAssistTiles.load(std::memory_order_relaxed);
        sample.slowRangeIndex = batch->slowRangeIndex;
        sample.slowRangeWorker = batch->slowRangeWorker;
        sample.slowRangeStartLogicalCore = batch->slowRangeStartLogicalCore;
        sample.slowRangeEndLogicalCore = batch->slowRangeEndLogicalCore;
        sample.slowRangeStartPhysicalCore = batch->slowRangeStartPhysicalCore;
        sample.slowRangeEndPhysicalCore = batch->slowRangeEndPhysicalCore;
        RecordBatchTiming(sample);
    }

    static void ReleaseBatch(BatchState* batch) noexcept
    {
        if (!batch) return;
        ReleaseBatchStorage(batch->storage);
    }

    // Cleanup 属于可观察完成契约：依赖 job 不得在前驱用户上下文释放前启动。
    // context 只认领一次；失败转入句柄冷路径异常通道，不跨 noexcept worker 边界。
    static void RunBatchCleanup(BatchState* batch, HandleState* state) noexcept
    {
        if (!batch || !state) return;
        // 先认领再接触非原子 context 指针：逻辑完成线程与物理 finalizer 可能竞争，
        // 只有赢者可读/清/调用户 cleanup。
        if (batch->cleanupStarted.exchange(true, std::memory_order_acq_rel))
            return;
        if (!batch->cleanup || !batch->context) return;
        void* context = batch->context;
        batch->context = nullptr;
        try
        {
            batch->cleanup(context);
        }
        catch (...)
        {
            RecordStateException(state, std::current_exception());
        }
    }

    void AbortUnsubmittedBatch(BatchState* batch, std::exception_ptr exception) noexcept
    {
        if (!batch || !batch->handle) return;
        if (batch->finalized.exchange(true, std::memory_order_acq_rel)) return;

        auto* state = batch->handle;
        batch->tilesRemaining.store(0, std::memory_order_release);
        batch->pendingTasks.store(0, std::memory_order_release);
        batch->logicalCompleted.store(true, std::memory_order_release);
        if (exception)
            RecordStateException(state, std::move(exception));
        RunBatchCleanup(batch, state);
        try
        {
            CompleteState(state);
        }
        catch (...)
        {
            RecordStateException(state, std::current_exception());
            state->completed.store(true, std::memory_order_release);
            state->completed.notify_all();
            state->completedCv.notify_all();
        }
        state->backendRetired.store(true, std::memory_order_release);
        state->backendRetired.notify_all();
        // No SubmitBatch in-flight reference/counter exists on this path.
        ReleaseBatch(batch);
    }

    static void TryCompleteLogicalBatch(BatchState* batch) noexcept
    {
        // null handle 表示 batch 已 finalize/退役/回收（ReleaseBatchStorage 的
        // construct_at 会清 handle）；finalization 由最后 tile 执行者单所有权，
        // 此守卫防陈旧重复调用触碰已回收 batch（null 解引用会崩溃）。
        if (!batch || !batch->handle) return;
        // E1：退役链分段（`ENTJOY_DIAG_E1=1` 时才读时钟）
        const bool e1 = E1::Enabled();
        const uint64_t e1T0 = e1 ? MonotonicNowNs() : 0;
        if (batch->logicalCompleted.exchange(
            true, std::memory_order_acq_rel)) return;
        auto* state = batch->handle;
        uint64_t e1T1 = 0;
        if (e1) { e1T1 = MonotonicNowNs(); E1::RetirePhase(E1::kRetireCas, e1T1 - e1T0); }

        RecordFinalizedBatchTiming(batch);
        uint64_t e1T2 = 0;
        if (e1) { e1T2 = MonotonicNowNs(); E1::RetirePhase(E1::kRetireTiming, e1T2 - e1T1); }
        const uint64_t publishedAt =
            batch->publishedAt.load(std::memory_order_acquire);
        const uint64_t lastTileAt =
            batch->lastTileAt.load(std::memory_order_acquire);
        if (publishedAt != 0 && lastTileAt >= publishedAt + kLongBatchBarrierNs)
            RegisterLongBatchBarrier(state);
        uint64_t e1T3 = 0;
        if (e1) { e1T3 = MonotonicNowNs(); E1::RetirePhase(E1::kRetireBarrier, e1T3 - e1T2); }
        // Cleanup 必须先于 CompleteState，使依赖 continuation 看到已完全退役的用户上下文；
        // 剩余 task token 使 BatchStorage 存活到下方物理 finalizer。
        RunBatchCleanup(batch, state);
        uint64_t e1T4 = 0;
        if (e1) { e1T4 = MonotonicNowNs(); E1::RetirePhase(E1::kRetireCleanup, e1T4 - e1T3); }
        auto* previousCompletingState = g_completingBatchState;
        g_completingBatchState = state;
        PushTraceEvent(TraceEventType::FinalizeBegin,
            batch->diagnosticId, -1, 0, 0);
        PushTraceEvent(TraceEventType::HandleComplete,
            batch->diagnosticId, -1, 0, 0);
        try
        {
            CompleteState(state);
        }
        catch (...)
        {
            // 完成回调通常由 CompleteState 兜住；平台分配/锁失败不得逃出 noexcept tile 边界。
            RecordStateException(state, std::current_exception());
            state->completed.store(true, std::memory_order_release);
            state->completed.notify_all();
            state->completedCv.notify_all();
        }
        g_completingBatchState = previousCompletingState;
        if (e1)
        {
            const uint64_t t5 = MonotonicNowNs();
            E1::RetirePhase(E1::kRetireCompleteState, t5 - e1T4);
            E1::RetirePhase(E1::kRetireTotal, t5 - lastTileAt);
        }

        // Chase-Lev 唯一路径：逻辑完成由最后 tile 完成者负责，物理退役由最后 task
        // 完成者负责；pendingTasks 归零时 tilesRemaining 必已归零（tile 均在 task 内
        // 执行），故退役单线程执行，消除双完成者并发访问 batch 的 data race。
        RecordTopologyCompletion(batch);
        if (e1) E1::RetirePhase(E1::kRetireTopology, MonotonicNowNs() - e1T4);
    }

    void SubmitBatch(BatchState* batch, int /*workerCap*/)
    {
        if (!batch || !batch->handle) return;
        const bool spDiag = SchedPhase::Enabled();
        uint64_t spT0 = 0;
        if (spDiag)
        {
            spT0 = MonotonicNowNs();
            SchedPhase::g_submitEntries.fetch_add(1, std::memory_order_relaxed);
        }
        auto scheduler = LoadChaseLevScheduler();
        if (!scheduler || !scheduler->IsRunning())
        {
            AbortUnsubmittedBatch(
                batch,
                std::make_exception_ptr(std::runtime_error(
                    "JobSystem backend is not running")));
            return;
        }
        auto* state = batch->handle;
        const int participantCount = std::max(1, static_cast<int>(batch->workerCount));

        // 下面 4 个计数全部是纯诊断（只被 GetStatsSnapshot/GUI 读取）：只做**一次** relaxed
        // 载入后整体旁路。
        // 注意：紧随其后的 g_backendBatchesOutstanding **不 gate** —— 它是 WaitForBackendBatches
        // 的等待条件与 ShutdownFinalizeTests 的验收账本，属同步原语。
        const bool stats = StatsEnabled();
        if (stats)
        {
            g_frameTasksSubmitted.fetch_add(static_cast<uint64_t>(participantCount), std::memory_order_relaxed);
            g_publishedJobs.fetch_add(1, std::memory_order_relaxed);
            g_workerTargetTotal.fetch_add(static_cast<uint64_t>(participantCount), std::memory_order_relaxed);
            g_totalTilesPublished.fetch_add(
                static_cast<uint64_t>(batch->tileCount),
                std::memory_order_relaxed);
        }

        RecordPublishedJob(batch->diagnosticId, static_cast<uint32_t>(batch->tileCount));

        uint64_t diagId = batch->diagnosticId;
        if (diagId != 0)
        {
            state->diagnosticBatchId.store(diagId, std::memory_order_release);
        }

        AcquireState(state);

        // ---- Chase-Lev 路径 ----
        state->backendRetired.store(false, std::memory_order_release);
        g_backendBatchesOutstanding.fetch_add(1, std::memory_order_acq_rel);
        const uint64_t publishedAt = MonotonicNowNs();
        batch->publishedAt.store(publishedAt, std::memory_order_release);
        E1::RecordPublish(publishedAt);   // E1：批间空隙（上一批 topologyDone → 本批 publish）
        // g_nativeBatches 是纯诊断计数；g_backendBatchesOutstanding（上一行）保持精确。
        if (stats)
            g_nativeBatches.fetch_add(1, std::memory_order_relaxed);

        uint64_t spT1 = 0;
        if (spDiag) spT1 = MonotonicNowNs();
        scheduler->SubmitBatch(batch);
        if (spDiag)
            SchedPhase::Add(SchedPhase::SubmitAcct, spT1 - spT0);
    }

    // ============================================================
    // 隐式批（native 收集）：开关开启时挂 pending，EndFrame/Complete 统一提交
    // ============================================================

    // 收集点：主线程直接提交的 tile 路径 job（ParallelFor/ParallelForBatch/Chunk/Entity）。
    // 开关开启 → 挂 pending（持有 state 引用，防 C# 丢弃 handle 导致 state 被回收后悬垂）；
    // 否则保持现状直接提交。依赖未完成路径（continuation 内）不经过本函数，照常立即提交。
    void SubmitOrPending(BatchState* batch)
    {
        if (!batch) return;
        // 快路径：开关关闭直接提交，不取锁。
        if (!g_implicitBatchEnabled.load(std::memory_order_relaxed))
        {
            SubmitBatch(batch);
            return;
        }
        // 开关开启：持 pending 引用后锁内复核。SetEnabled(false) 在锁内置 false
        // 再 flush，故锁内复核能避免「读 true 后、入队前开关被关闭并 flush 空」
        // 的竞态把 batch 留在无人 flush 的队列。
        auto* pendingState = batch->handle;
        AcquireState(pendingState);
        bool queued = false;
        try
        {
            std::lock_guard<std::mutex> lock(g_pendingBatchesMutex);
            if (g_implicitBatchEnabled.load(std::memory_order_relaxed))
            {
                g_pendingBatches.push_back(batch);
                queued = true;
            }
        }
        catch (...)
        {
            AbortUnsubmittedBatch(batch, std::current_exception());
            // Keep the pending reference alive until Abort has finished
            // reading the batch's state and releasing its storage.
            ReleaseState(pendingState);
            return;
        }
        if (!queued)
        {
            ReleaseState(pendingState);
            SubmitBatch(batch);
        }
    }

    // force point：swap 出全部 pending → deferNotify 窗口内逐个 SubmitBatch →
    // 统一 WakePending 一次。SubmitBatch 内部已 AcquireState（在飞引用），
    // 这里 ReleaseState 释放 pending 持有的引用。
    void FlushPendingSubmits()
    {
        std::vector<BatchState*> local;
        {
            std::lock_guard<std::mutex> lock(g_pendingBatchesMutex);
            local.swap(g_pendingBatches);
        }
        if (local.empty()) return;
        g_submitDeferDepth.fetch_add(1, std::memory_order_relaxed);
        for (auto* b : local)
        {
            // SubmitBatch 可能在返回前由 worker 同步完成小 batch：提交前捕获 state，
            // 提交后绝不解引用 b（storage 可能已被回收）。
            auto* state = b ? b->handle : nullptr;
            try
            {
                SubmitBatch(b);
            }
            catch (...)
            {
                AbortUnsubmittedBatch(b, std::current_exception());
            }
            ReleaseState(state);
        }
        g_submitDeferDepth.fetch_sub(1, std::memory_order_relaxed);
        if (auto scheduler = LoadChaseLevScheduler())
            scheduler->WakePending();
    }

    // N12 `ENTJOY_WAKE_POLL`（默认**开**；`=0` 关闭）：提交侧只在"醒着的 worker 数不够本次派发用"
    // 时才写唤醒字（全协议见 JobSystemInternal.h 的 `WakePollEnabled()` 注释块）。
    // ⚠ 形态依赖：本协议要求"登记人数 ≥ 本次派发需要的 worker 数"。两个入口的 need 分别是
    //   小 job=1、真并行趟=`tokenCount`；若将来有入口**低报**需求，就会少唤醒（是延迟问题，
    //   不是丢任务：登记者的下一次读注入器必然在 push 之后）。
    bool WakePollEnabled() noexcept
    {
        static const bool enabled = [] {
            const char* v = std::getenv("ENTJOY_WAKE_POLL");
            return !(v != nullptr && v[0] == '0');
        }();
        return enabled;
    }

    // ---------- Chunk/Entity adaptors ----------
    // ChunkBatchContext / GeneralBatchContext 定义见 JobSystemInternal.h。

    // 预取下一 tile 数据（在 TryExecuteOneTile 执行当前 tile 前调用），
    // 使下一 batch 的 DRAM 读与当前计算重叠。
    // Prefetch helper: x86 uses SSE prefetch; other ISAs (ARM/NEON, wasm)
    // fall back to the compiler builtin (no-op where unsupported).
#if defined(__x86_64__) || defined(__i386__) || defined(_M_X64) || defined(_M_IX86)
#define GDJS_PREFETCH_NTA(p) _mm_prefetch(reinterpret_cast<const char*>(p), _MM_HINT_NTA)
#else
#define GDJS_PREFETCH_NTA(p) __builtin_prefetch(p)
#endif

    static void PrefetchNextTileData(void* context, const ExecutionTile& nextTile) noexcept
    {
        // 打包 plain IJob 的 context 不是 ChunkBatchContext（见 SubmitPackedPlainJobs）：
        // 必须在任何转型之前退出，避免把 PackedPlainBatch* 当 ChunkBatchContext* 解引用。
        if (nextTile.kind == TileKind::PackedJobs) return;
        auto* cc = static_cast<ChunkBatchContext*>(context);
        if (nextTile.kind == TileKind::EntityBatchRange)
        {
            const auto* nextBatch = &cc->entityBatches[nextTile.firstItem];
            if (nextBatch->componentArrays)
            {
                GDJS_PREFETCH_NTA(nextBatch->componentArrays[0]);
            }
        }
        else if (nextTile.kind == TileKind::ChunkCallbacks ||
                 nextTile.kind == TileKind::ChunkRange)
        {
            const auto& nextChunk = cc->chunks[nextTile.firstItem];
            if (nextChunk.entityArray)
                GDJS_PREFETCH_NTA(nextChunk.entityArray);
        }
    }

    // Unified Tile executor for Chunk callbacks, Chunk ranges and Entity ranges.
    bool ChunkExecuteTile(void* ctx, const ExecutionTile& tile)
    {
        auto* bc = static_cast<ChunkBatchContext*>(ctx);
        switch (tile.kind)
        {
        case TileKind::GeneralRange:
            return false;
        case TileKind::ChunkCallbacks:
            for (uint32_t i = 0; i < tile.itemCount; ++i)
                bc->func(bc->originalContext, &bc->chunks[tile.firstItem + i]);
            break;
        case TileKind::ChunkRange:
            bc->rangeFunc(bc->originalContext, bc->chunks,
                static_cast<int>(tile.firstItem), static_cast<int>(tile.itemCount));
            break;
        case TileKind::EntityBatchRange:
            bc->entityRangeFunc(bc->originalContext, bc->entityBatches,
                static_cast<int>(tile.firstItem), static_cast<int>(tile.itemCount));
            break;
        }
        return true;
    }

    void CleanupChunkContext(void* ctx)
    {
        auto* bc = static_cast<ChunkBatchContext*>(ctx);
        try
        {
            if (bc->originalCleanup) bc->originalCleanup(bc->originalContext);
        }
        catch (...)
        {
            ReleaseChunkBatchContext(bc);
            throw;
        }
        ReleaseChunkBatchContext(bc);
    }

    void DestroyChunkContextWithoutCleanup(void* ctx) noexcept
    {
        ReleaseChunkBatchContext(static_cast<ChunkBatchContext*>(ctx));
    }

    // 每-job 自计时用的时基（`ENTJOY_JOB_BATCH_TABLE_DUMP=1` 才走到）：x86 用 rdtsc（~10 ns），
    // 其余平台退回单调时钟。TSC 直接当 ns 累加（频率不变时比值正确；绝对值略偏，不影响"每次调用 vs
    // 每元素"的判断，且跨键比较的偏置相同）。
    static inline uint64_t PerKeyNow() noexcept
    {
#if defined(_WIN32)
        return static_cast<uint64_t>(__rdtsc());
#else
        return MonotonicNowNs();
#endif
    }

    bool GeneralExecuteTile(void* ctx, const ExecutionTile& tile)
    {        auto* bc = static_cast<GeneralBatchContext*>(ctx);
        const int start = static_cast<int>(tile.firstItem);
        const int count = static_cast<int>(tile.itemCount);
        // 每-job 分母：**内核调用的真实次数**与**逐次传给内核的元素数**（融合后一次调用可能很多元素）。
        // 这是"每次调用贵 vs 每元素贵"的那个分母；索引由 Schedule 时解析好，
        // worker 侧不做哈希查找 ⇒ 不会因为碰撞把两个内核混成一行。
        // 与 Schedule 侧的 `elems` 对照可作一致性判据：不等 = tile 漏执行/重复执行。
        if (bc->perKeyIndex >= 0)
        {
            const uint32_t pi = static_cast<uint32_t>(bc->perKeyIndex);
            g_perKeyCalls[pi].fetch_add(1, std::memory_order_relaxed);
            g_perKeyElemsCalled[pi].fetch_add(static_cast<uint64_t>(count), std::memory_order_relaxed);
            // 内核自计时：**每 32 次调用抽 1 次**，把探针自身扰动压到可忽略
            //（否则高频调用的内核会被时钟读取本身明显拖慢）。
            thread_local uint32_t tlCallSeq = 0;
            const bool sample = ((++tlCallSeq & 31u) == 0u);
            if (sample)
            {
                const uint64_t t0 = PerKeyNow();
                if (bc->batchFunc)
                    bc->batchFunc(bc->originalContext, start, count);
                else
                    for (int i = start; i < start + count; ++i)
                        bc->indexFunc(bc->originalContext, i);
                const uint64_t d = PerKeyNow() - t0;
                g_perKeyNsSamples[pi].fetch_add(1, std::memory_order_relaxed);
                g_perKeyNsSum[pi].fetch_add(d, std::memory_order_relaxed);
                uint64_t curMax = g_perKeyNsMax[pi].load(std::memory_order_relaxed);
                while (d > curMax &&
                       !g_perKeyNsMax[pi].compare_exchange_weak(
                           curMax, d, std::memory_order_relaxed, std::memory_order_relaxed))
                { /* retry with refreshed curMax */ }
                return true;
            }
        }
        if (bc->batchFunc)
            bc->batchFunc(bc->originalContext, start, count);
        else
            for (int i = start; i < start + count; ++i)
                bc->indexFunc(bc->originalContext, i);
        return true;
    }

    void CleanupGeneralContext(void* ctx)
    {
        auto* bc = static_cast<GeneralBatchContext*>(ctx);
        try
        {
            if (bc->originalCleanup) bc->originalCleanup(bc->originalContext);
        }
        catch (...)
        {
            ReleaseGeneralBatchContext(bc);
            throw;
        }
        ReleaseGeneralBatchContext(bc);
    }

    void DestroyGeneralContextWithoutCleanup(void* ctx) noexcept
    {
        ReleaseGeneralBatchContext(static_cast<GeneralBatchContext*>(ctx));
    }

    // ============================================================
    // 打包提交：K 个无依赖 plain IJob 作为**一个** batch
    // ============================================================
    // 一次 `SubmitBatch` 只投 O(workers) 个 token，每 token 连续执行 4 个 tile，
    // 每 tile = 一段连续描述符；每 job 只保留"必须可观察"的若干次原子操作。
    //
    // 语义保持（与逐描述符路径逐项对齐）：
    //   - 每个 descriptor 独占一个 HandleState（就是返回给调用方的句柄）：
    //     Complete()/IsCompleted()/异常/诊断 id 仍逐 job 可观察；
    //   - func 之后**立即**执行该 job 自己的 cleanup（与 Scheduler::FastPath 顺序一致）；
    //   - 每 job 异常记录到**该 job 自己的** HandleState（native Complete 时重抛；
    //     C# 侧仍按 SetCurrentBatchId(id) 归属到该 job 的 batchId，协议未变）；
    //   - 描述符不再有任何顺序/依赖保证之外的语义变化：本路径只接受 dependency == null
    //     （带依赖的描述符由 Exports 回退到逐描述符路径）。
    //
    // 引用账（与逐 job `Scheduler::FastPath` 逐位等价）：
    //   每个 job 一个 HandleState：
    //     CreateState 的初始引用 = **打包侧在飞引用**（逐 job 路径里由 FastPath 的
    //       AcquireState 扮演同一角色）；
    //     发布给调用方的用户引用 = 发布时的一次 `JobHandle::Acquire`（等价 Exports::toHandle）；
    //     job[0] 另有 SubmitBatch 的 `AcquireState(batch->handle)`（退役路径 ReleaseState 平衡）。
    //   ⇒ 每个 job 的在飞引用恰好由 PackedPlainFinishJob 释放一次（无论正常执行还是
    //     Abort/Shutdown 兜底），用户引用由调用方释放。故本路径**不需要**任何
    //     "0 号 job 特殊处理"，也不存在 Double-Release / 泄漏。
    struct PackedPlainJob
    {
        void (*func)(void*){ nullptr };
        void* context{ nullptr };
        void (*cleanup)(void*){ nullptr };
        HandleState* state{ nullptr };
        // 幂等认领：正常由执行该 job 的 worker 置位；batch **未执行就退役**
        // （AbortUnsubmittedBatch / ForceFinalizeBatch）时由 PackedPlainCleanup 兜底，
        // 保证用户 context 恰好 cleanup 一次、句柄恰好达终态一次、在飞引用恰好释放一次。
        std::atomic<bool> claimed{ false };
    };

    struct PackedPlainBatch
    {
        PackedPlainJob* jobs{ nullptr };
        uint32_t count{ 0 };
        // 打包内存由两侧共同"持有"：执行侧（PackedPlainCleanup）与提交侧
        // （SubmitPackedPlainJobs）。SubmitBatch 可以在**同步 Abort** 时（backend 未运行/
        // 无 worker）立刻走到 cleanup，而提交侧此时还要读 jobs[] 把句柄交给调用方
        // ⇒ 谁最后放手谁 delete（各 exactly once），避免 UAF / 双重释放。
        std::atomic<int> pendingOwners{ 2 };
    };

    static void PackedPlainDropOwner(PackedPlainBatch* pack) noexcept
    {
        if (!pack) return;
        if (pack->pendingOwners.fetch_sub(1, std::memory_order_acq_rel) == 1)
        {
            delete[] pack->jobs;
            delete pack;
        }
    }

    // job 的收尾：cleanup → 句柄终态（backendRetired 先于 completed 发布）→ 释放在飞引用。
    static void PackedPlainFinishJob(PackedPlainJob& job) noexcept
    {
        auto* state = job.state;
        try
        {
            if (job.cleanup) job.cleanup(job.context);
        }
        catch (...)
        {
            RecordStateException(state, std::current_exception());
        }
        job.context = nullptr;
        job.cleanup = nullptr;
        // backendRetired 必须先于 completed 发布：这样 Complete() 一旦观察到 completed=true，
        // 随后的 WaitBackendRetired 立即返回，不产生额外等待窗口。
        state->backendRetired.store(true, std::memory_order_release);
        state->backendRetired.notify_all();
        try
        {
            CompleteState(state);
        }
        catch (...)
        {
            RecordStateException(state, std::current_exception());
            state->completed.store(true, std::memory_order_release);
            state->completed.notify_all();
            state->completedCv.notify_all();
        }
        ReleaseState(state);   // 打包侧在飞引用（= CreateState 的初始引用）
    }

    // 打包批次的 batch->cleanup：兜底完成**未执行**的 job（Abort / Shutdown 强制退役），
    // 然后交还执行侧对打包内存的持有。
    static void PackedPlainCleanup(void* raw) noexcept
    {
        auto* pack = static_cast<PackedPlainBatch*>(raw);
        if (!pack) return;
        for (uint32_t i = 0; i < pack->count; ++i)
        {
            auto& job = pack->jobs[i];
            if (job.claimed.exchange(true, std::memory_order_acq_rel)) continue;
            PackedPlainFinishJob(job);
        }
        PackedPlainDropOwner(pack);
    }

    // tile 执行：连续跑完 [firstItem, firstItem+itemCount) 的 job（func → cleanup → 终态）。
    static bool PackedPlainExecuteTile(void* raw, const ExecutionTile& tile) noexcept
    {
        auto* pack = static_cast<PackedPlainBatch*>(raw);
        if (!pack || !pack->jobs) return false;
        const uint32_t begin = tile.firstItem;
        const uint32_t end = std::min<uint32_t>(begin + tile.itemCount, pack->count);
        for (uint32_t i = begin; i < end; ++i)
        {
            auto& job = pack->jobs[i];
            auto* state = job.state;
            const uint64_t id = state->diagnosticBatchId.load(std::memory_order_acquire);
            DebugBeginExec(id, 1, 1, false);   // 每 job 一个面板窗口（1 tile / 1 线程）
            if (id != 0) SetCurrentBatchId(id);
            try
            {
                if (job.func) job.func(job.context);
            }
            catch (...)
            {
                RecordStateException(state, std::current_exception());
            }
            if (id != 0) SetCurrentBatchId(0);
            DebugEndExec();
            if (!job.claimed.exchange(true, std::memory_order_acq_rel))
                PackedPlainFinishJob(job);
        }
        return true;
    }

    int SubmitPackedPlainJobs(const PackedPlainJobDesc* descs, int count, void** outStates) noexcept
    {
        if (!descs || count <= 0 || !outStates) return 0;
        // backend 未运行 / 不是 Chase-Lev 路径：不消费任何 context，交调用方走逐描述符路径。
        auto scheduler = LoadChaseLevScheduler();
        if (!scheduler || !scheduler->IsRunning()) return 0;

        PackedPlainBatch* pack = nullptr;
        try
        {
            pack = new PackedPlainBatch();
            pack->jobs = new PackedPlainJob[count];
            pack->count = static_cast<uint32_t>(count);
        }
        catch (...)
        {
            if (pack) delete[] pack->jobs;
            delete pack;
            return 0;
        }

        const int workers = std::max(1, std::min(
            static_cast<int>(scheduler->WorkerCount()), kMaxTrackedWorkers));
        // 分片数：每 worker 4 个 tile（与 ChaseLevScheduler::kClaimBatchSize 一致 ⇒
        // tokenCount == tileCount 且 step == 4，每个 token 恰好认领一段不重叠的 4 个 tile，
        // 无 token 间二次认领的额外原子流量）。
        const uint32_t sliceTarget = static_cast<uint32_t>(workers) * 4u;
        const uint32_t sliceCount = std::min<uint32_t>(
            static_cast<uint32_t>(count), std::max<uint32_t>(1u, sliceTarget));
        const uint32_t sliceSize = CeilDiv(static_cast<uint32_t>(count), sliceCount);

        BatchStorage* storage = nullptr;
        uint32_t created = 0;     // 已创建 state 数
        uint32_t published = 0;   // 已发布（含用户引用）数
        try
        {
            storage = AcquireBatchStorage(sliceCount);
            auto* batch = &storage->batch;
            batch->context = pack;
            batch->cleanup = &PackedPlainCleanup;
            batch->executeTile = &PackedPlainExecuteTile;
            batch->funcHash = 0;
            batch->jccFine = false;
            batch->totalElements = 0;
            batch->tileCount = sliceCount;
            batch->nextTile.store(0, std::memory_order_relaxed);
            batch->tilesRemaining.store(sliceCount, std::memory_order_relaxed);
            batch->workerCount = static_cast<uint32_t>(workers);

            uint32_t offset = 0;
            for (uint32_t t = 0; t < sliceCount; ++t)
            {
                const uint32_t n = std::min<uint32_t>(
                    sliceSize, static_cast<uint32_t>(count) - offset);
                storage->tileBuffer[t] = { offset, n, TileKind::PackedJobs };
                offset += n;
            }
            batch->tiles = storage->tileBuffer;

            // 逐 job：state + 诊断 id（提交线程分配，避免 worker 侧全局原子争用）。
            for (int i = 0; i < count; ++i)
            {
                HandleState* state = CreateState(false);
                AssignStateDiagnosticId(state);
                if (StatsEnabled())
                    g_publishedJobs.fetch_add(1, std::memory_order_relaxed);
                RecordPublishedJob(state->diagnosticBatchId.load(std::memory_order_relaxed), 1);
                auto& job = pack->jobs[i];
                job.func = descs[i].func;
                job.context = descs[i].context;
                job.cleanup = descs[i].cleanup;
                job.state = state;
                ++created;
            }

            batch->handle = pack->jobs[0].state;
            batch->diagnosticId =
                pack->jobs[0].state->diagnosticBatchId.load(std::memory_order_relaxed);
            PushTraceEvent(TraceEventType::Publish, batch->diagnosticId, -1, 0, 0);

            // 必须在 SubmitBatch **之前**发布用户引用：SubmitBatch 可能在同步 Abort 路径里
            // 立刻走到 PackedPlainCleanup 并释放打包侧在飞引用（此时若还没发布，state 会被
            // 回收到池里 → 调用方拿到悬垂指针）。
            for (int i = 0; i < count; ++i)
            {
                HandleState* state = pack->jobs[i].state;
                JobHandle::Acquire(state);
                outStates[i] = static_cast<void*>(state);
                ++published;
            }

            SubmitBatch(batch);
            PackedPlainDropOwner(pack);   // 提交侧放手（执行侧在 cleanup 里放手）
            return count;
        }
        catch (...)
        {
            // 构造/提交失败：回滚引用，用户 context 所有权交回调用方
            // （不调用用户 cleanup —— C# 侧看到句柄为 0 后自行 Cleanup）。
            for (uint32_t i = 0; i < created; ++i)
            {
                HandleState* state = pack->jobs[i].state;
                ReleaseState(state);              // 打包侧在飞引用
                if (i < published) ReleaseState(state);   // 用户引用
                if (i < published) outStates[i] = nullptr;
            }
            if (storage)
            {
                // 未提交：剥离本地 cleanup（打包内存由提交侧 DropOwner 释放）。
                storage->batch.context = nullptr;
                storage->batch.cleanup = nullptr;
                ReleaseBatchStorage(storage);
            }
            PackedPlainDropOwner(pack);
            return 0;
        }
    }

    // ============================================================
    // Chase-Lev tile-level work stealing
    // 使用持久 per-worker deque（ChaseLevScheduler 持有），无需 per-batch 分配。
    // ============================================================

    // ChaseLev 回调：供 ChaseLevScheduler::WorkerLoop 调用。
    // 因为 TryExecuteOneTile 是 static，通过此 trampoline 暴露给 ChaseLevScheduler。
    void ChaseLevExecuteTile(BatchState* batch, uint32_t tileIndex) noexcept
    {
        TryExecuteOneTile(batch, tileIndex);
    }

    // ---- 认领组聚合记账（固定启用，无开关；声明与语义见 JobSystemInternal.h） ----
    thread_local bool t_tileAcctGroupActive = false;
    thread_local uint32_t t_tileAcctGroupCount = 0;

    void TileAcctGroupBegin() noexcept
    {
        t_tileAcctGroupActive = true;
        t_tileAcctGroupCount = 0;
    }

    void TileAcctGroupFlush(BatchState* batch) noexcept
    {
        const uint32_t n = t_tileAcctGroupCount;
        t_tileAcctGroupActive = false;
        t_tileAcctGroupCount = 0;
        if (batch == nullptr || n == 0) return;
        // 与逐 tile 路径同序（acq_rel）：保证本组 tile 的写入对收尾者可见。
        // n 只统计本线程已执行的 tile ⇒ 计数恰好归零，不会越过 0。
        if (batch->tilesRemaining.fetch_sub(n, std::memory_order_acq_rel) == n)
        {
            batch->lastTileAt.store(MonotonicNowNs(), std::memory_order_release);
            TryCompleteLogicalBatch(batch);
        }
    }

    // ---- 切片认领的段游标初始化（由调用点声明或 F6 决定，见 ResolveClaimSliced） ----
    // 语义见 JobSystemInternal.h 中 BatchState 的注释块。必须在 publish 之前调用。
    void InitSliceCursors(BatchState* batch, bool sliced) noexcept
    {
        if (batch == nullptr) return;
        const uint32_t end = batch->tileCount;
        if (!sliced || end == 0)
        {
            batch->sliceCount = 0;
            return;
        }
        uint32_t slices = std::min(std::max(1u, batch->workerCount), end);
        if (slices > BatchState::kMaxSliceSlots) slices = BatchState::kMaxSliceSlots;
        const uint32_t len = CeilDiv(end, slices);
        for (uint32_t i = 0; i < slices; ++i)
        {
            const uint32_t s = i * len;
            batch->sliceCursors[i].next.store(s, std::memory_order_relaxed);
            batch->sliceEnd[i] = std::min(end, s + len);
        }
        batch->sliceTaken.store(0, std::memory_order_relaxed);
        batch->sliceCount = slices;
    }

    // ChaseLev 记录 worker 进入批次时间（供 timing 诊断）。
    void ChaseLevRecordWorkerEntry(BatchState* batch) noexcept
    {
        RecordWorkerEntry(batch);
    }

    // Chase-Lev 双条件退役：tilesRemaining==0（所有 tile 执行完）&& pendingTasks==0
    // （所有任务执行完，无任务再引用本 storage）。由"最后完成者"调用（最后 tile
    // 完成者或最后任务完成者）；未满足条件时返回，等另一个完成者再试。
    void TryFinalizeChaseLevBatch(BatchState* batch) noexcept
    {
        if (!batch || !batch->handle) return;
        const uint32_t tr = batch->tilesRemaining.load(std::memory_order_acquire);
        const uint32_t pt = batch->pendingTasks.load(std::memory_order_acquire);
        if (tr != 0 || pt != 0) return;

        auto* state = batch->handle;
        batch->workersFinished.store(true, std::memory_order_release);
        // 【关键】ReleaseState 必须在 finalized.exchange 成功块内：
        // 最后一个 tile 完成者 与 最后一个 task 完成者 可能同时通过双条件检查，
        // 若 ReleaseState 在块外，两者都会执行 → double release → use-after-free。
        if (!batch->finalized.exchange(true, std::memory_order_acq_rel))
        {
            // ---- JobCostCache：batch 退役时更新 per-job 每元素成本 EWMA ----
            // 安全：finalized.exchange 保证本块单线程；读取均在 ReleaseBatch 之前
            // （无 use-after-free）；flag 默认关闭 → 零热路径开销。
            // F6（`ENTJOY_CLAIM_ADAPT`）的决策完全用 `GetPerElemCost(funcHash)`：它只**读**这里
            // 学到的 perElem，自己不写入、不参与本块的入口条件。
            const bool jccLearn = g_jobCostCacheEnabled.load(std::memory_order_relaxed) != 0;
            if (jccLearn && batch->funcHash != 0 && batch->totalElements > 0)
            {
                // perElem 用纯执行口径：首 tile 开始 → 末 tile 完成
                //（含唤醒/排队会虚高）。
                const uint64_t firstTile =
                    batch->firstTileAt.load(std::memory_order_relaxed);
                const uint64_t lastTile =
                    batch->lastTileAt.load(std::memory_order_relaxed);
                if (lastTile > firstTile)
                {
                    const double totalNs = static_cast<double>(lastTile - firstTile);
                    const double perElemNs = totalNs / static_cast<double>(batch->totalElements);
                    // targetCoarse = !jccFine：JCC 公式产出=细样本；tpw 兜底 /
                    // mem-bound / 显式 batchSize=粗样本。粗样本用于 memory-bound 检测参考。
                    const bool targetCoarse = !batch->jccFine;
                    // 两因子：反解每 tile 固定开销 C_fixed。
                    // execSpan = (tiles/wc)×C_fixed + (N/wc)×C_elem
                    // → C_fixed = (wc×execSpan − N×C_elem)/tiles（C_elem 用当前细 EWMA，
                    //   冷启动用本批 perElem 近似）。固定开销与分块粒度无关，粗/细批都学习。
                    const int wcNow = std::max(1, g_numThreads.load(std::memory_order_relaxed));
                    if (batch->tileCount > 0)
                    {
                        double celemRef = g_jobCostCache.GetPerElemCost(batch->funcHash);
                        if (celemRef <= 0.0) celemRef = perElemNs;
                        const double perTileNs =
                            (static_cast<double>(wcNow) * totalNs -
                             static_cast<double>(batch->totalElements) * celemRef)
                            / static_cast<double>(batch->tileCount);
                        if (perTileNs > 0.0)
                            g_jobCostCache.UpdatePerTileCost(batch->funcHash, perTileNs);
                    }
                    g_jobCostCache.UpdatePerElemCost(batch->funcHash, perElemNs, targetCoarse);
                    if (g_jobCostCacheVerbose)
                        std::printf("[JCC] L hash=%08x tiles=%u N=%u execSpanUs=%.1f perElem=%.2fns coarse=%d\n",
                            batch->funcHash, batch->tileCount, batch->totalElements,
                            totalNs / 1000.0, perElemNs, targetCoarse ? 1 : 0);
                    // ── 估算器精度诊断（由 `ENTJOY_JCC_VERBOSE=1` 开启，采样打印防刷屏）──
                    //   measuredNs   = wcNow × 执行窗口 = 本批的**等效聚合 CPU 时长**
                    //   estNs        = 学习到的 perElem×N + perTile×tiles（估算器实际用的两个量）
                    //   idealWorkers = ceil(measuredNs / 150µs) —— 按实测该唤醒几个才够
                    //   estWorkers   = ceil(estNs / 150µs)     —— 按估算实际会唤醒几个
                    // 若 estWorkers << idealWorkers ⇒ 低估、并行度不足。
                    if (g_jobCostCacheVerbose)
                    {
                        static std::atomic<uint64_t> s_estDump{ 0 };
                        const uint64_t dn = s_estDump.fetch_add(1, std::memory_order_relaxed);
                        if (dn < 400 || (dn & 63) == 0)
                        {
                            const double learnedElem = g_jobCostCache.GetPerElemCost(batch->funcHash);
                            const double learnedTile = g_jobCostCache.GetPerTileCost(batch->funcHash);
                            const double estNs = learnedElem * static_cast<double>(batch->totalElements)
                                               + learnedTile * static_cast<double>(batch->tileCount);
                            const double measuredNs = static_cast<double>(wcNow) * totalNs;
                            constexpr double kPerWorkerTargetNs = 150000.0;
                            int estWorkers = static_cast<int>(estNs / kPerWorkerTargetNs) + (estNs > 0.0 ? 1 : 0);
                            int idealWorkers = static_cast<int>(measuredNs / kPerWorkerTargetNs) + (measuredNs > 0.0 ? 1 : 0);
                            if (estWorkers < 1) estWorkers = 1;
                            if (idealWorkers < 1) idealWorkers = 1;
                            std::printf("[JCC-EST] hash=%08x tiles=%u N=%u measuredUs=%.1f estUs=%.1f ratio=%.3f"
                                        " perElem=%.3fns perTile=%.3fns estWorkers=%d idealWorkers=%d baselineWorkers=%u\n",
                                batch->funcHash, batch->tileCount, batch->totalElements,
                                measuredNs / 1000.0, estNs / 1000.0,
                                measuredNs > 0.0 ? estNs / measuredNs : 0.0,
                                learnedElem, learnedTile, estWorkers, idealWorkers, wcNow);
                        }
                    }
                }
            }
            // 标准 Chase-Lev：不需要 UnregisterBatch（无共享注册表）
            // RangeTask 对象在执行后立即释放回池，不持有 batch 引用
            RunBatchCleanup(batch, state);
            ReleaseBatch(batch);
            state->backendRetired.store(true, std::memory_order_release);
            state->backendRetired.notify_all();
            g_backendBatchesOutstanding.fetch_sub(
                1, std::memory_order_acq_rel);
            g_backendBatchesOutstanding.notify_all();
            // 平衡 SubmitBatch 里的 AcquireState（ReleaseState 由最后一个完成者执行）
            ReleaseState(state);
        }
    }

    // ChaseLev 任务完成回调：每个范围任务执行完后 pendingTasks--，
    // 归零时触发双条件退役检查（可能本线程就是最后一个完成者）。
    //
    // **代次校验**：`batchGen` = 令牌创建时从 `batch->storage->generation` 抄下的值；若该 storage
    // 已被回收复用，其 generation 已前进（`ReleaseBatchStorage` 里 ++），于是这里的比对失败 ⇒
    // 本次结算属于**上一代**、对象已换人 ⇒ 直接丢弃：不 `fetch_sub`、不进
    // `TryFinalizeChaseLevBatch`、不动 `g_backendBatchesOutstanding`（否则会减坏新一代的
    // `pendingTasks`）。注意：这里只守**结算**侧；"迟到令牌执行 tile"需要更早的先验违反才可能
    //（双条件退役要求 pendingTasks==0 才释放），本项不改变执行侧行为。
    void ChaseLevTaskDone(BatchState* batch, uint32_t batchGen) noexcept
    {
        if (!batch) return;
        // 无 storage 的批（理论上不存在；防御）按旧行为处理，避免把正常结算误判成迟到。
        if (batch->storage)
        {
            const uint32_t liveGen =
                batch->storage->generation.load(std::memory_order_acquire);
            if (liveGen != batchGen)
            {
                g_staleSettleDropped.fetch_add(1, std::memory_order_relaxed);
                return;
            }
        }
        const uint32_t prev = batch->pendingTasks.fetch_sub(1, std::memory_order_acq_rel);
        // 回绕检出：fetch_sub 在 0 上执行（本不该发生，令牌数与 store 恒配平）。
        // 检出不改变行为：返回值 0 时同样不触发退役。
        if (prev == 0)
        {
            g_pendingTasksWrap.fetch_add(1, std::memory_order_relaxed);
            return;
        }
        if (prev == 1)
            TryFinalizeChaseLevBatch(batch);
    }

    // shutdown 残留 batch 强制退役：对齐 TryFinalizeChaseLevBatch 的 finalized 块，
    // 但不检查 tilesRemaining/pendingTasks（shutdown 时 worker 已 join，单线程安全）。
    // 幂等：已 finalized 则跳过。这里平衡 SubmitBatch 的 in-flight 引用（用户句柄引用独立保留）。
    void ForceFinalizeBatch(BatchState* batch) noexcept
    {
        if (!batch || !batch->handle) return;
        if (!batch->finalized.exchange(true, std::memory_order_acq_rel))
        {
            auto* state = batch->handle;
            // 强制 shutdown 是未排空工作的终结完成点：发布与正常最后 tile 路径相同的
            // 状态协议，避免 Complete/IsCompleted 观察到 completed=false 且 backendRetired=true。
            batch->tilesRemaining.store(0, std::memory_order_release);
            batch->pendingTasks.store(0, std::memory_order_release);
            RunBatchCleanup(batch, state);
            if (!batch->logicalCompleted.exchange(true, std::memory_order_acq_rel))
            {
                try
                {
                    CompleteState(state);
                }
                catch (...)
                {
                    RecordStateException(state, std::current_exception());
                    state->completed.store(true, std::memory_order_release);
                    state->completed.notify_all();
                    state->completedCv.notify_all();
                }
            }
            ReleaseBatch(batch);
            state->backendRetired.store(true, std::memory_order_release);
            state->backendRetired.notify_all();
            g_backendBatchesOutstanding.fetch_sub(1, std::memory_order_acq_rel);
            g_backendBatchesOutstanding.notify_all();
            // Balance the in-flight reference acquired by SubmitBatch.  The
            // public handle retains its own state reference independently.
            ReleaseState(state);
        }
    }

} // namespace JobSystem
