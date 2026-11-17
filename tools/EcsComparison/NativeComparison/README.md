# NativeComparison：Flecs / EnTT 对照臂

与 `tools/EcsComparison` 的 .NET 侧（EntJoy、Arch、Unity DOTS）共用 kernel、实体数、测量协议与输出格式，产出同一套 `MATRIX_RESULT` 行后可合并成一张表。结果按**语言**分组对比：C++ 与 C++、C# 与 C#。

## 编译政策

- C++ 臂一律 **clang**。主结果 `-O2`，`-O3` 只作敏感性。
- Flecs / EnTT：`clang 23.1.1`，`--target=x86_64-w64-windows-gnu --sysroot=C:/msys64/ucrt64`，`-O2`，不带 `-march`，即 SSE2 基线（`Export-NativeMatrix.ps1` 第 48–52 行）。
- EntJoy 的转译产物：CMake 生成工程，`NativeCompileTask` 优先选 ClangCL（`src/NativeTranspiler.Tasks/NativeCompileTask.cs` 第 151–154 行）。`build/CMakeCache.txt` 记录 `CMAKE_GENERATOR_TOOLSET:INTERNAL=ClangCL`，`CMakeCXXCompiler.cmake` 记录 `CMAKE_CXX_COMPILER_ID=Clang`、`19.1.5`、`VC/Tools/Llvm/x64/bin/clang-cl.exe`。生成目录给的参数是 `/O2 /Ob2 /Oi /arch:AVX2 /fp:fast` 后接 `/clang:-O3 /clang:-ffp-contract=fast /clang:-fno-unroll-loops`，尾部 `-O3` 覆盖 `/O2`，实际生效档位是 `-O3`；把同一份生成物改按 `/clang:-O0` 重编，100k×3 从 12.4 µs 涨到 296.5 µs，尾部 `-clang:` 参数确实决定档位。
- 所以两边的共同点只有 clang 家族：优化档 `-O3` 对 `-O2`、ISA `AVX2` 对 `SSE2`、目标三元组 `amd64-pc-windows-msvc` 对 `x86_64-w64-windows-gnu` 都不同。实测敏感性：同 ISA 下 `-O2` 与 `-O3` 差 ≤3%；AVX2 降到 SSE2 在 131 块档 +29%（×2 / ×3 同），2 块档 ×2 +22%、×3 0…5%。**不能用 `compiler=clang` 三个字断言两边同级。**
- **不要用 GCC 出结论**：同一份 `flecs_no_addons.c`，GCC 15.2 `-O2` 编出的 Flecs 比 clang 慢 3 倍以上（28.15 对 8.18 µs），排名会与 Arch 托管路径互换。
- `compiler` 与 `opt` 写进每条 `MATRIX_RESULT` 并参与合并分组。.NET 侧的 `MATRIX_RESULT` 不带这两个字段，`Export-NativeMatrix.ps1` 第 89–90 行按缺省填 `dotnet` / `jit`；EntJoy 的 C++ 转译行因此显示 `compiler=dotnet opt=jit`，该列不描述原生产物的编译参数。

## 语言归属

| 组 | 框架 / 路径 | 说明 |
| --- | --- | --- |
| **C++** | Flecs 4.2.0 | 纯 C++，clang -O2 |
| **C++** | EnTT 4.0.0 | 纯 C++，clang -O2 |
| **C++** | EntJoy 的 C++ 转译路径 | NativeTranspiler 生成 C++，ClangCL（clang 19.1.5）`-O3` + AVX2；`IJobEntity.Run` / `IJobChunk.Run` 的 `backend=Cpp` 行 |
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

EntJoy 的 C++ 转译行仍然由托管进程驱动：`Run` 的调度、ChunkJobData 表构建与完成等待都在托管侧，原生部分只有块循环。这一行的读数因此是"托管驱动 + 原生循环"，与 Flecs / EnTT 的纯原生路径不同层。

Unity 的遍历与结构变更同源：都用 `Builds/PlayerIL2CPP` 的 IL2CPP 播放器（`compiler=il2cpp`）。Mono Player（`Builds/PlayerMatrix`）与 Editor 口径不进表。

## 测量协议

