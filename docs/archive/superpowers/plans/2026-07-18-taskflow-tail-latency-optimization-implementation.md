# Taskflow Tail Latency Optimization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce EntJoy partition-scheduler tail work, reuse batch metadata, expose Taskflow boundary latency, and add an isolated native WorkerPool A/B backend while keeping Taskflow as the default.

**Architecture:** Taskflow continues to receive one coarse drain task per local partition. Workers and `Complete()` share a bounded heaviest-victim selector and common tile executor; pooled `BatchStorage` owns the batch and its aligned buffers. `SubmitBatch` publishes common assist state and dispatches the batch to exactly one backend selected once during initialization.

**Tech Stack:** C++20, Taskflow, Windows/MSVC native threads and condition variables, CMake native tests, C#/.NET 9 diagnostics export.

## Global Constraints

- Keep Taskflow as the default backend and reuse its process-level Executor.
- Do not create native WorkerPool threads unless `ENTJOY_JOB_BACKEND=native` is selected before `Scheduler::Initialize`.
- A batch is consumed by exactly one backend.
- Preserve `Complete()` assist ownership and assist-reader lifetime safety.
- Keep `workerCap` as a participant cap, not a claim about exact unique Taskflow worker wakeups.
- Append exported statistics fields; do not reorder existing ABI fields.
- Do not modify ECS chunk capacity, component layout, ISPC kernels, thread affinity, or thread priority.
- Do not use Codex-machine absolute runtime as a performance acceptance threshold.

## File Map

- Modify `src/NativeDll/JobSystem.cpp`: victim selection, storage pool, timing probes, backend dispatch.
- Modify `src/NativeDll/JobSystem.h`: backend enum/test hooks and appended native statistics.
- Create `src/NativeDll/NativeWorkerPool.h`: persistent experimental executor interface.
- Create `src/NativeDll/NativeWorkerPool.cpp`: token queue, worker lifecycle, and completion callback.
- Modify `src/NativeDll/NativeDll.vcxproj`: compile the experimental executor into the DLL.
- Modify `src/NativeDll.Tests/CMakeLists.txt`: compile WorkerPool sources into native tests.
- Modify `src/NativeDll.Tests/JobSystemTests.cpp`: exact-once, stealing, pool, timing, and backend integration tests.
- Create `src/NativeDll.Tests/NativeWorkerPoolTests.cpp`: focused executor lifecycle and token tests.
- Modify `src/NativeDll/Exports.h` and `src/NativeDll/Exports.cpp`: append and copy statistics.
- Modify `src/EntJoy/JobSystem/NativeJobScheduler.cs`: append managed interop/statistics fields.
- Modify `src/EntJoySample/02_IJobChunkECS/IJobChunkMoveCompareTest/IJobChunkMoveCompareSample.cs`: print measured fields and preserve the user's current N/A(Taskflow) correction.

---

### Task 1: Bounded heaviest-victim stealing

**Files:**
- Modify: `src/NativeDll/JobSystem.cpp`
- Modify: `src/NativeDll/JobSystem.h`
- Test: `src/NativeDll.Tests/JobSystemTests.cpp`

**Interfaces:**
- Produces: `VictimSnapshot SelectHeaviestVictim(BatchState*, uint32_t excludedSlot) noexcept`.
- Produces stats: `victimScans`, `stealEmptyExits` appended to `JobSystemStatsSnapshot`.

- [ ] **Step 1: Write failing exact-once and bounded-attempt tests**

Add a table-driven test scheduling `1, 2, 7, 8, 31, 32, 100` one-item chunks with `workerCap=8`, asserting every item is hit once. Add a 31-tile balanced run assertion that actual `stealAttempts` stays below the old all-victim baseline of `partitionCount * (partitionCount - 1)` per batch and that `victimScans >= stealAttempts`.

- [ ] **Step 2: Run tests and verify RED**

Run:

```powershell
cmake -S src/NativeDll.Tests -B src/NativeDll.Tests/build
cmake --build src/NativeDll.Tests/build --config Release --target JobSystemTests
& src/NativeDll.Tests/build/Release/JobSystemTests.exe
```

Expected: compilation fails because the new statistics fields do not exist.

- [ ] **Step 3: Implement the minimal selector and bounded retry loop**

Implement a read-only scan that selects the largest `back-front` snapshot. Worker behavior is: drain local, select one victim with at least two remaining tiles, attempt half-steal once, retry victim selection at most once after a failed CAS, then exit. Assist uses the same selector and retains the single-tile `TryTakeLocal` fallback. Count scans separately from actual half-steal calls.

- [ ] **Step 4: Run the native tests and verify GREEN**

Run the commands from Step 2. Expected: all tests print `PASS` and exit 0.

---

### Task 2: Pooled BatchStorage ownership

**Files:**
- Modify: `src/NativeDll/JobSystem.cpp`
- Modify: `src/NativeDll/JobSystem.h`
- Test: `src/NativeDll.Tests/AssistLifetimeTests.cpp`
- Test: `src/NativeDll.Tests/JobSystemTests.cpp`

