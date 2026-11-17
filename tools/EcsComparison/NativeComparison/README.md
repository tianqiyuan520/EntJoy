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

Unity 的遍历与结构变更同源：都用 `Builds/PlayerIL2CPP` 的 IL2CPP 播放器（`compiler=il2cpp`）。Mono Player 与 Editor 口径的历史数据留作对照，不进表。

## 测量协议

与 `TraversalProtocol` 一致：warmup 512，12 samples × 1024 repetitions，每 sample 为总时长除以 repetitions，median 取排序后第 5、6 项均值；每次进程 2 轮、每轮方法顺序正逆交替。

**跨运行会漂移**：同一 arm 单次读数在 8.1–17.0 µs 之间跳过（Flecs 三组件最大差 49%），因此默认跑 3 次独立进程，每个 arm 汇总 3 × 2 = 6 个中位样本再取中位，CSV 里的 `spread` 是 6 个样本的 max/min。

进程级波动比轮内波动大一个量级：同一构建、同一臂，3 个进程的最小值可差 1.6 倍（EntJoy C++ `IJobChunk.Run` 100k×3：12.4 / 12.5 / 16.3 µs；同批托管臂最高到 22.9）。因此：

- **同一进程内成对交替**（`--ab-pair`，轮内 A/B 顺序反转）是唯一可用的比较方式；跨进程只允许聚合出 min / 中位，**不同进程的数字不得相除或相减**。
- 需要绝对位置时用 `min`：轮内最小值在同一构建内重复性最好（同格 3 个进程相差 ≤5%）；中位会被偶发慢轮抬走。
- 合并表里的 `median_us` 是"6 个进程-中位的聚合"，落在 min 与 max 之间，不能当成该配置的确定值；只有它的 `min_us` 与趋势可用。

.NET 侧的 JIT 配置也是口径的一部分：`DOTNET_TieredCompilation=0` 与默认（tiered + dynamic PGO）会改变托管臂的读数，100k×2 的 `foreach` 臂为 177.9 对 93.9 µs（1.9 倍），`IJobChunk.Run`（C#）不变。C++ 臂不受影响（12.5 对 12.5）。合并表里的 C# 组读数来自 tiering 关闭的会话，引用时须与同口径的会话比较。

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

### C++ 组（合并 CSV 视图）

这组是 `results/native-vs-dotnet-clang-o2-summary.csv` 的合并视图，直接由 `MATRIX_RESULT` 行聚合。它不对齐块数与编译参数，10 万档的进程级波动可达 1.6 倍；跨框架结论以下一节"对齐后"为准，1 千档见"1 千档不进对比"。

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

### C++ 组（对齐后）

10 万实体，单线程，`results/aligned-cpp-group-summary.csv`。每格给 min / 中位（单位 µs）；样本是该配置下全部轮次的中位，原生侧 6 个（2 轮 × 3 进程）、EntJoy 侧 21 个（7 轮 × 3 进程）。原始日志不入库（`results/*.log` 被忽略），CSV 是聚合结果，每行的 `source` 列写明产出命令。

| 形式 | 框架 | 路径 | 块数 | 编译器 / 档 / ISA | 10 万 × 2 | 10 万 × 3 |
| --- | --- | --- | --- | --- | --- | --- |
| Chunk | Flecs | `iter-span`（单 table） | 1 | clang 23.1.1 `-O2` SSE2 | 7.33 / 8.69 | 11.50 / 13.52 |
| Chunk | Flecs | `iter-span`（13 table） | 13 | clang 23.1.1 `-O2` SSE2 | 8.65 / 8.93 | 13.08 / 14.24 |
| Chunk | Flecs | `iter-span`（131 table） | 131 | clang 23.1.1 `-O2` SSE2 | 14.11 / 14.80 | 21.72 / 25.37 |
| Query | EnTT | `group-each` | — | clang 23.1.1 `-O2` SSE2 | 41.55 / 45.00 | 58.75 / 63.97 |
| Chunk | EntJoy | `IJobChunk.Run` 默认容量 | 105 / 131 | clang-cl 19.1.5 `-O3` AVX2 | 7.40 / 8.45 | 12.40 / 13.35 |
| Chunk | EntJoy | `IJobChunk.Run` 容量 65536 | 2 | clang-cl 19.1.5 `-O3` AVX2 | 6.20 / 6.70 | 11.70 / 12.75 |
| Chunk | EntJoy | `IJobChunk.Run` 默认容量 | 105 / 131 | clang-cl 19.1.5 `-O2` AVX2 | 7.40 / 8.40 | 12.40 / 13.70 |
| Chunk | EntJoy | `IJobChunk.Run` 容量 65536 | 2 | clang-cl 19.1.5 `-O2` AVX2 | 6.30 / 6.80 | 12.30 / 12.90 |
| Chunk | EntJoy | `IJobChunk.Run` 默认容量 | 105 / 131 | clang-cl 23.1.1 `-O2` SSE2 | 10.00 / 10.90 | 16.30 / 17.20 |
| Chunk | EntJoy | `IJobChunk.Run` 容量 65536 | 2 | clang-cl 23.1.1 `-O2` SSE2 | 7.60 / 8.20 | 12.30 / 12.70 |

