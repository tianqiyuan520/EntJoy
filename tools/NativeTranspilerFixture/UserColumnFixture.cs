using EntJoy.Collections;
using EntJoy.ECS;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace NativeTranspilerFixture;

// 消费方自己的非拥有分段视图；转译器按普通结构体和同程序集静态函数处理。
public unsafe struct UserColumnView<T> where T : unmanaged
{
    public T* Flat;
    public T** Chunks;
    public int Capacity, Shift, Length;
}

public static unsafe class UserColumnAccess
{
    public static T* ElementPtr<T>(ref UserColumnView<T> view, int index) where T : unmanaged
    {
        if (view.Chunks == null) return view.Flat + index;
        if (view.Shift >= 0) return view.Chunks[index >> view.Shift] + (index & (view.Capacity - 1));
        int chunk = index / view.Capacity;
        int offset = index - chunk * view.Capacity;
        return view.Chunks[chunk] + offset;
    }
}

[NativeTranspile]
public unsafe struct UserColumnJob : IJobParallelFor
{
    public UserColumnView<int> Ints;
    public UserColumnView<long> Longs;

    public void Execute(int index)
    {
        *UserColumnAccess.ElementPtr(ref Ints, index) += 3;
        System.Threading.Interlocked.Add(ref *UserColumnAccess.ElementPtr(ref Longs, index), 0x100000001L);
    }
}

public struct UserFieldCursor { public int Value; }

public struct UserColumnEntry : IComponentData
{
    public int Slot;
    public long Proof;
}

[NativeTranspile]
public unsafe struct UserChunkColumnJob : IJobChunk
{
    public UserColumnView<int> Ints;
    public UserColumnView<long> Longs;

    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var entries = chunk.GetComponentDataNativeArray<UserColumnEntry>();
        for (int i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            entry.Proof = *UserColumnAccess.ElementPtr(ref Ints, entry.Slot)
                + *UserColumnAccess.ElementPtr(ref Longs, entry.Slot);
            entries[i] = entry;
        }
    }
}

public static class UserFieldAccess
{
    public static void Advance(ref int value) { value++; }
}

[NativeTranspile]
public struct UserFieldMutationJob : IJob
{
    public int Counter;
    public UserFieldCursor Cursor;
    public NativeArray<int> Output;

    public void Execute()
    {
        for (int i = 0; i < Output.Length; i++)
        {
            UserFieldAccess.Advance(ref Counter);
            this.Cursor.Value += 2;
            Output[i] = Counter + Cursor.Value;
        }
    }
}

public static unsafe class UserColumnFixture
{
    public static void Run()
    {
        using var output = new NativeArray<int>(17, Allocator.Persistent);
        for (int mode = 0; mode < 2; mode++)
        {
            var mutation = new UserFieldMutationJob { Counter = 3, Cursor = new UserFieldCursor { Value = 5 }, Output = output };
            if (mode == 0) mutation.Run(); else mutation.Schedule().Complete();
            for (int i = 0; i < output.Length; i++)
                if (output[i] != 11 + 3 * i) throw new InvalidOperationException($"Field mutation mismatch {i}");
        }
        foreach (int capacity in new[] { 0, 3, 128, 192 })
        {
            int length = capacity == 0 ? 259 : capacity + 2;
            using var ints = new NativeArray<int>(length, Allocator.Persistent);
            using var longs = new NativeArray<long>(length, Allocator.Persistent);
            using var intTable = new NativeArray<nint>(2, Allocator.Persistent);
            using var longTable = new NativeArray<nint>(2, Allocator.Persistent);
            var iv = new UserColumnView<int> { Flat = (int*)ints.GetUnsafePtr(), Length = length };
            var lv = new UserColumnView<long> { Flat = (long*)longs.GetUnsafePtr(), Length = length };
            if (capacity != 0)
            {
                var it = (int**)intTable.GetUnsafePtr();
                var lt = (long**)longTable.GetUnsafePtr();
                it[0] = iv.Flat; it[1] = iv.Flat + capacity;
                lt[0] = lv.Flat; lt[1] = lv.Flat + capacity;
                iv.Flat = null; iv.Chunks = it; iv.Capacity = capacity; iv.Shift = capacity == 128 ? 7 : -1;
                lv.Flat = null; lv.Chunks = lt; lv.Capacity = capacity; lv.Shift = iv.Shift;
            }
            var job = new UserColumnJob { Ints = iv, Longs = lv };
            job.Run(length);
            job.Schedule(length, 2).Complete();
            job.Run(0);
            for (int i = 0; i < length; i++)
                if (ints[i] != 6 || longs[i] != 0x200000002L)
                    throw new InvalidOperationException($"User column mismatch: capacity={capacity}, index={i}");

            using var world = new World("UserColumnFixture");
            world.CreateEntities(length, typeof(UserColumnEntry));
            var query = new QueryBuilder().WithAll<UserColumnEntry>();
            int slot = 0;
            foreach (var archetype in world.EntityManager.GetAllArchetypes())
            foreach (var chunk in archetype.GetChunks())
            {
                var entries = (UserColumnEntry*)chunk.GetComponentArrayPointer(archetype.GetComponentTypeIndex<UserColumnEntry>());
                for (int i = 0; i < chunk.EntityCount; i++) entries[i].Slot = slot++;
            }
            if (slot != length) throw new InvalidOperationException("User column entity coverage mismatch");
            var chunkJob = new UserChunkColumnJob { Ints = iv, Longs = lv };
            for (int mode = 0; mode < 2; mode++)
            {
                if (mode == 0) NativeTranspiler.Bindings.NativeExports.RunImmediate_UserChunkColumnJob(ref chunkJob, query, world);
                else NativeTranspiler.Bindings.NativeExports.Schedule_UserChunkColumnJob(ref chunkJob, query, default, world).Complete();
                foreach (var archetype in world.EntityManager.GetAllArchetypes())
                foreach (var chunk in archetype.GetChunks())
                {
                    var entries = (UserColumnEntry*)chunk.GetComponentArrayPointer(archetype.GetComponentTypeIndex<UserColumnEntry>());
                    for (int i = 0; i < chunk.EntityCount; i++)
                        if (entries[i].Proof != 0x200000008L)
                            throw new InvalidOperationException($"User chunk column mismatch: capacity={capacity}, slot={entries[i].Slot}");
                }
            }
        }
        Console.WriteLine("PASS: user generic views, mixed types, flat/chunked/tail, IJob/ParallelFor/Chunk Run/Schedule, field mutations and 64-bit atomics.");
    }
}
