#include "JobSystemInternal.h"

#include <algorithm>
#include <thread>

#if defined(_MSC_VER) && (defined(_M_IX86) || defined(_M_X64))
#include <immintrin.h>
#endif

namespace JobSystem
{
    // ============================================================
    // Unified execution tiles + dynamic atomic range claiming
    // ============================================================
    int ResolveWorkerTarget(int workerCap, int targetCount) noexcept
    {
        if (targetCount <= 0) return 1;
        // Match Unity-style worker configuration: by default every job can use
        // the full persistent worker cohort (logical CPU count minus one).
        // An explicit per-job workerCap remains authoritative.
        const int cap = workerCap > 0 ? workerCap : g_numThreads;
        return std::max(1, std::min({ cap, g_numThreads, targetCount }));
    }

    int ResolveEcsBatchRangeSize(
        int itemCount,
        int workerCount) noexcept
    {
        // Keep enough independently claimable ranges to absorb worker skew,
        // without paying one atomic claim/callback for every physical chunk.
        constexpr int kTargetTilesPerWorker = 4;
        constexpr int kMinChunksPerTile = 4;
        constexpr int kMaxChunksPerTile = 32;
        const int targetTiles = std::max(
            1, workerCount * kTargetTilesPerWorker);
        const int chunksPerTile =
            (itemCount + targetTiles - 1) / targetTiles;
        return std::clamp(
            chunksPerTile,
            kMinChunksPerTile,
            kMaxChunksPerTile);
    }

    // ---- 实体数衡 tile（Entity-Count-Balanced Tile）----
    // 每个 unit(chunk/batch) 的存活实体数。实体数衡 tile 用它切分，替代"固定 chunk 数"。
    int UnitEntityCount(const ChunkBatchContext* cc, TileKind kind, int unit) noexcept
    {
        if (kind == TileKind::EntityBatchRange)
            return cc->entityBatches[unit].entityCount;
        return cc->chunks[unit].entityCount;
    }

