#include "JobSystem.h"
#include "ChunkJobData.h"
#include "../../external/cpp-taskflow/taskflow/taskflow.hpp"

#include <algorithm>
#if defined(_MSC_VER) && (defined(_M_IX86) || defined(_M_X64))
#include <immintrin.h>
#endif
#include <thread>
#include <utility>
#include <atomic>
#include <memory>

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
    constexpr int kSpinBeforeWait = 16384;

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

    int ResolveWorkerCount(int numThreads)
    {
        if (numThreads > 0) return numThreads;
        const unsigned int hardwareThreads = std::thread::hardware_concurrency();
        return static_cast<int>(std::max(1u, hardwareThreads));
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

        int ResolveDispatchTaskCount(int workItemCount)
        {
            if (workItemCount <= 0) return 1;
            return std::max(1, std::min(workItemCount, CurrentWorkerCount()));
        }

        bool ShouldUseDispatchLoop(int workItemCount)
        {
            const int workerCount = std::max(1, CurrentWorkerCount());
            return workItemCount > workerCount * 8;
        }

        bool ShouldUseBatchDispatchLoop(int batchCount)
        {
            const int workerCount = std::max(1, CurrentWorkerCount());
            return batchCount > workerCount * 4;
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

        for (int spin = 0; spin < kSpinBeforeWait; ++spin) {
            RelaxCpu();
            if (_state->completed.load(std::memory_order_acquire)) return;
        }
        _state->completed.wait(false, std::memory_order_acquire);
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

            // 提交与线程数相等的任务，每个任务提升所在线程的优先级，保证所有工作线程都被覆盖
            for (int i = 0; i < g_numThreads; ++i)
            {
                g_executor->silent_async([]() {
#ifdef _WIN32
                    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
#else
                    struct sched_param param;
                    param.sched_priority = sched_get_priority_max(SCHED_FIFO);
                    if (pthread_setschedparam(pthread_self(), SCHED_FIFO, &param) != 0)
                    {
                        setpriority(PRIO_PROCESS, 0, -20);
                    }
#endif
                    });
            }
            g_executor->wait_for_all();  // 等待所有优先级设置任务执行完毕
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
        const int dispatchTaskCount = ResolveDispatchTaskCount(chunkCount);
        const bool useDispatchLoop = ShouldUseDispatchLoop(chunkCount);

        if (chunkCount == 1 || (useDispatchLoop && dispatchTaskCount == 1))
        {
            return ScheduleFastPathWithDependency(
                [func, context, length]() { ExecuteForSync(func, context, length); },
                context, cleanup, dependency);
        }

        return ScheduleWithDependency(dependency, [=](HandleState* state, auto executor) {
            SubmitTaskflow(executor, [=](tf::Taskflow& taskflow) {
                if (!useDispatchLoop)
                {
                    for (int c = 0; c < chunkCount; ++c)
                    {
                        taskflow.emplace([=]() {
                            int begin = c * chunkSize;
                            int end = std::min(length, begin + chunkSize);
                            for (int i = begin; i < end; ++i) func(context, i);
                            });
                    }
                }
                else
                {
                    auto nextChunk = std::make_shared<std::atomic<int>>(0);
                    for (int w = 0; w < dispatchTaskCount; ++w)
                    {
                        taskflow.emplace([=]() {
                            while (true) {
                                int chunkIdx = nextChunk->fetch_add(1, std::memory_order_relaxed);
                                if (chunkIdx >= chunkCount) break;
                                int begin = chunkIdx * chunkSize;
                                int end = std::min(length, begin + chunkSize);
                                for (int i = begin; i < end; ++i) func(context, i);
                            }
                            });
                    }
                }
                }, state, context, cleanup);
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
        const int dispatchTaskCount = ResolveDispatchTaskCount(batchCount);
        const bool useDispatchLoop = ShouldUseBatchDispatchLoop(batchCount);

        if (batchCount == 1 || (useDispatchLoop && dispatchTaskCount == 1))
        {
            return ScheduleFastPathWithDependency(
                [func, context, length]() { ExecuteBatchSync(func, context, length); },
                context, cleanup, dependency);
        }

        return ScheduleWithDependency(dependency, [=](HandleState* state, auto executor) {
            SubmitTaskflow(executor, [=](tf::Taskflow& taskflow) {
                if (!useDispatchLoop)
                {
                    for (int b = 0; b < batchCount; ++b)
                    {
                        taskflow.emplace([=]() {
                            int start = b * safeBatchSize;
                            int count = std::min(safeBatchSize, length - start);
                            func(context, start, count);
                            });
                    }
                }
                else
                {
                    auto nextBatch = std::make_shared<std::atomic<int>>(0);
                    for (int w = 0; w < dispatchTaskCount; ++w)
                    {
                        taskflow.emplace([=]() {
                            while (true) {
                                int batchIdx = nextBatch->fetch_add(1, std::memory_order_relaxed);
                                if (batchIdx >= batchCount) break;
                                int start = batchIdx * safeBatchSize;
                                int count = std::min(safeBatchSize, length - start);
                                func(context, start, count);
                            }
                            });
                    }
                }
                }, state, context, cleanup);
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
        auto remaining = std::make_shared<std::atomic<int>>(chunkCount);
        auto executor = EnsureExecutor();

        // chunks 是指针，按值捕获（指针本身很小，且访问的数据在 cleanup 之前一直有效）
        auto launch = [=]() {
            for (int i = 0; i < chunkCount; ++i)
            {
                // 重要：计算 chunkPtr 为 &chunks[i] 的指针值，按值捕获
                // 不要捕获局部引用（如 const auto& cd = chunks[i]; 并 capture &cd），
                // 因为该引用在 i 迭代结束后失效，lambda 异步执行时引用悬挂。
                const ChunkJobData* chunkPtr = &chunks[i];
                AcquireState(state);
                executor->silent_async([=]() {
                    // 在 worker 线程中执行 C# 回调
                    // chunkPtr 是按值捕获的指针，始终有效（非托管内存在 cleanup 之前不会被释放）
                    func(context, chunkPtr);

                    // 递减计数，如果是最后一个完成的，执行 cleanup + 完成 signal
                    if (--*remaining == 0)
                    {
                        if (cleanup) cleanup(context);
                        CompleteState(state);
                    }
                    ReleaseState(state);
                });
            }
        };

        // 处理依赖
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
