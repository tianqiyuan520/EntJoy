using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;
using NativeTranspiler;
using NativeTranspiler.Bindings;

namespace NativeTranspilerFixture;

public struct ReadLink : IRelationComponent { public RelationSlot Target; public int Weight; }
public struct ReadLinkOutput : IComponentData { public int Value; }

[NativeTranspile(Target = BackendTarget.Cpp)]
public struct ReadLinkCpp : IJobChunk
{
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
    {
        var links = chunk.GetComponentReadOnlyDataSpan<ReadLink>();
        var output = chunk.GetComponentDataSpan<ReadLinkOutput>();
        for (int i = 0; i < chunk.Count; i++) output[i].Value = links[i].Target.TargetId + links[i].Weight;
    }
}

[NativeTranspile(Target = BackendTarget.Cpp, AutoSIMD = AutoSIMD.Enabled)]
public struct ReadLinkCppSimd : IJobChunk
{
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
    {
        var links = chunk.GetComponentReadOnlyDataNativeArray<ReadLink>();
        var output = chunk.GetComponentDataSpan<ReadLinkOutput>();
        for (int i = 0; i < chunk.Count; i++) output[i].Value = links[i].Target.TargetId + links[i].Weight;
    }
}

[NativeTranspile(Target = BackendTarget.Cpp, AutoSIMD = AutoSIMD.Vectorize)]
public struct ReadLinkCppVectorize : IJobEntity
{
    public void Execute(ReadLink link, ref ReadLinkOutput output)
    {
        link.Weight += 5;
        output.Value = link.Target.TargetId + link.Weight;
    }
}

[NativeTranspile(Target = BackendTarget.Cpp)]
public struct ReadLinkCppCopy : IJobEntity
{
    public void Execute(ReadLink link, ref ReadLinkOutput output)
    {
        link.Weight += 5;
        output.Value = link.Target.TargetId + link.Weight;
    }
}

[NativeTranspile(Target = BackendTarget.Cpp)]
public struct ReadLinkCppIn : IJobEntity
{
    public void Execute(in ReadLink link, ref ReadLinkOutput output)
        { output.Value = link.Target.TargetId + link.Weight; }
}

#if ENTJOY_FIXTURE_ISPC
[NativeTranspile(Target = BackendTarget.Ispc)]
public struct ReadLinkIspc : IJobChunk
{
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
    {
        var links = chunk.GetComponentReadOnlyDataSpan<ReadLink>();
        var output = chunk.GetComponentDataSpan<ReadLinkOutput>();
        for (int i = 0; i < chunk.Count; i++) output[i].Value = links[i].Target.TargetId + links[i].Weight;
    }
}

[NativeTranspile(Target = BackendTarget.Ispc)]
public struct ReadLinkIspcArray : IJobChunk
{
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
    {
        var links = chunk.GetComponentReadOnlyDataNativeArray<ReadLink>();
        var output = chunk.GetComponentDataSpan<ReadLinkOutput>();
        for (int i = 0; i < chunk.Count; i++) output[i].Value = links[i].Target.TargetId + links[i].Weight;
    }
}

[NativeTranspile(Target = BackendTarget.Ispc)]
public struct ReadLinkIspcCopy : IJobEntity
{
    public void Execute(ReadLink link, ref ReadLinkOutput output)
    {
        link.Weight += 5;
        output.Value = link.Target.TargetId + link.Weight;
    }
}

[NativeTranspile(Target = BackendTarget.Ispc)]
public struct ReadLinkIspcIn : IJobEntity
{
    public void Execute(in ReadLink link, ref ReadLinkOutput output)
        { output.Value = link.Target.TargetId + link.Weight; }
}
#endif

