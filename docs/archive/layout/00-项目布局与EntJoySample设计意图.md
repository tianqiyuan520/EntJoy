# 项目布局与 EntJoySample 设计意图

> 本文档描述当前仓库**布局**、各工程**角色**，以及 EntJoySample 的**设计意图与样例组织方式**。
> 技术细节（分配器 / 调度 / 托管开销）见 [gridsearch/](../../gridsearch/) 的 01–05 系列，历史快照见 [archive/](../README.md)。

---

## 1. 顶层项目布局

```
EntJoy/
├── bin/                 ← 构建输出（EntJoy.dll、EntJoySample.exe、NativeDll.dll …）
├── docs/                ← 设计文档（本文、gridsearch/ 场景分析、archive/ 历史；GPU offload 探索见 [10-GPU-Offload探索分析](../gpu-offload/10-GPU-Offload探索分析.md)）
├── external/cpp-taskflow/ ← Taskflow Git 子模块（原生执行器的兼容/A-B 后端）
├── src/
│   ├── EntJoy/          ← 托管层核心库（ECS、Query、JobSystem、Native Collections、数学）
│   ├── EntJoy.SourceGenerator/ ← C# Source Generator（IJobEntity、QueryBuilder、EntitySystem）
│   ├── NativeTranspiler/        ← C# → C++/ISPC 生成器与分析器（编译时 MSBuild Analyzer）
│   ├── NativeTranspiler.Tasks/  ← MSBuild 自定义任务：调 CMake 编译生成的原生代码
│   ├── NativeDll/              ← 原生运行时：C++ JobSystem、WorkerPool、Profiler、容器支持
│   ├── NativeDll.Tests/        ← 原生单元测试（CMake + C++ Test）
│   ├── EntJoySample/           ← 样例工程（本文 §3 详述）
│   └── Godot/                  ← Godot 4.4.1 集成工程（C#，复用同一套 NativeDll）
└── JobSystem.cpp        ← ⚠️ 根目录遗留副本（87eb7fd 时期，无任何构建引用，可删）
```

## 2. 各工程角色

| 工程 | 语言 | 角色 | 关键内容 |
|---|---|---|---|
| `src/EntJoy` | C# | 托管核心库 | ECS（Archetype/Chunk/Entity/Component/System/World）、Query、JobSystem（`JobScheduler`/`NativeJobScheduler`）、Collections（`NativeArray`/`NativeList`/`PersistentAllocator`） |
| `src/EntJoy.SourceGenerator` | C# (Roslyn) | 编译时代码生成 | `IJobEntity` 调度扩展、`QueryBuilder`、`EntitySystem` |
| `src/NativeTranspiler` | C# (Roslyn Analyzer) | C# → C++/ISPC 生成 | `CppJobGenerator`/`IspcGenerator`/`BindingsGenerator`、布局推导、SIMD 生成器 |
| `src/NativeTranspiler.Tasks` | C# (MSBuild task) | 构建编排 | `NativeCompileTask`：调 CMake/MSVC/ISPC，增量编译生成物 |
| `src/NativeDll` | C++ | 原生运行时 | `JobSystem.cpp`（调度）、`NativeWorkerPool.*`（worker 池）、`NativeContainers.h`、`NativeSIMD.h`/`SimdValue.h`、`Exports.*`（C ABI） |
| `src/NativeDll.Tests` | C++ | 原生测试 | `JobSystemTests`/`AssistLifetimeTests` |
| `src/EntJoySample` | C# | 样例/基准（本文 §3） | 01–05 分类样例，唯一的 EXE 工程 |
| `src/Godot` | C# (Godot.NET.Sdk) | 编辑器集成示例 | 复用 `EntJoy` + `NativeDll`，验证引擎内运行 |

### 2.1 构建链（一次 `dotnet build -c Release`）

1. 编译 `EntJoy` + 两个 Roslyn Generator（`EntJoy.SourceGenerator`、`NativeTranspiler`）。
2. `NativeTranspiler` 把标记了 `[NativeTranspile]` 的 C# Job 生成 C# bindings + C++/ISPC 到 `EntJoySample/NativeTranspiler_Generated/`。
3. `NativeCompileTask`（`NativeTranspiler.Tasks`）调 CMake → MSVC 编译 C++、ISPC 编译 SIMD kernel，产出 `NativeDll.dll`。
4. `NativeDll.dll` 复制到仓库根 `bin/`，与 `EntJoySample.exe` 一起运行。