**Interfaces:**
- Produces: internal `BatchStorage* AcquireBatchStorage(uint32_t tileCapacity, uint32_t partitionCapacity)`.
- Produces: internal `void ReleaseBatchStorage(BatchStorage*) noexcept` and `void ClearBatchStoragePool() noexcept`.
- Produces stats: `batchStorageCreated`, `batchStorageReused`, `batchStorageReturned`, `batchStorageDropped`.

- [ ] **Step 1: Write failing reuse and lifecycle tests**

Schedule and complete two sequential 31-tile batches after resetting statistics. Assert one or more storage creations, one or more reuses, and `returned == created + reused` after both handles complete. Extend shutdown/assist stress coverage to assert cleanup is called exactly once and no pooled storage is returned before the blocked assist reader exits.

- [ ] **Step 2: Run both test executables and verify RED**

```powershell
cmake --build src/NativeDll.Tests/build --config Release --target JobSystemTests AssistLifetimeTests
& src/NativeDll.Tests/build/Release/JobSystemTests.exe
& src/NativeDll.Tests/build/Release/AssistLifetimeTests.exe
```

Expected: compilation fails on missing pool statistics.

- [ ] **Step 3: Implement BatchStorage and a bounded pool**

Make `BatchStorage` own `BatchState`, `ExecutionTile[]`, and cache-line-aligned `LocalPartition[]`. Acquire the best-fit idle object under a short mutex; grow buffers only while the object is exclusively acquired. Release clears every pointer, callback, counter, timing field and atomic cursor before pooling. Replace `new BatchState`, `new[] tiles`, `new[] partitions`, and `ReleaseBatch` in partition-backed Chunk/Entity scheduling. General old-range batches may initially use a zero-tile storage object so lifetime finalization remains one code path.

- [ ] **Step 4: Clear the storage pool during orderly shutdown**

After the selected executor has stopped and no in-flight task can call `ReleaseBatchStorage`, destroy all idle storage. Do not clear it before executor completion.

- [ ] **Step 5: Run tests and verify GREEN**

Run the commands from Step 2. Expected: both executables exit 0.

---

### Task 3: Taskflow boundary timing diagnostics

**Files:**
- Modify: `src/NativeDll/JobSystem.cpp`
- Modify: `src/NativeDll/JobSystem.h`
- Test: `src/NativeDll.Tests/JobSystemTests.cpp`

**Interfaces:**
- Adds aggregate fields: `submitToFirstWorkerEwmaNs`, `workerStartSpreadEwmaNs`, `lastTileToTopologyDoneEwmaNs`, `completeWakeToReturnEwmaNs`.
- Uses zero timestamp internally as “not recorded”; only valid ordered pairs update an EWMA.

- [ ] **Step 1: Write failing timing and reset tests**

Run a partition batch and assert submit-to-first-worker and last-tile-to-topology samples are recorded, timestamps cannot produce unsigned underflow, and reset clears all four aggregates. Use a controlled blocked first worker to ensure worker-start spread is measurable without asserting a machine-specific duration.

- [ ] **Step 2: Run JobSystemTests and verify RED**

Expected: compilation fails on missing diagnostic fields.

- [ ] **Step 3: Add one-shot monotonic timestamps**

Store nanoseconds from `steady_clock` in atomics on `BatchState`. Record publish immediately before backend submission, per-slot first entry using a slot-seen bitset/counter, last tile when `completedTiles.fetch_add` returns `tileCount-1`, and topology completion in the Taskflow callback. Update aggregate EWMAs only when both endpoints exist and end is not earlier than start.

- [ ] **Step 4: Instrument Complete wake-to-return**

Record the instant the waiting `Complete()` path observes `completed=true`, then update `completeWakeToReturnEwmaNs` immediately before returning. Do not manufacture this metric for already-completed handles that never waited.

- [ ] **Step 5: Run JobSystemTests and verify GREEN**

Expected: all timing/reset tests and existing trace lifecycle tests pass.

---

### Task 4: Experimental native WorkerPool backend

**Files:**
- Create: `src/NativeDll/NativeWorkerPool.h`
- Create: `src/NativeDll/NativeWorkerPool.cpp`
- Create: `src/NativeDll.Tests/NativeWorkerPoolTests.cpp`
- Modify: `src/NativeDll.Tests/CMakeLists.txt`
- Modify: `src/NativeDll/NativeDll.vcxproj`
- Modify: `src/NativeDll/JobSystem.h`
- Modify: `src/NativeDll/JobSystem.cpp`
- Test: `src/NativeDll.Tests/JobSystemTests.cpp`

**Interfaces:**
- `NativeWorkerPool::Start(uint32_t workerCount)` creates persistent workers.
- `NativeWorkerPool::Submit(void* batch, uint32_t slotCount, RunSlotFn runSlot, CompletionFn completion)` publishes exactly one token per slot.
- `NativeWorkerPool::Stop()` drains accepted work, joins workers, and rejects later submissions.
- Adds `ExecutionBackend { Taskflow, NativeWorkerPoolExperimental }` and stats `taskflowBatches`, `nativeBatches`, `invalidBackendSelections`.

