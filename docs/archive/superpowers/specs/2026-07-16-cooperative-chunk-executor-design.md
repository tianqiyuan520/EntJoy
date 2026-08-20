# Cooperative Chunk Executor Design

## Objective

Reduce the one-million-entity, 16 ms frame-gap `IJobChunk` latency to the Unity-class range without keeping workers busy across the frame gap. Preserve EntJoy's continuous-frame throughput, dependency semantics, native transpiler ABI, and exact-once cleanup guarantees.

The primary target is the C# `IJobChunk` Sleep benchmark. C++, ISPC, and `IJobEntity` use the same execution model and must retain correctness and avoid material regressions.

## Evidence and Baseline

On the current development machine and dirty worktree, the reproduced C# `IJobChunk` results were:

- Continuous light: 0.210 ms average.
- 16 ms Sleep light: 1.100 ms average.
- Reference Unity result supplied by the user: 0.95 ms.

The large continuous-to-Sleep difference shows that cold caches, memory power state, and worker reactivation dominate the Sleep case. The smaller EntJoy-to-Unity gap is consistent with scheduling, wake-up overlap, and tail balancing overhead.

The current scheduler publishes fixed-range tokens into a global mutex-protected deque. Once claimed, a token cannot be split. `Complete()` searches the deque for a token belonging to its target batch. This creates avoidable queue contention and tail latency after workers have parked.

An explicit `KeepWorkersWarm` call did not improve the reproduced Sleep result and changes the benchmark's power behavior. It is not part of the production solution.

## Scope

This design changes the execution of parallel Chunk-backed jobs:

- C# `IJobChunk`
- Transpiled C++ and ISPC `IJobChunk`
- C# `IJobEntity`
- Transpiled C++ and ISPC `IJobEntity`

It preserves the public scheduling APIs, native exports, dependency handles, query caches, enabled-component fallback behavior, and source-level job definitions.

The initial implementation does not replace Taskflow, rewrite the complete general-purpose JobSystem, or change the default ECS Chunk capacity. Worker-count and Chunk-capacity tuning are separate evidence-gated follow-up stages.

## Alternatives Considered

### Cooperative shared-range execution on the existing Chunk worker pool

This is the selected approach. It keeps the mature worker lifecycle and handle infrastructure while replacing fixed ownership with an atomic range cursor. It has the smallest concurrency and regression surface that directly addresses the measured problem.

### Route all Chunk work through Taskflow

This would unify worker ownership but risks reintroducing task-graph construction and dispatch overhead into a path that currently completes a hot one-million-entity workload in 0.210 ms. It is not selected without evidence that the cooperative executor cannot meet the target.

### Replace the scheduler with a Chase-Lev work-stealing pool

This is a reasonable long-term architecture for mixed general and ECS workloads, but it requires a much wider rewrite of queues, dependency activation, cancellation, shutdown, and memory reclamation. It is deferred unless the selected approach fails its acceptance criteria.

## Architecture

### ChunkBatchState

`ChunkBatchState` remains the unit of dependency, cleanup, and completion. Fixed `firstRange` and `lastRange` ownership moves out of queue tokens. The batch gains a shared claim cursor and explicit participant lifetime counters:

```text
ChunkBatchState
  function pointers and context
  chunk or entity-batch pointer
  itemCount
  rangeSize
  rangeCount
  atomic nextRange
  atomic completedRanges
  atomic activeTickets
  atomic assistReaders
  atomic scheduleState
  atomic cleanupDone
  HandleState pointer
```

`nextRange` is the only allocator of executable ranges. Every participant receives unique monotonically increasing indices using an atomic claim operation. `completedRanges` determines logical job completion. `activeTickets`, existing assist-reader counts, and `cleanupDone` determine when the pooled batch state can be reclaimed.

### Worker tickets

Publishing a batch creates up to `min(workerTarget, rangeCount)` lightweight queue tickets. A ticket references only the batch; it owns no range. After dequeuing a ticket, a worker repeatedly claims ranges directly from the batch until no work remains or the cooperative drain policy yields participation to other queued batches.

Tickets retain the batch until they are consumed or cancelled. Stale tickets may observe an exhausted cursor, release their retain, and exit without executing work.

The initial implementation retains the existing global mutex and condition variable for batch publication. Removing fixed ranges and main-thread queue searches is the first isolated hypothesis. A local-deque or lock-free publication redesign is out of scope unless profiling still identifies the publication lock as material.

