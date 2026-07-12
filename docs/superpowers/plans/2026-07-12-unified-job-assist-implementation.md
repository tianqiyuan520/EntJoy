# Unified Job Assist Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore Unity-style caller participation for Taskflow parallel jobs, unify assist ownership across all parallel job families, and prevent unrelated worker-pool wakeups without changing public C# APIs or generated native ABI.

**Architecture:** Taskflow remains the general worker executor. Every parallel batch exposes a raw `AssistState` whose callback atomically claims one unit; Taskflow drains and `JobHandle::Complete()` use the same claimant. Chunk jobs adapt their existing fixed tokens to the same interface, while pool-specific prewake operations remain internal behind the existing public ABI.

**Tech Stack:** C++20, Taskflow, C#/.NET 8, MSBuild, CMake, MSVC, Intel ISPC.

## Global Constraints

- Keep Taskflow as the general-purpose worker executor.
- Preserve all public C# APIs and NativeTranspiler export signatures.
- Preserve dependency, cancellation, shutdown, exception recording, and cleanup safety.
- Optimize continuous throughput first.
- Use Release binaries without an attached debugger for performance acceptance.
- Do not stage or modify unrelated existing worktree changes.

---

## File Map

- Create `src/NativeDll.Tests/CMakeLists.txt`: isolated native regression-test build.
- Create `src/NativeDll.Tests/JobSystemTests.cpp`: exact-once, assist, dependency, cleanup, and shutdown tests.
- Modify `src/NativeDll/JobSystem.h`: define the generic assist contract and remove legacy `assistStep`.
- Modify `src/NativeDll/JobSystem.cpp`: general parallel batch ownership, generic completion assist, Chunk adaptation, batching, and prewake separation.
- Modify `src/NativeDll/Exports.h`: no ABI additions; comments only if needed.
- Modify `src/NativeDll/Exports.cpp`: keep `JobSystem_PrewakeWorkers` mapped to the compatibility wrapper.
- Modify `src/EntJoy/JobSystem/NativeJobScheduler.cs`: route automatic normal-job prewake to a new internal native export only if an ABI-preserving export addition is unavoidable; preferred implementation keeps this routing native-side.
- Modify `src/EntJoySample/05_Algorithms/GridSearch/GridSearch2D.cs`: restore the baseline ISPC attribute for `AssignAndCountJobPointer` only after preserving the user's current uncommented state.
- Create `docs/performance/job-system-assist-results.md`: record commands, hardware, median, P95, and before/after results.

### Task 1: Native Regression-Test Harness

**Files:**
- Create: `src/NativeDll.Tests/CMakeLists.txt`
- Create: `src/NativeDll.Tests/JobSystemTests.cpp`
- Test: `src/NativeDll.Tests/JobSystemTests.cpp`

**Interfaces:**
- Consumes: `JobSystem::Scheduler`, `JobSystem::JobHandle`, and `JobSystemStatsSnapshot` from `src/NativeDll/JobSystem.h`.
- Produces: executable `JobSystemTests.exe`; named test cases such as `PASS ParallelForExactOnce`, or process exit code `1`.

- [ ] **Step 1: Create the test build definition**

```cmake
cmake_minimum_required(VERSION 3.20)
project(EntJoyJobSystemTests LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_executable(JobSystemTests
    JobSystemTests.cpp
    ../NativeDll/JobSystem.cpp
    ../NativeDll/JobProfiler.cpp
)

target_include_directories(JobSystemTests PRIVATE
    ../NativeDll
    ../../external/cpp-taskflow
)

if(MSVC)
    target_compile_options(JobSystemTests PRIVATE /O2 /Ob2 /Oi /Ot /arch:AVX2 /MP)
    target_compile_definitions(JobSystemTests PRIVATE NDEBUG NOMINMAX)
endif()
```

- [ ] **Step 2: Write the initial exact-once and cleanup tests**

