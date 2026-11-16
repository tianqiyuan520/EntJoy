using System;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests;

/// <summary>
/// 借用校验的现行边界：校验在 <c>MoveNext</c> 的块边界收敛，失效后的引用访问**不再抛异常**；
/// 迭代期间的结构变更走 <see cref="DeferredCommandBuffer"/>。
/// 这一组用例锁定"不抛"这条契约本身，替代因行为变更被 Skip 的历史用例。
/// </summary>
public class QueryBorrowContractTests
{
    private struct Pos { public int X; }
    private struct Vel { public int X; }

    [Fact]
    public void StaleCurrentAfterStructuralChangeDoesNotThrow()
    {
        TestBackend.EnsureInitialized();
        using var world = new World("BorrowStale");
        var em = world.EntityManager;
        em.CreateEntities(8, typeof(Pos), typeof(Vel));

        var it = world.Query<Pos, Vel>().GetEnumerator();
        Assert.True(it.MoveNext());
        var current = it.Current;

        // 迭代器仍在同一个 MoveNext 块内：结构变更不在这里校验。
        em.NewEntity(typeof(Pos));

        try
        {
            ref readonly var pos = ref current.ReadOnlyComp0;
            ref readonly var vel = ref current.ReadOnlyComp1;
            _ = pos;
            _ = vel;
            _ = it.Current;
        }
        catch (Exception ex)
        {
            Assert.Fail($"失效后不应再抛借用异常：{ex.GetType().Name}");
        }
    }

    [Fact]
    public void DeferredStructuralChangeDuringIterationIsApplied()
    {
        TestBackend.EnsureInitialized();
        using var world = new World("BorrowDeferred");
        var em = world.EntityManager;
        em.CreateEntities(4, typeof(Pos));

        int before = 0;
        foreach (var _ in world.Query<Pos>()) before++;

        using (var ecb = new DeferredCommandBuffer())
        {
            foreach (var _ in world.Query<Pos>()) ecb.CreateEntity(typeof(Pos));
            ecb.Playback(em);
        }

        int after = 0;
        foreach (var _ in world.Query<Pos>()) after++;

        Assert.Equal(before * 2, after);
    }
}
