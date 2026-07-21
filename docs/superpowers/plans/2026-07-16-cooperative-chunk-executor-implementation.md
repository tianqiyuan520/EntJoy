# Cooperative Chunk Executor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace fixed-range Chunk queue tokens with direct cooperative range claiming so workers and the `Complete()` caller share one exact-once cursor without frame-spanning busy-waiting.

**Architecture:** Keep the existing Chunk worker pool, condition variable, dependency handles, and native ABI entry points. Queue lightweight participation tickets that reference a `ChunkBatchState`; workers and handle assist callbacks claim one logical range at a time from `nextRange`, while existing completion and reader counters protect exact-once cleanup and pooled-state lifetime.

**Tech Stack:** C++20, C#/.NET 8, CMake/MSVC, Taskflow, EntJoy ECS, NativeTranspiler source generators, ISPC, PowerShell.

## Global Constraints

- Preserve public C# scheduling APIs and existing native export signatures.
- Do not route Chunk work through Taskflow in this plan.
- Do not change the default ECS Chunk capacity or default worker count in this plan.
- Do not use `KeepWorkersWarm` in the Sleep benchmark or spin through the 16 ms frame interval.
- Every logical range executes at most once; cleanup and handle completion occur exactly once.
- Do not manually edit files under `src/EntJoySample/NativeTranspiler_Generated`.
- Performance runs use Release x64 without a debugger, five independent processes, identical warmup/measurement settings, and report process-median average plus frame p50/p95.
- Preserve unrelated user changes; stage only files listed by each task.

## File Map

- `src/NativeDll/JobSystem.cpp`: owns `ChunkBatchState`, cooperative claim logic, worker tickets, assist, publication, shutdown, and native statistics.
- `src/NativeDll/JobSystem.h`: defines the native statistics snapshot shared with `Exports.cpp`.
- `src/NativeDll/Exports.h`: defines the append-only C ABI statistics structure.
- `src/NativeDll/Exports.cpp`: copies native statistics into the exported ABI structure.
- `src/NativeDll.Tests/JobSystemTests.cpp`: deterministic exact-once, concurrent-complete, dependency, exhausted-ticket, and shutdown tests.
- `src/EntJoy/JobSystem/NativeJobScheduler.cs`: mirrors the statistics ABI and selects range scheduling for transpiled Chunk jobs.
- `src/NativeTranspiler/Analyzer/BindingsGenerator.cs`: binds native `IJobChunk` jobs to their generated range adapter.
- `src/NativeTranspiler/Analyzer/CppJobGenerator.cs`: hoists native job-context decoding outside the generated per-Chunk range loop.
- `src/EntJoySample/02_IJobChunkECS/IJobChunkMoveCompareTest/IJobChunkMoveCompareSample.cs`: removes benchmark keep-warm calls, records frame distributions, and prints scheduler attribution.
- `docs/performance/cooperative-chunk-executor-results.md`: records reproducible before/after commands, correctness, latency, and regression decisions.

---

### Task 1: Add Scheduler Attribution Counters

**Files:**
- Modify: `src/NativeDll/JobSystem.h`
- Modify: `src/NativeDll/JobSystem.cpp`
- Modify: `src/NativeDll/Exports.h`
- Modify: `src/NativeDll/Exports.cpp`
- Modify: `src/EntJoy/JobSystem/NativeJobScheduler.cs`
- Test: `src/NativeDll.Tests/JobSystemTests.cpp`

**Interfaces:**
- Consumes: `JobSystem::GetStatsSnapshot(JobSystemStatsSnapshot*)`, `JobSystem_GetStats`, and `NativeJobScheduler.GetStats()`.
- Produces: append-only fields `directAssistClaims`, `exhaustedTickets`, `scheduleToPublishEwmaNs`, `publishToFirstMainClaimEwmaNs`, `publishToFirstWorkerClaimEwmaNs`, `publishToCompletionEwmaNs`, and `queueLockWaitEwmaNs` in matching C++, C ABI, and C# order.

- [ ] **Step 1: Write the failing native statistics reset test**

Add this function before `main()` in `src/NativeDll.Tests/JobSystemTests.cpp`:

```cpp
void TestCooperativeStatsReset()
{
    JobSystem::ResetStatsSnapshot();
    JobSystem::JobSystemStatsSnapshot stats{};
    JobSystem::GetStatsSnapshot(&stats);
    Require(stats.directAssistClaims == 0, "direct assist stats did not reset");
    Require(stats.exhaustedTickets == 0, "exhausted ticket stats did not reset");
    Require(stats.scheduleToPublishEwmaNs == 0, "schedule-to-publish stats did not reset");
    Require(stats.publishToFirstMainClaimEwmaNs == 0, "main-claim stats did not reset");
    Require(stats.publishToFirstWorkerClaimEwmaNs == 0, "worker-claim stats did not reset");
    Require(stats.publishToCompletionEwmaNs == 0, "completion stats did not reset");
    Require(stats.queueLockWaitEwmaNs == 0, "queue-lock stats did not reset");
}
```

Call it as the first test in `main()` and print `PASS CooperativeStatsReset`.

- [ ] **Step 2: Build the native test to verify it fails**

