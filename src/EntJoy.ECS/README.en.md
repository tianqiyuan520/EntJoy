# EntJoy.ECS

[中文](https://github.com/tianqiyuan520/EntJoy/blob/main/src/EntJoy.ECS/README.md) · **English**

Archetype/Chunk ECS with entities, components, queries, Systems, relationships, events, and ECS jobs.

## Install

```powershell
dotnet add package EntJoy.ECS
```

Target `net8.0` and enable `AllowUnsafeBlocks`. This package includes the ECS generator and depends on Jobs → Collections → Mathematics. ECS applications only need this package.

## Usage notes

- `World` owns data lifetime. Complete jobs accessing affected data before structural changes.
- `IJobEntity` processes entities; `IJobChunk` accesses component columns. Scheduling extensions live in `EntJoy.ECS.JobSystem`.
- Ordinary C# usage needs no local native compiler. Transpilation needs CMake and MSVC or ClangCL; ISPC also requires Intel ISPC.
- Native assets currently target win-x64. All four packages share a version; see [releases and installation](https://github.com/tianqiyuan520/EntJoy/blob/main/README.en.md#releases-and-installation).

[Example](https://github.com/tianqiyuan520/EntJoy/blob/main/README.en.md#ecs-example) · [Samples](https://github.com/tianqiyuan520/EntJoy/tree/main/samples/EntJoySample/09_ECS) · [Runtime contracts](https://github.com/tianqiyuan520/EntJoy/blob/main/docs/public/Runtime-Contracts-and-Known-Limitations.md) (Chinese)