与 `TraversalProtocol` 一致：warmup 512，12 samples × 1024 repetitions，每 sample 为总时长除以 repetitions，median 取排序后第 5、6 项均值；每次进程 2 轮、每轮方法顺序正逆交替。

**跨运行会漂移**：同一 arm 单次读数在 8.1–17.0 µs 之间跳过（Flecs 三组件最大差 49%），因此默认跑 3 次独立进程，每个 arm 汇总 3 × 2 = 6 个中位样本再取中位，CSV 里的 `spread` 是 6 个样本的 max/min。

进程级波动比轮内波动大一个量级：同一构建、同一臂，3 个进程的最小值可差 1.6 倍（EntJoy C++ `IJobChunk.Run` 100k×3：12.4 / 12.5 / 16.3 µs；同批托管臂最高到 22.9）。因此：

- **同一进程内成对交替**（`--ab-pair`，轮内 A/B 顺序反转）是唯一可用的比较方式；跨进程只允许聚合出 min / 中位，**不同进程的数字不得相除或相减**。
- 需要绝对位置时用 `min`：轮内最小值在同一构建内重复性最好（同格 3 个进程相差 ≤5%）；中位会被偶发慢轮抬走。
- 合并表里的 `median_us` 是"6 个进程-中位的聚合"，落在 min 与 max 之间，不能当成该配置的确定值；只有它的 `min_us` 与趋势可用。
- **两条路径的读数不能互比**：`--traversal-matrix` 每个臂新建 10 万实体世界（合并 CSV 与跨框架比较走这条），`--ab-pair` 在一个进程里复用同一个探针反复测（同框架内的相对影响走这条）。同一配置 100k×3 的 EntJoy C++ `IJobChunk.Run` 在矩阵路径是 15.6–20.9 µs、在 `--ab-pair` 是 12.4–13.4 µs。跨框架只允许用矩阵口径的数字。

.NET 侧的 JIT 配置也是口径的一部分：`DOTNET_TieredCompilation=0` 与默认（tiered + dynamic PGO）会改变托管臂的读数，100k×2 的 `foreach` 臂为 177.9 对 93.9 µs（1.9 倍），`IJobChunk.Run`（C#）不变。C++ 臂不受影响（12.5 对 12.5）。合并表里的 C# 组读数来自 tiering 关闭的会话，引用时须与同口径的会话比较。

## 构建

```powershell
# 默认：clang -O2，编译 + 3 次重复跑双侧 + 合并
.\Export-NativeMatrix.ps1

# clang -O3 敏感性对照
.\Export-NativeMatrix.ps1 -OptNumber 3

# 只合并已有 log
.\Export-NativeMatrix.ps1 -SkipBuild -SkipRun

# Unity 遍历臂：构建 IL2CPP Player，再逐档跑（每档单独一个进程，矩阵档位由 -matrixEntities/-matrixComponents 指定）
& $env:UNITY -batchmode -quit -nographics -projectPath ..\UnityComparison -executeMethod PlayerBuild.BuildWindowsIl2Cpp
& ..\UnityComparison\Builds\PlayerIL2CPP\PlayerIL2CPP.exe -batchmode -nographics `
  -matrixLog ..\..\results\unity-100000x2.log -matrixEntities 100000 -matrixComponents 2