Run:

```powershell
cmake -S src/NativeDll.Tests -B src/NativeDll.Tests/build
cmake --build src/NativeDll.Tests/build --config Release --parallel
```

Expected: compilation fails because the seven statistics fields do not exist.

- [ ] **Step 3: Append the statistics fields consistently across the ABI**

Append the following members after `frameQueueDepthPeak` in `JobSystemStatsSnapshot` and `JobSystemStatsNative`, preserving this exact order and using `uint64_t` and `unsigned long long` respectively:

```cpp
uint64_t directAssistClaims;
uint64_t exhaustedTickets;
uint64_t scheduleToPublishEwmaNs;
uint64_t publishToFirstMainClaimEwmaNs;
uint64_t publishToFirstWorkerClaimEwmaNs;
uint64_t publishToCompletionEwmaNs;
uint64_t queueLockWaitEwmaNs;
```

Append the matching C# fields after `FrameQueueDepthPeak` in the same layout order:

```csharp
public ulong DirectAssistClaims;
public ulong ExhaustedTickets;
public ulong ScheduleToPublishEwmaNs;
public ulong PublishToFirstMainClaimEwmaNs;
public ulong PublishToFirstWorkerClaimEwmaNs;
public ulong PublishToCompletionEwmaNs;
public ulong QueueLockWaitEwmaNs;
```

Declare matching `std::atomic<uint64_t>` counters in `JobSystem.cpp`, initialize them to zero, load them in `GetStatsSnapshot`, reset them in `ResetStatsSnapshot`, and copy them in `JobSystem_GetStats`:

```cpp
stats->directAssistClaims = snapshot.directAssistClaims;
stats->exhaustedTickets = snapshot.exhaustedTickets;
stats->scheduleToPublishEwmaNs = snapshot.scheduleToPublishEwmaNs;
stats->publishToFirstMainClaimEwmaNs = snapshot.publishToFirstMainClaimEwmaNs;
stats->publishToFirstWorkerClaimEwmaNs = snapshot.publishToFirstWorkerClaimEwmaNs;
stats->publishToCompletionEwmaNs = snapshot.publishToCompletionEwmaNs;
stats->queueLockWaitEwmaNs = snapshot.queueLockWaitEwmaNs;
```

Use one helper for timing EWMAs so timing updates remain consistent:

```cpp
void UpdateUnsignedEwma(std::atomic<uint64_t>& target, uint64_t sample) noexcept
{
    if (sample == 0) return;
    uint64_t current = target.load(std::memory_order_relaxed);
    while (true)
    {
        const uint64_t next = current == 0
            ? sample
            : (sample >= current
                ? current + (sample - current) / 8
                : current - (current - sample) / 8);
        if (target.compare_exchange_weak(current, next, std::memory_order_relaxed)) return;
    }
}
```

- [ ] **Step 4: Run native and managed ABI verification**

Run:

```powershell
cmake --build src/NativeDll.Tests/build --config Release --parallel
& src/NativeDll.Tests/build/Release/JobSystemTests.exe
dotnet build src/EntJoySample/EntJoySample.csproj -c Release --no-restore
```

Expected: native tests print `PASS CooperativeStatsReset`; managed build succeeds with no ABI-related compile error.

- [ ] **Step 5: Commit the attribution counters**

```powershell
git add src/NativeDll/JobSystem.h src/NativeDll/JobSystem.cpp src/NativeDll/Exports.h src/NativeDll/Exports.cpp src/EntJoy/JobSystem/NativeJobScheduler.cs src/NativeDll.Tests/JobSystemTests.cpp
git commit -m "test(jobs): add cooperative scheduler attribution"
```

### Task 2: Replace Fixed Range Ownership with a Shared Claim Cursor

**Files:**
- Modify: `src/NativeDll/JobSystem.cpp`
- Test: `src/NativeDll.Tests/JobSystemTests.cpp`

**Interfaces:**
- Consumes: existing `ChunkBatchState::rangeSize`, `rangeCount`, `RunChunkRangeSpan`, and exact-once finalization.
- Produces: `bool TryRunOneChunkRange(ChunkBatchState*, bool worker) noexcept`; queue tickets reference only `ChunkBatchState*`.

- [ ] **Step 1: Write failing concurrent-complete and exhausted-ticket tests**

Add the following context and callback:

```cpp
struct CooperativeChunkContext
{
    std::vector<std::atomic<int>>* hits;
    std::atomic<int>* cleanupCount;
    std::thread::id schedulingThread;
    std::atomic<int>* nonWorkerExecutions;
};

void ExecuteCooperativeChunkRange(void* raw, const ChunkJobData*, int start, int count)
{
    auto& context = *static_cast<CooperativeChunkContext*>(raw);
    if (std::this_thread::get_id() != context.schedulingThread)
        context.nonWorkerExecutions->fetch_add(1, std::memory_order_relaxed);
    for (int index = start; index < start + count; ++index)
    {
        (*context.hits)[static_cast<size_t>(index)].fetch_add(1, std::memory_order_relaxed);
        if ((index & 31) == 0) std::this_thread::yield();
    }
}

void CleanupCooperativeChunkRange(void* raw)
{
    static_cast<CooperativeChunkContext*>(raw)->cleanupCount->fetch_add(1, std::memory_order_relaxed);
}
```

