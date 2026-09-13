using System;
using System.Diagnostics;
using EntJoy.Collections;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using NativeTranspiler;

namespace EntJoySample.EnableBitmap
{
    // ═══════════════════════════════════════════════════════════════════════════
    // P1-6 / P1-7 运行期验收（2026-09-13）：
    //   P1-6 原生 IJobChunk 内核**读**逐组件 enable 位图，与托管侧一致（逐实体比对，不一致 = 0）；
    //   P1-7 原生内核**写**位图后，托管侧 `IsComponentEnabled` / `WithEnabled` 查询立刻反映。
    //
    // 数据面：`ChunkJobData.requiredEnableBitMaps`（与 requiredComponentArrays **同序**）
    //   → 生成的 C++ 包装里由 `ArchetypeChunk.GetEnableBitMapPtr<T>()` 取用：
    //     `reinterpret_cast<unsigned long long*>(__chunkData->requiredEnableBitMaps[requiredIdx])`
    //
    // 布局约定：本示例把 `Archetype.ChunkCapacityOverride` 设为实体总数 ⇒ **单 chunk**，
    //   于是"chunk 内序号 i" 就是全局序号，可以做逐实体比对（多 chunk 下原生 job 拿不到全局序号）。
    // ═══════════════════════════════════════════════════════════════════════════

    public struct EPos : IComponentData { public int V; }
    public struct EAlive : IComponentData, IEnableableComponent { public int V; }

