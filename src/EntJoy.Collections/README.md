# EntJoy.Collections

**中文** | [English](#entjoycollections-english)

EntJoy 的原生容器、分配器与安全检查层：`NativeArray<T>` / `NativeList<T>` / `UnsafeList<T>`，配套持久/临时分配器、页锁定内存登记、共享数据块，以及 JobSystem 并发访问所依赖的安全检查句柄与泄漏检测。

## 内容

| 类型 | 说明 |
| --- | --- |
| `NativeArray<T>` | 连续原生数组（`where T : unmanaged`，`IDisposable`）。`CreateView` / `FromExternalPtr`（后者 `pinned: true` 时自动登记到 `PinnedMemory`）、`GetSubArray`、`AsSpan`、`AsReadOnly`、静态 `Copy` 重载族、`GetAtomicSafetyHandle`。嵌套 `ReadOnly` 为只读视图。 |
| `NativeList<T>` | 可增长列表，构造时用 `NativeArrayOptions` 选择是否清零。 |
| `UnsafeList<T>` | 无安全检查的裸列表。 |
| `UnsafeUtility` | 低层内存工具：`Malloc` / `Free` / `MemCpy` / `MemSet` / `MemClear` / `ReadArrayElement` / `WriteArrayElement` / `AddressOf` / `AsRef` / `IsUnmanaged`。 |
| `Allocator` | 分配器种类枚举（`Invalid` / `None` / `Temp` / `TempJob` / `Persistent`，与 Unity 语义一致）。 |
| `TempAllocator` | 帧内临时分配器：大块（≥ 池阈值）走 free-list 分配池，帧末 `Reset` 归还池而非直接还给 OS；小块直通。 |
| `PersistentAllocator` | 持久分配器，附 `Stats` 统计。 |
| `PinnedMemory` | 页锁定内存登记表：`Register` / `Unregister`。用于 CUDA `cuMemAllocHost`、D3D12 页锁定堆等「CPU 可直写、GPU 可直读」的宿主内存，配合 `NativeArray<T>.FromExternalPtr` 让 GPU 调度识别该指针。 |
| `SharedBlob<T>` / `SharedBlobBuilder` | 不可变共享数据块 + 引用计数（跨实体共享配置、导航网格、动画曲线一类只读大块）。内存布局 `[BlobHeader(RefCount)][T]`；复制引用需显式 `Clone()`。 |
| `AtomicSafetyHandle` | 并行读写安全检查句柄（经 `NativeArray<T>.GetAtomicSafetyHandle()` 取出），是 JobSystem 拦截「job 运行期间主线程访问同一容器」的依据。 |
| `DisposeSentinel` | Debug 构建专用的泄漏检测：哨兵存于静态表（key = safety handle index），容器 `Dispose` 时注销；`DumpLeaks` 扫描并报告未释放容器。 |
| `JobIdentity` | 当前线程正在执行的 job 身份（`CurrentContext`，非 job 代码为 `default`）。容器写入点据此区分「同一 job 的并行 tile 写」（合法）与「不同 job 交叉写同一容器」（冲突）。 |

> 安全检查的行为细节（并行读写声明的登记时机、拦截窗口、`ENTJOY_SAFETY` / `ENTJOY_SAFETY_BOUNDS` 开关）见 [`docs/public/Runtime-Contracts-and-Known-Limitations.md`](https://github.com/tianqiyuan520/EntJoy/blob/main/docs/public/Runtime-Contracts-and-Known-Limitations.md)。

## 引用

```xml
<PackageReference Include="EntJoy.Collections" Version="1.0.0" />
```

- `net8.0` + `AllowUnsafeBlocks`；**纯托管**，不需要 CMake / MSVC / ISPC。
- 依赖：`EntJoy.Mathematics`。
- 命名空间：`EntJoy.Collections`。
- 版本由 `src/Directory.Build.props` 的 `EntJoyVersion` 统一决定，四个包 lockstep 发布，当前仅 win-x64。

## 相关

- [`EntJoy.Mathematics`](https://github.com/tianqiyuan520/EntJoy/blob/main/src/EntJoy.Mathematics/README.md) —— 本包的依赖。
- [`EntJoy.Jobs`](https://github.com/tianqiyuan520/EntJoy/blob/main/src/EntJoy.Jobs/README.md) —— 在其之上提供 JobSystem 与原生调度器。
- 根 [README](https://github.com/tianqiyuan520/EntJoy/blob/main/README.md#通过-nuget-使用) 的「通过 NuGet 使用」。

# EntJoy.Collections (English)

[中文](#entjoycollections) | **English**

EntJoy's native containers, allocators, and safety layer: `NativeArray<T>` / `NativeList<T>` / `UnsafeList<T>`, persistent and per-frame allocators, pinned-memory registration, shared blobs, plus the safety handles and leak detection that the JobSystem relies on.

## Contents

| Type | Notes |
| --- | --- |
| `NativeArray<T>` | Contiguous native array (`where T : unmanaged`, `IDisposable`). `CreateView` / `FromExternalPtr` (the latter registers with `PinnedMemory` when `pinned: true`), `GetSubArray`, `AsSpan`, `AsReadOnly`, the static `Copy` overload family, and `GetAtomicSafetyHandle`. The nested `ReadOnly` is a read-only view. |
| `NativeList<T>` | Growable list; `NativeArrayOptions` selects whether the buffer is cleared on construction. |
| `UnsafeList<T>` | Bare list without safety checks. |
| `UnsafeUtility` | Low-level memory helpers: `Malloc` / `Free` / `MemCpy` / `MemSet` / `MemClear` / `ReadArrayElement` / `WriteArrayElement` / `AddressOf` / `AsRef` / `IsUnmanaged`. |
| `Allocator` | Allocator kind enum (`Invalid` / `None` / `Temp` / `TempJob` / `Persistent`, matching Unity semantics). |
| `TempAllocator` | Per-frame temporary allocator: large blocks (≥ pool threshold) go through a free-list pool and are returned to the pool on frame `Reset` instead of to the OS; small blocks pass straight through. |
| `PersistentAllocator` | Persistent allocator, with `Stats`. |
| `PinnedMemory` | Registry of page-locked memory: `Register` / `Unregister`. For host memory that the CPU can write and the GPU can read directly (CUDA `cuMemAllocHost`, D3D12 pinned heaps); pair it with `NativeArray<T>.FromExternalPtr` so the GPU scheduler recognizes the pointer. |
| `SharedBlob<T>` / `SharedBlobBuilder` | Immutable shared blob plus refcount (config tables, navigation meshes, animation curves shared across entities). Layout: `[BlobHeader(RefCount)][T]`; copying a reference requires an explicit `Clone()`. |
| `AtomicSafetyHandle` | Safety handle for parallel reads/writes (obtained via `NativeArray<T>.GetAtomicSafetyHandle()`); it is what lets the JobSystem reject main-thread access to a container while a job owns it. |
| `DisposeSentinel` | Leak detection for Debug builds: the sentinel lives in a static table keyed by safety-handle index and is unregistered on container `Dispose`; `DumpLeaks` scans and reports containers that were never disposed. |
| `JobIdentity` | Identity of the job currently running on this thread (`CurrentContext`; `default` outside jobs). Container write paths use it to tell "parallel tile writes of the same job" (legal) from "different jobs writing the same container" (a conflict). |

> For the precise safety-check behavior (when parallel read/write claims are registered, the interception window, and the `ENTJOY_SAFETY` / `ENTJOY_SAFETY_BOUNDS` switches), see [`docs/public/Runtime-Contracts-and-Known-Limitations.md`](https://github.com/tianqiyuan520/EntJoy/blob/main/docs/public/Runtime-Contracts-and-Known-Limitations.md).

## Reference it

```xml
<PackageReference Include="EntJoy.Collections" Version="1.0.0" />
```

- `net8.0` + `AllowUnsafeBlocks`; **pure managed**, no CMake / MSVC / ISPC required.
- Dependencies: `EntJoy.Mathematics`.
- Namespace: `EntJoy.Collections`.
- The version comes from `EntJoyVersion` in `src/Directory.Build.props`; all four packages are released in lockstep and are win-x64 only.

## See also

- [`EntJoy.Mathematics`](https://github.com/tianqiyuan520/EntJoy/blob/main/src/EntJoy.Mathematics/README.md) — this package's dependency.
- [`EntJoy.Jobs`](https://github.com/tianqiyuan520/EntJoy/blob/main/src/EntJoy.Jobs/README.md) — builds the JobSystem and native scheduler on top of it.
- [Using NuGet Packages](https://github.com/tianqiyuan520/EntJoy/blob/main/README.md#using-nuget-packages) in the root README.