```cpp
#include "../NativeDll/JobSystem.h"
#include <atomic>
#include <chrono>
#include <iostream>
#include <stdexcept>
#include <thread>
#include <vector>

namespace {
struct TestFailure : std::runtime_error { using std::runtime_error::runtime_error; };
void Require(bool value, const char* message) { if (!value) throw TestFailure(message); }

struct ParallelContext {
    std::vector<std::atomic<int>>* hits;
    std::atomic<int>* cleanupCount;
    std::atomic<int>* callerExecutions;
    std::thread::id caller;
};

void ExecuteRange(void* raw, int start, int count) {
    auto& ctx = *static_cast<ParallelContext*>(raw);
    if (std::this_thread::get_id() == ctx.caller)
        ctx.callerExecutions->fetch_add(1, std::memory_order_relaxed);
    for (int i = start; i < start + count; ++i)
        (*ctx.hits)[static_cast<size_t>(i)].fetch_add(1, std::memory_order_relaxed);
}

void Cleanup(void* raw) {
    static_cast<ParallelContext*>(raw)->cleanupCount->fetch_add(1, std::memory_order_relaxed);
}

void TestParallelForExactOnce() {
    constexpr int length = 100'000;
    std::vector<std::atomic<int>> hits(length);
    std::atomic<int> cleanupCount{0};
    std::atomic<int> callerExecutions{0};
    ParallelContext ctx{&hits, &cleanupCount, &callerExecutions, std::this_thread::get_id()};
    auto handle = JobSystem::Scheduler::ScheduleParallelForBatch(
        &ExecuteRange, &ctx, length, 0, &Cleanup);
    handle.Complete();
    for (const auto& hit : hits) Require(hit.load() == 1, "index was missed or duplicated");
    Require(cleanupCount.load() == 1, "cleanup must run exactly once");
}
}

int main() {
    JobSystem::Scheduler::Initialize();
    try {
        TestParallelForExactOnce();
        std::cout << "PASS ParallelForExactOnce\n";
        JobSystem::Scheduler::Shutdown();
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "FAIL " << error.what() << '\n';
        JobSystem::Scheduler::Shutdown();
        return 1;
    }
}
```

- [ ] **Step 3: Build and run the baseline test**

Run:

```powershell
cmake -S src/NativeDll.Tests -B src/NativeDll.Tests/build
cmake --build src/NativeDll.Tests/build --config Release --parallel
& src/NativeDll.Tests/build/Release/JobSystemTests.exe
```

Expected: build succeeds and prints `PASS ParallelForExactOnce`.

- [ ] **Step 4: Add the failing caller-assist assertion**

Add after the cleanup assertion:

```cpp
Require(callerExecutions.load(std::memory_order_relaxed) > 0,
    "Complete caller did not execute any parallel batch");
```

- [ ] **Step 5: Run the regression test and verify current behavior fails**

Run:

```powershell
cmake --build src/NativeDll.Tests/build --config Release --parallel
& src/NativeDll.Tests/build/Release/JobSystemTests.exe
```

Expected: exit code `1` with `FAIL Complete caller did not execute any parallel batch`.

- [ ] **Step 6: Commit the failing regression test**

```powershell
git add src/NativeDll.Tests/CMakeLists.txt src/NativeDll.Tests/JobSystemTests.cpp
git commit -m "test(jobs): reproduce missing Complete caller assist"
```

### Task 2: Unified Assist Contract in HandleState

**Files:**
- Modify: `src/NativeDll/JobSystem.h`
- Modify: `src/NativeDll/JobSystem.cpp`
- Test: `src/NativeDll.Tests/JobSystemTests.cpp`

**Interfaces:**
- Consumes: existing `AssistStepCallback = bool (*)(void*) noexcept`.
- Produces: `AssistState`, `HandleState::assist`, `TryAcquireAssist`, and `ReleaseAssist`.

- [ ] **Step 1: Add the generic assist state and remove legacy storage**

In `JobSystem.h`, replace `assistCallback`, `assistContext`, `assistReaders`, and `assistStep` with:

```cpp
struct AssistState {
    std::atomic<AssistStepCallback> callback{nullptr};
    std::atomic<void*> context{nullptr};
    std::atomic<int> readers{0};
};

struct alignas(hardware_destructive_interference_size) HandleState {
    // existing fields before assist
    AssistState assist;
    std::atomic<AssistStepCallback> pendingCallback{nullptr};
    std::atomic<void*> pendingContext{nullptr};
    std::mutex mtx;
    // existing debug fields and constructor
};
```

