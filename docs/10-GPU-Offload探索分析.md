# 10. GPU Offload 探索分析：把部分逻辑 Job 交给 GPU

> 目标：探索把**部分逻辑 Job** 的运算交给 GPU。纯逻辑处理（**不在意渲染**），**要求全平台**。
> 本文是**分析文档**（不包含项目代码改动），基于仓库实测基线 + 知识性估计，待核实项在 §12 明确标注。
> 关联：[00-项目布局](00-项目布局与EntJoySample设计意图.md)、[gridsearch/04-基准测量方法论](gridsearch/04-基准测量方法论与调度开销分析.md)、历史见 [archive/](archive/README.md)。

---

## 1. 目标与约束

| 约束 | 内容 |
|---|---|
| 目标负载 | 部分逻辑 Job（普通 Job 与 ECS Job） |
| 消费端 | **纯逻辑**（CPU 游戏逻辑消费结果，非渲染） |
| 平台 | **全平台**（Win/Linux/macOS/Android/iOS，可能含 Web） |
| 定位 | GPU 是**可选的渐进增强**，ISPC+JobSystem 保持基线 |
| 开发机 | RTX 4060 8GB + CUDA 13.2 驱动（nvidia-smi 实测 2026-08） |

## 2. 基线事实（仓库实测，见 [archive/simd/auto-simd-ispc-gap-analysis.md](archive/simd/auto-simd-ispc-gap-analysis.md)）

| 负载 | C# | C++ 内在 | **ISPC** | 形态 |
|---|---|---|---|---|
| HeavyMove @1M（16 次超越函数/实体） | ~19.5ms | 17-20ms | **~2.0ms(AVX-512) / ~2.9ms(AVX2)** | ALU 密集 |
| LightMove @1M | — | — | ~0.5ms | 带宽绑定 |
| GridSearch QueryCore | — | — | p50 646μs | ~93% 内存带宽绑定 |

**关键事实**：转译器已把 `Execute` body 生成为 SPMD 内核。实测生成物（`SharpNative_Job__global_namespace__MoveSystemJobEntityIspc_Execute.ispc`）：

```ispc
export void ..._Execute_impl(uniform Position position_ptr[], uniform Vel velocity_ptr[],
                             uniform float * uniform dt_ptr, uniform int __entity_count) {
    uniform float dt = *dt_ptr;
    foreach_tiled (__entity_index = 0 ... __entity_count) {
        position_ptr[i].pos.x += velocity_ptr[i].vel.x * dt;
        position_ptr[i].pos.y += velocity_ptr[i].vel.y * dt;
    }
}
```

这个契约（`(数组*, 数组*, 参数, count)` + SPMD 逐实体）与 compute shader **完全相同**——GPU 后端 = 同一个 body，换执行模型头。

## 3. 可行性结论（诚实版）

- GPU 相对 **ISPC** 的增量上限是 **~3-6x**，不是 5-10x（ISPC 已吃掉 C# 的 10x，GPU 是在已优化路径上再追）。
- GPU 只在 **「ALU 密集 × 数据可常驻 × CPU 无/小回读」** 三条件同时满足时赢。
- **跨平台形态 = 双轨**：ISPC 基线（已有，Win/Linux/macOS 通用）+ GPU 尽力加速；GPU 不可用/弱 → ISPC 顶上，Job 照跑。GPU offload 不是硬依赖。

## 4. Job ↔ 计算着色器关联（生成器复用）

| C# / ISPC 侧 | GPU 侧 | 复用依据 |
|---|---|---|
| `Execute` body 语句 | SPMD 逐实体 | `IspcGenerator` 语句翻译复用 |
| `foreach_tiled` 索引 | dispatch ID（`gl_GlobalInvocationID` / `blockIdx/threadIdx` / `@compute` workgroup） | SPMD 执行模型等价 |
| 组件数组（query 序） | storage buffer binding 0/1/… | 现有 adapter 已按 query 定序 |
| job 字段（context block 字节） | uniform block（**std140 填充**） | `ChunkContextHeader` + job struct 现有布局 |
| 实体数 | entity_count uniform | 现有 chunk `entityCount` |

**关联是自动的，不是手写的**——新增量 = 一个新的代码生成 target，复用 `IspcGenerator` 的 SPMD 基础 + `CppJobGenerator.GetStructSizeRecursive` 布局推导 + `SimdEligibilityAnalyzer`。

## 5. 数据提交与回读

### 5.1 提交（每帧数据流）

