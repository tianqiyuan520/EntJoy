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

    public sealed unsafe class IJobChunkMoveCompareSample : IDisposable
    {
        private const int EntityCount = 1_000_000;
        private const int WarmupFrames = 5;
        private const int MeasureFrames = 500;
        private const float DeltaTime = 1.0f / 60.0f;
        private const float Epsilon = 0.001f;

        private readonly QueryBuilder _query = new QueryBuilder().WithAll<MovePosition, MoveVelocity>();
        private readonly World _csharpWorld;
        private readonly World _cppWorld;

        public IJobChunkMoveCompareSample()
        {
            Console.WriteLine($"Preparing {EntityCount:N0} entities...");

            _csharpWorld = new World("IJobChunkMoveCompare_CSharp");
            CreateEntities(_csharpWorld);

            _cppWorld = new World("IJobChunkMoveCompare_Cpp");
            CreateEntities(_cppWorld);
        }

        public void Run()
        {
            NativeJobScheduler.PrewakeWorkersOnce();

            Console.WriteLine();
            Console.WriteLine("=== IJobChunk 100w 移动对比 ===");
            Console.WriteLine($"实体数: {EntityCount:N0}, Warmup: {WarmupFrames}, Measure: {MeasureFrames}, dt={DeltaTime:F6}");
            Console.WriteLine();

            double csharpAverage = RunCSharp();
            double cppAverage = RunCpp();
            VerifyResults();

            Console.WriteLine();
            Console.WriteLine($"C# IJobChunk : {csharpAverage:F3} ms/frame");
            Console.WriteLine($"C++ IJobChunk: {cppAverage:F3} ms/frame");
            Console.WriteLine($"Speedup      : {csharpAverage / cppAverage:F2}x");
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

        private void VerifyResults()
        {
            float maxDiff = 0;
            int mismatchCount = 0;

            var csharpChunks = GetPositionChunks(_csharpWorld);
            var cppChunks = GetPositionChunks(_cppWorld);
            int entityOffset = 0;

            for (int chunkIndex = 0; chunkIndex < csharpChunks.Length; chunkIndex++)
            {
                var csharpChunk = csharpChunks[chunkIndex];
                var cppChunk = cppChunks[chunkIndex];
                int count = Math.Min(csharpChunk.Count, cppChunk.Count);

                for (int localIndex = 0; localIndex < count; localIndex++)
                {
                    int entityIndex = entityOffset + localIndex;
                    float2 csharp = csharpChunk.Positions[localIndex].Value;
                    float2 cpp = cppChunk.Positions[localIndex].Value;
                    float diff = MathF.Max(MathF.Abs(csharp.x - cpp.x), MathF.Abs(csharp.y - cpp.y));
                    if (diff > Epsilon)
                    {
                        mismatchCount++;
                        if (mismatchCount <= 3)
                        {
                            Console.WriteLine($"Mismatch entity {entityIndex}: C#=({csharp.x:F4},{csharp.y:F4}) C++=({cpp.x:F4},{cpp.y:F4}) diff={diff:E4}");
                        }
                    }

                    if (diff > maxDiff)
                    {
                        maxDiff = diff;
                    }
                }

                entityOffset += count;
            }

            Console.WriteLine(mismatchCount == 0
                ? $"Verify       : OK, maxDiff={maxDiff:E4}"
                : $"Verify       : ERROR, mismatch={mismatchCount:N0}, maxDiff={maxDiff:E4}");
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
            _cppWorld.Dispose();
            _csharpWorld.Dispose();
        }
    }

}
