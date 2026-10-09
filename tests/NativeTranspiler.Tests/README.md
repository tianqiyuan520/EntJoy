# NativeTranspiler.Tests

`src/NativeTranspiler`（C#→C++/ISPC 源生成器）的**进程内**测试工程（xUnit / net8.0）。
不依赖 MSBuild 源生成器管线、不做原生编译 —— 直接用 `CSharpGeneratorDriver` 跑一次生成器，
断言**生成的 C++/ISPC 文本**与**诊断**（NT001…）。

## 怎么跑

```powershell
# 全部
dotnet test tests/NativeTranspiler.Tests/NativeTranspiler.Tests.csproj

# 单个缺陷
dotnet test tests/NativeTranspiler.Tests/NativeTranspiler.Tests.csproj --filter "FullyQualifiedName~NT01_ElementStore"
```

## 夹具（`GeneratorHarness.EmitFor(string userSource)`）

| 产物 | 来源 |
|---|---|
| `EmitResult.Cpp` | 生成器落盘的 `NativeTranspiler_Generated/*`（含 `// ===== <文件名> =====` 分隔） |
| `EmitResult.Bindings` | `GeneratorDriverRunResult.GeneratedTrees`（bindings / attribute / marker 的 C# 文本） |
| `EmitResult.Diagnostics` | 生成器上报的全部诊断（`HasDiagnostic("NT024")` / `DiagnosticSummary`） |

三个必须知道的坑（都实际踩过）：

1. **不要在替身源码里定义 `NativeTranspiler.NativeTranspileAttribute`**：生成器自己用
   `RegisterPostInitializationOutput` 注入它，而且 post-init 源**对同一次生成可见**
   （`HarnessInternalsTests.PostInitOutputVisibility` 是这条不变量的自检）。再定义一份 = 同名类型重复定义
   ⇒ `GetTypeByMetadataName` 返回 null ⇒ **一个 job 都认不出来，产物为空且无任何诊断**。
   ⇒ 断言一律用 `EmitForJob`（源码里一个 job 都没识别到时**直接抛**），不要用会返回空产物的 `EmitFor`。
2. **`[NativeTranspile]` 必须真的写在被测 struct 上**（谓词 `s.AttributeLists.Count > 0` 是入口条件）。
3. 替身类型只需"命名空间 + 名字"对得上：`EntJoy.JobSystem.IJob*`、`EntJoy.ECS.IJobChunk/IJobEntity/
   ArchetypeChunk`、`EntJoy.Collections.NativeArray<T>`（见 `GeneratorHarness.Stubs`）。

## 缺陷回归清单

| 用例 | 缺陷 |
|---|---|
| `NT05_WarningOnlyJobTests` | warning-only job 被当成校验失败 ⇒ Schedule 绑定被吞（CS0103 + NT028） |
| `NT02_EntityVectorizeReturnTests` | IJobEntity Vectorize 把 `return;` **删掉** ⇒ 无括号 if 控制流反转 |
| `NT03_NT04_PerLaneRenderTests` | per-lane 渲染器缺 `do{}while(false)`；`IJob`（无索引）被跑 ×8 lane |
| `NT07_ReturnInsideNestedLoopTests` | 循环内 `return;` → `break;` 只跳内层循环（静默错值）⇒ 改发 `__ENTJOY_UNSUPPORTED` 标记 |
| `NT01_ElementStoreTests` | 非连续 varying 下标发无掩码连续 store；IJobChunk 批路径 `batchOffsetVar:"0"` |
| `NT11_ChunkArrayAliasTests` | chunk 数组元素别名判定非递归（if/for 体内写看不见）+ 带副作用下标被别名 |
| `NT10_IntUIntPromotionTests` | `int op uint` 的 C# 结果是 **long**，却被发成 32 位无符号回绕（`100000*100000u` 得 1,410,065,408 而非 10,000,000,000）；二阶：回绕节点让 `>>`/`%` 变有符号。`-uint` 同族 |
| `NT08_AutoSimdRemainderTests` | AutoSIMD 余数循环原样搬 C# 文本（9 条手写替换表）⇒ `1UL <<`（C++/LLP64 是 32 位，位移 ≥32 = UB）、`MathF.Max(` 直接漏进 C++ |
| `NT09_VectorizedInnerLoopTests` | 内层向量路径从"体内第一个 `if (x < y)`"凭空合成"对原始数组取 min/max"⇒ 体内计算（closest-point 的 `d`、argmin）整段丢弃 ⇒ 改"能证明才向量化，否则退标量" |
| `NT06_ArgumentQuotingTests` | MSBuild 任务拼命令行只按空格加引号且不转义反斜杠 ⇒ 仓库自带的 `…NativeTranspiler_Generated\`（永远以 `\` 结尾）在路径含空格时吃掉闭合引号 |

## 发射快照工具（可选）

`EmitSnapshot` 把代表性 job 的产物落盘（当前 **44 个文件**），用于**逐字对比"改动前/改动后"**，
证明除缺陷点外没有无关发射变化：

```powershell
$env:ENTJOY_NT_SNAP_DIR="$env:TEMP\snap\after"
dotnet test tests/NativeTranspiler.Tests/NativeTranspiler.Tests.csproj -c Release --filter "FullyQualifiedName~EmitSnapshot"
# 不设该环境变量时该用例直接跳过（无副作用）
```

门禁 `emit-snapshot` 用同一套产物与基线目录 `artifacts/verifier/snap/after` 逐文件比哈希，要求 `changed=0`（`SNAP-IDENTICAL`）。
