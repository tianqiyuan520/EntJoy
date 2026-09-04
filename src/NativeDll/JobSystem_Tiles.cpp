#include "JobSystemInternal.h"
#include "ChaseLevScheduler.h"

#include <algorithm>
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

    int ResolveEcsBatchRangeSize(
        int itemCount,
        int workerCount) noexcept
    {
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

    void ReleaseBatchStorage(BatchStorage* storage) noexcept
    {
        if (!storage) return;
        std::destroy_at(&storage->batch);
        // std::construct_at 是 C++20 才进入 <memory> 的，NDK r23c 的 libc++ 尚未实现；
        // 用等价的 placement new 调用默认构造，语义完全一致且 C++17 即可用。
        new (&storage->batch) BatchState();
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

        const auto& tile = batch->tiles[tileIndex];
        // trace 关闭时跳过事件上报（fast path：一次内联 load，零 call）。
        const bool traceOn = g_traceEnabled.load(std::memory_order_relaxed);
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
        const bool timingEnabled = g_timingDiagnosticsEnabled.load(std::memory_order_relaxed);
        const uint64_t rangeStartedAt = timingEnabled ? MonotonicNowNs() : 0;
        const uint64_t threadCpuStartedAt = timingEnabled
            ? CurrentThreadCpuTimeNsForDiagnostics() : 0;
        const uint64_t threadCyclesStartedAt = timingEnabled
            ? CurrentThreadCyclesForDiagnostics() : 0;
        const int rangeStartLogicalCore = timingEnabled
            ? CurrentProcessorIndexForDiagnostics() : -1;
        // 无条件记录 firstTileAt（JCC perElem 纯执行口径 + execSpan 诊断共用）。
        // load 快速路径：首个 tile 之后仅 1 次 relaxed load（~1ns/tile），零 MonotonicNowNs 重复调用。
        if (batch->firstTileAt.load(std::memory_order_relaxed) == 0)
        {
            uint64_t empty = 0;
            batch->firstTileAt.compare_exchange_strong(
                empty, timingEnabled ? rangeStartedAt : MonotonicNowNs(),
                std::memory_order_release, std::memory_order_relaxed);
        }

        // Prefetch the next tile's data (delegated to a helper below that
        // has access to the full ChunkBatchContext layout).
        if (tileIndex + 1 < batch->tileCount)
            PrefetchNextTileData(batch->context, batch->tiles[tileIndex + 1]);

        // C++ 异常协议：捕获用户回调异常 → 记录第一个 → 计数继续正常递减（任务不悬挂）；
        // Complete() 在退役后 rethrow；多次异常只保留第一个。
        try
        {
            batch->executeTile(batch->context, batch->tiles[tileIndex]);
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
        if (batch->tilesRemaining.fetch_sub(1, std::memory_order_acq_rel) == 1)
        {
            batch->lastTileAt.store(MonotonicNowNs(), std::memory_order_release);
            TryCompleteLogicalBatch(batch);
        }
        return true;
    }

    static void RecordWorkerEntry(BatchState* batch) noexcept
    {
        // 诊断计数：relaxed 足够（只记录首/末 worker 进入时刻）。
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
        if (batch->logicalCompleted.exchange(
            true, std::memory_order_acq_rel)) return;
        auto* state = batch->handle;

        RecordFinalizedBatchTiming(batch);
        const uint64_t publishedAt =
            batch->publishedAt.load(std::memory_order_acquire);
        const uint64_t lastTileAt =
            batch->lastTileAt.load(std::memory_order_acquire);
        if (publishedAt != 0 && lastTileAt >= publishedAt + kLongBatchBarrierNs)
            RegisterLongBatchBarrier(state);
        // Cleanup 必须先于 CompleteState，使依赖 continuation 看到已完全退役的用户上下文；
        // 剩余 task token 使 BatchStorage 存活到下方物理 finalizer。
        RunBatchCleanup(batch, state);
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

        // Chase-Lev 唯一路径：逻辑完成由最后 tile 完成者负责，物理退役由最后 task
        // 完成者负责；pendingTasks 归零时 tilesRemaining 必已归零（tile 均在 task 内
        // 执行），故退役单线程执行，消除双完成者并发访问 batch 的 data race。
        RecordTopologyCompletion(batch);
    }

    void SubmitBatch(BatchState* batch, int /*workerCap*/)
    {
        if (!batch || !batch->handle) return;
        if (!g_chaseLevScheduler || !g_chaseLevScheduler->IsRunning())
        {
            AbortUnsubmittedBatch(
                batch,
                std::make_exception_ptr(std::runtime_error(
                    "JobSystem backend is not running")));
            return;
        }
        auto* state = batch->handle;
        const int participantCount = std::max(1, static_cast<int>(batch->workerCount));

        g_frameTasksSubmitted.fetch_add(static_cast<uint64_t>(participantCount), std::memory_order_relaxed);
        g_publishedJobs.fetch_add(1, std::memory_order_relaxed);
        g_workerTargetTotal.fetch_add(static_cast<uint64_t>(participantCount), std::memory_order_relaxed);
        g_totalTilesPublished.fetch_add(
            static_cast<uint64_t>(batch->tileCount),
            std::memory_order_relaxed);

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
        g_nativeBatches.fetch_add(1, std::memory_order_relaxed);

        g_chaseLevScheduler->SubmitBatch(batch);
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
        if (g_chaseLevScheduler)
            g_chaseLevScheduler->WakePending();
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
            delete bc;
            throw;
        }
        delete bc;
    }

    void DestroyChunkContextWithoutCleanup(void* ctx) noexcept
    {
        delete static_cast<ChunkBatchContext*>(ctx);
    }

    bool GeneralExecuteTile(void* ctx, const ExecutionTile& tile)
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

    void CleanupGeneralContext(void* ctx)
    {
        auto* bc = static_cast<GeneralBatchContext*>(ctx);
        try
        {
            if (bc->originalCleanup) bc->originalCleanup(bc->originalContext);
        }
        catch (...)
        {
            delete bc;
            throw;
        }
        delete bc;
    }

    void DestroyGeneralContextWithoutCleanup(void* ctx) noexcept
    {
        delete static_cast<GeneralBatchContext*>(ctx);
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
            if (g_jobCostCacheEnabled.load(std::memory_order_relaxed) &&
                batch->funcHash != 0 && batch->totalElements > 0)
            {
                // perElem 用纯执行口径：首 tile 开始 → 末 tile 完成
                //（旧口径含唤醒/排队会虚高）。
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
    void ChaseLevTaskDone(BatchState* batch) noexcept
    {
        if (!batch) return;
        if (batch->pendingTasks.fetch_sub(1, std::memory_order_acq_rel) == 1)
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