    // 实体数衡 tile 目标：每块约 targetTilesPerWorker 个 worker、约 totalEntities/(workerCount*4) 个实体。
    // 钳制上下限：下限防"极度稀疏实体的空块贪块"，上限防"大工作负载下单块过重/失衡"。
    int ResolveEcsEntityTileTarget(int totalEntities, int workerCount) noexcept
    {
        // 保持与旧 chunk 计数路径相当的并行粒度（~16 tiles/worker），同时每块实体数均衡：
        // 足够多的小块 → 并行填充充分、尾部均衡；实体均衡 → 稀疏/聚集实体不再让单块过重。
        constexpr int kTargetTilesPerWorker = 16;
        constexpr int kMinEntitiesPerTile = 256;      // 防"极度稀疏实体的空块贪块"导致块数爆炸
        constexpr int kMaxEntitiesPerTile = 1 << 18;  // 262144，仅防超大均匀负载单块过粗
        const int targetTiles = std::max(1, workerCount * kTargetTilesPerWorker);
        int target = (totalEntities + targetTiles - 1) / targetTiles;
        target = std::clamp(target, kMinEntitiesPerTile, kMaxEntitiesPerTile);
        return std::max(1, target);
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

    // Guided（OpenMP schedule(guided) 同族）tile 大小：chunk = ceil(remaining/(W*k))，
    // 头部大块（Poisson 平滑、非 straggler）、尾部递减到 floor（钳 straggler 上界）。
    // 总认领数 ~ W*k*ln(N/floor)，少于 uniform k=26 的同时尾部更细。
    // k/floor 由 JobSystem_ConfigureGuided 配置。返回实际 tile 数。
    int GuidedTileCount(int length, int workerCount, int k, int floor) noexcept
    {
        const int denom = std::max(1, workerCount) * std::max(1, k);
        const int f = std::max(1, floor);
        int offset = 0;
        int count = 0;
        while (offset < length)
        {
            const int remaining = length - offset;
            int size = (remaining + denom - 1) / denom;   // ceil(remaining/denom)
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
            int size = (remaining + denom - 1) / denom;   // ceil(remaining/denom)
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
    // 迁移（一次锁 / ~8 次 acquire 或 release）。弃用原 O(n) 最佳适配扫描——buffer
    // 在 AcquireBatchStorage 按需增长，缓存里任何 storage 都可用，扫描纯属全局锁竞争点。
    std::mutex g_batchStoragePoolMutex;
    std::vector<BatchStorage*> g_batchStoragePool;

    constexpr size_t kBatchStorageCacheCap = 8;
    struct ThreadBatchStorageCache
    {
        std::vector<BatchStorage*> entries;
        ~ThreadBatchStorageCache()
        {
            // 线程退出（worker join / 进程 teardown）时把缓存 storage 交还共享池或释放
            // （池满）。全局互斥体在 Shutdown 中始终存活（本对象先于 g_batchStoragePoolMutex
            // 初始化，按标准后销毁），此处取锁安全。
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
        BatchStorage* storage = nullptr;
        if (!t_batchStorageCache.entries.empty())
        {
            storage = t_batchStorageCache.entries.back();
            t_batchStorageCache.entries.pop_back();
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
                g_batchStorageReused.fetch_add(1, std::memory_order_relaxed);
            else
            {
                storage = new BatchStorage();
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
        return storage;
    }

    static void ReleaseBatchStorage(BatchStorage* storage) noexcept
    {
        if (!storage) return;
        std::destroy_at(&storage->batch);
        std::construct_at(&storage->batch);
        storage->batch.storage = storage;
        g_batchStorageReturned.fetch_add(1, std::memory_order_relaxed);

        // 近无锁：先入 per-thread 缓存；满额时整体迁移共享池（一次锁 / ~8 次回收）。
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

    // Forward declaration for tile prefetch (defined after ChunkBatchContext).
    static void PrefetchNextTileData(void* context, const ExecutionTile& nextTile) noexcept;

    // Process one tile and update completion counter.
    // Returns true if the tile was processed (for assist comptability).
    static bool TryExecuteOneTile(
        BatchState* batch,
        uint32_t tileIndex) noexcept
    {
        if (!batch || tileIndex >= batch->tileCount) return false;

        const auto& tile = batch->tiles[tileIndex];
        PushTraceEvent(TraceEventType::Claim, batch->diagnosticId,
            static_cast<int>(tileIndex),
            static_cast<int>(tile.firstItem),
            static_cast<int>(tile.itemCount));
        PushTraceEvent(TraceEventType::ExecuteBegin, batch->diagnosticId,
            static_cast<int>(tileIndex),
            static_cast<int>(tile.firstItem),
            static_cast<int>(tile.itemCount));
        const bool timingEnabled = g_timingDiagnosticsEnabled.load(std::memory_order_relaxed);
        const uint64_t rangeStartedAt = timingEnabled ? MonotonicNowNs() : 0;
        const uint64_t threadCpuStartedAt = timingEnabled
            ? CurrentThreadCpuTimeNsForDiagnostics() : 0;
        const uint64_t threadCyclesStartedAt = timingEnabled
            ? CurrentThreadCyclesForDiagnostics() : 0;
        const int rangeStartLogicalCore = timingEnabled
            ? CurrentProcessorIndexForDiagnostics() : -1;
        if (timingEnabled)
        {
            uint64_t empty = 0;
            batch->firstTileAt.compare_exchange_strong(
                empty, rangeStartedAt, std::memory_order_release, std::memory_order_relaxed);
        }

        // Prefetch the next tile's data (delegated to a helper below that
        // has access to the full ChunkBatchContext layout).
        if (tileIndex + 1 < batch->tileCount)
            PrefetchNextTileData(batch->context, batch->tiles[tileIndex + 1]);

        batch->executeTile(batch->context, batch->tiles[tileIndex]);
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
        PushTraceEvent(TraceEventType::ExecuteEnd, batch->diagnosticId,
            static_cast<int>(tileIndex),
            static_cast<int>(tile.firstItem),
            static_cast<int>(tile.itemCount));

        // Completion follows actual callback completion.  This is the hot-path
        // atomic that replaces the much more expensive requirement that every
        // published participant slot must first enter and retire.
        if (batch->tilesRemaining.fetch_sub(1, std::memory_order_acq_rel) == 1)
        {
            batch->lastTileAt.store(MonotonicNowNs(), std::memory_order_release);
            TryCompleteLogicalBatch(batch);
        }
        return true;
    }

    static void RecordWorkerEntry(BatchState* batch) noexcept
    {
        const uint64_t now = MonotonicNowNs();
        uint64_t empty = 0;
        batch->firstWorkerAt.compare_exchange_strong(
            empty, now, std::memory_order_acq_rel, std::memory_order_relaxed);
        if (batch->workerSlotsEntered.fetch_add(1, std::memory_order_acq_rel) + 1 ==
            batch->workerCount)
            batch->lastWorkerAt.store(now, std::memory_order_release);
    }

    static void WorkerAtomicRangeLoop(BatchState* batch) noexcept
    {
        RecordWorkerEntry(batch);
        const uint64_t active =
            g_activeWorkers.fetch_add(1, std::memory_order_acq_rel) + 1;
        uint64_t peak = g_activeWorkersPeak.load(std::memory_order_relaxed);
        while (active > peak && !g_activeWorkersPeak.compare_exchange_weak(
            peak, active, std::memory_order_relaxed)) {}
        g_workerClaimedTokens.fetch_add(1, std::memory_order_relaxed);

        const int wi = WorkerIndexManager::GetCurrentIndex();

        // 逐 Tile 认领（fetch_add(1)）：引导/guided tile 是"大块在前、逐块递减"，逐块交错认领
        // 让各 worker 均匀拿到大+小块的混合，天然负载均衡。批量连续认领只在"每 Tile 等量"时成立
        //（会破坏 guided 的均衡，导致头批 worker 拿全大块、尾部失衡）——故此处保持逐块。
        uint64_t executed = 0;
        while (true)
        {
            const uint32_t tile = batch->nextTile.fetch_add(
                1, std::memory_order_relaxed);
            if (tile >= batch->tileCount) break;

            // ---- 调试面板：更新当前 tile 索引 ----
            if (wi >= 0 && wi < kMaxTrackedWorkers)
                g_workerCurrentTile[wi].store(tile, std::memory_order_relaxed);

            TryExecuteOneTile(batch, tile);
            ++executed;
        }

        g_workerExecutedRanges.fetch_add(executed, std::memory_order_relaxed);
        g_localTiles.fetch_add(executed, std::memory_order_relaxed);
        g_activeWorkers.fetch_sub(1, std::memory_order_acq_rel);
        // 调试面板：把当前执行窗口的 tiles 更新为本 worker 实际认领执行的 tile 数
        DebugUpdateExecTiles(static_cast<uint32_t>(executed));
    }

    static bool AssistExecuteOneTile(void* ptr) noexcept
    {
        auto* batch = static_cast<BatchState*>(ptr);
        if (!batch) return false;
        const uint32_t tile = batch->nextTile.fetch_add(
            1, std::memory_order_relaxed);
        if (tile >= batch->tileCount) return false;
        SetCurrentBatchId(batch->diagnosticId);
        TryExecuteOneTile(batch, tile);
        SetCurrentBatchId(0);
        g_mainExecutedRanges.fetch_add(1, std::memory_order_relaxed);
        g_assistExecuted.fetch_add(1, std::memory_order_relaxed);
        g_assistTiles.fetch_add(1, std::memory_order_relaxed);
        batch->batchAssistTiles.fetch_add(1, std::memory_order_relaxed);
        return true;
    }

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
        if (lastTile != 0 && now >= lastTile)
            UpdateUnsignedEwma(g_lastTileToTopologyDoneEwmaNs,
                std::max<uint64_t>(1, now - lastTile));

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

    static void TryCompleteLogicalBatch(BatchState* batch) noexcept
    {
        // handle is cleared by construct_at in ReleaseBatchStorage, so a null
        // handle means the batch was already finalized, retired and recycled.
        // Finalization is single-owned by the last tile executor, but keep this
        // guard so a stale duplicate call can never touch a recycled batch's
        // state (would crash on the null-handle dereference below).
        if (!batch || !batch->handle) return;
        if (batch->logicalCompleted.exchange(
            true, std::memory_order_acq_rel)) return;

        auto* state = batch->handle;
        // Stop admitting new assist calls. Readers that already captured the
        // callback can only observe empty partitions at this point.
        state->assistCallback.store(nullptr, std::memory_order_release);

        RecordFinalizedBatchTiming(batch);
        const uint64_t publishedAt =
            batch->publishedAt.load(std::memory_order_acquire);
        const uint64_t lastTileAt =
            batch->lastTileAt.load(std::memory_order_acquire);
        if (publishedAt != 0 && lastTileAt >= publishedAt + kLongBatchBarrierNs)
            RegisterLongBatchBarrier(state);
        auto* previousCompletingState = g_completingBatchState;
        g_completingBatchState = state;
        PushTraceEvent(TraceEventType::FinalizeBegin,
            batch->diagnosticId, -1, 0, 0);
        // context/blob 释放延迟到 TryRetireCompletedBatch（assistReaders==0 && workersFinished 门控）——
        // 主线程 assist 可能正拿着 bc/context 执行 tile；此处不可等待 readers（末块由 assist 自执时自死锁）。
        PushTraceEvent(TraceEventType::HandleComplete,
            batch->diagnosticId, -1, 0, 0);
        CompleteState(state);
        g_completingBatchState = previousCompletingState;
    }

    static void TryRetireCompletedBatch(HandleState* state) noexcept
    {
        if (!state) return;

        BatchState* batch = nullptr;
        {
            std::lock_guard<std::mutex> lock(state->mtx);
            batch = static_cast<BatchState*>(
                state->assistContext.load(std::memory_order_acquire));
            if (!batch ||
                !batch->logicalCompleted.load(std::memory_order_acquire) ||
                !batch->workersFinished.load(std::memory_order_acquire) ||
                state->assistReaders.load(std::memory_order_acquire) != 0)
            {
                return;
            }

            state->assistContext.store(nullptr, std::memory_order_release);
            state->assistReadersDrained.store(nullptr, std::memory_order_release);
        }

        if (!batch->finalized.exchange(true, std::memory_order_acq_rel))
        {
            // 本处保证 assistReaders==0 && workersFinished（无执行者在使用 bc/context）
            if (batch->cleanup)
            {
                batch->cleanup(batch->context);
                batch->context = nullptr;
            }
            ReleaseBatch(batch);
            state->backendRetired.store(true, std::memory_order_release);
            state->backendRetired.notify_all();
            g_backendBatchesOutstanding.fetch_sub(
                1, std::memory_order_acq_rel);
            g_backendBatchesOutstanding.notify_all();
        }
    }

    // The last assist reader only requests finalization. Batch memory remains
    // alive until every worker slot has also finished its tile loop.
    static void OnAssistReadersDrained(void* handlePtr) noexcept
    {
        TryRetireCompletedBatch(static_cast<HandleState*>(handlePtr));
    }

    // Acquire assist reader: returns false if batch is already finalized
    static void ExecuteBatchSlot(void* raw, uint32_t slot) noexcept
    {
        auto* batch = static_cast<BatchState*>(raw);
        const bool timingEnabled = g_timingDiagnosticsEnabled.load(std::memory_order_relaxed);
        const int startProcessor = timingEnabled
            ? CurrentProcessorIndexForDiagnostics() : -1;
        if (WorkerIndexManager::GetCurrentIndex() < 0)
            WorkerIndexManager::SetCurrentIndex(WorkerIndexManager::AllocateIndex());

        // ---- 调试面板：执行窗口事件上报（start 时记录时间戳，end 时追加共享历史）----
        DebugBeginExec(batch->diagnosticId, static_cast<uint32_t>(batch->tileCount),
                       batch->workerCount, false); // Job：记录计划参与 worker 数

        // worker 整个 slot 只执行这一个 batch —— 窗口一次，覆盖槽内所有 tile。
        SetCurrentBatchId(batch->diagnosticId);
        WorkerAtomicRangeLoop(batch);
        SetCurrentBatchId(0);

        // ---- 调试面板：执行窗口结束 ----
        DebugEndExec();

        const int endProcessor = timingEnabled
            ? CurrentProcessorIndexForDiagnostics() : -1;
        if (startProcessor >= 0 && endProcessor >= 0 && startProcessor != endProcessor)
            batch->coreMigrations.fetch_add(1, std::memory_order_relaxed);
    }

    static void CompleteBackendBatch(void* raw) noexcept
    {
        auto* batch = static_cast<BatchState*>(raw);
        auto* state = batch->handle;
        RecordTopologyCompletion(batch);
        state->assistCallback.store(nullptr, std::memory_order_release);
        batch->workersFinished.store(true, std::memory_order_release);
        // Logical completion is owned exclusively by the last tile executor
        // (TryCompleteLogicalBatch in TryExecuteOneTile), which provably cannot
        // run on a retired/recycled batch (it holds a worker slot or an assist
        // reader, both of which block retirement). The old defensive second
        // call here could double-finalize a batch after it was recycled —
        // removed; tileCount is always >= 1 so no empty-batch path needs it.
        TryRetireCompletedBatch(state);
        ReleaseState(state);
    }

    void SubmitBatch(BatchState* batch, int /*workerCap*/)
    {
        auto* state = batch->handle;
        bool (*assistFn)(void*) noexcept = &AssistExecuteOneTile;
        const int participantCount = std::max(1, static_cast<int>(batch->workerCount));

        g_frameTasksSubmitted.fetch_add(static_cast<uint64_t>(participantCount), std::memory_order_relaxed);
        g_publishedJobs.fetch_add(1, std::memory_order_relaxed);
        g_workerTargetTotal.fetch_add(static_cast<uint64_t>(participantCount), std::memory_order_relaxed);
        g_totalTilesPublished.fetch_add(
            static_cast<uint64_t>(batch->tileCount),
            std::memory_order_relaxed);

        RecordPublishedJob(batch->diagnosticId, static_cast<uint32_t>(batch->tileCount));

        // Register assist callback + readersDrained for Complete()
        state->assistCallback.store(assistFn, std::memory_order_release);
        state->assistContext.store(batch, std::memory_order_release);
        state->assistReadersDrained.store(&OnAssistReadersDrained, std::memory_order_release);

        uint64_t diagId = batch->diagnosticId;
        if (diagId != 0)
        {
            state->diagnosticBatchId.store(diagId, std::memory_order_release);
        }

        AcquireState(state);
        state->backendRetired.store(false, std::memory_order_release);
        g_backendBatchesOutstanding.fetch_add(1, std::memory_order_acq_rel);
        const uint64_t publishedAt = MonotonicNowNs();
        batch->publishedAt.store(publishedAt, std::memory_order_release);
        g_nativeBatches.fetch_add(1, std::memory_order_relaxed);
        if (!g_nativeWorkerPool || !g_nativeWorkerPool->Submit(
            batch,
            static_cast<uint32_t>(participantCount),
            &ExecuteBatchSlot,
            &CompleteBackendBatch))
        {
            for (int slot = 0; slot < participantCount; ++slot)
                ExecuteBatchSlot(batch, static_cast<uint32_t>(slot));
            CompleteBackendBatch(batch);
        }
    }

    // ---------- Chunk/Entity adaptors ----------
    // ChunkBatchContext / GeneralBatchContext 定义见 JobSystemInternal.h。

    // Prefetch data for the next tile. Called from TryExecuteOneTile before
    // executing the current tile, so DRAM reads for the next batch overlap
    // with computation of the current one.
    static void PrefetchNextTileData(void* context, const ExecutionTile& nextTile) noexcept
    {
        auto* cc = static_cast<ChunkBatchContext*>(context);
        if (nextTile.kind == TileKind::EntityBatchRange)
        {
            const auto* nextBatch = &cc->entityBatches[nextTile.firstItem];
            if (nextBatch->componentArrays)
            {
                _mm_prefetch(
                    reinterpret_cast<const char*>(nextBatch->componentArrays[0]),
                    _MM_HINT_NTA);
            }
        }
        else if (nextTile.kind == TileKind::ChunkCallbacks ||
                 nextTile.kind == TileKind::ChunkRange)
        {
            const auto& nextChunk = cc->chunks[nextTile.firstItem];
            if (nextChunk.entityArray)
                _mm_prefetch(
                    reinterpret_cast<const char*>(nextChunk.entityArray),
                    _MM_HINT_NTA);
        }
    }

    // Unified Tile executor for Chunk callbacks, Chunk ranges and Entity ranges.
    bool ChunkExecuteTile(void* ctx, const ExecutionTile& tile) noexcept
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

    void CleanupChunkContext(void* ctx) noexcept
    {
        auto* bc = static_cast<ChunkBatchContext*>(ctx);
        if (bc->originalCleanup) bc->originalCleanup(bc->originalContext);
        delete bc;
    }

    bool GeneralExecuteTile(void* ctx, const ExecutionTile& tile) noexcept
    {
        auto* bc = static_cast<GeneralBatchContext*>(ctx);
        const int start = static_cast<int>(tile.firstItem);
        const int count = static_cast<int>(tile.itemCount);
        if (bc->batchFunc)
            bc->batchFunc(bc->originalContext, start, count);
        else
            for (int i = start; i < start + count; ++i)
                bc->indexFunc(bc->originalContext, i);
        return true;
    }

    void CleanupGeneralContext(void* ctx) noexcept
    {
        auto* bc = static_cast<GeneralBatchContext*>(ctx);
        if (bc->originalCleanup) bc->originalCleanup(bc->originalContext);
        delete bc;
    }

} // namespace JobSystem
