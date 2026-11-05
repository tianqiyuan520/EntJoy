# 原生执行与列视图示例

本目录两个 `.cs` 文件当前以整段注释形式保存；使用时先去掉每行最外层的 `//`，保留代码内部的注释。入口为 `EntJoySample.NativeExecution.Program`，在仓库根目录运行：

```powershell
dotnet build samples/EntJoySample/EntJoySample.csproj -c Release --no-incremental -p:ENTJOY_STARTUP=EntJoySample.NativeExecution.Program
.\bin\EntJoySample.exe
```

需要 .NET 8、CMake 和 Visual Studio C++ 工具链。当前样例工程还包含 ISPC Job，需要 `ispc` 在 PATH 中。`--no-incremental` 确保切换 StartupObject 后重新编译入口。构建会运行 NativeTranspiler 与原生编译；末尾输出 `PASS: native execution examples.`，任一结果不符会抛异常并以非零退出。可将 `Release` 改为 `Debug` 验证字段布局。

## 使用内容

| 文件与方法 | 内容 |
|---|---|
| `NativeExecutionDemo.cs` / `ShowColumnViews` | 示例自有 `SampleColumn<T>` 连续/分段视图、静态寻址助手与末块定位 |
| `ShowDependencies` | `JobHandle.CombineDependencies(a, b)`，以及等待合并依赖后修改数据 |
| `NativeFeatureColumnJob` / `NativeFeatureMath` | `[NativeTranspile]` 作业调用带 `MethodImplOptions.AggressiveInlining` 的同程序集辅助函数 |
| `ShowOrdinaryCodeOptimizations` | 普通 C# 数组访问、4 次固定循环与 `ulong` 位开关；对照 Run/Schedule、零/非零开关、同址/分离数组的结果 |
| `CopiedContextDemo.cs` | `ScheduleCopiedRaw`、`ScheduleParallelForCopiedRaw`、`ScheduleParallelForRangeRaw`，以及 `RentJobContext` 与配套 cleanup |
| `ShowLayoutAndSnapshot` | 含 bool/byte 组件的 CLR 布局、普通多 Chunk 实体与 `TakeSnapshot` / `Restore` |

### 应用侧槽位视图的拥有关系

`SampleColumn<T>` 定义在示例中，按普通非托管结构体转译。数据和块指针表由调用方拥有。本例用三个 NativeArray 构成容量为 4、4、2 的分段存储；固定容量为 4，总长度为 10。索引 8 对应第三块的第一个元素。

块表使用栈内存，因此本方法在返回前完成所有 Job。Job 完成前，数据地址、块表内容和容量保持稳定；给视图赋 default 只清空描述。原生 Job 通过同程序集静态函数 `SampleColumnAccess.ElementPtr(ref view, index)` 寻址，调用方保证索引有效。

普通 `IJobChunk` 在块入口通过 `GetComponentDataNativeArray<T>()` 等 API 取得当前块组件列。示例自有视图用于固定槽位存储的跨块访问，槽位编号不等于 Entity 身份。`ShowLayoutAndSnapshot` 使用 ECS 默认 Chunk 配置，没有设置大 Chunk。

分段索引表示固定容量的存储槽位，不是 Entity 身份：除末块外，每块需要提供 capacity 个可访问槽位，空闲槽位由调用方处理。它不自动适配不同 Archetype 的容量，也不维护实体代次、组件变更追踪或结构变化后的索引映射；这些属于 ECS 访问与生命周期管理。

本例检查功能和生成结果，不计时。分段访问比连续数组多一层块定位；它提供的是无需展平数据的索引能力，没有固定的性能提升百分比。

### 原始上下文的两种提交方式

- `ScheduleCopiedRaw` / `ScheduleParallelForCopiedRaw` 在返回前复制上下文字节，返回后调用方可修改或结束源上下文的生命周期。示例先修改源 Value，再放行回调，验证执行端使用复制后的值。
- 上下文内部的指针仍指向原数据。本例 Output、Gate 的 NativeArray 一直保留到 Complete 返回；复制上下文不深拷贝指针所指资源。
- `RentJobContext` 得到的内存与 `PooledJobContextCleanupPtr` 配对。提交给 Raw 调度后，由调度器清理，调用方不再访问或释放这块上下文。
- 逐索引 Range adapter 必须执行传入 `[first, first + count)` 中的全部索引，连续范围允许合并。`indexedRanges: false` 表示显式 Batch 语义，保留每批的 start/count 与尾批边界。

