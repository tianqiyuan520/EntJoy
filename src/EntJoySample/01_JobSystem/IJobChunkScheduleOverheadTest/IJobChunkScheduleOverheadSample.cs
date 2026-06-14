using EntJoy;
using EntJoy.Collections;
using System.Diagnostics;

namespace EntJoySample.IJobChunkScheduleOverheadTest
{
    public struct ScheduleValue : IComponentData
    {
        public int Value;
    }

    public struct EmptyJobCSharp : IJob
    {
        public int Value;

        public void Execute()
        {
            Value += 1;
        }
    }

    public struct EmptyForJobCSharp : IJobFor
    {
        public void Execute(int index)
        {
        }
    }

    public struct AddOneForJobCSharp : IJobFor
    {
        public NativeArray<int> Values;

        public void Execute(int index)
        {
            Values[index] = Values[index] + 1;
        }
    }

    public struct EmptyParallelForJobCSharp : IJobParallelFor
    {
        public void Execute(int index)
        {
        }
    }

    public struct AddOneParallelForJobCSharp : IJobParallelFor
    {
        public NativeArray<int> Values;

        public void Execute(int index)
        {
            Values[index] = Values[index] + 1;
        }
    }

    public struct EmptyParallelForBatchJobCSharp : IJobParallelForBatch
    {
        public void Execute(int startIndex, int count)
        {
        }
    }

    public struct AddOneParallelForBatchJobCSharp : IJobParallelForBatch
    {
        public NativeArray<int> Values;

        public void Execute(int startIndex, int count)
        {
            int end = startIndex + count;
            for (int index = startIndex; index < end; index++)
            {
                Values[index] = Values[index] + 1;
            }
        }
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
        private const int BatchSize = 256;

        private readonly QueryBuilder _query = new QueryBuilder().WithAll<ScheduleValue>();
        private readonly NativeArray<int> _forValues;
        private readonly NativeArray<int> _parallelForValues;
        private readonly NativeArray<int> _parallelForBatchValues;
        private readonly World _csharpWorld;
        private readonly World _cppWorld;
        private readonly World _ispcWorld;

        public IJobChunkScheduleOverheadSample()
        {
            Console.WriteLine($"Preparing {EntityCount:N0} entities/array elements...");

            _forValues = new NativeArray<int>(EntityCount, Allocator.Persistent);
            _parallelForValues = new NativeArray<int>(EntityCount, Allocator.Persistent);
            _parallelForBatchValues = new NativeArray<int>(EntityCount, Allocator.Persistent);

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
            Console.WriteLine($"实体数/数组长度: {EntityCount:N0}, Warmup: {WarmupFrames}, Measure: {MeasureFrames}, BatchSize: {BatchSize}");
            Console.WriteLine("Empty: Execute 空函数，只测 Schedule/Complete/chunk dispatch。");
            Console.WriteLine("AddOne: 每实体 int +1，测极轻 kernel + 调度固定开销。");
            Console.WriteLine();

            Console.WriteLine("--- 普通 C# JobSystem ---");
            double csharpJobEmpty = Run("C# Empty IJob", () => new EmptyJobCSharp().Schedule().Complete());
            double csharpForEmpty = Run("C# Empty IJobFor", () => new EmptyForJobCSharp().Schedule(EntityCount).Complete());
            double csharpParallelForEmpty = Run("C# Empty ParallelFor", () => new EmptyParallelForJobCSharp().Schedule(EntityCount, BatchSize).Complete());
            double csharpParallelForBatchEmpty = Run("C# Empty ParallelForBatch", () => new EmptyParallelForBatchJobCSharp().ScheduleBatch(EntityCount, BatchSize).Complete());

            Console.WriteLine();
            double csharpForAddOne = Run("C# AddOne IJobFor", () => new AddOneForJobCSharp { Values = _forValues }.Schedule(EntityCount).Complete());
            double csharpParallelForAddOne = Run("C# AddOne ParallelFor", () => new AddOneParallelForJobCSharp { Values = _parallelForValues }.Schedule(EntityCount, BatchSize).Complete());
            double csharpParallelForBatchAddOne = Run("C# AddOne ParallelForBatch", () => new AddOneParallelForBatchJobCSharp { Values = _parallelForBatchValues }.ScheduleBatch(EntityCount, BatchSize).Complete());
            VerifyNativeArrays();

            Console.WriteLine();
            Console.WriteLine("--- IJobChunk C# / C++ / ISPC ---");
            double csharpEmpty = RunInWorld(_csharpWorld, "C# Empty IJobChunk", () => new EmptyChunkJobCSharp().Schedule(_query).Complete());
            double cppEmpty = RunInWorld(_cppWorld, "C++ Empty IJobChunk", () => new EmptyChunkJobCpp().Schedule(_query).Complete());
            double ispcEmpty = RunInWorld(_ispcWorld, "ISPC Empty IJobChunk", () => new EmptyChunkJobIspc().Schedule(_query).Complete());

            Console.WriteLine();
            double csharpAddOne = RunInWorld(_csharpWorld, "C# AddOne IJobChunk", () => new AddOneChunkJobCSharp().Schedule(_query).Complete());
            double cppAddOne = RunInWorld(_cppWorld, "C++ AddOne IJobChunk", () => new AddOneChunkJobCpp().Schedule(_query).Complete());
            double ispcAddOne = RunInWorld(_ispcWorld, "ISPC AddOne IJobChunk", () => new AddOneChunkJobIspc().Schedule(_query).Complete());
            VerifyAddOne();
            VerifyFlushScheduledJobs();

            Console.WriteLine();
            Console.WriteLine("=== Summary ===");
            Console.WriteLine($"C# IJob Empty              : {csharpJobEmpty:F4} ms/frame");
            Console.WriteLine($"C# IJobFor Empty           : {csharpForEmpty:F4} ms/frame");
            Console.WriteLine($"C# ParallelFor Empty       : {csharpParallelForEmpty:F4} ms/frame");
            Console.WriteLine($"C# ParallelForBatch Empty  : {csharpParallelForBatchEmpty:F4} ms/frame");
            Console.WriteLine($"C# IJobFor AddOne          : {csharpForAddOne:F4} ms/frame");
            Console.WriteLine($"C# ParallelFor AddOne      : {csharpParallelForAddOne:F4} ms/frame");
            Console.WriteLine($"C# ParallelForBatch AddOne : {csharpParallelForBatchAddOne:F4} ms/frame");
            Console.WriteLine($"C# IJobChunk Empty         : {csharpEmpty:F4} ms/frame");
            Console.WriteLine($"C++ IJobChunk Empty        : {cppEmpty:F4} ms/frame");
            Console.WriteLine($"ISPC IJobChunk Empty       : {ispcEmpty:F4} ms/frame");
            Console.WriteLine($"C# IJobChunk AddOne        : {csharpAddOne:F4} ms/frame");
            Console.WriteLine($"C++ IJobChunk AddOne       : {cppAddOne:F4} ms/frame");
            Console.WriteLine($"ISPC IJobChunk AddOne      : {ispcAddOne:F4} ms/frame");
        }

