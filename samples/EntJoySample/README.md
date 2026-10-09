# EntJoySample 样例分类

## `01_JobSystem`

- `01_JobSystem/CSharpJobManagedContextTest`：C# Job unmanaged raw-copy context 与 managed GCHandle context 性能差异。
- `01_JobSystem/IJobChunkScheduleOverheadTest`：`IJobChunk` 空任务、极轻 AddOne kernel 的 C# / C++ / ISPC 调度固定开销。
- `01_JobSystem/JobProfilerTest`：Job profiler 相关验证。
- `01_JobSystem/HeavyJob`：重计算 Job 压测。

## `02_IJobChunkECS`

- `02_IJobChunkECS/SimpleIJobChunkTest`：最小 `IJobChunk` 功能验证。
- `02_IJobChunkECS/IJobChunkMoveCompareTest`：100w 实体移动，C# / C++ / C++ fast / ISPC `IJobChunk` 对比。
- `02_IJobChunkECS/SpritesRandomMoveLikeTest`：SpritesRandomMove 风格的持续运动测试入口。

## `03_NativeTranspiler`

- `03_NativeTranspiler/MovementTest`：移动类 C# / C++ / ISPC Job 对比与验证。
- `03_NativeTranspiler/StaticMethodTest`：NativeTranspiler 静态方法翻译测试。
- `03_NativeTranspiler/ISPCMT`：ISPC 多线程相关测试。
- `03_NativeTranspiler/AutoSIMDTest`：AutoSIMD 基准套件（8 个用例 × 5 个 Job 变体 + LLVM IR 分析）。入口整体注释停用，见该目录 [README](03_NativeTranspiler/AutoSIMDTest/README.md)。
- `NativeTranspiler_Generated`：NativeTranspiler 生成物目录，保留在根目录，不作为手写样例移动。

## `04_NativeCollections`

- `04_NativeCollections/NativeListTest`：`NativeList<T>` 功能测试。
- `04_NativeCollections/NativeColletionStructTest`：Native collection 结构体场景测试。
- `04_NativeCollections/AtomicTest`：原子操作相关测试。

## `05_Algorithms`

- `05_Algorithms/GridSearch`：二维网格搜索、最近点、范围搜索等算法测试（当前整体注释停用）。

## `06_HotFieldHandle`

- `06_HotFieldHandle/HotFieldHandle`：HotField 可行性原型（class + `[HotFieldEntity]` → 字段级 SoA 存储）。

## `08_EntityRandomAccess`

- `08_EntityRandomAccess`：稀疏 Entity 随机访问开销基准（ComponentLookup 优化）。

## `09_ECS`

托管 ECS 的功能与契约验证。入口：`09_ECS/Program.cs`（当前整体注释停用）。

- `Demos/`：Change Tracking、Chunk 碎片整理、组件生命周期与内存、Observer、Reactive、关系场景、Shared Component、System 依赖与注册等演示。
- `Benchmarks/`：Enabled 过滤对比、EntityQuery 缓存、`IJobEntity` + Enabled、关系、Schedule 固定开销。
- `Jobs/`：关系、托管事件与 ISPC 事件的原生 Job 验证。

## `10_SIMD`

ISPC / AutoSIMD / 标量 C++ 三后端在 8 个用例上的性能与正确性对比（对照 C# 标量 oracle），外加非 8 倍数尺寸与特殊浮点值的压力测试。入口：`10_SIMD/Program.cs`（当前整体注释停用；由 `09_ECS` 的入口调用 `SimdCompareTest.Run()`）。

## `12_EntityNativeLookup`

可运行示例，入口：`12_EntityNativeLookup/Program.cs`（当前整体注释停用，需解除注释）。

- **定位表**：`EntityLocateB`（24B/实体 = chunk 基址 + 该 Archetype 列偏移表 + 槽位 + 版本）、
  默认多 chunk 下"组件列不连续"的事实、`EntityManager.VerifyLocateTable()` 逐实体一致性校验。
- **job-safe lookup**：`NativeComponentLookup<T>` / `NativeEntityLookup` 在**并行 job 内跨 chunk 随机访问**（托管 `IJobParallelFor`）；
  以及**原生内核版** `[NativeTranspile(Target = Cpp)]`。⚠ 原生 job 不能调用 `EntJoy.ECS` 的任何方法（实例方法与跨程序集静态方法都报 **NT004**）
  ⇒ 体内必须**内联字段运算**；手写 C++ 侧对等原语见 `src/NativeDll/NativeEntityLookup.h`。
- **ECB 批量**：`CreateEntitiesRange` + `SetComponentRange`（**回放期零托管分配**）+ `GetBatch`、
  `DestroyEntities(Entity*, count)` / `DestroyAllInArchetype`（ClearAll 快路径，O(chunk 数)）。
- **零分配重建**：清空后重建的 Id 全部来自回收池（非托管栈），销毁/创建路径无逐实体托管分配。

## `13_EnableBitMapNative`

