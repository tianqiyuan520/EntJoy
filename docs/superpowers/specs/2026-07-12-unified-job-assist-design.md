# Unified Job Assist Design

## Objective

Improve EntJoy JobSystem throughput while retaining Taskflow, the existing C# Job APIs, and the NativeTranspiler ABI. The design restores useful work on the thread calling `Complete()`, unifies assist behavior across parallel job types, and prevents unrelated worker pools from being awakened.

The primary regression baseline is commit `fa41acd19f4549c062b8a1545b1967ef56140bff`. GridSearch2D is the primary throughput benchmark.

## Constraints

- Keep Taskflow as the general-purpose worker executor.
- Preserve all public C# APIs and generated native export signatures.
- Preserve dependency, cancellation, shutdown, and cleanup safety.
- Optimize continuous throughput first.
- Do not refactor ECS, Query, or Native Collections outside changes required for JobSystem correctness.

## Current Problem

Large `IJobParallelFor` and `IJobParallelForBatch` jobs register an `assistStep` that can atomically claim and execute one batch. Since commit `a810077`, `JobHandle::Complete()` no longer consumes `assistStep`. The caller therefore spins and blocks while Taskflow workers perform all remaining work.

Chunk and entity-batch jobs later gained a separate `assistCallback` mechanism. The two protocols now have different lifetime rules and behavior. `Complete()` also understands `ChunkBatchState`, which couples generic handle completion to one scheduler implementation.

Automatic prewake uses a shared native entry point. A normal Taskflow ParallelFor can consequently notify the separate Chunk worker pool even though it has no Chunk work.

GridSearch also currently benchmarks `AssignAndCountJobPointer` as C++ rather than the ISPC backend used by the baseline. Benchmark comparisons must restore the same backend before attributing all changes to scheduling.

## Chosen Approach

Use one type-safe, allocation-free assist protocol for all parallel job families:

```cpp
struct AssistState
{
    bool (*tryExecuteOne)(void*) noexcept;
    void* context;
    std::atomic<int> readers;
};
```

`HandleState` references an `AssistState` without knowing its concrete batch type. Taskflow workers and the thread calling `Complete()` claim work through the same atomic claim operation. A claimed unit can execute only once.

This replaces the ordinary-job `std::function assistStep` and the Chunk-specific assumptions in `Complete()`.

## Scheduling Architecture

### General Parallel Jobs

An `IJobParallelFor` or `IJobParallelForBatch` schedule creates a batch state containing:

- callback and job context;
- array length and resolved batch size;
- total batch count;
- atomic next-batch cursor;
- atomic completed-batch counter;
- one-time cleanup/finalization state;
- embedded assist state.

Taskflow receives at most `workerCount` drain tasks. Each drain task repeatedly calls the same `tryExecuteOne` function until no unclaimed batch remains. `Complete()` calls that function through `AssistState`.

Both small and large batch counts use this ownership model. Small jobs may still use a synchronous fast path when already covered by the existing length thresholds.

### Chunk and Entity-Batch Jobs

The existing fixed token/range ownership is retained. Its claim function is exposed through the same `AssistState` interface. Worker tokens and the calling thread compete only for unclaimed tokens.

`Complete()` must not cast the assist context to `ChunkBatchState`. Chunk-specific wake-latency and range statistics remain inside the Chunk scheduler.

### Automatic Batch Size

When the caller passes zero, the target is four batches per worker:

```text
targetBatchCount = workerCount * 4
batchSize = ceil(length / targetBatchCount)
```

This provides enough work units for load balancing and tail recovery on irregular kernels. A positive explicit batch size remains authoritative. Existing negative batch-size force-async behavior remains unchanged.

## Complete Behavior

`JobHandle::Complete()` uses three phases:

1. **Immediate assist:** attempt one work unit before waiting.
2. **Bounded throughput assist:** continue claiming work while useful, stopping when the job completes, no work remains, active workers cover the remaining units, or the assist time budget expires.
3. **Short spin and atomic wait:** when there is no useful work to claim, briefly spin for final worker completion and then block with `atomic::wait`.

The throughput policy uses observed state rather than a fixed batch count. Initial time budgets should be simple constants validated by benchmarks; adaptive heuristics are added only when measurements justify them.

## Lifetime and Cleanup

Before using an assist callback, `Complete()` increments `AssistState::readers`. It decrements readers after the last callback access. Batch cleanup and state reuse require all of the following:

- all work units completed or the batch was safely cancelled;
- all queued worker tokens released;
- assist reader count is zero;
- cleanup has not already executed.

Cleanup is guarded by a one-time atomic transition. Exceptions from native callbacks cannot bypass completion accounting or cleanup. Existing managed exception recording remains intact.

## Prewake Separation

Introduce internal operations:

- `PrewakeTaskflowWorkers()` for normal Job, ParallelFor, and ParallelForBatch work;
- `PrewakeChunkWorkers()` for Chunk and entity-batch queues.

Automatic prewake calls only the relevant operation. The public `JobSystem_PrewakeWorkers` ABI remains unchanged and calls both operations for compatibility.

For continuous Taskflow workloads, automatic prewake is skipped when a recent normal job indicates the executor is already active. Normal jobs must not notify the Chunk condition variable.

## Implementation Stages

### Stage 1: General Parallel Assist

- Add regression tests for main-thread participation and exact-once execution.
- Introduce the unified assist representation for normal parallel jobs.
- Replace `std::function assistStep` with a function pointer and batch context.
- Restore useful work in `Complete()` without changing Chunk scheduling.
- Measure GridSearch query performance before proceeding.

### Stage 2: Chunk Protocol Unification

- Adapt fixed Chunk and entity-batch tokens to the unified assist interface.
- Remove concrete `ChunkBatchState` knowledge from `Complete()`.
- Preserve current token ownership and use-after-free protections.
- Re-run Chunk, Entity, dependency, and shutdown tests.

### Stage 3: Batching and Prewake

- Change automatic general-job batching to four batches per worker.
- Split Taskflow and Chunk prewake internally.
- Tune only from benchmark evidence.

Each stage is independently reviewable and reversible.

## Correctness Tests

Tests must cover:

- every index executes exactly once;
- concurrent worker and caller claims cause no omission or duplication;
- zero length, one batch, explicit batch size, and automatic batch size;
- unfinished dependencies prevent premature execution;
- copied handles, `CompleteAndRelease`, and combined dependencies;
- cleanup executes exactly once after the last assist reader exits;
- shutdown safely completes or cancels outstanding jobs;
- callback exceptions cannot cause permanent waits or context leaks;
- managed and unmanaged job contexts retain existing behavior.

## Performance Validation

Run Release binaries without an attached debugger. Report median and P95, not only the mean.

Required benchmarks:

- GridSearch2D with 100,000 build positions and 100,000 queries;
- light and heavy `IJobParallelFor`;
- continuous `IJobChunkMoveCompare`;
- the 16 ms sleep/cold-start variant;
- explicit versus automatic batch sizes;
- C#, C++, and ISPC backends where supported.

Before comparing GridSearch to `fa41acd`, restore `AssignAndCountJobPointer` to the same ISPC backend used by that baseline.

Targets:

- GridSearch query median at or below 0.8 ms, aiming for approximately 0.6 ms;
- GridSearch core build median at or below 1.0 ms under the matching ISPC configuration;
- no more than 10% regression in the 16 ms sleep scenario;
- no correctness or lifecycle regression.

## Out of Scope

- replacing or removing Taskflow;
- changing public C# Job APIs;
- changing NativeTranspiler export ABI;
- adding public scheduling modes;
- unrelated ECS, Query, or collection refactors.
