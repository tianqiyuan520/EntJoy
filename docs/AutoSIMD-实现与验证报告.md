# AutoSIMD 实现与验证报告（2026-08-23 持续更新）

> 本文档记录 NativeTranspiler AutoSIMD 后端的架构、修复历史、验证体系与剩余盲区。
> 对应代码：`src/NativeTranspiler/Analyzer/Simd/`、`src/NativeDll/`（SimdValue.h / NativeSIMD.h / NativeSIMD_math.h）、
> 验证工具：`tools/AutoSIMDVerify/`、`samples/EntJoySample/03_NativeTranspiler/AutoSIMDTest/`、`tools/JobLibsBenchmark/`。

## 一、架构

```
C# job (IJobParallelFor / IJobFor / IJob / static 方法)
   │  [NativeTranspile(AutoSIMD = Enabled)]
   ▼
NativeTranspiler (Roslyn 源生成器)
   ├─ SimdVariableAnalyzer      变量 uniform/varying/reduction 分类 + 参数类型推导
   ├─ SimdControlFlowGenerator  主流程：if-else → mask/save-blend、break/continue/return
   ├─ SimdExpressionTranslator  表达式：算术/比较/位运算/数学函数/字面量/uint 语义
   ├─ SimdLoopGenerator         循环：uniform/reduction/unroll/per-lane
   └─ OuterSimdGenerator / CppGenerator  外层 batch 框架（jobStruct 与 static 两条路径）
   ▼
生成 C++（simd_value<T> + n_* 内建抽象层）
   ▼
ClangCL /O2 编译为 NativeTranspiled.dll（Unity Build）
```

- **SIMD 宽度自适应**：运行时 `DetectSimdWidth()` 检测 CPU（AVX2=8 / SSE=4 / 标量=1），
  并**钳制到编译时 `NSIMD_WIDTH`**——运行时宽度绝不能超过编译时寄存器宽度，否则 lane 越界读垃圾。