原生 `IJobChunk` **读/写**逐组件 enable 位图（`GetEnableBitMapPtr<T>()`，数据面 `ChunkJobData.requiredEnableBitMaps`，与 `requiredComponentArrays` 同序），
并与托管侧 `IsComponentEnabled` / `WithEnabled` **逐实体比对**一致。示例把 `Archetype.ChunkCapacityOverride` 设为实体总数 ⇒ 单 chunk，
于是"chunk 内序号"即全局序号（多 chunk 下原生 job 拿不到全局序号）。入口：`EnableBitMapNativeDemo.cs`。

## `13_HotReload`

热重载**可跑样例**。入口：`13_HotReload/Program.cs`（**本工程缺省入口**）—— `Initialize()` + 一个 `while` 跑
`HotReloadAddJob`，并在安全点用 `NativeHotReloadWatcher` 检查**新构建的 `bin\NativeTranspiled.dll`**。
`HotReloadJob.cs` 的 `Execute` 体就是要手改的内核。

- 值由内核常量决定：`Values[index] += Delta + 1;` ⇒ `values[0]=101`，改成 `+ 2` ⇒ `102`（**不重启**）。
- 三条命令（一次性准备 + 常驻宿主 + 日常构建，**不用手动拷任何文件**）：

  ```powershell
  # ① 一次性：准备宿主目录（= 把整套搬过去，MSBuild 自动做，含 NativeTranspiled.layout.json）
  dotnet build samples\EntJoySample\EntJoySample.csproj -c Release -o artifacts\hotreload-demo
  # ② 常驻宿主（监视 bin；Ctrl+C 退出）
  artifacts\hotreload-demo\EntJoySample.exe
  # ③ 之后每次改完内核常量：零参数普通构建（VS 的 Build 也行）
  dotnet build samples\EntJoySample\EntJoySample.csproj -c Release
  ```

- ⚠ 宿主**必须**从 `artifacts\hotreload-demo` 跑：跑在 `bin` 里时它锁住 `bin`（`NativeTranspiled.dll`
  与正在运行的 `EntJoySample.dll`），普通构建就写不进去（MSB3021/MSB3027）。样例会检查这一点，直接拒启并打印 ① ②。
  同理**不能**"监视当前加载的那份 DLL"——那份文件正被自己锁着，构建写不进去 ⇒ 监视目录与构建输出目录必须分开。
- 迭代耗时（改一个内核常量 → 零参数构建）：本工程实测 **≈9.5 s**（托管 + 代码生成 ~6.2 s、原生 ~2.7 s），
  空转 ~1–2 s。⚠ 若这个数字突然回到 ~20 s，先查 `NativeTranspiler_Generated\*_ispc.h` 是否存在
  （缺失 ⇒ 31 次 ISPC 全量重跑；成因与修法见 [转译器契约 §10](../../docs/public/NativeTranspiler-Boundaries-and-Diagnostics.md)）。
- 端到端证据与机制：`tools/HotReloadProbe/p1-7-run.ps1`（`101→203`、数据保留）、`p4-run.ps1`（自动检测+耗时）、
  `docs/热重载设计.md` §6。

## `14_AutoSimdChunkWriteback`

`IJobChunk` + AutoSIMD 的"第二个组件整结构体回写"路径，三条路径逐实体比对：① 托管 C# 标量（基线）、
② `[NativeTranspile(Cpp)]` 标量 C++ 内核、③ `[NativeTranspile(Cpp, AutoSIMD)]` 真 SIMD 内核。
判据：② 与 ① 必须**逐实体完全相等**；③ 与 ① 允许 ≤1 ulp 级误差但不得有结构错位。入口：`AutoSimdChunkWritebackDemo.cs`。

## 入口切换约定

- 入口由 `EntJoySample.csproj` 的 `StartupObject` 决定：缺省 `EntJoySample.HotReload.Program`，用 `-p:ENTJOY_STARTUP=<全限定类型名>` 覆盖。
- 因此**允许多个非注释 `Main`**，直接 Build 即可（不传属性也不会 `CS0017`）。
- ⚠ 指定的类型必须有非注释的 `Main`，否则报 `CS1555`；该错误在增量构建下可能不出现（CSC 未重跑），必要时加 `--no-incremental`。
- 切换样例时，若目标 `Main` 处于注释态，需先把该文件的注释解除。

## C# Job 上下文约定

- 普通 `Schedule` 对用户只有一个入口，内部自动分流。
- Job struct 不含托管引用字段时，走 unmanaged raw-copy context 快路径。
- Job struct 含 `string`、数组、class 等托管引用字段时，走 managed GCHandle context 安全路径。
- 两条路径都是调度时拷贝语义；`Execute` 内修改 job 自身字段不会回写到调用方原始 struct。
- managed 路径只保证托管对象能被安全持有到 job 完成，不自动保证托管对象的多线程读写安全。
- C# Job callback 内异常会被捕获，native 侧正常 cleanup/complete，并在 `Complete()` 后由 C# 重新抛出。