Add these two tests and invoke them from `main()` with distinct PASS labels:

```cpp
void TestConcurrentChunkComplete()
{
    constexpr int chunkCount = 4'096;
    std::vector<ChunkJobData> chunks(chunkCount);
    std::vector<std::atomic<int>> hits(chunkCount);
    std::atomic<int> cleanupCount{ 0 };
    std::atomic<int> nonWorkerExecutions{ 0 };
    CooperativeChunkContext context{
        &hits, &cleanupCount, std::this_thread::get_id(), &nonWorkerExecutions
    };

    JobSystem::ResetStatsSnapshot();
    auto handle = JobSystem::Scheduler::ScheduleChunkRanges(
        &ExecuteCooperativeChunkRange, &context, &CleanupCooperativeChunkRange,
        chunks.data(), chunkCount, {},
        JobSystem::ChunkScheduleMode::PublishAssist, 8, 1);
    auto first = handle;
    auto second = handle;
    auto third = handle;
    auto fourth = handle;
    std::jthread a([first]() mutable { first.Complete(); });
    std::jthread b([second]() mutable { second.Complete(); });
    std::jthread c([third]() mutable { third.Complete(); });
    std::jthread d([fourth]() mutable { fourth.Complete(); });
    a.join();
    b.join();
    c.join();
    d.join();
    handle.Complete();

    for (const auto& hit : hits)
        Require(hit.load(std::memory_order_relaxed) == 1,
            "concurrent Complete missed or duplicated a Chunk range");
    Require(cleanupCount.load(std::memory_order_relaxed) == 1,
        "concurrent Complete duplicated Chunk cleanup");
}

void TestExhaustedChunkTicketsDrain()
{
    constexpr int chunkCount = 2;
    JobSystem::ResetStatsSnapshot();
    for (int iteration = 0; iteration < 256; ++iteration)
    {
        std::vector<ChunkJobData> chunks(chunkCount);
        std::vector<std::atomic<int>> hits(chunkCount);
        std::atomic<int> cleanupCount{ 0 };
        std::atomic<int> nonWorkerExecutions{ 0 };
        CooperativeChunkContext context{
            &hits, &cleanupCount, std::this_thread::get_id(), &nonWorkerExecutions
        };
        auto handle = JobSystem::Scheduler::ScheduleChunkRanges(
            &ExecuteCooperativeChunkRange, &context, &CleanupCooperativeChunkRange,
            chunks.data(), chunkCount, {},
            JobSystem::ChunkScheduleMode::PublishAssist, 8, 1);
        handle.Complete();
        for (const auto& hit : hits)
            Require(hit.load(std::memory_order_relaxed) == 1,
                "exhausted ticket test missed or duplicated a range");
        Require(cleanupCount.load(std::memory_order_relaxed) == 1,
            "exhausted ticket test duplicated cleanup");
    }

    JobSystem::JobSystemStatsSnapshot stats{};
    for (int retry = 0; retry < 100; ++retry)
    {
        JobSystem::GetStatsSnapshot(&stats);
        if (stats.exhaustedTickets > 0) break;
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    Require(stats.exhaustedTickets > 0,
        "stale cooperative tickets did not drain through the exhausted path");
}
```

- [ ] **Step 2: Build to verify the behavioral test fails**

Run:

```powershell
cmake --build src/NativeDll.Tests/build --config Release --parallel
& src/NativeDll.Tests/build/Release/JobSystemTests.exe
```

Expected: failure at `exhaustedTickets > 0` because fixed-range tokens do not drain through a shared-cursor exhausted path.

- [ ] **Step 3: Add and reset the shared cursor**

Add these fields to `ChunkBatchState`:

```cpp
std::atomic<int> nextRange{ 0 };
std::atomic<int> activeTickets{ 0 };
int64_t scheduleNs{ 0 };
```

Reset both in `AcquireChunkBatchState` and initialize `nextRange` before publication. Replace `ChunkQueueToken` with:

```cpp
struct ChunkQueueTicket
{
    ChunkBatchState* batch{ nullptr };
};
```

Change `g_chunkRunnableBatches` to `std::deque<ChunkQueueTicket>`. Queue `tokenCount` copies of `{ batch }`; retain the existing `queueTokens` counter until every queued ticket has been consumed.

- [ ] **Step 4: Implement the one-range cooperative claimant**

Add this function beside `RunChunkRangeSpan`:

```cpp
bool TryRunOneChunkRange(ChunkBatchState* batch, bool worker) noexcept
{
    if (!batch || batch->cleanupDone.load(std::memory_order_acquire)) return false;
    const int rangeIndex = batch->nextRange.fetch_add(1, std::memory_order_relaxed);
    if (rangeIndex >= batch->rangeCount) return false;

    if (worker)
        g_workerClaimedTokens.fetch_add(1, std::memory_order_relaxed);
    else
        g_mainClaimedTokens.fetch_add(1, std::memory_order_relaxed);

    return RunChunkRangeSpan(batch, rangeIndex, rangeIndex + 1);
}
```