- [ ] **Step 1: Write failing standalone WorkerPool tests**

Cover: no threads before `Start`; each submitted slot runs exactly once; completion fires once after all slots; multiple sequential batches work; `Stop` joins; submit-after-stop is rejected without executing callbacks.

- [ ] **Step 2: Add the test target and verify RED**

```powershell
cmake -S src/NativeDll.Tests -B src/NativeDll.Tests/build
cmake --build src/NativeDll.Tests/build --config Release --target NativeWorkerPoolTests
```

Expected: build fails because `NativeWorkerPool` does not exist.

- [ ] **Step 3: Implement the minimal persistent pool**

Use a mutex-protected FIFO of `{batch, slot}` tokens, a condition variable, and fixed `std::jthread` workers. A shared submission record owns an atomic remaining-slot counter and invokes completion exactly once when it reaches zero. `Stop` first prevents publication, drains accepted tokens, requests stop, notifies, and joins. Do not add affinity, priority, per-worker deques, or a second Tile stealing algorithm.

- [ ] **Step 4: Verify standalone WorkerPool tests GREEN**

Run `NativeWorkerPoolTests.exe`; expected exit 0.

- [ ] **Step 5: Write failing JobSystem backend-selection tests**

Initialize in fresh test processes/configured test cases with unset, `taskflow`, `native`, and invalid `ENTJOY_JOB_BACKEND`. Assert backend batch counters are mutually exclusive, invalid values fall back to Taskflow, and exact-once Tile accounting passes under both valid backends.

- [ ] **Step 6: Route SubmitBatch to one backend**

Read the environment only in `Initialize`. Construct only the selected executor. Extract common worker-finished handling from the Taskflow callback, then have both backends call it. Default and invalid selection initialize Taskflow only; native selection initializes WorkerPool only. General non-partition jobs must either use the selected backend through a slot adapter or remain explicitly Taskflow-only with initialization documented; do not silently construct both pools.

- [ ] **Step 7: Verify backend integration GREEN**

Build and run all three native test executables under default and native environment settings. Expected: all exit 0 and counters reconcile.

---

### Task 5: Export diagnostics and expose A/B output

**Files:**
- Modify: `src/NativeDll/Exports.h`
- Modify: `src/NativeDll/Exports.cpp`
- Modify: `src/EntJoy/JobSystem/NativeJobScheduler.cs`
- Modify: `src/EntJoySample/02_IJobChunkECS/IJobChunkMoveCompareTest/IJobChunkMoveCompareSample.cs`
- Test: `src/NativeDll.Tests/JobSystemTests.cpp`

**Interfaces:**
- Appends all Task 1–4 counters to the native and managed statistics structs in identical order and width.
- Sample prints unavailable executor-specific metrics as `N/A(Taskflow)` or `N/A(Native)`, never as measured zero.

- [ ] **Step 1: Write failing export-layout/stat-copy assertions**

Extend native tests to compare `GetStatsSnapshot` values with the exported snapshot for the new counters. Add managed compile-time use of every appended field through the sample formatter.

- [ ] **Step 2: Verify RED**

Build NativeDll tests and EntJoySample. Expected: missing export/managed fields cause compilation failure.

- [ ] **Step 3: Append native and managed fields**

Append fields without reordering existing members; copy each field in `Exports.cpp`; reset each native aggregate in `ResetStatsSnapshot`. Preserve the user's current local N/A(Taskflow) edits while extending the formatter.

- [ ] **Step 4: Build and verify all targets**

```powershell
cmake --build src/NativeDll.Tests/build --config Release
& src/NativeDll.Tests/build/Release/AssistLifetimeTests.exe
& src/NativeDll.Tests/build/Release/JobSystemTests.exe
& src/NativeDll.Tests/build/Release/NativeWorkerPoolTests.exe
dotnet build src/EntJoySample/EntJoySample.csproj -c Release --no-restore
```

Expected: every command exits 0.

- [ ] **Step 5: Run bounded stress and diagnostic smoke tests**

```powershell
1..20 | ForEach-Object {
  & src/NativeDll.Tests/build/Release/JobSystemTests.exe
  if ($LASTEXITCODE -ne 0) { throw "JobSystemTests iteration $_ failed" }
}
$env:ENTJOY_JOB_BACKEND='taskflow'
& bin/EntJoySample.exe
$env:ENTJOY_JOB_BACKEND='native'
& bin/EntJoySample.exe
Remove-Item Env:ENTJOY_JOB_BACKEND -ErrorAction SilentlyContinue
```

Expected: no crash, all verification lines are OK, Tile accounting reconciles, and exactly one backend batch counter is nonzero in each sample run. Absolute timing is recorded only for user-side comparison.

- [ ] **Step 6: Review scope and working tree**

Run `git diff --check`, inspect `git diff --stat`, verify the unrelated pre-existing C# change was preserved, and compare every design requirement with a passing test or explicit diagnostic output.