```
Query(WithAll<Position,Vel>) → CPU 侧聚合匹配 archetype/chunk（现有 BuildRawChunkScheduleCache）
 → 每个匹配 archetype：
    1) 保证 per-(archetype, component) GPU buffer 存在（容量×组件大小，跨帧复用）
    2) 上传：结构变化才 re-upload（复用 structural-version 失效机制）；稳态只传 dirty chunk
    3) context block（job 字段字节）→ uniform buffer
    4) dispatch(ceil(count/64), 1, 1)
 → submit → sync
```

**两个关键设计决策**：
1. **buffer 粒度 = per-(archetype, component)**，不是 per-chunk——对象开销可控，dispatch 按 archetype。
2. **稳态上传 ≠ 每帧整块**：结构稳定时只传 dirty chunk，上传 ~0.1-0.3ms，而非全量 16MB ≈ 0.5-1ms。

### 5.2 回读

```
Complete() = GPU sync（等 GPU 完成）
CPU 要结果 → buffer_get_data → memcpy 回 chunk（或 staging NativeArray）
```

回读本质 = 又一次传输 + 同步停顿。**纯逻辑消费端不可能完全零回读，但输出形态决定税的大小**（见 §7）。

## 6. 传输税与削减（杠杆按收益排序）

传输税 = 上传（O(bytes)）+ 回读（O(bytes)）+ sync stall。MoveSystem @1M 全量往返 = pos+vel(16MB) 上传 + pos(8MB) 回读 = **24MB**：

| 总线 | 有效带宽 | 24MB 往返税 |
|---|---|---|
| PCIe 3.0 x16 | ~12 GB/s | ~2ms |
| PCIe 4.0 x16 | ~24 GB/s | ~1ms |
| PCIe 5.0 x16 | ~48 GB/s | ~0.4ms |

**税与计算同量级（0.4-2ms vs 计算 0.5ms），是临界区的生死线。** 减税杠杆：

| 杠杆 | 机制 | 效果 |
|---|---|---|
| **① 零/小回读（最大）** | 输出小化（reduce/query 形态，见 §7） | 回读税 O(结果) |
| **② 常驻 + 脏同步** | 数据留 GPU，稳态帧 upload→0；结构变化才 re-upload（复用 structural-version） | 每帧税 O(N·bytes)→O(dirty) |
| ③ 窄化传输面 | 只传 GPU 要读的组件；fp16/fixed-point 量化 24→12MB | -50% |
| ④ 双缓冲 + 异步 | 回读帧 N-1 与计算帧 N 重叠；多 dispatch 一次 sync | 隐藏 stall |
| ⑤ PCIe4/5 红利 | 目标机器总线代际 | 税基数减半~减 5x |

**组合效果**：①+② → 稳态税 ≈ 0 → GPU 纯计算 4x 优势全量兑现。**减税不是技巧堆叠，是架构选择：数据常驻 GPU + 结果小化。任何一条回读都是把税请回来。**

## 7. 规模阈值（基于仓库实测，明确数字）

设 N' = N/1M：CPU(N) = 2N' ms（Heavy ALU），GPU(N) = 0.5N' + 传输税。

| 负载形态 | 数据模式 | GPU 开始赢的规模 | 依据 |
|---|---|---|---|
| Heavy ALU（16 次超越函数/实体） | 常驻 + 无/小回读 | **~50-100k** | GPU 4x 计算优势从首个完整 dispatch 起显现 |
| Heavy ALU | 每帧全传 + 回读 | **~1-1.5M**（PCIe4 在 1M 已略胜 1.5ms vs 2ms；PCIe3 到 ~1.4M） | 传输税与计算同量级，临界区由税决定 |
| Light / 带宽绑定（LightMove 类） | 常驻 + 无回读 | **~10M+** | 需 GPU DRAM 带宽 7-10x CPU 才兑现；回读必须为 0 |
| Light / 带宽绑定 | 每帧全传 | **永不** | 0.5ms 计算 + 1.0ms 传输 > 0.5ms CPU |
| 需回读（玩法/AI 消费结果） | 任意 | **通常不赢** | 回读 + sync stall 吃掉一切 |

**纯逻辑消费端的重推导——"纯逻辑" ≠ "零回读"（CPU 逻辑最终要消费结果），但输出形态决定税**：

| 输出形态 | 例子 | 回读税 | 结论 |
|---|---|---|---|
| **Reduce/query**（输出 ≪ 输入） | GridSearch 最近点/范围查询 | O(结果)，几乎为零 | ✅ **纯逻辑 GPU offload 主战场** |
| **Bulk transform**（输出 = 写集） | HeavyMove 位置写回、逻辑逐实体读 | O(写集) | 回到 ~1-1.5M 临界 / light 永不 |

