// IJobChunk + AutoSIMD + EntityBatch 路径正确性测试
// 验证：AutoSIMD=Enabled 的 IJobChunk 走 EntityBatch adapter（SimdControlFlowGenerator 真 SIMD），
//       计算结果与 C# 标量版本一致。
// 覆盖 Light（纯乘加）与 Heavy（16×sin/cos，容差 1.5e-3）两种场景。

using System;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;
using EntJoy.Collections;
using EntJoy.Mathematics;
using NativeTranspiler;

// C# 标量 body helper（对标 IJobEntityAutoSIMDTest 的 CSharpBody）
internal static class ChunkCSharpBody
{
    public static void Light(ref EntJoySample.AutoSIMDTest.TestMovePosition p, in EntJoySample.AutoSIMDTest.TestMoveVelocity v, float dt)
        => p.Value += v.Value * dt;

    public static void Heavy(ref EntJoySample.AutoSIMDTest.TestMovePosition p, in EntJoySample.AutoSIMDTest.TestMoveVelocity v, float dt)
    {
        var pos = p.Value;
        var vel = v.Value;
        float px = pos.x, py = pos.y;
        float vx = vel.x, vy = vel.y;
        float accX = px * 0.001f + vx * 0.01f, accY = py * 0.001f + vy * 0.01f;
        for (int it = 0; it < 16; it++)
        {
            float phX = accX + it * 0.03125f, phY = accY - it * 0.0625f;
            float w = MathF.Sin(phX) + MathF.Cos(phY);
            float r = MathF.Sqrt(accX * accX + accY * accY + 1.0f);
            accX = accX * 0.985f + w * 0.015f + r * 0.0002f + vx * 0.0001f;
            accY = accY * 0.982f - w * 0.012f + r * 0.0003f + vy * 0.0001f;
        }
        pos.x = px + vx * dt + accX * 0.001f;
        pos.y = py + vy * dt + accY * 0.001f;
        p.Value = pos;
    }
}

namespace EntJoySample.AutoSIMDTest
{
    // ═══ Light IJobChunk: AutoSIMD=Enabled（EntityBatch 路径，SimdControlFlowGenerator）═══
    [NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
    public struct LightJobChunkAutoSIMDEntityBatch : IJobChunk
    {
        public float DeltaTime;
        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            var positions = chunk.GetComponentDataNativeArray<TestMovePosition>();
            var velocities = chunk.GetComponentDataNativeArray<TestMoveVelocity>();
            for (int index = 0; index < positions.Length; index++)
            {
                var pos = positions[index];
                pos.Value += velocities[index].Value * DeltaTime;
                positions[index] = pos;
            }
        }
    }

    // ═══ Heavy IJobChunk: AutoSIMD=Enabled（EntityBatch 路径，SimdControlFlowGenerator）═══
    // 注意：用 read-modify-write 模式（局部变量 + 字段修改 + 写回），
    // 与 IJobChunkMoveCompareTest 的 HeavyJobChunkCppFast 一致，确保 DecomposeStructLocals
    // 能分解为字段级 gather/scatter（直接 new 结构体赋值会触发 n_store_epi32 缺陷）。
    [NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
    public struct HeavyJobChunkAutoSIMDEntityBatch : IJobChunk
    {
        public float DeltaTime;
        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            var positions = chunk.GetComponentDataNativeArray<TestMovePosition>();
            var velocities = chunk.GetComponentDataNativeArray<TestMoveVelocity>();
            for (int index = 0; index < positions.Length; index++)
            {
                var position = positions[index];
                var velocity = velocities[index];
                float px = position.Value.x;
                float py = position.Value.y;
                float vx = velocity.Value.x;
                float vy = velocity.Value.y;
                float accX = px * 0.001f + vx * 0.01f;
                float accY = py * 0.001f + vy * 0.01f;
                for (int it = 0; it < 16; it++)
                {
                    float phX = accX + it * 0.03125f;
                    float phY = accY - it * 0.0625f;
                    float w = MathF.Sin(phX) + MathF.Cos(phY);
                    float r = MathF.Sqrt(accX * accX + accY * accY + 1.0f);
                    accX = accX * 0.985f + w * 0.015f + r * 0.0002f + vx * 0.0001f;
                    accY = accY * 0.982f - w * 0.012f + r * 0.0003f + vy * 0.0001f;
                }
                position.Value.x = px + vx * DeltaTime + accX * 0.001f;
                position.Value.y = py + vy * DeltaTime + accY * 0.001f;
                positions[index] = position;
            }
        }
    }

    public static class IJobChunkAutoSIMDEntityBatchTest
    {
        public const int N = 100_000;
        public const float Dt = 1.0f / 60.0f;
        public const float LightEpsilon = 0.0f;
        public const float HeavyEpsilon = 1.5e-3f;

        public static int RunAll()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine($"=== IJobChunk + AutoSIMD + EntityBatch Test (N={N}) ===\n");
            int passed = 0, failed = 0;
            RunCase("Light IJobChunk (Position += Velocity * dt)", false, LightEpsilon, ref passed, ref failed);
            RunCase("Heavy IJobChunk (16x sin/cos iterations)", true, HeavyEpsilon, ref passed, ref failed);
            Console.WriteLine($"\n=== {passed} passed, {failed} failed ===");
            return failed == 0 ? 0 : 1;
        }