每格做的对齐动作：

- Flecs 侧块数是存储形态，不是开关：单 table 是 1 块（原生形态），多块只能靠把实体分散到多张表（`--flecs-tables=N`，每张表各自分配 A/B/C 列）。131 块因此同时带上逐 table 查询开销与列不连续两项代价，是"131 块"的上界。
- EntJoy 侧用 `Archetype.ChunkCapacityOverride` 改块容量：默认容量给 105 块（2 组件）/ 131 块（3 组件），容量 65536 给 2 块。容量上限是 `EntityIndexInWorld.PackedSlotLimit = 1<<16`，10 万实体做不出 1 块。
- 编译器档：`-O3` 行是出厂构建（见"编译政策"），`-O2` 行是同一份转译源码重编；SSE2 行用 clang 23.1.1 与 Flecs/EnTT 同版本、同 `-O2`、去掉 `/arch:AVX2`。
- 未对齐项：EntJoy 的读数是托管进程驱动原生块循环，Flecs/EnTT 是纯原生进程；目标三元组也不同。

结论：

- 同按出厂默认（Flecs 1 块、EntJoy 105/131 块），10 万×3 是 11.50–13.52 对 12.40–13.35，两者重叠；合并表里的 11.36 对 16.63（+46%）在本机同一构建的会话里是 +8%（min）到 −1%（中位），+46% 落在进程级波动区间内。
- 块数拉到同一档（131 对 131）时 Flecs 21.72–25.37、EntJoy 12.40–13.35，顺序反转。
- 编译参数按 Flecs 的档（`-O2` + SSE2）重编 EntJoy、块数用 2 块对齐 Flecs 的 1 块，10 万×3 是 12.30–12.70 对 11.50–13.52，差距 0–7%，小于本机的跨进程波动。
- 因此 C++ 组的排序由块数决定，不由循环体实现决定：Flecs 领先的量全部来自"10 万实体一张连续表"。

复现：

```powershell
# Flecs 块粒度：1 / 13 / 131 张表
.\native-comparison-clang-o2.exe --entities=100000 --rounds=2 --flecs-tables=131

# EntJoy 块容量：0=默认，65536=2 块（10 万实体最少 2 块）
.\bin\Release\net8.0\EcsComparison.exe --ab-pair chunk-job-cpp arch-chunk 7 65536 100000

# 转译产物重编：按 build/…/Release 的编译行改 /clang:-O2 与 /arch，产出 DLL 覆盖到
# bin\Release\net8.0\NativeTranspiled.dll，再跑上面的 --ab-pair
```

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

C# 组内 EntJoy 的 query 路径领先 Arch 3.6 倍（31.12 对 111.46）；chunk 形式三家挤在 30.15–30.82，Unity Burst 慢 2.6 倍。1 千档列只作固定开销参考，不进排序。

## 块粒度

两边的"块"不在同一层，读表前先看这一节。

- Flecs 4.2.0 没有块尺寸参数：table 的列是一整块连续分配（`src/storage/table.c` 的列初始化按 `ecs_column_t` 向量生长，无按字节切块），一次 `ecs_query_next` 交付整张表，`iter.count` 等于表的实体数（`src/iter.c`）；公开 API 只有 `ecs_page_iter`（一个窗口，不会续页）与 `ecs_worker_iter`（把单次结果切区间）。10 万实体落在同一张表就是 1 块。
- EntJoy 的块容量由 `Archetype.CalculateOptimalChunkCapacity` 按字节预算算出，10 万实体在 2 组件下 105 块、3 组件下 131 块（每块 952 / 763 实体）。`ChunkCapacityOverride` 可改，上限 `1<<16` 槽位。
- 每块成本的量级：Flecs 1 千档 1 块 0.112 µs、131 块 2.061 µs，相减得每次交付 ≈ 15 ns；10 万档从 1 块到 131 块是 ×2 +70…93%、×3 +88…89%（中位 / min），多出来的部分远超 131 × 15 ns ≈ 2 µs，其余是列不再连续后的缺失。EntJoy 从默认 131 块降到 13 / 4 / 2 块，10 万×2 是 7.40 / 6.90 / 5.90 / 6.20（min），3 组件档的差异落在轮次波动内。
- 读法：**块数是这两个实现的容量选择，不是实现质量**。Flecs 用 1 块拿到 70–90% 的便宜，EntJoy 的默认容量则让它多付 15–25%（2 组件档）。

## 1 千档不进对比

1 千档测的是固定开销，不是遍历吞吐。

