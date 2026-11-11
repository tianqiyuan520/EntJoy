using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;
using Xunit;

namespace EntJoy.ECS.Tests;

/// <summary>
/// 结构变更快路径靠 <c>_jobTrackingDirty</c> 一个标记判断「有没有在飞 Job」。标记必须能回到 0：
/// 停在 1 会让所有结构变更永久走慢路径（功能正确、性能静默退化）；停在 0 而实际有 Job 则更糟。
/// 这里钉住「调度后清除、完成后为空」这个往返。
/// </summary>
public sealed class JobTrackingIdleFlagTests
{
    public struct Position
    {
        public float X;
    }

    public struct TouchChunkJob : IJobChunk
    {
        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
        {
            var values = chunk.GetComponentDataSpan<Position>();
            for (int i = 0; i < values.Length; i++)
                values[i].X += 1f;
        }
    }

    [Fact]
    public void FlagReturnsToIdleAfterJobsComplete()
    {
        TestBackend.EnsureInitialized();
        using var world = new World("JobTrackingIdleFlag");
        var em = world.EntityManager;

        Assert.True(em.JobTrackingIsIdle);

        for (int i = 0; i < 64; i++)
            em.NewEntity(typeof(Position));

        var query = new QueryBuilder().WithAll<Position>();
        new TouchChunkJob().Schedule(query).Complete();
        em.CompleteActiveJobs();

        Assert.True(em.JobTrackingIsIdle);

        for (int i = 0; i < 32; i++)
            em.NewEntity(typeof(Position));

        new TouchChunkJob().Schedule(query).Complete();
        em.CompleteActiveJobs();

        Assert.True(em.JobTrackingIsIdle);
    }
}
