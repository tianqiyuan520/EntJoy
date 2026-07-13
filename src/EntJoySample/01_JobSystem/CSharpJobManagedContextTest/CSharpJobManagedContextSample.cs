//using System.Diagnostics;
//using EntJoy;
//using EntJoy.Collections;

//namespace EntJoySample.CSharpJobManagedContextTest
//{
//    public struct ManagedContextValue : IComponentData
//    {
//        public int Value;
//    }

//    public struct EmptyUnmanagedJob : IJob
//    {
//        public int Delta;

//        public void Execute()
//        {
//            Delta += 1;
//        }
//    }

//    public struct EmptyManagedJob : IJob
//    {
//        public string Label;
//        public int Delta;

//        public void Execute()
//        {
//            if (Label.Length >= 0)
//            {
//                Delta += 1;
//            }
//        }
//    }

//    public struct AddOneUnmanagedParallelForJob : IJobParallelFor
//    {
//        public NativeArray<int> Values;

//        public void Execute(int index)
//        {
//            Values[index] = Values[index] + 1;
//        }
//    }

//    public struct AddOneManagedParallelForJob : IJobParallelFor
//    {
//        public NativeArray<int> Values;
//        public string Label;

//        public void Execute(int index)
//        {
//            if (Label.Length >= 0)
//            {
//                Values[index] = Values[index] + 1;
//            }
//        }
//    }

//    public struct AddOneUnmanagedChunkJob : IJobChunk
//    {
//        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
//        {
//            Span<ManagedContextValue> values = chunk.GetComponentDataSpan<ManagedContextValue>();
//            for (int index = 0; index < values.Length; index++)
//            {
//                ManagedContextValue value = values[index];
//                value.Value += 1;
//                values[index] = value;
//            }
//        }
//    }

//    public struct AddOneManagedChunkJob : IJobChunk
//    {
//        public string Label;

//        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
//        {
//            Span<ManagedContextValue> values = chunk.GetComponentDataSpan<ManagedContextValue>();
//            for (int index = 0; index < values.Length; index++)
//            {
//                ManagedContextValue value = values[index];
//                value.Value += Label.Length >= 0 ? 1 : 0;
//                values[index] = value;
//            }
//        }
//    }

//    public sealed unsafe class CSharpJobManagedContextSample : IDisposable
//    {
//        private const int EntityCount = 1_000_000;
//        private const int ArrayLength = 1_000_000;
//        private const int WarmupFrames = 20;
//        private const int MeasureFrames = 1_000;
//        private const int EmptyJobRepeatPerFrame = 1_000;
//        private const int BatchSize = 256;

//        private readonly QueryBuilder _query = new QueryBuilder().WithAll<ManagedContextValue>();
//        private readonly World _unmanagedChunkWorld;
//        private readonly World _managedChunkWorld;
//        private readonly NativeArray<int> _unmanagedArray;
//        private readonly NativeArray<int> _managedArray;

//        public CSharpJobManagedContextSample()
//        {
//            Console.WriteLine($"Preparing arrays/entities: {ArrayLength:N0} / {EntityCount:N0}...");

//            _unmanagedArray = new NativeArray<int>(ArrayLength, Allocator.Persistent);
//            _managedArray = new NativeArray<int>(ArrayLength, Allocator.Persistent);

//            _unmanagedChunkWorld = new World("CSharpJobManagedContext_UnmanagedChunk");
//            CreateEntities(_unmanagedChunkWorld);

//            _managedChunkWorld = new World("CSharpJobManagedContext_ManagedChunk");
//            CreateEntities(_managedChunkWorld);
//        }

//        public void Run()
//        {
//            Console.WriteLine();
//            Console.WriteLine("=== C# Job 托管/非托管上下文性能差异 ===");
//            Console.WriteLine($"Array: {ArrayLength:N0}, Entity: {EntityCount:N0}, Warmup: {WarmupFrames}, Measure: {MeasureFrames}, BatchSize: {BatchSize}");
//            Console.WriteLine("Unmanaged: Job struct 不含引用字段，走 raw-copy context 快路径。");
//            Console.WriteLine("Managed  : Job struct 含 string 字段，走 GCHandle 托管 context 安全路径。");
//            Console.WriteLine();

//            double unmanagedEmpty = RunRepeated("IJob Unmanaged Empty", EmptyJobRepeatPerFrame, () => new EmptyUnmanagedJob { Delta = 1 }.Schedule().Complete());
//            double managedEmpty = RunRepeated("IJob Managed Empty", EmptyJobRepeatPerFrame, () => new EmptyManagedJob { Label = "managed", Delta = 1 }.Schedule().Complete());

//            Console.WriteLine();
//            double unmanagedParallelFor = Run("ParallelFor Unmanaged AddOne", () => new AddOneUnmanagedParallelForJob { Values = _unmanagedArray }.Schedule(ArrayLength, BatchSize).Complete());
//            double managedParallelFor = Run("ParallelFor Managed AddOne", () => new AddOneManagedParallelForJob { Values = _managedArray, Label = "managed" }.Schedule(ArrayLength, BatchSize).Complete());
//            VerifyArrays();

//            Console.WriteLine();
//            double unmanagedChunk = RunChunk(_unmanagedChunkWorld, "IJobChunk Unmanaged AddOne", () => new AddOneUnmanagedChunkJob().Schedule(_query).Complete());
//            double managedChunk = RunChunk(_managedChunkWorld, "IJobChunk Managed AddOne", () => new AddOneManagedChunkJob { Label = "managed" }.Schedule(_query).Complete());
//            VerifyChunks();

