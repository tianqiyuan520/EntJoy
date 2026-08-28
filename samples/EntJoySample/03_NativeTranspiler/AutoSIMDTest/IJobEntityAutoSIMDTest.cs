//// IJobEntity + AutoSIMD=Enabled 正确性测试
//// 验证：AutoSIMD 走 ChunkRange 路径后，IJobEntity 的 SIMD 计算结果与 C# 标量版本一致。
//// 覆盖 Light（纯乘加，无 fast-math 容差）与 Heavy（16×sin/cos，_n_sin_avx2 ~3.5ULP）两种场景。
//// 同时覆盖无 AutoSIMD 的 CPP IJobEntity 与 IJobChunk（对照 baseline）。

//using EntJoy.ECS;
//using EntJoy.ECS.JobSystem;
//using EntJoy.Collections;
//using EntJoy.Mathematics;
//using NativeTranspiler;

//// C# 标量 body helper（避开 IJobEntity.Schedule 的 native adapter — 无 [NativeTranspile] 时 AV）
//// 放在 namespace 外、IJobEntity 定义之后，通过完全限定名引用
//internal static class CSharpBody
//{
//    public static void Light(ref EntJoySample.AutoSIMDTest.TestMovePosition p, in EntJoySample.AutoSIMDTest.TestMoveVelocity v, float dt) => p.Value += v.Value * dt;
//    public static void Heavy(ref EntJoySample.AutoSIMDTest.TestMovePosition p, in EntJoySample.AutoSIMDTest.TestMoveVelocity v, float dt)
//    {
//        var pos = p.Value;
//        var vel = v.Value;
//        float px = pos.x, py = pos.y;
//        float vx = vel.x, vy = vel.y;
//        float accX = px * 0.001f + vx * 0.01f, accY = py * 0.001f + vy * 0.01f;
//        for (int it = 0; it < 16; it++)
//        {
//            float phX = accX + it * 0.03125f, phY = accY - it * 0.0625f;
//            float w = MathF.Sin(phX) + MathF.Cos(phY);
//            float r = MathF.Sqrt(accX * accX + accY * accY + 1.0f);
//            accX = accX * 0.985f + w * 0.015f + r * 0.0002f + vx * 0.0001f;
//            accY = accY * 0.982f - w * 0.012f + r * 0.0003f + vy * 0.0001f;
//        }
//        pos.x = px + vx * dt + accX * 0.001f;
//        pos.y = py + vy * dt + accY * 0.001f;
//        p.Value = pos;
//    }
//}

//namespace EntJoySample.AutoSIMDTest
//{
//    public struct TestMovePosition : IComponentData
//    {
//        public float2 Value;
//    }

//    public struct TestMoveVelocity : IComponentData
//    {
//        public float2 Value;
//    }

//    // ═══ Light IJobEntity: AutoSIMD=Enabled（被测目标）═══
//    [NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
//    public struct LightJobEntityAutoSIMD : IJobEntity
//    {
//        public float DeltaTime;
//        public void Execute(ref TestMovePosition position, in TestMoveVelocity velocity)
//        {
//            position.Value += velocity.Value * DeltaTime;
//        }
//    }

//    // ═══ Heavy IJobEntity: AutoSIMD=Enabled（被测目标）═══
//    [NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
//    public struct HeavyJobEntityAutoSIMD : IJobEntity
//    {
//        public float DeltaTime;
//        public void Execute(ref TestMovePosition position, in TestMoveVelocity velocity)
//        {
//            float px = position.Value.x;
//            float py = position.Value.y;
//            float vx = velocity.Value.x;
//            float vy = velocity.Value.y;
//            float accX = px * 0.001f + vx * 0.01f;
//            float accY = py * 0.001f + vy * 0.01f;
//            for (int it = 0; it < 16; it++)
//            {
//                float phX = accX + it * 0.03125f;
//                float phY = accY - it * 0.0625f;
//                float w = MathF.Sin(phX) + MathF.Cos(phY);
//                float r = MathF.Sqrt(accX * accX + accY * accY + 1.0f);
//                accX = accX * 0.985f + w * 0.015f + r * 0.0002f + vx * 0.0001f;
//                accY = accY * 0.982f - w * 0.012f + r * 0.0003f + vy * 0.0001f;
//            }
//            position.Value.x = px + vx * DeltaTime + accX * 0.001f;
//            position.Value.y = py + vy * DeltaTime + accY * 0.001f;
//        }
//    }

