# Native Job：怎么写、怎么配

> 适用对象：用 `[NativeTranspile]` 把 C# Job 转译为 C++/ISPC 并在原生调度器上运行的开发者。
> 相关文档：[NativeTranspiler：边界、诊断与回归防线](NativeTranspiler-Boundaries-and-Diagnostics.md)（诊断清单与已知边界）、[EntJoy 运行时契约与已知限制](Runtime-Contracts-and-Known-Limitations.md)（运行时契约）。
> 可运行参考：`tests/NuGetJobsConsumer`（Jobs-only，包消费）、`tests/NuGetConsumer`（ECS 侧，包消费）、`tools/NativeTranspilerFixture`（生成器回归夹具）、`samples/EntJoySample`（ISPC / AutoSIMD / 性能对比）。

---

## 1. 先选消费方式

| 方式 | 适用 | 前置 |
|---|---|---|
| **NuGet 包**（推荐仓库外项目） | `PackageReference` 一条引用 `EntJoy.ECS`（含 ECS）或 `EntJoy.Jobs`（只写 Job） | 写 `[NativeTranspile]` 需本机 **CMake + MSVC（或 ClangCL）**；ISPC 可选。不写 native job 则**零工具链** |
| **源码引用**（仓库内项目） | `<Import …\src\EntJoy.Jobs\msbuild\EntJoy.Jobs.props|.targets>` | 同上；另需 CMake/MSVC 构建 `NativeDll.dll`（或让它走包内预编译件） |

两种方式下 `[NativeTranspile]` 的**写法完全一致**；差别只在"分析器/任务/原生头与导入库从哪来"。

---

## 2. 怎么写

### 2.1 最小示例（数组 job）

```csharp
using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler;          // [NativeTranspile] 与 BackendTarget 由生成器注入到本编译单元

[NativeTranspile(Target = BackendTarget.Cpp)]
public struct AddJob : IJobParallelFor
{
    public NativeArray<int> Values;   // 字段必须 unmanaged（NativeArray/NativeList/裸指针/POD）
    public int Delta;

    public void Execute(int index)
    {
        Values[index] += Delta;
    }
}

// 调度：生成器为每个 [NativeTranspile] job 生成同名扩展方法（走 native），
//       非 [NativeTranspile] 的同名 job 走托管/原生 C# 回调路径。
new AddJob { Values = values, Delta = 1 }.Schedule(values.Length, 64).Complete();
```

要点：

- **只写 unmanaged 字段/参数/局部量**（组件是 `struct`，容器用 `NativeArray`/`NativeList`/裸指针）。
  托管类型（`string`、class、普通数组、委托、`switch` 表达式、`static readonly` 引用类型）会被**拦截并给出替代写法**（NT003/NT006），不会静默跳过。
- **不要用 `unchecked { }` 等未支持构造**：生成器会写出 `__ENTJOY_UNSUPPORTED_*` 标记，`NativeCompileTask` **拒绝编译**（构建失败，报错含文件与行号），避免"编过但算了个寂寞"。
- **调度 API 由生成器提供**：同一 job 在"托管执行"与"native 执行"下结果必须一致（`tests/NuGetConsumer` 里就有这种成对断言）。

### 2.2 六种形态对照

| Job 接口 | `Execute` 签名 | 调度入口（生成） | 是否需要 ECS |
|---|---|---|---|
| `IJob` | `void Execute()` | `.Schedule()` / `.Schedule(dependsOn)` | 否 |
| `IJobFor` | `void Execute(int index)` | `.Schedule(arrayLength, innerBatchCount)` | 否 |
| `IJobParallelFor` | `void Execute(int index)` | `.Schedule(arrayLength, innerBatchCount)` | 否 |
| `IJobParallelForBatch` | `void Execute(int startIndex, int count)` | `.Schedule(arrayLength, innerBatchCount)` | 否（仅 Cpp 后端） |
| `IJobChunk` | `void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)` | `.Schedule(query)` / `RunImmediate(query)` | **是** |
| `IJobEntity` | `void Execute(ref T0, in T1, …)`（逐实体） | `.Schedule(query)` | **是** |

- **前四种**（数组类）跑在裸 JobSystem 上：只依赖 `NativeArray` + length，**不引用 `EntJoy.ECS` 也能用**。
- **后两种**（ECS 类）按定义需要 `ArchetypeChunk` / `Entity` / `World`，因此必须引用 `EntJoy.ECS`。
  调度扩展方法位于命名空间 **`EntJoy.ECS.JobSystem`**，必须 `using`，否则会解析到不匹配的重载。
- ECS 类的调度可显式指定 World（多 World 隔离）：`NativeTranspiler.Bindings.NativeExports.Schedule_<Job>(ref job, query, dependsOn, world)`；
  数组类 job **没有** World 概念（也就不存在 world 参数）。

### 2.3 属性选项（全部可选项，均有默认值）

