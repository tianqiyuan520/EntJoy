using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace NativeTranspilerFixture
{
    /// <summary>
    /// B2（内核结构）实验臂：**逐格元数据预计算**。
    ///
    /// 内核体与 <see cref="BenchMeleeScanPackJob"/> 逐行相同，只改一处：把逐格计算的三元组
    /// `j % 9 - 4` / `4 - j / 9` / `oy * cellsW` 换成调用方预计算的偏移表 `CellDelta[jj] = ox - oy*cellsW`
    /// ⇒ 热循环里少一次 `ScanOrder[jj]` 载入 + 两处"除以 9"的魔数乘序列 + 一次乘法；`jj` 直接索引，
    /// 寄存器压力（活跃值）也随之下降。
    ///
    /// 目的：验证 docs §9.2b 的判读"多出来的 425 条集中在标量脚手架，机制是活跃值 > GP 寄存器"——
    /// 若"把逐格整数运算移出循环"真能降脚手架，这条路才成立；否则说明瓶颈不在这类可静态消掉的
    /// 整数运算上（那就是后端寄存器分配问题，属"不要再试"一类）。
    ///
    /// 语义等价由 `Bench.Run()` 的校验和对拍断言把关（同一输入、同一清零后的输出数组）。
    /// </summary>
    [NativeTranspile]
    public unsafe struct BenchMeleeScanOffsetsJob : IJobParallelFor
    {
        public NativeArray<EntJoy.Mathematics.float2> Positions;
        public NativeArray<int> ConfigId;
        public NativeArray<byte> Team;
        public NativeArray<int> CellStart;
        public NativeArray<int> SortedIndex;
        /// <summary>调用方预计算：`CellDelta[jj] = (ScanOrder[jj] % 9 - 4) - (4 - ScanOrder[jj] / 9) * CellsW`。</summary>
        public NativeArray<int> CellDelta;
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

            int outerProcessed = 0;
            int orcaCount = 0;
            float worstK2 = 0f;
            float closestEnemyD2 = 1000f * 1000f;
            int kBase = index * 8;
            int outerCap = Props.OuterCap;
            byte myTeam = Team[index];

            for (int jj = 0; jj < 81; jj++)
            {
                bool isCenter = jj == 0;
                if (!isCenter && outerProcessed >= outerCap) break;
                int newHash = hashId + CellDelta[jj];
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
                    if (jj < 9 && Team[i] != myTeam && d2 < closestEnemyD2 && d2 < Props.MySeekR2)
                        closestEnemyD2 = d2;
                }
            }
            KD2[kBase + 7] = worstK2 + closestEnemyD2;
        }
    }
}
