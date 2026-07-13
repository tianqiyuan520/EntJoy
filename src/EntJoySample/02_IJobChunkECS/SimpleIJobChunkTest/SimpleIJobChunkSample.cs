//using EntJoy;
//using EntJoy.Collections;
//using System;
//using System.Diagnostics;

//namespace EntJoySample.SimpleIJobChunkTest;

//public struct TestValue : IComponentData
//{
//    public int Value;
//}

//[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
//public readonly struct AddOneJob : IJobChunk
//{
//    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
//    {
//        NativeArray<TestValue> values = chunk.GetComponentDataNativeArray<TestValue>();
//        for (int index = 0; index < values.Length; index++)
//        {
//            TestValue value = values[index];
//            value.Value += 1;
//            values[index] = value;
//        }
//    }
//}

//public sealed unsafe class SimpleIJobChunkSample : IDisposable
//{
//    private readonly QueryBuilder _query = new QueryBuilder().WithAll<TestValue>();
//    private readonly World _world;

//    public SimpleIJobChunkSample()
//    {
//        _world = new World("SimpleIJobChunkWorld");

//        for (int index = 0; index < 8; index++)
//        {
//            var entity = _world.EntityManager.NewEntity(typeof(TestValue));
//            _world.EntityManager.Set(entity, new TestValue { Value = index });
//        }
//    }

//    public void Run()
//    {
//        PrintValues("Before");

//        var stopwatch = Stopwatch.StartNew();
//        new AddOneJob().Schedule(_query).Complete();
//        stopwatch.Stop();

//        PrintValues("After");
//        Console.WriteLine($"IJobChunk elapsed: {stopwatch.Elapsed.TotalMilliseconds:F4} ms");
//    }

//    private void PrintValues(string label)
//    {
//        Console.WriteLine(label + ":");
//        int printed = 0;

//        var entityManager = _world.EntityManager;
//        for (int archetypeIndex = 0; archetypeIndex < entityManager.ArchetypeCount; archetypeIndex++)
//        {
//            var archetype = entityManager.Archetypes[archetypeIndex];
//            if (archetype == null || !archetype.IsMatch(_query))
//            {
//                continue;
//            }

//            foreach (var chunk in archetype.GetChunks())
//            {
//                int valueTypeIndex = archetype.GetComponentTypeIndex<TestValue>();
//                TestValue* values = (TestValue*)chunk.GetComponentArrayPointer(valueTypeIndex);
//                for (int index = 0; index < chunk.EntityCount; index++)
//                {
//                    Console.WriteLine($"  entity[{printed}] = {values[index].Value}");
//                    printed++;
//                }
//            }
//        }
//    }

//    public void Dispose()
//    {
//        _world.Dispose();
//    }
//}