Do not use `unclaimedTokens` as an execution allocator. Remove `ClaimChunkToken`; remove `firstRange` and `lastRange` reads from worker execution.

At batch creation, set `scheduleNs` from `steady_clock` in nanoseconds. At publication, update `scheduleToPublishEwmaNs` from `publishNs - scheduleNs`. In `TryRunOneChunkRange`, use the existing `firstWorkerStartNs` and `firstAssistShardNs` compare-exchanges to update the matching first-claim EWMA once. In `FinalizeChunkBatch`, update `publishToCompletionEwmaNs` from the final timestamp minus `publishNs`.

Measure publication queue-lock wait around lock acquisition, excluding ticket insertion:

```cpp
const auto lockStart = std::chrono::steady_clock::now();
std::unique_lock<std::mutex> lock(g_chunkWorkerMutex);
const auto lockAcquired = std::chrono::steady_clock::now();
UpdateUnsignedEwma(g_queueLockWaitEwmaNs,
    static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(
        lockAcquired - lockStart).count()));
```

- [ ] **Step 5: Make worker tickets drain the shared cursor**

Replace `RunWorkerChunkToken` with ticket behavior:

```cpp
void RunWorkerChunkTicket(const ChunkQueueTicket& ticket) noexcept
{
    auto* batch = ticket.batch;
    if (!batch) return;
    batch->activeTickets.fetch_add(1, std::memory_order_acq_rel);

    bool ranAny = false;
    while (TryRunOneChunkRange(batch, true)) ranAny = true;
    if (!ranAny) g_exhaustedTickets.fetch_add(1, std::memory_order_relaxed);

    batch->activeTickets.fetch_sub(1, std::memory_order_acq_rel);
    FinishChunkQueueToken(batch);
    TryReleaseChunkBatchState(batch);
}
```

Update worker-loop local types and calls from token to ticket. Add `activeTickets != 0` to `TryReleaseChunkBatchState`'s guard.

Keep assist compiling during this transitional task by changing `RunOneQueuedChunkToken` to remove one matching `ChunkQueueTicket`, call `TryRunOneChunkRange(requestedBatch, false)`, finish that removed ticket, and release its assist-reader retain. Do not increment `directAssistClaims` in this transitional queue-search path; Task 3 removes it.

- [ ] **Step 6: Run exact-once and concurrency verification**

Run:

```powershell
cmake --build src/NativeDll.Tests/build --config Release --parallel
1..20 | ForEach-Object { & src/NativeDll.Tests/build/Release/JobSystemTests.exe; if ($LASTEXITCODE -ne 0) { throw "native test run $_ failed" } }
```

Expected: all 20 processes exit zero and print the concurrent-complete and exhausted-ticket PASS labels.

- [ ] **Step 7: Commit shared range claiming**

```powershell
git add src/NativeDll/JobSystem.cpp src/NativeDll.Tests/JobSystemTests.cpp
git commit -m "perf(jobs): share chunk range claims across workers"
```

### Task 3: Make Complete Assist the Target Batch Directly

**Files:**
- Modify: `src/NativeDll/JobSystem.cpp`
- Test: `src/NativeDll.Tests/JobSystemTests.cpp`

**Interfaces:**
- Consumes: `TryRunOneChunkRange(ChunkBatchState*, bool)` and `HandleState::assist`.
- Produces: `RunOneDirectChunkAssist(void*) noexcept`; removes batch-specific deque scanning from caller assist.

- [ ] **Step 1: Strengthen the assist test to reject queue consumption**

In the concurrent-complete test, after completion wait until all tickets drain, take a statistics snapshot, and add:

```cpp
Require(stats.directAssistClaims > 1,
    "Complete callers did not claim target ranges directly");
Require(stats.mainClaimedTokens == stats.directAssistClaims,
    "main claims used a path other than direct batch assist");
```

Keep the 4,096 ranges sufficiently long with the existing periodic yield so more than one direct claim is deterministic.

- [ ] **Step 2: Run the test against the transitional implementation**

Run the native test executable.

Expected: the new equality fails while `RunOneQueuedChunkToken` still removes tickets from the global deque and increments main claims through the old path.

- [ ] **Step 3: Replace deque-search assist with direct claiming**

Replace `RunOneQueuedChunkToken` and `RunOneQueuedColdChunkToken` with:

```cpp
bool RunOneDirectChunkAssist(void* rawBatch) noexcept
{
    auto* batch = static_cast<ChunkBatchState*>(rawBatch);
    if (!batch) return false;
    const bool ran = TryRunOneChunkRange(batch, false);
    if (ran) g_directAssistClaims.fetch_add(1, std::memory_order_relaxed);
    return ran;
}
```

In `PublishChunkBatch`, publish `batch` as assist context and `RunOneDirectChunkAssist` as callback for both hot and cold batches. Retain `OnChunkAssistReadersDrained` as the reclamation callback. Remove all `std::find_if`/erase logic used only by assist.

- [ ] **Step 4: Bound Complete assist without delaying its first claim**

Keep `TryAcquireAssist` and the current time budget, but ensure the loop calls the direct callback before checking its deadline:

```cpp
do
{
    g_assistAttempts.fetch_add(1, std::memory_order_relaxed);
    if (!assist.callback(assist.context)) break;
    g_assistExecuted.fetch_add(1, std::memory_order_relaxed);
    g_mainExecutedRanges.fetch_add(1, std::memory_order_relaxed);
} while (!_state->completed.load(std::memory_order_acquire) &&
         std::chrono::steady_clock::now() < deadline);
```

Do not call `PrewakeWorkers` or `KeepWorkersWarm` from `Complete()`.

- [ ] **Step 5: Run repeated assist and lifetime tests**

Run:

```powershell
cmake --build src/NativeDll.Tests/build --config Release --parallel
1..100 | ForEach-Object { & src/NativeDll.Tests/build/Release/JobSystemTests.exe; if ($LASTEXITCODE -ne 0) { throw "native stress run $_ failed" } }
```

Expected: 100/100 processes exit zero; concurrent callers assist directly; no hang or access violation occurs.

- [ ] **Step 6: Commit direct Complete assist**

```powershell
git add src/NativeDll/JobSystem.cpp src/NativeDll.Tests/JobSystemTests.cpp
git commit -m "perf(jobs): let Complete claim chunk ranges directly"
```

### Task 4: Harden Dependencies, Cancellation, and Shutdown

**Files:**
- Modify: `src/NativeDll/JobSystem.cpp`
- Test: `src/NativeDll.Tests/JobSystemTests.cpp`

**Interfaces:**
- Consumes: cooperative tickets, `scheduleState`, `cleanupDone`, `queueTokens`, `activeTickets`, assist-reader counts, and `ReleaseChunkBatchState`.
- Produces: deterministic lifecycle under dependency publication, copied handles, concurrent completion, and shutdown.

- [ ] **Step 1: Add dependency and shutdown race tests**

Add these tests before `main()` and invoke them with distinct PASS labels:

```cpp
void TestDependentChunkRangeCooperation()
{
    constexpr int chunkCount = 1'024;
    std::vector<ChunkJobData> chunks(chunkCount);
    std::vector<std::atomic<int>> hits(chunkCount);
    std::atomic<int> cleanupCount{ 0 };
    std::atomic<int> nonWorkerExecutions{ 0 };
    std::atomic<bool> releaseDependency{ false };

    auto dependency = JobSystem::Scheduler::ScheduleParallelForBatch(
        [](void* raw, int, int)
        {
            auto* release = static_cast<std::atomic<bool>*>(raw);
            release->wait(false, std::memory_order_acquire);
        }, &releaseDependency, 100'000, 100'000);

    CooperativeChunkContext context{
        &hits, &cleanupCount, std::this_thread::get_id(), &nonWorkerExecutions
    };
    auto original = JobSystem::Scheduler::ScheduleChunkRanges(
        &ExecuteCooperativeChunkRange, &context, &CleanupCooperativeChunkRange,
        chunks.data(), chunkCount, dependency,
        JobSystem::ChunkScheduleMode::PublishAssist, 8, 1);
    auto first = original;
    auto second = original;
    std::jthread firstCaller([first]() mutable { first.Complete(); });
    std::jthread secondCaller([second]() mutable { second.Complete(); });

    std::this_thread::sleep_for(std::chrono::milliseconds(2));
    for (const auto& hit : hits)
        Require(hit.load(std::memory_order_relaxed) == 0,
            "dependent Chunk range ran before its prerequisite");

    releaseDependency.store(true, std::memory_order_release);
    releaseDependency.notify_all();
    firstCaller.join();
    secondCaller.join();
    original.Complete();

    for (const auto& hit : hits)
        Require(hit.load(std::memory_order_relaxed) == 1,
            "dependent Chunk range was missed or duplicated");
    Require(cleanupCount.load(std::memory_order_relaxed) == 1,
        "dependent Chunk cleanup did not run exactly once");
}

void TestChunkShutdownRace()
{
    for (int iteration = 0; iteration < 50; ++iteration)
    {
        constexpr int chunkCount = 1'024;
        std::vector<ChunkJobData> chunks(chunkCount);
        std::vector<std::atomic<int>> hits(chunkCount);
        std::atomic<int> cleanupCount{ 0 };
        std::atomic<int> nonWorkerExecutions{ 0 };
        CooperativeChunkContext context{
            &hits, &cleanupCount, std::this_thread::get_id(), &nonWorkerExecutions
        };

        auto handle = JobSystem::Scheduler::ScheduleChunkRanges(
            &ExecuteCooperativeChunkRange, &context, &CleanupCooperativeChunkRange,
            chunks.data(), chunkCount, {},
            JobSystem::ChunkScheduleMode::PublishAssist, 8, 1);
        auto copied = handle;
        std::jthread caller([copied]() mutable { copied.Complete(); });
        JobSystem::Scheduler::Shutdown();
        caller.join();

        Require(handle.IsCompleted(), "shutdown left cooperative Chunk work incomplete");
        Require(cleanupCount.load(std::memory_order_relaxed) == 1,
            "shutdown raced cooperative Chunk cleanup");
        JobSystem::Scheduler::Initialize();
    }
}
```