# 4 档 × 3 进程的 log 一起并入：.\Export-NativeMatrix.ps1 -SkipBuild -SkipRun -UnityTraverseLogs <12 个路径>
```

Unity 的 Player 若在 `-batchmode` 下报 Success 而产物哈希不变，是 IL2CPP 缓存命中（输入未变），不必删 `Builds` 重来。

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

单线程，3 次独立进程取中位（.NET / 原生每次 2 轮共 6 个样本，Unity IL2CPP Player 每次单轮共 3 个样本），AMD Ryzen 7 8845H。单位 µs/次遍历。`results/native-vs-dotnet-clang-o2-summary.csv` 是合并视图：C++ / C# 行来自 2026-11-17 的会话，Unity 行来自同一轮的 IL2CPP Player（`Builds/PlayerIL2CPP`，3 次进程 × 4 档，各 3 个样本）。

### C++ 组（10 万，矩阵口径）

跨框架结论只从这张表读：两边由同一个矩阵 harness 产出（每个臂新建 10 万实体世界、3 进程 × 2 轮、每轮方法顺序正逆交替）。每格是 min / 中位（µs），来源 `results/aligned-cpp-group-summary.csv` 里 `harness=matrix` 的行。

| 形式 | 框架 | 路径 | 块数 | 编译器 / 档 / ISA | 10 万 × 2 | 10 万 × 3 |
| --- | --- | --- | --- | --- | --- | --- |
| Query | **Flecs** | `iter-span`（单 table） | 1 | clang 23.1.1 `-O2` SSE2 | **7.85 / 8.32** | **12.68 / 14.43** |
| Chunk | Flecs | `iter-span`（单 table） | 1 | clang 23.1.1 `-O2` SSE2 | 7.85 / 8.32 | 12.68 / 14.43 |
| Chunk | Flecs | `iter-span`（131 table） | 131 | clang 23.1.1 `-O2` SSE2 | 14.61 / 16.50 | 23.59 / 27.07 |
| Query | EntJoy | `IJobEntity.Run` 默认容量 | 105 / 131 | clang-cl 19.1.5 `-O3` AVX2 | 9.25 / 11.33 | 15.78 / 16.61 |
| Query | EntJoy | `IJobEntity.Run` 容量 65536 | 2 | clang-cl 19.1.5 `-O3` AVX2 | 6.85 / 7.33 | 13.35 / 14.17 |
| Chunk | EntJoy | `IJobChunk.Run` 默认容量 | 105 / 131 | clang-cl 19.1.5 `-O3` AVX2 | 9.16 / 10.56 | 15.64 / 20.93 |
| Chunk | EntJoy | `IJobChunk.Run` 容量 65536 | 2 | clang-cl 19.1.5 `-O3` AVX2 | 6.95 / 7.84 | 13.00 / 14.28 |
| Query | EnTT | `group-each`（owned view） | — | clang 23.1.1 `-O2` SSE2 | 43.68 / 50.87 | 69.42 / 72.23 |

Flecs 没有逐实体回调 API，`ecs_query_iter` 是块级实现，所以在 Query 与 Chunk 两行都出现。EnTT 的 `view.each` 逐实体查 sparse 表，10 万×3 是 261.7 µs，换成 owned `group` 后是 72.2 µs，这是它在该形式的真实最好成绩。

每格做的对齐动作：

- Flecs 侧块数是存储形态、不是开关：单 table 是 1 块（原生形态），多块只能把实体分散到多张表（`--flecs-tables=N`，每张表各自分配 A/B/C 列）。131 块因此同时带上逐 table 查询开销与列不连续两项代价，是"131 块"的上界。
- EntJoy 侧用 `Archetype.ChunkCapacityOverride` 改块容量：默认容量给 105 块（2 组件）/ 131 块（3 组件），容量 65536 给 2 块。上限是 `EntityIndexInWorld.PackedSlotLimit = 1<<16`，10 万实体做不出 1 块。
- 未对齐项：EntJoy 的读数是托管进程驱动原生块循环，Flecs / EnTT 是纯原生进程；目标三元组也不同。
- 编译档的敏感性在 `--ab-pair` 口径下单独测（共享探针，只看相对影响）：`-O2` 与 `-O3` 差 ≤3%；AVX2 降到 SSE2 后 131 块档 +29%、2 块档 ×2 +22%、×3 0…5%。

结论：

- **出厂默认**（Flecs 1 块、EntJoy 105/131 块）：×2 是 8.32 对 10.56（+27%），×3 是 14.43 对 20.93（+45%）。Flecs 领先。
- **块数是主因**：Flecs 1 块 → 131 表，×2 +98%、×3 +88%（中位）；EntJoy 131 块 → 2 块，×2 −26%、×3 −32%（同上，中位）。
- **各自能到的最少块数**（Flecs 1 块、EntJoy 2 块）：×2 是 8.32 对 7.84（EntJoy 快 6%），×3 是 14.43 对 14.28（打平）。差距 0–6%，小于本机跨进程波动。
- **131 块对 131 块**：Flecs 16.50 / 27.07 对 EntJoy 10.56 / 20.93，EntJoy 快 1.3–1.6 倍。Flecs 多块要连列连续性一起付，所以这一格是它的上界，不是等块数下的下界。
- 结论 1 与结论 3 的差就是"块数"这一个变量的量级：Flecs 的领先来自 10 万实体一张连续表，不是循环体实现。

复现：

```powershell
# 合并视图（原生 3 进程 + .NET 3 进程）
.\Export-NativeMatrix.ps1