    /// <summary>托管孪生：直接用位图判断启用，与原生 job 同算法（用于 A/B 对比）。</summary>
    public unsafe struct BitReadJobCs : IJobChunk
    {
        public NativeArray<int> Flag;
        public NativeArray<int> Val;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            var pos = chunk.GetComponentDataNativeArray<EPos>();
            ulong* bits = chunk.GetEnableBitMapPtr<EAlive>();
            for (int i = 0; i < pos.Length; i++)
            {
                int word = i >> 6;
                int bit = i & 63;
                bool on = ((bits[word] >> bit) & 1UL) != 0UL;
                Flag[i] = on ? 1 : 0;
                Val[i] = on ? pos[i].V : -1;
            }
        }
    }

    /// <summary>原生内核：完全相同的算法，走 NativeTranspile C++。</summary>
    [NativeTranspile(Target = BackendTarget.Cpp)]
    public unsafe struct BitReadJobCpp : IJobChunk
    {
        public NativeArray<int> Flag;
        public NativeArray<int> Val;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            var pos = chunk.GetComponentDataNativeArray<EPos>();
            ulong* bits = chunk.GetEnableBitMapPtr<EAlive>();
            for (int i = 0; i < pos.Length; i++)
            {
                int word = i >> 6;
                int bit = i & 63;
                bool on = ((bits[word] >> bit) & 1UL) != 0UL;
                Flag[i] = on ? 1 : 0;
                Val[i] = on ? pos[i].V : -1;
            }
        }
    }

    /// <summary>
    /// 原生内核**写**位图（P1-7）：i % Mod == 0 的实体被禁用，其余启用。
    ///
    /// 写成"按 64 位字整体写、且字值是该字索引的纯函数"是刻意的：位图是 1 bit/实体、64 实体/字，
    /// 若按实体逐位 read-modify-write，多 lane 命中同一字时后写覆盖先写；而按字整体写 + 纯函数值
    /// 即使两个 lane 命中同一字也写入相同值（幂等）⇒ 无丢更新。
    ///
    /// ⚠ 本 job 第一版给出的错误位图（782 字中 781 字错、popcount 25,006 而非 42,857）**不是**并发问题，
    ///   根因是转译器把 C# `1UL` 原样输出为 C++ `1UL`（Windows 上 `unsigned long` 是 32 位）⇒
    ///   `1UL << b`（b 可达 63）触发 `shift count >= width of type` 的 UB。框架已修（UL→ULL / L→LL），
    ///   下面的 Diag[3] 就是该修复的运行期判据（`(1UL<<40)>>32` 必须为 256）。
    /// </summary>
    [NativeTranspile(Target = BackendTarget.Cpp)]
    public unsafe struct BitWriteJobCpp : IJobChunk
    {
        public int Mod;
        /// <summary>诊断：[0]=体内看到的 Mod，[1]=wordCount，[2]=n，[3]=字面量后缀判据 `(int)((1UL&lt;&lt;40)&gt;&gt;32)`（必须 256）。</summary>
        public NativeArray<int> Diag;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            var pos = chunk.GetComponentDataNativeArray<EPos>();
            ulong* bits = chunk.GetEnableBitMapPtr<EAlive>();
            int n = pos.Length;
            int wordCount = (n + 63) >> 6;
            Diag[0] = Mod;
            Diag[1] = wordCount;
            Diag[2] = n;
            Diag[3] = (int)((1UL << 40) >> 32);
            for (int w = 0; w < wordCount; w++)
            {
                ulong v = 0UL;
                int baseIndex = w << 6;
                int limit = 64;
                if (baseIndex + limit > n) limit = n - baseIndex;
                for (int b = 0; b < limit; b++)
                {
                    int i = baseIndex + b;
                    if ((i % Mod) != 0) v = v | (1UL << b);
                }
                bits[w] = v;
            }
        }
    }

    /// <summary>
    /// P2-10 验证点：原生 **IJobEntity** + `NativeArray` 辅助表字段 + `Entity` 参数。
    /// （历史记载为"不支持"，实际只需按增量对拍确认；本 job 把结果写进按 `e.Id` 索引的辅助表。）
    /// </summary>
    [NativeTranspile(Target = BackendTarget.Cpp)]
    public struct EPosAddJobCpp : IJobEntity
    {
        public NativeArray<int> Out;
        public int Delta;

        public void Execute(Entity e, ref EPos p, in EAlive a)
        {
            p.V = p.V + Delta;
            Out[e.Id] = p.V;
        }
    }

    /// <summary>托管：把 chunk 的 enable 位图整字拷出来（诊断 + 判据：位图内容到底被写成了什么）。</summary>
    public unsafe struct BitDumpJobCs : IJobChunk
    {
        public NativeArray<ulong> Words;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            var pos = chunk.GetComponentDataNativeArray<EPos>();
            ulong* bits = chunk.GetEnableBitMapPtr<EAlive>();
            int wordCount = (pos.Length + 63) >> 6;
            for (int w = 0; w < wordCount; w++) Words[w] = bits[w];
        }
    }

    public static unsafe class EnableBitMapNativeDemo
    {
        private const int EntityCount = 50_000;
        private const int DisableEvery = 3;   // 初始：i % 3 == 0 禁用
        private const int WriteMod = 7;       // 原生写入：i % 7 == 0 禁用

        public static void Run()
        {
            Console.WriteLine("=== 13_EnableBitMapNative：原生 IJobChunk 读/写逐组件 enable 位图（P1-6 / P1-7）===\n");

            int savedOverride = Archetype.ChunkCapacityOverride;
            Archetype.ChunkCapacityOverride = EntityCount;   // 单 chunk ⇒ chunk 内序号 = 全局序号

            var sw = new Stopwatch();
            var world = new World("EnableBitMapNativeDemo");
            var em = world.EntityManager;

            // ── 1) 建实体：EPos.V = i，EAlive 按 i % 3 == 0 禁用 ──
            var entities = world.CreateEntities(EntityCount, typeof(EPos), typeof(EAlive));
            for (int i = 0; i < entities.Length; i++)
            {
                em.Set(entities[i], new EPos { V = i });
                em.Set(entities[i], new EAlive { V = i });
                em.SetComponentEnabled<EAlive>(entities[i], (i % DisableEvery) != 0);
            }

            var arch = FindArchetype(em);
            Console.WriteLine($"[1] 实体={entities.Length:N0} | chunk 容量覆盖={savedOverride}→{EntityCount} ⇒ chunk 数={arch.ChunkList.Count}"
                + $" | 初始：i % {DisableEvery} == 0 为**禁用**");

            // ── 2) 托管逐实体参考值（与被测路径完全独立：走 EntityManager API）──
            var expectFlag = new int[EntityCount];
            var expectVal = new int[EntityCount];
            long expectSum = 0;
            for (int i = 0; i < EntityCount; i++)
            {
                bool on = em.IsComponentEnabled<EAlive>(entities[i]);
                expectFlag[i] = on ? 1 : 0;
                expectVal[i] = on ? em.GetComponent<EPos>(entities[i]).V : -1;
                if (on) expectSum += i;
            }
            Console.WriteLine($"[2] 托管参考值：启用 {CountOn(expectFlag):N0} / {EntityCount:N0}，启用实体 EPos.V 之和={expectSum:N0}");

            var query = new QueryBuilder().WithAll<EPos, EAlive>();
            var flagCs = new NativeArray<int>(EntityCount, Allocator.Persistent);
            var valCs = new NativeArray<int>(EntityCount, Allocator.Persistent);
            var flagCpp = new NativeArray<int>(EntityCount, Allocator.Persistent);
            var valCpp = new NativeArray<int>(EntityCount, Allocator.Persistent);
            Fill(flagCs, -1); Fill(valCs, -1); Fill(flagCpp, -1); Fill(valCpp, -1);

            // ── 3) 托管孪生 job（位图读）──
            sw.Restart();
            new BitReadJobCs { Flag = flagCs, Val = valCs }.Schedule(query).Complete();
            sw.Stop();
            Console.WriteLine($"[3] 托管孪生 job（GetEnableBitMapPtr 托管实现）：{sw.Elapsed.TotalMilliseconds:F2} ms，"
                + $"与参考值不一致 flag={Compare(flagCs, expectFlag)} val={Compare(valCs, expectVal)}");

            // ── 4) 原生内核 job（同一算法，C++）──
            long badCppFlag = -1, badCppVal = -1, badCross = -1;
            try
            {
                sw.Restart();
                new BitReadJobCpp { Flag = flagCpp, Val = valCpp }.Schedule(query).Complete();
                sw.Stop();
                badCppFlag = Compare(flagCpp, expectFlag);
                badCppVal = Compare(valCpp, expectVal);
                badCross = Compare(flagCpp, flagCs);
                Console.WriteLine($"[4] 原生内核 job（[NativeTranspile] C++）：{sw.Elapsed.TotalMilliseconds:F2} ms，"
                    + $"与托管参考值不一致 flag={badCppFlag} val={badCppVal}，与托管孪生交叉不一致={badCross}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[4] 原生内核 job：**本机不可用**（{ex.GetType().Name}）——"
                    + "绑定已生成但 NativeTranspiled.dll 无该导出（未原生编译 / DLL 陈旧）。"
                    + "用 `dotnet run --project samples\\EntJoySample\\EntJoySample.csproj -c Release` 走完整原生编译即可。");
            }

            // ── 5) 托管查询过滤与位图一致（WithEnabled 口径）──
            long qSum = 0;
            int qCount = 0;
            foreach (var r in world.Query<EPos>().WithEnabled<EAlive>())
            {
                qSum += (long)r.Comp0.V;
                qCount++;
            }
            Console.WriteLine($"[5] 托管 `WithEnabled<EAlive>` 查询：{qCount:N0} 个，EPos.V 之和={qSum:N0}"
                + $" ⇒ 与参考值一致={((qCount == CountOn(expectFlag) && qSum == expectSum) ? 1 : 0)}");

            // ── 6) 原生内核**写**位图（P1-7）──
            var diag = new NativeArray<int>(8, Allocator.Persistent);
            for (int i = 0; i < diag.Length; i++) diag[i] = -1;
            try
            {
                sw.Restart();
                new BitWriteJobCpp { Mod = WriteMod, Diag = diag }.Schedule(query).Complete();
                sw.Stop();
                Console.WriteLine($"[6] 原生内核 job（写位图，i % {WriteMod} == 0 禁用）：{sw.Elapsed.TotalMilliseconds:F2} ms"
                    + $"（按 64 位字整体写；值=字索引的纯函数 ⇒ 跨 lane 幂等）");
                Console.WriteLine($"    原生体内读数：Mod={diag[0]}(期望 {WriteMod}) | wordCount={diag[1]}(期望 {(EntityCount + 63) >> 6})"
                    + $" | n={diag[2]}(期望 {EntityCount}) | `(1UL<<40)>>32`={diag[3]}(期望 256；修复前为 -4 ⇒ C++ 把 1UL 当 32 位)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[6] 原生写位图：**本机不可用**（{ex.GetType().Name}）");
            }

            // ── 7) 原生写之后：托管侧立即一致 ──
            long afterMis = 0, afterOn = 0;
            for (int i = 0; i < EntityCount; i++)
            {
                bool on = em.IsComponentEnabled<EAlive>(entities[i]);
                bool want = (i % WriteMod) != 0;
                if (on != want) afterMis++;
                if (on) afterOn++;
            }
            long qCount2 = 0, qSum2 = 0;
            foreach (var r in world.Query<EPos>().WithEnabled<EAlive>())
            {
                qCount2++;
                qSum2 += (long)r.Comp0.V;
            }
            Console.WriteLine($"[7] 原生写后逐实体比对 `em.IsComponentEnabled<EAlive>`：不一致={afterMis}（应为 0）"
                + $"，启用 {afterOn:N0} / {EntityCount:N0}");
            Console.WriteLine($"    原生写后 `WithEnabled<EAlive>` 查询：{qCount2:N0} 个，与逐实体一致={(qCount2 == afterOn ? 1 : 0)}");

            // ── 7b) 诊断：位图**整字**内容 vs 期望纯函数（定位"写到哪去了"）──
            int wordCount = (EntityCount + 63) >> 6;
            var words = new NativeArray<ulong>(wordCount, Allocator.Persistent);
            new BitDumpJobCs { Words = words }.Schedule(query).Complete();
            int badWords = 0, firstBadWord = -1;
            ulong firstGot = 0, firstWant = 0;
            for (int w = 0; w < wordCount; w++)
            {
                ulong want = 0UL;
                int baseIndex = w << 6;
                for (int b = 0; b < 64; b++)
                {
                    int i = baseIndex + b;
                    if (i >= EntityCount) break;
                    if ((i % WriteMod) != 0) want = want | (1UL << b);
                }
                if (words[w] != want)
                {
                    badWords++;
                    if (firstBadWord < 0) { firstBadWord = w; firstGot = words[w]; firstWant = want; }
                }
            }
            Console.WriteLine($"[7b] 位图逐字比对（{wordCount} 个字）：不一致字={badWords}，首个不一致字={firstBadWord}"
                + $"，got=0x{firstGot:X16} want=0x{firstWant:X16}，popcount(全体)={PopCount(words):N0}");
            words.Dispose();

            bool pass = badCppFlag == 0 && badCppVal == 0 && badCross == 0 && afterMis == 0 && qCount == CountOn(expectFlag) && qCount2 == afterOn;

            // ── 8) P2-10：原生 IJobEntity + NativeArray 辅助表字段 + Entity 参数 ──
            var outTable = new NativeArray<int>(EntityCount, Allocator.Persistent);
            Fill(outTable, -1);
            long badEntity = -1;
            try
            {
                sw.Restart();
                new EPosAddJobCpp { Out = outTable, Delta = 1 }.Schedule(query).Complete();
                sw.Stop();
                badEntity = 0;
                for (int i = 0; i < EntityCount; i++)
                    if (outTable[entities[i].Id] != i + 1) badEntity++;
                Console.WriteLine($"[8] 原生 IJobEntity + `NativeArray` 辅助表（P2-10）：{sw.Elapsed.TotalMilliseconds:F2} ms，"
                    + $"逐实体不一致={badEntity}（应 0：Out[e.Id] == EPos.V + 1）");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[8] 原生 IJobEntity（P2-10）：**本机不可用**（{ex.GetType().Name}）");
            }
            outTable.Dispose();
            pass = pass && badEntity == 0;
            Console.WriteLine($"\n判定：{(pass ? "PASS（读一致 + 写可见）" : "未通过或不完整（原生不可用时 [4]/[6] 见上）")}");

            flagCs.Dispose(); valCs.Dispose(); flagCpp.Dispose(); valCpp.Dispose();
            world.Dispose();
            Archetype.ChunkCapacityOverride = savedOverride;
            Console.WriteLine("\n=== 13_EnableBitMapNative 示例结束 ===\n");
        }

        private static void Fill(NativeArray<int> arr, int v)
        {
            for (int i = 0; i < arr.Length; i++) arr[i] = v;
        }

        private static int CountOn(int[] flags)
        {
            int n = 0;
            for (int i = 0; i < flags.Length; i++) if (flags[i] == 1) n++;
            return n;
        }

        private static long PopCount(NativeArray<ulong> words)
        {
            long n = 0;
            for (int i = 0; i < words.Length; i++)
            {
                ulong v = words[i];
                while (v != 0UL) { v = v & (v - 1UL); n++; }
            }
            return n;
        }

        private static long Compare(NativeArray<int> got, int[] expect)
        {
            long bad = 0;
            for (int i = 0; i < expect.Length; i++)
                if (got[i] != expect[i]) bad++;
            return bad;
        }

        private static long Compare(NativeArray<int> a, NativeArray<int> b)
        {
            long bad = 0;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) bad++;
            return bad;
        }

        private static Archetype FindArchetype(EntityManager em)
        {
            var archs = em.Archetypes;
            for (int i = 0; i < em.ArchetypeCount; i++)
                if (archs[i] != null && archs[i].Has(typeof(EPos))) return archs[i];
            throw new InvalidOperationException("未找到含 EPos 的 Archetype");
        }
    }
}
