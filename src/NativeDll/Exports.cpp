#include "Exports.h"
#include "JobSystem.h"
#include "ChunkJobData.h"

static JobSystem::HandleState* fromHandle(void* ptr)
{
    return static_cast<JobSystem::HandleState*>(ptr);
}

static void* toHandle(const JobSystem::JobHandle& handle)
{
    if (auto* state = handle.State())
    {
        JobSystem::JobHandle::Acquire(state);
        return static_cast<void*>(state);
    }
    return nullptr;
}

extern "C"
{
    void JobSystem_Initialize(int numThreads)
    {
        JobSystem::Scheduler::Initialize(numThreads);
    }

    void JobSystem_Shutdown()
    {
        JobSystem::Scheduler::Shutdown();
    }

    void* JobSystem_Schedule(JobFunc func, void* context, ContextCleanupFunc cleanup, void* dependency)
    {
        JobSystem::JobHandle dep;
        if (dependency)
            dep = JobSystem::JobHandle(fromHandle(dependency), true);
        auto handle = JobSystem::Scheduler::Schedule(func, context, cleanup, dep);
        return toHandle(handle);
    }

    void* JobSystem_ScheduleParallelFor(IndexJobFunc func, void* context, ContextCleanupFunc cleanup,
        int length, int batchSize, void* dependency)
    {
        JobSystem::JobHandle dep;
        if (dependency)
            dep = JobSystem::JobHandle(fromHandle(dependency), true);
        auto handle = JobSystem::Scheduler::ScheduleParallelFor(func, context, length, batchSize, cleanup, dep);
        return toHandle(handle);
    }

    void* JobSystem_ScheduleFor(IndexJobFunc func, void* context, ContextCleanupFunc cleanup,
        int length, void* dependency)
    {
        JobSystem::JobHandle dep;
        if (dependency)
            dep = JobSystem::JobHandle(fromHandle(dependency), true);
        auto handle = JobSystem::Scheduler::ScheduleFor(func, context, length, cleanup, dep);
        return toHandle(handle);
    }

    void* JobSystem_ScheduleParallelForBatch(BatchJobFunc func, void* context, ContextCleanupFunc cleanup,
        int length, int batchSize, void* dependency)
    {
        JobSystem::JobHandle dep;
        if (dependency)
            dep = JobSystem::JobHandle(fromHandle(dependency), true);
        auto handle = JobSystem::Scheduler::ScheduleParallelForBatch(func, context, length, batchSize, cleanup, dep);
        return toHandle(handle);
    }

    void JobSystem_Complete(void* handle)
    {
        // 仅等待任务完成，不改变引用计数
        if (handle)
            JobSystem::JobHandle(fromHandle(handle), true).Complete();
    }

    void JobSystem_CompleteAndRelease(void* handle)
    {
        // 接管调用方持有的引用，等待完成后自动释放
        if (handle)
        {
            JobSystem::JobHandle jobHandle(fromHandle(handle), false); // 不增加引用
            jobHandle.Complete();
        } // 析构时 Release(state)
    }

    int JobSystem_IsCompleted(void* handle)
    {
        if (!handle) return 1;
        return fromHandle(handle)->completed.load(std::memory_order_acquire) ? 1 : 0;
    }

    void JobSystem_ReleaseHandle(void* handle)
    {
        JobSystem::JobHandle::Release(fromHandle(handle));
    }

    void* JobSystem_CombineDependencies(void** handles, int count)
    {
        std::vector<JobSystem::JobHandle> vec;
        vec.reserve(count);
        for (int i = 0; i < count; ++i)
        {
            if (handles[i])
                vec.emplace_back(fromHandle(handles[i]), true);
        }
        auto combined = JobSystem::JobHandle::CombineDependencies(vec);
        return toHandle(combined);
    }

    void* JobSystem_ScheduleChunkJob(
        ChunkJobFunc func,
        void* context,
        ContextCleanupFunc cleanup,
        const ChunkJobData* chunks,
        int chunkCount,
        void* dependency)
    {
        // 委托给 JobSystem.cpp 中的完整实现（包含 taskflow 头文件）
        JobSystem::JobHandle dep;
        if (dependency)
            dep = JobSystem::JobHandle(fromHandle(dependency), true);
        auto handle = JobSystem::Scheduler::ScheduleChunks(func, context, cleanup, chunks, chunkCount, dep);
        return toHandle(handle);
    }

} // extern "C"
