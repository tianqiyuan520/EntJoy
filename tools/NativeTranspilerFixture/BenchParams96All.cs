using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace NativeTranspilerFixture
{
    public struct BenchParams96In
    {
        public NativeArray<int> PadArr0, PadArr1, PadArr2, PadArr3, PadArr4, PadArr5, PadArr6, PadArr7;
        public NativeArray<int> PadArr8, PadArr9, PadArr10, PadArr11, PadArr12, PadArr13, PadArr14, PadArr15;
        public NativeArray<int> PadArr16, PadArr17, PadArr18, PadArr19, PadArr20, PadArr21, PadArr22, PadArr23;
        public NativeArray<int> PadArr24, PadArr25, PadArr26, PadArr27, PadArr28, PadArr29, PadArr30, PadArr31;
        public NativeArray<EntJoy.Mathematics.float2> Positions;
        public NativeArray<int> ConfigId;
        public NativeArray<byte> Team;
        public NativeArray<int> CellStart;
        public NativeArray<int> SortedIndex;
        public NativeArray<int> ScanOrder;
        public int CellsW, CellsH;
        public float InvCellSize, OriginX, OriginY, MyOrcaRadiusSq, MySeekR2, MyMass;
        public int OuterCap;
    }

    [NativeTranspile]
    public unsafe struct BenchParams96AllJob : IJobParallelFor
    {
        public BenchParams96In In;
        public NativeArray<float> KD2;
        public NativeArray<int> KPeer;
        public NativeArray<int> Counters;

        public void Execute(int index)
        {
            var p = In.Positions[index];
            int cx = (int)(p.x * In.InvCellSize + In.OriginX);
            int cy = (int)(p.y * In.InvCellSize + In.OriginY);
            cx = System.Math.Max(0, System.Math.Min(cx, In.CellsW - 1));
            cy = System.Math.Max(0, System.Math.Min(cy, In.CellsH - 1));
            int hashId = cy * In.CellsW + cx;
            int gridCells = In.CellsW * In.CellsH;

            int outerProcessed = 0, orcaCount = 0, kBase = index * 8;
            float worstK2 = 0f, closestEnemyD2 = 1000f * 1000f;
            byte myTeam = In.Team[index];
            int* counters = (int*)Counters.GetUnsafePtr();

            for (int jj = 0; jj < 81; jj++)
            {
                int j = In.ScanOrder[jj];
                bool isCenter = jj == 0;
                if (!isCenter && outerProcessed >= In.OuterCap) break;
                int newHash = hashId + (j % 9 - 4) - (4 - j / 9) * In.CellsW;
                if (newHash < 0 || newHash >= gridCells) continue;
                int start = In.CellStart[newHash], end = In.CellStart[newHash + 1];
                for (int s = start; s < end; s++)
                {
                    int i = In.SortedIndex[s];
                    if (i == index) continue;
                    if (!isCenter) { if (outerProcessed >= In.OuterCap) break; outerProcessed++; }
                    var q = In.Positions[i];
                    float dx = p.x - q.x, dy = p.y - q.y;
                    float d2 = dx * dx + dy * dy;
                    System.Threading.Interlocked.Increment(ref counters[0]);
                    if (d2 < In.MyOrcaRadiusSq && (orcaCount < 8 || d2 < worstK2))
                    {
                        float peerMass = In.ConfigId[i] == 2 ? 20f : 1f;
                        if (peerMass / (In.MyMass + peerMass + 1e-6f) >= 0.1f)
                        {
                            int slot = orcaCount < 8 ? orcaCount : 8;
                            while (slot > 0 && KD2[kBase + slot - 1] > d2)
                            {
                                if (slot < 8) { KD2[kBase + slot] = KD2[kBase + slot - 1]; KPeer[kBase + slot] = KPeer[kBase + slot - 1]; }
                                slot--;
                            }
                            KD2[kBase + slot] = d2; KPeer[kBase + slot] = i;
                            if (orcaCount < 8) orcaCount++;
                            worstK2 = KD2[kBase + 7];
                            System.Threading.Interlocked.Increment(ref counters[1]);
                        }
                    }
                    if (jj < 9 && In.Team[i] != myTeam && d2 < closestEnemyD2 && d2 < In.MySeekR2)
                    {
                        closestEnemyD2 = d2;
                        System.Threading.Interlocked.Increment(ref counters[2]);
                    }
                }
            }
            KD2[kBase + 7] = worstK2 + closestEnemyD2;
        }
    }
}