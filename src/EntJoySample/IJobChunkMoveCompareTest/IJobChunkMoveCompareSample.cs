using EntJoy;
using EntJoy.Collections;
using EntJoy.Mathematics;
using System.Diagnostics;

namespace EntJoySample.IJobChunkMoveCompareTest
{
    public struct MovePosition : IComponentData
    {
        public float2 Value;
    }

    public struct MoveVelocity : IComponentData
    {
        public float2 Value;
    }

    public struct MoveJobChunkCSharp : IJobChunk
    {
        public float DeltaTime;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            Span<MovePosition> positions = chunk.GetComponentDataSpan<MovePosition>();
            Span<MoveVelocity> velocities = chunk.GetComponentDataSpan<MoveVelocity>();

            for (int index = 0; index < positions.Length; index++)
            {
                MovePosition position = positions[index];
                position.Value += velocities[index].Value * DeltaTime;
                positions[index] = position;
            }
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
    public struct MoveJobChunkCpp : IJobChunk
    {
        public float DeltaTime;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            NativeArray<MovePosition> positions = chunk.GetComponentDataNativeArray<MovePosition>();
            NativeArray<MoveVelocity> velocities = chunk.GetComponentDataNativeArray<MoveVelocity>();

            for (int index = 0; index < positions.Length; index++)
            {
                MovePosition position = positions[index];
                position.Value += velocities[index].Value * DeltaTime;
                positions[index] = position;
            }
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
    public struct MoveJobChunkIspc : IJobChunk
    {
        public float DeltaTime;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            NativeArray<MovePosition> positions = chunk.GetComponentDataNativeArray<MovePosition>();
            NativeArray<MoveVelocity> velocities = chunk.GetComponentDataNativeArray<MoveVelocity>();

            for (int index = 0; index < positions.Length; index++)
            {
                MovePosition position = positions[index];
                position.Value += velocities[index].Value * DeltaTime;
                positions[index] = position;
            }
        }
    }

    public struct HeavyJobChunkCSharp : IJobChunk
    {
        public float DeltaTime;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            Span<MovePosition> positions = chunk.GetComponentDataSpan<MovePosition>();
            Span<MoveVelocity> velocities = chunk.GetComponentDataSpan<MoveVelocity>();

            for (int index = 0; index < positions.Length; index++)
            {
                MovePosition position = positions[index];
                MoveVelocity velocity = velocities[index];

                float px = position.Value.x;
                float py = position.Value.y;
                float vx = velocity.Value.x;
                float vy = velocity.Value.y;
                float accX = px * 0.001f + vx * 0.01f;
                float accY = py * 0.001f + vy * 0.01f;

                for (int iteration = 0; iteration < 16; iteration++)
                {
                    float phaseX = accX + iteration * 0.03125f;
                    float phaseY = accY - iteration * 0.0625f;
                    float wave = MathF.Sin(phaseX) + MathF.Cos(phaseY);
                    float radius = MathF.Sqrt(accX * accX + accY * accY + 1.0f);
                    accX = accX * 0.985f + wave * 0.015f + radius * 0.0002f + vx * 0.0001f;
                    accY = accY * 0.982f - wave * 0.012f + radius * 0.0003f + vy * 0.0001f;
                }

                position.Value.x = px + vx * DeltaTime + accX * 0.001f;
                position.Value.y = py + vy * DeltaTime + accY * 0.001f;
                positions[index] = position;
            }
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp, CppMathLib = NativeTranspiler.CppMathLib.@default)]
    public struct HeavyJobChunkCpp : IJobChunk
    {
        public float DeltaTime;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            NativeArray<MovePosition> positions = chunk.GetComponentDataNativeArray<MovePosition>();
            NativeArray<MoveVelocity> velocities = chunk.GetComponentDataNativeArray<MoveVelocity>();