//    // ═══ 无 AutoSIMD 的 CPP IJobEntity（对照 baseline）═══
//    [NativeTranspile(Target = BackendTarget.Cpp)]
//    public struct LightJobEntityCpp : IJobEntity
//    {
//        public float DeltaTime;
//        public void Execute(ref TestMovePosition position, in TestMoveVelocity velocity)
//        {
//            position.Value += velocity.Value * DeltaTime;
//        }
//    }

//    // ═══ 无 AutoSIMD 的 CPP IJobEntity（Heavy）═══
//    [NativeTranspile(Target = BackendTarget.Cpp)]
//    public struct HeavyJobEntityCpp : IJobEntity
//    {
//        public float DeltaTime;
//        public void Execute(ref TestMovePosition position, in TestMoveVelocity velocity)
//        {
//            float px = position.Value.x;
//            float py = position.Value.y;
//            float vx = velocity.Value.x;
//            float vy = velocity.Value.y;
//            float accX = px * 0.001f + vx * 0.01f;
//            float accY = py * 0.001f + vy * 0.01f;
//            for (int it = 0; it < 16; it++)
//            {
//                float phX = accX + it * 0.03125f;
//                float phY = accY - it * 0.0625f;
//                float w = MathF.Sin(phX) + MathF.Cos(phY);
//                float r = MathF.Sqrt(accX * accX + accY * accY + 1.0f);
//                accX = accX * 0.985f + w * 0.015f + r * 0.0002f + vx * 0.0001f;
//                accY = accY * 0.982f - w * 0.012f + r * 0.0003f + vy * 0.0001f;
//            }
//            position.Value.x = px + vx * DeltaTime + accX * 0.001f;
//            position.Value.y = py + vy * DeltaTime + accY * 0.001f;
//        }
//    }

//    // ═══ 无 AutoSIMD 的 CPP IJobChunk（对照 baseline）═══
//    [NativeTranspile(Target = BackendTarget.Cpp)]
//    public struct LightJobChunkCpp : IJobChunk
//    {
//        public float DeltaTime;
//        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
//        {
//            var positions = chunk.GetComponentDataNativeArray<TestMovePosition>();
//            var velocities = chunk.GetComponentDataNativeArray<TestMoveVelocity>();
//            for (int index = 0; index < positions.Length; index++)
//            {
//                var pos = positions[index];
//                pos.Value += velocities[index].Value * DeltaTime;
//                positions[index] = pos;
//            }
//        }
//    }

//    // ═══ 无 AutoSIMD 的 CPP IJobChunk（Heavy）═══
//    [NativeTranspile(Target = BackendTarget.Cpp)]
//    public struct HeavyJobChunkCpp : IJobChunk
//    {
//        public float DeltaTime;
//        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
//        {
//            var positions = chunk.GetComponentDataNativeArray<TestMovePosition>();
//            var velocities = chunk.GetComponentDataNativeArray<TestMoveVelocity>();
//            for (int index = 0; index < positions.Length; index++)
//            {
//                var position = positions[index];
//                var velocity = velocities[index];
//                float px = position.Value.x;
//                float py = position.Value.y;
//                float vx = velocity.Value.x;
//                float vy = velocity.Value.y;
//                float accX = px * 0.001f + vx * 0.01f;
//                float accY = py * 0.001f + vy * 0.01f;
//                for (int it = 0; it < 16; it++)
//                {
//                    float phX = accX + it * 0.03125f;
//                    float phY = accY - it * 0.0625f;
//                    float w = MathF.Sin(phX) + MathF.Cos(phY);
//                    float r = MathF.Sqrt(accX * accX + accY * accY + 1.0f);
//                    accX = accX * 0.985f + w * 0.015f + r * 0.0002f + vx * 0.0001f;
//                    accY = accY * 0.982f - w * 0.012f + r * 0.0003f + vy * 0.0001f;
//                }
//                position.Value.x = px + vx * DeltaTime + accX * 0.001f;
//                position.Value.y = py + vy * DeltaTime + accY * 0.001f;
//                positions[index] = position;
//            }
//        }
//    }

//    public static class IJobEntityAutoSIMDTest
//    {
//        public const int N = 100_000;
//        public const float Dt = 1.0f / 60.0f;
//        // Light：AutoSIMD 无 fast-math（Precise 库 fast-math OFF），纯 float 乘加应与 C# 标量零误差
//        // Heavy：_n_sin_avx2 ~3.5ULP × 16 次迭代，保守 1.5e-3 容差
//        public const float LightEpsilon = 0.0f;
//        public const float HeavyEpsilon = 1.5e-3f;
//        // CPP 标量路径：C++ 编译器可能对 a + b*c 做 FMA 融合（fma(v, dt, pos)），
//        // 产生 1 ULP（~2.4e-7）的舍入差异 → 用 1e-6 容差（AutoSIMD 保持零容差）
//        public const float LightCppEpsilon = 1.0e-6f;