```csharp
[NativeTranspile(
    Target = BackendTarget.Cpp,          // Cpp(默认) | Ispc
    UseISPC_MT = false,                  // ISPC task 多线程；仅 Target=Ispc
    MathLib = IspcMathLib.fast,          // system | fast(默认) | @default；仅 Target=Ispc
    CppMathLib = CppMathLib.@default,    // @default(默认) | fast
    AutoSIMD = AutoSIMD.Disabled,        // Disabled(默认) | Enabled | Vectorize
    MathPrecision = SimdMathPrecision.Fastest, // Fastest(默认) | High | IEEE
    DisabledAutoRefresh = false)]
public struct MyJob : IJobParallelFor { … }
```

**后端与选项的兼容性**（写错会被生成器以 error 拦下，不会静默丢弃）：

| 选项 | 约束 | 诊断 |
|---|---|---|
| `Target = Ispc` + `AutoSIMD` | ISPC 不读 AutoSIMD（靠 foreach/gang 向量化） | NT019 |
| 非 Cpp 后端 + `MathPrecision` | 按 job 精度只写进 C++ TU 的 `#define` | NT020 |
| 非 Cpp 后端 + `CppMathLib = fast` | C++ 专用 | NT021 |
| 非 ISPC 后端 + `UseISPC_MT` | ISPC 专用 | NT022 |
| `AutoSIMD = Vectorize` | 仅 `IJobChunk`/`IJobEntity` 实现 | NT018 |
| `IJobParallelForBatch` | 仅 Cpp 且不带 AutoSIMD | NT025 |

**两个"能编但没用"的坑**（warning，明确告知不阻断）：`MathPrecision = High` 未实现（与 `IEEE` 产物逐字相同，NT023）；`AutoSIMD = Enabled` 在 `IJobParallelFor`/`IJobFor`/`IJob` 上实测 **比标量基线慢 ~10%**（NT024）。详见边界文档 §5。

### 2.4 混合 ECS 与 native

同一个 job 既能以托管方式运行、也能以 native 方式运行：**不带** `[NativeTranspile]` 的版本走 C# 路径，**带**的版本走 native。这是做 parity 校验最省事的方式（`tests/NuGetConsumer` 的 `[3b]/[3c]` 就是这么写的）。

---

## 3. 怎么配

### 3.1 MSBuild 属性