### Complete-thread assist

`HandleState` exposes an assist callback and direct `ChunkBatchState` context as it does today. `Complete()` acquires an assist lease and claims ranges directly from that batch. It does not search or erase entries in the global runnable deque.

The completing thread starts useful work immediately and continues until one of these conditions holds:

- The target handle completes.
- The batch has no unclaimed range.
- The bounded assist policy decides to yield to already active workers.

For the immediate `Schedule().Complete()` path, the main thread is a normal participant rather than a waiter. The assist budget is a safeguard against monopolizing unrelated work, not a fixed delay before blocking.

### Range claims

The first implementation uses one logical range per claim. The existing range adapter may process one or more consecutive Chunks within that logical range according to `rangeSize`. This preserves contiguous memory traversal and keeps the correctness proof simple.

Adaptive multi-range claims are deferred. They may be added only if profiling shows atomic claim overhead is material. Near the tail, claim size must remain one so an idle participant can rebalance the final work.

### Wake and idle policy

Workers remain persistent. After exhausting work, a worker may perform a short bounded spin intended only to bridge back-to-back submissions, then parks on the existing condition variable. No policy spins across the 16 ms frame interval.

`KeepWorkersWarm` is removed from the Sleep benchmark. The public API may remain temporarily for compatibility, but the cooperative executor must meet acceptance criteria without calling it.

## Scheduling Data Flow

### Dependency already complete

1. C# builds or leases the cached Chunk metadata and allocates the job context.
2. Native scheduling creates and initializes `ChunkBatchState`.
3. The batch is published and worker tickets are enqueued.
4. `Schedule()` returns its handle.
5. If the caller immediately invokes `Complete()`, the caller claims work directly from the batch while workers wake and join.
6. Each range is claimed once and reported complete once.
7. The participant completing the final range performs exact-once cleanup and completes the handle.
8. The batch returns to its pool only after tickets and assist leases drain.

### Dependency incomplete

1. The batch is created but not published.
2. A continuation retains the batch and target state.
3. When the dependency completes, the continuation publishes the batch.
4. A concurrent `Complete()` may help dependencies through existing handle behavior, then assists the target batch after publication.
5. Shutdown either publishes/drains valid work or cancels it through the same exact-once finalization path.

### Empty and filtered queries

Empty queries return a completed handle after releasing the context. Queries using enabled-component filters retain their existing managed or per-Chunk fallback. They may adopt cooperative scheduling only when their metadata and callback satisfy the same lifetime rules.

## Concurrency Invariants

The implementation must maintain all of these invariants:

1. Every logical range is claimed at most once.
2. A completed range increments `completedRanges` exactly once.
3. Cleanup executes exactly once, after the final logical range or cancellation.
4. Handle completion becomes visible only after job writes and cleanup are published.
5. `ChunkBatchState` cannot return to its pool while any worker ticket, assist lease, continuation, or handle assist reader can access it.
6. Cancelling or shutting down cannot strand a ticket or leave an incomplete handle.
7. Copied handles and concurrent `Complete()` calls may assist the same batch without duplicate execution.
8. A worker observing an exhausted cursor must not infer that cleanup has already run; it releases participation and lets the final completed range perform finalization.

Atomic memory ordering should be no stronger than necessary, but the initial correctness implementation favors acquire-release transitions at publication, claim completion, cleanup, and reclamation boundaries. Relaxed operations are acceptable only for diagnostic counters or when ordered by an explicit surrounding transition.

## Native Transpiler Integration

Generated C++, ISPC, C# Chunk, and entity-batch adapters converge on a range callback shape:

```text
callback(context, items, startIndex, count)
```

The range adapter hoists context decoding and immutable job-field decoding outside the per-Chunk loop. It must retain correct translation for:

- Native containers and pointer fields.
- Read-only and writable component parameters.
- Job struct field alignment and offsets.
- C++ and ISPC calling conventions.
- Enabled-component fallback paths.

Generated-code changes are verified through generator outputs and runtime parity tests; generated files are not edited manually.

## ECS Chunk Capacity

Chunk capacity is not changed in the scheduler implementation commit. After the scheduler passes its correctness and performance gates, benchmark `128`, `256`, `512`, and `768` KiB targets in separate runs.

`256` KiB is the initial candidate because it increases scheduling granularity while keeping component arrays contiguous, but it becomes the default only if it satisfies all of these conditions:

- Improves or preserves Sleep latency.
- Keeps continuous light latency within its regression budget.
- Keeps Heavy throughput within its regression budget.
- Does not materially regress entity creation, destruction, migration, or memory overhead.

The environment override remains available for workload-specific experiments.

## Worker Count

Do not assume all logical processors are optimal for a memory-streaming job. After the cooperative scheduler is stable, run isolated worker-cap sweeps at `4`, `8`, and `15` workers on the target 8-core/16-thread machine.

Select a default only from repeatable evidence across both light memory-bound and Heavy compute-bound jobs. Preserve an explicit worker cap for applications with different latency, throughput, or power requirements. Automatic per-job tuning is out of scope for the first implementation.

## Diagnostics

Extend native statistics enough to attribute total latency without logging inside the hot loop:

- Schedule-to-publish time.
- Publish-to-first-main-claim time.
- Publish-to-first-worker-claim time.
- Last-range completion time.
- Worker and main claimed-range counts.
- Exhausted ticket count.
- Queue lock contention or wait time.
- Park/wake count and wake-latency EWMA.
- Wait fallback count.

Counters are reset outside measured regions. Diagnostic collection must be cheap enough to leave compiled in or be guarded by an existing profiler switch.

## Error Handling and Shutdown

Native job callbacks are treated as non-throwing across the ABI. A defensive native catch transitions the batch to cancellation and runs cleanup once. Managed exceptions must not cross unmanaged function-pointer boundaries.

Shutdown stops new scheduling, resolves pending dependency continuations according to current policy, wakes parked workers, drains or cancels runnable batches, consumes remaining tickets, waits for assist readers, then destroys worker and batch pools. Tests must cover shutdown racing with schedule, worker claims, and concurrent `Complete()`.

## Testing Strategy

### Native correctness tests

- Every range executes exactly once under one worker, all workers, and concurrent caller assist.
- Two or more concurrent `Complete()` calls do not duplicate ranges.
- A copied handle remains valid through completion and release.
- Dependencies publish the target batch only after prerequisite completion.
- Empty, one-range, fewer-ranges-than-workers, and many-range jobs complete.
- Worker tickets that arrive after cursor exhaustion release safely.
- Cancellation and shutdown run cleanup once and do not hang.
- Repeated initialize/shutdown cycles do not retain stale worker state.
- Stress tests vary range count, worker count, dependency depth, and completion timing.

### Managed and transpiler tests

- C# `IJobChunk` and `IJobEntity` parity.
- C++ and ISPC Chunk and entity parity.
- Enabled-component filters and empty queries.
- Query cache invalidation after structural changes.
- Native container and pointer-field adapter generation.
- Existing GridSearch and JobSystem test suites.

### Performance protocol

- Release x64, no debugger, fixed power mode, stable background load.
- Five independent processes per configuration.
- Report median process average plus frame p50 and p95.
- Keep warmup, measurement count, entity count, `dt`, and Sleep duration identical between configurations.
- Do not include Sleep in timed duration and do not invoke `KeepWorkersWarm`.
- Compare against an unchanged baseline binary where possible.

## Acceptance Criteria

On the target machine:

- Sleep C# `IJobChunk`: average at or below 0.95 ms and p95 at or below 1.10 ms.
- Continuous C# `IJobChunk`: no more than 5% regression from the 0.210 ms reproduced baseline.
- Heavy scenarios: no more than 3% regression.
- C#, C++, ISPC, Chunk, and Entity correctness checks all pass.
- Native exact-once, assist, dependency, copied-handle, combined-dependency, shutdown, and stress tests pass.
- Idle CPU returns close to zero after the bounded post-work spin and does not remain active through the 16 ms Sleep interval.
- No benchmark-only prewake or keep-warm call is required.

If the latency target is missed but diagnostics show scheduler and wake overhead have been reduced below a material threshold, the result is recorded rather than hidden by busy-waiting. Worker-count and Chunk-capacity stages are then evaluated one variable at a time. A full work-stealing-pool rewrite requires a separate design review.

## Rollout and Rollback

Implement behind one internal scheduler path while preserving the existing native exports. Keep commits separated into diagnostics, cooperative claim behavior, transpiler range integration, and optional tuning. Each commit must pass correctness tests before performance comparison.

The fixed-token implementation remains recoverable through version control; no runtime dual scheduler is required after acceptance. Worker-count and Chunk-capacity changes are separate commits so either can be reverted without reverting the cooperative executor.