# Flecs 块粒度：1 / 13 / 131 张表
.\native-comparison-clang-o2.exe --entities=100000 --rounds=2 --flecs-tables=131

# EntJoy 块容量（矩阵口径，与上面同一 harness）：0=默认，65536=2 块
..\bin\Release\net8.0\EcsComparison.exe --traversal-matrix --chunk-capacity=65536

# EntJoy 块容量（共享探针，只看相对影响）
..\bin\Release\net8.0\EcsComparison.exe --ab-pair chunk-job-cpp arch-chunk 7 65536 100000

# 转译产物重编：按 build/…/Release 的编译行改 /clang:-O2 与 /arch，产出 DLL 覆盖到
# bin\Release\net8.0\NativeTranspiled.dll，再跑上面的 --ab-pair
```
```

### C# 组

`results/native-vs-dotnet-clang-o2-summary.csv` 的 C# 行与 Unity 行（2026-11-17 会话，`DOTNET_TieredCompilation=0`），单位 µs。

| 形式 | 框架 | 路径 | 10 万 × 2 | 10 万 × 3 | 1 千 × 2 | 1 千 × 3 |
| --- | --- | --- | --- | --- | --- | --- |
| Query | **EntJoy** | `IJobEntity.Run`（C#） | **35.48** | **59.46** | 0.48 | 0.75 |
| Query | Unity | `SystemAPI.Query`（Burst） | 98.50 | 132.83 | **0.75** | **0.96** |
| Query | Arch | `InlineQuery` | 126.47 | 135.75 | 1.57 | 1.61 |
| Query | EntJoy | `foreach`（C#） | 230.93 | 154.39 | 3.50 | 2.20 |
| Query | Unity | `SystemAPI.Query`（托管臂） | 1304.19 | 1538.98 | 11.83 | 14.80 |
| Chunk | **Arch** | chunk `foreach` | **32.61** | 60.03 | 0.45 | 0.76 |
| Chunk | EntJoy | chunk `foreach`（C#） | 33.11 | **58.90** | 0.74 | 0.85 |
| Chunk | EntJoy | `IJobChunk.Run`（C#） | 34.89 | 63.54 | 0.45 | 0.74 |
| Chunk | Arch | `CachedQuery.foreach` | 37.90 | 62.04 | 0.41 | 0.59 |
| Chunk | EntJoy | `Archetype.ChunkSpan` | 57.87 | 59.60 | 0.59 | 0.54 |
| Chunk | Unity | `IJobChunk`（Burst） | 77.34 | 105.38 | 0.74 | 1.18 |

C# 组内 EntJoy 的 query 路径领先 Arch 3.6 倍（35.48 对 126.47）；chunk 形式 Arch 32.61 与 EntJoy 33.11 差 1.5%，`ChunkSpan` 与 `CachedQuery.foreach` 各偏一侧。Unity Burst 的 `IJobChunk` 在 77.34，比 Arch 慢 2.4 倍，块数是 782 对 100（见"块粒度"）。1 千档列只作固定开销参考，不进排序。

## 块粒度

两边的"块"不在同一层，读表前先看这一节。

