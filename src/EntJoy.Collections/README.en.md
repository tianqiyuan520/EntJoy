# EntJoy.Collections

[中文](https://github.com/tianqiyuan520/EntJoy/blob/main/src/EntJoy.Collections/README.md) · **English**

Native containers, allocation, and Job access checks. Depends on EntJoy.Mathematics.

## Install

```powershell
dotnet add package EntJoy.Collections
```

Target `net8.0` and enable `AllowUnsafeBlocks`. No C++ toolchain is needed. ECS and Jobs install this dependency automatically.

## Contents and ownership

- `NativeArray<T>`: contiguous arrays; `NativeList<T>`: growable lists; `UnsafeList<T>`: unchecked lists.
- `Allocator`, `UnsafeUtility`, and allocators manage native memory. `SharedBlob<T>` stores reference-counted shared data.
- Dispose owned containers after use. Views do not own storage; keep data alive until jobs finish.
- Safety handles check task access conflicts. Callers ensure parallel indices within a job do not race.
- Use `Clone()` to retain shared Blob references. Owners of external pointers control their lifetime.

All four packages share a version; the current release target is win-x64. [Releases](https://github.com/tianqiyuan520/EntJoy/blob/main/README.en.md#releases-and-installation) · [Samples](https://github.com/tianqiyuan520/EntJoy/tree/main/samples/EntJoySample/04_NativeCollections) · [Safety contracts](https://github.com/tianqiyuan520/EntJoy/blob/main/docs/public/Runtime-Contracts-and-Known-Limitations.md) (Chinese)
