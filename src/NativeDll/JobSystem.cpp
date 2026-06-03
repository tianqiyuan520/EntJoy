#include "JobSystem.h"
#include "ChunkJobData.h"
#include "JobProfiler.h"
#include "../../external/cpp-taskflow/taskflow/taskflow.hpp"

#include <algorithm>
#if defined(_MSC_VER) && (defined(_M_IX86) || defined(_M_X64))
#include <immintrin.h>
#endif
#include <thread>
#include <utility>
#include <atomic>
#include <memory>
#include <deque>
#include <condition_variable>

// ---------- 跨平台线程优先级 ----------
#ifdef _WIN32
#define NOMINMAX
#include <windows.h>
#else
#include <pthread.h>
#include <sched.h>
#include <sys/resource.h>
#endif

namespace JobSystem
{
    // ---------- 可调参数 ----------
    constexpr size_t kMaxPooledStates = 4096;
    constexpr size_t kMaxPooledTaskflows = 1024;

    constexpr int kSyncExecutionLengthThreshold = 512;
    constexpr int kSyncWithCompletedDepThreshold = 4096;
    constexpr int kSpinBeforeWait = 256;

    // 全局资源
    std::mutex g_executorMutex;
    std::shared_ptr<tf::Executor> g_executor;
    int g_numThreads = 0;

    std::mutex g_statePoolMutex;
    std::vector<HandleState*> g_statePool;

    std::mutex g_taskflowPoolMutex;
    std::vector<tf::Taskflow*> g_taskflowPool;

    // ---------- 内部函数实现（非匿名，供 Exports.cpp 等 TU 使用） ----------

    void RecycleState(HandleState* state) noexcept
    {
        if (!state) return;
#ifdef _DEBUG
        state->inPool.store(true, std::memory_order_relaxed);
#endif
        {
            std::lock_guard<std::mutex> lock(state->mtx);
            state->assistStep = {};
        }
        state->inlineContinuation = {};
        state->continuations.clear();
        state->waiterCount.store(0, std::memory_order_relaxed);
        state->completed.store(false, std::memory_order_relaxed);
        state->refCount.store(1, std::memory_order_relaxed);

        std::lock_guard<std::mutex> lock(g_statePoolMutex);
        if (g_statePool.size() < kMaxPooledStates)
            g_statePool.push_back(state);
        else
            delete state;
    }