**一句话规模阈值（给决策用）**：单 Job 每帧实体数 **≥ ~1-2M**、**ALU 密集**、**数据可常驻 GPU（跨帧复用）**、**CPU 无/小回读**——四条件齐 GPU 才有确定性优势（2-4x）；缺任何一条，ISPC+JobSystem 保持更优。**仓库 1M 基准恰好落在临界区，必须实测而非外推。**

## 8. 战略判断（GPU ECS 是不是趋势）

- "ISPC 已经太快"（2ms @1M）恰恰是 GPU 会成为趋势的原因：HeavyMove 是 ALU 绑定，线性外推 **1M→2ms、10M→~20ms 超帧预算，CPU 这条路到头**；GPU @10M 数据常驻 ~5ms 仍有余量。
- GPU ECS 趋势成立范围**不是现在的 1M 基准**，而是**规模跨过 CPU 带宽/ALU 上限之后**——那时 GPU 是唯一可行路径。
- 行业现实形态是**混合**：gameplay 逻辑 ECS 留 CPU，数据并行子系统（粒子/PBD/流体/crowd）GPU offload。**全量"GPU ECS 取代 CPU ECS"无人发货**（往返延迟、结构变化、发散查询、内存模型四个硬问题）。
- **对 EntJoy 的战略含义**：转译器 = "C# 源 → 多后端"，ISPC 是已验证主力，GPU 是**保持未来选项的最便宜方式**。正确姿势：**ISPC 当主力，GPU 当按规模/负载选择的常驻后端，用基准数据而不是趋势判断决定投入。**

## 9. GPU 后端选型（含 C++ JobSystem 集成问题）

### 9.1 适配当前框架的框架对比

判定维度：转译器产什么、构建链改动、与 NativeDll/P-Invoke 契合、跨平台。

| 框架 | 集成方式 | 转译器产 | 构建链 | 跨平台 | 适合度 |
|---|---|---|---|---|---|
| **wgpu-native** | NativeDll C ABI（wgpu.h） | `.wgsl`（wgpu 运行时 naga 编译，零 shader 工具链） | 加 wgpu-native 库 | ✅ 全平台 | **全平台主选** |
| **原生 CUDA** | NativeDll + nvcc = **现有 ISPC 管线克隆** | `.cu` | CMake 加 nvcc + CUDA toolkit | ❌ NVIDIA only | 架构完美匹配，锁厂商 |
| **ILGPU** | NuGet 纯 .NET | C# kernel（JIT→PTX） | 无 | CUDA/OpenCL | 省事，量阈值最快 |
| **ComputeSharp** | NuGet 纯 .NET | C# kernel → HLSL | 无 | ❌ 仅 Windows | Windows 最轻，不符跨平台 |
| Godot RD | Godot 壳 | gdshader/SPIR-V | 渲染器 Forward+ | ✅ 桌面+移动 | 仅当 Godot 交付 |

**wgpu / ILGPU / ComputeSharp 三者零工具链**（wgpu 运行时 naga 编译 WGSL、ILGPU JIT、ComputeSharp 运行时 DXIL）；原生 CUDA 是唯一要 nvcc 的，但结构与现有 ISPC 路径逐点同构。

### 9.2 C++ 集成问题：ILGPU 绕开 C++ 执行层的代价

架构里"调度层"与"执行层"要分开看：

- **调度层**：`NativeJobScheduler.cs`（**C#**）——依赖图、RawChunkScheduleCache 都在这里。
- **执行层**：C++ NativeDll（worker pool 执行 CPU job）。

**ILGPU 绕过的只有执行层**（调度层它绕不过，GPU job 依赖图仍是 C# 调度器管）。但三个真实损失：

| ILGPU 丢掉的 | 重要性 | 说明 |
|---|---|---|
| **buffer 数据面在托管侧** | ⚠️ 重要 | managed array → pinned → GPU 多一层拷贝；常驻/脏同步/stream 重叠这些减税杠杆（§6）握不住 |
| 提交只能走 .NET 线程 | 次要 | C++ worker 无法内联提交 dispatch，异步延续与 worker pool 脱节（μs vs ms） |
| 拿不到 cuBLAS / CUDA stream 全控 | 视负载 | 矩阵/频谱类负载缺库 |

