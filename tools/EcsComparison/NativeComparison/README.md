# NativeComparison：Flecs / EnTT 对照臂

与 `tools/EcsComparison` 的 .NET 侧（EntJoy、Arch、Unity DOTS）共用 kernel、实体数、测量协议与输出格式，产出同一套 `MATRIX_RESULT` 行后可合并成一张表。结果按**语言**分组对比：C++ 与 C++、C# 与 C#。

## 编译政策

- C++ 臂统一用 **clang**，Flecs 与 EnTT 同编译器、同优化档，参数逐字一致。
- 主结果 `-O2`；`-O3` 作敏感性对照，两者差 3% 内。
- **不要用 GCC 出结论**：同一份 `flecs_no_addons.c`，GCC 15.2 `-O2` 编出的 Flecs 比 clang 慢 3 倍以上（28.15 对 8.18 µs），排名会与 Arch 托管路径互换。
- `compiler` 与 `opt` 写进每条 `MATRIX_RESULT` 并参与合并分组。

## 语言归属

| 组 | 框架 / 路径 | 说明 |
| --- | --- | --- |
| **C++** | Flecs 4.2.0 | 纯 C++，clang -O2 |
| **C++** | EnTT 4.0.0 | 纯 C++，clang -O2 |
| **C++** | EntJoy 的 C++ 转译路径 | NativeTranspiler 生成 C++，MSVC 编译；`IJobEntity.Run` / `IJobChunk.Run` 的 `backend=Cpp` 行 |
| **C#** | EntJoy 托管路径 | `backend=CSharp` 行，.NET 8.0.30 JIT |
| **C#** | Arch 1.2.8.1-alpha | 纯托管，JIT |
| **C#** | Unity DOTS | 源码是 C#；Burst 臂编译成原生，托管臂留在 IL2CPP，两个臂都归 C# 组 |

EntJoy 同时出现在两组：它在 C++ 组用转译路径，在 C# 组用托管路径。

## 遍历形式

同语言内还要分形式，不能把 query 和 chunk 混在一张表里。

| 框架 | 实体级回调（Query） | 块级（Chunk） |
| --- | --- | --- |
| EntJoy | `Query<T>()` foreach / `IJobEntity` | `IJobChunk` / `Archetype.ChunkSpan` / chunk foreach |
| Arch | `InlineQuery` | chunk `foreach` / `CachedQuery.foreach` |
| Unity DOTS | `SystemAPI.Query` | `IJobChunk` |
| Flecs 4.2 | 无回调 API | `ecs_query_iter` + `ecs_field` |
| EnTT 4.0 | `view.each` | 无公开 API |

EnTT 的 chunk 格与 Flecs 的 query 格输出 `MATRIX_UNAVAILABLE`，不编造数字。

## 运行时差异

EntJoy / Arch 在 .NET 8.0.30 上由 JIT 编译；Unity 走 IL2CPP Player 构建；Flecs / EnTT 由 clang 23.1.1 编成原生代码。**跨运行时只读量级与相对位置，比值不当精确结论。**

Unity 的遍历与结构变更同源：都用 `Builds/PlayerIL2CPP` 的 IL2CPP 播放器（`compiler=il2cpp`）。Mono Player 与 Editor 口径的历史数据留作对照，不进表。

## 测量协议

与 `TraversalProtocol` 一致：warmup 512，12 samples × 1024 repetitions，每 sample 为总时长除以 repetitions，median 取排序后第 5、6 项均值；每次进程 2 轮、每轮方法顺序正逆交替。

**跨运行会漂移**：同一 arm 单次读数在 8.1–17.0 µs 之间跳过（Flecs 三组件最大差 49%），因此默认跑 3 次独立进程，每个 arm 汇总 3 × 2 = 6 个中位样本再取中位，CSV 里的 `spread` 是 6 个样本的 max/min。

## 构建

```powershell
# 默认：clang -O2，编译 + 3 次重复跑双侧 + 合并
.\Export-NativeMatrix.ps1

# clang -O3 敏感性对照
.\Export-NativeMatrix.ps1 -OptNumber 3

# 只合并已有 log
.\Export-NativeMatrix.ps1 -SkipBuild -SkipRun
```

脚本默认取 `D:\llvm23\clang+llvm-23.1.1-x86_64-pc-windows-msvc`，走 `--target=x86_64-w64-windows-gnu --sysroot=C:/msys64/ucrt64`。手动编译：

```powershell
clang --target=x86_64-w64-windows-gnu --sysroot=C:/msys64/ucrt64 -O2 -c third_party\flecs\distr\flecs_no_addons.c -I third_party\flecs\distr -o flecs_clang_o2.o
clang++ --target=x86_64-w64-windows-gnu --sysroot=C:/msys64/ucrt64 -O2 -std=c++20 -DENTJOY_OPT_NUMBER=2 -I third_party\flecs\distr -I third_party\entt\src main.cpp flecs_clang_o2.o -ldbghelp -o native-comparison-clang-o2.exe
.\native-comparison-clang-o2.exe --rounds=2
```

`third_party/flecs` 与 `third_party/entt` 是各自仓库的 tag 源码，未入库：

