# Cooperative Chunk Executor Results

## Environment

- Commit tested: `ed8d7ab046251931ba0b1b94813b8c1def61bd86`
- CPU: AMD Ryzen 7 8845H with Radeon 780M Graphics
- CPU topology: 8 physical cores / 16 logical processors
- Windows power plan: Balanced (`381b4222-f694-41f0-9685-ff5bb260df2e`)
- Build: Release x64, run directly from PowerShell without an attached debugger
- Benchmark: 1,000,000 entities, 5 warmup frames, 100 measured frames, 16 ms Sleep interval
- Commands: `dotnet build src/EntJoySample/EntJoySample.csproj -c Release --no-restore --disable-build-servers`, native CMake Release build/test, 100 bounded native test processes, and five independent `bin/EntJoySample.exe` processes

## Correctness

- Managed Release build: PASS with 0 errors. The build retains 84 pre-existing analyzer/compiler warnings.
- Native named tests: PASS, including concurrent Complete, exhausted tickets, dependency cooperation, Chunk shutdown race, copied handles, and outstanding-work shutdown.
- Native lifecycle stress: PASS for 100/100 independent processes after the explicit batch lifetime and shutdown-drain fix.
- Generated binding assertion: PASS; native `IJobChunk` bindings use `ScheduleChunkRangeRaw` and `ChunkRangeFuncPtr`.
- C#/C++/ISPC Chunk and Entity parity: PASS in all five benchmark processes.
- Sleep expected-position check: PASS in all five benchmark processes.
- Benchmark source contains no `KeepWorkersWarm` call.

## Performance

| Run | Continuous C# Chunk avg | Sleep C# Chunk avg | Sleep C# Chunk p95 | Heavy C# Chunk avg |
|---|---:|---:|---:|---:|
| 1 | 0.131 ms | 1.193 ms | 2.135 ms | 22.581 ms |
| 2 | 0.143 ms | 1.061 ms | 1.696 ms | 22.367 ms |
| 3 | 0.146 ms | 1.104 ms | 1.849 ms | 23.247 ms |
| 4 | 0.133 ms | 1.241 ms | 2.129 ms | 22.882 ms |
| 5 | 0.149 ms | 1.206 ms | 2.038 ms | 23.032 ms |
| Process median | 0.143 ms | 1.193 ms | 2.038 ms | 22.882 ms |

Heavy medians were 20.602 ms C++ Chunk, 19.837 ms C++ Fast Chunk, 2.160 ms ISPC Chunk, 22.171 ms C# Entity, 20.233 ms C++ Entity, 2.133 ms ISPC Entity, and 2.623 ms ISPC MT Entity. Each improved relative to the same-session pre-acceptance checkpoint, so none exceeded the 3% regression budget.

## Scheduler Attribution

Values below are for the Sleep C# `IJobChunk` case. Latencies are EWMAs in microseconds.

| Run | Direct assist | Exhausted tickets | First main | First worker | Completion | Queue lock | Wait fallbacks |
|---|---:|---:|---:|---:|---:|---:|---:|
| 1 | 98 | 184 | 418.174 us | 30.323 us | 1158.540 us | 0.121 us | 81 |
| 2 | 49 | 203 | 344.439 us | 27.523 us | 968.489 us | 0.107 us | 88 |
| 3 | 50 | 235 | 466.502 us | 28.383 us | 1064.766 us | 0.108 us | 91 |
| 4 | 65 | 195 | 622.942 us | 32.843 us | 1344.906 us | 0.128 us | 92 |
| 5 | 70 | 207 | 426.507 us | 26.348 us | 1150.313 us | 0.112 us | 87 |

Workers begin claiming in roughly 26-33 us and queue-lock wait is about 0.1 us, so parked-worker notification and publication locking are not the remaining dominant cost. The publish-to-completion tail is approximately 0.97-1.34 ms and 81-92 frames per process fall back to blocking wait. This points to cold-memory execution and tail distribution rather than a missed wake-up.

## Evidence-Gated Tuning Investigation

After the five-process acceptance run, three isolated parameter studies were run with 5 warmup and 30 measured frames per process. These short runs are diagnostic only; they do not replace the 100-frame acceptance data above. Each row reports the median of three independent processes.

| Variable | Configuration | Continuous C# Chunk avg | Sleep C# Chunk avg | Sleep C# Chunk p95 | Heavy C# Chunk avg | Decision |
|---|---|---:|---:|---:|---:|---|
| Worker participation | cap 15 (production) | 0.191 ms | 1.036 ms | 1.181 ms | 26.088 ms | Retain |
| Worker participation | cap 12 | 0.159 ms | 1.018 ms | 1.187 ms | 28.221 ms | Reject: Heavy +8.2% |
| Worker participation | cap 8 | 0.432 ms | 1.074 ms | 1.304 ms | 37.552 ms | Reject: broad regression |
| ECS Chunk capacity | 768 KiB (production) | 0.191 ms | 1.036 ms | 1.181 ms | 26.088 ms | Retain |
| ECS Chunk capacity | 512 KiB | 0.718 ms | 1.060 ms | 1.281 ms | 24.510 ms | Reject: continuous regression |
| ECS Chunk capacity | 256 KiB | 0.596 ms | 1.045 ms | 1.303 ms | 23.826 ms | Reject: continuous regression |
| Complete wait policy | 256 pause iterations (production) | 0.191 ms | 1.036 ms | 1.181 ms | 26.088 ms | Retain |
| Complete wait policy | spin for 200 us | 0.157 ms | 1.027 ms | 1.165 ms | 23.739 ms | Reject: target still missed; burns caller CPU |
| Complete wait policy | spin for 500 us | 0.217 ms | 1.039 ms | 1.168 ms | 26.449 ms | Reject: no latency benefit; burns caller CPU |

The 200 us policy reduced C# Sleep wait fallbacks from roughly 27/30 frames to 7-11/30, and the 500 us policy reduced them to zero, without materially improving average or p95 completion. This is direct evidence that blocking-wait entry is a symptom of the approximately 1 ms execution tail, not its cause. The final source therefore retains cap 15, the 768 KiB default Chunk capacity, and the bounded 256-pause wait before parking.

## Acceptance

- Sleep average gate: **FAIL**, median 1.193 ms versus the required 0.950 ms.
- Sleep p95 gate: **FAIL**, median 2.038 ms versus the required 1.100 ms.
- Continuous regression gate: **PASS**, median 0.143 ms versus the maximum 0.2205 ms derived from the 0.210 ms baseline.
- Heavy regression gate: **PASS**, every backend remained below its same-session checkpoint and therefore below the 3% regression allowance.
- Correctness gate: **PASS**, including named tests, 100-process stress, generated binding selection, parity, and expected positions.

## Decision

Stopped at the evidence gate. The cooperative cursor, direct assist, range adapter, exact-once lifecycle, and shutdown drain are retained because they pass correctness and improve continuous/Heavy throughput, but this implementation is **not accepted as meeting the Unity-class Sleep target**.

The worker-count, Chunk-capacity, and wait-policy investigation is now complete and rejected all three as safe routes to the target. A further performance phase must optimize the movement data path or introduce an explicit fused synchronous execution API; it must not compensate with persistent frame-gap spinning, `KeepWorkersWarm`, a silent default worker-count change, or a Chunk-capacity change.