- [ ] **Step 2: Run the new race tests repeatedly**

Run the native test executable 20 times.

Expected before hardening: any hang, duplicate cleanup, missed completion, or access violation is a failing result; record the failing label and iteration.

- [ ] **Step 3: Centralize reclaim eligibility**

Make `TryReleaseChunkBatchState` the only path that returns a batch to the pool. Its guard must require all of the following:

```cpp
batch->cleanupDone.load(std::memory_order_acquire) &&
batch->queueTokens.load(std::memory_order_acquire) == 0 &&
batch->activeTickets.load(std::memory_order_acquire) == 0 &&
batch->assistReaders.load(std::memory_order_acquire) == 0 &&
(!batch->handleState ||
 batch->handleState->assist.readers.load(std::memory_order_acquire) == 0)
```

Every continuation, ticket, and batch-held handle state must have one documented acquire and one release. `FinalizeChunkBatch` and `CancelChunkBatch` set `cleanupDone` and complete the handle, then call `TryReleaseChunkBatchState`; neither directly deletes or pools the batch.

- [ ] **Step 4: Drain stale tickets during shutdown**

Before joining Chunk workers, ensure shutdown wakes all parked workers and allows every queued ticket to be consumed. After workers join, cancel any pending unpublished batch through `CancelChunkBatch`. Do not clear `g_chunkRunnableBatches` without decrementing each referenced batch's `queueTokens` and invoking `TryReleaseChunkBatchState`.

- [ ] **Step 5: Verify lifecycle stability**

Run:

```powershell
cmake --build src/NativeDll.Tests/build --config Release --parallel
1..100 | ForEach-Object { & src/NativeDll.Tests/build/Release/JobSystemTests.exe; if ($LASTEXITCODE -ne 0) { throw "lifecycle stress run $_ failed" } }
```

Expected: 100/100 processes pass all dependency, copied-handle, concurrent-complete, and shutdown tests.

- [ ] **Step 6: Commit lifecycle hardening**

```powershell
git add src/NativeDll/JobSystem.cpp src/NativeDll.Tests/JobSystemTests.cpp
git commit -m "fix(jobs): harden cooperative chunk lifetime"
```

### Task 5: Route Transpiled IJobChunk Through the Range Adapter

**Files:**
- Modify: `src/EntJoy/JobSystem/NativeJobScheduler.cs`
- Modify: `src/NativeTranspiler/Analyzer/BindingsGenerator.cs`
- Modify: `src/NativeTranspiler/Analyzer/CppJobGenerator.cs`
- Test by generation: `src/EntJoySample/obj/Release/NativeTranspiler/NativeTranspiler.Analyzer.NativeTranspilerGenerator/NativeTranspiler.Bindings.g.cs`
- Test at runtime: `src/EntJoySample/02_IJobChunkECS/IJobChunkMoveCompareTest/IJobChunkMoveCompareSample.cs`

**Interfaces:**
- Consumes: `JobSystem_ScheduleChunkRangeJobEx` and the generated `s_{jobStruct.Name}_ChunkRangeFuncPtr` field.
- Produces: `NativeJobScheduler.ScheduleChunkRangeRaw<T>(...)`; native `IJobChunk` bindings use the range pointer while C# and filtered fallbacks retain existing behavior.

- [ ] **Step 1: Add a build-time binding assertion**

Add an MSBuild-independent PowerShell verification command to the task notes and run it after generation:

```powershell
$generated = 'src/EntJoySample/obj/Release/NativeTranspiler/NativeTranspiler.Analyzer.NativeTranspilerGenerator/NativeTranspiler.Bindings.g.cs'
if (-not (Select-String -Path $generated -Pattern 'ScheduleChunkRangeRaw' -Quiet)) { throw 'native IJobChunk binding did not use range scheduling' }
if (-not (Select-String -Path $generated -Pattern 'ChunkRangeFuncPtr' -Quiet)) { throw 'native IJobChunk range pointer was not emitted' }
```

Run it before implementation.

Expected: it throws because current generated scheduling still calls `ScheduleChunkRaw`.

- [ ] **Step 2: Add the managed range scheduling entry point**

Add this public method beside `ScheduleChunkRaw<T>` in `NativeJobScheduler.cs`:

```csharp
public static NativeJobHandle ScheduleChunkRangeRaw<T>(
    ref T job,
    EntityManager entityManager,
    QueryBuilder query,
    IntPtr rangeFuncPtr,
    int[] requiredComponentTypeIds,
    NativeJobHandle? dependsOn = null)
    where T : struct, IJobChunk
    => ScheduleNativeChunkRangeRawCore(
        ref job, entityManager, query, rangeFuncPtr,
        requiredComponentTypeIds, dependsOn, workerCap: 0, rangeSize: 0);
```

This reuses the existing cache, enabled-filter fallback, cleanup, and dependency lifetime logic in `ScheduleNativeChunkRangeRawCore`.

- [ ] **Step 3: Generate bindings that select the range entry point**

In `BindingsGenerator.cs`, select these exact values for native `IJobChunk`:

```csharp
string scheduleMethod = CppJobGenerator.IsEntityJob(jobStruct)
    ? "ScheduleEntityBatchRawWithWorkerCapAndRangeSize"
    : "ScheduleChunkRangeRaw";
string funcPtrName = CppJobGenerator.IsEntityJob(jobStruct)
    ? $"s_{jobStruct.Name}_EntityBatchFuncPtr"
    : $"s_{jobStruct.Name}_ChunkRangeFuncPtr";
```

Keep the existing entity-job extra arguments unchanged.

- [ ] **Step 4: Hoist context decoding outside generated Chunk loops**

In `CppJobGenerator.cs`, make the generated range adapter decode `__EntJoyChunkContextHeader`, job field pointers/references, and `requiredComponentTypeIds` once before `for (__chunkIndex ...)`. Inline the translated `IJobChunk.Execute` body inside the range loop rather than calling the single-Chunk adapter. Preserve the generated single-Chunk adapter for ABI compatibility and fallback paths.

The generated shape must be:

```cpp
HEAD void CALLINGCONVENTION Job_ChunkRangeAdapter(
    void* context, const ChunkJobData* chunks, int startIndex, int count)
{
    auto* header = (__EntJoyChunkContextHeader*)context;
    char* jobContext = /* header plus metadata */;
    // Decode immutable job fields once here.
    const int endIndex = startIndex + count;
    for (int chunkIndex = startIndex; chunkIndex < endIndex; ++chunkIndex)
    {
        auto* chunkData = &chunks[chunkIndex];
        // Translated Execute body uses chunkData.
    }
}
```

- [ ] **Step 5: Build, inspect generated output, and run parity**

Run:

```powershell
dotnet build src/EntJoySample/EntJoySample.csproj -c Release --no-restore
$generated = 'src/EntJoySample/obj/Release/NativeTranspiler/NativeTranspiler.Analyzer.NativeTranspilerGenerator/NativeTranspiler.Bindings.g.cs'
if (-not (Select-String -Path $generated -Pattern 'ScheduleChunkRangeRaw' -Quiet)) { throw 'native IJobChunk binding did not use range scheduling' }
& .\bin\EntJoySample.exe
```

Expected: build succeeds; binding assertion passes; all Light, Heavy, Sleep, and Expected correctness lines report `OK`.

- [ ] **Step 6: Commit range-adapter integration**

```powershell
git add src/EntJoy/JobSystem/NativeJobScheduler.cs src/NativeTranspiler/Analyzer/BindingsGenerator.cs src/NativeTranspiler/Analyzer/CppJobGenerator.cs
git commit -m "perf(transpiler): schedule native chunk range adapters"
```

### Task 6: Make the Sleep Benchmark Fair and Distribution-Aware

**Files:**
- Modify: `src/EntJoySample/02_IJobChunkECS/IJobChunkMoveCompareTest/IJobChunkMoveCompareSample.cs`

**Interfaces:**
- Consumes: `NativeJobScheduler.ResetStats()` and `GetStats()`.
- Produces: average, p50, p95, and native scheduler attribution per Sleep backend without prewake or keep-warm mutation.

- [ ] **Step 1: Add a source-level guard against benchmark keep-warm calls**

Run before editing:

```powershell
$sample = 'src/EntJoySample/02_IJobChunkECS/IJobChunkMoveCompareTest/IJobChunkMoveCompareSample.cs'
if (Select-String -Path $sample -Pattern 'KeepWorkersWarm' -Quiet) { throw 'Sleep benchmark still mutates worker warmth' }
```

Expected on any worktree containing the earlier experiment: the command throws. Remove every benchmark call; do not remove the compatibility API in this task.

- [ ] **Step 2: Record every measured frame and compute percentiles**

Replace the Sleep accumulator with a `double[] samples`, sort a clone after measurement, and use:

```csharp
private static double Percentile(double[] sorted, double percentile)
{
    if (sorted.Length == 0) return 0;
    double position = (sorted.Length - 1) * percentile;
    int lower = (int)Math.Floor(position);
    int upper = (int)Math.Ceiling(position);
    if (lower == upper) return sorted[lower];
    double weight = position - lower;
    return sorted[lower] * (1.0 - weight) + sorted[upper] * weight;
}
```

Print `avg`, `p50`, and `p95` with three decimal places. Keep `Thread.Sleep(FrameSleepMilliseconds)` outside the timed region.

- [ ] **Step 3: Reset and print scheduler attribution for each Sleep case**

Call `NativeJobScheduler.ResetStats()` after warmup and before measurement. After measurement, print these `GetStats()` fields on one line:

```text
directAssist, exhaustedTickets, mainClaims, workerClaims,
firstMainUs, firstWorkerUs, completionUs, queueLockUs, waitFallbacks
```

Convert nanoseconds to microseconds only when formatting. Do not call stats APIs inside a timed frame.

- [ ] **Step 4: Verify the benchmark source and runtime correctness**

Run:

```powershell
$sample = 'src/EntJoySample/02_IJobChunkECS/IJobChunkMoveCompareTest/IJobChunkMoveCompareSample.cs'
if (Select-String -Path $sample -Pattern 'KeepWorkersWarm' -Quiet) { throw 'Sleep benchmark still mutates worker warmth' }
dotnet build src/EntJoySample/EntJoySample.csproj -c Release --no-restore
& .\bin\EntJoySample.exe
```