Do not change any exported function signature.

- [ ] **Step 2: Reset the unified state in pooled-handle initialization**

Use this sequence in both `RecycleState` and `CreateState`:

```cpp
state->assist.callback.store(nullptr, std::memory_order_release);
state->assist.context.store(nullptr, std::memory_order_release);
state->assist.readers.store(0, std::memory_order_relaxed);
```

Delete the locked `state->assistStep = {};` blocks.

- [ ] **Step 3: Clear new claims during completion without invalidating active readers**

In `CompleteState`, replace legacy clearing with:

```cpp
state->assist.callback.store(nullptr, std::memory_order_release);
state->assist.context.store(nullptr, std::memory_order_release);
```

Do not reset `readers` here. Active callers decrement it on exit.

- [ ] **Step 4: Add scoped assist acquisition helpers in JobSystem.cpp**

```cpp
struct AssistLease {
    AssistState* state{nullptr};
    AssistStepCallback callback{nullptr};
    void* context{nullptr};

    AssistLease() = default;
    AssistLease(const AssistLease&) = delete;
    AssistLease& operator=(const AssistLease&) = delete;
    ~AssistLease() {
        if (state) state->readers.fetch_sub(1, std::memory_order_acq_rel);
    }
};

AssistLease TryAcquireAssist(HandleState* handle) noexcept {
    AssistLease lease;
    auto& assist = handle->assist;
    assist.readers.fetch_add(1, std::memory_order_acq_rel);
    lease.state = &assist;
    lease.callback = assist.callback.load(std::memory_order_acquire);
    lease.context = assist.context.load(std::memory_order_acquire);
    if (!lease.callback || !lease.context || handle->completed.load(std::memory_order_acquire)) {
        assist.readers.fetch_sub(1, std::memory_order_acq_rel);
        lease.state = nullptr;
        lease.callback = nullptr;
        lease.context = nullptr;
    }
    return lease;
}
```

Give `AssistLease` an explicit move constructor so returning it does not decrement twice:

```cpp
AssistLease(AssistLease&& other) noexcept
    : state(std::exchange(other.state, nullptr)),
      callback(other.callback), context(other.context) {}
```

- [ ] **Step 5: Build to expose all legacy references**

Run:

```powershell
cmake --build src/NativeDll.Tests/build --config Release --parallel
```

Expected: compilation fails only at remaining `assistCallback`, `assistContext`, `assistReaders`, or `assistStep` references. Use `rg -n "assistCallback|assistContext|assistReaders|assistStep" src/NativeDll` to enumerate them.

- [ ] **Step 6: Migrate direct Chunk field accesses mechanically**

Map fields as follows without changing behavior yet:

```text
state->assistCallback  -> state->assist.callback
state->assistContext   -> state->assist.context
state->assistReaders   -> state->assist.readers
```

Expected after migration:

```powershell
rg -n "assistCallback|assistContext|assistReaders|assistStep" src/NativeDll
```

prints no matches.

- [ ] **Step 7: Build and run tests**

Run:

```powershell
cmake --build src/NativeDll.Tests/build --config Release --parallel
& src/NativeDll.Tests/build/Release/JobSystemTests.exe
```

Expected: build succeeds; caller-assist test still fails because general parallel batches are not registered yet.

- [ ] **Step 8: Commit the contract migration**

```powershell
git add src/NativeDll/JobSystem.h src/NativeDll/JobSystem.cpp
git commit -m "refactor(jobs): unify handle assist state"
```

### Task 3: General Parallel Batch Ownership and Caller Assist

**Files:**
- Modify: `src/NativeDll/JobSystem.cpp`
- Test: `src/NativeDll.Tests/JobSystemTests.cpp`

**Interfaces:**
- Consumes: `HandleState::assist` and `TryAcquireAssist` from Task 2.
- Produces: one shared batch claimant for `ScheduleParallelFor` and `ScheduleParallelForBatch`.

- [ ] **Step 1: Add a reusable general batch state**

