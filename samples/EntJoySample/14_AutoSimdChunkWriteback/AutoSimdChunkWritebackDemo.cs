using System;
using System.Diagnostics;
using EntJoy.Collections;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using EntJoy.Mathematics;
using NativeTranspiler;

namespace EntJoySample.AutoSimdBulkWriteback
{
    // ═══════════════════════════════════════════════════════════════════════════
    // P2-12 验收（2026-09-13）：IJobChunk + AutoSIMD 的"**第二个组件的整结构体回写**"缺陷。
    //
    // 现状沉淀坑 2 记载："AutoSIMD 源生成器缺陷：IJobChunk 对『第二个组件整结构体回写』生成 bug"
    //   ⇒ 当时的规避办法是改用 IJobEntity + [NativeTranspile]。
    // 本示例用**同一段 C# 体**跑三条路径并逐实体比对（两条组件列都要被写）：
    //   ① 托管 C# 标量（基线）
    //   ② [NativeTranspile(Cpp)]          标量 C++ 内核
    //   ③ [NativeTranspile(Cpp, AutoSIMD)] 真 SIMD 内核
    // 判据：② 与 ① 必须逐实体**完全相等**（float 精确相等）；③ 与 ① 允许 ≤1 ulp 级误差但不得有结构错位。
    // ═══════════════════════════════════════════════════════════════════════════

    public struct WPos : IComponentData { public float2 V; }
    public struct WVel : IComponentData { public float2 V; }

