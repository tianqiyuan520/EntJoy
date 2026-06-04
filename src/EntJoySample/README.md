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
- `NativeTranspiler_Generated`：NativeTranspiler 生成物目录，保留在根目录，不作为手写样例移动。

## `04_NativeCollections`

- `04_NativeCollections/NativeListTest`：`NativeList<T>` 功能测试。
- `04_NativeCollections/NativeColletionStructTest`：Native collection 结构体场景测试。
- `04_NativeCollections/AtomicTest`：原子操作相关测试。

## `05_Algorithms`

- `05_Algorithms/GridSearch`：二维网格搜索、最近点、范围搜索等算法测试。

## 入口切换约定

- 每次只保留一个 `Program.cs` 中的 `Main` 为非注释状态。
- 切换样例时，先注释当前入口，再取消目标样例入口注释。

## C# Job 上下文约定

- 普通 `Schedule` 对用户只有一个入口，内部自动分流。
- Job struct 不含托管引用字段时，走 unmanaged raw-copy context 快路径。
- Job struct 含 `string`、数组、class 等托管引用字段时，走 managed GCHandle context 安全路径。
- 两条路径都是调度时拷贝语义；`Execute` 内修改 job 自身字段不会回写到调用方原始 struct。
- managed 路径只保证托管对象能被安全持有到 job 完成，不自动保证托管对象的多线程读写安全。
- C# Job callback 内异常会被捕获，native 侧正常 cleanup/complete，并在 `Complete()` 后由 C# 重新抛出。
