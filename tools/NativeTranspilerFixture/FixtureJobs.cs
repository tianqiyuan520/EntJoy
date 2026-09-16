using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace NativeTranspilerFixture
{
    /// <summary>
    /// F-5 夹具：IJobParallelForBatch。<c>Execute(startIndex, count)</c> 是一次**区间**调用，
    /// C++ 侧不得再包一层 index 循环，且两个形参必须分别映射到 <c>__startIndex</c>/<c>__count</c>。
    /// </summary>
    [NativeTranspile]
    public struct BatchFillJob : IJobParallelForBatch
    {
        public NativeArray<int> Out;
        public NativeArray<int> Visits;

        public void Execute(int startIndex, int count)
        {
            for (int i = startIndex; i < startIndex + count; i++)
            {
                Visits[i] = Visits[i] + 1;   // 每个 index 必须恰好被处理一次
                Out[i] = count;              // 区间长度形参必须可用
            }
        }
    }

    /// <summary>
    /// F-4 夹具：批体内 <c>return;</c> 只结束**本次 index**（C# 语义），
    /// 不得退出整个批（旧实现会跳过本批剩余下标）。
    /// </summary>
    [NativeTranspile]
    public struct EarlyReturnJob : IJobParallelFor
    {
        public NativeArray<int> Mark;

        public void Execute(int index)
        {
            if (index == 0) return;
            Mark[index] = index + 1;
        }
    }

    /// <summary>F-4 夹具（带前后语句）：return 之前的语句必须执行、之后的必须被跳过。</summary>
    [NativeTranspile]
    public struct EarlyReturnDeepJob : IJobParallelFor
    {
        public NativeArray<int> Before;
        public NativeArray<int> After;

        public void Execute(int index)
        {
            Before[index] = 1;
            if (index % 3 == 0) return;
            After[index] = 2;
        }
    }

    /// <summary>
    /// **代码生成质量闸门**（bench 用，非语义夹具）。kernel 形状**逐行镜像真实内核**：
    /// - `CountCellsJob`（CPUBattleSpatialHash.cs:165-177）的谓词 + float2 读 + 钳制 + 哈希 + 原子递增；
    /// - `MeleeSimJob`（CPUBattleCombat.cs:243-330）的邻居扫描：d² + 半径门 + alpha 门 + 8 槽有序插入 + 索敌。
    /// 为什么放这里：真实内核 1 秒只能跑几次、且混着框架与仿真；本 fixture 把 kernel 单独拿出来按
    /// "每元素"重复调用，周转从分钟级降到秒级，从而让"改发射规则 ⇒ 每元素周期数下降"**可判定**。
    /// 判据（runner 侧）：同一台机、同一编译标志下比较各臂的 ns/元素；**不是**墙钟端到端。
    /// </summary>
    [NativeTranspile]
    public unsafe struct BenchCountCellsJob : IJobParallelFor
    {
        public NativeArray<EntJoy.Mathematics.float2> Positions;
        public NativeArray<byte> Alive;
        public NativeArray<int> State;
        public NativeArray<int> Counts;
        public int Length;
        public float InvCellSize;
        public int CellsW;
        public int CellsH;
        public float OriginX;
        public float OriginY;
        public int StateDeath;

        public void Execute(int index)
        {
            if (index < Length && Alive[index] != 0 && State[index] != StateDeath)
            {
                var p = Positions[index];
                int cx = (int)(p.x * InvCellSize + OriginX);
                int cy = (int)(p.y * InvCellSize + OriginY);
                cx = System.Math.Max(0, System.Math.Min(cx, CellsW - 1));
                cy = System.Math.Max(0, System.Math.Min(cy, CellsH - 1));
                int hash = cy * CellsW + cx;
                int* countsPtr = (int*)Counts.GetUnsafePtr();
                System.Threading.Interlocked.Increment(ref countsPtr[hash]);
            }
        }
    }

    /// <summary>闸门（Melee 形状）：按顺序表的 81 格扫描 + d² + 半径门 + alpha 门 + 8 槽有序插入 + 索敌。</summary>
    [NativeTranspile]
    public unsafe struct BenchMeleeScanJob : IJobParallelFor
    {
        public NativeArray<EntJoy.Mathematics.float2> Positions;
        public NativeArray<int> ConfigId;
        public NativeArray<byte> Team;
        public NativeArray<int> CellStart;
        public NativeArray<int> SortedIndex;
        public NativeArray<int> ScanOrder;
        public NativeArray<float> KD2;
        public NativeArray<int> KPeer;
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

        public void Execute(int index)
        {
            var p = Positions[index];
            int cx = (int)(p.x * InvCellSize + OriginX);
            int cy = (int)(p.y * InvCellSize + OriginY);
            cx = System.Math.Max(0, System.Math.Min(cx, CellsW - 1));
            cy = System.Math.Max(0, System.Math.Min(cy, CellsH - 1));
            int hashId = cy * CellsW + cx;
            int gridCells = CellsW * CellsH;

            int outerProcessed = 0;
            int orcaCount = 0;
            float worstK2 = 0f;
            float closestEnemyD2 = 1000f * 1000f;
            int kBase = index * 8;
            byte myTeam = Team[index];

            for (int jj = 0; jj < 81; jj++)
            {
                int j = ScanOrder[jj];
                bool isCenter = jj == 0;
                if (!isCenter && outerProcessed >= OuterCap) break;
                int ox = j % 9 - 4;
                int oy = 4 - j / 9;
                int newHash = hashId + ox - oy * CellsW;
                if (newHash < 0 || newHash >= gridCells) continue;
                int start = CellStart[newHash];
                int end = CellStart[newHash + 1];
                for (int s = start; s < end; s++)
                {
                    int i = SortedIndex[s];
                    if (i == index) continue;
                    if (!isCenter)
                    {
                        if (outerProcessed >= OuterCap) break;
                        outerProcessed++;
                    }
                    var q = Positions[i];
                    float dx = p.x - q.x;
                    float dy = p.y - q.y;
                    float d2 = dx * dx + dy * dy;
                    if (d2 < MyOrcaRadiusSq && (orcaCount < 8 || d2 < worstK2))
                    {
                        float peerMass = ConfigId[i] == 2 ? 20f : 1f;
                        if (peerMass / (MyMass + peerMass + 1e-6f) >= 0.1f)
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
                    if (jj < 9 && Team[i] != myTeam && d2 < closestEnemyD2 && d2 < MySeekR2)
                        closestEnemyD2 = d2;
                }
            }
            KD2[kBase + 7] = worstK2 + closestEnemyD2;
        }
    }

    /// <summary>闸门辅助：为每个 cell 预编译"邻域格列表"（9×9 展开、**已钳制到合法格**、按 cell 顺序）。
    /// 相比逐候选列表省 21 倍内存（只存格号不存槽号），且扫描时不再需要逐格查 CellStart。</summary>
    [NativeTranspile]
    public unsafe struct BenchCompileCellsJob : IJobParallelFor
    {
        public NativeArray<int> ScanOrder;
        public NativeArray<int> CompStart;   // [Cells+1]：每 cell 邻域格列表区间
        public NativeArray<int> CompCell;    // 邻域格号（顺序、已钳制）
        public int CellsW;
        public int CellsH;

        public void Execute(int cell)
        {
            int gridCells = CellsW * CellsH;
            int* order = (int*)ScanOrder.GetUnsafePtr();
            int* compCell = (int*)CompCell.GetUnsafePtr();
            int* compStart = (int*)CompStart.GetUnsafePtr();
            int write = compStart[cell];
            for (int jj = 0; jj < 81; jj++)
            {
                int j = order[jj];
                int nh = cell + (j % 9 - 4) - (4 - j / 9) * CellsW;
                if (nh < 0 || nh >= gridCells) continue;
                compCell[write++] = nh;
            }
            compStart[cell + 1] = write;
        }
    }

    /// <summary>闸门变体：扫描**预编译的邻域格列表**（无逐格 `hash→CellStart` 查表、无越界判断），
    /// 其余逻辑与 <see cref="BenchMeleeScanJob"/> 逐行相同。</summary>
    [NativeTranspile]
    public unsafe struct BenchMeleeScanCompJob : IJobParallelFor
    {
        public NativeArray<EntJoy.Mathematics.float2> Positions;
        public NativeArray<int> ConfigId;
        public NativeArray<byte> Team;
        public NativeArray<int> CellStart;
        public NativeArray<int> SortedIndex;
        public NativeArray<int> CompStart;
        public NativeArray<int> CompCell;
        public NativeArray<float> KD2;
        public NativeArray<int> KPeer;
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
        public int RadarCells;   // 前 N 个邻域格参与索敌（对应原 jj<9）

        public void Execute(int index)
        {
            var p = Positions[index];
            int cx = (int)(p.x * InvCellSize + OriginX);
            int cy = (int)(p.y * InvCellSize + OriginY);
            cx = System.Math.Max(0, System.Math.Min(cx, CellsW - 1));
            cy = System.Math.Max(0, System.Math.Min(cy, CellsH - 1));
            int cell = cy * CellsW + cx;

            int g0 = CompStart[cell];
            int g1 = CompStart[cell + 1];

            int outerProcessed = 0;
            int orcaCount = 0;
            float worstK2 = 0f;
            float closestEnemyD2 = 1000f * 1000f;
            int kBase = index * 8;
            byte myTeam = Team[index];
            int* compCell = (int*)CompCell.GetUnsafePtr();
            int* cs = (int*)CellStart.GetUnsafePtr();
            int* sortedPtr = (int*)SortedIndex.GetUnsafePtr();

            for (int g = g0; g < g1; g++)
            {
                bool isCenter = g == g0;
                if (!isCenter && outerProcessed >= OuterCap) break;
                int nh = compCell[g];
                int s0 = cs[nh];
                int s1 = cs[nh + 1];
                for (int s = s0; s < s1; s++)
                {
                    int i = sortedPtr[s];
                    if (i == index) continue;
                    if (!isCenter)
                    {
                        if (outerProcessed >= OuterCap) break;
                        outerProcessed++;
                    }
                    var q = Positions[i];
                    float dx = p.x - q.x;
                    float dy = p.y - q.y;
                    float d2 = dx * dx + dy * dy;
                    if (d2 < MyOrcaRadiusSq && (orcaCount < 8 || d2 < worstK2))
                    {
                        float peerMass = ConfigId[i] == 2 ? 20f : 1f;
                        if (peerMass / (MyMass + peerMass + 1e-6f) >= 0.1f)
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
                    if (!isCenter && g - g0 < RadarCells
                        && Team[i] != myTeam && d2 < closestEnemyD2 && d2 < MySeekR2)
                        closestEnemyD2 = d2;
                }
            }
            KD2[kBase + 7] = worstK2 + closestEnemyD2;
        }
    }
}
