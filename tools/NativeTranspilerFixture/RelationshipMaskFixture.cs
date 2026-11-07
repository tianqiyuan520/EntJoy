using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace NativeTranspilerFixture;

public struct MaskEnabled : IComponentData, IEnableableComponent { }
public struct MaskChunkVisits : IComponentData { public int Count; }

[NativeTranspile(Target = BackendTarget.Cpp)]
public struct RelationMaskCpp : IJobChunk
{
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
    {
        var output = chunk.GetComponentDataSpan<ReadLinkOutput>();
        var visits = chunk.GetComponentDataSpan<MaskChunkVisits>();
        visits[0].Count += 1;
        for (int i = 0; i < chunk.Count; i++)
        {
            if (mask.Length != 0 && !mask.IsEnabled(i)) continue;
            output[i].Value += 1;
        }
    }
}

[NativeTranspile(Target = BackendTarget.Cpp)]
public struct RelationMaskCppBits : IJobChunk
{
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
    {
        var output = chunk.GetComponentDataSpan<ReadLinkOutput>();
        for (int i = 0; i < chunk.Count; i++)
        {
            if (mask.Length != 0 && (mask.Bits[i >> 6] & (1UL << (i & 63))) == 0) continue;
            output[i].Value += 1;
        }
    }
}

[NativeTranspile(Target = BackendTarget.Cpp, AutoSIMD = AutoSIMD.Vectorize)]
public struct RelationMaskCppVectorize : IJobChunk
{
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
    {
        var output = chunk.GetComponentDataSpan<ReadLinkOutput>();
        for (int i = 0; i < chunk.Count; i++)
        {
            if (mask.Length != 0 && !mask.IsEnabled(i)) continue;
            output[i].Value += 1;
        }
    }
}

[NativeTranspile(Target = BackendTarget.Cpp)]
public struct RelationMaskEntityCpp : IJobEntity
{
    public void Execute(ref ReadLinkOutput output) { output.Value += 1; }
}

[NativeTranspile(Target = BackendTarget.Cpp, AutoSIMD = AutoSIMD.Vectorize)]
public struct RelationMaskEntityIdentityCpp : IJobEntity
{
    public void Execute(Entity entity, ref ReadLinkOutput output) { output.Value = entity.Id + 1; }
}

[NativeTranspile(Target = BackendTarget.Cpp)]
public struct RelationMaskRangesCpp : IJobChunk
{
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
    {
        var output = chunk.GetComponentDataSpan<ReadLinkOutput>();
        if (mask.Length == 0)
        {
            for (int i = 0; i < chunk.Count; i++) output[i].Value += 1;
        }
        else
        {
            int cursor = 0, first = 0, end = 0;
            while (mask.TryGetNextRange(ref cursor, out first, out end))
                for (int i = first; i < end; i++) output[i].Value += 1;
        }
    }
}

#if ENTJOY_FIXTURE_ISPC
[NativeTranspile(Target = BackendTarget.Ispc)]
public struct RelationMaskIspc : IJobChunk
{
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
    {
        var output = chunk.GetComponentDataSpan<ReadLinkOutput>();
        for (int i = 0; i < chunk.Count; i++)
        {
            if (mask.Length != 0 && !mask.IsEnabled(i)) continue;
            output[i].Value += 1;
        }
    }
}

[NativeTranspile(Target = BackendTarget.Ispc)]
public struct RelationMaskIspcBits : IJobChunk
{
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
    {
        var output = chunk.GetComponentDataSpan<ReadLinkOutput>();
        for (int i = 0; i < chunk.Count; i++)
        {
            if (mask.Length != 0 && (mask.Bits[i >> 6] & (1UL << (i & 63))) == 0) continue;
            output[i].Value += 1;
        }
    }
}

[NativeTranspile(Target = BackendTarget.Ispc, UseISPC_MT = true)]
public struct RelationMaskIspcTasks : IJobChunk
{
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
    {
        var output = chunk.GetComponentDataSpan<ReadLinkOutput>();
        for (int i = 0; i < chunk.Count; i++)
        {
            if (mask.Length != 0 && !mask.IsEnabled(i)) continue;
            output[i].Value += 1;
        }
    }
}

[NativeTranspile(Target = BackendTarget.Ispc)]
public struct RelationMaskEntityIspc : IJobEntity
{
    public void Execute(ref ReadLinkOutput output) { output.Value += 1; }
}

public struct RelationMaskEvent { public int Slot; }

[NativeTranspile(Target = BackendTarget.Ispc, UseISPC_MT = true)]
public struct RelationMaskIspcTaskEvents : IJobChunk
{
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
    {
        var output = chunk.GetComponentDataSpan<ReadLinkOutput>();
        for (int i = 0; i < chunk.Count; i++)
        {
            if (mask.Length != 0 && !mask.IsEnabled(i)) continue;
            output[i].Value += 1;
            SystemAPI.SendEvent(new RelationMaskEvent { Slot = i });
        }
    }
}
#endif