- Flecs 4.2.0 没有块尺寸参数：table 的列是一整块连续分配（`src/storage/table.c` 的列初始化按 `ecs_column_t` 向量生长，无按字节切块），一次 `ecs_query_next` 交付整张表，`iter.count` 等于表的实体数（`src/iter.c`）；公开 API 只有 `ecs_page_iter`（一个窗口，不会续页）与 `ecs_worker_iter`（把单次结果切区间）。10 万实体落在同一张表就是 1 块。
- EntJoy 的块容量由 `Archetype.CalculateOptimalChunkCapacity` 按 16 KiB 预算算出（`Archetype.cs` 第 132–137 行，注释为"Unity 风格的小物理 Chunk"），10 万实测 2 组件 105 块、3 组件 131 块（每块 960 / 768 实体）；Arch 同期实测 1000 / 800 实体一块（100k 时 100 / 125 块），与 EntJoy 同量级。`ChunkCapacityOverride` 可改，上限 `1<<16` 槽位。
- Unity 的 `query.CalculateChunkCount()` 在 10 万给 782 块（约 128 实体/块），1024 给 8 块。成因是硬上限，不是字节预算：`Chunk.kChunkSize` 是 16 KiB（`Chunk.cs:347`），但 `TypeManager.MaximumChunkCapacity = 128`（`TypeManager.cs:667`）在 `EntityComponentStore.cs:2792` 取 `math.min`，128 永远先命中；`[MaximumChunkCapacity]` 只能往下调。所以 Unity 的块数是 Arch / EntJoy 的 6–7.8 倍，且无法对齐。
- 每块成本的量级（矩阵口径）：Flecs 1 千档 1 块 0.115 µs、131 块 2.06 µs，相减得每次交付 ≈ 15 ns；10 万档从 1 块到 131 表是 ×2 中位 +98%、×3 +88%，多出来的部分远超 131 × 15 ns ≈ 2 µs，其余是列不再连续后的缺失。EntJoy 131 块 → 2 块是 ×2 中位 −26%、×3 −32%。
- 读法：**块数是设计选择，不是实现质量**。EntJoy 的块按 16 KiB 目标对齐 Arch（960 / 768 对 1000 / 800 实体一块），Flecs 4.2 不分块，Unity 另有 128 上限。Flecs 用 1 块拿到 70–90% 的便宜，EntJoy 的 131 块是这条对齐线的结果。容量调到 65536 会让 10 万×2 从 10.56 降到 7.84（中位），但会离开对齐线，只作为分离"块数"变量的实验臂。
- C# 组 chunk 形式 Arch 与 EntJoy 差 1.5%、块数差 5%（100/125 对 105/131），是块规模对齐的直接结果；Unity 与这两家差 6–7.8 倍块数，那 2.4 倍差距里含块数，不能整段归给实现。

## 1 千档不进对比

1 千档测的是固定开销，不是遍历吞吐。

- 空 body 对照（`--job-overhead`，共享探针，3 进程）：1 千档 EntJoy C++ `job-empty-cpp` 0.4 µs 对 `chunk-job-cpp` 0.5 µs；10 万档同一对照是 1.0 µs 对 13.4 µs。1 千档里 80–100% 的时间是固定开销。
- 块数在 1 千档无影响：容量 65536（1 块）0.4–0.5 µs，默认（2 块）0.5 µs。
- 矩阵口径的量级：Flecs 1 块 0.115 / 0.157 µs（2 / 3 组件）、EntJoy C++ `IJobEntity` 0.613 / 0.880、`IJobChunk` 1.044 / 0.748、EnTT `group-each` 0.546 / 0.646。
- 合并表里 1 千档单臂的 spread 最大 5.2（EntJoy C++ `IJobChunk.Run` ×2：min 0.477、max 2.477），托管 `foreach` 臂是 2.0（2.175–4.324），都是跨进程波动。同一臂在单进程内取多轮最小值就稳定：`foreach` C# 臂 1.8–2.0 µs（tiering 关闭）、1.0–1.1 µs（默认 tiering），3 进程 × 15 轮（`--ab-pair`）。

结论：1 千档从框架对比里拿掉，只在 `--job-overhead` 里当固定开销探针；跨框架比较从 10 万档读。

## 结构变更

协议与 .NET 侧 `StructuralBench` 同构：25 轮，每轮内两侧只隔一次复位，复位一律在计时之外，按轮取最小。原生侧在 1024 / 10000 / 100000 三档都稳定到 0.5% 内（`results/struct-vs-native-summary.csv`，2026-11-15 会话，与遍历表不是同一轮）。单位 ns/实体。

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

C++ 组（10 万实体，矩阵口径，绝对读数 min / 中位，µs）。块数决定位置，逐格对齐动作见"C++ 组（10 万，矩阵口径）"：