| 属性 | 作用 | 谁来设 |
|---|---|---|
| `EntJoyNativeDllDir` | 原生目录：源码模式 = `src\NativeDll`（含头 + `tasksys.cpp`）；包模式 = 包内 `build\native\`（头 + `tasksys.cpp` + `NativeDll.lib`） | 共享 props 自动设；手写接线时自己设 |
| `EntJoyPrebuiltNativeDir` | **非空即进入 prebuilt-native 模式**：不编译 NativeDll，改为链接该目录下的 `NativeDll.lib` | 包模式由 props 设；源码模式留空 |
| `EnableNativeCompile` | `false` 时跳过 CMake 原生编译（只生成 C++，不编 native） | 需要快速构建/CI 分阶段时手动传 |
| `EntJoyConsumeMode` / `EntJoyIsPrebuiltNative` | 由 props 按文件位置自动判定（仓库内=Source，包内=Package） | 一般不用手设 |

> ⚠ **必须声明 `CompilerVisibleProperty`**：生成器通过 `build_property.*` 读上述属性，
> 手写接线时要加 `<CompilerVisibleProperty Include="EntJoyNativeDllDir" />`（prebuilt 模式再加
> `<CompilerVisibleProperty Include="EntJoyPrebuiltNativeDir" />`），否则属性对生成器不可见 —— 症状是
> "生成的 CMakeLists 没变 / 走了仓库探测回退路径"。

包内 `buildTransitive/EntJoy.Jobs.props|targets` 已经把这些全部接好：`UsingTask`、`CompilerVisibleProperty`、
CMake 编译目标、以及把 `NativeDll.dll`/`NativeTranspiled.dll` 复制到输出目录。

### 3.2 C# 侧安全宏（与 native 无关，但常一起配）

`EntJoy.Collections` 在 **Debug** 定义 `ENTJOY_SAFETY`（句柄 + 边界检查）、**Release** 定义 `ENTJOY_SAFETY_BOUNDS`（仅边界）；
全关可 `-p:DefineConstants=` 覆盖，完整开启可 `-p:DefineConstants=ENTJOY_SAFETY`。

### 3.3 生成的 CMake 可配选项

生成的 `NativeTranspiler_Generated/CMakeLists.txt` 暴露：

| 选项 | 取值 | 默认 |
|---|---|---|
| `NATIVE_SIMD_LEVEL` | `AUTO` / `AVX2` / `AVX` / `SSE4` / `NEON` / `SCALAR` | `AUTO`（x86_64→AVX2，arm64→NEON） |
| `NATIVE_SIMD_MATH_PRECISION` | `1` Fastest / `2` High（未实现）/ `3` IEEE | `1` |
| `ENTJOY_ENABLE_SENTINEL` | `ON`/`OFF`：对齐 C# Debug 容器布局增 8B 字段 | `OFF`（当前 C# 侧无内嵌 sentinel 字段，保持 OFF） |

传参方式：`cmake -S NativeTranspiler_Generated -B build -DNATIVE_SIMD_LEVEL=AVX2`。正常构建走 MSBuild 任务，自动调用 CMake；
需要手动调优时可用生成的 `run_clangcl.bat`（ClangCL 工具链脚本）或自己 configure。

### 3.4 产物与目录布局

```
<项目>/
├─ NativeTranspiler_Generated/          # 生成物（不应手工编辑；已默认从编译中排除）
│  ├─ *.cpp / *.h                       # job wrapper、adapter、结构体头
│  ├─ *.ispc (+ *_ispc.h)               # 仅 Target=Ispc
│  ├─ CMakeLists.txt                    # 原生编译工程（源模式编 NativeDll+NativeTranspiled；prebuilt 模式只编后者）
│  ├─ run_clangcl.bat / run_ispc.bat    # 手动编译脚本
│  ├─ generator.stamp                   # 生成器内容哈希（陈旧门控用）
│  ├─ native_compile.hash               # 原生依赖内容哈希（增量跳过 CMake 用）
│  └─ build/Release/                    # NativeDll.dll + NativeTranspiled.dll
└─ bin/<Cfg>/net8.0/                    # 运行期需要 NativeDll.dll 与 NativeTranspiled.dll 同目录
```

包模式下的对应位置（`EntJoy.Jobs` 包内）：

```
runtimes/win-x64/native/NativeDll.dll     # 运行时（纯托管消费者零工具链）
build/native/                             # 链接套件：NativeDll.lib + *.h + tasksys.cpp
tools/NativeTranspiler.dll                # 分析器（由 props 显式 <Analyzer Include> 挂一次）
tools/NativeTranspiler.Tasks.dll          # MSBuild 任务
buildTransitive/EntJoy.Jobs.props|targets # 接线：UsingTask + 编译目标 + DLL 复制
```

### 3.5 工具链要求

| 场景 | 需要 |
|---|---|
| 纯 C#（不写 `[NativeTranspile]`） | .NET 8 SDK only |
| `[NativeTranspile(Target = Cpp)]` | + CMake、MSVC（或 VS 自带 ClangCL；缺 ClangCL 自动回退 MSVC） |
| `[NativeTranspile(Target = Ispc)]` | + Intel ISPC 在 `PATH`；**缺 ISPC 时 ISPC 后端的 job 会链接失败**（不会静默降级） |

平台：当前仅 **win-x64** 验证。

---

## 4. 排错速查

| 症状 | 先看什么 |
|---|---|
| `error CS0234/CS0246: EntJoy.ECS / World / ChunkJobData 不存在` | 这是"生成物耦合 ECS 但项目没引用 ECS"。看是否有 **NT030** warning 指名了具体泄漏符号；若只是你自己用了 ECS 类 job，则 `PackageReference EntJoy.ECS` / 加 `ProjectReference` |
| 构建失败并报 `silently-degraded` / `__ENTJOY_UNSUPPORTED_*` | job 体内用了未支持的构造（如 `unchecked { }`）。报错含文件与行号；改成支持写法或换 job 边界 |
| `Native output missing (NativeTranspiled.dll)` 后 CMake 失败 | 看是否有 CMake/MSVC 错误；ISPC job 需要 `ispc` 在 `PATH` |
| 改了生成器但产物没更新（无提示） | 生成器陈旧门控 warning；按边界文档 §6 的配方删 `obj/<Cfg>` + `NativeTranspiler_Generated/build` + `native_compile.hash` 再构建 |
| 生成的 CMakeLists 路径不对/没更新 | 检查 `CompilerVisibleProperty` 是否声明了 `EntJoyNativeDllDir`（§3.1 的坑） |
| 数组 job 的 `Schedule_*` 找不到 `world` 参数 | 数组 job 无 World 概念；多 World 只对 ECS 类 job 有意义（§2.2） |

诊断编号全集与严重性见 [NativeTranspiler：边界、诊断与回归防线](NativeTranspiler-Boundaries-and-Diagnostics.md) §4。

---

## 5. 最小可运行参考

| 目标 | 看这个 |
|---|---|
| 只写 native job，不引用 ECS | `tests/NuGetJobsConsumer`（`PackageReference EntJoy.Jobs`，4 种数组形态 parity） |
| ECS + native（chunk / entity / 数组） | `tests/NuGetConsumer`（`PackageReference EntJoy.ECS`，原生 vs 托管成对断言） |
| 生成器回归夹具（含负向门控） | `tools/NativeTranspilerFixture`（`dotnet run` + `negative-check.ps1`） |
| ISPC / AutoSIMD / 性能对比 | `samples/EntJoySample/03_NativeTranspiler`、`02_IJobChunkECS/IJobChunkMoveCompareTest` |
| 一键验证包消费链路 | `powershell -File tests\NuGetConsumer\run.ps1` |
