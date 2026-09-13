using System;
using System.Diagnostics;
using EntJoy.Collections;
using EntJoy.ECS;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace EntJoySample.NativeLookup
{
    // ═══════════════════════════════════════════════════════════════════════════
    // 本轮框架新增能力的可运行示例（2026-09-13）：
    //   P0-1 blittable 实体定位表（EntityLocateB）
    //   P0-3 job-safe 跨 chunk 组件随机访问（NativeComponentLookup<T> / NativeEntityLookup）
    //   P0-4/P0-5/P1-8 ECB 批量创建 + 批量写列（零分配回放）
    //   P0-4b 批量销毁 / DestroyAllInArchetype（ClearAll 快路径）
    //   P0-4c 销毁/创建路径零托管分配（回收池非托管栈 + 隐性分配修复）
    // ═══════════════════════════════════════════════════════════════════════════

    public struct LPos : IComponentData { public int V; }
    public struct LTag : IComponentData { public int V; }

    /// <summary>
    /// **托管并行 job**：用 job-safe 句柄做跨 chunk 邻居随机访问。
    /// 对比旧的 <see cref="ComponentLookup{T}"/>（自述 main-thread only、含可变缓存），
    /// 本句柄只含裸指针与整数 ⇒ 可按值传给并行 job、可多线程只读共享。
    /// </summary>
    public unsafe struct LookupManagedJob : IJobParallelFor
    {
        public NativeComponentLookup<LPos> Pos;
        public NativeArray<int> NeighborStart;
        public NativeArray<int> NeighborCount;
        public NativeArray<int> NeighborIds;
        public NativeArray<int> Out;

        public void Execute(int index)
        {
            int start = NeighborStart[index];
            int n = NeighborCount[index];
            int sum = 0;
            for (int k = 0; k < n; k++)
            {
                int id = NeighborIds[start + k];
                if (!Pos.IsAllocated(id)) continue;
                LPos* p = Pos.UnsafeResolve(id);
                sum += p->V;
            }
            Out[index] = sum;
        }
    }

    /// <summary>
    /// **原生内核版**：同样的跨 chunk 随机访问，但走 NativeTranspile（C++ 内核）。
    ///
    /// ⚠ 关键纪律（实测 NT004）：原生 job **不能调用 EntJoy.ECS 里的任何方法**（实例方法、跨程序集静态方法都报
    /// `NT004: … is not a static method in the same assembly`）⇒ 必须在 job 体内**内联字段运算**，
    /// 即直接访问 `Lookup.Locate[id].ChunkMemory / ChunkOffsets / SlotInChunk`。
    /// C++ 侧的对等原语见 `src/NativeDll/NativeEntityLookup.h`（手写 C++ 可用，不受 NT004 约束）。
    ///
    /// P0-5b 通解：`ref` 局部（`ref EntityLocateB ep = ref Lookup.Locate[id];`）现在由转译器翻成
    /// C++ 引用 `EntityLocateB& ep = Lookup.Locate[id];`（此前在 `Nullable=enable` 工程里会打崩生成器）。
    /// </summary>
    [NativeTranspile(Target = BackendTarget.Cpp)]
    public unsafe struct LookupNativeJob : IJobParallelFor
    {
        public NativeComponentLookup<LPos> Lookup;
        public NativeArray<int> NeighborStart;
        public NativeArray<int> NeighborCount;
        public NativeArray<int> NeighborIds;
        public NativeArray<int> Out;
        /// <summary>组件元素字节数（宿主传 sizeof(LPos)）：转译器对 sizeof(T) 的支持面未知，故显式传。</summary>
        public int Stride;

        public void Execute(int index)
        {
            int start = NeighborStart[index];
            int n = NeighborCount[index];
            int sum = 0;
            for (int k = 0; k < n; k++)
            {
                int id = NeighborIds[start + k];
                if ((uint)id >= (uint)Lookup.Length) continue;
                // P0-5b 通解验证点：`ref` 局部引用指针数组元素（转译为 C++ `EntityLocateB& ep = Lookup.Locate[id];`）
                ref EntityLocateB ep = ref Lookup.Locate[id];
                if (ep.ChunkMemory == null || ep.SlotInChunk < 0) continue;
                LPos* p = (LPos*)((byte*)ep.ChunkMemory + ep.ChunkOffsets[Lookup.ComponentIndex] + ep.SlotInChunk * Stride);
                sum += p->V;
            }
            Out[index] = sum;
        }
    }

    public static unsafe class EntityNativeLookupDemo
    {
        private const int EntityCount = 200_000;
        private const int NeighborsPerEntity = 4;
        private const int EcbBatch = 65_536;

        public static void Run()
        {
            Console.WriteLine("=== 12_EntityNativeLookup：原生可用定位表 / job-safe lookup / ECB 批量 ===\n");
            var sw = new Stopwatch();
            var world = new World("EntityNativeLookupDemo");
            var em = world.EntityManager;

            // ── 1) 建实体并写入已知值 ──
            var entities = world.CreateEntities(EntityCount, typeof(LPos), typeof(LTag));
            for (int i = 0; i < entities.Length; i++)
            {
                ref var p = ref em.GetComponent<LPos>(entities[i]);
                p.V = entities[i].Id * 3 + 1;
            }
            var arch = FindArchetype(em);
            Console.WriteLine($"[1] 实体={entities.Length:N0} | Archetype chunk 数={arch.ChunkList.Count}（默认 chunk 容量 ⇒ 组件列**不连续**）");
            Console.WriteLine($"    定位表容量={em.LocateCapacity:N0} | 每实体定位项 {sizeof(EntityLocateB)} B（chunk 基址 + 列偏移表 + 槽位 + 版本）");

            // ── 2) 造邻居表（每单位 4 个跨 chunk 邻居）──
            int nl = entities.Length * NeighborsPerEntity;
            var nStart = new NativeArray<int>(entities.Length, Allocator.Persistent);
            var nCount = new NativeArray<int>(entities.Length, Allocator.Persistent);
            var nIds = new NativeArray<int>(nl, Allocator.Persistent);
            var outManaged = new NativeArray<int>(entities.Length, Allocator.Persistent);
            var outNative = new NativeArray<int>(entities.Length, Allocator.Persistent);
            for (int i = 0; i < entities.Length; i++)
            {
                nStart[i] = i * NeighborsPerEntity;
                nCount[i] = NeighborsPerEntity;
                for (int k = 0; k < NeighborsPerEntity; k++)
                    nIds[nStart[i] + k] = entities[(i + 1 + k * 7) % entities.Length].Id;
            }

            // 参考值：用托管逐实体读取算一遍（与被测路径完全独立的算法）
            var expect = new int[entities.Length];
            for (int i = 0; i < entities.Length; i++)
            {
                int sum = 0;
                for (int k = 0; k < NeighborsPerEntity; k++)
                {
                    int nb = nIds[nStart[i] + k];
                    sum += em.GetComponent<LPos>(entities[nb]).V;
                }
                expect[i] = sum;
            }

            // ── 3) 托管并行 job：job-safe lookup 跨 chunk 随机访问 ──
            sw.Restart();
            new LookupManagedJob
            {
                Pos = em.CreateNativeLookup<LPos>(arch),
                NeighborStart = nStart,
                NeighborCount = nCount,
                NeighborIds = nIds,
                Out = outManaged,
            }.Schedule(entities.Length, 0).Complete();
            sw.Stop();
            long badManaged = Compare(outManaged, expect);
            Console.WriteLine($"[2] 托管并行 job（NativeComponentLookup）：{sw.Elapsed.TotalMilliseconds:F2} ms，"
                + $"逐实体与参考值不一致={badManaged}");

            // ── 4) 原生内核 job（NativeTranspile C++）：同样的事，体内内联算术 ──
            // ⚠ 原生内核需要 NativeTranspiled.dll 里存在本 job 的导出：绑定已生成但**未原生编译**
            //   （如 -p:EnableNativeCompile=false 或 DLL 陈旧）时，调度会抛异常 ⇒ 本示例兜底跳过并说明。
            sw.Restart();
            try
            {
                new LookupNativeJob
                {
                    Lookup = em.CreateNativeLookup<LPos>(arch),
                    NeighborStart = nStart,
                    NeighborCount = nCount,
                    NeighborIds = nIds,
                    Out = outNative,
                    Stride = sizeof(LPos),
                }.Schedule(entities.Length, 0).Complete();
                sw.Stop();
                long badNative = Compare(outNative, expect);
                Console.WriteLine($"[3] 原生内核 job（[NativeTranspile]）：{sw.Elapsed.TotalMilliseconds:F2} ms，"
                    + $"逐实体与参考值不一致={badNative}");
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.WriteLine($"[3] 原生内核 job：**本机不可用**（{ex.GetType().Name}）——"
                    + $"绑定已生成但 NativeTranspiled.dll 里没有该 job 的导出（未原生编译 / DLL 陈旧）。"
                    + $"在有 ClangCL 的环境用 `dotnet run --project samples\\EntJoySample\\EntJoySample.csproj -c Release` 走完整原生编译即可。");
            }
            Console.WriteLine($"    纪律：NT004 禁止原生 job 调用 EntJoy.ECS 的方法 ⇒ 体内内联；"
                + $"ref 局部已支持（P0-5b 通解：转译为 C++ 引用），本 job 用的就是 `ref EntityLocateB ep = ref Lookup.Locate[id];`");

            // ── 5) ECB 批量创建 + 批量写列（零分配回放）──
            var posValues = new NativeArray<LPos>(EcbBatch, Allocator.Persistent);
            var tagValues = new NativeArray<LTag>(EcbBatch, Allocator.Persistent);
            for (int i = 0; i < EcbBatch; i++)
            {
                posValues[i] = new LPos { V = 10_000 + i };
                tagValues[i] = new LTag { V = 20_000 + i };
            }
            using (var ecb = new DeferredCommandBuffer())
            {
                int batch = ecb.CreateEntitiesRange(EcbBatch, typeof(LPos), typeof(LTag));
                ecb.SetComponentRange(batch, posValues);
                ecb.SetComponentRange(batch, tagValues);
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                sw.Restart();
                ecb.Playback(em);
                sw.Stop();
                long alloc = GC.GetAllocatedBytesForCurrentThread() - a0;

                long badPos = 0, badTag = 0;
                for (int i = 0; i < ecb.CreatedEntityCount; i++)
                {
                    if (em.GetComponent<LPos>(ecb.CreatedEntities[i]).V != 10_000 + i) badPos++;
                    if (em.GetComponent<LTag>(ecb.CreatedEntities[i]).V != 20_000 + i) badTag++;
                }
                Console.WriteLine($"[4] ECB 批量创建 {ecb.CreatedEntityCount:N0} + 2 列批量写：{sw.Elapsed.TotalMilliseconds:F2} ms，"
                    + $"值不一致 pos={badPos} tag={badTag}");
                Console.WriteLine($"    冷启动回放期托管分配={alloc} B（含托管实体表 EntityIndexInWorld[] 的一次性翻倍 ⇒ 摊销，非逐实体；"
                    + $"稳态口径见 tools/EntityCommandBufferProbe：65,536 实体 → 816 B）");
            }

            // ── 6) 批量销毁（ECB 连续段合并 → 一次 DestroyEntities）──
            // 取最近创建的 100k 个实体（上一步 ECB 的 batch 已被释放，这里重新取一批）
            var toDestroy = new NativeArray<Entity>(100_000, Allocator.Persistent);
            for (int i = 0; i < toDestroy.Length; i++) toDestroy[i] = entities[i];
            sw.Restart();
            int destroyed = em.DestroyEntities((Entity*)toDestroy.GetUnsafePtr(), toDestroy.Length);
            sw.Stop();
            Console.WriteLine($"[5] DestroyEntities({toDestroy.Length:N0})：{sw.Elapsed.TotalMilliseconds:F2} ms"
                + $"（{sw.Elapsed.TotalMilliseconds * 1000 / toDestroy.Length:F3} µs/实体），实际销毁={destroyed:N0}");

            long allocMis = em.VerifyLocateTable(out int checkedEntities);
            Console.WriteLine($"[6] 定位表一致性（逐实体比对定位表 vs 托管表）：不一致={allocMis}（比对 {checkedEntities:N0}）");

            // ── 7) DestroyAllInArchetype（ClearAll 快路径，O(chunk 数)）──
            int chunksBefore = arch.ChunkList.Count;
            sw.Restart();
            long cleared = em.DestroyAllInArchetype(arch);
            sw.Stop();
            Console.WriteLine($"[7] DestroyAllInArchetype：清空 {cleared:N0} 个实体，{sw.Elapsed.TotalMilliseconds:F2} ms，"
                + $"chunk 数 {chunksBefore} → {arch.ChunkList.Count}，Archetype.EntityCount={arch.EntityCount}");

            // ── 8) 清空后重建：Id 全部来自回收池（P0-4c 非托管栈）──
            var again = world.CreateEntities(50_000, typeof(LPos), typeof(LTag));
            int maxId = -1;
            for (int i = 0; i < again.Length; i++) if (again[i].Id > maxId) maxId = again[i].Id;
            Console.WriteLine($"[8] 重建 {again.Length:N0}：max Id={maxId:N0}"
                + $"（< 首轮最大 Id ⇒ Id 来自回收池，Version+1 防悬垂）");

            nStart.Dispose(); nCount.Dispose(); nIds.Dispose();
            outManaged.Dispose(); outNative.Dispose();
            posValues.Dispose(); tagValues.Dispose(); toDestroy.Dispose();
            world.Dispose();
            Console.WriteLine("\n=== 12_EntityNativeLookup 示例结束 ===\n");
        }

        private static long Compare(NativeArray<int> got, int[] expect)
        {
            long bad = 0;
            for (int i = 0; i < expect.Length; i++)
                if (got[i] != expect[i]) bad++;
            return bad;
        }

        private static Archetype FindArchetype(EntityManager em)
        {
            var archs = em.Archetypes;
            for (int i = 0; i < em.ArchetypeCount; i++)
                if (archs[i] != null && archs[i].Has(typeof(LPos))) return archs[i];
            throw new InvalidOperationException("未找到含 LPos 的 Archetype");
        }
    }
}