- 空 body 对照（`--job-overhead`，3 进程）：1 千档 EntJoy C++ `job-empty-cpp` 0.4 µs 对 `chunk-job-cpp` 0.5 µs；10 万档同一对照是 1.0 µs 对 13.4 µs。1 千档里 80–100% 的时间是固定开销。
- 块数在 1 千档无影响：容量 65536（1 块）0.4–0.5 µs，默认（2 块）0.5 µs。
- Flecs 1 千档 1 块 0.112 µs、EnTT `group-each` 0.387 µs，量级同样由各自的每次交付开销决定。
- 合并表里 1 千档同一臂的 6 个进程-中位从 1.756 到 22.91（13 倍），是进程级波动；单进程内取多轮最小值则是 `foreach` C# 臂 1.8–2.0 µs（tiering 关闭）与 1.0–1.1 µs（默认 tiering），3 进程 × 15 轮。

结论：1 千档从框架对比里拿掉，只在 `--job-overhead` 里当固定开销探针；跨框架比较从 10 万档读。

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

C++ 组（10 万实体，绝对读数 min / 中位，µs）。块数决定位置，逐格对齐动作见"C++ 组（对齐后）"：

| 场景 | Flecs 1 块 | Flecs 131 块 | EntJoy 默认块 | EntJoy 2 块 | EnTT |
| --- | --- | --- | --- | --- | --- |
| 10 万 × 2 | 7.33 / 8.69 | 14.11 / 14.80 | 7.40 / 8.45（105 块） | 6.20 / 6.70 | 41.55 / 45.00 |
| 10 万 × 3 | 11.50 / 13.52 | 21.72 / 25.37 | 12.40 / 13.35（131 块） | 11.70 / 12.75 | 58.75 / 63.97 |

EntJoy 默认块数改用 Flecs 的编译档（clang 23.1.1 `-O2` SSE2）重编：10 万×2 为 10.00 / 10.90，10 万×3 为 16.30 / 17.20；同档下 2 块是 7.60 / 8.20 与 12.30 / 12.70。

结构变更（C++ 组，ns/实体，10 万，`results/struct-vs-native-summary.csv`）：

| 操作 | Flecs | EnTT |
| --- | --- | --- |
| 创建实体 | 54.02 | **30.35** |
| 添加组件 | 44.67 | **10.48** |
| 移除组件 | 44.01 | **19.78** |
| 销毁实体 | **49.71** | 58.72 |

C# 组（相对该组最快者，只列 10 万档；1 千档不进排序）：

| 场景 | Arch | EntJoy | Unity |
| --- | --- | --- | --- |
| Query 10 万 × 2 | 3.58 | 1.00 | 3.00 |
| Query 10 万 × 3 | 2.40 | 1.00 | 2.32 |
| Chunk 10 万 × 2 | 1.00 | 1.02 | 2.60 |
| Chunk 10 万 × 3 | 1.00 | 1.07 | 1.87 |
| 创建实体 10 万 | 1.00 | 1.48 | 8.16 |
| 添加组件 10 万 | 1.00 | 1.19 | 3.23 |
| 移除组件 10 万 | 1.10 | 1.00 | 2.55 |
| 销毁实体 10 万 | 1.00 | 1.03 | 6.60 |

读法：

- **C++ 组**：按各自出厂默认（Flecs 1 块、EntJoy 105/131 块），10 万×3 是 11.50–13.52 对 12.40–13.35，两者重叠；合并表里的 +46% 在本机同一构建的会话里是 +8%（min）到 −1%（中位）。块数是决定位置的变量：131 块对 131 块时 Flecs 21.72–25.37、EntJoy 12.40–13.35，顺序反转；把 EntJoy 的编译档也降到 Flecs 的 `-O2` SSE2 后仍是 0–7%，小于跨进程波动。EnTT 用官方 owned `group` 在 41.55–63.97，比 Flecs 1 块慢 5–6 倍。
- **1 千档**：不进排序。它量的是固定开销（EntJoy C++ 空 body 与满 body 在 1 千档同为 0.4–0.5 µs），详见"1 千档不进对比"。
- **C# 组**：EntJoy 的 query 路径（31.12）领先 Arch 3.6 倍、Unity Burst 3 倍；chunk 形式三家挤在 30 附近，EntJoy 的 C# chunk（30.74）与 Arch（30.15）差 2%。这些读数来自 tiering 关闭的会话，换 JIT 配置会动（见"测量协议"）。
- **同框架跨语言**：EntJoy 的 C++ 转译路径比自家托管 chunk 路径快 3–4 倍（10 万×2：7.40–8.45 对 30.82，跨会话，只读量级）。
- **结构变更**：C++ 组 EnTT 拿添加、移除与创建，Flecs 拿销毁，量级差 1.2–4.3 倍；C# 组 Arch 拿创建、添加与销毁，EntJoy 拿移除。

口径：Arch / EntJoy 的托管路径是 .NET JIT（合并表来自 `DOTNET_TieredCompilation=0` 的会话），EntJoy 的 C++ 路径是 ClangCL（clang 19.1.5，实际生效 `-O3` + AVX2），Flecs / EnTT 是 clang 23.1.1 `-O2` SSE2，Unity 是 IL2CPP + Burst。框架间的位置只从 10 万档读，1 千档只作固定开销探针。
