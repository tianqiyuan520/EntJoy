# Relationship 压力与成本验证

本工具核对关系图的正反状态、完整实体身份、事件、定位表、slab 与内存上界。Native 模式要求实际使用原生调度器，无法加载时失败；Managed 模式显式选择托管后端。输出为 JSONL，记录请求/实际后端、worker 数、运行参数、运行时及二进制 SHA256。

在仓库根目录构建：

```powershell
dotnet build tools/RelationshipValidation/RelationshipValidation.csproj -c Release
```

Native 模式需要将构建的 ABI 4 NativeDll.dll 放到 `tools/RelationshipValidation/bin/Release/net8.0/`，CI 从 tools/NativeDllCore 构建并放置该库。

## CI 入口

```powershell
./.github/scripts/Invoke-RelationshipChecks.ps1 -Backend managed
./.github/scripts/Invoke-RelationshipChecks.ps1 -Backend native -Extended
```

| 模式 | 每次提交 | Extended / 周期性运行 |
|---|---|---|
| multi-stress | 4096 source × 8 条边；列表、定长各一组 | 125000 source × 8 条边，百万边；列表、定长各一组 |
| 高出度与级联 | 列表单 source 指向 4096 target；批删、环形级联、Enabled 筛选 | 列表单 source 指向 125000 target；批删、环形级联、Enabled 筛选 |
| multi-steady | 三种形态各 20000 轮；常规图为 512 source × 8 条边 | 三种形态各百万轮；常规图为 4096 source × 8 条边 |
| bench | 100000 source，三次原始读数 | 1000000 source，三次原始读数 |

长稳三种形态为列表 M:N、定长 M:N、单 source 的 64 目标列表。每轮真实移除/添加边，经 ParallelWriter 排序回放；穿插重复 Add、Job、组复制/销毁、目标删除与身份复用，以及快照恢复。独立整数邻接表给出预期边与 Job 数据，核对事件数量与存活身份，不从 ECS 反向索引推导预期结果。

复制与恢复场景使用当前可快照的值组件；Enabled 与关系掩码的交集单独验证。长稳核对预热后存活堆增长不超过 8 MiB、身份高水位有界、原生块保留量有界，并在 World 释放加 Trim 后回到初值。

## 单独运行

```powershell
dotnet tools/RelationshipValidation/bin/Release/net8.0/RelationshipValidation.dll --mode multi-stress --backend managed --size 125000 --degree 8 --layout both --out artifacts/multi-stress.jsonl
dotnet tools/RelationshipValidation/bin/Release/net8.0/RelationshipValidation.dll --mode multi-steady --backend native --size 4096 --steps 1000000 --layout both --out artifacts/multi-steady.jsonl
```

`--layout` 为 list/fixed/both，`--degree` 为 1..8；高出度列表长稳使用独立的 64 目标场景。原有 model、steady、bench 模式分别提供单目标参考模型、单目标长稳和批量成本。未知、重复或缺值参数会拒绝。

## 门禁与读数

CI 结果校验拒绝缺少样本、未完成、后端不符、运行规模不符和超出内存上界的输出。每命令保留进程日志、JSONL 和 CSV；进程超时以 124 失败。CI 的分配预算针对 Release、无 Observer、单 hub：

- 组复制：每 source 224 字节，加 1 MiB 固定余量。
- 删除目标：每 source 110 字节，加 512 KiB 固定余量。

三个样本都必须满足预算。调用线程分配单独统计，核对和清理位于计时外；时间保存原始样本，不用共享 runner 的毫秒数判定性能回归。峰值工作集、私有字节、完整 GC 后存活堆、活跃 slab 和块池保留量各有含义，不能互换。

该门禁覆盖所列形态与上界，不承诺任意关系图、任意系统内存耗尽或真实游戏的帧预算。功能建模与 API 用法见 [Relationship 场景](../../samples/EntJoySample/17_Relationship/README.md)。