```cpp
struct GeneralBatchState {
    HandleState* handle{nullptr};
    void* jobContext{nullptr};
    void* cleanupContext{nullptr};
    void (*cleanup)(void*){nullptr};
    void (*batchCallback)(void*, int, int){nullptr};
    std::shared_ptr<void> ownedAdapter;
    int length{0};
    int batchSize{1};
    int batchCount{0};
    std::atomic<int> nextBatch{0};
    std::atomic<int> completedBatches{0};
    std::atomic<int> activeWorkers{0};
    std::atomic<bool> finalized{false};
};

void FinalizeGeneralBatch(GeneralBatchState* batch) noexcept {
    if (!batch->finalized.exchange(true, std::memory_order_acq_rel)) {
        batch->handle->assist.callback.store(nullptr, std::memory_order_release);
        batch->handle->assist.context.store(nullptr, std::memory_order_release);
        if (batch->cleanup) { try { batch->cleanup(batch->cleanupContext); } catch (...) {} }
        CompleteState(batch->handle);
    }
}

bool TryExecuteGeneralBatch(void* raw) noexcept {
    auto* batch = static_cast<GeneralBatchState*>(raw);
    const int index = batch->nextBatch.fetch_add(1, std::memory_order_relaxed);
    if (index >= batch->batchCount) return false;
    const int start = index * batch->batchSize;
    const int count = std::min(batch->batchSize, batch->length - start);
    try { batch->batchCallback(batch->jobContext, start, count); } catch (...) {}
    if (batch->completedBatches.fetch_add(1, std::memory_order_acq_rel) + 1 == batch->batchCount)
        FinalizeGeneralBatch(batch);
    return true;
}
```

The state must be owned by `std::shared_ptr<GeneralBatchState>` captured by all Taskflow drain tasks and the Taskflow completion callback. Do not delete it from `FinalizeGeneralBatch`.

- [ ] **Step 2: Adapt index callbacks to batch callbacks**

For `ScheduleParallelFor`, create one context-local batch adapter that runs the existing index callback:

```cpp
struct IndexBatchContext {
    void (*callback)(void*, int){nullptr};
    void* context{nullptr};
};

auto executeIndexBatch = [](void* raw, int start, int count) {
    auto& adapter = *static_cast<IndexBatchContext*>(raw);
    for (int i = start; i < start + count; ++i)
        adapter.callback(adapter.context, i);
};
```

Keep the adapter alive in `GeneralBatchState::ownedAdapter` and point `jobContext` at the adapter. Set `cleanupContext` to the original job context.

- [ ] **Step 3: Replace both asynchronous scheduling branches with drain tasks**

For each schedule:

```cpp
auto batch = std::make_shared<GeneralBatchState>();
batch->handle = state;
batch->jobContext = callbackContext;
batch->cleanupContext = context;
batch->cleanup = cleanup;
batch->batchCallback = callback;
batch->length = length;
batch->batchSize = resolvedBatchSize;
batch->batchCount = (length + resolvedBatchSize - 1) / resolvedBatchSize;

state->assist.context.store(batch.get(), std::memory_order_release);
state->assist.callback.store(&TryExecuteGeneralBatch, std::memory_order_release);

auto taskflow = AcquireTaskflow();
const int drainCount = std::min(workerCount, batch->batchCount);
for (int worker = 0; worker < drainCount; ++worker) {
    taskflow->emplace([batch] {
        batch->activeWorkers.fetch_add(1, std::memory_order_acq_rel);
        while (TryExecuteGeneralBatch(batch.get())) {}
        batch->activeWorkers.fetch_sub(1, std::memory_order_acq_rel);
    });
}
AcquireState(state);
executor->run(*taskflow, [taskflow, state, batch] {
    ReleaseState(state);
});
```

Keep the existing synchronous thresholds and dependency scheduling wrapper.

- [ ] **Step 4: Make Complete consume the generic assist contract**

At the start of `Complete()` after pending publication:

```cpp
auto lease = TryAcquireAssist(_state);
if (lease.callback && lease.context) {
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::microseconds(1500);
    do {
        g_assistAttempts.fetch_add(1, std::memory_order_relaxed);
        if (!lease.callback(lease.context)) break;
        g_assistExecuted.fetch_add(1, std::memory_order_relaxed);
        g_mainExecutedRanges.fetch_add(1, std::memory_order_relaxed);
    } while (!_state->completed.load(std::memory_order_acquire) &&
             std::chrono::steady_clock::now() < deadline);
}
```