public static class RelationshipReadFixture
{
    public static void Run()
    {
        int comparisons = 0;
        foreach (int count in new[] { 1, 7, 8, 9, 63, 64, 65, 257, 4097 })
        {
            using var world = new World("RelationshipRead");
            var em = world.EntityManager;
            var target = em.NewEntity(Array.Empty<ComponentType>());
            var entities = new Entity[count];
            for (int i = 0; i < count; i++)
            {
                entities[i] = em.NewEntity(typeof(ReadLink), typeof(ReadLinkOutput));
                em.SetComponent(entities[i], new ReadLink { Target = RelationSlot.From(target), Weight = i * 3 });
            }
            var query = new QueryBuilder().WithAll<ReadLink>().WithAll<ReadLinkOutput>();
            int paths = 5;
#if ENTJOY_FIXTURE_ISPC
            paths = 9;
#endif
            for (int path = 0; path < paths; path++)
            for (int schedule = 0; schedule < 4; schedule++)
            {
                foreach (var entity in entities) em.SetComponent(entity, new ReadLinkOutput { Value = -1 });
                switch (path)
                {
                    case 0:
                    {
                        var job = new ReadLinkCpp();
                        if (schedule == 0) NativeExports.RunImmediate_ReadLinkCpp(ref job, query, world);
                        else if (schedule == 1) NativeExports.Schedule_ReadLinkCpp(ref job, query, default, world).Complete();
                        else RunRaw(ref job, schedule, world, query);
                        break;
                    }
                    case 1:
                    {
                        var job = new ReadLinkCppSimd();
                        if (schedule == 0) NativeExports.RunImmediate_ReadLinkCppSimd(ref job, query, world);
                        else if (schedule == 1) NativeExports.Schedule_ReadLinkCppSimd(ref job, query, default, world).Complete();
                        else RunRaw(ref job, schedule, world, query);
                        break;
                    }
                    case 2:
                    {
                        var job = new ReadLinkCppVectorize();
                        if (schedule == 0) NativeExports.RunImmediate_ReadLinkCppVectorize(ref job, query, world);
                        else if (schedule == 1) NativeExports.Schedule_ReadLinkCppVectorize(ref job, query, default, world).Complete();
                        else RunRaw(ref job, schedule, world, query);
                        break;
                    }
                    case 3:
                    {
                        var job = new ReadLinkCppCopy();
                        if (schedule == 0) NativeExports.RunImmediate_ReadLinkCppCopy(ref job, query, world);
                        else if (schedule == 1) NativeExports.Schedule_ReadLinkCppCopy(ref job, query, default, world).Complete();
                        else RunRaw(ref job, schedule, world, query);
                        break;
                    }
                    case 4:
                    {
                        var job = new ReadLinkCppIn();
                        if (schedule == 0) NativeExports.RunImmediate_ReadLinkCppIn(ref job, query, world);
                        else if (schedule == 1) NativeExports.Schedule_ReadLinkCppIn(ref job, query, default, world).Complete();
                        else RunRaw(ref job, schedule, world, query);
                        break;
                    }
#if ENTJOY_FIXTURE_ISPC
                    case 5:
                    {
                        var job = new ReadLinkIspc();
                        if (schedule == 0) NativeExports.RunImmediate_ReadLinkIspc(ref job, query, world);
                        else if (schedule == 1) NativeExports.Schedule_ReadLinkIspc(ref job, query, default, world).Complete();
                        else RunRaw(ref job, schedule, world, query);
                        break;
                    }
                    case 6:
                    {
                        var job = new ReadLinkIspcArray();
                        if (schedule == 0) NativeExports.RunImmediate_ReadLinkIspcArray(ref job, query, world);
                        else if (schedule == 1) NativeExports.Schedule_ReadLinkIspcArray(ref job, query, default, world).Complete();
                        else RunRaw(ref job, schedule, world, query);
                        break;
                    }
                    case 7:
                    {
                        var job = new ReadLinkIspcCopy();
                        if (schedule == 0) NativeExports.RunImmediate_ReadLinkIspcCopy(ref job, query, world);
                        else if (schedule == 1) NativeExports.Schedule_ReadLinkIspcCopy(ref job, query, default, world).Complete();
                        else RunRaw(ref job, schedule, world, query);
                        break;
                    }
                    case 8:
                    {
                        var job = new ReadLinkIspcIn();
                        if (schedule == 0) NativeExports.RunImmediate_ReadLinkIspcIn(ref job, query, world);
                        else if (schedule == 1) NativeExports.Schedule_ReadLinkIspcIn(ref job, query, default, world).Complete();
                        else RunRaw(ref job, schedule, world, query);
                        break;
                    }
#endif
                }
                int extra = path == 2 || path == 3 || path == 7 ? 5 : 0;
                for (int i = 0; i < count; i++)
                {
                    var link = em.GetComponentReadOnly<ReadLink>(entities[i]);
                    var output = em.GetComponentReadOnly<ReadLinkOutput>(entities[i]);
                    if (link.Weight != i * 3 || !link.Target.ToEntity().Equals(target) || output.Value != target.Id + i * 3 + extra)
                        throw new InvalidOperationException($"Relationship read: count={count}, path={path}, schedule={schedule}, index={i}, output={output.Value}, weight={link.Weight}");
                    comparisons++;
                }
                if (em.GetRelationsOf<ReadLink>(target).Length != count) throw new InvalidOperationException("Reverse relation index mismatch.");
            }
        }
        Console.WriteLine($"PASS: Relationship readonly native kernels: {comparisons} comparisons");
    }

    private static void RunRaw<T>(ref T job, int path, World world, QueryBuilder query) where T : struct
    {
        string entry = $"Get_SharpNative_Job_NativeTranspilerFixture_{typeof(T).Name}_Execute";
        int[] required = { ComponentTypeManager.GetComponentType(typeof(ReadLink)).Id, ComponentTypeManager.GetComponentType(typeof(ReadLinkOutput)).Id };
        var handle = path == 2
            ? ChunkJobScheduler.ScheduleEntityRawWithWorkerCapAndRangeSize(ref job, world.EntityManager, query,
                NativeJobScheduler.GetNativeExportPtr(entry + "_AdapterPtr"), required, 1, 1, world: world)
            : ChunkJobScheduler.ScheduleChunkRangeRaw(ref job, world.EntityManager, query,
                NativeJobScheduler.GetNativeExportPtr(entry + "_RangeAdapterPtr"), required, world: world);
        NativeJobScheduler.Complete(ref handle);
    }
}