//        public static int RunAll()
//        {
//            Console.OutputEncoding = System.Text.Encoding.UTF8;
//            Console.WriteLine($"=== IJobEntity / IJobChunk Test (N={N}) ===\n");
//            int passed = 0, failed = 0;

//            // ── AutoSIMD IJobEntity（ChunkRange 真 SIMD）──
//            RunCase("Light IJobEntity AutoSIMD", false, LightEpsilon, q => new LightJobEntityAutoSIMD { DeltaTime = Dt }.Schedule(q).Complete(), ref passed, ref failed);
//            RunCase("Heavy IJobEntity AutoSIMD", true, HeavyEpsilon, q => new HeavyJobEntityAutoSIMD { DeltaTime = Dt }.Schedule(q).Complete(), ref passed, ref failed);

//            // ── 无 AutoSIMD 的 CPP IJobEntity（标量 EntityBatch/ChunkRange）──
//            // 注意：CPP 标量路径允许 C++ 编译器 FMA 融合，Light 用 1e-6 容差（1 ULP）
//            RunCase("Light IJobEntity CPP (no AutoSIMD)", false, LightCppEpsilon, q => new LightJobEntityCpp { DeltaTime = Dt }.Schedule(q).Complete(), ref passed, ref failed);
//            RunCase("Heavy IJobEntity CPP (no AutoSIMD)", true, HeavyEpsilon, q => new HeavyJobEntityCpp { DeltaTime = Dt }.Schedule(q).Complete(), ref passed, ref failed);

//            // ── 无 AutoSIMD 的 CPP IJobChunk（标量 EntityBatch）──
//            RunCase("Light IJobChunk CPP (no AutoSIMD)", false, LightCppEpsilon, q => new LightJobChunkCpp { DeltaTime = Dt }.Schedule(q).Complete(), ref passed, ref failed);
//            RunCase("Heavy IJobChunk CPP (no AutoSIMD)", true, HeavyEpsilon, q => new HeavyJobChunkCpp { DeltaTime = Dt }.Schedule(q).Complete(), ref passed, ref failed);

//            Console.WriteLine($"\n=== {passed} passed, {failed} failed ===");
//            return failed == 0 ? 0 : 1;
//        }

//        private static void RunCase(string name, bool heavy, float epsilon, Action<QueryBuilder> schedule, ref int passed, ref int failed)
//        {
//            // 1) C# baseline：创建 world + 实体 + 初始值，手写循环执行 CSharpBody（纯托管，避开 native IJobEntity 调度）
//            var worldC = new World("csc_" + (heavy ? "heavy" : "light"));
//            var emC = worldC.EntityManager;
//            var types = new ComponentType[] { typeof(TestMovePosition), typeof(TestMoveVelocity) };
//            var seed = 12345u;
//            var entitiesC = new Entity[N];
//            for (int i = 0; i < N; i++)
//            {
//                entitiesC[i] = emC.NewEntity(types);
//                emC.Set(entitiesC[i], new TestMovePosition { Value = new float2(RandFloat(ref seed) * 100f - 50f, RandFloat(ref seed) * 100f - 50f) });
//                emC.Set(entitiesC[i], new TestMoveVelocity { Value = new float2(RandFloat(ref seed) * 4f - 2f, RandFloat(ref seed) * 4f - 2f) });
//            }
//            ApplyCSharpBody(worldC, types, heavy, Dt);

//            // 收集 C# 结果
//            var baseline = CollectPositions(worldC);
//            worldC.Dispose();

//            // 2) AutoSIMD native：用相同 seed 重建世界 + 跑 Schedule().Complete()
//            var worldS = new World("simd_" + (heavy ? "heavy" : "light"));
//            var emS = worldS.EntityManager;
//            var seed2 = 12345u;
//            for (int i = 0; i < N; i++)
//            {
//                var e = emS.NewEntity(types);
//                emS.Set(e, new TestMovePosition { Value = new float2(RandFloat(ref seed2) * 100f - 50f, RandFloat(ref seed2) * 100f - 50f) });
//                emS.Set(e, new TestMoveVelocity { Value = new float2(RandFloat(ref seed2) * 4f - 2f, RandFloat(ref seed2) * 4f - 2f) });
//            }
//            var queryS = new QueryBuilder().WithAll<TestMovePosition, TestMoveVelocity>();
//            var sw = System.Diagnostics.Stopwatch.StartNew();
//            schedule(queryS);
//            sw.Stop();
//            double msPerSched = sw.Elapsed.TotalMilliseconds;

