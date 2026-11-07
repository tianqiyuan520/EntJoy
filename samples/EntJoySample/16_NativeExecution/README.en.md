# Native Execution

[中文](README.md) · **English**

Native job examples covering column views, combined dependencies, context ownership, and component layout.

## Run

Requires .NET 8, CMake, Visual Studio C++ tools, and ISPC on PATH. Run from the repository root:

```powershell
dotnet build samples/EntJoySample/EntJoySample.csproj -c Release --no-incremental -p:ENTJOY_STARTUP=EntJoySample.NativeExecution.Program
.\bin\EntJoySample.exe
```

Success prints `PASS: native execution examples.`. This sample checks results rather than measuring performance.

## Contents

| File | Examples |
| --- | --- |
| [NativeExecutionDemo.cs](NativeExecutionDemo.cs) | Contiguous/segmented column views, helpers, dependencies, bit flags, fixed loops, layout, and snapshots |
| [CopiedContextDemo.cs](CopiedContextDemo.cs) | Raw context copying, pooling, and cleanup |

## Ownership and boundaries

- `SampleColumn<T>` is a sample-owned slot view. The caller owns data and pointer tables; keep addresses, capacities, and slot order stable until jobs finish.
- All segments except the last use a fixed capacity. Slot numbers are not Entity identities; ECS manages cross-Archetype lookup and entity generations.
- `ScheduleCopiedRaw*` copies context bytes; embedded pointers still reference original data, which must remain valid until completion.
- Pair pooled contexts with `PooledJobContextCleanupPtr`; the scheduler reclaims them after submission.
- Explicit Batch keeps start/count boundaries; indexed Range callbacks process the entire supplied range.
- Raw C# callbacks execute C#. `[NativeTranspile]` and generated entry points execute native kernels.
- Component columns use CLR layouts for values without managed references. Snapshot restore checks layout and assembly identity; see runtime contracts for supported states.

The generator handles stable-pointer analysis, inlining, bounded loop optimizations, and SIMD semantic checks. Inspect generated code and machine code to verify vectorization. Pooling does not imply zero allocation throughout the framework.

## Diagnostics

Before starting the process, set `ENTJOY_DIAG_NATIVE_BIND=1` to inspect bindings or `ENTJOY_MAIN_ASSIST=0` to observe worker-only execution. See [flags](../../../docs/public/Gates-and-Flags.md) for other settings.

[Runtime contracts](../../../docs/public/Runtime-Contracts-and-Known-Limitations.md) · [Translation boundaries](../../../docs/public/NativeTranspiler-Boundaries-and-Diagnostics.md) · [C++/AutoSIMD](../../../docs/public/Cpp-AutoSIMD-Translation.md) (Chinese)