Raw 示例用 `[UnmanagedCallersOnly]` C# 回调说明 C ABI 与所有权。这些回调仍执行 C#；应用通常使用 `[NativeTranspile]` 及生成的 Schedule/Run，直接执行转译后的 C++ 内核。

### 依赖合并与布局

`JobHandle`、`NativeJobScheduler` 都提供 1、2、3 个句柄的合并重载。小数量 Native 依赖合并无需创建 params 数组；Managed 路径保留其现有合并实现。完成最终依赖后，才能读写、销毁任务使用的数据。

ECS 组件列采用实际 C# 无引用值布局；bool/byte 等字段不使用 interop marshalling 的步长。快照包含布局和程序集标识，兼容性校验失败会拒绝恢复。`WorldSnapshot` 的字节数据及构造入口为内部 API，外部使用 TakeSnapshot/Restore；Shared、Enableable、MultiRelation 和带资源所有权等未完整序列化的状态明确拒绝快照。

### 可选执行与诊断开关

这些环境变量在启动进程前设置，不需要改变 Job 内容：

| 开关 | 用途 |
|---|---|
| `ENTJOY_MAIN_ASSIST=0` | 关闭 Complete 调用线程的协助执行，观察纯 worker 路径；运行时也可用既有 `NativeJobScheduler.SetMainThreadAssistEnabled(bool)` API 切换 |
| `ENTJOY_DIAG_NATIVE_BIND=1` | 输出各 Job 的原生绑定及首次使用明细，便于核查是否采用生成入口 |
| `ENTJOY_JOB_PIN=core` | 诊断时将 worker 绑定到物理核代表 CPU，`even` / `low` 使用逻辑 CPU 编号策略；通常保持默认不绑定 |
| `ENTJOY_JOB_TOPO_DUMP=1` | 输出 worker 启动与稳定运行时的拓扑定位信息 |

例如在 PowerShell 中设置 `$env:ENTJOY_DIAG_NATIVE_BIND='1'`，再运行本页开头的命令；结束后用 `Remove-Item Env:ENTJOY_DIAG_NATIVE_BIND` 清除此项设置。

## 内部实现说明

以下机制由框架或生成器自动处理，调用方无需手写对应的优化代码。

### NativeTranspiler

| 机制 | 当前实现与边界 |
|---|---|
| 批入口 | 传入 Job 上下文地址、起点和长度，减少大量字段经调用边界传递；Run 与 Schedule 复用布局约定 |
| 稳定数组指针 | 证明 NativeArray 句柄保持稳定后，在批入口取一次数据指针；元素可以读写，句柄赋值、逃逸或不确定访问会保留原绑定。长度独立处理，不引入 restrict，允许同址数组 |
| 只读 Chunk 上下文 | 可证明元数据不写且大小不超过 1 KiB 时，复制到私有上下文，减少外部数据写入对元数据加载优化的阻碍；不能证明时保留原路径 |
| 零掩码特化 | 稳定整数或枚举字段在循环中控制至少三组不同的非零位掩码时，选择一个字段生成零值和通用变体；每次 adapter 调用只分派一次。并非任意常量传播，也不枚举所有位组合 |
| 小固定循环 | 标量 C++ 下，对可证明的 2–8 次短最内层 int 递增循环提示 Clang 完全展开；限制复制量，排除循环变量被修改、地址逃逸和嵌套循环等情况 |
| 元素边界 | 整个 Execute 体是一个无 else 的 if，首项是 `index < 稳定int字段` 时，可缩短批循环；保留原始 if。带前后语句、其他分支或边界写入/逃逸时遍历完整区间 |
| 内联提示 | 同程序集辅助函数在头文件中发射，默认仍为 forceinline；识别 AggressiveInlining、NoInlining，NoInlining 优先。显式禁内联不代表更快 |
| 寄存器压力 | Clang 转译内核默认关闭一般循环展开，调度器保留展开；受限的小固定循环使用单独提示。生成器保留源归约循环与累加顺序，不做四路归约展开 |
| SIMD 语义 | 活动 lane 掩码维护 break/continue/return 及条件递增；保留归约初值、比较符、逐轮上界与尾部。无法安全转译的控制形式通过诊断或标记门禁拒绝 |
| 数学读路径 | int2/uint2 读成员使用按值访问器，写成员保留引用语义；double cos 提供标量回退 |