//            // 收集 AutoSIMD 结果
//            var actual = CollectPositions(worldS);
//            worldS.Dispose();

//            // 3) 逐元素对比
//            float maxDiff = 0f;
//            int mismatch = 0;
//            for (int i = 0; i < N; i++)
//            {
//                float dx = MathF.Abs(baseline[i].Value.x - actual[i].Value.x);
//                float dy = MathF.Abs(baseline[i].Value.y - actual[i].Value.y);
//                float d = MathF.Max(dx, dy);
//                if (d > epsilon) mismatch++;
//                if (d > maxDiff) maxDiff = d;
//            }

//            bool ok = (epsilon == 0f) ? (maxDiff == 0f) : (mismatch == 0);
//            if (ok) passed++; else failed++;
//            string status = ok ? "PASS" : "FAIL";
//            Console.WriteLine($"  {name}");
//            Console.WriteLine($"    MaxDiff={maxDiff:E4}  Mismatch={mismatch}/{N}  Epsilon={epsilon:E4}  Time={msPerSched:F3}ms  [{status}]");
//        }

//        private static float RandFloat(ref uint s)
//        {
//            s = s * 1664525u + 1013904223u;
//            return (s & 0xFFFFFF) / (float)0x1000000;
//        }

//        // 从 World 收集所有 TestMovePosition（按 archetype 遍历，unsafe 直接读组件数组指针）
//        // 整个方法 try/catch + null checks：EntJoy.ECS 内部 archetype 表可能未初始化/dispose 中
//        private static unsafe TestMovePosition[] CollectPositions(World world)
//        {
//            var result = new TestMovePosition[N];
//            int idx = 0;
//            try
//            {
//                if (world == null) return result;
//                var em = world.EntityManager;
//                if (em == null) return result;
//                var archetypes = em.GetAllArchetypes();
//                if (archetypes == null) return result;
//                for (int a = 0; a < archetypes.Length && idx < N; a++)
//                {
//                    Archetype arch = null;
//                    try { arch = archetypes[a]; } catch { continue; }
//                    if (arch == null) continue;
//                    int compIdx;
//                    try { compIdx = arch.GetComponentTypeIndex<TestMovePosition>(); } catch { continue; }
//                    var chunks = arch.GetChunks();
//                    if (chunks == null) continue;
//                    for (int c = 0; c < chunks.Count && idx < N; c++)
//                    {
//                        Chunk ch = chunks[c];
//                        var ptr = (TestMovePosition*)ch.GetComponentArrayPointer(compIdx);
//                        int n = ch.EntityCount;
//                        for (int i = 0; i < n && idx < N; i++) result[idx++] = ptr[i];
//                    }
//                }
//            }
//            catch (Exception ex) { Console.WriteLine($"[CollectPositions caught] {ex.GetType().Name}: {ex.Message}"); }
//            return result;
//        }

//        // 对 world 中所有 TestMovePosition 实体，手写循环执行 C# 标量 body（CSharpBody）
//        // 避开 IJobEntity.Schedule 的 native adapter（无 [NativeTranspile] 时会 AV）
//        private static unsafe void ApplyCSharpBody(World world, ComponentType[] types, bool heavy, float dt)
//        {
//            var posType = typeof(TestMovePosition);
//            var velType = typeof(TestMoveVelocity);
//            var archetypes = world.EntityManager.GetAllArchetypes();
//            for (int a = 0; a < archetypes.Length; a++)
//            {
//                var arch = archetypes[a];
//                if (arch == null) continue;
//                if (!arch.Has(posType) || !arch.Has(velType)) continue;
//                int posIdx = arch.GetComponentTypeIndex<TestMovePosition>();
//                int velIdx = arch.GetComponentTypeIndex<TestMoveVelocity>();
//                if (posIdx < 0 || velIdx < 0) continue;
//                var chunks = arch.GetChunks();
//                for (int c = 0; c < chunks.Count; c++)
//                {
//                    var ch = chunks[c];
//                    int n = ch.EntityCount;
//                    var posPtr = (TestMovePosition*)ch.GetComponentArrayPointer(posIdx);
//                    var velPtr = (TestMoveVelocity*)ch.GetComponentArrayPointer(velIdx);
//                    for (int i = 0; i < n; i++)
//                    {
//                        if (heavy) CSharpBody.Heavy(ref posPtr[i], in velPtr[i], dt);
//                        else       CSharpBody.Light(ref posPtr[i], in velPtr[i], dt);
//                    }
//                }
//            }
//        }
//    }
//}