After the lease leaves scope, retain the existing short spin and atomic wait. Do not cast `lease.context` to a concrete batch type.

- [ ] **Step 5: Run the regression test**

Run:

```powershell
cmake --build src/NativeDll.Tests/build --config Release --parallel
& src/NativeDll.Tests/build/Release/JobSystemTests.exe
```

Expected: `PASS ParallelForExactOnce`; the caller-assist assertion passes.

- [ ] **Step 6: Add explicit and automatic batch-size cases**

Factor `TestParallelForExactOnce(int batchSize)` and invoke it with:

```cpp
TestParallelForExactOnce(0);
TestParallelForExactOnce(1);
TestParallelForExactOnce(257);
TestParallelForExactOnce(100'000);
```

Expected: every case executes each index once and cleanup once.

- [ ] **Step 7: Add dependency ordering test**

```cpp
std::atomic<bool> dependencyFinished{false};
std::atomic<bool> childRanEarly{false};
auto dependency = JobSystem::Scheduler::Schedule(
    [](void* raw) {
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
        static_cast<std::atomic<bool>*>(raw)->store(true, std::memory_order_release);
    }, &dependencyFinished);

struct DependentContext { std::atomic<bool>* done; std::atomic<bool>* early; } depCtx{&dependencyFinished, &childRanEarly};
auto child = JobSystem::Scheduler::ScheduleParallelForBatch(
    [](void* raw, int, int) {
        auto& ctx = *static_cast<DependentContext*>(raw);
        if (!ctx.done->load(std::memory_order_acquire)) ctx.early->store(true);
    }, &depCtx, 100'000, 0, nullptr, dependency);
child.Complete();
Require(!childRanEarly.load(), "dependent parallel job ran before dependency");
```

- [ ] **Step 8: Run tests repeatedly for race coverage**

Run:

```powershell
1..100 | ForEach-Object { & src/NativeDll.Tests/build/Release/JobSystemTests.exe; if ($LASTEXITCODE -ne 0) { throw "iteration $_ failed" } }
```

Expected: all 100 iterations exit `0`.

- [ ] **Step 9: Commit general parallel assist**

```powershell
git add src/NativeDll/JobSystem.cpp src/NativeDll.Tests/JobSystemTests.cpp
git commit -m "perf(jobs): restore caller assist for parallel batches"
```

### Task 4: Adapt Chunk and Entity Batches to Generic Complete

**Files:**
- Modify: `src/NativeDll/JobSystem.cpp`
- Test: `src/NativeDll.Tests/JobSystemTests.cpp`

**Interfaces:**
- Consumes: generic `AssistState` and `TryAcquireAssist`.
- Produces: Chunk token callback usable without any `ChunkBatchState` cast in `Complete()`.

- [ ] **Step 1: Register existing token claim callbacks through AssistState**

In `PublishChunkBatch`, retain the cold/hot callback selection but store it generically:

```cpp
batch->handleState->assist.context.store(batch, std::memory_order_release);
batch->handleState->assist.callback.store(
    batch->coldStart ? &RunOneQueuedColdChunkToken : &RunOneQueuedChunkToken,
    std::memory_order_release);
```

- [ ] **Step 2: Move Chunk-specific assist budgeting into the callback**

`RunOneQueuedColdChunkToken` may claim one fixed token and return. Remove `tokenCount`, `activeWorkers`, `workerTarget`, and wake-latency decisions from `JobHandle::Complete()`. The generic completion loop controls only its time deadline and stops when the callback returns false.

- [ ] **Step 3: Preserve Chunk recycle gates**

Update `TryReleaseChunkBatchState` to require:

```cpp
if (!batch->cleanupDone.load(std::memory_order_acquire) ||
    batch->queueTokens.load(std::memory_order_acquire) != 0 ||
    batch->assistReaders.load(std::memory_order_acquire) != 0 ||
    (batch->handleState && batch->handleState->assist.readers.load(std::memory_order_acquire) != 0))
    return;
```

- [ ] **Step 4: Add a Chunk exact-once native test**