    HandleState* CreateState(bool completed)
    {
        HandleState* state = nullptr;
        {
            std::lock_guard<std::mutex> lock(g_statePoolMutex);
            if (!g_statePool.empty())
            {
                state = g_statePool.back();
                g_statePool.pop_back();
            }
        }
        if (!state) state = new HandleState(completed);
        state->refCount.store(1, std::memory_order_relaxed);
        state->completed.store(completed, std::memory_order_relaxed);
        state->waiterCount.store(0, std::memory_order_relaxed);
        {
            std::lock_guard<std::mutex> lock(state->mtx);
            state->assistStep = {};
        }
        state->inlineContinuation = {};
        state->continuations.clear();
#ifdef _DEBUG
        state->inPool.store(false, std::memory_order_relaxed);
        state->generation.fetch_add(1, std::memory_order_relaxed);
#endif
        return state;
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

    void CompleteState(HandleState* state)
    {
        if (!state) return;
        std::function<void()> inlineContinuation;
        std::vector<std::function<void()>> continuations;
        {
            std::lock_guard<std::mutex> lock(state->mtx);
            if (state->completed.exchange(true, std::memory_order_acq_rel))
                return;
            state->assistStep = {};
            inlineContinuation = std::move(state->inlineContinuation);
            continuations.swap(state->continuations);
        }

        state->completed.notify_all();

        if (inlineContinuation)
        {
            try { inlineContinuation(); }
            catch (...) {}
        }
        for (auto& cont : continuations)
        {
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
        std::function<void()> toRun;
        {
            std::lock_guard<std::mutex> lock(state->mtx);
            if (state->completed.load(std::memory_order_acquire))
                toRun = std::move(continuation);
            else if (!state->inlineContinuation)
                state->inlineContinuation = std::move(continuation);
            else
                state->continuations.emplace_back(std::move(continuation));
        }
        if (toRun) toRun();
    }

    std::shared_ptr<tf::Executor> EnsureExecutor()
    {
        std::lock_guard<std::mutex> lock(g_executorMutex);
        if (!g_executor)
        {
            g_numThreads = ResolveWorkerCount(0);
            g_executor = std::make_shared<tf::Executor>(static_cast<size_t>(g_numThreads));
        }
        return g_executor;
    }

    // 统一使用全部逻辑核心
    // 所有进程实例都使用 hardware_concurrency() 个线程
    // 由操作系统调度器负责在多个进程间分配 CPU 时间
    int ResolveWorkerCount(int numThreads)
    {
        if (numThreads > 0) return numThreads;
        const unsigned int hardwareThreads = std::thread::hardware_concurrency();
        // 默认预留 2 个逻辑核给系统/录屏/音频等后台任务，提升复杂环境下的帧稳定性
        constexpr unsigned int kReservedThreads = 2;
        if (hardwareThreads <= 2) return 1;
        const unsigned int workerThreads = (hardwareThreads > kReservedThreads)
            ? (hardwareThreads - kReservedThreads)
            : 1u;
        return static_cast<int>(std::max(1u, workerThreads));
    }

    int CurrentWorkerCount()
    {
        std::lock_guard<std::mutex> lock(g_executorMutex);
        if (g_numThreads > 0) return g_numThreads;
        return ResolveWorkerCount(0);
    }

    namespace
    {
        using JobSystem::CreateState;
        using JobSystem::AcquireState;
        using JobSystem::ReleaseState;
        using JobSystem::CompleteState;
        using JobSystem::AddContinuationOrRunNow;
        using JobSystem::EnsureExecutor;

        void ReturnTaskflow(tf::Taskflow* taskflow) noexcept
        {
            if (!taskflow) return;
            taskflow->clear();
            std::lock_guard<std::mutex> lock(g_taskflowPoolMutex);
            if (g_taskflowPool.size() < kMaxPooledTaskflows)
                g_taskflowPool.push_back(taskflow);
            else
                delete taskflow;
        }

        std::shared_ptr<tf::Taskflow> AcquireTaskflow()
        {
            tf::Taskflow* taskflow = nullptr;
            {
                std::lock_guard<std::mutex> lock(g_taskflowPoolMutex);
                if (!g_taskflowPool.empty())
                {
                    taskflow = g_taskflowPool.back();
                    g_taskflowPool.pop_back();
                }
            }
            if (!taskflow) taskflow = new tf::Taskflow();
            return std::shared_ptr<tf::Taskflow>(taskflow, [](tf::Taskflow* ptr) { ReturnTaskflow(ptr); });
        }

        int ResolveChunkSize(int length, int requestedChunk)
        {
            if (length <= 0) return 1;
            if (requestedChunk > 0) return requestedChunk;
            const int workerCount = std::max(1, CurrentWorkerCount());
            return std::max(64, length / (workerCount * 2));
        }

        inline void RelaxCpu() noexcept
        {
#if defined(_MSC_VER) && (defined(_M_IX86) || defined(_M_X64))
            _mm_pause();
#elif defined(__i386__) || defined(__x86_64__)
            __builtin_ia32_pause();
#else
            std::this_thread::yield();
#endif
        }

        JobHandle MakeCompletedHandle() { return JobHandle(CreateState(true)); }

        // ---------- 调度辅助 ----------
        template <typename WorkBuilder>
        JobHandle ScheduleWithDependency(const JobHandle& dependency, WorkBuilder&& builder)
        {
            auto* state = CreateState(false);
            auto executor = EnsureExecutor();
            auto* depState = dependency.State();

            if (!depState || depState->completed.load(std::memory_order_acquire))
            {
                builder(state, executor);
                return JobHandle(state);
            }

            AcquireState(state);
            auto launch = [state, executor, builder = std::forward<WorkBuilder>(builder)]() mutable {
                builder(state, executor);
                ReleaseState(state);
                };
            AddContinuationOrRunNow(depState, std::move(launch));
            return JobHandle(state);
        }

        template <typename Work>
        void ScheduleFastPath(Work&& work, void* context, void (*cleanup)(void*), HandleState* state, const std::shared_ptr<tf::Executor>& executor)
        {
            AcquireState(state);
            executor->silent_async([work = std::forward<Work>(work), state, context, cleanup]() {
                work();
                if (cleanup) cleanup(context);
                CompleteState(state);
                ReleaseState(state);
                });
        }

        template <typename Work>
        JobHandle ScheduleFastPathWithDependency(Work&& work, void* context, void (*cleanup)(void*), const JobHandle& dependency)
        {
            auto* state = CreateState(false);
            auto executor = EnsureExecutor();
            auto* depState = dependency.State();

            if (!depState || depState->completed.load(std::memory_order_acquire))
            {
                ScheduleFastPath(std::forward<Work>(work), context, cleanup, state, executor);
                return JobHandle(state);
            }

            AcquireState(state);
            auto launch = [work = std::forward<Work>(work), context, cleanup, state, executor]() mutable {
                ScheduleFastPath(std::forward<Work>(work), context, cleanup, state, executor);
                ReleaseState(state);
                };
            AddContinuationOrRunNow(depState, std::move(launch));
            return JobHandle(state);
        }

        template <typename TaskflowBuilder>
        void SubmitTaskflow(const std::shared_ptr<tf::Executor>& executor, TaskflowBuilder&& builder,
            HandleState* state, void* context, void (*cleanup)(void*))
        {
            auto taskflow = AcquireTaskflow();
            builder(*taskflow);
            AcquireState(state);
            executor->run(*taskflow, [taskflow, state, context, cleanup]() mutable {
                if (cleanup) cleanup(context);
                CompleteState(state);
                ReleaseState(state);
                });
        }

        // 同步执行包装
        inline void ExecuteJobSync(void (*func)(void*), void* context) { func(context); }
        inline void ExecuteForSync(void (*func)(void*, int), void* context, int length) {
            for (int i = 0; i < length; ++i) func(context, i);
        }
        inline void ExecuteBatchSync(void (*func)(void*, int, int), void* context, int length) {
            func(context, 0, length);
        }

    } // namespace

    // ========== JobHandle 实现 ==========
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

    void JobHandle::Complete() const
    {
        if (!_state) return;
        if (_state->completed.load(std::memory_order_acquire)) return;

        // 阶段 1: 有限自旋，覆盖极短任务，避免立刻进入内核等待路径
        for (int i = 0; i < kSpinBeforeWait; ++i) {
            if (_state->completed.load(std::memory_order_acquire)) return;
            RelaxCpu();
        }

        // 阶段 2: 主线程协作执行（Unity-style help execute）
        // 阶段 3: 无可执行工作时再阻塞等待，降低复杂负载场景下的抢占与抖动
        while (!_state->completed.load(std::memory_order_acquire)) {
            std::function<bool()> assistStep;
            {
                std::lock_guard<std::mutex> lock(_state->mtx);
                assistStep = _state->assistStep;
            }
            if (assistStep && assistStep()) {
                continue;
            }
            _state->completed.wait(false, std::memory_order_acquire);
        }
    }

    bool JobHandle::IsCompleted() const noexcept {
        return !_state || _state->completed.load(std::memory_order_acquire);
    }

    HandleState* JobHandle::State() const noexcept { return _state; }

    JobHandle JobHandle::CombineDependencies(const std::vector<JobHandle>& handles)
    {
        std::vector<HandleState*> pendingStates;
        pendingStates.reserve(handles.size());
        for (const auto& h : handles)
            if (h._state && !h._state->completed.load(std::memory_order_acquire))
                pendingStates.push_back(h._state);
        if (pendingStates.empty()) return MakeCompletedHandle();

        auto* combinedState = CreateState(false);
        auto remaining = std::make_shared<std::atomic<int>>(static_cast<int>(pendingStates.size()));

        for (const auto& depState : pendingStates) {
            AcquireState(combinedState);
            AddContinuationOrRunNow(depState, [combinedState, remaining]() {
                if (remaining->fetch_sub(1, std::memory_order_acq_rel) == 1)
                    CompleteState(combinedState);
                ReleaseState(combinedState);
                });
        }
        return JobHandle(combinedState);
    }

    // ========== Scheduler 实现 ==========
    void Scheduler::Initialize(int numThreads)
    {
        const int resolved = ResolveWorkerCount(numThreads);
        std::shared_ptr<tf::Executor> oldExecutor;
        {
            std::lock_guard<std::mutex> lock(g_executorMutex);
            if (g_executor && g_numThreads == resolved) return;
            oldExecutor = std::move(g_executor);
            g_numThreads = resolved;
            g_executor = std::make_shared<tf::Executor>(static_cast<size_t>(g_numThreads));

            // 使用高线程优先级，减少被其他线程抢占导致的抖动
            for (int i = 0; i < g_numThreads; ++i)
            {
                g_executor->silent_async([]() {
#ifdef _WIN32
                    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);
#else
                    setpriority(PRIO_PROCESS, 0, -5);
#endif
                    });
            }
            g_executor->wait_for_all();
        }
        if (oldExecutor) oldExecutor->wait_for_all();
    }

    void Scheduler::Shutdown()
    {
        std::shared_ptr<tf::Executor> executor;
        {
            std::lock_guard<std::mutex> lock(g_executorMutex);
            executor = std::move(g_executor);
            g_numThreads = 0;
        }
        if (executor) executor->wait_for_all();

        {
            std::lock_guard<std::mutex> lock(g_statePoolMutex);
            for (auto* s : g_statePool) delete s;
            g_statePool.clear();
        }
        {
            std::lock_guard<std::mutex> lock(g_taskflowPoolMutex);
            for (auto* tf : g_taskflowPool) delete tf;
            g_taskflowPool.clear();
        }
    }

    void Scheduler::PrewakeWorkers()
    {
        std::shared_ptr<tf::Executor> executor;
        int workerCount = 0;
        {
            std::lock_guard<std::mutex> lock(g_executorMutex);
            executor = g_executor;
            workerCount = g_numThreads;
        }
        if (!executor || workerCount <= 0) return;

        const int wakeCount = std::min(workerCount, 8);
        for (int i = 0; i < wakeCount; ++i)
        {
            executor->silent_async([] {});
        }
    }

    JobHandle Scheduler::Schedule(
        void (*func)(void*), void* context,
        void (*cleanup)(void*),
        const JobHandle& dependency)
    {
        if (!func) { if (cleanup) cleanup(context); return MakeCompletedHandle(); }

        if (!dependency.State() || dependency.IsCompleted())
        {
            ExecuteJobSync(func, context);
            if (cleanup) cleanup(context);
            return MakeCompletedHandle();
        }

        return ScheduleFastPathWithDependency(
            [func, context]() { ExecuteJobSync(func, context); },
            context, cleanup, dependency);
    }

    JobHandle Scheduler::ScheduleFor(
        void (*func)(void*, int), void* context,
        int length,
        void (*cleanup)(void*),
        const JobHandle& dependency)
    {
        if (!func) { if (cleanup) cleanup(context); return MakeCompletedHandle(); }
        if (length <= 0) { if (cleanup) cleanup(context); return MakeCompletedHandle(); }

        bool depCompleted = !dependency.State() || dependency.IsCompleted();

        if (length <= kSyncExecutionLengthThreshold || (depCompleted && length <= kSyncWithCompletedDepThreshold))
        {
            ExecuteForSync(func, context, length);
            if (cleanup) cleanup(context);
            return MakeCompletedHandle();
        }

        if (length <= 64)
        {
            return ScheduleFastPathWithDependency(
                [func, context, length]() { ExecuteForSync(func, context, length); },
                context, cleanup, dependency);
        }

        return ScheduleWithDependency(dependency, [func, context, length, cleanup](HandleState* state, auto executor) {
            SubmitTaskflow(executor, [func, context, length](tf::Taskflow& taskflow) {
                taskflow.emplace([func, context, length]() { ExecuteForSync(func, context, length); });
                }, state, context, cleanup);
            });
    }

    JobHandle Scheduler::ScheduleParallelFor(
        void (*func)(void*, int), void* context,
        int length, int batchSize,
        void (*cleanup)(void*),
        const JobHandle& dependency)
    {
        if (!func) { if (cleanup) cleanup(context); return MakeCompletedHandle(); }
        if (length <= 0) { if (cleanup) cleanup(context); return MakeCompletedHandle(); }

        bool depCompleted = !dependency.State() || dependency.IsCompleted();

        if (length <= kSyncExecutionLengthThreshold || (depCompleted && length <= kSyncWithCompletedDepThreshold))
        {
            ExecuteForSync(func, context, length);
            if (cleanup) cleanup(context);
            return MakeCompletedHandle();
        }

        const int chunkSize = ResolveChunkSize(length, batchSize);
        const int chunkCount = (length + chunkSize - 1) / chunkSize;
        const int workerCount = std::max(1, CurrentWorkerCount());

        if (chunkCount == 1)
        {
            return ScheduleFastPathWithDependency(
                [func, context, length]() { ExecuteForSync(func, context, length); },
                context, cleanup, dependency);
        }

        return ScheduleWithDependency(dependency, [=](HandleState* state, auto executor) {
            // 当 chunk 数较少时，使用 silent_async 直接提交每个 chunk
            // 避免 taskflow 图构建开销（taskflow 的 emplace + run 对于少量 task 开销占比大）
            if (chunkCount <= workerCount * 2)
            {
                auto remaining = std::make_shared<std::atomic<int>>(chunkCount);
                for (int i = 0; i < chunkCount; ++i)
                {
                    int begin = i * chunkSize;
                    int end = std::min(length, begin + chunkSize);
                    AcquireState(state);
                    executor->silent_async([=]() {
                        for (int j = begin; j < end; ++j) func(context, j);
                        if (--*remaining == 0)
                        {
                            if (cleanup) cleanup(context);
                            CompleteState(state);
                        }
                        ReleaseState(state);
                        });
                }
            }
            else
            {
                auto nextChunk = std::make_shared<std::atomic<int>>(0);
                auto runOneChunk = [=]() -> bool {
                    const int chunkIdx = nextChunk->fetch_add(1, std::memory_order_relaxed);
                    if (chunkIdx >= chunkCount) return false;
                    const int begin = chunkIdx * chunkSize;
                    const int end = std::min(length, begin + chunkSize);
                    for (int i = begin; i < end; ++i) func(context, i);
                    return true;
                };
                {
                    std::lock_guard<std::mutex> lock(state->mtx);
                    state->assistStep = runOneChunk;
                }
                SubmitTaskflow(executor, [=](tf::Taskflow& taskflow) {
                    for (int w = 0; w < workerCount; ++w)
                    {
                        taskflow.emplace([=]() {
                            while (runOneChunk()) {}
                            });
                    }
                    }, state, context, cleanup);
            }
            });
    }

    JobHandle Scheduler::ScheduleParallelForBatch(
        void (*func)(void*, int, int), void* context,
        int length, int batchSize,
        void (*cleanup)(void*),
        const JobHandle& dependency)
    {
        if (!func) { if (cleanup) cleanup(context); return MakeCompletedHandle(); }
        if (length <= 0) { if (cleanup) cleanup(context); return MakeCompletedHandle(); }

        bool depCompleted = !dependency.State() || dependency.IsCompleted();

        if (length <= kSyncExecutionLengthThreshold || (depCompleted && length <= kSyncWithCompletedDepThreshold))
        {
            ExecuteBatchSync(func, context, length);
            if (cleanup) cleanup(context);
            return MakeCompletedHandle();
        }

        const int safeBatchSize = std::max(1, ResolveChunkSize(length, batchSize));
        const int batchCount = (length + safeBatchSize - 1) / safeBatchSize;
        const int workerCount = std::max(1, CurrentWorkerCount());

        if (batchCount == 1)
        {
            return ScheduleFastPathWithDependency(
                [func, context, length]() { ExecuteBatchSync(func, context, length); },
                context, cleanup, dependency);
        }

        return ScheduleWithDependency(dependency, [=](HandleState* state, auto executor) {
            // 当 batch 数较少时，使用 silent_async 直接提交每个 batch
            // 避免 taskflow 图构建开销
            if (batchCount <= workerCount * 2)
            {
                auto remaining = std::make_shared<std::atomic<int>>(batchCount);
                for (int i = 0; i < batchCount; ++i)
                {
                    int start = i * safeBatchSize;
                    int count = std::min(safeBatchSize, length - start);
                    AcquireState(state);
                    executor->silent_async([=]() {
                        func(context, start, count);
                        if (--*remaining == 0)
                        {
                            if (cleanup) cleanup(context);
                            CompleteState(state);
                        }
                        ReleaseState(state);
                        });
                }
            }
            else
            {
                auto nextBatch = std::make_shared<std::atomic<int>>(0);
                auto runOneBatch = [=]() -> bool {
                    const int batchIdx = nextBatch->fetch_add(1, std::memory_order_relaxed);
                    if (batchIdx >= batchCount) return false;
                    const int start = batchIdx * safeBatchSize;
                    const int count = std::min(safeBatchSize, length - start);
                    func(context, start, count);
                    return true;
                };
                {
                    std::lock_guard<std::mutex> lock(state->mtx);
                    state->assistStep = runOneBatch;
                }
                SubmitTaskflow(executor, [=](tf::Taskflow& taskflow) {
                    for (int w = 0; w < workerCount; ++w)
                    {
                        taskflow.emplace([=]() {
                            while (runOneBatch()) {}
                            });
                    }
                    }, state, context, cleanup);
            }
            });
    }

    // ========== ScheduleChunks 实现 ==========
    JobHandle Scheduler::ScheduleChunks(
        void (*func)(void*, const struct ChunkJobData*), void* context,
        void (*cleanup)(void*),
        const struct ChunkJobData* chunks,
        int chunkCount,
        const JobHandle& dependency)
    {
        if (!func || chunkCount <= 0)
        {
            if (cleanup) cleanup(context);
            return MakeCompletedHandle();
        }

        auto* state = CreateState(false);
        auto executor = EnsureExecutor();

        auto launch = [=]() {
            const int workerCount = std::max(1, CurrentWorkerCount());

            if (chunkCount <= workerCount * 2)
            {
                auto remaining = std::make_shared<std::atomic<int>>(chunkCount);
                for (int i = 0; i < chunkCount; ++i)
                {
                    const ChunkJobData* chunkPtr = &chunks[i];
                    AcquireState(state);
                    executor->silent_async([=]() {
                        func(context, chunkPtr);
                        if (--*remaining == 0)
                        {
                            if (cleanup) cleanup(context);
                            CompleteState(state);
                        }
                        ReleaseState(state);
                    });
                }
                return;
            }

            auto nextChunk = std::make_shared<std::atomic<int>>(0);
            auto runOneChunk = [=]() -> bool {
                const int chunkIndex = nextChunk->fetch_add(1, std::memory_order_relaxed);
                if (chunkIndex >= chunkCount) return false;
                func(context, &chunks[chunkIndex]);
                return true;
            };

            {
                std::lock_guard<std::mutex> lock(state->mtx);
                state->assistStep = runOneChunk;
            }

            SubmitTaskflow(executor, [=](tf::Taskflow& taskflow) {
                for (int w = 0; w < workerCount; ++w)
                {
                    taskflow.emplace([=]() {
                        while (runOneChunk()) {}
                    });
                }
            }, state, context, cleanup);
            };

        if (dependency.State() && !dependency.IsCompleted())
        {
            AcquireState(state);
            AddContinuationOrRunNow(dependency.State(), [=]() {
                launch();
                ReleaseState(state);
            });
        }
        else
        {
            launch();
        }

        return JobHandle(state);
    }

} // namespace JobSystem