```powershell
git clone --depth 1 --branch v4.2.0 https://github.com/SanderMertens/flecs.git third_party/flecs
git clone --depth 1 --branch v4.0.0 https://github.com/skypjack/entt.git third_party/entt
```

## 结果

单线程，3 次独立进程取中位（.NET / 原生每次 2 轮共 6 个样本，Unity IL2CPP Player 每次单轮共 3 个样本），clang 23.1.1 `-O2`（`results/native-vs-dotnet-clang-o2-summary.csv`，2026-11-15，AMD Ryzen 7 8845H）。单位 µs/次遍历。

### C++ 组

| 形式 | 框架 | 路径 | 10 万 × 2 | 10 万 × 3 | 1 千 × 2 | 1 千 × 3 |
| --- | --- | --- | --- | --- | --- | --- |
| Query | **Flecs** | `iter-span`（query 语义，块级实现） | **7.41** | **11.36** | **0.11** | **0.15** |
| Query | EntJoy | `IJobEntity.Run`（C++ 转译） | 9.76 | 16.54 | 0.90 | 0.52 |
| Query | EnTT | `group-each`（owned view，官方最快） | 42.62 | 59.04 | 0.39 | 0.54 |
| Query | EnTT | `view.each` | 115.56 | 222.57 | 1.14 | 2.19 |
| Chunk | Flecs | `iter-span` | 7.41 | 11.36 | 0.11 | 0.15 |
| Chunk | EntJoy | `IJobChunk.Run`（C++ 转译） | 8.36 | 16.63 | 1.17 | 0.52 |
| Chunk | EnTT | 无 chunk 级 API | — | — | — | — |

Flecs 没有逐实体回调 API，但它的 `ecs_query_iter` 是 query 遍历（块级实现），所以在 Query 与 Chunk 两张表里都出现。EnTT 的 `view.each` 每次遍历逐实体查 sparse 表，换成官方 owned `group` 后快 2.7–3.8 倍（115.56 → 42.62，222.57 → 59.04），这是它在该形式的真实最好成绩。

### C# 组

| 形式 | 框架 | 路径 | 10 万 × 2 | 10 万 × 3 | 1 千 × 2 | 1 千 × 3 |
| --- | --- | --- | --- | --- | --- | --- |
| Query | **EntJoy** | `IJobEntity.Run`（C#） | **31.12** | **57.33** | 1.76 | 1.95 |
| Query | Unity | `SystemAPI.Query`（Burst） | 93.46 | 133.06 | **0.73** | **0.99** |
| Query | Arch | `InlineQuery` | 111.46 | 137.36 | 4.61 | 3.93 |
| Query | EntJoy | `foreach`（C#） | 179.14 | 180.58 | 8.71 | 10.89 |
| Query | Unity | `SystemAPI.Query`（托管臂） | 1216.85 | 1453.97 | 12.67 | 14.28 |
| Chunk | **Arch** | chunk `foreach` | **30.15** | **55.15** | 0.60 | 2.00 |
| Chunk | EntJoy | `Archetype.ChunkSpan` | 30.74 | 58.80 | **0.35** | 0.57 |
| Chunk | Arch | `CachedQuery.foreach` | 30.77 | 55.58 | 0.43 | 0.48 |
| Chunk | EntJoy | `IJobChunk.Run`（C#） | 30.82 | 59.99 | 0.55 | 0.52 |
| Chunk | Unity | `IJobChunk`（Burst） | 78.40 | 102.90 | 0.73 | 0.91 |

C# 组内 EntJoy 的 query 路径领先 Arch 3.6 倍（31.12 对 111.46）；chunk 形式三家挤在 30.15–30.82，Unity Burst 慢 2.6 倍。

## 结构变更

协议与 .NET 侧 `StructuralBench` 同构：25 轮，每轮内两侧只隔一次复位，复位一律在计时之外，按轮取最小。原生侧在 1024 / 10000 / 100000 三档都稳定到 0.5% 内（`results/struct-vs-native-summary.csv`）。单位 ns/实体。

### C++ 组

| 操作 | Flecs | EnTT | Flecs / EnTT |
| --- | --- | --- | --- |
| 创建实体 | 54.02 | **30.35** | 1.78 |
| 添加组件 | 44.67 | **10.48** | 4.26 |
| 移除组件 | 44.01 | **19.78** | 2.22 |
| 销毁实体 | **49.71** | 58.72 | 0.85 |

EnTT 的 sparse set 让添加与移除只动自己的 storage、不迁移原型，这两项快 2–4 倍；favor 的代价是销毁要清三张 storage，反而慢 18%。Flecs 创建最慢，它没有一次调用建多组件实体的 API，等于两次原型迁移。

### C# 组

| 操作 | Arch | EntJoy | Unity | EntJoy / Arch |
| --- | --- | --- | --- | --- |
| 创建实体 | **23.96** | 35.41 | 195.63 | 1.48 |
| 添加组件 | **63.19** | 75.10 | 204.39 | 1.19 |
| 移除组件 | 74.86 | **67.88** | 173.28 | 0.91 |
| 销毁实体 | **27.18** | 28.11 | 179.49 | 1.03 |