Construct 1,024 zero-initialized `ChunkJobData` entries and schedule a range callback that increments one atomic counter per entry:

```cpp
void ExecuteChunkRange(void* raw, const ChunkJobData*, int start, int count) {
    auto* hits = static_cast<std::atomic<int>*>(raw);
    for (int i = start; i < start + count; ++i)
        hits[i].fetch_add(1, std::memory_order_relaxed);
}
```

Schedule with `ScheduleChunkRanges`, `PublishAssist`, `workerCap = 0`, `rangeSize = 0`; call `Complete()` and require every counter equals one.

- [ ] **Step 5: Build and run all native tests repeatedly**

Run:

```powershell
cmake --build src/NativeDll.Tests/build --config Release --parallel
1..100 | ForEach-Object { & src/NativeDll.Tests/build/Release/JobSystemTests.exe; if ($LASTEXITCODE -ne 0) { throw "iteration $_ failed" } }
```

Expected: all exact-once, dependency, cleanup, and Chunk cases pass.

- [ ] **Step 6: Commit Chunk protocol unification**

```powershell
git add src/NativeDll/JobSystem.cpp src/NativeDll.Tests/JobSystemTests.cpp
git commit -m "refactor(jobs): unify chunk and parallel assist"
```

### Task 5: Automatic Batching and Pool-Specific Prewake

**Files:**
- Modify: `src/NativeDll/JobSystem.h`
- Modify: `src/NativeDll/JobSystem.cpp`
- Modify: `src/NativeDll/Exports.cpp`
- Test: `src/NativeDll.Tests/JobSystemTests.cpp`

**Interfaces:**
- Consumes: generic claimant from Tasks 3–4.
- Produces: four-batches-per-worker auto sizing and separate internal prewake paths; public `Scheduler::PrewakeWorkers()` remains unchanged.

- [ ] **Step 1: Add a deterministic batch-size helper and test hook**

Declare internally in `JobSystem.cpp`:

```cpp
int ResolveChunkSize(int length, int requestedChunk) {
    if (length <= 0) return 1;
    if (requestedChunk > 0) return requestedChunk;
    const int workerCount = std::max(1, CurrentWorkerCount());
    const int targetBatchCount = workerCount * 4;
    return std::max(64, (length + targetBatchCount - 1) / targetBatchCount);
}
```

Add a debug/test-only declaration guarded by `ENTJOY_JOB_SYSTEM_TESTS`:

```cpp
#ifdef ENTJOY_JOB_SYSTEM_TESTS
int ResolveChunkSizeForTests(int length, int requestedChunk) {
    return ResolveChunkSize(length, requestedChunk);
}
#endif
```

- [ ] **Step 2: Add batch-size assertions**

Compile tests with `ENTJOY_JOB_SYSTEM_TESTS` and verify:

```cpp
Require(JobSystem::ResolveChunkSizeForTests(100'000, 257) == 257,
    "explicit batch size changed");
const int automatic = JobSystem::ResolveChunkSizeForTests(100'000, 0);
Require(automatic >= 64, "automatic batch size below minimum");
```

- [ ] **Step 3: Split internal prewake functions**

```cpp
void PrewakeTaskflowWorkers() {
    std::shared_ptr<tf::Executor> executor;
    int count = 0;
    {
        std::lock_guard<std::mutex> lock(g_executorMutex);
        executor = g_executor;
        count = g_numThreads;
    }
    if (!executor || count <= 0) return;
    for (int i = 0; i < std::min(count, 4); ++i)
        executor->silent_async([] {});
}

void PrewakeChunkWorkers() {
    const int workerCount = std::max(1, CurrentWorkerCount());
    EnsureChunkWorkers(workerCount);
    g_chunkPrewakeGeneration.fetch_add(1, std::memory_order_release);
    g_chunkWorkerCv.notify_all();
}

void Scheduler::PrewakeWorkers() {
    g_prewakeCount.fetch_add(1, std::memory_order_relaxed);
    PrewakeTaskflowWorkers();
    PrewakeChunkWorkers();
}
```

- [ ] **Step 4: Route automatic ordinary-job prewake only to Taskflow**

Add a native helper callable inside `ScheduleParallelFor` and `ScheduleParallelForBatch` before scheduling:

```cpp
void AutoPrewakeTaskflowIfNeeded(int length) {
    if (length < 1024) return;
    static std::atomic<int64_t> lastTicks{0};
    const auto now = std::chrono::steady_clock::now().time_since_epoch();
    const int64_t nowNs = std::chrono::duration_cast<std::chrono::nanoseconds>(now).count();
    const int64_t previous = lastTicks.exchange(nowNs, std::memory_order_acq_rel);
    if (previous == 0 || nowNs - previous >= 1'000'000)
        PrewakeTaskflowWorkers();
}
```

Remove the C# `AutoPrewakeIfNeeded(length)` calls for ordinary schedules only after native-side routing exists. Keep explicit `NativeJobScheduler.PrewakeWorkersOnce()` behavior unchanged.

- [ ] **Step 5: Verify public ABI remains present**

Run:

```powershell
dumpbin /exports bin/NativeDll.dll | Select-String 'JobSystem_PrewakeWorkers'
```

Expected: exactly one exported `JobSystem_PrewakeWorkers` symbol. No existing export is removed.

- [ ] **Step 6: Run all correctness tests and Release build**

Run:

```powershell
cmake --build src/NativeDll.Tests/build --config Release --parallel
& src/NativeDll.Tests/build/Release/JobSystemTests.exe
dotnet build src/EntJoySample/EntJoySample.csproj -c Release
```

Expected: native tests pass; .NET/native build succeeds with zero errors.

- [ ] **Step 7: Commit batching and prewake**

```powershell
git add src/NativeDll/JobSystem.h src/NativeDll/JobSystem.cpp src/NativeDll/Exports.cpp src/EntJoy/JobSystem/NativeJobScheduler.cs src/NativeDll.Tests/JobSystemTests.cpp
git commit -m "perf(jobs): tune batching and split worker prewake"
```

### Task 6: GridSearch Baseline and Performance Acceptance

**Files:**
- Modify: `src/EntJoySample/05_Algorithms/GridSearch/GridSearch2D.cs`
- Modify: `src/EntJoySample/05_Algorithms/GridSearch/TestGridSearch.cs`
- Create: `docs/performance/job-system-assist-results.md`

**Interfaces:**
- Consumes: completed scheduler implementation and the user's currently enabled GridSearch sample.
- Produces: comparable baseline/backend configuration and recorded median/P95 results.

- [ ] **Step 1: Preserve and inspect the user's GridSearch changes**

Run:

```powershell
git diff -- src/EntJoySample/05_Algorithms/GridSearch/GridSearch2D.cs src/EntJoySample/05_Algorithms/GridSearch/TestGridSearch.cs
```

Expected: uncommented sample plus the current backend selection. Do not replace the files wholesale.

- [ ] **Step 2: Restore only AssignAndCount to the baseline backend**

Use:

```csharp
[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
public unsafe struct AssignAndCountJobPointer : IJobParallelFor
```

Remove the adjacent default `[NativeTranspile]` attribute. Do not change the algorithm body.

- [ ] **Step 3: Change benchmark aggregation to median and P95**

Store each build/query duration in arrays and add:

```csharp
private static (double Median, double P95) Summarize(double[] samples)
{
    double[] sorted = (double[])samples.Clone();
    Array.Sort(sorted);
    double median = sorted[sorted.Length / 2];
    int p95Index = Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * 0.95) - 1);
    return (median, sorted[p95Index]);
}
```

Print both metrics for total and detailed phases. Keep existing mean output for historical comparison.

- [ ] **Step 4: Build and run three clean benchmark passes**

Run outside Visual Studio:

```powershell
dotnet build src/EntJoySample/EntJoySample.csproj -c Release
1..3 | ForEach-Object { & .\bin\EntJoySample.exe | Tee-Object "gridsearch-run-$_.txt" }
```

Expected: all passes exit `0` and return identical first ten result indices.

- [ ] **Step 5: Record hardware, commands, and results**

Collect the exact environment first:

```powershell
git rev-parse HEAD
Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors
```

