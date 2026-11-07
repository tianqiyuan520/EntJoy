# EntJoySample

[中文](README.md) · **English**

Usage examples, correctness checks, and benchmarks. The default entry point is `TestGridSearch`.

## Navigation

| Directory | Contents |
| --- | --- |
| [01 JobSystem](01_JobSystem) | Managed contexts, scheduling costs, heavy work, Profiler, and access conflicts |
| [02 ECS jobs](02_IJobChunkECS) | Minimal Chunk jobs, million-entity movement, and backend comparisons |
| [03 NativeTranspiler](03_NativeTranspiler) | Movement, static methods, ISPC tasks, and [AutoSIMD](03_NativeTranspiler/AutoSIMDTest/README.en.md) |
| [04 Collections](04_NativeCollections) | NativeList, containers in structs, and atomics |
| [05 GridSearch](05_Algorithms/GridSearch) | Grid search, nearest points, and range queries |
| [06 HotField](06_HotFieldHandle) | Shared storage for object fields and SoA |
| [08 Random Access](08_EntityRandomAccess) | Entity random-access costs |
| [09 ECS](09_ECS) | Queries, change tracking, Systems, events, relations, Observers, and snapshots |
| [10 SIMD](10_SIMD) | C#/C++/AutoSIMD/ISPC correctness and performance |
| [12 Native Lookup](12_EntityNativeLookup) | Entity lookup, cross-Chunk access, and ECB bulk operations |
| [13 Enable Bitmap](13_EnableBitMapNative) | Component enable bitmaps and native jobs |
| [13 Hot Reload](13_HotReload) | Native job hot reload |
| [14 AutoSIMD Writeback](14_AutoSimdChunkWriteback) | SIMD writes across components |
| [16 Native Execution](16_NativeExecution/README.en.md) | Column views, combined dependencies, contexts, and layout |

## Build and entry points

Requires .NET 8, CMake, Visual Studio C++ tools, and ISPC on PATH. Clone with submodules, then run from the repository root:

```powershell
dotnet build samples/EntJoySample/EntJoySample.csproj -c Release
.\bin\EntJoySample.exe
```

Select another entry with `-p:ENTJOY_STARTUP=<fully-qualified-type>`; add `--no-incremental` to force recompilation. Some samples are commented out; enable their source and Main before running.

For example, run Native Execution:

```powershell
dotnet build samples/EntJoySample/EntJoySample.csproj -c Release --no-incremental -p:ENTJOY_STARTUP=EntJoySample.NativeExecution.Program
.\bin\EntJoySample.exe
```

`IJobChunkMoveCompareTest` also needs `-p:ENTJOY_SAMPLE_SET=movecompare`. The default AutoSIMD sample set and the move comparison set are mutually exclusive.

## Hot reload

Enable the entry in `13_HotReload/Program.cs`, then keep the running host separate from normal build outputs:

```powershell
dotnet build samples/EntJoySample/EntJoySample.csproj -c Release --no-incremental -p:ENTJOY_STARTUP=EntJoySample.HotReload.Program -o artifacts/hotreload-demo
.\artifacts\hotreload-demo\EntJoySample.exe
```

Modify the kernel and rebuild from another terminal:

```powershell
dotnet build samples/EntJoySample/EntJoySample.csproj -c Release -p:ENTJOY_STARTUP=EntJoySample.HotReload.Program
```

The host watches `bin/NativeTranspiled.dll`. Running from a separate directory avoids locking the output being replaced.

## Usage notes

- Benchmark in Release without a debugger, keeping thread counts, power mode, and background load consistent.
- Jobs are copied by value. Changes to job fields do not write back to the caller; callers ensure managed-reference thread safety.
- Read results or release data after completion. `NativeTranspiler_Generated/` contains build outputs.
- Native Lookup jobs use supported native access paths. A local Chunk index is not a global entity index.
- Enable Bitmap uses one large Chunk. Check each sample's source for indexing and performance assumptions.

[Guide](../../docs/public/Native-Jobs-Guide.md) · [Runtime contracts](../../docs/public/Runtime-Contracts-and-Known-Limitations.md) · [Translation boundaries](../../docs/public/NativeTranspiler-Boundaries-and-Diagnostics.md) (Chinese)
