namespace EntJoy.JobSystem
{
    /// <summary>
    /// 扁平 `IJobParallelFor` 的认领几何（claim geometry）：worker 以什么顺序、什么粒度去"认领"工作单元。
    ///
    /// 落地为调用点参数（通解）：几何依赖的是内核的访问模式，不是 job 名字，所以由调度方
    /// 在每个调用点声明；框架不再靠"按 job 名特判"或"全局一个开关"。
    ///
    /// 实测依据（对齐档 = 逐 job 镜像 Unity 的 innerloopBatchCount）：
    /// | 内核 | 访问模式 | 需要 | 实测 |
    /// | | | | |
    /// | `CountCellsJob` / `PlaceCellsJob` | 每个元素对同一批计数器做原子 RMW | <see cref="Spread"/> | count 1.70→1.13 ms、place 2.53→1.87 ms |
    /// | `MeleeSimJob` 等邻居扫描 | 相邻索引共享邻居表/cell 计数器的 cacheline | <see cref="Adjacent"/> | 强行 Spread 会让 Melee +10%（123.7→135.8 ms） |
    /// ⇒ 同一条几何对这两类反号，"一条几何套所有 job"不通；通解 = 调用点声明（本枚举）。
    ///
    /// 数值与 native `kClaimGeom{Auto,Spread,Adjacent}` 一一对应（跨 ABI 传 int）。
    /// </summary>
    public enum ClaimPolicy
    {
        /// <summary>
        /// 缺省：不声明。框架按"批表第四字段（诊断覆盖）→ F6 学习 → 全局 env → <see cref="Adjacent"/>"
        /// 解析，因此不写参数时行为与本次改动前逐位一致。
        /// </summary>
        Auto = 0,

        /// <summary>
        /// 每个 worker 独占一段连续工作单元，只在自己那段推进；空了才去别的段尾部窃取。
        /// 等于 Unity 扁平 `IJobParallelFor` 的"静态大块 + 尾部窃取"模型。
        /// 适合：元素级代价低、且集中竞争同一批原子计数器（count/place）的内核 —— 消除共享游标的 cacheline 争用。
        /// </summary>
        Spread = 1,

        /// <summary>
        /// 共享游标按顺序发相邻窗口：同一时刻的 N 个 worker 落在 N 个相邻窗口上，
        /// 于是它们共享的邻居表/cell 计数器 cacheline 是热的。适合：空间局部性敏感（邻居扫描、gather）的内核。
        /// 也是改动前的默认行为。
        /// </summary>
        Adjacent = 2,
    }
}
