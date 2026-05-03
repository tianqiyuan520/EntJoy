#pragma once

#ifdef _WIN32
#ifdef JOB_SYSTEM_EXPORT
#define JOB_API __declspec(dllexport)
#else
#define JOB_API __declspec(dllimport)
#endif
#else
#define JOB_API __attribute__((visibility("default")))
#endif

// Forward declaration for ChunkJobData struct
struct ChunkJobData;

extern "C" {

    typedef void (*JobFunc)(void* context);
    typedef void (*IndexJobFunc)(void* context, int index);
    typedef void (*BatchJobFunc)(void* context, int startIndex, int count);
    typedef void (*ContextCleanupFunc)(void* context);
    // Chunk 任务回调：context 为 C# 传入的自定义数据，chunkData 为当前 Chunk 的描述块
    typedef void (*ChunkJobFunc)(void* context, const struct ChunkJobData* chunkData);

    JOB_API void JobSystem_Initialize(int numThreads);
    JOB_API void JobSystem_Shutdown();

    JOB_API void* JobSystem_Schedule(JobFunc func, void* context, ContextCleanupFunc cleanup, void* dependency);
    JOB_API void* JobSystem_ScheduleParallelFor(IndexJobFunc func, void* context, ContextCleanupFunc cleanup, int length, int batchSize, void* dependency);
    JOB_API void* JobSystem_ScheduleFor(IndexJobFunc func, void* context, ContextCleanupFunc cleanup, int length, void* dependency);
    JOB_API void* JobSystem_ScheduleParallelForBatch(BatchJobFunc func, void* context, ContextCleanupFunc cleanup, int length, int batchSize, void* dependency);

    JOB_API void JobSystem_Complete(void* handle);
    JOB_API void JobSystem_CompleteAndRelease(void* handle);
    JOB_API int JobSystem_IsCompleted(void* handle);
    JOB_API void JobSystem_ReleaseHandle(void* handle);
    JOB_API void* JobSystem_CombineDependencies(void** handles, int count);

    /** 
     * 调度多个 Chunk 任务，每个 Chunk 并行执行一次 func 回调。
     * @param func        C# 回调函数指针
     * @param context     C# 上下文数据指针（包含 job 拷贝和辅助数据）
     * @param cleanup     所有 Chunk 任务完成后的清理回调
     * @param chunks      ChunkJobData 数组（非托管内存）
     * @param chunkCount  数组长度
     * @param dependency  依赖的 JobHandle 指针（可为 nullptr）
     * @return 新的 JobHandle 指针，表示所有 Chunk 任务完成
     */
    JOB_API void* JobSystem_ScheduleChunkJob(
        ChunkJobFunc func,
        void* context,
        ContextCleanupFunc cleanup,
        const struct ChunkJobData* chunks,
        int chunkCount,
        void* dependency);

} // extern "C"