Expected: no keep-warm call; build succeeds; runtime prints avg/p50/p95 and scheduler attribution; every correctness line reports `OK`.

- [ ] **Step 5: Commit the fair benchmark**

```powershell
git add src/EntJoySample/02_IJobChunkECS/IJobChunkMoveCompareTest/IJobChunkMoveCompareSample.cs
git commit -m "test(bench): report cooperative chunk latency distribution"
```

### Task 7: Run Full Correctness, Stress, and Performance Acceptance

**Files:**
- Create: `docs/performance/cooperative-chunk-executor-results.md`
- Verify only: all implementation files from Tasks 1-6

**Interfaces:**
- Consumes: native tests, generated binding assertions, EntJoy sample output, and design acceptance criteria.
- Produces: a reproducible acceptance record and an explicit pass/fail decision without changing worker count or Chunk capacity.

- [ ] **Step 1: Run clean Release verification**

Run:

```powershell
dotnet build src/EntJoySample/EntJoySample.csproj -c Release --no-restore
cmake --build src/NativeDll.Tests/build --config Release --parallel
& src/NativeDll.Tests/build/Release/JobSystemTests.exe
```

Expected: both builds succeed; native executable exits zero; all named tests print `PASS`.

- [ ] **Step 2: Run 100-process native stress**

Run:

```powershell
1..100 | ForEach-Object {
    & src/NativeDll.Tests/build/Release/JobSystemTests.exe *> $null
    if ($LASTEXITCODE -ne 0) { throw "native stress run $_ failed" }
}
```

Expected: all 100 processes exit zero.

- [ ] **Step 3: Capture five independent benchmark processes**

Run:

```powershell
New-Item -ItemType Directory -Force bin/bench-cooperative | Out-Null
1..5 | ForEach-Object {
    & .\bin\EntJoySample.exe 2>&1 |
        Tee-Object -FilePath ("bin/bench-cooperative/run-{0}.txt" -f $_)
    if ($LASTEXITCODE -ne 0) { throw "benchmark run $_ failed" }
}
```

Expected: five logs; every log contains `Sleep Verify : OK` and `Sleep Expected: OK`.

- [ ] **Step 4: Evaluate the fixed acceptance gates**

From the five logs, record each process's Sleep C# `IJobChunk` average and p95, then use the median process values. Mark the scheduler accepted only when:

```text
Sleep C# IJobChunk median average <= 0.950 ms
Sleep C# IJobChunk median p95     <= 1.100 ms
Continuous C# IJobChunk regression <= 5% from 0.210 ms
Every Heavy backend regression      <= 3% from its recorded baseline
All correctness and stress tests pass
No KeepWorkersWarm call exists in the benchmark
```

If a latency gate misses, record the actual attribution counters and stop this plan. Do not compensate with persistent spin, a worker-count change, or a Chunk-capacity change; those require the separate evidence-gated tuning plan described by the design.

- [ ] **Step 5: Write the results document**

Create `docs/performance/cooperative-chunk-executor-results.md` containing:

```markdown
# Cooperative Chunk Executor Results

## Environment
- Commit tested: output captured from `git rev-parse HEAD`
- CPU and logical/physical core count
- Power mode and debugger state
- Build and benchmark commands

## Correctness
- Native named tests and 100-process stress result
- C#/C++/ISPC Chunk and Entity parity result

## Performance
| Run | Continuous C# Chunk avg | Sleep C# Chunk avg | Sleep C# Chunk p95 | Heavy C# Chunk avg |
|---|---:|---:|---:|---:|

## Scheduler Attribution
| Run | Direct assist | Exhausted tickets | First main | First worker | Completion | Queue lock | Wait fallbacks |
|---|---:|---:|---:|---:|---:|---:|---:|

## Acceptance
- Sleep average gate: PASS or FAIL with value
- Sleep p95 gate: PASS or FAIL with value
- Continuous regression gate: PASS or FAIL with value
- Heavy regression gate: PASS or FAIL with value
- Correctness gate: PASS or FAIL

## Decision
Accepted, or stopped for a separate worker-count/Chunk-capacity investigation.
```

Populate every environment and table value from the commands in Steps 1-4 before committing; the final results document must contain no empty cells or instructional text.

- [ ] **Step 6: Commit the verified results**

```powershell
git add docs/performance/cooperative-chunk-executor-results.md
git commit -m "docs(perf): record cooperative chunk executor results"
```

## Final Review Checklist

- [ ] `git diff HEAD~6 --check` reports no whitespace errors.
- [ ] `git status --short` contains no unintended generated files or benchmark logs.
- [ ] Native tests pass once visibly and 100 times in stress mode.
- [ ] Managed Release build succeeds.
- [ ] Generated native `IJobChunk` bindings call `ScheduleChunkRangeRaw`.
- [ ] Sleep benchmark source contains no `KeepWorkersWarm` call.
- [ ] Five benchmark processes pass correctness checks.
- [ ] Results document contains exact values and an explicit acceptance decision.