    /// <summary>基线（托管标量）：两个组件列都写回。</summary>
    public struct TwoWriteChunkCs : IJobChunk
    {
        public float D1;
        public float D2;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            var p = chunk.GetComponentDataNativeArray<WPos>();
            var v = chunk.GetComponentDataNativeArray<WVel>();
            for (int i = 0; i < p.Length; i++)
            {
                WPos pp = p[i];
                pp.V.x = pp.V.x + D1;
                pp.V.y = pp.V.y + D1;
                p[i] = pp;
                WVel vv = v[i];
                vv.V.x = vv.V.x + D2;
                vv.V.y = vv.V.y + D2;
                v[i] = vv;
            }
        }
    }

    /// <summary>② 标量 C++ 内核（对照组：确认"两列回写"本身没问题）。</summary>
    [NativeTranspile(Target = BackendTarget.Cpp)]
    public struct TwoWriteChunkCpp : IJobChunk
    {
        public float D1;
        public float D2;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            var p = chunk.GetComponentDataNativeArray<WPos>();
            var v = chunk.GetComponentDataNativeArray<WVel>();
            for (int i = 0; i < p.Length; i++)
            {
                WPos pp = p[i];
                pp.V.x = pp.V.x + D1;
                pp.V.y = pp.V.y + D1;
                p[i] = pp;
                WVel vv = v[i];
                vv.V.x = vv.V.x + D2;
                vv.V.y = vv.V.y + D2;
                v[i] = vv;
            }
        }
    }

    /// <summary>③ AutoSIMD 内核（被测：坑 2 的现场）。</summary>
    [NativeTranspile(Target = BackendTarget.Cpp, AutoSIMD = AutoSIMD.Enabled)]
    public struct TwoWriteChunkSimd : IJobChunk
    {
        public float D1;
        public float D2;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            var p = chunk.GetComponentDataNativeArray<WPos>();
            var v = chunk.GetComponentDataNativeArray<WVel>();
            for (int i = 0; i < p.Length; i++)
            {
                WPos pp = p[i];
                pp.V.x = pp.V.x + D1;
                pp.V.y = pp.V.y + D1;
                p[i] = pp;
                WVel vv = v[i];
                vv.V.x = vv.V.x + D2;
                vv.V.y = vv.V.y + D2;
                v[i] = vv;
            }
        }
    }

    public static class AutoSimdChunkWritebackDemo
    {
        private const int N = 20_000;
        private const float D1 = 1.5f;
        private const float D2 = -0.25f;

        public static void Run()
        {
            Console.WriteLine("=== 14_AutoSimdChunkWriteback：IJobChunk 双组件回写（P2-12 / 现状沉淀坑 2）===\n");

            var basePos = new float2[N];
            var baseVel = new float2[N];
            var rng = new Random(9876);
            for (int i = 0; i < N; i++)
            {
                basePos[i] = new float2((float)(rng.NextDouble() * 200 - 100), (float)(rng.NextDouble() * 200 - 100));
                baseVel[i] = new float2((float)(rng.NextDouble() * 8 - 4), (float)(rng.NextDouble() * 8 - 4));
            }

            var expectPos = new float2[N];
            var expectVel = new float2[N];
            for (int i = 0; i < N; i++)
            {
                expectPos[i] = new float2(basePos[i].x + D1, basePos[i].y + D1);
                expectVel[i] = new float2(baseVel[i].x + D2, baseVel[i].y + D2);
            }

            var q = new QueryBuilder().WithAll<WPos, WVel>();

            // ② 标量 C++ 内核
            long badCpp = -1;
            try
            {
                var (w2, e2) = BuildWorld(basePos, baseVel);
                new TwoWriteChunkCpp { D1 = D1, D2 = D2 }.Schedule(q).Complete();
                badCpp = CountDiff(w2, e2, expectPos, expectVel, exact: true, out _);
                w2.Dispose();
            }
            catch (Exception ex) { Console.WriteLine($"    （标量 C++ 内核不可用：{ex.GetType().Name}）"); }

            // ③ AutoSIMD 内核
            long badSimd = -1;
            double maxDiff = -1;
            try
            {
                var (w3, e3) = BuildWorld(basePos, baseVel);
                var sw = Stopwatch.StartNew();
                new TwoWriteChunkSimd { D1 = D1, D2 = D2 }.Schedule(q).Complete();
                sw.Stop();
                badSimd = CountDiff(w3, e3, expectPos, expectVel, exact: false, out maxDiff);
                Console.WriteLine($"[1] AutoSIMD IJobChunk（双列回写）：{sw.Elapsed.TotalMilliseconds:F2} ms");
                w3.Dispose();
            }
            catch (Exception ex) { Console.WriteLine($"    （AutoSIMD 内核不可用：{ex.GetType().Name}）"); }

            Console.WriteLine($"[2] 标量 C++ 内核 vs 基线：不一致={badCpp}（要求 0）");
            Console.WriteLine($"[3] AutoSIMD 内核 vs 基线：不一致={badSimd}（要求 0），最大绝对误差={maxDiff:R}");
            Console.WriteLine($"    判定：{((badCpp == 0 && badSimd == 0) ? "PASS（坑 2 未复现：第二列回写正确）" : "FAIL/未完成（见上）")}");

            Console.WriteLine("\n=== 14_AutoSimdChunkWriteback 结束 ===\n");
        }

        private static (World world, Entity[] entities) BuildWorld(float2[] pos, float2[] vel)
        {
            var world = new World("autosimd_writeback");
            var em = world.EntityManager;
            var entities = world.CreateEntities(N, typeof(WPos), typeof(WVel));
            for (int i = 0; i < N; i++)
            {
                em.Set(entities[i], new WPos { V = pos[i] });
                em.Set(entities[i], new WVel { V = vel[i] });
            }
            return (world, entities);
        }

        private static long CountDiff(World world, Entity[] entities, float2[] expectPos, float2[] expectVel, bool exact, out double maxDiff)
        {
            maxDiff = 0;
            long bad = 0;
            var em = world.EntityManager;
            for (int i = 0; i < N; i++)
            {
                float2 p = em.GetComponent<WPos>(entities[i]).V;
                float2 v = em.GetComponent<WVel>(entities[i]).V;
                double d = Math.Max(
                    Math.Max(Math.Abs(p.x - expectPos[i].x), Math.Abs(p.y - expectPos[i].y)),
                    Math.Max(Math.Abs(v.x - expectVel[i].x), Math.Abs(v.y - expectVel[i].y)));
                if (exact ? d != 0 : d > 1e-5) bad++;
                if (d > maxDiff) maxDiff = d;
            }
            return bad;
        }
    }
}