public static unsafe class RelationshipMaskFixture
{
    public static void Run()
    {
        var cases = new (Action<int, World, QueryBuilder> Run, int Paths, bool Visits, bool Identity)[]
        {
            ((path, world, query) => ExecuteChunk<RelationMaskCpp>(path, world, query, true), 5, true, false),
            ((path, world, query) => ExecuteChunk<RelationMaskCppBits>(path, world, query, false), 5, false, false),
            ((path, world, query) => ExecuteChunk<RelationMaskCppVectorize>(path, world, query, false), 5, false, false),
            ((path, world, query) => ExecuteChunk<RelationMaskRangesCpp>(path, world, query, false), 5, false, false),
            ((path, world, query) => Execute<RelationMaskEntityCpp>(path, world, query, false, entity: true), 3, false, false),
            ((path, world, query) => Execute<RelationMaskEntityIdentityCpp>(path, world, query, false, entity: true), 3, false, true),
#if ENTJOY_FIXTURE_ISPC
            ((path, world, query) => Execute<RelationMaskIspc>(path, world, query, false), 3, false, false),
            ((path, world, query) => Execute<RelationMaskIspcBits>(path, world, query, false), 3, false, false),
            ((path, world, query) => Execute<RelationMaskIspcTasks>(path, world, query, false), 3, false, false),
            ((path, world, query) => Execute<RelationMaskEntityIspc>(path, world, query, false, entity: true), 3, false, false),
#endif
        };
        int comparisons = 0;
        int previous = Archetype.ChunkCapacityOverride;
        Archetype.ChunkCapacityOverride = 65;
        try
        {
            foreach (int count in new[] { 0, 1, 63, 64, 65, 129, 4097 })
            {
                using var world = new World("NativeRelationshipMasks");
                var em = world.EntityManager;
                var a = em.NewEntity(Array.Empty<ComponentType>());
                var b = em.NewEntity(Array.Empty<ComponentType>());
                var unused = em.NewEntity(Array.Empty<ComponentType>());
                var sources = new Entity[count];
                var targets = new Entity[count];
                for (int i = 0; i < count; i++)
                {
                    sources[i] = em.NewEntity(typeof(ReadLink), typeof(ReadLinkOutput), typeof(MaskEnabled), typeof(MaskChunkVisits));
                    targets[i] = i % 131 < 65 ? a : b;
                    em.AddRelationship<ReadLink>(sources[i], targets[i]);
                    em.SetComponentEnabled<MaskEnabled>(sources[i], i % 5 != 0);
                }
                for (int scenario = 0; scenario < 7; scenario++)
                {
                    if (scenario == 6 && count > 0)
                    {
                        int version = em.StructuralVersion;
                        em.AddRelationship<ReadLink>(sources[0], b);
                        targets[0] = b;
                        if (version != em.StructuralVersion) throw new Exception("Retarget changed Archetype.");
                    }
                    var query = new QueryBuilder().WithAll<ReadLinkOutput>().WithAll<MaskChunkVisits>();
                    if (scenario != 0 && scenario != 5) query = query.WithRelationship<ReadLink>(scenario == 3 ? unused : scenario == 2 ? b : a);
                    if (scenario == 4 || scenario == 5) query = query.WithEnabled<MaskEnabled>();
                    var selected = new HashSet<Entity>();
                    for (int i = 0; i < count; i++)
                    {
                        bool matches = scenario == 0 || scenario == 5 || targets[i].Equals(scenario == 3 ? unused : scenario == 2 ? b : a);
                        if ((scenario == 4 || scenario == 5) && i % 5 == 0) matches = false;
                        if (matches) selected.Add(sources[i]);
                    }
                    for (int job = 0; job < cases.Length; job++)
                    for (int path = 0; path < cases[job].Paths; path++)
                    {
                        foreach (var source in sources)
                        {
                            em.SetComponent(source, default(ReadLinkOutput));
                            em.SetComponent(source, default(MaskChunkVisits));
                        }
                        cases[job].Run(path, world, query);
                        foreach (var source in sources)
                        {
                            int actual = em.GetComponentReadOnly<ReadLinkOutput>(source).Value;
                            if (actual != (selected.Contains(source) ? cases[job].Identity ? source.Id + 1 : 1 : 0))
                                throw new Exception($"Native relationship mask N={count}, scenario={scenario}, job={job}, path={path}, entity={source.Id}: {actual}");
                            comparisons++;
                        }
                        if (cases[job].Visits)
                            foreach (var arch in em.GetAllArchetypes())
                            {
                                if (!arch.Has(typeof(MaskChunkVisits))) continue;
                                foreach (var chunk in arch.GetChunks())
                                {
                                    bool any = false;
                                    var entities = (Entity*)chunk.GetEntityPointer();
                                    for (int i = 0; i < chunk.EntityCount; i++) any |= selected.Contains(entities[i]);
                                    int column = arch.GetComponentTypeIndex<MaskChunkVisits>();
                                    for (int i = 0; i < chunk.EntityCount; i++)
                                        if (chunk.GetComponentReadOnly<MaskChunkVisits>(i, column).Count != (i == 0 && any ? 1 : 0))
                                            throw new Exception("Execute must run once per nonempty physical Chunk.");
                                }
                            }
                    }
                }
                em.DestroyEntity(a);
                var recycled = em.NewEntity(Array.Empty<ComponentType>());
                if (recycled.Id != a.Id) throw new Exception("Target ID was not recycled.");
                if (count > 0) em.AddRelationship<ReadLink>(sources[0], recycled);
                var expired = new QueryBuilder().WithAll<ReadLinkOutput>().WithRelationship<ReadLink>(a);
                foreach (var source in sources) em.SetComponent(source, default(ReadLinkOutput));
                Execute<RelationMaskEntityCpp>(0, world, expired, false, entity: true);
                foreach (var source in sources)
                    if (em.GetComponentReadOnly<ReadLinkOutput>(source).Value != 0) throw new Exception("Expired target identity matched.");
            }
#if ENTJOY_FIXTURE_ISPC
            using (var world = new World("NativeRelationTaskEvents"))
            {
                var em = world.EntityManager;
                var target = em.NewEntity(Array.Empty<ComponentType>());
                for (int i = 0; i < 65; i++)
                {
                    var source = em.NewEntity(typeof(ReadLink), typeof(ReadLinkOutput), typeof(MaskEnabled));
                    em.AddRelationship<ReadLink>(source, target);
                    em.SetComponentEnabled<MaskEnabled>(source, i % 3 != 0);
                }
                world.RegisterEvent<RelationMaskEvent>();
                ChunkJobScheduler.RegisterEventBufferMeta(typeof(RelationMaskIspcTaskEvents), new[] { typeof(RelationMaskEvent) });
                var query = new QueryBuilder().WithAll<ReadLinkOutput>().WithRelationship<ReadLink>(target).WithEnabled<MaskEnabled>();
                Execute<RelationMaskIspcTaskEvents>(0, world, query, false);
                world.NextFrameEvents();
                var events = world.GetEventStream<RelationMaskEvent>().ReadBuffer().ToArray().Select(value => value.Slot).OrderBy(value => value);
                if (!events.SequenceEqual(Enumerable.Range(0, 65).Where(i => i % 3 != 0)))
                    throw new Exception("ISPC task events did not honor the combined relationship mask.");
            }
#endif
        }
        finally { Archetype.ChunkCapacityOverride = previous; }
        Console.WriteLine($"PASS: Native relationship and Enabled masks: {comparisons} entity comparisons; physical Chunk invocation counts verified");
    }

