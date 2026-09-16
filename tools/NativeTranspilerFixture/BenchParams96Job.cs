using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace NativeTranspilerFixture
{
    /// <summary>闸门：96 形参规模的 job（32 个 NativeArray + 32 个标量），复刻 MeleeSimJob 的字段规模。
    /// 内核体与 BenchMeleeScanJob 同构（81 格扫描 + d2 + 半径门 + alpha 门 + 8 槽插入 + 索敌），
    /// 额外 32 个未读字段只为撑开形参表——用来判定"形参数是否真是瓶颈"。
    /// 计数器 counters[0..2] 用于校验和，必须三臂一致。</summary>
    [NativeTranspile]
    public unsafe struct BenchParams96Job : IJobParallelFor
    {
        public NativeArray<int> PadArr0;
        public NativeArray<int> PadArr1;
        public NativeArray<int> PadArr2;
        public NativeArray<int> PadArr3;
        public NativeArray<int> PadArr4;
        public NativeArray<int> PadArr5;
        public NativeArray<int> PadArr6;
        public NativeArray<int> PadArr7;
        public NativeArray<int> PadArr8;
        public NativeArray<int> PadArr9;
        public NativeArray<int> PadArr10;
        public NativeArray<int> PadArr11;
        public NativeArray<int> PadArr12;
        public NativeArray<int> PadArr13;
        public NativeArray<int> PadArr14;
        public NativeArray<int> PadArr15;
        public NativeArray<int> PadArr16;
        public NativeArray<int> PadArr17;
        public NativeArray<int> PadArr18;
        public NativeArray<int> PadArr19;
        public NativeArray<int> PadArr20;
        public NativeArray<int> PadArr21;
        public NativeArray<int> PadArr22;
        public NativeArray<int> PadArr23;
        public NativeArray<int> PadArr24;
        public NativeArray<int> PadArr25;
        public NativeArray<int> PadArr26;
        public NativeArray<int> PadArr27;
        public NativeArray<int> PadArr28;
        public NativeArray<int> PadArr29;
        public NativeArray<int> PadArr30;
        public NativeArray<int> PadArr31;
        public int PadScalar0;
        public int PadScalar1;
        public int PadScalar2;
        public int PadScalar3;
        public int PadScalar4;
        public int PadScalar5;
        public int PadScalar6;
        public int PadScalar7;
        public int PadScalar8;
        public int PadScalar9;
        public int PadScalar10;
        public int PadScalar11;
        public int PadScalar12;
        public int PadScalar13;
        public int PadScalar14;
        public int PadScalar15;
        public int PadScalar16;
        public int PadScalar17;
        public int PadScalar18;
        public int PadScalar19;
        public int PadScalar20;
        public int PadScalar21;
        public int PadScalar22;
        public int PadScalar23;
        public int PadScalar24;
        public int PadScalar25;
        public int PadScalar26;
        public int PadScalar27;
        public int PadScalar28;
        public int PadScalar29;
        public int PadScalar30;
        public int PadScalar31;
        public NativeArray<EntJoy.Mathematics.float2> Positions;
        public NativeArray<int> ConfigId;
        public NativeArray<byte> Team;
        public NativeArray<int> CellStart;
        public NativeArray<int> SortedIndex;
        public NativeArray<int> ScanOrder;
        public NativeArray<float> KD2;
        public NativeArray<int> KPeer;
        public NativeArray<int> Counters;
        public int CellsW;
        public int CellsH;
        public float InvCellSize;
        public float OriginX;
        public float OriginY;
        public float MyOrcaRadiusSq;
        public float MySeekR2;
        public float MyMass;
        public int OuterCap;

        public void Execute(int index)
        {
            var p = Positions[index];
            int cx = (int)(p.x * InvCellSize + OriginX);
            int cy = (int)(p.y * InvCellSize + OriginY);
            cx = System.Math.Max(0, System.Math.Min(cx, CellsW - 1));
            cy = System.Math.Max(0, System.Math.Min(cy, CellsH - 1));
            int hashId = cy * CellsW + cx;
            int gridCells = CellsW * CellsH;

            int outerProcessed = 0, orcaCount = 0, kBase = index * 8;
            float worstK2 = 0f, closestEnemyD2 = 1000f * 1000f;
            byte myTeam = Team[index];
            int* counters = (int*)Counters.GetUnsafePtr();

            for (int jj = 0; jj < 81; jj++)
            {
                int j = ScanOrder[jj];
                bool isCenter = jj == 0;
                if (!isCenter && outerProcessed >= OuterCap) break;
                int newHash = hashId + (j % 9 - 4) - (4 - j / 9) * CellsW;
                if (newHash < 0 || newHash >= gridCells) continue;
                int start = CellStart[newHash], end = CellStart[newHash + 1];
                for (int s = start; s < end; s++)
                {
                    int i = SortedIndex[s];
                    if (i == index) continue;
                    if (!isCenter) { if (outerProcessed >= OuterCap) break; outerProcessed++; }
                    var q = Positions[i];
                    float dx = p.x - q.x, dy = p.y - q.y;
                    float d2 = dx * dx + dy * dy;
                    System.Threading.Interlocked.Increment(ref counters[0]);
                    if (d2 < MyOrcaRadiusSq && (orcaCount < 8 || d2 < worstK2))
                    {
                        float peerMass = ConfigId[i] == 2 ? 20f : 1f;
                        if (peerMass / (MyMass + peerMass + 1e-6f) >= 0.1f)
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
                    if (jj < 9 && Team[i] != myTeam && d2 < closestEnemyD2 && d2 < MySeekR2)
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