| 场景 | Flecs 1 块 | Flecs 131 块 | EntJoy 默认块 | EntJoy 2 块 | EnTT |
| --- | --- | --- | --- | --- | --- |
| 10 万 × 2 | 7.85 / 8.32 | 14.61 / 16.50 | 9.16 / 10.56（105 块） | 6.95 / 7.84 | 43.68 / 50.87 |
| 10 万 × 3 | 12.68 / 14.43 | 23.59 / 27.07 | 15.64 / 20.93（131 块） | 13.00 / 14.28 | 69.42 / 72.23 |

EntJoy 默认块数改用 Flecs 的编译档（clang 23.1.1 `-O2` SSE2）重编，在 `--ab-pair` 口径下：10 万×2 为 10.00 / 10.90，10 万×3 为 16.30 / 17.20；同档下 2 块是 7.60 / 8.20 与 12.30 / 12.70。这一行只看相对影响，不与上表混比。

结构变更（C++ 组，ns/实体，10 万，`results/struct-vs-native-summary.csv`）：

| 操作 | Flecs | EnTT |
| --- | --- | --- |
| 创建实体 | 54.02 | **30.35** |
| 添加组件 | 44.67 | **10.48** |
| 移除组件 | 44.01 | **19.78** |
| 销毁实体 | **49.71** | 58.72 |

C# 组遍历（相对该组最快者，10 万，同一会话，1 千档不进排序）：

| 场景 | Arch | EntJoy | Unity |
| --- | --- | --- | --- |
| Query 10 万 × 2 | 3.56 | 1.00 | 2.78 |
| Query 10 万 × 3 | 2.28 | 1.00 | 2.23 |
| Chunk 10 万 × 2 | 1.00 | 1.02 | 2.37 |
| Chunk 10 万 × 3 | 1.02 | 1.00 | 1.79 |

结构变更（C# 组，2026-11-15 会话，三方同轮）：

| 操作 | Arch | EntJoy | Unity |
| --- | --- | --- | --- |
| 创建实体 10 万 | 1.00 | 1.48 | 8.16 |
| 添加组件 10 万 | 1.00 | 1.19 | 3.23 |
| 移除组件 10 万 | 1.10 | 1.00 | 2.55 |
| 销毁实体 10 万 | 1.00 | 1.03 | 6.60 |

读法：

- **C++ 组**：出厂默认下 Flecs 领先（10 万×3 是 14.43 对 20.93，+45%），领先的量来自"10 万实体一张连续表"：Flecs 1 块换 131 表要 +88%，EntJoy 131 块降到 2 块能省 32%。两边各按能到的最少块数比（Flecs 1 块、EntJoy 2 块），10 万×3 是 14.43 对 14.28，打平；×2 是 8.32 对 7.84，EntJoy 快 6%。强制 131 块对 131 块时反过来，EntJoy 快 1.3–1.6 倍。
- **1 千档**：不进排序。它量的是固定开销（EntJoy C++ 空 body 与满 body 在 1 千档同为 0.4–0.5 µs），详见"1 千档不进对比"。
- **C# 组**：EntJoy 的 query 路径（35.48）领先 Arch 3.6 倍；chunk 形式 Arch 32.61 与 EntJoy 33.11 差 1.5%，块数也差 5%。这些读数来自 tiering 关闭的会话，换 JIT 配置会动（见"测量协议"）。
- **同框架跨语言**：EntJoy 的 C++ 转译路径比自家托管 chunk 路径快 2–3 倍（10 万×3：15.64–20.93 对 58.90–63.54，同一会话的两侧，只读量级）。
- **结构变更**：C++ 组 EnTT 拿添加、移除与创建，Flecs 拿销毁，量级差 1.2–4.3 倍；C# 组 Arch 拿创建、添加与销毁，EntJoy 拿移除。

口径：Arch / EntJoy 的托管路径是 .NET JIT（`DOTNET_TieredCompilation=0`），EntJoy 的 C++ 路径是 ClangCL（clang 19.1.5，实际生效 `-O3` + AVX2），Flecs / EnTT 是 clang 23.1.1 `-O2` SSE2，Unity 是 IL2CPP + Burst。框架间的位置只从矩阵口径的 10 万档读，1 千档只作固定开销探针。