**诚实的一面：worker pool 那套优势（并行执行、futex 唤醒、park/wake）对 GPU dispatch 几乎没用**——GPU 自己就是并行器，CPU 侧只需一个提交线程。真正该保的是**数据面控制 + 统一调度**，不是 worker 并行。

**结论**：
- **要保 C++ 优势 → 用 C-API 后端（wgpu-native / 原生 CUDA）绑进 NativeDll**，让 C++ 拥有 GPU 提交 + 数据面（buffer 常驻、脏同步、stream 重叠紧贴现有 NativeContainers）。GPU job 成为 C++ JobSystem 的一等公民。
- **ILGPU 只留作 Gate 1a 快速验证**（JIT，零工程投入），不作为正式后端。
- **wgpu 集成应走 NativeDll C ABI 路线，而非纯 C# 的 Silk.NET.WebGPU**——Silk.NET 纯 C# 绑定有和 ILGPU 一样的"托管侧"毛病。

### 9.3 各后端性能差异（知识性估计，未实测）

**kernel 在 GPU 上的执行时间与 API 无关**（同一块 GPU、同样的 PTX/SPIR-V，执行速度一致）。API 差别只在 CPU 侧 launch 开销（μs 级）、代码生成质量、内存/传输控制权。

| 维度 | 原生 CUDA | ILGPU | wgpu-native |
|---|---|---|---|
| kernel 执行 | 基准 | ≈（同 PTX，LLVM 产） | = 底层 API（SPIR-V） |
| 复杂内核代码质量 | 最强（nvcc） | 可能 -10~30% | 看 glslang/后端 |
| 简单/超越函数内核 | 基准 | 差异 <5%（SFU 吞吐瓶颈） | 差异 <5% |
| CPU 每 launch/命令 | ~1-5μs | ~10-30μs | ~5-20μs |
| 首跑编译 | nvcc 构建期 | JIT 几十 ms（可预热缓存） | 构建/加载期 |
| 内存/stream 控制 | 全控（pinned/UVA/stream） | 有异步 copy，定制弱 | 严格 buffer 模型，异步重叠弱 |
| cuBLAS/cuFFT | ✅ | ❌ | ❌ |

**对 §6 杠杆④（异步重叠）的影响**：原生 CUDA stream 重叠最强；ILGPU 有异步 copy；wgpu 单 queue 为主，重叠受限。

### 9.4 C# JobSystem vs C++ JobSystem——分层而非二选一

不是二选一：JobSystem 分两个平面，调用频率差几个量级。

| 平面 | 调用频率 | 做什么 | 放哪 | 依据 |
|---|---|---|---|---|
| **调度平面** | 每 job 一次（非热路径） | 依赖图、完成跟踪、RawChunkScheduleCache、context 分流 | **C#** | 开发效率、已有实现；GC 风险被 job 时长摊薄 |
| **执行平面** | 每 task 一次（热路径） | work-stealing、worker 唤醒、原子操作、缓存控制 | **C++** | 无 GC、无每 task P/Invoke、直接控原子/futex/缓存行 |

**仓库证据**：最近的 perf 提交（worker 池 futex 混合等待、Guided tile 调度、park/wake 消除）全部是**执行平面**优化，只能在 C++ 兑现——C# 拿不到 futex/park-wake 级控制。

**GPU 视角**：GPU dispatch 不需要 worker pool 的并行优化（GPU 自己并行，CPU 侧只需提交线程）→ 执行平面放哪对 GPU job 几乎无影响；要紧的是**数据面归谁管**（C-API 后端归 NativeDll，ILGPU 归托管侧）。

**战略判断**：C++ 执行平面的价值**取决于 CPU job 是否仍是主体**——
- 若 CPU job 占主体（当前状态、gameplay ECS 常态）→ 保留 C++ 执行，已验证的优化继续兑现；
- 若未来重型负载全移 GPU → worker pool 边际价值下降，futex/tile 优化部分搁浅，架构可精简为 C# worker pool + 原生 GPU 数据面；
- 现实 = 混合：C++ 执行跑 CPU job（主体），GPU 跑 offload 子集。**GPU 是加层，不是替换。**

**结论**：**维持 C# 调度 + C++ 执行的现状是正确解**。把 C# 调度重写成 C++ 无意义（调度非热路径）；把 C++ 执行换回 C# 是主体回归。GPU 路径独立叠加。

## 10. 跨平台（API 覆盖矩阵）

**没有单一 GPU API 覆盖所有平台**——每个后端都有洞：

