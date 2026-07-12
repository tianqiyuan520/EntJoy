# JobSystem Assist Performance Results

## Environment

- Implementation commit: `89a71157cf983364b8d8ae221c66483d7e7d38b2`
- CPU: AMD Ryzen 7 8845H with Radeon 780M Graphics
- CPU topology: 8 cores / 16 logical processors
- Build: Release, run directly from PowerShell without an attached debugger
- Dataset: 100,000 positions / 100,000 queries
- Warmup / iterations: 2 / 1,000 per run
- GridSearch backend: ISPC `AssignAndCount`, ISPC `CopyHashIndex`, ISPC `ClosestPoint`

## GridSearch Results

Each value below is the mean reported by one 1,000-iteration process. The middle of the three runs is included to show run-to-run stability; it is not a per-iteration median.

| Metric | Before change | Run 1 | Run 2 | Run 3 | Middle run value |
|---|---:|---:|---:|---:|---:|
| Dispose native collections | 0.238 ms | 0.254 ms | 0.237 ms | 0.255 ms | 0.254 ms |
| Create/copy native collections | 0.547 ms | 0.503 ms | 0.470 ms | 0.499 ms | 0.499 ms |
| Bounding box | 0.134 ms | 0.119 ms | 0.123 ms | 0.128 ms | 0.123 ms |
| Hash assign/count | 1.073 ms | 0.127 ms | 0.121 ms | 0.130 ms | 0.127 ms |
| Prefix/fill | 0.285 ms | 0.270 ms | 0.267 ms | 0.271 ms | 0.270 ms |
| Element placement | 0.974 ms | 0.150 ms | 0.142 ms | 0.146 ms | 0.146 ms |
| Core build | 2.466 ms | 0.667 ms | 0.653 ms | 0.675 ms | 0.667 ms |
| Core query | 1.727 ms | 0.726 ms | 0.708 ms | 0.717 ms | 0.717 ms |
| Full build | 3.867 ms | 1.724 ms | 1.633 ms | 1.709 ms | 1.709 ms |
| Full query | 1.791 ms | 0.799 ms | 0.790 ms | 0.778 ms | 0.790 ms |
| Full build + query | 5.658 ms | 2.523 ms | 2.424 ms | 2.487 ms | 2.487 ms |

The first ten result indices were identical in all runs:

```text
74945 21160 15114 75587 37949 80702 88467 19643 11454 87386
```

## Acceptance

- Core query middle run value at or below 0.8 ms: **PASS**, 0.717 ms.
- Core build middle run value at or below 1.0 ms: **PASS**, 0.667 ms.
- First ten result indices unchanged: **PASS**.
- Native exact-once, caller assist, explicit batch, dependency, Chunk, copied handle, combined dependency, and shutdown tests: **PASS**.
- Native stress: **PASS** for 100 consecutive runs before adding the slower shutdown case. The later 500-process and 100-process audit commands exceeded the 120-second command limit without reporting a test failure; they are not claimed as completed runs.

## Commands

```powershell
dotnet build src/EntJoySample/EntJoySample.csproj -c Release --no-restore
1..3 | ForEach-Object { & .\bin\EntJoySample.exe }
cmake --build src/NativeDll.Tests/build --config Release --parallel
& src/NativeDll.Tests/build/Release/JobSystemTests.exe
```