- **save-blend**：if-else 链修改 varying 变量时，保存原值 → 分支体 → `blend(saved, new, mask)`。
  支持 int/uint/**float** 累积器；嵌套 if 用**每层独立**的 save 索引与 saved-name 列表（全局状态会被嵌套覆盖）。
- **无符号语义**：uint 字面量 / `(uint)` cast 的比较用 `x ^ 0x80000000` 翻转技巧走有符号比较（等价无符号）。
- **数学函数**：AVX2 下 SLEEF 风格多项式（sin/cos/log，~3.5 ULP）；非 AVX2 回退逐 lane `::sinf/::logf`。

## 二、修复历史（按发现顺序）

### 2.1 正确性审计（663f736）
| Bug | 影响 |
|---|---|
| static 方法参数全部误标 `int`（float threshold） | float 比较编译成 `n_cmp_*_epi32` |
| reduction 循环 SIMD 上界未对齐 `NSIMD_WIDTH` | **越界读内存**（100%8≠0） |
| `isAllTrue` 短路跳过条件计算 | 分支无条件执行、elseMask 恒 true |
| `TryFoldReduction` 误判条件写为 min/max | else-if 分支丢失 |
| `RemovePerLaneWrites` 误删必需的分支 masked store | 分支体被清空、悬空引用 |
| SLEEF log mantissa 域错（[0.5,1) 应为 [1,2)）；sin/cos 象限处理错；cos 用 float π/2 触发 round 边界象限翻转 | 误差高达 ~1.0-2.1 |

### 2.2 save-blend 嵌套污染（e52a2ab）
| Bug | 影响 |
|---|---|
| `_saveBlendCounter`/`_savedVarNames` 全局扁平状态 | 嵌套 if 外层 blend 引用未生成的 save、掩码超作用域（GridSearch 编译失败） |
| branch mask 在 body 执行后才取 | 被内层循环替换成循环局部变量 |

修复：save 状态每层 if 局部化 + branch mask 在 body 前捕获。

### 2.3 对抗性压力测试（d0478bf）
| Bug | 触发 | 影响 |
|---|---|---|
| float 字面量 `40f` 缺小数点 | 深层 if-elif 链 | 非法 C++ |
| 分支变量声明落在分支块内 | `float v; if...` | 后续分支 `v_v` 不可见 |
| float 累积器无 save-blend | 循环+parity 分支 `acc += v` | 所有 lane 无条件累加 |
| 一元负号 / `+=` / `-=` / `^=` 等运算符缺漏或歧义 | 多运算符组合 | 编译失败/歧义 |
| **break 在 uniform 循环 + varying 条件** | 查找第一个满足项 | 未匹配 lane 也退出、循环后 mask 被污染 |
| **uint 比较用 signed 语义** | `(uint)p > (uint)q`、`x < 1000u` | 负数比较错 |
| `~x` 翻译成一元负 | `s & ~3` | 按位非变取负，off-by-one |
| `<=` 闭区间 unroll / 循环边界丢末次迭代 | `for (dx=-1; dx<=1; ++dx)` | 少算 1/3 邻域 |
| continue 条件反转自抵消（eq→ne→eq）+ 违反 De Morgan（`&&` 未转 `||`） | `if (dx==0 && dy==0) continue` | 只剩角点邻域 |

## 三、验证体系

### 3.1 AutoSIMDVerify（tools/AutoSIMDVerify — 23 项检查）
以 C# 托管基线为 oracle，逐元素对比 AutoSIMD native 输出：

| 组 | 覆盖 | 变体 |
|---|---|---|
| 标准 5 Case | SimpleArith / MathFuncs / Reduce / ComplexFlow / GatherReduce | Static + IJobFor + IJobPF + IJob |
| 对抗 10 Stress | 见 2.3 触发列（嵌套×分支×循环×多变量） | IJobFor |
| 输入 | 非 8 倍数 N=2045/4093（触发 remainder 标量路径）+ 随机 [-100,100] | |

运行：`dotnet run --project tools/AutoSIMDVerify -c Release`
注意：需先 `samples/EntJoySample` 构建 NativeTranspiled.dll 并复制到 `bin/`。

### 3.2 JobLibsBenchmark 内嵌验证
S1/S3/S5/S6 的 AutoSIMD 结果与 Cpp/ISPC/标量参考逐元素一致（`Program.VerifyAutoSIMD`）。

### 3.3 回归状态（最新）
```
AutoSIMDVerify        23/23 ✅
JobLibsBenchmark      S1/S3/S5/S6 ✅（S6 AutoSIMD 3.14ms = ISPC 1.71x）
EntJoySample + GridSearch CMake   ✅
```

## 四、性能（结果正确前提下，2026-08-23 实测）
| 场景 | AutoSIMD | ISPC | 倍率 |
|---|---|---|---|
| S6 控制流 LCG+分支 | 3.14ms | 5.39ms | **1.71x** |
| S5 高竞争（sum=i*j） | ~0.56ms | ~0.53ms | 1.05x |

注意：早期"3.5x"数据是错误的——当时宽度误判 16 但寄存器 8-wide，半条 lane 读垃圾导致结果错、工作量减半。

## 五、剩余问题（诚实清单）

### 5.1 正确性盲区（未验证的语义，风险最高优先补）
| 项 | 说明 | 风险 |
|---|---|---|
| float2/int2 组件写 | IJobChunk/IJobEntity（MoveJob 类）——AutoSIMD 暂不支持 ECS 调度，`SimpleArith_SIMD_Entity` 仅编译通过、结果未对比 | 高（最常见的 ECS 形态） |
| while / do-while 循环 | 生成路径存在但未做输出对比 | 中 |
| 特殊浮点值 | NaN / ±Inf / ±0 / 次正规数（输入范围 [-100,100] 未覆盖；SLEEF 多项式对这些值的行为未验证，log(≤0) 尤其可疑） | 中 |
| 嵌套循环内 return | 只测了单层 break + goto 路径，嵌套 return 的标号/恢复未验 | 中 |
| 自定义函数调用 | MathF 系已验证，用户静态方法（非 [NativeTranspile] 宿主内）翻译未测 | 低-中 |
| 非常量循环边界 + 分支 | 组合半边未覆盖 | 中 |
| int 溢出边界 | INT_MIN/MAX 附近的算术/位运算组合未测 | 低 |
| 多层 `&&`/`\|\|` 混合条件的 continue 反转 | De Morgan 已修单层 AND，混合 AND/OR 嵌套未验 | 低-中 |

### 5.2 已知功能限制（transpiler 不支持或降级）
| 限制 | 行为 |
|---|---|
| `long`/`int64` | ISPC/原生路径不支持（重算必须用 int/uint） |
| 表达式体 `Execute => ...` | 源生成器要求块体 `{ ... }` |
| 非托管字段约束 | job 字段必须非托管类型（NativeArray<T>、标量、结构体等），不能用 int[] |
| 无法调 `Console` 等托管 API | [NativeTranspile] 方法体内不可调用非托管签名之外的方法 |
| ECS 调度（IJobChunk/IJobEntity） | AutoSIMD 当前不参与（用户明示暂不支持，后续启用时需补验证） |
| 自研浮点数学精度 | AVX2 下 SLEEF 多项式 ~3.5 ULP（非 IEEE），对追求精确结果的场景需要 SIMD_MATH_PRECISION=2 回退 |

### 5.3 性能优化方向（基于实际生成代码分析）

> 状态标注：**[已落地]** = 2026-08-23 第二轮实现并验证（详见第七章）；[待实施] = 未开始。
> 以下基于生成的 C++ 代码模式分析，列出可落地的优化项，按**收益/难度比**排序。

#### P0：冗余变量 save/blend 消除 —— ✅ 已落地（7.1，实际收益见 7.2）

**问题**：`EmitSaveVaryingVars` 保存 if-else 链中**所有 modifiedVars**，但一个变量可能只在**某个分支**被修改。对于"只读"变量（在 if-else 链前声明、链内不修改），整个 if-else 链的每个分支末尾都在做无意义的 blend。

**示例**（ST1 5-way if-elif）：
```cpp
// v_a 只读（分支里从不修改它），却在每个分支后都 blend：
v_a = blend(__save_0_v_a, v_a, __cond_0);   // ← 完全冗余
__save_0_v_a = v_a;                          // ← save 也被覆盖
v_v = blend(__save_0_v_v, v_v, __cond_0);   // ← 这个有意义

// 5 个分支 × 2 冗余 blend = 10 次无意义操作
```

**修复方向**：在 `GenerateIfStatement` 中，将 `EmitSaveVaryingVars` 改为**per-branch**：只保存在**当前分支体**中实际被写入的变量（而非整个 if-else 链的所有 modifiedVars）。需要对每个分支单独调用 `CollectWrites`。

**预估收益**：ST1 5 分支 × 4 冗余 blend = 20 次无意义操作消除，约 **10-15%**。

---

#### P0：AND(all_true, X) 消除 —— ✅ 已落地（7.1）

**问题**：当 `_currentMask == "simd_mask::all_true()"` 时，生成的分支掩码 `__cm_N = n_and_mask(all_true.m, X.m)` 冗余——`all_true & X == X`。

**示例**（第31行）：
```cpp
simd_mask __cm_2 = simd_mask{ n_and_mask(simd_mask::all_true().m, simd_mask{...}.m) };
// 等价于：__cm_2 = simd_mask{...}.m （去掉外层 AND）
```

**修复方向**：在 `GenerateIfStatement` 中，当 `_currentMask == "simd_mask::all_true()"` 且条件无前缀排除时，直接用 condition mask 本身，不生成 AND。

**预估收益**：每个条件减少 1 个 AND + 1 个临时 mask 变量，约 **3-5%**。

---

#### P1：标量 store → 向量化 store —— ✅ 已落地（7.1）

**问题**：当 mask 为 all_true 且索引是连续的（`v_base + si`），当前用逐 lane 提取 store：
```cpp
for(int __l=0;__l<g_simdWidthInt;__l++){
    R_ptr[n_extract_lane_epi32(v_i.v,__l)] = n_extract_lane_f32(v_v.v,__l);
}
// 8 次 extract + 8 次标量 store
```

**修复方向**：在 `TranslateAssignment` 的连续索引路径中，当 mask=all_true 时，直接用 `n_store_ps(&R_ptr[si], v_v.v)`。

**预估收益**：减少 8 次 extract + 8 次标量 store → 1 次向量 store，约 **3-5%**。

---

#### P1：掩码级联深度优化 —— ✅ 已落地（7.1）

**问题**：5 分支 if-else 的第 4 个条件掩码产生 5 层嵌套 AND+NOT：
```cpp
n_and_mask(
    n_and_mask(
        n_and_mask(
            n_and_mask(n_not_mask(__cond_0), n_not_mask(__cond_1)),
            n_not_mask(__cond_3)),
        n_not_mask(__cond_5)),
    __cond_5)
```

每个括号都生成一个临时 mask 变量，导致**寄存器压力**剧增。

**修复方向**：用递推变量缓存"已排除条件"的组合掩码：
```cpp
simd_mask __excluded = simd_mask{ n_not_mask(__cond_0.m) };
__excluded = simd_mask{ n_and_mask(__excluded.m, n_not_mask(__cond_1).m) };
__excluded = simd_mask{ n_and_mask(__excluded.m, n_not_mask(__cond_3).m) };
__cm_4 = simd_mask{ n_and_mask(__excluded.m, __cond_5.m) };
```

**预估收益**：减少掩码表达式深度，改善指令调度和寄存器压力，约 **2-5%**。

---

#### P2：循环展开优化（预期 5-10%）

**现状**：已实现常量界 unroll（≤64 次），但均匀界 non-reduction 的内层循环（如 S3 的 `for j < 100`）走 Docs-style scalar loop——每次迭代都做 `broadcast(j)` + mask check + store。

**修复方向**：对固定次数的小循环（如 16-64 次），做半展开（2x/4x）：合并相邻迭代的独立操作，减少循环开销和分支预测失败。

**预估收益**：约 **5-10%**，但实现复杂度高（需分析循环体依赖）。

---

#### P2：SIMD 宽度多版本编译（预期 10-20% on AVX-512）

**现状**：单宽度（AVX2=8）+ 运行时钳制。在 AVX-512 机器上浪费一半寄存器宽度。

**修复方向**：编译 3 个版本（AVX512=16 / AVX2=8 / SSE2=4），运行时选择。需要：
1. NativeSIMD.h 定义 `kMaxSimdWidth = 16`
2. 每个函数签名加 `int width` 参数
3. 运行时 dispatch

**预估收益**：AVX-512 机器上 **2x**；但 99% 用户是 AVX2，收益有限。工程量大。

---

#### P3：其他潜在优化（待进一步分析）

| 方向 | 说明 | 预期 | 难度 |
|---|---|---|---|
| 函数调用内联 | 用户静态方法调用翻译为直接 P/Invoke，可内联 | 2-5% | 中 |
| 条件常量折叠 | 编译时已知的条件（如 bool 字段）跳过 mask 计算 | 1-2% | 低 |
| gather/scatter 优化 | 跨步索引 gather 可用 `_mm256_i32gather_ps` | 3-5% | 中 |
| 分支预测提示 | 对高概率分支加 `__builtin_expect` | 1-2% | 低 |

---

#### 综合预估

| 组合 | 累计收益 | 总预估 |
|---|---|---|
| P0 完成 | 13-20% | 当前 1.71x → ~2.1x |
| P0+P1 完成 | 18-30% | 当前 1.71x → ~2.4x |
| P0+P1+P2 完成 | 28-50% | 当前 1.71x → ~3.0x |

### 5.4 工程问题
| 问题 | 说明 |
|---|---|
| 验证依赖手动复制 DLL | AutoSIMDVerify 需先 `Copy-Item` 新构建的 NativeTranspiled.dll 到 `bin/`，未纳入自动构建链 |
| docs 未纳入版本控制 | `docs/` 在 .gitignore（AutoSIMD-实现与验证报告.md 未被 git 跟踪，仅有本地副本） |
| 生成警告噪音 | Unity Build 下大量 unused-variable/unused-label 警告（`v_base`/`__simd_exit` 等），需清理生成模板 |
| 测试数据量小 | 压力输入固定 seed、范围 [-100,100]，未做大规模随机模糊（fuzz） |

## 六、运行与维护
```powershell
# 重新生成 + 编译全部
dotnet build samples/EntJoySample/EntJoySample.csproj -c Release

# 全量 AutoSIMD 正确性验证（需先把新 DLL 复制到 bin）
Copy-Item samples\EntJoySample\NativeTranspiler_Generated\build\Release\NativeTranspiled.dll bin\ -Force
dotnet run --project tools/AutoSIMDVerify\AutoSIMDVerify.csproj -c Release

# JobLibsBenchmark 回归
dotnet run --project tools\JobLibsBenchmark\JobLibsBenchmark.csproj -c Release -- S6
```
新增对抗用例：在 `samples/EntJoySample/03_NativeTranspiler/AutoSIMDTest/Case7_Stress/StressJobs.cs`
添加 CSharp 基线 + SIMD 变体 job，并在 `tools/AutoSIMDVerify/Program.cs` 的 `RunStress` 注册对比。

## 七、P0/P1 优化迭代记录（2026-08-23 第二轮）

### 7.1 已落地优化（全部经 23/23 AutoSIMDVerify + JobLibsBenchmark 验证）

| 优化 | 修改点 | 效果（生成代码验证） |
|---|---|---|
| **P0-1 AND(all_true, X) 消除** | `SimdControlFlowGenerator.GenerateIfStatement` 分支/else 掩码生成 | `n_and_mask(all_true, X)` → `X`，每条件省 1 个 AND + 1 临时变量 |
| **P0-2 冗余 save/blend 消除** | `GenerateIfStatement` 改为 per-branch 写分析 + `CollectWrites` 修复 | 只读变量不再被 save/blend（Stress1 消除 6 次 `v_a` blend）；修复 `-a` 一元负被误判为写 |
| **P1-1 向量化 load/store** | `OuterSimdGenerator` 传 `batchLoopVar: "si"`（此前从未传，连续路径从未生效） | `n_store_ps(ptr+si, v)` 替代 8 次 extract+8 次标量 store；`n_load_ps` 替代 gather |
| **P1-2 掩码级联 O(N)→O(1)** | `GenerateIfStatement` 新增 `EnsureExcludedMaskUpTo` 递推变量 | 5 分支链的 5 层嵌套 `n_and_mask(n_and_mask(...))` → `__excl_k = __excl_{k-1} & ~c_k` 逐层引用 |

### 7.2 性能回归（S6 控制流重算，10万×1000）

| 阶段 | AutoSIMD | ISPC | 倍率 |
|---|---|---|---|
| 优化前基准 | 3.14ms | 5.39ms | 1.71x |
| P0 后 | 3.287ms | 5.590ms | 1.70x（噪音内持平，正确性不降） |
| P0+P1 后 | 3.246ms | 5.698ms | 1.76x |
| 最终回归 | **3.178ms** | **5.504ms** | **1.73x**（多轮中位数，噪音 ±13%） |

注：S6 以 LCG+分支为主，load/store 非瓶颈，P1 收益主要体现在 S1 类纯算场景（生成代码从逐 lane 变单条向量指令）。P0/P1 对分支密集场景（如 Stress1 5-way 链）的生成代码缩减最明显：6 次冗余 blend + all_true AND 包层 + 5 层掩码嵌套全部消除。

### 7.3 对抗性 EdgeCase 测试（tools/AutoSIMDEdgeCases）

新增 `samples/.../Case8_EdgeCases/EdgeCaseJobs.cs`（EC1-EC9 × C# 基线/SIMD 对）+ `tools/AutoSIMDEdgeCases/` 独立验证项目：
- **特殊浮点值**：±Inf / NaN（多载荷）/ ±0 / 次正规 / FLT_MAX/MIN / 符号零，位精确比较
- **对抗语义**：嵌套循环 return、多层 &&|| 混合 continue、非 8 倍数边界、int 溢出边界
- **随机 fuzz**：24 seed × 4 尺寸（1001/2047/4093/8192），随机 + 特殊值注入
- 运行：`dotnet run --project tools/AutoSIMDEdgeCases -c Release -- --fast`

### 7.4 EdgeCase 测试挖掘出的真实漏洞（未修复，风险登记）

| # | 触发 | 证据（生成代码） | 风险 |
|---|---|---|---|
| E1 | `int` 与 `float` 混合比较 | EC4 `(dx*dy>2)` int 比较生成 `n_cmp_gt_ps(simd_value<int>.v, n_set1_ps(2))`；`(i%3==0)` int==0 生成 `n_cmp_ne_ps` | **高**：uniform int 条件的类型推断错，结果全 0 |
| E2 | `unchecked(x+y)` | 生成 `/* unsupported expr: UncheckedExpression */ 0` | 高：算术直接变 0 |
| E3 | `int.MinValue/MaxValue` | 映射成 `std::numeric_limits<float>::lowest()/max()` | 高：类型错误，INT_MIN 分支失效 |
| E4 | 未初始化 varying 变量 | EC2 `simd_value<float> v_r; __save_0_v_r = v_r;` 保存未初始化垃圾 | 中-高：NaN lane 输出垃圾（C# 侧所有分支都有赋值则安全） |
| E5 | NaN 载荷传播 | EC2 `src=C... (-999) simd=7FC00001 (NaN)`：SIMD NaN lane 未走 else 分支 | 中-高：分支掩码对 NaN 的处理 |
| E6 | 嵌套 continue + unroll | EC4 多分支短路后 `v_sum` 全 0（`__good_N` 掩码链含 E1 错误比较） | 高：控制流+int 比较组合 |
| E7 | 嵌套循环 return | EC5 输出全 0（嵌套 return 的标号/恢复路径错） | 高（5.1 已列盲区，实测确认） |
| E8 | 非常量循环边界 | EC6 结果 `450886BD` vs `45088A95`（0x100000 差） | 中 |
| E9 | while 循环生成 | EC7 生成 `if (!all_true() & __wcond_1 & tracker.any_true())`、`/* unsupported: PostIncrementExpression */`、`__mask_2` 未定义类型 | **高**：while 路径编译期即错（构建被阻塞，已改用 for 覆盖语义） |
| E10 | `long` cast / `(float)n` cast | EC3 `(long)`、EC6 `(float)` cast 均编译失败（不支持 long；varying int→float cast 缺失） | 中（已知限制 5.2 确认 + 新增 float cast 缺口） |
| E11 | SLEEF 对 -Inf 输入 | EC1 `log/sqrt` 域：`-Inf` → NaN（cs=FF800000 simd=FFC00000） | 中（5.1 已预警 log(≤0)，实测确认） |

**结论**：EdgeCase 套件首次运行即挖出 11 类真实缺陷/限制（其中 E1/E2/E3/E9 为编译期或结构性错误，E5/E6/E7 为控制流语义错）。这正是对抗性验证的价值——现有 23 项测试无法覆盖上述组合。后续修复优先级按 E1→E9→E5→E7/E6 推进。