| 后端 | Win | Linux | macOS | Android | iOS | Web |
|---|---|---|---|---|---|---|
| CUDA | ✅ | ✅ | ❌(Apple 砍) | ❌ | ❌ | ❌ |
| D3D12 (ComputeSharp) | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Vulkan | ✅ | ✅ | ✅(MoltenVK) | ✅ | ❌ | ❌ |
| Metal | ❌ | ❌ | ✅ | ❌ | ✅ | ❌ |
| GL/GLES compute | ✅4.3+ | ✅ | ✅ | 参差 | 参差 | ❌(WebGL2 无 compute) |
| **WebGPU (wgpu)** | ✅ | ✅ | ✅ | ✅ | ✅ | ✅(Chrome/FF/WebKit) |

**唯一"一个 API 覆盖全部目标"的是 WebGPU（wgpu）**——底层按平台自动选 Vulkan/Metal/D3D12/WebGPU。**跨平台 GPU 永远需要 CPU 回退**：`Ispc`（已有，跨平台）基线 + `Gpu`（wgpu）尽力。**跨平台不是"所有平台都用 GPU"，而是"GPU 可用时用它，不可用时 ISPC"。**

**两条落地形态（取决于交付层）**：
- **A. Godot 交付（Win/Linux/macOS/Android/iOS）** → 用 Godot RD：内部按平台代选 Vulkan/Metal/WebGPU，GPU 跨平台零成本。代价：RD 抽象 + **Godot .NET 不支持 Web export**（C# 无法上 Web）。
- **B. 纯 .NET 跨平台** → wgpu-native 绑进 NativeDll（配现有 CMake/C ABI），WGSL kernel 由 transpiler 生成。覆盖最全（含 Web），工作量最大。

## 11. 探索路线（成本递增，每步有"过/不过"门）

```
Gate 0（半天）   裸内核基准——HeavyMove 形态内核，CPU ISPC（~2ms 基线）vs GPU compute，
                测纯计算吞吐 / 常驻稳态帧成本 / 回读成本。
                门：GPU 稳态总成本（含真实传输）≤ ISPC 1/3（≤~0.7ms）才继续。

Gate 1a（1-2 周）普通 Job → GPU PoC（先用 ILGPU 快速验证，JIT 零工程投入）：
                GpuNativeArray / buffer 常驻 + uniform 打包 + dispatch/sync + 回读。
                门：1M heavy 常驻无回读 GPU ≤ ISPC 1/3。

Gate 1b（1-2 周）全量传输/回读开销实测 → 摸清传输税，确认 §6 各杠杆的真实收益。

Gate 2（数周-数月）正式后端：wgpu-native（NativeDll C ABI，C++ 拥有 GPU 提交+数据面）
                 + BackendTarget.Wgsl + C#/C++/ISPC/GPU 四路 parity。
可选               原生 CUDA 作 NVIDIA 桌面增强（与 ISPC 管线同构，cuBLAS + stream）。
```

**顺序原则**：门槛卡在**传输税**不是代码生成 → 先用 ILGPU 手写 PoC 把数据面真相钉死（Gate 1a），确认"GPU 值不值得"后再投 wgpu 生成器（Gate 2）。

## 12. 风险与待核实项

### 12.1 风险

| 风险 | 说明 | 核实方式 |
|---|---|---|
| 离散独显数据仍走 PCIe | 双缓冲/stream 重叠收益打折 | Gate 0/1a 实测上传带宽 |
| 传输税真实量级 | PCIe3/4/5 分档差异大 | Gate 1b 实测 |
| std140 uniform 填充 | job struct 字段进 uniform 需 pad | Gate 2 生成器处理 |
| 回读 sync stall | GPU→CPU 同步可致数百 μs~ms 停顿 | Gate 0/1a 实测 |
| 结构变化 re-upload | 高频增删实体拖垮稳态 | Gate 1 实测 |
| wgpu 异步重叠弱 | 单 queue 为主，§6 杠杆④打折扣 | Gate 2 验证 |

### 12.2 待核实项（网络阻断，本文为知识性估计）

- "automating-cpu-to-gpu-acceleration-in-ecs-game" 仓库内容——本环境 WebSearch/WebFetch 均被安全策略阻断，无法核实其做法与结论。若拿到链接/摘要可补核。
- ILGPU / wgpu-native 的性能数值（§9.3）为知识性估计，非本机实测。
- Godot RD 在 Metal 后端的 compute 支持程度——需在目标平台验证。

---

*本文为纯分析文档，不包含项目代码改动。核心决策数据（传输税、规模阈值）应在 Gate 0/1 用本机实测钉死后再定投入。*