`NativeFeatureOptionsJob` 使用普通 C# 写法即可触发相应分析。生成目录中可检查稳定指针的 `T* const`、零值模板 `__ejZeroMask` 和固定循环的 `unroll(full)` 提示；编译器最后如何使用寄存器与 SIMD，仍以生成的机器码为准。

Windows ClangCL 的普通内核采用 `/O2 /fp:fast` 和显式的 `-O3`、FMA 收缩、循环展开策略；异常浮点、符号零和 frame pointer 采用该配置自身的行为。AutoSIMD 精度目标继续使用各自的编译配置。

### JobSystem 与 Native

- 转译绑定将小上下文字段写到有界栈缓冲并提交原生复制接口；较大上下文租用池化内存。原生层负责复制后的生命周期和回收，减少逐任务托管封装与 cleanup 回调。
- 上下文池采用线程本地缓存与有界共享桶，处理跨线程归还与对齐。池化会保留可复用容量，不表示运行时不再发生任何原生分配。
- Native 句柄使用稳定槽位和代次，身份比较不读取已回收状态；合并依赖登记所有权，完成后及时回收相关状态。异常、阻塞等待和额外延续数据按需创建。
- 队列直接存执行函数、上下文和完成状态；批次 ID 在原生 TLS 内维护，未启用的计时与调试采集跳过相应工作。
- 按需投放参与令牌，执行者从共享游标认领工作；批内任务描述符复用稳定地址。描述符先复制到本地、再释放、再发布可窃取副本，避免存储回收复用时继续读取旧描述符。
- 逐索引作业可合并连续执行范围；显式 Batch 保留边界。ECS 范围保留完整 Chunk，按实体数估计工作量，较重内核可根据已有成本信息采用更细的 tile 布局。
- 粒度选择使用已有成本缓存：轻工作减少拆分，内存受限工作可按约 20 微秒的目标 tile 时间反推元素数，未知成本保留默认回退。ECS 先按每 worker 约 16 个 tile 构建布局，估计工作较重时可细化到约 64 个；默认认领上限为 ECS 1 个 tile、普通工作 4 个，并受公平份额和显式设置约束。
- Complete 的调用线程可以协助执行；粒度决策计入可用执行者。协助开关作用于调度策略，不改变 Run 的同步执行语义。不同的启停、批次、失败和依赖路径保持各自的引用与完成记账。
- Windows 进程与 worker 使用 Normal 优先级；拓扑与计时诊断区分调用线程和 worker，成本缓存的并发采样使用原子值，避免统计本身的数据竞争。

这里的池化和低分配路径适用于满足条件的 Native 转译作业；带托管引用的 C# Job 仍使用其安全持有路径，不能据此声称整个框架零托管分配。

### ECS 与正确性检查

- 默认容量以 16 KiB 为布局目标、最多 128 个实体，并核算组件列、位图、Shared 区及间隔；位图以 64 个实体为粒度。缓存行对齐和列间错位由硬件参数驱动，信息不足时采用 64/4096 默认值。
- 示例自有分段视图使用固定容量寻址，类型定义与寻址函数都在消费方程序集内，无需框架容器注册。
- 查询缓存按完整条件判等，存储条件数组快照，避免哈希碰撞及调用方修改规则导致错误复用；Changed 查询重新筛选当前变更位。
- 快速写入等待已登记 Job，执行旧值析构并维护变更追踪；批量写入校验实体代次。
- 组件、共享值和事件缓冲统一 CLR 布局。快照先完整校验再替换 World，恢复实体代次，拒绝旧的不兼容格式和未支持的资源状态。
- ECB 校验整数溢出与分配字节数，类型校验在命令写入之前；共享销毁统一预留字节与销毁槽，失败不消耗计数。Job 关系过滤、ChangedSince、非空 ThreadCounter 等当前未实现的语义明确拒绝。
- IJobEntity 的 Enabled 模式当前不提供显式 SIMD，报 NT031；Vectorize 请求 C++ 编译器自动向量化，是否得到 SIMD 由机器码决定。

CI 还覆盖双配置、双后端、调度组合、sanitizer、真实 ARM64、ISPC、包发布与扩展压力；当前配置见 [CI 与回归夹具](../../../docs/public/NativeTranspiler-Boundaries-and-Diagnostics.md#7-ci-与回归夹具)。