C# 组内 Arch 拿创建、添加与销毁，EntJoy 只在移除上更快 9%；Unity 四格都慢 2.3–8.2 倍（相对 Arch）。

10 万档以外的档位：1 万档与 10 万档一致（EntJoy 创建 35.16、添加 72.10、移除 65.02、销毁 26.26）；1024 档被固定开销主导，Arch 创建涨到 131.15 ns/实体，不用于 per-entity 比较。

复现：

```powershell
# .NET 侧
& .\bin\Release\net8.0\EcsComparison.exe --struct 1024 25   | Out-File -Encoding utf8 ..\results\struct-1024.log
& .\bin\Release\net8.0\EcsComparison.exe --struct 10000 25  | Out-File -Encoding utf8 ..\results\struct-10000.log
& .\bin\Release\net8.0\EcsComparison.exe --struct 100000 25 | Out-File -Encoding utf8 ..\results\struct-20261115.log

# 原生侧（三档一次跑完）
.\native-comparison-clang-o2.exe --struct 1024,10000,100000 25 | Out-File -Encoding utf8 results\struct-clang-o2-tiers.log

# Unity 侧：先构建 IL2CPP Player，再跑
& $env:UNITY -batchmode -quit -nographics -projectPath ..\UnityComparison -executeMethod PlayerBuild.BuildWindowsIl2Cpp
& ..\UnityComparison\Builds\PlayerIL2CPP\PlayerIL2CPP.exe -batchmode -nographics `
  -structLog ..\results\struct-unity-il2cpp.log `
  -structEntities 1024,10000,100000 -structRounds 25

# 合并五家
.\Export-StructMatrix.ps1
```

## 场景速查

先在语言组内看位置，再跨组读量级。`—` 表示该框架没有这种形式。

C++ 组（相对该组最快者）：

| 场景 | Flecs | EntJoy（C++） | EnTT |
| --- | --- | --- | --- |
| Query 10 万 × 2 | 1.00 | 1.32 | 5.75 |
| Query 10 万 × 3 | 1.00 | 1.46 | 5.20 |
| Query 1 千 × 2 | 1.00 | 7.90 | 3.43 |
| Query 1 千 × 3 | 1.00 | 3.58 | 3.70 |
| Chunk 10 万 × 2 | 1.00 | 1.13 | — |
| Chunk 10 万 × 3 | 1.00 | 1.46 | — |
| Chunk 1 千 × 2 | 1.00 | 10.25 | — |
| Chunk 1 千 × 3 | 1.00 | 3.54 | — |
| 创建实体 10 万 | 1.78 | — | 1.00 |
| 添加组件 10 万 | 4.26 | — | 1.00 |
| 移除组件 10 万 | 2.22 | — | 1.00 |
| 销毁实体 10 万 | 1.00 | — | 1.18 |

C# 组（相对该组最快者）：

| 场景 | Arch | EntJoy | Unity |
| --- | --- | --- | --- |
| Query 10 万 × 2 | 3.58 | 1.00 | 3.00 |
| Query 10 万 × 3 | 2.40 | 1.00 | 2.32 |
| Query 1 千 × 2 | 6.32 | 2.41 | 1.00 |
| Query 1 千 × 3 | 3.95 | 1.97 | 1.00 |
| Chunk 10 万 × 2 | 1.00 | 1.02 | 2.60 |
| Chunk 10 万 × 3 | 1.00 | 1.07 | 1.87 |
| Chunk 1 千 × 2 | 1.25 | 1.00 | 2.10 |
| Chunk 1 千 × 3 | 1.00 | 1.09 | 1.91 |
| 创建实体 10 万 | 1.00 | 1.48 | 8.16 |
| 添加组件 10 万 | 1.00 | 1.19 | 3.23 |
| 移除组件 10 万 | 1.10 | 1.00 | 2.55 |
| 销毁实体 10 万 | 1.00 | 1.03 | 6.60 |

读法：

- **C++ 组**：Flecs 的 `iter-span` 在两种形式里都是第一（7.41）；EntJoy 的 C++ 转译路径紧随（9.76）；EnTT 用官方 owned `group` 后是 42.62，用 `view.each` 则是 115.56——选错遍历方式会差 2.7 倍。
- **C# 组**：EntJoy 的 query 路径（31.12）领先 Arch 3.6 倍、Unity Burst 3 倍；chunk 形式三家挤在 30 附近，EntJoy 的 C# chunk（30.74）与 Arch（30.15）差 2%。
- **同框架跨语言**：EntJoy 的 C++ 转译路径比自己的 C# 路径快 3.7 倍（8.36 对 30.82，chunk 形式）。
- **结构变更**：C++ 组 EnTT 拿两项、Flecs 拿销毁；C# 组 Arch 拿三项、EntJoy 拿移除。

口径：Arch / EntJoy 的托管路径是 .NET JIT，EntJoy 的 C++ 路径是 MSVC，Flecs / EnTT 是 clang 23.1.1 `-O2`，Unity 是 IL2CPP + Burst。