            for (int index = 0; index < positions.Length; index++)
            {
                MovePosition position = positions[index];
                MoveVelocity velocity = velocities[index];

                float px = position.Value.x;
                float py = position.Value.y;
                float vx = velocity.Value.x;
                float vy = velocity.Value.y;
                float accX = px * 0.001f + vx * 0.01f;
                float accY = py * 0.001f + vy * 0.01f;

                for (int iteration = 0; iteration < 16; iteration++)
                {
                    float phaseX = accX + iteration * 0.03125f;
                    float phaseY = accY - iteration * 0.0625f;
                    float wave = MathF.Sin(phaseX) + MathF.Cos(phaseY);
                    float radius = MathF.Sqrt(accX * accX + accY * accY + 1.0f);
                    accX = accX * 0.985f + wave * 0.015f + radius * 0.0002f + vx * 0.0001f;
                    accY = accY * 0.982f - wave * 0.012f + radius * 0.0003f + vy * 0.0001f;
                }

                position.Value.x = px + vx * DeltaTime + accX * 0.001f;
                position.Value.y = py + vy * DeltaTime + accY * 0.001f;
                positions[index] = position;
            }
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.fast)]
    public struct HeavyJobChunkIspc : IJobChunk
    {
        public float DeltaTime;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            NativeArray<MovePosition> positions = chunk.GetComponentDataNativeArray<MovePosition>();
            NativeArray<MoveVelocity> velocities = chunk.GetComponentDataNativeArray<MoveVelocity>();

            for (int index = 0; index < positions.Length; index++)
            {
                MovePosition position = positions[index];
                MoveVelocity velocity = velocities[index];

                float px = position.Value.x;
                float py = position.Value.y;
                float vx = velocity.Value.x;
                float vy = velocity.Value.y;
                float accX = px * 0.001f + vx * 0.01f;
                float accY = py * 0.001f + vy * 0.01f;

                for (int iteration = 0; iteration < 16; iteration++)
                {
                    float phaseX = accX + iteration * 0.03125f;
                    float phaseY = accY - iteration * 0.0625f;
                    float wave = MathF.Sin(phaseX) + MathF.Cos(phaseY);
                    float radius = MathF.Sqrt(accX * accX + accY * accY + 1.0f);
                    accX = accX * 0.985f + wave * 0.015f + radius * 0.0002f + vx * 0.0001f;
                    accY = accY * 0.982f - wave * 0.012f + radius * 0.0003f + vy * 0.0001f;
                }

                position.Value.x = px + vx * DeltaTime + accX * 0.001f;
                position.Value.y = py + vy * DeltaTime + accY * 0.001f;
                positions[index] = position;
            }
        }
    }

    public sealed unsafe class IJobChunkMoveCompareSample : IDisposable
    {
        private const int EntityCount = 1_000_000;
        private const int WarmupFrames = 5;
        private const int MeasureFrames = 300;
        private const int HeavyIterations = 32;
        private const float DeltaTime = 1.0f / 60.0f;
        private const float Epsilon = 0.001f;
        private const float HeavyEpsilon = 0.001f;

        private readonly QueryBuilder _query = new QueryBuilder().WithAll<MovePosition, MoveVelocity>();
        private readonly World _csharpWorld;
        private readonly World _cppWorld;
        private readonly World _ispcWorld;

        public IJobChunkMoveCompareSample()
        {
            Console.WriteLine($"Preparing {EntityCount:N0} entities...");

            _csharpWorld = new World("IJobChunkMoveCompare_CSharp");
            CreateEntities(_csharpWorld);

            _cppWorld = new World("IJobChunkMoveCompare_Cpp");
            CreateEntities(_cppWorld);

            _ispcWorld = new World("IJobChunkMoveCompare_Ispc");
            CreateEntities(_ispcWorld);
        }

        public void Run()
        {
            NativeJobScheduler.PrewakeWorkersOnce();

            Console.WriteLine();
            Console.WriteLine("=== IJobChunk 100w 轻量移动对比 ===");
            Console.WriteLine($"实体数: {EntityCount:N0}, Warmup: {WarmupFrames}, Measure: {MeasureFrames}, dt={DeltaTime:F6}");
            Console.WriteLine();

            double csharpAverage = RunCSharp();
            double cppAverage = RunCpp();
            double ispcAverage = RunIspc();
            VerifyResults("Light Verify", Epsilon);

            Console.WriteLine();
            Console.WriteLine($"C# IJobChunk : {csharpAverage:F3} ms/frame");
            Console.WriteLine($"C++ IJobChunk: {cppAverage:F3} ms/frame");
            Console.WriteLine($"ISPC IJobChunk: {ispcAverage:F3} ms/frame");
            Console.WriteLine($"C++ Speedup  : {csharpAverage / cppAverage:F2}x");
            Console.WriteLine($"ISPC Speedup : {csharpAverage / ispcAverage:F2}x");

            Console.WriteLine();
            Console.WriteLine("=== IJobChunk 100w Heavy 计算对比 ===");
            Console.WriteLine($"实体数: {EntityCount:N0}, Warmup: {WarmupFrames}, Measure: {MeasureFrames}, dt={DeltaTime:F6}, iterations={HeavyIterations}");
            Console.WriteLine();

            double csharpHeavyAverage = RunHeavyCSharp();
            double cppHeavyAverage = RunHeavyCpp();
            double ispcHeavyAverage = RunHeavyIspc();
            VerifyResults("Heavy Verify", HeavyEpsilon);

            Console.WriteLine();
            Console.WriteLine($"C# Heavy IJobChunk  : {csharpHeavyAverage:F3} ms/frame");
            Console.WriteLine($"C++ Heavy IJobChunk : {cppHeavyAverage:F3} ms/frame");
            Console.WriteLine($"ISPC Heavy IJobChunk: {ispcHeavyAverage:F3} ms/frame");
            Console.WriteLine($"C++ Heavy Speedup   : {csharpHeavyAverage / cppHeavyAverage:F2}x");
            Console.WriteLine($"ISPC Heavy Speedup  : {csharpHeavyAverage / ispcHeavyAverage:F2}x");
        }

        private double RunCSharp()
        {
            World.DefaultWorld = _csharpWorld;
            var job = new MoveJobChunkCSharp { DeltaTime = DeltaTime };
            return RunBenchmark("C# IJobChunk", () => job.Schedule(_query).Complete());
        }

        private double RunCpp()
        {
            World.DefaultWorld = _cppWorld;
            var job = new MoveJobChunkCpp { DeltaTime = DeltaTime };
            return RunBenchmark("C++ IJobChunk", () => job.Schedule(_query).Complete());
        }

        private double RunIspc()
        {
            World.DefaultWorld = _ispcWorld;
            var job = new MoveJobChunkIspc { DeltaTime = DeltaTime };
            return RunBenchmark("ISPC IJobChunk", () => job.Schedule(_query).Complete());
        }

        private double RunHeavyCSharp()
        {
            World.DefaultWorld = _csharpWorld;
            var job = new HeavyJobChunkCSharp { DeltaTime = DeltaTime };
            return RunBenchmark("C# Heavy IJobChunk", () => job.Schedule(_query).Complete());
        }

        private double RunHeavyCpp()
        {
            World.DefaultWorld = _cppWorld;
            var job = new HeavyJobChunkCpp { DeltaTime = DeltaTime };
            return RunBenchmark("C++ Heavy IJobChunk", () => job.Schedule(_query).Complete());
        }

        private double RunHeavyIspc()
        {
            World.DefaultWorld = _ispcWorld;
            var job = new HeavyJobChunkIspc { DeltaTime = DeltaTime };
            return RunBenchmark("ISPC Heavy IJobChunk", () => job.Schedule(_query).Complete());
        }

        private static double RunBenchmark(string label, Action scheduleAndComplete)
        {
            for (int frame = 0; frame < WarmupFrames; frame++)
            {
                scheduleAndComplete();
            }

            double totalMilliseconds = 0;
            for (int frame = 0; frame < MeasureFrames; frame++)
            {
                long start = Stopwatch.GetTimestamp();
                scheduleAndComplete();
                long end = Stopwatch.GetTimestamp();
                totalMilliseconds += (end - start) * 1000.0 / Stopwatch.Frequency;
            }

            double average = totalMilliseconds / MeasureFrames;
            Console.WriteLine($"{label,-14}: avg={average:F3} ms");
            return average;
        }

        private void VerifyResults(string label, float epsilon)
        {
            float cppMaxDiff = 0;
            float ispcMaxDiff = 0;
            int cppMismatchCount = 0;
            int ispcMismatchCount = 0;

            var csharpChunks = GetPositionChunks(_csharpWorld);
            var cppChunks = GetPositionChunks(_cppWorld);
            var ispcChunks = GetPositionChunks(_ispcWorld);
            int entityOffset = 0;

            for (int chunkIndex = 0; chunkIndex < csharpChunks.Length; chunkIndex++)
            {
                var csharpChunk = csharpChunks[chunkIndex];
                var cppChunk = cppChunks[chunkIndex];
                var ispcChunk = ispcChunks[chunkIndex];
                int count = Math.Min(csharpChunk.Count, Math.Min(cppChunk.Count, ispcChunk.Count));

                for (int localIndex = 0; localIndex < count; localIndex++)
                {
                    int entityIndex = entityOffset + localIndex;
                    float2 csharp = csharpChunk.Positions[localIndex].Value;
                    float2 cpp = cppChunk.Positions[localIndex].Value;
                    float2 ispc = ispcChunk.Positions[localIndex].Value;
                    float cppDiff = MathF.Max(MathF.Abs(csharp.x - cpp.x), MathF.Abs(csharp.y - cpp.y));
                    float ispcDiff = MathF.Max(MathF.Abs(csharp.x - ispc.x), MathF.Abs(csharp.y - ispc.y));
                    if (cppDiff > epsilon)
                    {
                        cppMismatchCount++;
                        if (cppMismatchCount <= 3)
                        {
                            Console.WriteLine($"C++ mismatch entity {entityIndex}: C#=({csharp.x:F4},{csharp.y:F4}) C++=({cpp.x:F4},{cpp.y:F4}) diff={cppDiff:E4}");
                        }
                    }

                    if (ispcDiff > epsilon)
                    {
                        ispcMismatchCount++;
                        if (ispcMismatchCount <= 3)
                        {
                            Console.WriteLine($"ISPC mismatch entity {entityIndex}: C#=({csharp.x:F4},{csharp.y:F4}) ISPC=({ispc.x:F4},{ispc.y:F4}) diff={ispcDiff:E4}");
                        }
                    }

                    if (cppDiff > cppMaxDiff) cppMaxDiff = cppDiff;
                    if (ispcDiff > ispcMaxDiff) ispcMaxDiff = ispcDiff;
                }

                entityOffset += count;
            }

            bool passed = cppMismatchCount == 0 && ispcMismatchCount == 0;
            Console.WriteLine(passed
                ? $"{label,-13}: OK, cppMaxDiff={cppMaxDiff:E4}, ispcMaxDiff={ispcMaxDiff:E4}, epsilon={epsilon:E4}"
                : $"{label,-13}: ERROR, cppMismatch={cppMismatchCount:N0}, ispcMismatch={ispcMismatchCount:N0}, cppMaxDiff={cppMaxDiff:E4}, ispcMaxDiff={ispcMaxDiff:E4}, epsilon={epsilon:E4}");
        }

        private static void CreateEntities(World world)
        {
            var entityManager = world.EntityManager;
            for (int index = 0; index < EntityCount; index++)
            {
                var entity = entityManager.NewEntity(typeof(MovePosition), typeof(MoveVelocity));
                entityManager.Set(entity, new MovePosition { Value = CreateInitialPosition(index) });
                entityManager.Set(entity, new MoveVelocity { Value = CreateInitialVelocity(index) });
            }
        }

        private static float2 CreateInitialPosition(int index)
        {
            float x = index % 1920;
            float y = index % 1080;
            return new float2(x, y);
        }

        private static float2 CreateInitialVelocity(int index)
        {
            float x = ((index * 17) % 201 - 100) * 0.25f;
            float y = ((index * 31) % 201 - 100) * 0.25f;
            return new float2(x, y);
        }

        private PositionChunkView[] GetPositionChunks(World world)
        {
            var chunks = new List<PositionChunkView>();
            var entityManager = world.EntityManager;
            for (int archetypeIndex = 0; archetypeIndex < entityManager.ArchetypeCount; archetypeIndex++)
            {
                var archetype = entityManager.Archetypes[archetypeIndex];
                if (archetype == null || !archetype.IsMatch(_query))
                {
                    continue;
                }

                foreach (var chunk in archetype.GetChunks())
                {
                    if (chunk.EntityCount == 0)
                    {
                        continue;
                    }

                    int positionTypeIndex = archetype.GetComponentTypeIndex<MovePosition>();
                    chunks.Add(new PositionChunkView((MovePosition*)chunk.GetComponentArrayPointer(positionTypeIndex), chunk.EntityCount));
                }
            }

            return chunks.ToArray();
        }

        private readonly struct PositionChunkView
        {
            public readonly MovePosition* Positions;
            public readonly int Count;

            public PositionChunkView(MovePosition* positions, int count)
            {
                Positions = positions;
                Count = count;
            }
        }

        public void Dispose()
        {
            _ispcWorld.Dispose();
            _cppWorld.Dispose();
            _csharpWorld.Dispose();
        }
    }

}