//            Console.WriteLine();
//            Console.WriteLine("=== Summary ===");
//            PrintSummary("IJob Empty", unmanagedEmpty, managedEmpty);
//            PrintSummary("ParallelFor AddOne", unmanagedParallelFor, managedParallelFor);
//            PrintSummary("IJobChunk AddOne", unmanagedChunk, managedChunk);
//        }

//        private static double Run(string label, Action scheduleAndComplete)
//        {
//            for (int frame = 0; frame < WarmupFrames; frame++)
//            {
//                scheduleAndComplete();
//            }

//            double totalMilliseconds = 0;
//            for (int frame = 0; frame < MeasureFrames; frame++)
//            {
//                long start = Stopwatch.GetTimestamp();
//                scheduleAndComplete();
//                long end = Stopwatch.GetTimestamp();
//                totalMilliseconds += (end - start) * 1000.0 / Stopwatch.Frequency;
//            }

//            double average = totalMilliseconds / MeasureFrames;
//            Console.WriteLine($"{label,-30}: avg={average:F4} ms");
//            return average;
//        }

//        private static double RunRepeated(string label, int repeatPerFrame, Action scheduleAndComplete)
//        {
//            for (int frame = 0; frame < WarmupFrames; frame++)
//            {
//                for (int repeat = 0; repeat < repeatPerFrame; repeat++)
//                {
//                    scheduleAndComplete();
//                }
//            }

//            double totalMilliseconds = 0;
//            for (int frame = 0; frame < MeasureFrames; frame++)
//            {
//                long start = Stopwatch.GetTimestamp();
//                for (int repeat = 0; repeat < repeatPerFrame; repeat++)
//                {
//                    scheduleAndComplete();
//                }
//                long end = Stopwatch.GetTimestamp();
//                totalMilliseconds += (end - start) * 1000.0 / Stopwatch.Frequency;
//            }

//            double average = totalMilliseconds / MeasureFrames / repeatPerFrame;
//            Console.WriteLine($"{label,-30}: avg={average:F6} ms/job ({repeatPerFrame}x/frame)");
//            return average;
//        }

//        private static double RunChunk(World world, string label, Action scheduleAndComplete)
//        {
//            World.DefaultWorld = world;
//            return Run(label, scheduleAndComplete);
//        }

//        private static void PrintSummary(string label, double unmanagedMs, double managedMs)
//        {
//            double ratio = unmanagedMs > 0 ? managedMs / unmanagedMs : 0;
//            Console.WriteLine($"{label,-20}: unmanaged={unmanagedMs:F4} ms, managed={managedMs:F4} ms, managed/unmanaged={ratio:F2}x");
//        }

//        private void VerifyArrays()
//        {
//            int expected = WarmupFrames + MeasureFrames;
//            int unmanagedActual = _unmanagedArray[0];
//            int managedActual = _managedArray[0];
//            Console.WriteLine(unmanagedActual == expected && managedActual == expected
//                ? $"Verify ParallelFor       : OK, value={expected}"
//                : $"Verify ParallelFor       : ERROR, expected={expected}, unmanaged={unmanagedActual}, managed={managedActual}");
//        }

//        private void VerifyChunks()
//        {
//            int expected = WarmupFrames + MeasureFrames;
//            bool unmanagedOk = VerifyWorld(_unmanagedChunkWorld, expected, out int unmanagedActual);
//            bool managedOk = VerifyWorld(_managedChunkWorld, expected, out int managedActual);
//            Console.WriteLine(unmanagedOk && managedOk
//                ? $"Verify IJobChunk         : OK, value={expected}"
//                : $"Verify IJobChunk         : ERROR, expected={expected}, unmanaged={unmanagedActual}, managed={managedActual}");
//        }

//        private bool VerifyWorld(World world, int expected, out int firstActual)
//        {
//            World.DefaultWorld = world;
//            firstActual = int.MinValue;
//            var entityManager = world.EntityManager;
//            for (int archetypeIndex = 0; archetypeIndex < entityManager.ArchetypeCount; archetypeIndex++)
//            {
//                var archetype = entityManager.Archetypes[archetypeIndex];
//                if (archetype == null || !archetype.IsMatch(_query))
//                {
//                    continue;
//                }

//                int valueTypeIndex = archetype.GetComponentTypeIndex<ManagedContextValue>();
//                foreach (var chunk in archetype.GetChunks())
//                {
//                    if (chunk.EntityCount == 0)
//                    {
//                        continue;
//                    }

//                    var values = (ManagedContextValue*)chunk.GetComponentArrayPointer(valueTypeIndex);
//                    for (int index = 0; index < chunk.EntityCount; index++)
//                    {
//                        int actual = values[index].Value;
//                        if (firstActual == int.MinValue) firstActual = actual;
//                        if (actual != expected) return false;
//                    }
//                }
//            }

//            return true;
//        }

//        private static void CreateEntities(World world)
//        {
//            var entityManager = world.EntityManager;
//            for (int index = 0; index < EntityCount; index++)
//            {
//                var entity = entityManager.NewEntity(typeof(ManagedContextValue));
//                entityManager.Set(entity, new ManagedContextValue { Value = 0 });
//            }
//        }

//        public void Dispose()
//        {
//            _unmanagedArray.Dispose();
//            _managedArray.Dispose();
//            _unmanagedChunkWorld.Dispose();
//            _managedChunkWorld.Dispose();
//        }
//    }
//}
