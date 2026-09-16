using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace NativeTranspilerFixture
{
    /// <summary>
    /// 闸门变体：**参数打包**。把 Melee 扫描所需的全部"每元素只读"的指针与标量收进一个
    /// `struct Props`，由 job 以 `in Props` 传参 —— 生成 C++ 的形参从 70+ 个降到 ~10 个。
    /// 目的：检验"形参过多 ⇒ 热循环内从栈重载参数指针"这一假设（见 docs §16.45(aa)）。
    /// 算法与 <see cref="BenchMeleeScanJob"/> 逐行相同，A/B 时只差参数传递形状。
    /// </summary>
    public struct BenchScanProps
    {
        public int Length;
        public int CellsW;
        public int CellsH;
        public float InvCellSize;
        public float OriginX;
        public float OriginY;
        public float MyOrcaRadiusSq;
        public float MySeekR2;
        public float MyMass;
        public int OuterCap;
    }

    /// <summary>打包参数版的 Melee 扫描（同一个内核体，只改参数形状）。</summary>
    [NativeTranspile]
    public unsafe struct BenchMeleeScanPackJob : IJobParallelFor
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

            int outerProcessed = 0;
            int orcaCount = 0;
            float worstK2 = 0f;
            float closestEnemyD2 = 1000f * 1000f;
            int kBase = index * 8;
            int outerCap = Props.OuterCap;
            byte myTeam = Team[index];

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
                    if (jj < 9 && Team[i] != myTeam && d2 < closestEnemyD2 && d2 < Props.MySeekR2)
                        closestEnemyD2 = d2;
                }
            }
            KD2[kBase + 7] = worstK2 + closestEnemyD2;
        }
    }
}