> 生成物目录 `NativeTranspiler_Generated/` 是构建产物，`EntJoySample.csproj` 显式 `Compile Remove`，不应手工编辑。

## 3. EntJoySample 设计意图

### 3.1 分类编号：按能力域纵向组织

样例目录以 `NN_能力域/` 编号，**数字越大越接近业务/性能层**，每层独立一个能力域：

| 目录 | 能力域 | 验证什么 |
|---|---|---|
| `01_JobSystem/` | JobSystem 基础 | context 路径（unmanaged raw-copy vs managed GCHandle）、调度固定开销、重负载、Profiler |
| `02_IJobChunkECS/` | ECS + Chunk Job | 最小 IJobChunk、百万实体 C#/C++/ISPC 对比、持续运动场景 |
| `03_NativeTranspiler/` | 转译与 SIMD | C#→C++/ISPC 正确性、静态方法、ISPC 多线程、Auto-SIMD |
| `04_NativeCollections/` | 原生容器 | NativeList、容器作字段、原子操作 |
| `05_Algorithms/` | 算法基准 | GridSearch（最近点/范围查询） |
| `06_HotFieldHandle/` | 句柄层（OOP 访问面） | HotField 可行性原型：普通 class + `[HotFieldEntity]` 属性 → 字段级 SoA 存储（`HotStore`）+ int 索引 + `ref` 属性重定向，System（`IJobParallelFor`）消费同一存储；验证「OOP 游戏代码与 plain class 逐字节相同（无感）、机械部分零成本、OOD↔DOD 共享存储一致」 |

**设计意图**：新样例应放进**与之匹配的既有能力域**（复用构建链与后端），而不是在根目录随意新建；能力域本身不按"测试类型"（正确性/性能）划分，而按**技术栈分层**划分。

### 3.2 设计方式：每个样例目录 = `Program.cs`（入口）+ `XXX`（实现）

**约定（设计方式）**：每个样例目录内放两个角色文件——

```
NN_能力域/
└── 某样例名/
    ├── Program.cs        ← 入口：Main 里 NativeJobScheduler.Initialize() → new 样例().Run()
    └── XXX.cs            ← 实现：样例主体（××Sample.cs / ××Test.cs）
```

- **`Program.cs`**：只负责**入口与生命周期**（初始化调度器、构造并运行样例、清理）。本身几乎无逻辑。
- **`XXX`**：样例实际逻辑（构造数据、调度 Job、测量/校验、打印）。

**入口切换约定**：整个 `EntJoySample` 是一个 EXE，**同一时间只启用一个 `Program.Main`**。切换样例时：
1. 注释当前入口的 `Main`；
2. 取消目标样例目录 `Program.cs`（或含 `Main` 的文件）的注释；
3. 重新构建运行 `.\bin\EntJoySample.exe`。

### 3.3 现状与约定的偏差（需要留意）

- **活动入口在 `06_HotFieldHandle/Program.cs`**（遵循 §3.2 约定，`Main` 只做初始化 + 运行）。`05_Algorithms/GridSearch`（`TestGridSearch.cs` + `GridSearch2D.cs`）整体 `/* */` 注释停用（2026-08），设计见 [06-HotFieldHandle设计](../hotfield/06-HotFieldHandle设计.md)。
- **部分目录无 `Program.cs`**：`01/HeavyJob`、`01/JobProfilerTest`、`03/ISPCMT`、`03/StaticMethodTest`、`04/*` 的 `Main` 直接写在 `××Test.cs` 里（历史遗留，未统一到 Program+XXX 约定）。
- **`AutoSIMDTest/` 基准套件整体处于注释停用状态**（2026-08 工作树），README 标"待集成到主 Program.cs"，尚未集成。

### 3.4 全局约定（跨样例）

- **C# Job context 分流**：Job struct 无托管引用 → unmanaged raw-copy 快路径；含 `string`/数组/class → managed GCHandle 安全路径。两条路径都是调度时拷贝语义，`Execute` 内改字段不回写调用方。
- **性能样例**：必须 `-c Release`、不挂调试器、保持电源模式/后台负载一致。结果不固定记录单机数字（见根 README），只保留相对结论。
- **原生生成物**：`NativeTranspiler_Generated/` 由构建生成，改动样例 → 重新构建自动再生成，不手工编辑。