Create `docs/performance/job-system-assist-results.md`. Record the exact commit and CPU command output, Release/detached-debugger configuration, 100,000/100,000 dataset, and 2/1000 warmup/iteration counts. Add a GridSearch table containing the fixed baselines `fa41acd: build 0.600 ms, query 0.600 ms` and `before change: build 2.466 ms, query 1.727 ms`, followed by the computed after-change median and P95 milliseconds from the three run files. End with three explicit acceptance lines covering query median at most 0.8 ms, build median at most 1.0 ms, and unchanged first-ten indices; state `PASS` or `FAIL` beside each measured value.

- [ ] **Step 6: Run Chunk continuous and 16 ms sleep benchmarks**

Enable the existing `IJobChunkMoveCompareTest` entry point without discarding other user edits, build Release, and capture continuous and sleep results. Compare sleep medians to the pre-change run; require no more than 10% regression.

- [ ] **Step 7: Run final verification**

Run:

```powershell
& src/NativeDll.Tests/build/Release/JobSystemTests.exe
dotnet build src/EntJoySample/EntJoySample.csproj -c Release
git diff --check
git status --short
```

Expected: tests and build pass; no whitespace errors; only intended implementation, benchmark, documentation, and pre-existing user files are listed.

- [ ] **Step 8: Commit benchmark changes separately**

```powershell
git add src/EntJoySample/05_Algorithms/GridSearch/GridSearch2D.cs src/EntJoySample/05_Algorithms/GridSearch/TestGridSearch.cs docs/performance/job-system-assist-results.md
git commit -m "perf(samples): validate unified job assist throughput"
```

Do not stage `IJobChunkMoveCompareSample.cs` or its `Program.cs` unless the user explicitly approves including those pre-existing edits.

### Task 7: Final Compatibility and Lifecycle Audit

**Files:**
- Modify only if a failing audit requires a focused correction.
- Test: `src/NativeDll.Tests/JobSystemTests.cpp`

**Interfaces:**
- Consumes: all prior tasks.
- Produces: verified implementation ready for review.

- [ ] **Step 1: Verify exported ABI against the design baseline**

Run:

```powershell
dumpbin /exports bin/NativeDll.dll | Select-String 'JobSystem_|JobProfiler_' | Set-Content current-exports.txt
git show fa41acd19f4549c062b8a1545b1967ef56140bff:src/NativeDll/Exports.h | Select-String 'JOB_API' | Set-Content baseline-api.txt
```

Manually confirm every baseline `JOB_API` function still has a corresponding current export.

- [ ] **Step 2: Add copied-handle and combined-dependency tests**

```cpp
auto original = JobSystem::Scheduler::ScheduleParallelForBatch(
    &ExecuteRange, &ctx, length, 257, &Cleanup);
auto copied = original;
copied.Complete();
original.Complete();
Require(cleanupCount.load() == 1, "copied handle caused duplicate cleanup");

std::vector<JobSystem::JobHandle> deps{first, second};
auto combined = JobSystem::JobHandle::CombineDependencies(deps);
combined.Complete();
Require(first.IsCompleted() && second.IsCompleted(), "combined dependency completed early");
```

- [ ] **Step 3: Add shutdown-with-outstanding-work test**

Schedule a job whose batches sleep for 100 microseconds, call `Scheduler::Shutdown()`, and require the process returns without a hang. Reinitialize afterward and run the exact-once test once more.

- [ ] **Step 4: Run lifecycle stress tests**

Run:

```powershell
cmake --build src/NativeDll.Tests/build --config Release --parallel
1..500 | ForEach-Object { & src/NativeDll.Tests/build/Release/JobSystemTests.exe; if ($LASTEXITCODE -ne 0) { throw "iteration $_ failed" } }
```

Expected: all 500 processes exit `0`; no hang, crash, duplicate cleanup, missed index, or early dependency execution.

- [ ] **Step 5: Inspect final diff scope**

Run:

```powershell
git diff --stat fa41acd19f4549c062b8a1545b1967ef56140bff..HEAD
git status --short
```

Expected: no unrelated ECS, Query, or Native Collections refactor was introduced by this work.

- [ ] **Step 6: Commit any focused audit correction**

If Step 1–4 required a correction, stage only its files and use:

```powershell
git commit -m "fix(jobs): harden unified assist lifecycle"
```

If no correction was required, do not create an empty commit.