        private static void RunCase(string name, bool heavy, float epsilon, ref int passed, ref int failed)
        {
            // 1) C# baseline
            var worldC = new World("csc_" + (heavy ? "heavy" : "light"));
            var emC = worldC.EntityManager;
            var types = new ComponentType[] { typeof(TestMovePosition), typeof(TestMoveVelocity) };
            var seed = 12345u;
            for (int i = 0; i < N; i++)
            {
                var e = emC.NewEntity(types);
                emC.Set(e, new TestMovePosition { Value = new float2(RandFloat(ref seed) * 100f - 50f, RandFloat(ref seed) * 100f - 50f) });
                emC.Set(e, new TestMoveVelocity { Value = new float2(RandFloat(ref seed) * 4f - 2f, RandFloat(ref seed) * 4f - 2f) });
            }
            ApplyCSharpBody(worldC, types, heavy, Dt);
            var baseline = CollectPositions(worldC);
            worldC.Dispose();

            // 2) AutoSIMD native（IJobChunk + EntityBatch 路径）
            var worldS = new World("simd_" + (heavy ? "heavy" : "light"));
            var emS = worldS.EntityManager;
            var seed2 = 12345u;
            for (int i = 0; i < N; i++)
            {
                var e = emS.NewEntity(types);
                emS.Set(e, new TestMovePosition { Value = new float2(RandFloat(ref seed2) * 100f - 50f, RandFloat(ref seed2) * 100f - 50f) });
                emS.Set(e, new TestMoveVelocity { Value = new float2(RandFloat(ref seed2) * 4f - 2f, RandFloat(ref seed2) * 4f - 2f) });
            }
            var queryS = new QueryBuilder().WithAll<TestMovePosition, TestMoveVelocity>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (heavy) new HeavyJobChunkAutoSIMDEntityBatch { DeltaTime = Dt }.Schedule(queryS).Complete();
            else       new LightJobChunkAutoSIMDEntityBatch { DeltaTime = Dt }.Schedule(queryS).Complete();
            sw.Stop();
            double msPerSched = sw.Elapsed.TotalMilliseconds;

            var actual = CollectPositions(worldS);
            worldS.Dispose();

            // 3) 对比
            float maxDiff = 0f;
            int mismatch = 0;
            for (int i = 0; i < N; i++)
            {
                float dx = MathF.Abs(baseline[i].Value.x - actual[i].Value.x);
                float dy = MathF.Abs(baseline[i].Value.y - actual[i].Value.y);
                float d = MathF.Max(dx, dy);
                if (d > epsilon) mismatch++;
                if (d > maxDiff) maxDiff = d;
            }

            bool ok = (epsilon == 0f) ? (maxDiff == 0f) : (mismatch == 0);
            if (ok) passed++; else failed++;
            string status = ok ? "PASS" : "FAIL";
            Console.WriteLine($"  {name}");
            Console.WriteLine($"    MaxDiff={maxDiff:E4}  Mismatch={mismatch}/{N}  Epsilon={epsilon:E4}  Time={msPerSched:F3}ms  [{status}]");
        }

        private static float RandFloat(ref uint s)
        {
            s = s * 1664525u + 1013904223u;
            return (s & 0xFFFFFF) / (float)0x1000000;
        }

        private static unsafe TestMovePosition[] CollectPositions(World world)
        {
            var result = new TestMovePosition[N];
            int idx = 0;
            try
            {
                if (world == null) return result;
                var em = world.EntityManager;
                if (em == null) return result;
                var archetypes = em.GetAllArchetypes();
                if (archetypes == null) return result;
                for (int a = 0; a < archetypes.Length && idx < N; a++)
                {
                    Archetype arch = null;
                    try { arch = archetypes[a]; } catch { continue; }
                    if (arch == null) continue;
                    int compIdx;
                    try { compIdx = arch.GetComponentTypeIndex<TestMovePosition>(); } catch { continue; }
                    var chunks = arch.GetChunks();
                    if (chunks == null) continue;
                    for (int c = 0; c < chunks.Count && idx < N; c++)
                    {
                        Chunk ch = chunks[c];
                        var ptr = (TestMovePosition*)ch.GetComponentArrayPointer(compIdx);
                        int n = ch.EntityCount;
                        for (int i = 0; i < n && idx < N; i++) result[idx++] = ptr[i];
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[CollectPositions caught] {ex.GetType().Name}: {ex.Message}"); }
            return result;
        }

        private static unsafe void ApplyCSharpBody(World world, ComponentType[] types, bool heavy, float dt)
        {
            var posType = typeof(TestMovePosition);
            var velType = typeof(TestMoveVelocity);
            var archetypes = world.EntityManager.GetAllArchetypes();
            for (int a = 0; a < archetypes.Length; a++)
            {
                var arch = archetypes[a];
                if (arch == null) continue;
                if (!arch.Has(posType) || !arch.Has(velType)) continue;
                int posIdx = arch.GetComponentTypeIndex<TestMovePosition>();
                int velIdx = arch.GetComponentTypeIndex<TestMoveVelocity>();
                if (posIdx < 0 || velIdx < 0) continue;
                var chunks = arch.GetChunks();
                for (int c = 0; c < chunks.Count; c++)
                {
                    var ch = chunks[c];
                    int n = ch.EntityCount;
                    var posPtr = (TestMovePosition*)ch.GetComponentArrayPointer(posIdx);
                    var velPtr = (TestMoveVelocity*)ch.GetComponentArrayPointer(velIdx);
                    for (int i = 0; i < n; i++)
                    {
                        if (heavy) ChunkCSharpBody.Heavy(ref posPtr[i], in velPtr[i], dt);
                        else       ChunkCSharpBody.Light(ref posPtr[i], in velPtr[i], dt);
                    }
                }
            }
        }
    }
}
