using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace NativeTranspilerFixture
{
    /// <summary>
    /// §54.5 预注册门槛的候选臂：**两遍切分**（live-set 缩小）。
    ///
    /// 背景（09 §41 / 54.5）：Melee 形状的环路被判为"**寄存器压力**受限"（33 个活跃指针 vs ~14 GPR），
    /// 而框架侧唯一还剩的候选是"降低同时活跃值数"（循环分裂/分块）。§54.5 给这条轴立了硬门槛：
    /// **必须先在夹具里做出一个环比 &lt;1 的臂，才允许碰生成器**。本 job 就是那个臂。
    ///
    /// 算法与 <see cref="BenchMeleeScanPackJob"/> **逐元素等价**，只把两个互不相关的累加拆成两遍：
    ///   ① 第一遍：orca top-8（需要 KD2/KPeer/ConfigId/MyOrcaRadiusSq/MyMass/orcaCount/worstK2）
    ///   ② 第二遍：3x3 中心块内的最近敌人（需要 Team/myTeam/MySeekR2/closestEnemyD2）
    /// 两遍各自都不需要对方的那批值 ⇒ 同时活跃值数严格下降。
    ///
    /// 等价性论证（判据①由夹具的 checksum 断言把关，不是靠这段注释）：
    /// - 遍历序列只由 `ScanOrder/jj/hashId/outerProcessed/outerCap/CellStart` 决定，两遍各自重放同一规则；
    ///   第②遍只覆盖 `jj &lt; 9`，那正是原循环的**前缀** ⇒ 用一个从 0 起的局部计数器重放 cap 规则与原语义一致。
    /// - 两个累加互相不读对方的写：orca 侧不读 closestEnemyD2；敌人侧不读 KD2/KPeer/worstK2/orcaCount。
    /// - `d2` 的表达式逐字相同 ⇒ 浮点结果（含 /fp:fast 下的收缩）逐位相同。
    /// - `KD2[kBase+7] = worstK2 + closestEnemyD2` 仍在最后写一次，与原来同序。
    /// </summary>
    [NativeTranspile]
    public unsafe struct BenchMeleeScanTwoPassJob : IJobParallelFor
    {
        public NativeArray<EntJoy.Mathematics.float2> Positions;
        public NativeArray<int> ConfigId;
        public NativeArray<byte> Team;
        public NativeArray<int> CellStart;
        public NativeArray<int> SortedIndex;
        public NativeArray<int> ScanOrder;
        public NativeArray<float> KD2;
        public NativeArray<int> KPeer;
        public BenchScanProps Props;

        public void Execute(int index)
        {
            var p = Positions[index];
            int cellsW = Props.CellsW;
            int cellsH = Props.CellsH;
            int cx = (int)(p.x * Props.InvCellSize + Props.OriginX);
            int cy = (int)(p.y * Props.InvCellSize + Props.OriginY);
            cx = System.Math.Max(0, System.Math.Min(cx, cellsW - 1));
            cy = System.Math.Max(0, System.Math.Min(cy, cellsH - 1));
            int hashId = cy * cellsW + cx;
            int gridCells = cellsW * cellsH;
            int kBase = index * 8;
            int outerCap = Props.OuterCap;

            // ── 第①遍：orca top-8 ──
            int outerProcessed = 0;
            int orcaCount = 0;
            float worstK2 = 0f;
            for (int jj = 0; jj < 81; jj++)
            {
                int j = ScanOrder[jj];
                bool isCenter = jj == 0;
                if (!isCenter && outerProcessed >= outerCap) break;
                int ox = j % 9 - 4;
                int oy = 4 - j / 9;
                int newHash = hashId + ox - oy * cellsW;
                if (newHash < 0 || newHash >= gridCells) continue;
                int start = CellStart[newHash];
                int end = CellStart[newHash + 1];
                for (int s = start; s < end; s++)
                {
                    int i = SortedIndex[s];
                    if (i == index) continue;
                    if (!isCenter)
                    {
                        if (outerProcessed >= outerCap) break;
                        outerProcessed++;
                    }
                    var q = Positions[i];
                    float dx = p.x - q.x;
                    float dy = p.y - q.y;
                    float d2 = dx * dx + dy * dy;
                    if (d2 < Props.MyOrcaRadiusSq && (orcaCount < 8 || d2 < worstK2))
                    {
                        float peerMass = ConfigId[i] == 2 ? 20f : 1f;
                        if (peerMass / (Props.MyMass + peerMass + 1e-6f) >= 0.1f)
                        {
                            int slot = orcaCount < 8 ? orcaCount : 8;
                            while (slot > 0 && KD2[kBase + slot - 1] > d2)
                            {
                                if (slot < 8)
                                {
                                    KD2[kBase + slot] = KD2[kBase + slot - 1];
                                    KPeer[kBase + slot] = KPeer[kBase + slot - 1];
                                }
                                slot--;
                            }
                            KD2[kBase + slot] = d2;
                            KPeer[kBase + slot] = i;
                            if (orcaCount < 8) orcaCount++;
                            worstK2 = KD2[kBase + 7];
                        }
                    }
                }
            }

            // ── 第②遍：3x3 中心块的最近敌人（只重放原循环的前 9 个格）──
            var p2 = Positions[index];
            byte myTeam = Team[index];
            float closestEnemyD2 = 1000f * 1000f;
            int outerProcessed2 = 0;
            for (int jj = 0; jj < 9; jj++)
            {
                int j = ScanOrder[jj];
                bool isCenter = jj == 0;
                if (!isCenter && outerProcessed2 >= outerCap) break;
                int ox = j % 9 - 4;
                int oy = 4 - j / 9;
                int newHash = hashId + ox - oy * cellsW;
                if (newHash < 0 || newHash >= gridCells) continue;
                int start = CellStart[newHash];
                int end = CellStart[newHash + 1];
                for (int s = start; s < end; s++)
                {
                    int i = SortedIndex[s];
                    if (i == index) continue;
                    if (!isCenter)
                    {
                        if (outerProcessed2 >= outerCap) break;
                        outerProcessed2++;
                    }
                    var q = Positions[i];
                    float dx = p2.x - q.x;
                    float dy = p2.y - q.y;
                    float d2 = dx * dx + dy * dy;
                    if (Team[i] != myTeam && d2 < closestEnemyD2 && d2 < Props.MySeekR2)
                        closestEnemyD2 = d2;
                }
            }

            KD2[kBase + 7] = worstK2 + closestEnemyD2;
        }
    }
}