        private static double Run(string label, Action scheduleAndComplete)
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
            Console.WriteLine($"{label,-30}: avg={average:F4} ms");
            return average;
        }

        private static double RunInWorld(World world, string label, Action scheduleAndComplete)
        {
            World.DefaultWorld = world;
            return Run(label, scheduleAndComplete);
        }

        private void VerifyNativeArrays()
        {
            int expected = WarmupFrames + MeasureFrames;
            bool forOk = VerifyNativeArray(_forValues, expected, out int forActual);
            bool parallelForOk = VerifyNativeArray(_parallelForValues, expected, out int parallelForActual);
            bool batchOk = VerifyNativeArray(_parallelForBatchValues, expected, out int batchActual);

            Console.WriteLine(forOk && parallelForOk && batchOk
                ? $"Verify C# AddOne     : OK, value={expected}"
                : $"Verify C# AddOne     : ERROR, expected={expected}, IJobFor={forActual}, ParallelFor={parallelForActual}, Batch={batchActual}");
        }

        private static bool VerifyNativeArray(NativeArray<int> values, int expected, out int firstActual)
        {
            firstActual = values.Length > 0 ? values[0] : int.MinValue;
            for (int index = 0; index < values.Length; index++)
            {
                if (values[index] != expected)
                {
                    firstActual = values[index];
                    return false;
                }
            }

            return true;
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

        private void VerifyFlushScheduledJobs()
        {
            World.DefaultWorld = _cppWorld;
            var handle = new AddOneChunkJobCpp().Schedule(_query);
            JobHandle.ScheduleBatchedJobs();
            handle.Complete();

            int expected = WarmupFrames + MeasureFrames + 1;
            bool ok = VerifyWorld(_cppWorld, expected, out int actual);
            Console.WriteLine(ok
                ? $"Verify Flush         : OK, value={expected}"
                : $"Verify Flush         : ERROR, expected={expected}, C++={actual}");
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
            _forValues.Dispose();
            _parallelForValues.Dispose();
            _parallelForBatchValues.Dispose();
            _csharpWorld.Dispose();
            _cppWorld.Dispose();
            _ispcWorld.Dispose();
        }
    }
}
