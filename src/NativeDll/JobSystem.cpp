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
#include <cstdint>

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
    struct DispatchRecord {
        std::atomic<int> nextIndex{ 0 };
        int chunkSize = 1;
        int chunkCount = 0;
        int length = 0;
        void* context = nullptr;
        void (*indexFunc)(void*, int) = nullptr;
        void (*batchFunc)(void*, int, int) = nullptr;
        void (*chunkFunc)(void*, const ChunkJobData*) = nullptr;
        const ChunkJobData* chunks = nullptr;
        uint8_t kind = 0; // 1=parallel_for, 2=parallel_batch, 3=chunks
    };

    struct DepsLaunchPayload {
        std::shared_ptr<tf::Executor> executor;
        void (*run)(HandleState*, const std::shared_ptr<tf::Executor>&, void*) = nullptr;
        void* userPayload = nullptr;
        void (*userPayloadCleanup)(void*) = nullptr;
    };

    // ---------- 可调参数 ----------
    constexpr size_t kMaxPooledStates = 4096;
    constexpr size_t kMaxPooledTaskflows = 1024;

    constexpr int kSyncExecutionLengthThreshold = 512;
    constexpr int kSyncWithCompletedDepThreshold = 4096;
    constexpr int kDefaultSpinBeforeWait = 256;
    constexpr int kDefaultAssistAfterWaitLoops = 64;
    constexpr int kDefaultAssistBurstMax = 1;
    constexpr int kDefaultAssistCooldownWaitLoops = 16;
    constexpr int kDefaultMinChunkSize = 256;

    // 全局资源
    std::mutex g_executorMutex;
    std::shared_ptr<tf::Executor> g_executor;
    int g_numThreads = 0;

    std::mutex g_statePoolMutex;
    std::vector<HandleState*> g_statePool;

    std::mutex g_taskflowPoolMutex;
    std::vector<tf::Taskflow*> g_taskflowPool;
    std::mutex g_dispatchPoolMutex;
    std::vector<DispatchRecord*> g_dispatchPool;
    constexpr size_t kMaxPooledDispatchRecords = 4096;

    std::atomic<uint64_t> g_statCompleteWaitLoops{ 0 };
    std::atomic<uint64_t> g_statAssistAttempts{ 0 };
    std::atomic<uint64_t> g_statAssistExecuted{ 0 };

    std::mutex g_tuningMutex;
    JobSystemTuning g_tuning{};

    namespace
    {
        DispatchRecord* AcquireDispatchRecord()
        {
            DispatchRecord* record = nullptr;
            {
                std::lock_guard<std::mutex> lock(g_dispatchPoolMutex);
                if (!g_dispatchPool.empty())
                {
                    record = g_dispatchPool.back();
                    g_dispatchPool.pop_back();
                }
            }
            if (!record) record = new DispatchRecord();
            record->nextIndex.store(0, std::memory_order_relaxed);
            record->chunkSize = 1;
            record->chunkCount = 0;
            record->length = 0;
            record->context = nullptr;
            record->indexFunc = nullptr;
            record->batchFunc = nullptr;
            record->chunkFunc = nullptr;
            record->chunks = nullptr;
            record->kind = 0;
            return record;
        }

        void ReleaseDispatchRecord(DispatchRecord* record) noexcept
        {
            if (!record) return;
            std::lock_guard<std::mutex> lock(g_dispatchPoolMutex);
            if (g_dispatchPool.size() < kMaxPooledDispatchRecords) g_dispatchPool.push_back(record);
            else delete record;
        }

        inline bool RunAssistToken(const AssistToken& token) noexcept
        {
            auto* record = token.record;
            if (!record) return false;
            const int idx = record->nextIndex.fetch_add(1, std::memory_order_relaxed);
            if (idx >= record->chunkCount) return false;

            if (token.kind == 1 && record->indexFunc)
            {
                const int begin = idx * record->chunkSize;
                const int end = std::min(record->length, begin + record->chunkSize);
                for (int i = begin; i < end; ++i) record->indexFunc(record->context, i);
                return true;
            }
            if (token.kind == 2 && record->batchFunc)
            {
                const int start = idx * record->chunkSize;
                const int count = std::min(record->chunkSize, record->length - start);
                if (count <= 0) return false;
                record->batchFunc(record->context, start, count);
                return true;
            }
            if (token.kind == 3 && record->chunkFunc && record->chunks)
            {
                record->chunkFunc(record->context, &record->chunks[idx]);
                return true;
            }
            return false;
        }
    }

    // ---------- 内部函数实现（非匿名，供 Exports.cpp 等 TU 使用） ----------

    void RecycleState(HandleState* state) noexcept
    {
        if (!state) return;
#ifdef _DEBUG
        state->inPool.store(true, std::memory_order_relaxed);
#endif
        {
            std::lock_guard<std::mutex> lock(state->mtx);
            state->assistToken = {};
        }
        state->inlineContinuation = {};
        state->continuations.clear();
        if (state->onDepsResolvedPayloadCleanup && state->onDepsResolvedPayload)
            state->onDepsResolvedPayloadCleanup(state->onDepsResolvedPayload);
        state->onDepsResolved = nullptr;
        state->onDepsResolvedPayload = nullptr;
        state->onDepsResolvedPayloadCleanup = nullptr;
        state->dependents.clear();
        state->waiterCount.store(0, std::memory_order_relaxed);
        state->completed.store(false, std::memory_order_relaxed);
        state->unfinishedDeps.store(0, std::memory_order_relaxed);
        state->depsResolvedFired.store(false, std::memory_order_relaxed);
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
        state->unfinishedDeps.store(0, std::memory_order_relaxed);
        state->depsResolvedFired.store(false, std::memory_order_relaxed);
        {
            std::lock_guard<std::mutex> lock(state->mtx);
            state->assistToken = {};
        }
        state->inlineContinuation = {};
        state->continuations.clear();
        state->onDepsResolved = nullptr;
        state->onDepsResolvedPayload = nullptr;
        state->onDepsResolvedPayloadCleanup = nullptr;
        state->dependents.clear();
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
        std::vector<HandleState*> dependents;
        {
            std::lock_guard<std::mutex> lock(state->mtx);
            if (state->completed.exchange(true, std::memory_order_acq_rel))
                return;
            state->assistToken = {};
            inlineContinuation = std::move(state->inlineContinuation);
            continuations.swap(state->continuations);
            dependents.swap(state->dependents);
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

        for (auto* dependent : dependents)
        {
            if (!dependent) continue;
            const int prev = dependent->unfinishedDeps.fetch_sub(1, std::memory_order_acq_rel);
            if (prev == 1)
            {
                OnDepsResolvedFn onDepsResolved = nullptr;
                void* payload = nullptr;
                OnDepsResolvedCleanupFn payloadCleanup = nullptr;
                {
                    std::lock_guard<std::mutex> lock(dependent->mtx);
                    onDepsResolved = dependent->onDepsResolved;
                    payload = dependent->onDepsResolvedPayload;
                    payloadCleanup = dependent->onDepsResolvedPayloadCleanup;
                    dependent->onDepsResolved = nullptr;
                    dependent->onDepsResolvedPayload = nullptr;
                    dependent->onDepsResolvedPayloadCleanup = nullptr;
                }
                if (onDepsResolved && !dependent->depsResolvedFired.exchange(true, std::memory_order_acq_rel))
                {
                    onDepsResolved(dependent, payload);
                    if (payloadCleanup && payload) payloadCleanup(payload);
                }
                else
                {
                    CompleteState(dependent);
                }
            }
            ReleaseState(dependent);
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

    static void AddDependencyLink(HandleState* dependency, HandleState* dependent)
    {
        if (!dependency || !dependent) return;
        dependent->unfinishedDeps.fetch_add(1, std::memory_order_relaxed);
        AcquireState(dependent);

        bool alreadyCompleted = false;
        {
            std::lock_guard<std::mutex> lock(dependency->mtx);
            alreadyCompleted = dependency->completed.load(std::memory_order_acquire);
            if (!alreadyCompleted)
            {
                dependency->dependents.push_back(dependent);
            }
        }

        if (alreadyCompleted)
        {
            const int prev = dependent->unfinishedDeps.fetch_sub(1, std::memory_order_acq_rel);
            if (prev == 1)
            {
                OnDepsResolvedFn onDepsResolved = nullptr;
                void* payload = nullptr;
                OnDepsResolvedCleanupFn payloadCleanup = nullptr;
                {
                    std::lock_guard<std::mutex> lock(dependent->mtx);
                    onDepsResolved = dependent->onDepsResolved;
                    payload = dependent->onDepsResolvedPayload;
                    payloadCleanup = dependent->onDepsResolvedPayloadCleanup;
                    dependent->onDepsResolved = nullptr;
                    dependent->onDepsResolvedPayload = nullptr;
                    dependent->onDepsResolvedPayloadCleanup = nullptr;
                }
                if (onDepsResolved && !dependent->depsResolvedFired.exchange(true, std::memory_order_acq_rel))
                {
                    onDepsResolved(dependent, payload);
                    if (payloadCleanup && payload) payloadCleanup(payload);
                }
                else
                {
                    CompleteState(dependent);
                }
            }
            ReleaseState(dependent);
        }
    }

    static void TryRunDepsResolved(HandleState* state)
    {
        if (!state) return;
        if (state->unfinishedDeps.load(std::memory_order_acquire) != 0) return;
        if (state->depsResolvedFired.exchange(true, std::memory_order_acq_rel)) return;

        OnDepsResolvedFn onDepsResolved = nullptr;
        void* payload = nullptr;
        OnDepsResolvedCleanupFn payloadCleanup = nullptr;
        {
            std::lock_guard<std::mutex> lock(state->mtx);
            onDepsResolved = state->onDepsResolved;
            payload = state->onDepsResolvedPayload;
            payloadCleanup = state->onDepsResolvedPayloadCleanup;
            state->onDepsResolved = nullptr;
            state->onDepsResolvedPayload = nullptr;
            state->onDepsResolvedPayloadCleanup = nullptr;
        }
        if (onDepsResolved) {
            onDepsResolved(state, payload);
            if (payloadCleanup && payload) payloadCleanup(payload);
        }
        else CompleteState(state);
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

    void SetTuning(const JobSystemTuning& tuning)
    {
        std::lock_guard<std::mutex> lock(g_tuningMutex);
        g_tuning.spinBeforeWait = std::max(0, tuning.spinBeforeWait);
        g_tuning.assistAfterWaitLoops = std::max(1, tuning.assistAfterWaitLoops);
        g_tuning.assistBurstMax = std::max(1, tuning.assistBurstMax);
        g_tuning.assistCooldownWaitLoops = std::max(1, tuning.assistCooldownWaitLoops);
        g_tuning.minChunkSize = std::max(1, tuning.minChunkSize);
        g_tuning.workerPriorityMode = tuning.workerPriorityMode;
    }

    JobSystemTuning GetTuning()
    {
        std::lock_guard<std::mutex> lock(g_tuningMutex);
        return g_tuning;
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
            int minChunkSize = kDefaultMinChunkSize;
            {
                std::lock_guard<std::mutex> lock(g_tuningMutex);
                minChunkSize = std::max(1, g_tuning.minChunkSize);
            }
            return std::max(minChunkSize, length / (workerCount * 2));
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
            struct BuilderPayload {
                WorkBuilder builder;
                explicit BuilderPayload(WorkBuilder&& b) : builder(std::move(b)) {}
            };
            auto runBuilder = [](HandleState* state, const std::shared_ptr<tf::Executor>& executor, void* p) {
                auto* payload = static_cast<BuilderPayload*>(p);
                payload->builder(state, executor);
            };
            auto freeBuilderPayload = [](void* p) { delete static_cast<BuilderPayload*>(p); };
            auto launchFromDeps = [](HandleState* state, void* p) {
                auto* launch = static_cast<DepsLaunchPayload*>(p);
                launch->run(state, launch->executor, launch->userPayload);
            };
            auto freeLaunchPayload = [](void* p) {
                auto* launch = static_cast<DepsLaunchPayload*>(p);
                if (launch->userPayloadCleanup && launch->userPayload)
                    launch->userPayloadCleanup(launch->userPayload);
                delete launch;
            };

            auto* state = CreateState(false);
            auto executor = EnsureExecutor();
            auto* depState = dependency.State();

            auto* builderPayload = new BuilderPayload(std::forward<WorkBuilder>(builder));
            auto* launchPayload = new DepsLaunchPayload{
                executor,
                runBuilder,
                static_cast<void*>(builderPayload),
                freeBuilderPayload
            };

            state->onDepsResolved = launchFromDeps;
            state->onDepsResolvedPayload = launchPayload;
            state->onDepsResolvedPayloadCleanup = freeLaunchPayload;

            if (!depState || depState->completed.load(std::memory_order_acquire))
            {
                TryRunDepsResolved(state);
                return JobHandle(state);
            }

            AddDependencyLink(depState, state);
            TryRunDepsResolved(state);
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
            return ScheduleWithDependency(dependency, [work = std::forward<Work>(work), context, cleanup](HandleState* state, const std::shared_ptr<tf::Executor>& executor) mutable {
                ScheduleFastPath(std::forward<Work>(work), context, cleanup, state, executor);
                });
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
        int spinBeforeWait = kDefaultSpinBeforeWait;
        int assistAfterWaitLoops = kDefaultAssistAfterWaitLoops;
        int assistBurstMax = kDefaultAssistBurstMax;
        int assistCooldownWaitLoops = kDefaultAssistCooldownWaitLoops;
        {
            std::lock_guard<std::mutex> lock(g_tuningMutex);
            spinBeforeWait = std::max(0, g_tuning.spinBeforeWait);
            assistAfterWaitLoops = std::max(1, g_tuning.assistAfterWaitLoops);
            assistBurstMax = std::max(1, g_tuning.assistBurstMax);
            assistCooldownWaitLoops = std::max(1, g_tuning.assistCooldownWaitLoops);
        }

        for (int i = 0; i < spinBeforeWait; ++i) {
            if (_state->completed.load(std::memory_order_acquire)) return;
            RelaxCpu();
        }

        int waitLoops = 0;
        int cooldown = 0;
        while (!_state->completed.load(std::memory_order_acquire)) {
            ++waitLoops;
            g_statCompleteWaitLoops.fetch_add(1, std::memory_order_relaxed);
            if (cooldown > 0) --cooldown;

            if (cooldown == 0 && waitLoops >= assistAfterWaitLoops) {
                waitLoops = 0;
                AssistToken assistToken{};
                {
                    std::lock_guard<std::mutex> lock(_state->mtx);
                    assistToken = _state->assistToken;
                }
                g_statAssistAttempts.fetch_add(1, std::memory_order_relaxed);
                if (assistToken.record) {
                    for (int i = 0; i < assistBurstMax; ++i) {
                        if (!RunAssistToken(assistToken)) break;
                        g_statAssistExecuted.fetch_add(1, std::memory_order_relaxed);
                        if (_state->completed.load(std::memory_order_acquire)) return;
                    }
                    cooldown = assistCooldownWaitLoops;
                }
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
        for (const auto& depState : pendingStates) {
            AddDependencyLink(depState, combinedState);
        }
        if (combinedState->unfinishedDeps.load(std::memory_order_acquire) == 0)
        {
            CompleteState(combinedState);
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

            int priorityMode = 0;
            {
                std::lock_guard<std::mutex> tuningLock(g_tuningMutex);
                priorityMode = g_tuning.workerPriorityMode;
            }
            // 默认 normal 优先级，复杂环境更稳（可通过 tuning 切到 above_normal）
            for (int i = 0; i < g_numThreads; ++i)
            {
                g_executor->silent_async([priorityMode]() {
#ifdef _WIN32
                    int winPriority = (priorityMode == 1) ? THREAD_PRIORITY_ABOVE_NORMAL : THREAD_PRIORITY_NORMAL;
                    SetThreadPriority(GetCurrentThread(), winPriority);
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
            DispatchRecord* record = AcquireDispatchRecord();
            record->kind = 1;
            record->indexFunc = func;
            record->context = context;
            record->length = length;
            record->chunkSize = chunkSize;
            record->chunkCount = chunkCount;
            {
                std::lock_guard<std::mutex> lock(state->mtx);
                state->assistToken = AssistToken{ record, 1 };
            }
            auto taskflow = AcquireTaskflow();
            (*taskflow).clear();
            [=](tf::Taskflow& taskflow) {
                for (int w = 0; w < workerCount; ++w)
                {
                    taskflow.emplace([record]() {
                        while (RunAssistToken(AssistToken{ record, 1 })) {}
                        });
                }
                }(*taskflow);

            AcquireState(state);
            executor->run(*taskflow, [taskflow, state, context, cleanup, record]() mutable {
                ReleaseDispatchRecord(record);
                if (cleanup) cleanup(context);
                CompleteState(state);
                ReleaseState(state);
                });
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
            DispatchRecord* record = AcquireDispatchRecord();
            record->kind = 2;
            record->batchFunc = func;
            record->context = context;
            record->length = length;
            record->chunkSize = safeBatchSize;
            record->chunkCount = batchCount;
            {
                std::lock_guard<std::mutex> lock(state->mtx);
                state->assistToken = AssistToken{ record, 2 };
            }
            auto taskflow = AcquireTaskflow();
            (*taskflow).clear();
            [=](tf::Taskflow& taskflow) {
                for (int w = 0; w < workerCount; ++w)
                {
                    taskflow.emplace([record]() {
                        while (RunAssistToken(AssistToken{ record, 2 })) {}
                        });
                }
                }(*taskflow);

            AcquireState(state);
            executor->run(*taskflow, [taskflow, state, context, cleanup, record]() mutable {
                ReleaseDispatchRecord(record);
                if (cleanup) cleanup(context);
                CompleteState(state);
                ReleaseState(state);
                });
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

        const int workerCount = std::max(1, CurrentWorkerCount());
        return ScheduleWithDependency(dependency, [=](HandleState* state, const std::shared_ptr<tf::Executor>& executor) {
            DispatchRecord* record = AcquireDispatchRecord();
            record->kind = 3;
            record->chunkFunc = func;
            record->context = context;
            record->chunks = chunks;
            record->chunkCount = chunkCount;
            record->chunkSize = 1;
            record->length = chunkCount;
            {
                std::lock_guard<std::mutex> lock(state->mtx);
                state->assistToken = AssistToken{ record, 3 };
            }

            auto taskflow = AcquireTaskflow();
            taskflow->clear();
            for (int w = 0; w < workerCount; ++w)
            {
                taskflow->emplace([record]() {
                    while (RunAssistToken(AssistToken{ record, 3 })) {}
                    });
            }

            AcquireState(state);
            executor->run(*taskflow, [taskflow, state, context, cleanup, record]() mutable {
                ReleaseDispatchRecord(record);
                if (cleanup) cleanup(context);
                CompleteState(state);
                ReleaseState(state);
                });
            });
    }

} // namespace JobSystem
