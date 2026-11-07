using System.Threading;
using System.Threading.Tasks;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;

namespace NativeTranspilerFixture;

public struct RelationshipDependencyGate : IJob
{
    public ManualResetEventSlim Started, Release;
    public void Execute()
    {
        Started.Set();
        if (!Release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Native relationship dependency timed out.");
    }
}

public struct RelationshipEnableDependency : IJobChunk
{
    public ManualResetEventSlim Release;
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
    {
        if (!Release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Native Enabled dependency timed out.");
        for (int i = 0; i < chunk.Count; i++) chunk.SetComponentEnabled<MaskEnabled>(i, i % 2 == 0);
    }
}

public static class RelationshipDependencyFixture
{
    private static IntPtr Reader => NativeJobScheduler.GetNativeExportPtr("Get_SharpNative_Job_NativeTranspilerFixture_RelationMaskEntityCpp_Execute_AdapterPtr");
    private static int[] Required => new[] { ComponentTypeManager.GetComponentType(typeof(ReadLinkOutput)).Id };

    public static void Run()
    {
        foreach (string mutation in new[] { "retarget", "delete", "create" })
        {
            using var world = new World("NativeRelationLifetime");
            using var started = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            using var scheduled = new ManualResetEventSlim(false);
            var em = world.EntityManager;
            var a = em.NewEntity(Array.Empty<ComponentType>());
            var b = em.NewEntity(Array.Empty<ComponentType>());
            var source = em.NewEntity(typeof(ReadLink), typeof(ReadLinkOutput));
            em.AddRelationship<ReadLink>(source, a);
            var releaseTask = Task.Run(() =>
            {
                if (!started.Wait(TimeSpan.FromSeconds(3)) || !scheduled.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Reader submission timed out.");
                Thread.Sleep(60);
                release.Set();
            });
            var parent = new RelationshipDependencyGate { Started = started, Release = release }.Schedule();
            NativeJobHandle reader = default;
            try
            {
                var query = new QueryBuilder().WithAll<ReadLinkOutput>().WithRelationship<ReadLink>(a);
                var job = new RelationMaskEntityCpp();
                reader = ChunkJobScheduler.ScheduleEntityRawWithWorkerCapAndRangeSize(ref job, em, query, Reader, Required, 2, 1, parent._nativeHandle, world);
                scheduled.Set();
                if (mutation == "retarget") em.AddRelationship<ReadLink>(source, b);
                else if (mutation == "delete") em.DestroyEntity(a);
                else em.NewEntity(Array.Empty<ComponentType>());
                if (em.GetComponentReadOnly<ReadLinkOutput>(source).Value != 1) throw new Exception($"Mutation did not wait for the native relationship reader: {mutation}");
            }
            finally
            {
                scheduled.Set(); release.Set(); NativeJobScheduler.Complete(ref reader); parent.Complete();
                releaseTask.GetAwaiter().GetResult();
            }
        }
        using (var world = new World("NativeRelationEnabledDependency"))
        using (var release = new ManualResetEventSlim(false))
        {
            var em = world.EntityManager;
            var target = em.NewEntity(Array.Empty<ComponentType>());
            var sources = new Entity[65];
            for (int i = 0; i < sources.Length; i++)
            {
                sources[i] = em.NewEntity(typeof(ReadLink), typeof(ReadLinkOutput), typeof(MaskEnabled));
                em.AddRelationship<ReadLink>(sources[i], target);
                em.SetComponentEnabled<MaskEnabled>(sources[i], false);
            }
            var all = new QueryBuilder().WithAll<ReadLinkOutput>().WithAll<MaskEnabled>();
            var parent = new RelationshipEnableDependency { Release = release }.Schedule(all, world);
            NativeJobHandle reader = default;
            try
            {
                var query = all.WithEnabled<MaskEnabled>().WithRelationship<ReadLink>(target);
                var job = new RelationMaskEntityCpp();
                reader = ChunkJobScheduler.ScheduleEntityRawWithWorkerCapAndRangeSize(ref job, em, query, Reader, Required, 2, 1, parent._nativeHandle, world);
                release.Set();
                NativeJobScheduler.Complete(ref reader);
                for (int i = 0; i < sources.Length; i++)
                    if (em.GetComponentReadOnly<ReadLinkOutput>(sources[i]).Value != (i % 2 == 0 ? 1 : 0))
                        throw new Exception("Native masks read Enabled bits before their dependency completed.");
            }
            finally { release.Set(); NativeJobScheduler.Complete(ref reader); parent.Complete(); }
        }
        Console.WriteLine("PASS: Native relationship lifecycle waits and dependency-written Enabled masks: 4 scenarios");
    }
}