    private static void Execute<T>(int path, World world, QueryBuilder query, bool visits, bool entity = false) where T : struct
    {
        T job = default;
        string entry = $"Get_SharpNative_Job_NativeTranspilerFixture_{typeof(T).Name}_Execute";
        int output = ComponentTypeManager.GetComponentType(typeof(ReadLinkOutput)).Id;
        int[] required = visits ? new[] { output, ComponentTypeManager.GetComponentType(typeof(MaskChunkVisits)).Id } : new[] { output };
        if (path == 0)
            ChunkJobScheduler.RunChunkRangeImmediate(ref job, world.EntityManager, query,
                NativeJobScheduler.GetNativeExportPtr(entry + "_RangeAdapterPtr"), required, world);
        else
        {
            var handle = path == 1
                ? ChunkJobScheduler.ScheduleEntityRawWithWorkerCapAndRangeSize(ref job, world.EntityManager, query,
                    NativeJobScheduler.GetNativeExportPtr(entry + "_AdapterPtr"), required, 2, 1, world: world)
                : path == 2 || entity
                    ? ChunkJobScheduler.ScheduleChunkRangeRaw(ref job, world.EntityManager, query,
                        NativeJobScheduler.GetNativeExportPtr(entry + "_RangeAdapterPtr"), required, world: world)
                    : RunBatch();
            NativeJobScheduler.Complete(ref handle);
        }
        NativeJobHandle RunBatch()
        {
            // IJobChunk 的紧凑批次 ABI；这里只传物理 Chunk，不切分匹配实体段。
            ChunkJobScheduler.RunChunkImmediate(ref job, world.EntityManager, query,
                NativeJobScheduler.GetNativeExportPtr(entry + "_EntityBatchAdapterPtr"), required, world);
            return default;
        }
    }

    private static void ExecuteChunk<T>(int path, World world, QueryBuilder query, bool visits) where T : struct, IJobChunk
    {
        if (path != 4) { Execute<T>(path, world, query, visits); return; }
        var job = default(T);
        string entry = $"Get_SharpNative_Job_NativeTranspilerFixture_{typeof(T).Name}_Execute_EntityBatchAdapterPtr";
        int output = ComponentTypeManager.GetComponentType(typeof(ReadLinkOutput)).Id;
        int[] required = visits ? new[] { output, ComponentTypeManager.GetComponentType(typeof(MaskChunkVisits)).Id } : new[] { output };
        var handle = ChunkJobScheduler.ScheduleChunkEntityBatchRawWithWorkerCapAndRangeSize(ref job, world.EntityManager, query,
            NativeJobScheduler.GetNativeExportPtr(entry), required, 2, 1, world: world);
        NativeJobScheduler.Complete(ref handle);
    }
}
