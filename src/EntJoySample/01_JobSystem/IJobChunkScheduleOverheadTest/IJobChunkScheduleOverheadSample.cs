using EntJoy;
using EntJoy.Collections;
using System.Diagnostics;

namespace EntJoySample.IJobChunkScheduleOverheadTest
{
    public struct ScheduleValue : IComponentData
    {
        public int Value;
    }

    public struct EmptyChunkJobCSharp : IJobChunk
    {
        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
    public struct EmptyChunkJobCpp : IJobChunk
    {
        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.fast)]
    public struct EmptyChunkJobIspc : IJobChunk
    {
        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
        }
    }

    public struct AddOneChunkJobCSharp : IJobChunk
    {
        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            Span<ScheduleValue> values = chunk.GetComponentDataSpan<ScheduleValue>();
            for (int index = 0; index < values.Length; index++)
            {
                ScheduleValue value = values[index];
                value.Value += 1;
                values[index] = value;
            }
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
    public struct AddOneChunkJobCpp : IJobChunk
    {
        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            NativeArray<ScheduleValue> values = chunk.GetComponentDataNativeArray<ScheduleValue>();
            for (int index = 0; index < values.Length; index++)
            {
                ScheduleValue value = values[index];
                value.Value += 1;
                values[index] = value;
            }
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.fast)]
    public struct AddOneChunkJobIspc : IJobChunk
    {
        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            NativeArray<ScheduleValue> values = chunk.GetComponentDataNativeArray<ScheduleValue>();
            for (int index = 0; index < values.Length; index++)
            {
                ScheduleValue value = values[index];
                value.Value += 1;
                values[index] = value;
            }
        }
    }

    public sealed unsafe class IJobChunkScheduleOverheadSample : IDisposable
    {
        private const int EntityCount = 1_000_000;
        private const int WarmupFrames = 20;
        private const int MeasureFrames = 1_000;

        private readonly QueryBuilder _query = new QueryBuilder().WithAll<ScheduleValue>();
        private readonly World _csharpWorld;
        private readonly World _cppWorld;
        private readonly World _ispcWorld;

        public IJobChunkScheduleOverheadSample()
        {
            Console.WriteLine($"Preparing {EntityCount:N0} entities...");

            _csharpWorld = new World("IJobChunkScheduleOverhead_CSharp");
            CreateEntities(_csharpWorld);

            _cppWorld = new World("IJobChunkScheduleOverhead_Cpp");
            CreateEntities(_cppWorld);

            _ispcWorld = new World("IJobChunkScheduleOverhead_Ispc");
            CreateEntities(_ispcWorld);
        }

        public void Run()
        {
            Console.WriteLine();
            Console.WriteLine("=== IJobChunk 调度固定开销测试 ===");
            Console.WriteLine($"实体数: {EntityCount:N0}, Warmup: {WarmupFrames}, Measure: {MeasureFrames}");
            Console.WriteLine("Empty: Execute 空函数，只测 Schedule/Complete/chunk dispatch。");
            Console.WriteLine("AddOne: 每实体 int +1，测极轻 kernel + 调度固定开销。");
            Console.WriteLine();

            double csharpEmpty = RunInWorld(_csharpWorld, "C# Empty IJobChunk", () => new EmptyChunkJobCSharp().Schedule(_query).Complete());
            double cppEmpty = RunInWorld(_cppWorld, "C++ Empty IJobChunk", () => new EmptyChunkJobCpp().Schedule(_query).Complete());
            double ispcEmpty = RunInWorld(_ispcWorld, "ISPC Empty IJobChunk", () => new EmptyChunkJobIspc().Schedule(_query).Complete());

            Console.WriteLine();
            double csharpAddOne = RunInWorld(_csharpWorld, "C# AddOne IJobChunk", () => new AddOneChunkJobCSharp().Schedule(_query).Complete());
            double cppAddOne = RunInWorld(_cppWorld, "C++ AddOne IJobChunk", () => new AddOneChunkJobCpp().Schedule(_query).Complete());
            double ispcAddOne = RunInWorld(_ispcWorld, "ISPC AddOne IJobChunk", () => new AddOneChunkJobIspc().Schedule(_query).Complete());
            VerifyAddOne();

            Console.WriteLine();
            Console.WriteLine("=== Summary ===");
            Console.WriteLine($"C# Empty   : {csharpEmpty:F4} ms/frame");
            Console.WriteLine($"C++ Empty  : {cppEmpty:F4} ms/frame");
            Console.WriteLine($"ISPC Empty : {ispcEmpty:F4} ms/frame");
            Console.WriteLine($"C# AddOne  : {csharpAddOne:F4} ms/frame");
            Console.WriteLine($"C++ AddOne : {cppAddOne:F4} ms/frame");
            Console.WriteLine($"ISPC AddOne: {ispcAddOne:F4} ms/frame");
        }

        private static double RunInWorld(World world, string label, Action scheduleAndComplete)
        {
            World.DefaultWorld = world;

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
            Console.WriteLine($"{label,-22}: avg={average:F4} ms");
            return average;
        }

        private void VerifyAddOne()
        {
            int expected = WarmupFrames + MeasureFrames;
            bool csharpOk = VerifyWorld(_csharpWorld, expected, out int csharpActual);
            bool cppOk = VerifyWorld(_cppWorld, expected, out int cppActual);
            bool ispcOk = VerifyWorld(_ispcWorld, expected, out int ispcActual);

            Console.WriteLine(csharpOk && cppOk && ispcOk
                ? $"Verify AddOne        : OK, value={expected}"
                : $"Verify AddOne        : ERROR, expected={expected}, C#={csharpActual}, C++={cppActual}, ISPC={ispcActual}");
        }

        private bool VerifyWorld(World world, int expected, out int firstActual)
        {
            World.DefaultWorld = world;
            firstActual = int.MinValue;
            var entityManager = world.EntityManager;
            for (int archetypeIndex = 0; archetypeIndex < entityManager.ArchetypeCount; archetypeIndex++)
            {
                var archetype = entityManager.Archetypes[archetypeIndex];
                if (archetype == null || !archetype.IsMatch(_query))
                {
                    continue;
                }

                int valueTypeIndex = archetype.GetComponentTypeIndex<ScheduleValue>();
                foreach (var chunk in archetype.GetChunks())
                {
                    if (chunk.EntityCount == 0)
                    {
                        continue;
                    }

                    var values = (ScheduleValue*)chunk.GetComponentArrayPointer(valueTypeIndex);
                    for (int index = 0; index < chunk.EntityCount; index++)
                    {
                        int actual = values[index].Value;
                        if (firstActual == int.MinValue) firstActual = actual;
                        if (actual != expected) return false;
                    }
                }
            }

            return true;
        }

        private static void CreateEntities(World world)
        {
            var entityManager = world.EntityManager;
            for (int index = 0; index < EntityCount; index++)
            {
                var entity = entityManager.NewEntity(typeof(ScheduleValue));
                entityManager.Set(entity, new ScheduleValue { Value = 0 });
            }
        }

        public void Dispose()
        {
            _csharpWorld.Dispose();
            _cppWorld.Dispose();
            _ispcWorld.Dispose();
        }
    }
}
